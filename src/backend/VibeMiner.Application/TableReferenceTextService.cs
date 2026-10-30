using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>
/// One reviewed reference-text rule. The declared target (Table::Column) selects the rule; the text
/// comes from TextTable.TextColumn on the row whose MatchColumn equals the source value. A redirect
/// (Spell.ID to SpellName) names its actual text table. See docs/WOW-DATA-REFERENCE.md.
/// </summary>
public sealed record TableReferenceTextRule(string Table, string Column, string TextTable, string TextTableId,
    string TextColumn, string MatchColumn, bool MatchesRecordId, long Minimum, long Maximum, long MeasuredEncodedBytes)
{
    /// <summary>Small text tables load automatically; larger ones wait for an explicit Load text.</summary>
    public string LoadPolicy => MeasuredEncodedBytes <= TableReferenceTextRules.AutomaticLoadMaximumEncodedBytes ? "automatic" : "onRequest";
}

public static class TableReferenceTextRules
{
    public const string PolicyRevision = "table-reference-text-v1";
    /// <summary>A non-ID match column is scanned only when the whole target fits this many rows.</summary>
    public const int MaximumValueIndexRows = 4096;
    /// <summary>Longer text is reported as textTooLong, never truncated.</summary>
    public const int MaximumTextUnits = 4096;
    /// <summary>1 MiB of encoded table. ItemClass and Creature are below it; SpellName and ItemSparse are above.</summary>
    public const long AutomaticLoadMaximumEncodedBytes = 1024 * 1024;

    // Reviewed on issue #17. Everything else shows the number only; no rule is inferred from names or matching numbers.
    // Encoded sizes were measured on Retail 12.1.5.70448 (docs/WOW-DATA-REFERENCE.md); other builds differ a little.
    public static readonly IReadOnlyList<TableReferenceTextRule> Allowlist =
    [
        new("ItemClass", "ClassID", "ItemClass", "b977271e", "ClassName_lang", "ClassID", false, sbyte.MinValue, sbyte.MaxValue, 453),
        new("Creature", "ID", "Creature", "c9d6b6b3", "Name_lang", "ID", true, 0, uint.MaxValue, 340_038),
        // Reviewed on #62: Creature.CreatureType and Creature.CreatureFamily name these rows; the text is their Name_lang. Their sizes were
        // measured on Retail 12.1.5.70575 (the newest Retail build then), not 12.1.5.70448.
        new("CreatureType", "ID", "CreatureType", "1f80ad3f", "Name_lang", "ID", true, 0, uint.MaxValue, 326),
        new("CreatureFamily", "ID", "CreatureFamily", "369f469f", "Name_lang", "ID", true, 0, uint.MaxValue, 2_196),
        new("Spell", "ID", "SpellName", "46c66698", "Name_lang", "ID", true, 0, uint.MaxValue, 2_754_982),
        new("Item", "ID", "ItemSparse", "919be54e", "Display_lang", "ID", true, 0, uint.MaxValue, 7_988_612),
    ];

    public static TableReferenceTextRule? For(TableReference reference) =>
        Allowlist.SingleOrDefault(rule => string.Equals(rule.Table, reference.Table, StringComparison.Ordinal) &&
            string.Equals(rule.Column, reference.Column, StringComparison.Ordinal));
}

public interface ITableReferenceTextStore
{
    /// <summary>Local only: never acquires. Unprepared text tables are reported as notPrepared.</summary>
    Task<TableReferenceTextPage> ReadReferenceTextAsync(TableRowsRequest request, CancellationToken token);
}

public sealed class TableReferenceTextService(ITableReferenceTextStore store)
{
    public Task<TableReferenceTextPage> ReadAsync(TableRowsRequest request, CancellationToken token = default)
    {
        TableDataService.Validate(request); token.ThrowIfCancellationRequested();
        return store.ReadReferenceTextAsync(request, token);
    }
}
