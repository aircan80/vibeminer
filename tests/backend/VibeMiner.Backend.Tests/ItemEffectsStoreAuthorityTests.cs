using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsStoreAuthorityTests
{
    [Fact]
    public async Task HistoricalUpgradeCannotSubstituteDifferentOriginalSourceAfterWait()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var old = await f.Source(ItemEffectsSourceRole.Bridge, f.Rows[ItemEffectsSourceRole.Bridge], "wdc5-values-v6+table-admission-v1",
            changeBinding: binding => binding with { DefinitionSha256 = new string('f', 64), ProviderRevision = "authored-historical-provider" });
        await f.Publish(ItemEffectsSourceRole.Bridge, old);
        f.Desired[ItemEffectsSourceRole.Bridge] = await f.Source(ItemEffectsSourceRole.Bridge, f.Rows[ItemEffectsSourceRole.Bridge], alternate: true);
        File.Delete(await f.ObjectPath(old.Artifact.Sha256, old.Artifact.ByteLength));
        Assert.Equal("ITEM_EFFECTS_BRIDGE_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default))).Code);
        Assert.Null(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default));
        Assert.Equal(new[] { "00cb674f" }, f.Prepared.ToArray());
    }

    [Fact]
    public async Task MissingIndexRebuildCannotBlessDifferentCanonicalRegisteredOutput()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        _ = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var saved = (await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default))!;
        var poison = saved.Artifact with { Sha256 = new string('f', 64) };
        await f.Sql("UPDATE item_effects_bridge_artifacts SET descriptor=$v", JsonSerializer.Serialize(poison, ItemEffectsBridgePolicy.Json));
        await f.Reopen();
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default))).Code);
        Assert.Equal(new string('f', 64), (await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default))!.Artifact.Sha256);
        Assert.False(f.Store.Pressure.Builder); Assert.Empty(f.Prepared);
    }

    [Fact]
    public async Task AdmittedUnsignedItemOutsideSignedParentDomainNeverProbesOptionalSources()
    {
        await using var f = await ItemEffectsStoreFixture.Create(twoItems: true, extraItem: uint.MaxValue);
        await f.Sql("UPDATE table_admissions SET source=$v WHERE table_id='00cb674f'", new string('x', 131073));
        var selection = f.Selection with { ItemId = "4294967295" };
        var read = await f.Store.ReadAsync(selection, default);
        Assert.True(read.Admitted); Assert.Equal(ItemEffectsReadinessState.ItemKeyOutOfRange, read.State); Assert.Null(read.Source);
        Assert.Equal(ItemEffectsReadinessState.ItemKeyOutOfRange, (await f.Store.PrepareLinksAsync(selection, _ => Task.CompletedTask, default)).State);
        Assert.Equal("ITEM_EFFECTS_ITEM_KEY_OUT_OF_RANGE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            f.Store.ReadDetailsAsync(new(selection, new string('a', 64), [new("0")]), default))).Code);
        Assert.Empty(f.Prepared); Assert.Equal(0, f.Store.Pressure.BridgeSlots);
    }

    [Fact]
    public async Task GenuinelyMissingEffectAfterNameWaitRetainsConsultedNameMetadataWithoutInventingStaleness()
    {
        await using var f = await ItemEffectsStoreFixture.Create(name: false);
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        var original = f.Desired[ItemEffectsSourceRole.Effect];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforePrepare = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var request = new ItemEffectNamesRequest(f.Selection, links.Snapshot.ArtifactId, original.Artifact.ContentKey, [page.Edges[0].Key]);
        var pending = f.Store.PrepareNamesAsync(request, _ => Task.CompletedTask, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, f.Store.Pressure.TargetPins); Assert.Equal(0, f.Store.Pressure.Queries);
            // Two legitimate newer readers force the idle old generation out of the global2pool.
            foreach (var variant in new[] { "second", "third" })
            {
                await f.Publish(ItemEffectsSourceRole.Effect, await f.Source(ItemEffectsSourceRole.Effect, f.Rows[ItemEffectsSourceRole.Effect], variant: variant));
                _ = await f.Store.ReadDetailsAsync(new(f.Selection, links.Snapshot.ArtifactId, [page.Edges[0].Key]), default);
                Assert.InRange(f.Store.Pressure.TargetSlots, 1, 2);
            }
            await f.Publish(ItemEffectsSourceRole.Effect, original);
            File.Delete(await f.ObjectPath(original.Artifact.Sha256, original.Artifact.ByteLength));
            release.TrySetResult(); var result = await pending;
            Assert.Equal(ItemEffectsSourceState.Unavailable, result.Sources[0].State);
            Assert.Equal(ItemEffectsSourceState.Ready, result.Sources[1].State);
            var outcome = Assert.Single(result.Outcomes); Assert.Equal(ItemEffectNameState.EffectNotReady, outcome.State);
            Assert.Null(outcome.Effect.SpellId); Assert.Null(outcome.Effect.Target); Assert.Null(outcome.ObservedCandidates);
            Assert.Null(outcome.Target); Assert.Null(outcome.Text); Assert.Null(outcome.Absence); Assert.Null(outcome.Issue);
            Assert.Equal(new[] { "46c66698" }, f.Prepared.ToArray());
        }
        finally { release.TrySetResult(); try { await pending; } catch (DiscoveryException) { } }
    }

    [Fact]
    public async Task ForeignParentOrdinalFailsBeforeAnyPoisonedEffectAuthorityRead()
    {
        await using var f = await ItemEffectsStoreFixture.Create(twoItems: true);
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        await f.Sql("UPDATE table_admissions SET source=$v WHERE table_id='4002a5b1'", new string('x', 131073));
        var request = new ItemEffectDetailsRequest(f.Selection with { ItemId = "2" }, links.Snapshot.ArtifactId, [page.Edges[0].Key]);
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.PrepareDetailsAsync(request, _ => Task.CompletedTask, default))).Code);
        Assert.Empty(f.Prepared); Assert.Equal(0, f.Store.Pressure.TargetSlots);
    }

    [Fact]
    public async Task BridgeReplacementDuringNameWaitIsFatalAndNeverRetargetsTheOriginalSelection()
    {
        await using var f = await ItemEffectsStoreFixture.Create(name: false);
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforePrepare = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var pending = f.Store.PrepareNamesAsync(new(f.Selection, links.Snapshot.ArtifactId,
            f.Desired[ItemEffectsSourceRole.Effect].Artifact.ContentKey, [page.Edges[0].Key]), _ => Task.CompletedTask, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Publish(ItemEffectsSourceRole.Bridge, await f.Source(ItemEffectsSourceRole.Bridge, f.Rows[ItemEffectsSourceRole.Bridge], variant: "replacement"));
            release.TrySetResult();
            Assert.Equal("ITEM_EFFECTS_BRIDGE_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => pending)).Code);
        }
        finally { release.TrySetResult(); try { await pending; } catch (DiscoveryException) { } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WarmPhysicalSlotCannotAuthenticateAChangedSqlHeaderByKeyAlone(bool bridgeRole)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        var request = new ItemEffectDetailsRequest(f.Selection, links.Snapshot.ArtifactId, [page.Edges[0].Key]);
        if (!bridgeRole) _ = await f.Store.ReadDetailsAsync(request, default);
        var role = bridgeRole ? ItemEffectsSourceRole.Bridge : ItemEffectsSourceRole.Effect;
        var rows = f.Desired[role].Artifact;
        var columns = rows.Header.Columns.ToArray(); columns[1] = columns[1] with { Kind = "string" };
        var poison = rows with { Header = rows.Header with { Columns = columns } };
        await f.Sql("UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='" + ItemEffectsStoreFixture.Table(role) + "')",
            JsonSerializer.Serialize(poison, ItemEffectsBridgePolicy.Json));
        if (bridgeRole) Assert.Equal(ItemEffectsReadinessState.Corrupt, (await f.Store.ReadAsync(f.Selection, default)).State);
        else Assert.Equal(ItemEffectsSourceState.Corrupt, (await f.Store.ReadDetailsAsync(request, default)).Source.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NameWaitFencesEveryConsultedTargetAndPrioritizesOriginalItems(bool staleItems)
    {
        await using var f = await ItemEffectsStoreFixture.Create(name: false);
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforePrepare = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var request = new ItemEffectNamesRequest(f.Selection, links.Snapshot.ArtifactId,
            f.Desired[ItemEffectsSourceRole.Effect].Artifact.ContentKey, page.Edges.Select(edge => edge.Key).ToArray());
        var pending = f.Store.PrepareNamesAsync(request, _ => Task.CompletedTask, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, f.Store.Pressure.Queries); Assert.Equal(0, f.Store.Pressure.BridgePins); Assert.Equal(0, f.Store.Pressure.TargetPins);
            await f.Publish(ItemEffectsSourceRole.Effect, f.Desired[ItemEffectsSourceRole.Effect] with { PreparedAt = "2026-04-05T06:07:08.0000000+00:00" });
            if (staleItems) await f.Sql("UPDATE items_admissions SET prepared_at=$v", "2026-04-05T06:07:08.0000000+00:00");
            release.TrySetResult();
            if (staleItems)
            {
                Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => pending)).Code);
            }
            else
            {
                var result = await pending;
                Assert.Equal(ItemEffectsSourceState.Stale, result.Sources[0].State);
                Assert.Equal(ItemEffectsSourceState.Ready, result.Sources[1].State);
                Assert.All(result.Outcomes, outcome => { Assert.Equal(ItemEffectNameState.EffectNotReady, outcome.State); Assert.Null(outcome.Effect.SpellId); Assert.Null(outcome.Text); Assert.Null(outcome.Target); Assert.Null(outcome.ObservedCandidates); });
                Assert.Equal(new[] { "46c66698" }, f.Prepared.ToArray());
                var retry = await f.Store.ReadNamesAsync(request, default);
                Assert.All(retry.Sources, source => Assert.Equal(ItemEffectsSourceState.Ready, source.State));
                Assert.Contains(retry.Outcomes, outcome => outcome.State == ItemEffectNameState.Ready);
            }
        }
        finally { release.TrySetResult(); try { await pending; } catch (DiscoveryException) { } }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PartialTargetCountsBeforeDecodeAndNeverInfersGlobalAbsence(int candidates)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        var key = page.Edges.First(edge => edge.ItemEffectId.Value == "0").Key;
        var rows = Enumerable.Repeat(f.Rows[ItemEffectsSourceRole.Effect][0], candidates).ToArray();
        await f.Publish(ItemEffectsSourceRole.Effect, await f.Source(ItemEffectsSourceRole.Effect, rows, partial: true));
        var result = await f.Store.ReadDetailsAsync(new(f.Selection, links.Snapshot.ArtifactId, [key]), default);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(candidates == 0 ? ItemEffectState.NotFoundInAdmittedScope : candidates == 1 ? ItemEffectState.ObservedReady : ItemEffectState.Ambiguous, outcome.State);
        Assert.Equal(candidates.ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.ObservedCandidates);
        if (candidates == 0) Assert.Equal(ItemEffectsAbsence.UnknownOutsideReadableScope, outcome.Absence);
        if (candidates != 1) { Assert.Null(outcome.Fields); Assert.Null(outcome.Target); }
        Assert.Equal("unencryptedSections", result.Source.Snapshot!.Coverage.Scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentUnsupportedStillAuthenticatesPhysicalRowsBeforePublishingItsState(bool corrupt)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var unsupported = await f.Source(ItemEffectsSourceRole.Bridge, f.Rows[ItemEffectsSourceRole.Bridge],
            variant: "unsupported", changeColumns: columns => columns[1] = columns[1] with { Kind = "string" });
        await f.Publish(ItemEffectsSourceRole.Bridge, unsupported);
        if (corrupt)
        {
            var path = await f.ObjectPath(unsupported.Artifact.Sha256, unsupported.Artifact.ByteLength);
            var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        }
        var result = await f.Store.ReadAsync(f.Selection, default);
        Assert.Equal(corrupt ? ItemEffectsReadinessState.Corrupt : ItemEffectsReadinessState.Unsupported, result.State);
        Assert.Null(result.Snapshot); Assert.Empty(f.Prepared);
        Assert.InRange(f.Store.Pressure.BridgeSlots, 0, 1);
    }

    [Fact]
    public async Task InitialUnavailableEffectNeverEvenReadsPoisonedNameAuthority()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        var effect = f.Desired[ItemEffectsSourceRole.Effect];
        File.Delete(await f.ObjectPath(effect.Artifact.Sha256, effect.Artifact.ByteLength));
        await f.Sql("UPDATE table_admissions SET source=$v WHERE table_id='46c66698'", new string('x', 131073));
        var request = new ItemEffectNamesRequest(f.Selection, links.Snapshot.ArtifactId, effect.Artifact.ContentKey, page.Edges.Select(edge => edge.Key).ToArray());
        var result = await f.Store.PrepareNamesAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsSourceState.Unavailable, result.Sources[0].State);
        Assert.Equal(ItemEffectsSourceState.NotChecked, result.Sources[1].State);
        Assert.All(result.Outcomes, outcome => { Assert.Equal(ItemEffectNameState.EffectNotReady, outcome.State); Assert.Null(outcome.Effect.SpellId); Assert.Null(outcome.Text); });
        Assert.Empty(f.Prepared);
    }

    [Fact]
    public async Task NegativeLinksDoNotConsultEitherPoisonedTarget()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
        var key = Assert.Single(page.Edges, edge => edge.ItemEffectId.Value == "-1").Key;
        await f.Sql("UPDATE table_admissions SET source=$v WHERE table_id IN ('4002a5b1','46c66698')", new string('x', 131073));
        var details = await f.Store.PrepareDetailsAsync(new(f.Selection, links.Snapshot.ArtifactId, [key]), _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsSourceState.NotChecked, details.Source.State);
        Assert.Equal(ItemEffectState.EffectKeyOutOfRange, Assert.Single(details.Outcomes).State);
        var names = await f.Store.PrepareNamesAsync(new(f.Selection, links.Snapshot.ArtifactId,
            f.Desired[ItemEffectsSourceRole.Effect].Artifact.ContentKey, [key]), _ => Task.CompletedTask, default);
        Assert.All(names.Sources, source => Assert.Equal(ItemEffectsSourceState.NotChecked, source.State));
        Assert.Empty(f.Prepared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDerivedIndexCanRebuildButCorruptCanonicalBytesRemainExplicit(bool corrupt)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var first = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var saved = (await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default))!;
        var path = await f.ObjectPath(saved.Artifact.Sha256, saved.Artifact.ByteLength);
        await f.Reopen();
        if (corrupt)
        {
            var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        }
        else File.Delete(path);
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.ReadAsync(f.Selection, default))).Code);
        Assert.Empty(f.Prepared);
        var repaired = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        if (corrupt)
        { Assert.Equal(ItemEffectsReadinessState.Corrupt, repaired.State); Assert.Null(repaired.Snapshot); }
        else Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repaired));
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default)));
        Assert.Empty(f.Prepared);
    }

    [Fact]
    public async Task SameBytesReadmissionUsesFreshSourceGenerationAndPreservesFirstIndexProvenance()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var first = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var saved = await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default);
        var fresh = f.Desired[ItemEffectsSourceRole.Bridge] with { PreparedAt = "2026-04-05T06:07:08.0000000+00:00" };
        await f.Publish(ItemEffectsSourceRole.Bridge, fresh);
        var read = await f.Store.ReadAsync(f.Selection, default);
        Assert.Equal(ItemEffectsReadinessState.Ready, read.State);
        Assert.Equal(first.Snapshot!.ArtifactId, read.Snapshot!.ArtifactId);
        Assert.Equal(first.Snapshot.PreparedAt, read.Snapshot.PreparedAt);
        Assert.Equal(fresh.PreparedAt, read.Source!.Snapshot!.PreparedAt);
        var again = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(JsonSerializer.Serialize(read), JsonSerializer.Serialize(again)); Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalLocalReadDoesNotOpenOldDerivedRowsAndExplicitPrepareUpgradesThem(bool oldDefinitionProvider)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var old = await f.Source(ItemEffectsSourceRole.Bridge, f.Rows[ItemEffectsSourceRole.Bridge], "wdc5-values-v6+table-admission-v1",
            changeBinding: binding => oldDefinitionProvider ? binding with { DefinitionSha256 = new string('f', 64), ProviderRevision = "authored-historical-provider" } : binding);
        await f.Publish(ItemEffectsSourceRole.Bridge, old);
        File.Delete(await f.ObjectPath(old.Artifact.Sha256, old.Artifact.ByteLength));
        var local = await f.Store.ReadAsync(f.Selection, default);
        Assert.Equal(ItemEffectsReadinessState.NotPrepared, local.State);
        Assert.Equal(old.Artifact.ContentKey, local.Source!.HistoricalArtifactId);
        Assert.Equal(0, f.Store.Pressure.BridgeSlots); Assert.Empty(f.Prepared);
        var prepared = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsReadinessState.Ready, prepared.State);
        Assert.Equal(new[] { "00cb674f" }, f.Prepared.ToArray());
    }
}
