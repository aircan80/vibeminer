using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tables;
using VibeMiner.Generated.Testing;
using VibeMiner.ObjectAccess.Testing;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// One table of a synthetic build for the real <see cref="TablesService"/>. <see cref="Name"/> null is a DB2 the manifest doesn't
/// name. <see cref="Dbd"/> is its bundled definition text; null with a name lists the table in the manifest with no bundled file.
/// <see cref="File"/> null means the build doesn't list it.
/// </summary>
public sealed record SyntheticTable(uint FileDataId, string? Name, string? Dbd, FakeFile? File);

/// <summary>A real tables service over the layer 4 and generated store fakes, with call counters. Synthetic data only.</summary>
public sealed record SyntheticWorld(TablesService Service, InMemoryFileSystem Files, CountingFileSystem Counted, CountingGeneratedTables Generated,
    InMemoryGeneratedStore Store);

/// <summary>Synthetic WDC5 files, WoWDBDefs-style definitions and an authored manifest. No game content.</summary>
public static class SyntheticTables
{
    public const string Version = "1.2.3.4";
    public const string Layout = "01020304";

    /// <summary>
    /// The world over <paramref name="tables"/>. Every listed file has a listfile name (<c>dbfilesclient/{name}.db2</c>, unnamed
    /// ones by FileDataID) unless <paramref name="names"/> is false. <paramref name="fetched"/> turns fetching on over that
    /// object source. <paramref name="options"/> adjusts the service's options last. <paramref name="wrap"/> puts another layer 4 between
    /// the fake and the counter.
    /// </summary>
    public static async Task<SyntheticWorld> CreateAsync(BuildRef build, IReadOnlyList<SyntheticTable> tables, Db2Limits? limits = null,
        IReadOnlyList<string>? locales = null, Func<BuildRef, ITactKeyStatus>? keys = null, int maxScanRows = TableQueries.DefaultMaxScanRows,
        bool names = true, InMemoryObjectSource? fetched = null, Func<TablesOptions, TablesOptions>? options = null,
        Func<IFileSystem, IFileSystem>? wrap = null)
    {
        var files = new InMemoryFileSystem().Add(build, locales ?? ["enUS", "deDE"], tables.Where(t => t.File is not null).Select(t => t.File!));
        if (names)
            files.Names.Use(tables.Where(t => t.File is not null).ToDictionary(t => t.FileDataId,
                t => $"dbfilesclient/{(t.Name ?? t.FileDataId.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant()}.db2"));
        var counted = new CountingFileSystem(wrap is null ? files : wrap(files));
        var store = new InMemoryGeneratedStore();
        var generated = new CountingGeneratedTables(new GeneratedTables(store));
        var provider = await ProviderAsync(tables.Where(t => t.Name is not null).ToArray());
        var definitions = fetched is null ? new TableDefinitions(provider, null, null)
            : new TableDefinitions(provider, new FetchedDefinitions(fetched), new FetchOn());
        var chosen = new TablesOptions((asked, _) => Task.FromResult<string?>(asked == build ? Version : null))
        {
            Limits = limits ?? new(), MaxScanRows = maxScanRows,
        };
        if (keys is not null) chosen = chosen with { Keys = keys };
        if (options is not null) chosen = options(chosen);
        return new(new TablesService(counted, definitions, generated, chosen), files, counted, generated, store);
    }

    private sealed class FetchOn : IDefinitionFetchSwitch
    {
        public Task<bool> FetchEnabledAsync(CancellationToken token) => Task.FromResult(true);
    }

    /// <summary>The scenario table as a synthetic one: its WDC5 bytes, a keyed section as a hole named by its key.</summary>
    public static SyntheticTable From(ScenarioTable table)
    {
        var dbd = table.Name is null ? null : Dbd(table.Columns);
        if (!table.InBuild) return new(table.FileDataId, table.Name, dbd, null);
        var (bytes, hole) = Wdc5(table.Columns, table.Rows, table.Keyed, table.FileDataId);
        return new(table.FileDataId, table.Name, dbd, new FakeFile(table.FileDataId, Content: bytes,
            Storage: table.Stored ? FileStorage.Stored : FileStorage.NotStored, Holes: hole is null ? null : [hole]));
    }

    /// <summary>A definition with one block for <paramref name="layout"/> listing <paramref name="build"/>; the ID column is non-inline.</summary>
    public static string Dbd(IReadOnlyList<ScenarioColumn> columns, string build = Version, string layout = Layout)
    {
        var text = new StringBuilder("COLUMNS\n");
        foreach (var c in columns)
        {
            var kind = c.Kind switch { ColumnKind.Int => "int", ColumnKind.Float => "float", ColumnKind.LocString => "locstring", _ => "string" };
            var reference = c.ForeignKey is { } fk ? $"<{fk.Table}::{fk.Column}>" : "";
            text.Append(CultureInfo.InvariantCulture, $"{kind}{reference} {c.Name}\n");
        }
        text.Append(CultureInfo.InvariantCulture, $"\nLAYOUT {layout}\nBUILD {build}\n");
        foreach (var c in columns)
        {
            if (c.IsId) { text.Append(CultureInfo.InvariantCulture, $"$noninline,id${c.Name}<u32>\n"); continue; }
            var size = c.Kind == ColumnKind.Int ? $"<{(c.Unsigned == true ? "u" : "")}{c.Bits ?? 32}>" : "";
            var array = c.ArrayLength > 1 ? $"[{c.ArrayLength}]" : "";
            text.Append(CultureInfo.InvariantCulture, $"{c.Name}{size}{array}\n");
        }
        return text.ToString();
    }

    private sealed record Section(byte[] Records, uint[] Ids, byte[] Strings, ulong Key, uint[]? EncryptedIds, int[]? Sizes = null);

    /// <summary>Bytes no strict UTF-8 decoder admits, for a bad-text cell.</summary>
    private static readonly byte[] BadUtf8 = [0xFF, 0xFE];

    /// <summary>
    /// A WDC5 file, every field stored plainly (storage kind 0) and the ID in a list per section. The rows go in one clear section;
    /// a keyed section follows with zeroed records, and the hole covers its body. Fixed records (flags 4) unless a row has
    /// <see cref="ScenarioRow.BadText"/>: invalid text is admitted only inline, so that table is sparse (flags 5, an offset map,
    /// strings in the records) and has no keyed section.
    /// </summary>
    public static (byte[] Bytes, FileHole? Hole) Wdc5(IReadOnlyList<ScenarioColumn> columns, IReadOnlyList<ScenarioRow> rows, ScenarioKeyedSection? keyed,
        uint tableHash = 0x11223344)
    {
        var fields = columns.Where(c => !c.IsId).ToArray();
        var bits = fields.Select(c => c.Kind == ColumnKind.Int ? c.Bits ?? 32 : 32).ToArray();
        var offsets = new int[fields.Length];
        var recordSize = 0;
        for (var i = 0; i < fields.Length; i++) { offsets[i] = recordSize; recordSize += bits[i] / 8 * fields[i].ArrayLength; }
        var declared = rows.Count + (keyed?.Ids.Count ?? 0);
        var sparse = rows.Any(row => row.BadText is { Count: > 0 });
        if (sparse && keyed is not null) throw new NotSupportedException("A sparse synthetic table has no keyed section.");

        var sections = new List<Section>();
        if (sparse) sections.Add(SparseSection(fields, bits, rows));
        else
        {
            var strings = new MemoryStream();
            strings.WriteByte(0);
            var records = new byte[rows.Count * recordSize];
            for (var r = 0; r < rows.Count; r++)
            {
                var values = rows[r].Values.Skip(1).ToArray();
                var v = 0;
                for (var f = 0; f < fields.Length; f++)
                    for (var e = 0; e < fields[f].ArrayLength; e++, v++)
                    {
                        var at = r * recordSize + offsets[f] + e * bits[f] / 8;
                        if (fields[f].Kind is ColumnKind.String or ColumnKind.LocString)
                        {
                            if (values[v].Length == 0) continue;
                            var global = strings.Length;
                            strings.Write(Encoding.UTF8.GetBytes(values[v])); strings.WriteByte(0);
                            // The reader adds the element's offset (field offset + 4 per element, D295) and (row - declared rows)
                            // records to the stored displacement.
                            var raw = global - (offsets[f] + e * 4) - (long)(r - declared) * recordSize;
                            BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(at), checked((uint)raw));
                        }
                        else Number(fields[f], bits[f], values[v]).CopyTo(records.AsSpan(at));
                    }
            }
            if (rows.Count > 0 || keyed is null)
                sections.Add(new(records, [.. rows.Select(row => row.Id)], rows.Count > 0 && strings.Length > 1 ? strings.ToArray() : [], 0, null));
        }
        if (keyed is not null)
            sections.Add(new(new byte[keyed.Ids.Count * recordSize], [.. keyed.Ids], [], ulong.Parse(keyed.KeyName, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                [.. keyed.Ids]));

        var prefix = 204 + sections.Count * 40 + fields.Length * 28 + sections.Where(s => s.Key != 0).Sum(s => 4 + s.EncryptedIds!.Length * 4);
        var header = new byte[204];
        "WDC5"u8.CopyTo(header); Put(header, 4, 5); Encoding.UTF8.GetBytes("authored").CopyTo(header, 8);
        Put(header, 136, (uint)declared); Put(header, 140, (uint)fields.Length); Put(header, 144, (uint)recordSize);
        Put(header, 148, (uint)sections.Sum(s => s.Strings.Length)); Put(header, 152, tableHash); Put(header, 156, 0x01020304);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(172), (ushort)(sparse ? 5 : 4)); Put(header, 176, (uint)fields.Length); Put(header, 188, (uint)fields.Length * 24);
        Put(header, 200, (uint)sections.Count);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(header);
        var offset = prefix;
        var starts = new List<int>();
        foreach (var s in sections)
        {
            starts.Add(offset);
            var map = s.Sizes is null ? 0u : (uint)s.Ids.Length;
            writer.Write(s.Key); writer.Write((uint)offset); writer.Write((uint)s.Ids.Length); writer.Write((uint)s.Strings.Length);
            writer.Write(s.Sizes is null ? 0u : (uint)(offset + s.Records.Length)); writer.Write((uint)s.Ids.Length * 4); writer.Write(0u); writer.Write(map); writer.Write(0u);
            offset += s.Records.Length + s.Strings.Length + s.Ids.Length * 4 + (int)map * 10;
        }
        for (var i = 0; i < fields.Length; i++) { writer.Write((short)(32 - bits[i])); writer.Write((ushort)offsets[i]); }
        for (var i = 0; i < fields.Length; i++)
        {
            writer.Write((ushort)(offsets[i] * 8)); writer.Write((ushort)(bits[i] * fields[i].ArrayLength));
            writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        }
        foreach (var s in sections.Where(s => s.Key != 0)) { writer.Write((uint)s.EncryptedIds!.Length); foreach (var id in s.EncryptedIds) writer.Write(id); }
        for (var n = 0; n < sections.Count; n++)
        {
            var s = sections[n];
            writer.Write(s.Records); writer.Write(s.Strings); foreach (var id in s.Ids) writer.Write(id);
            if (s.Sizes is null) continue;
            // The offset map (absolute offset and size per record), then its ID list.
            var at = (uint)starts[n];
            foreach (var size in s.Sizes) { writer.Write(at); writer.Write((ushort)size); at += (uint)size; }
            foreach (var id in s.Ids) writer.Write(id);
        }
        writer.Flush();
        FileHole? hole = null;
        if (keyed is not null)
        {
            var last = sections[^1];
            hole = new FileHole(starts[^1], last.Records.Length + last.Ids.Length * 4, keyed.KeyName);
        }
        return (output.ToArray(), hole);
    }

    /// <summary>Sparse records: each field in order, strings inline and NUL-terminated, padded to four bytes (eight with a 64-bit field).</summary>
    private static Section SparseSection(ScenarioColumn[] fields, int[] bits, IReadOnlyList<ScenarioRow> rows)
    {
        var alignment = bits.Contains(64) ? 8 : 4;
        var records = new MemoryStream();
        var sizes = new int[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var start = records.Length;
            var v = 0;
            for (var f = 0; f < fields.Length; f++)
                for (var e = 0; e < fields[f].ArrayLength; e++, v++)
                {
                    // Flattened indexes count the ID column first.
                    if (fields[f].Kind is not (ColumnKind.String or ColumnKind.LocString)) { records.Write(Number(fields[f], bits[f], rows[r].Values[v + 1])); continue; }
                    records.Write(rows[r].BadText?.Contains(v + 1) == true ? BadUtf8 : Encoding.UTF8.GetBytes(rows[r].Values[v + 1]));
                    records.WriteByte(0);
                }
            while ((records.Length - start) % alignment != 0) records.WriteByte(0);
            sizes[r] = (int)(records.Length - start);
        }
        return new(records.ToArray(), [.. rows.Select(row => row.Id)], [], 0, null, sizes);
    }

    /// <summary>An integer cell's low bits, little-endian, or a float32 cell's bits.</summary>
    private static byte[] Number(ScenarioColumn column, int bits, string value)
    {
        var bytes = new byte[bits / 8];
        if (column.Kind == ColumnKind.Float)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)));
            return bytes;
        }
        var raw = (ulong)(BigInteger.Parse(value, CultureInfo.InvariantCulture) & ((BigInteger.One << bits) - 1));
        for (var b = 0; b < bytes.Length; b++) bytes[b] = (byte)(raw >> (8 * b));
        return bytes;
    }

    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);

    /// <summary>An authored provider: table ids are the FileDataID as 8 lower hex; tables with a definition text are bundled.</summary>
    private static Task<BundledDefinitions> ProviderAsync(IReadOnlyList<SyntheticTable> tables)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(tables.Select(t => new Dictionary<string, object>
        {
            ["tableName"] = t.Name!, ["tableHash"] = t.FileDataId.ToString("x8", CultureInfo.InvariantCulture), ["db2FileDataID"] = t.FileDataId,
        }));
        var bundled = tables.Where(t => t.Dbd is not null).ToArray();
        var decoded = bundled.SelectMany(t => Encoding.UTF8.GetBytes(t.Dbd!)).ToArray();
        var stream = new byte[BrotliEncoder.GetMaxCompressedLength(Math.Max(decoded.Length, 1))];
        Assert.True(BrotliEncoder.TryCompress(decoded, stream, out var written, 11, 24)); stream = stream[..written];
        var sources = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = "WoWDBDefs contributors", revision = BundledDefinitions.Revision, definitionLicense = "CC-BY-SA-4.0",
            sourceBase = BundledDefinitions.Origin, resources = Array.Empty<object>(),
            definitions = new
            {
                path = "definitions.br", compression = "brotli", sha256 = Sha(stream), byteLength = stream.Length, decodedLength = decoded.Length,
                files = bundled.Select(t => new { path = $"definitions/{t.Name}.dbd", sha256 = Sha(Encoding.UTF8.GetBytes(t.Dbd!)), byteLength = Encoding.UTF8.GetByteCount(t.Dbd!) }),
            },
        });
        return BundledDefinitions.CreateAsync(manifest, sources, () => new MemoryStream(stream, writable: false), new(), default);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// Layer 4 with its byte reads counted (whole reads, range reads and stream opens), the filters it was listed with, the streams
/// still open, and scripted layer 2 failures: <see cref="StatFailure"/> and <see cref="ListFailure"/> are thrown by stats and lists.
/// </summary>
public sealed class CountingFileSystem(IFileSystem inner) : IFileSystem
{
    private int reads, streams;
    private readonly List<FileFilter> lists = [];
    public int Reads => Volatile.Read(ref reads);
    /// <summary>Streams opened and not yet disposed.</summary>
    public int OpenStreams => Volatile.Read(ref streams);
    public IReadOnlyList<FileFilter> Lists { get { lock (lists) return [.. lists]; } }
    public Exception? StatFailure { get; set; }
    public Exception? ListFailure { get; set; }
    public VibeMiner.Files.Names.IFileNames Names => inner.Names;

    public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        var opened = await inner.ForAsync(build, locale, cancellationToken);
        return opened.Files is { } files ? opened with { Files = new Handle(this, files) } : opened;
    }

    private sealed class Handle(CountingFileSystem owner, IBuildFiles inner) : IBuildFiles
    {
        public BuildRef Build => inner.Build;
        public string Locale => inner.Locale;
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public async IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (owner.lists) owner.lists.Add(filter);
            if (owner.ListFailure is { } failure) throw failure;
            await foreach (var file in inner.ListAsync(filter, cancellationToken)) yield return file;
        }

        public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) =>
            owner.StatFailure is { } failure ? Task.FromException<FileStat>(failure) : inner.StatAsync(file, cancellationToken);
        public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
        public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
            inner.EnsureAsync(selection, priority, progress, cancellationToken);
        public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) { Interlocked.Increment(ref owner.reads); return inner.GetAsync(file, cancellationToken); }
        public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken)
        { Interlocked.Increment(ref owner.reads); return inner.ReadRangeAsync(file, offset, length, cancellationToken); }

        public async Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.reads);
            var opened = await inner.OpenReadAsync(file, cancellationToken);
            if (opened.Stream is not { } stream) return opened;
            Interlocked.Increment(ref owner.streams);
            return opened with { Stream = new CountedStream(stream, () => Interlocked.Decrement(ref owner.streams)) };
        }
    }

    /// <summary>A stream that reports its first dispose.</summary>
    private sealed class CountedStream(FileReadStream inner, Action disposed) : FileReadStream
    {
        private int done;
        public override IReadOnlyList<FileHole> Holes => inner.Holes;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref done, 1) == 0) { inner.Dispose(); disposed(); }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref done, 1) == 0) { await inner.DisposeAsync(); disposed(); }
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>
/// The generated store with its decodes (build callbacks run) counted, with hooks. <see cref="AfterDecode"/>, when set, runs after a decode
/// has made its content and before the store gets it, without the build's token (as a decode still busy would).
/// </summary>
public sealed class CountingGeneratedTables(IGeneratedTables inner) : IGeneratedTables
{
    private int decodes;
    public int Decodes => Volatile.Read(ref decodes);
    public Func<Task>? AfterDecode { get; set; }
    /// <summary>Runs as a decode starts, before it reads the file, with the table it builds; a gate here holds the build.</summary>
    public Func<GeneratedTableSource, Task>? Decoding { get; set; }
    /// <summary>Runs as a caller asks for a table to be opened or built, before it looks.</summary>
    public Action<GeneratedTableSource>? Building { get; set; }
    /// <summary>Runs once a stored table opened or was built, before the caller reads it.</summary>
    public Func<Task>? AfterOpen { get; set; }

    public async Task<GeneratedTable?> OpenAsync(GeneratedTableSource source, ITactKeyStatus keys, CancellationToken cancellationToken = default)
    {
        var table = await inner.OpenAsync(source, keys, cancellationToken);
        if (table is not null && AfterOpen is { } after) await after();
        return table;
    }

    public Task<GeneratedTable> StoreAsync(GeneratedTableSource source, ITactKeyStatus keys, GeneratedTableContent content, CancellationToken cancellationToken = default) =>
        inner.StoreAsync(source, keys, content, cancellationToken);

    public async Task<GeneratedTable> GetOrBuildAsync(GeneratedTableSource source, ITactKeyStatus keys, Func<CancellationToken, Task<GeneratedTableContent>> build,
        CancellationToken cancellationToken = default)
    {
        Building?.Invoke(source);
        var table = await inner.GetOrBuildAsync(source, keys, async token =>
        {
            Interlocked.Increment(ref decodes);
            if (Decoding is { } decoding) await decoding(source);
            var content = await build(token);
            if (AfterDecode is { } after) await after();
            return content;
        }, cancellationToken);
        if (AfterOpen is { } opened) await opened();
        return table;
    }
}
