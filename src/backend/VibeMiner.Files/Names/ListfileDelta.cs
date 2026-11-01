using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VibeMiner.Files.Names;

// Listfile delta format 1 (#186): one wow-listfile release as changes to the release before it. All integers little-endian;
// "varint" is unsigned LEB128, at most 5 bytes, value at most 4294967295, shortest form not required.
//
//   0   4  magic "VMLD"
//   4   2  format version, 1
//   6   2  flags, 0
//   8  32  SHA-256 of the base release's CSV
//  40  32  SHA-256 of the target release's CSV
//  72   8  target size in bytes, i64, 0 or more
//  80   4  lead length L, at most MaxUnitBytes
//  84   L  the target's lead, stored whole
//  .    4  op count N
//  .    .  N ops, FileDataIDs strictly ascending:
//          u8 kind: 1 Set, 2 Remove
//          varint FileDataID minus the previous op's (the first op's from 0; so 0 only for a first op on FileDataID 0)
//          Set only: varint length 1..MaxUnitBytes, then the target's unit bytes, which start with "<id>;" in canonical decimal
//  The delta ends exactly after the last op; trailing bytes are refused.
//
// A CSV splits into a lead (bytes before the first id line) and units. An id line starts with decimal digits and a ';'; its
// digits must be a canonical FileDataID (no leading zero, at most 4294967295) and ascend strictly. A unit is the raw bytes from
// the start of one id line to the start of the next one, so it carries its terminator (LF or CRLF) and any lines after it
// that aren't id lines (blank ones); the last unit may lack a newline. Target = lead + for each FileDataID in order, the Set
// bytes, else the base unit unless Removed. Remove names a FileDataID the base has; Set adds one or replaces one whose bytes
// differ.
internal static class ListfileDelta
{
    public const uint Magic = 0x444C4D56; // "VMLD" read as a little-endian u32
    public const ushort Version = 1;
    public const int ShaBytes = 32;
    public const int FixedHeaderBytes = 84;
    /// <summary>A unit or lead may be at most this long; a real line is at most <see cref="ListfileParser.MaxLineBytes"/>.</summary>
    public const int MaxUnitBytes = 64 << 10;
    public const int MaxChainDeltas = 16;
    public const byte SetKind = 1;
    public const byte RemoveKind = 2;
    private const int ReadBytes = 64 << 10;

    /// <summary>
    /// The delta from <paramref name="base"/> to <paramref name="target"/>, or null once it would be over
    /// <paramref name="maxDeltaBytes"/> (both streams are then left part read). Memory is the delta plus a few 64 KiB buffers.
    /// Either CSV that doesn't split (an id line out of order or not canonical, a unit or lead over 64 KiB) is a
    /// <see cref="ListfileFormatException"/>. Streams whose bytes don't have the given SHA-256 or target size are an
    /// <see cref="ListfileDeltaProblem.InputMismatch"/>.
    /// </summary>
    public static async Task<byte[]?> EncodeAsync(Stream @base, Stream target, ReadOnlyMemory<byte> baseSha256,
        ReadOnlyMemory<byte> targetSha256, long targetSize, int maxDeltaBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(target);
        if (baseSha256.Length != ShaBytes) throw new ArgumentException("A SHA-256 is 32 bytes.", nameof(baseSha256));
        if (targetSha256.Length != ShaBytes) throw new ArgumentException("A SHA-256 is 32 bytes.", nameof(targetSha256));
        ArgumentOutOfRangeException.ThrowIfNegative(targetSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeltaBytes, FixedHeaderBytes + 4);

        using var b = new UnitSource(@base);
        using var t = new UnitSource(target);
        var bu = await b.NextAsync(cancellationToken).ConfigureAwait(false);
        var tu = await t.NextAsync(cancellationToken).ConfigureAwait(false);
        // The target's lead is complete once its first unit started or it ended.
        var lead = t.Lead;
        long size = FixedHeaderBytes + lead.Length + 4;
        if (size > maxDeltaBytes) return null;
        var ops = new ArrayBufferWriter<byte>();
        long count = 0;
        uint previous = 0;
        while (bu is not null || tu is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tu is null || bu is not null && bu.Value.Id < tu.Value.Id)
            {
                WriteOp(ops, RemoveKind, bu!.Value.Id - previous, default);
                previous = bu.Value.Id;
                count++;
                bu = await b.NextAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (bu is null || tu.Value.Id < bu.Value.Id)
            {
                WriteOp(ops, SetKind, tu.Value.Id - previous, tu.Value.Bytes);
                previous = tu.Value.Id;
                count++;
                tu = await t.NextAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!bu.Value.Bytes.AsSpan().SequenceEqual(tu.Value.Bytes))
                {
                    WriteOp(ops, SetKind, tu.Value.Id - previous, tu.Value.Bytes);
                    previous = tu.Value.Id;
                    count++;
                }
                bu = await b.NextAsync(cancellationToken).ConfigureAwait(false);
                tu = await t.NextAsync(cancellationToken).ConfigureAwait(false);
            }
            if (size + ops.WrittenCount > maxDeltaBytes || count > uint.MaxValue) return null;
        }
        if (!b.Hash().AsSpan().SequenceEqual(baseSha256.Span))
            throw new ListfileDeltaException(ListfileDeltaProblem.InputMismatch, "The base's bytes don't have the SHA-256 they were given.");
        if (!t.Hash().AsSpan().SequenceEqual(targetSha256.Span) || t.Length != targetSize)
            throw new ListfileDeltaException(ListfileDeltaProblem.InputMismatch, "The target's bytes don't have the SHA-256 or size they were given.");

        var delta = new byte[size + ops.WrittenCount];
        var span = delta.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 0);
        baseSha256.Span.CopyTo(span[8..]);
        targetSha256.Span.CopyTo(span[40..]);
        BinaryPrimitives.WriteInt64LittleEndian(span[72..], targetSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[80..], lead.Length);
        lead.CopyTo(span[84..]);
        var at = FixedHeaderBytes + lead.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)count);
        ops.WrittenSpan.CopyTo(span[(at + 4)..]);
        return delta;
    }

    /// <summary>The header of a delta whose whole structure is valid; anything else is <see cref="ListfileDeltaProblem.Malformed"/>.</summary>
    public static ListfileDeltaHeader ReadHeader(ReadOnlySpan<byte> delta) => Parse(delta, out _, out _, out _);

    /// <summary>
    /// Replays <paramref name="deltas"/> (oldest first, each against the one before) over the base streamed from
    /// <paramref name="base"/> and writes the last target to <paramref name="output"/>. What was written is the target only
    /// once this returns; any failure is a <see cref="ListfileDeltaException"/>.
    /// </summary>
    public static async Task ApplyAsync(Stream @base, IReadOnlyList<ReadOnlyMemory<byte>> deltas, IBufferWriter<byte> output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@base);
        using var chain = new ListfileDeltaChain(deltas, output);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBytes);
        try
        {
            int read;
            while ((read = await @base.ReadAsync(buffer.AsMemory(0, ReadBytes), cancellationToken).ConfigureAwait(false)) > 0)
                chain.Feed(buffer.AsSpan(0, read));
            chain.Complete();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static ListfileDeltaHeader Parse(ReadOnlySpan<byte> delta, out int leadOffset, out int leadLength, out int opsOffset)
    {
        if (delta.Length < FixedHeaderBytes + 4) throw Malformed("The delta is shorter than its header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(delta) != Magic) throw Malformed("The delta doesn't start with VMLD.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(delta[4..]);
        if (version != Version) throw Malformed($"The delta is format version {version}; VibeMiner reads version {Version}.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(delta[6..]) != 0) throw Malformed("The delta has flags set.");
        var targetSize = BinaryPrimitives.ReadInt64LittleEndian(delta[72..]);
        if (targetSize < 0) throw Malformed("The delta's target size is negative.");
        var lead = BinaryPrimitives.ReadUInt32LittleEndian(delta[80..]);
        if (lead > MaxUnitBytes) throw Malformed("The delta's lead is over 64 KiB.");
        if (FixedHeaderBytes + lead + 4 > (uint)delta.Length) throw Malformed("The delta ends inside its lead.");
        if (lead > targetSize) throw Malformed("The delta's lead is longer than its target.");
        leadOffset = FixedHeaderBytes;
        leadLength = (int)lead;
        opsOffset = FixedHeaderBytes + leadLength + 4;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(delta[(opsOffset - 4)..]);
        // An op is at least two bytes; check before trusting the count.
        if (count > (uint)(delta.Length - opsOffset) / 2) throw Malformed("The delta has fewer bytes than its op count needs.");
        var reader = new OpReader(opsOffset, count);
        while (reader.Next(delta, out _)) { }
        if (reader.Position != delta.Length) throw Malformed("The delta has bytes after its last op.");
        return new ListfileDeltaHeader(delta.Slice(8, ShaBytes).ToArray(), delta.Slice(40, ShaBytes).ToArray(), targetSize);
    }

    private static void WriteOp(ArrayBufferWriter<byte> ops, byte kind, uint idDelta, ReadOnlySpan<byte> bytes)
    {
        var span = ops.GetSpan(1 + 5 + 5 + bytes.Length);
        span[0] = kind;
        var at = 1 + WriteVar(span[1..], idDelta);
        if (kind == SetKind)
        {
            at += WriteVar(span[at..], (uint)bytes.Length);
            bytes.CopyTo(span[at..]);
            at += bytes.Length;
        }
        ops.Advance(at);
    }

    private static int WriteVar(Span<byte> span, uint value)
    {
        var at = 0;
        while (value >= 0x80) { span[at++] = (byte)(value | 0x80); value >>= 7; }
        span[at++] = (byte)value;
        return at;
    }

    internal static uint ReadVar(ReadOnlySpan<byte> source, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (position >= source.Length) throw Malformed("The delta ends inside a number.");
            var b = source[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (value > uint.MaxValue) throw Malformed("The delta has an oversized number.");
                return (uint)value;
            }
        }
        throw Malformed("The delta has an oversized number.");
    }

    internal static ListfileDeltaException Malformed(string detail) => new(ListfileDeltaProblem.Malformed, detail);

    /// <summary>The decimal FileDataID at the start of <paramref name="bytes"/> when they start with exactly it and ';'.</summary>
    internal static bool StartsWithId(ReadOnlySpan<byte> bytes, uint id)
    {
        Span<byte> digits = stackalloc byte[10];
        id.TryFormat(digits, out var length, provider: System.Globalization.CultureInfo.InvariantCulture);
        return bytes.Length > length && bytes[..length].SequenceEqual(digits[..length]) && bytes[length] == (byte)';';
    }

    internal readonly record struct Op(byte Kind, uint Id, int Offset, int Length);

    /// <summary>Reads ops one by one, checking each; <see cref="Parse"/> runs it once over the whole delta first.</summary>
    internal struct OpReader(int position, uint count)
    {
        private uint left = count;
        private long previous = -1;
        public int Position { get; private set; } = position;

        public bool Next(ReadOnlySpan<byte> delta, out Op op)
        {
            op = default;
            if (left == 0) return false;
            left--;
            var at = Position;
            if (at >= delta.Length) throw Malformed("The delta ends inside its ops.");
            var kind = delta[at++];
            if (kind is not (SetKind or RemoveKind)) throw Malformed($"The delta has an op of unknown kind {kind}.");
            var step = ReadVar(delta, ref at);
            var id = (previous < 0 ? 0 : previous) + step;
            if (previous >= 0 && step == 0) throw Malformed($"The delta's ops aren't in FileDataID order (FileDataID {id} twice).");
            if (id > uint.MaxValue) throw Malformed("The delta has a FileDataID above 4294967295.");
            int offset = 0, length = 0;
            if (kind == SetKind)
            {
                var size = ReadVar(delta, ref at);
                if (size is 0 or > MaxUnitBytes) throw Malformed($"The delta sets FileDataID {id} to {size} bytes; a unit is 1 to {MaxUnitBytes}.");
                if (size > delta.Length - at) throw Malformed("The delta ends inside a unit.");
                offset = at;
                length = (int)size;
                if (!StartsWithId(delta.Slice(offset, length), (uint)id)) throw Malformed($"The delta's unit for FileDataID {id} doesn't start with it.");
                at += length;
            }
            previous = id;
            Position = at;
            op = new Op(kind, (uint)id, offset, length);
            return true;
        }
    }

    /// <summary>One side of an encode: units of a stream, read as needed, with the stream's SHA-256 and length.</summary>
    private sealed class UnitSource(Stream stream) : ListfileUnitSplitter.ISink, IDisposable
    {
        private readonly byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBytes);
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly Queue<Unit> ready = new();
        private readonly ArrayBufferWriter<byte> lead = new();
        private readonly byte[] current = new byte[MaxUnitBytes];
        private ListfileUnitSplitter? splitter;
        private int currentLength;
        private long currentId = -1;
        private bool ended;

        public long Length { get; private set; }
        public byte[] Lead => lead.WrittenSpan.ToArray();

        public async ValueTask<Unit?> NextAsync(CancellationToken cancellationToken)
        {
            splitter ??= new ListfileUnitSplitter(this);
            while (ready.Count == 0 && !ended)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, ReadBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    splitter.Complete();
                    if (currentId >= 0) ready.Enqueue(new Unit((uint)currentId, current.AsSpan(0, currentLength).ToArray()));
                    ended = true;
                    break;
                }
                hash.AppendData(buffer, 0, read);
                Length += read;
                splitter.Feed(buffer.AsSpan(0, read));
            }
            return ready.TryDequeue(out var unit) ? unit : null;
        }

        public byte[] Hash() => hash.GetCurrentHash();

        void ListfileUnitSplitter.ISink.Start(uint id)
        {
            if (currentId >= 0) ready.Enqueue(new Unit((uint)currentId, current.AsSpan(0, currentLength).ToArray()));
            currentId = id;
            currentLength = 0;
        }

        void ListfileUnitSplitter.ISink.Bytes(ReadOnlySpan<byte> bytes)
        {
            if (currentId < 0) { lead.Write(bytes); return; }
            bytes.CopyTo(current.AsSpan(currentLength)); // The splitter keeps a unit within MaxUnitBytes.
            currentLength += bytes.Length;
        }

        public void Dispose()
        {
            hash.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private readonly record struct Unit(uint Id, byte[] Bytes);
}

/// <summary>A delta's identity: the release it was made against and the one it makes.</summary>
internal sealed record ListfileDeltaHeader(byte[] BaseSha256, byte[] TargetSha256, long TargetSize);

/// <summary>
/// Splits a listfile CSV, fed in chunks of any size, into its lead and units (see <see cref="ListfileDelta"/>). Each id line
/// calls <see cref="ISink.Start"/> before its bytes; every byte of the CSV goes to <see cref="ISink.Bytes"/> once, in order,
/// in pieces of whole lines. An id line out of order or not canonical, a line, unit or lead over
/// <see cref="ListfileDelta.MaxUnitBytes"/> is a <see cref="ListfileFormatException"/>, after which the splitter is failed.
/// </summary>
internal sealed class ListfileUnitSplitter(ListfileUnitSplitter.ISink sink)
{
    internal interface ISink
    {
        void Start(uint id);
        void Bytes(ReadOnlySpan<byte> bytes);
    }

    private readonly byte[] carry = new byte[ListfileDelta.MaxUnitBytes];
    private int carried;
    private long previous = -1;
    private long line;
    private int unitBytes;
    private bool failed, complete;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        Usable();
        try
        {
            while (chunk.Length > 0)
            {
                var newline = chunk.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    if (carried + chunk.Length > carry.Length) throw TooLong();
                    chunk.CopyTo(carry.AsSpan(carried));
                    carried += chunk.Length;
                    return;
                }
                var end = newline + 1;
                if (carried > 0)
                {
                    if (carried + end > carry.Length) throw TooLong();
                    chunk[..end].CopyTo(carry.AsSpan(carried));
                    var length = carried + end;
                    carried = 0;
                    Line(carry.AsSpan(0, length));
                }
                else
                {
                    if (end > carry.Length) throw TooLong();
                    Line(chunk[..end]);
                }
                chunk = chunk[end..];
            }
        }
        catch (ListfileFormatException) { failed = true; throw; }
    }

    /// <summary>Hands on a last line without a newline.</summary>
    public void Complete()
    {
        Usable();
        complete = true;
        try
        {
            if (carried > 0) { var length = carried; carried = 0; Line(carry.AsSpan(0, length)); }
        }
        catch (ListfileFormatException) { failed = true; throw; }
    }

    private void Usable()
    {
        if (failed) throw new InvalidOperationException("The splitter refused its input.");
        if (complete) throw new InvalidOperationException("The splitter is complete.");
    }

    private ListfileFormatException TooLong() => new($"Line {line + 1} is longer than {ListfileDelta.MaxUnitBytes} bytes.");

    private void Line(ReadOnlySpan<byte> text)
    {
        line++;
        var digits = 0;
        while (digits < text.Length && text[digits] is >= (byte)'0' and <= (byte)'9') digits++;
        if (digits > 0 && digits < text.Length && text[digits] == (byte)';')
        {
            if (digits > 10) throw new ListfileFormatException($"Line {line} has a FileDataID of more than 10 digits.");
            if (digits > 1 && text[0] == (byte)'0') throw new ListfileFormatException($"Line {line} has a FileDataID with a leading zero.");
            long id = 0;
            for (var i = 0; i < digits; i++) id = id * 10 + (text[i] - '0');
            if (id > uint.MaxValue) throw new ListfileFormatException($"Line {line} has a FileDataID above 4294967295.");
            if (id <= previous) throw new ListfileFormatException($"Line {line} isn’t in FileDataID order (FileDataID {id} after {previous}).");
            previous = id;
            unitBytes = 0;
            sink.Start((uint)id);
        }
        unitBytes += text.Length;
        if (unitBytes > ListfileDelta.MaxUnitBytes)
            throw new ListfileFormatException(previous < 0 ? $"The lead before line {line} is over 64 KiB." : $"FileDataID {previous}'s lines are over 64 KiB.");
        sink.Bytes(text);
    }
}

/// <summary>
/// Replays one delta over its base, streaming: <see cref="Feed"/> base chunks of any size, then <see cref="Complete"/>.
/// The target goes to the output as it is made, so what was written is the target only once <see cref="Complete"/>
/// returns. A malformed delta is refused by the constructor. A base that isn't the delta's (by SHA-256) is
/// <see cref="ListfileDeltaProblem.BrokenChain"/>, ops that don't fit the right base are
/// <see cref="ListfileDeltaProblem.Malformed"/>, and output without the delta's size and SHA-256 is
/// <see cref="ListfileDeltaProblem.WrongTarget"/>, all reported by <see cref="Complete"/>: until the base's hash is known
/// a problem can't be told apart from a wrong base, so the applier then stops writing and only hashes what it is fed.
/// After a refusal every call throws <see cref="InvalidOperationException"/>.
/// </summary>
internal sealed class ListfileDeltaApplier : ListfileUnitSplitter.ISink, IDisposable
{
    private readonly ReadOnlyMemory<byte> delta;
    private readonly IBufferWriter<byte> output;
    private readonly ListfileDeltaHeader header;
    private readonly int leadOffset, leadLength;
    private readonly ListfileUnitSplitter splitter;
    private readonly IncrementalHash baseHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash outputHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private ListfileDelta.OpReader reader;
    private ListfileDelta.Op next;
    private bool hasNext, started, copying, done, failed;
    private (ListfileDeltaProblem Problem, string Detail)? problem;

    public ListfileDeltaApplier(ReadOnlyMemory<byte> delta, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        this.delta = delta;
        this.output = output;
        header = ListfileDelta.Parse(delta.Span, out leadOffset, out leadLength, out var opsOffset);
        reader = new ListfileDelta.OpReader(opsOffset, BinaryPrimitives.ReadUInt32LittleEndian(delta.Span[(opsOffset - 4)..]));
        Advance();
        splitter = new ListfileUnitSplitter(this);
    }

    public ListfileDeltaHeader Header => header;
    public long Written { get; private set; }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        Usable();
        try
        {
            Start();
            baseHash.AppendData(chunk);
            if (problem is not null) return;
            try { splitter.Feed(chunk); }
            catch (ListfileFormatException e) { Problem(ListfileDeltaProblem.BrokenChain, $"The base doesn't split: {e.Message}"); }
        }
        catch { failed = true; throw; }
    }

    public void Complete()
    {
        Usable();
        done = true;
        try
        {
            Start();
            if (problem is null)
            {
                try { splitter.Complete(); }
                catch (ListfileFormatException e) { Problem(ListfileDeltaProblem.BrokenChain, $"The base doesn't split: {e.Message}"); }
            }
            while (problem is null && hasNext) Insert();
            if (!baseHash.GetCurrentHash().AsSpan().SequenceEqual(header.BaseSha256))
                throw new ListfileDeltaException(ListfileDeltaProblem.BrokenChain, "The base isn't the release this delta was made against.");
            if (problem is { } p) throw new ListfileDeltaException(p.Problem, p.Detail);
            if (Written != header.TargetSize || !outputHash.GetCurrentHash().AsSpan().SequenceEqual(header.TargetSha256))
                throw new ListfileDeltaException(ListfileDeltaProblem.WrongTarget, "The replayed release doesn't have the delta's size and SHA-256.");
        }
        catch { failed = true; throw; }
    }

    private void Usable()
    {
        if (failed) throw new InvalidOperationException("The delta was refused.");
        if (done) throw new InvalidOperationException("The delta is complete.");
    }

    private void Start()
    {
        if (started) return;
        started = true;
        Emit(delta.Span.Slice(leadOffset, leadLength));
    }

    private void Advance() => hasNext = reader.Next(delta.Span, out next);

    // An op on a FileDataID the base doesn't have: a Set adds it, a Remove makes no sense.
    private void Insert()
    {
        if (next.Kind == ListfileDelta.RemoveKind) { Problem(ListfileDeltaProblem.Malformed, $"The delta removes FileDataID {next.Id}, which the base doesn't have."); return; }
        Emit(delta.Span.Slice(next.Offset, next.Length));
        Advance();
    }

    void ListfileUnitSplitter.ISink.Start(uint id)
    {
        while (problem is null && hasNext && next.Id < id) Insert();
        if (problem is not null) return;
        if (hasNext && next.Id == id)
        {
            if (next.Kind == ListfileDelta.SetKind) Emit(delta.Span.Slice(next.Offset, next.Length));
            copying = false;
            Advance();
        }
        else copying = true;
    }

    // Bytes before the first id line are the base's lead, replaced by the delta's.
    void ListfileUnitSplitter.ISink.Bytes(ReadOnlySpan<byte> bytes)
    {
        if (copying && problem is null) Emit(bytes);
    }

    private void Emit(ReadOnlySpan<byte> bytes)
    {
        if (problem is not null || bytes.IsEmpty) return;
        if (Written + bytes.Length > header.TargetSize)
        {
            Problem(ListfileDeltaProblem.WrongTarget, "The replayed release is longer than the delta's target.");
            return;
        }
        outputHash.AppendData(bytes);
        Written += bytes.Length;
        output.Write(bytes);
    }

    private void Problem(ListfileDeltaProblem kind, string detail)
    {
        problem ??= (kind, detail);
        copying = false;
    }

    public void Dispose()
    {
        baseHash.Dispose();
        outputHash.Dispose();
    }
}

/// <summary>
/// Up to <see cref="ListfileDelta.MaxChainDeltas"/> deltas replayed level on level: each level's output feeds the next
/// as it is made, so no level holds a whole release. Links whose hashes don't meet are refused up front as
/// <see cref="ListfileDeltaProblem.BrokenChain"/>. <see cref="Complete"/> completes the levels oldest first.
/// </summary>
internal sealed class ListfileDeltaChain : IDisposable
{
    private readonly ListfileDeltaApplier[] levels;

    public ListfileDeltaChain(IReadOnlyList<ReadOnlyMemory<byte>> deltas, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        ArgumentNullException.ThrowIfNull(output);
        if (deltas.Count is 0 or > ListfileDelta.MaxChainDeltas)
            throw new ArgumentOutOfRangeException(nameof(deltas), $"A chain has 1 to {ListfileDelta.MaxChainDeltas} deltas.");
        var headers = new ListfileDeltaHeader[deltas.Count];
        for (var i = 0; i < deltas.Count; i++)
        {
            headers[i] = ListfileDelta.ReadHeader(deltas[i].Span);
            if (i > 0 && !headers[i].BaseSha256.AsSpan().SequenceEqual(headers[i - 1].TargetSha256))
                throw new ListfileDeltaException(ListfileDeltaProblem.BrokenChain, $"Delta {i + 1} wasn't made against delta {i}'s release.");
        }
        levels = new ListfileDeltaApplier[deltas.Count];
        IBufferWriter<byte> sink = output;
        for (var i = deltas.Count - 1; i >= 0; i--)
        {
            levels[i] = new ListfileDeltaApplier(deltas[i], sink);
            sink = new FeedingWriter(levels[i]);
        }
    }

    public void Feed(ReadOnlySpan<byte> chunk) => levels[0].Feed(chunk);

    public void Complete()
    {
        foreach (var level in levels) level.Complete();
    }

    public void Dispose()
    {
        foreach (var level in levels) level.Dispose();
    }

    /// <summary>A writer whose every Advance feeds the next level.</summary>
    private sealed class FeedingWriter(ListfileDeltaApplier next) : IBufferWriter<byte>
    {
        private byte[] buffer = new byte[16 << 10];

        public void Advance(int count) => next.Feed(buffer.AsSpan(0, count));

        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return buffer; }

        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return buffer; }

        private void Ensure(int sizeHint)
        {
            if (sizeHint > buffer.Length) buffer = new byte[Math.Max(sizeHint, buffer.Length * 2)];
        }
    }
}
