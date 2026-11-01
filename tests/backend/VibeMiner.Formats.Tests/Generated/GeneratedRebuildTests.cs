using System.Buffers.Binary;
using System.Text;
using VibeMiner.Formats.Generated;
using VibeMiner.Generated.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;
using static VibeMiner.Formats.Tests.Generated.GeneratedFixtures;

namespace VibeMiner.Formats.Tests.Generated;

public sealed class GeneratedRebuildTests
{
    [Fact]
    public async Task Deleting_the_generated_store_rebuilds_with_zero_downloads()
    {
        var (bytes, definition) = ScalarFixture();
        var key = ObjectKey.Parse(ObjectKeyType.Encoded, "0123456789abcdef0123456789abcdef");
        // Layer 2 fake; the object is on the CDN, not yet stored. It stands in for layer 4's reads (#172).
        var objects = new InMemoryObjectSource().Add(key, bytes);
        var generated = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(generated);
        var source = Source(contentKey: "fedcba9876543210fedcba9876543210");

        var firstSource = new LayerTwoBytes(objects, key, bytes.Length);
        var first = await tables.GetOrBuildAsync(source, TactKeyStatus.None, token => Decode(firstSource, definition, source.ReaderLimits, token));
        Assert.Equal(1, firstSource.Served.Count(s => s == ServedFrom.Cdn));
        var expected = await All(first);
        Assert.Equal([1u, uint.MaxValue, 8u], expected.Select(r => r.Id));
        Assert.True(expected[2].IsCopy);

        generated.Clear();
        Assert.Null(await tables.OpenAsync(source, TactKeyStatus.None));
        var askedBefore = objects.Asked.Count;
        var rebuildSource = new LayerTwoBytes(objects, key, bytes.Length);
        var rebuilt = await tables.GetOrBuildAsync(source, TactKeyStatus.None, token => Decode(rebuildSource, definition, source.ReaderLimits, token));

        Assert.True(objects.Asked.Count > askedBefore);
        Assert.NotEmpty(rebuildSource.Served);
        Assert.All(rebuildSource.Served, s => Assert.Equal(ServedFrom.Store, s));
        SameRows(expected, await All(rebuilt));
        Assert.Equal(first.Header with { Columns = [] }, rebuilt.Header with { Columns = [] });
        Assert.Equal(first.Header.Columns, rebuilt.Header.Columns);
        Assert.Equal(first.Completion, rebuilt.Completion);
    }

    private static async Task<GeneratedTableContent> Decode(IDb2ByteSource bytes, DbdDocument definition, Db2Limits limits, CancellationToken token)
    {
        var table = await Db2Reader.OpenAsync(bytes, definition, "1.2.3.4", limits, token);
        string[] keys = [.. table.Sections.Where(s => s.Key != "0000000000000000").Select(s => s.Key)];
        return new GeneratedTableContent(table.Header, table.Sections, table.Completion, keys, table.ReadAllAsync(token));
    }

    /// <summary>A DB2 byte source over layer 2 ranged reads; records where each answer came from.</summary>
    private sealed class LayerTwoBytes(IObjectSource objects, ObjectKey key, long length) : IDb2ByteSource
    {
        private readonly List<ServedFrom> served = [];
        public long Length => length;
        public IReadOnlyList<Db2Hole> Holes => [];
        public IReadOnlyList<ServedFrom> Served { get { lock (served) return [.. served]; } }

        public async ValueTask ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            var result = await objects.GetRangeAsync(new ObjectRequest(key, length), offset, destination, null, FetchPriority.Interactive, token);
            if (!result.IsPresent) throw new IOException($"Layer 2 answered {result.State} ({result.Code}).");
            lock (served) served.Add(result.ServedFrom!.Value);
        }
    }

    // The scalar fixture of Db2ReaderTests: two physical rows and one copy, synthetic WDC5.
    private static (byte[] Bytes, DbdDocument Definition) ScalarFixture()
    {
        var records = new byte[34]; records[0] = 255; records[17] = 128;
        BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(1), 1.25f); BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(18), -0.5f);
        BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(5), 30); BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(22), 19);
        BinaryPrimitives.WriteUInt64LittleEndian(records.AsSpan(9), ulong.MaxValue); BinaryPrimitives.WriteUInt64LittleEndian(records.AsSpan(26), (1UL << 63) + 9);
        var definition = DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(
            "COLUMNS\nint ID\nint Small\nfloat Weight\nstring Name\nint Wide\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nSmall<8>\nWeight\nName\nWide<u64>\n"));
        var strings = Encoding.UTF8.GetBytes("\0café\0hello\0");
        (short Bits, ushort Position)[] fields = [(24, 0), (0, 1), (0, 5), (-32, 9)];
        (ushort Offset, ushort Width)[] storage = [(0, 8), (8, 32), (40, 32), (72, 64)];
        uint[] ids = [1, uint.MaxValue];
        var prefix = 204 + 40 + fields.Length * 28;

        var header = new byte[204]; "WDC5"u8.CopyTo(header); Put(header, 4, 5); Encoding.UTF8.GetBytes("authored").CopyTo(header, 8);
        Put(header, 136, 2); Put(header, 140, 4); Put(header, 144, 17); Put(header, 148, (uint)strings.Length);
        Put(header, 152, 0x11223344); Put(header, 156, 0x01020304); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(172), 4);
        Put(header, 176, 4); Put(header, 188, 4 * 24); Put(header, 200, 1);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(header);
        writer.Write(0UL); writer.Write((uint)prefix); writer.Write(2u); writer.Write((uint)strings.Length); writer.Write(0u);
        writer.Write((uint)ids.Length * 4); writer.Write(0u); writer.Write(0u); writer.Write(1u);
        foreach (var (bits, position) in fields) { writer.Write(bits); writer.Write(position); }
        foreach (var (offset, width) in storage) { writer.Write(offset); writer.Write(width); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); }
        writer.Write(records); writer.Write(strings);
        foreach (var id in ids) writer.Write(id);
        writer.Write(8u); writer.Write(1u);
        writer.Flush();
        return (output.ToArray(), definition);
    }

    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
}
