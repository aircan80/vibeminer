using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private readonly SemaphoreSlim orderedResolution = new(1,1);
    // One read-only WAL snapshot supplies sorted immutable inputs; the existing database worker
    // owns it and the writer. No thread per record, no live reader on the writer connection.
    internal async Task ResolveOrderedEncodedAsync(long generation, string family, long total,
        Func<string,Task> progress, CancellationToken token, Action? savingStarted = null)
    {
        if (family is not ("download" or "size")) throw new ArgumentException("Expected an encoded manifest family.",nameof(family));
        if (!await orderedResolution.WaitAsync(0,token)) throw new DiscoveryException("OPEN_BUSY","Another ordered resolver is active.",true);
        OrderedResolutionSession? session = null;
        try
        {
            var checkpoint = await ReadOrderedCheckpointAsync(generation,family,total,progress,token);
            if (!checkpoint.Matched)
            {
                await progress(InventoryProgress.Format($"Resolving {family} inventory",0,total,"entries"));
                await ResetIncompleteOrderedMatchAsync(generation,family,total,progress,token);
                await progress($"Ordering {family} inventory for merge");
                session = await Schedule(() => OpenOrderedSession(generation,family,token),token);
                long completed = 0, reported = 0;
                while (true)
                {
                    var batch = await Schedule(() => session.Cursor.Read(8192,1024,token),token);
                    if (batch.Rows.Count > 0)
                    {
                        await PublishEncodedBatchAsync(generation,family,batch.Rows,token);
                        completed = checked(completed + batch.Rows.Count);
                    }
                    if (completed-reported >= 16384 || batch.Complete)
                    {
                        await progress(InventoryProgress.Format($"Resolving {family} inventory",completed,total,"entries")); reported = completed;
                    }
                    if (!batch.Complete) continue;
                    if (completed != total) throw new DiscoveryException("CATALOG_INTEGRITY", "Ordered inventory count differs from its verified source.");
                    break;
                }
                await Schedule(() => { session.Dispose();return true; },CancellationToken.None);session = null;
                await MarkOrderedMatchCompleteAsync(generation,family,total,token);
                checkpoint = (true,0L);
            }
            // The durable matched checkpoint survives process exit. Saving consumes its rows in
            // source-id order; no snapshot, prefix merge or duplicate transfer is repeated here.
            var saved = checkpoint.Saved;
            savingStarted?.Invoke();
            await progress(InventoryProgress.Format($"Saving {family} inventory",saved,total,"entries"));
            while (true)
            {
                var applied = await ApplyOrderedOriginsAsync(generation,family,token);
                if (applied.Count==0) break;
                saved = applied.Saved;
                if (saved%16384<1024 || saved==total)
                    await progress(InventoryProgress.Format($"Saving {family} inventory",saved,total,"entries"));
            }
            if (saved!=total) throw new DiscoveryException("CATALOG_INTEGRITY","Ordered inventory publication lost memberships.");
        }
        finally
        {
            try
            {
                if (session is not null) await Schedule(() => { session.Dispose(); return true; },CancellationToken.None);
            }
            finally { orderedResolution.Release(); }
        }
    }
    private async Task<(bool Matched,long Saved)> ReadOrderedCheckpointAsync(long generation,string family,long total,Func<string,Task> progress,CancellationToken token)
    {
        var checkpoint=await Schedule(() =>
        {
        RequireStagingInventory(generation);
        if(total<0 || Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_origins WHERE g=$g AND family=$f",null,("$g",generation),("$f",family)))!=total)
            throw new DiscoveryException("CATALOG_INTEGRITY","Ordered checkpoint source count differs from its verified source.");
        using var command=Command("SELECT total,matched,saved FROM inventory_merge_state WHERE g=$g AND family=$f",null,("$g",generation),("$f",family));
        using var reader=command.ExecuteReader();
        var exists=reader.Read();var declared=exists?reader.GetInt64(0):total;var matched=exists?reader.GetInt64(1):0;var saved=exists?reader.GetInt64(2):0;
        reader.Close();
        var resolved=Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_origins WHERE g=$g AND family=$f AND ekey IS NOT NULL",null,("$g",generation),("$f",family)));
        var pending=Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_merge_results WHERE g=$g AND family=$f",null,("$g",generation),("$f",family)));
        if(declared!=total || matched is not (0 or 1) || saved<0 || saved>total || pending>total || !exists && pending!=0 || resolved!=saved || matched==0 && saved!=0 || matched==1 && pending!=total-saved)
            throw new DiscoveryException("CATALOG_INTEGRITY","Ordered checkpoint counts or state cannot be verified.");
        return (Matched:matched==1,Saved:saved);
        },token);
        if(checkpoint.Saved>0)
        {
            long after=0,verified=0,reported=0;
            await progress(InventoryProgress.Format($"Verifying saved {family} matches",0,checkpoint.Saved,"entries"));
            while(verified<checkpoint.Saved)
            {
                var batch=await Schedule(() =>
                {
                    RequireStagingInventory(generation);
                    using var command=Command("""
                        SELECT o.id,CASE WHEN length(o.reference) IN (18,32) THEN o.reference ELSE NULL END,
                            o.width,o.declared_encoded,CASE WHEN length(o.ekey)=32 THEN o.ekey ELSE NULL END,
                            o.encoded,u.bytes,o.ckey,
                            CASE WHEN o.width!=9 THEN 1 ELSE
                                EXISTS(SELECT 1 FROM en_objects e WHERE e.g=o.g AND e.ekey=o.ekey AND e.bytes=o.encoded)
                                OR EXISTS(SELECT 1 FROM inventory_origins d WHERE d.g=o.g AND d.family='download'
                                    AND d.reference=o.ekey AND d.width=16 AND d.ekey=d.reference
                                    AND d.encoded=d.declared_encoded AND d.declared_encoded=o.encoded) END
                        FROM inventory_origins o LEFT JOIN inventory_units u ON u.g=o.g AND u.ekey=o.ekey
                        WHERE o.g=$g AND o.family=$f AND o.id>$after AND o.ekey IS NOT NULL
                        ORDER BY o.id LIMIT $limit
                        """,null,("$g",generation),("$f",family),("$after",after),("$limit",Math.Min(1024,checkpoint.Saved-verified)));
                    using var reader=command.ExecuteReader();int count=0;var last=after;
                    while(reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if(reader.IsDBNull(1)||reader.IsDBNull(2)||reader.IsDBNull(3)||reader.IsDBNull(4)||reader.IsDBNull(5)||reader.IsDBNull(6)||!reader.IsDBNull(7))
                            throw new DiscoveryException("CATALOG_INTEGRITY","Previously saved ordered membership is incomplete.");
                        var reference=reader.GetString(1);var width=reader.GetInt32(2);var key=reader.GetString(4);var bytes=reader.GetInt64(5);
                        if(width is not (9 or 16)||family=="download"&&width!=16||reference.Length!=width*2||!key.StartsWith(reference,StringComparison.Ordinal)||bytes<0||reader.GetInt64(3)!=bytes||reader.GetInt64(6)!=bytes||reader.GetInt64(8)!=1||key.Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                            throw new DiscoveryException("CATALOG_INTEGRITY","Previously saved ordered membership differs from its verified source or object universe.");
                        last=reader.GetInt64(0);count++;
                    }
                    return (Count:count,After:last);
                },token);
                if(batch.Count==0)throw new DiscoveryException("CATALOG_INTEGRITY","Previously saved ordered memberships are missing.");
                verified+=batch.Count;after=batch.After;
                if(verified-reported>=16384||verified==checkpoint.Saved)
                {await progress(InventoryProgress.Format($"Verifying saved {family} matches",verified,checkpoint.Saved,"entries"));reported=verified;}
            }
        }
        return checkpoint;
    }
    private async Task ResetIncompleteOrderedMatchAsync(long generation,string family,long total,Func<string,Task> progress,CancellationToken token)
    {
        var discarded = await Schedule(() => Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_merge_results WHERE g=$g AND family=$f", null, ("$g", generation), ("$f", family))), token);
        long removed = 0;
        var lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
        if (discarded > 0) await progress(InventoryProgress.Format($"Cleaning up unfinished {family} match data", 0, discarded, "rows"));
        while(true)
        {
            var deleted=await Schedule(() =>
            {
                RequireStagingInventory(generation);CheckOrderedJournalBudget();
                using var command=Command("DELETE FROM inventory_merge_results WHERE rowid IN (SELECT rowid FROM inventory_merge_results WHERE g=$g AND family=$f LIMIT 8192)",null,("$g",generation),("$f",family));
                return command.ExecuteNonQuery();
            },token);
            removed += deleted;
            if (discarded > 0 && (removed == discarded || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 500))
            {
                await progress(InventoryProgress.Format($"Cleaning up unfinished {family} match data", removed, discarded, "rows"));
                lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            if(deleted<8192)break;
        }
        await Schedule(() =>
        {
            RequireStagingInventory(generation);
            using var command=Command("INSERT INTO inventory_merge_state(g,family,total,matched,saved) VALUES($g,$f,$n,0,0) ON CONFLICT(g,family) DO UPDATE SET total=$n,matched=0,saved=0 WHERE inventory_merge_state.matched=0",null,("$g",generation),("$f",family),("$n",total));
            if(command.ExecuteNonQuery()!=1)throw new DiscoveryException("CATALOG_INTEGRITY","Ordered match restart lost its staging fence.");
            return true;
        },token);
    }
    private Task MarkOrderedMatchCompleteAsync(long generation,string family,long total,CancellationToken token) => Schedule(() =>
    {
        RequireStagingInventory(generation);
        using var transaction=Db.BeginTransaction();
        var count=Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_merge_results WHERE g=$g AND family=$f",transaction,("$g",generation),("$f",family)));
        if(count!=total)throw new DiscoveryException("CATALOG_INTEGRITY","Ordered match results are incomplete.");
        using var command=Command("UPDATE inventory_merge_state SET matched=1 WHERE g=$g AND family=$f AND total=$n AND saved=0 AND matched=0",transaction,("$g",generation),("$f",family),("$n",total));
        if(command.ExecuteNonQuery()!=1)throw new DiscoveryException("CATALOG_INTEGRITY","Ordered match completion lost its staging fence.");
        token.ThrowIfCancellationRequested();transaction.Commit();return true;
    },token);
    private OrderedResolutionSession OpenOrderedSession(long generation,string family,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequireStagingInventory(generation);
        var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource=databasePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=5 }.ToString());
        SqliteTransaction? read = null;
        try
        {
            snapshot.Open();
            using (var settings = snapshot.CreateCommand())
            { settings.CommandText="PRAGMA query_only=ON; PRAGMA cache_size=-32768; PRAGMA mmap_size=0; PRAGMA temp_store=FILE"; settings.ExecuteNonQuery(); }
            read = snapshot.BeginTransaction(deferred:true);
            SqliteCommand Query(string sql)
            { var command=snapshot.CreateCommand();command.Transaction=read;command.CommandText=sql;command.Parameters.AddWithValue("$g",generation);return command; }
            int width;
            using (var command=Query("SELECT width FROM inventory_origins WHERE g=$g AND family=$f ORDER BY id LIMIT 1"))
            { command.Parameters.AddWithValue("$f",family); width=command.ExecuteScalar() is { } value ? Convert.ToInt32(value) : 16; }
            if (width is not (9 or 16) || family=="download" && width!=16) throw new DiscoveryException("CATALOG_INTEGRITY","Invalid encoded-reference width.");
            IEnumerable<CompactEncodedObject> Objects(string table)
            {
                using var command=Query($"SELECT ekey,bytes FROM {table} WHERE g=$g ORDER BY ekey");
                using var reader=command.ExecuteReader();
                while (reader.Read()) { token.ThrowIfCancellationRequested(); yield return new(InventoryKey.Parse(reader.GetString(0)),reader.GetInt64(1)); }
            }
            IEnumerable<CompactSizeRequest> Requests()
            {
                using var command=Query("SELECT id,reference,declared_encoded,width FROM inventory_origins WHERE g=$g AND family=$f ORDER BY reference,id");
                command.Parameters.AddWithValue("$f",family);using var reader=command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.GetInt32(3)!=width || reader.GetString(1).Length!=width*2) throw new DiscoveryException("CATALOG_INTEGRITY","Mixed encoded-reference widths.");
                    yield return new(reader.GetInt64(0),InventoryKey.Parse(reader.GetString(1)),reader.GetInt64(2));
                }
            }
            var universe = family=="size" ? MergeObjectSources(Objects("en_objects"),Objects("inventory_units"),token) : Objects("en_objects");
            return new(snapshot,read,new(universe,Requests(),width,allowAbsentExact:width==16));
        }
        catch { read?.Dispose();snapshot.Dispose();throw; }
    }
    private static IEnumerable<CompactEncodedObject> MergeObjectSources(IEnumerable<CompactEncodedObject> encoding,
        IEnumerable<CompactEncodedObject> required,CancellationToken token)
    {
        using var left=encoding.GetEnumerator();using var right=required.GetEnumerator();
        var l=left.MoveNext();var r=right.MoveNext();
        while (l||r)
        {
            token.ThrowIfCancellationRequested();
            var comparison=!l ? 1 : !r ? -1 : left.Current.Key.CompareTo(right.Current.Key);
            if(comparison<0) { yield return left.Current;l=left.MoveNext(); }
            else if(comparison>0) { yield return right.Current;r=right.MoveNext(); }
            else
            {
                if(left.Current.Bytes!=right.Current.Bytes) throw new DiscoveryException("INVENTORY_SIZE_CONFLICT","Required and EN encoded lengths disagree.");
                yield return left.Current;l=left.MoveNext();r=right.MoveNext();
            }
        }
    }
    private void RequireStagingInventory(long generation)
    {
        if ((string?)Scalar("SELECT state FROM inventory_generations WHERE id=$g",null,("$g",generation))!="staging")
            throw new DiscoveryException("CATALOG_INTEGRITY","Only an owned staging inventory can be resolved.");
    }
    private void CheckOrderedJournalBudget()
    {
        if(new FileInfo(databasePath+"-wal") is { Exists:true, Length:>2L*1024*1024*1024 })
            throw new DiscoveryException("INVENTORY_LIMIT","Ordered resolution exceeds its snapshot journal budget.");
    }
    private static byte[] OrderedResultProof(long generation,string family,long id,string key,long bytes)
    {
        if(key.Length!=32 || key.Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || bytes<0)
            throw new DiscoveryException("CATALOG_INTEGRITY","Ordered result has an invalid exact identity or length.");
        Span<byte> input=stackalloc byte[49];"VMIMR001"u8.CopyTo(input);
        BinaryPrimitives.WriteInt64BigEndian(input[8..],generation);input[16]=family=="download"?(byte)1:(byte)2;
        BinaryPrimitives.WriteInt64BigEndian(input[17..],id);Convert.FromHexString(key).CopyTo(input[25..]);
        BinaryPrimitives.WriteInt64BigEndian(input[41..],bytes);return SHA256.HashData(input);
    }
    private Task PublishEncodedBatchAsync(long generation,string family,IReadOnlyList<ResolvedEncodedOrigin> rows,CancellationToken token) => Schedule(() =>
    {
        RequireStagingInventory(generation);CheckOrderedJournalBudget();
        using var tx=Db.BeginTransaction();
        using var origin=Command("INSERT INTO inventory_merge_results(g,family,id,ekey,encoded,proof) VALUES($g,$f,$i,$e,$n,$p)",tx,("$g",generation),("$f",family),("$i",0L),("$e",""),("$n",0L),("$p",Array.Empty<byte>()));
        origin.Prepare();
        foreach(var row in rows)
        {
            token.ThrowIfCancellationRequested();var key=row.Object.Key.ToString();
            origin.Parameters[2].Value=row.Id;origin.Parameters[3].Value=key;origin.Parameters[4].Value=row.Object.Bytes;
            origin.Parameters[5].Value=OrderedResultProof(generation,family,row.Id,key,row.Object.Bytes);
            if(origin.ExecuteNonQuery()!=1)throw new DiscoveryException("CATALOG_INTEGRITY","An ordered membership changed during matching.");
        }
        token.ThrowIfCancellationRequested();tx.Commit();return true;
    },token);
    private Task<(int Count,long Saved)> ApplyOrderedOriginsAsync(long generation,string family,CancellationToken token) => Schedule(() =>
    {
        RequireStagingInventory(generation);CheckOrderedJournalBudget();
        var rows=new List<(long Id,string Key,long Bytes)>();
        using(var cmd=Command("SELECT r.id,CASE WHEN length(r.ekey)=32 THEN r.ekey ELSE NULL END,r.encoded,CASE WHEN length(r.proof)=32 THEN r.proof ELSE NULL END,o.reference,o.width,o.declared_encoded,o.ekey,o.ckey FROM inventory_merge_results r LEFT JOIN inventory_origins o ON o.g=r.g AND o.family=r.family AND o.id=r.id WHERE r.g=$g AND r.family=$f ORDER BY r.id LIMIT 1024",null,("$g",generation),("$f",family)))
        using(var reader=cmd.ExecuteReader())while(reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if(reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))throw new DiscoveryException("CATALOG_INTEGRITY","Saved ordered result identity or proof has an invalid width.");
            var id=reader.GetInt64(0);var key=reader.GetString(1);var bytes=reader.GetInt64(2);
            var proof=(byte[])reader[3];
            if(proof.Length!=32 || !CryptographicOperations.FixedTimeEquals(proof,OrderedResultProof(generation,family,id,key,bytes)) || reader.IsDBNull(4) || reader.IsDBNull(5) || reader.IsDBNull(6) || !reader.IsDBNull(7) || !reader.IsDBNull(8))
                throw new DiscoveryException("CATALOG_INTEGRITY","Saved ordered result proof or source membership cannot be verified.");
            var reference=reader.GetString(4);var width=reader.GetInt32(5);
            if(width is not (9 or 16) || family=="download" && width!=16 || reference.Length!=width*2 || !key.StartsWith(reference,StringComparison.Ordinal) || reader.GetInt64(6)!=bytes)
                throw new DiscoveryException("CATALOG_INTEGRITY","Saved ordered result differs from its original reference or size.");
            rows.Add((id,key,bytes));
        }
        var saved=Convert.ToInt64(Scalar("SELECT saved FROM inventory_merge_state WHERE g=$g AND family=$f AND matched=1",null,("$g",generation),("$f",family)));
        if(rows.Count==0)return (0,saved);
        using var tx=Db.BeginTransaction();
        using var unit=Command("INSERT INTO inventory_units(g,ekey,bytes) VALUES($g,$e,$n) ON CONFLICT(g,ekey) DO UPDATE SET bytes=inventory_units.bytes WHERE inventory_units.bytes=excluded.bytes",tx,("$g",generation),("$e",""),("$n",0L));
        using var update=Command("UPDATE inventory_origins SET ekey=$e,encoded=$n WHERE g=$g AND family=$f AND id=$i AND ckey IS NULL AND ekey IS NULL",tx,("$e",""),("$n",0L),("$g",generation),("$f",family),("$i",0L));
        unit.Prepare();update.Prepare();
        foreach(var row in rows)
        {
            token.ThrowIfCancellationRequested();unit.Parameters[1].Value=row.Key;unit.Parameters[2].Value=row.Bytes;
            if(unit.ExecuteNonQuery()!=1)throw new DiscoveryException("INVENTORY_SIZE_CONFLICT","One exact encoded object has conflicting published sizes.");
            update.Parameters[0].Value=row.Key;update.Parameters[1].Value=row.Bytes;update.Parameters[4].Value=row.Id;
            if(update.ExecuteNonQuery()!=1)throw new DiscoveryException("CATALOG_INTEGRITY","An ordered membership changed during publication.");
        }
        Execute("DELETE FROM inventory_merge_results WHERE g=$g AND family=$f AND id<=$last",tx,("$g",generation),("$f",family),("$last",rows[^1].Id));
        using var state=Command("UPDATE inventory_merge_state SET saved=saved+$count WHERE g=$g AND family=$f AND matched=1 AND saved=$saved AND saved+$count<=total",tx,("$g",generation),("$f",family),("$saved",saved),("$count",rows.Count));
        if(state.ExecuteNonQuery()!=1)throw new DiscoveryException("CATALOG_INTEGRITY","Ordered publication lost its checkpoint fence.");
        token.ThrowIfCancellationRequested();tx.Commit();return (rows.Count,checked(saved+rows.Count));
    },token);
    private sealed class OrderedResolutionSession(SqliteConnection connection,SqliteTransaction read,OrderedInventoryCursor cursor) : IDisposable
    {
        internal OrderedInventoryCursor Cursor => cursor;
        public void Dispose() { try { cursor.Dispose(); } finally { try { read.Dispose(); } finally { connection.Dispose(); } } }
    }
}
