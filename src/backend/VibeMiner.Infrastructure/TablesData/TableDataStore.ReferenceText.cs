using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;
using S = VibeMiner.Domain.TableReferenceTextStates;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Batched reference text for one exact row page. Local only: an unprepared text table is reported,
/// never acquired here. Record-ID targets use the ID directory; a non-ID target column is scanned only
/// when the whole admitted target has at most <see cref="TableReferenceTextRules.MaximumValueIndexRows"/> rows.
/// </summary>
public sealed partial class TableDataStore
{
    internal const int MaximumReferenceReaders = 4, MaximumReferenceTextBytes = 512 * 1024;
    private static readonly JsonSerializerOptions ReferenceJson = new(JsonSerializerDefaults.Web);
    // One batch at a time. Text tables get their own readers so a page read never re-verifies the source artifact.
    private readonly SemaphoreSlim referenceGate = new(1, 1);
    private readonly List<ReferenceReader> referenceReaders = [];
    private bool referenceClosed;

    private sealed class ReferenceReader(string key, TableRowArtifacts rows)
    {
        internal string Key { get; } = key;
        internal TableRowArtifacts Rows { get; } = rows;
        // Match column index -> key -> (emitted matches, directory ordinal of the first match).
        internal Dictionary<int, Dictionary<long, (long Count, long Ordinal)>> Indexes { get; } = [];
        // Follow (D072): match column index -> exact value -> directory ordinals of every matching row, in order.
        internal Dictionary<int, Dictionary<Int128, List<long>>> Matches { get; } = [];
    }

    private sealed class TextTarget
    {
        internal required TableReferenceTextRule Rule { get; init; }
        internal string State = S.Ready;
        internal TableReferenceTextProblem? Problem;
        internal TableRowsArtifact? Artifact;
        internal ReferenceReader? Reader;
        internal int TextIndex = -1, MatchIndex = -1;
        internal TableReferenceTextSource Source(string? artifactId = null, string? scope = null) =>
            Describe(Rule, artifactId, scope);
    }

    public async Task<TableReferenceTextPage> ReadReferenceTextAsync(TableRowsRequest request, CancellationToken token)
    {
        await referenceGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(referenceClosed, this);
            var dataRequest = new TableDataRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale, FetchedDefinitions: true);
            var (context, provider, entry, bundled, fetchEnabled) = await ResolveAsync(dataRequest, token);
            var admission = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token)
                ?? throw new DiscoveryException("TABLE_NOT_PREPARED", "Load this exact table and research locale before reading its records.");
            var configuration = await ReadBuildConfigurationAsync(context, token);
            ValidateSourceAssociation(configuration, admission.Association);
            var definition = await definitions.ForRowsAsync(entry, bundled, context.Build.Version, admission.Artifact, fetchEnabled, token);
            Validate(admission, entry, definition, bundled, context.Build.Version);
            if (request.ArtifactId != admission.Artifact.ContentKey)
                throw new DiscoveryException("TABLE_ARTIFACT_STALE", "This row page belongs to a different table interpretation. Reopen the table data.");
            // The exact rows tables.rows returns for this query, including its byte trimming.
            var source = await rows.ReadPageAsync(admission.Artifact,
                request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture), request.Limit,
                request.RecordId is null ? null : uint.Parse(request.RecordId, CultureInfo.InvariantCulture), token);
            var page = TableRowProjection.Page(request, source, token);

            var columns = new List<TableReferenceTextColumn>(); var values = new List<TableReferenceTextValue>();
            var targets = new Dictionary<string, TextTarget>(StringComparer.Ordinal);
            var header = admission.Artifact.Header.Columns;
            for (var index = 0; index < header.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var column = ProjectColumn(header[index], definition.Document);
                if (column.Reference is not { } reference) continue;
                var rule = TableReferenceTextRules.For(reference);
                if (rule is null)
                {
                    // Number only. Say whether the target is a bundled table without a reviewed text column or has no definition.
                    var targetBundled = provider.Entries.Any(e => e.Name == reference.Table && provider.Definitions.ContainsKey(e.TableId));
                    columns.Add(new(index, column.Name, reference, targetBundled ? S.NoTextRule : S.NoDefinition, null, null));
                    continue;
                }
                if (column.Family != "integer")
                {
                    columns.Add(new(index, column.Name, reference, S.Unsupported,
                        Describe(rule, null, null),
                        new("TABLE_REFERENCE_TEXT_SOURCE", "This reference column is not an integer key.")));
                    continue;
                }
                if (!targets.TryGetValue(rule.TextTableId, out var target))
                    targets[rule.TextTableId] = target = await OpenTextTargetAsync(rule, context, provider, configuration, request.ResearchLocale, fetchEnabled, token);
                if (target.State != S.Ready)
                {
                    columns.Add(new(index, column.Name, reference, target.State, target.Source(), target.Problem));
                    continue;
                }
                try
                {
                    var resolved = await ResolveColumnAsync(target, index, page.Rows, token);
                    values.AddRange(resolved);
                    columns.Add(new(index, column.Name, reference, S.Ready,
                        target.Source(target.Artifact!.ContentKey, target.Artifact.Completion.Scope), null));
                }
                catch (DiscoveryException failure)
                {
                    await DropReaderAsync(target.Reader);
                    target.State = S.Unreadable; target.Problem = new(failure.Code, failure.Message); target.Reader = null;
                    columns.Add(new(index, column.Name, reference, S.Unreadable, target.Source(), target.Problem));
                }
            }
            var result = new TableReferenceTextPage(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId,
                request.ResearchLocale, request.ArtifactId, request.After ?? "0", request.RecordId, TableReferenceTextRules.PolicyRevision,
                Array.AsReadOnly(page.Rows.Select(row => row.Id).ToArray()), columns.AsReadOnly(), values.AsReadOnly());
            if (JsonSerializer.SerializeToUtf8Bytes(result, ReferenceJson).Length > MaximumReferenceTextBytes)
                throw new DiscoveryException("TABLE_REFERENCE_TEXT_LIMIT", "Reference text for this page exceeds its bounded response size. Raw values stay available.");
            return result;
        }
        finally { referenceGate.Release(); }
    }

    private async Task<TextTarget> OpenTextTargetAsync(TableReferenceTextRule rule, CapturedOpeningContext context,
        BundledTableDefinitions provider, TactConfiguration configuration, string locale, bool fetchEnabled, CancellationToken token)
    {
        var target = new TextTarget { Rule = rule };
        TextTarget Fail(string state, string code, string message) { target.State = state; target.Problem = new(code, message); return target; }
        var entry = provider.Entries.SingleOrDefault(e => e.TableId == rule.TextTableId);
        if (entry?.Db2FileDataId is null || !provider.Definitions.TryGetValue(rule.TextTableId, out var bundled))
            return Fail(S.NoDefinition, "DEFINITION_NOT_BUNDLED", $"The {rule.TextTable} definition is not bundled.");
        if (!fetchEnabled && !OpensBuild(bundled.Document, context.Build.Version, token))
            return Fail(S.Unsupported, "SCHEMA_BUILD_NOT_LISTED", $"The pinned {rule.TextTable} definition does not list this exact build or any layout on its version line.");
        var admission = await catalog.ReadTableAdmissionAsync(context, rule.TextTableId, locale, token);
        if (admission is null) { target.State = S.NotPrepared; return target; }
        try
        {
            ValidateSourceAssociation(configuration, admission.Association);
            Validate(admission, entry, await definitions.ForRowsAsync(entry, bundled, context.Build.Version, admission.Artifact, fetchEnabled, token), bundled, context.Build.Version);
        }
        catch (DiscoveryException failure) when (failure.Code == "TABLE_INTERPRETATION_STALE") { target.State = S.NotPrepared; return target; }
        catch (DiscoveryException failure) when (failure.Code == "TABLE_SOURCE_CONTEXT_MISMATCH") { return Fail(S.Unreadable, failure.Code, failure.Message); }
        catch (DiscoveryException failure) { return Fail(S.Unsupported, failure.Code, failure.Message); }
        var columns = admission.Artifact.Header.Columns;
        target.TextIndex = Single(columns, c => c.Name == rule.TextColumn && c.Kind == "string" && !c.IsId && c.Interpretation is null);
        target.MatchIndex = Single(columns, c => c.Name == rule.MatchColumn && c.Kind == "integer" && c.IsId == rule.MatchesRecordId && c.Interpretation is null);
        if (target.TextIndex < 0 || target.MatchIndex < 0)
            return Fail(S.Unsupported, "TABLE_REFERENCE_TEXT_LAYOUT", $"The admitted {rule.TextTable} layout has no usable {rule.MatchColumn} and {rule.TextColumn} columns.");
        var total = admission.Artifact.Completion.PhysicalRows + admission.Artifact.Completion.CopyRows;
        if (!rule.MatchesRecordId && total > TableReferenceTextRules.MaximumValueIndexRows)
            return Fail(S.Unindexed, "TABLE_REFERENCE_TEXT_UNINDEXED", $"{rule.TextTable}.{rule.MatchColumn} is not the record ID and has no value index for {total} rows.");
        target.Artifact = admission.Artifact;
        target.Reader = await ReaderForAsync(admission.Artifact);
        try
        {
            // Opening verifies the whole immutable artifact before any text is trusted.
            await target.Reader.Rows.ReadPageAsync(admission.Artifact, 0, 1, null, token);
            if (!rule.MatchesRecordId) await IndexAsync(target, token);
        }
        catch (DiscoveryException failure)
        {
            await DropReaderAsync(target.Reader); target.Reader = null;
            return Fail(S.Unreadable, failure.Code, failure.Message);
        }
        return target;
    }

    private static TableReferenceTextSource Describe(TableReferenceTextRule rule, string? artifactId, string? scope) =>
        new(rule.TextTableId, rule.TextTable, rule.TextColumn, rule.MatchColumn, artifactId, scope,
            rule.MeasuredEncodedBytes.ToString(CultureInfo.InvariantCulture), rule.LoadPolicy);

    private static int Single(IReadOnlyList<Db2Column> columns, Func<Db2Column, bool> match)
    {
        var found = -1;
        for (var i = 0; i < columns.Count; i++) if (match(columns[i])) { if (found >= 0) return -1; found = i; }
        return found;
    }

    // Bounded scan of a small target: at most MaximumValueIndexRows rows, read 50 at a time, kept as keys and ordinals only.
    private async Task IndexAsync(TextTarget target, CancellationToken token)
    {
        var reader = target.Reader!; if (reader.Indexes.ContainsKey(target.MatchIndex)) return;
        var index = new Dictionary<long, (long Count, long Ordinal)>(); long after = 0;
        while (true)
        {
            var page = await reader.Rows.ReadPageAsync(target.Artifact!, after, 50, null, token);
            using (resources is null ? null : await resources.EnterCpuAsync(null, token))
            {
                for (var i = 0; i < page.Rows.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = page.Rows[i]; var raw = row.Values[target.MatchIndex];
                    if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ||
                        key.ToString(CultureInfo.InvariantCulture) != raw || key < target.Rule.Minimum || key > target.Rule.Maximum)
                        throw new DiscoveryException("TABLE_REFERENCE_TEXT_LAYOUT", $"An admitted {target.Rule.TextTable}.{target.Rule.MatchColumn} value is outside its reviewed range.");
                    index[key] = index.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.Ordinal) : (1, after + i);
                }
            }
            after += page.Rows.Count;
            if (page.NextCursor is null || page.Rows.Count == 0) break;
            if (after > TableReferenceTextRules.MaximumValueIndexRows) throw new DiscoveryException("TABLE_ARTIFACT_INVALID", "The value index exceeded its row bound.");
        }
        reader.Indexes[target.MatchIndex] = index;
    }

    private async Task<List<TableReferenceTextValue>> ResolveColumnAsync(TextTarget target, int columnIndex,
        IReadOnlyList<TableDataRow> pageRows, CancellationToken token)
    {
        var rule = target.Rule; var artifact = target.Artifact!; var reader = target.Reader!;
        var result = new List<TableReferenceTextValue>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pageRow in pageRows)
        {
            token.ThrowIfCancellationRequested();
            var value = pageRow.Values[columnIndex];
            if (!seen.Add(value)) continue;
            // Exact mathematical value. Never wrap signed to unsigned; out of the target domain is a miss.
            if (!Int128.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ||
                key.ToString(CultureInfo.InvariantCulture) != value || key < rule.Minimum || key > rule.Maximum)
            { result.Add(new(columnIndex, value, S.KeyOutOfRange, null, "0", null)); continue; }
            long count; Db2Row? row;
            if (rule.MatchesRecordId)
            {
                var unique = await reader.Rows.ReadUniqueAsync(artifact, (uint)key, token);
                count = unique.MatchCount; row = unique.Row;
            }
            else
            {
                count = reader.Indexes[target.MatchIndex].TryGetValue((long)key, out var hit) ? hit.Count : 0; row = null;
                if (count == 1)
                {
                    row = Assert1((await reader.Rows.ReadPageAsync(artifact, hit.Ordinal, 1, null, token)).Rows);
                    if (row.Values[target.MatchIndex] != value) throw new DiscoveryException("TABLE_ARTIFACT_INVALID", "The indexed text row differs from its key.");
                }
            }
            var matches = count.ToString(CultureInfo.InvariantCulture);
            if (count == 0) { result.Add(new(columnIndex, value, S.NotFound, null, matches, null)); continue; }
            if (count > 1 || row is null) { result.Add(new(columnIndex, value, S.Multiple, null, matches, null)); continue; }
            var located = new TableReferenceTextRow(row.Id.ToString(CultureInfo.InvariantCulture), row.IsCopy,
                row.CopySourceId?.ToString(CultureInfo.InvariantCulture));
            var text = row.Values[target.TextIndex];
            if (row.TextIssues?.Any(issue => issue.ColumnIndex == target.TextIndex) == true)
                result.Add(new(columnIndex, value, S.TextUnreadable, null, matches, located));
            else if (text.Length == 0) result.Add(new(columnIndex, value, S.Empty, "", matches, located));
            else if (text.Length > TableReferenceTextRules.MaximumTextUnits) result.Add(new(columnIndex, value, S.TextTooLong, null, matches, located));
            else result.Add(new(columnIndex, value, S.Resolved, text, matches, located));
        }
        return result;
    }
    private static Db2Row Assert1(IReadOnlyList<Db2Row> rows) => rows.Count == 1 ? rows[0]
        : throw new DiscoveryException("TABLE_ARTIFACT_INVALID", "The indexed text row is missing.");

    private async Task<ReferenceReader> ReaderForAsync(TableRowsArtifact artifact)
    {
        var key = artifact.ContentKey + ":" + artifact.Sha256;
        var existing = referenceReaders.FindIndex(reader => reader.Key == key);
        if (existing >= 0) { var hit = referenceReaders[existing]; referenceReaders.RemoveAt(existing); referenceReaders.Add(hit); return hit; }
        if (referenceReaders.Count >= MaximumReferenceReaders) { var oldest = referenceReaders[0]; referenceReaders.RemoveAt(0); await oldest.Rows.DisposeAsync(); }
        var created = new ReferenceReader(key, resources is null ? new TableRowArtifacts(objects) : new TableRowArtifacts(objects, resources, null));
        referenceReaders.Add(created); return created;
    }
    private async Task DropReaderAsync(ReferenceReader? reader)
    {
        if (reader is null || !referenceReaders.Remove(reader)) return;
        await reader.Rows.DisposeAsync();
    }
    private async Task DisposeReferenceReadersAsync()
    {
        await referenceGate.WaitAsync();
        try
        {
            if (referenceClosed) return; referenceClosed = true;
            foreach (var reader in referenceReaders) await reader.Rows.DisposeAsync();
            referenceReaders.Clear();
        }
        finally { referenceGate.Release(); }
    }
}
