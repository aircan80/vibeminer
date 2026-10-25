using System.Text;
using System.Text.Json;

namespace VibeMiner.Infrastructure;

/// <summary>A retained appearance row no current key reaches (D103), with the VMA1 file it names, if any. A row whose recorded
/// file is malformed is returned as <paramref name="Malformed"/> so the pass can skip it alone.</summary>
internal sealed record GeometryAppearanceGarbage(long Rowid, string Key, string? Sha256, long? ByteLength, bool Malformed = false);

public sealed partial class DiscoveryCatalog
{
    private const string SupersededAppearance = "(key_revision IS NULL OR key_revision<>$r)";
    // A row written before D103 whose file has not been read from its descriptor yet: it may name any file.
    private const string UnnamedAppearance = "(artifact_id IS NOT NULL AND artifact_sha256 IS NULL AND artifact_bytes IS NULL)";
    // artifact_bytes -1 marks a pre-D103 row whose descriptor does not say which file it uses. It is kept, so the file it
    // uses (whichever it is) is never left without a row, and it no longer counts as possibly naming every file.
    private const long UnreadableAppearanceFile = -1;

    /// <summary>Records the VMA1 file of up to <paramref name="limit"/> rows written before D103, read from the stored
    /// descriptor's manifest. A descriptor too large or without a readable manifest file is marked and the row kept.</summary>
    internal Task<int> NameGeometryAppearanceFilesAsync(int limit, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction(); var rows = new List<(string Key, string? Descriptor)>();
        using (var command = Command($"""
            SELECT appearance_key,CASE WHEN length(CAST(descriptor AS BLOB))<=$m THEN descriptor END FROM geometry_appearances
            WHERE {UnnamedAppearance} AND {SupersededAppearance} LIMIT $l
            """, transaction, ("$m", GeometryAppearanceDescriptorLimit), ("$r", GeometryAppearanceKeyRevision), ("$l", limit)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        foreach (var (key, descriptor) in rows)
        {
            token.ThrowIfCancellationRequested();
            var file = descriptor is null ? null : AppearanceFile(descriptor);
            if (file is null) EngineLog.Write("geometry appearance cleanup: row " + key[..Math.Min(16, key.Length)] + " names no readable VMA1 file; it is kept");
            Execute("UPDATE geometry_appearances SET artifact_sha256=$s,artifact_bytes=$n WHERE appearance_key=$k", transaction,
                ("$k", key), ("$s", (object?)file?.Sha256 ?? DBNull.Value), ("$n", file?.ByteLength ?? UnreadableAppearanceFile));
        }
        transaction.Commit(); return rows.Count;
    }, token);

    /// <summary>Up to <paramref name="limit"/> superseded rows whose file is known (or that have none), in rowid order after
    /// <paramref name="after"/>. A key that is not 64 hex digits, or a malformed recorded file, comes back
    /// <see cref="GeometryAppearanceGarbage.Malformed"/>; an oversized key is never read.</summary>
    internal Task<IReadOnlyList<GeometryAppearanceGarbage>> ReadGeometryAppearanceGarbageAsync(long after, int limit, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction(); var rows = new List<GeometryAppearanceGarbage>();
        using (var command = Command($"""
            SELECT rowid,CASE WHEN length(CAST(appearance_key AS BLOB))=64 THEN appearance_key END,
                CASE WHEN length(CAST(artifact_sha256 AS BLOB))=64 THEN artifact_sha256 END,artifact_sha256 IS NULL,artifact_bytes FROM geometry_appearances
            WHERE rowid>$a AND {SupersededAppearance} AND (artifact_id IS NULL OR artifact_sha256 IS NOT NULL)
            ORDER BY rowid LIMIT $l
            """, transaction, ("$a", after), ("$r", GeometryAppearanceKeyRevision), ("$l", limit)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var rowid = reader.GetInt64(0);
                var key = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (key is null || !DownloadHash(key, 64)) { rows.Add(new(rowid, "", null, null, Malformed: true)); continue; }
                if (reader.GetInt64(3) == 1) { rows.Add(new(rowid, key, null, null)); continue; }
                var sha = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (sha is null || !DownloadHash(sha, 64) || reader.IsDBNull(4) || reader.GetInt64(4) is < 1 or > VerifiedObjectStore.MaxObjectBytes)
                    rows.Add(new(rowid, key, null, null, Malformed: true));
                else rows.Add(new(rowid, key, sha, reader.GetInt64(4)));
            }
        transaction.Commit(); return (IReadOnlyList<GeometryAppearanceGarbage>)rows.AsReadOnly();
    }, token);

    /// <summary>Whether a row names the file: any other row, or row <paramref name="key"/> itself once it is no longer superseded
    /// (a Prepare made it current). A row written before D103 whose file is not yet recorded counts as naming it.</summary>
    internal Task<bool> IsGeometryAppearanceFileNamedAsync(string sha256, string key, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        using var command = Command($"""
            SELECT EXISTS(SELECT 1 FROM geometry_appearances WHERE (appearance_key<>$k OR key_revision=$r) AND
                (artifact_sha256=$s OR {UnnamedAppearance}))
            """, transaction, ("$k", key), ("$r", GeometryAppearanceKeyRevision), ("$s", sha256));
        var named = Convert.ToInt64(command.ExecuteScalar()) == 1; transaction.Commit(); return named;
    }, token);

    /// <summary>Deletes the row if it is still superseded; false when it is gone or was made current meanwhile.</summary>
    internal Task<bool> DeleteGeometryAppearanceAsync(string key, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        using var command = Command("DELETE FROM geometry_appearances WHERE appearance_key=$k AND " + SupersededAppearance, transaction,
            ("$k", key), ("$r", GeometryAppearanceKeyRevision));
        var deleted = command.ExecuteNonQuery() == 1; token.ThrowIfCancellationRequested(); transaction.Commit(); return deleted;
    }, token);

    private const int GeometryAppearanceDescriptorLimit = 64 << 10;

    // Reads only appearance.manifest.sha256 and byteLength, so a descriptor from an older record shape still names its file.
    private static (string Sha256, long ByteLength)? AppearanceFile(string descriptor)
    {
        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes(descriptor), new() { MaxDepth = 64 });
            if (Child(document.RootElement, "appearance") is not { } appearance || Child(appearance, "manifest") is not { } manifest ||
                Child(manifest, "sha256") is not { ValueKind: JsonValueKind.String } sha || Child(manifest, "byteLength") is not { ValueKind: JsonValueKind.Number } bytes ||
                !bytes.TryGetInt64(out var length) || length is < 1 or > VerifiedObjectStore.MaxObjectBytes || sha.GetString() is not { } text || !DownloadHash(text, 64)) return null;
            return (sha.GetString()!, length);
        }
        catch (JsonException) { return null; }
        static JsonElement? Child(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
            return null;
        }
    }
}
