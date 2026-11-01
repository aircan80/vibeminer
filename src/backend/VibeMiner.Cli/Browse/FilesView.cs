using System.Collections.Immutable;
using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Names;

namespace VibeMiner.Cli.Browse;

/// <summary>The files target a Files view last opened in this session, with its breadcrumb label (the version name).</summary>
internal sealed record OpenedFiles(FilesTarget Target, string Label);

/// <summary>
/// A build's files for one locale, one page at a time, ascending by FileDataID. Loads are read-only: they open the build from the
/// store through the session's layer 4 and never fetch. <c>/text</c> is a path prefix (the community names); <c>t</c> formats,
/// <c>s</c> stored state, <c>l</c> the locale, <c>n</c>/<c>p</c> pages. Enter shows a file. <c>g</c> and <c>G</c> ensure files
/// as the store's writer (<see cref="EnsureTask"/>), the one way this view fetches. Built with no target (the <c>:files</c> entry)
/// it shows the last target this session opened, or says how to open one.
/// </summary>
internal sealed class FilesView : BrowseView
{
    public const int PageSize = 500;
    public const string DefaultLocale = "enUS";

    /// <summary>The cycle <c>s</c> steps through: all, stored, not stored, no encoded copy.</summary>
    private static readonly (string Label, FileStorage? Storage)[] StoredCycle =
        [("all", null), ("stored", FileStorage.Stored), ("not stored", FileStorage.NotStored), ("no encoded copy", FileStorage.NoEncodedCopy)];

    /// <summary>
    /// Everything the keys change, replaced whole under the lock. A load copies it once at its start, since a canceled load of
    /// this view may still be running while a key sets the next one.
    /// </summary>
    private sealed record State(OpenedFiles? Opened, IReadOnlySet<FileFormat>? Formats, int Stored, ImmutableList<uint> Pages, string PagesFor, int Version);

    /// <summary>What the last finished load found, for <c>n</c>: the state version it read and where the next page starts.</summary>
    private sealed record PageEnd(int Version, uint? NextFrom);

    private readonly Lock gate = new();
    private State state;
    private PageEnd? end;
    // The target of the rows the last current load listed, which g ensures in (a locale change may be loading meanwhile).
    private FilesTarget? listed;

    public FilesView(FilesTarget target, string label)
    {
        ArgumentNullException.ThrowIfNull(target);
        state = new(new(target, label), null, 0, ImmutableList<uint>.Empty, "", 0);
    }

    /// <summary>The <c>:files</c> entry: no target until its first load takes the session's last one.</summary>
    public FilesView() => state = new(null, null, 0, ImmutableList<uint>.Empty, "", 0);

    private State Snapshot { get { lock (gate) return state; } }

    public FilesTarget? Target => Snapshot.Opened?.Target;

    public override string Title => Snapshot.Opened is { } o ? $"files {o.Target.Locale}" : "files";
    public override bool FiltersOnLoad => true;

    public override IReadOnlyList<ViewKey> Keys =>
    [
        new('n', "next page", _ => Next()),
        new('p', "previous page", _ => Previous()),
        new('t', "formats: comma list (Db2, Blp, ...); Blp? = only from the name", _ => AskFormats()),
        new('s', "stored: cycle all, stored, not stored, no encoded copy", _ => CycleStored()),
        new('l', LocaleHelp, _ => AskLocale()),
        new('g', "ensure: fetch the selected file so it is stored (writer lock)", EnsureOne),
        new('G', "ensure all: fetch every file the filters match (asks first)", _ => AskEnsureAll()),
    ];

    /// <summary>The <c>l</c> help: every locale, wrapped under the help's key column onto lines that fit <see cref="HelpWidth"/>.</summary>
    internal static string LocaleHelp
    {
        get
        {
            var lines = new List<string> { "locale: one of" };
            foreach (var name in FileLocales.Names)
            {
                // The help indents each line 11 columns; a wrapped line lines up under the text.
                if (11 + lines[^1].Length + 1 + name.Length + 1 > HelpWidth) lines.Add("");
                lines[^1] = lines[^1].Length == 0 ? name : $"{lines[^1]} {name}";
            }
            return string.Join("\n" + new string(' ', 11), lines);
        }
    }

    /// <summary><c>g</c>: the selected row's file, at Interactive priority.</summary>
    private KeyOutcome EnsureOne(BrowseRow? row)
    {
        if (row?.Tag is not BuildFile file) return new(Say: Snapshot.Opened is null ? NoTarget : "No file selected.");
        FilesTarget? target;
        lock (gate) target = listed;
        if (target is null) return new(Say: "Still loading this page.");
        return new(Start: EnsureTask.Start(target, EnsureSelection.One(FileRef.Id(file.FileDataId)), EnsurePriority.Interactive));
    }

    /// <summary>
    /// <c>G</c>: every file the current filters match (path, formats, stored state, locale) on every page, at Background priority,
    /// once a <c>y</c> answer confirms it. Anything else starts nothing.
    /// </summary>
    private KeyOutcome AskEnsureAll()
    {
        var s = Snapshot;
        if (s.Opened is not { } opened) return new(Say: NoTarget);
        var filter = Filter;
        var target = opened.Target;
        var matching = Listing(s, filter, 0);
        var filters = Filters(s, filter);
        var what = filters.Count == 0 ? $"{target.Locale} files" : $"{target.Locale}, {string.Join("; ", filters)}";
        // The shell shows at most MaxQuestion columns of a question, so the filters are cut and the y/N choice always shows.
        const string Before = "ensure all ", After = " (writer lock)? y/N";
        what = EnsureTask.Clip(what, BrowseShell.MaxQuestion - Before.Length - After.Length);
        return new(Ask: new(Before + what + After, "", typed => typed.Trim() is "y" or "Y"
            ? new(Start: EnsureTask.Start(target, EnsureSelection.Matching(matching), EnsurePriority.Background))
            : new(Say: "ensure all: not started; nothing fetched.")));
    }

    /// <summary>The listing filter for the state and path filter, from <paramref name="from"/> on.</summary>
    private static FileFilter Listing(State s, string filter, uint from) => new()
    {
        FromId = from,
        PathPrefix = filter.Length == 0 ? null : filter,
        Formats = s.Formats,
        Storage = StoredCycle[s.Stored].Storage is { } storage ? new HashSet<FileStorage> { storage } : null,
    };

    /// <summary>The filters set, in words: the path, the formats, the stored state.</summary>
    private static List<string> Filters(State s, string filter)
    {
        var filters = new List<string>();
        if (filter.Length > 0) filters.Add($"path {filter}");
        if (s.Formats is { } formats) filters.Add(string.Join(",", formats.Order()));
        if (s.Stored != 0) filters.Add(StoredCycle[s.Stored].Label);
        return filters;
    }

    /// <summary>Changes the state and starts over at the first page.</summary>
    private KeyOutcome Restart(Func<State, State> change)
    {
        lock (gate) state = change(state) with { Pages = ImmutableList<uint>.Empty, Version = state.Version + 1 };
        return new(Reload: true);
    }

    private KeyOutcome Next()
    {
        lock (gate)
        {
            if (state.Opened is null) return new(Say: NoTarget);
            if (end is null || end.Version != state.Version) return new(Say: "Still loading this page.");
            if (end.NextFrom is not { } from) return new(Say: "This is the last page.");
            state = state with { Pages = state.Pages.Add(from), Version = state.Version + 1 };
        }
        return new(Reload: true);
    }

    private KeyOutcome Previous()
    {
        lock (gate)
        {
            if (state.Pages.IsEmpty) return new(Say: "This is the first page.");
            state = state with { Pages = state.Pages.RemoveAt(state.Pages.Count - 1), Version = state.Version + 1 };
        }
        return new(Reload: true);
    }

    private KeyOutcome AskFormats()
    {
        var current = Snapshot.Formats is { } f ? string.Join(",", f.Order()) : "";
        return new(Ask: new("Formats (comma list, empty for all):", current, typed =>
        {
            var names = typed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var formats = new HashSet<FileFormat>();
            foreach (var name in names)
            {
                var match = Enum.GetNames<FileFormat>().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (match is null) return new(Say: Fit([$"No format {name}.", "E.g. Db2, Blp, M2Chunked, WmoRoot, AdtRoot, Ogg, Lua."]));
                formats.Add(Enum.Parse<FileFormat>(match));
            }
            return Restart(s => s with { Formats = formats.Count == 0 ? null : formats });
        }));
    }

    private KeyOutcome CycleStored() => Restart(s => s with { Stored = (s.Stored + 1) % StoredCycle.Length });

    private KeyOutcome AskLocale()
    {
        if (Snapshot.Opened is not { } opened) return new(Say: NoTarget);
        return new(Ask: new("Locale:", opened.Target.Locale, typed =>
        {
            var locale = typed.Trim();
            if (!FileLocales.IsKnown(locale)) return new(Say: Fit([$"No locale {locale}.", "? lists them; e.g. enUS, deDE, frFR, zhCN."]));
            return Restart(s => s with { Opened = s.Opened! with { Target = s.Opened.Target with { Locale = locale } } });
        }));
    }

    /// <summary><c>f</c> on a build: its files for enUS in the region <see cref="BuildView.RegionOf"/> picks; a Say when there is none.</summary>
    internal static KeyOutcome Open(CatalogBuild? build)
    {
        if (build is null) return new(Say: "No build selected.");
        if (BuildView.RegionOf(build) is not { } region) return new(Say: "No region lists a cdn config for this build, so it can't be opened.");
        return new(Open: new FilesView(new(new BuildRef(build.Product, build.BuildConfig, region.CdnConfig, region.Region), DefaultLocale), build.VersionsName));
    }

    internal const string NoTarget = "No build open yet. In :builds, Enter a product, then f on a build.";

    /// <summary>The widest a help line may be: the help dialog's list is narrower than the 80-column screen.</summary>
    internal const int HelpWidth = 74;

    /// <summary>The longest a note or a message may be: the shell shows each as one line of an 80-column terminal.</summary>
    internal const int Width = 80;

    /// <summary>
    /// Joins parts in order while they fit in <see cref="Width"/>. The first part, and the one at <paramref name="cut"/>, are cut
    /// with "..." when too long (the latter only with room for it to say something); any other part that doesn't fit is left out,
    /// and so is everything after it.
    /// </summary>
    internal static string Fit(IReadOnlyList<string> parts, int cut = 0)
    {
        var line = "";
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var room = i == 0 ? Width : Width - line.Length - 1;
            if (part.Length <= room) { line = i == 0 ? part : $"{line} {part}"; continue; }
            if (i == 0) line = part[..(room - 3)] + "...";
            else if (i == cut && room >= 16) line = $"{line} {part[..(room - 3)]}...";
            break;
        }
        return line;
    }

    private static readonly string[] Columns = ["Id", "Path", "Format", "Size", "Stored", "Enc"];

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        var filter = Filter;
        State s;
        lock (gate)
        {
            // A new path filter starts over at the first page; the :files entry takes the session's last target.
            if (state.PagesFor != filter) state = state with { Pages = ImmutableList<uint>.Empty, PagesFor = filter, Version = state.Version + 1 };
            if (state.Opened is null && session.LastFiles is { } previous) state = state with { Opened = previous, Version = state.Version + 1 };
            s = state;
        }
        if (s.Opened is not { } opened) return new(Columns, [], NoTarget);
        var target = opened.Target;

        await using var lease = await session.OpenFilesAsync(target, ct).ConfigureAwait(false);
        if (lease.Result.State != FilesOpenState.Open || lease.Files is not { } files)
        {
            Finish(s, null);
            return new(Columns, [], NotOpen(lease.Result));
        }

        var from = s.Pages.IsEmpty ? 0u : s.Pages[^1];
        var listing = Listing(s, filter, from);
        var rows = new List<BrowseRow>(PageSize);
        var more = false;
        await foreach (var file in files.ListAsync(listing, ct).ConfigureAwait(false))
        {
            // One past the page only says there is a next one.
            if (rows.Count == PageSize) { more = true; break; }
            rows.Add(Row(target, file));
        }
        var last = rows.Count > 0 ? ((BuildFile)rows[^1].Tag!).FileDataId : (uint?)null;
        if (Finish(s, more && last is { } l && l < uint.MaxValue ? l + 1 : null))
        {
            session.LastFiles = opened;
            lock (gate) listed = target;
        }

        var names = await lease.Names.StatusAsync(ct).ConfigureAwait(false);
        return new(Columns, rows, Note(opened.Label, target.Locale, s, filter, rows, more, names));
    }

    /// <summary>
    /// Records where the page ended, unless a newer load already did (a stale load can finish after it). True when this load read
    /// the current state, so it may stand as the session's last opened files.
    /// </summary>
    private bool Finish(State read, uint? nextFrom)
    {
        lock (gate)
        {
            if (end is null || read.Version >= end.Version) end = new(read.Version, nextFrom);
            return read.Version == state.Version;
        }
    }

    private static BrowseRow Row(FilesTarget target, BuildFile f) => new(
        [f.FileDataId.ToString(CultureInfo.InvariantCulture), f.Path ?? "", FormatCell(f),
         f.Size is { } size ? size.ToString("N0", CultureInfo.InvariantCulture) : "-", StoredCell(f.Storage), f.Encrypted ? "yes" : ""],
        () => new FileView(target, f), f);

    /// <summary>The format, with <c>?</c> when only the name's extension says so; <c>-</c> when nothing does.</summary>
    internal static string FormatCell(BuildFile f) => f.FormatSource switch
    {
        FileFormatSource.None => "-",
        FileFormatSource.Extension => $"{f.Format}?",
        _ => f.Format.ToString(),
    };

    internal static string StoredCell(FileStorage s) => s switch
    {
        FileStorage.Stored => "yes",
        FileStorage.NotStored => "no",
        _ => "no copy",
    };

    /// <summary>What an open that isn't Open means for a browser that never fetches, state first, within <see cref="Width"/>.</summary>
    internal static string NotOpen(FilesOpenResult r) => Fit(r.State switch
    {
        FilesOpenState.NeedsNetwork => ["NeedsNetwork: not stored.", "a on a build: Download all, whole build, writer lock."],
        FilesOpenState.LocaleNotInBuild => [$"LocaleNotInBuild: no {r.Locale} files in this build; l picks another."],
        FilesOpenState.Missing => [$"Missing ({r.Code ?? "no code"}):", "the CDN had no copy of a part when last asked."],
        FilesOpenState.Encrypted => [$"Encrypted ({r.Code ?? "no code"}):", "part of the build needs a TACT key this app lacks."],
        _ => [$"{r.State} ({r.Code ?? "no code"}):", "can't open the build."],
    });

    /// <summary>The failed names note with its code, the code cut with "..." so the note fits <paramref name="room"/>.</summary>
    internal static string NamesFailed(string? code, int room)
    {
        const string Before = "Names failed (", After = "): no paths, / finds none.";
        return Before + EnsureTask.Clip(code ?? "no code", Math.Max(8, room - Before.Length - After.Length)) + After;
    }

    /// <summary>
    /// The page first, then what the names state means, the filters, the page keys and the build, as many as fit in
    /// <see cref="Width"/>.
    /// </summary>
    private static string Note(string label, string locale, State s, string filter, List<BrowseRow> rows, bool more, FileNamesStatus names)
    {
        var page = s.Pages.Count + 1;
        var keys = string.Join(", ", new[] { more ? "n next" : null, page > 1 ? "p back" : null }.Where(k => k is not null));
        var parts = new List<string>
        {
            (rows.Count == 0 ? $"Page {page}, no files match" : $"Page {page}, {((BuildFile)rows[0].Tag!).FileDataId}-{((BuildFile)rows[^1].Tag!).FileDataId}{(more ? "" : " (last)")}")
                + (keys.Length > 0 ? $"; {keys}." : "."),
        };
        var namesNote = names.State switch
        {
            FileNamesState.Off => "Names off in Settings: no paths, / finds none.",
            FileNamesState.NotReady => "Names not loaded: no paths, / finds none.",
            FileNamesState.Failed => NamesFailed(names.Code, Width - parts[0].Length - 1),
            _ => null,
        };
        if (namesNote is not null) parts.Add(namesNote);
        var filters = Filters(s, filter);
        var cut = parts.Count;
        if (filters.Count > 0) parts.Add($"Filter: {string.Join("; ", filters)}.");
        parts.Add($"{label} {locale}.");
        return Fit(parts, filters.Count > 0 ? cut : 0);
    }
}
