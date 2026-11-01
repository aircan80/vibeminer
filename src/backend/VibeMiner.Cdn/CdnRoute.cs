using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace VibeMiner.Cdn;

/// <summary>A 16-byte CDN key (config MD5, index footer MD5 or EKey). Its meaning is the caller's business.</summary>
public readonly struct CdnKey : IEquatable<CdnKey>, IComparable<CdnKey>
{
    private readonly ulong high, low;
    public CdnKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16) throw new ArgumentException("A CDN key is exactly 16 bytes.", nameof(key));
        high = BinaryPrimitives.ReadUInt64BigEndian(key); low = BinaryPrimitives.ReadUInt64BigEndian(key[8..]);
    }
    public static CdnKey Parse(string hex)
    {
        if (hex is null || hex.Length != 32 || !hex.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A CDN key is 32 hexadecimal digits.", nameof(hex));
        return new(Convert.FromHexString(hex));
    }
    public byte[] ToBytes() { var b = new byte[16]; WriteTo(b); return b; }
    public void WriteTo(Span<byte> destination)
    { BinaryPrimitives.WriteUInt64BigEndian(destination, high); BinaryPrimitives.WriteUInt64BigEndian(destination[8..], low); }
    public string Hex => Convert.ToHexStringLower(ToBytes());
    public bool Equals(CdnKey other) => high == other.high && low == other.low;
    public override bool Equals(object? obj) => obj is CdnKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(high, low);
    public int CompareTo(CdnKey other) { var c = high.CompareTo(other.high); return c != 0 ? c : low.CompareTo(other.low); }
    public static bool operator ==(CdnKey a, CdnKey b) => a.Equals(b);
    public static bool operator !=(CdnKey a, CdnKey b) => !a.Equals(b);
    public static bool operator <(CdnKey a, CdnKey b) => a.CompareTo(b) < 0;
    public static bool operator >(CdnKey a, CdnKey b) => a.CompareTo(b) > 0;
    public static bool operator <=(CdnKey a, CdnKey b) => a.CompareTo(b) <= 0;
    public static bool operator >=(CdnKey a, CdnKey b) => a.CompareTo(b) >= 0;
    public override string ToString() => Hex;
}

/// <summary>Where a key lives under a product's CDN path.</summary>
public enum CdnObjectKind
{
    /// <summary><c>config/xx/yy/key</c>: build, CDN and patch configs.</summary>
    Config,
    /// <summary><c>data/xx/yy/key</c>: loose encoded objects and whole archives (ranged).</summary>
    Data,
    /// <summary><c>data/xx/yy/key.index</c>: archive indexes and the loose file-index.</summary>
    Index,
}

/// <summary>
/// One product's CDN route from a <c>cdns</c> row: the hosts in their advertised order and the product path
/// (<c>tpr/wow</c>). Only official hosts are admitted; the rest are dropped when the route is built.
/// </summary>
public sealed record CdnRoute
{
    private static readonly Regex PathPattern = new("^[a-z0-9_-]{1,32}(?:/[a-z0-9_-]{1,64}){1,3}$", RegexOptions.CultureInvariant);
    private static readonly Regex BlizzardCdn = new("^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant);

    public CdnRoute(IReadOnlyList<string> hosts, string path)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        if (path is null || !PathPattern.IsMatch(path)) throw new ArgumentException("A CDN path is like tpr/wow.", nameof(path));
        var admitted = hosts.Where(IsOfficialHost).Distinct(StringComparer.Ordinal).ToArray();
        if (admitted.Length == 0) throw new ArgumentException("A CDN route needs at least one official host.", nameof(hosts));
        if (admitted.Length > 16) throw new ArgumentException("A CDN route has at most 16 hosts.", nameof(hosts));
        Hosts = admitted; Path = path;
    }

    /// <summary>Official hosts in the order the <c>cdns</c> reply gave them.</summary>
    public IReadOnlyList<string> Hosts { get; }
    public string Path { get; }

    /// <summary>The same route with its hosts in another order (for example HTTP/2 hosts first for bulk work).</summary>
    public CdnRoute WithHosts(IReadOnlyList<string> hosts) => new(hosts, Path);

    /// <summary>Official CDN hosts only: <c>level3.blizzard.com</c>, <c>level3.ssl.blizzard.com</c> and <c>xx.cdn.blizzard.com</c>.</summary>
    public static bool IsOfficialHost(string host) =>
        host is "level3.blizzard.com" or "level3.ssl.blizzard.com" || host is not null && BlizzardCdn.IsMatch(host);

    public Uri UrlFor(string host, CdnObjectKind kind, CdnKey key)
    {
        if (!Hosts.Contains(host, StringComparer.Ordinal)) throw new ArgumentException("The host is not part of this route.", nameof(host));
        var hex = key.Hex;
        var folder = kind == CdnObjectKind.Config ? "config" : "data";
        var suffix = kind == CdnObjectKind.Index ? ".index" : "";
        return new Uri($"https://{host}/{Path}/{folder}/{hex[..2]}/{hex[2..4]}/{hex}{suffix}", UriKind.Absolute);
    }

    public bool Equals(CdnRoute? other) => other is not null && Path == other.Path && Hosts.SequenceEqual(other.Hosts);
    public override int GetHashCode() => HashCode.Combine(Path, Hosts.Count, Hosts[0]);
}
