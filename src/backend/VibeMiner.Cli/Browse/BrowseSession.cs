using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;
using VibeMiner.Generated;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// What every view works against: the resolved data folder and the store in it. Read-only by default: views open the store
/// read-only for one load and close it; only an action (<c>r</c>, <c>a</c>, <c>m</c>, <c>g</c>/<c>G</c>) and the tables composition
/// ask for the writer lock, and they share one handle (<see cref="SharedWriter"/>). The Files views share one session-owned
/// read-only store and file system instead (<see cref="OpenFilesAsync"/>), since opening a build and loading the names are too
/// slow to redo per load. The profile's generated store (<c>generated-v1/</c>) is opened once per session, as its one writer, and
/// shared by the names index (#186), the tables composition and the BLP images (#199) (<see cref="GeneratedStoreAsync"/>); when another writer (the app)
/// holds it, names are off rather than failing the view. Dispose the session when browse exits: it closes the tables
/// composition, the files and then the generated store.
/// </summary>
internal sealed class BrowseSession : IAsyncDisposable
{
    private readonly IStoreOpener stores;
    private readonly SharedWriter writer;
    private readonly FilesCache files;
    private readonly Lock gate = new();
    private readonly Dictionary<BuildRef, MeasuredBuild> measured = [];
    private SessionTables? tables;

    public BrowseSession(IStoreOpener stores, DataFolder folder, string storeFolder, CliEnvironment? environment = null)
    {
        this.stores = stores;
        writer = new SharedWriter(stores, storeFolder);
        Folder = folder;
        StoreFolder = storeFolder;
        Environment = environment ?? CliEnvironment.Current();
        NameSwitch = new SettingsNameSwitch(folder.Path);
        // The seams are read when the store opens, so a test may set them after construction.
        FileSystem = DefaultFileSystemAsync;
        files = new FilesCache(OpenReadAsync, (objects, ct) => FileSystem(objects, Builds(objects), NameSwitch, ct));
        Preloads = new SessionPreloads(this);
        Concepts = new ConceptsCache(ct => OpenConcepts is { } open ? open(this, ct)
            : throw new InvalidOperationException("Layer 6 isn't wired into this session (UseConcepts)."));
    }

    /// <summary>
    /// The session's layer 6 handles (#204): the lease <see cref="OpenConcepts"/> gives, made once, and the build handles of the
    /// last <see cref="ConceptsCache.MaxBuilds"/> builds, kept so their relation indexes survive key presses.
    /// </summary>
    internal ConceptsCache Concepts { get; }

    /// <summary>The session's table preloads (#159), over the tables composition; stopped and awaited before it closes.</summary>
    public SessionPreloads Preloads { get; }

    public DataFolder Folder { get; }
    public string StoreFolder { get; }
    /// <summary>What the data folder was resolved from: the OS, environment and home, for <c>:settings</c>.</summary>
    public CliEnvironment Environment { get; }
    public IStoreOpener Stores => stores;

    /// <summary>The clock a remembered figure is dated by. A test seam.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Keeps the latest Downloaded figure of a build (from <c>d</c>, or the end of <c>a</c>), dated now. Measuring every build
    /// would mean opening each one, so <c>:settings</c> lists only these. Safe from any thread.
    /// </summary>
    public void Remember(BuildRef build, string versionsName, DownloadedFigure figure)
    {
        var at = Clock.GetUtcNow();
        lock (gate) measured[build] = new(build, versionsName, figure, at);
    }

    /// <summary>The remembered figures, newest first.</summary>
    public IReadOnlyList<MeasuredBuild> Measured
    {
        get { lock (gate) return [.. measured.Values.OrderByDescending(m => m.At).ThenBy(m => m.Build.Product, StringComparer.Ordinal)]; }
    }

    public Task<IStoreHandle> OpenReadAsync(CancellationToken ct) => stores.OpenReadOnlyAsync(StoreFolder, ct);

    /// <summary>
    /// A lease on the session's writer handle, taking the lock when nobody in the session holds it and creating a missing store
    /// then (as <c>vm builds refresh</c> does) unless <paramref name="create"/> is false. OBJECT_STORE_BUSY when another process
    /// (the app) holds it. Dispose the lease when done; the lock goes with the session's last lease.
    /// </summary>
    public Task<IStoreHandle> OpenWriterAsync(CancellationToken ct, bool create = true) => writer.AcquireAsync(create, ct);

    /// <summary>Writer leases out now. For tests.</summary>
    internal int WriterLeases => writer.Leases;

    /// <summary>The session's tables composition, once <see cref="UseTables"/> wired it.</summary>
    internal SessionTables? Tables => tables;

    /// <summary>Wires <see cref="OpenTables"/> to one composition for the whole session, made by <paramref name="compose"/> on first use.</summary>
    internal void UseTables(Func<IStoreHandle, string, bool, CancellationToken, Task<TablesLease>> compose)
    {
        tables = new SessionTables(this, compose);
        OpenTables = tables.LeaseAsync;
    }

    /// <summary>
    /// Opens layer 6 for one <c>:items</c> or <c>:creatures</c> load (#204); the view disposes the lease before the load returns.
    /// Null until <see cref="UseConcepts"/> wires it (the views then say so). A test seam too.
    /// </summary>
    internal Func<BrowseSession, CancellationToken, Task<ConceptsLease>>? OpenConcepts { get; set; }

    /// <summary>Wires <see cref="OpenConcepts"/> to the real layer 6 over the session's own layers (<see cref="ComposeConceptsAsync"/>).</summary>
    internal void UseConcepts() => OpenConcepts = ComposeConceptsAsync;

    /// <summary>
    /// Layer 6 over what the session already keeps, opening nothing twice (D472): the one tables composition
    /// (<see cref="OpenTables"/>), the session's read-only file system and its BLP images. Icons and models therefore read only
    /// stored bytes, with no TACT keys, as the Files views do; the lease releases its holds and closes nothing else.
    /// </summary>
    internal async Task<ConceptsLease> ComposeConceptsAsync(BrowseSession _, CancellationToken ct)
    {
        if (OpenTables is not { } open) throw new InvalidOperationException("Layer 6 needs the session's tables composition (UseTables).");
        var tables = await open(this, ct).ConfigureAwait(false);
        try
        {
            var hold = await files.HoldAsync(ct).ConfigureAwait(false);
            try
            {
                var images = await ImagesAsync(ct).ConfigureAwait(false);
                return ConceptsLease.Over(tables.Tables, hold.FileSystem, images, async () =>
                {
                    try { await hold.DisposeAsync().ConfigureAwait(false); }
                    finally { await tables.DisposeAsync().ConfigureAwait(false); }
                });
            }
            catch
            {
                await hold.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await tables.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The region a build opens in when it lists it and its route is usable (D280). There is no setting for it yet.</summary>
    public string PreferredRegion { get; init; } = "us";

    /// <summary>The region listing browse opens a build in, from layer 3's rule (D280), read over a store handle's objects.</summary>
    public Task<BuildRegionChoice> ChooseRegionAsync(IObjectSource objects, CatalogBuild build, CancellationToken ct) =>
        BuildRegions.ChooseAsync(objects, build, PreferredRegion, ct);

    /// <summary>Opens builds over a store handle's objects. A test seam: the real one is layer 3's <see cref="BuildOpener"/>.</summary>
    internal Func<IObjectSource, IBuilds> Builds { get; set; } = objects => new BuildOpener(objects);

    /// <summary>
    /// The community names switch the Files views' file system reads: <c>communityListfile</c> in the data folder's settings-v1.json
    /// (<see cref="SettingsNameSwitch"/>). A test seam.
    /// </summary>
    internal IFileNameSwitch NameSwitch { get; set; }

    /// <summary>
    /// Makes layer 4 over a store's objects and builds, with the names switch. A test seam: the real one is
    /// <see cref="VibeMiner.Files.FileSystem"/> with the session's generated store for names (<see cref="GeneratedStoreAsync"/>). Handles only read the stored copy; only <c>N</c>
    /// (<see cref="NamesTask"/>) calls <see cref="IFileNames.UpdateAsync"/>, on a file system over the writer's objects.
    /// </summary>
    internal Func<IObjectSource, IBuilds, IFileNameSwitch, CancellationToken, Task<IFileSystem>> FileSystem { get; set; }

    /// <summary>
    /// Opens the data folder's generated store at <c>generated-v1/</c>. A test seam: the real one is <see cref="GeneratedStore.OpenAsync"/>.
    /// </summary>
    internal Func<string, CancellationToken, Task<IGeneratedStore>> OpenGeneratedStore { get; set; } =
        async (folder, ct) => await GeneratedStore.OpenAsync(folder, cancellationToken: ct).ConfigureAwait(false);

    private readonly SemaphoreSlim generatedOpening = new(1, 1);
    private IGeneratedStore? generatedStore;

    private async Task<IFileSystem> DefaultFileSystemAsync(IObjectSource objects, IBuilds builds, IFileNameSwitch names, CancellationToken ct) =>
        new VibeMiner.Files.FileSystem(builds, objects, new FileSystemOptions { Names = names, NamesStore = await GeneratedStoreAsync(ct).ConfigureAwait(false) });

    /// <summary>
    /// The session's generated store (names index and tables), opened once per session; null leaves names off. GENERATED_STORE_BUSY (the app
    /// holds it) and any other failure to open it are null, and the next file system tries again.
    /// </summary>
    internal async Task<IGeneratedStore?> GeneratedStoreAsync(CancellationToken ct)
    {
        await generatedOpening.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (generatedStore is not null) return generatedStore;
            try { generatedStore = await OpenGeneratedStore(Path.Combine(Folder.Path, GeneratedStore.DefaultFolderName), ct).ConfigureAwait(false); }
            catch (Exception e) when (e is GeneratedStoreException or IOException or UnauthorizedAccessException) { return null; }
            return generatedStore;
        }
        finally { generatedOpening.Release(); }
    }

    private BlpImages? images;

    /// <summary>
    /// The session's layer 5 BLP images (#199), over the session's generated store, made once. The Files views' layer 4 decrypts
    /// with no TACT keys, so the images know none either (their cache identity must match what the handle decrypts with). When the
    /// generated store didn't open, an uncached one over <see cref="NoGeneratedStore"/> still inspects headers, which never touch it.
    /// </summary>
    internal async Task<BlpImages> ImagesAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref images) is { } made) return made;
        if (await GeneratedStoreAsync(ct).ConfigureAwait(false) is not { } store) return new BlpImages(NoGeneratedStore.Instance);
        var fresh = new BlpImages(store);
        return Interlocked.CompareExchange(ref images, fresh, null) ?? fresh;
    }

    /// <summary>
    /// Opens a build and locale through the session's file system, reusing the last one opened. Dispose the lease when the load
    /// ends. A missing store throws OBJECT_STORE_MISSING, as <see cref="OpenReadAsync"/> does; a failed open comes back in the lease's result.
    /// </summary>
    public Task<FilesLease> OpenFilesAsync(FilesTarget target, CancellationToken ct) => files.OpenAsync(target, ct);

    private OpenedFiles? lastFiles;

    /// <summary>The files target a Files view last opened (Open) in this session, which <c>:files</c> shows again; null before one.</summary>
    public OpenedFiles? LastFiles { get => Volatile.Read(ref lastFiles); set => Volatile.Write(ref lastFiles, value); }

    /// <summary>
    /// Drops the session's store, file system and open build: the next <see cref="OpenFilesAsync"/> opens them again. The layer 6
    /// handles over them go too (<see cref="ConceptsCache.InvalidateAsync"/>).
    /// </summary>
    public async ValueTask InvalidateFilesAsync()
    {
        // Files first: a layer 6 compose that starts after this then holds the new file system.
        try { await files.InvalidateAsync().ConfigureAwait(false); }
        finally { await Concepts.InvalidateAsync().ConfigureAwait(false); }
    }

    /// <summary>Stops and awaits the table preloads, then closes the tables composition and the session's files (each once no lease
    /// holds it), then the generated store. Call after every load has ended.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                try
                {
                    try { await Preloads.DisposeAsync().ConfigureAwait(false); }
                    // Layer 6 holds the tables lease and a file system hold, so it closes first.
                    finally { await Concepts.DisposeAsync().ConfigureAwait(false); }
                }
                finally { if (tables is not null) await tables.DisposeAsync().ConfigureAwait(false); }
            }
            finally { await files.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            if (Interlocked.Exchange(ref generatedStore, null) is IAsyncDisposable store) await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens layer 5 for one <c>:tables</c> load; the view disposes the lease before the load returns. Null until the composition
    /// root wires it, and the tables views then say tables aren't available. A test seam too.
    /// </summary>
    internal Func<BrowseSession, CancellationToken, Task<TablesLease>>? OpenTables { get; set; }

    /// <summary>
    /// The real tables composition for this session (<see cref="UseTables"/>): over the session's generated store and names switch,
    /// so names and tables share one writer of <c>generated-v1/</c>. When that store can't be opened, the composition opens its
    /// own, which reports GENERATED_STORE_BUSY as before.
    /// </summary>
    internal async Task<TablesLease> ComposeTablesAsync(IStoreHandle handle, string dataFolder, bool fetchDefinitions, CancellationToken ct) =>
        await GeneratedStoreAsync(ct).ConfigureAwait(false) is { } shared
            ? await TablesComposition.LeaseAsync(handle, shared, NameSwitch, fetchDefinitions, ct, Builds(handle.Objects)).ConfigureAwait(false)
            : await TablesComposition.LeaseAsync(handle, dataFolder, fetchDefinitions, ct).ConfigureAwait(false);

    /// <summary>Free bytes on the volume holding a folder, for Download all; throws when it can't be read. A test seam.</summary>
    internal Func<string, long> FreeSpace { get; set; } = folder => new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;

    public static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>One line for a failure a view or task hit: the store's named codes in words, anything else by type.</summary>
    public static string Explain(Exception e) => e switch
    {
        ObjectStoreException { Code: ObjectStoreCodes.Missing } s => $"{s.Code}: {s.Message} Nothing was created; r in :builds fetches builds and creates it.",
        ObjectStoreException { Code: ObjectStoreCodes.Busy } s => $"{s.Code}: another writer holds the object store (is VibeMiner running?).",
        ObjectStoreException s => $"{s.Code}: {s.Message}",
        VibeMiner.Generated.GeneratedStoreException { Code: VibeMiner.Generated.GeneratedStoreCodes.Busy } g => $"{g.Code}: another writer holds the generated store (is VibeMiner running?).",
        VibeMiner.Generated.GeneratedStoreException g => $"{g.Code}: {g.Message}",
        TablesBusyException t => $"{t.Code}: {t.Message} Wait for it to finish and try again.",
        _ => $"{e.GetType().Name}: {e.Message}",
    };
}

/// <summary>A generated store that holds nothing and refuses writes, for when the profile's one didn't open.</summary>
internal sealed class NoGeneratedStore : IGeneratedStore
{
    public static NoGeneratedStore Instance { get; } = new();
    public ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken cancellationToken = default) => ValueTask.FromResult(new GeneratedRead(GeneratedReadState.Absent));
    public ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new GeneratedRangeRead(GeneratedReadState.Absent));
    public Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken cancellationToken = default) =>
        throw new GeneratedStoreException(GeneratedStoreCodes.Busy, "The generated store didn't open; nothing is stored.");
    public Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken cancellationToken = default) =>
        throw new GeneratedStoreException(GeneratedStoreCodes.Busy, "The generated store didn't open.");
}

/// <summary>A build's Downloaded figure as it was when measured.</summary>
internal sealed record MeasuredBuild(BuildRef Build, string VersionsName, DownloadedFigure Figure, DateTimeOffset At);
