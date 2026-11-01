using VibeMiner.Builds;
using VibeMiner.Formats.Definitions;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// A synthetic build's tables, described neutrally so each side of <see cref="TablesContract"/> can build it its own way: the
/// fake seeds them, the real service writes DB2 files and definitions from them. <see cref="MaxScanRows"/> is the service's
/// bound on rows one filtered page looks at. Synthetic data only.
/// </summary>
public sealed record TableScenario(BuildRef Build, string Locale, IReadOnlyList<ScenarioTable> Tables,
    int MaxScanRows = Formats.Tables.TableQueries.DefaultMaxScanRows);

/// <summary>
/// One column of a definition. Bits and Unsigned are for Int only; an array flattens to <c>Name[i]</c> columns.
/// </summary>
public sealed record ScenarioColumn(string Name, ColumnKind Kind, int? Bits = null, bool? Unsigned = null, int ArrayLength = 1,
    bool IsId = false, ForeignKey? ForeignKey = null)
{
    public static ScenarioColumn Id(string name = "ID") => new(name, ColumnKind.Int, 32, true, IsId: true);
    public static ScenarioColumn Int(string name, int bits = 32, bool unsigned = false, int arrayLength = 1, ForeignKey? foreignKey = null) =>
        new(name, ColumnKind.Int, bits, unsigned, arrayLength, ForeignKey: foreignKey);
    public static ScenarioColumn Text(string name) => new(name, ColumnKind.String);
    public static ScenarioColumn LocText(string name, int arrayLength = 1) => new(name, ColumnKind.LocString, ArrayLength: arrayLength);
    public static ScenarioColumn Float(string name, int arrayLength = 1) => new(name, ColumnKind.Float, ArrayLength: arrayLength);

    /// <summary>The flattened header column names this column gives.</summary>
    public IEnumerable<string> Flattened => ArrayLength == 1 ? [Name] : Enumerable.Range(0, ArrayLength).Select(i => $"{Name}[{i}]");
}

/// <summary>
/// A row with one exact string per flattened column, the ID column included. <see cref="BadText"/> are the flattened indexes of
/// string cells stored as invalid text: the reader gives "" there with a <see cref="Db2TextIssue"/>.
/// </summary>
public sealed record ScenarioRow(uint Id, IReadOnlyList<string> Values, IReadOnlyList<int>? BadText = null);

/// <summary>A section keyed with a TACT key this build doesn't know; <see cref="Ids"/> are its encrypted-ID list.</summary>
public sealed record ScenarioKeyedSection(string KeyName, IReadOnlyList<uint> Ids);

/// <summary>
/// One table. A null <see cref="Name"/> is a DB2 the definition manifest doesn't know (NoDefinition). <see cref="Rows"/> are the
/// readable rows in file order; a <see cref="Keyed"/> section adds rows nobody can read (Partial, or Encrypted with no readable
/// rows). <see cref="InBuild"/> false means the definition names a FileDataID the build doesn't list. <see cref="Stored"/> false
/// lists NotStored; a check fetches it. <see cref="Generated"/> means a check already put it in the generated store.
/// </summary>
public sealed record ScenarioTable(uint FileDataId, string? Name, IReadOnlyList<ScenarioColumn> Columns, IReadOnlyList<ScenarioRow> Rows,
    ScenarioKeyedSection? Keyed = null, bool InBuild = true, bool Stored = true, bool Generated = false);
