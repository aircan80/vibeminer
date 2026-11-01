using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.Formats.Tables;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// What every view works against: the resolved data folder and the store in it. Read-only by default: views open the store
/// read-only for one load and close it; only an action (<c>r</c>, <c>a</c>, <c>m</c>, <c>g</c>/<c>G</c>) and the tables composition
/// ask for the writer lock, and they share one handle (<see cref="SharedWriter"/>). The Files views share one session-owned
/// read-only store and file system instead (<see cref="OpenFilesAsync"/>), since opening a build and loading the names are too
/// slow to redo per load. Dispose the session when browse exits: it closes the tables composition and the files.
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
        files = new FilesCache(OpenReadAsync, objects => FileSystem(objects, Builds(objects), NameSwitch));
    }

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
    /// Makes layer 4 over a read-only store's objects and builds, with the names switch. A test seam: the real one is
    /// <see cref="VibeMiner.Files.FileSystem"/>. Browse never fetches names: handles only read the stored copy, and nothing calls
    /// <see cref="IFileNames.UpdateAsync"/>.
    /// </summary>
    internal Func<IObjectSource, IBuilds, IFileNameSwitch, IFileSystem> FileSystem { get; set; } =
        (objects, builds, names) => new VibeMiner.Files.FileSystem(builds, objects, new FileSystemOptions { Names = names });

    /// <summary>
    /// Opens a build and locale through the session's file system, reusing the last one opened. Dispose the lease when the load
    /// ends. A missing store throws OBJECT_STORE_MISSING, as <see cref="OpenReadAsync"/> does; a failed open comes back in the lease's result.
    /// </summary>
    public Task<FilesLease> OpenFilesAsync(FilesTarget target, CancellationToken ct) => files.OpenAsync(target, ct);

    private OpenedFiles? lastFiles;

    /// <summary>The files target a Files view last opened (Open) in this session, which <c>:files</c> shows again; null before one.</summary>
    public OpenedFiles? LastFiles { get => Volatile.Read(ref lastFiles); set => Volatile.Write(ref lastFiles, value); }

    /// <summary>Drops the session's store, file system and open build: the next <see cref="OpenFilesAsync"/> opens them again.</summary>
    public ValueTask InvalidateFilesAsync() => files.InvalidateAsync();

    /// <summary>Closes the tables composition and the session's files (each once no lease holds it). Call after every load has ended.</summary>
    public async ValueTask DisposeAsync()
    {
        try { if (tables is not null) await tables.DisposeAsync().ConfigureAwait(false); }
        finally { await files.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>
    /// Opens layer 5 for one <c>:tables</c> load; the view disposes the lease before the load returns. Null until the composition
    /// root wires it, and the tables views then say tables aren't available. A test seam too.
    /// </summary>
    internal Func<BrowseSession, CancellationToken, Task<TablesLease>>? OpenTables { get; set; }

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

/// <summary>A build's Downloaded figure as it was when measured.</summary>
internal sealed record MeasuredBuild(BuildRef Build, string VersionsName, DownloadedFigure Figure, DateTimeOffset At);
