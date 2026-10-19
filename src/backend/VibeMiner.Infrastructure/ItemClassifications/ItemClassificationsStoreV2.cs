using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class ItemClassificationsStore
{
    private sealed class KindResult(ItemClassificationKind kind)
    {
        internal readonly ItemClassificationKind Kind = kind;
        internal Held? Held;
        internal ItemClassificationSource Source = new(kind, ItemClassificationState.Unavailable, null, null,
            new("ITEM_CLASSIFICATION_KEYS_UNAVAILABLE", "No admitted verified Item keys are available."));
    }
    private async Task<ItemClassificationBatch> PrepareV2(ItemClassificationBatchRequest request,
        Func<ItemClassificationProgress, Task> progress, CancellationToken token)
    {
        var authorization = await items.AuthorizeSummariesAsync(request.Context, request.ArtifactId, request.ItemIds, token);
        var opening = await catalog.ResolveOpeningAsync(Workspace(request.Context), token);
        var classColumn = await ItemColumn(authorization.Snapshot, opening.Build.Version, "ClassID", 32, false, token);
        var subColumn = request.IncludeSubclass ? await ItemColumn(authorization.Snapshot, opening.Build.Version, "SubclassID", 8, true, token) : -1;
        var kinds = new List<KindResult> { new(ItemClassificationKind.Class) };
        if (request.IncludeSubclass) kinds.Add(new(ItemClassificationKind.Subclass));
        var eligible = kinds.ToDictionary(k => k.Kind, k => authorization.Items.Any(i =>
            SourceKey(i, classColumn, subColumn, k.Kind).State is null));
        operations.Release();
        try
        {
            foreach (var result in kinds)
            {
                if (!eligible[result.Kind]) continue;
                try { await EnsurePrerequisite(request.Context, progress, token, result.Kind); }
                catch (DiscoveryException error) when (!FatalV2(error)) { result.Source = Failed(result.Kind, error); eligible[result.Kind] = false; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result.Source = Failed(result.Kind, Io()); eligible[result.Kind] = false; }
            }
        }
        finally { await operations.WaitAsync(CancellationToken.None); }
        token.ThrowIfCancellationRequested();
        authorization = await items.AuthorizeSummariesAsync(request.Context, request.ArtifactId, request.ItemIds, token);
        var pins = new HashSet<SessionKey>();
        foreach (var result in kinds)
        {
            if (!eligible[result.Kind]) continue;
            try
            {
                result.Held = await Target(request.Context, opening, authorization.Snapshot, progress, token, result.Kind, pins);
                pins.Add(new(request.Context, result.Kind));
                var artifact = result.Held.Index.Artifact;
                result.Source = new(result.Kind, artifact.Binding.Source.Coverage.Scope == "complete"
                    ? ItemClassificationState.Ready : ItemClassificationState.IncompleteTarget, await PublicSnapshot(artifact.Binding, token), artifact.ContentKey);
            }
            catch (DiscoveryException error) when (!FatalV2(error)) { result.Source = Failed(result.Kind, error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result.Source = Failed(result.Kind, Io()); }
        }
        var outcomes = new ItemClassificationOutcome[authorization.Items.Count];
        for (var i = 0; i < outcomes.Length; i++)
        {
            var item = authorization.Items[i];
            var values = new ItemClassificationValue[kinds.Count];
            for (var j = 0; j < kinds.Count; j++)
            {
                var result = kinds[j]; var key = SourceKey(item, classColumn, subColumn, result.Kind);
                if (key.State is { } state) values[j] = new(state, key.Keys);
                else if (result.Held is null) values[j] = new(result.Source.State, key.Keys, Problem: result.Source.Problem);
                else
                {
                    try { values[j] = await result.Held.Index.ResolveAsync(key.Key, key.Keys, token); }
                    catch (DiscoveryException error) when (!FatalV2(error))
                    { result.Source = Failed(result.Kind, error); await RetireKind(request.Context, result); values[j] = new(result.Source.State, key.Keys, Problem: result.Source.Problem); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { result.Source = Failed(result.Kind, Io()); await RetireKind(request.Context, result); values[j] = new(result.Source.State, key.Keys, Problem: result.Source.Problem); }
                }
            }
            outcomes[i] = new(item.ItemId, item.Admitted, values[0], request.IncludeSubclass ? values[1] : null);
        }
        foreach (var result in kinds)
        {
            if (result.Held is null) continue;
            try { await ValidateCurrentProfile(result.Held.Index.Artifact.Binding, token); }
            catch (DiscoveryException error) when (!FatalV2(error))
            { result.Source = Failed(result.Kind, error); await RetireKind(request.Context, result); }
        }
        var currentItems = await items.AuthorizeSummariesAsync(request.Context, request.ArtifactId, [], token);
        var successful = kinds.Where(k => k.Held is not null).ToArray();
        var fenced = await catalog.ReauthorizeItemClassificationBatchAsync(opening, currentItems.Snapshot,
            successful.Select(k => k.Held!.Index.Artifact).ToArray(), token);
        for (var i = 0; i < successful.Length; i++)
        {
            if (fenced[i] is not { } problem) continue;
            var result = successful[i]; result.Source = Failed(result.Kind, new(problem.Code, problem.Message, problem.Retryable));
            await RetireKind(request.Context, result);
        }
        foreach (var result in kinds)
        {
            if (result.Held is not null) continue;
            for (var i = 0; i < outcomes.Length; i++)
            {
                var value = result.Kind == ItemClassificationKind.Class ? outcomes[i].Class : outcomes[i].Subclass!;
                if (value.State is ItemClassificationState.MissingItem or ItemClassificationState.FieldUnavailable or ItemClassificationState.KeyOutOfRange) continue;
                value = new(result.Source.State, value.SourceKeys, Problem: result.Source.Problem);
                outcomes[i] = result.Kind == ItemClassificationKind.Class ? outcomes[i] with { Class = value } : outcomes[i] with { Subclass = value };
            }
        }
        var batch = new ItemClassificationBatch(request.Context, request.ArtifactId, request.PolicyRevision,
            kinds.Select(k => k.Source).ToArray(), Array.AsReadOnly(outcomes));
        RequireResponseBudget(batch); return batch;
    }
    private async Task RetireKind(ItemsContext context, KindResult result)
    {
        if (sessions.Remove(new(context, result.Kind), out var held)) await held.DisposeAsync();
        result.Held = null;
    }
    private static DiscoveryException Io() => new("ITEM_CLASSIFICATION_IO", "Optional classification storage or acquisition could not complete.", true);
    private static bool FatalV2(DiscoveryException error) => error.Code.StartsWith("ITEMS_", StringComparison.Ordinal) || error.Code == "ENGINE_STOPPING";
    private static ItemClassificationSource Failed(ItemClassificationKind kind, DiscoveryException error) => new(kind,
        error.Code == "ITEM_CLASSIFICATION_STALE" ? ItemClassificationState.Unavailable : FailureState(error), null, null, new(error.Code, error.Message, error.Retryable));

    internal static (ItemClassificationState? State, int Key, IReadOnlyList<ItemSourceValue> Keys) SourceKey(
        AuthorizedItemSummary item, int classColumn, int subColumn, ItemClassificationKind kind)
    {
        var classKeys = Keys(item, classColumn);
        var keys = kind == ItemClassificationKind.Class ? classKeys : Array.AsReadOnly(classKeys.Concat(Keys(item, subColumn)).ToArray());
        if (!item.Admitted) return (ItemClassificationState.MissingItem, 0, keys);
        if (classKeys.Count != 1 || !int.TryParse(classKeys[0].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var classId) ||
            N(classId) != classKeys[0].Value) return (ItemClassificationState.FieldUnavailable, 0, keys);
        var subId = 0;
        if (kind == ItemClassificationKind.Subclass)
        {
            var subs = Keys(item, subColumn);
            if (subs.Count != 1 || !byte.TryParse(subs[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || N(parsed) != subs[0].Value)
                return (ItemClassificationState.FieldUnavailable, 0, keys);
            subId = parsed;
        }
        if (classId is < -128 or > 127 || subId > 127) return (ItemClassificationState.KeyOutOfRange, 0, keys);
        return (null, kind == ItemClassificationKind.Class ? classId : ((classId + 128) << 8) | (subId + 128), keys);
    }
}
