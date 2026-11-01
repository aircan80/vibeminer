using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;

namespace VibeMiner.Formats.Definitions;

/// <summary>One pinned definition file in the bundled stream: where it starts, its length and the SHA-256 it must have.</summary>
internal sealed record BundledDefinitionFile(string TableId, string Name, string SourceUrl, string Sha256, int ByteLength, long Offset);

/// <summary>How many parsed definitions stay cached: at most MaxEntries, and at most MaxSourceBytes of their source
/// files together. A parsed definition holds about four times its source bytes. The most recent one always stays.</summary>
internal sealed record BundledDefinitionCacheLimits(int MaxEntries = 64, long MaxSourceBytes = 2L << 20);

/// <summary>
/// The bundled definitions, read on demand (#144). A table's definition is decoded from the stream, checked against its
/// listed length and SHA-256 and parsed the first time it is asked for, then kept in a least-recently-used cache bounded
/// by <see cref="BundledDefinitionCacheLimits"/>. A file that fails its check or doesn't parse makes only its own table
/// unusable, and says why; every other table still opens. Callers asking for the same table at once share one read.
/// </summary>
internal sealed class BundledDefinitionCache
{
    private sealed record Outcome(BundledDefinition? Definition, DefinitionException? Failure);
    private sealed record Slot(BundledDefinitionFile File, Lazy<Outcome> Value);

    private readonly IReadOnlyDictionary<string, BundledDefinitionFile> files;
    private readonly BundledDefinitionFile[] ordered;
    private readonly Func<Stream> open;
    private readonly BundledDefinitionCacheLimits limits;
    private readonly Lock gate = new();
    private readonly Dictionary<string, LinkedListNode<Slot>> slots = new(StringComparer.Ordinal);
    private readonly LinkedList<Slot> recent = new();
    private readonly Lazy<Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>> columns;
    private long cachedBytes;
    private int reads;

    internal BundledDefinitionCache(IReadOnlyDictionary<string, BundledDefinitionFile> files, Func<Stream> open, BundledDefinitionCacheLimits limits)
    {
        if (limits.MaxEntries < 1 || limits.MaxSourceBytes < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        (this.files, this.open, this.limits) = (files, open, limits);
        ordered = files.Values.OrderBy(file => file.Offset).ToArray();
        // One pass over the stream on one worker, the first time a search needs it.
        columns = new(() => Task.Run(ReadColumnNames));
    }

    /// <summary>How many tables have a bundled definition file, readable or not.</summary>
    internal int Count => files.Count;
    internal bool ContainsKey(string tableId) => files.ContainsKey(tableId);
    internal BundledDefinitionFile? File(string tableId) => files.GetValueOrDefault(tableId);
    internal int CachedCount { get { lock (gate) return slots.Count; } }
    internal long CachedSourceBytes { get { lock (gate) return cachedBytes; } }
    /// <summary>How many definition files have been decoded and parsed so far, for tests.</summary>
    internal int Reads => Volatile.Read(ref reads);

    /// <summary>The definition, or the failure that makes its table unusable (see <see cref="Require"/>).</summary>
    internal BundledDefinition this[string tableId] => Require(tableId);

    /// <summary>The definition a table reads with. Throws DEFINITION_NOT_BUNDLED when the table has no file,
    /// DEFINITION_CORRUPT when the file fails its pinned check and DEFINITION_UNSUPPORTED when it doesn't parse.</summary>
    internal BundledDefinition Require(string tableId) =>
        TryGet(tableId, out var definition, out var failure) ? definition
            : throw failure ?? new DefinitionException("DEFINITION_NOT_BUNDLED", "This table's definition is not bundled yet.");

    /// <summary>True with the parsed definition. False with a failure when the table's file can't be used, or with no
    /// failure when the table has no bundled file.</summary>
    internal bool TryGet(string tableId, [NotNullWhen(true)] out BundledDefinition? definition, out DefinitionException? failure)
    {
        definition = null; failure = null;
        if (!files.TryGetValue(tableId, out var file)) return false;
        var outcome = Touch(file).Value.Value;
        definition = outcome.Definition;
        // A new exception per caller: one shared instance thrown on several threads would mix their stack traces.
        failure = outcome.Failure is { } shared ? new DefinitionException(shared.Code, shared.Message, shared.Retryable) : null;
        return definition is not null;
    }

    /// <summary>Every bundled table's column names, for the catalogue search. Read once from the COLUMNS sections only and
    /// not cached as definitions; a file that fails its check or its COLUMNS section has none.</summary>
    internal Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ColumnNamesAsync(CancellationToken token) => columns.Value.WaitAsync(token);

    private Slot Touch(BundledDefinitionFile file)
    {
        lock (gate)
        {
            if (slots.TryGetValue(file.TableId, out var node)) { recent.Remove(node); recent.AddFirst(node); return node.Value; }
            var slot = new Slot(file, new(() => Read(file), LazyThreadSafetyMode.ExecutionAndPublication));
            slots.Add(file.TableId, recent.AddFirst(slot)); cachedBytes += file.ByteLength;
            while (slots.Count > 1 && (slots.Count > limits.MaxEntries || cachedBytes > limits.MaxSourceBytes))
            {
                var last = recent.Last!; recent.RemoveLast(); slots.Remove(last.Value.File.TableId); cachedBytes -= last.Value.File.ByteLength;
            }
            return slot;
        }
    }

    private Outcome Read(BundledDefinitionFile file)
    {
        Interlocked.Increment(ref reads);
        byte[] bytes;
        try
        {
            using var stream = new BrotliStream(open(), CompressionMode.Decompress);
            Skip(stream, file.Offset);
            bytes = new byte[file.ByteLength]; stream.ReadExactly(bytes);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IOException) { return new(null, Corrupt(file)); }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256) return new(null, Corrupt(file));
        try { return new(new(file.Sha256, file.SourceUrl, DbdDefinitionReader.Parse(bytes)), null); }
        catch (Db2FormatException error) { return new(null, Unsupported(file, error)); }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> ReadColumnNames()
    {
        var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            using var stream = new BrotliStream(open(), CompressionMode.Decompress);
            long at = 0; var buffer = new byte[BundledDefinitions.MaxDefinitionBytes];
            foreach (var file in ordered)
            {
                Skip(stream, file.Offset - at);
                var bytes = buffer.AsSpan(0, file.ByteLength); stream.ReadExactly(bytes); at = file.Offset + file.ByteLength;
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256) continue;
                try { names.Add(file.TableId, Array.AsReadOnly(DbdDefinitionReader.ParseColumns(bytes).Select(column => column.Name).ToArray())); }
                catch (Db2FormatException) { }
            }
        }
        // The stream was checked whole at load, so this needs damaged memory; tables read so far keep their names.
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IOException) { }
        return names.AsReadOnly();
    }

    private static void Skip(Stream stream, long count)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (count > 0)
            {
                var read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, count));
                if (read == 0) throw new EndOfStreamException();
                count -= read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    private static DefinitionException Corrupt(BundledDefinitionFile file) => new("DEFINITION_CORRUPT",
        $"The bundled {file.Name} definition doesn’t match its pinned SHA-256, so this table can’t open. Other tables still open; reinstalling VibeMiner restores the file.");

    private static DefinitionException Unsupported(BundledDefinitionFile file, Db2FormatException error) => new("DEFINITION_UNSUPPORTED",
        $"VibeMiner can’t read the pinned {file.Name} definition ({error.Code}), so this table can’t open. {error.Message} Other tables still open.");
}
