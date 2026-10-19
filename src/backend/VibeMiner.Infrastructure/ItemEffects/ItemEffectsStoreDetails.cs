using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public sealed partial class ItemEffectsStore
{
    private static readonly ItemEffectsProblem FieldProblem = new("ITEM_EFFECTS_FIELD_UNAVAILABLE", "A required effect input is unavailable.", false);
    private static DiscoveryException RowInvalid() => new("TABLE_ARTIFACT_INVALID", "The selected target row differs from its verified primary directory.");
    private static bool PhysicalFailure(Exception error, out ItemEffectsSourceState state)
    {
        if (error is DiscoveryException { Code: "TABLE_ARTIFACT_INVALID" }) { state = ItemEffectsSourceState.Corrupt; return true; }
        return AcquisitionFailure(error, out state);
    }
    public Task<ItemEffectDetailsResult> ReadDetailsAsync(ItemEffectDetailsRequest request, CancellationToken token) =>
        RunAsync(false, null, token, operation => DetailsAsync(request, false, operation));
    public Task<ItemEffectDetailsResult> PrepareDetailsAsync(ItemEffectDetailsRequest request,
        Func<ItemEffectsProgress, Task> progress, CancellationToken token) =>
        RunAsync(true, progress, token, operation => DetailsAsync(request, true, operation));
    private async Task<ItemEffectDetailsResult> DetailsAsync(ItemEffectDetailsRequest request, bool prepare, Operation operation)
    {
        var owned = ItemEffectsValidation.Snapshot(request); var token = operation.Token;
        var original = await CaptureAsync(owned.Selection, token); RequireItem(original);
        var bridge = await RequireBridgeAsync(original, owned.BridgeIndexArtifactId, operation);
        EdgeSelection[] edges;
        await using (var selected = await BorrowBridgeAsync(original, bridge, false, false, token))
            edges = await ReadEdgesAsync(selected, Parent(original), owned.Edges, token);
        var effect = edges.Any(edge => edge.Tuple.ItemEffectId >= 0) ? await ProbeAsync(original, ItemEffectsSourceRole.Effect, token) : Unchecked(ItemEffectsSourceRole.Effect);
        if (prepare && effect.Public.State == ItemEffectsSourceState.NotPrepared)
            effect = await PrepareOptionalAsync(original, effect, bridge, operation);
        // No reader/query borrow crossed the prerequisite wait.
        await using var index = await BorrowBridgeAsync(original, bridge, false, false, token);
        TargetBorrow? target = null;
        try
        {
            ItemEffectOutcome[] outcomes;
            if (effect.CurrentProfile)
            {
                try
                {
                    target = await BorrowTargetAsync(original, effect, token);
                    outcomes = await ReadEffectOutcomesAsync(effect, target.Reader, edges, token);
                }
                catch (Exception error) when (PhysicalFailure(error, out _))
                {
                    _ = PhysicalFailure(error, out var state); target?.Invalidate(); Invalidate(effect, false);
                    effect = Failed(effect, state); outcomes = EffectsForState(effect, edges);
                }
            }
            else outcomes = EffectsForState(effect, edges);
            var result = new ItemEffectDetailsResult(owned.Selection, owned.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy,
                effect.Public, Array.AsReadOnly(outcomes));
            ItemEffectsValidation.Validate(result, owned); await CountAsync(result, token, true);
            var final = (await FinishAsync(original, bridge, [effect], token))[0];
            if (final.Public != effect.Public) result = result with { Source = final.Public, Outcomes = Array.AsReadOnly(EffectsForState(final, edges)) };
            ItemEffectsValidation.Validate(result, owned); return result;
        }
        catch { index.Invalidate(); throw; }
        finally { if (target is not null) await target.DisposeAsync(); }
    }
    private static ItemEffectOutcome[] EffectsForState(SourceProbe source, EdgeSelection[] edges) => edges.Select(edge =>
        edge.Tuple.ItemEffectId < 0 ? new ItemEffectOutcome(edge.Key, N(edge.Tuple.ItemEffectId), ItemEffectState.EffectKeyOutOfRange) :
        new ItemEffectOutcome(edge.Key, N(edge.Tuple.ItemEffectId), Enum.Parse<ItemEffectState>(source.Public.State.ToString()), Problem: source.Public.Problem)).ToArray();
    private async Task<ItemEffectOutcome[]> ReadEffectOutcomesAsync(SourceProbe source, TableRowArtifacts reader,
        EdgeSelection[] edges, CancellationToken token)
    {
        using var query = EnterQuery(token);
        var unique = new Dictionary<int, ItemEffectOutcome>(edges.Length); var outcomes = new ItemEffectOutcome[edges.Length];
        for (var i = 0; i < edges.Length; i++)
        {
            var edge = edges[i]; var id = edge.Tuple.ItemEffectId;
            if (id < 0) { outcomes[i] = new(edge.Key, N(id), ItemEffectState.EffectKeyOutOfRange); continue; }
            if (!unique.TryGetValue(id, out var outcome))
            {
                var found = await reader.ReadUniqueAsync(source.Admission!.Artifact, (uint)id, token);
                await CpuAsync(() => outcome = EffectOutcome(source, edge.Key, id, found), token);
                found = null!; unique.Add(id, outcome!);
            }
            outcomes[i] = outcome! with { Key = edge.Key };
        }
        return outcomes;
    }
    private static ItemEffectOutcome EffectOutcome(SourceProbe source, ItemEffectsEdgeKey key, int id, TableUniqueRow found)
    {
        if (found.MatchCount is < 0 or > TableRowArtifacts.MaxRows || (found.MatchCount == 1) != (found.Row is not null)) throw RowInvalid();
        if (found.MatchCount == 0) return new(key, N(id), ItemEffectState.NotFoundInAdmittedScope, "0", Absence: Absence(source));
        if (found.MatchCount > 1) return new(key, N(id), ItemEffectState.Ambiguous, N(found.MatchCount));
        var row = found.Row!;
        if (row.Id != (uint)id || row.IsCopy != row.CopySourceId.HasValue || row.Values.Count != 10 || row.Values[0] != N(row.Id)) throw RowInvalid();
        var fields = new ItemEffectFieldEvidence[9];
        for (var i = 1; i <= 9; i++)
        {
            var value = row.Values[i];
            if (row.TextIssues?.Any(issue => issue.ColumnIndex == i) == true ||
                (i is 1 or 2 ? !Unsigned(value, byte.MaxValue) : i is 3 or 6 or 8 ? !Unsigned(value, uint.MaxValue) : !Signed(value)))
                return new(key, N(id), ItemEffectState.FieldUnavailable, Problem: FieldProblem);
            var kind = i is 3 or 6 or 8 ? ItemEffectEvidenceKind.RawPhysicalInteger :
                i == 9 ? ItemEffectEvidenceKind.UnverifiedCandidate : ItemEffectEvidenceKind.StrictNumeric;
            var candidate = i switch { 6 => new TableReference("SpellCategory", "ID"), 8 => new("ChrSpecialization", "ID"), 9 => new("PlayerCondition", "ID"), _ => null };
            fields[i - 1] = new(EffectColumns[i], i, value, kind, source.CapturedSnapshot!.Columns[i].Interpretation, candidate);
        }
        return new(key, N(id), ItemEffectState.ObservedReady, "1", Target(source, row), Array.AsReadOnly(fields));
    }
    private static ItemEffectsSourceReference Target(SourceProbe source, Db2Row row) => new(Role(source.Role).Table,
        source.CapturedSnapshot!.ArtifactId, N(row.Id), row.IsCopy, row.CopySourceId is { } id ? N(id) : null);
    private static ItemEffectsAbsence Absence(SourceProbe source) => source.CapturedSnapshot!.Coverage.Scope == "complete" ?
        ItemEffectsAbsence.AbsentFromCompleteSource : ItemEffectsAbsence.UnknownOutsideReadableScope;
    private static bool Signed(string? value) => value is not null && value.Length is > 0 and <= 11 &&
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) && N(parsed) == value;
    private static bool Unsigned(string? value, uint maximum) => value is not null && value.Length is > 0 and <= 10 &&
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed <= maximum && N(parsed) == value;
}
