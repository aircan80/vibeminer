using VibeMiner.Builds;
using VibeMiner.Formats.Generated;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// How <see cref="TablesService"/> reads. <see cref="BuildVersion"/> gives a build's exact four-part version (such as 11.0.2.56196), the
/// one definitions list; null or anything else means the build's tables can't be read, and opening it answers Unsupported
/// (TABLES_BUILD_VERSION_UNKNOWN). <see cref="Limits"/> are the DB2 reader's; keyed sections and invalid sparse text are always
/// admitted (<see cref="Db2Limits.AllowPartialEncryptedSections"/> and <see cref="Db2Limits.AllowRawTextEvidence"/> are set whatever
/// is given), so invalid text reads as "" with a <see cref="Db2TextIssue"/>. <see cref="Keys"/> is the TACT key status tables are
/// filed under in the generated store, asked once per call; by default no key is known. <see cref="MaxScanRows"/> bounds the rows
/// one filtered page looks at, and one follow or one referenced-by source scans. <see cref="MaxReferenceSources"/> and
/// <see cref="MaxReferenceScanRows"/> bound one referenced-by's sources and its rows scanned across them. Setting any of them to
/// null (or MaxScanRows below 1, or a reference bound below 0) throws. <see cref="Scheduler"/> runs the expensive work by priority
/// (D263); null, the default, gives each service its own. Give one instance to every service that should share the slots.
/// </summary>
public sealed record TablesOptions(Func<BuildRef, CancellationToken, Task<string?>> BuildVersion)
{
    private readonly Func<BuildRef, CancellationToken, Task<string?>> buildVersion = BuildVersion ?? throw new ArgumentNullException(nameof(BuildVersion));
    private readonly Db2Limits limits = new();
    private readonly Func<BuildRef, ITactKeyStatus> keys = _ => TactKeyStatus.None;
    private readonly int maxScanRows = TableQueries.DefaultMaxScanRows;
    private readonly int maxReferenceSources = TableQueries.DefaultMaxReferenceSources;
    private readonly long maxReferenceScanRows = TableQueries.DefaultMaxReferenceScanRows;

    public Func<BuildRef, CancellationToken, Task<string?>> BuildVersion
    {
        get => buildVersion;
        init => buildVersion = value ?? throw new ArgumentNullException(nameof(BuildVersion));
    }

    public Db2Limits Limits { get => limits; init => limits = value ?? throw new ArgumentNullException(nameof(Limits)); }

    public Func<BuildRef, ITactKeyStatus> Keys { get => keys; init => keys = value ?? throw new ArgumentNullException(nameof(Keys)); }

    public TableWorkScheduler? Scheduler { get; init; }

    public int MaxScanRows
    {
        get => maxScanRows;
        init { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxScanRows)); maxScanRows = value; }
    }

    public int MaxReferenceSources
    {
        get => maxReferenceSources;
        init { ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxReferenceSources)); maxReferenceSources = value; }
    }

    public long MaxReferenceScanRows
    {
        get => maxReferenceScanRows;
        init { ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxReferenceScanRows)); maxReferenceScanRows = value; }
    }
}
