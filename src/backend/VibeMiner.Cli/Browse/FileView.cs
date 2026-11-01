using System.Globalization;
using System.Text;
using VibeMiner.Files;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// One file of a build and locale: a fresh stat, then its decoded bytes as hex, read <see cref="Window"/> bytes at a time from the
/// session's read-only store (a file that isn't stored answers NeedsNetwork; nothing is fetched). Holes are named lines, never
/// zero-filled. Down on the last line or <c>n</c> reads the next window, up to <see cref="MaxShown"/>; windows already read are
/// kept, a failed one isn't (the next load retries it, so after <c>g</c> ensures the file the reload reads it). <c>e</c> exports
/// the whole file as a background task; <c>g</c> fetches it as the store's writer (<see cref="EnsureTask"/>). A BLP file also gets an
/// Image line from layer 5's header inspection (#199): its 148-byte header read through the same read-only handle, so a file that
/// isn't stored says so and nothing is fetched. A Ready inspection is kept for the content key; any other is read again next load.
/// An M2 model also gets a model section between the stat and the hex (<see cref="ModelLines"/>), read with the same handle and
/// never fetched.
/// </summary>
internal sealed class FileView(FilesTarget target, BuildFile file) : BrowseView
{
    public const int Window = 4096;
    public const int MaxShown = 1 << 20;
    private const int Columns = 80;

    /// <summary>The build and locale the view reads.</summary>
    public FilesTarget Target { get; } = target;
    /// <summary>The file as the list gave it; the view shows a fresh stat.</summary>
    public BuildFile File { get; } = file;
    private readonly uint id = file.FileDataId;
    private readonly string? listedPath = file.Path;
    private readonly Lock gate = new();
    // Kept windows, in order: window i starts at i * Window. Loads may overlap (a canceled one can still run), so a window is only
    // added when it is the next one.
    private readonly List<FileRead> windows = [];
    private int wanted = 1;
    private BuildFile current = file;
    private bool found;
    private bool lastFailed;
    private (string ContentKey, BlpInspection Inspection)? inspected;

    /// <summary>How many hex bytes the view shows at most (<see cref="MaxShown"/>); a test seam, a multiple of <see cref="Window"/>.</summary>
    internal int ShownLimit { get; init; } = MaxShown;
    private int MaxWindows => ShownLimit / Window;

    /// <summary>How many referenced files the model section lists at most; a test seam.</summary>
    internal int ReferenceLimit { get; init; } = MaxReferenceLines;
    public const int MaxReferenceLines = 200;

    /// <summary>A test seam: runs after the export's temp file is written, before it is moved into place.</summary>
    internal Func<string, CancellationToken, Task>? AfterTempWritten { get; set; }

    public override string Title
    {
        get
        {
            var name = FileName(listedPath);
            return name is null ? id.ToString(CultureInfo.InvariantCulture) : name.Length > 40 ? name[..37] + "..." : name;
        }
    }

    public override IReadOnlyList<ViewKey> Keys =>
    [
        new('n', "next 4 KiB (also Down on the last line); retries a failed read", _ => Next()),
        new('e', "export the whole file to a new file at a path you give", _ => new(Ask: new("export to:", InitialExportPath(), Export))),
        new('g', "ensure: fetch this file so it is stored (writer lock)",
            _ => new(Start: EnsureTask.Start(Target, EnsureSelection.One(FileRef.Id(id)), EnsurePriority.Interactive))),
    ];

    public override bool HasMore
    {
        get
        {
            lock (gate) return found && !lastFailed && windows.Count < MaxWindows && !AtEnd();
        }
    }

    public override void More()
    {
        lock (gate) wanted = Math.Max(wanted, Math.Min(windows.Count + 1, MaxWindows));
    }

    private KeyOutcome Next()
    {
        bool failed;
        lock (gate) failed = lastFailed;
        if (failed) return new(Reload: true);
        if (!HasMore) return new(Say: "Nothing more to read here.");
        More();
        return new(Reload: true);
    }

    // Under the gate.
    private bool AtEnd()
    {
        if (windows.Count == 0) return current.Size == 0;
        var last = windows[^1];
        var end = last.Offset + last.Length;
        return current.Size is { } size ? end >= size : last.Length < Window;
    }

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        int want;
        lock (gate) want = wanted;
        var note = $"{Target.Build.Product} {Short(Target.Build.BuildConfig)} {Target.Locale}";
        await using var lease = await session.OpenFilesAsync(Target, ct).ConfigureAwait(false);
        if (lease.Files is not { } files)
        {
            var r = lease.Result;
            return ViewContent.OfText(Fit($"The build didn't open: {r.State}{Code(r.Code)}{Message(r.Message)}"), note);
        }
        var stat = await files.StatAsync(FileRef.Id(id), ct).ConfigureAwait(false);
        var lines = new List<string>();
        switch (stat.State)
        {
            case FileLookupState.UnknownFile:
                lock (gate) found = false;
                lines.Add($"FileDataID   {id}");
                lines.Add($"Lookup       unknown file: this build doesn't list FileDataID {id}.");
                return ViewContent.OfText(string.Join('\n', lines), note);
            case FileLookupState.NotInLocale:
                lock (gate) found = false;
                lines.Add($"FileDataID   {id}");
                lines.Add($"Lookup       not in locale: the build lists it, but not for {Target.Locale}.");
                return ViewContent.OfText(string.Join('\n', lines), note);
        }
        var stated = stat.File!;
        lines.AddRange(StatLines(stat.State, stated));
        if (stat.State == FileLookupState.Found && stated.Format == FileFormat.Blp)
            lines.InsertRange(lines.FindIndex(l => l.StartsWith("Format ", StringComparison.Ordinal)) + 1,
                ImageLines(await InspectAsync(session, files, stated, ct).ConfigureAwait(false)));
        lines.Add("");
        if (stat.State == FileLookupState.NotInEncoding)
        {
            lock (gate) { found = false; current = stated; }
            lines.Add("No bytes: ENCODING has no encoded copy of this file.");
            return ViewContent.OfText(string.Join('\n', lines), note);
        }
        lock (gate) { found = true; current = stated; }
        if (stated.Format is FileFormat.M2 or FileFormat.M2Chunked or FileFormat.M2Legacy)
        {
            // Read again on every load, the next window's too: a load can't tell it apart from the reload after an ensure, and
            // a kept section would show references' stored state from before it.
            lines.AddRange(await ModelLines(files, ct).ConfigureAwait(false));
            lines.Add("");
        }
        if (stated.Size == 0)
        {
            lines.Add("The file is empty (0 bytes).");
            return ViewContent.OfText(string.Join('\n', lines), note);
        }

        FileRead? failure = null;
        int have;
        lock (gate) have = windows.Count;
        while (have < want)
        {
            long offset = (long)have * Window;
            if (stated.Size is { } size && offset >= size) break;
            var read = await files.ReadRangeAsync(FileRef.Id(id), offset, Window, ct).ConfigureAwait(false);
            if (read.State is not (FileReadState.Complete or FileReadState.Partial or FileReadState.Protected)) { failure = read; break; }
            lock (gate)
            {
                if (windows.Count == have) windows.Add(read);
                have = windows.Count;
            }
            if (read.Length < Window) break;
        }
        List<FileRead> shown;
        bool more, atEnd;
        lock (gate)
        {
            lastFailed = failure is not null;
            shown = [.. windows];
            atEnd = AtEnd();
            more = !lastFailed && windows.Count < MaxWindows && !atEnd;
        }
        lines.AddRange(HexLines(shown));
        long shownEnd = shown.Count == 0 ? 0 : shown[^1].Offset + shown[^1].Length;
        if (failure is not null) lines.Add(Fit($"{failure.Offset:x8}  {ReadFailure(failure)}"));
        else if (atEnd) lines.Add("End of the file.");
        else if (!more) lines.Add($"Hex stops at {VmCommands.Size(ShownLimit)}; e exports the whole file.");
        else lines.Add("Down or n reads the next 4 KiB.");
        var of = stated.Size is { } total ? $" of {VmCommands.Size(total)}" : "";
        return ViewContent.OfText(string.Join('\n', lines), $"{note}. Shown {VmCommands.Size(shownEnd)}{of}.");
    }

    private static IEnumerable<string> StatLines(FileLookupState state, BuildFile f)
    {
        yield return $"FileDataID   {f.FileDataId}";
        yield return $"Lookup       {(state == FileLookupState.Found ? "found" : "not in encoding")}";
        yield return f.PathSource switch
        {
            // A long name is cut from the left, so the end of it and where it came from still show.
            FilePathSource.Listfile => Line("Path         ", f.Path ?? "", " (community listfile name, not Blizzard data)"),
            FilePathSource.RootHash => Line("Path         ", f.Path ?? "", " (matched ROOT's name hash)"),
            _ => "Path         none (names off or not loaded, or the listfile doesn't name it)",
        };
        yield return f.Size is { } size ? $"Size         {size:N0} bytes decoded ({VmCommands.Size(size)})" : "Size         unknown (no ENCODING entry)";
        yield return $"Content key  {f.ContentKey}";
        yield return $"Format       {f.Format} ({f.FormatSource switch
        {
            FileFormatSource.Magic => "from the bytes",
            FileFormatSource.Extension => "from the name's extension",
            _ => "neither the bytes nor the name say",
        }})";
        yield return f.Storage switch
        {
            FileStorage.Stored => "Stored       yes",
            FileStorage.NotStored => "Stored       no (browse reads only stored bytes; g fetches the file)",
            _ => "Stored       no encoded copy (ENCODING doesn't list it)",
        };
        yield return $"Encrypted    {(f.Encrypted ? "yes" : "no")} (ROOT's flag, a hint; reading tells)";
    }

    private async Task<BlpInspection> InspectAsync(BrowseSession session, IBuildFiles files, BuildFile f, CancellationToken ct)
    {
        lock (gate)
            if (inspected is { } kept && kept.ContentKey == f.ContentKey) return kept.Inspection;
        var images = await session.ImagesAsync(ct).ConfigureAwait(false);
        var inspection = await images.InspectAsync(files, FileRef.Id(f.FileDataId), ct).ConfigureAwait(false);
        if (inspection.State == BlpImageState.Ready) lock (gate) inspected = (f.ContentKey, inspection);
        return inspection;
    }

    private const string ImageLabel = "Image        ";
    private static readonly string Indent = new(' ', ImageLabel.Length);

    /// <summary>The Image line(s) of a BLP: size, encoding and mip count when Ready, else the one state it is in, in words.</summary>
    internal static IEnumerable<string> ImageLines(BlpInspection i)
    {
        var code = Code(i.Code);
        var h = i.Header;
        switch (i.State)
        {
            case BlpImageState.Ready:
                var mips = i.Mips!.Count;
                var encoding = i.Encoding!.Value;
                // BC1 and paletted decode differently by alpha depth; BC2, BC3 and BGRA8 don't.
                var alpha = encoding is Blp2Encoding.Bc1 or Blp2Encoding.Paletted ? (h!.AlphaDepth == 0 ? ", no alpha" : $", {h.AlphaDepth}-bit alpha") : "";
                yield return $"{ImageLabel}{h!.Width} x {h.Height}, {EncodingName(encoding)}{alpha}, {mips} mip{(mips == 1 ? "" : "s")}";
                yield break;
            case BlpImageState.Protected:
                yield return Fit($"{ImageLabel}header encrypted, needs TACT key{(i.HoleKeys.Count == 1 ? "" : "s")} {string.Join(", ", i.HoleKeys)}");
                yield break;
            case BlpImageState.FileFailed:
                var failure = i.FileState switch
                {
                    FileReadState.UnknownFile or FileReadState.NotInLocale or FileReadState.NotInEncoding => $"not read: {i.Message ?? i.FileState.ToString()}",
                    { } state => ReadFailure(state, i.Code, i.Message, i.ObservedAt),
                    null => $"not read{code}{Message(i.Message)}",
                };
                yield return Fit($"{ImageLabel}{failure}");
                yield break;
            case BlpImageState.Unsupported when h is not null && !KnownProfile(h):
                yield return $"{ImageLabel}unsupported encoding profile{code}";
                yield return $"{Indent}color encoding {h.ColorEncoding}, alpha depth {h.AlphaDepth}, alpha encoding {h.AlphaEncoding}";
                break;
            default:
                var what = i.State switch
                {
                    BlpImageState.Unsupported => "unsupported",
                    BlpImageState.Invalid => "invalid BLP",
                    BlpImageState.LimitExceeded => "over the decoder's limits",
                    BlpImageState.MipUnavailable => "mip unavailable",
                    _ => i.State.ToString(),
                };
                yield return Fit($"{ImageLabel}{what}{code}{Message(i.Message)}");
                break;
        }
        // A refused header that was read still says what it declares.
        if (h is not null)
            yield return Fit($"{Indent}header says {h.Width} x {h.Height}, {h.DeclaredMips} mip{(h.DeclaredMips == 1 ? "" : "s")}, mip flags 0x{h.MipFlags:x2}");
    }

    private static bool KnownProfile(BlpHeader h) => Enum.GetValues<Blp2Encoding>().Any(e =>
        (e switch { Blp2Encoding.Paletted => 1, Blp2Encoding.Bgra8 => 3, _ => 2 }) == h.ColorEncoding && Blp2Decoder.IsSupported(e, h.AlphaDepth, h.AlphaEncoding));

    internal static string EncodingName(Blp2Encoding encoding) => encoding switch
    {
        Blp2Encoding.Paletted => "paletted (256 colors)",
        Blp2Encoding.Bc1 => "BC1 (DXT1)",
        Blp2Encoding.Bc2 => "BC2 (DXT3)",
        Blp2Encoding.Bc3 => "BC3 (DXT5)",
        Blp2Encoding.Bgra8 => "BGRA8 (uncompressed)",
        _ => encoding.ToString(),
    };

    /// <summary>
    /// The model section: one line when the model doesn't read, else its counts, main skin profile 0 and the files it refers to
    /// (at most <see cref="ReferenceLimit"/> lines, then how many were cut). Reads only what is stored: not stored is a line.
    /// </summary>
    private async Task<List<string>> ModelLines(IBuildFiles files, CancellationToken ct)
    {
        var models = new BuildModels(files);
        var read = await models.ReadModelAsync(FileRef.Id(id), ct).ConfigureAwait(false);
        if (read.State != ModelReadState.Read || read.Model is not { } m2)
            return [Fit("Model        not read: " + Failure(read.State, read.Code, read.Message, read.ObservedAt, read.KeyNames, "g fetches it"))];
        var lines = new List<string>
        {
            $"Model        version {m2.Version}, {(m2.IsChunked ? "chunked (MD21)" : "legacy (MD20)")}",
            Fit($"Name         {(m2.Name.Length == 0 ? "none" : m2.Name)}"),
            $"Vertices     {m2.Vertices.Count:N0}",
            $"Textures     {m2.Textures.Count:N0}",
            $"Materials    {m2.Materials.Count:N0}",
            $"Bones        {m2.Bones.Count:N0}",
            Fit("Skins        " + (!m2.IsChunked
                ? $"{Count(read.MainSkinCount, "profile", "profiles")} in the header; legacy MD20 declares no skin FileDataIDs"
                : read.LodSkinCount == 0 ? $"{Count(read.MainSkinCount, "main profile", "main profiles")}"
                : $"{read.MainSkinCount} main, {read.LodSkinCount} LOD (SFID lists {read.MainSkinCount + read.LodSkinCount}, the header {read.MainSkinCount})")),
            Fit($"Animations   {Count(m2.AnimationFiles.Count, "AFID entry", "AFID entries")}, {Count(m2.AnimationFiles.Where(a => a.FileDataId != 0).Select(a => a.FileDataId).Distinct().Count(), "distinct file", "distinct files")}, {m2.AnimationFiles.Count(a => a.FileDataId == 0):N0} inline in the M2 (id 0)"),
            $"Bone files   {Count(m2.BoneFileDataIds.Count, "BFID entry", "BFID entries")}",
            "Skeleton     " + Single(read.References, ModelReferenceKind.Skeleton, "SKID"),
            "Physics      " + Single(read.References, ModelReferenceKind.PhysicsFile, "PFID"),
            Fit($"Bounds       min {V(m2.DeclaredBounds.Minimum)} max {V(m2.DeclaredBounds.Maximum)} radius {F(m2.DeclaredBounds.Radius)} (declared)"),
        };
        if (read.MainSkinCount == 0) lines.Add("Skin 0       none: the model has no skin profiles");
        else
        {
            var skin = await models.ReadSkinAsync(read, 0, ct).ConfigureAwait(false);
            if (skin.State == ModelReadState.Read && skin.Skin is { } s)
                lines.Add(Fit($"Skin 0       {s.VertexLookup.Count:N0} vertex lookup, {Count(s.TriangleLookup.Count, "triangle index", "triangle indices")}, {Count(s.Sections.Count, "submesh", "submeshes")}, {Count(s.Batches.Count, "batch", "batches")}"));
            else if (skin.State == ModelReadState.NotDeclared)
                lines.Add(Fit("Skin 0       not declared: " + (m2.IsChunked ? "no file for profile 0 in SFID" : "legacy model, no SFID; no name is guessed")));
            else if (skin.State == ModelReadState.NeedsNetwork)
                lines.Add($"Skin 0       {skin.FileDataId} not stored; g on that file fetches it");
            else
                lines.Add(Fit($"Skin 0       {skin.FileDataId} not read: " + Failure(skin.State, skin.Code, skin.Message, skin.ObservedAt, skin.KeyNames, "g on that file fetches it")));
        }
        var references = read.References.OrderBy(r => r.Kind).ToList();
        lines.Add(references.Count == 0 ? "References   none" : $"References   {references.Count:N0} declared, by kind");
        var shown = references.Take(ReferenceLimit).ToList();
        // The label column fits the longest shown label (13 at least, as the stat labels), so the FileDataIDs line up.
        var width = shown.Count == 0 ? 13 : Math.Max(13, shown.Max(r => Label(r).Length));
        foreach (var reference in shown) lines.Add(ReferenceLine(reference, width));
        if (references.Count > ReferenceLimit)
            lines.Add($"{references.Count - ReferenceLimit:N0} more not shown (first {ReferenceLimit:N0} of {references.Count:N0}).");
        return lines;
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    private static string Single(IReadOnlyList<ModelReference> references, ModelReferenceKind kind, string chunk) =>
        references.FirstOrDefault(r => r.Kind == kind) switch
        {
            null => $"none (no {chunk})",
            { FileDataId: 0 or null } => $"none ({chunk} declares no external file)",
            { } r => $"FileDataID {r.FileDataId}",
        };

    private static string V(System.Numerics.Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

    private static string F(float value) => value.ToString("G6", CultureInfo.InvariantCulture);

    private static string Label(ModelReference r) => r.Kind switch
    {
        ModelReferenceKind.Skin => $"Skin {r.Ordinal}",
        ModelReferenceKind.LodSkin => $"LOD skin {r.Ordinal}",
        ModelReferenceKind.Texture => $"Texture {r.Ordinal}",
        ModelReferenceKind.Skeleton => "Skeleton",
        ModelReferenceKind.BoneFile => $"Bone file {r.Ordinal}",
        ModelReferenceKind.AnimationFile => $"Anim {r.Ordinal} {r.AnimationId}.{r.Variation}",
        ModelReferenceKind.PhysicsFile => "Physics",
        ModelReferenceKind.RecursiveParticleModel => $"RPID {r.Ordinal}",
        ModelReferenceKind.GeometryParticleModel => $"GPID {r.Ordinal}",
        _ => $"{r.Kind} {r.Ordinal}",
    };

    /// <summary>One referenced file: kind and ordinal, FileDataID (or name), its state, and for a found one stored and its name.</summary>
    internal static string ReferenceLine(ModelReference r, int width = 13)
    {
        var before = $"{Label(r).PadRight(width)} {(r.FileDataId is { } fid ? fid.ToString(CultureInfo.InvariantCulture) : "-"),-9} ";
        if (r.FileDataId is null && r.Name is { } name && r.State != ModelReferenceState.Found)
            return Fit(Line(before + "name ", name, $": {State(r)}"));
        var state = State(r);
        if (r.State != ModelReferenceState.Found || r.File is not { } f) return Fit(before + state);
        var stored = f.Storage == FileStorage.Stored ? "found, stored yes" : "found, stored no";
        return f.PathSource switch
        {
            FilePathSource.Listfile when f.Path is { } p => Fit(Line(before + stored + ", ", p, " (community name)")),
            FilePathSource.RootHash when f.Path is { } p => Fit(Line(before + stored + ", ", p, " (ROOT name hash)")),
            _ => Fit(before + stored + ", no name"),
        };
    }

    private static string State(ModelReference r) => r.State switch
    {
        // A typed texture's TXID id is only a default the game replaces at run time: never a missing texture.
        ModelReferenceState.UnknownFile when r.Kind == ModelReferenceKind.Texture && r.TextureType is { } t and not 0 =>
            $"not in this build (type {t} default; game sets it)",
        ModelReferenceState.NotInEncoding when r.Kind == ModelReferenceKind.Texture && r.TextureType is { } t and not 0 =>
            $"not in encoding (type {t} default; game sets it)",
        ModelReferenceState.Found => "found",
        ModelReferenceState.NotInEncoding => "not in encoding",
        ModelReferenceState.UnknownFile => "unknown file",
        ModelReferenceState.NotInLocale => "not in locale",
        ModelReferenceState.NoExternalFile => "no external file",
        ModelReferenceState.NoFileDeclared => "no file declared",
        ModelReferenceState.ResolvedElsewhere => $"resolved elsewhere (type {r.TextureType}: {r.TextureType switch
        {
            >= 11 and <= 13 => "creature display",
            >= 15 and <= 17 => "guild emblem/banner",
            _ => "character or item data",
        }})",
        ModelReferenceState.NotDeclared => "not declared (legacy model, never guessed)",
        ModelReferenceState.UnresolvedByName => "unresolved by name",
        ModelReferenceState.CheckFailed => $"check failed{Message(r.Message)}",
        _ => r.State.ToString(),
    };

    /// <summary>
    /// A model or skin read that didn't end in Read: the states layer 4 also has in the words of <see cref="ReadFailure(FileRead)"/>,
    /// with the model's own for not stored (which file g fetches), holes and a reader refusal.
    /// </summary>
    private static string Failure(ModelReadState state, string? code, string? message, DateTimeOffset? observedAt, IReadOnlyList<string> keys, string fetch) => state switch
    {
        ModelReadState.NeedsNetwork => $"not stored{Code(code)}; {fetch}",
        ModelReadState.Protected => $"every byte needs TACT key {string.Join(", ", keys)}",
        ModelReadState.Partial => $"part needs TACT key {string.Join(", ", keys)}; never read with holes",
        ModelReadState.Rejected => $"rejected{Code(code)}{Message(message)}",
        // Every other model state is layer 4's read state of the same name.
        _ when Enum.TryParse<FileReadState>(state.ToString(), out var read) => ReadFailure(read, code, message, observedAt),
        _ => $"{state}{Code(code)}{Message(message)}",
    };

    /// <summary>The windows as hex lines and hole lines: adjacent bytes run on across windows, adjacent holes of one key merge.</summary>
    internal static List<string> HexLines(IReadOnlyList<FileRead> reads)
    {
        var bytes = new List<byte>();
        long bytesAt = -1;
        var lines = new List<string>();
        void Flush()
        {
            if (bytes.Count > 0) lines.AddRange(Hex(bytesAt, bytes));
            bytes.Clear();
            bytesAt = -1;
        }
        (long Offset, long Length, string Key)? hole = null;
        void FlushHole()
        {
            if (hole is { } h) lines.Add($"{h.Offset:x8}-{h.Offset + h.Length - 1:x8}  encrypted, needs TACT key {h.Key} ({h.Length:N0} bytes)");
            hole = null;
        }
        foreach (var read in reads)
        {
            var items = read.Segments.Select(s => (s.Offset, (long)s.Bytes.Length, (string?)null, (FileSegment?)s))
                .Concat(read.Holes.Select(h => (h.Offset, h.Length, (string?)h.KeyName, (FileSegment?)null)))
                .OrderBy(i => i.Item1);
            foreach (var (offset, length, key, segment) in items)
            {
                if (segment is { } s)
                {
                    FlushHole();
                    if (bytesAt >= 0 && bytesAt + bytes.Count != offset) Flush();
                    if (bytesAt < 0) bytesAt = offset;
                    bytes.AddRange(s.Bytes.Span);
                }
                else
                {
                    Flush();
                    if (hole is { } h && h.Key == key && h.Offset + h.Length == offset) hole = (h.Offset, h.Length + length, key!);
                    else { FlushHole(); hole = (offset, length, key!); }
                }
            }
        }
        Flush();
        FlushHole();
        return lines;
    }

    /// <summary>16 bytes a line: <c>00000000  xx xx xx xx xx xx xx xx  xx xx xx xx xx xx xx xx  |0123456789abcdef|</c>, 78 columns.</summary>
    private static IEnumerable<string> Hex(long offset, List<byte> bytes)
    {
        var line = new StringBuilder(80);
        for (var i = 0; i < bytes.Count; i += 16)
        {
            line.Clear();
            line.Append((offset + i).ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
            var n = Math.Min(16, bytes.Count - i);
            for (var j = 0; j < 16; j++)
            {
                if (j == 8) line.Append(' ');
                line.Append(j < n ? bytes[i + j].ToString("x2", CultureInfo.InvariantCulture) : "  ");
                if (j < 15) line.Append(' ');
            }
            line.Append("  |");
            for (var j = 0; j < n; j++) line.Append(bytes[i + j] is >= 0x20 and < 0x7f ? (char)bytes[i + j] : '.');
            line.Append('|');
            yield return line.ToString();
        }
    }

    /// <summary>What a failed read means for a read-only browser, in one line.</summary>
    internal static string ReadFailure(FileRead read) => ReadFailure(read.State, read.Code, read.Message, read.ObservedAt);

    private static string ReadFailure(FileReadState state, string? code, string? message, DateTimeOffset? observedAt) => state switch
    {
        FileReadState.NeedsNetwork => $"not stored{Code(code)}; g fetches it",
        FileReadState.Missing => $"Missing: the CDN answered 404{(observedAt is { } at ? " at " + BrowseSession.Time(at) + " UTC" : "")}{Code(code)}",
        FileReadState.Unavailable => $"Unavailable{Code(code)}{Message(message)}",
        FileReadState.WrongKey => $"WrongKey: a known TACT key didn't decrypt it{Code(code)}",
        FileReadState.UnknownFile or FileReadState.NotInLocale or FileReadState.NotInEncoding => $"not found now: {state}",
        _ => $"{state}{Code(code)}{Message(message)}",
    };

    private static string Message(string? message) => string.IsNullOrEmpty(message) ? "" : ": " + message;

    private static string Code(string? code) => code is null ? "" : $" ({code})";

    private static string Short(string key) => key.Length > 8 ? key[..8] : key;

    /// <summary>The last part of a listfile path (either slash), or null when there is none usable.</summary>
    private static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..].Trim();
        return name is "" or "." or ".." || name.Contains('\0') ? null : name;
    }

    private string InitialExportPath()
    {
        BuildFile f;
        lock (gate) f = current;
        var name = FileName(f.Path) ?? $"{f.FileDataId}{(Extension(f.Format) is { } ext ? "." + ext : "")}";
        return Path.Combine(Directory.GetCurrentDirectory(), name);
    }

    internal static string? Extension(FileFormat format) => format switch
    {
        FileFormat.Db2 => "db2",
        FileFormat.Blp => "blp",
        FileFormat.M2 or FileFormat.M2Chunked or FileFormat.M2Legacy => "m2",
        FileFormat.Skin => "skin",
        FileFormat.Anim => "anim",
        FileFormat.Bone => "bone",
        FileFormat.Phys => "phys",
        FileFormat.Skel => "skel",
        FileFormat.Tex => "tex",
        FileFormat.WmoRoot or FileFormat.WmoGroup => "wmo",
        FileFormat.AdtRoot or FileFormat.AdtTex or FileFormat.AdtObj or FileFormat.AdtLod => "adt",
        FileFormat.Wdt or FileFormat.WdtSplit => "wdt",
        FileFormat.Wdl => "wdl",
        FileFormat.Bls => "bls",
        FileFormat.Wwf => "wwf",
        FileFormat.Ogg => "ogg",
        FileFormat.Mp3 => "mp3",
        FileFormat.Wav => "wav",
        FileFormat.Ttf => "ttf",
        FileFormat.Otf => "otf",
        FileFormat.Ttc => "ttc",
        FileFormat.Png => "png",
        FileFormat.Jpeg => "jpg",
        FileFormat.Lua => "lua",
        FileFormat.Xml => "xml",
        FileFormat.Toc => "toc",
        FileFormat.Txt => "txt",
        FileFormat.Html => "html",
        _ => null,
    };

    private KeyOutcome Export(string typed)
    {
        var path = typed.Trim();
        if (path.Length == 0) return new(Say: "export: no path given; nothing written.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(Say: Fit($"export: not a usable path ({e.Message}); nothing written."));
        }
        return new(Start: new("export", (session, progress, ct) => ExportAsync(session, full, progress, ct)));
    }

    private async Task<string> ExportAsync(BrowseSession session, string path, IProgress<string> progress, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return Line("export: no folder ", folder ?? path, "; nothing written.");
        if (System.IO.File.Exists(path) || Directory.Exists(path)) return Line("export: ", path, " exists; nothing written.");
        progress.Report("reading");
        FileRead read;
        await using (var lease = await session.OpenFilesAsync(Target, ct).ConfigureAwait(false))
        {
            if (lease.Files is not { } files)
                return Fit($"export: the build didn't open: {lease.Result.State}{Code(lease.Result.Code)}; nothing written.");
            read = await files.GetAsync(FileRef.Id(id), ct).ConfigureAwait(false);
        }
        switch (read.State)
        {
            case FileReadState.Complete: break;
            case FileReadState.Partial or FileReadState.Protected:
                var n = read.Holes.Count;
                return Fit($"export: {n:N0} encrypted range{(n == 1 ? " needs a TACT key" : "s need TACT keys")} (never zero-filled); nothing written.");
            case FileReadState.NeedsNetwork:
                return Fit($"export: not stored; nothing fetched or written{Code(read.Code)}.");
            case FileReadState.TooLarge:
                return Fit($"export: TooLarge{Code(read.Code)}, over the whole-read limit; nothing written.");
            default:
                return Fit($"export: {read.State}{Code(read.Code)}; nothing written.");
        }
        var bytes = read.Bytes!.Value;
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.vmpart");
        try
        {
            progress.Report("writing");
            await using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous,
            }))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            if (AfterTempWritten is { } hook) await hook(temp, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            try { System.IO.File.Move(temp, path, overwrite: false); }
            catch (IOException) when (System.IO.File.Exists(path) || Directory.Exists(path))
            {
                return Line("export: ", path, " exists; nothing written.");
            }
        }
        finally
        {
            try { System.IO.File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return Line($"export: wrote {bytes.Length:N0} bytes to ", path, "");
    }

    /// <summary>A status line with a path in it, the path cut from the left so the line fits 80 columns where it can.</summary>
    private static string Line(string before, string path, string after)
    {
        var room = Columns - before.Length - after.Length;
        if (path.Length > room && room > 12) path = "..." + path[^(room - 3)..];
        return before + path + after;
    }

    private static string Fit(string line) => line.Length <= Columns ? line : line[..(Columns - 3)] + "...";
}
