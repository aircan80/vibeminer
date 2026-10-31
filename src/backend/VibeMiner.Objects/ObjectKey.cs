using System.Buffers.Binary;

namespace VibeMiner.Objects;

/// <summary>What a key names and how its bytes are verified before they reach a pack. 0 and 7..255 are spare.</summary>
public enum ObjectKeyType : byte
{
    /// <summary>A CDN config file: key = MD5 of the whole file.</summary>
    Config = 1,
    /// <summary>A native archive <c>.index</c>: key = MD5 of its 28-byte footer, offset width 4.</summary>
    ArchiveIndex = 2,
    /// <summary>The loose-file <c>file-index</c>: as an archive index, offset width 0.</summary>
    FileIndex = 3,
    /// <summary>A BLTE object: key = EKey (header MD5, or whole-object MD5 when headerless).</summary>
    Encoded = 4,
    /// <summary>The raw bytes of one version-service reply: key = MD5 of the bytes.</summary>
    HistoryDocument = 5,
    /// <summary>One dated observation naming a <see cref="HistoryDocument"/>: key = MD5 of the record.</summary>
    HistoryRecord = 6,
}

/// <summary>Where an object's bytes came from, as recorded in its entry header.</summary>
public enum ObjectOrigin : byte { Unknown = 0, Cdn = 1, SharedStore = 2 }

/// <summary>A full layer 1 identity: key type, namespace byte and the full 16-byte key.</summary>
/// <remarks>Namespace 0 is the product's CDN path (`config/` and `data/`, patch configs included). Other namespaces are reserved, for `patch/` objects and the product-config path, and refused by Put in format 1.</remarks>
public readonly struct ObjectKey : IEquatable<ObjectKey>, IComparable<ObjectKey>
{
    internal const int EncodedBytes = 18;
    private readonly ulong high, low;

    public ObjectKey(ObjectKeyType type, ReadOnlySpan<byte> key, byte keyNamespace = 0)
    {
        if (key.Length != 16) throw new ArgumentException("A layer 1 key is exactly 16 bytes.", nameof(key));
        Type = type; Namespace = keyNamespace;
        high = BinaryPrimitives.ReadUInt64BigEndian(key); low = BinaryPrimitives.ReadUInt64BigEndian(key[8..]);
    }

    public ObjectKeyType Type { get; }
    public byte Namespace { get; }

    public static ObjectKey Parse(ObjectKeyType type, string hex, byte keyNamespace = 0)
    {
        if (hex is null || hex.Length != 32 || !hex.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A layer 1 key is 32 hexadecimal digits.", nameof(hex));
        return new(type, Convert.FromHexString(hex), keyNamespace);
    }

    public byte[] KeyBytes() { var b = new byte[16]; WriteKey(b); return b; }
    public void WriteKey(Span<byte> destination)
    { BinaryPrimitives.WriteUInt64BigEndian(destination, high); BinaryPrimitives.WriteUInt64BigEndian(destination[8..], low); }
    public string KeyHex => Convert.ToHexStringLower(KeyBytes());

    /// <summary>The index's primary key: type, namespace, key (18 bytes), so rows sort by type first.</summary>
    internal byte[] ToIndexKey()
    {
        var b = new byte[EncodedBytes]; b[0] = (byte)Type; b[1] = Namespace; WriteKey(b.AsSpan(2)); return b;
    }
    internal static ObjectKey FromIndexKey(ReadOnlySpan<byte> b)
    {
        if (b.Length != EncodedBytes) throw new ArgumentException("An index key is 18 bytes.");
        return new((ObjectKeyType)b[0], b[2..], b[1]);
    }

    public bool Equals(ObjectKey other) => high == other.high && low == other.low && Type == other.Type && Namespace == other.Namespace;
    public override bool Equals(object? obj) => obj is ObjectKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(high, low, Type, Namespace);
    public int CompareTo(ObjectKey other)
    {
        var c = Type.CompareTo(other.Type); if (c != 0) return c;
        c = Namespace.CompareTo(other.Namespace); if (c != 0) return c;
        c = high.CompareTo(other.high); return c != 0 ? c : low.CompareTo(other.low);
    }
    public static bool operator ==(ObjectKey a, ObjectKey b) => a.Equals(b);
    public static bool operator !=(ObjectKey a, ObjectKey b) => !a.Equals(b);
    public static bool operator <(ObjectKey a, ObjectKey b) => a.CompareTo(b) < 0;
    public static bool operator >(ObjectKey a, ObjectKey b) => a.CompareTo(b) > 0;
    public static bool operator <=(ObjectKey a, ObjectKey b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ObjectKey a, ObjectKey b) => a.CompareTo(b) >= 0;
    public override string ToString() => $"{Type}/{Namespace}/{KeyHex}";
}
