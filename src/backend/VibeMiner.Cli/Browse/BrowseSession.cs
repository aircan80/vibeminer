using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// What every view works against: the resolved data folder and the store in it. Read-only by default: views open the store
/// read-only for one load and close it; only an action (<c>r</c>) asks for the writer lock. The Files views share one
/// session-owned read-only store and file system instead (<see cref="OpenFilesAsync"/>), since opening a build and loading the
/// names are too slow to redo per load. Disposing the session closes them.
/// </summary>
internal sealed class BrowseSession : IAsyncDisposable
{
    private readonly IStoreOpener stores;
    private readonly FilesCache files;
    private readonly Lock gate = new();
    private readonly Dictionary<BuildRef, MeasuredBuild> measured = [];

    public BrowseSession(IStoreOpener stores, DataFolder folder, string storeFolder, CliEnvironment? environment = null)
    {
        this.stores = stores;
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

    /// <summary>Takes the writer lock, creating a missing store (as <c>vm builds refresh</c> does). OBJECT_STORE_BUSY when the app holds it.</summary>
    public Task<IStoreHandle> OpenWriterAsync(CancellationToken ct) => stores.OpenWriterAsync(StoreFolder, create: true, ct);

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

    /// <summary>Closes the session's files (each once no lease holds it). Call after every load has ended.</summary>
    public ValueTask DisposeAsync() => files.DisposeAsync();

    /// <summary>Free bytes on the volume holding a folder, for Download all; throws when it can't be read. A test seam.</summary>
    internal Func<string, long> FreeSpace { get; set; } = folder => new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;

    public static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>One line for a failure a view or task hit: the store's named codes in words, anything else by type.</summary>
    public static string Explain(Exception e) => e switch
    {
        ObjectStoreException { Code: ObjectStoreCodes.Missing } s => $"{s.Code}: {s.Message} Nothing was created; r in :builds fetches builds and creates it.",
        ObjectStoreException { Code: ObjectStoreCodes.Busy } s => $"{s.Code}: another writer holds the object store (is VibeMiner running?).",
        ObjectStoreException s => $"{s.Code}: {s.Message}",
        _ => $"{e.GetType().Name}: {e.Message}",
    };
}

/// <summary>A build's Downloaded figure as it was when measured.</summary>
internal sealed record MeasuredBuild(BuildRef Build, string VersionsName, DownloadedFigure Figure, DateTimeOffset At);
