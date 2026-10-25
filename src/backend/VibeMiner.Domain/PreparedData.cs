namespace VibeMiner.Domain;

/// <summary>
/// One build's file catalog on disk (D071, D085). State is the file catalog state, or "none" when the build never queued
/// one. Bytes and Files are the lengths and count of its files (the catalog, -wal, -shm and -journal of every generation
/// of this build) as found on disk when measured; only this build uses them.
/// </summary>
public sealed record PreparedFileCatalog(string State, string Bytes, int Files);

/// <summary>
/// The table row files this build's table admissions use: their count and verified byte length, each file counted once,
/// and how many of those bytes another build's admissions also use. Kept by Free (D085).
/// </summary>
public sealed record PreparedTables(int Count, string Bytes, string SharedBytes);

/// <summary>
/// The build's stored download figure (D065): "notMeasured" (no measured download manifest), "counting", "current" or
/// "unavailable". Stored objects are content-addressed and may be shared with other builds. Kept by Free (D085).
/// </summary>
public sealed record PreparedDownloads(string State, string? StoredObjects, string? StoredBytes);

/// <summary>What one opened build holds locally, measured when read (D085).</summary>
public sealed record PreparedBuild(string ProductId, string BuildIdentity, string? BuildVersion, string LastOpenedAt,
    PreparedFileCatalog FileCatalog, PreparedTables Tables, PreparedDownloads Downloads);

/// <summary>Every opened build, most recently opened first, at most 256; Truncated says more were opened.</summary>
public sealed record PreparedDataList(IReadOnlyList<PreparedBuild> Builds, bool Truncated, string MeasuredAt);

/// <summary>The outcome of freeing one build's file catalog: the bytes and files deleted, and the catalog's state after.</summary>
public sealed record PreparedDataFreed(string ProductId, string BuildIdentity, string FreedBytes, int FreedFiles, PreparedFileCatalog FileCatalog);
