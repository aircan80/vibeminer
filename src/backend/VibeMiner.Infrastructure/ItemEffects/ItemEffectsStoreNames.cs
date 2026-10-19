using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class ItemEffectsStore
{
    public Task<ItemEffectNamesResult> ReadNamesAsync(ItemEffectNamesRequest request, CancellationToken token) =>
        RunAsync(false, null, token, operation => NamesAsync(request, false, operation));
    public Task<ItemEffectNamesResult> PrepareNamesAsync(ItemEffectNamesRequest request, Func<ItemEffectsProgress, Task> progress, CancellationToken token) =>
        RunAsync(true, progress, token, operation => NamesAsync(request, true, operation));
    private async Task<ItemEffectNamesResult> NamesAsync(ItemEffectNamesRequest request, bool prepare, Operation operation)
    {
        var owned = ItemEffectsValidation.Snapshot(request); var token = operation.Token;
        var original = await CaptureAsync(owned.Selection, token); RequireItem(original);
        var bridge = await RequireBridgeAsync(original, owned.BridgeIndexArtifactId, operation);
        EdgeSelection[] edges;
        await using (var first = await BorrowBridgeAsync(original, bridge, false, false, token))
            edges = await ReadEdgesAsync(first, Parent(original), owned.Edges, token);
        var effect = edges.Any(edge => edge.Tuple.ItemEffectId >= 0) ? await ProbeAsync(original, ItemEffectsSourceRole.Effect, token) : Unchecked(ItemEffectsSourceRole.Effect);
        if (effect.CurrentProfile && effect.CapturedSnapshot!.ArtifactId != owned.ItemEffectArtifactId) effect = Failed(effect, ItemEffectsSourceState.Stale);
        var evidence = SpellsForState(effect, edges);
        // This initial local phase establishes eligibility. Its borrow is gone before Name can acquire.
        if (effect.CurrentProfile)
        {
            try
            {
                await using var firstEffect = await BorrowTargetAsync(original, effect, token);
                evidence = await ReadSpellEvidenceAsync(effect, firstEffect.Reader, edges, token);
            }
            catch (Exception error) when (PhysicalFailure(error, out _))
            {
                _ = PhysicalFailure(error, out var state); Invalidate(effect, false); effect = Failed(effect, state); evidence = SpellsForState(effect, edges);
            }
        }
        var name = evidence.Any(spell => spell.State == ItemEffectState.ObservedReady && spell.SpellId!.Value[0] != '-') ?
            await ProbeAsync(original, ItemEffectsSourceRole.Name, token) : Unchecked(ItemEffectsSourceRole.Name);
        if (prepare && name.Public.State == ItemEffectsSourceState.NotPrepared)
            name = await PrepareOptionalAsync(original, name, bridge, operation);
        await using var index = await BorrowBridgeAsync(original, bridge, false, false, token);
        TargetBorrow? effectReader = null, nameReader = null;
        try
        {
            // Preserve the originally pinned Effect generation across Name's wait, not a newly resolved target.
            if (effect.CurrentProfile)
            {
                try
                {
                    effectReader = await BorrowTargetAsync(original, effect, token);
                    evidence = await ReadSpellEvidenceAsync(effect, effectReader.Reader, edges, token);
                }
                catch (Exception error) when (PhysicalFailure(error, out _))
                {
                    _ = PhysicalFailure(error, out var state); effectReader?.Invalidate(); Invalidate(effect, false);
                    effect = Failed(effect, state); evidence = SpellsForState(effect, edges);
                }
            }
            ItemEffectNameOutcome[] outcomes;
            if (name.CurrentProfile)
            {
                try
                {
                    // A consulted Name source can remain independently Ready even if late Effect loss
                    // removes every current lookup. Verify its retained source; decode no ineligible row.
                    nameReader = await BorrowTargetAsync(original, name, token);
                    outcomes = await ReadNameOutcomesAsync(name, nameReader.Reader, edges, evidence, token);
                }
                catch (Exception error) when (PhysicalFailure(error, out _))
                {
                    _ = PhysicalFailure(error, out var state); nameReader?.Invalidate(); Invalidate(name, false);
                    name = Failed(name, state); outcomes = NamesForState(name, edges, evidence);
                }
            }
            else outcomes = NamesForState(name, edges, evidence);
            var result = new ItemEffectNamesResult(owned.Selection, owned.BridgeIndexArtifactId, owned.ItemEffectArtifactId,
                owned.PolicyRevision, Array.AsReadOnly(new[] { effect.Public, name.Public }), Array.AsReadOnly(outcomes));
            ItemEffectsValidation.Validate(result, owned); await CountAsync(result, token, true);
            var final = await FinishAsync(original, bridge, [effect, name], token);
            if (final[0].Public != effect.Public)
            {
                evidence = SpellsForState(final[0], edges); outcomes = NamesForState(final[1], edges, evidence);
            }
            else if (final[1].Public != name.Public) outcomes = NamesForState(final[1], edges, evidence);
            result = result with { Sources = Array.AsReadOnly(new[] { final[0].Public, final[1].Public }), Outcomes = Array.AsReadOnly(outcomes) };
            ItemEffectsValidation.Validate(result, owned); return result;
        }
        catch { index.Invalidate(); throw; }
        finally
        {
            Exception? failure = null;
            if (effectReader is not null) try { await effectReader.DisposeAsync(); } catch (Exception error) { failure = error; }
            if (nameReader is not null) try { await nameReader.DisposeAsync(); } catch (Exception error) { failure ??= error; }
            if (failure is not null) { Debt(); throw Cleanup(); }
        }
    }
    private static ItemEffectSpellEvidence[] SpellsForState(SourceProbe source, EdgeSelection[] edges) => edges.Select(edge =>
        edge.Tuple.ItemEffectId < 0 ? new ItemEffectSpellEvidence(ItemEffectState.EffectKeyOutOfRange, N(edge.Tuple.ItemEffectId)) :
        new ItemEffectSpellEvidence(source.Public.State == ItemEffectsSourceState.Ready ? ItemEffectState.NotPrepared :
            Enum.Parse<ItemEffectState>(source.Public.State.ToString()), N(edge.Tuple.ItemEffectId), Problem: source.Public.Problem)).ToArray();
    private async Task<ItemEffectSpellEvidence[]> ReadSpellEvidenceAsync(SourceProbe source, TableRowArtifacts reader,
        EdgeSelection[] edges, CancellationToken token)
    {
        using var query = EnterQuery(token); var unique = new Dictionary<int, ItemEffectSpellEvidence>(edges.Length);
        var result = new ItemEffectSpellEvidence[edges.Length];
        for (var i = 0; i < edges.Length; i++)
        {
            var id = edges[i].Tuple.ItemEffectId;
            if (id < 0) { result[i] = new(ItemEffectState.EffectKeyOutOfRange, N(id)); continue; }
            if (!unique.TryGetValue(id, out var evidence))
            {
                var found = await reader.ReadUniqueAsync(source.Admission!.Artifact, (uint)id, token);
                await CpuAsync(() => evidence = SpellEvidence(source, id, found), token);
                found = null!; unique.Add(id, evidence!);
            }
            result[i] = evidence!;
        }
        return result;
    }
    private static ItemEffectSpellEvidence SpellEvidence(SourceProbe source, int id, TableUniqueRow found)
    {
        if (found.MatchCount is < 0 or > TableRowArtifacts.MaxRows || (found.MatchCount == 1) != (found.Row is not null)) throw RowInvalid();
        if (found.MatchCount == 0) return new(ItemEffectState.NotFoundInAdmittedScope, N(id), "0", Absence: Absence(source));
        if (found.MatchCount > 1) return new(ItemEffectState.Ambiguous, N(id), N(found.MatchCount));
        var row = found.Row!;
        if (row.Id != (uint)id || row.IsCopy != row.CopySourceId.HasValue || row.Values.Count != 10 || row.Values[0] != N(row.Id)) throw RowInvalid();
        if (!Signed(row.Values[7]) || row.TextIssues?.Any(issue => issue.ColumnIndex == 7) == true)
            return new(ItemEffectState.FieldUnavailable, N(id), Problem: FieldProblem);
        return new(ItemEffectState.ObservedReady, N(id), "1", Target(source, row), new("4002a5b1", 7, row.Values[7]));
    }
    private static ItemEffectNameOutcome[] NamesForState(SourceProbe name, EdgeSelection[] edges, ItemEffectSpellEvidence[] evidence)
    {
        var result = new ItemEffectNameOutcome[edges.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var effect = evidence[i];
            result[i] = effect.State != ItemEffectState.ObservedReady ? new(edges[i].Key, effect, ItemEffectNameState.EffectNotReady) :
                effect.SpellId!.Value[0] == '-' ? new(edges[i].Key, effect, ItemEffectNameState.SpellKeyOutOfRange) :
                new(edges[i].Key, effect, Enum.Parse<ItemEffectNameState>(name.Public.State.ToString()), Problem: name.Public.Problem);
        }
        return result;
    }
    private async Task<ItemEffectNameOutcome[]> ReadNameOutcomesAsync(SourceProbe source, TableRowArtifacts reader,
        EdgeSelection[] edges, ItemEffectSpellEvidence[] evidence, CancellationToken token)
    {
        using var query = EnterQuery(token); var unique = new Dictionary<int, ItemEffectNameOutcome>(edges.Length);
        var result = new ItemEffectNameOutcome[edges.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var effect = evidence[i]; var key = edges[i].Key;
            if (effect.State != ItemEffectState.ObservedReady) { result[i] = new(key, effect, ItemEffectNameState.EffectNotReady); continue; }
            if (effect.SpellId!.Value[0] == '-') { result[i] = new(key, effect, ItemEffectNameState.SpellKeyOutOfRange); continue; }
            var id = int.Parse(effect.SpellId.Value, CultureInfo.InvariantCulture);
            if (!unique.TryGetValue(id, out var outcome))
            {
                var found = await reader.ReadUniqueAsync(source.Admission!.Artifact, (uint)id, token);
                await CpuAsync(() => outcome = NameOutcome(source, key, effect, id, found), token);
                found = null!; unique.Add(id, outcome!);
            }
            result[i] = outcome! with { Key = key, Effect = effect };
        }
        return result;
    }
    private static ItemEffectNameOutcome NameOutcome(SourceProbe source, ItemEffectsEdgeKey key, ItemEffectSpellEvidence effect, int id, TableUniqueRow found)
    {
        if (found.MatchCount is < 0 or > TableRowArtifacts.MaxRows || (found.MatchCount == 1) != (found.Row is not null)) throw RowInvalid();
        if (found.MatchCount == 0) return new(key, effect, ItemEffectNameState.NotFoundInAdmittedScope, "0", Absence: Absence(source));
        if (found.MatchCount > 1) return new(key, effect, ItemEffectNameState.Ambiguous, N(found.MatchCount));
        var row = found.Row!;
        if (row.Id != (uint)id || row.IsCopy != row.CopySourceId.HasValue || row.Values.Count != 2 || row.Values[0] != N(row.Id)) throw RowInvalid();
        var target = Target(source, row); var text = row.Values[1];
        if (row.TextIssues?.Any(issue => issue.ColumnIndex == 1) == true)
        {
            if (row.TextIssues.Count(issue => issue.ColumnIndex == 1) != 1 || row.TextIssues.Single(issue => issue.ColumnIndex == 1).Code != "DB2_TEXT_ENCODING_INVALID") throw RowInvalid();
            return new(key, effect, ItemEffectNameState.InvalidText, "1", target,
                Issue: new("DB2_TEXT_ENCODING_INVALID", target with { ColumnIndex = 1 }));
        }
        if (text.Length > 65536 || !WellFormed(text)) throw RowInvalid();
        if (text.Length > 4096) return new(key, effect, ItemEffectNameState.TextTooLarge, "1", target, TextUnits: N(text.Length));
        return new(key, effect, text.Length == 0 ? ItemEffectNameState.EmptyText : ItemEffectNameState.Ready, "1", target, text);
    }
    private static bool WellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}
