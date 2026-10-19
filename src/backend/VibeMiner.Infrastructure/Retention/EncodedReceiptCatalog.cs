using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    internal const string EncodedVerifierRevision = "blte-encoded-v1";
    private const int MaxEncodedReceiptRequests = 128;

    private void UpgradeEncodedReceipts()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE encoded_objects(ekey TEXT PRIMARY KEY,sha256 TEXT NOT NULL,
                encoded_bytes INTEGER NOT NULL,receipt TEXT NOT NULL);
            PRAGMA user_version=8;
            """, transaction);
        transaction.Commit();
    }

    internal async Task<EncodedReceipt?> FindEncodedReceiptAsync(string encodingKey, long encodedBytes, CancellationToken token)
    {
        var result = await ReadEncodedReceiptsAsync([(encodingKey, encodedBytes)], token);
        return result.GetValueOrDefault(encodingKey);
    }

    internal Task<IReadOnlyDictionary<string, EncodedReceipt>> ReadEncodedReceiptsAsync(
        IReadOnlyList<(string EncodingKey, long EncodedBytes)> requests, CancellationToken token)
    {
        var required = ValidateEncodedRequests(requests);
        return Schedule(() => ReadEncodedReceipts(required, null, token), token);
    }

    internal Task RecordEncodedReceiptAsync(EncodedReceipt receipt, CancellationToken token) => Schedule(() =>
    {
        ValidateEncodedReceipt(receipt);
        using var transaction = Db.BeginTransaction();
        var previous = ReadEncodedReceipts(new Dictionary<string, long>(StringComparer.Ordinal)
            { [receipt.EncodingKey] = receipt.EncodedBytes }, transaction, token).GetValueOrDefault(receipt.EncodingKey);
        if (previous is not null && (previous.Sha256 != receipt.Sha256 || previous.EncodedBytes != receipt.EncodedBytes))
            throw EncodedIntegrity("An exact encoded identity cannot be associated with different verified bytes.");
        Execute("INSERT OR IGNORE INTO encoded_objects VALUES($k,$h,$n,$r)", transaction,
            ("$k", receipt.EncodingKey), ("$h", receipt.Sha256), ("$n", receipt.EncodedBytes), ("$r", Serialize(receipt)));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    private static IReadOnlyDictionary<string, long> ValidateEncodedRequests(IReadOnlyList<(string EncodingKey, long EncodedBytes)> requests)
    {
        if (requests is null || requests.Count > MaxEncodedReceiptRequests)
            throw new DiscoveryException("INVALID_REQUEST", "Request at most 128 exact encoded-object receipts.");
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (!ValidEncodedKey(request.EncodingKey) || request.EncodedBytes is < 9 or > VerifiedObjectStore.MaxObjectBytes ||
                result.TryGetValue(request.EncodingKey, out var previous) && previous != request.EncodedBytes)
                throw new DiscoveryException("INVALID_REQUEST", "Choose consistent full encoded identities and bounded exact lengths.");
            result[request.EncodingKey] = request.EncodedBytes;
        }
        return new ReadOnlyDictionary<string, long>(result);
    }

    private IReadOnlyDictionary<string, EncodedReceipt> ReadEncodedReceipts(IReadOnlyDictionary<string, long> required,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction, CancellationToken token)
    {
        var found = new Dictionary<string, EncodedReceipt>(StringComparer.Ordinal);
        if (required.Count == 0) return new ReadOnlyDictionary<string, EncodedReceipt>(found);
        // Every branch uses its primary-key index against only the bounded requested identities.
        using var command = Command("""
            SELECT kind,ekey,sha256,encoded,ckey,decoded,receipt,receipt_bytes FROM (
                SELECT 0 kind,ekey,sha256,encoded_bytes encoded,NULL ckey,NULL decoded,receipt,length(receipt) receipt_bytes
                    FROM encoded_objects WHERE ekey IN (SELECT value FROM json_each($keys))
                UNION ALL
                SELECT 1 kind,ekey,NULL sha256,encoded,ckey,NULL decoded,receipt,length(receipt) receipt_bytes
                    FROM metadata_objects WHERE ekey IN (SELECT value FROM json_each($keys))
                UNION ALL
                SELECT 2 kind,ekey,sha256,encoded_bytes encoded,ckey,decoded_bytes decoded,receipt,length(receipt) receipt_bytes
                    FROM content_objects WHERE ekey IN (SELECT value FROM json_each($keys)))
            ORDER BY ekey,kind
            """, transaction, ("$keys", Serialize(required.Keys.ToArray())));
        using var reader = command.ExecuteReader(); var native = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); var kind = reader.GetInt32(0);
            if (reader.GetInt64(7) is < 1 || reader.GetInt64(7) > (kind == 2 ? 32_768 : 16_384))
                throw EncodedIntegrity("A retained encoded-source receipt exceeds its bounded projection.");
            EncodedReceipt receipt;
            try
            {
                if (kind == 0)
                {
                    receipt = Deserialize<EncodedReceipt>(reader.GetString(6)); ValidateEncodedReceipt(receipt);
                }
                else if (kind == 1)
                {
                    var source = Deserialize<MetadataReceipt>(reader.GetString(6)) ?? throw EncodedIntegrity("A retained metadata receipt is empty."); ValidateDownloadReceiptFacts(source);
                    if (source.ContentKey != reader.GetString(4)) throw EncodedIntegrity("A metadata alias differs from its original source content identity.");
                    receipt = new(source.EncodingKey, source.Sha256, DownloadNumber(source.EncodedBytes), source.SourceUrl,
                        source.RetrievedAt, source.VerifiedAt, EncodedVerifierRevision, MetadataSourceLocation(source));
                    ValidateEncodedReceipt(receipt);
                }
                else
                {
                    var source = Deserialize<ContentReceipt>(reader.GetString(6)) ?? throw EncodedIntegrity("A retained content receipt is empty."); ValidateContentReceipt(source);
                    if (source.ContentKey != reader.GetString(4) || DownloadNumber(source.DecodedBytes) != reader.GetInt64(5))
                        throw EncodedIntegrity("A content alias differs from its original decoded-source facts.");
                    receipt = new(source.EncodingKey, source.Sha256, DownloadNumber(source.EncodedBytes), source.SourceUrl,
                        source.RetrievedAt, source.VerifiedAt, EncodedVerifierRevision, source.Location);
                    ValidateEncodedReceipt(receipt);
                }
            }
            catch (JsonException) { throw EncodedIntegrity("A retained encoded-source receipt has malformed framing."); }
            if (receipt.EncodingKey != reader.GetString(1) || receipt.EncodedBytes != reader.GetInt64(3) ||
                kind != 1 && receipt.Sha256 != reader.GetString(2))
                throw EncodedIntegrity("A retained encoded alias differs from its original physical source proof.");
            if (!required.TryGetValue(receipt.EncodingKey, out var length) || length != receipt.EncodedBytes)
                throw EncodedIntegrity("The requested encoded length conflicts with a retained exact-object identity.");
            if (found.TryGetValue(receipt.EncodingKey, out var previous))
            {
                if (previous.Sha256 != receipt.Sha256 || previous.EncodedBytes != receipt.EncodedBytes)
                    throw EncodedIntegrity("Retained source kinds disagree on one exact encoded object's digest or length.");
                if (!native.Contains(receipt.EncodingKey) && UtcInstant(receipt.RetrievedAt) < UtcInstant(previous.RetrievedAt))
                    found[receipt.EncodingKey] = receipt;
            }
            else found.Add(receipt.EncodingKey, receipt);
            if (kind == 0) native.Add(receipt.EncodingKey);
        }
        return new ReadOnlyDictionary<string, EncodedReceipt>(found);
    }

    private static void ValidateEncodedReceipt(EncodedReceipt receipt)
    {
        if (receipt is null || !ValidEncodedKey(receipt.EncodingKey) || !DownloadHash(receipt.Sha256, 64) ||
            receipt.EncodedBytes is < 9 or > VerifiedObjectStore.MaxObjectBytes || receipt.VerifierRevision != EncodedVerifierRevision ||
            !CanonicalEncodedUtc(receipt.RetrievedAt) || !CanonicalEncodedUtc(receipt.VerifiedAt) ||
            UtcInstant(receipt.VerifiedAt) < UtcInstant(receipt.RetrievedAt) ||
            receipt.SourceUrl is null || receipt.SourceUrl.Length > 2048 || !Uri.TryCreate(receipt.SourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" || Regex.IsMatch(uri.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant)) ||
            receipt.Location is null || receipt.Location.Kind is not ("loose" or "archive"))
            throw EncodedIntegrity("An encoded-object receipt has incomplete original verification facts.");
        var location = receipt.Location;
        var offset = EncodedOrdinal(location.Offset); var sourceBytes = EncodedOrdinal(location.SourceBytes);
        var key = location.Kind == "archive" ? location.ArchiveKey : receipt.EncodingKey;
        if (!ValidEncodedKey(key!) || sourceBytes < receipt.EncodedBytes || offset > sourceBytes - receipt.EncodedBytes ||
            location.Kind == "loose" && (location.ArchiveKey is not null || offset != 0 || sourceBytes != receipt.EncodedBytes) ||
            !Regex.IsMatch(uri.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/data/" + key![..2] + "/" + key[2..4] + "/" + key + "$", RegexOptions.CultureInvariant))
            throw EncodedIntegrity("An encoded-object receipt has an invalid exact loose/archive source locator.");
    }
    private static bool ValidEncodedKey(string key) => DownloadHash(key, 32) && key.Any(ch => ch != '0');
    private static long EncodedOrdinal(string value)
    {
        if (!CanonicalOrdinal(value)) throw EncodedIntegrity("An encoded source offset or size is not a bounded canonical integer.");
        return long.Parse(value, CultureInfo.InvariantCulture);
    }
    private static bool CanonicalEncodedUtc(string value) => value is not null && value.Length is >= 20 and <= 33 &&
        Regex.IsMatch(value, "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:Z|\\+00:00)$", RegexOptions.CultureInvariant) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Offset == TimeSpan.Zero;
    private static DateTimeOffset UtcInstant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static DiscoveryException EncodedIntegrity(string message) => new("ENCODED_RECEIPT_INTEGRITY", message);
}
