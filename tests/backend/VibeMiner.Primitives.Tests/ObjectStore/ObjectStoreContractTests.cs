using System.Security.Cryptography;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.Primitives.Tests;

/// <summary>
/// The layer 1 contract (#153), one suite run against the real store and against <see cref="InMemoryObjectStore"/>, so tests of the
/// layers above can use the fake without it drifting. It also pins <see cref="IObjectVerifier"/> to what Put answers.
/// </summary>
public abstract class ObjectStoreContractTests : IAsyncLifetime
{
    protected IObjectReader Reader { get; private set; } = null!;
    protected IObjectWriter Writer { get; private set; } = null!;
    protected IHistoryLog History { get; private set; } = null!;
    protected abstract Task<(IObjectReader reader, IObjectWriter writer, IHistoryLog history)> CreateAsync();
    public async Task InitializeAsync() => (Reader, Writer, History) = await CreateAsync();
    public abstract Task DisposeAsync();

    [Fact]
    public async Task EveryKeyTypeRoundTripsWithItsLengthAndOrigin()
    {
        var objects = new[]
        {
            Synthetic.Config(1), Synthetic.Index(2, 500), Synthetic.Index(3, 300, offsetWidth: 0), Synthetic.Index(4, 0),
            Synthetic.Headerless(5, 9), Synthetic.Headerless(6, 70_000), Synthetic.Chunked(7, 10, 65_536, 65_537, 1), Synthetic.Config(8) with { Origin = ObjectOrigin.SharedStore },
        };
        var all = objects.Concat(HistoryRecordFormat.Objects(Synthetic.Observation(1))).ToList();
        Assert.All(await Writer.PutAsync(all), r => Assert.Equal(PutOutcome.Stored, r.Outcome));
        foreach (var o in all)
        {
            Assert.True(await Reader.HasAsync(o.Key));
            var read = await Reader.ReadAsync(o.Key);
            Assert.Equal(ReadState.Present, read.State);
            Assert.Equal(o.Bytes.ToArray(), read.Bytes);
            Assert.Equal((o.Key, (long)o.Bytes.Length, o.Origin), (read.Info!.Key, read.Info.Length, read.Info.Origin));
        }
    }

    public static TheoryData<string> Refusals() => new() { "key", "length", "chunk", "short-chunks", "page", "width", "headerless-key", "type", "namespace", "declared-huge", "table-format", "orphan-record" };

    private static (PutObject o, string code) Refusal(string name)
    {
        var config = Synthetic.Config(1); var chunked = Synthetic.Chunked(2, 100, 200); var index = Synthetic.Index(3, 400);
        var fileIndex = Synthetic.Index(4, 50, offsetWidth: 0); var headerless = Synthetic.Headerless(5);
        byte[] Flip(PutObject o, int at) { var b = o.Bytes.ToArray(); b[at] ^= 1; return b; }
        return name switch
        {
            "key" => (config with { Key = ObjectKey.Parse(ObjectKeyType.Config, new string('a', 32)) }, ObjectRefusalCodes.KeyMismatch),
            "length" => (config with { DeclaredLength = config.DeclaredLength + 1 }, ObjectRefusalCodes.SizeMismatch),
            "chunk" => (chunked with { Bytes = Flip(chunked, chunked.Bytes.Length - 5) }, ObjectRefusalCodes.BlteInvalid),
            "short-chunks" => (chunked with { Bytes = chunked.Bytes[..^1], DeclaredLength = chunked.DeclaredLength - 1 }, ObjectRefusalCodes.SizeMismatch),
            "page" => (index with { Bytes = Flip(index, 10) }, ObjectRefusalCodes.IndexInvalid),
            "width" => (fileIndex with { Key = new ObjectKey(ObjectKeyType.ArchiveIndex, fileIndex.Key.KeyBytes()) }, ObjectRefusalCodes.IndexUnsupported),
            "headerless-key" => (headerless with { Key = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData([1])) }, ObjectRefusalCodes.KeyMismatch),
            "type" => (headerless with { Key = new ObjectKey((ObjectKeyType)7, headerless.Key.KeyBytes()) }, ObjectRefusalCodes.KeyTypeUnsupported),
            "namespace" => (headerless with { Key = new ObjectKey(ObjectKeyType.Encoded, headerless.Key.KeyBytes(), 1) }, ObjectRefusalCodes.NamespaceUnsupported),
            "declared-huge" => (new PutObject(headerless.Key, (64L << 20) + 1, headerless.Bytes), ObjectRefusalCodes.SizeMismatch),
            "table-format" => (Rekeyed(chunked, 0x10), ObjectRefusalCodes.BlteUnsupported),
            "orphan-record" => (HistoryRecordFormat.Objects(Synthetic.Observation(7))[1], ObjectRefusalCodes.HistoryDocumentMissing),
            _ => throw new ArgumentException(name),
        };
    }

    private static PutObject Rekeyed(PutObject chunked, byte format)
    {
        var b = chunked.Bytes.ToArray(); b[8] = format;
        var header = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
        return chunked with { Key = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData(b.AsSpan(0, header))), Bytes = b };
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task PutRefusesWithTheVerifiersCodeAndStoresNothing(string name)
    {
        var (o, code) = Refusal(name);
        var r = Assert.Single(await Writer.PutAsync([o]));
        Assert.Equal((PutOutcome.Refused, code), (r.Outcome, r.Code));
        Assert.False(await Reader.HasAsync(o.Key));
        // IObjectVerifier gives Put's answer; a missing history document is the store's state, not the bytes'.
        var check = ObjectVerification.Default.Check(o.Key, o.Bytes.Span, o.DeclaredLength);
        Assert.Equal(code == ObjectRefusalCodes.HistoryDocumentMissing ? null : code, check?.Code);
        // Fed in pieces, the incremental check agrees.
        using var pieces = ObjectVerification.Default.Begin(o.Key, o.DeclaredLength);
        ObjectRefusal? seen = null;
        for (var at = 0; at < o.Bytes.Length && seen is null; at += 37) seen = pieces.Append(o.Bytes.Span.Slice(at, Math.Min(37, o.Bytes.Length - at)));
        seen ??= pieces.Complete();
        Assert.Equal(check?.Code, seen?.Code);
    }

    [Fact]
    public async Task TheVerifierAcceptsWhatPutStores()
    {
        foreach (var o in new[] { Synthetic.Config(1), Synthetic.Index(2, 300), Synthetic.Headerless(3, 1000), Synthetic.Chunked(4, 500, 70_000) })
        {
            Assert.Null(ObjectVerification.Default.Check(o.Key, o.Bytes.Span, o.DeclaredLength));
            using var pieces = ObjectVerification.Default.Begin(o.Key, o.DeclaredLength);
            Assert.Null(pieces.Append(o.Bytes.Span[..(o.Bytes.Length / 2)]));
            Assert.Null(pieces.Append(o.Bytes.Span[(o.Bytes.Length / 2)..]));
            Assert.Null(pieces.Complete());
            Assert.Equal(PutOutcome.Stored, Assert.Single(await Writer.PutAsync([o])).Outcome);
        }
    }

    [Fact]
    public async Task ADuplicateIsAlreadyPresentInOneBatchOrTwo()
    {
        var o = Synthetic.Headerless(1, 5000);
        Assert.Equal([PutOutcome.Stored, PutOutcome.AlreadyPresent], (await Writer.PutAsync([o, o])).Select(r => r.Outcome));
        Assert.Equal(PutOutcome.AlreadyPresent, Assert.Single(await Writer.PutAsync([o])).Outcome);
    }

    [Fact]
    public async Task AnUnknownKeyIsAbsentEverywhere()
    {
        var key = Synthetic.Headerless(42).Key;
        Assert.False(await Reader.HasAsync(key));
        Assert.Equal(ReadState.Absent, (await Reader.ReadAsync(key)).State);
        Assert.Equal(ReadState.Absent, (await Reader.ReadRangeAsync(key, 0, new byte[4])).State);
    }

    [Fact]
    public async Task RangesReadExactBytesAndARangePastTheEndIsTheCallersError()
    {
        var o = Synthetic.Chunked(1, Enumerable.Repeat(100_000, 3).ToArray());
        await Writer.PutAsync([o]);
        foreach (var (at, n) in new[] { (0L, 1), (65_535L, 2), (100L, 150_000), (o.Bytes.Length - 7L, 7), (10L, 0) })
        {
            var buffer = new byte[n];
            Assert.Equal(ReadState.Present, (await Reader.ReadRangeAsync(o.Key, at, buffer)).State);
            Assert.Equal(o.Bytes.Span.Slice((int)at, n).ToArray(), buffer);
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Reader.ReadRangeAsync(o.Key, o.Bytes.Length - 1, new byte[2]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Reader.ReadRangeAsync(o.Key, -1, new byte[2]));
    }

    [Fact]
    public async Task DeleteRemovesRefusesHistoryAndAllowsAPutAgain()
    {
        var o = Synthetic.Headerless(1); var history = HistoryRecordFormat.Objects(Synthetic.Observation(1));
        await Writer.PutAsync([o, .. history]);
        var deleted = await Writer.DeleteAsync([o.Key, Synthetic.Headerless(2).Key, history[0].Key, history[1].Key]);
        Assert.Equal([DeleteOutcome.Deleted, DeleteOutcome.Absent, DeleteOutcome.Refused, DeleteOutcome.Refused], deleted.Select(d => d.Outcome));
        Assert.Equal(ObjectRefusalCodes.DeleteRefused, deleted[2].Code);
        Assert.False(await Reader.HasAsync(o.Key));
        Assert.True(await Reader.HasAsync(history[1].Key));
        Assert.Equal(PutOutcome.Stored, Assert.Single(await Writer.PutAsync([o])).Outcome);
    }

    [Fact]
    public async Task StagingResumesFromTheLastProvenChunkAndRefusesABadOne() =>
        await ObjectStagingContract.AssertChunkResumeAsync(Reader, Writer);

    [Fact]
    public async Task TheHistoryLogReadsBackFilteredOldestFirstAndPaged()
    {
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var observations = new[]
        {
            Synthetic.Observation(1, "wow", "us", at: at.AddMinutes(3)), Synthetic.Observation(2, "wow", "eu", at: at.AddMinutes(1)),
            Synthetic.Observation(3, "wow", "us", HistoryKind.Cdns, at.AddMinutes(2)), Synthetic.Observation(4, "wowt", "us", at: at.AddMinutes(1)),
            Synthetic.Observation(5, "", "us", HistoryKind.Summary, at.AddMinutes(4)) with { Url = "https://us.version.battle.net/v2/summary" },
        };
        foreach (var o in observations) Assert.All(await History.AppendHistoryAsync(o), r => Assert.Equal(PutOutcome.Stored, r.Outcome));
        // The same observation again is one entry; a Put of the two objects is the same as an append.
        Assert.All(await History.AppendHistoryAsync(observations[1]), r => Assert.Equal(PutOutcome.AlreadyPresent, r.Outcome));
        var later = observations[0] with { ObservedAt = at.AddMinutes(9) };
        Assert.Equal([PutOutcome.AlreadyPresent, PutOutcome.Stored], (await Writer.PutAsync(HistoryRecordFormat.Objects(later))).Select(r => r.Outcome));

        var all = await History.ReadHistoryAsync(new HistoryQuery());
        Assert.Equal(6, all.Count);
        Assert.Equal(all.OrderBy(e => e.ObservedAt).ThenBy(e => Convert.ToHexString(e.Record.KeyBytes())).Select(e => e.Record), all.Select(e => e.Record));
        var first = all.Single(e => e.ObservedAt == at.AddMinutes(3));
        var expected = HistoryRecordFormat.Objects(observations[0]);
        Assert.Equal((HistoryKind.Versions, "wow", "us", observations[0].Url, expected[0].Key, expected[1].Key),
            (first.Kind, first.Product, first.Region, first.Url, first.Document, first.Record));
        Assert.Equal(observations[0].Reply.ToArray(), (await Reader.ReadAsync(first.Document)).Bytes);

        Assert.Equal(2, (await History.ReadHistoryAsync(new HistoryQuery("wow", "us", HistoryKind.Versions))).Count);
        Assert.Single(await History.ReadHistoryAsync(new HistoryQuery(Region: "eu")));
        var summary = Assert.Single(await History.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Summary)));
        Assert.Equal(("", "us", observations[4].Url), (summary.Product, summary.Region, summary.Url));
        var page1 = await History.ReadHistoryAsync(new HistoryQuery(Limit: 4));
        var page2 = await History.ReadHistoryAsync(new HistoryQuery(After: page1[^1], Limit: 4));
        Assert.Equal((4, 2), (page1.Count, page2.Count));
        Assert.Equal(all.Select(e => e.Record), page1.Concat(page2).Select(e => e.Record));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await History.ReadHistoryAsync(new HistoryQuery(Limit: 0)));
    }

    [Fact]
    public async Task BatchBoundsAndCancellationBeforeStartAreThrown()
    {
        await Assert.ThrowsAsync<ObjectStoreException>(() => Writer.PutAsync(Enumerable.Range(0, 4097).Select(i => Synthetic.Headerless(i, 9)).ToList()));
        using var canceled = new CancellationTokenSource(); await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Writer.PutAsync([Synthetic.Headerless(1)], ct: canceled.Token));
        Assert.False(await Reader.HasAsync(Synthetic.Headerless(1).Key));
    }
}

public sealed class RealObjectStoreContractTests : ObjectStoreContractTests
{
    private readonly TempDirectory directory = new();
    private ObjectStore? store;
    protected override async Task<(IObjectReader reader, IObjectWriter writer, IHistoryLog history)> CreateAsync()
    {
        store = await ObjectStore.OpenAsync(ObjectStoreTestKit.StoreDir(directory), ObjectStoreTestKit.Quick);
        return (store, store, store);
    }
    public override async Task DisposeAsync() { if (store is not null) await store.DisposeAsync(); directory.Dispose(); }
}

public sealed class InMemoryObjectStoreContractTests : ObjectStoreContractTests
{
    protected override Task<(IObjectReader reader, IObjectWriter writer, IHistoryLog history)> CreateAsync()
    {
        var fake = new InMemoryObjectStore();
        return Task.FromResult<(IObjectReader, IObjectWriter, IHistoryLog)>((fake, fake, fake));
    }
    public override Task DisposeAsync() => Task.CompletedTask;
}
