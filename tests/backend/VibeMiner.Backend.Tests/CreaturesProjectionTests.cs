using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreaturesProjectionTests(ITestOutputHelper output)
{
    private static Task Progress(CreaturesPreparationProgress _) => Task.CompletedTask;
    [Fact]
    public async Task DeclaredSlotsNumericFactsNamesCopiesAndCoverageRemainExact()
    {
        await using var f = new Fixture(); var binding = await f.Binding([new(0, "  "), new(9, "", 0), new(4294967295, "", null, true)], true);
        await using var source = new TableRowArtifacts(f.Objects); var artifact = await f.Projection.BuildAsync(binding, source, Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        Assert.Equal(new CreatureCounts("3", "1", "1", "1", "6", "3", "3"), artifact.Counts);
        var page = await session.ReadPageAsync(new(binding.Context, artifact.ContentKey), default);
        Assert.Equal(["0", "9", "4294967295"], page.Entries.Select(e => e.Id));
        Assert.Equal("  ", page.Entries[0].Name.Value); Assert.Equal(CreatureNameState.Empty, page.Entries[1].Name.State);
        var invalid = page.Entries[2].Name; Assert.Null(invalid.Value); Assert.Equal("DB2_TEXT_ENCODING_INVALID", invalid.Issue!.Code);
        Assert.Equal(new CreatureSourceReference(CreaturesPolicy.Table, binding.Rows.ContentKey, "4294967295", binding.Fields[0].ColumnIndex), invalid.Issue.Source);
        Assert.Equal(["-128", "255", "65535", "127"], page.Entries[0].Facts.Select(v => v.Value));
        Assert.Equal(["123", "123", "0", "-1"], page.Entries[0].DisplaySlots.Select(v => v.DisplayId));
        Assert.Equal(["-1", "0", "0.5", "1"], page.Entries[0].DisplaySlots.Select(v => v.Probability));
        Assert.Equal([CreatureDisplayState.Declared, CreatureDisplayState.Declared, CreatureDisplayState.NoDeclaredDisplay, CreatureDisplayState.InvalidDisplayReference], page.Entries[0].DisplaySlots.Select(v => v.State));
        Assert.Equal("unencryptedSections", session.Snapshot.CreatureSource.Coverage.Scope);
        Assert.Equal(["Classification", "CreatureType", "CreatureFamily", "StartAnimState"], binding.Fields.Skip(4).Take(4).Select(v => v.Name));
        Assert.Null(await session.FindAsync(8, default));
    }
    [Fact]
    public async Task SearchUsesOnlyPrimaryLiteralOrdinalIgnoreCaseAndExactIdRemainsDistinct()
    {
        await using var f = new Fixture(); var b = await f.Binding([new(0, "123"), new(2, "Straße %_"), new(3, "STRASSE %_"), new(4, "café"), new(5, "CAFE"), new(9, "  ")]);
        await using var source = new TableRowArtifacts(f.Objects); var artifact = await f.Projection.BuildAsync(b, source, Progress, default);
        await using var s = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        async Task<string[]> Search(string query) => (await s.ReadPageAsync(new(b.Context, artifact.ContentKey, query), default)).Entries.Select(v => v.Id).ToArray();
        Assert.Equal(["0"], await Search("123")); Assert.Equal(["2", "3"], await Search("%_")); Assert.Equal(["2"], await Search("straße"));
        Assert.Equal(["4"], await Search("CAFÉ")); Assert.Equal(["5"], await Search("cafe")); Assert.Empty(await Search("alternate"));
        Assert.Equal("0", Assert.Single((await s.ReadPageAsync(new(b.Context, artifact.ContentKey, ExactCreatureId: "0"), default)).Entries).Id);
        var first = await s.ReadPageAsync(new(b.Context, artifact.ContentKey, Limit: 2), default); Assert.Equal(["0", "2"], first.Entries.Select(e => e.Id)); Assert.NotNull(first.NextCursor);
        var second = await s.ReadPageAsync(new(b.Context, artifact.ContentKey, Limit: 2, After: first.NextCursor), default); Assert.Equal(["3", "4"], second.Entries.Select(e => e.Id));
        foreach (var changed in new[] { new CreaturesPageRequest(b.Context, artifact.ContentKey, Limit: 3, After: first.NextCursor), new(b.Context, artifact.ContentKey, "x", After: first.NextCursor),
            new(b.Context with { SourceRegion = "eu" }, artifact.ContentKey, Limit: 2, After: first.NextCursor) })
            Assert.Equal("CREATURES_CURSOR_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => s.ReadPageAsync(changed, default))).Code);
    }
    [Fact]
    public async Task ByteLimitedPagesConcatenateMaximalNamesWithoutGapsOrDuplicates()
    {
        await using var f = new Fixture(); var binding = await f.Binding(Enumerable.Range(0, 54).Select(i => new Row((uint)i, new string('\u0800', 16000))).ToArray());
        await using var source = new TableRowArtifacts(f.Objects); var artifact = await f.Projection.BuildAsync(binding, source, Progress, default);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default); var ids = new List<string>(); string? cursor = null; var pages = 0;
        do
        {
            var page = await session.ReadPageAsync(new(binding.Context, artifact.ContentKey, Limit: 50, After: cursor), default);
            Assert.InRange(page.Entries.Count, 1, 5); await CreaturesResponseCounter.RequireAsync(page, default); ids.AddRange(page.Entries.Select(e => e.Id)); cursor = page.NextCursor; pages++;
        } while (cursor is not null);
        Assert.True(pages > 10); Assert.Equal(Enumerable.Range(0, 54).Select(i => i.ToString(CultureInfo.InvariantCulture)), ids);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public async Task MalformedHeaderAndCanonicalDirectoryAreRejected(int poison)
    {
        await using var f = new Fixture(); var b = await f.Binding([new(1, "Name")]); await using var source = new TableRowArtifacts(f.Objects);
        var artifact = await f.Projection.BuildAsync(b, source, Progress, default); byte[] bytes;
        await using (var file = await f.Objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, default)) { bytes = new byte[file.Length]; await file.ReadExactlyAsync(bytes); }
        var directory = 64 + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        switch (poison)
        {
            case 0: bytes[0] ^= 1; break;
            case 1: bytes[8] = 2; break;
            case 2: bytes[40] = 1; break;
            case 3: bytes[20] = 31; break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 131073); break;
            case 5: bytes[directory + 5] = 1; break;
            case 6: bytes[directory + 4] = 3; break;
            case 7: BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan((int)directory + 8), 1); break;
            case 8: BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan((int)directory + 20), 1); break;
            case 9: bytes = bytes.Concat(new byte[1]).ToArray(); break;
            case 10: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1000001); break;
            case 11: BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), (64L << 20) + 1); break;
            case 12: BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), -1); break;
            case 13: BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((int)directory + 16), 262145); break;
            case 14: BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((int)directory + 28), 2049); break;
            case 15: BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan((int)directory + 8), -1); break;
        }
        var changed = await f.Republish(artifact, bytes); Assert.Equal("CREATURES_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Projection.OpenAsync(changed, Fixture.Time, default))).Code);
    }
    [Fact]
    public async Task DuplicateEmittedIdsFailWithoutLeakingStagingAndRetryWorks()
    {
        await using var f = new Fixture(); var b = await f.Binding([new(1, "Name"), new(1, "Name", 1)]); await using var source = new TableRowArtifacts(f.Objects);
        Assert.Equal("CREATURES_SOURCE_DUPLICATE", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Projection.BuildAsync(b, source, Progress, default))).Code); Assert.Empty(f.Staging);
        var good = await f.Binding([new(1, "Retry")]); await using var retry = new TableRowArtifacts(f.Objects); await f.Projection.BuildAsync(good, retry, Progress, default); Assert.Empty(f.Staging);
    }
    [Theory]
    [InlineData("rows")] [InlineData("names")] [InlineData("final")] [InlineData("summary")] [InlineData("metadata")]
    public async Task EveryConfiguredPublicationLimitFailsTypedAndCleansStaging(string limit)
    {
        await using var f = new Fixture(); var b = await f.Binding([new(1, "Name")]); var limits = limit switch
        { "rows" => new CreaturesLimits(MaxRows: 0), "names" => new(MaxNameBytes: 3), "final" => new(MaxArtifactBytes: 100), "summary" => new(MaxSummaryBytes: 1), _ => new(MaxMetadataBytes: 1) };
        await using var source = new TableRowArtifacts(f.Objects);
        Assert.Equal("CREATURES_PROJECTION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => new CreaturesProjection(f.Objects, new(), limits).BuildAsync(b, source, Progress, default))).Code); Assert.Empty(f.Staging);
    }
    [Fact]
    public async Task CancellationCleansSpoolsAndOriginalObservationTimesDoNotChangeBytesOrKey()
    {
        await using var f = new Fixture(); var b = await f.Binding([new(1, "Name")]); using var stop = new CancellationTokenSource(); await using var source = new TableRowArtifacts(f.Objects);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Projection.BuildAsync(b, source, _ => { stop.Cancel(); return Task.CompletedTask; }, stop.Token)); Assert.Empty(f.Staging);
        var changed = b with { Configurations = b.Configurations.Select(c => c with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-21T00:00:00Z" }).ToArray(), Source = b.Source with { PreparedAt = "2026-10-22T00:00:00Z" } };
        await using var firstReader = new TableRowArtifacts(f.Objects); await using var secondReader = new TableRowArtifacts(f.Objects);
        var first = await f.Projection.BuildAsync(b, firstReader, Progress, default); var second = await f.Projection.BuildAsync(changed, secondReader, Progress, default);
        Assert.Equal(first.ContentKey, second.ContentKey); Assert.Equal(first.Sha256, second.Sha256); Assert.Equal(b.Configurations, first.Binding.Configurations);
    }
    [Fact]
    public async Task MaximalUnicodeQueryHasShortCanonicalContinuationAndLiteralMatches()
    {
        await using var f = new Fixture(); var query = string.Concat(Enumerable.Repeat("𝄞", 128)); var b = await f.Binding([new(1, query), new(2, query)]);
        await using var reader = new TableRowArtifacts(f.Objects); var artifact = await f.Projection.BuildAsync(b, reader, Progress, default); await using var s = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var first = await s.ReadPageAsync(new(b.Context, artifact.ContentKey, query, Limit: 1), default); Assert.NotNull(first.NextCursor); Assert.InRange(first.NextCursor.Length, 1, 2048);
        var next = new CreaturesPageRequest(b.Context, artifact.ContentKey, query, Limit: 1, After: first.NextCursor); CreaturesService.Validate(next);
        Assert.Equal("2", Assert.Single((await s.ReadPageAsync(next, default)).Entries).Id);
    }
    [Theory]
    [InlineData("layout")] [InlineData("raw")] [InlineData("missing")] [InlineData("width")] [InlineData("unsigned")]
    public async Task HonestUnsupportedProfilesRemainUnsupported(string condition)
    {
        await using var f = new Fixture(); var b = await f.Binding([new(1, "Name")]);
        if (condition == "layout") b = b with { Source = b.Source with { LayoutHash = "12345678" }, Rows = b.Rows with { Header = b.Rows.Header with { LayoutHash = "12345678" } } };
        else if (condition is "width" or "unsigned") b = b with { Fields = b.Fields.Select((v, i) => i == 4 ? v with { Bits = condition == "width" ? 16 : 8, Unsigned = condition == "unsigned" } : v).ToArray() };
        else
        {
            var ordinal = b.Fields[0].ColumnIndex;
            var columns = b.Source.Columns.Select((v, i) => i != ordinal ? v : v with { Name = condition == "missing" ? "UnknownName" : v.Name,
                Interpretation = condition == "raw" ? new("authored", 32, false, 32, false, "authored") : null }).ToArray();
            var header = b.Rows.Header.Columns.Select((v, i) => i != ordinal ? v : v with { Name = columns[i].Name,
                Interpretation = condition == "raw" ? new("authored", 32, false, 32, false, "authored") : null }).ToArray();
            b = b with { Source = b.Source with { Columns = columns }, Rows = b.Rows with { Header = b.Rows.Header with { Columns = header } } };
        }
        Assert.Equal("CREATURES_SCHEMA_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => CreaturesPolicy.ValidateBinding(b)).Code);
    }
    [Fact]
    public async Task FullNamePoolUsesBoundedManagedLiveScratchAndWarmNoMatchScan()
    {
        await using var f = new Fixture(); var name = new string('n', 65536);
        var b = await f.Binding(Enumerable.Range(0, 1024).Select(i => new Row((uint)i, name)).ToArray());
        await using var source = new TableRowArtifacts(f.Objects); var baseline = GC.GetTotalMemory(true); var peak = baseline;
        var artifact = await f.Projection.BuildAsync(b, source, p =>
        { if (p.Completed < 10 || p.Completed % 128 == 0 || p.Completed == p.Total) peak = Math.Max(peak, GC.GetTotalMemory(true)); return Task.CompletedTask; }, default);
        output.WriteLine($"Creature64MiB names: sampled builder managed-live delta={peak - baseline:N0}; final={artifact.ByteLength:N0} bytes. Forced-GC samples exclude process peak and cumulative allocations.");
        Assert.InRange(peak - baseline, 0, 16 << 20);
        await using var session = await f.Projection.OpenAsync(artifact, Fixture.Time, default);
        var before = session.ReadCounts; var watch = System.Diagnostics.Stopwatch.StartNew();
        var queryBaseline = GC.GetTotalMemory(true); var page = await session.ReadPageAsync(new(b.Context, artifact.ContentKey, "absent"), default); watch.Stop();
        var after = session.ReadCounts;
        output.WriteLine($"Creature1024 maximal-name no-match: {watch.ElapsedMilliseconds}ms, directory reads={after.DirectoryReads - before.DirectoryReads}, name reads={after.NameReads - before.NameReads}, summary reads={after.SummaryReads - before.SummaryReads}, decoded names={after.DecodedNames - before.DecodedNames}.");
        Assert.Equal(1, after.DirectoryReads - before.DirectoryReads); Assert.Equal(64, after.NameReads - before.NameReads); Assert.Equal(0, after.SummaryReads - before.SummaryReads);
        before = session.ReadCounts; watch.Restart(); var exact = await session.ReadPageAsync(new(b.Context, artifact.ContentKey, ExactCreatureId: "1023"), default); watch.Stop(); after = session.ReadCounts;
        output.WriteLine($"Creature1024 exact-ID: {watch.ElapsedMilliseconds}ms, directory reads={after.DirectoryReads - before.DirectoryReads}, name reads={after.NameReads - before.NameReads}, summary reads={after.SummaryReads - before.SummaryReads}.");
        Assert.Equal("1023", Assert.Single(exact.Entries).Id); Assert.InRange(after.DirectoryReads - before.DirectoryReads, 1, 12); Assert.Equal(1, after.NameReads - before.NameReads); Assert.Equal(1, after.SummaryReads - before.SummaryReads);
        Assert.Empty(page.Entries); Assert.Null(page.NextCursor); Assert.InRange(GC.GetTotalMemory(true) - queryBaseline, -16 << 20, 8 << 20);
        var first = await session.ReadPageAsync(new(b.Context, artifact.ContentKey), default); Assert.Single(first.Entries); Assert.NotNull(first.NextCursor);
        await CreaturesResponseCounter.RequireAsync(first, default);
    }
    [Theory]
    [InlineData(2, false)] [InlineData(3, false)] [InlineData(2, true)] [InlineData(3, true)]
    public async Task EveryLaterStagingCreationFailureCleansEarlierSpools(int failAt, bool cancel)
    {
        await using var f = new Fixture(); var b = await f.Binding([new(1, "Name")]); using var stop = new CancellationTokenSource(); var calls = 0;
        var projection = new CreaturesProjection(f.Objects, new(), createStaging: ct =>
        { if (++calls != failAt) return f.Objects.CreateStagingFileAsync(ct); if (cancel) { stop.Cancel(); return Task.FromCanceled<FileStream>(stop.Token); } return Task.FromException<FileStream>(new IOException("authored staging failure")); });
        await using var reader = new TableRowArtifacts(f.Objects);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => projection.BuildAsync(b, reader, Progress, stop.Token));
        else await Assert.ThrowsAsync<IOException>(() => projection.BuildAsync(b, reader, Progress, default));
        Assert.Equal(failAt, calls); Assert.Empty(f.Staging); await using var retry = new TableRowArtifacts(f.Objects); await f.Projection.BuildAsync(b, retry, Progress, default);
    }
    [Fact]
    public async Task OriginalDescriptorCapPrecedesTimeNormalizationAndUtf16NameCapIsExplicit()
    {
        await using var f = new Fixture(); var b = await f.Binding([new(1, "Name")]);
        var oversizedOriginal = b with { Configurations = b.Configurations.Select(c => c with { RetrievedAt = new string('x', 131073) }).ToArray() };
        Assert.Equal("CREATURES_PROJECTION_LIMIT", Assert.Throws<DiscoveryException>(() => CreaturesPolicy.ValidateBinding(oversizedOriginal)).Code);
        // The shared original-row writer rejects this impossible source before the feature can admit it.
        Assert.Equal("TABLE_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Binding([new(1, new string('x', 65537))]))).Code); Assert.Empty(f.Staging);
    }
    [Fact]
    public async Task EmptySourceStillVerifiesOriginalRowArtifactBeforeCompactPublication()
    {
        await using var f = new Fixture(); var b = await f.Binding([]);
        File.Delete(Path.Combine(f.DirectoryPath, "objects-v1", b.Rows.Sha256[..2], b.Rows.Sha256[2..])); await using var reader = new TableRowArtifacts(f.Objects);
        await Assert.ThrowsAsync<DiscoveryException>(() => f.Projection.BuildAsync(b, reader, Progress, default)); Assert.Empty(f.Staging);
    }
    internal sealed record Row(uint Id, string Name, uint? CopySource = null, bool InvalidName = false);
    internal sealed class Fixture : IAsyncDisposable
    {
        internal const string Time = "2026-10-19T00:00:00Z";
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "vibeminer-creature-projection-" + Guid.NewGuid().ToString("N"));
        internal readonly VerifiedObjectStore Objects; internal readonly CreaturesProjection Projection;
        internal IEnumerable<string> Staging => Directory.EnumerateFiles(Path.Combine(DirectoryPath, "staging-v1"));
        internal Fixture() { Objects = new(DirectoryPath); Projection = new(Objects, new()); }
        internal async Task<CreaturesBinding> Binding(Row[] values, bool partial = false)
        {
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[CreaturesPolicy.Table]; const string version = "12.1.5.70448";
            var selected = Assert.Single(definition.Document.SelectBuild(version, CreaturesPolicy.Layout).Blocks);
            var columns = selected.Fields.SelectMany(f => Enumerable.Range(0, f.ArrayLength).Select(i => new Db2Column(f.ArrayLength == 1 ? f.Column.Name : f.Column.Name + "[" + i + "]",
                f.Column.Family == "int" ? "integer" : f.Column.Family == "float" ? "float" : "string", f.IsId))).ToArray();
            var binding = new TableArtifactBinding(new('b', 64), new('c', 64), definition.Sha256, BundledTableDefinitions.Revision, "enUS", TableDataStore.DecoderRevision, TableRowArtifacts.SchemaSelectionKey(definition.Document, version));
            var copies = values.Count(v => v.CopySource is not null); var physical = values.Length - copies;
            var header = new Wdc5TableHeader(1, CreaturesPolicy.Table.ToUpperInvariant(), CreaturesPolicy.Layout, physical + (partial ? 1 : 0), copies, 4, columns);
            var completion = new Wdc5Completion(header.DeclaredRecords, physical, copies, partial ? 1 : 0, partial ? 1 : 0, 0, partial ? "unencryptedSections" : "complete", 10, values.Count(v => v.InvalidName));
            await using var reader = new TableRowArtifacts(Objects); await using var writer = await reader.CreateWriterAsync(binding, default); await writer.HeaderAsync(header, default);
            foreach (var value in values)
            {
                var row = columns.Select(c => c.Kind == "string" ? "alternate" : "0").ToArray();
                void V(string name, string text) => row[Array.FindIndex(columns, c => c.Name == name)] = text;
                V("ID", value.Id.ToString(CultureInfo.InvariantCulture)); V("Name_lang", value.Name); V("Classification", "-128"); V("CreatureType", "255"); V("CreatureFamily", "65535"); V("StartAnimState", "127");
                for (var i = 0; i < 4; i++) { V("DisplayID[" + i + "]", new[] { "123", "123", "0", "-1" }[i]); V("DisplayProbability[" + i + "]", new[] { "-1", "0", "0.5", "1" }[i]); }
                await writer.RowAsync(new(value.Id, value.CopySource is not null, value.CopySource, row,
                    value.InvalidName ? [new(Array.FindIndex(columns, c => c.Name == "Name_lang"), "DB2_TEXT_ENCODING_INVALID", "ff")] : null), default);
            }
            var artifact = await writer.CompleteAsync(completion, default);
            var metadata = new MetadataReceipt("encoding", new('1', 32), new('2', 32), new('3', 64), "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/fixture", Time, Time, "fixture-v1");
            var source = new ContentReceipt(new('4', 32), new('5', 32), binding.SourceSha256, "10", "10", metadata.SourceUrl, Time, Time, "fixture-v1", new("loose", "0", "10", null));
            var association = new SelectedFileAssociation(CreaturesPolicy.FileDataId, "enUS", "enus-consistent-root-variants-v1", metadata, metadata with { Role = "root" }, [new("0", "0", source.ContentKey, "2", "0", "0", "0", "0", null)]);
            var snapshot = new TableDataSnapshot("wow", new('a', 64), "us", CreaturesPolicy.Table, "enUS", artifact.ContentKey, Time, CreaturesPolicy.Layout,
                binding.DefinitionSha256, binding.ProviderRevision, binding.DecoderRevision, source, association, columns.Select(c => new TableDataColumn(c.Name, c.Kind, c.IsId, null)).ToArray(),
                new(completion.Scope, CreaturesPolicy.N(completion.DeclaredRecords), CreaturesPolicy.N(physical), CreaturesPolicy.N(copies), CreaturesPolicy.N(completion.ExcludedEncryptedSections), CreaturesPolicy.N(completion.ExcludedPhysicalRecords),
                    CreaturesPolicy.N(copies), CreaturesPolicy.N(completion.ExcludedPhysicalRecords), "0"), CreaturesPolicy.N(values.Length), CreaturesPolicy.N(completion.TextIssueCount));
            ConfigurationReceipt[] configs = [new("buildConfig", new('1', 32), new('2', 64), 10, metadata.SourceUrl, Time, Time, 1), new("cdnConfig", new('3', 32), new('4', 64), 10, metadata.SourceUrl, Time, Time, 1)];
            return new(new("wow", snapshot.BuildIdentity, "us", "enUS"), version, new("1", "us", new('a', 64), 0, new('b', 64), 0), configs, snapshot, artifact,
                CreaturesPolicy.SelectFields(definition.Document, version, CreaturesPolicy.Layout, snapshot.Columns));
        }
        internal async Task<CreaturesArtifact> Republish(CreaturesArtifact artifact, byte[] bytes)
        { string path; await using (var file = await Objects.CreateStagingFileAsync(default)) { path = file.Name; await file.WriteAsync(bytes); } var sha = await Objects.PublishFileAsync(path, bytes.Length, default); return artifact with { Sha256 = sha, ByteLength = bytes.Length }; }
        public async ValueTask DisposeAsync() { await Objects.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
}
[CollectionDefinition("Creature projection builder", DisableParallelization = true)]
public sealed class CreatureProjectionBuilderCollection;
