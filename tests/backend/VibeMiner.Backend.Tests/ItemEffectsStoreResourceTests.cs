using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests;

[Collection(ManagedHeapBudgetCollection.Name)]
public sealed class ItemEffectsStoreResourceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactOpeningDropsOnlyDiscoveryProjectionAndRetainsOriginalAuthority(bool poisonConfiguration)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var original = await f.H.Store.AuthorizeItemEffectsAsync(f.Selection, default);
        // Authored successful-discovery projection shape, below the existing750000-byte
        // output ceiling. It is not a fabricated opening/source authority or new fixture CDN.
        var regions = Enumerable.Range(0, 1200).Select(index => "r" + (char)('a' + index / 676) +
            (char)('a' + index / 26 % 26) + (char)('a' + index % 26)).ToArray();
        var receipt = original.Opening.Build.SourceReceipts[0];
        var build = original.Opening.Build with
        {
            Regions = regions, AdvertisedRegions = regions, RouteRegions = regions,
            SourceReceipts = regions.Select(region => receipt with { Region = region }).ToArray()
        };
        original = original with { Opening = original.Opening with { Build = build } };
        var encoded = JsonSerializer.SerializeToUtf8Bytes(build, ItemEffectsBridgePolicy.Json).Length;
        Assert.InRange(encoded, 300000, 750000);
        var before = JsonSerializer.Serialize(original.Opening.Build, ItemEffectsBridgePolicy.Json);
        var compact = ItemEffectsStore.CompactItemsProof(original);
        Assert.Empty(compact.Opening.Build.IdentityFields); Assert.Empty(compact.Opening.Build.Regions);
        Assert.Empty(compact.Opening.Build.AdvertisedRegions); Assert.Empty(compact.Opening.Build.RouteRegions);
        Assert.Empty(compact.Opening.Build.SourceReceipts); Assert.Empty(compact.Opening.Build.CdnConfigHistory);
        Assert.Equal(original.Opening.Build.CdnConfig, compact.Opening.Build.CdnConfig);
        Assert.Same(original.Opening.Reference, compact.Opening.Reference);
        Assert.Same(original.Opening.Versions, compact.Opening.Versions); Assert.Same(original.Opening.Cdns, compact.Opening.Cdns);
        Assert.Same(original.Artifact, compact.Artifact); Assert.Same(original.Snapshot, compact.Snapshot);
        Assert.Same(original.Item, compact.Item); Assert.Same(original.Configurations, compact.Configurations);
        Assert.Equal(build with { IdentityFields = compact.Opening.Build.IdentityFields, Regions = compact.Opening.Build.Regions,
            AdvertisedRegions = compact.Opening.Build.AdvertisedRegions, RouteRegions = compact.Opening.Build.RouteRegions,
            SourceReceipts = compact.Opening.Build.SourceReceipts, CdnConfigHistory = compact.Opening.Build.CdnConfigHistory }, compact.Opening.Build);
        Assert.Equal(before, JsonSerializer.Serialize(original.Opening.Build, ItemEffectsBridgePolicy.Json));
        Assert.Empty(await f.H.Catalog.ReauthorizeItemEffectsAsync(compact, null, [], default));
        if (poisonConfiguration)
            await f.Sql("UPDATE configuration_objects SET retrieved_at='2026-10-18T00:00:00Z'");
        else
            compact = compact with { Opening = compact.Opening with { Reference = compact.Opening.Reference with { VersionsRowOrdinal = 1 } } };
        await Assert.ThrowsAsync<DiscoveryException>(() => f.H.Catalog.ReauthorizeItemEffectsAsync(compact, null, [], default));
        output.WriteLine("unusedDiscoveryProjectionEncodedBytes={0}; compact retains original source bodies/reference/configurations; poisonConfiguration={1}", encoded, poisonConfiguration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourOwnedProofCopiesCanQueueBehindOneCatalogWorkerWithoutNewPublicationAdmissions(bool leakFrames)
    {
        await using var f = await ItemEffectsStoreFixture.Create(coreColumns: 384);
        foreach (var role in Enum.GetValues<ItemEffectsSourceRole>())
        {
            var source = f.Desired[role]; var member = source.Association.Memberships.Single();
            source = source with { Association = source.Association with { Memberships = Enumerable.Range(0, role == ItemEffectsSourceRole.Bridge ? 160 : 256)
                .Select(index => member with { BlockOrdinal = index.ToString(System.Globalization.CultureInfo.InvariantCulture), EntryOrdinal = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray() } };
            await f.Publish(role, source);
        }
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default);
        var request = new ItemEffectNamesRequest(f.Selection, ready.Snapshot.ArtifactId,
            f.Desired[ItemEffectsSourceRole.Effect].Artifact.ContentKey, [page.Edges[0].Key]);
        await f.Store.DisposeAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var arrivals = 0; var queuedCopies = 0; var armed = false;
        // leakFrames: each copy parks a 20 MiB frame in state the store keeps alive (its callback).
        var kept = new List<byte[]>(4);
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, TimeProvider.System,
            observeResources: phase =>
            {
                if (!Volatile.Read(ref armed)) return;
                if (phase == "result-before-count")
                { if (Interlocked.Increment(ref arrivals) == 4) arrived.TrySetResult(); release.Wait(); }
                if (phase != "catalog-input-queued") return;
                if (leakFrames) lock (kept) kept.Add(new byte[20 << 20]);
                if (Interlocked.Increment(ref queuedCopies) == 4) queued.TrySetResult();
            });
        // Stands in for earlier work that is still live before the copies queue and is freed
        // while they wait. It is larger than what the copies hold, so a baseline delta would go negative.
        var earlier = new System.Runtime.CompilerServices.StrongBox<byte[]?>(new byte[16 << 20]);
        _ = await f.Store.ReadNamesAsync(request, default);
        using var admissionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (f.Store.Pressure.Operations != 0) { admissionDeadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        var armedHeap = ManagedHeap.Settled(); Volatile.Write(ref armed, true);
        var pending = Enumerable.Range(0, 4).Select(_ => f.Store.ReadNamesAsync(request, default)).ToArray();
        await using var blocker = new SqliteConnection("Data Source=" + Path.Combine(f.H.DirectoryPath, "discovery-v1.sqlite3"));
        await blocker.OpenAsync(); SqliteTransaction? transaction = null;
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4, f.Store.Pressure.Operations); Assert.Equal(4, f.Store.Pressure.Queries);
            Assert.Equal("ITEM_EFFECTS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.ReadAsync(f.Selection, default))).Code);
            // Every operation has captured/decoded its owned result before blocking the ONE SQL worker.
            transaction = blocker.BeginTransaction(); release.Set();
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            earlier.Value = null;
            // Charge the queued copies by what their completion releases. Growth from the armed
            // sample has no lower bound: earlier work freed in between (finalizers, pool trimming,
            // leftover tasks) made it read -3 to -9 MB on Linux CI (#36). Its ceiling still
            // catches a copy that leaves its frame in state the store keeps.
            var queuedHeap = ManagedHeap.Settled(); var bridgeSlots = f.Store.Pressure.BridgeSlots; var targetSlots = f.Store.Pressure.TargetSlots;
            transaction.Rollback(); transaction.Dispose(); transaction = null;
            await RequireReadyAsync(pending);
            Assert.Equal(4, queuedCopies);
            pending = [];
            using var retireDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (f.Store.Pressure.Operations != 0) { retireDeadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            var retiredHeap = ManagedHeap.Settled(); var released = queuedHeap - retiredHeap; var retained = retiredHeap - armedHeap;
            Assert.InRange(released, 0, 48L << 20);
            if (leakFrames) Assert.True(retained > 48L << 20, $"Parked frames must count as retained; retained={retained}.");
            else Assert.True(retained <= 48L << 20, $"Retired copies left {retained} bytes behind.");
            GC.KeepAlive(kept);
            output.WriteLine("fourQueuedProofCopies leakFrames={0}; releasedManagedLive={1}; retainedSinceArmed={2}; coreColumnsEach384; bridgeMembers160; effect/nameMembers256; heldBridgeSlots={3}; heldTargetSlots={4}. One SQL worker is blocked BEFORE current-graph decode; separate6MiB executing-phase reservation still required.", leakFrames, released, retained, bridgeSlots, targetSlots);
        }
        finally
        {
            release.Set(); transaction?.Rollback(); transaction?.Dispose();
            foreach (var task in pending) try { await task; } catch (DiscoveryException) { }
        }
    }

    // Its own state machine, so no hoisted local of the test keeps a finished task or result alive.
    private static async Task RequireReadyAsync(Task<ItemEffectNamesResult>[] pending)
    {
        foreach (var task in pending) Assert.Equal(ItemEffectNameState.Ready, Assert.Single((await task).Outcomes).State);
        Assert.All(pending, task => Assert.True(task.IsCompleted));
    }

    [Fact]
    public async Task EightMaximumEscapedNamesRemainBoundedWithoutWholeRejectedFrameAllocation()
    {
        await using var f = await ItemEffectsStoreFixture.Create(coreColumns: 384);
        var bridge = Enumerable.Range(0, 8).Select(id => new Db2Row((uint)(500 + id), false, null, [(500 + id).ToString(System.Globalization.CultureInfo.InvariantCulture), id.ToString(System.Globalization.CultureInfo.InvariantCulture), "1"])).ToArray();
        var effects = Enumerable.Range(0, 8).Select(id => new Db2Row((uint)id, false, null,
            [id.ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "0", "4294967295", "-1", "-2", "65536", id.ToString(System.Globalization.CultureInfo.InvariantCulture), "70000", "-1"])).ToArray();
        var names = Enumerable.Range(0, 8).Select(id => new Db2Row((uint)id, false, null,
            [id.ToString(System.Globalization.CultureInfo.InvariantCulture), new string('\u0001', 4096)])).ToArray();
        foreach (var role in Enum.GetValues<ItemEffectsSourceRole>())
        {
            var source = await f.Source(role, role == ItemEffectsSourceRole.Bridge ? bridge : role == ItemEffectsSourceRole.Effect ? effects : names);
            var member = source.Association.Memberships.Single();
            source = source with { Association = source.Association with { Memberships = Enumerable.Range(0, role == ItemEffectsSourceRole.Bridge ? 160 : 256)
                .Select(index => member with { BlockOrdinal = index.ToString(System.Globalization.CultureInfo.InvariantCulture), EntryOrdinal = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray() } };
            await f.Publish(role, source);
        }
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default);
        var effect = (await f.H.Catalog.ReadItemEffectsSourceAsync(f.H.Opening, ItemEffectsSourceRole.Effect, default))!;
        await f.Store.DisposeAsync();
        var baseline = GC.GetTotalMemory(true); var highwater = baseline;
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, TimeProvider.System,
            observeResources: _ => highwater = Math.Max(highwater, GC.GetTotalMemory(true)));
        var result = await f.Store.ReadNamesAsync(new(f.Selection, ready.Snapshot.ArtifactId, effect.Artifact.ContentKey,
            page.Edges.Select(edge => edge.Key).ToArray()), default);
        Assert.All(result.Outcomes, outcome => { Assert.Equal(ItemEffectNameState.Ready, outcome.State); Assert.Equal(4096, outcome.Text!.Length); });
        Assert.InRange(highwater - baseline, 0, 8L << 20);
        await ItemEffectsStoreResponseCounter.RequireAsync(result, default);
        await Assert.ThrowsAsync<DiscoveryException>(() => ItemEffectsStoreResponseCounter.RequireAsync(new { text = new string('\u0001', 100000) }, default));
        var saved = (await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default))!;
        _ = ItemEffectsBridgePolicy.SameAuthority(saved.Artifact, saved.Artifact);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(ItemEffectsBridgePolicy.SameAuthority(saved.Artifact, saved.Artifact));
        var policyAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.InRange(policyAllocated, 1, 6L << 20);
        var proof = await f.H.Store.AuthorizeItemEffectsAsync(f.Selection, default);
        var sourceBytes = result.Sources.Select(source => JsonSerializer.SerializeToUtf8Bytes(source.Snapshot, ItemEffectsBridgePolicy.Json).Length).ToArray();
        output.WriteLine("enlarged384columnCore artifact/snapshot encodedBytes={0}/{1}; bridgeDescriptorBytes={2}; effect/nameSnapshotBytes={3}/{4}; warmed128KiBBridgePolicy allocatedBytes={5} (allocation, not retained live).",
            JsonSerializer.SerializeToUtf8Bytes(proof.Artifact, ItemEffectsBridgePolicy.Json).Length,
            JsonSerializer.SerializeToUtf8Bytes(proof.Snapshot, ItemEffectsBridgePolicy.Json).Length,
            JsonSerializer.SerializeToUtf8Bytes(saved.Artifact, ItemEffectsBridgePolicy.Json).Length, sourceBytes[0], sourceBytes[1], policyAllocated);
        output.WriteLine("eightEscaped4096Names sampledManagedLiveDelta={0}; baseline={1}; existing 2target reader cap={2}. Forced-GC phase samples, not complete adversarial graph/RSS proof.", highwater - baseline, baseline, f.Store.Pressure.TargetSlots);
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(65537)]
    public async Task CompleteBridgeCeilingIsBoundedWithoutSilentEdgeTruncation(int count)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var rows = Enumerable.Range(0, count).Select(_ => new Db2Row(500, false, null, ["500", "0", "1"])).ToArray();
        await f.Publish(ItemEffectsSourceRole.Bridge, await f.Source(ItemEffectsSourceRole.Bridge, rows));
        rows = null!;
        await f.Store.DisposeAsync();
        var baseline = GC.GetTotalMemory(true); var highwater = baseline; var samples = 0;
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, TimeProvider.System,
            observeResources: _ => { highwater = Math.Max(highwater, GC.GetTotalMemory(true)); samples++; });
        var time = Stopwatch.StartNew();
        var result = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        if (count == 65536)
        {
            Assert.Equal(ItemEffectsReadinessState.Ready, result.State); Assert.Equal("65536", result.ObservedAssociations);
            var page = await f.Store.ReadPageAsync(new(f.Selection, result.Snapshot!.ArtifactId, 16), default);
            Assert.Equal(16, page.Edges.Count); Assert.NotNull(page.NextCursor);
            Assert.Equal("15", page.Edges[^1].Key.IndexOrdinal);
            var stored = (await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default))!;
            Assert.Equal(64L + ItemEffectsBridgePolicy.MetadataBytes(stored.Artifact.Binding, stored.Artifact.RowCount).Length + 65536L * 32, stored.Artifact.ByteLength);
            Assert.InRange(highwater - baseline, 0, 16L << 20);
        }
        else
        {
            Assert.Equal(ItemEffectsReadinessState.ResourceLimit, result.State); Assert.Null(result.Snapshot);
            Assert.Null(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default));
            Assert.Equal(0, f.Store.Pressure.BridgeSlots);
        }
        output.WriteLine("rows={0}; elapsedMs={1}; sampledManagedLiveDelta={2}; samples={3}; baseline={4}. Forced-GC checkpoint sample, excludes fixture setup and is not cumulative allocation/RSS or adversarial graph proof.", count, time.ElapsedMilliseconds, highwater - baseline, samples, baseline);
        Assert.Empty(f.Prepared); Assert.False(f.Store.Pressure.Builder);
    }
}
