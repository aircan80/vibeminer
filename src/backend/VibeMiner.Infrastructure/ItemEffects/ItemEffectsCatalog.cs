using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record ItemEffectsBridgeProof(StoredTableAdmission Admission, TableDataSnapshot Snapshot, StoredItemEffectsBridgeAdmission? Index);
internal sealed record ItemEffectsTargetProof(ItemEffectsSourceRole Role, StoredTableAdmission Admission, TableDataSnapshot Snapshot);
internal sealed record ItemEffectsTargetFenceResult(ItemEffectsSourceRole Role, ItemEffectsProblem? Problem);

/// <summary>Bounded local authority and one SQL linearization point; never opens CAS or queries a game row.</summary>
public sealed partial class DiscoveryCatalog
{
    private const int EffectsItemsBytes = 256 << 10, EffectsSourceBytes = 128 << 10;
    private static readonly JsonSerializerOptions EffectsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private static DiscoveryException EffectsLimit() => new("ITEM_EFFECTS_LIMIT", "The original feature evidence exceeds its bounded metadata budget.");
    private static DiscoveryException EffectsItemsInvalid() => new("ITEMS_SOURCE_INVALID", "The selected Items source has invalid original authority.");
    private static DiscoveryException EffectsItemsStale() => new("ITEMS_ARTIFACT_STALE", "The selected Items feature or its source authority changed.", true);
    private static DiscoveryException EffectsIndexInvalid() => new("ITEM_EFFECTS_INDEX_INVALID", "The effects bridge has invalid original authority.");
    private static DiscoveryException EffectsBridgeStale() => new("ITEM_EFFECTS_BRIDGE_STALE", "The selected effects bridge authority changed.", true);
    private sealed record EffectsRawSource(byte[] Source, byte[] Association, byte[] Artifact, byte[] PreparedAt,
        string Key, byte[] Content, byte[] Encoding, byte[] Root);

    private sealed record EffectsRawConfiguration(byte[][] Text, int ByteLength, int ParsedFields, int OriginalByteLength);

    internal Task<ItemEffectsItemsAuthority> ReadItemEffectsItemsAuthorityAsync(CapturedOpeningContext opening,
        string locale, string expectedArtifactId, CancellationToken token)
    {
        if (locale != "enUS" || !DownloadHash(expectedArtifactId, 64)) throw new DiscoveryException("INVALID_REQUEST", "Choose an exact Items artifact and research locale.");
        return Schedule(() =>
        {
            using var transaction = Db.BeginTransaction();
            var result = EffectsItemsAuthority(opening, locale, expectedArtifactId, transaction, token);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        }, token);
    }

    private ItemEffectsItemsAuthority EffectsItemsAuthority(CapturedOpeningContext opening, string locale,
        string expectedArtifactId, SqliteTransaction transaction, CancellationToken token)
    {
        EffectsOpening(opening, transaction);
        var budget = EffectsItemsBytes;
        var configurationRaw = EffectsConfigurationsRaw(opening, transaction, ref budget);
        byte[]? descriptor = null, prepared = null; string? key = null;
        using (var command = Command("""
            SELECT CAST(f.descriptor AS BLOB),CAST(a.prepared_at AS BLOB),CAST(a.content_key AS BLOB),
                length(CAST(f.descriptor AS BLOB)),length(CAST(a.prepared_at AS BLOB)),length(CAST(a.content_key AS BLOB))
            FROM items_admissions a LEFT JOIN items_artifacts f ON f.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.locale=$l
            """, transaction, ("$b", opening.Build.Id), ("$r", opening.Reference.Region), ("$ref", Serialize(opening.Reference)), ("$l", locale)))
        using (var reader = command.ExecuteReader()) if (reader.Read())
        {
            EffectsSizes(reader, [3, 4], ref budget); key = EffectsHash(reader, 2, 5, 64);
            descriptor = EffectsBlob(reader, 0); prepared = EffectsBlob(reader, 1);
        }
        if (descriptor is null)
        {
            // A historical exact requested interpretation distinguishes stale from never prepared,
            // without invoking the old unbounded generic descriptor reader.
            var historical = EffectsDescriptor("items_artifacts", expectedArtifactId, transaction, ref budget);
            if (historical is not null)
            {
                var previous = EffectsDecode<ItemsProjectionArtifact>(historical);
                EffectsValidateItems(previous);
                if (previous.Binding.Context.ProductId == opening.Build.ProductId && previous.Binding.Context.BuildIdentity == opening.Build.Id &&
                    previous.Binding.Context.SourceRegion == opening.Reference.Region && previous.Binding.Context.ResearchLocale == locale) throw EffectsItemsStale();
            }
            throw new DiscoveryException("ITEMS_NOT_PREPARED", "Prepare this exact build's Items before reading its effects.");
        }
        if (key != expectedArtifactId) throw EffectsItemsStale();
        // Collect every raw source and canonical receipt under the combined budget before typed graphs.
        var itemRaw = EffectsSourceRaw(opening, ItemsProjection.ItemTable, transaction, ref budget) ?? throw EffectsItemsStale();
        var sparseRaw = EffectsSourceRaw(opening, ItemsProjection.SparseTable, transaction, ref budget) ?? throw EffectsItemsStale();
        EffectsPreflight(descriptor, EffectsShape.Feature); var artifact = EffectsDecode<ItemsProjectionArtifact>(descriptor); EffectsValidateItems(artifact);
        var configurations = EffectsConfigurations(opening, configurationRaw);
        var at = EffectsUtf8(prepared!); if (!DownloadUtc(at) || artifact.ContentKey != key) throw EffectsItemsInvalid();
        RequireItemsContext(opening, locale, artifact);
        if (artifact.FeatureRevision != ItemsProjection.Revision) throw EffectsItemsStale();
        var item = EffectsSource(itemRaw, ItemsProjection.ItemTable); var sparse = EffectsSource(sparseRaw, ItemsProjection.SparseTable);
        foreach (var pair in new[] { (Current: item, First: artifact.Binding.ItemSource, Rows: artifact.Binding.ItemRows),
            (Current: sparse, First: artifact.Binding.SparseSource, Rows: artifact.Binding.SparseRows) })
        {
            if (!EffectsAuthority(pair.Current.Artifact, pair.Rows) || !EffectsAuthority(pair.Current.Source, pair.First.Source) ||
                !EffectsAuthority(pair.Current.Association, pair.First.Association)) throw EffectsItemsStale();
            // First descriptor admission time is provenance, not the current source generation.
            EffectsSnapshot(pair.First with { PreparedAt = pair.Current.PreparedAt, Source = pair.Current.Source, Association = pair.Current.Association }, pair.Current, opening);
        }
        token.ThrowIfCancellationRequested();
        return new(new(artifact, at), item, sparse, configurations);
    }

    internal Task<StoredTableAdmission?> ReadItemEffectsSourceAsync(CapturedOpeningContext opening, ItemEffectsSourceRole role, CancellationToken token)
    {
        var table=EffectsTable(role);
        return Schedule(() =>
        {
            using var transaction=Db.BeginTransaction();EffectsOpening(opening,transaction);var budget=EffectsSourceBytes;
            var configurations=EffectsConfigurationsRaw(opening,transaction,ref budget);
            var raw=EffectsSourceRaw(opening,table,transaction,ref budget);
            _=EffectsConfigurations(opening,configurations);
            var result=raw is null?null:EffectsSource(raw,table);
            token.ThrowIfCancellationRequested();transaction.Commit();return result;
        },token);
    }
    private static string EffectsTable(ItemEffectsSourceRole role)=>role switch
    {
        ItemEffectsSourceRole.Bridge=>"00cb674f",ItemEffectsSourceRole.Effect=>"4002a5b1",ItemEffectsSourceRole.Name=>"46c66698",
        _=>throw new DiscoveryException("INVALID_REQUEST","Choose one exact effects source role.")
    };
    private static ItemEffectsItemsProof EffectsCopyItems(ItemEffectsItemsProof items)
    {
        if(items?.Opening?.Build is null||items.Opening.Reference is null||items.Opening.Versions is null||items.Opening.Cdns is null||items.Configurations is null)throw EffectsItemsInvalid();
        var opening=items.Opening;var build=opening.Build;var reference=opening.Reference;
        if(!DownloadHash(build.Id,64)||!DownloadHash(build.BuildConfig,32)||!DownloadHash(build.CdnConfig,32)||build.ProductId is null||build.ProductId.Length<1||build.ProductId.Length>ProductRegistry.MaxIdBytes||
            build.Version is null||build.Version.Length is <1 or >43||!DownloadHash(reference.VersionsSha256,64)||!DownloadHash(reference.CdnsSha256,64)||
            reference.Region is null||reference.Region.Length is <1 or >16||reference.VersionsRowOrdinal<0||reference.CdnsRowOrdinal<0||
            !long.TryParse(reference.Generation,NumberStyles.None,CultureInfo.InvariantCulture,out var generation)||generation<1||generation.ToString(CultureInfo.InvariantCulture)!=reference.Generation||
            opening.Versions.Role!="versions"||opening.Cdns.Role!="cdns"||opening.Versions.Sha256!=reference.VersionsSha256||opening.Cdns.Sha256!=reference.CdnsSha256)throw EffectsItemsInvalid();
        var artifact=EffectsDecode<ItemsProjectionArtifact>(EffectsBytes(items.Artifact,EffectsItemsBytes));
        var snapshot=EffectsDecode<ItemsSnapshot>(EffectsBytes(items.Snapshot,EffectsItemsBytes));
        var summary=EffectsDecode<AuthorizedItemSummary>(EffectsBytes(items.Item,8<<10));
        int count;try{count=items.Configurations.Count;}catch(Exception error)when(error is ArgumentException or InvalidOperationException){throw EffectsItemsInvalid();}
        if(count!=2)throw EffectsItemsInvalid();var configurations=new ConfigurationReceipt[2];
        for(var i=0;i<2;i++)
        {
            ConfigurationReceipt value;try{value=items.Configurations[i];}catch(Exception error)when(error is ArgumentException or IndexOutOfRangeException or InvalidOperationException){throw EffectsItemsInvalid();}
            if(value is null)throw EffectsItemsInvalid();configurations[i]=value with{};
        }
        if(configurations.Count(c=>c.Role=="buildConfig")!=1||configurations.Count(c=>c.Role=="cdnConfig")!=1)throw EffectsItemsInvalid();
        _=EffectsBytes(configurations,8<<10);
        // Opening documents retain their separately bounded owned bytes; SQL consumes only
        // the copied immutable scalar authority, never serializes/duplicates those raw documents.
        return new(opening with{Build=build with{},Reference=reference with{}},artifact,snapshot,summary,Array.AsReadOnly(configurations));
    }
    private ItemEffectsItemsAuthority EffectsRequireItems(ItemEffectsItemsProof items,SqliteTransaction transaction,CancellationToken token)
    {
        if(items.Opening is null||items.Artifact is null||items.Snapshot is null||items.Snapshot.Context is null||items.Item is null||items.Item.BaseValues is null||
            !CanonicalUInt(items.Item.ItemId)||items.Item.BaseValues.Count>8||!items.Item.Admitted&&items.Item.BaseValues.Count!=0)throw EffectsItemsInvalid();
        var current=EffectsItemsAuthority(items.Opening,items.Snapshot.Context.ResearchLocale,items.Artifact.ContentKey,transaction,token);
        if(items.Configurations.Count!=2||items.Configurations.Any(c=>!current.Configurations.Any(original=>original.Role==c.Role&&EffectsAuthority(original,c))))throw EffectsItemsStale();
        var first=current.Items.Artifact;var b=first.Binding;
        var snapshot=new ItemsSnapshot(b.Context,first.ContentKey,current.Items.PreparedAt,first.FeatureRevision,ItemsProjection.QueryPolicy,
            b.ItemSource with{PreparedAt=current.Item.PreparedAt,Source=current.Item.Source,Association=current.Item.Association},
            b.SparseSource with{PreparedAt=current.Sparse.PreparedAt,Source=current.Sparse.Source,Association=current.Sparse.Association},first.Counts,first.Groups);
        if(!EffectsAuthority(first,items.Artifact)||!EffectsAuthority(snapshot,items.Snapshot))throw EffectsItemsStale();
        foreach(var value in items.Item.BaseValues)
        {
            if(value is null||value.Value is null||value.Value.Length>64)throw EffectsItemsInvalid();
            var source=value.TableId==ItemsProjection.ItemTable?b.ItemSource:value.TableId==ItemsProjection.SparseTable?b.SparseSource:null;
            if(source is null||value.ColumnIndex<0||value.ColumnIndex>=source.Columns.Count)throw EffectsItemsInvalid();
        }
        return current;
    }

    internal Task<ItemEffectsBridgeArtifact?> FindItemEffectsBridgeArtifactAsync(string contentKey,CancellationToken token)
    {
        if(!DownloadHash(contentKey,64))throw EffectsIndexInvalid();
        return Schedule(()=>{var budget=EffectsSourceBytes;return EffectsBridgeDescriptor(contentKey,null,ref budget);},token);
    }
    internal Task RecordItemEffectsBridgeArtifactAsync(ItemEffectsBridgeArtifact artifact,CancellationToken token)
    {
        var owned=EffectsDecode<ItemEffectsBridgeArtifact>(EffectsBytes(artifact,EffectsSourceBytes));ItemEffectsBridgePolicy.ValidateArtifact(owned);
        return Schedule(()=>
        {
            using var transaction=Db.BeginTransaction();var budget=EffectsSourceBytes;
            var previous=EffectsBridgeDescriptor(owned.ContentKey,transaction,ref budget);
            if(previous is not null&&(previous.Sha256!=owned.Sha256||previous.ByteLength!=owned.ByteLength))throw EffectsIndexInvalid();
            Execute("INSERT OR IGNORE INTO item_effects_bridge_artifacts(content_key,descriptor) VALUES($k,$d)",transaction,
                ("$k",owned.ContentKey),("$d",EffectsUtf8(EffectsBytes(owned,EffectsSourceBytes))));
            token.ThrowIfCancellationRequested();transaction.Commit();return true;
        },token);
    }
    internal Task<StoredItemEffectsBridgeAdmission?> ReadItemEffectsBridgeAdmissionAsync(CapturedOpeningContext opening,CancellationToken token)=>Schedule(()=>
    {
        using var transaction=Db.BeginTransaction();EffectsOpening(opening,transaction);var budget=EffectsSourceBytes;
        var current=EffectsBridgeAdmission(opening,transaction,ref budget);
        token.ThrowIfCancellationRequested();transaction.Commit();return current;
    },token);
    internal Task<StoredItemEffectsBridgeAdmission> CommitItemEffectsBridgeAdmissionAsync(ItemEffectsItemsProof items,
        ItemEffectsBridgeArtifact bridge,string preparedAt,CancellationToken token)
    {
        var ownedItems=EffectsCopyItems(items);var owned=EffectsDecode<ItemEffectsBridgeArtifact>(EffectsBytes(bridge,EffectsSourceBytes));
        ItemEffectsBridgePolicy.ValidateArtifact(owned);if(!DownloadUtc(preparedAt))throw EffectsIndexInvalid();
        return Schedule(()=>
        {
            using var transaction=Db.BeginTransaction();var authority=EffectsRequireItems(ownedItems,transaction,token);
            var budget=EffectsSourceBytes;var savedRaw=EffectsBridgeDescriptorRaw(owned.ContentKey,transaction,ref budget)??throw EffectsIndexInvalid();
            var configurationRaw=EffectsConfigurationsRaw(ownedItems.Opening,transaction,ref budget);
            var sourceRaw=EffectsSourceRaw(ownedItems.Opening,ItemEffectsBridgePolicy.Table,transaction,ref budget)??throw EffectsBridgeStale();
            var previous=EffectsBridgeCompact(ownedItems.Opening,transaction,ref budget);
            var configurations=EffectsConfigurations(ownedItems.Opening,configurationRaw);
            var saved=EffectsBridgeDecode(savedRaw,owned.ContentKey);var source=EffectsSource(sourceRaw,ItemEffectsBridgePolicy.Table);
            // Re-registration may carry a newer source observation, but immutable first provenance is canonical.
            if(saved.Sha256!=owned.Sha256||saved.ByteLength!=owned.ByteLength||
                !ItemEffectsBridgePolicy.SameCurrentSource(saved.Binding.Source,owned.Binding.Source)||
                !EffectsAuthority(saved.Binding.Rows,owned.Binding.Rows)||!EffectsAuthority(saved.Binding.Configurations,owned.Binding.Configurations)||
                saved.Binding.Context!=owned.Binding.Context||saved.Binding.SourceReference!=owned.Binding.SourceReference||saved.Binding.BuildVersion!=owned.Binding.BuildVersion||saved.Binding.Fields!=owned.Binding.Fields||saved.RowCount!=owned.RowCount)throw EffectsIndexInvalid();
            if(source.PreparedAt!=owned.Binding.Source.PreparedAt)throw EffectsBridgeStale();
            EffectsSnapshot(owned.Binding.Source,source,ownedItems.Opening);
            EffectsBridgeBindingCurrent(ownedItems.Opening,saved,configurations,source);
            var at=previous?.Key==saved.ContentKey?EffectsUtf8(previous.Value.PreparedAt):preparedAt;
            if(!DownloadUtc(at))throw EffectsIndexInvalid();
            Execute("""
                INSERT INTO item_effects_bridge_admissions(build_id,region,source_reference,locale,content_key,prepared_at)
                VALUES($b,$r,$ref,'enUS',$k,$at) ON CONFLICT(build_id,region,source_reference,locale)
                DO UPDATE SET content_key=excluded.content_key,prepared_at=excluded.prepared_at
                """,transaction,("$b",ownedItems.Opening.Build.Id),("$r",ownedItems.Opening.Reference.Region),("$ref",Serialize(ownedItems.Opening.Reference)),("$k",saved.ContentKey),("$at",at));
            token.ThrowIfCancellationRequested();transaction.Commit();return new StoredItemEffectsBridgeAdmission(saved,at);
        },token);
    }
    internal Task<IReadOnlyList<ItemEffectsTargetFenceResult>> ReauthorizeItemEffectsAsync(ItemEffectsItemsProof items,
        ItemEffectsBridgeProof? bridge,IReadOnlyList<ItemEffectsTargetProof> targets,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var ownedItems=EffectsCopyItems(items);
        var ownedBridge=bridge is null?null:EffectsDecode<ItemEffectsBridgeProof>(EffectsBytes(bridge,EffectsItemsBytes));
        if(targets is null)throw EffectsIndexInvalid();int count;
        try{count=targets.Count;}catch(Exception error)when(error is ArgumentException or InvalidOperationException){throw EffectsIndexInvalid();}
        if(count is <0 or >2)throw EffectsIndexInvalid();
        if(count>0&&ownedBridge?.Index is null)throw new DiscoveryException("INVALID_REQUEST","Consulted effects targets require the current bridge index proof.");
        var owned=new ItemEffectsTargetProof[count];var previous=-1;
        for(var i=0;i<count;i++)
        {
            ItemEffectsTargetProof value;
            try{value=targets[i];}catch(Exception error)when(error is ArgumentException or IndexOutOfRangeException or InvalidOperationException){throw EffectsIndexInvalid();}
            if(value is null||value.Role is not(ItemEffectsSourceRole.Effect or ItemEffectsSourceRole.Name)||(int)value.Role<=previous)throw EffectsIndexInvalid();
            previous=(int)value.Role;owned[i]=EffectsDecode<ItemEffectsTargetProof>(EffectsBytes(value,EffectsItemsBytes));
            if(owned[i].Admission is null||owned[i].Snapshot is null)throw EffectsIndexInvalid();
        }
        return Schedule<IReadOnlyList<ItemEffectsTargetFenceResult>>(()=>
        {
            using var transaction=Db.BeginTransaction();var authority=EffectsRequireItems(ownedItems,transaction,token);
            if(ownedBridge is not null)EffectsRequireBridge(ownedItems.Opening,ownedBridge,authority.Configurations,transaction);
            var results=new ItemEffectsTargetFenceResult[owned.Length];
            for(var i=0;i<owned.Length;i++)
            {
                token.ThrowIfCancellationRequested();ItemEffectsProblem? problem=null;
                try
                {
                    var target=owned[i];var table=EffectsTable(target.Role);var budget=EffectsSourceBytes;
                    var configurations=EffectsConfigurationsRaw(ownedItems.Opening,transaction,ref budget);
                    var raw=EffectsSourceRaw(ownedItems.Opening,table,transaction,ref budget);
                    _=EffectsConfigurations(ownedItems.Opening,configurations);
                    if(raw is null)problem=EffectsTargetStale();
                    else
                    {
                        var current=EffectsSource(raw,table);EffectsSnapshot(target.Snapshot,target.Admission,ownedItems.Opening);
                        if(current.PreparedAt!=target.Admission.PreparedAt||!EffectsAuthority(current.Artifact,target.Admission.Artifact)||
                            !EffectsAuthority(current.Source,target.Admission.Source)||!EffectsAuthority(current.Association,target.Admission.Association))problem=EffectsTargetStale();
                    }
                }
                catch(DiscoveryException error)
                {
                    problem=error.Code=="ITEM_EFFECTS_LIMIT"?new("ITEM_EFFECTS_LIMIT","The effects resource limit was exceeded.",false):
                        new("ITEM_EFFECTS_SOURCE_CORRUPT","The selected source failed integrity validation.",false);
                }
                results[i]=new(owned[i].Role,problem);
            }
            token.ThrowIfCancellationRequested();transaction.Commit();return results;
        },token);
    }
    private static ItemEffectsProblem EffectsTargetStale()=>new("ITEM_EFFECTS_TARGET_STALE","The selected target source changed.",true);
    private ItemEffectsBridgeArtifact? EffectsBridgeDescriptor(string key,SqliteTransaction? transaction,ref int budget)
    {
        var bytes=EffectsBridgeDescriptorRaw(key,transaction,ref budget);return bytes is null?null:EffectsBridgeDecode(bytes,key);
    }
    private byte[]? EffectsBridgeDescriptorRaw(string key,SqliteTransaction? transaction,ref int budget)
    {
        using var command=Command("SELECT CAST(descriptor AS BLOB),length(CAST(descriptor AS BLOB)),CAST(content_key AS BLOB),length(CAST(content_key AS BLOB)) FROM item_effects_bridge_artifacts WHERE content_key=$k",transaction,("$k",key));
        using var reader=command.ExecuteReader();if(!reader.Read())return null;
        EffectsSizes(reader,[1],ref budget);if(EffectsHash(reader,2,3,64)!=key)throw EffectsIndexInvalid();return EffectsBlob(reader,0);
    }
    private static ItemEffectsBridgeArtifact EffectsBridgeDecode(byte[] bytes,string key)
    {
        var value=EffectsDecode<ItemEffectsBridgeArtifact>(bytes);ItemEffectsBridgePolicy.ValidateArtifact(value);if(value.ContentKey!=key)throw EffectsIndexInvalid();return value;
    }
    private (string Key,byte[] PreparedAt)? EffectsBridgeCompact(CapturedOpeningContext opening,SqliteTransaction transaction,ref int budget)
    {
        using var command=Command("""
            SELECT CAST(content_key AS BLOB),CAST(prepared_at AS BLOB),length(CAST(content_key AS BLOB)),length(CAST(prepared_at AS BLOB))
            FROM item_effects_bridge_admissions WHERE build_id=$b AND region=$r AND source_reference=$ref AND locale='enUS'
            """,transaction,("$b",opening.Build.Id),("$r",opening.Reference.Region),("$ref",Serialize(opening.Reference)));
        using var reader=command.ExecuteReader();if(!reader.Read())return null;
        var key=EffectsHash(reader,0,2,64);EffectsSizes(reader,[3],ref budget);return(key,EffectsBlob(reader,1));
    }
    private StoredItemEffectsBridgeAdmission? EffectsBridgeAdmission(CapturedOpeningContext opening,SqliteTransaction transaction,ref int budget)
    {
        var compact=EffectsBridgeCompact(opening,transaction,ref budget);if(compact is null)return null;
        var bytes=EffectsBridgeDescriptorRaw(compact.Value.Key,transaction,ref budget)??throw EffectsIndexInvalid();
        var at=EffectsUtf8(compact.Value.PreparedAt);if(!DownloadUtc(at))throw EffectsIndexInvalid();
        return new(EffectsBridgeDecode(bytes,compact.Value.Key),at);
    }
    private static void EffectsBridgeBindingCurrent(CapturedOpeningContext opening,ItemEffectsBridgeArtifact artifact,
        IReadOnlyList<ConfigurationReceipt> configurations,StoredTableAdmission current)
    {
        var b=artifact.Binding;
        if(b.SourceReference!=opening.Reference||b.BuildVersion!=opening.Build.Version||b.Context.ProductId!=opening.Build.ProductId||
            b.Context.BuildIdentity!=opening.Build.Id||b.Context.SourceRegion!=opening.Reference.Region||b.Context.ResearchLocale!="enUS"||
            b.Configurations.Count!=2||b.Configurations.Any(c=>!configurations.Any(original=>original.Role==c.Role&&EffectsAuthority(original,c))))throw EffectsBridgeStale();
        EffectsSnapshot(b.Source with{PreparedAt=current.PreparedAt,Source=current.Source,Association=current.Association},current,opening);
        if(!EffectsAuthority(current.Artifact,b.Rows)||!EffectsAuthority(current.Source,b.Source.Source)||!EffectsAuthority(current.Association,b.Source.Association))throw EffectsBridgeStale();
    }
    private void EffectsRequireBridge(CapturedOpeningContext opening,ItemEffectsBridgeProof proof,
        IReadOnlyList<ConfigurationReceipt> configurations,SqliteTransaction transaction)
    {
        var budget=EffectsSourceBytes;var configurationRaw=EffectsConfigurationsRaw(opening,transaction,ref budget);
        var raw=EffectsSourceRaw(opening,ItemEffectsBridgePolicy.Table,transaction,ref budget)??throw EffectsBridgeStale();
        (string Key,byte[] PreparedAt)? compact=proof.Index is null?null:EffectsBridgeCompact(opening,transaction,ref budget)??throw EffectsBridgeStale();
        var descriptor=compact is null?null:EffectsBridgeDescriptorRaw(compact.Value.Key,transaction,ref budget)??throw EffectsBridgeStale();
        _=EffectsConfigurations(opening,configurationRaw);
        var current=EffectsSource(raw,ItemEffectsBridgePolicy.Table);EffectsSnapshot(proof.Snapshot,proof.Admission,opening);
        if(current.PreparedAt!=proof.Admission.PreparedAt||!EffectsAuthority(current.Artifact,proof.Admission.Artifact)||
            !EffectsAuthority(current.Source,proof.Admission.Source)||!EffectsAuthority(current.Association,proof.Admission.Association))throw EffectsBridgeStale();
        if(proof.Index is null)return;
        var at=EffectsUtf8(compact!.Value.PreparedAt);if(!DownloadUtc(at))throw EffectsIndexInvalid();
        var saved=EffectsBridgeDecode(descriptor!,compact.Value.Key);
        if(at!=proof.Index.PreparedAt||!EffectsAuthority(saved,proof.Index.Artifact))throw EffectsBridgeStale();
        EffectsBridgeBindingCurrent(opening,saved,configurations,current);
        if(!ItemEffectsBridgePolicy.SameCurrentSource(saved.Binding.Source,proof.Snapshot))throw EffectsBridgeStale();
    }

    private void EffectsOpening(CapturedOpeningContext opening, SqliteTransaction transaction)
    {
        if (opening is null || opening.Build is null || opening.Reference is null) throw EffectsItemsInvalid();
        using var command = Command("""
            SELECT r.generation,r.ordinal,r.cdns_ordinal,CAST(v.sha256 AS BLOB),CAST(c.sha256 AS BLOB),
                length(CAST(v.sha256 AS BLOB)),length(CAST(c.sha256 AS BLOB)) FROM build_regions r
            JOIN documents v ON v.generation=r.generation AND v.role='versions'
            JOIN documents c ON c.generation=r.generation AND c.role='cdns'
            WHERE r.build_id=$b AND r.region=$r
            """, transaction, ("$b", opening.Build.Id), ("$r", opening.Reference.Region));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw EffectsItemsStale();
        var versions = EffectsHash(reader, 3, 5, 64); var cdns = EffectsHash(reader, 4, 6, 64);
        if (reader.GetInt64(0).ToString(CultureInfo.InvariantCulture) != opening.Reference.Generation ||
            reader.GetInt32(1) != opening.Reference.VersionsRowOrdinal || reader.IsDBNull(2) || reader.GetInt32(2) != opening.Reference.CdnsRowOrdinal ||
            versions != opening.Reference.VersionsSha256 || cdns != opening.Reference.CdnsSha256) throw EffectsItemsStale();
        reader.Close();
        try { RequireTableWorkspace(opening, transaction); }
        catch (DiscoveryException) { throw EffectsItemsStale(); }
    }

    private EffectsRawConfiguration[] EffectsConfigurationsRaw(CapturedOpeningContext opening, SqliteTransaction transaction, ref int budget)
    {
        var id = Scalar("SELECT id FROM workspace_admissions WHERE build_id=$b AND product=$p AND region=$r AND source_reference=$ref ORDER BY id DESC LIMIT 1", transaction,
            ("$b", opening.Build.Id), ("$p", opening.Build.ProductId), ("$r", opening.Reference.Region), ("$ref", Serialize(opening.Reference))) ?? throw EffectsItemsStale();
        var values = new List<EffectsRawConfiguration>(2);
        using var command = Command("""
            SELECT CAST(w.role AS BLOB),CAST(w.key AS BLOB),CAST(w.sha256 AS BLOB),w.byte_length,CAST(w.source_url AS BLOB),CAST(w.retrieved_at AS BLOB),CAST(w.verified_at AS BLOB),w.parsed_field_count,
                CAST(c.sha256 AS BLOB),c.byte_length,CAST(c.source_url AS BLOB),CAST(c.retrieved_at AS BLOB),CAST(c.verifier_revision AS BLOB),
                length(CAST(w.role AS BLOB)),length(CAST(w.key AS BLOB)),length(CAST(w.sha256 AS BLOB)),
                length(CAST(w.source_url AS BLOB)),length(CAST(w.retrieved_at AS BLOB)),length(CAST(w.verified_at AS BLOB)),
                length(CAST(c.sha256 AS BLOB)),length(CAST(c.source_url AS BLOB)),length(CAST(c.retrieved_at AS BLOB)),length(CAST(c.verifier_revision AS BLOB))
            FROM workspace_configurations w LEFT JOIN configuration_objects c ON c.key=w.key
            WHERE w.admission_id=$id ORDER BY w.role LIMIT 3
            """, transaction, ("$id", id));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (values.Count == 2) throw EffectsLimit();
            EffectsSizes(reader, [13,14,15,16,17,18,19,20,21,22], ref budget);
            if (reader.GetInt64(14) != 32 || reader.GetInt64(15) != 64 || reader.GetInt64(19) != 64) throw EffectsItemsInvalid();
            values.Add(new([EffectsBlob(reader,0),EffectsBlob(reader,1),EffectsBlob(reader,2),EffectsBlob(reader,4),EffectsBlob(reader,5),EffectsBlob(reader,6),
                EffectsBlob(reader,8),EffectsBlob(reader,10),EffectsBlob(reader,11),EffectsBlob(reader,12)],reader.GetInt32(3),reader.GetInt32(7),reader.GetInt32(9)));
        }
        return values.ToArray();
    }
    private static IReadOnlyList<ConfigurationReceipt> EffectsConfigurations(CapturedOpeningContext opening, EffectsRawConfiguration[] raw)
    {
        var values=new ConfigurationReceipt[raw.Length];
        for(var i=0;i<raw.Length;i++)
        {
            var row=raw[i];var text=row.Text.Select(EffectsUtf8).ToArray();
            var value=new ConfigurationReceipt(text[0],text[1],text[2],row.ByteLength,text[3],text[4],text[5],row.ParsedFields);
            if (!DownloadHash(value.Key, 32) || !DownloadHash(value.Sha256, 64) || value.Sha256 != text[6] || value.ByteLength != row.OriginalByteLength ||
                value.SourceUrl != text[7] || value.RetrievedAt != text[8] || text[9] != ConfigurationWorkspacePreparer.VerifierRevision ||
                !DownloadUtc(value.RetrievedAt) || !DownloadUtc(value.VerifiedAt) || value.ByteLength is < 1 or > (1 << 20) || value.ParsedFieldCount is < 0 or > 4096) throw EffectsItemsInvalid();
            values[i]=value;
        }
        if (values.Length != 2 || values.Count(v => v.Role == "buildConfig" && v.Key == opening.Build.BuildConfig) != 1 ||
            values.Count(v => v.Role == "cdnConfig" && v.Key == opening.Build.CdnConfig) != 1) throw EffectsItemsInvalid();
        return Array.AsReadOnly(values);
    }

    private EffectsRawSource? EffectsSourceRaw(CapturedOpeningContext opening, string tableId, SqliteTransaction transaction, ref int budget)
    {
        byte[][] raw; string key;
        using (var command = Command("""
            SELECT CAST(a.source AS BLOB),CAST(a.association AS BLOB),CAST(r.descriptor AS BLOB),CAST(a.prepared_at AS BLOB),CAST(a.content_key AS BLOB),
                length(CAST(a.source AS BLOB)),length(CAST(a.association AS BLOB)),length(CAST(r.descriptor AS BLOB)),length(CAST(a.prepared_at AS BLOB)),length(CAST(a.content_key AS BLOB))
            FROM table_admissions a LEFT JOIN table_artifacts r ON r.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.table_id=$t AND a.locale='enUS'
            """, transaction, ("$b", opening.Build.Id), ("$r", opening.Reference.Region), ("$ref", Serialize(opening.Reference)), ("$t", tableId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            EffectsSizes(reader, [5,6,7,8], ref budget); key = EffectsHash(reader, 4, 9, 64);
            raw = [EffectsBlob(reader,0), EffectsBlob(reader,1), EffectsBlob(reader,2), EffectsBlob(reader,3)];
        }
        EffectsPreflight(raw[0],EffectsShape.Content);EffectsPreflight(raw[1],EffectsShape.Association);EffectsPreflight(raw[2],EffectsShape.Rows);
        var sourceKey = EffectsJsonKey(raw[0], "encodingKey");
        var encodingKey = EffectsJsonKey(raw[1], "encodingSource", "encodingKey");
        var rootKey = EffectsJsonKey(raw[1], "rootSource", "encodingKey");
        return new(raw[0],raw[1],raw[2],raw[3],key,
            EffectsCanonical("content_objects",sourceKey,transaction,ref budget),
            EffectsCanonical("metadata_objects",encodingKey,transaction,ref budget),
            EffectsCanonical("metadata_objects",rootKey,transaction,ref budget));
    }

    private static StoredTableAdmission EffectsSource(EffectsRawSource raw, string tableId)
    {
        try
        {
            var source = EffectsDecode<ContentReceipt>(raw.Source); var association = EffectsDecode<SelectedFileAssociation>(raw.Association);
            var rows = EffectsDecode<TableRowsArtifact>(raw.Artifact); var at = EffectsUtf8(raw.PreparedAt);
            if (rows.ContentKey != raw.Key || rows.Binding is null || rows.Header?.Columns is null || rows.Completion is null ||
                association.EncodingSource is null || association.RootSource is null || association.Memberships is null || association.Memberships.Any(v => v is null)) throw EffectsItemsInvalid();
            ValidateTableAdmission(tableId,"enUS",source,association,rows,at,requireCurrentRevision:false);
            var canonical = EffectsDecode<ContentReceipt>(raw.Content); ValidateContentReceipt(canonical);
            var encoding = EffectsDecode<MetadataReceipt>(raw.Encoding); var root = EffectsDecode<MetadataReceipt>(raw.Root);
            ValidateDownloadReceiptFacts(encoding); ValidateDownloadReceiptFacts(root);
            if (!EffectsAuthority(source,canonical) || !EffectsAuthority(association.EncodingSource,encoding) || !EffectsAuthority(association.RootSource,root)) throw EffectsItemsInvalid();
            return new(rows,source,association,at);
        }
        catch (DiscoveryException error) when (error.Code is not "ITEM_EFFECTS_LIMIT") { throw EffectsItemsInvalid(); }
        catch (Exception error) when (error is NullReferenceException or FormatException or OverflowException or ArgumentException) { throw EffectsItemsInvalid(); }
    }

    private byte[] EffectsCanonical(string table, string key, SqliteTransaction transaction, ref int budget)
    {
        // Fixed internal table names only.
        using var command = Command($"SELECT CAST(receipt AS BLOB),length(CAST(receipt AS BLOB)) FROM {table} WHERE ekey=$k", transaction,("$k",key));
        using var reader=command.ExecuteReader(); if(!reader.Read()) throw EffectsItemsInvalid();
        EffectsSizes(reader,[1],ref budget); var bytes=EffectsBlob(reader,0); EffectsPreflight(bytes,table=="content_objects"?EffectsShape.Content:EffectsShape.Unknown); return bytes;
    }
    private byte[]? EffectsDescriptor(string table,string key,SqliteTransaction transaction,ref int budget)
    {
        using var command=Command($"SELECT CAST(descriptor AS BLOB),length(CAST(descriptor AS BLOB)),CAST(content_key AS BLOB),length(CAST(content_key AS BLOB)) FROM {table} WHERE content_key=$k",transaction,("$k",key));
        using var reader=command.ExecuteReader(); if(!reader.Read())return null;
        EffectsSizes(reader,[1],ref budget); if(EffectsHash(reader,2,3,64)!=key)throw EffectsItemsInvalid();
        return EffectsBlob(reader,0);
    }
    private static string EffectsJsonKey(byte[] bytes, params string[] path)
    {
        // Only bounded raw tokens are inspected to find the canonical receipt keys. No DOM/DTO
        // is constructed before all original receipt lengths have joined the role budget.
        try
        {
            var reader=new Utf8JsonReader(bytes,new JsonReaderOptions{MaxDepth=32});var parent=path.Length==1;
            while(reader.Read())
            {
                if(reader.TokenType!=JsonTokenType.PropertyName)continue;
                if(path.Length==2&&reader.CurrentDepth==1&&reader.ValueTextEquals(path[0]))
                { if(!reader.Read()||reader.TokenType!=JsonTokenType.StartObject)throw EffectsItemsInvalid();parent=true;continue; }
                if(parent&&reader.CurrentDepth==path.Length&&reader.ValueTextEquals(path[^1]))
                {
                    if(!reader.Read()||reader.TokenType!=JsonTokenType.String||reader.ValueSpan.Length>192)throw EffectsItemsInvalid();
                    Span<char> key=stackalloc char[33];var count=reader.CopyString(key);
                    if(count!=32)throw EffectsItemsInvalid();var value=new string(key[..count]);if(!DownloadHash(value,32))throw EffectsItemsInvalid();return value;
                }
                if(path.Length==2&&reader.CurrentDepth==1)parent=false;
            }
            throw EffectsItemsInvalid();
        }
        catch(JsonException){throw EffectsItemsInvalid();}
        catch(ArgumentException){throw EffectsItemsInvalid();}
    }
    private static void EffectsSizes(SqliteDataReader reader,int[] columns,ref int budget)
    {
        long total=0;
        foreach(var column in columns)
        {
            if(reader.IsDBNull(column))throw EffectsItemsInvalid(); var size=reader.GetInt64(column);
            if(size<1 || size>budget)throw EffectsLimit(); total+=size;
        }
        if(total>budget)throw EffectsLimit(); budget-=checked((int)total);
    }
    private static string EffectsHash(SqliteDataReader reader,int blob,int length,int width)
    {
        if(reader.IsDBNull(length)||reader.GetInt64(length)!=width)throw EffectsItemsInvalid();
        var value=EffectsUtf8(EffectsBlob(reader,blob)); if(!DownloadHash(value,width))throw EffectsItemsInvalid(); return value;
    }
    private static byte[] EffectsBlob(SqliteDataReader reader,int column)=>reader.GetFieldValue<byte[]>(column);
    private static string EffectsUtf8(byte[] bytes)
    { try{return new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){throw EffectsItemsInvalid();} }
    private static T EffectsDecode<T>(byte[] bytes)
    {
        EffectsPreflight(bytes,EffectsRoot<T>());
        try{return JsonSerializer.Deserialize<T>(bytes,EffectsJsonOptions)??throw EffectsItemsInvalid();}
        catch(JsonException){throw EffectsItemsInvalid();}
    }
    private static void EffectsValidateItems(ItemsProjectionArtifact artifact)
    {
        try{ItemsProjection.ValidateArtifact(artifact);}
        catch(Exception error)when(error is NullReferenceException or FormatException or OverflowException or ArgumentException){throw EffectsItemsInvalid();}
    }
    private static bool EffectsAuthority<T>(T left,T right)
        => EffectsCompareAuthority(left,right);
    private static byte[] EffectsBytes<T>(T value,int maximum)
    {
        try { var output=new EffectsWriter(maximum);using var writer=new Utf8JsonWriter(output);JsonSerializer.Serialize(writer,value,EffectsJsonOptions);writer.Flush();return output.Written(); }
        catch(JsonException){throw EffectsItemsInvalid();}
    }
    private sealed class EffectsWriter(int maximum):Stream
    {
        private readonly byte[] bytes=new byte[maximum];private int count;
        public override void Write(ReadOnlySpan<byte> value){if(value.Length>bytes.Length-count)throw EffectsLimit();value.CopyTo(bytes.AsSpan(count));count+=value.Length;}
        public override void Write(byte[] buffer,int offset,int length)=>Write(buffer.AsSpan(offset,length));
        internal byte[] Written()=>bytes.AsSpan(0,count).ToArray();
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>count;public override long Position{get=>count;set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] buffer,int offset,int length)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    }
    private enum EffectsShape { Unknown, Feature, FeatureBinding, Snapshot, Rows, Header, Association, Content, Projection,
        ItemsSnapshot, SourceBundle, BridgeArtifact, BridgeBinding, StoredBridge, ItemsProof, SourceProof, Summary, Group,
        Columns, Memberships, Configurations, Groups, Fields, Ranges }
    private static EffectsShape EffectsRoot<T>() => typeof(T)==typeof(ItemsProjectionArtifact)?EffectsShape.Feature:
        typeof(T)==typeof(TableRowsArtifact)?EffectsShape.Rows:typeof(T)==typeof(TableDataSnapshot)?EffectsShape.Snapshot:
        typeof(T)==typeof(SelectedFileAssociation)?EffectsShape.Association:typeof(T)==typeof(ContentReceipt)?EffectsShape.Content:
        typeof(T)==typeof(ItemsSnapshot)?EffectsShape.ItemsSnapshot:typeof(T)==typeof(StoredTableAdmission)?EffectsShape.SourceBundle:
        typeof(T)==typeof(ItemEffectsBridgeArtifact)?EffectsShape.BridgeArtifact:typeof(T)==typeof(StoredItemEffectsBridgeAdmission)?EffectsShape.StoredBridge:
        typeof(T)==typeof(ItemEffectsItemsProof)?EffectsShape.ItemsProof:typeof(T)==typeof(ItemEffectsBridgeProof)||typeof(T)==typeof(ItemEffectsTargetProof)?EffectsShape.SourceProof:
        typeof(T)==typeof(AuthorizedItemSummary)?EffectsShape.Summary:EffectsShape.Unknown;
    private static void EffectsPreflight(ReadOnlySpan<byte> bytes,EffectsShape root=EffectsShape.Unknown)
    {
        Span<int> limits=stackalloc int[33],counts=stackalloc int[33];Span<EffectsShape> contexts=stackalloc EffectsShape[33];
        var names=new HashSet<string>?[34];var properties=new string?[33];Span<char> propertyName=stackalloc char[97];
        try
        {
            var reader=new Utf8JsonReader(bytes,new JsonReaderOptions{MaxDepth=64});
            while(reader.Read())
            {
                var depth=reader.CurrentDepth;var type=reader.TokenType;
                if(depth>=32&&type is JsonTokenType.StartObject or JsonTokenType.StartArray)throw EffectsLimit();
                if(depth>0&&limits[depth-1]>0&&type is not(JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray))
                    if(++counts[depth-1]>limits[depth-1])throw EffectsLimit();
                if(type==JsonTokenType.PropertyName)
                {
                    if(reader.ValueSpan.Length>576)throw EffectsItemsInvalid();var count=reader.CopyString(propertyName);
                    if(count>96)throw EffectsItemsInvalid();var property=new string(propertyName[..count]);
                    if(!names[depth]!.Add(property))throw EffectsItemsInvalid();properties[depth]=property.ToLowerInvariant();
                }
                else if(type==JsonTokenType.StartObject)
                {
                    names[depth+1]=new(StringComparer.OrdinalIgnoreCase);limits[depth]=0;
                    contexts[depth]=depth==0?root:(contexts[depth-1],properties[depth]) switch
                    {
                        (EffectsShape.Feature,"binding")=>EffectsShape.FeatureBinding,(EffectsShape.BridgeArtifact,"binding")=>EffectsShape.BridgeBinding,
                        (EffectsShape.FeatureBinding,"itemsource" or "sparsesource")=>EffectsShape.Snapshot,
                        (EffectsShape.FeatureBinding,"itemrows" or "sparserows")=>EffectsShape.Rows,
                        (EffectsShape.BridgeBinding,"source")=>EffectsShape.Snapshot,(EffectsShape.BridgeBinding,"rows")=>EffectsShape.Rows,
                        (EffectsShape.Snapshot,"source")=>EffectsShape.Content,(EffectsShape.Snapshot,"association")=>EffectsShape.Association,
                        (EffectsShape.Rows,"header")=>EffectsShape.Header,(EffectsShape.Content,"projection")=>EffectsShape.Projection,
                        (EffectsShape.ItemsSnapshot,"itemsource" or "sparsesource")=>EffectsShape.Snapshot,
                        (EffectsShape.SourceBundle,"artifact")=>EffectsShape.Rows,(EffectsShape.SourceBundle,"source")=>EffectsShape.Content,
                        (EffectsShape.SourceBundle,"association")=>EffectsShape.Association,(EffectsShape.StoredBridge,"artifact")=>EffectsShape.BridgeArtifact,
                        (EffectsShape.ItemsProof,"artifact")=>EffectsShape.Feature,(EffectsShape.ItemsProof,"snapshot")=>EffectsShape.ItemsSnapshot,
                        (EffectsShape.ItemsProof,"item")=>EffectsShape.Summary,(EffectsShape.SourceProof,"admission")=>EffectsShape.SourceBundle,
                        (EffectsShape.SourceProof,"snapshot")=>EffectsShape.Snapshot,(EffectsShape.SourceProof,"index")=>EffectsShape.StoredBridge,
                        (EffectsShape.Groups,_)=>EffectsShape.Group,_=>EffectsShape.Unknown
                    };
                }
                else if(type==JsonTokenType.StartArray)
                {
                    contexts[depth]=depth==0?EffectsShape.Unknown:(contexts[depth-1],properties[depth]) switch
                    {
                        (EffectsShape.Snapshot or EffectsShape.Header,"columns")=>EffectsShape.Columns,
                        (EffectsShape.Association,"memberships")=>EffectsShape.Memberships,
                        (EffectsShape.BridgeBinding,"configurations")=>EffectsShape.Configurations,
                        (EffectsShape.Feature,"groups")or(EffectsShape.ItemsSnapshot,"propertygroups")=>EffectsShape.Groups,
                        (EffectsShape.Group,"fields")=>EffectsShape.Fields,(EffectsShape.Summary,"basevalues")=>EffectsShape.Fields,
                        (EffectsShape.Projection,"readableranges" or "unavailableranges")=>EffectsShape.Ranges,_=>EffectsShape.Unknown
                    };
                    limits[depth]=contexts[depth] switch{EffectsShape.Columns=>512,EffectsShape.Memberships=>256,EffectsShape.Configurations=>2,
                        EffectsShape.Groups=>7,EffectsShape.Fields=>contexts[depth-1]==EffectsShape.Summary?8:1024,EffectsShape.Ranges=>128,_=>0};
                    counts[depth]=0;
                }
            }
        }
        catch(JsonException){throw EffectsItemsInvalid();}
        catch(ArgumentException){throw EffectsItemsInvalid();}
    }
    private static void EffectsSnapshot(TableDataSnapshot snapshot,StoredTableAdmission admission,CapturedOpeningContext opening)
    {
        if(admission?.Artifact?.Header?.Columns is null||admission.Artifact.Completion is null)throw EffectsItemsInvalid();
        var r=admission.Artifact;var c=r.Completion;
        if(snapshot is null||snapshot.Columns is null||snapshot.Coverage is null||snapshot.ProductId!=opening.Build.ProductId||snapshot.BuildIdentity!=opening.Build.Id||
            snapshot.SourceRegion!=opening.Reference.Region||snapshot.ResearchLocale!="enUS"||snapshot.ArtifactId!=r.ContentKey||snapshot.PreparedAt!=admission.PreparedAt||
            snapshot.TableId!=r.Header.TableHash.ToLowerInvariant()||snapshot.LayoutHash!=r.Header.LayoutHash.ToUpperInvariant()||snapshot.DefinitionSha256!=r.Binding.DefinitionSha256||
            snapshot.ProviderRevision!=r.Binding.ProviderRevision||snapshot.DecoderRevision!=r.Binding.DecoderRevision||!EffectsAuthority(snapshot.Source,admission.Source)||
            !EffectsAuthority(snapshot.Association,admission.Association)||snapshot.Columns.Count!=r.Header.Columns.Count)throw EffectsItemsInvalid();
        var coverage=new TableDataCoverage(c.Scope,N(c.DeclaredRecords),N(c.PhysicalRows),N(c.CopyRows),N(c.ExcludedEncryptedSections),N(c.ExcludedPhysicalRecords+c.ExcludedCopyRecords),N(r.Header.DeclaredCopyRecords),N(c.ExcludedPhysicalRecords),N(c.ExcludedCopyRecords));
        if(snapshot.Coverage!=coverage||snapshot.RowCount!=N(c.PhysicalRows+c.CopyRows)||snapshot.TextIssueCount!=N(c.TextIssueCount))throw EffectsItemsInvalid();
        for(var i=0;i<snapshot.Columns.Count;i++)
        {
            var column=snapshot.Columns[i];var original=r.Header.Columns[i];
            if(column is null||column.Name!=original.Name||column.Family!=original.Kind||column.IsId!=original.IsId||column.Interpretation!=(original.Interpretation is {} p?new TableColumnInterpretation(p.Policy,p.PhysicalBits,p.PhysicalSigned,p.CandidateBits,p.CandidateUnsigned,p.Warning):null))throw EffectsItemsInvalid();
        }
        static string N(long number)=>number.ToString(CultureInfo.InvariantCulture);
    }
}
