using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;

// Developer-only. All writes are fenced to a marked scratch directory under this checkout's
// ignored .local directory. Retained inputs are read-only. No HTTP or game-install access.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var token = stop.Token;
if (args.Length == 0) throw new ArgumentException("Use prepare, compact, sqlite, reopen-compact, reopen-sqlite or check; see README.md.");
var mode = args[0];
if (args.Length < (mode == "prepare" ? 4 : 2)) throw new ArgumentException("Missing benchmark arguments; see README.md.");
var scratch = Scratch(args[mode == "prepare" ? 3 : 1]);
if (mode == "prepare")
{
    if (args.Length != 4) throw new ArgumentException("prepare <app-owned-source-directory> <DL-full-EKey> <scratch-directory>");
    if (File.Exists(Path.Combine(scratch, "inputs.json"))) throw new IOException("Prepared inputs already exist; use a new scratch directory.");
    var directory = Path.GetFullPath(args[1]);
    var key = args[2];
    if (key.Length != 32 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Choose one exact full DL EKey.");
    MetadataReceipt receipt;
    using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "discovery-v1.sqlite3"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
    {
        database.Open();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT receipt FROM metadata_objects WHERE ekey=$e";
        query.Parameters.AddWithValue("$e", key.ToLowerInvariant());
        receipt = JsonSerializer.Deserialize<MetadataReceipt>((string?)query.ExecuteScalar()
            ?? throw new InvalidOperationException("The exact retained source receipt is absent."), json)!;
    }
    if (receipt.Role != "download") throw new InvalidOperationException("The chosen receipt is not a DL source.");
    var encoded = Number(receipt.EncodedBytes); var decoded = Number(receipt.DecodedBytes);
    var timer = Stopwatch.StartNew();
    await using var source = ReadFile(Path.Combine(directory, "objects-v1", receipt.Sha256[..2], receipt.Sha256[2..]));
    if (source.Length != encoded || await Hash(source, token) != receipt.Sha256) throw new InvalidOperationException("Retained encoded SHA/length mismatch.");
    source.Position = 0;
    var targetPath = Path.Combine(scratch, "decoded-dl.bin");
    await using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
        FileOptions.Asynchronous | FileOptions.SequentialScan))
    {
        await BlteDecoder.DecodeAsync(source, output, receipt.EncodingKey, receipt.ContentKey, encoded, decoded, token);
        await output.FlushAsync(token);
    }
    await using var verifiedDecoded = ReadFile(targetPath);
    if (verifiedDecoded.Length != decoded) throw new InvalidOperationException("Decoded length mismatch.");
    var decodedSha = await Hash(verifiedDecoded, token);
    await File.WriteAllTextAsync(Path.Combine(scratch, "inputs.json"), JsonSerializer.Serialize(new Inputs(receipt, decodedSha), json), token);
    Console.WriteLine(JsonSerializer.Serialize(new { mode, encodedBytes = encoded, decodedBytes = decoded,
        verifyAndDecodeSeconds = timer.Elapsed.TotalSeconds, sourceEncodingKey = receipt.EncodingKey, decodedSha256 = decodedSha }, json));
    return;
}
if (mode == "check")
{
    var valid = Fixture((2, 5), (1, 7), (2, 5));
    var receipt = AuthoredReceipt(valid);
    var compactPath = NewRun(scratch, "authored-compact");
    var sqlitePath = NewRun(scratch, "authored-sqlite");
    var compact = await BuildCompactBytes(compactPath, valid, receipt, token);
    var sqlite = await BuildSqliteBytes(sqlitePath, valid, receipt, token);
    if (compact.Proof != sqlite.Proof || compact.Proof.Count != 2 || compact.Proof.Total != 12)
        throw new InvalidOperationException("Authored duplicate equivalence failed.");
    var conflict = Fixture((2, 5), (1, 7), (2, 6));
    var rejected = 0;
    foreach (var implementation in new[] { "compact", "sqlite" })
    {
        var path = NewRun(scratch, "authored-conflict-" + implementation);
        try
        {
            if (implementation == "compact") await BuildCompactBytes(path, conflict, AuthoredReceipt(conflict), token);
            else await BuildSqliteBytes(path, conflict, AuthoredReceipt(conflict), token);
        }
        catch (DiscoveryException error) when (error.Code == "DOWNLOAD_SIZE_CONFLICT") { rejected++; }
    }
    if (rejected != 2) throw new InvalidOperationException("Both implementations must reject conflicting duplicate sizes.");
    Console.WriteLine(JsonSerializer.Serialize(new { mode, duplicateProof = compact.Proof, conflictingDuplicatesRejected = rejected }, json));
    return;
}
if (args.Length != 3) throw new ArgumentException("<compact|sqlite|reopen-compact|reopen-sqlite> <scratch-directory> <run-name>");
var name = args[2];
if (mode is "compact" or "sqlite")
{
    var inputs = JsonSerializer.Deserialize<Inputs>(await File.ReadAllTextAsync(Path.Combine(scratch, "inputs.json"), token), json)!;
    var output = NewRun(scratch, name);
    var phases = new Dictionary<string, double>();
    var clock = Stopwatch.StartNew();
    await using var source = ReadFile(Path.Combine(scratch, "decoded-dl.bin"));
    if (source.Length != Number(inputs.Receipt.DecodedBytes) || await Hash(source, token) != inputs.DecodedSha256)
        throw new InvalidOperationException("Prepared decoded source SHA/length mismatch.");
    phases["verify_prepared_decoded_sha256"] = clock.Elapsed.TotalSeconds;
    source.Position = 0; clock.Restart();
    var measured = mode == "compact"
        ? await BuildCompact(output, source, inputs.Receipt, token)
        : await BuildSqlite(output, source, inputs.Receipt, token);
    foreach (var phase in measured.Phases) phases[phase.Key] = phase.Value;
    var manifest = new RunManifest(mode, measured.Artifact, measured.Proof, measured.OutputSha256, measured.OutputBytes);
    await File.WriteAllTextAsync(Path.Combine(output, "run.json"), JsonSerializer.Serialize(manifest, json), token);
    Report(mode, phases, measured, inputs.Receipt.EncodingKey);
    return;
}
if (mode is "reopen-compact" or "reopen-sqlite")
{
    ValidateName(name);
    var output = Path.Combine(scratch, "runs", name);
    var manifest = JsonSerializer.Deserialize<RunManifest>(await File.ReadAllTextAsync(Path.Combine(output, "run.json"), token), json)!;
    if (mode != "reopen-" + manifest.Mode) throw new ArgumentException("Reopen mode does not match the chosen run.");
    var clock = Stopwatch.StartNew(); var phases = new Dictionary<string, double>(); Proof proof;
    if (manifest.Mode == "compact")
    {
        await using var objects = new VerifiedObjectStore(output);
        var ledger = new DownloadLedger(objects);
        await ledger.ValidateAsync(manifest.Artifact!, token);
        phases["production_full_sha_header_and_record_validation"] = clock.Elapsed.TotalSeconds;
        clock.Restart();
        proof = await CompactProof(output, manifest.Artifact!, token);
    }
    else
    {
        var file = Path.Combine(output, "objects.sqlite3");
        await using (var stream = ReadFile(file))
            if (stream.Length != manifest.OutputBytes || await Hash(stream, token) != manifest.OutputSha256)
                throw new InvalidOperationException("SQLite artifact SHA/length mismatch.");
        phases["full_sha256_validation"] = clock.Elapsed.TotalSeconds; clock.Restart();
        using var database = OpenSqlite(file, readOnly: true);
        proof = SqliteProof(database, token);
    }
    phases["canonical_record_proof"] = clock.Elapsed.TotalSeconds;
    if (proof != manifest.Proof) throw new InvalidOperationException("Reopen canonical proof differs from publication.");
    Report(mode, phases, new(manifest.Artifact, proof, manifest.OutputSha256, manifest.OutputBytes, []), null);
    return;
}
throw new ArgumentException("Unknown benchmark mode.");

void Report(string strategy, Dictionary<string, double> phases, Measurement result, string? key)
{
    var process = Process.GetCurrentProcess(); process.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new { mode = strategy, sourceEncodingKey = key, result.Proof,
        phases, totalMeasuredSeconds = phases.Values.Sum(), outputBytes = result.OutputBytes,
        peakWorkingSetBytes = process.PeakWorkingSet64, managedHeapBytes = GC.GetTotalMemory(false),
        totalManagedAllocatedBytes = GC.GetTotalAllocatedBytes(), result.OutputSha256,
        notes = "Retained decoded source; fresh private outputs, warm OS cache is not evicted. No network/config/source acquisition. SQLite is BLOB16 PK WITHOUT ROWID, WAL+FULL, one private publication transaction then checkpoint. Compact uses production external sort/CAS publication plus a measured final-file Flush(true), without directory fsync. No power-loss equivalence claim; canonical proof is separate verification cost. Output bytes exclude input, temporary files, descriptors and filesystem allocation overhead." }, json));
}

static async Task<Measurement> BuildCompactBytes(string path, byte[] source, MetadataReceipt receipt, CancellationToken token)
{ using var input = new MemoryStream(source); return await BuildCompact(path, input, receipt, token); }
static async Task<Measurement> BuildCompact(string path, Stream source, MetadataReceipt receipt, CancellationToken token)
{
    var phases = new Dictionary<string, double>(); var clock = Stopwatch.StartNew();
    await using var objects = new VerifiedObjectStore(path);
    var ledger = new DownloadLedger(objects);
    var artifact = await ledger.BuildAsync(source, receipt, _ => Task.CompletedTask, token);
    phases["parse_md5_external_sort_dedup_hash_and_publish"] = clock.Elapsed.TotalSeconds;
    clock.Restart();
    var file = Path.Combine(path, "objects-v1", artifact.Sha256[..2], artifact.Sha256[2..]);
    // Standalone benchmark barrier, not production async orchestration. Directory durability
    // after rename is explicitly unqualified; this is not a power-loss test.
    using (var flush = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) flush.Flush(flushToDisk: true);
    phases["additional_final_file_fsync"] = clock.Elapsed.TotalSeconds; clock.Restart();
    var proof = await CompactProof(path, artifact, token);
    phases["canonical_record_proof"] = clock.Elapsed.TotalSeconds;
    if (proof.Count != artifact.ObjectCount || proof.Total != artifact.TotalEncodedBytes) throw new InvalidOperationException("Compact summary mismatch.");
    return new(artifact, proof, artifact.Sha256, artifact.ByteLength, phases);
}
static async Task<Measurement> BuildSqliteBytes(string path, byte[] source, MetadataReceipt receipt, CancellationToken token)
{ using var input = new MemoryStream(source); return await BuildSqlite(path, input, receipt, token); }
static async Task<Measurement> BuildSqlite(string path, Stream source, MetadataReceipt receipt, CancellationToken token)
{
    var phases = new Dictionary<string, double>(); var clock = Stopwatch.StartNew();
    var file = Path.Combine(path, "objects.sqlite3");
    Proof proof;
    using (var database = OpenSqlite(file, readOnly: false))
    {
        using (var setup = database.CreateCommand())
        { setup.CommandText = "CREATE TABLE objects(ekey BLOB PRIMARY KEY CHECK(length(ekey)=16), bytes INTEGER NOT NULL CHECK(bytes>=0 AND bytes<=1099511627775)) WITHOUT ROWID"; setup.ExecuteNonQuery(); }
        using (var transaction = database.BeginTransaction())
        {
            using var insert = database.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO objects(ekey,bytes) VALUES($k,$n) ON CONFLICT(ekey) DO UPDATE SET bytes=objects.bytes WHERE objects.bytes=excluded.bytes";
            insert.Parameters.Add("$k", SqliteType.Blob); insert.Parameters.Add("$n", SqliteType.Integer); insert.Prepare();
            var declared = Number(receipt.DecodedBytes);
            using var checkedInput = new LedgerSourceStream(source, declared);
            var result = await DownloadInventoryReader.ReadAsync(checkedInput, new(MaxDecodedBytes: declared), new SqliteConsumer(insert), token);
            if (result.ConsumedBytes != declared || checkedInput.ConsumedBytes != declared || checkedInput.ContentKey() != receipt.ContentKey)
                throw new DiscoveryException("DOWNLOAD_SOURCE_INTEGRITY", "Decoded source differs from its verified receipt.");
            token.ThrowIfCancellationRequested(); transaction.Commit();
        }
        using (var summary = database.CreateCommand())
        { summary.CommandText = "SELECT count(*),coalesce(sum(bytes),0) FROM objects"; using var rows = summary.ExecuteReader(); rows.Read(); _ = rows.GetInt64(0); _ = rows.GetInt64(1); }
        using (var checkpoint = database.CreateCommand())
        { checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; using var rows = checkpoint.ExecuteReader(); rows.Read(); if (rows.GetInt32(0) != 0) throw new InvalidOperationException("SQLite checkpoint remained busy."); }
        phases["parse_md5_btree_dedup_full_commit_summary_and_checkpoint"] = clock.Elapsed.TotalSeconds;
        clock.Restart(); proof = SqliteProof(database, token);
        phases["canonical_record_proof"] = clock.Elapsed.TotalSeconds;
    }
    clock.Restart();
    await using var output = ReadFile(file); var sha = await Hash(output, token);
    phases["whole_database_publication_sha256"] = clock.Elapsed.TotalSeconds;
    return new(null, proof, sha, output.Length, phases);
}
static SqliteConnection OpenSqlite(string file, bool readOnly)
{
    var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = file, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    database.Open();
    using var settings = database.CreateCommand();
    settings.CommandText = readOnly ? "PRAGMA query_only=ON; PRAGMA cache_size=-32768; PRAGMA mmap_size=0" :
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-32768; PRAGMA mmap_size=0; PRAGMA temp_store=FILE; PRAGMA max_page_count=262144";
    settings.ExecuteNonQuery(); return database;
}
static async Task<Proof> CompactProof(string path, DownloadLedgerArtifact artifact, CancellationToken token)
{
    await using var stream = ReadFile(Path.Combine(path, "objects-v1", artifact.Sha256[..2], artifact.Sha256[2..]));
    if (stream.Length != artifact.ByteLength) throw new InvalidOperationException("Compact artifact length mismatch.");
    stream.Position = DownloadLedger.HeaderBytes;
    using var proof = new ProofBuilder();
    var bytes = new byte[24 * 2730]; long remaining = artifact.ObjectCount;
    while (remaining > 0)
    {
        var count = (int)Math.Min(2730, remaining); await stream.ReadExactlyAsync(bytes.AsMemory(0, count * 24), token);
        for (var index = 0; index < count; index++) proof.Add(bytes.AsSpan(index * 24, 24));
        remaining -= count;
    }
    return proof.Finish();
}
static Proof SqliteProof(SqliteConnection database, CancellationToken token)
{
    using var query = database.CreateCommand(); query.CommandText = "SELECT ekey,bytes FROM objects ORDER BY ekey";
    using var rows = query.ExecuteReader(); using var proof = new ProofBuilder();
    var buffer = new byte[24];
    while (rows.Read())
    {
        token.ThrowIfCancellationRequested(); var key = (byte[])rows[0];
        if (key.Length != 16) throw new InvalidOperationException("Invalid SQLite key.");
        key.CopyTo(buffer, 0); BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(16), rows.GetInt64(1)); proof.Add(buffer);
    }
    return proof.Finish();
}
static string Scratch(string value)
{
    var path = Path.GetFullPath(value); var allowed = Path.Combine(Directory.GetCurrentDirectory(), ".local") + Path.DirectorySeparatorChar;
    if (!path.StartsWith(allowed, StringComparison.Ordinal)) throw new ArgumentException("Scratch must be below this checkout's .local directory; invoke from repository root.");
    RefuseSymbolicAncestors(path);
    Directory.CreateDirectory(path);
    var marker = Path.Combine(path, ".download-ledger-benchmark");
    if (!File.Exists(marker)) File.WriteAllText(marker, "Developer benchmark scratch; no application admission.\n");
    return path;
}
static void ValidateName(string value)
{ if (value.Length is < 1 or > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Choose a plain unique run name."); }
static string NewRun(string scratch, string name)
{
    ValidateName(name); var path = Path.Combine(scratch, "runs", name);
    RefuseSymbolicAncestors(path);
    if (Directory.Exists(path)) throw new IOException("Output run already exists; choose a new name.");
    Directory.CreateDirectory(path); return path;
}
static void RefuseSymbolicAncestors(string path)
{
    for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        if (new DirectoryInfo(ancestor).LinkTarget is not null) throw new ArgumentException("Benchmark output ancestors must not be symbolic links.");
}
static FileStream ReadFile(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
static async Task<string> Hash(Stream stream, CancellationToken token) => Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
static long Number(string value) => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
static byte[] Fixture(params (uint Key, long Bytes)[] rows)
{
    using var output = new MemoryStream(); output.Write("DL"u8); output.Write([1, 16, 0]);
    var count = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(count, (uint)rows.Length); output.Write(count); output.Write([0, 0]);
    foreach (var row in rows)
    { var key = new byte[16]; BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(12), row.Key); output.Write(key); var size = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(size, row.Bytes); output.Write(size.AsSpan(3)); output.WriteByte(0); }
    return output.ToArray();
}
static MetadataReceipt AuthoredReceipt(byte[] bytes) => new("download", Convert.ToHexStringLower(MD5.HashData(bytes)), new string('1', 32),
    new string('2', 64), "9", bytes.Length.ToString(CultureInfo.InvariantCulture), "https://authored.invalid/source", "authored", "authored", "download-v1");

sealed record Inputs(MetadataReceipt Receipt, string DecodedSha256);
sealed record RunManifest(string Mode, DownloadLedgerArtifact? Artifact, Proof Proof, string OutputSha256, long OutputBytes);
sealed record Measurement(DownloadLedgerArtifact? Artifact, Proof Proof, string OutputSha256, long OutputBytes, Dictionary<string, double> Phases);
sealed record Proof(long Count, long Total, string SortedRecordSha256);
sealed class ProofBuilder : IDisposable
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] previous = new byte[16]; private bool hasPrevious; private long count, total;
    internal void Add(ReadOnlySpan<byte> row)
    {
        if (row.Length != 24 || hasPrevious && previous.AsSpan().SequenceCompareTo(row[..16]) >= 0) throw new InvalidOperationException("Canonical keys are not unique and ordered.");
        var bytes = BinaryPrimitives.ReadInt64BigEndian(row[16..]);
        if (bytes < 0) throw new InvalidOperationException("Negative encoded length.");
        total = checked(total + bytes); count++; row[..16].CopyTo(previous); hasPrevious = true; hash.AppendData(row);
    }
    internal Proof Finish() => new(count, total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    public void Dispose() => hash.Dispose();
}
sealed class SqliteConsumer(SqliteCommand insert) : IDownloadConsumer
{
    public ValueTask EntryAsync(long ordinal, DownloadEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); insert.Parameters[0].Value = Convert.FromHexString(entry.EncodingKey); insert.Parameters[1].Value = entry.EncodedBytes;
        if (insert.ExecuteNonQuery() != 1) throw new DiscoveryException("DOWNLOAD_SIZE_CONFLICT", "One exact object has conflicting source lengths.");
        return ValueTask.CompletedTask;
    }
    public ValueTask TagAsync(int ordinal, DownloadTag tag, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
}
