using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;

namespace VibeMiner.Formats.Generated;

/// <summary>
/// <see cref="IGeneratedTables"/> over the generated store. Keys:
/// base = SHA-256 of <see cref="GeneratedTableSource.IdentityText"/>; digest(S) = SHA-256 of the sorted names in S, one
/// "name=fingerprint" or "name=?" per line; node N(S) = GeneratedKey.Hash(base || digest(S)); a table filed under key set D has its
/// record file manifest at GeneratedKey.Hash("table" || N(D)).
/// Lookup starts at S = {} and follows nodes whose keys grow (at most <see cref="MaxChainSteps"/>) until a node's keys equal its own
/// set. Rows are streamed into the record file; memory is one row, one block and the writer's id list.
/// Segments are content-addressed and may be shared by tables with the same rows, so only segments that read damaged are deleted;
/// stale nodes and files are left for a later collection.
/// </summary>
public sealed class GeneratedTables : IGeneratedTables
{
    public const int MaxChainSteps = 64;
    private static readonly byte[] TablePrefix = "table"u8.ToArray();

    private readonly IGeneratedStore store;
    private readonly RecordFileOptions options;
    private readonly Lock gate = new();
    private readonly Dictionary<GeneratedKey, Flight> flights = [];

    public GeneratedTables(IGeneratedStore store, RecordFileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        // A row can carry up to 512 strings of 64 KiB (Db2Limits); the record file's own ceiling is 16 MiB.
        this.options = options ?? new RecordFileOptions { MaxRecordBytes = 16 << 20 };
    }

    public async Task<GeneratedTable?> OpenAsync(GeneratedTableSource source, ITactKeyStatus keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        var lookup = new Lookup(source, keys);
        var set = Array.Empty<string>();
        for (var step = 0; step < MaxChainSteps; step++)
        {
            var nodeKey = lookup.Node(set);
            var node = await ReadNodeAsync(nodeKey, lookup, cancellationToken).ConfigureAwait(false);
            if (node is null) return null;
            if (node.Keys.SequenceEqual(set)) return await OpenFileAsync(lookup, nodeKey, set, cancellationToken).ConfigureAwait(false);
            if (node.Keys.Count > set.Length && set.All(node.Keys.Contains)) { set = [.. node.Keys]; continue; }
            await store.DeleteAsync([nodeKey], cancellationToken).ConfigureAwait(false);
            return null;
        }
        // A chain this long can't come from a real table: drop its start so it is rebuilt.
        await store.DeleteAsync([lookup.Node([])], cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<GeneratedTable> StoreAsync(GeneratedTableSource source, ITactKeyStatus keys, GeneratedTableContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        try { return await StoreCoreAsync(source, keys, content, cancellationToken).ConfigureAwait(false); }
        finally { if (content.Owner is { } owner) await owner.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task<GeneratedTable> StoreCoreAsync(GeneratedTableSource source, ITactKeyStatus keys, GeneratedTableContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(content.Header, nameof(content));
        ArgumentNullException.ThrowIfNull(content.Sections, nameof(content));
        ArgumentNullException.ThrowIfNull(content.Completion, nameof(content));
        ArgumentNullException.ThrowIfNull(content.KeysUsed, nameof(content));
        ArgumentNullException.ThrowIfNull(content.Rows, nameof(content));
        if (content.KeysUsed.Count > TactKeyNames.MaxKeys) throw new ArgumentException($"A table depends on at most {TactKeyNames.MaxKeys} keys.", nameof(content));
        foreach (var name in content.KeysUsed) TactKeyNames.Check(name, nameof(content));
        foreach (var section in content.Sections)
        {
            ArgumentNullException.ThrowIfNull(section, nameof(content));
            if (section.IsEncrypted && !content.KeysUsed.Contains(section.Key, StringComparer.Ordinal))
                throw new ArgumentException($"Section {section.Index} is keyed with {section.Key}, which isn't among the keys used.", nameof(content));
        }
        if (content.Completion.ExcludedEncryptedSections != content.Sections.Count(s => s.IsEncrypted && !s.IsDecrypted))
            throw new ArgumentException("The completion's excluded encrypted sections don't match the encrypted sections.", nameof(content));
        cancellationToken.ThrowIfCancellationRequested();

        var lookup = new Lookup(source, keys);
        string[] used = [.. content.KeysUsed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var finalKey = lookup.Node(used);

        // Already stored and whole: keep it. Whatever is there and not whole has been deleted by the check.
        if (await ReadNodeAsync(finalKey, lookup, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (existing.Keys.SequenceEqual(used) && await OpenFileAsync(lookup, finalKey, used, cancellationToken).ConfigureAwait(false) is { } table) return table;
            await store.DeleteAsync([finalKey], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var orphan = await RecordFile.OpenAsync(store, ManifestKey(finalKey), cancellationToken).ConfigureAwait(false);
            // A whole manifest left behind (its node was deleted) holds the same bytes this write would; a damaged one must go first.
            if (orphan.State == GeneratedReadState.Corrupt) await store.DeleteAsync([ManifestKey(finalKey)], cancellationToken).ConfigureAwait(false);
        }

        var metadata = new TableMetadata(TableMetadata.FormatName, source.Echo(), content.Header, [.. content.Sections], content.Completion, used);
        using (var writer = new RecordFileWriter(store, options))
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            await foreach (var row in content.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                RowCodec.Encode(row, buffer);
                await writer.AddAsync(row.Id, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            }
            await writer.CompleteAsync(ManifestKey(finalKey), TableJson.Encode(metadata), cancellationToken).ConfigureAwait(false);
        }

        var nodeBytes = TableJson.Encode(new TableNode(TableNode.FormatName, source.Echo(), used));
        await store.PutAsync([new GeneratedPut(finalKey, nodeBytes)], cancellationToken).ConfigureAwait(false);
        await LinkChainAsync(lookup, used, nodeBytes, cancellationToken).ConfigureAwait(false);

        return await OpenFileAsync(lookup, finalKey, used, cancellationToken).ConfigureAwait(false)
            ?? throw TableJson.Corrupt("The table read back damaged right after it was stored.");
    }

    /// <summary>Rounds of open-then-join before a caller stops sharing and builds for its own key status.</summary>
    private const int MaxSharedRounds = 8;

    public async Task<GeneratedTable> GetOrBuildAsync(GeneratedTableSource source, ITactKeyStatus keys, Func<CancellationToken, Task<GeneratedTableContent>> build,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(build);
        var lookup = new Lookup(source, keys);
        var flightKey = lookup.Node([]);
        for (var round = 0; round < MaxSharedRounds; round++)
        {
            if (await OpenAsync(source, keys, cancellationToken).ConfigureAwait(false) is { } hit) return hit;
            var table = await JoinAsync(flightKey, source, keys, build, cancellationToken).ConfigureAwait(false);
            // The flight was started with its first caller's key status. When a key this table uses has another status for this
            // caller (it arrived in between), the table isn't this caller's: go round again.
            if (lookup.Node(table.KeysUsed) == table.NodeKey) return table;
        }
        return await StoreAsync(source, keys, await build(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeneratedTable> JoinAsync(GeneratedKey flightKey, GeneratedTableSource source, ITactKeyStatus keys,
        Func<CancellationToken, Task<GeneratedTableContent>> build, CancellationToken cancellationToken)
    {
        Flight flight;
        var start = false;
        lock (gate)
        {
            if (!flights.TryGetValue(flightKey, out flight!) || flight.Abandoned)
            {
                flight = new Flight();
                flights[flightKey] = flight;
                start = true;
            }
            flight.Waiters++;
        }
        if (start) _ = RunAsync(flight, flightKey, source, keys, build);
        try
        {
            return await flight.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var cancel = false;
            lock (gate)
                if (--flight.Waiters == 0 && !flight.Done) { flight.Abandoned = true; cancel = true; }
            if (cancel)
                try { await flight.Cancel.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
        }
    }

    private async Task RunAsync(Flight flight, GeneratedKey flightKey, GeneratedTableSource source, ITactKeyStatus keys,
        Func<CancellationToken, Task<GeneratedTableContent>> build)
    {
        var token = flight.Cancel.Token;
        GeneratedTable? table = null;
        Exception? error = null;
        try
        {
            await Task.Yield();
            table = await OpenAsync(source, keys, token).ConfigureAwait(false)
                ?? await StoreAsync(source, keys, await build(token).ConfigureAwait(false), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { error = e; }
        // Leave the map before answering, so a waiter that goes round again never rejoins this finished flight.
        lock (gate)
        {
            flight.Done = true;
            if (flights.TryGetValue(flightKey, out var current) && current == flight) flights.Remove(flightKey);
        }
        if (table is not null) flight.Result.TrySetResult(table);
        else if (error is not null) flight.Result.TrySetException(error);
        else flight.Result.TrySetCanceled(token);
        flight.Cancel.Dispose();
    }

    /// <summary>Points the chain from S = {} at the table filed under <paramref name="used"/>.</summary>
    private async Task LinkChainAsync(Lookup lookup, string[] used, byte[] nodeBytes, CancellationToken cancellationToken)
    {
        var set = Array.Empty<string>();
        for (var step = 0; step < MaxChainSteps && !set.SequenceEqual(used); step++)
        {
            var nodeKey = lookup.Node(set);
            var node = await ReadNodeAsync(nodeKey, lookup, cancellationToken).ConfigureAwait(false);
            if (node is not null && node.Keys.Count > set.Length && set.All(node.Keys.Contains) && node.Keys.All(used.Contains))
            {
                set = [.. node.Keys];
                continue;
            }
            if (node is not null) await store.DeleteAsync([nodeKey], cancellationToken).ConfigureAwait(false);
            await store.PutAsync([new GeneratedPut(nodeKey, nodeBytes)], cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>The node at the key, or null when absent. A damaged or foreign node is deleted and read as null.</summary>
    private async Task<TableNode?> ReadNodeAsync(GeneratedKey key, Lookup lookup, CancellationToken cancellationToken)
    {
        var read = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (read.State == GeneratedReadState.Absent) return null;
        TableNode? node = null;
        if (read.State == GeneratedReadState.Present && read.Bytes is { Length: <= TableNode.MaxBytes } bytes)
        {
            try { node = TableJson.Decode<TableNode>(bytes, "A node"); }
            catch (GeneratedStoreException e) when (e.Code == GeneratedStoreCodes.Corrupt) { }
        }
        if (node is not null && node.Format == TableNode.FormatName && node.Identity == lookup.Echo && IsKeySet(node.Keys)) return node;
        await store.DeleteAsync([key], cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static bool IsKeySet(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count > TactKeyNames.MaxKeys) return false;
        for (var i = 0; i < keys.Count; i++)
            if (!TactKeyNames.IsName(keys[i]) || (i > 0 && string.CompareOrdinal(keys[i - 1], keys[i]) >= 0)) return false;
        return true;
    }

    /// <summary>Opens the table filed at the final node; anything damaged is deleted (node, manifest, damaged segments) and read as null.</summary>
    private async Task<GeneratedTable?> OpenFileAsync(Lookup lookup, GeneratedKey nodeKey, string[] used, CancellationToken cancellationToken)
    {
        var manifestKey = ManifestKey(nodeKey);
        var open = await RecordFile.OpenAsync(store, manifestKey, cancellationToken).ConfigureAwait(false);
        if (open.State == GeneratedReadState.Absent) return null;
        if (open.File is not { } file)
        {
            await store.DeleteAsync([nodeKey, manifestKey], cancellationToken).ConfigureAwait(false);
            return null;
        }
        TableMetadata? metadata = null;
        try { metadata = TableJson.Decode<TableMetadata>(file.Metadata.Span, "The table metadata"); }
        catch (GeneratedStoreException e) when (e.Code == GeneratedStoreCodes.Corrupt) { }
        if (metadata is null || metadata.Format != TableMetadata.FormatName || metadata.Identity != lookup.Echo || metadata.Header is null ||
            metadata.Sections is null || metadata.Completion is null || metadata.KeysUsed is null || !metadata.KeysUsed.SequenceEqual(used))
        {
            await store.DeleteAsync([nodeKey, manifestKey], cancellationToken).ConfigureAwait(false);
            return null;
        }
        // A cheap probe of each segment: missing or damaged ones are found here rather than on the first page.
        var segments = file.Keys.Skip(1).ToArray();
        var probe = new byte[1];
        foreach (var segment in segments)
            if ((await store.ReadRangeAsync(segment, 0, probe, cancellationToken).ConfigureAwait(false)).State != GeneratedReadState.Present)
            {
                await DiscardAsync(nodeKey, manifestKey, segments, cancellationToken).ConfigureAwait(false);
                return null;
            }
        return new GeneratedTable(lookup.Source, nodeKey, metadata, file, token => DiscardAsync(nodeKey, manifestKey, segments, token));
    }

    /// <summary>Deletes a damaged table: its node and manifest, and those of its segments that read damaged in full.</summary>
    private async Task DiscardAsync(GeneratedKey nodeKey, GeneratedKey manifestKey, GeneratedKey[] segments, CancellationToken cancellationToken)
    {
        var doomed = new List<GeneratedKey> { nodeKey, manifestKey };
        foreach (var segment in segments)
            if ((await store.ReadAsync(segment, cancellationToken).ConfigureAwait(false)).State == GeneratedReadState.Corrupt) doomed.Add(segment);
        await store.DeleteAsync(doomed, cancellationToken).ConfigureAwait(false);
    }

    private static GeneratedKey ManifestKey(GeneratedKey nodeKey)
    {
        Span<byte> identity = stackalloc byte[TablePrefix.Length + GeneratedKey.Bytes];
        TablePrefix.CopyTo(identity);
        nodeKey.WriteTo(identity[TablePrefix.Length..]);
        return GeneratedKey.Hash(identity);
    }

    /// <summary>One lookup's base hash and key statuses, each key asked once so a lookup sees one snapshot.</summary>
    private sealed class Lookup(GeneratedTableSource source, ITactKeyStatus keys)
    {
        private readonly byte[] baseHash = source.BaseHash();
        private readonly Dictionary<string, string> statuses = new(StringComparer.Ordinal);

        public GeneratedTableSource Source { get; } = source;
        public TableIdentity Echo { get; } = source.Echo();

        public GeneratedKey Node(IReadOnlyList<string> set)
        {
            var text = new StringBuilder();
            foreach (var name in set) text.Append(name).Append('=').Append(Status(name)).Append('\n');
            Span<byte> identity = stackalloc byte[64];
            baseHash.CopyTo(identity);
            SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), identity[32..]);
            return GeneratedKey.Hash(identity);
        }

        private string Status(string name)
        {
            if (statuses.TryGetValue(name, out var status)) return status;
            var fingerprint = keys.FingerprintOf(name);
            if (fingerprint is not null && !TactKeyNames.IsFingerprint(fingerprint))
                throw new InvalidOperationException("The key status gave a fingerprint that isn't 1-128 printable ASCII characters without spaces, and not \"?\".");
            return statuses[name] = fingerprint ?? "?";
        }
    }

    private sealed class Flight
    {
        public readonly TaskCompletionSource<GeneratedTable> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Cancel = new();
        public int Waiters;
        public bool Done, Abandoned;
    }
}
