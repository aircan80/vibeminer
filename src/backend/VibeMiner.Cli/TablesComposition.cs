using System.Text.RegularExpressions;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tables;
using VibeMiner.Generated;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Cli;

/// <summary>
/// The CLI's composition root for layer 5 (#180): over one open store handle, layer 3's <see cref="BuildOpener"/>, layer 4 file
/// systems, the bundled definitions (fetched WoWDBDefs ones only when asked, D081), the generated store at
/// <c>generated-v1</c> in the data folder (D210, created when missing) and <see cref="TablesService"/>. A read-only handle
/// reads only what is stored (anything else answers NeedsNetwork); a writer handle fetches from the official CDN.
/// <para>
/// TACT keys (D260): the first <see cref="ForAsync"/> for a build opens that build's tables with no keys, reads its TactKey and
/// TactKeyLookup tables with <see cref="BuildTactKeys.LoadAsync"/>. Every handle is then opened by its own layer 5 service over
/// a file system made with one set, the snapshot taken at that open, and the generated store files its tables under the same
/// snapshot's fingerprints; evicting or re-reading the set later never changes an open handle. One build's keys never reach
/// another build. Only a complete load is kept (<see cref="IsComplete"/>), at most <see cref="MaxKeyedBuilds"/> of them, the
/// oldest going first; an incomplete one serves its open and is read again next time.
/// </para>
/// Dispose the handles it gave first, then this (it closes the generated store), then the store handle, which the caller owns.
/// <c>vm browse</c> keeps one for the whole session (<see cref="Browse.SessionTables"/>).
/// </summary>
internal sealed partial class TablesComposition : ITables, IAsyncDisposable
{
    public const int MaxKeyedBuilds = 8;

    private readonly IBuilds builds;
    private readonly IAsyncDisposable? store;
    private readonly Func<IBuilds, FileSystemOptions, TablesOptions, ITables> service;
    private readonly TablesOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<BuildRef, BuildTactKeySet> keys = [];
    private readonly Queue<BuildRef> keyOrder = new();
    private int disposed;

    private TablesComposition(IStoreHandle handle, GeneratedStore store, TableDefinitions definitions)
        : this(handle.Objects, store, Real(handle.Objects, definitions, new GeneratedTables(store))) { }

    private static Func<IBuilds, FileSystemOptions, TablesOptions, ITables> Real(IObjectSource objects, TableDefinitions definitions, GeneratedTables generated) =>
        (builds, files, options) => new TablesService(new FileSystem(builds, objects, files), definitions, generated, options);

    /// <summary>
    /// The seam under the real composition: <paramref name="service"/> makes layer 5 for one open, over the shared build opener,
    /// with the layer 4 options and tables options of that open (no keys for the bootstrap; one build's snapshot otherwise).
    /// <paramref name="store"/> is closed on dispose.
    /// </summary>
    internal TablesComposition(IObjectSource objects, IAsyncDisposable? store, Func<IBuilds, FileSystemOptions, TablesOptions, ITables> service)
    {
        builds = new BuildOpener(objects);
        this.store = store;
        this.service = service;
        // One scheduler for every handle this composition opens, so their priorities share the slots (D263).
        options = new TablesOptions(VersionAsync) { Scheduler = new TableWorkScheduler() };
    }

    /// <summary>The generated store's folder in a data folder.</summary>
    public static string GeneratedFolder(string dataFolder) => Path.Combine(Path.GetFullPath(dataFolder), GeneratedStore.DefaultFolderName);

    /// <summary>
    /// Composes layer 5 over <paramref name="handle"/>. <paramref name="fetchDefinitions"/> turns on fetching newer WoWDBDefs
    /// definitions (D081) for this run; it fetches only through a writer handle. Throws <see cref="GeneratedStoreException"/>
    /// when the generated store can't be opened (GENERATED_STORE_BUSY while the app holds it).
    /// </summary>
    public static async Task<TablesComposition> OpenAsync(IStoreHandle handle, string dataFolder, bool fetchDefinitions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var bundled = await BundledDefinitions.OpenAsync(ct).ConfigureAwait(false);
        var definitions = fetchDefinitions
            ? new TableDefinitions(bundled, new FetchedDefinitions(handle.Objects), new FixedSwitch(true))
            : new TableDefinitions(bundled, null, null);
        var store = await GeneratedStore.OpenAsync(GeneratedFolder(dataFolder), cancellationToken: ct).ConfigureAwait(false);
        return new TablesComposition(handle, store, definitions);
    }

    /// <summary>
    /// A <see cref="TablesLease"/> over an open store handle: closing it disposes the composition, then the handle. When
    /// composing throws, the caller still owns the handle.
    /// </summary>
    public static async Task<TablesLease> LeaseAsync(IStoreHandle handle, string dataFolder, bool fetchDefinitions, CancellationToken ct)
    {
        var tables = await OpenAsync(handle, dataFolder, fetchDefinitions, ct).ConfigureAwait(false);
        return new TablesLease(tables, async () =>
        {
            try { await tables.DisposeAsync().ConfigureAwait(false); }
            finally { await handle.DisposeAsync().ConfigureAwait(false); }
        });
    }

    public async Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(build);
        var (set, failure) = await KeysForAsync(build, locale, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;
        // One snapshot for both sides: what layer 4 decrypts with is what the generated store files the rows under.
        var snapshot = set!;
        return await service(builds, new FileSystemOptions { Keys = snapshot.Keys },
            options with { Keys = b => b == build ? snapshot.Status : TactKeyStatus.None }).ForAsync(build, locale, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The kept key set of a build, or null when none is kept. For tests and reports.</summary>
    public BuildTactKeySet? KeysOf(BuildRef build)
    {
        lock (keys) return keys.GetValueOrDefault(build);
    }

    /// <summary>
    /// A load is complete when neither table was Stale and neither ended in a state that may change on a retry (not yet
    /// looked at, not stored, no network, a 404 or an unavailable CDN, a store failure). Only a complete load is kept.
    /// </summary>
    internal static bool IsComplete(BuildTactKeySet set) =>
        Settled(set.KeyTable) && Settled(set.LookupTable);

    internal static bool Settled(BuildTactKeyTable table) => table.Outcome != BuildTactKeyTableOutcome.Stale
        && table.Table.State is not (TableState.NotChecked or TableState.NotStored or TableState.NeedsNetwork or TableState.Missing
            or TableState.Unavailable or TableState.StoreFailed);

    /// <summary>
    /// The build's key set: the kept one, else read now through a keyless open. A failed keyless open is the answer for this
    /// call; an incomplete load serves this call only. Neither is kept.
    /// </summary>
    private async Task<(BuildTactKeySet? Set, TablesOpenResult? Failure)> KeysForAsync(BuildRef build, string locale, CancellationToken ct)
    {
        if (KeysOf(build) is { } kept) return (kept, null);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (KeysOf(build) is { } raced) return (raced, null);
            var opened = await service(builds, new FileSystemOptions(), options).ForAsync(build, locale, ct).ConfigureAwait(false);
            if (opened is not { State: FilesOpenState.Open, Tables: { } tables }) return (null, opened);
            BuildTactKeySet set;
            await using (tables) set = await BuildTactKeys.LoadAsync(tables, TablePriority.Interactive, ct).ConfigureAwait(false);
            if (IsComplete(set))
                lock (keys)
                {
                    while (keys.Count >= MaxKeyedBuilds) keys.Remove(keyOrder.Dequeue());
                    keys[build] = set;
                    keyOrder.Enqueue(build);
                }
            return (set, null);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The build's exact four-part version for definitions, from layer 3's build config: <c>build-name</c> such as
    /// <c>WOW-63796patch11.2.5_Retail</c> gives <c>11.2.5.63796</c>. Null when the build doesn't open or the name doesn't parse.
    /// </summary>
    private async Task<string?> VersionAsync(BuildRef build, CancellationToken ct)
    {
        var opened = await builds.OpenAsync(build, ct).ConfigureAwait(false);
        return opened is { State: BuildOpenState.Open, Opened: { } b } ? VersionOf(b.Metadata.BuildName) : null;
    }

    internal static string? VersionOf(string? buildName)
    {
        if (buildName is null) return null;
        var match = BuildNamePattern().Match(buildName);
        return match.Success ? $"{match.Groups[2].Value}.{match.Groups[1].Value}" : null;
    }

    [GeneratedRegex(@"^WOW-(\d{1,9})patch(\d{1,9}\.\d{1,9}\.\d{1,9})(?:_|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BuildNamePattern();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }

    private sealed class FixedSwitch(bool on) : IDefinitionFetchSwitch
    {
        public Task<bool> FetchEnabledAsync(CancellationToken token) => Task.FromResult(on);
    }
}
