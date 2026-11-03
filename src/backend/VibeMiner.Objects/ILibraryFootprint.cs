namespace VibeMiner.Objects;

/// <summary>
/// Metadata-only logical file lengths observed over a borrowed mixed library during an interval. These are neither allocated
/// disk blocks nor verified payload/retention or reclaimable bytes. Dispose cancels and drains this view, leaving the library open.
/// Descriptor-relative confinement currently supports Linux and macOS on x64/arm64; other platforms report UnsupportedPlatform.
/// </summary>
public interface ILibraryFootprint : IAsyncDisposable
{
    ValueTask<LibraryFootprintSample> MeasureAsync(CancellationToken ct = default);
}

public sealed record LibraryFootprintBucket(long Files, long Bytes);
public enum LibraryFootprintProblemCode
{
    MissingRequiredPath, Unreadable, Disappeared, Link, UnsupportedEntry, EntryLimit, DepthLimit, PathLimit,
    Overflow, UnsupportedPlatform, ChangedDirectory
}
/// <summary>Relative paths use '/' separators; the empty path identifies the captured library root.</summary>
public sealed record LibraryFootprintProblem(string RelativePath, LibraryFootprintProblemCode Code);
/// <summary>
/// Disjoint observed regular-file lengths. Complete means traversal finished without observed gaps, not an atomic snapshot.
/// Required paths are the root, packs/, staging/ and objects.sqlite3; sidecars and individual pack/staging files are optional.
/// Unknown regular names, library-v1 and writer.lock belong to Other. Mixed packs are not split by logical role.
/// </summary>
public sealed record LibraryFootprintSample(DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    LibraryFootprintBucket Packs, LibraryFootprintBucket Database, LibraryFootprintBucket DatabaseWal,
    LibraryFootprintBucket DatabaseShm, LibraryFootprintBucket Staging, LibraryFootprintBucket Other,
    LibraryFootprintBucket Total, long ExaminedEntries, bool Complete,
    IReadOnlyList<LibraryFootprintProblem> Problems, bool TruncatedDiagnostics);

public sealed record LibraryFootprintOptions
{
    public int MaxEntries { get; init; } = 100_000;
    public int MaxDepth { get; init; } = 16;
    public int MaxPathLength { get; init; } = 2048;
    public int MaxProblems { get; init; } = 32;
    /// <summary>Fail-fast admission per view, additionally bounded by two accepted measurements across this library.</summary>
    public int MaxConcurrentMeasurements { get; init; } = 1;
    public TimeProvider Time { get; init; } = TimeProvider.System;
    internal Action<LibraryFootprintPoint, string>? Fault { get; init; }
    internal Func<string, long, long>? LengthOverride { get; init; }

    internal void Validate()
    {
        if (MaxEntries is < 1 or > 1_000_000 || MaxDepth is < 1 or > 32 || MaxPathLength is < 32 or > 4096 ||
            MaxProblems is < 1 or > 64 || MaxConcurrentMeasurements is < 1 or > 2 || Time is null)
            throw new ArgumentException("Library footprint limits are invalid.");
    }
}

internal enum LibraryFootprintPoint
{
    MeasurementStarted, BeforeRootOpen, BeforeDirectoryOpen, BeforeEntryMetadata, BeforeFileLength, MeasurementFinishing
}

public static class LibraryFootprintCodes
{
    public const string Busy = "LIBRARY_FOOTPRINT_BUSY";
    public const string Closed = "LIBRARY_FOOTPRINT_CLOSED";
}
