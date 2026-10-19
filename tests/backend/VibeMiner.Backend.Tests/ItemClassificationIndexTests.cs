using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests;

[Collection("Classification index builder")]
public sealed class ItemClassificationIndexTests(ITestOutputHelper output)
{
    private static Task Progress(ItemClassificationProgress _) => Task.CompletedTask;
    private static readonly ItemSourceValue[] Keys = [new(ItemsProjection.ItemTable, 1, "2")];
    private static Db2Row Row(uint id, int key, string text, uint? copy = null, params Db2TextIssue[] issues) =>
        new(id, copy is not null, copy, [N(id), N(key), text, ""], issues);
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task SemanticClassIdMatchesIndependentlyOfRecordIdAndPreservesSignedKeysAndCopies()
    {
        await using var f = new Fixture();
        var a = await f.Index.BuildAsync(await f.Binding([Row(99, 2, "Weapon"), Row(100, -1, "Negative"), Row(uint.MaxValue, 0, "Zero", 99)]), Progress, default);
        await using var session = await f.Index.OpenAsync(a, default);
        var value = await session.ResolveAsync(2, Keys, default);
        Assert.Equal(ItemClassificationState.Ready, value.State); Assert.Equal("Weapon", value.Text); Assert.Equal("99", value.Target!.RowId);
        Assert.Equal(Keys, value.SourceKeys); Assert.Equal("Negative", (await session.ResolveAsync(-1, Keys, default)).Text);
        var copy = await session.ResolveAsync(0, Keys, default); Assert.Equal("4294967295", copy.Target!.RowId);
        Assert.True(copy.Target.IsCopy); Assert.Equal("99", copy.Target.CopySourceId);
        Assert.Equal(ItemClassificationState.NotFound, (await session.ResolveAsync(99, Keys, default)).State);
        Assert.Equal(ItemClassificationState.KeyOutOfRange, (await session.ResolveAsync(255, Keys, default)).State);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task EveryEmittedDuplicateIncludingCopiesIsAmbiguousEvenWithEqualText(bool partial, bool sameText)
    {
        await using var f = new Fixture();
        var a = await f.Index.BuildAsync(await f.Binding([Row(1, 2, "First"), Row(2, 2, sameText ? "First" : "Other", 1)], partial), Progress, default);
        await using var s = await f.Index.OpenAsync(a, default);
        var value = await s.ResolveAsync(2, Keys, default);
        Assert.Equal(ItemClassificationState.Ambiguous, value.State); Assert.Equal("2", value.ObservedCandidates);
        Assert.Null(value.Text); Assert.Null(value.Target);
    }
    [Fact]
    public async Task PartialUniqueOrMissingNeverClaimsCanonicalLabelOrAbsence()
    {
        await using var f = new Fixture(); var a = await f.Index.BuildAsync(await f.Binding([Row(99, 2, "Observed")], true), Progress, default);
        await using var s = await f.Index.OpenAsync(a, default);
        foreach (var key in new[] { 2, 3 })
        {
            var value = await s.ResolveAsync(key, Keys, default);
            Assert.Equal(ItemClassificationState.IncompleteTarget, value.State); Assert.Equal(key == 2 ? "1" : "0", value.ObservedCandidates);
            Assert.Null(value.Text); Assert.Null(value.Target);
        }
    }
    [Fact]
    public async Task UniqueEmptyWhitespaceInvalidAndOversizedTextPreservesExactNavigableTarget()
    {
        await using var f = new Fixture();
        var a = await f.Index.BuildAsync(await f.Binding([Row(1, 1, ""), Row(2, 2, "  "),
            Row(3, 3, "", null, new Db2TextIssue(2, TableTextEvidence.InvalidUtf8, "ff")), Row(4, 4, new('x', 4097)),
            Row(5, 5, "Selected", null, new Db2TextIssue(3, TableTextEvidence.InvalidUtf8, "ff"))]), Progress, default);
        await using var s = await f.Index.OpenAsync(a, default);
        var values = new List<ItemClassificationValue>();
        for (var key = 1; key <= 5; key++) values.Add(await s.ResolveAsync(key, Keys, default));
        Assert.Equal([ItemClassificationState.EmptyText, ItemClassificationState.Ready, ItemClassificationState.InvalidText,
            ItemClassificationState.Unsupported, ItemClassificationState.Ready], values.Select(v => v.State));
        Assert.Equal(["", "  ", null, null, "Selected"], values.Select(v => v.Text));
        Assert.All(values, v => { Assert.NotNull(v.Target); Assert.Equal(2, v.Target!.ColumnIndex); Assert.Equal("1", v.ObservedCandidates); });
        Assert.Equal("ITEM_CLASSIFICATION_TEXT_LIMIT", values[3].Problem!.Code);
    }
    [Fact]
    public async Task ObservationTimeChangesProduceIdenticalLogicalKeyAndActualCasBytesButDoNotMatchAuthority()
    {
        await using var f = new Fixture(); var binding = await f.Binding([Row(1, 2, "Name")]);
        var changed = binding with
        {
            Configurations = binding.Configurations.Select(c => c with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-21T00:00:00Z" }).ToArray(),
            Source = binding.Source with { PreparedAt = "2026-10-22T00:00:00Z", Source = binding.Source.Source with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-21T00:00:00Z" },
                Association = binding.Source.Association with { EncodingSource = binding.Source.Association.EncodingSource with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-21T00:00:00Z" } } }
        };
        var a = await f.Index.BuildAsync(binding, Progress, default); var b = await f.Index.BuildAsync(changed, Progress, default);
        Assert.Equal(a.ContentKey, b.ContentKey); Assert.Equal(a.Sha256, b.Sha256); Assert.Equal(a.ByteLength, b.ByteLength);
        await using var left = await f.Objects.OpenVerifiedAsync(a.Sha256, a.ByteLength, default);
        await using var right = await f.Objects.OpenVerifiedAsync(b.Sha256, b.ByteLength, default);
        var bytesA = new byte[a.ByteLength]; var bytesB = new byte[b.ByteLength]; await left.ReadExactlyAsync(bytesA); await right.ReadExactlyAsync(bytesB);
        Assert.Equal(bytesA, bytesB); Assert.False(ClassificationPolicy.SameAuthority(binding.Source.Source, changed.Source.Source));
        Assert.True(ClassificationPolicy.SameAuthority(binding.Source.Source, binding.Source.Source with { VerifiedAt = "2026-10-20T00:00:00Z" }));
    }
    [Fact]
    public async Task DuplicateRecordIdsAndNonCanonicalSemanticKeysCannotPublishAnIndex()
    {
        await using var f = new Fixture();
        foreach (var rows in new[] { new[] { Row(1, 2, "A"), Row(1, 3, "B") }, new[] { Row(1, 128, "A") },
            new[] { Row(1, -129, "A") }, new[] { Row(1, 2, "A") with { Values = ["1", "02", "A", ""] } } })
            Assert.Equal("ITEM_CLASSIFICATION_INDEX_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(async () =>
                await f.Index.BuildAsync(await f.Binding(rows), Progress, default))).Code);
        Assert.Empty(f.StagingFiles);
    }
    [Fact]
    public async Task SourceContextSchemaAndIncompleteCoverageClaimsCannotBecomeReady()
    {
        await using var f = new Fixture(); var b = await f.Binding([Row(1, 2, "A")]);
        var bad = new[] { b with { Context = b.Context with { SourceRegion = "eu" } },
            b with { Source = b.Source with { ArtifactId = new('0', 64) } },
            b with { Source = b.Source with { Coverage = b.Source.Coverage with { ExcludedCopyRecords = "1" } } },
            b with { KeyFields = [b.KeyFields[0] with { IntegerBits = 32 }] },
            b with { KeyFields = [b.KeyFields[0] with { Unsigned = true }] },
            b with { TextField = b.TextField with { VerifiedName = false } },
            b with { Source = b.Source with { Association = b.Source.Association with { FileDataId = "1" } } } };
        foreach (var value in bad) await Assert.ThrowsAsync<DiscoveryException>(() => f.Index.BuildAsync(value, Progress, default));
        Assert.Empty(f.StagingFiles);
    }
    [Fact]
    public async Task CancellationRetiresBuilderAndAllStagingBeforeRetry()
    {
        await using var f = new Fixture(); var b = await f.Binding(Enumerable.Range(0, 100).Select(i => Row((uint)i, i, "A")).ToArray());
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Index.BuildAsync(b, _ => { stop.Cancel(); return Task.CompletedTask; }, stop.Token));
        Assert.Empty(f.StagingFiles);
        var a = await f.Index.BuildAsync(b, Progress, default); Assert.Equal("100", a.RowCount); Assert.Empty(f.StagingFiles);
    }
    [Fact]
    public void CompleteEscapedResponseLimitRefusesWholeBatchWithoutTruncation()
    {
        var context = new ItemsContext("wow", new('a', 64), "us", "enUS");
        ItemClassificationBatch Batch(int length) => new(context, new('b', 64), ClassificationPolicy.Revision, [],
            Enumerable.Range(0, 50).Select(_ => new ItemClassificationOutcome("4294967295", true,
                new(ItemClassificationState.Ready, Keys, new string('\u0800', length), new(ClassificationPolicy.ClassTable, "99", 2, false, null), "1"))).ToArray());
        ItemClassificationsStore.RequireResponseBudget(Batch(512));
        Assert.Equal("ITEM_CLASSIFICATION_RESPONSE_LIMIT", Assert.Throws<DiscoveryException>(() => ItemClassificationsStore.RequireResponseBudget(Batch(4096))).Code);
    }
    [Fact]
    public async Task MaximalSigned8LabelsAndNearMetadataLimitStayWithinSampledLiveScratchBudget()
    {
        await using var f = new Fixture();
        var binding = await f.Binding(Enumerable.Range(0, 256).Select(i => Row((uint)i, i - 128, new string('\u0800', 4096))).ToArray());
        var baseline = GC.GetTotalMemory(true); long peak = baseline;
        var artifact = await f.Index.BuildAsync(binding, _ => { peak = Math.Max(peak, GC.GetTotalMemory(true)); return Task.CompletedTask; }, default);
        output.WriteLine($"Forced-GC managed live sample delta={peak - baseline:N0} bytes; artifact={artifact.ByteLength:N0}; 256 unique signed8 groups×4096Unicode units. This is sampled live memory, not cumulative allocation or process peak.");
        Assert.InRange(peak - baseline, 0, ClassificationPolicy.MaxScratchBytes);
        Assert.True(artifact.ByteLength < ClassificationPolicy.MaxArtifactBytes);
        var small = await f.Binding([Row(1, 2, "Name")]);
        var length = ClassificationPolicy.MaxLiveDescriptorBytes - ClassificationPolicy.NormalizedBytes(small).Length - 2048;
        var near = small with { Source = small.Source with { Source = small.Source.Source with { SourceUrl = new string('x', length) } } };
        await f.Index.BuildAsync(near, Progress, default);
        var over = small with { Source = small.Source with { Source = small.Source.Source with { SourceUrl = new string('x', ClassificationPolicy.MaxLiveDescriptorBytes) } } };
        Assert.Equal("ITEM_CLASSIFICATION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Index.BuildAsync(over, Progress, default))).Code);
        Assert.Empty(f.StagingFiles);
    }
    [Fact]
    public async Task LegitimateSourceCeilingIsTypedLimitRatherThanCorruption()
    {
        await using var f = new Fixture(); var b = await f.Binding([Row(1, 2, "Name")]);
        b = b with { Rows = b.Rows with { Completion = b.Rows.Completion with { PhysicalRows = 65_537, DeclaredRecords = 65_537 } } };
        Assert.Equal("ITEM_CLASSIFICATION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Index.BuildAsync(b, Progress, default))).Code);
    }
    private static Db2Row Sub(uint id, int cls, int sub, string text, uint? copy = null, params Db2TextIssue[] issues) =>
        new(id, copy is not null, copy, [N(id), N(cls), N(sub), text, ""], issues);
    private static int Pair(int cls, int sub) => ((cls + 128) << 8) | (sub + 128);
    [Fact]
    public async Task CompositeKeysDistinguishClassesAndPreserveLegacyClassBytes()
    {
        await using var f = new Fixture(); var oldBinding = await f.Binding([Row(99, 2, "Class")]);
        var before = await f.Index.BuildAsync(oldBinding, Progress, default);
        var composite = new CompositeClassificationIndex(f.Objects, new());
        var artifact = await composite.BuildAsync(await f.Binding([Sub(10, 2, 1, "A"), Sub(11, 4, 1, "B"), Sub(12, -1, -1, "Negative"), Sub(13, 0, 0, "Zero", 10)], subclass: true), Progress, default);
        await using var session = await composite.OpenAsync(artifact, default);
        Assert.Equal("A", (await session.ResolveAsync(Pair(2, 1), Keys, default)).Text);
        Assert.Equal("B", (await session.ResolveAsync(Pair(4, 1), Keys, default)).Text);
        Assert.Equal("Negative", (await session.ResolveAsync(Pair(-1, -1), Keys, default)).Text);
        var copy = await session.ResolveAsync(Pair(0, 0), Keys, default); Assert.Equal("13", copy.Target!.RowId); Assert.Equal("10", copy.Target.CopySourceId);
        Assert.Equal(ItemClassificationState.NotFound, (await session.ResolveAsync(Pair(10, 1), Keys, default)).State);
        Assert.Equal(ClassificationPolicy.CompositeRevision, artifact.Revision);
        var after = await f.Index.BuildAsync(oldBinding, Progress, default);
        Assert.Equal(before.ContentKey, after.ContentKey); Assert.Equal(before.Sha256, after.Sha256); Assert.Equal(before.ByteLength, after.ByteLength);
        await using var legacy = await f.Index.OpenAsync(before, default); Assert.Equal("Class", (await legacy.ResolveAsync(2, Keys, default)).Text);
        await Assert.ThrowsAsync<DiscoveryException>(() => f.Index.OpenAsync(artifact, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => composite.OpenAsync(before, default));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompositeDuplicateCopyPartialAndSelectedTextRules(bool partial)
    {
        await using var f = new Fixture(); var index = new CompositeClassificationIndex(f.Objects, new());
        var artifact = await index.BuildAsync(await f.Binding([Sub(1, 2, 1, "Same"), Sub(2, 2, 1, "Same", 1), Sub(3, 4, 1, ""),
            Sub(4, 4, 2, "", null, new Db2TextIssue(3, TableTextEvidence.InvalidUtf8, "ff")), Sub(5, 4, 3, new('x', 4097)),
            Sub(6, 4, 4, "  ", null, new Db2TextIssue(4, TableTextEvidence.InvalidUtf8, "ff"))], partial, true), Progress, default);
        await using var session = await index.OpenAsync(artifact, default);
        var duplicate = await session.ResolveAsync(Pair(2, 1), Keys, default); Assert.Equal(ItemClassificationState.Ambiguous, duplicate.State); Assert.Equal("2", duplicate.ObservedCandidates);
        for (var i = 1; i <= 4; i++)
        {
            var value = await session.ResolveAsync(Pair(4, i), Keys, default);
            Assert.Equal(partial ? ItemClassificationState.IncompleteTarget : new[] { ItemClassificationState.EmptyText, ItemClassificationState.InvalidText,
                ItemClassificationState.Unsupported, ItemClassificationState.Ready }[i - 1], value.State);
            if (partial) Assert.Null(value.Target); else Assert.NotNull(value.Target);
        }
        Assert.Equal(partial ? ItemClassificationState.IncompleteTarget : ItemClassificationState.NotFound,
            (await session.ResolveAsync(Pair(0, 0), Keys, default)).State);
    }
    [Fact]
    public async Task CompositeFullKeyspaceUsesBoundedLiveScratchAndDoesNotRetainDirectoryObjects()
    {
        await using var f = new Fixture(); var index = new CompositeClassificationIndex(f.Objects, new());
        var binding = await f.Binding(Enumerable.Range(0, 65_536).Select(i => Sub((uint)i, (i >> 8) - 128, (i & 255) - 128, "x")).ToArray(), subclass: true);
        var baseline = GC.GetTotalMemory(true); long peak = baseline;
        var artifact = await index.BuildAsync(binding, p => { if (p.Completed == 50 || p.Completed % 10000 == 0 || p.Completed == p.Total) peak = Math.Max(peak, GC.GetTotalMemory(true)); return Task.CompletedTask; }, default);
        output.WriteLine($"Composite65536 groups: sampled live delta={peak - baseline:N0}, artifact={artifact.ByteLength:N0} bytes; managed samples exclude process peak/cumulative allocations.");
        Assert.InRange(peak - baseline, 0, ClassificationPolicy.MaxScratchBytes); Assert.Equal("65536", artifact.KeyCount);
        await using var session = await index.OpenAsync(artifact, default);
        foreach (var key in new[] { 0, 1, 32767, 65535 }) Assert.Equal(N(key), (await session.ResolveAsync(key, Keys, default)).Target!.RowId);
    }
    [Fact]
    public async Task CompositeSpoolLimitAndCancellationRetireBothStagingFiles()
    {
        await using var f = new Fixture(); var index = new CompositeClassificationIndex(f.Objects, new());
        var binding = await f.Binding(Enumerable.Range(0, 1000).Select(i => Sub((uint)i, (i >> 8) - 128, (i & 255) - 128, new string('\u0800', 4096))).ToArray(), subclass: true);
        Assert.Equal("ITEM_CLASSIFICATION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => index.BuildAsync(binding, Progress, default))).Code);
        Assert.Empty(f.StagingFiles);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.BuildAsync(binding, _ => { stop.Cancel(); return Task.CompletedTask; }, stop.Token));
        Assert.Empty(f.StagingFiles);
        var small = await index.BuildAsync(await f.Binding([Sub(1, 2, 1, "Retry")], subclass: true), Progress, default);
        await using var session = await index.OpenAsync(small, default); Assert.Equal("Retry", (await session.ResolveAsync(Pair(2, 1), Keys, default)).Text);
    }
    [Theory]
    [InlineData("-1", ItemClassificationState.FieldUnavailable)] [InlineData("256", ItemClassificationState.FieldUnavailable)]
    [InlineData("01", ItemClassificationState.FieldUnavailable)] [InlineData("128", ItemClassificationState.KeyOutOfRange)]
    [InlineData("255", ItemClassificationState.KeyOutOfRange)] [InlineData("127", null)] [InlineData("0", null)]
    public void UnsignedItemSourceIsValidatedBeforeMathematicalSignedTargetRange(string sub, ItemClassificationState? expected)
    {
        var item = new AuthorizedItemSummary("1", true, [new(ItemsProjection.ItemTable, 1, "2"), new(ItemsProjection.ItemTable, 2, sub)]);
        var result = ItemClassificationsStore.SourceKey(item, 1, 2, ItemClassificationKind.Subclass);
        Assert.Equal(expected, result.State); Assert.Equal(2, result.Keys.Count);
        if (expected is null) Assert.Equal(Pair(2, int.Parse(sub, CultureInfo.InvariantCulture)), result.Key);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SecondStagingCreateFailureOrCancellationDeletesFirstFileAndReleasesBuilder(bool cancel)
    {
        await using var f = new Fixture(); var binding = await f.Binding([Sub(1, 2, 1, "Name")], subclass: true);
        using var stop = new CancellationTokenSource(); var calls = 0;
        var index = new CompositeClassificationIndex(f.Objects, new(), token =>
        {
            if (++calls == 1) return f.Objects.CreateStagingFileAsync(token);
            if (cancel) { stop.Cancel(); return Task.FromCanceled<FileStream>(stop.Token); }
            return Task.FromException<FileStream>(new IOException("Authored second-stage creation failure"));
        });
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.BuildAsync(binding, Progress, stop.Token));
        else await Assert.ThrowsAsync<IOException>(() => index.BuildAsync(binding, Progress, default));
        Assert.Equal(2, calls); Assert.Empty(f.StagingFiles);
        await new CompositeClassificationIndex(f.Objects, new()).BuildAsync(binding, Progress, default);
    }
    [Fact]
    public async Task CompositeObservationTimesDoNotChangeLogicalKeyOrActualIndexBytes()
    {
        await using var f = new Fixture(); var index = new CompositeClassificationIndex(f.Objects, new());
        var binding = await f.Binding([Sub(99, 2, 1, "Name")], subclass: true);
        var changed = binding with { Configurations = binding.Configurations.Select(c => c with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-21T00:00:00Z" }).ToArray(),
            Source = binding.Source with { PreparedAt = "2026-10-22T00:00:00Z", Source = binding.Source.Source with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-21T00:00:00Z" } } };
        var first = await index.BuildAsync(binding, Progress, default); var second = await index.BuildAsync(changed, Progress, default);
        Assert.Equal(first.ContentKey, second.ContentKey); Assert.Equal(first.Sha256, second.Sha256); Assert.Equal(first.ByteLength, second.ByteLength);
        Assert.False(ClassificationPolicy.SameAuthority(binding.Source.Source, changed.Source.Source));
    }
    [Theory]
    [InlineData("-128", null)] [InlineData("-1", null)] [InlineData("127", null)]
    [InlineData("-129", ItemClassificationState.KeyOutOfRange)] [InlineData("128", ItemClassificationState.KeyOutOfRange)]
    [InlineData("2147483647", ItemClassificationState.KeyOutOfRange)]
    [InlineData("2147483648", ItemClassificationState.FieldUnavailable)] [InlineData("-2147483649", ItemClassificationState.FieldUnavailable)]
    public void Signed32SourceDomainPrecedesCompositeTargetBounds(string cls, ItemClassificationState? expected)
    {
        var item = new AuthorizedItemSummary("1", true, [new(ItemsProjection.ItemTable, 1, cls), new(ItemsProjection.ItemTable, 2, "1")]);
        Assert.Equal(expected, ItemClassificationsStore.SourceKey(item, 1, 2, ItemClassificationKind.Subclass).State);
    }
    [Fact]
    public void CompleteTwoLabelEnvelopeCountingRefusesBoundaryWithoutBuildingRejectedJson()
    {
        var context = new ItemsContext("wow", new('a', 64), "us", "enUS");
        ItemClassificationBatch Batch(int length) => new(context, new('b', 64), ClassificationPolicy.CompositeRevision, [],
            Enumerable.Range(0, 50).Select(_ => new ItemClassificationOutcome("4294967295", true,
                new(ItemClassificationState.Ready, Keys, new string('\u0800', length), new(ClassificationPolicy.ClassTable, "99", 2, false, null), "1"),
                new(ItemClassificationState.Ready, Keys, new string('\u0800', length), new(ClassificationPolicy.SubclassTable, "99", 3, false, null), "1"))).ToArray());
        var lo = 0; var hi = 4096;
        while (lo < hi) { var mid = lo + (hi - lo + 1) / 2; try { ItemClassificationsStore.RequireResponseBudget(Batch(mid)); lo = mid; } catch (DiscoveryException) { hi = mid - 1; } }
        Assert.InRange(lo, 1, 4095); ItemClassificationsStore.RequireResponseBudget(Batch(lo));
        Assert.Equal("ITEM_CLASSIFICATION_RESPONSE_LIMIT", Assert.Throws<DiscoveryException>(() => ItemClassificationsStore.RequireResponseBudget(Batch(lo + 1))).Code);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-classification-index-" + Guid.NewGuid().ToString("N"));
        internal readonly VerifiedObjectStore Objects;
        internal readonly ClassificationIndex Index;
        internal IEnumerable<string> StagingFiles => Directory.EnumerateFiles(Path.Combine(directory, "staging-v1"));
        internal Fixture() { Objects = new(directory); Index = new(Objects, new()); }
        internal async Task<ClassificationIndexBinding> Binding(Db2Row[] values, bool partial = false, bool subclass = false)
        {
            const string time = "2026-10-19T00:00:00Z";
            var context = new ItemsContext("wow", new('a', 64), "us", "enUS");
            var binding = new TableArtifactBinding(new('b', 64), new('c', 64), new('d', 64), "fixture-provider", "enUS", "fixture-decoder", new('e', 64));
            Db2Column[] columns = subclass
                ? [new("ID", "integer", true), new("ClassID", "integer", false), new("SubClassID", "integer", false), new("DisplayName_lang", "string", false), new("VerboseName_lang", "string", false)]
                : [new("ID", "integer", true), new("ClassID", "integer", false), new("ClassName_lang", "string", false), new("Other", "string", false)];
            var table = subclass ? ClassificationPolicy.SubclassTable : ClassificationPolicy.ClassTable;
            var copies = values.Count(v => v.IsCopy); var physical = values.Length - copies;
            var header = new Wdc5TableHeader(1, table, "12345678", physical + (partial ? 1 : 0), copies, 4, columns);
            var completion = new Wdc5Completion(header.DeclaredRecords, physical, copies, partial ? 1 : 0, partial ? 1 : 0, 0,
                partial ? "unencryptedSections" : "complete", 10, values.Sum(v => v.TextIssues?.Count ?? 0));
            await using var reader = new TableRowArtifacts(Objects); await using var writer = await reader.CreateWriterAsync(binding, default);
            await writer.HeaderAsync(header, default); foreach (var row in values) await writer.RowAsync(row, default);
            var artifact = await writer.CompleteAsync(completion, default);
            var metadata = new MetadataReceipt("encoding", new('1', 32), new('2', 32), new('3', 64), "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/fixture", time, time, "fixture-v1");
            var source = new ContentReceipt(new('4', 32), new('5', 32), binding.SourceSha256, "10", "10", metadata.SourceUrl, time, time, "fixture-v1", new("loose", "0", "10", null));
            var association = new SelectedFileAssociation(subclass ? ClassificationPolicy.SubclassFile : ClassificationPolicy.ClassFile, "enUS", "fixture-v1", metadata, metadata with { Role = "root" }, []);
            var snapshot = new TableDataSnapshot("wow", context.BuildIdentity, "us", table, "enUS", artifact.ContentKey,
                time, header.LayoutHash, binding.DefinitionSha256, binding.ProviderRevision, binding.DecoderRevision, source, association,
                columns.Select(c => new TableDataColumn(c.Name, c.Kind, c.IsId, null)).ToArray(),
                new(completion.Scope, N(completion.DeclaredRecords), N(physical), N(copies), N(completion.ExcludedEncryptedSections), N(completion.ExcludedPhysicalRecords),
                    N(copies), N(completion.ExcludedPhysicalRecords), "0"), N(values.Length), N(completion.TextIssueCount));
            ConfigurationReceipt[] configurations = [new("buildConfig", new('1', 32), new('2', 64), 10, metadata.SourceUrl, time, time, 1),
                new("cdnConfig", new('3', 32), new('4', 64), 10, metadata.SourceUrl, time, time, 1)];
            return new(context, "12.1.5.70448", new("1", "us", new('a', 64), 0, new('b', 64), 0), configurations,
                subclass ? ItemClassificationKind.Subclass : ItemClassificationKind.Class, snapshot, artifact,
                subclass ? [new("ClassID", 1, "integer", 8, false, true), new("SubClassID", 2, "integer", 8, false, true)] : [new("ClassID", 1, "integer", 8, false, true)],
                new(subclass ? "DisplayName_lang" : "ClassName_lang", subclass ? 3 : 2, "string", null, null, true));
        }
        public async ValueTask DisposeAsync() { await Objects.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
[CollectionDefinition("Classification index builder", DisableParallelization = true)]
public sealed class ClassificationIndexBuilderCollection { }
