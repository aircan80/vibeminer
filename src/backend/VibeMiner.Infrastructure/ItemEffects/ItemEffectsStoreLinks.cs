using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class ItemEffectsStore
{
    private sealed record BridgeResolution(SourceProbe Source, StoredItemEffectsBridgeAdmission? Index);
    private sealed record EdgeSelection(ItemEffectsEdgeKey Key, ItemEffectsBridgeTuple Tuple);
    private static ItemEffectsBridgeProof? BridgeProof(BridgeResolution? bridge) => bridge?.Source.Admission is { } source ?
        new(source, bridge.Source.CapturedSnapshot!, bridge.Index) : null;
    private static ItemEffectsBridgeBinding Binding(ItemEffectsItemsProof original, SourceProbe source) => new(
        original.Snapshot.Context, original.Opening.Build.Version, original.Opening.Reference, original.Configurations,
        source.CapturedSnapshot!, source.Admission!.Artifact, new(new("00cb674f", 0), new("00cb674f", 1), new("00cb674f", 2)));

    public Task<ItemEffectsReadiness> ReadAsync(ItemEffectsSelection selection, CancellationToken token) =>
        RunAsync(false, null, token, operation => LinksAsync(selection, false, operation));
    public Task<ItemEffectsReadiness> PrepareLinksAsync(ItemEffectsSelection selection, Func<ItemEffectsProgress, Task> progress, CancellationToken token) =>
        RunAsync(true, progress, token, operation => LinksAsync(selection, true, operation));
    private async Task<ItemEffectsReadiness> LinksAsync(ItemEffectsSelection selection, bool prepare, Operation operation)
    {
        var token = operation.Token; var original = await CaptureAsync(selection, token);
        if (!original.Item.Admitted || ulong.Parse(original.Item.ItemId, CultureInfo.InvariantCulture) > int.MaxValue)
        {
            var independent = new ItemEffectsReadiness(selection, original.Item.Admitted,
                original.Item.Admitted ? ItemEffectsReadinessState.ItemKeyOutOfRange : ItemEffectsReadinessState.MissingItem);
            await CountAsync(independent, token); await FinishAsync(original, null, [], token); return independent;
        }
        var resolution = await ResolveBridgeAsync(original, prepare, operation);
        if (!resolution.Source.CurrentProfile)
        {
            var result = Readiness(selection, resolution, null);
            await CountAsync(result, token); await FinishAsync(original, resolution, [], token); return result;
        }
        await operation.ReportAsync(new(prepare ? "Preparing effects bridge index" : "Verifying local effects bridge", "stage", TableId: "00cb674f"));
        BridgeBorrow? borrow = null;
        try
        {
            try { borrow = await BorrowBridgeAsync(original, resolution, prepare, !prepare, token); }
            catch (Exception error) when (AcquisitionFailure(error, out _))
            {
                _ = AcquisitionFailure(error, out var state); resolution = resolution with { Source = Failed(resolution.Source, state) };
                var failure = Readiness(selection, resolution, null);
                await CountAsync(failure, token); await FinishAsync(original, resolution, [], token); return failure;
            }
            if (prepare)
            {
                var index = borrow.Index!;
                var incoming = index.Artifact with { Binding = Binding(original, resolution.Source) };
                await operation.ReportAsync(new("Saving exact effects bridge admission", "stage", TableId: "00cb674f"));
                var preparedAt = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
                var saved = await CatalogCopyAsync(() => catalog.CommitItemEffectsBridgeAdmissionAsync(original, incoming, preparedAt, token), token);
                resolution = resolution with { Index = saved };
            }
            if (resolution.Index is null)
            {
                var notPrepared = Readiness(selection, resolution, null);
                await CountAsync(notPrepared, token); await FinishAsync(original, resolution, [], token); return notPrepared;
            }
            ItemEffectsBridgeRange range;
            using (EnterQuery(token)) range = await borrow.Index!.Session.FindRangeAsync(Parent(original), token);
            var ready = Readiness(selection, resolution, range.Count);
            await CountAsync(ready, token); await FinishAsync(original, resolution, [], token); return ready;
        }
        catch { borrow?.Invalidate(); throw; }
        finally { if (borrow is not null) await borrow.DisposeAsync(); }
    }
    private static ItemEffectsReadiness Readiness(ItemEffectsSelection selection, BridgeResolution resolution, uint? count)
    {
        var source = resolution.Source.Public;
        if (source.State == ItemEffectsSourceState.Ready && resolution.Index is { } index && count is not null)
            return new(selection, true, ItemEffectsReadinessState.Ready,
                new(selection.Context, index.Artifact.ContentKey, index.PreparedAt, ItemEffectsValidation.IndexRevision,
                    ItemEffectsValidation.OrderPolicy, source.Snapshot!, index.Artifact.Binding.Fields, index.Artifact.RowCount), N(count.Value), source);
        if (source.State is ItemEffectsSourceState.NotPrepared or ItemEffectsSourceState.Ready)
            return new(selection, true, ItemEffectsReadinessState.NotPrepared, Source: source);
        return new(selection, true, Enum.Parse<ItemEffectsReadinessState>(source.State.ToString()), Source: source, Problem: source.Problem);
    }
    private async Task<BridgeResolution> ResolveBridgeAsync(ItemEffectsItemsProof original, bool prepare, Operation operation)
    {
        var token = operation.Token; var source = await ProbeAsync(original, ItemEffectsSourceRole.Bridge, token);
        if (prepare) source = await EnsureAsync(original, source, null, operation);
        if (!source.CurrentProfile) return new(source, null);
        if (source.Admission!.Artifact.Completion.PhysicalRows + source.Admission.Artifact.Completion.CopyRows > ItemEffectsBridgePolicy.MaxRows)
            return new(Failed(source, ItemEffectsSourceState.ResourceLimit), null);
        StoredItemEffectsBridgeAdmission? saved;
        using (EnterQuery(token)) saved = await catalog.ReadItemEffectsBridgeAdmissionAsync(original.Opening, token);
        if (saved is not null)
        {
            var same = false; await CpuAsync(() => same = ItemEffectsBridgePolicy.SameCurrentSource(saved.Artifact.Binding.Source, source.CapturedSnapshot!) &&
                ItemEffectsBridgePolicy.SameAuthority(saved.Artifact.Binding.Rows, source.Admission.Artifact) &&
                ItemEffectsBridgePolicy.SameAuthority(saved.Artifact.Binding.Configurations, original.Configurations) &&
                saved.Artifact.Binding.SourceReference == original.Opening.Reference, token);
            if (!same) saved = null;
        }
        return new(source, saved);
    }
    private async Task<BridgeResolution> RequireBridgeAsync(ItemEffectsItemsProof original, string expected, Operation operation)
    {
        var resolution = await ResolveBridgeAsync(original, false, operation);
        if (resolution.Index is null || !resolution.Source.CurrentProfile)
            throw new DiscoveryException("ITEM_EFFECTS_NOT_PREPARED", "Prepare the current effects bridge index before querying selected effects.");
        if (resolution.Index.Artifact.ContentKey != expected) throw Stale();
        return resolution;
    }
    public Task<ItemEffectsPage> ReadPageAsync(ItemEffectsPageRequest request, CancellationToken token) => RunAsync(false, null, token, async operation =>
    {
        var owned = ItemEffectsValidation.Snapshot(request); var original = await CaptureAsync(owned.Selection, operation.Token); RequireItem(original);
        var resolution = await RequireBridgeAsync(original, owned.BridgeIndexArtifactId, operation);
        await using var borrow = await BorrowBridgeAsync(original, resolution, false, false, operation.Token);
        try
        {
            ItemEffectsPage page;
            using (EnterQuery(operation.Token)) page = await borrow.Index!.Session.ReadPageAsync(owned, resolution.Source.CapturedSnapshot!, operation.Token);
            await FinishAsync(original, resolution, [], operation.Token); return page;
        }
        catch { borrow.Invalidate(); throw; }
    });
    private async Task<EdgeSelection[]> ReadEdgesAsync(BridgeBorrow index, int selectedParent,
        IReadOnlyList<ItemEffectsEdgeKey> keys, CancellationToken token)
    {
        using var query = EnterQuery(token); var result = new EdgeSelection[keys.Count];
        for (var i = 0; i < result.Length; i++) result[i] = new(keys[i], await index.Index!.Session.ReadTupleAsync(
            uint.Parse(keys[i].IndexOrdinal, CultureInfo.InvariantCulture), selectedParent, token));
        return result;
    }
    private async Task CountAsync<T>(T result, CancellationToken token, bool targetsMayFail = false)
    {
        using var query = EnterQuery(token);
        observeResources?.Invoke("result-before-count");
        using (await resources.EnterCpuAsync(null, token)) await ItemEffectsStoreResponseCounter.RequireAsync(result, token, targetsMayFail);
    }
}
