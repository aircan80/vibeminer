using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemClassificationsStoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Progress(ItemClassificationProgress _) => Task.CompletedTask;
    private static async Task<ItemClassificationBatchRequest> Request(Harness h, bool subclass = false)
    { var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default); return new(h.Request, snapshot.ArtifactId, ["1", "2", "1"], subclass); }

    [Fact]
    public async Task ExactLabelsRetainNumericEvidenceOrderDuplicatesMissingItemsAndUnsupportedSubclassOffline()
    {
        await using var h = await Harness.Create(); var request = await Request(h, true);
        var batch = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(["1", "2", "1"], batch.Outcomes.Select(v => v.ItemId));
        Assert.Equal([true, false, true], batch.Outcomes.Select(v => v.Admitted));
        Assert.Equal(ItemClassificationState.Ready, batch.Outcomes[0].Class.State);
        Assert.Equal("Weapon", batch.Outcomes[0].Class.Text); Assert.Equal("99", batch.Outcomes[0].Class.Target!.RowId);
        Assert.Equal("2", Assert.Single(batch.Outcomes[0].Class.SourceKeys).Value);
        Assert.Equal(ItemClassificationState.MissingItem, batch.Outcomes[1].Class.State);
        Assert.Equal(ItemClassificationState.Unsupported, batch.Outcomes[0].Subclass!.State);
        Assert.Equal(ItemClassificationState.MissingItem, batch.Outcomes[1].Subclass!.State);
        Assert.Equal(ItemClassificationState.Ready, batch.Sources[0].State); Assert.Equal(ItemClassificationState.Unsupported, batch.Sources[1].State);
        Assert.Empty(h.PreparedTables);
        await h.Labels.DisposeAsync(); h.Labels = new(h.Catalog, h.Objects, h.Store, h.Tables, h.Resources);
        var reopened = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(batch.Sources[0].IndexArtifactId, reopened.Sources[0].IndexArtifactId);
        Assert.Equal("Weapon", reopened.Outcomes[0].Class.Text); Assert.Empty(h.PreparedTables);
    }
    [Fact]
    public async Task LabelsPrepareAndReopenWhenTheRootScopeSavedTheBuildsRootCopyFirst()
    {
        // #58: metadata_objects holds the root scope's metadata-blte-source-v1 copy; Items and ItemClass admitted root-v1.
        await using var h = await Harness.Create(admittedRootRevision: "root-v1"); var request = await Request(h);
        var batch = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(ItemClassificationState.Ready, batch.Sources[0].State); Assert.Equal("Weapon", batch.Outcomes[0].Class.Text);
        var saved = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!;
        Assert.Equal("root-v1", saved.Artifact.Binding.Source.Association.RootSource.ParserRevision);
        await h.Catalog.ReauthorizeItemClassificationTargetAsync(h.Opening, saved.Artifact, default);
        await h.Labels.DisposeAsync(); h.Labels = new(h.Catalog, h.Objects, h.Store, h.Tables, h.Resources);
        var reopened = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(batch.Sources[0].IndexArtifactId, reopened.Sources[0].IndexArtifactId); Assert.Equal("Weapon", reopened.Outcomes[0].Class.Text);
    }
    [Fact]
    public async Task ASavedRootReceiptForAnotherCopyIsAReceiptMismatchNotACorruptIndex()
    {
        await using var h = await Harness.Create(admittedRootRevision: "root-v1"); var request = await Request(h);
        await h.Labels.PrepareBatchAsync(request, Progress, default);
        var saved = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!;
        var root = saved.Artifact.Binding.Source.Association.RootSource;
        await h.Sql("UPDATE metadata_objects SET receipt=$v WHERE ekey='" + root.EncodingKey + "'",
            JsonSerializer.Serialize(root with { RetrievedAt = "2026-10-20T00:00:00Z" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeItemClassificationTargetAsync(h.Opening, saved.Artifact, default));
        Assert.Equal("ITEM_CLASSIFICATION_SOURCE_RECEIPT_MISMATCH", error.Code); Assert.Contains("ROOT copy", error.Message);
    }
    [Fact]
    public async Task PublicSubclassSourceKeepsDeclaredReferencesWithoutRewritingRetainedIndexes()
    {
        await using var h = await Harness.Create();
        var legacy = await Request(h);
        var originalClass = await h.Labels.PrepareBatchAsync(legacy, Progress, default);
        var classIndex = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!.Artifact;
        var request = legacy with { IncludeSubclass = true, PolicyRevision = ClassificationPolicy.CompositeRevision };
        var batch = await h.Labels.PrepareBatchAsync(request, Progress, default);
        var subIndex = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Subclass, default))!.Artifact;
        var stored = JsonSerializer.Serialize(subIndex);
        var source = Assert.Single(batch.Sources, source => source.Kind == ItemClassificationKind.Subclass).Snapshot!;
        var column = Assert.Single(source.Columns, column => column.Name == "ClassID");
        Assert.Equal(new TableReference("ItemClass", "ClassID"), column.Reference);
        // The existing immutable index descriptor is historical input, not a public table projection.
        Assert.Null(Assert.Single(subIndex.Binding.Source.Columns, column => column.Name == "ClassID").Reference);
        Assert.Equal(originalClass.Sources[0].IndexArtifactId, batch.Sources[0].IndexArtifactId);
        Assert.Equal(JsonSerializer.Serialize(classIndex), JsonSerializer.Serialize((await h.Catalog.ReadItemClassificationAdmissionAsync(
            h.Opening, "enUS", ItemClassificationKind.Class, default))!.Artifact));
        await h.Labels.DisposeAsync(); h.Labels = new(h.Catalog, h.Objects, h.Store, h.Tables, h.Resources);
        var reopened = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(Assert.Single(reopened.Sources, source => source.Kind == ItemClassificationKind.Subclass).Snapshot));
        Assert.Equal(subIndex.ContentKey, reopened.Sources[1].IndexArtifactId);
        Assert.Equal(stored, JsonSerializer.Serialize((await h.Catalog.ReadItemClassificationAdmissionAsync(
            h.Opening, "enUS", ItemClassificationKind.Subclass, default))!.Artifact));
    }

    [Fact]
    public async Task CanonicalPublicColumnProjectionSuppressesReferencesOnlyForRawEvidence()
    {
        var definition = (await TablesCatalog.Provider.Value).Definitions[ClassificationPolicy.SubclassTable].Document;
        var column = new Db2Column("ClassID", "integer", false);
        Assert.Equal(new TableReference("ItemClass", "ClassID"), TableDataStore.ProjectColumn(column, definition).Reference);
        var raw = column with { Interpretation = new("raw-physical-integer-v1", 32, false, 8, false, "Authored evidence") };
        var projected = TableDataStore.ProjectColumn(raw, definition);
        Assert.Null(projected.Reference); Assert.Equal(raw.Interpretation!.Policy, projected.Interpretation!.Policy);
        Assert.Equal(raw.Interpretation.PhysicalBits, projected.Interpretation.PhysicalBits);
    }

    [Fact]
    public async Task AutomaticClassPrerequisiteUsesSharedTablesAndReauthorizesItemsBeforeIndexPublication()
    {
        await using var h = await Harness.Create(true); var request = await Request(h);
        h.BeforePrepare = async (_, _) => await h.ReplaceItem();
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Labels.PrepareBatchAsync(request, Progress, default))).Code);
        Assert.Equal([ClassificationPolicy.ClassTable], h.PreparedTables);
        Assert.Null(await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default));
    }
    [Fact]
    public async Task SharedPrerequisiteBusyWaitPreservesIndependentTableConsumerAndCancellation()
    {
        await using var h = await Harness.Create(true); var request = await Request(h);
        var entered = Signal(); var release = Signal();
        h.BeforePrepare = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var independent = h.Tables.PrepareAsync(new("wow", h.Request.BuildIdentity, "us", ItemsProjection.ItemTable, "enUS"));
        await entered.Task.WaitAsync(Deadline); var waiting = Signal(); using var stop = new CancellationTokenSource();
        var batch = h.Labels.PrepareBatchAsync(request, p => { if (p.Stage.Contains("Waiting", StringComparison.Ordinal)) waiting.TrySetResult(); return Task.CompletedTask; }, stop.Token);
        await waiting.Task.WaitAsync(Deadline); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
        Assert.False(independent.IsCompleted); release.SetResult(); await independent.WaitAsync(Deadline);
        h.BeforePrepare = null; Assert.Equal("Weapon", (await h.Labels.PrepareBatchAsync(request, Progress, default)).Outcomes[0].Class.Text);
    }
    [Fact]
    public async Task DirectStoreShutdownCancelsAndAwaitsActualPrerequisiteCleanup()
    {
        await using var h = await Harness.Create(true); var request = await Request(h);
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var never = Signal();
        h.BeforePrepare = async (_, token) => { entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupStarted.SetResult(); await cleanup.Task; } };
        var accepted = h.Labels.PrepareBatchAsync(request, Progress, default); await entered.Task.WaitAsync(Deadline);
        var stopping = h.Labels.DisposeAsync().AsTask(); await cleanupStarted.Task.WaitAsync(Deadline);
        Assert.False(stopping.IsCompleted); Assert.False(accepted.IsCompleted);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Labels.PrepareBatchAsync(request, Progress, default))).Code);
        cleanup.SetResult(); await stopping.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted);
    }
    [Fact]
    public async Task CachedTargetReplacementRecoversWithoutRepreparingCoreItemsAndOldCommitIsRejected()
    {
        await using var h = await Harness.Create(); var request = await Request(h);
        var original = await h.Labels.PrepareBatchAsync(request, Progress, default);
        var saved = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!;
        await h.ReplaceClass(); var snapshot = await h.Store.ReadAsync(h.Request, default);
        Assert.Equal("ITEM_CLASSIFICATION_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitItemClassificationAdmissionAsync(
            h.Opening, snapshot.Snapshot!, saved.Artifact, Harness.Time, default))).Code);
        Assert.Equal("ITEM_CLASSIFICATION_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.ReauthorizeItemClassificationTargetAsync(h.Opening, saved.Artifact, default))).Code);
        var replaced = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal("Replacement", replaced.Outcomes[0].Class.Text); Assert.NotEqual(original.Sources[0].IndexArtifactId, replaced.Sources[0].IndexArtifactId);
        Assert.Equal(request.ArtifactId, replaced.ArtifactId);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HonestMissingOrRawPhysicalTargetProfilesAreUnsupportedAndKeepNumericItem(bool missing)
    {
        await using var h = await Harness.Create(); var request = await Request(h, true);
        h.Class = await h.ClassSource(h.Fixture.PayloadB, "Unused", !missing, missing); await h.Publish(h.Class);
        var result = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(ItemClassificationState.Unsupported, result.Sources[0].State);
        Assert.Equal(ItemClassificationState.Unsupported, result.Outcomes[0].Class.State);
        Assert.Equal("2", Assert.Single(result.Outcomes[0].Class.SourceKeys).Value);
        Assert.Equal(ItemClassificationState.Unsupported, result.Outcomes[0].Subclass!.State);
    }
    [Fact]
    public async Task OriginalRetrievalForgeryIsRejectedDespiteIdenticalDerivedKeyAndVerifiedTimeMayChange()
    {
        await using var h = await Harness.Create(); var request = await Request(h);
        await h.Labels.PrepareBatchAsync(request, Progress, default);
        var saved = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!;
        var binding = saved.Artifact.Binding;
        var bad = saved.Artifact with { Binding = binding with { Configurations = binding.Configurations.Select(c => c with { RetrievedAt = "2026-10-20T00:00:00Z" }).ToArray() } };
        Assert.Equal(saved.Artifact.ContentKey, ClassificationPolicy.ContentKey(bad.Binding));
        var snapshot = (await h.Store.ReadAsync(h.Request, default)).Snapshot!;
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitItemClassificationAdmissionAsync(h.Opening, snapshot, bad, Harness.Time, default));
        var sourceBad = saved.Artifact with { Binding = binding with { Source = binding.Source with { Source = binding.Source.Source with { RetrievedAt = "2026-10-20T00:00:00Z" } } } };
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitItemClassificationAdmissionAsync(h.Opening, snapshot, sourceBad, Harness.Time, default));
        var verified = saved.Artifact with { Binding = binding with { Source = binding.Source with { Source = binding.Source.Source with { VerifiedAt = "2026-10-20T00:00:00Z" } } } };
        await h.Catalog.CommitItemClassificationAdmissionAsync(h.Opening, snapshot, verified, Harness.Time, default);
        Assert.Equal(saved.Artifact.Binding.Source.Source.RetrievedAt,
            (await h.Catalog.FindItemClassificationArtifactAsync(saved.Artifact.ContentKey, default))!.Binding.Source.Source.RetrievedAt);
    }
    [Fact]
    public async Task ShortCommitChecksExactCurrentItemsSourceAssociationBeyondParsedRowArtifactIdentity()
    {
        await using var h = await Harness.Create(); var request = await Request(h);
        await h.Labels.PrepareBatchAsync(request, Progress, default);
        var saved = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!;
        var snapshot = (await h.Store.ReadAsync(h.Request, default)).Snapshot!;
        await h.PoisonItemAssociation(snapshot.ItemSource.Association with
            { EncodingSource = snapshot.ItemSource.Association.EncodingSource with { RetrievedAt = "2026-10-20T00:00:00Z" } });
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitItemClassificationAdmissionAsync(
            h.Opening, snapshot, saved.Artifact, Harness.Time, default))).Code);
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"binding\":null}")] [InlineData("null")]
    public async Task MalformedBoundedDescriptorReturnsTypedIntegrityError(string json)
    {
        await using var h = await Harness.Create(); var request = await Request(h); var batch = await h.Labels.PrepareBatchAsync(request, Progress, default);
        await h.PoisonDescriptor(batch.Sources[0].IndexArtifactId!, json);
        Assert.Equal("ITEM_CLASSIFICATION_INDEX_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.FindItemClassificationArtifactAsync(batch.Sources[0].IndexArtifactId!, default))).Code);
    }
    [Fact]
    public async Task EnRootRetrievalForgeryAndTargetChangeDuringIndexingCannotAcquireAuthority()
    {
        await using var h = await Harness.Create(); var request = await Request(h);
        await h.Labels.PrepareBatchAsync(request, Progress, default);
        var saved = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!;
        foreach (var encoding in new[] { true, false })
        {
            var association = saved.Artifact.Binding.Source.Association;
            var forged = encoding ? association with { EncodingSource = association.EncodingSource with { RetrievedAt = "2026-10-20T00:00:00Z" } }
                : association with { RootSource = association.RootSource with { RetrievedAt = "2026-10-20T00:00:00Z" } };
            var artifact = saved.Artifact with { Binding = saved.Artifact.Binding with { Source = saved.Artifact.Binding.Source with { Association = forged } } };
            Assert.Equal(saved.Artifact.ContentKey, ClassificationPolicy.ContentKey(artifact.Binding));
            await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeItemClassificationTargetAsync(h.Opening, artifact, default));
        }
        await h.Labels.DisposeAsync(); h.Labels = new(h.Catalog, h.Objects, h.Store, h.Tables, h.Resources);
        await h.ReplaceClass("Before indexing"); var changed = false;
        Assert.Equal("ITEM_CLASSIFICATION_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Labels.PrepareBatchAsync(request, async p =>
        { if (!changed && p.Unit == "rows") { changed = true; h.Class = await h.ClassSource(h.Fixture.PayloadA, "Weapon"); await h.Publish(h.Class); } }, default))).Code);
        Assert.True(changed);
    }
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public async Task ColdMissingOrCorruptOriginalAndIndexNeverClaimsLabels(int kind, bool corrupt)
    {
        await using var h = await Harness.Create(); var request = await Request(h);
        await h.Labels.PrepareBatchAsync(request, Progress, default);
        var saved = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!;
        await h.Labels.DisposeAsync();
        var sha = kind == 0 ? h.Class.Source.Sha256 : kind == 1 ? h.Class.Rows.Sha256 : saved.Artifact.Sha256;
        var length = kind == 0 ? long.Parse(h.Class.Source.EncodedBytes, CultureInfo.InvariantCulture) : kind == 1 ? h.Class.Rows.ByteLength : saved.Artifact.ByteLength;
        string path; await using (var file = await h.Objects.OpenVerifiedAsync(sha, length, default)) path = file.Name;
        if (corrupt) { var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes); }
        else File.Delete(path);
        h.Labels = new(h.Catalog, h.Objects, h.Store, h.Tables, h.Resources);
        var result = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(corrupt ? ItemClassificationState.Corrupt : ItemClassificationState.Unavailable, result.Outcomes[0].Class.State);
        Assert.Equal("2", Assert.Single(result.Outcomes[0].Class.SourceKeys).Value); Assert.Null(result.Outcomes[0].Class.Text);
    }
    [Fact]
    public async Task ExplicitV2PreparesBothTargetsAndPreservesDefaultUnsupportedAndClassArtifact()
    {
        await using var h = await Harness.Create(subMissing: true); var legacy = await Request(h, true);
        var first = await h.Labels.PrepareBatchAsync(legacy, Progress, default);
        Assert.Equal(ItemClassificationState.Unsupported, first.Outcomes[0].Subclass!.State); Assert.Empty(h.PreparedTables);
        var modern = legacy with { PolicyRevision = ClassificationPolicy.CompositeRevision };
        var next = await h.Labels.PrepareBatchAsync(modern, Progress, default);
        Assert.Equal("Subclass", next.Outcomes[0].Subclass!.Text); Assert.Equal("99", next.Outcomes[0].Subclass!.Target!.RowId);
        Assert.Equal(2, next.Outcomes[0].Subclass!.SourceKeys.Count);
        Assert.Equal(first.Sources[0].IndexArtifactId, next.Sources[0].IndexArtifactId);
        Assert.Equal([ClassificationPolicy.SubclassTable], h.PreparedTables); Assert.Equal(2, h.Labels.MaximumHeldTargets);
        Assert.Equal(ItemClassificationState.MissingItem, next.Outcomes[1].Subclass!.State);
        await h.Labels.DisposeAsync(); h.Labels = new(h.Catalog, h.Objects, h.Store, h.Tables, h.Resources);
        Assert.Equal("Subclass", (await h.Labels.PrepareBatchAsync(modern, Progress, default)).Outcomes[0].Subclass!.Text);
        Assert.Equal(2, h.Labels.MaximumHeldTargets);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task V2KindFailureDoesNotPreventOtherKindSuccess(bool subclassFails)
    {
        await using var h = await Harness.Create(); var request = (await Request(h, true)) with { PolicyRevision = ClassificationPolicy.CompositeRevision };
        var bad = await h.ClassSource(subclassFails ? h.Fixture.PayloadA : h.Fixture.PayloadB, "Bad", raw: true, subclass: subclassFails);
        if (subclassFails) h.Subclass = bad; else h.Class = bad;
        await h.Publish(bad);
        var result = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(subclassFails ? ItemClassificationState.Ready : ItemClassificationState.Unsupported, result.Outcomes[0].Class.State);
        Assert.Equal(subclassFails ? ItemClassificationState.Unsupported : ItemClassificationState.Ready, result.Outcomes[0].Subclass!.State);
        Assert.InRange(h.Labels.MaximumHeldTargets, 1, 2);
    }
    [Fact]
    public async Task ClassReplacementDuringSubclassBuildInvalidatesOnlyClassAndRetryRecovers()
    {
        await using var h = await Harness.Create(); var request = (await Request(h, true)) with { PolicyRevision = ClassificationPolicy.CompositeRevision };
        var changed = false;
        var result = await h.Labels.PrepareBatchAsync(request, async p =>
        {
            if (!changed && p.TableId == ClassificationPolicy.SubclassTable && p.Unit == "rows") { changed = true; await h.ReplaceClass(); }
        }, default);
        Assert.True(changed); Assert.Equal(ItemClassificationState.Unavailable, result.Outcomes[0].Class.State);
        Assert.Null(result.Outcomes[0].Class.Target); Assert.Equal("ITEM_CLASSIFICATION_STALE", result.Outcomes[0].Class.Problem!.Code);
        Assert.Equal(ItemClassificationState.Ready, result.Outcomes[0].Subclass!.State);
        Assert.Equal(ItemClassificationState.MissingItem, result.Outcomes[1].Class.State);
        var recovered = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal("Replacement", recovered.Outcomes[0].Class.Text); Assert.Equal("Subclass", recovered.Outcomes[0].Subclass!.Text);
        Assert.Equal(2, h.Labels.MaximumHeldTargets);
    }
    [Fact]
    public async Task V2ShutdownAcrossSecondPrerequisiteWaitRetiresOperation()
    {
        await using var h = await Harness.Create(missing: true, subMissing: true); var request = (await Request(h, true)) with { PolicyRevision = ClassificationPolicy.CompositeRevision };
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var never = Signal();
        h.BeforePrepare = async (table, token) =>
        {
            if (table.TableId != ClassificationPolicy.SubclassTable) return;
            entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupStarted.SetResult(); await cleanup.Task; }
        };
        var accepted = h.Labels.PrepareBatchAsync(request, Progress, default); await entered.Task.WaitAsync(Deadline);
        var stopping = h.Labels.DisposeAsync().AsTask(); await cleanupStarted.Task.WaitAsync(Deadline);
        Assert.False(stopping.IsCompleted); Assert.False(accepted.IsCompleted); Assert.Equal(0, h.Labels.MaximumHeldTargets);
        cleanup.SetResult(); await stopping.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted);
        Assert.Equal([ClassificationPolicy.ClassTable, ClassificationPolicy.SubclassTable], h.PreparedTables);
    }
    [Fact]
    public async Task TwoKindCacheEvictsBeforeOpeningReplacementContextsAndNeverExceedsTwoTargets()
    {
        await using var h = await Harness.Create();
        for (var i = 0; i < 3; i++)
        {
            if (i != 0) await h.AddContext(70448 + i);
            var request = (await Request(h, true)) with { PolicyRevision = ClassificationPolicy.CompositeRevision };
            var batch = await h.Labels.PrepareBatchAsync(request, Progress, default);
            Assert.Equal(ItemClassificationState.Ready, batch.Outcomes[0].Class.State);
            Assert.Equal(ItemClassificationState.Ready, batch.Outcomes[0].Subclass!.State);
            Assert.Equal(2, h.Labels.MaximumHeldTargets);
        }
    }
    [Fact]
    public async Task OptionalSubclassPrerequisiteIoFailurePreservesClassAndRetry()
    {
        await using var h = await Harness.Create(subMissing: true); var request = (await Request(h, true)) with { PolicyRevision = ClassificationPolicy.CompositeRevision };
        h.BeforePrepare = (_, _) => Task.FromException(new IOException("Authored optional adapter failure"));
        var failed = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(ItemClassificationState.Ready, failed.Outcomes[0].Class.State);
        Assert.Equal(ItemClassificationState.Unavailable, failed.Outcomes[0].Subclass!.State);
        Assert.Equal("ITEM_CLASSIFICATION_IO", failed.Outcomes[0].Subclass!.Problem!.Code);
        h.BeforePrepare = null; Assert.Equal("Subclass", (await h.Labels.PrepareBatchAsync(request, Progress, default)).Outcomes[0].Subclass!.Text);
    }
    [Fact]
    public async Task CoherentFinalFenceReturnsOnlyBadTargetAndFencesItemsFirst()
    {
        await using var h = await Harness.Create(); var request = (await Request(h, true)) with { PolicyRevision = ClassificationPolicy.CompositeRevision };
        await h.Labels.PrepareBatchAsync(request, Progress, default);
        var cls = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Class, default))!.Artifact;
        var sub = (await h.Catalog.ReadItemClassificationAdmissionAsync(h.Opening, "enUS", ItemClassificationKind.Subclass, default))!.Artifact;
        var snapshot = (await h.Store.ReadAsync(h.Request, default)).Snapshot!;
        await h.ReplaceClass();
        var problems = await h.Catalog.ReauthorizeItemClassificationBatchAsync(h.Opening, snapshot, [cls, sub], default);
        Assert.Equal("ITEM_CLASSIFICATION_STALE", problems[0]!.Code); Assert.Null(problems[1]);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeItemClassificationBatchAsync(h.Opening, snapshot, [sub, sub], default));
        await h.PoisonItemAssociation(snapshot.ItemSource.Association with
            { RootSource = snapshot.ItemSource.Association.RootSource with { RetrievedAt = "2026-10-20T00:00:00Z" } });
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeItemClassificationBatchAsync(
            h.Opening, snapshot, [cls, sub], default))).Code);
    }
    [Fact]
    public async Task CoherentFinalFenceRejectsReplacedDerivedKindAssociationAndRetryRepublishes()
    {
        await using var h = await Harness.Create(); var request = (await Request(h, true)) with { PolicyRevision = ClassificationPolicy.CompositeRevision };
        var first = await h.Labels.PrepareBatchAsync(request, Progress, default);
        await h.PoisonClassificationAssociation(first.Sources[0].IndexArtifactId!);
        var rejected = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal(ItemClassificationState.Ready, rejected.Outcomes[0].Class.State);
        Assert.Equal(ItemClassificationState.Unavailable, rejected.Outcomes[0].Subclass!.State);
        Assert.Null(rejected.Outcomes[0].Subclass!.Text); Assert.Null(rejected.Outcomes[0].Subclass!.Target); Assert.Null(rejected.Outcomes[0].Subclass!.ObservedCandidates);
        var retry = await h.Labels.PrepareBatchAsync(request, Progress, default);
        Assert.Equal("Subclass", retry.Outcomes[0].Subclass!.Text); Assert.Equal(2, h.Labels.MaximumHeldTargets);
    }
    private sealed record SourceFixture(TableRowsArtifact Rows, ContentReceipt Source, SelectedFileAssociation Association, TableDataSnapshot Snapshot);
    private sealed class Harness : IAsyncDisposable
    {
        internal const string Time = "2026-10-19T00:00:00Z";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-classifications-store-" + Guid.NewGuid().ToString("N"));
        internal readonly InventoryFixture Fixture = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal CapturedOpeningContext Opening = null!;
        internal ItemsContext Request = null!;
        internal SourceFixture Item = null!, Sparse = null!, Class = null!, Subclass = null!;
        internal ItemClassificationsStore Labels = null!;
        internal bool ClassMissing, SubclassMissing;
        internal readonly AcquisitionResources Resources = new();
        internal TableDataService Tables = null!;
        internal ItemsStore Store = null!;

        internal readonly List<string> PreparedTables = [];
        internal Func<TableDataRequest, CancellationToken, Task>? BeforePrepare;
        internal string AdmittedRootRevision = MetadataAcquirer.SourceRevision;
        internal static async Task<Harness> Create(bool missing = false, bool subMissing = false, string admittedRootRevision = MetadataAcquirer.SourceRevision)
        {
            var h = new Harness { AdmittedRootRevision = admittedRootRevision }; h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory); h.Objects = new(h.directory);
            var versions = Fixtures.Versions($"us|{h.Fixture.BuildKey}|{h.Fixture.CdnKey}||70448|12.1.5.70448||items");
            var cdns = Encoding.UTF8.GetString(h.Fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
            var discovery = await Fixtures.Attempt(versions, cdns); var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            var page = await h.Catalog.CommitAsync("wow", generation, discovery, Time, default);
            h.Request = new("wow", page.Builds.Single().Id, "us", "enUS");
            h.Opening = await h.Catalog.ResolveOpeningAsync(new("wow", h.Request.BuildIdentity, "us"), default);
            var configurations = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", h.Fixture.BuildKey, h.Fixture.BuildConfig), ("cdnConfig", h.Fixture.CdnKey, h.Fixture.CdnConfig) })
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); await h.Objects.PublishAsync(bytes, sha, default);
                await h.Catalog.RecordConfigurationAsync(new(key, sha, bytes.Length, InventoryFixture.Url("config", key), Time), default);
                configurations.Add(new(role, key, sha, bytes.Length, InventoryFixture.Url("config", key), Time, Time, 1));
            }
            await h.Catalog.CommitWorkspaceAsync(h.Opening, configurations, Time, default);
            foreach (var role in new[] { "encoding", "root" }) await h.Catalog.RecordMetadataAsync(h.Metadata(role), default);
            h.Item = await h.Source(ItemsProjection.ItemTable, h.Fixture.PayloadA);
            h.Sparse = await h.Source(ItemsProjection.SparseTable, h.Fixture.PayloadB);
            await h.Publish(h.Item); await h.Publish(h.Sparse);
            h.Class = await h.ClassSource(h.Fixture.PayloadA, "Weapon"); h.ClassMissing = missing;
            if (!missing) await h.Publish(h.Class);
            h.Subclass = await h.ClassSource(h.Fixture.PayloadB, "Subclass", subclass: true); h.SubclassMissing = subMissing;
            if (!subMissing) await h.Publish(h.Subclass);
            h.Tables = new(new ReadyTables(h)); h.Store = new(h.Catalog, h.Objects, h.Tables, h.Resources);
            h.Labels = new(h.Catalog, h.Objects, h.Store, h.Tables, h.Resources); return h;
        }
        private MetadataReceipt Metadata(string role)
        {
            var source = Fixture.Metadata[role]; return new(role, source.ContentKey, source.EncodingKey,
                Convert.ToHexStringLower(SHA256.HashData(source.Encoded)), N(source.Encoded.Length), N(source.Decoded.Length),
                InventoryFixture.Url("data", source.EncodingKey), Time, Time, MetadataAcquirer.SourceRevision);
        }
        private MetadataReceipt AdmittedRoot() => Metadata("root") with { ParserRevision = AdmittedRootRevision };
        internal async Task<SourceFixture> Source(string table, InventoryFixture.FixtureObject bytes, string? decoder = null)
        {
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[table];
            var source = new ContentReceipt(bytes.ContentKey, bytes.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(bytes.Encoded)),
                N(bytes.Encoded.Length), N(bytes.Decoded.Length), InventoryFixture.Url("data", bytes.EncodingKey), Time, Time, "content-blte-v1", new("loose", "0", N(bytes.Encoded.Length), null));
            await Catalog.RecordContentAsync(source, default);
            var entry = provider.Entries.Single(e => e.TableId == table);
            var association = new SelectedFileAssociation(entry.Db2FileDataId!, "enUS", "enus-consistent-root-variants-v1", Metadata("encoding"), AdmittedRoot(),
                [new("0", "0", source.ContentKey, "2", "0", "0", "0", "0", null)]);
            var binding = new TableArtifactBinding(source.Sha256, new('d', 64), definition.Sha256, BundledTableDefinitions.Revision, "enUS",
                decoder ?? TableDataStore.DecoderRevision, TableRowArtifacts.SchemaSelectionKey(definition.Document, Opening.Build.Version));
            var sparse = table == ItemsProjection.SparseTable;
            Db2Column[] columns = sparse ? [new("ID", "integer", true), new("Display_lang", "string", false), new("OverallQualityID", "integer", false)]
                : [new("ID", "integer", true), new("ClassID", "integer", false), new("SubclassID", "integer", false), new("IconFileDataID", "integer", false)];
            await using var rows = new TableRowArtifacts(Objects); await using var writer = await rows.CreateWriterAsync(binding, default);
            var header = new Wdc5TableHeader(1, table.ToUpperInvariant(), sparse ? "1C17D17F" : "996192AA", 1, 0, 4, columns);
            await writer.HeaderAsync(header, default); await writer.RowAsync(new(1, false, null, sparse ? ["1", "Original", "4"] : ["1", "2", "1", "132759"]), default);
            var completion = new Wdc5Completion(1, 1, 0, 0, 0, 0, "complete", bytes.Decoded.Length);
            var artifact = await writer.CompleteAsync(completion, default);
            var snapshot = new TableDataSnapshot(Request.ProductId, Request.BuildIdentity, Request.SourceRegion, table, "enUS", artifact.ContentKey,
                Time, header.LayoutHash, definition.Sha256, binding.ProviderRevision, binding.DecoderRevision, source, association,
                columns.Select(c => new TableDataColumn(c.Name, c.Kind, c.IsId, null)).ToArray(),
                new("complete", "1", "1", "0", "0", "0", "0", "0", "0"), "1");
            return new(artifact, source, association, snapshot);
        }
        internal async Task Publish(SourceFixture source)
        {
            await Catalog.RecordTableArtifactAsync(source.Rows, default);
            await Catalog.CommitTableAdmissionAsync(Opening, source.Snapshot.TableId, "enUS", source.Source, source.Association, source.Rows, Time, default);
        }
        internal async Task ReplaceItem()
        { Item = await Source(ItemsProjection.ItemTable, Fixture.PayloadB); await Publish(Item); }
        internal async Task<SourceFixture> ClassSource(InventoryFixture.FixtureObject bytes, string text, bool raw = false, bool missing = false, bool subclass = false)
        {
            var table = subclass ? ClassificationPolicy.SubclassTable : ClassificationPolicy.ClassTable;
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[table];
            var source = new ContentReceipt(bytes.ContentKey, bytes.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(bytes.Encoded)),
                N(bytes.Encoded.Length), N(bytes.Decoded.Length), InventoryFixture.Url("data", bytes.EncodingKey), Time, Time, "content-blte-v1", new("loose", "0", N(bytes.Encoded.Length), null));
            string path; await using (var file = await Objects.CreateStagingFileAsync(default)) { path = file.Name; await file.WriteAsync(bytes.Encoded); }
            await Objects.PublishFileAsync(path, bytes.Encoded.Length, default);
            await Catalog.RecordContentAsync(source, default);
            var association = new SelectedFileAssociation(subclass ? ClassificationPolicy.SubclassFile : ClassificationPolicy.ClassFile, "enUS", "enus-consistent-root-variants-v1", Metadata("encoding"), AdmittedRoot(),
                [new("0", "0", source.ContentKey, "2", "0", "0", "0", "0", null)]);
            var binding = new TableArtifactBinding(source.Sha256, new('d', 64), definition.Sha256, BundledTableDefinitions.Revision, "enUS",
                TableDataStore.DecoderRevision, TableRowArtifacts.SchemaSelectionKey(definition.Document, Opening.Build.Version));
            var selected = Assert.Single(definition.Document.SelectBuild(Opening.Build.Version).Blocks);
            var columns = selected.Fields.SelectMany(f => Enumerable.Range(0, f.ArrayLength).Select(i => new Db2Column(f.ArrayLength == 1 ? f.Column.Name : f.Column.Name + "[" + i + "]",
                f.Column.Family == "int" ? "integer" : f.Column.Family == "float" ? "float" : "string", f.IsId))).ToArray();
            var values = columns.Select(c => c.Kind == "string" ? "" : "0").ToArray();
            var idColumn = Array.FindIndex(columns, c => c.IsId); var keyColumn = Array.FindIndex(columns, c => c.Name == "ClassID");
            values[idColumn] = "99"; values[keyColumn] = "2"; values[Array.FindIndex(columns, c => c.Name == (subclass ? "DisplayName_lang" : "ClassName_lang"))] = text;
            if (subclass) values[Array.FindIndex(columns, c => c.Name == "SubClassID")] = "1";
            if (raw) columns[keyColumn] = columns[keyColumn] with { Interpretation = new("authored-raw", 32, false, 8, false, "Authored raw physical field") };
            if (missing) columns[keyColumn] = columns[keyColumn] with { Name = "UnknownClass" };
            await using var rows = new TableRowArtifacts(Objects); await using var writer = await rows.CreateWriterAsync(binding, default);
            var header = new Wdc5TableHeader(1, table.ToUpperInvariant(), selected.LayoutHashes.First(), 1, 0, 4, columns);
            await writer.HeaderAsync(header, default); await writer.RowAsync(new(99, false, null, values), default);
            var artifact = await writer.CompleteAsync(new(1, 1, 0, 0, 0, 0, "complete", bytes.Decoded.Length), default);
            var snapshot = new TableDataSnapshot(Request.ProductId, Request.BuildIdentity, Request.SourceRegion, table, "enUS", artifact.ContentKey,
                Time, header.LayoutHash, definition.Sha256, binding.ProviderRevision, binding.DecoderRevision, source, association,
                columns.Select(c => new TableDataColumn(c.Name, c.Kind, c.IsId, null, c.Interpretation is { } i ? new(i.Policy, i.PhysicalBits, i.PhysicalSigned, i.CandidateBits, i.CandidateUnsigned, i.Warning) : null)).ToArray(),
                new("complete", "1", "1", "0", "0", "0", "0", "0", "0"), "1");
            return new(artifact, source, association, snapshot);
        }
        internal async Task ReplaceClass(string text = "Replacement")
        { Class = await ClassSource(Fixture.PayloadB, text); await Publish(Class); }
        private sealed class ReadyTables(Harness h) : ITableDataStore
        {
            private TableDataSnapshot Snapshot(string table) => table == ItemsProjection.ItemTable ? h.Item.Snapshot : table == ItemsProjection.SparseTable ? h.Sparse.Snapshot : table == ClassificationPolicy.ClassTable ? h.Class.Snapshot : h.Subclass.Snapshot;
            public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token) => Task.FromResult(new TableDataReadiness(Snapshot(request.TableId)));
            public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
            {
                h.PreparedTables.Add(request.TableId);
                if (h.BeforePrepare is { } before) await before(request, token);
                if (request.TableId == ClassificationPolicy.ClassTable && h.ClassMissing) { await h.Publish(h.Class); h.ClassMissing = false; }
                if (request.TableId == ClassificationPolicy.SubclassTable && h.SubclassMissing) { await h.Publish(h.Subclass); h.SubclassMissing = false; }
                return Snapshot(request.TableId);
            }
            public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("No per-Item table queries permitted.");
        }
        internal async Task AddContext(int buildId)
        {
            var configurations = await Catalog.ReadItemClassificationConfigurationsAsync(Opening, default);
            var versions = Fixtures.Versions($"us|{Fixture.BuildKey}|{Fixture.CdnKey}||{buildId}|12.1.5.70448||items");
            var cdns = Encoding.UTF8.GetString(Fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
            var discovery = await Fixtures.Attempt(versions, cdns); var generation = await Catalog.BeginRefreshAsync("wow", default);
            var page = await Catalog.CommitAsync("wow", generation, discovery, Time, default);
            var build = page.Builds.Single(b => b.BuildId == N(buildId)); Request = new("wow", build.Id, "us", "enUS");
            Opening = await Catalog.ResolveOpeningAsync(new("wow", Request.BuildIdentity, "us"), default);
            await Catalog.CommitWorkspaceAsync(Opening, configurations, Time, default);
            Item = await Source(ItemsProjection.ItemTable, Fixture.PayloadA); Sparse = await Source(ItemsProjection.SparseTable, Fixture.PayloadB);
            Class = await ClassSource(Fixture.PayloadA, "Weapon"); Subclass = await ClassSource(Fixture.PayloadB, "Subclass", subclass: true);
            await Publish(Item); await Publish(Sparse); await Publish(Class); await Publish(Subclass);
        }
        internal async Task PoisonClassificationAssociation(string key)
        {
            await using var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "discovery-v1.sqlite3"));
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE item_classification_admissions SET content_key=$k WHERE kind='Subclass'";
            command.Parameters.AddWithValue("$k", key); await command.ExecuteNonQueryAsync();
        }
        internal async Task PoisonItemAssociation(SelectedFileAssociation association)
        {
            await using var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "discovery-v1.sqlite3"));
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE table_admissions SET association=$a WHERE table_id=$t";
            command.Parameters.AddWithValue("$a", JsonSerializer.Serialize(association, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("$t", ItemsProjection.ItemTable); await command.ExecuteNonQueryAsync();
        }
        internal async Task PoisonDescriptor(string key, string json)
        {
            await using var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "discovery-v1.sqlite3"));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE item_classification_artifacts SET descriptor=$d WHERE content_key=$k";
            command.Parameters.AddWithValue("$d", json); command.Parameters.AddWithValue("$k", key); await command.ExecuteNonQueryAsync();
        }
        internal async Task Sql(string sql, string value)
        {
            await using var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "discovery-v1.sqlite3"));
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = sql; command.Parameters.AddWithValue("$v", value); await command.ExecuteNonQueryAsync();
        }
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        public async ValueTask DisposeAsync()
        { if (Labels is not null) await Labels.DisposeAsync(); if (Tables is not null) await Tables.DisposeAsync(); if (Store is not null) await Store.DisposeAsync(); if (Objects is not null) await Objects.DisposeAsync(); if (Catalog is not null) await Catalog.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
