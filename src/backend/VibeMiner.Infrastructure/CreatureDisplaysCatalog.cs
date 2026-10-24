using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record CreatureDisplayCatalogBinding(string TableId, StoredTableAdmission Admission);

/// <summary>Local source reads and one atomic current-authority fence; no chain registry or acquisition.</summary>
public sealed partial class DiscoveryCatalog
{
    private const string DisplayTable = "bfdaf9f1", DisplayModelTable = "7f695fa5";
    private static DiscoveryException DisplayLimit() => new("CREATURE_DISPLAY_RESOURCE_LIMIT", "The display source metadata exceeds its bounded source budget.");
    private static DiscoveryException DisplayInvalid() => new("CREATURE_DISPLAY_BINDING_INVALID", "The display source has invalid original admission evidence.");
    private static DiscoveryException DisplayStale() => new("CREATURE_DISPLAY_STALE", "A selected display source changed. Retry the selected display.", true);

    internal Task<StoredTableAdmission?> ReadCreatureDisplayTargetAsync(CapturedOpeningContext opening, string tableId, CancellationToken token)
    {
        DisplayTableId(tableId);
        return Schedule(() =>
        {
            using var transaction = Db.BeginTransaction();
            RequireCreatureOpening(opening, transaction);
            var result = DisplayAdmission(opening, tableId, transaction);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        }, token);
    }

    internal Task ValidateCreatureDisplayCurrentAsync(CapturedOpeningContext opening, CreaturesArtifact core,
        IReadOnlyList<CreatureDisplayCatalogBinding> targets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (targets is null) throw DisplayInvalid();
        var count = targets.Count; if (count is < 0 or > 2) throw DisplayInvalid();
        // Own caller collections before scheduling. Bound original bytes before any authority normalization.
        var frozenCore = CreatureJson<CreaturesArtifact>(Encoding.UTF8.GetString(CreaturesPolicy.Bytes(core)));
        CreaturesPolicy.Validate(frozenCore);
        var frozen = new CreatureDisplayCatalogBinding[count];
        for (var i = 0; i < frozen.Length; i++)
        {
            CreatureDisplayCatalogBinding target;
            try { target = targets[i]; }
            catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException) { throw DisplayInvalid(); }
            if (target is null || target.TableId != (i == 0 ? DisplayTable : DisplayModelTable) || target.Admission is null) throw DisplayInvalid();
            frozen[i] = target with { Admission = DisplayCopy(target.Admission) };
        }
        return Schedule(() =>
        {
            using var transaction = Db.BeginTransaction();
            var coreBudget = CreaturesPolicy.MaxDescriptorBytes;
            RequireCreatureFeature(opening, frozenCore, transaction, ref coreBudget);
            foreach (var target in frozen)
            {
                token.ThrowIfCancellationRequested();
                var current = DisplayAdmission(opening, target.TableId, transaction) ?? throw DisplayStale();
                if (current.PreparedAt != target.Admission.PreparedAt || !CreatureAuthority(current.Artifact, target.Admission.Artifact) ||
                    !CreatureAuthority(current.Source, target.Admission.Source) || !CreatureAuthority(current.Association, target.Admission.Association)) throw DisplayStale();
            }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
        }, token);
    }

    private static void DisplayTableId(string tableId)
    { if (tableId is not (DisplayTable or DisplayModelTable)) throw DisplayInvalid(); }

    private static StoredTableAdmission DisplayCopy(StoredTableAdmission admission)
    {
        try { return DisplayJson<StoredTableAdmission>(Encoding.UTF8.GetString(CreaturesPolicy.Bytes(admission))); }
        catch (DiscoveryException error) when (error.Code == "CREATURES_PROJECTION_LIMIT") { throw DisplayLimit(); }
    }

    private StoredTableAdmission? DisplayAdmission(CapturedOpeningContext opening, string tableId, SqliteTransaction transaction)
    {
        var budget = CreaturesPolicy.MaxDescriptorBytes;
        using var command = Command("""
            SELECT a.source,a.association,r.descriptor,a.prepared_at,
                length(CAST(a.source AS BLOB)),length(CAST(a.association AS BLOB)),
                length(CAST(r.descriptor AS BLOB)),length(CAST(a.prepared_at AS BLOB)),a.content_key,length(CAST(a.content_key AS BLOB))
            FROM table_admissions a JOIN table_artifacts r ON r.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.table_id=$t AND a.locale='enUS'
            """, transaction, ("$b", opening.Build.Id), ("$r", opening.Reference.Region), ("$ref", Serialize(opening.Reference)), ("$t", tableId));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        // Inspect every BLOB byte length before materializing even the first JSON field.
        long total = 0;
        for (var i = 4; i < 8; i++)
        {
            if (reader.IsDBNull(i) || reader.GetInt64(i) is < 1 or > CreaturesPolicy.MaxDescriptorBytes) throw DisplayLimit();
            total += reader.GetInt64(i);
        }
        if (total > budget) throw DisplayLimit();
        if (reader.IsDBNull(9) || reader.GetInt64(9) != 64) throw DisplayInvalid();
        var source = DisplayJson<ContentReceipt>(DisplayText(reader, 0, 4, ref budget));
        var association = DisplayJson<SelectedFileAssociation>(DisplayText(reader, 1, 5, ref budget));
        var artifact = DisplayJson<TableRowsArtifact>(DisplayText(reader, 2, 6, ref budget));
        var at = DisplayText(reader, 3, 7, ref budget); var key = reader.GetString(8); reader.Close();
        var result = new StoredTableAdmission(artifact, source, association, at);
        // The serialized live bundle also has a fixed cap before any normalization/identity comparison.
        _ = DisplayCopy(result);
        if (association.EncodingSource is null || association.RootSource is null || association.Memberships is null ||
            association.Memberships.Count is < 1 or > 256 || association.Memberships.Any(m => m is null) ||
            artifact.ContentKey != key || artifact.Binding is null || artifact.Header is null || artifact.Header.Columns is null || artifact.Header.Columns.Any(c => c is null) || artifact.Completion is null)
            throw DisplayInvalid();
        ValidateTableAdmission(tableId, "enUS", source, association, artifact, at, requireCurrentRevision: false);
        var original = DisplayCanonical<ContentReceipt>("content_objects", source.EncodingKey, transaction, ref budget);
        ValidateContentReceipt(original); if (!CreatureAuthority(original, source)) throw DisplayInvalid();
        foreach (var metadata in new[] { association.EncodingSource, association.RootSource })
        {
            var canonical = DisplayCanonical<MetadataReceipt>("metadata_objects", metadata.EncodingKey, transaction, ref budget);
            ValidateDownloadReceiptFacts(canonical);
            if (!MetadataReceiptIdentity.Same(canonical, metadata)) throw MetadataReceiptIdentity.Mismatch("CREATURE_DISPLAY_SOURCE_RECEIPT_MISMATCH", "A Creature display table", metadata);
        }
        return result;
    }

    private static T DisplayJson<T>(string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json); DisplayArrayBounds<T>(bytes);
            return JsonSerializer.Deserialize<T>(bytes, CreaturesPolicy.Json) ?? throw DisplayInvalid();
        }
        catch (JsonException) { throw DisplayInvalid(); }
    }
    private static void DisplayArrayBounds<T>(ReadOnlySpan<byte> bytes)
    {
        // A fixed parser stack checks only the two DTO collection paths before DTO allocation.
        // Projection proof arrays retain their separate domain limits.
        const int unknown = -1, combined = 0, artifact = 1, header = 2, association = 3, columns = 4, memberships = 5;
        Span<int> contexts = stackalloc int[33], properties = stackalloc int[33], counts = stackalloc int[33];
        Span<char> name = stackalloc char[97];
        var root = typeof(T) == typeof(TableRowsArtifact) ? artifact : typeof(T) == typeof(SelectedFileAssociation) ? association :
            typeof(T) == typeof(StoredTableAdmission) ? combined : unknown;
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        while (reader.Read())
        {
            var depth = reader.CurrentDepth; var token = reader.TokenType;
            if (depth > 0 && contexts[depth - 1] is columns or memberships && token is not (JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray))
                if (++counts[depth - 1] > (contexts[depth - 1] == columns ? 512 : 256)) throw DisplayLimit();
            if (token == JsonTokenType.PropertyName)
            {
                properties[depth] = unknown;
                if (reader.ValueSpan.Length <= 96)
                {
                    var text = name[..reader.CopyString(name)];
                    properties[depth] = text.Equals("artifact", StringComparison.OrdinalIgnoreCase) ? artifact : text.Equals("header", StringComparison.OrdinalIgnoreCase) ? header :
                        text.Equals("association", StringComparison.OrdinalIgnoreCase) ? association : text.Equals("columns", StringComparison.OrdinalIgnoreCase) ? columns :
                        text.Equals("memberships", StringComparison.OrdinalIgnoreCase) ? memberships : unknown;
                }
            }
            else if (token == JsonTokenType.StartObject)
                contexts[depth] = depth == 0 ? root : contexts[depth - 1] switch
                {
                    combined when properties[depth] is artifact or association => properties[depth],
                    artifact when properties[depth] == header => header,
                    _ => unknown
                };
            else if (token == JsonTokenType.StartArray)
            {
                contexts[depth] = depth == 0 ? unknown : contexts[depth - 1] switch
                {
                    header when properties[depth] == columns => columns,
                    association when properties[depth] == memberships => memberships,
                    _ => unknown
                };
                counts[depth] = 0;
            }
        }
    }
    private static string DisplayText(SqliteDataReader reader, int text, int length, ref int budget)
    {
        var size = reader.GetInt64(length); if (size < 1 || size > budget) throw DisplayLimit();
        budget -= checked((int)size); return reader.GetString(text);
    }
    private T DisplayCanonical<T>(string table, string key, SqliteTransaction transaction, ref int budget)
    {
        // Fixed internal table constants only, never a caller-supplied identifier.
        using var command = Command($"SELECT receipt,length(CAST(receipt AS BLOB)) FROM {table} WHERE ekey=$k", transaction, ("$k", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw DisplayInvalid();
        return DisplayJson<T>(DisplayText(reader, 0, 1, ref budget));
    }
}
