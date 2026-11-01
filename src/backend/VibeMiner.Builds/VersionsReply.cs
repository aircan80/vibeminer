using System.Globalization;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary>One data row of a versions reply that names a build: its identity, the row's Region, and its CDN config.</summary>
internal sealed record Sighting(BuildIdentity Identity, string Region, string CdnConfig, int Row);

/// <summary>What one versions history entry says: its builds, the rows or the whole reply that couldn't be read, and <c>## seqn</c>.</summary>
internal sealed record VersionsRead(IReadOnlyList<Sighting> Sightings, IReadOnlyList<CatalogProblem> Problems, string? Sequence)
{
    /// <summary><see cref="Sequence"/> as a number; null when absent or not a plain decimal number.</summary>
    public long? SequenceNumber =>
        long.TryParse(Sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>False when the whole reply has a problem (one with no row): such an entry never decides what is current.</summary>
    public bool Readable => Problems.All(p => p.Row is not null);
}

/// <summary>Reads a versions reply from the history log. Pure: bytes in, sightings and problems out. Never throws for bad bytes.</summary>
internal static class VersionsReply
{
    private static readonly string[] Required = ["Region", "BuildConfig", "CDNConfig", "VersionsName"];

    /// <summary>
    /// The builds the reply lists, in reply order. A bad row is a problem and the others still count. A region listed by
    /// more than one row is ambiguous: none of its rows count.
    /// </summary>
    public static VersionsRead Read(HistoryEntry entry, ReadOnlySpan<byte> reply)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != HistoryKind.Versions)
            return Fail(new("VERSIONS_WRONG_KIND", $"The history entry is {entry.Kind}, not a versions reply.", entry.Record));
        if (!Products.IsWow(entry.Product))
            return Fail(new("VERSIONS_UNKNOWN_PRODUCT", "The history entry's product isn't a WoW product id.", entry.Record));
        VersionsTable document;
        try { document = VersionsTable.Parse(reply); }
        catch (FormatException e)
        { return Fail(new("VERSIONS_UNREADABLE", $"The versions reply can't be read: {e.Message}", entry.Record)); }
        var names = document.Columns.ToHashSet(StringComparer.Ordinal);
        var missing = Required.Where(n => !names.Contains(n)).ToArray();
        if (missing.Length != 0)
            return new([], [new("VERSIONS_COLUMNS_MISSING", $"The versions reply has no {string.Join(", ", missing)} column.",
                entry.Record)], document.Sequence);

        var problems = new List<CatalogProblem>();
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in document.Rows)
        {
            var region = row.Fields["Region"];
            if (!seen.Add(region) && ambiguous.Add(region))
                problems.Add(new("VERSIONS_REGION_AMBIGUOUS", $"More than one row lists region '{region}'.", entry.Record, row.Ordinal));
        }
        var sightings = new List<Sighting>();
        foreach (var row in document.Rows)
        {
            try
            {
                var identity = BuildIdentity.Create(entry.Product, row.Fields);
                var region = row.Fields["Region"];
                if (!ambiguous.Contains(region))
                    sightings.Add(new(identity, region, BuildIdentity.ObservedCdnConfig(row.Fields), row.Ordinal));
            }
            catch (BuildIdentityException e)
            { problems.Add(new("VERSIONS_ROW_INVALID", $"A versions row can't be read ({e.Code}).", entry.Record, row.Ordinal)); }
        }
        return new(sightings, problems, document.Sequence);
    }

    private static VersionsRead Fail(CatalogProblem problem) => new([], [problem], null);
}
