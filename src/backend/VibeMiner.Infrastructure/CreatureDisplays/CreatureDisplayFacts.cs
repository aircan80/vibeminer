using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Reviewed facts of the rows a local display chain read (#62). Facts are picked by base field name, so Retail
/// (7275F5F6, 0F5449F8) and Classic (F75F884C, 3FFEE408) layouts give the same keys; a field the admitted layout lacks is
/// unsupported, never zero. Every other column follows as read. Nothing is calculated, combined or defaulted.</summary>
internal static class CreatureDisplayFacts
{
    internal const int MaxCells = 512, MaxFactCells = 16;
    private static readonly (string Key, string Field)[] DisplayFacts = [("model", "ModelID"), ("scale", "CreatureModelScale"), ("textureVariations", "TextureVariationFileDataID")];
    private static readonly (string Key, string Field)[] ModelFacts = [("file", "FileDataID"), ("scale", "ModelScale")];

    internal static CreatureDisplayFactsResult Project(CreatureDisplayMetadataResult metadata, IReadOnlyList<CreatureDisplayProbe> delivered)
    {
        var records = new List<CreatureDisplayRecordFacts>(2);
        foreach (var target in metadata.Sources)
        {
            if (target.Row is not { } row) continue;
            var probe = delivered.SingleOrDefault(p => p.Target.Role == target.Role && p.Target.Row == row);
            // A unique match whose row values weren't kept (a text issue or a column count that differs) has no facts.
            if (probe?.Values is not { } values || values.Count != target.Snapshot.Columns.Count) continue;
            records.Add(Record(target, row, values));
        }
        return new(metadata, VibeMiner.Application.CreatureDisplayMetadataService.FactsRevision, records.AsReadOnly());
    }

    internal static CreatureDisplayRecordFacts Record(CreatureDisplayTarget target, CreatureDisplayRowEvidence row, IReadOnlyList<string> values)
    {
        var columns = target.Snapshot.Columns;
        if (columns.Count > MaxCells) throw Limit();
        var used = new HashSet<int>();
        var reviewed = (target.Role == CreatureDisplayTargetRole.Display ? DisplayFacts : ModelFacts).Select(fact =>
        {
            var cells = new List<CreatureDisplayFactCell>();
            for (var i = 0; i < columns.Count; i++)
                if (BaseName(columns[i].Name) == fact.Field && !columns[i].IsId) { cells.Add(new(columns[i].Name, i, values[i])); used.Add(i); }
            // The contract allows 16 cells per fact; a wider array is refused whole, never cut.
            if (cells.Count > MaxFactCells) throw Limit();
            return new CreatureDisplayFact(fact.Key, cells.Count == 0 ? CreatureDisplayFactState.Unsupported : CreatureDisplayFactState.Value, cells.AsReadOnly());
        }).ToArray();
        var raw = new List<CreatureDisplayFactCell>();
        for (var i = 0; i < columns.Count; i++)
            if (!used.Contains(i) && !columns[i].IsId) raw.Add(new(columns[i].Name, i, values[i]));
        return new(target.Role, target.Snapshot.TableId, target.Role == CreatureDisplayTargetRole.Display ? "CreatureDisplayInfo" : "CreatureModelData",
            row.Source.RowId, Array.AsReadOnly(reviewed), raw.AsReadOnly());
    }
    internal static string BaseName(string column) { var open = column.IndexOf('['); return open < 0 ? column : column[..open]; }
    private static DiscoveryException Limit() => new("CREATURES_RESULT_LIMIT", "The display row has more columns than the facts reply can hold.");
}
