using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record ItemEffectsStoreStagingHooks(Func<CancellationToken, Task<IDisposable>>? Reserve = null,
    Func<CancellationToken, Task<FileStream>>? Create = null, Action<string>? Delete = null);

public sealed partial class ItemEffectsStore
{
    private abstract class PhysicalSlot(string key, StoredTableAdmission admission)
    {
        internal readonly string Key = key;
        internal readonly StoredTableAdmission Admission = admission;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<SourceHeld> SourceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<object> Uncertain = new(3);
        internal Task? SourceTask, Disposal;
        internal TaskCompletionSource? Notification;
        internal int Pins;
        internal bool ProducerFinished, SourceFinished, Retiring, Debt;
    }
    private sealed class BridgeSlot(string key, StoredTableAdmission admission) : PhysicalSlot(key, admission)
    {
        internal TaskCompletionSource<IndexHeld>? IndexReady;
        internal CancellationTokenSource? IndexStop;
        internal Task? IndexTask;
        internal TaskCompletionSource? IndexNotification;
        internal bool IndexProducerFinished, IndexFinished;
        internal int IndexPins;
        internal DerivedArtifactStaging? StagingDebt;
    }
    private sealed class TargetSlot(string key, StoredTableAdmission admission) : PhysicalSlot(key, admission);
    private sealed class SourceHeld(FileStream encoded, TableRowArtifacts rows, TableArtifactPage? first) : IAsyncDisposable
    {
        internal readonly TableRowArtifacts Rows = rows;
        internal TableArtifactPage? FirstPage = first;
        private Task? retirement;
        public ValueTask DisposeAsync() => new(retirement ??= RetireAsync());
        private async Task RetireAsync()
        {
            FirstPage = null; Exception? failure = null;
            try { await Rows.DisposeAsync(); } catch (Exception error) { failure = error; }
            try { await encoded.DisposeAsync(); } catch (Exception error) { failure ??= error; }
            if (failure is not null) throw Cleanup();
        }
    }
    private sealed record IndexHeld(ItemEffectsBridgeArtifact Artifact, ItemEffectsBridgeArtifacts.Session Session);
    private sealed class BridgeBorrow(ItemEffectsStore owner, BridgeSlot slot, SourceHeld source, IndexHeld? index) : IAsyncDisposable
    {
        internal readonly SourceHeld Source = source;
        internal readonly IndexHeld? Index = index;
        internal void Invalidate() { lock (owner.gate) slot.Retiring = true; }
        public ValueTask DisposeAsync() => new(owner.ReleaseAsync(slot, Index is not null));
    }
    private sealed class TargetBorrow(ItemEffectsStore owner, TargetSlot slot, SourceHeld source) : IAsyncDisposable
    {
        internal readonly TableRowArtifacts Reader = source.Rows;
        internal void Invalidate() { lock (owner.gate) slot.Retiring = true; }
        public ValueTask DisposeAsync() => new(owner.ReleaseAsync(slot, false));
    }
    private static string PhysicalKey(ItemEffectsItemsProof original, SourceProbe probe)
    {
        var source = probe.Admission!; var artifact = source.Artifact; var opening = original.Opening.Reference;
        return original.Snapshot.Context + "/" + opening + "/" + probe.Role + "/" + artifact.ContentKey + "/" + artifact.Sha256 +
            "/" + artifact.ByteLength.ToString(CultureInfo.InvariantCulture) + "/" + source.Source.Sha256 + "/" + source.Source.EncodedBytes;
    }
    private async Task<TSlot> ReserveAsync<TSlot>(List<TSlot> pool, string key, Func<TSlot> create,
        bool localOnly, bool firstPage, CancellationToken token) where TSlot : PhysicalSlot
    {
        TSlot slot; bool start;
        while (true)
        {
            TSlot? evict = null;
            lock (gate)
            {
                token.ThrowIfCancellationRequested(); if (cleanupDebt) throw Cleanup();
                slot = pool.FirstOrDefault(candidate => candidate.Key == key && !candidate.Retiring)!;
                if (slot is not null)
                {
                    if (localOnly && !slot.SourceFinished) throw Busy();
                    slot.Pins++; start = false; break;
                }
                if (pool.Count < 2) { slot = create(); slot.Pins = 1; pool.Add(slot); start = true; break; }
                evict = pool.FirstOrDefault(candidate => candidate.Pins == 0 && candidate.SourceFinished &&
                    (candidate is not BridgeSlot bridge || bridge.IndexReady is null || bridge.IndexFinished));
                if (evict is null) throw Busy(); evict.Retiring = true;
            }
            await DisposeSlotAsync(evict);
        }
        if (start)
        {
            var worker = OpenSourceAsync(slot, firstPage);
            slot.SourceTask = CompleteSourceAsync(slot, worker);
        }
        try { _ = await slot.SourceReady.Task.WaitAsync(token); return slot; }
        catch { await ReleaseAsync(slot, false); throw; }
    }
    private async Task<SourceHeld> OpenSourceAsync(PhysicalSlot slot, bool firstPage)
    {
        FileStream? encoded = null; TableRowArtifacts? rows = null;
        try
        {
            var token = slot.Stop.Token; var source = slot.Admission;
            encoded = openSource is { } open ? await open(source, token) : await objects.OpenVerifiedAsync(source.Source.Sha256,
                long.Parse(source.Source.EncodedBytes, CultureInfo.InvariantCulture), resources, null, token);
            rows = new(objects, resources, null);
            var total = checked(source.Artifact.Completion.PhysicalRows + source.Artifact.Completion.CopyRows);
            var page = await rows.ReadPageAsync(source.Artifact, firstPage ? 0 : total, firstPage ? 50 : 1, null, token);
            var held = new SourceHeld(encoded, rows, firstPage ? page : null); encoded = null; rows = null; return held;
        }
        catch
        {
            var debt = false;
            if (rows is not null) try { await rows.DisposeAsync(); } catch { lock (gate) slot.Uncertain.Add(rows); debt = true; }
            if (encoded is not null) try { await encoded.DisposeAsync(); } catch { lock (gate) slot.Uncertain.Add(encoded); debt = true; }
            if (debt) { lock (gate) slot.Debt = true; Debt(); throw Cleanup(); }
            throw;
        }
    }
    private async Task CompleteSourceAsync(PhysicalSlot slot, Task<SourceHeld> worker)
    {
        SourceHeld? held = null; Exception? failure = null;
        try { held = await worker; } catch (Exception error) { failure = error; }
        Task? notification;
        lock (gate) { slot.ProducerFinished = true; notification = slot.Notification?.Task; }
        if (notification is not null) await notification;
        lock (gate)
        {
            slot.SourceFinished = true;
            if (failure is null) slot.SourceReady.TrySetResult(held!);
            else { slot.Retiring = true; slot.SourceReady.TrySetException(failure); }
        }
    }
    private Task CancelSlotAsync(PhysicalSlot slot, bool index)
    {
        TaskCompletionSource completion; CancellationTokenSource cancellation;
        lock (gate)
        {
            if (index)
            {
                var bridge = (BridgeSlot)slot;
                if (bridge.IndexNotification is not null) return bridge.IndexNotification.Task;
                if (bridge.IndexProducerFinished || bridge.IndexStop is null) return Task.CompletedTask;
                completion = bridge.IndexNotification = new(TaskCreationOptions.RunContinuationsAsynchronously); cancellation = bridge.IndexStop;
            }
            else
            {
                if (slot.Notification is not null) return slot.Notification.Task;
                if (slot.ProducerFinished) return Task.CompletedTask;
                completion = slot.Notification = new(TaskCreationOptions.RunContinuationsAsynchronously); cancellation = slot.Stop;
            }
        }
        _ = NotifyAsync(); return completion.Task;
        async Task NotifyAsync()
        {
            await Task.Yield();
            try { await cancellation.CancelAsync(); } catch (AggregateException) { }
            finally { completion.TrySetResult(); }
        }
    }
    private async Task ReleaseAsync(PhysicalSlot slot, bool index)
    {
        bool cancelSource, cancelIndex, dispose;
        lock (gate)
        {
            slot.Pins--; if (slot.Pins < 0) throw Cleanup();
            if (index) { var bridge = (BridgeSlot)slot; bridge.IndexPins--; if (bridge.IndexPins < 0) throw Cleanup(); }
            cancelSource = slot.Pins == 0 && !slot.SourceFinished;
            cancelIndex = slot is BridgeSlot b && b.IndexReady is not null && b.IndexPins == 0 && !b.IndexFinished;
            if (cancelSource || cancelIndex) slot.Retiring = true;
            dispose = slot.Pins == 0 && (closed || slot.Retiring);
        }
        // Both notifications start before either can wait for a foreign callback.
        var sourceNotification = cancelSource ? CancelSlotAsync(slot, false) : Task.CompletedTask;
        var indexNotification = cancelIndex ? CancelSlotAsync(slot, true) : Task.CompletedTask;
        await sourceNotification; await indexNotification;
        if (dispose) await DisposeSlotAsync(slot);
    }
    private async Task<TargetBorrow> BorrowTargetAsync(ItemEffectsItemsProof original, SourceProbe probe, CancellationToken token)
    {
        using var query = EnterQuery(token);
        var key = PhysicalKey(original, probe);
        var slot = await ReserveAsync(targets, key, () => new TargetSlot(key, probe.Admission!), false, false, token);
        try
        {
            await RequireHeldAuthorityAsync(slot, probe, token);
            return new(this, slot, await slot.SourceReady.Task);
        }
        catch { lock (gate) slot.Retiring = true; await ReleaseAsync(slot, false); throw; }
    }
    private async Task RequireHeldAuthorityAsync(PhysicalSlot slot, SourceProbe probe, CancellationToken token)
    {
        var exact = false;
        await CpuAsync(() => exact = ItemEffectsBridgePolicy.SameAuthority(slot.Admission.Artifact, probe.Admission!.Artifact) &&
            ItemEffectsBridgePolicy.SameAuthority(slot.Admission.Source, probe.Admission.Source) &&
            ItemEffectsBridgePolicy.SameAuthority(slot.Admission.Association, probe.Admission.Association), token);
        if (!exact) { lock (gate) slot.Retiring = true; throw RowInvalid(); }
    }
    private async Task<BridgeBorrow> BorrowBridgeAsync(ItemEffectsItemsProof original, BridgeResolution resolution,
        bool allowBuild, bool localOnly, CancellationToken token)
    {
        using var query = EnterQuery(token);
        var probe = resolution.Source; var key = PhysicalKey(original, probe);
        var slot = await ReserveAsync(bridges, key, () => new BridgeSlot(key, probe.Admission!), localOnly,
            allowBuild && resolution.Index is null, token);
        var indexed = false;
        try
        {
            var source = await slot.SourceReady.Task;
            await RequireHeldAuthorityAsync(slot, probe, token);
            if (!allowBuild && resolution.Index is null) return new(this, slot, source, null);
            bool start;
            lock (gate)
            {
                if (slot.Retiring) throw Busy();
                if (localOnly && slot.IndexReady is not null && !slot.IndexFinished) throw Busy();
                start = slot.IndexReady is null;
                if (start)
                {
                    if (allowBuild && resolution.Index is null && builder) throw Busy();
                    if (allowBuild && resolution.Index is null) builder = true;
                    slot.IndexReady = new(TaskCreationOptions.RunContinuationsAsynchronously); slot.IndexStop = new();
                }
                slot.IndexPins++; indexed = true;
            }
            if (start)
            {
                var worker = PromoteAsync(Binding(original, resolution.Source), resolution, source, slot, allowBuild);
                slot.IndexTask = CompleteIndexAsync(slot, worker);
            }
            var index = await slot.IndexReady!.Task.WaitAsync(token);
            var exact = false;
            await CpuAsync(() => exact = resolution.Index is not null ?
                ItemEffectsBridgePolicy.SameAuthority(index.Artifact, resolution.Index.Artifact) :
                ItemEffectsBridgePolicy.SameCurrentSource(index.Artifact.Binding.Source, resolution.Source.CapturedSnapshot!) &&
                ItemEffectsBridgePolicy.SameAuthority(index.Artifact.Binding.Rows, resolution.Source.Admission!.Artifact) &&
                ItemEffectsBridgePolicy.SameAuthority(index.Artifact.Binding.Configurations, original.Configurations) &&
                index.Artifact.Binding.SourceReference == original.Opening.Reference, token);
            if (!exact) { lock (gate) slot.Retiring = true; throw ItemEffectsBridgePolicy.Invalid(); }
            return new(this, slot, source, index);
        }
        catch { await ReleaseAsync(slot, indexed); throw; }
    }
    private async Task<IndexHeld> PromoteAsync(ItemEffectsBridgeBinding binding, BridgeResolution resolution, SourceHeld source, BridgeSlot slot, bool allowBuild)
    {
        var token = slot.IndexStop!.Token; var ownsBuilder = allowBuild && resolution.Index is null;
        var artifact = resolution.Index?.Artifact;
        var module = new ItemEffectsBridgeArtifacts(CpuAsync);
        try
        {
            if (artifact is null)
            {
                string? key = null; await CpuAsync(() => key = ItemEffectsBridgePolicy.ContentKey(binding, resolution.Source.CapturedSnapshot!.RowCount), token);
                artifact = await catalog.FindItemEffectsBridgeArtifactAsync(key!, token);
            }
            if (artifact is not null)
            {
                var same = false;
                await CpuAsync(() => same = ItemEffectsBridgePolicy.SameCurrentSource(artifact.Binding.Source, binding.Source) &&
                    ItemEffectsBridgePolicy.SameAuthority(artifact.Binding.Rows, binding.Rows) &&
                    ItemEffectsBridgePolicy.SameAuthority(artifact.Binding.Configurations, binding.Configurations) &&
                    artifact.Binding.SourceReference == binding.SourceReference, token);
                if (!same) throw ItemEffectsBridgePolicy.Invalid();
                try { return await OpenIndexAsync(artifact, module, slot, token); }
                catch (DiscoveryException error) when (error.Code == "LOCAL_OBJECT_MISSING")
                { if (!allowBuild) throw ItemEffectsBridgePolicy.Invalid(); }
                catch (DiscoveryException error) when (error.Code == "LOCAL_OBJECT_CORRUPT")
                { if (!allowBuild) throw ItemEffectsBridgePolicy.Invalid(); throw; }
            }
            if (!allowBuild) throw ItemEffectsBridgePolicy.Invalid();
            if (!ownsBuilder)
            {
                lock (gate) { if (builder) throw Busy(); builder = ownsBuilder = true; }
            }
            var rebuilt = await BuildIndexAsync(binding, resolution.Source, source, slot, module, token);
            if (artifact is not null)
            {
                var deterministic = false; await CpuAsync(() => deterministic = artifact.Sha256 == rebuilt.Sha256 &&
                    artifact.ByteLength == rebuilt.ByteLength && artifact.ContentKey == rebuilt.ContentKey && artifact.RowCount == rebuilt.RowCount &&
                    ItemEffectsBridgePolicy.MetadataBytes(artifact.Binding, artifact.RowCount).AsSpan().SequenceEqual(
                        ItemEffectsBridgePolicy.MetadataBytes(rebuilt.Binding, rebuilt.RowCount)), token);
                if (!deterministic) throw ItemEffectsBridgePolicy.Invalid();
            }
            await RecordBridgeAsync(rebuilt, token);
            return await OpenIndexAsync(artifact ?? rebuilt, module, slot, token);
        }
        finally
        {
            source.FirstPage = null;
            if (ownsBuilder) lock (gate) if (!slot.Debt) builder = false;
        }
    }
    private async Task<ItemEffectsBridgeArtifact> BuildIndexAsync(ItemEffectsBridgeBinding binding, SourceProbe probe, SourceHeld source,
        BridgeSlot slot, ItemEffectsBridgeArtifacts module, CancellationToken token)
    {
        DerivedArtifactStaging? staging = null;
        try
        {
            var reservation = stagingHooks?.Reserve is { } reserve ? await reserve(token) : await resources.ReserveStagingAsync(8 << 20, null, token);
            staging = new(reservation, stagingHooks?.Delete ?? File.Delete, "ITEM_EFFECTS_CLEANUP_FAILED");
            var stage = await staging.CreateAsync(stagingHooks?.Create ?? objects.CreateStagingFileAsync, token);
            var path = stage.Name;
            long nextResourceSample = 0;
            var result = await module.BuildAsync(binding, stage, async (after, limit, ct) =>
            {
                if (observeResources is not null && after >= nextResourceSample)
                { observeResources("bridge-build-page"); nextResourceSample = after + 4096; }
                if (after == 0 && source.FirstPage is { } first) { source.FirstPage = null; return first; }
                return await source.Rows.ReadPageAsync(probe.Admission!.Artifact, after, limit, null, ct);
            }, token);
            await stage.FlushAsync(token); await staging.CloseAsync();
            var sha = await objects.PublishFileAsync(path, result.ByteLength, resources, null, token);
            return new(result.ContentKey, sha, result.ByteLength, result.Binding, result.RowCount);
        }
        finally
        {
            if (staging is not null && !await staging.RetireAsync())
            {
                lock (gate) { slot.Debt = true; slot.StagingDebt = staging; slot.Retiring = true; }
                Debt(); throw Cleanup();
            }
        }
    }
    private async Task<IndexHeld> OpenIndexAsync(ItemEffectsBridgeArtifact artifact, ItemEffectsBridgeArtifacts module,
        BridgeSlot slot, CancellationToken token)
    {
        var input = await objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, resources, null, token);
        ItemEffectsBridgeArtifacts.Session session;
        try { session = await module.OpenAsync(artifact, input, token); }
        catch (DiscoveryException error) when (error.Code == "ITEM_EFFECTS_CLEANUP_FAILED")
        { lock (gate) { slot.Uncertain.Add(input); slot.Debt = true; } Debt(); throw; }
        return new(artifact, session);
    }
    private async Task CompleteIndexAsync(BridgeSlot slot, Task<IndexHeld> worker)
    {
        IndexHeld? result = null; Exception? failure = null;
        try { result = await worker; } catch (Exception error) { failure = error; }
        Task? notification;
        lock (gate) { slot.IndexProducerFinished = true; notification = slot.IndexNotification?.Task; }
        if (notification is not null) await notification;
        lock (gate)
        {
            slot.IndexFinished = true;
            if (failure is null) slot.IndexReady!.TrySetResult(result!);
            else { slot.Retiring = true; slot.IndexReady!.TrySetException(failure); }
        }
    }
    private Task DisposeSlotAsync(PhysicalSlot slot)
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (slot.Disposal is not null) return slot.Disposal;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); slot.Disposal = completion.Task; slot.Retiring = true;
        }
        _ = CloseAsync(); return completion.Task;
        async Task CloseAsync()
        {
            Exception? failure = null;
            var sourceNotification = CancelSlotAsync(slot, false);
            var indexNotification = slot is BridgeSlot b ? CancelSlotAsync(b, true) : Task.CompletedTask;
            await sourceNotification; await indexNotification;
            if (slot.SourceTask is { } sourceTask) try { await sourceTask; } catch (Exception error) { failure = error; }
            if (slot is BridgeSlot bridge && bridge.IndexTask is { } indexTask)
            {
                try { await indexTask; } catch (Exception error) { failure ??= error; }
                if (bridge.IndexReady!.Task.IsCompletedSuccessfully)
                    try { await (await bridge.IndexReady.Task).Session.DisposeAsync(); } catch (Exception error) { failure ??= error; }
                else if (bridge.IndexReady.Task.IsFaulted) _ = bridge.IndexReady.Task.Exception;
            }
            if (slot.SourceReady.Task.IsCompletedSuccessfully)
                try { await (await slot.SourceReady.Task).DisposeAsync(); } catch (Exception error) { failure ??= error; }
            else if (slot.SourceReady.Task.IsFaulted) _ = slot.SourceReady.Task.Exception;
            lock (gate)
            {
                if (slot.Debt || slot.Uncertain.Count != 0) failure ??= Cleanup();
                if (failure is null)
                { if (slot is BridgeSlot bs) bridges.Remove(bs); else targets.Remove((TargetSlot)slot); }
                else slot.Debt = cleanupDebt = true;
            }
            if (failure is null)
            { slot.Stop.Dispose(); if (slot is BridgeSlot bs) bs.IndexStop?.Dispose(); completion.TrySetResult(); }
            else completion.TrySetException(Cleanup());
        }
    }
    private async Task CloseSessionsAsync()
    {
        PhysicalSlot[] slots; lock (gate) { slots = bridges.Cast<PhysicalSlot>().Concat(targets).ToArray(); foreach (var slot in slots) slot.Retiring = true; }
        Exception? failure = null;
        var retirements = slots.Select(DisposeSlotAsync).ToArray();
        foreach (var retirement in retirements) try { await retirement; } catch (Exception error) { failure ??= error; }
        if (failure is not null) throw Cleanup();
    }
}
