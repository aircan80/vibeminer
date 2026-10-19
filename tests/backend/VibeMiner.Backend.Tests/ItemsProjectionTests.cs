using System.Globalization;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemsProjectionTests(ITestOutputHelper output)
{
    private static readonly ItemsContext Context = new("wow", new('a', 64), "us", "enUS");
    private static Task Progress(ItemsPreparationProgress _) => Task.CompletedTask;

    [Fact]
    public async Task AnchorJoinIncludesCopyTargetsAndCountsMissingSparseOnlyAndTypedNames()
    {
        await using var f = new Fixture();
        var binding = await f.Binding([Item(0), Item(1), Item(2, true, 0), Item(uint.MaxValue)],
            [Sparse(0, "Sword"), Sparse(1, ""), Sparse(2, "", true, 0, [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")]), Sparse(3, "Sparse only")]);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default);
        Assert.Equal(new ItemsJoinCounts("4", "4", "3", "1", "1", "1", "1", "1"), artifact.Counts);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var page = await session.ReadPageAsync(new(Context, artifact.ContentKey), default);
        Assert.Equal(["0", "1", "2", "4294967295"], page.Entries.Select(row => row.Id));
        Assert.Equal([ItemNameState.Valid, ItemNameState.Empty, ItemNameState.Invalid, ItemNameState.MissingSparse], page.Entries.Select(row => row.Name.State));
        Assert.Null(page.Entries[2].Name.Value); Assert.Null(page.Entries[3].Name.Value); Assert.Equal("", page.Entries[1].Name.Value);
        var detail = await session.InspectAsync(new(Context, artifact.ContentKey, "2"), default);
        Assert.True(detail.Detail!.ItemRow.IsCopy); Assert.Equal("0", detail.Detail.ItemRow.CopySourceId);
        Assert.True(detail.Detail.SparseRow!.IsCopy); Assert.Equal("0", detail.Detail.SparseRow.CopySourceId);
        Assert.Single(detail.Detail.SparseRow.TextIssues!);
        Assert.Null((await session.InspectAsync(new(Context, artifact.ContentKey, "3"), default)).Detail);
        Assert.Equal(4, session.LastPayloadReads - 1); // Browse four, then one inspected payload.
    }

    [Fact]
    public async Task NamesUseOnlyDisplayWhitespaceIsValidAndDescriptionIssueDoesNotInvalidateName()
    {
        await using var f = new Fixture();
        var binding = await f.Binding([Item(1), Item(2)], [Sparse(1, "  "), Sparse(2, "Actual", issues: [new(2, "DB2_TEXT_ENCODING_INVALID", "ff")])]);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        Assert.Single((await session.ReadPageAsync(new(Context, artifact.ContentKey, "ACTUAL"), default)).Entries);
        Assert.Single((await session.ReadPageAsync(new(Context, artifact.ContentKey, "  "), default)).Entries);
        Assert.Empty((await session.ReadPageAsync(new(Context, artifact.ContentKey, "Description"), default)).Entries);
        Assert.Equal("Actual", (await session.InspectAsync(new(Context, artifact.ContentKey, "2"), default)).Detail!.Summary.Name.Value);
    }

    [Theory]
    [InlineData("STRASSE", 0)]
    [InlineData("STRAßE", 1)]
    [InlineData("%_\\\"", 1)]
    [InlineData("é𝄞", 1)]
    public async Task SearchUsesLiteralDotnetOrdinalIgnoreCase(string query, int count)
    {
        await using var f = new Fixture();
        var binding = await f.Binding([Item(1), Item(2)], [Sparse(1, "Straße %_\\\""), Sparse(2, "Café𝄞")]);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        Assert.Equal(count, (await session.ReadPageAsync(new(Context, artifact.ContentKey, query), default)).Entries.Count);
        Assert.True(session.LastPayloadReads <= 1);
    }

    [Fact]
    public async Task DirectoryAndNameBlockBoundariesPreserveMultibyteMatchesWithoutPerRowReads()
    {
        await using var f = new Fixture(); const int count = 5000;
        var rows = Enumerable.Range(1, count).Select(i => Sparse((uint)i, new string('é', 130) + (i is 4096 or 4097 or 5000 ? "𝄞MATCH" : "other"))).ToArray();
        var artifact = await f.Projection.BuildAsync(await f.Binding(Enumerable.Range(1, count).Select(i => Item((uint)i)).ToArray(), rows), Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var first = await session.ReadPageAsync(new(Context, artifact.ContentKey, "𝄞match", Limit: 2), default);
        Assert.Equal(["4096", "4097"], first.Entries.Select(row => row.Id)); Assert.NotNull(first.NextCursor);
        Assert.True(session.LastNameBlockReads < 10); Assert.Equal(2, session.LastPayloadReads); Assert.Equal(count, session.LastScannedRows);
        var last = await session.ReadPageAsync(new(Context, artifact.ContentKey, "𝄞match", Limit: 2, After: first.NextCursor), default);
        Assert.Equal("5000", Assert.Single(last.Entries).Id); Assert.Null(last.NextCursor);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => session.ReadPageAsync(new(Context, artifact.ContentKey, "other", Limit: 2, After: first.NextCursor), default))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => session.ReadPageAsync(new(Context, artifact.ContentKey, "𝄞match", Limit: 1, After: first.NextCursor), default))).Code);
    }

    [Fact]
    public async Task ResponseBudgetTrimmingResumesTheFirstUnreturnedMatch()
    {
        await using var f = new Fixture(); var name = new string('x', 65536);
        var artifact = await f.Projection.BuildAsync(await f.Binding(Enumerable.Range(1, 10).Select(i => Item((uint)i)).ToArray(),
            Enumerable.Range(1, 10).Select(i => Sparse((uint)i, name)).ToArray()), Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        string? after = null; var ids = new List<string>();
        do
        {
            var page = await session.ReadPageAsync(new(Context, artifact.ContentKey, "x", After: after), default);
            Assert.InRange(page.Entries.Count, 1, 7);
            var frame = new { protocolVersion = 1, id = string.Concat(Enumerable.Repeat("𝄞", 128)), result = page };
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(frame, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } }).Length <= 512 << 10);
            ids.AddRange(page.Entries.Select(row => row.Id)); after = page.NextCursor;
        } while (after is not null);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => N(i)), ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateIdsInEitherSourceNeverPublishReadyProjection(bool sparseDuplicate)
    {
        await using var f = new Fixture();
        var binding = await f.Binding(sparseDuplicate ? [Item(1)] : [Item(1), Item(1, true, 0)],
            sparseDuplicate ? [Sparse(1, "One"), Sparse(1, "Copy", true, 0)] : [Sparse(1, "One")]);
        Assert.Equal("ITEMS_SOURCE_DUPLICATE", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Projection.BuildAsync(binding, Progress, default))).Code);
        Assert.Empty(f.StagingFiles);
    }

    [Fact]
    public async Task ContextCoverageAndRawPhysicalClaimsRemainExactAndPreparedAtDoesNotChangeIdentity()
    {
        await using var f = new Fixture();
        var binding = await f.Binding([Item(1)], [Sparse(1, "Name")], rawQuality: true, partial: true);
        Assert.Equal(ItemsProjection.ContentKey(binding), ItemsProjection.ContentKey(binding with { ItemSource = binding.ItemSource with { PreparedAt = "2027-01-01T00:00:00Z" } }));
        Assert.Equal("ITEMS_SOURCE_CONTEXT", Assert.Throws<DiscoveryException>(() => ItemsProjection.ContentKey(binding with { SparseSource = binding.SparseSource with { BuildIdentity = new('b', 64) } })).Code);
        Assert.Equal("ITEMS_SOURCE_CONTEXT", Assert.Throws<DiscoveryException>(() => ItemsProjection.ContentKey(binding with { SparseSource = binding.SparseSource with { Coverage = binding.SparseSource.Coverage with { Scope = "complete" } } })).Code);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        Assert.Equal("unencryptedSections", session.Snapshot.SparseSource.Coverage.Scope);
        var summary = Assert.Single((await session.ReadPageAsync(new(Context, artifact.ContentKey, ExactItemId: "1"), default)).Entries);
        Assert.DoesNotContain(summary.BaseValues, v => v.TableId == ItemsProjection.SparseTable && v.ColumnIndex == 3);
        Assert.DoesNotContain(artifact.Groups.SelectMany(g => g.Fields), v => v.TableId == ItemsProjection.SparseTable && v.ColumnIndex == 3);
        Assert.Equal("42", (await session.InspectAsync(new(Context, artifact.ContentKey, "1"), default)).Detail!.SparseRow!.Values[3]);
    }

    [Fact]
    public async Task CancellationBeforePublicationCleansEveryPrivateRunAndColdReopenUsesLocalProjection()
    {
        await using var f = new Fixture(); var binding = await f.Binding([Item(1)], [Sparse(1, "Name")]);
        using var stop = new CancellationTokenSource();
        Task Report(ItemsPreparationProgress p) { if (p.Stage.StartsWith("Publishing", StringComparison.Ordinal)) stop.Cancel(); return Task.CompletedTask; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Projection.BuildAsync(binding, Report, stop.Token)); Assert.Empty(f.StagingFiles);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default);
        await using var session = await new ItemsProjection(f.Objects, f.Resources).OpenAsync(artifact, Fixture.Time, default);
        Assert.Equal("Name", Assert.Single((await session.ReadPageAsync(new(Context, artifact.ContentKey), default)).Entries).Name.Value);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadPageAsync(new(Context, artifact.ContentKey), canceled.Token));
    }

    [Fact]
    public async Task InspectPreservesTenAlignedStatSlotsMaskWordsSentinelsAndConflictingSourceValues()
    {
        await using var f = new Fixture(); var sparse = Sparse(1, "Name"); var values = sparse.Values.ToArray(); values[5] = "17";
        var artifact = await f.Projection.BuildAsync(await f.Binding([Item(1)], [sparse with { Values = values }]), Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var detail = (await session.InspectAsync(new(Context, artifact.ContentKey, "1"), default)).Detail!;
        string Value(string column) => detail.SparseRow!.Values[session.Snapshot.SparseSource.Columns.Select((c, i) => (c, i)).Single(p => p.c.Name == column).i];
        Assert.Equal(103, detail.SparseRow!.Values.Count);
        for (var slot = 0; slot < 10; slot++)
        {
            Assert.Equal(slot == 0 ? "-1" : N(slot), Value($"StatModifier_bonusStat[{slot}]"));
            Assert.Equal(N(100 + slot), Value($"StatPercentEditor[{slot}]"));
            Assert.Equal(N(200 + slot), Value($"StatPercentageOfSocket[{slot}]"));
        }
        Assert.Equal("4294967295", Value("Flags[0]")); Assert.Equal("2147483648", Value("AllowableRaces[1]")); Assert.Equal("-1", Value("RequiredLevel"));
        Assert.Contains(detail.Summary.BaseValues, v => v.TableId == ItemsProjection.ItemTable && v.Value == "13");
        Assert.Contains(detail.Summary.BaseValues, v => v.TableId == ItemsProjection.SparseTable && v.ColumnIndex == 5 && v.Value == "17");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2SocketGroupsReferenceExactSourceNamesAndLegacyMetadataRemainsReadable(bool rawSockets)
    {
        await using var f = new Fixture();
        var artifact = await f.Projection.BuildAsync(await f.Binding([Item(1), Item(2)], [Sparse(1, "Name"), Sparse(2, "Other")], rawSockets: rawSockets), Progress, default);
        Assert.Equal("items-projection-v2", artifact.FeatureRevision);
        foreach (var name in new[] { "Socket_match_enchantment_ID", "Gem_properties" })
        {
            var index = artifact.Binding.SparseSource.Columns.Select((c, i) => (c, i)).Single(p => p.c.Name == name).i;
            Assert.Equal(!rawSockets, artifact.Groups.Single(g => g.Key == "socketsStats").Fields.Contains(new(ItemsProjection.SparseTable, index)));
        }
        var legacy = await HistoricalV1(f.Objects, artifact);
        var descriptor = JsonSerializer.SerializeToNode(legacy, WireJson)!.AsObject(); descriptor.Remove("featureRevision");
        legacy = JsonSerializer.Deserialize<ItemsProjectionArtifact>(descriptor.ToJsonString(), WireJson)!;
        Assert.Equal(ItemsProjection.LegacyRevision, legacy.FeatureRevision);
        Assert.NotEqual(artifact.ContentKey, legacy.ContentKey);
        await using var old = await f.Projection.OpenAsync(legacy, Fixture.Time, default);
        Assert.Equal(ItemsProjection.LegacyRevision, old.Snapshot.FeatureRevision);
        var page = await old.ReadPageAsync(new(Context, legacy.ContentKey, Limit: 1), default);
        Assert.Equal("Name", Assert.Single(page.Entries).Name.Value);
        await using var fresh = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var detail = (await fresh.InspectAsync(new(Context, artifact.ContentKey, "1"), default)).Detail!;
        Assert.Equal("0", detail.SparseRow!.Values[47]); Assert.Equal("1", detail.SparseRow.Values[48]);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => fresh.ReadPageAsync(new(Context,
            artifact.ContentKey, Limit: 1, After: page.NextCursor), default))).Code);
        Assert.Equal("ITEMS_PROJECTION_INVALID", Assert.Throws<DiscoveryException>(() => ItemsProjection.ValidateArtifact(legacy with { FeatureRevision = ItemsProjection.Revision })).Code);
        Assert.Equal("ITEMS_PROJECTION_INVALID", Assert.Throws<DiscoveryException>(() => ItemsProjection.ValidateArtifact(artifact with { FeatureRevision = "items-projection-v99" })).Code);
        Assert.Equal("ITEMS_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Projection.OpenAsync(legacy with
            { ContentKey = ItemsProjection.ContentKey(legacy.Binding), FeatureRevision = ItemsProjection.Revision, Groups = artifact.Groups }, Fixture.Time, default))).Code);
    }

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    // Authored transformation reproduces the original three-field metadata geometry;
    // production only builds v2, and never rewrites a retained v1 object.
    internal static async Task<ItemsProjectionArtifact> HistoricalV1(VerifiedObjectStore objects, ItemsProjectionArtifact current)
    {
        var original = await objects.ReadAsync(current.Sha256, checked((int)current.ByteLength), default);
        var metadataOffset = BinaryPrimitives.ReadInt64LittleEndian(original.AsSpan(24));
        var namesOffset = BinaryPrimitives.ReadInt64LittleEndian(original.AsSpan(36));
        var directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(original.AsSpan(52));
        var groups = current.Groups.Select(g => g with { Fields = g.Fields.Where(f =>
            (f.TableId == ItemsProjection.ItemTable ? current.Binding.ItemSource : current.Binding.SparseSource).Columns[f.ColumnIndex].Name
                is not ("Socket_match_enchantment_ID" or "Gem_properties")).ToArray() }).Where(g => g.Fields.Count > 0).ToArray();
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { current.Binding, current.Counts, Groups = groups }, WireJson);
        var revised = new byte[checked((int)(metadataOffset + metadata.Length + original.Length - namesOffset))];
        original.AsSpan(0, (int)metadataOffset).CopyTo(revised); metadata.CopyTo(revised.AsSpan((int)metadataOffset));
        original.AsSpan((int)namesOffset).CopyTo(revised.AsSpan((int)metadataOffset + metadata.Length));
        var delta = metadataOffset + metadata.Length - namesOffset;
        BinaryPrimitives.WriteInt32LittleEndian(revised.AsSpan(32), metadata.Length);
        BinaryPrimitives.WriteInt64LittleEndian(revised.AsSpan(36), namesOffset + delta);
        BinaryPrimitives.WriteInt64LittleEndian(revised.AsSpan(52), directoryOffset + delta);
        var sha = Convert.ToHexStringLower(SHA256.HashData(revised)); await objects.PublishAsync(revised, sha, default);
        var binding = current.Binding;
        var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            revision = "items-projection-v1", queryPolicy = "ordinal-ignorecase-contains-v1", binding.Context,
            item = binding.ItemSource with { PreparedAt = "" }, sparse = binding.SparseSource with { PreparedAt = "" },
            binding.ItemRows, binding.SparseRows
        }, WireJson)));
        return new(key, sha, revised.Length,
            current.Binding, current.Counts, groups, ItemsProjection.LegacyRevision);
    }

    [Fact]
    public async Task OversizedCombinedDetailFailsExplicitlyWithoutTruncatingSourceRows()
    {
        await using var f = new Fixture();
        var artifact = await f.Projection.BuildAsync(await f.Binding([Item(1)], [Sparse(1, "Name")], hugeDetails: true), Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        Assert.Equal("ITEMS_DETAIL_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => session.InspectAsync(new(Context, artifact.ContentKey, "1"), default))).Code);
        Assert.Equal("Name", Assert.Single((await session.ReadPageAsync(new(Context, artifact.ContentKey), default)).Entries).Name.Value);
    }

    [Fact]
    public async Task UnsupportedOptionalIconIntegerDoesNotDiscardUsableItemOrRawEvidence()
    {
        await using var f = new Fixture(); var item = Item(1) with { Values = ["1", "2", "7", "13", "-1"] };
        var artifact = await f.Projection.BuildAsync(await f.Binding([item], [Sparse(1, "Name")]), Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var row = Assert.Single((await session.ReadPageAsync(new(Context, artifact.ContentKey), default)).Entries);
        Assert.Null(row.IconFileDataId); Assert.Equal("Name", row.Name.Value);
        Assert.Equal("-1", (await session.InspectAsync(new(Context, artifact.ContentKey, "1"), default)).Detail!.ItemRow.Values[4]);
    }

    [Fact]
    public async Task IconAuthorizationReadsOnlyBoundedPayloadsAndPreservesAnchorAndOptionalIdStates()
    {
        await using var f = new Fixture();
        var artifact = await f.Projection.BuildAsync(await f.Binding(
            [Item(0) with { Values = ["0", "2", "7", "13", "0"] }, Item(1) with { Values = ["1", "2", "7", "13", "-1"] }, Item(uint.MaxValue)],
            [Sparse(0, "Name")]), Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var batch = await session.AuthorizeIconsAsync(Context, artifact.ContentKey, ["4294967295", "0", "1", "3", "0"], default);
        Assert.Equal(artifact.ContentKey, batch.Snapshot.ArtifactId);
        Assert.Equal([true, true, true, false, true], batch.Items.Select(i => i.Admitted));
        Assert.Equal("0", batch.Items[1].IconFileDataId); Assert.Null(batch.Items[2].IconFileDataId); Assert.Null(batch.Items[3].IconFileDataId);
        Assert.Equal(["4294967295", "0", "1", "3", "0"], batch.Items.Select(i => i.ItemId));
        Assert.Equal(4, session.LastPayloadReads); Assert.Equal(0, session.LastNameBlockReads); Assert.Equal(0, session.LastScannedRows);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => session.AuthorizeIconsAsync(Context, artifact.ContentKey,
            Enumerable.Repeat("0", 51).ToArray(), default))).Code);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => session.AuthorizeIconsAsync(Context, artifact.ContentKey, ["00"], default))).Code);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => session.AuthorizeIconsAsync(Context with { BuildIdentity = new('b', 64) }, artifact.ContentKey, ["0"], default))).Code);
    }

    [Fact]
    public async Task SummaryAuthorizationReadsOnlyCompactSourceValuesWithoutNamesOrOriginalRows()
    {
        await using var f = new Fixture();
        var artifact = await f.Projection.BuildAsync(await f.Binding([Item(0), Item(uint.MaxValue)],
            [Sparse(0, "Name")]), Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var batch = await session.AuthorizeSummariesAsync(Context, artifact.ContentKey, ["4294967295", "0", "3", "0"], default);
        Assert.Equal(artifact.ContentKey, batch.Snapshot.ArtifactId);
        Assert.Equal(["4294967295", "0", "3", "0"], batch.Items.Select(i => i.ItemId));
        Assert.Equal([true, true, false, true], batch.Items.Select(i => i.Admitted));
        Assert.Empty(batch.Items[2].BaseValues);
        Assert.Equal(batch.Items[1].BaseValues, batch.Items[3].BaseValues);
        Assert.Contains(batch.Items[1].BaseValues, v => v.TableId == ItemsProjection.ItemTable &&
            batch.Snapshot.ItemSource.Columns[v.ColumnIndex].Name == "ClassID" && v.Value == "2");
        Assert.Equal(3, session.LastPayloadReads); Assert.Equal(0, session.LastNameBlockReads); Assert.Equal(0, session.LastScannedRows);
        Assert.Empty((await session.AuthorizeSummariesAsync(Context, artifact.ContentKey, [], default)).Items);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => session.AuthorizeSummariesAsync(Context,
            artifact.ContentKey, Enumerable.Repeat("0", 51).ToArray(), default))).Code);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => session.AuthorizeSummariesAsync(Context,
            artifact.ContentKey, ["00"], default))).Code);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AuthorizeSummariesAsync(Context, artifact.ContentKey, ["0"], canceled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdFeatureOpenRequiresBothOriginalSourceArtifacts(bool corrupt)
    {
        await using var f = new Fixture(); var binding = await f.Binding([Item(1)], [Sparse(1, "Name")]);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default); string path;
        await using (var original = await f.Objects.OpenVerifiedAsync(binding.SparseRows.Sha256, binding.SparseRows.ByteLength, default)) path = original.Name;
        if (corrupt) { var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes); }
        else File.Delete(path);
        Assert.Equal(corrupt ? "LOCAL_OBJECT_CORRUPT" : "LOCAL_OBJECT_MISSING",
            (await Assert.ThrowsAsync<DiscoveryException>(() => f.Projection.OpenAsync(artifact, Fixture.Time, default))).Code);
    }

    [Fact]
    public async Task RepeatedDetailUsesHeldOriginalSourceLeasesInsteadOfReopeningPaths()
    {
        await using var f = new Fixture(); var binding = await f.Binding([Item(1)], [Sparse(1, "Name")]);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        foreach (var source in new[] { binding.ItemRows, binding.SparseRows })
        {
            var path = f.ObjectPath(source.Sha256);
            try { File.Move(path, path + ".held-test"); }
            catch (IOException) when (OperatingSystem.IsWindows())
            { Assert.True(File.Exists(path)); } // Windows denies rename while the read lease is held.
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var detail = (await session.InspectAsync(new(Context, artifact.ContentKey, "1"), default)).Detail!;
            Assert.Equal("Name", detail.Summary.Name.Value); Assert.Equal(103, detail.SparseRow!.Values.Count);
        }
    }

    [Theory]
    [InlineData("names")]
    [InlineData("rows")]
    [InlineData("artifact")]
    public async Task ProjectionLimitsRetireAllPrivateStagingAndLeaveSourcesReusable(string bound)
    {
        await using var f = new Fixture(); var binding = await f.Binding([Item(1)], [Sparse(1, "Name")]);
        var limits = bound switch { "names" => new ItemsProjectionLimits(MaxNameBytes: 1), "rows" => new(MaxRows: 0), _ => new(MaxArtifactBytes: 1024) };
        var limited = new ItemsProjection(f.Objects, f.Resources, configuredLimits: limits);
        Assert.Equal("ITEMS_PROJECTION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => limited.BuildAsync(binding, Progress, default))).Code);
        Assert.Empty(f.StagingFiles);
        var artifact = await f.Projection.BuildAsync(binding, Progress, default);
        await using var ready = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        Assert.Single((await ready.ReadPageAsync(new(Context, artifact.ContentKey), default)).Entries);
    }

    [Fact]
    public async Task CompactNamesHaveMeasuredBoundedScansAcrossFiftyThousand103ColumnSources()
    {
        await using var f = new Fixture(); const int count = 50_000;
        var binding = await f.Binding(Enumerable.Range(0, count).Select(i => Item((uint)i)).ToArray(),
            Enumerable.Range(0, count).Select(i => Sparse((uint)i, $"Bench Item {i:D5} Café𝄞")).ToArray());
        var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
        var artifact = await f.Projection.BuildAsync(binding, Progress, default); var buildMs = clock.Elapsed.TotalMilliseconds;
        var buildAllocations = GC.GetTotalAllocatedBytes(true) - allocated; clock.Restart();
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default); var openMs = clock.Elapsed.TotalMilliseconds;
        var samples = new List<double>(); var queryAllocated = GC.GetTotalAllocatedBytes(true);
        for (var pass = 0; pass < 3; pass++)
        {
            clock.Restart(); var page = await session.ReadPageAsync(new(Context, artifact.ContentKey, "no-such-name"), default);
            samples.Add(clock.Elapsed.TotalMilliseconds); Assert.Empty(page.Entries);
            Assert.Equal(count, session.LastScannedRows); Assert.Equal(0, session.LastPayloadReads); Assert.InRange(session.LastNameBlockReads, 1, 13);
        }
        queryAllocated = GC.GetTotalAllocatedBytes(true) - queryAllocated;
        var match = await session.ReadPageAsync(new(Context, artifact.ContentKey, "item 49999"), default);
        Assert.Equal("49999", Assert.Single(match.Entries).Id); Assert.Equal(1, session.LastPayloadReads);
        output.WriteLine($"Synthetic 50,000 Item / 50,000 Sparse rows; Sparse columns=103; six attributed summary fields. Source bytes={binding.ItemRows.ByteLength + binding.SparseRows.ByteLength}; projection bytes={artifact.ByteLength}.");
        output.WriteLine($"Warm retained source files/cold projection: build={buildMs:F3}ms, allocated={buildAllocations}; fresh session map+two-source SHA/open={openMs:F3}ms.");
        output.WriteLine($"Three full compact-name scans (ms)={string.Join(',', samples.Select(ms => ms.ToString("F3", CultureInfo.InvariantCulture)))}; total query allocated={queryAllocated}; 13 name blocks/50,000 rows per scan, zero payload deserializations. Exact-name match deserializes one payload.");
    }

    private static Db2Row Item(uint id, bool copy = false, uint? source = null) => new(id, copy, source, [N(id), "2", "7", "13", "132759"]);
    private static Db2Row Sparse(uint id, string name, bool copy = false, uint? source = null, IReadOnlyList<Db2TextIssue>? issues = null)
    {
        var values = new List<string> { N(id), name, "", "42", "500", "13", "1", "2", "3" };
        values.AddRange(Enumerable.Range(0, 10).Select(i => i == 0 ? "-1" : N(i)));
        values.AddRange(Enumerable.Range(100, 10).Select(i => N(i))); values.AddRange(Enumerable.Range(200, 10).Select(i => N(i)));
        values.AddRange(["4294967295", "0", "2147483648", "1", "2", "4294967295", "2147483648", "-1"]);
        values.AddRange(Enumerable.Range(0, 56).Select(i => N(i))); return new(id, copy, source, values.ToArray(), issues);
    }
    private static string N(long number) => number.ToString(CultureInfo.InvariantCulture);
    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Time = "2026-10-19T00:00:00Z";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-items-" + Guid.NewGuid().ToString("N"));
        internal readonly VerifiedObjectStore Objects;
        internal readonly AcquisitionResources Resources = new();
        internal readonly ItemsProjection Projection;
        internal Fixture() { Objects = new(directory); Projection = new(Objects, Resources); }
        internal string ObjectPath(string sha256) => Path.Combine(directory, "objects-v1", sha256[..2], sha256[2..]);
        internal IEnumerable<string> StagingFiles => Directory.EnumerateFiles(Path.Combine(directory, "staging-v1"));
        internal async Task<ItemsProjectionBinding> Binding(Db2Row[] itemRows, Db2Row[] sparseRows, bool rawQuality = false, bool partial = false, bool hugeDetails = false, bool rawSockets = false)
        {
            Db2Column[] itemColumns = [new("ID", "integer", true), new("ClassID", "integer", false), new("SubclassID", "integer", false), new("InventoryType", "integer", false), new("IconFileDataID", "integer", false)];
            if (hugeDetails)
            {
                itemColumns = [.. itemColumns, .. Enumerable.Range(0, 4).Select(i => new Db2Column($"OtherText{i}", "string", false))];
                itemRows = itemRows.Select(row => row with { Values = [.. row.Values, .. Enumerable.Repeat(new string('x', 65300), 4)] }).ToArray();
                sparseRows = sparseRows.Select(row => row with { Values = row.Values.Select((v, i) => i is >= 99 and <= 102 ? new string('x', 65300) : v).ToArray() }).ToArray();
            }
            var item = await Table(ItemsProjection.ItemTable, itemColumns, itemRows, 'b');
            var columns = new List<Db2Column> { new("ID", "integer", true), new("Display_lang", "string", false), new("Description_lang", "string", false),
                new("OverallQualityID", "integer", false, rawQuality ? new("raw-physical-integer-v1", 32, false, 8, true, "Authored raw physical value") : null), new("ItemLevel", "integer", false),
                new("InventoryType", "integer", false) };
            columns.AddRange(Enumerable.Range(0, 3).Select(i => new Db2Column($"SocketType[{i}]", "integer", false)));
            foreach (var name in new[] { "StatModifier_bonusStat", "StatPercentEditor", "StatPercentageOfSocket" })
                columns.AddRange(Enumerable.Range(0, 10).Select(i => new Db2Column($"{name}[{i}]", "integer", false)));
            columns.AddRange(Enumerable.Range(0, 5).Select(i => new Db2Column($"Flags[{i}]", "integer", false)));
            columns.AddRange(Enumerable.Range(0, 2).Select(i => new Db2Column($"AllowableRaces[{i}]", "integer", false)));
            columns.Add(new("RequiredLevel", "integer", false));
            var socketPolicy = rawSockets ? new Db2ValueInterpretation("raw-physical-integer-v1", 32, false, 8, true, "Authored raw socket value") : null;
            columns.Add(new("Socket_match_enchantment_ID", "integer", false, socketPolicy)); columns.Add(new("Gem_properties", "integer", false, socketPolicy));
            columns.AddRange(Enumerable.Range(0, 54).Select(i => new Db2Column($"Other{i}", "string", false)));
            var sparse = await Table(ItemsProjection.SparseTable, columns.ToArray(), sparseRows, 'c', partial);
            return new(Context, Snapshot(item), Snapshot(sparse), item, sparse);
        }
        private async Task<TableRowsArtifact> Table(string table, Db2Column[] columns, Db2Row[] values, char key, bool partial = false)
        {
            await using var rows = new TableRowArtifacts(Objects);
            var binding = new TableArtifactBinding(new(key, 64), new('d', 64), new('e', 64), "authored-provider", "enUS", "authored-decoder", new('f', 64));
            await using var writer = await rows.CreateWriterAsync(binding, default);
            var copies = values.Count(row => row.IsCopy); await writer.HeaderAsync(new(1, table.ToUpperInvariant(), "12345678", values.Length, copies, 4, columns), default);
            foreach (var value in values) await writer.RowAsync(value, default);
            return await writer.CompleteAsync(new(values.Length + (partial ? 1 : 0), values.Length - copies, copies, partial ? 1 : 0,
                partial ? 1 : 0, 0, partial ? "unencryptedSections" : "complete", 10, values.Sum(row => row.TextIssues?.Count ?? 0)), default);
        }
        private static TableDataSnapshot Snapshot(TableRowsArtifact rows)
        {
            var b = rows.Binding; var c = rows.Completion;
            var metadata = new MetadataReceipt("encoding", new('1', 32), new('2', 32), new('3', 64), "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/authored", Time, Time, "authored-v1");
            var source = new ContentReceipt(new('4', 32), new('5', 32), b.SourceSha256, "10", "10", metadata.SourceUrl, Time, Time, "authored-v1", new("loose", "0", "10", null));
            var association = new SelectedFileAssociation("841626", "enUS", "authored-v1", metadata, metadata with { Role = "root" }, []);
            return new(Context.ProductId, Context.BuildIdentity, Context.SourceRegion, rows.Header.TableHash.ToLowerInvariant(), "enUS", rows.ContentKey, Time,
                rows.Header.LayoutHash, b.DefinitionSha256, b.ProviderRevision, b.DecoderRevision, source, association,
                rows.Header.Columns.Select(column => new TableDataColumn(column.Name, column.Kind, column.IsId, null,
                    column.Interpretation is { } p ? new(p.Policy, p.PhysicalBits, p.PhysicalSigned, p.CandidateBits, p.CandidateUnsigned, p.Warning) : null)).ToArray(),
                new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), N(c.CopyRows), N(c.ExcludedEncryptedSections), N(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords),
                    N(rows.Header.DeclaredCopyRecords), N(c.ExcludedPhysicalRecords), N(c.ExcludedCopyRecords)), N(c.PhysicalRows + c.CopyRows), N(c.TextIssueCount));
        }
        public async ValueTask DisposeAsync() { await Objects.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
