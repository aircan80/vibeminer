using Xunit;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;

namespace VibeMiner.Primitives.Tests;

/// <summary>Shared helpers for the layer 1 object store tests: index snapshots, raw pack edits and the crash-workload checker.</summary>
internal static class ObjectStoreTestKit
{
    public static string StoreDir(TempDirectory t) => Path.Combine(t.Path, ObjectStore.DefaultFolderName);
    public static ObjectStoreOptions Quick => new() { CommitDelay = TimeSpan.FromMilliseconds(5) };
    public static string PackPath(string dir, int seq) => Path.Combine(dir, "packs", $"pack-{seq:D6}.vmp");
    public static string[] PackFiles(string dir) => Directory.GetFiles(Path.Combine(dir, "packs"), "pack-*.vmp").Order(StringComparer.Ordinal).ToArray();

    private static SqliteConnection Index(string dir)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "objects.sqlite3"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>Every object row (without last_read) and every history row, sorted: what Scan must rebuild exactly.</summary>
    public static List<string> Rows(string dir)
    {
        using var c = Index(dir);
        var rows = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT hex(k), pack, offset, length, hex(sha256) FROM objects ORDER BY k";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add($"o {r.GetString(0)} {r.GetInt64(1)} {r.GetInt64(2)} {r.GetInt64(3)} {r.GetString(4)}");
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT hex(record), observed_at, product, region, kind, hex(document), url FROM history ORDER BY record";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add($"h {r.GetString(0)} {r.GetInt64(1)} {r.GetString(2)} {r.GetString(3)} {r.GetInt64(4)} {r.GetString(5)} {r.GetString(6)}");
        }
        return rows;
    }

    public static (int pack, long offset, long length) Row(string dir, ObjectKey key)
    {
        using var c = Index(dir);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT pack, offset, length FROM objects WHERE k = $k";
        var k = new byte[18]; k[0] = (byte)key.Type; k[1] = key.Namespace; key.WriteKey(k.AsSpan(2));
        cmd.Parameters.AddWithValue("$k", k);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), $"{key} has no index row");
        return (r.GetInt32(0), r.GetInt64(1), r.GetInt64(2));
    }

    public static long? LastRead(string dir, ObjectKey key)
    {
        using var c = Index(dir);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT last_read FROM objects WHERE k = $k";
        var k = new byte[18]; k[0] = (byte)key.Type; k[1] = key.Namespace; key.WriteKey(k.AsSpan(2));
        cmd.Parameters.AddWithValue("$k", k);
        var v = cmd.ExecuteScalar();
        return v is DBNull or null ? null : (long)v;
    }

    public static void Flip(string path, long offset, byte mask = 0x10)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        f.Position = offset; var b = f.ReadByte(); f.Position = offset; f.WriteByte((byte)(b ^ mask));
    }

    public static void Write(string path, long offset, ReadOnlySpan<byte> bytes)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        f.Position = offset; f.Write(bytes);
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static Dictionary<string, string> HashFolder(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("-shm", StringComparison.Ordinal) && !f.EndsWith("-wal", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(dir, f), Hash);

    /// <summary>A whole raw entry (header, payload, block table) as format 1 writes it, for crafting pack tails.</summary>
    public static byte[] RawEntry(ObjectKey key, ReadOnlySpan<byte> payload, ulong sequence, bool tombstone = false, byte version = 1)
    {
        var e = new EntryHeader(key, payload.Length, SHA256.HashData(payload), 1_790_000_000_000, ObjectOrigin.Cdn, sequence, tombstone);
        var bytes = new byte[PackFormat.EntryBytes(payload.Length)];
        PackFormat.WriteEntryHeader(bytes, e);
        if (version != 1)
        {
            bytes[4] = version;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), PackFormat.Crc32C(bytes.AsSpan(0, 92)));
        }
        payload.CopyTo(bytes.AsSpan(PackFormat.EntryHeaderBytes));
        PackFormat.BlockTable(payload).CopyTo(bytes.AsSpan(PackFormat.EntryHeaderBytes + payload.Length));
        return bytes;
    }

    public static void Append(string path, ReadOnlySpan<byte> bytes)
    {
        using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        f.Write(bytes);
    }

    public static ulong NextSeq(string dir)
    {
        using var c = Index(dir);
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT next_seq FROM store";
        return (ulong)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Workload lines → what must hold after any crash: every confirmed object present and exact, every confirmed delete absent.</summary>
    public static async Task CheckWorkloadAsync(string dir, IEnumerable<string> lines)
    {
        var committed = new Dictionary<string, (string kind, int seed)>();
        var deleted = new HashSet<string>();
        var uncertain = new HashSet<string>();
        foreach (var line in lines)
        {
            var p = line.Split(' ');
            if (p[0] == "COMMITTED") { committed[p[3]] = (p[1], int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)); deleted.Remove(p[3]); uncertain.Remove(p[3]); }
            else if (p[0] == "PUTTING" && deleted.Contains(p[1])) { deleted.Remove(p[1]); uncertain.Add(p[1]); }  // a Put of a deleted key the kill may have cut short
            else if (p[0] == "DELETED") { deleted.Add(p[1]); uncertain.Remove(p[1]); }
            else if (p[0] == "DELETING") uncertain.Add(p[1]);  // a delete the crash may have cut short: either answer is right
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            // The totals in the store row must have stayed exact through whatever happened: a drift would rebuild at every open.
            Assert.False(store.OpenReport.IndexRebuilt, string.Join("; ", store.OpenReport.Problems));
            Assert.True(store.OpenReport.FullIndexCheck, "a crash must leave the clean-close marker unset");
            foreach (var (hex, (kind, seed)) in committed)
            {
                if (deleted.Contains(hex) || uncertain.Contains(hex)) continue;
                if (kind == "Y")
                {
                    var history = HistoryRecordFormat.Objects(Synthetic.Observation(seed));
                    Assert.Equal(hex, history[1].Key.KeyHex);
                    foreach (var o in history)
                    {
                        var h = await store.ReadAsync(o.Key);
                        Assert.Equal(ReadState.Present, h.State);
                        Assert.Equal(o.Bytes.ToArray(), h.Bytes);
                    }
                    Assert.Contains(await store.ReadHistoryAsync(new HistoryQuery(Limit: 10_000)), e => e.Record == history[1].Key);
                    continue;
                }
                var expected = Workload.Make(kind, seed);
                Assert.Equal(hex, expected.Key.KeyHex);
                var read = await store.ReadAsync(expected.Key);
                Assert.True(read.State == ReadState.Present, $"{kind} {seed} {hex} read {read.State} {read.Problem}");
                Assert.Equal(expected.Bytes.ToArray(), read.Bytes);
            }
            foreach (var hex in deleted)
            {
                var kind = committed.TryGetValue(hex, out var c) ? c.kind : "H";
                var type = kind switch { "G" => ObjectKeyType.Config, "I" => ObjectKeyType.ArchiveIndex, "F" => ObjectKeyType.FileIndex, _ => ObjectKeyType.Encoded };
                Assert.False(await store.HasAsync(ObjectKey.Parse(type, hex)), $"{hex} was confirmed deleted");
            }
            var verify = await store.VerifyAsync(full: true);
            Assert.Equal(0, verify.CorruptCount);
            Assert.Equal(0, verify.LostCount);
        }
        var before = Rows(dir);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.ScanAsync();
        Assert.Equal(before, Rows(dir));
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) Assert.False(store.OpenReport.IndexRebuilt || store.OpenReport.FullIndexCheck, string.Join("; ", store.OpenReport.Problems));
    }
}
