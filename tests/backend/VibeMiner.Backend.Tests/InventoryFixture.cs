using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Backend.Tests;

// Entirely authored synthetic metadata and payloads. Official-looking URLs are keys for an
// injected HTTP handler; this fixture performs no I/O and includes no downloaded game bytes.
internal sealed class InventoryFixture
{
    internal sealed record FixtureObject(string Role, string ContentKey, string EncodingKey,
        byte[] Decoded, byte[] Encoded);

    public byte[] BuildConfig { get; private init; } = [];
    public byte[] CdnConfig { get; private init; } = [];
    public string BuildKey { get; private init; } = "";
    public string CdnKey { get; private init; } = "";
    public string BuildIdentity { get; private init; } = "";
    public DiscoveryAttempt Discovery { get; private init; } = null!;
    public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FixtureObject> Metadata { get; } = new(StringComparer.Ordinal);
    public FixtureObject PayloadA { get; private init; } = null!;
    public FixtureObject PayloadB { get; private init; } = null!;
    /// <summary>With a ROOT gap: a file ROOT lists and ENCODING maps, but the download manifest leaves out (D066).</summary>
    public FixtureObject? RootOnly { get; private init; }
    /// <summary>With a ROOT gap: a ROOT content key ENCODING does not map, so it cannot be resolved to an encoded object.</summary>
    public string? Unresolvable { get; private init; }
    public long ExpectedEncodedBytes => PayloadA.Encoded.LongLength + PayloadB.Encoded.LongLength;
    public int ExpectedDownloadObjectCount => 2;
    public int ExpectedLogicalContentCount => 2;
    public int ExpectedRootMembershipCount => 3;
    public int ExpectedInstallMembershipCount => 1;
    public int ExpectedDownloadMembershipCount => 2;
    public int ExpectedDownloadSizeMembershipCount => 2;
    public int ExpectedMembershipCount => 8;
    public int ExpectedFileDataIdCount => 2;
    public long ExpectedDecodedBytes => PayloadA.Decoded.LongLength + PayloadB.Decoded.LongLength;
    public long ExpectedMetadataEncodedBytes => Metadata.Values.Sum(value => value.Encoded.LongLength);

    // Ordered matching batches stop early after a 40 ms slice, so a "Resolving" frame carries
    // whatever committed count first crossed the 16,384-entry step, not a fixed multiple of 1024.
    // Tests that cancel from that frame read the count back instead of matching a literal.
    public static long? ResolvingCount(string progress, string family)
    {
        var prefix = $"Resolving {family} inventory · ";
        if (!progress.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = progress.IndexOf(" of ", prefix.Length, StringComparison.Ordinal);
        if (end < 0) return null;
        return long.Parse(progress.AsSpan(prefix.Length, end - prefix.Length),
            System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static InventoryFixture Create(bool rootGap = false, bool legacyRoot = false)
    {
        var a = Object("payloadA", "authored inventory payload A\n"u8.ToArray());
        var b = Object("payloadB", "authored inventory payload B has different bytes\n"u8.ToArray());
        var c = rootGap ? Object("payloadC", "authored file outside the download manifest\n"u8.ToArray()) : null;
        var unresolvable = rootGap ? Convert.ToHexStringLower(MD5.HashData("authored file without an encoding entry"u8)) : null;
        // A pre-TSFM layout ENCODING still maps, but VibeMiner does not read it.
        var root = Object("root", legacyRoot ? "authored legacy file list layout"u8.ToArray() : Root(a, b, c, unresolvable));
        var install = Object("install", Install(a));
        var download = Object("download", Download(a, b));
        var size = Object("size", DownloadSize(a, b));
        var encoding = Object("encoding", EncodingManifest(c is null ? [a, b, root, install, download, size] : [a, b, c, root, install, download, size]));
        var config = new StringBuilder("# authored complete inventory configuration\n");
        config.Append("root = ").Append(root.ContentKey).Append('\n');
        foreach (var item in new[] { encoding, install, download, size })
        {
            config.Append(item.Role).Append(" = ").Append(item.ContentKey).Append(' ').Append(item.EncodingKey).Append('\n');
            config.Append(item.Role).Append("-size = ").Append(item.Decoded.Length).Append(' ').Append(item.Encoded.Length).Append('\n');
        }
        var buildConfig = Encoding.UTF8.GetBytes(config.ToString());
        var cdnConfig = "# authored metadata uses admitted loose objects\narchives =\n"u8.ToArray();
        var buildKey = Hash(buildConfig);
        var cdnKey = Hash(cdnConfig);
        var versions = Encoding.UTF8.GetBytes("Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!STRING:0|ProductConfig!HEX:16\n" +
            $"us|{buildKey}|{cdnKey}||12345|1.2.3.12345|\n");
        var cdns = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\nus|tpr/wow|us.cdn.blizzard.com|https://us.cdn.blizzard.com/\n"u8.ToArray();
        var versionRow = MetadataParser.Parse(versions).Rows.Single();
        var cdnRow = MetadataParser.Parse(cdns).Rows.Single();
        var identity = VibeMiner.Domain.BuildIdentity.Create("wow", versionRow.Fields);
        const string time = "2026-10-18T12:00:00.0000000+00:00";
        SourceDocument Document(string role, byte[] bytes) => new(role,
            "https://us.version.battle.net/v2/products/wow/" + role, time,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
        var fixture = new InventoryFixture
        {
            PayloadA = a, PayloadB = b, RootOnly = c, Unresolvable = unresolvable, BuildConfig = buildConfig, CdnConfig = cdnConfig,
            BuildKey = buildKey, CdnKey = cdnKey, BuildIdentity = identity.Id,
            Discovery = new([Document("versions", versions), Document("cdns", cdns)],
                [new(identity, "us", versionRow.Ordinal, cdnRow.Ordinal, versionRow.Fields, cdnRow.Fields)],
                ["us"], ["us"], [], "online")
        };
        fixture.Responses.Add(Url("config", buildKey), buildConfig);
        fixture.Responses.Add(Url("config", cdnKey), cdnConfig);
        foreach (var item in new[] { encoding, root, install, download, size })
        {
            fixture.Metadata.Add(item.Role, item);
            fixture.Responses.Add(Url("data", item.EncodingKey), item.Encoded);
        }
        fixture.Payloads.Add(a.EncodingKey, a.Encoded);
        fixture.Payloads.Add(b.EncodingKey, b.Encoded);
        if (c is not null) fixture.Payloads.Add(c.EncodingKey, c.Encoded);
        return fixture;
    }

    public static string Url(string family, string key) =>
        $"https://us.cdn.blizzard.com/tpr/wow/{family}/{key[..2]}/{key[2..4]}/{key}";

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static FixtureObject Object(string role, byte[] decoded)
    {
        // Headerless BLTE: EKey covers the whole encoded object; CKey covers only decoded bytes.
        byte[] encoded = [(byte)'B', (byte)'L', (byte)'T', (byte)'E', 0, 0, 0, 0, (byte)'N', .. decoded];
        return new(role, Hash(decoded), Hash(encoded), decoded, encoded);
    }

    private static byte[] Root(FixtureObject a, FixtureObject b, FixtureObject? c = null, string? unresolvable = null)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("TSFM"u8); writer.Write(24u); writer.Write(2u); writer.Write(c is null ? 3u : 5u); writer.Write(2u); writer.Write(0u);
        // Named locale1 source block: FDIDs1001 and1002. Delta encoding increments after each ID.
        writer.Write(2u); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0);
        writer.Write(1001u); writer.Write(0u);
        writer.Write(Convert.FromHexString(a.ContentKey)); writer.Write(Convert.FromHexString(b.ContentKey));
        writer.Write(0x123456789abcdef0UL); writer.Write(0xfedcba9876543210UL);
        // Another locale's unnamed membership for the same FDID/content remains a separate origin.
        writer.Write(1u); writer.Write(2u); writer.Write(0x10000000u); writer.Write(0u); writer.Write((byte)0);
        writer.Write(1001u); writer.Write(Convert.FromHexString(a.ContentKey));
        if (c is not null)
        {
            // An unnamed block for FDIDs 2001 and 2002: one file outside the manifest, one with no encoding entry.
            writer.Write(2u); writer.Write(1u); writer.Write(0x10000000u); writer.Write(0u); writer.Write((byte)0);
            writer.Write(2001u); writer.Write(0u);
            writer.Write(Convert.FromHexString(c.ContentKey)); writer.Write(Convert.FromHexString(unresolvable!));
        }
        return output.ToArray();
    }

    private static byte[] Install(FixtureObject a)
    {
        using var output = new MemoryStream();
        output.Write("IN"u8); output.Write([1, 16]); Big(output, 1, 2); Big(output, 1, 4);
        Name(output, "all-variants"); Big(output, 3, 2); output.WriteByte(0x81); // retains unused padding
        Name(output, "authored/data-a.bin"); output.Write(Convert.FromHexString(a.ContentKey)); Big(output, a.Decoded.Length, 4);
        return output.ToArray();
    }

    private static byte[] Download(FixtureObject a, FixtureObject b)
    {
        using var output = new MemoryStream();
        output.Write("DL"u8); output.Write([3, 16, 0]); Big(output, 2, 4); Big(output, 1, 2);
        output.Write([2, 0xff, 1, 2, 3]); // two flag bytes, raw base priority, unknown v3 bytes
        foreach (var (item, priority) in new[] { (a, (byte)0xfe), (b, (byte)1) })
        {
            output.Write(Convert.FromHexString(item.EncodingKey)); Big(output, item.Encoded.Length, 5);
            output.Write([priority, 0x80, 1]);
        }
        Name(output, "all-variants"); Big(output, 4, 2); output.WriteByte(0xc1);
        return output.ToArray();
    }

    private static byte[] DownloadSize(FixtureObject a, FixtureObject b)
    {
        using var output = new MemoryStream();
        output.Write("DS"u8); output.Write([1, 9]); Big(output, 2, 4); Big(output, 1, 2);
        Big(output, a.Encoded.Length + b.Encoded.Length, 5);
        Name(output, "all-variants"); Big(output, 5, 2); output.WriteByte(0xc1);
        foreach (var item in new[] { a, b })
        { output.Write(Convert.FromHexString(item.EncodingKey).AsSpan(0, 9)); Big(output, item.Encoded.Length, 4); }
        return output.ToArray();
    }

    private static byte[] EncodingManifest(FixtureObject[] objects)
    {
        const int pageBytes = 1024;
        var contentPage = new byte[pageBytes];
        var encodingPage = new byte[pageBytes];
        var contents = objects.OrderBy(item => item.ContentKey, StringComparer.Ordinal).ToArray();
        var encodings = objects.OrderBy(item => item.EncodingKey, StringComparer.Ordinal).ToArray();
        using (var page = new MemoryStream(contentPage, writable: true))
        {
            foreach (var item in contents)
            {
                page.WriteByte(1); Big(page, item.Decoded.Length, 5);
                page.Write(Convert.FromHexString(item.ContentKey)); page.Write(Convert.FromHexString(item.EncodingKey));
            }
        }
        using (var page = new MemoryStream(encodingPage, writable: true))
        {
            foreach (var item in encodings)
            { page.Write(Convert.FromHexString(item.EncodingKey)); Big(page, 0, 4); Big(page, item.Encoded.Length, 5); }
        }
        byte[] specs = "b:*=n\0"u8.ToArray();
        using var output = new MemoryStream();
        output.Write("EN"u8); output.Write([1, 16, 16]); Big(output, 1, 2); Big(output, 1, 2);
        Big(output, 1, 4); Big(output, 1, 4); output.WriteByte(0); Big(output, specs.Length, 4); output.Write(specs);
        output.Write(Convert.FromHexString(contents[0].ContentKey)); output.Write(MD5.HashData(contentPage)); output.Write(contentPage);
        output.Write(Convert.FromHexString(encodings[0].EncodingKey)); output.Write(MD5.HashData(encodingPage)); output.Write(encodingPage);
        return output.ToArray();
    }

    private static void Name(Stream output, string value) { output.Write(Encoding.UTF8.GetBytes(value)); output.WriteByte(0); }
    private static void Big(Stream output, long value, int bytes)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, (ulong)value);
        output.Write(buffer[(8 - bytes)..]);
    }
}
