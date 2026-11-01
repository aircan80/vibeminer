using System.Globalization;
using VibeMiner.Formats.Definitions;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// A table asked for by FileDataID or by definition name (ASCII case doesn't matter). The FileDataID is the key; a name is
/// looked up in the definition manifest for this build, and a name it doesn't know answers NoDefinition with FileDataID 0.
/// FileDataID 0 and an empty name throw <see cref="ArgumentException"/>; so does <c>default(TableRef)</c> when used.
/// </summary>
public readonly record struct TableRef
{
    private TableRef(uint? id, string? name) { FileDataId = id; Name = name; }

    public uint? FileDataId { get; }
    public string? Name { get; }

    public static TableRef Id(uint fileDataId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(fileDataId);
        return new(fileDataId, null);
    }

    public static TableRef ForName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(null, name);
    }

    public static implicit operator TableRef(uint fileDataId) => Id(fileDataId);

    public override string ToString() => FileDataId is { } id ? id.ToString(CultureInfo.InvariantCulture) : Name ?? "";
}

/// <summary>
/// What a table is in this build and locale. Shallow states come from a stat and a local definition lookup; checked states
/// from opening the table. NotChecked is never shown as Ready. Missing is layer 2's dated 404, never proof of absence.
/// </summary>
public enum TableState
{
    /// <summary>Shallow: stored, a definition covers the build, not opened yet.</summary>
    NotChecked,
    /// <summary>Checked: opened, every section readable, rows in the generated store.</summary>
    Ready,
    /// <summary>Checked: opened, some keyed sections hidden (their key isn't known) or BLTE holes; <see cref="TableEntry.Counts"/> says how much.</summary>
    Partial,
    /// <summary>The file is wholly in holes, or opens with every section keyed and hidden (0 readable rows); the reason says which.</summary>
    Encrypted,
    /// <summary>Shallow: listed, bytes not stored. A check fetches.</summary>
    NotStored,
    /// <summary>Checked: not stored, and layer 2 is local only.</summary>
    NeedsNetwork,
    /// <summary>The definition's FileDataID isn't in this build (layer 4 UnknownFile).</summary>
    NotInBuild,
    NotInLocale,
    NotInEncoding,
    /// <summary>No definition reads the table (DefinitionState NotInProvider, NoMatch, FetchingOff), or a DB2 the manifest doesn't know.</summary>
    NoDefinition,
    /// <summary>A definition exists but can't be used (FetchFailed, Unreadable, Ambiguous, BlockUnsupported; reader DB2_SCHEMA_*).</summary>
    DefinitionUnusable,
    /// <summary>Layer 4 Unsupported, or the reader's DB2_UNSUPPORTED.</summary>
    Unsupported,
    /// <summary>Layer 4 TooLarge, or the reader's DB2_LIMIT.</summary>
    TooLarge,
    /// <summary>Any other reader refusal (DB2_INVALID and the like); the reason keeps its code.</summary>
    Rejected,
    Corrupt,
    Missing,
    Unavailable,
    WrongKey,
    StoreFailed,
}

/// <summary>
/// Why a table is in its state. <see cref="Code"/>, <see cref="Message"/> and <see cref="ObservedAt"/> are the layer's that
/// said so (layer 4, layer 2, the definitions or the reader), kept as they came. <see cref="KeyNames"/> are the TACT key names
/// (16 upper hex) the table's unreadable parts need, sorted and distinct.
/// </summary>
public sealed record TableStateReason(string? Code, string? Message, DateTimeOffset? ObservedAt, DefinitionState? DefinitionState,
    Db2OpenState? Db2OpenState, IReadOnlyList<string> KeyNames);

/// <summary>
/// The scope of an opened table. <see cref="DeclaredRows"/> is what the header declared (records plus copies);
/// <see cref="ReadableRows"/> the physical rows and copies read. <see cref="ExcludedSections"/> and
/// <see cref="ExcludedRecords"/> are what hidden keyed sections (no key, so layer 4 left a hole) left out; a decrypted keyed section is read. <see cref="Format"/> is the header magic ("WDC5").
/// </summary>
public sealed record TableCounts(long DeclaredRows, long ReadableRows, int ExcludedSections, long ExcludedRecords, string Format, string LayoutHash);

/// <summary>
/// One table in one build and locale. <see cref="FileDataId"/> is 0 only for a name nobody knows. <see cref="Name"/> is the
/// definition name, null for a DB2 the manifest doesn't know. <see cref="Checked"/> is true when the state comes from a check
/// (here or earlier, through the generated store), false when it is shallow. <see cref="Counts"/> is set once the table opened.
/// <see cref="ContentKey"/> is the file's CKey (32 lower hex) when the build lists the file.
/// </summary>
public sealed record TableEntry(uint FileDataId, string? Name, TableState State, bool Checked, TableStateReason? Reason, TableCounts? Counts,
    string? ContentKey);

/// <summary>
/// What <see cref="IBuildTables.ListAsync"/> returns. Every set criterion must hold. <see cref="Name"/> is a literal substring of
/// the definition name, ASCII case-insensitive; it matches only named tables, and <c>""</c> matches every named table. A null
/// <see cref="States"/> means any state.
/// </summary>
public sealed record TableListFilter
{
    public static TableListFilter All { get; } = new();

    public string? Name { get; init; }
    public IReadOnlySet<TableState>? States { get; init; }
}

/// <summary>
/// A table's columns. <see cref="Columns"/> are the header's flattened columns (<c>Flags[1]</c>) in row value order, set once the
/// table opened. <see cref="Schema"/> is the resolved definition block, set when a definition resolves for this build (and the
/// file's layout, when it was read); its foreign keys are what follow and referenced-by use.
/// </summary>
public sealed record TableSchemaResult(TableEntry Table, IReadOnlyList<Db2Column>? Columns, TableSchema? Schema);
