using Xunit;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
namespace VibeMiner.Backend.Tests;

public sealed class InventoryPreparationTests
{
    [Fact]
    public async Task Full_metadata_preparation_seals_exact_sizes_and_paged_file_memberships_without_payload_requests()
    {
        await using var h = await Harness.Create();
        var workspace = await h.Open(); var summary = workspace.Inventory!;
        Assert.Equal("home-inventory-v1", workspace.PreparationProfile); Assert.Equal("ready", workspace.InventoryState);
        Assert.Equal(h.Fixture.ExpectedEncodedBytes.ToString(), summary.TotalEncodedBytes);
        Assert.Equal("0", summary.VerifiedEncodedBytes); Assert.Equal(summary.TotalEncodedBytes, summary.RemainingEncodedBytes);
        Assert.Equal("8", summary.MembershipCount); Assert.Equal("3", summary.RootMembershipCount);
        Assert.Equal("2", summary.UniqueContentCount); Assert.Equal("2", summary.DownloadObjectCount);
        Assert.Equal(h.Fixture.ExpectedDecodedBytes.ToString(), summary.LogicalDecodedBytes);
        Assert.Equal(5, workspace.Metadata!.Count); Assert.True(workspace.Build.IsLocal); Assert.False(workspace.Build.IsNew);
        Assert.Equal(7, h.Handler.Requests.Count); Assert.All(h.Handler.Requests, url => Assert.True(h.Fixture.Responses.ContainsKey(url)));
        var entries = new List<InventoryEntry>(); string? after = null;
        do
        {
            var page = await h.Catalog.ReadInventoryAsync(new("wow", workspace.Build.Id, "us", summary.Generation, 3, after), default);
            Assert.InRange(page.Entries.Count, 1, 3); Assert.Equal("8", page.TotalCount); entries.AddRange(page.Entries); after = page.NextCursor;
        } while (after is not null);
        Assert.Equal(8, entries.Count); Assert.Equal(8, entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(3, entries.Count(e => e.Family == "root")); Assert.Contains(entries, e => e.Family == "install" && e.Name is not null);
        Assert.All(entries, e => Assert.False(e.Retained)); Assert.Empty(Directory.GetFiles(Path.Combine(h.Directory, "staging-v1")));
        var locals = await h.Catalog.ReadAsync("wow", "local", 50, null, default); Assert.Single(locals.Builds);
    }
    [Fact]
    public async Task Healthy_reopen_revalidates_metadata_without_HTTP_and_preserves_original_receipts_and_generation()
    {
        await using var h = await Harness.Create(); var first = await h.Open(); h.Handler.BlockAll = true;
        var second = await h.Open(); Assert.Equal(7, h.Handler.Requests.Count); Assert.Equal(first.Inventory!.Generation, second.Inventory!.Generation);
        Assert.Equal(first.Metadata!.Select(m => (m.EncodingKey, m.SourceUrl, m.RetrievedAt)), second.Metadata!.Select(m => (m.EncodingKey, m.SourceUrl, m.RetrievedAt)));
        Assert.Equal(first.Inventory.TotalEncodedBytes, second.Inventory.TotalEncodedBytes);
    }
    [Fact]
    public async Task Missing_previously_admitted_metadata_fails_without_repair_or_a_new_successful_admission()
    {
        await using var h = await Harness.Create(); var first = await h.Open(); var receipt = first.Metadata!.Single(m => m.Role == "root");
        File.Delete(Path.Combine(h.Directory, "objects-v1", receipt.Sha256[..2], receipt.Sha256[2..])); h.Handler.BlockAll = true;
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Open()); Assert.Equal("LOCAL_OBJECT_MISSING", error.Code);
        Assert.Equal(7, h.Handler.Requests.Count);
    }
    [Fact]
    public async Task A_late_source_integrity_failure_keeps_rows_hidden_and_does_not_acknowledge_Open_retry_reuses_verified_roles()
    {
        await using var h = await Harness.Create(); h.Handler.Corrupt = h.Fixture.Metadata["size"].EncodingKey;
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Open());
        Assert.Equal(0L, await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state='ready'"));
        Assert.Equal(0L, await h.Scalar("SELECT COUNT(*) FROM open_acknowledgements"));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadInventoryAsync(new("wow", h.Fixture.BuildIdentity, "us", "1"), default));
        h.Handler.Corrupt = null; var next = await h.Open(); Assert.Equal("8", next.Inventory!.MembershipCount);
        Assert.Equal(8, h.Handler.Requests.Count); // only failed DS is downloaded again
        Assert.Equal(0L, await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state != 'ready'"));
    }
    [Fact]
    public async Task Verified_exact_shared_object_reduces_remaining_once_across_variants_and_builds()
    {
        await using var h = await Harness.Create(); var payload = h.Fixture.PayloadA;
        var acquirer = new MetadataAcquirer(h.Client, h.Objects);
        h.Handler.Additional[h.PayloadUrl(payload.EncodingKey)] = payload.Encoded;
        await using (var verified = await acquirer.AcquireAsync(new("https://us.cdn.blizzard.com/tpr/wow/"), new("root", payload.ContentKey, payload.EncodingKey, payload.Encoded.Length, payload.Decoded.Length), _ => Task.CompletedTask, default))
            await h.Catalog.RecordMetadataAsync(verified.Receipt with { ParserRevision = "root-v1" }, default);
        var first = await h.Open(); Assert.Equal(payload.Encoded.Length.ToString(), first.Inventory!.VerifiedEncodedBytes);
        Assert.Equal((h.Fixture.ExpectedEncodedBytes-payload.Encoded.Length).ToString(), first.Inventory.RemainingEncodedBytes);
        Assert.Equal("0", first.Inventory.SharedVerifiedEncodedBytes);
        var original = h.Fixture.Discovery;
        var versions = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original.Documents.Single(d => d.Role == "versions").Bytes).Replace("12345", "12346", StringComparison.Ordinal));
        var row = MetadataParser.Parse(versions).Rows.Single(); var identity = BuildIdentity.Create("wow", row.Fields);
        var document = new SourceDocument("versions", "https://us.version.battle.net/v2/products/wow/versions", DateTimeOffset.UtcNow.ToString("O"), Convert.ToHexStringLower(SHA256.HashData(versions)), versions);
        var observation = original.Observations.Single() with { Identity = identity, OriginalFields = row.Fields };
        var discovery = original with { Documents = [document, original.Documents.Single(d => d.Role == "cdns")], Observations = [observation] };
        var generation = await h.Catalog.BeginRefreshAsync("wow", default); await h.Catalog.CommitAsync("wow", generation, discovery, DateTimeOffset.UtcNow.ToString("O"), default);
        h.Handler.BlockAll = true;
        var second = await h.Preparer.PrepareAsync(new("wow", identity.Id, "us"), _ => Task.CompletedTask, default);
        Assert.Equal(first.Inventory.TotalEncodedBytes, second.Inventory!.TotalEncodedBytes);
        Assert.Equal(payload.Encoded.Length.ToString(), second.Inventory.SharedVerifiedEncodedBytes);
    }
    [Fact]
    public async Task Exact_generation_context_and_page_bounds_are_enforced_without_network()
    {
        await using var h = await Harness.Create(); var opened = await h.Open(); h.Handler.BlockAll = true;
        foreach (var request in new[] { new InventoryReadRequest("wow", opened.Build.Id, "eu", opened.Inventory!.Generation),
            new("wowt", opened.Build.Id, "us", opened.Inventory.Generation), new("wow", opened.Build.Id, "us", "999") })
            await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadInventoryAsync(request, default));
        foreach (var request in new[] { new InventoryReadRequest("wow", opened.Build.Id, "us", opened.Inventory.Generation, 101),
            new("wow", opened.Build.Id, "us", "01"), new("wow", opened.Build.Id, "us", opened.Inventory.Generation, 50, "-1") })
            Assert.Throws<DiscoveryException>(() => InventoryService.Validate(request));
        Assert.Equal(7, h.Handler.Requests.Count);
    }
    [Fact]
    public async Task Same_count_derived_corruption_refuses_cached_readiness_without_HTTP_or_automatic_rebuild()
    {
        await using var h = await Harness.Create(); await h.Open(); h.Handler.BlockAll = true;
        await h.Execute("UPDATE inventory_units SET bytes=bytes+1");
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Open()); Assert.Equal("LOCAL_INVENTORY_CORRUPT", error.Code);
        Assert.Equal(7, h.Handler.Requests.Count);
    }
    [Fact]
    public async Task Escaped_install_names_obey_serialized_page_budget_and_preserve_all_cursor_rows()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Open();
        var g = snapshot.Inventory!.Generation;
        for (var i = 1000; i < 1100; i++) await h.Execute($"INSERT INTO inventory_origins SELECT g,{i},'install',replace(hex(zeroblob(2048)),'0','<'),NULL,ckey,NULL,0,NULL,decoded,NULL,NULL,NULL,'authored escaped name',ekey,encoded,decoded FROM inventory_origins WHERE g={g} AND id=1");
        var summary = snapshot.Inventory with { MembershipCount = "108" };
        await h.Execute("UPDATE inventory_generations SET summary='" + JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Replace("'", "''", StringComparison.Ordinal) + "'");
        string? after = null; var ids = new HashSet<string>(); var pages = 0;
        do
        {
            var page = await h.Catalog.ReadInventoryAsync(new("wow", snapshot.Build.Id, "us", g, 100, after), default);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length, 1, 760_000);
            Assert.All(page.Entries, entry => Assert.True(ids.Add(entry.Id))); after = page.NextCursor; pages++;
        } while (after is not null);
        Assert.Equal(108, ids.Count); Assert.True(pages > 1);
    }
    [Fact]
    public async Task Root_only_content_prefers_an_explicitly_verified_candidate_before_lexicographic_choice()
    {
        await using var h = await Harness.Create(); var context = await h.Catalog.ResolveOpeningAsync(new("wow", h.Fixture.BuildIdentity, "us"), default);
        var g = await h.Catalog.StartInventoryAsync(context, default); var ckey = new string('1', 32); var a = new string('a',32); var b = new string('b',32);
        await h.Catalog.AddEncodingAsync(g, [new(ckey, 20, [a,b])], [new(a,10),new(b,11)], default);
        await h.Catalog.AddOriginsAsync(g, [new(1,"root",null,"1",ckey,null,0,null,null,"0","0",null,"authored")], default);
        await h.Catalog.SetAvailableAsync(g, [b], default); await h.Catalog.ResolveOriginsAsync(g,"root",0,default);
        Assert.Equal(1L, await h.Scalar($"SELECT COUNT(*) FROM inventory_units WHERE g={g} AND ekey='{b}'"));
        Assert.Equal(0L, await h.Scalar($"SELECT COUNT(*) FROM inventory_units WHERE g={g} AND ekey='{a}'"));
    }
    [Fact]
    public async Task Ambiguous_size_prefix_and_conflicting_exact_lengths_cannot_seal_inventory()
    {
        await using var h = await Harness.Create(); var context = await h.Catalog.ResolveOpeningAsync(new("wow", h.Fixture.BuildIdentity, "us"), default);
        var g = await h.Catalog.StartInventoryAsync(context, default); var prefix = new string('a',18); var a = prefix + new string('1',14); var b = prefix + new string('2',14);
        await h.Catalog.AddEncodingAsync(g, [], [new(a,10),new(b,11)], default);
        await h.Catalog.AddOriginsAsync(g, [new(1,"size",null,null,null,prefix,9,10,null,null,null,null,"authored")], default);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ResolveOrderedEncodedAsync(g,"size",1,_=>Task.CompletedTask,default)); Assert.Equal("INVENTORY_PREFIX_UNRESOLVED",error.Code);
        await h.Catalog.AddOriginsAsync(g, [new(2,"download",null,null,null,a,16,12,null,null,null,null,"authored")], default);
        error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ResolveOrderedEncodedAsync(g,"download",1,_=>Task.CompletedTask,default)); Assert.Equal("INVENTORY_SIZE_CONFLICT",error.Code);
        Assert.Equal(0L,await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state='ready'"));
    }
    [Fact]
    public async Task A_retained_encoding_must_prove_the_ROOT_install_content_and_decoded_size_it_would_satisfy()
    {
        await using var h = await Harness.Create(); var context = await h.Catalog.ResolveOpeningAsync(new("wow", h.Fixture.BuildIdentity, "us"), default);
        var g = await h.Catalog.StartInventoryAsync(context, default); var p = h.Fixture.PayloadA; var other = new string('1',32);
        await h.Catalog.AddEncodingAsync(g, [new(other,p.Decoded.Length,[p.EncodingKey])], [new(p.EncodingKey,p.Encoded.Length)], default);
        await h.Catalog.AddOriginsAsync(g, [new(1,"root",null,"1",other,null,0,null,null,"0","0",null,"authored")], default);
        var receipt = new MetadataReceipt("root",p.ContentKey,p.EncodingKey,new string('a',64),p.Encoded.Length.ToString(),p.Decoded.Length.ToString(),h.PayloadUrl(p.EncodingKey),"2026-10-18T00:00:00Z","2026-10-18T00:00:00Z","root-v1");
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCandidateContentAsync(g,receipt,default)); Assert.Equal("INVENTORY_MAPPING_CONFLICT",error.Code);
        await h.Execute($"UPDATE en_content SET ckey='{p.ContentKey}',bytes=bytes+1 WHERE g={g}; UPDATE en_candidates SET ckey='{p.ContentKey}' WHERE g={g}; UPDATE inventory_origins SET ckey='{p.ContentKey}' WHERE g={g}");
        error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCandidateContentAsync(g,receipt,default)); Assert.Equal("INVENTORY_MAPPING_CONFLICT",error.Code);
    }
    [Fact]
    public async Task Resolution_cursor_moves_directly_across_noncontiguous_batches_and_progress_has_family_totals()
    {
        await using var h = await Harness.Create(); var stages = new List<string>();
        await h.Preparer.PrepareAsync(new("wow", h.Fixture.BuildIdentity,"us"),stage=>{stages.Add(stage);return Task.CompletedTask;},default);
        Assert.Contains("Resolving download inventory · 2 of 2 entries · 100%",stages);
        Assert.Contains("Resolving size inventory · 2 of 2 entries · 100%",stages);
        Assert.Contains("Resolving root inventory · 3 of 3 entries · 100%",stages);
        Assert.Contains(stages,stage=>stage.StartsWith("Indexing encoding content · 1 of 1 pages · 100%",StringComparison.Ordinal));
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default); var key=new string('d',32);
        var ckey=new string('c',32);
        await h.Catalog.AddEncodingAsync(g,[new(ckey,20,[key])],[new(key,10)],default);
        var rows=Enumerable.Range(1,300).Select(i=>new InventoryOrigin(i*3,"root",null,i.ToString(),ckey,null,0,null,null,"0","0",null,"authored")).ToArray();
        await h.Catalog.AddOriginsAsync(g,rows,default);
        var first=await h.Catalog.ResolveOriginBatchAsync(g,"root",0,default); Assert.Equal(256,first.Count); Assert.Equal(768,first.After);
        var second=await h.Catalog.ResolveOriginBatchAsync(g,"root",first.After,default); Assert.Equal(44,second.Count); Assert.Equal(900,second.After);
        var done=await h.Catalog.ResolveOriginBatchAsync(g,"root",second.After,default); Assert.Equal(0,done.Count); Assert.Equal(900,done.After);
    }

    [Fact]
    public async Task Ordered_merge_keeps_duplicates_and_resolves_size_prefixes_from_EN_and_explicit_download_objects()
    {
        await using var h=await Harness.Create();
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default);
        var a=new string('a',32);var b=new string('b',32);var c=new string('c',32);
        await h.Catalog.AddEncodingAsync(g,[],[new(a,10),new(c,12)],default);
        InventoryOrigin Row(long id,string family,string key,int width,long bytes)=>new(id,family,null,null,null,key,width,bytes,null,null,null,null,"authored");
        await h.Catalog.AddOriginsAsync(g,[Row(80,"download",b,16,11),Row(2,"download",a,16,10),Row(20,"download",b,16,11),
            Row(90,"size",b[..18],9,11),Row(3,"size",c[..18],9,12),Row(1,"size",b[..18],9,11)],default);
        var progress=new List<string>();Task Report(string text){progress.Add(text);return Task.CompletedTask;}
        await h.Catalog.ResolveOrderedEncodedAsync(g,"download",3,Report,default);
        await h.Catalog.ResolveOrderedEncodedAsync(g,"size",3,Report,default);
        Assert.Equal(6L,await h.Scalar($"SELECT count(*) FROM inventory_origins WHERE g={g} AND ekey IS NOT NULL"));
        Assert.Equal(3L,await h.Scalar($"SELECT count(*) FROM inventory_units WHERE g={g}"));
        Assert.Equal(33L,await h.Scalar($"SELECT sum(bytes) FROM inventory_units WHERE g={g}"));
        Assert.Equal(2L,await h.Scalar($"SELECT count(*) FROM inventory_origins WHERE g={g} AND family='size' AND ekey='{b}'"));
        Assert.Contains("Resolving size inventory · 3 of 3 entries · 100%",progress);
        Assert.Equal(0L,await h.Scalar("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ordered_merge_rolls_back_an_ambiguous_or_conflicting_batch_and_releases_its_snapshot(bool ambiguous)
    {
        await using var h=await Harness.Create();
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default);
        var prefix=new string('a',18);var a=prefix+new string('1',14);var b=prefix+new string('2',14);
        await h.Catalog.AddEncodingAsync(g,[],ambiguous?[new(a,10),new(b,10)]:[new(a,10)],default);
        await h.Catalog.AddOriginsAsync(g,[new(1,"size",null,null,null,prefix,9,ambiguous?10:11,null,null,null,null,"authored")],default);
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ResolveOrderedEncodedAsync(g,"size",1,_=>Task.CompletedTask,default));
        Assert.Equal(ambiguous?"INVENTORY_PREFIX_UNRESOLVED":"INVENTORY_SIZE_CONFLICT",error.Code);
        Assert.Equal(0L,await h.Scalar($"SELECT count(*) FROM inventory_units WHERE g={g}"));
        await h.Execute("PRAGMA wal_checkpoint(TRUNCATE)"); // Snapshot/readers retired on error.
        await h.Execute($"DELETE FROM en_objects WHERE ekey='{b}'; UPDATE inventory_origins SET declared_encoded=10 WHERE g={g}");
        await h.Catalog.ResolveOrderedEncodedAsync(g,"size",1,_=>Task.CompletedTask,default);
        Assert.Equal(1L,await h.Scalar($"SELECT count(*) FROM inventory_units WHERE g={g}"));
    }
    [Fact]
    public async Task Ordered_merge_cancellation_never_publishes_ready_and_a_retry_can_use_the_same_catalog()
    {
        await using var h=await Harness.Create();
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default);var a=new string('a',32);
        await h.Catalog.AddOriginsAsync(g,[new(1,"download",null,null,null,a,16,10,null,null,null,null,"authored")],default);
        using var ct=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>h.Catalog.ResolveOrderedEncodedAsync(g,"download",1,text=>{if(text.StartsWith("Ordering",StringComparison.Ordinal))ct.Cancel();return Task.CompletedTask;},ct.Token));
        Assert.Equal(0L,await h.Scalar("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
        await h.Catalog.ResolveOrderedEncodedAsync(g,"download",1,_=>Task.CompletedTask,default);
        Assert.Equal(1L,await h.Scalar($"SELECT count(*) FROM inventory_units WHERE g={g}"));
    }

    [Fact]
    public async Task Cancellation_after_matching_batches_retires_readers_and_temporary_rows_before_retry()
    {
        await using var h=await Harness.Create();
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default);var key=new string('a',32);
        // At most 1024 rows per batch: the first report at or past 16,384 is never the last one.
        const int total=20000;
        await h.Catalog.AddOriginsAsync(g,Enumerable.Range(1,total).Select(id=>new InventoryOrigin(id,"download",null,null,null,key,16,10,null,null,null,null,"authored")).ToArray(),default);
        using var ct=new CancellationTokenSource();long canceledAt=-1;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>h.Catalog.ResolveOrderedEncodedAsync(g,"download",total,text=>
        {
            if(canceledAt<0&&InventoryFixture.ResolvingCount(text,"download") is >=16384 and var count){canceledAt=count;ct.Cancel();}
            return Task.CompletedTask;
        },ct.Token));
        Assert.InRange(canceledAt,16384,total-1);
        Assert.Equal(canceledAt,await h.Scalar($"SELECT count(*) FROM inventory_merge_results WHERE g={g}"));
        Assert.Equal(0L,await h.Scalar($"SELECT matched FROM inventory_merge_state WHERE g={g}"));
        Assert.Equal(0L,await h.Scalar($"SELECT count(*) FROM inventory_origins WHERE g={g} AND ekey IS NOT NULL"));
        await h.Catalog.ResolveOrderedEncodedAsync(g,"download",total,_=>Task.CompletedTask,default);
        Assert.Equal((long)total,await h.Scalar($"SELECT count(*) FROM inventory_origins WHERE g={g} AND ekey='{key}'"));
        Assert.Equal(0L,await h.Scalar($"SELECT count(*) FROM inventory_merge_results WHERE g={g}"));
    }
    [Fact]
    public async Task Full_width_size_rows_can_declare_objects_absent_from_EN_and_still_detect_conflicting_duplicates()
    {
        await using var h=await Harness.Create();
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default);var key=new string('d',32);
        await h.Catalog.AddOriginsAsync(g,[new(1,"size",null,null,null,key,16,10,null,null,null,null,"authored"),new(2,"size",null,null,null,key,16,11,null,null,null,null,"authored")],default);
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.ResolveOrderedEncodedAsync(g,"size",2,_=>Task.CompletedTask,default));
        Assert.Equal("INVENTORY_SIZE_CONFLICT",error.Code);
        Assert.Equal(0L,await h.Scalar($"SELECT count(*) FROM inventory_units WHERE g={g}"));
        await h.Execute($"UPDATE inventory_origins SET declared_encoded=10 WHERE g={g}");
        await h.Catalog.ResolveOrderedEncodedAsync(g,"size",2,_=>Task.CompletedTask,default);
        Assert.Equal(1L,await h.Scalar($"SELECT count(*) FROM inventory_units WHERE g={g}"));
    }

    [Fact]
    public async Task Forward_ROOT_lookup_seeks_its_content_key_without_scanning_other_candidates_in_a_fresh_catalog()
    {
        await using var h=await Harness.Create();
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default);
        var content=Enumerable.Range(1,2000).Select(i=>new EncodingContentRow(i.ToString("x32"),20,[(i+10000).ToString("x32")])).ToArray();
        var objects=Enumerable.Range(1,2000).Select(i=>new EncodingObjectRow((i+10000).ToString("x32"),10)).ToArray();
        await h.Catalog.AddEncodingAsync(g,content,objects,default);
        var key=content[^1].Key;var encoded=objects[^1].Key;
        await h.Catalog.AddOriginsAsync(g,[new(1,"root",null,"12345",key,null,0,null,null,"0","0",null,"authored")],default);
        // Performance regression checks access bounds, never elapsed milliseconds. The reverse
        // covering index exists; an unanalyzed catalog must still seek the complete CKey prefix.
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(h.Directory,"discovery-v1.sqlite3"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {
            db.Open();using var command=db.CreateCommand();command.CommandText="EXPLAIN QUERY PLAN "+DiscoveryCatalog.RootCandidateSql;
            command.Parameters.AddWithValue("$g",g);command.Parameters.AddWithValue("$c",key);
            using var reader=command.ExecuteReader();var plan=new List<string>();while(reader.Read())plan.Add(reader.GetString(3));
            Assert.Contains(plan,step=>step.Contains("SEARCH c ",StringComparison.Ordinal)&&step.Contains("ckey=?",StringComparison.Ordinal));
        }
        var metadata=await h.Catalog.ResolveContentAsync(g,key,default);Assert.Single(metadata.Candidates);Assert.Equal(encoded,metadata.Candidates[0].Key);
        await h.Catalog.ResolveOriginsAsync(g,"root",0,default);
        Assert.Equal(1L,await h.Scalar($"SELECT count(*) FROM inventory_units WHERE g={g} AND ekey='{encoded}'"));
    }
    [Fact]
    public async Task An_invalid_256th_candidate_is_rejected_instead_of_silently_omitted_by_the_forward_bound()
    {
        await using var h=await Harness.Create();
        var context=await h.Catalog.ResolveOpeningAsync(new("wow",h.Fixture.BuildIdentity,"us"),default);
        var g=await h.Catalog.StartInventoryAsync(context,default);
        var keys=Enumerable.Range(1,256).Select(i=>i.ToString("x32")).ToArray();
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>h.Catalog.AddEncodingAsync(g,[new(new string('a',32),20,keys)],[],default));
        Assert.Equal("INVENTORY_LIMIT",error.Code);
        Assert.Equal(0L,await h.Scalar($"SELECT count(*) FROM en_content WHERE g={g}"));
    }
    private sealed class Handler(InventoryFixture fixture) : HttpMessageHandler
    {
        public List<string> Requests { get; } = []; public Dictionary<string, byte[]> Additional { get; } = [];
        public bool BlockAll; public string? Corrupt;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var url = request.RequestUri!.AbsoluteUri;
            if (BlockAll) throw new InvalidOperationException("No HTTP is allowed during local reuse.");
            Requests.Add(url);
            if (!fixture.Responses.TryGetValue(url, out var bytes) && !Additional.TryGetValue(url, out bytes)) throw new InvalidOperationException("Unexpected payload/metadata request: " + url);
            bytes = bytes.ToArray(); if (Corrupt is not null && url.EndsWith(Corrupt, StringComparison.Ordinal)) bytes[^1] ^= 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("binary/octet-stream"); return Task.FromResult(response);
        }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public InventoryFixture Fixture { get; } = InventoryFixture.Create();
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-inventory-" + Guid.NewGuid().ToString("N"));
        public DiscoveryCatalog Catalog { get; private set; } = null!; public VerifiedObjectStore Objects { get; private set; } = null!;
        public Handler Handler { get; private set; } = null!; public HttpClient Client { get; private set; } = null!;
        public InventoryWorkspacePreparer Preparer { get; private set; } = null!;
        public static async Task<Harness> Create()
        {
            var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); h.Objects = new(h.Directory);
            h.Handler = new(h.Fixture); h.Client = new(h.Handler); h.Preparer = new(h.Catalog, h.Objects, h.Client);
            var g = await h.Catalog.BeginRefreshAsync("wow", default); await h.Catalog.CommitAsync("wow", g, h.Fixture.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            return h;
        }
        public Task<WorkspaceSnapshot> Open() => Preparer.PrepareAsync(new("wow", Fixture.BuildIdentity, "us"), _ => Task.CompletedTask, default);
        public string PayloadUrl(string key) => $"https://us.cdn.blizzard.com/tpr/wow/data/{key[..2]}/{key[2..4]}/{key}";
        public async Task Execute(string sql)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync();
            await using var cmd = db.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync();
        }
        public async Task<long> Scalar(string sql)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync();
            await using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        public async ValueTask DisposeAsync()
        { Preparer.Dispose(); Client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
