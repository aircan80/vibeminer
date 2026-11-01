using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// What every view works against: the resolved data folder and the store in it. Read-only by default: views open the store
/// read-only for one load and close it; only an action (<c>r</c>) asks for the writer lock.
/// </summary>
internal sealed class BrowseSession(IStoreOpener stores, DataFolder folder, string storeFolder, CliEnvironment? environment = null)
{
    private readonly Lock gate = new();
    private readonly Dictionary<BuildRef, MeasuredBuild> measured = [];

    public DataFolder Folder { get; } = folder;
    public string StoreFolder { get; } = storeFolder;
    /// <summary>What the data folder was resolved from: the OS, environment and home, for <c>:settings</c>.</summary>
    public CliEnvironment Environment { get; } = environment ?? CliEnvironment.Current();
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

    /// <summary>Opens builds over a store handle's objects. A test seam: the real one is layer 3's <see cref="BuildOpener"/>.</summary>
    internal Func<IObjectSource, IBuilds> Builds { get; set; } = objects => new BuildOpener(objects);

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
