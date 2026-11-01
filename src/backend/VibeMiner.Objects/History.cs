using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VibeMiner.Objects;

/// <summary>
/// What a reply is: a version-service document (versions, cdns, bgdl (background-download versions), or the summary of
/// every product), or a WoWDBDefs table definition. A summary entry's product is "". A definition entry's product is the
/// table id (8 lower hex), its region "wowdbdefs", and its URL the raw file at the commit it came from.
/// </summary>
public enum HistoryKind : byte { Versions = 1, Cdns = 2, Bgdl = 3, Summary = 4, Definition = 5 }

/// <summary>One raw version-service or WoWDBDefs reply as it was received: what was asked, where, when, and the exact bytes.</summary>
public sealed record HistoryObservation(HistoryKind Kind, string Product, string Region, DateTimeOffset ObservedAt, string Url, ReadOnlyMemory<byte> Reply);

/// <summary>A history log entry. The reply bytes are the <see cref="Document"/> object; Read it to get them.</summary>
public sealed record HistoryEntry(HistoryKind Kind, string Product, string Region, DateTimeOffset ObservedAt, string Url, ObjectKey Document, ObjectKey Record);

/// <summary>Filters for a history read; <see cref="After"/> continues from the last entry of the previous page.</summary>
public sealed record HistoryQuery(string? Product = null, string? Region = null, HistoryKind? Kind = null, HistoryEntry? After = null, int Limit = 1000);

/// <summary>
/// History record format 1 (little-endian): u8 version 1 | u8 kind | u16 reserved 0 | i64 observed unix ms | document key (16)
/// | u8 product length + ASCII [a-z0-9_-] (1..64; 0 exactly for a summary) | u8 region length + ASCII [a-z0-9] (1..16) | u16 URL length + UTF-8 (0..1024).
/// Exact end. The record's key is its MD5, so the same observation stored twice is one entry.
/// </summary>
public static class HistoryRecordFormat
{
    internal const int MaxBytes = 28 + 1 + 64 + 1 + 16 + 2 + 1024;  // 1,136

    internal readonly record struct Parsed(HistoryKind Kind, long ObservedMs, ObjectKey Document, string Product, string Region, string Url);

    /// <summary>The two objects one observation adds: the reply document, then the record naming it. Put them in one batch, in this order.</summary>
    public static IReadOnlyList<PutObject> Objects(HistoryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var reply = observation.Reply.ToArray();
        if (reply.Length == 0) throw new ArgumentException("A reply has at least one byte.", nameof(observation));
        var document = new ObjectKey(ObjectKeyType.HistoryDocument, MD5.HashData(reply));
        var record = Encode(observation.Kind, observation.ObservedAt.ToUnixTimeMilliseconds(), document, observation.Product, observation.Region, observation.Url);
        return [new PutObject(document, reply.Length, reply), new PutObject(new ObjectKey(ObjectKeyType.HistoryRecord, MD5.HashData(record)), record.Length, record)];
    }

    internal static byte[] Encode(HistoryKind kind, long observedMs, ObjectKey document, string product, string region, string url)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!ValidProduct(kind, product)) throw new ArgumentException(kind == HistoryKind.Summary ? "A summary has no product." : "A product is 1-64 of [a-z0-9_-].", nameof(product));
        if (!ValidRegion(region)) throw new ArgumentException("A region is 1-16 of [a-z0-9].", nameof(region));
        var u = Encoding.UTF8.GetBytes(url ?? "");
        if (u.Length > 1024) throw new ArgumentException("A URL is at most 1024 UTF-8 bytes.", nameof(url));
        var b = new byte[28 + 1 + product.Length + 1 + region.Length + 2 + u.Length];
        b[0] = 1; b[1] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), observedMs);
        document.WriteKey(b.AsSpan(12));
        var at = 28;
        b[at++] = (byte)product.Length; Encoding.ASCII.GetBytes(product, b.AsSpan(at)); at += product.Length;
        b[at++] = (byte)region.Length; Encoding.ASCII.GetBytes(region, b.AsSpan(at)); at += region.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)u.Length); at += 2;
        u.CopyTo(b, at);
        return b;
    }

    internal static bool TryParse(ReadOnlySpan<byte> b, out Parsed parsed)
    {
        parsed = default;
        if (b.Length < 28 + 1 + 1 + 1 + 2 || b[0] != 1 || b[1] is < (byte)HistoryKind.Versions or > (byte)HistoryKind.Definition || b[2] != 0 || b[3] != 0) return false;
        var kind = (HistoryKind)b[1];
        var at = 28;
        int pl = b[at++]; if (pl > 64 || at + pl > b.Length) return false;
        var product = Encoding.ASCII.GetString(b.Slice(at, pl)); at += pl;
        if (!ValidProduct(kind, product) || at >= b.Length) return false;
        int rl = b[at++]; if (rl == 0 || rl > 16 || at + rl > b.Length) return false;
        var region = Encoding.ASCII.GetString(b.Slice(at, rl)); at += rl;
        if (!ValidRegion(region) || at + 2 > b.Length) return false;
        int ul = BinaryPrimitives.ReadUInt16LittleEndian(b[at..]); at += 2;
        if (ul > 1024 || at + ul != b.Length) return false;
        string url;
        try { url = new UTF8Encoding(false, true).GetString(b.Slice(at, ul)); } catch (DecoderFallbackException) { return false; }
        parsed = new(kind, BinaryPrimitives.ReadInt64LittleEndian(b[4..]), new ObjectKey(ObjectKeyType.HistoryDocument, b.Slice(12, 16)), product, region, url);
        return true;
    }

    /// <summary>A summary has no product (""); every other kind has a 1-64 character one.</summary>
    private static bool ValidProduct(HistoryKind kind, string? s) => kind == HistoryKind.Summary ? s == "" : s is { Length: >= 1 and <= 64 } && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');
    private static bool ValidRegion(string? s) => s is { Length: >= 1 and <= 16 } && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));
}
