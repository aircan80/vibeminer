using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using F = VibeMiner.Domain.TableFollowStates;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Follow a declared reference by exact target column value (D072). Local only. The source column must declare the
/// reference in its pinned definition and the target must be that declared table, already admitted. Values compare
/// as exact integers: never wrapped or truncated between signed and unsigned widths. A record-ID target uses the ID
/// directory; another column is scanned only when the whole target has at most <see cref="TableFollowRules.MaximumScanRows"/> rows.
/// </summary>
public sealed partial class TableDataStore : ITableFollowStore
{
    public async Task<TableFollowPage> FollowAsync(TableFollowRequest request, CancellationToken token)
    {
        // Shares the reference-text gate and its verified readers: one local lookup at a time engine-wide.
        await referenceGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(referenceClosed, this);
            var sourceRequest = new TableDataRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale);
            var (context, provider, entry, definition) = await ResolveAsync(sourceRequest, token);
            var admission = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token)
                ?? throw new DiscoveryException("TABLE_NOT_PREPARED", "Load this exact table and research locale before following its references.");
            var configuration = await ReadBuildConfigurationAsync(context, token);
            ValidateSourceAssociation(configuration, admission.Association);
            Validate(admission, entry, definition, context.Build.Version);
            if (request.ArtifactId != admission.Artifact.ContentKey)
                throw new DiscoveryException("TABLE_ARTIFACT_STALE", "This row belongs to a different table interpretation. Reopen the table data.");

            var header = admission.Artifact.Header.Columns;
            var column = request.ColumnIndex < header.Count ? ProjectColumn(header[request.ColumnIndex], definition.Document) : null;
            if (column?.Reference is not { } reference || column.Family != "integer")
                throw new DiscoveryException("TABLE_FOLLOW_NOT_DECLARED", "This column declares no reference that can be followed by value.");

            // The target is the declared table by its exact provider name, never a table guessed from matching numbers.
            var targetEntry = provider.Entries.SingleOrDefault(e => e.TableId == request.TargetTableId);
            if (targetEntry is null || !string.Equals(targetEntry.Name, reference.Table, StringComparison.Ordinal))
                throw new DiscoveryException("TABLE_FOLLOW_TARGET_MISMATCH", $"{column.Name} declares {reference.Table}.{reference.Column}, not this table.");
            var (_, _, resolvedTarget, targetDefinition) = await ResolveAsync(sourceRequest with { TableId = request.TargetTableId }, token);
            var target = await catalog.ReadTableAdmissionAsync(context, request.TargetTableId, request.ResearchLocale, token)
                ?? throw new DiscoveryException("TABLE_NOT_PREPARED", $"Load {reference.Table} before following {column.Name}.");
            ValidateSourceAssociation(configuration, target.Association);
            Validate(target, resolvedTarget, targetDefinition, context.Build.Version);
            if (request.TargetArtifactId != target.Artifact.ContentKey)
                throw new DiscoveryException("TABLE_ARTIFACT_STALE", $"The {reference.Table} rows changed. Reopen its table data.");

            var artifact = target.Artifact; var after = request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture);
            TableFollowPage Result(string state, int? index, bool isId, TableFollowRange? range, TableDataPage? page, TableReferenceTextProblem? problem) =>
                new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.ResearchLocale, TableFollowRules.PolicyRevision,
                    request.TableId, request.ArtifactId, request.ColumnIndex, column.Name, request.Value, reference, request.TargetTableId,
                    request.TargetArtifactId, index, isId, range, artifact.Completion.Scope, state, request.After ?? "0", page, problem);

            var targetColumns = artifact.Header.Columns;
            var matchIndex = Single(targetColumns, c => c.Name == reference.Column);
            if (matchIndex < 0 || targetColumns[matchIndex].Kind != "integer" || targetColumns[matchIndex].Interpretation is not null)
                return Result(F.Unsupported, null, false, null, null, new("TABLE_FOLLOW_LAYOUT",
                    $"The admitted {reference.Table} layout has no exact integer {reference.Column} column to match."));
            var isId = targetColumns[matchIndex].IsId;
            var bounds = isId ? (Minimum: (Int128)0, Maximum: (Int128)uint.MaxValue)
                : DeclaredRange(targetDefinition.Document, context.Build.Version, artifact.Header.LayoutHash, reference.Column, token);
            var range = bounds is { } b ? new TableFollowRange(b.Minimum.ToString(CultureInfo.InvariantCulture), b.Maximum.ToString(CultureInfo.InvariantCulture)) : null;
            if (!TableFollowService.TryParseValue(request.Value, out var key)) throw new DiscoveryException("INVALID_REQUEST", "The followed value is not a canonical integer.");
            if (bounds is { } limit && (key < limit.Minimum || key > limit.Maximum))
                return Result(F.KeyOutOfRange, matchIndex, isId, range, null, null);

            var rowsRequest = new TableRowsRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TargetTableId,
                request.ResearchLocale, request.TargetArtifactId, request.Limit, request.After);
            var total = artifact.Completion.PhysicalRows + artifact.Completion.CopyRows;
            if (!isId && total > TableFollowRules.MaximumScanRows)
                return Result(F.Unindexed, matchIndex, false, range, null, new("TABLE_FOLLOW_UNINDEXED",
                    $"{reference.Table}.{reference.Column} isn't the record ID, and {reference.Table} has {total} rows, more than {TableFollowRules.MaximumScanRows} that can be searched by value."));

            var reader = await ReaderForAsync(artifact);
            try
            {
                TableArtifactPage source;
                if (isId) source = await reader.Rows.ReadPageAsync(artifact, after, request.Limit, (uint)key, token);
                else
                {
                    var hits = (await MatchesAsync(reader, artifact, matchIndex, token)).GetValueOrDefault(key) ?? [];
                    if (after > hits.Count) throw new DiscoveryException("INVALID_CURSOR", "The follow cursor exceeds the matching rows.");
                    var rows = new List<Db2Row>(); var take = (int)Math.Min(request.Limit, hits.Count - after);
                    for (var i = 0; i < take; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var row = Assert1((await reader.Rows.ReadPageAsync(artifact, hits[(int)after + i], 1, null, token)).Rows);
                        if (row.Values[matchIndex] != request.Value) throw new DiscoveryException("TABLE_ARTIFACT_INVALID", "A matched row differs from its value.");
                        rows.Add(row);
                    }
                    var next = after + rows.Count;
                    source = new(rows.AsReadOnly(), hits.Count, next < hits.Count ? next : null);
                }
                return Result(F.Ready, matchIndex, isId, range, TableRowProjection.Page(rowsRequest, source, token), null);
            }
            // A bad cursor is the caller's; the verified reader stays. Anything else may mean the artifact, so it is dropped.
            catch (DiscoveryException failure) when (failure.Code is not "INVALID_CURSOR") { await DropReaderAsync(reader); throw; }
        }
        finally { referenceGate.Release(); }
    }

    // The declared width and sign of a non-ID target column in the admitted layout. Null when the definition gives none.
    private static (Int128 Minimum, Int128 Maximum)? DeclaredRange(DbdDocument definition, string buildVersion, string layoutHash, string name, CancellationToken token)
    {
        var block = definition.SelectFile(buildVersion, layoutHash, token).Blocks.Single();
        var field = block.Fields.SingleOrDefault(f => f.Column.Name == name && f.ArrayLength == 1);
        if (field?.IntegerBits is not (>= 1 and <= 64) || field.Unsigned is not { } unsigned) return null;
        var bits = field.IntegerBits.Value;
        return unsigned ? (0, (Int128.One << bits) - 1) : (-(Int128.One << (bits - 1)), (Int128.One << (bits - 1)) - 1);
    }

    // Bounded scan of a small target, 50 rows at a time, kept as exact values and directory ordinals only.
    private async Task<Dictionary<Int128, List<long>>> MatchesAsync(ReferenceReader reader, TableRowsArtifact artifact, int matchIndex, CancellationToken token)
    {
        if (reader.Matches.TryGetValue(matchIndex, out var cached)) return cached;
        var index = new Dictionary<Int128, List<long>>(); long after = 0;
        while (true)
        {
            var page = await reader.Rows.ReadPageAsync(artifact, after, 50, null, token);
            using (resources is null ? null : await resources.EnterCpuAsync(null, token))
            {
                for (var i = 0; i < page.Rows.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var raw = page.Rows[i].Values[matchIndex];
                    if (!TableFollowService.TryParseValue(raw, out var value))
                        throw new DiscoveryException("TABLE_ARTIFACT_INVALID", "An admitted match value is not a canonical integer.");
                    if (!index.TryGetValue(value, out var ordinals)) index[value] = ordinals = [];
                    ordinals.Add(after + i);
                }
            }
            after += page.Rows.Count;
            if (page.NextCursor is null || page.Rows.Count == 0) break;
            if (after > TableFollowRules.MaximumScanRows) throw new DiscoveryException("TABLE_ARTIFACT_INVALID", "The follow scan exceeded its row bound.");
        }
        reader.Matches[matchIndex] = index;
        return index;
    }
}
