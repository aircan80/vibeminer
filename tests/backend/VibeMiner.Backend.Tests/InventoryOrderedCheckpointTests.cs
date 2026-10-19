using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class InventoryOrderedCheckpointTests
{
    [Theory]
    [InlineData("download",16)]
    [InlineData("size",9)]
    public async Task Completed_matching_survives_catalog_restart_without_reordering_or_matching(string family,int width)
    {
        await using var fixture=await CheckpointFixture.CreateAsync(family,width,3);
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,family,3,_=>Task.CompletedTask,cancel.Token,()=>cancel.Cancel()));
        Assert.Equal(1,fixture.Scalar("SELECT matched FROM inventory_merge_state"));
        Assert.Equal(3,fixture.Scalar("SELECT count(*) FROM inventory_merge_results"));
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_units"));
        await fixture.RestartAsync();
        var progress=new List<string>();var saving=false;
        await fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,family,3,text=>{progress.Add(text);return Task.CompletedTask;},default,()=>saving=true);
        Assert.True(saving);Assert.DoesNotContain(progress,text=>text.StartsWith("Ordering",StringComparison.Ordinal)||text.StartsWith("Resolving",StringComparison.Ordinal));
        Assert.Equal(3,fixture.Scalar("SELECT saved FROM inventory_merge_state"));
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_merge_results"));
        Assert.Equal(3,fixture.Scalar("SELECT count(*) FROM inventory_origins WHERE ekey IS NOT NULL"));
        Assert.Equal(2,fixture.Scalar("SELECT count(*) FROM inventory_units"));
        Assert.Equal(21,fixture.Scalar("SELECT sum(bytes) FROM inventory_units"));
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
    }

    [Fact]
    public async Task Interrupted_matching_restarts_only_matching_and_does_not_publish_orphan_units()
    {
        const int total=17000;
        await using var fixture=await CheckpointFixture.CreateAsync("download",16,total);
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"download",total,text=>
        {if(text.StartsWith("Resolving",StringComparison.Ordinal)&&text.Contains("16,384 of",StringComparison.Ordinal))cancel.Cancel();return Task.CompletedTask;},cancel.Token));
        Assert.Equal(0,fixture.Scalar("SELECT matched FROM inventory_merge_state"));
        Assert.Equal(16384,fixture.Scalar("SELECT count(*) FROM inventory_merge_results"));
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_units"));
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_origins WHERE ekey IS NOT NULL"));
        await fixture.RestartAsync();
        var ordering=false;
        await fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"download",total,text=>
        {
            if(text.StartsWith("Ordering",StringComparison.Ordinal))
            {ordering=true;Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_merge_results"));}
            return Task.CompletedTask;
        },default);
        Assert.True(ordering);Assert.Equal(total,fixture.Scalar("SELECT saved FROM inventory_merge_state"));
        Assert.Equal(2,fixture.Scalar("SELECT count(*) FROM inventory_units"));
        Assert.Equal(21,fixture.Scalar("SELECT sum(bytes) FROM inventory_units"));
    }

    [Fact]
    public async Task Interrupted_saving_resumes_remaining_rows_and_saved_counter_in_same_generation()
    {
        const int total=17000;
        await using var fixture=await CheckpointFixture.CreateAsync("download",16,total);
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"download",total,text=>
        {if(text.StartsWith("Saving",StringComparison.Ordinal)&&text.Contains("16,384 of",StringComparison.Ordinal))cancel.Cancel();return Task.CompletedTask;},cancel.Token));
        Assert.Equal(1,fixture.Scalar("SELECT matched FROM inventory_merge_state"));
        Assert.Equal(16384,fixture.Scalar("SELECT saved FROM inventory_merge_state"));
        Assert.Equal(total-16384,fixture.Scalar("SELECT count(*) FROM inventory_merge_results"));
        Assert.Equal(16384,fixture.Scalar("SELECT count(*) FROM inventory_origins WHERE ekey IS NOT NULL"));
        await fixture.RestartAsync();
        var progress=new List<string>();
        await fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"download",total,text=>{progress.Add(text);return Task.CompletedTask;},default);
        Assert.Contains(progress,text=>text.StartsWith("Saving download inventory · 16,384 of",StringComparison.Ordinal));
        Assert.DoesNotContain(progress,text=>text.StartsWith("Ordering",StringComparison.Ordinal)||text.StartsWith("Resolving",StringComparison.Ordinal));
        Assert.Equal(total,fixture.Scalar("SELECT saved FROM inventory_merge_state"));
        Assert.Equal(total,fixture.Scalar("SELECT count(*) FROM inventory_origins WHERE ekey IS NOT NULL"));
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_merge_results"));
        Assert.Equal(2,fixture.Scalar("SELECT count(*) FROM inventory_units"));
        Assert.Equal(21,fixture.Scalar("SELECT sum(bytes) FROM inventory_units"));
    }

    [Theory]
    [InlineData("download",16)]
    [InlineData("size",9)]
    public async Task Same_count_saved_mapping_corruption_cannot_hide_behind_duplicate_required_units(string family,int width)
    {
        const int total=17000;
        await using var fixture=await CheckpointFixture.CreateAsync(family,width,total,equalSizes:true);
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,family,total,text=>
        {if(text.StartsWith("Saving",StringComparison.Ordinal)&&text.Contains("16,384 of",StringComparison.Ordinal))cancel.Cancel();return Task.CompletedTask;},cancel.Token));
        var a=new string('a',32);var b=new string('b',32);
        // Other saved A memberships still reference A, so both exact units remain non-orphans.
        fixture.Execute($"UPDATE inventory_origins SET ekey='{b}' WHERE id=3 AND ekey='{a}'");
        Assert.Equal(2,fixture.Scalar("SELECT count(*) FROM inventory_units"));
        Assert.Equal(16384,fixture.Scalar("SELECT count(*) FROM inventory_origins WHERE ekey IS NOT NULL"));
        await fixture.RestartAsync();
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,family,total,_=>Task.CompletedTask,default));
        Assert.Equal("CATALOG_INTEGRITY",error.Code);
        Assert.Equal(16384,fixture.Scalar("SELECT saved FROM inventory_merge_state"));
        Assert.Equal(total-16384,fixture.Scalar("SELECT count(*) FROM inventory_merge_results"));
    }

    [Fact]
    public async Task Saved_size_prefix_mapping_requires_its_full_key_in_verified_EN_or_committed_download_sources()
    {
        const int total=17000;
        await using var fixture=await CheckpointFixture.CreateAsync("size",9,total,equalSizes:true);
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"size",total,text=>
        {if(text.StartsWith("Saving",StringComparison.Ordinal)&&text.Contains("16,384 of",StringComparison.Ordinal))cancel.Cancel();return Task.CompletedTask;},cancel.Token));
        var a=new string('a',32);var fake=new string('a',18)+new string('c',14);
        // All original references still match their nine-byte prefix and all exact unit sizes
        // agree. The fabricated suffix nevertheless has no source-universe membership.
        fixture.Execute($"UPDATE inventory_origins SET ekey='{fake}' WHERE ekey='{a}'; UPDATE inventory_units SET ekey='{fake}' WHERE ekey='{a}';");
        await fixture.RestartAsync();
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"size",total,_=>Task.CompletedTask,default));
        Assert.Equal("CATALOG_INTEGRITY",error.Code);
        Assert.Equal(16384,fixture.Scalar("SELECT saved FROM inventory_merge_state"));
    }

    [Theory]
    [InlineData("UPDATE inventory_merge_results SET encoded=encoded+1")]
    [InlineData("UPDATE inventory_merge_results SET proof=zeroblob(32)")]
    [InlineData("UPDATE inventory_merge_results SET proof=zeroblob(1000000)")]
    [InlineData("UPDATE inventory_merge_results SET id=id+9")]
    [InlineData("UPDATE inventory_merge_state SET saved=1")]
    [InlineData("UPDATE inventory_merge_state SET total=2")]
    [InlineData("UPDATE inventory_merge_state SET matched=2")]
    public async Task Tampered_durable_result_or_match_state_cannot_be_saved(string mutation)
    {
        await using var fixture=await CheckpointFixture.CreateAsync("download",16,1);
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"download",1,_=>Task.CompletedTask,cancel.Token,()=>cancel.Cancel()));
        fixture.Execute(mutation);
        await fixture.RestartAsync();
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>fixture.Catalog.ResolveOrderedEncodedAsync(fixture.Generation,"download",1,_=>Task.CompletedTask,default));
        Assert.Equal("CATALOG_INTEGRITY",error.Code);
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_units"));
        Assert.Equal(0,fixture.Scalar("SELECT count(*) FROM inventory_origins WHERE ekey IS NOT NULL"));
    }

    private sealed class CheckpointFixture:IAsyncDisposable
    {
        private readonly TempDirectory directory=new();
        public DiscoveryCatalog Catalog {get;private set;}=null!;
        public long Generation {get;private set;}
        public static async Task<CheckpointFixture> CreateAsync(string family,int width,int total,bool equalSizes=false)
        {
            // The fixture owns one isolated catalogue throughout close/reopen.
            var fixture=new CheckpointFixture();fixture.Catalog=await DiscoveryCatalog.OpenAsync(fixture.directory.Path);
            var source=InventoryFixture.Create();var refresh=await fixture.Catalog.BeginRefreshAsync("wow",default);
            await fixture.Catalog.CommitAsync("wow",refresh,source.Discovery,"2026-10-18T12:00:00Z",default);
            var context=await fixture.Catalog.ResolveOpeningAsync(new("wow",source.BuildIdentity,"us"),default);
            fixture.Generation=await fixture.Catalog.StartInventoryAsync(context,default);
            var a=new string('a',32);var b=new string('b',32);
            await fixture.Catalog.AddEncodingAsync(fixture.Generation,[],[new(a,10),new(b,equalSizes?10:11)],default);
            var rows=Enumerable.Range(1,total).Select(i=>
            {
                var key=(i%2==0?b:a);return new InventoryOrigin(i*3L,family,null,null,null,key[..(width*2)],width,i%2==0&&!equalSizes?11:10,null,null,null,null,"authored checkpoint");
            }).ToArray();
            foreach(var chunk in rows.Chunk(1024))await fixture.Catalog.AddOriginsAsync(fixture.Generation,chunk,default);
            return fixture;
        }
        public async Task RestartAsync(){await Catalog.DisposeAsync();Catalog=await DiscoveryCatalog.OpenAsync(directory.Path);}
        public void Execute(string sql){using var db=Database();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        public long Scalar(string sql){using var db=Database();using var command=db.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
        private SqliteConnection Database(){var db=new SqliteConnection($"Data Source={Path.Combine(directory.Path,"discovery-v1.sqlite3")};Pooling=False");db.Open();return db;}
        public async ValueTask DisposeAsync(){await Catalog.DisposeAsync();directory.Dispose();}
    }
}
