using Xunit;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>The generated store kind: type 7 only, its own pack and index marker, everything else the Cdn store does.</summary>
public sealed class GeneratedKindTests
{
    private static ObjectStoreOptions Generated => Quick with { Kind = ObjectStoreKind.Generated };
    private static string GenDir(TempDirectory t) => Path.Combine(t.Path, "generated-v1");

    /// <summary>Synthetic prepared data under a key the writer chose (here a hash of the seed, not of the bytes).</summary>
    private static PutObject Gen(int seed, int length = 1000)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        var key = SHA256.HashData(BitConverter.GetBytes(seed)).AsSpan(0, 16);
        return new PutObject(new ObjectKey(ObjectKeyType.Generated, key), length, bytes);
    }

    private static uint PackKind(string pack) => BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(pack).AsSpan(12, 4));

    private static long ApplicationId(string dir)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "objects.sqlite3"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA application_id";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task AGeneratedStorePutsReadsRangesAndDeletesTypeSeven()
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        var objects = new[] { Gen(1, 1), Gen(2, 5000), Gen(3, 200_000) };
        await using var store = await ObjectStore.OpenAsync(dir, Generated);
        Assert.Equal(ObjectStoreKind.Generated, store.Kind);
        Assert.All(await store.PutAsync(objects, commitNow: true), r => Assert.Equal(PutOutcome.Stored, r.Outcome));
        Assert.Equal(PutOutcome.AlreadyPresent, (await store.PutAsync([objects[1]])).Single().Outcome);
        foreach (var o in objects)
        {
            Assert.True(await store.HasAsync(o.Key));
            var read = await store.ReadAsync(o.Key);
            Assert.Equal(ReadState.Present, read.State);
            Assert.Equal(o.Bytes.ToArray(), read.Bytes);
        }
        var range = new byte[10_000];
        var r = await store.ReadRangeAsync(objects[2].Key, 60_000, range);  // crosses the 64 KiB block boundary
        Assert.Equal(ReadState.Present, r.State);
        Assert.Equal(objects[2].Bytes.Slice(60_000, 10_000).ToArray(), range);
        Assert.Equal(3, (await store.StatsAsync()).Objects);

        var deleted = await store.DeleteAsync([objects[0].Key, Gen(9).Key]);
        Assert.Equal([DeleteOutcome.Deleted, DeleteOutcome.Absent], deleted.Select(d => d.Outcome));
        Assert.Equal(ReadState.Absent, (await store.ReadAsync(objects[0].Key)).State);
        Assert.Equal(ReadState.Absent, (await store.ReadRangeAsync(objects[0].Key, 0, new byte[1])).State);
        Assert.Equal(0, (await store.VerifyAsync(full: true)).CorruptCount);
    }

    [Fact]
    public async Task AGeneratedStoreRefusesTypesOneToSixAndTheHistoryLog()
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        await using var store = await ObjectStore.OpenAsync(dir, Generated);
        PutObject[] cdn = [Synthetic.Config(1), Synthetic.Index(2, 50), Synthetic.Index(3, 50, offsetWidth: 0), Synthetic.Headerless(4), .. HistoryRecordFormat.Objects(Synthetic.Observation(5))];
        Assert.Equal(6, cdn.Select(o => o.Key.Type).Distinct().Count());
        var results = await store.PutAsync(cdn, commitNow: true);
        Assert.All(results, r => Assert.Equal((PutOutcome.Refused, ObjectRefusalCodes.KeyTypeUnsupported), (r.Outcome, r.Code)));
        Assert.All(await store.AppendHistoryAsync(Synthetic.Observation(6)), r => Assert.Equal(ObjectRefusalCodes.KeyTypeUnsupported, r.Code));
        Assert.Empty(await store.ReadHistoryAsync(new HistoryQuery(Limit: 10)));
        Assert.Equal(0, (await store.StatsAsync()).Objects);
        Assert.Empty(PackFiles(dir));
    }

    [Fact]
    public async Task AGeneratedStoreChecksOnlyTheLength()
    {
        using var t = new TempDirectory();
        await using var store = await ObjectStore.OpenAsync(GenDir(t), Generated);
        var o = Gen(1, 100);
        var cases = new (PutObject o, string code)[]
        {
            (o with { DeclaredLength = 0, Bytes = Array.Empty<byte>() }, ObjectRefusalCodes.TooLarge),
            (o with { DeclaredLength = 101 }, ObjectRefusalCodes.SizeMismatch),
            (o with { DeclaredLength = (64L << 20) + 1 }, ObjectRefusalCodes.TooLarge),
            (o with { Key = new ObjectKey(ObjectKeyType.Generated, o.Key.KeyBytes(), 1) }, ObjectRefusalCodes.NamespaceUnsupported),
        };
        foreach (var (bad, code) in cases) Assert.Equal(code, (await store.PutAsync([bad])).Single().Code);
        // Any key goes: the writer chooses it, so bytes that hash to something else are still stored.
        Assert.Equal(PutOutcome.Stored, (await store.PutAsync([o with { Key = new ObjectKey(ObjectKeyType.Generated, new byte[16]) }])).Single().Outcome);
    }

    [Fact]
    public async Task TypeSevenIsRefusedByACdnStoreAndTheDefaultVerifier()
    {
        using var t = new TempDirectory();
        var o = Gen(1);
        var refusal = ObjectVerification.Default.Check(o.Key, o.Bytes.Span, o.DeclaredLength);
        Assert.Equal(ObjectRefusalCodes.KeyTypeUnsupported, refusal?.Code);
        using (var check = ObjectVerification.Default.Begin(o.Key, o.DeclaredLength))
        {
            Assert.Equal(ObjectRefusalCodes.KeyTypeUnsupported, check.Append(o.Bytes.Span)?.Code);
            Assert.Equal(ObjectRefusalCodes.KeyTypeUnsupported, check.Complete()?.Code);
        }
        await using var store = await ObjectStore.OpenAsync(StoreDir(t), Quick);
        Assert.Equal(ObjectStoreKind.Cdn, store.Kind);
        var put = (await store.PutAsync([o])).Single();
        Assert.Equal((PutOutcome.Refused, ObjectRefusalCodes.KeyTypeUnsupported), (put.Outcome, put.Code));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(o.Key, 100_000_000));
    }

    [Fact]
    public async Task AGeneratedStoreHasNoStaging()
    {
        using var t = new TempDirectory();
        await using var store = await ObjectStore.OpenAsync(GenDir(t), Generated);
        await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(Gen(1).Key, 100_000_000));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(Synthetic.Headerless(1).Key, 100_000_000));
    }

    [Fact]
    public async Task PackHeadersAndTheIndexRecordTheKindAndCdnStaysAsItWas()
    {
        using var t = new TempDirectory();
        var cdn = StoreDir(t); var gen = GenDir(t);
        await using (var store = await ObjectStore.OpenAsync(cdn, Quick)) await store.PutAsync([Synthetic.Headerless(1)]);
        await using (var store = await ObjectStore.OpenAsync(gen, Generated)) await store.PutAsync([Gen(1)]);
        Assert.Equal(0u, PackKind(PackFiles(cdn).Single()));
        Assert.Equal(1u, PackKind(PackFiles(gen).Single()));
        Assert.Equal(0, ApplicationId(cdn));
        Assert.Equal(1, ApplicationId(gen));
    }

    [Fact]
    public async Task AGeneratedStoreReopensRecoversItsTailAndRebuildsItsIndex()
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        var committed = Gen(1, 3000);
        await using (var store = await ObjectStore.OpenAsync(dir, Generated)) await store.PutAsync([committed]);
        var pack = PackFiles(dir).Single();
        var seq = NextSeq(dir);
        var fresh = Gen(2, 4000);
        Append(pack, RawEntry(fresh.Key, fresh.Bytes.Span, seq));                 // whole, never committed
        var torn = RawEntry(Gen(3).Key, Gen(3).Bytes.Span, seq + 1);
        Append(pack, torn.AsSpan(0, torn.Length - 5));                            // torn
        await using (var store = await ObjectStore.OpenAsync(dir, Generated))
        {
            Assert.Equal(1, store.OpenReport.RecoveredEntries);
            Assert.Equal(torn.Length - 5, store.OpenReport.TruncatedBytes);
            Assert.Equal(committed.Bytes.ToArray(), (await store.ReadAsync(committed.Key)).Bytes);
            Assert.Equal(fresh.Bytes.ToArray(), (await store.ReadAsync(fresh.Key)).Bytes);
            Assert.False(await store.HasAsync(Gen(3).Key));
        }
        var rows = Rows(dir);
        Assert.Equal(2, rows.Count);
        await using (var store = await ObjectStore.OpenAsync(dir, Generated))
        {
            Assert.False(store.OpenReport.IndexRebuilt);
            var scan = await store.ScanAsync();
            Assert.Equal(2, scan.Live);
        }
        Assert.Equal(rows, Rows(dir));
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Generated))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            Assert.Equal(fresh.Bytes.ToArray(), (await store.ReadAsync(fresh.Key)).Bytes);
        }
        Assert.Equal(rows, Rows(dir));
        Assert.Equal(1, ApplicationId(dir));                                       // the rebuilt index keeps the kind
        await using var reader = await ObjectStore.OpenReadOnlyAsync(dir, Generated);
        Assert.Equal(committed.Bytes.ToArray(), (await reader.ReadAsync(committed.Key)).Bytes);
    }

    [Fact]
    public async Task AnEntryOfTheOtherKindReadsAsNewerAndIsNeverTruncated()
    {
        using var t = new TempDirectory();
        var cdn = StoreDir(t); var gen = GenDir(t);
        await using (var store = await ObjectStore.OpenAsync(cdn, Quick)) await store.PutAsync([Synthetic.Headerless(1)]);
        await using (var store = await ObjectStore.OpenAsync(gen, Generated)) await store.PutAsync([Gen(1)]);
        Append(PackFiles(cdn).Single(), RawEntry(Gen(2).Key, Gen(2).Bytes.Span, NextSeq(cdn)));
        Append(PackFiles(gen).Single(), RawEntry(Synthetic.Headerless(2).Key, Synthetic.Headerless(2).Bytes.Span, NextSeq(gen)));
        foreach (var (dir, options) in new[] { (cdn, Quick), (gen, Generated) })
        {
            var before = HashFolder(dir);
            Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, options))).Code);
            Assert.Equal(before, HashFolder(dir));
        }
    }

    [Fact]
    public async Task AGeneratedStoreCompactsAndKeepsItsKind()
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        var objects = Enumerable.Range(0, 40).Select(i => Gen(i, 20_000)).ToList();
        await using var store = await ObjectStore.OpenAsync(dir, Generated with { MaxPackBytes = 200_000 });
        await store.PutAsync(objects);
        var size = PackFiles(dir).Sum(p => new FileInfo(p).Length);
        await store.DeleteAsync(objects.Where((_, i) => i % 2 == 0).Select(o => o.Key).ToList());
        var report = await store.CompactAsync(new CompactOptions(0.2, 100));
        Assert.True(report.PacksCompacted >= 3);
        Assert.True(PackFiles(dir).Sum(p => new FileInfo(p).Length) < size * 0.75);
        Assert.All(PackFiles(dir), p => Assert.Equal(1u, PackKind(p)));
        for (var i = 0; i < objects.Count; i++)
        {
            var r = await store.ReadAsync(objects[i].Key);
            Assert.Equal(i % 2 == 0 ? ReadState.Absent : ReadState.Present, r.State);
            if (i % 2 == 1) Assert.Equal(objects[i].Bytes.ToArray(), r.Bytes);
        }
        Assert.Equal(0, (await store.VerifyAsync(full: true)).CorruptCount);
        var rows = Rows(dir);
        await store.ScanAsync();
        Assert.Equal(rows, Rows(dir));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task OpeningAFolderOfTheOtherKindIsRefusedAndChangesNothing(bool generatedOnDisk, bool withPacks)
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        var disk = generatedOnDisk ? Generated : Quick;
        var asked = generatedOnDisk ? Quick : Generated;
        await using (var store = await ObjectStore.OpenAsync(dir, disk))
            if (withPacks) await store.PutAsync([generatedOnDisk ? Gen(1) : Synthetic.Headerless(1)]);
        Assert.Equal(withPacks ? 1 : 0, PackFiles(dir).Length);  // without packs only the index tells the kind
        var before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.KindMismatch, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, asked))).Code);
        Assert.Equal(ObjectStoreCodes.KindMismatch, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(dir, asked))).Code);
        Assert.Equal(before, HashFolder(dir));
        await using var again = await ObjectStore.OpenAsync(dir, disk);
        Assert.Empty(again.OpenReport.Problems);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnIndexNeverCommittedInAGeneratedFolderIsRecreatedAsGenerated(bool emptyFile)
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        Directory.CreateDirectory(dir);
        var index = Path.Combine(dir, "objects.sqlite3");
        if (emptyFile) File.WriteAllBytes(index, []);
        else
            using (var c = new SqliteConnection($"Data Source={index};Pooling=False"))
            { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE half(x)"; cmd.ExecuteNonQuery(); }  // user_version 0, application_id 0
        await using (var store = await ObjectStore.OpenAsync(dir, Generated))
            Assert.Equal(PutOutcome.Stored, (await store.PutAsync([Gen(1)])).Single().Outcome);
        Assert.Equal(1, ApplicationId(dir));
        await using var again = await ObjectStore.OpenAsync(dir, Generated);
        Assert.True(await again.HasAsync(Gen(1).Key));
    }

    [Fact]
    public async Task APackOfTheOtherKindIsRefusedEvenWhenTheIndexIsGone()
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Generated)) await store.PutAsync([Gen(1)]);
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        var before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.KindMismatch, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, Quick))).Code);
        Assert.Equal(before, HashFolder(dir));
    }

    [Fact]
    public async Task AnUnknownKindInAPackHeaderIsNewer()
    {
        using var t = new TempDirectory();
        var dir = GenDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Generated)) await store.PutAsync([Gen(1)]);
        var pack = PackFiles(dir).Single();
        var header = File.ReadAllBytes(pack).AsSpan(0, 64).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), PackFormat.Crc32C(header.AsSpan(0, 60)));
        Write(pack, 0, header);
        var before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, Generated))).Code);
        Assert.Equal(before, HashFolder(dir));
    }
}
