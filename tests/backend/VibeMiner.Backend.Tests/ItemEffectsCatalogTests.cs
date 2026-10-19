using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Formats.Wow;
using Xunit;
using Harness = VibeMiner.Backend.Tests.ItemsStoreTests.Harness;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsCatalogTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task BoundedItemsAuthorityReturnsOwnedStrictSourcesAndOriginalConfigurations()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _=>Task.CompletedTask, default);
        var actual = await h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default);
        Assert.Equal(snapshot.ArtifactId,actual.Items.Artifact.ContentKey);
        Assert.Equal(h.Item.Rows.ContentKey,actual.Item.Artifact.ContentKey); Assert.Equal(h.Sparse.Rows.ContentKey,actual.Sparse.Artifact.ContentKey);
        Assert.Equal(2,actual.Configurations.Count); Assert.All(actual.Configurations,c=>Assert.Equal(Harness.Time,c.RetrievedAt));
    }
    [Theory]
    [InlineData("feature")] [InlineData("source")] [InlineData("association")] [InlineData("rows")] [InlineData("content")] [InlineData("metadata")]
    public async Task OversizedRawEvidenceFailsBeforeMalformedEvidenceCanBecomeAuthority(string field)
    {
        await using var h=await Harness.Create();var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
        var sql=field switch
        {
            "feature"=>"UPDATE items_artifacts SET descriptor=$v",
            "source"=>"UPDATE table_admissions SET source=$v WHERE table_id='50238ec2'",
            "association"=>"UPDATE table_admissions SET association=$v WHERE table_id='50238ec2'",
            "rows"=>"UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='50238ec2')",
            "content"=>"UPDATE content_objects SET receipt=$v",
            _=>"UPDATE metadata_objects SET receipt=$v"
        };
        await Sql(h,sql,new string('x',(256<<10)+1));
        Assert.Equal("ITEM_EFFECTS_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default))).Code);
    }
    [Theory]
    [InlineData("columns")] [InlineData("Columns")] [InlineData("\\u0063olumns")]
    public async Task PersistedColumnArrayIsBoundedBeforeTypedGraphAllocation(string name)
    {
        await using var h=await Harness.Create();var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
        var rows=h.Item.Rows with{Header=h.Item.Rows.Header with{Columns=Enumerable.Repeat(h.Item.Rows.Header.Columns[0],513).ToArray()}};
        var json=JsonSerializer.Serialize(rows,Json).Replace("\"columns\":","\""+name+"\":",StringComparison.Ordinal);
        await Sql(h,"UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='50238ec2')",json);
        Assert.Equal("ITEM_EFFECTS_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default))).Code);
    }
    [Fact]
    public async Task MultibyteOriginalEvidenceIsLimitedByUtf8Bytes()
    {
        await using var h=await Harness.Create();var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
        await Sql(h,"UPDATE items_artifacts SET descriptor=$v",new string('\u0800',90000));
        Assert.Equal("ITEM_EFFECTS_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default))).Code);
    }
    [Fact]
    public async Task CompleteOriginalReceiptAuthorityCannotBeChangedBehindAnUnchangedArtifactKey()
    {
        await using var h=await Harness.Create();var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
        await Sql(h,"UPDATE content_objects SET receipt=$v WHERE ekey='"+h.Item.Source.EncodingKey+"'",JsonSerializer.Serialize(h.Item.Source with{RetrievedAt="2026-10-20T00:00:00Z"},Json));
        Assert.Equal("ITEMS_SOURCE_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default))).Code);
    }
    [Theory]
    [InlineData("short")] [InlineData("nonhash")] [InlineData("oversize")]
    public async Task PersistedAdmissionKeysAreLengthAndHashCheckedBeforeLookupOrDescriptorUse(string kind)
    {
        await using var h=await Harness.Create();var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
        var key=kind=="short"?"a":new string(kind=="nonhash"?'z':'a',kind=="oversize"?100000:64);
        await Sql(h,"PRAGMA foreign_keys=OFF; UPDATE items_admissions SET content_key=$v",key);
        Assert.Equal("ITEMS_SOURCE_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default))).Code);
    }
    [Fact]
    public async Task SourceOnlyUnsupportedBridgeStillHasOneFatalAuthorityFenceWithoutAnIndex()
    {
        await using var f=await EffectsFixture.Create();
        await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,[],default);
        await Sql(f.H,"UPDATE table_admissions SET prepared_at=$v WHERE table_id='00cb674f'","2026-10-20T00:00:00Z");
        Assert.Equal("ITEM_EFFECTS_BRIDGE_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,[],default))).Code);
        await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,null,[],default);
    }
    [Fact]
    public async Task IndependentTargetReplacementReturnsOnlyItsTypedProblemAndItemsAreAlwaysFatalFirst()
    {
        await using var f=await EffectsFixture.Create(indexed:true);
        await Sql(f.H,"UPDATE table_admissions SET prepared_at=$v WHERE table_id='4002a5b1'","2026-10-20T00:00:00Z");
        var results=await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,f.Targets,default);
        Assert.Equal("ITEM_EFFECTS_TARGET_STALE",results[0].Problem!.Code);Assert.True(results[0].Problem!.Retryable);Assert.Null(results[1].Problem);
        await Sql(f.H,"UPDATE items_admissions SET prepared_at=$v","2026-10-21T00:00:00Z");
        await Sql(f.H,"UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='46c66698')",new string('x',131073));
        Assert.Equal("ITEMS_ARTIFACT_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,f.Targets,default))).Code);
    }
    [Fact]
    public async Task CorruptAndOverBudgetCapturedTargetsBecomeSeparateBoundedProblems()
    {
        await using var f=await EffectsFixture.Create(indexed:true);
        await Sql(f.H,"UPDATE table_admissions SET source=$v WHERE table_id='4002a5b1'","null");
        await Sql(f.H,"UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='46c66698')",new string('x',131073));
        var result=await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,f.Targets,default);
        Assert.Equal(new[]{"ITEM_EFFECTS_SOURCE_CORRUPT","ITEM_EFFECTS_LIMIT"},result.Select(v=>v.Problem!.Code));Assert.All(result,v=>Assert.False(v.Problem!.Retryable));
        Assert.Equal("ITEM_EFFECTS_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReadItemEffectsSourceAsync(f.H.Opening,ItemEffectsSourceRole.Name,default))).Code);
    }
    [Fact]
    public async Task SameKeySourceReadmissionKeepsFirstIndexDescriptorAndCompactTimeButFencesFreshGeneration()
    {
        await using var f=await EffectsFixture.Create();var artifact=f.Index();
        await f.H.Catalog.RecordItemEffectsBridgeArtifactAsync(artifact,default);
        var first=await f.H.Catalog.CommitItemEffectsBridgeAdmissionAsync(f.Items,artifact,Harness.Time,default);
        const string next="2026-10-20T00:00:00Z";
        await Sql(f.H,"UPDATE table_admissions SET prepared_at=$v WHERE table_id='00cb674f'",next);
        var current=await f.H.Catalog.ReadItemEffectsSourceAsync(f.H.Opening,ItemEffectsSourceRole.Bridge,default);
        var refreshed=artifact with{Binding=artifact.Binding with{Source=artifact.Binding.Source with{PreparedAt=next}}};
        Assert.Equal(artifact.ContentKey,ItemEffectsBridgePolicy.ContentKey(refreshed.Binding,refreshed.RowCount));
        var retried=await f.H.Catalog.CommitItemEffectsBridgeAdmissionAsync(f.Items,refreshed,"2026-10-21T00:00:00Z",default);
        Assert.Equal(JsonSerializer.Serialize(first,Json),JsonSerializer.Serialize(retried,Json));
        var freshProof=new ItemEffectsBridgeProof(current!,f.Bridge.Snapshot with{PreparedAt=next},retried);
        await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,freshProof,[],default);
        Assert.Equal("ITEM_EFFECTS_BRIDGE_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge with{Index=first},[],default))).Code);
    }
    [Fact]
    public async Task SourceReadmissionAfterCapturedBuildButBeforeCommitCannotPublishAnOldGeneration()
    {
        await using var f=await EffectsFixture.Create();var artifact=f.Index();await f.H.Catalog.RecordItemEffectsBridgeArtifactAsync(artifact,default);
        await Sql(f.H,"UPDATE table_admissions SET prepared_at=$v WHERE table_id='00cb674f'","2026-10-20T00:00:00Z");
        Assert.Equal("ITEM_EFFECTS_BRIDGE_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.CommitItemEffectsBridgeAdmissionAsync(f.Items,artifact,Harness.Time,default))).Code);
        Assert.Null(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening,default));
    }
    [Fact]
    public async Task RegistrationConflictAndFailedCommitLeaveOriginalCanonicalAdmissionIntact()
    {
        await using var f=await EffectsFixture.Create();var artifact=f.Index();await f.H.Catalog.RecordItemEffectsBridgeArtifactAsync(artifact,default);
        var first=await f.H.Catalog.CommitItemEffectsBridgeAdmissionAsync(f.Items,artifact,Harness.Time,default);
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.RecordItemEffectsBridgeArtifactAsync(artifact with{Sha256=new('f',64)},default))).Code);
        await Sql(f.H,"UPDATE table_admissions SET prepared_at=$v WHERE table_id='50238ec2'","2026-10-20T00:00:00Z");
        Assert.Equal("ITEMS_ARTIFACT_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.CommitItemEffectsBridgeAdmissionAsync(f.Items,artifact,"2026-10-21T00:00:00Z",default))).Code);
        Assert.Equal(JsonSerializer.Serialize(first,Json),JsonSerializer.Serialize(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening,default),Json));
    }
    [Fact]
    public async Task TargetCollectionsAreCapturedOnceWithoutEnumerationAndRejectBadRolesBeforeSchedule()
    {
        await using var f=await EffectsFixture.Create(indexed:true);var values=new ChangingTargets(f.Targets[0],false);
        Assert.Null(Assert.Single(await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,values,default)).Problem);Assert.Equal(1,values.CountReads);
        foreach(var targets in new IReadOnlyList<ItemEffectsTargetProof>[] {new ChangingTargets(f.Targets[0],true),[f.Targets[0],f.Targets[0]],[f.Targets[1],f.Targets[0]],[f.Targets[0] with{Role=ItemEffectsSourceRole.Bridge}]})
            Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,targets,default))).Code);
    }
    [Fact]
    public async Task SimultaneousCanonicalAndWorkspaceConfigurationRewriteCannotReplaceCapturedOriginalAuthority()
    {
        await using var f=await EffectsFixture.Create(indexed:true);
        const string next="2026-10-20T00:00:00Z";
        await Sql(f.H,"UPDATE workspace_configurations SET retrieved_at=$v WHERE role='buildConfig'",next);
        await Sql(f.H,"UPDATE configuration_objects SET retrieved_at=$v WHERE key='"+f.H.Fixture.BuildKey+"'",next);
        Assert.Equal("ITEMS_ARTIFACT_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,f.Targets,default))).Code);
    }
    [Fact]
    public async Task VerifiedObservationOnlyChangesDoNotReplaceOriginalReceiptAuthority()
    {
        await using var f=await EffectsFixture.Create(indexed:true);const string next="2026-10-20T00:00:00Z";
        await Sql(f.H,"UPDATE workspace_configurations SET verified_at=$v",next);
        // Only verifiedAt is changed in both current receipts and their canonical originals.
        var source=JsonSerializer.Serialize(f.H.Item.Source with{VerifiedAt=next},Json);
        await Sql(f.H,"UPDATE content_objects SET receipt=$v WHERE ekey='"+f.H.Item.Source.EncodingKey+"'",source);
        await Sql(f.H,"UPDATE table_admissions SET source=$v WHERE json_extract(source,'$.encodingKey')='"+f.H.Item.Source.EncodingKey+"'",source);
        await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,f.Targets,default);
    }
    [Theory]
    [InlineData("memberships")] [InlineData("depth")] [InlineData("duplicate")]
    public async Task RawPathPreflightRefusesAmplifiedOrConflictingEvidenceBeforeDtoConstruction(string kind)
    {
        await using var h=await Harness.Create();var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
        string sql,json,code;
        if(kind=="memberships")
        {sql="UPDATE table_admissions SET association=$v WHERE table_id='50238ec2'";json=JsonSerializer.Serialize(h.Item.Association with{Memberships=Enumerable.Repeat(h.Item.Association.Memberships[0],257).ToArray()},Json);code="ITEM_EFFECTS_LIMIT";}
        else if(kind=="depth")
        {sql="UPDATE table_admissions SET source=$v WHERE table_id='50238ec2'";json=string.Concat(Enumerable.Repeat("{\"nested\":",33))+"null"+new string('}',33);code="ITEM_EFFECTS_LIMIT";}
        else
        {sql="UPDATE table_admissions SET source=$v WHERE table_id='50238ec2'";var original=JsonSerializer.Serialize(h.Item.Source,Json);json="{\"encodingKey\":\""+h.Item.Source.EncodingKey+"\","+original[1..];code="ITEMS_SOURCE_INVALID";}
        await Sql(h,sql,json);
        Assert.Equal(code,(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default))).Code);
    }
    [Fact]
    public async Task CompleteItemsRawBudgetAcceptsExactly256KiBAndRejectsOneMoreUtf8Byte()
    {
        await using var h=await Harness.Create();var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
        var bytes=await SourceAndConfigurationBytes(h,"'50238ec2','919be54e'");
        bytes+=await Scalar(h,"SELECT length(CAST(f.descriptor AS BLOB))+length(CAST(a.prepared_at AS BLOB)) FROM items_admissions a JOIN items_artifacts f ON f.content_key=a.content_key");
        var descriptor=JsonSerializer.Serialize((await h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default)).Items.Artifact,Json);
        var padded=new string(' ',checked((int)((256<<10)-bytes)))+descriptor;
        await Sql(h,"UPDATE items_artifacts SET descriptor=$v",padded);
        Assert.Equal(snapshot.ArtifactId,(await h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default)).Items.Artifact.ContentKey);
        await Sql(h,"UPDATE items_artifacts SET descriptor=$v"," "+padded);
        Assert.Equal("ITEM_EFFECTS_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default))).Code);
    }
    [Fact]
    public async Task BridgeUsesOne128KiBAggregateIncludingRegisteredDescriptorAndCurrentOriginalSource()
    {
        await using var f=await EffectsFixture.Create();var artifact=f.Index();await f.H.Catalog.RecordItemEffectsBridgeArtifactAsync(artifact,default);
        var saved=await f.H.Catalog.CommitItemEffectsBridgeAdmissionAsync(f.Items,artifact,Harness.Time,default);var proof=f.Bridge with{Index=saved};
        var bytes=await SourceAndConfigurationBytes(f.H,"'00cb674f'");
        bytes+=await Scalar(f.H,"SELECT length(CAST(f.descriptor AS BLOB))+length(CAST(a.prepared_at AS BLOB)) FROM item_effects_bridge_admissions a JOIN item_effects_bridge_artifacts f ON f.content_key=a.content_key");
        var padded=new string(' ',checked((int)((128<<10)-bytes)))+JsonSerializer.Serialize(artifact,Json);
        await Sql(f.H,"UPDATE item_effects_bridge_artifacts SET descriptor=$v",padded);
        await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,proof,[],default);
        await Sql(f.H,"UPDATE item_effects_bridge_artifacts SET descriptor=$v"," "+padded);
        Assert.Equal("ITEM_EFFECTS_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,proof,[],default))).Code);
        await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,[],default); // Source-only did not consult the poisoned index.
    }
    [Fact]
    public async Task EachTargetHasItsOwn128KiBAggregateAndOnlyConsultedTargetsAreRead()
    {
        await using var f=await EffectsFixture.Create(indexed:true);var bytes=await SourceAndConfigurationBytes(f.H,"'4002a5b1'");
        var descriptor=JsonSerializer.Serialize(f.Targets[0].Admission.Artifact,Json);var padded=new string(' ',checked((int)((128<<10)-bytes)))+descriptor;
        await Sql(f.H,"UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='4002a5b1')",padded);
        Assert.All(await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,f.Targets,default),v=>Assert.Null(v.Problem));
        await Sql(f.H,"UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='4002a5b1')"," "+padded);
        var result=await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,f.Targets,default);
        Assert.Equal("ITEM_EFFECTS_LIMIT",result[0].Problem!.Code);Assert.Null(result[1].Problem);
        await f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,f.Bridge,[],default);
    }
    private static async Task<long> SourceAndConfigurationBytes(Harness h,string controlledTableList)
    {
        // Controlled authored fixture SQL measures every original field in the documented
        // bundle; padding is JSON whitespace, so accepted authority remains unchanged.
        var sources=await Scalar(h,"""
            SELECT SUM(length(CAST(a.source AS BLOB))+length(CAST(a.association AS BLOB))+length(CAST(r.descriptor AS BLOB))+length(CAST(a.prepared_at AS BLOB))+
                length(CAST(c.receipt AS BLOB))+length(CAST(e.receipt AS BLOB))+length(CAST(o.receipt AS BLOB)))
            FROM table_admissions a JOIN table_artifacts r ON r.content_key=a.content_key
            JOIN content_objects c ON c.ekey=json_extract(a.source,'$.encodingKey')
            JOIN metadata_objects e ON e.ekey=json_extract(a.association,'$.encodingSource.encodingKey')
            JOIN metadata_objects o ON o.ekey=json_extract(a.association,'$.rootSource.encodingKey') WHERE a.table_id IN (
            """+controlledTableList+")");
        var configs=await Scalar(h,"""
            SELECT SUM(length(CAST(w.role AS BLOB))+length(CAST(w.key AS BLOB))+length(CAST(w.sha256 AS BLOB))+length(CAST(w.source_url AS BLOB))+
                length(CAST(w.retrieved_at AS BLOB))+length(CAST(w.verified_at AS BLOB))+length(CAST(c.sha256 AS BLOB))+length(CAST(c.source_url AS BLOB))+
                length(CAST(c.retrieved_at AS BLOB))+length(CAST(c.verifier_revision AS BLOB)))
            FROM workspace_configurations w JOIN configuration_objects c ON c.key=w.key
            """);
        return sources+configs;
    }
    private static async Task<long> Scalar(Harness h,string sql)
    {
        await using var connection=new SqliteConnection("Data Source="+Path.Combine(h.DirectoryPath,"discovery-v1.sqlite3"));await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText=sql;return (long)(await command.ExecuteScalarAsync())!;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConsultedTargetsRequireAnIndexedBridgeBeforeSchedulingEvenWhenItemsHaveSinceChanged(bool omitBridge)
    {
        await using var f=await EffectsFixture.Create(indexed:true);
        await Sql(f.H,"UPDATE items_admissions SET prepared_at=$v","2026-10-20T00:00:00Z");
        var bridge=omitBridge?null:f.Bridge with{Index=null};
        Assert.Equal("INVALID_REQUEST",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.H.Catalog.ReauthorizeItemEffectsAsync(f.Items,bridge,f.Targets,default))).Code);
    }
    private sealed class ChangingTargets(ItemEffectsTargetProof value,bool vanish):IReadOnlyList<ItemEffectsTargetProof>
    {
        internal int CountReads;public int Count=>++CountReads==1?1:int.MaxValue;
        public ItemEffectsTargetProof this[int index]=>vanish?throw new ArgumentOutOfRangeException(nameof(index)):value;
        public IEnumerator<ItemEffectsTargetProof> GetEnumerator()=>throw new InvalidOperationException("Do not enumerate caller authority.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class EffectsFixture:IAsyncDisposable
    {
        internal Harness H=null!;internal ItemEffectsItemsProof Items=null!;internal ItemEffectsBridgeProof Bridge=null!;internal ItemEffectsTargetProof[] Targets=[];
        internal IReadOnlyList<ConfigurationReceipt> Configurations=[];
        internal static async Task<EffectsFixture> Create(bool indexed=false)
        {
            var f=new EffectsFixture{H=await Harness.Create()};var h=f.H;var snapshot=await h.Store.PrepareAsync(h.Request,_=>Task.CompletedTask,default);
            f.Items=await h.Store.AuthorizeItemEffectsAsync(new(h.Request,snapshot.ArtifactId,"1"),default);
            f.Configurations=(await h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening,"enUS",snapshot.ArtifactId,default)).Configurations;
            foreach(var role in new[]{ItemEffectsSourceRole.Bridge,ItemEffectsSourceRole.Effect,ItemEffectsSourceRole.Name})
            {
                var table=role==ItemEffectsSourceRole.Bridge?"00cb674f":role==ItemEffectsSourceRole.Effect?"4002a5b1":"46c66698";
                var fdid=role==ItemEffectsSourceRole.Bridge?"3177687":role==ItemEffectsSourceRole.Effect?"969941":"1990283";
                // Catalog-only authored identities; target columns deliberately need not qualify a semantic profile.
                var binding=h.Item.Rows.Binding with{DefinitionSha256=role==ItemEffectsSourceRole.Bridge?ItemEffectsBridgePolicy.Definition:new(role==ItemEffectsSourceRole.Effect?'e':'f',64)};
                var columns=new[]{new Db2Column("ID","integer",true),new Db2Column("ItemEffectID","integer",false),new Db2Column("ItemID","integer",false)};
                var rows=h.Item.Rows with{Binding=binding,ContentKey=TableRowArtifacts.ContentKey(binding),Header=h.Item.Rows.Header with{SchemaVersion=5,TableHash=table.ToUpperInvariant(),LayoutHash=role==ItemEffectsSourceRole.Bridge?ItemEffectsBridgePolicy.Layout:"12345678",Columns=columns}};
                var association=h.Item.Association with{FileDataId=fdid};
                var source=h.Item.Snapshot with{TableId=table,ArtifactId=rows.ContentKey,LayoutHash=rows.Header.LayoutHash,DefinitionSha256=binding.DefinitionSha256,Association=association,
                    Columns=columns.Select(c=>new TableDataColumn(c.Name,c.Kind,c.IsId,null)).ToArray()};
                var admission=new StoredTableAdmission(rows,h.Item.Source,association,Harness.Time);
                await h.Catalog.RecordTableArtifactAsync(rows,default);await h.Catalog.CommitTableAdmissionAsync(h.Opening,table,"enUS",admission.Source,association,rows,Harness.Time,default);
                if(role==ItemEffectsSourceRole.Bridge)f.Bridge=new(admission,source,null);else f.Targets=[..f.Targets,new(role,admission,source)];
            }
            if(indexed)
            {
                var artifact=f.Index();await h.Catalog.RecordItemEffectsBridgeArtifactAsync(artifact,default);
                f.Bridge=f.Bridge with{Index=await h.Catalog.CommitItemEffectsBridgeAdmissionAsync(f.Items,artifact,Harness.Time,default)};
            }
            return f;
        }
        internal ItemEffectsBridgeArtifact Index()
        {
            var b=new ItemEffectsBridgeBinding(H.Request,H.Opening.Build.Version,H.Opening.Reference,Configurations,Bridge.Snapshot,Bridge.Admission.Artifact,
                new(new("00cb674f",0),new("00cb674f",1),new("00cb674f",2)));
            var metadata=ItemEffectsBridgePolicy.MetadataBytes(b,"1");
            return new(ItemEffectsBridgePolicy.ContentKey(b,"1"),new('a',64),64+metadata.Length+32,b,"1");
        }
        public ValueTask DisposeAsync()=>H.DisposeAsync();
    }
    private static async Task Sql(Harness h,string sql,string? value=null)
    {
        await using var connection=new SqliteConnection("Data Source="+Path.Combine(h.DirectoryPath,"discovery-v1.sqlite3"));await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText=sql;if(value is not null)command.Parameters.AddWithValue("$v",value);await command.ExecuteNonQueryAsync();
    }
}
