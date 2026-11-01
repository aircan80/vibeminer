using VibeMiner.Files;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Generated;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// How layer 4, the definitions, the DB2 reader and the generated store say a table's state. Codes, messages and times are
/// kept as each layer gave them.
/// </summary>
internal static class TableStates
{
    public const string NotInManifest = "TABLE_NOT_IN_MANIFEST";
    public const string AllSectionsKeyed = "TABLE_ALL_SECTIONS_KEYED";

    /// <summary>A stat that found no readable entry: UnknownFile, NotInLocale or NotInEncoding. Null for Found.</summary>
    public static TableState? Lookup(FileLookupState state) => state switch
    {
        FileLookupState.Found => null,
        FileLookupState.NotInLocale => TableState.NotInLocale,
        FileLookupState.NotInEncoding => TableState.NotInEncoding,
        _ => TableState.NotInBuild,
    };

    /// <summary>
    /// A definition that can't read the table. NotInProvider, NoMatch and FetchingOff mean no definition covers it;
    /// FetchFailed, Unreadable, Ambiguous and BlockUnsupported mean one exists and can't be used. Null for Resolved.
    /// </summary>
    public static TableState? Definition(DefinitionState state) => state switch
    {
        DefinitionState.Resolved => null,
        DefinitionState.NotInProvider or DefinitionState.NoMatch or DefinitionState.FetchingOff => TableState.NoDefinition,
        _ => TableState.DefinitionUnusable,
    };

    public static TableStateReason DefinitionReason(DefinitionResolution resolution) =>
        new(resolution.Problem?.Code, resolution.Problem?.Message, null, resolution.State, null, []);

    /// <summary>A DB2 open (or a later row read) that ended in <paramref name="state"/>. Rejected goes by the reader's code.</summary>
    public static TableState Open(Db2OpenState state, string? code) => state switch
    {
        Db2OpenState.UnknownFile => TableState.NotInBuild,
        Db2OpenState.NotInLocale => TableState.NotInLocale,
        Db2OpenState.NotInEncoding => TableState.NotInEncoding,
        Db2OpenState.Missing => TableState.Missing,
        Db2OpenState.NeedsNetwork => TableState.NeedsNetwork,
        Db2OpenState.Unavailable => TableState.Unavailable,
        Db2OpenState.Corrupt => TableState.Corrupt,
        Db2OpenState.Unsupported => TableState.Unsupported,
        Db2OpenState.WrongKey => TableState.WrongKey,
        Db2OpenState.StoreFailed => TableState.StoreFailed,
        Db2OpenState.TooLarge => TableState.TooLarge,
        Db2OpenState.Protected => TableState.Encrypted,
        Db2OpenState.Rejected => Reader(code),
        _ => TableState.Corrupt,
    };

    /// <summary>
    /// A reader refusal: DB2_UNSUPPORTED, DB2_LIMIT, DB2_ENCRYPTED (keyed sections that aren't read partially, as in WDC3) and
    /// DB2_SCHEMA_* have their own states; anything else is Rejected.
    /// </summary>
    public static TableState Reader(string? code) => code switch
    {
        "DB2_UNSUPPORTED" => TableState.Unsupported,
        "DB2_ENCRYPTED" => TableState.Encrypted,
        "DB2_LIMIT" => TableState.TooLarge,
        not null when code.StartsWith("DB2_SCHEMA_", StringComparison.Ordinal) => TableState.DefinitionUnusable,
        _ => TableState.Rejected,
    };

    public static IReadOnlyList<string> KeyNames(IEnumerable<string> names) =>
        [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A table in the generated store: Encrypted when every section is hidden (keyed, with a hole layer 4 couldn't decrypt) and no
    /// row was read, Partial when some section is hidden or a BLTE hole was left out, else Ready. A keyed section layer 4 decrypted
    /// is read like any other (D310), so a table whose keyed sections all decrypted is Ready. The reason's key names are the
    /// missing ones: the hidden sections' keys and the hole names. A hole named like a decrypted section's key can't be told
    /// apart once stored (hole names aren't kept); it lies in a hidden section, whose own key still names it.
    /// </summary>
    public static (TableState State, TableStateReason? Reason, TableCounts Counts) Generated(GeneratedTable table)
    {
        var hidden = table.Sections.Count(section => section.IsEncrypted && !section.IsDecrypted);
        // KeysUsed is every keyed section's key plus the hole names; without the decrypted sections' keys the missing ones are left.
        var decrypted = table.Sections.Where(section => section.IsDecrypted).Select(section => section.Key).ToHashSet(StringComparer.Ordinal);
        var keys = KeyNames(table.Sections.Where(section => section.IsEncrypted && !section.IsDecrypted).Select(section => section.Key)
            .Concat(table.KeysUsed.Where(name => !decrypted.Contains(name))));
        var state = table.RowCount == 0 && hidden > 0 && hidden == table.Sections.Count ? TableState.Encrypted
            : hidden > 0 || keys.Count > 0 ? TableState.Partial
            : TableState.Ready;
        TableStateReason? reason = state switch
        {
            TableState.Encrypted => new(AllSectionsKeyed, "Every section of the table is keyed with a key this build doesn't have.", null, null,
                Db2OpenState.Open, keys),
            TableState.Partial => new(null, null, null, null, Db2OpenState.Open, keys),
            _ => null,
        };
        var counts = new TableCounts(table.Header.DeclaredRecords + table.Header.DeclaredCopyRecords, table.RowCount, hidden,
            table.Completion.ExcludedPhysicalRecords + table.Completion.ExcludedCopyRecords, table.Header.Format, table.Header.LayoutHash);
        return (state, reason, counts);
    }
}
