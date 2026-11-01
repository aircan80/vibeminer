namespace VibeMiner.Generated.RecordFiles;

/// <summary>Bounds and tuning of a <see cref="RecordFileWriter"/>. Going over a limit throws <see cref="GeneratedStoreException"/> (Limit).</summary>
public sealed record RecordFileOptions
{
    /// <summary>The most records in one file.</summary>
    public int MaxRecords { get; init; } = 4 << 20;
    /// <summary>The largest record.</summary>
    public int MaxRecordBytes { get; init; } = 1 << 20;
    /// <summary>The largest metadata blob.</summary>
    public int MaxMetadataBytes { get; init; } = 16 << 20;
    /// <summary>A block is closed once its uncompressed body reaches this size.</summary>
    public int BlockBytes { get; init; } = 16 << 10;
    /// <summary>The most bytes in one segment object, unless one frame alone is larger.</summary>
    public int SegmentBytes { get; init; } = 8 << 20;
    /// <summary>The zstd level of blocks, id chunks and the manifest.</summary>
    public int CompressionLevel { get; init; } = 9;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRecords, 0, nameof(MaxRecords));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRecords, 64 << 20, nameof(MaxRecords));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRecordBytes, 0, nameof(MaxRecordBytes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRecordBytes, 16 << 20, nameof(MaxRecordBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMetadataBytes, 0, nameof(MaxMetadataBytes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMetadataBytes, 16 << 20, nameof(MaxMetadataBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(BlockBytes, 1, nameof(BlockBytes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BlockBytes, 1 << 20, nameof(BlockBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(SegmentBytes, 1, nameof(SegmentBytes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SegmentBytes, 32 << 20, nameof(SegmentBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(CompressionLevel, 1, nameof(CompressionLevel));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CompressionLevel, 19, nameof(CompressionLevel));
    }
}

/// <summary>
/// What <see cref="RecordFileWriter.CompleteAsync"/> wrote. <see cref="UncompressedBytes"/> and <see cref="CompressedBytes"/> are the
/// record blocks; <see cref="IdIndexBytes"/> the compressed id chunks; <see cref="ManifestBytes"/> the manifest this writer built.
/// <see cref="ManifestWasPresent"/>: a manifest was already stored at the key, and that one wins; the store kept it and this
/// writer's manifest was dropped, so the key reads back the earlier file.
/// </summary>
public sealed record RecordFileInfo(
    GeneratedKey Manifest, long Count, long UncompressedBytes, long CompressedBytes, long IdIndexBytes, long ManifestBytes,
    int Blocks, int IdChunks, int Segments, bool ManifestWasPresent);
