using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Files.Names;
using Xunit;

namespace VibeMiner.Files.Tests.Names;

/// <summary>The listfile delta codec (#186) over authored listfiles only.</summary>
public sealed class ListfileDeltaTests
{
    private const int NoLimit = int.MaxValue;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<byte[]?> Encode(byte[] @base, byte[] target, int max = NoLimit) =>
        await ListfileDelta.EncodeAsync(new MemoryStream(@base), new MemoryStream(target), SHA256.HashData(@base),
            SHA256.HashData(target), target.Length, max);

    private static async Task<byte[]> EncodeSome(string @base, string target) =>
        (await Encode(Bytes(@base), Bytes(target)))!;

    /// <summary>Replays the chain over <paramref name="base"/>, fed in chunks the random source picks.</summary>
    private static byte[] Replay(byte[] @base, IReadOnlyList<byte[]> deltas, Random? random = null)
    {
        var output = new ArrayBufferWriter<byte>();
        using var chain = new ListfileDeltaChain(deltas.Select(d => (ReadOnlyMemory<byte>)d).ToArray(), output);
        for (var at = 0; at < @base.Length;)
        {
            var size = random is null ? @base.Length : random.Next(3) == 0 ? random.Next(1, 16) : random.Next(1, 8192);
            size = Math.Min(size, @base.Length - at);
            chain.Feed(@base.AsSpan(at, size));
            at += size;
        }
        chain.Complete();
        return output.WrittenSpan.ToArray();
    }

    private static ListfileDeltaException Refused(byte[] @base, byte[] delta) =>
        Assert.Throws<ListfileDeltaException>(() => Replay(@base, [delta]));

    private static int OpCount(byte[] delta)
    {
        var lead = BinaryPrimitives.ReadInt32LittleEndian(delta.AsSpan(80));
        return BinaryPrimitives.ReadInt32LittleEndian(delta.AsSpan(84 + lead));
    }

    /// <summary>A delta written by hand, so its ops can break the rules the encoder keeps.</summary>
    private static byte[] Handmade(byte[] @base, byte[] target, string lead, params (byte Kind, uint Step, string? Unit)[] ops)
    {
        var bytes = new List<byte>();
        var header = new byte[84];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ListfileDelta.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        SHA256.HashData(@base).CopyTo(header, 8);
        SHA256.HashData(target).CopyTo(header, 40);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(72), target.Length);
        var leadBytes = Bytes(lead);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(80), leadBytes.Length);
        bytes.AddRange(header);
        bytes.AddRange(leadBytes);
        bytes.AddRange(BitConverter.GetBytes((uint)ops.Length));
        foreach (var (kind, step, unit) in ops)
        {
            bytes.Add(kind);
            Var(bytes, step);
            if (unit is null) continue;
            var u = Bytes(unit);
            Var(bytes, (uint)u.Length);
            bytes.AddRange(u);
        }
        return bytes.ToArray();
    }

    private static void Var(List<byte> bytes, uint value)
    {
        while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
    }

    [Fact]
    public async Task ReplaysRenamesAddsAndRemoves()
    {
        var @base = Bytes("1;a/one.blp\n5;b/five.m2\n9;c/nine.wmo\n");
        var target = Bytes("0;z/zero.blp\n1;a/one.blp\n5;b/FIVE.m2\n12;d/twelve.wmo\n");
        var delta = (await Encode(@base, target))!;
        Assert.Equal(4, OpCount(delta)); // Set 0, Set 5, Remove 9, Set 12; 1 is unchanged.
        Assert.Equal(target, Replay(@base, [delta]));
    }

    [Fact]
    public async Task UnchangedReleaseIsAnEmptyDelta()
    {
        var csv = Bytes("1;a\n2;b");
        var delta = (await Encode(csv, csv))!;
        Assert.Equal(0, OpCount(delta));
        Assert.Equal(88, delta.Length);
        Assert.Equal(csv, Replay(csv, [delta]));
    }

    [Fact]
    public async Task LinesThatAreNotIdLinesBelongToTheUnitBeforeThem()
    {
        // "x1;b" and "12 c" aren't id lines; a changed blank line changes only the unit it follows.
        var @base = "#lead\n1;a\n\nx1;b\n12 c\n2;d\n3;e\n";
        var target = "#lead\n1;a\n\r\nx1;b\n12 c\n2;d\n3;e\n";
        var delta = await EncodeSome(@base, target);
        Assert.Equal(1, OpCount(delta));
        Assert.Equal(Bytes(target), Replay(Bytes(@base), [delta]));
    }

    [Fact]
    public async Task KeepsTheTargetsLeadAndDropsTheBases()
    {
        var delta = await EncodeSome("old lead\n\n1;a\n", "\r\nnew;lead\n1;a\n");
        Assert.Equal(0, OpCount(delta));
        Assert.Equal(Bytes("\r\nnew;lead\n1;a\n"), Replay(Bytes("old lead\n\n1;a\n"), [delta]));
    }

    [Fact]
    public async Task EmptyAndLeadOnlyFilesWork()
    {
        var add = await EncodeSome("", "1;a\n");
        var clear = await EncodeSome("1;a\n", "");
        var lead = await EncodeSome("1;a", "lead only");
        Assert.Equal(Bytes("1;a\n"), Replay([], [add]));
        Assert.Empty(Replay(Bytes("1;a\n"), [clear]));
        Assert.Equal(Bytes("lead only"), Replay(Bytes("1;a"), [lead]));
    }

    [Fact]
    public async Task StreamsThroughApplyAsync()
    {
        var @base = Bytes("1;a\n2;b\n");
        var delta = await EncodeSome("1;a\n2;b\n", "2;c\n4294967295;max\n");
        var output = new ArrayBufferWriter<byte>();
        await ListfileDelta.ApplyAsync(new MemoryStream(@base), [delta], output);
        Assert.Equal(Bytes("2;c\n4294967295;max\n"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task MaxSizeReturnsNull()
    {
        var @base = Bytes("1;a\n");
        var target = Bytes("1;a\n2;" + new string('n', 500) + "\n");
        var delta = (await Encode(@base, target))!;
        Assert.Equal(delta, await Encode(@base, target, delta.Length));
        Assert.Null(await Encode(@base, target, delta.Length - 1));
        Assert.Null(await Encode(@base, Bytes(new string('#', 200) + "\n"), 200));
    }

    [Theory]
    [InlineData("5;a\n3;b\n")]
    [InlineData("5;a\n5;b\n")]
    [InlineData("01;a\n")]
    [InlineData("4294967296;a\n")]
    [InlineData("12345678901;a\n")]
    public async Task RefusesIdsThatDontAscendOrArentCanonical(string csv)
    {
        await Assert.ThrowsAsync<ListfileFormatException>(() => Encode(Bytes("1;a\n"), Bytes(csv)));
        await Assert.ThrowsAsync<ListfileFormatException>(() => Encode(Bytes(csv), Bytes("1;a\n")));
    }

    [Fact]
    public async Task RefusesUnitsAndLeadsOver64KiB()
    {
        var limit = ListfileDelta.MaxUnitBytes;
        var fits = Bytes("1;" + new string('a', limit - 3) + "\n");
        Assert.Equal(fits, Replay([], [(await Encode([], fits))!]));
        await Assert.ThrowsAsync<ListfileFormatException>(() => Encode([], Bytes("1;" + new string('a', limit - 2) + "\n")));
        await Assert.ThrowsAsync<ListfileFormatException>(() => Encode([], Bytes("1;a\n" + new string('\n', limit))));
        await Assert.ThrowsAsync<ListfileFormatException>(() => Encode([], Bytes(new string('\n', limit + 1) + "1;a\n")));
    }

    [Fact]
    public async Task RefusesInputThatDoesntMatchItsHashOrSize()
    {
        var csv = Bytes("1;a\n");
        var e = await Assert.ThrowsAsync<ListfileDeltaException>(() => ListfileDelta.EncodeAsync(new MemoryStream(csv),
            new MemoryStream(csv), new byte[32], SHA256.HashData(csv), csv.Length, NoLimit));
        Assert.Equal(ListfileDeltaProblem.InputMismatch, e.Problem);
        e = await Assert.ThrowsAsync<ListfileDeltaException>(() => ListfileDelta.EncodeAsync(new MemoryStream(csv),
            new MemoryStream(csv), SHA256.HashData(csv), SHA256.HashData(csv), csv.Length + 1, NoLimit));
        Assert.Equal(ListfileDeltaProblem.InputMismatch, e.Problem);
    }

    [Fact]
    public async Task AWrongBaseIsABrokenChain()
    {
        var delta = await EncodeSome("1;a\n2;b\n", "1;a\n2;c\n");
        Assert.Equal(ListfileDeltaProblem.BrokenChain, Refused(Bytes("1;a\n2;x\n"), delta).Problem);
        Assert.Equal(ListfileDeltaProblem.BrokenChain, Refused(Bytes("1;a\n"), delta).Problem);
        Assert.Equal(ListfileDeltaProblem.BrokenChain, Refused(Bytes("2;b\n1;a\n"), delta).Problem); // doesn't even split
        // A base whose bytes give an op nothing to remove is still a broken chain, not a malformed delta.
        var remove = await EncodeSome("1;a\n2;b\n", "1;a\n");
        Assert.Equal(ListfileDeltaProblem.BrokenChain, Refused(Bytes("1;a\n3;b\n"), remove).Problem);
    }

    [Fact]
    public async Task LinksThatDontMeetAreABrokenChain()
    {
        var first = await EncodeSome("1;a\n", "1;b\n");
        var unrelated = await EncodeSome("1;c\n", "1;d\n");
        var e = Assert.Throws<ListfileDeltaException>(() => Replay(Bytes("1;a\n"), [first, unrelated]));
        Assert.Equal(ListfileDeltaProblem.BrokenChain, e.Problem);
    }

    [Fact]
    public async Task RefusesEveryTruncationAndTrailingBytes()
    {
        var @base = Bytes("1;a\n2;b\n");
        var delta = await EncodeSome("1;a\n2;b\n", "#\n0;z\n2;c\n7;d\n");
        for (var length = 0; length < delta.Length; length++)
            Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, delta[..length]).Problem);
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, [.. delta, 0]).Problem);
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, [.. delta, ListfileDelta.RemoveKind, 1]).Problem);
    }

    [Fact]
    public void RefusesOpsOutOfOrder()
    {
        var @base = Bytes("3;a\n5;b\n");
        var target = Bytes("3;x\n5;y\n");
        // A correct handmade delta replays, so the refusals below are about order alone.
        Assert.Equal(target, Replay(@base, [Handmade(@base, target, "", (1, 3, "3;x\n"), (1, 2, "5;y\n"))]));
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (1, 3, "3;x\n"), (1, 0, "3;y\n"))).Problem);
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (2, 5, null), (2, 0, null))).Problem);
        // A step that wraps past 4294967295 can't go back below the previous id.
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (1, 5, "5;y\n"), (1, uint.MaxValue - 1, "3;x\n"))).Problem);
    }

    [Fact]
    public void RefusesMalformedOps()
    {
        var @base = Bytes("3;a\n");
        var target = Bytes("3;x\n");
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (3, 3, null))).Problem);
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (1, 3, "4;x\n"))).Problem);
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (1, 3, "03;x\n"))).Problem);
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (1, 3, ""))).Problem);
        // Removing a FileDataID the right base doesn't have.
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (1, 3, "3;x\n"), (2, 1, null))).Problem);
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, Handmade(@base, target, "", (2, 1, null), (1, 2, "3;x\n"))).Problem);
    }

    [Fact]
    public void RefusesHeadersItDoesntRead()
    {
        var @base = Bytes("3;a\n");
        var good = Handmade(@base, @base, "");
        Assert.Equal(@base, Replay(@base, [good]));
        foreach (var (at, value) in new[] { (0, (byte)'X'), (4, (byte)2), (6, (byte)1), (79, (byte)0x80), (80, (byte)5) })
        {
            var bad = (byte[])good.Clone();
            bad[at] = value;
            Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, bad).Problem);
        }
        var count = (byte[])good.Clone();
        count[84] = 1;
        Assert.Equal(ListfileDeltaProblem.Malformed, Refused(@base, count).Problem);
    }

    [Fact]
    public void OutputWithoutTheTargetsHashIsWrongTarget()
    {
        var @base = Bytes("3;a\n");
        // Claims "3;b\n" but sets "3;c\n": same size, different hash.
        Assert.Equal(ListfileDeltaProblem.WrongTarget, Refused(@base, Handmade(@base, Bytes("3;b\n"), "", (1, 3, "3;c\n"))).Problem);
        Assert.Equal(ListfileDeltaProblem.WrongTarget, Refused(@base, Handmade(@base, Bytes("3;b"), "", (1, 3, "3;bb\n"))).Problem);
        Assert.Equal(ListfileDeltaProblem.WrongTarget, Refused(@base, Handmade(@base, Bytes("3;bbbbb\n"), "", (1, 3, "3;b\n"))).Problem);
    }

    [Fact]
    public async Task ARefusedApplierRefusesLaterCalls()
    {
        var delta = await EncodeSome("1;a\n", "1;b\n");
        using var applier = new ListfileDeltaApplier(delta, new ArrayBufferWriter<byte>());
        applier.Feed(Bytes("1;x\n"));
        Assert.Throws<ListfileDeltaException>(applier.Complete);
        Assert.Throws<InvalidOperationException>(() => applier.Feed(Bytes("1;a\n")));
        Assert.Throws<InvalidOperationException>(applier.Complete);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FuzzedChainOfSixteenReplaysEveryRelease(int seed)
    {
        var random = new Random(seed);
        var release = new Release();
        while (release.Lines.Count < 4000)
            release.Lines[(uint)random.NextInt64(1_000, 4_000_000_000)] = new Line(Name(random), false, "");
        var releases = new List<byte[]> { release.Render() };
        var deltas = new List<byte[]>();
        for (var r = 1; r <= 16; r++)
        {
            Mutate(release, random, r);
            var target = release.Render();
            var delta = (await Encode(releases[^1], target))!;
            Assert.True(delta.Length < target.Length / 4, $"Release {r}'s delta is {delta.Length} bytes for {target.Length}.");
            if (r == 9) Assert.Equal(0, OpCount(delta));
            releases.Add(target);
            deltas.Add(delta);
        }
        for (var level = 1; level <= 16; level++)
        {
            var replayed = Replay(releases[0], deltas.Take(level).ToList(), random);
            Assert.Equal(releases[level], replayed);
            Assert.Equal(SHA256.HashData(releases[level]), SHA256.HashData(replayed));
        }
    }

    private static void Mutate(Release release, Random random, int r)
    {
        var ids = release.Lines.Keys.ToList();
        if (r == 9) return; // An unchanged release.
        var changes = ids.Count / 100;
        for (var i = 0; i < changes; i++)
        {
            var id = ids[random.Next(ids.Count)];
            if (release.Lines.TryGetValue(id, out var line)) release.Lines[id] = line with { Name = Name(random) };
        }
        for (var i = 0; i < changes; i++) release.Lines.Remove(ids[random.Next(ids.Count)]);
        for (var i = 0; i < changes; i++) release.Lines.TryAdd((uint)random.NextInt64(0, 4_294_967_296), new Line(Name(random), false, ""));
        // Occasional formatting changes anywhere.
        foreach (var id in release.Lines.Keys.Where(_ => random.Next(500) == 0).ToList())
            release.Lines[id] = release.Lines[id] with { Crlf = !release.Lines[id].Crlf };
        var first = release.Lines.Keys.First();
        var last = release.Lines.Keys.Last();
        switch (r)
        {
            case 1:
                foreach (var id in release.Lines.Keys.Where(_ => random.Next(20) == 0).ToList()) release.Lines[id] = release.Lines[id] with { Crlf = true };
                break;
            case 2:
                foreach (var id in release.Lines.Keys.Where(_ => random.Next(50) == 0).ToList())
                    release.Lines[id] = release.Lines[id] with { Blanks = random.Next(3) switch { 0 => "\n", 1 => "\r\n", _ => "\n \n" } };
                break;
            case 3: release.FinalNewline = false; break;
            case 4: release.Lead = "#FileDataID;Name\n\n"; break;
            case 5: release.Lines.Remove(first); break;
            case 6: release.Lines.Remove(last); break;
            case 7: release.Lines[first - 1] = new Line(Name(random), false, ""); break;
            case 8: release.Lines[last + 1] = new Line(Name(random), true, ""); break;
            case 10: release.FinalNewline = true; release.Lead = ""; break;
            case 11: release.Lines[0] = new Line(Name(random), false, ""); release.Lines[uint.MaxValue] = new Line(Name(random), false, "\n"); break;
            case 12: release.Lines.Remove(0); release.Lines.Remove(uint.MaxValue); release.Lead = "\r\n"; break;
            case 13: release.FinalNewline = false; release.Lines.Remove(last); break;
            case 14: release.Lines[last + 7] = new Line(Name(random), false, "\n\n"); release.FinalNewline = true; break;
        }
    }

    private static string Name(Random random)
    {
        const string letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";
        var builder = new StringBuilder();
        var segments = random.Next(1, 6);
        for (var s = 0; s < segments; s++)
        {
            if (s > 0) builder.Append('/');
            var length = random.Next(1, 16);
            for (var i = 0; i < length; i++) builder.Append(letters[random.Next(letters.Length)]);
        }
        return builder.Append(random.Next(4) switch { 0 => ".blp", 1 => ".m2", 2 => ".ogg", _ => ".wmo" }).ToString();
    }

    private sealed record Line(string Name, bool Crlf, string Blanks);

    private sealed class Release
    {
        public SortedDictionary<uint, Line> Lines { get; } = [];
        public string Lead { get; set; } = "";
        public bool FinalNewline { get; set; } = true;

        public byte[] Render()
        {
            var builder = new StringBuilder(Lead);
            var last = Lines.Keys.Last();
            foreach (var (id, line) in Lines)
            {
                builder.Append(id).Append(';').Append(line.Name);
                if (id == last && !FinalNewline) break;
                builder.Append(line.Crlf ? "\r\n" : "\n").Append(line.Blanks);
            }
            return Bytes(builder.ToString());
        }
    }
}
