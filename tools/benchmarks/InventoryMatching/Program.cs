using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;

// Explicit developer workload. Read-only comparison of one app-owned interrupted inventory; integrated mode requires a disposable clone.
// only aggregate results go to stdout. No CDN, game-install access or application admission.
if (args.Length != 4) throw new ArgumentException("Usage: <prepare|map|merge|direct|baseline|publish|integrated|root> <app-owned-data-dir> <generation> <scratch-dir>");
var mode = args[0]; var directory = Path.GetFullPath(args[1]); var generation = long.Parse(args[2], CultureInfo.InvariantCulture);
var scratch = Path.GetFullPath(args[3]); Directory.CreateDirectory(scratch);
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var token = stop.Token;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var phases = new Dictionary<string, double>();
var timer = Stopwatch.StartNew();
void Lap(string name) { phases[name] = timer.Elapsed.TotalSeconds; timer.Restart(); }
SqliteConnection Open()
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory,"discovery-v1.sqlite3"),Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString());
    connection.Open(); using var c=connection.CreateCommand(); c.CommandText="PRAGMA cache_size=-32768; PRAGMA temp_store=FILE; PRAGMA query_only=ON"; c.ExecuteNonQuery(); return connection;
}
SqliteCommand Query(SqliteConnection connection, string sql)
{
    var c=connection.CreateCommand(); c.CommandText=sql; c.Parameters.AddWithValue("$g",generation); return c;
}
if (mode=="prepare")
{
    using var db=Open();
    long Count(string table,string extra="") {using var c=Query(db,$"SELECT COUNT(*) FROM {table} WHERE g=$g {extra}");return Convert.ToInt64(c.ExecuteScalar());}
    var n=Count("en_objects"); var requests=Count("inventory_origins","AND family='size'");
    if(n>int.MaxValue || requests>int.MaxValue)throw new InvalidOperationException("Workload too large.");
    using var query=Query(db,"SELECT MIN(id),MIN(width),MAX(width) FROM inventory_origins WHERE g=$g AND family='size'"); using var r=query.ExecuteReader();r.Read();
    var first=r.GetInt64(0); var width=r.GetInt32(1);if(width!=r.GetInt32(2))throw new InvalidOperationException("Mixed DS widths.");r.Close();
    using var missing=Query(db,"SELECT COUNT(*) FROM inventory_origins o WHERE o.g=$g AND o.family='download' AND NOT EXISTS(SELECT 1 FROM en_objects e WHERE e.g=o.g AND e.ekey=o.reference)");
    if(Convert.ToInt64(missing.ExecuteScalar())!=0)throw new InvalidOperationException("This focused EN/DS benchmark requires all explicit DL keys to be in EN; do not generalize to other inventories.");
    var receipts=new List<MetadataReceipt>();using var q=db.CreateCommand();q.CommandText="SELECT receipt FROM metadata_objects WHERE json_extract(receipt,'$.role') IN ('encoding','size')";
    using var rows=q.ExecuteReader();while(rows.Read())receipts.Add(JsonSerializer.Deserialize<MetadataReceipt>(rows.GetString(0),options)!);
    if(receipts.Count!=2)throw new InvalidOperationException("Expected one exact EN and DS receipt.");
    var input=new Inputs((int)n,(int)requests,first,width,receipts.ToArray());
    await File.WriteAllTextAsync(Path.Combine(scratch,"inputs.json"),JsonSerializer.Serialize(input,options),token);
    Console.WriteLine(JsonSerializer.Serialize(new{mode,objects=n,requests,width,seconds=timer.Elapsed.TotalSeconds},options));return;
}

if (mode is "integrated" or "root")
{
    if(directory!=Path.Combine(scratch,"integration-catalog"))throw new ArgumentException("Integrated writes require the explicitly named scratch integration-catalog clone.");
    var families=mode=="root" ? new[]{"root","install"} : new[]{"download","size"};
    var counts=new Dictionary<string,long>();
    using(var db=Open())foreach(var family in families)
    {using var cmd=Query(db,"SELECT count(*) FROM inventory_origins WHERE g=$g AND family=$f");cmd.Parameters.AddWithValue("$f",family);counts[family]=Convert.ToInt64(cmd.ExecuteScalar());}
    await using(var catalog=await DiscoveryCatalog.OpenAsync(directory,token))
    {
        foreach(var family in families)
        {
            if(mode=="root")
            {
                long after=0,completed=0;
                while(true)
                {
                    var batch=await catalog.ResolveOriginBatchAsync(generation,family,after,token);
                    if(batch.Count==0)break;
                    after=batch.After;completed+=batch.Count;
                    if(completed%16384<256||completed==counts[family])Console.Error.WriteLine($"Resolved {family}: {completed} / {counts[family]}");
                }
                if(completed!=counts[family])throw new InvalidOperationException("Incomplete content resolution.");
                Lap(family+"_forward_lookup_and_publication");
            }
            else
            {
                await catalog.ResolveOrderedEncodedAsync(generation,family,counts[family],text=>{Console.Error.WriteLine(text);return Task.CompletedTask;},token);
                Lap(family+"_ordered_match_and_publication");
            }
        }
    }
    var scope=mode=="root" ? "'root','install'" : "'download','size'";
    using var verified=Open();using var unresolved=Query(verified,$"SELECT count(*) FROM inventory_origins WHERE g=$g AND family IN ({scope}) AND ekey IS NULL");
    if(Convert.ToInt64(unresolved.ExecuteScalar())!=0)throw new InvalidOperationException("Unresolved encoded memberships remain.");
    using var mismatch=Query(verified,$"SELECT count(*) FROM inventory_origins o LEFT JOIN inventory_units u ON u.g=o.g AND u.ekey=o.ekey WHERE o.g=$g AND o.family IN ({scope}) AND (u.ekey IS NULL OR u.bytes!=o.encoded)");
    if(Convert.ToInt64(mismatch.ExecuteScalar())!=0)throw new InvalidOperationException("Resolved ledger disagrees with manifest sizes.");
    if(mode=="integrated")
    {
        using var declared=Query(verified,"SELECT count(*) FROM inventory_origins WHERE g=$g AND family IN ('download','size') AND encoded!=declared_encoded");
        if(Convert.ToInt64(declared.ExecuteScalar())!=0)throw new InvalidOperationException("Resolved encoded sizes disagree with source declarations.");
    }
    var measured=Process.GetCurrentProcess();measured.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new{mode,counts,phases,totalSeconds=phases.Values.Sum(),peakWorkingSetBytes=measured.PeakWorkingSet64,unresolved=0,mismatchedSizes=0,notes="Writes only a disposable app-owned clone; no source parsing, digest, seal or Home admission included."},options));return;
}
var data=JsonSerializer.Deserialize<Inputs>(await File.ReadAllTextAsync(Path.Combine(scratch,"inputs.json"),token),options)!;
var check=new Check();long sourceRows=0;
IEnumerable<CompactEncodedObject> Objects(SqliteConnection db)
{
    using var c=Query(db,"SELECT ekey,bytes FROM en_objects WHERE g=$g ORDER BY ekey");using var r=c.ExecuteReader();
    while(r.Read()){token.ThrowIfCancellationRequested();yield return new(InventoryKey.Parse(r.GetString(0)),r.GetInt64(1));}
}
IEnumerable<CompactSizeRequest> Requests(SqliteConnection db,bool sorted,int? limit=null)
{
    using var c=Query(db,$"SELECT id,reference,declared_encoded FROM inventory_origins WHERE g=$g AND family='size' ORDER BY {(sorted?"reference,id":"id")} {(limit is {} cap?"LIMIT "+cap:"")}");
    using var r=c.ExecuteReader();while(r.Read()){token.ThrowIfCancellationRequested();yield return new(r.GetInt64(0)-data.FirstSizeId+1,InventoryKey.Parse(r.GetString(1)),r.GetInt64(2));}
}
if(mode=="map")
{
    using var db=Open();
    var map=CompactInventoryMatching.BuildMap(Objects(db),data.Objects,data.Width,token);sourceRows=map.Count;
    Lap("read_sqlite_objects_and_build_map");
    foreach(var request in Requests(db,false))check.Add(request,CompactInventoryMatching.Resolve(map,request,data.Width));
    Lap("read_sqlite_requests_and_match");
    GC.KeepAlive(map);
}
else if(mode=="merge")
{
    using var db=Open();
    // Measure SQLite's sort/first-row cost separately; remaining ordered reads and merge stream together.
    using var cmd=Query(db,"SELECT id,reference,declared_encoded FROM inventory_origins WHERE g=$g AND family='size' ORDER BY reference,id");
    using var reader=cmd.ExecuteReader();var has=reader.Read();Lap("sqlite_request_sort_and_first_row");
    IEnumerable<CompactSizeRequest> Ordered()
    {
        while(has){token.ThrowIfCancellationRequested();var row=new CompactSizeRequest(reader.GetInt64(0)-data.FirstSizeId+1,InventoryKey.Parse(reader.GetString(1)),reader.GetInt64(2));has=reader.Read();yield return row;}
    }
    CompactInventoryMatching.Merge(Objects(db),Ordered(),data.Width,check.Add,token);Lap("read_both_sqlite_streams_and_merge");sourceRows=data.Objects;
}
else if(mode=="direct")
{
    var decoded=new Dictionary<string,string>();
    try
    {
        foreach(var receipt in data.Receipts)
        {
            var path=Path.Combine(directory,"objects-v1",receipt.Sha256[..2],receipt.Sha256[2..]);
            await using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
            if(source.Length!=long.Parse(receipt.EncodedBytes,CultureInfo.InvariantCulture)||Convert.ToHexStringLower(await SHA256.HashDataAsync(source,token))!=receipt.Sha256)throw new InvalidOperationException("Retained source SHA/length mismatch.");
            source.Position=0;var output=Path.Combine(scratch,receipt.Role+"-decoded.part");decoded.Add(receipt.Role,output);
            await using var target=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
            await BlteDecoder.DecodeAsync(source,target,receipt.EncodingKey,receipt.ContentKey,long.Parse(receipt.EncodedBytes,CultureInfo.InvariantCulture),long.Parse(receipt.DecodedBytes,CultureInfo.InvariantCulture),token);
        }
        Lap("verify_retained_sources_and_decode_blte");
        var consumer=new DirectConsumer();
        await using(var en=new FileStream(decoded["encoding"],FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))await EncodingInventoryReader.ReadAsync(en,new(),consumer,token);
        Lap("parse_full_en_into_compact_array");
        await using(var ds=new FileStream(decoded["size"],FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))await DownloadSizeInventoryReader.ReadAsync(ds,new(),consumer,token);
        Lap("parse_full_ds_into_compact_array");
        if(consumer.ObjectCount!=data.Objects||consumer.Requests.Length!=data.Requests||consumer.Width!=data.Width)throw new InvalidOperationException("Manifest and catalog workload shapes differ.");
        // EN is already verified ascending by its original reader. Only DS needs sorting.
        Array.Sort(consumer.Requests,Comparer<CompactSizeRequest>.Create((a,b)=>a.Reference.CompareTo(b.Reference)));
        token.ThrowIfCancellationRequested();Lap("sort_size_requests_in_memory");
        CompactInventoryMatching.Merge(consumer.Objects.Take(consumer.ObjectCount),consumer.Requests,consumer.Width,check.Add,token);Lap("merge_compact_arrays");sourceRows=consumer.ObjectCount;
        GC.KeepAlive(consumer);
    }
    finally{foreach(var path in decoded.Values)File.Delete(path);}
}
else if(mode=="baseline")
{
    using var db=Open();
    var sample=Requests(db,false,10_000).ToArray();Lap("read_sample_10000_requests");
    // Existing range-lookup shape. No writes, reverse CKey enrichment or worker scheduling included.
    foreach(var request in sample)
    {
        token.ThrowIfCancellationRequested();using var cmd=Query(db,"SELECT ekey,bytes FROM en_objects WHERE g=$g AND ekey >= $lo AND ekey <= $hi LIMIT 2");
        var prefix=request.Reference.ToString()[..(data.Width*2)];cmd.Parameters.AddWithValue("$lo",prefix);cmd.Parameters.AddWithValue("$hi",prefix+new string('f',32-prefix.Length));
        using var reader=cmd.ExecuteReader();if(!reader.Read())throw new InvalidOperationException("Missing prefix");var value=new CompactEncodedObject(InventoryKey.Parse(reader.GetString(0)),reader.GetInt64(1));if(reader.Read())throw new InvalidOperationException("Ambiguous prefix");
        if(value.Bytes!=request.Bytes)throw new InvalidOperationException("Size conflict");check.Add(request,value);
    }
    Lap("per_record_sql_lookup_10000_only");
}
else if(mode=="publish")
{
    // Isolated durable publication workload, not the actual multi-table inventory publication.
    using var source=Open();
    var rows=Objects(source).Take(100_000).Select((row,index)=>new CompactSizeRequest(index+1,row.Key,row.Bytes)).ToArray();
    new Random(86).Shuffle(rows);
    for(var i=0;i<rows.Length;i++)rows[i]=rows[i] with{Id=i+1};
    Lap("read_and_shuffle_100000_full_key_publication_inputs");
    var output=Path.Combine(scratch,"publication-"+Guid.NewGuid().ToString("N")+".sqlite3");
    try
    {
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=output, Pooling=false }.ToString()))
        {
            db.Open();using(var setup=db.CreateCommand()){setup.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE objects(ekey TEXT PRIMARY KEY,bytes INTEGER NOT NULL); CREATE TABLE receipts(id INTEGER PRIMARY KEY,ekey TEXT NOT NULL,verified INTEGER NOT NULL)";setup.ExecuteNonQuery();}
            Lap("create_durable_publication_catalog");
            void Write(IReadOnlyList<CompactSizeRequest> batch)
            {
                using var tx=db.BeginTransaction();using var obj=db.CreateCommand();obj.Transaction=tx;obj.CommandText="INSERT OR IGNORE INTO objects VALUES($key,$bytes)";var key=obj.Parameters.Add("$key",SqliteType.Text);var bytes=obj.Parameters.Add("$bytes",SqliteType.Integer);obj.Prepare();
                using var receipt=db.CreateCommand();receipt.Transaction=tx;receipt.CommandText="INSERT INTO receipts VALUES($id,$key,1)";var id=receipt.Parameters.Add("$id",SqliteType.Integer);var rk=receipt.Parameters.Add("$key",SqliteType.Text);receipt.Prepare();
                foreach(var row in batch){token.ThrowIfCancellationRequested();key.Value=row.Reference.ToString();bytes.Value=row.Bytes;obj.ExecuteNonQuery();id.Value=row.Id;rk.Value=key.Value;receipt.ExecuteNonQuery();}tx.Commit();
            }
            foreach(var batch in rows.Chunk(1024))Write(batch);Lap("publish_100000_rows_in_1024_row_transactions");
            // Separate IDs so updates/receipts do not collide with bulk rows.
            foreach(var row in rows.Take(1000))Write([row with{Id=row.Id+1_000_000}]);Lap("publish_1000_individual_completion_transactions");
        }
    }
    finally{foreach(var suffix in new[]{"","-wal","-shm"})File.Delete(output+suffix);}
}
else throw new ArgumentException("Unknown benchmark mode.");
var process=Process.GetCurrentProcess();process.Refresh();
Console.WriteLine(JsonSerializer.Serialize(new{mode,sourceRows,matched=check.Count,encodedBytes=check.Bytes,fingerprint=check.Fingerprint,phases,totalSeconds=phases.Values.Sum(),peakWorkingSetBytes=process.PeakWorkingSet64,managedHeapBytes=GC.GetTotalMemory(false),totalAllocatedBytes=GC.GetTotalAllocatedBytes(),runtime=Environment.Version.ToString(),processors=Environment.ProcessorCount,notes="Warm OS cache; one isolated process per strategy. No production publication included in matching timings."},options));

internal sealed record Inputs(int Objects,int Requests,long FirstSizeId,int Width,MetadataReceipt[] Receipts);
internal sealed class Check
{
    internal long Count,Bytes;private ulong fingerprint;
    internal string Fingerprint=>fingerprint.ToString("x16",CultureInfo.InvariantCulture);
    internal void Add(CompactSizeRequest request,CompactEncodedObject value)
    {
        Count++;Bytes=checked(Bytes+value.Bytes);
        // Order-independent workload equivalence check, not a cryptographic publication proof.
        unchecked{ulong v=value.Key.High^System.Numerics.BitOperations.RotateLeft(value.Key.Low,23)^(ulong)request.Id*0x9e3779b97f4a7c15UL^(ulong)value.Bytes;v=(v^(v>>30))*0xbf58476d1ce4e5b9UL;v=(v^(v>>27))*0x94d049bb133111ebUL;fingerprint+=v^(v>>31);}
    }
}
internal sealed class DirectConsumer:IEncodingConsumer,IDownloadSizeConsumer
{
    internal CompactEncodedObject[] Objects=[];internal CompactSizeRequest[] Requests=[];internal int ObjectCount,Width;
    public ValueTask HeaderAsync(EncodingHeader header,CancellationToken token)
    {
        var capacity=checked(header.EncodingPageCount*(header.EncodingPageBytes/25));
        if(capacity*24>128L<<20)throw new InvalidOperationException("EN compact-array budget exceeded");Objects=new CompactEncodedObject[checked((int)capacity)];return ValueTask.CompletedTask;
    }
    public ValueTask ContentAsync(int page,long ordinal,EncodingContentEntry entry,CancellationToken token)=>ValueTask.CompletedTask;
    public ValueTask EncodingAsync(int page,long ordinal,EncodingEncodedEntry entry,CancellationToken token)
    {if(ObjectCount==Objects.Length)throw new InvalidOperationException("EN capacity exceeded");Objects[ObjectCount++]=new(InventoryKey.Parse(entry.EncodingKey),entry.EncodedBytes);return ValueTask.CompletedTask;}
    public ValueTask HeaderAsync(DownloadSizeHeader header,CancellationToken token)
    {if(header.EntryCount*32>128L<<20)throw new InvalidOperationException("DS compact-array budget exceeded");Requests=new CompactSizeRequest[checked((int)header.EntryCount)];Width=header.EncodingKeyBytes;return ValueTask.CompletedTask;}
    public ValueTask TagAsync(int ordinal,DownloadSizeTag tag,CancellationToken token)=>ValueTask.CompletedTask;
    public ValueTask EntryAsync(long ordinal,DownloadSizeEntry entry,CancellationToken token)
    {Requests[checked((int)ordinal)]=new(ordinal+1,InventoryKey.Parse(entry.EncodedKey.Hex),entry.EncodedBytes);return ValueTask.CompletedTask;}
}
