using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VibeMiner.Generated;

/// <summary>
/// The generated store (D210): what layers 4-6 prepare, kept in packs of layer 1's format in their own folder and rebuilt from the
/// object store when lost. Reset is to wipe it (not wired yet). Implementations: <c>GeneratedStore</c> and the in-memory fake in
/// <c>VibeMiner.Generated.Testing</c>; one contract suite runs against both. Calls may run concurrently.
/// </summary>
public interface IGeneratedStore
{
    /// <summary>Whether a committed object exists for the key. No bytes are read.</summary>
    ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken cancellationToken = default);

    /// <summary>A whole object, at most <see cref="GeneratedLimits.MaxObjectBytes"/>. Bytes that fail a read check read Corrupt, never as bytes.</summary>
    ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="destination"/>.Length bytes from <paramref name="offset"/>, checking only the blocks under the range. A range past the
    /// object's end throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a batch, one outcome per object in order. Every object is committed and visible when this returns. A key already stored is
    /// left as it is (<see cref="GeneratedPutOutcome.AlreadyPresent"/>). Store failures, such as a full disk, throw <see cref="GeneratedStoreException"/>.
    /// </summary>
    Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken cancellationToken = default);

    /// <summary>Removes objects; a key that isn't stored is ignored.</summary>
    Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken cancellationToken = default);
}

/// <summary>Fixed bounds of the generated store.</summary>
public static class GeneratedLimits
{
    /// <summary>The largest object: a Put and a whole read hold it in memory. Larger outputs are split by their writer (record files use segments).</summary>
    public const int MaxObjectBytes = 64 << 20;
    /// <summary>The most objects in one Put.</summary>
    public const int MaxBatch = 4096;
}

/// <summary>
/// A generated object's 16-byte key. The writer chooses it: the first 16 bytes of a SHA-256 over an identity
/// (<see cref="Hash(ReadOnlySpan{byte})"/>) or over the bytes themselves.
/// </summary>
public readonly struct GeneratedKey : IEquatable<GeneratedKey>, IComparable<GeneratedKey>
{
    public const int Bytes = 16;
    private readonly ulong high, low;

    public GeneratedKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != Bytes) throw new ArgumentException("A generated key is exactly 16 bytes.", nameof(key));
        high = BinaryPrimitives.ReadUInt64BigEndian(key); low = BinaryPrimitives.ReadUInt64BigEndian(key[8..]);
    }

    /// <summary>The first 16 bytes of the SHA-256 of <paramref name="identity"/>.</summary>
    public static GeneratedKey Hash(ReadOnlySpan<byte> identity)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        return new(digest[..Bytes]);
    }

    public static GeneratedKey Parse(string hex)
    {
        if (hex is null || hex.Length != 2 * Bytes || !hex.All(char.IsAsciiHexDigit)) throw new ArgumentException("A generated key is 32 hexadecimal digits.", nameof(hex));
        return new(Convert.FromHexString(hex));
    }

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, high); BinaryPrimitives.WriteUInt64BigEndian(destination[8..], low);
    }

    public byte[] ToBytes() { var b = new byte[Bytes]; WriteTo(b); return b; }
    public string Hex => Convert.ToHexStringLower(ToBytes());

    public bool Equals(GeneratedKey other) => high == other.high && low == other.low;
    public override bool Equals(object? obj) => obj is GeneratedKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(high, low);
    public int CompareTo(GeneratedKey other) { var c = high.CompareTo(other.high); return c != 0 ? c : low.CompareTo(other.low); }
    public static bool operator ==(GeneratedKey a, GeneratedKey b) => a.Equals(b);
    public static bool operator !=(GeneratedKey a, GeneratedKey b) => !a.Equals(b);
    public override string ToString() => Hex;
}

public enum GeneratedReadState
{
    /// <summary>The bytes passed every read check.</summary>
    Present,
    /// <summary>No committed object has the key.</summary>
    Absent,
    /// <summary>The object is indexed but its bytes are damaged or gone. No bytes were returned; delete it and generate it again.</summary>
    Corrupt,
}

/// <summary>A whole read. <see cref="Bytes"/> is set only for Present; <see cref="Length"/> is the object's length when Present.</summary>
public sealed record GeneratedRead(GeneratedReadState State, long Length = 0, byte[]? Bytes = null, string? Problem = null);

/// <summary>A range read. The destination holds the range only for Present; <see cref="Length"/> is the whole object's length when Present.</summary>
public sealed record GeneratedRangeRead(GeneratedReadState State, long Length = 0, string? Problem = null);

/// <summary>One object to store: 1 to <see cref="GeneratedLimits.MaxObjectBytes"/> bytes.</summary>
public sealed record GeneratedPut(GeneratedKey Key, ReadOnlyMemory<byte> Bytes);

public enum GeneratedPutOutcome { Stored, AlreadyPresent }

/// <summary>Named generated-store failures.</summary>
public static class GeneratedStoreCodes
{
    /// <summary>The volume is full. Nothing past the last commit was kept.</summary>
    public const string DiskFull = "GENERATED_STORE_DISK_FULL";
    /// <summary>A write failed; the store stopped writing. Reopening recovers.</summary>
    public const string WriteFailed = "GENERATED_STORE_WRITE_FAILED";
    /// <summary>The store was closed before the call finished.</summary>
    public const string Closed = "GENERATED_STORE_CLOSED";
    /// <summary>A call was over a fixed bound (<see cref="GeneratedLimits"/>), or an object was empty.</summary>
    public const string Limit = "GENERATED_STORE_LIMIT";
    /// <summary>Generated data read back damaged or not in the shape its writer gave it. Delete it and generate it again.</summary>
    public const string Corrupt = "GENERATED_CORRUPT";
    /// <summary>Another writer holds the folder. Transient: try again later; never a reason to wipe the folder.</summary>
    public const string Busy = "GENERATED_STORE_BUSY";
    /// <summary>
    /// The folder holds a store this engine can't use (another kind, a newer format, an index its writer can't rebuild). Deleting the
    /// folder and generating its contents again is the remedy.
    /// </summary>
    public const string Unusable = "GENERATED_STORE_UNUSABLE";
}

public sealed class GeneratedStoreException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
