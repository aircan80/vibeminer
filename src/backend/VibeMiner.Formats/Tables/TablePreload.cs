using VibeMiner.Builds;
using VibeMiner.Files;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// Where a table preload is (D360). Opening: the build's metadata (configs, ENCODING, ROOT, the archive indexes and file-index)
/// through layers 3 and 4. Downloading: one layer 4 Ensure of every listed table's DB2 file at Background priority, in merged
/// ranges. Preparing: each table not yet in the generated store is checked at Background priority, which builds its rows.
/// </summary>
public enum TablePreloadStage { Opening, Downloading, Preparing }

/// <summary>
/// A preload's figures; no field ever goes down from one report to the next, and the stage only moves forward. Files and bytes
/// are layer 4's <see cref="EnsureProgress"/> for the DB2 files (zero while Opening): done counts every file with an outcome,
/// failed ones too. <see cref="FilesFailed"/> are files that ended neither Stored nor AlreadyStored. <see cref="TablesTotal"/>
/// is the tables a definition may read (the ones Preparing checks or found already checked); <see cref="TablesDone"/> those
/// with a checked state, and <see cref="TablesFailed"/> those whose check ended in a state that isn't Ready, Partial or Encrypted.
/// A table whose file the ensure didn't store isn't opened (that would fetch outside the Background ensure): it is done in the
/// state the ensure gave it (Missing, NeedsNetwork, Corrupt for a refused copy, and so on), as a check would answer.
/// </summary>
public readonly record struct TablePreloadProgress(TablePreloadStage Stage, long FilesDone, long FilesTotal, long FilesFailed, long BytesDone,
    long BytesTotal, long CdnBytes, int TablesDone, int TablesTotal, int TablesFailed);

public enum TablePreloadOutcome
{
    /// <summary>Every listed DB2 file is stored and every table a definition reads has a checked state Ready, Partial or Encrypted.</summary>
    Completed,
    /// <summary>The preload ran to its end, but some file or table didn't (see the counts and <see cref="TablePreloadResult.Tables"/>).</summary>
    Incomplete,
    /// <summary>Stopped, or its owner was disposed. What was stored and prepared stays; a new preload starts from there.</summary>
    Canceled,
    /// <summary>The build couldn't be opened (the layer 4 state and code are kept), or something unexpected threw.</summary>
    Failed,
}

/// <summary>
/// One preload's one terminal result. <see cref="Final"/> is its last progress report. <see cref="Tables"/> holds the entry of
/// each table a definition may read that ended neither Ready, Partial nor Encrypted, ascending by FileDataID (bounded by the
/// build's table count); a canceled preload lists only those that ended so before it stopped. <see cref="OpenState"/>
/// is layer 4's open state when the build didn't open, else null.
/// </summary>
public sealed record TablePreloadResult(TablePreloadOutcome Outcome, TablePreloadProgress Final, IReadOnlyList<TableEntry> Tables,
    FilesOpenState? OpenState, string? Code, string? Message);
