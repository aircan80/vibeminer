using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    internal Task<BulkArchiveIndexSource?> FindRetentionNativeSourceAsync(string cdnConfigurationSha256,
        string archiveKey, CancellationToken token) => Schedule(() =>
    {
        ValidateNativeIdentity(cdnConfigurationSha256, archiveKey);
        using var command = Command("SELECT receipt,length(receipt) FROM retention_native_sources WHERE cdn_sha256=$c AND archive_key=$a", null,
            ("$c", cdnConfigurationSha256), ("$a", archiveKey));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt64(1) is < 1 or > 8192) throw RetentionIntegrity("A retained native index receipt exceeds its bound.");
        BulkArchiveIndexSource source;
        try { source = Deserialize<BulkArchiveIndexSource>(reader.GetString(0)); }
        catch (JsonException) { throw RetentionIntegrity("A retained native index receipt is malformed."); }
        ValidateNativeSource(cdnConfigurationSha256, source);
        if (source.ArchiveKey != archiveKey) throw RetentionIntegrity("A retained native source differs from its exact archive binding.");
        return source;
    }, token);

    internal Task RecordRetentionNativeSourceAsync(string cdnConfigurationSha256, BulkArchiveIndexSource source,
        CancellationToken token) => Schedule(() =>
    {
        ValidateNativeSource(cdnConfigurationSha256, source);
        using var transaction = Db.BeginTransaction();
        var value = Scalar("SELECT receipt FROM retention_native_sources WHERE cdn_sha256=$c AND archive_key=$a", transaction,
            ("$c", cdnConfigurationSha256), ("$a", source.ArchiveKey)) as string;
        if (value is not null)
        {
            var original = Deserialize<BulkArchiveIndexSource>(value); ValidateNativeSource(cdnConfigurationSha256, original);
            if (original.Sha256 != source.Sha256 || original.ByteLength != source.ByteLength || original.ArchiveKey != source.ArchiveKey)
                throw RetentionIntegrity("An authenticated immutable native archive index cannot be rebound to different bytes.");
        }
        else Execute("INSERT INTO retention_native_sources VALUES($c,$a,$r)", transaction,
            ("$c", cdnConfigurationSha256), ("$a", source.ArchiveKey), ("$r", Serialize(source)));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    private static void ValidateNativeIdentity(string cdnConfigurationSha256, string archiveKey)
    {
        if (!DownloadHash(cdnConfigurationSha256, 64) || !DownloadHash(archiveKey, 32) || archiveKey.All(c => c == '0'))
            throw RetentionIntegrity("A native archive index requires its exact CDN configuration and full archive identity.");
    }
    private static void ValidateNativeSource(string cdnConfigurationSha256, BulkArchiveIndexSource source)
    {
        ValidateNativeIdentity(cdnConfigurationSha256, source.ArchiveKey);
        var key = source.ArchiveKey;
        if (source.CdnConfigurationSha256 != cdnConfigurationSha256 || !DownloadHash(source.Sha256, 64) || source.ByteLength is < 28 or > (512L << 20) ||
            source.ParserRevision != BulkArchiveLocator.NativeRevision || source.RetrievedAt.Offset != TimeSpan.Zero ||
            source.SourceUrl.Length > 2048 || !Uri.TryCreate(source.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" || Regex.IsMatch(uri.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant)) ||
            !Regex.IsMatch(uri.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/data/" + key[..2] + "/" + key[2..4] + "/" + key + "\\.index$", RegexOptions.CultureInvariant))
            throw RetentionIntegrity("A native archive index lacks supported authenticated original source facts.");
    }
}
