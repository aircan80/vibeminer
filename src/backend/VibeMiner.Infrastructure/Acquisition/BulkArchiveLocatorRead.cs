using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed partial class BulkArchiveLocator
{
    internal async Task<BulkArchiveLocatorSession> OpenAsync(BulkArchiveLocatorArtifact artifact,
        string expectedCdnConfigurationSha256, IReadOnlyList<string> expectedArchiveKeys,
        DownloadLedgerArtifact expectedLedger, CancellationToken token)
    {
        ValidateContext(expectedCdnConfigurationSha256, expectedArchiveKeys, expectedLedger);
        expectedArchiveKeys = expectedArchiveKeys.ToArray();
        ValidateContext(expectedCdnConfigurationSha256, expectedArchiveKeys, expectedLedger);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!Canonical(artifact.MapSha256, 64) || !Canonical(artifact.ManifestSha256, 64) || artifact.ManifestBytes is < 1 or > 2097152 ||
            artifact.LocationCount < 0 || artifact.LocationCount > limits.MaxLocations || artifact.LocatedMembers < 0 ||
            artifact.LocatedMembers > expectedLedger.ObjectCount || artifact.LocatedMembers > artifact.LocationCount ||
            artifact.MapBytes != HeaderBytes + artifact.LocationCount * RecordBytes)
            throw Error("BULK_LOCATOR_INTEGRITY", "The sealed archive map descriptor exceeds its exact supported geometry.");
        BulkArchiveLocatorManifest manifest;
        await using (var input = await OpenVerifiedFileAsync(artifact.ManifestSha256, artifact.ManifestBytes, token))
        {
            try
            {
                manifest = await JsonSerializer.DeserializeAsync<BulkArchiveLocatorManifest>(input,
                    new JsonSerializerOptions { MaxDepth = 16 }, token)
                    ?? throw Error("BULK_LOCATOR_INTEGRITY", "The sealed archive provenance manifest is missing.");
            }
            catch (JsonException) { throw Error("BULK_LOCATOR_INTEGRITY", "The sealed archive provenance manifest is malformed."); }
        }
        if (manifest.Revision != Revision || manifest.CdnConfigurationSha256 != expectedCdnConfigurationSha256 ||
            manifest.LedgerRevision != DownloadLedger.Revision || manifest.Ledger != expectedLedger || manifest.MapSha256 != artifact.MapSha256 || manifest.MapBytes != artifact.MapBytes ||
            manifest.LocationCount != artifact.LocationCount || manifest.LocatedMembers != artifact.LocatedMembers ||
            manifest.Sources is null || manifest.Sources.Length != expectedArchiveKeys.Count)
            throw Error("BULK_LOCATOR_CONTEXT", "The sealed archive map differs from the exact CDN configuration, DL source or supported revision.");
        long nativeBytes = 0;
        for (var i = 0; i < manifest.Sources.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var source = manifest.Sources[i];
            ValidateSourceReceipt(source, expectedCdnConfigurationSha256, expectedArchiveKeys[i]);
            if (source.ByteLength > limits.MaxNativeIndexBytes - nativeBytes)
                throw Error("BULK_LOCATOR_LIMIT", "The retained native index receipts exceed the aggregate admitted source budget.");
            nativeBytes += source.ByteLength;
        }
        var map = await OpenVerifiedFileAsync(artifact.MapSha256, artifact.MapBytes, token);
        try
        {
            var header = new byte[HeaderBytes]; await map.ReadExactlyAsync(header, token);
            if (!header.AsSpan().SequenceEqual(Header(artifact.LocationCount, artifact.LocatedMembers)))
                throw Error("BULK_LOCATOR_INTEGRITY", "The sealed archive map header differs from its descriptor.");
            var buffer = new byte[RecordBytes * 2048]; long remaining = artifact.LocationCount, members = 0;
            Location? previous = null;
            while (remaining > 0)
            {
                var count = (int)Math.Min(2048, remaining); await map.ReadExactlyAsync(buffer.AsMemory(0, count * RecordBytes), token);
                using (await resources.EnterCpuAsync(context, token))
                {
                    for (var at = 0; at < count; at++)
                    {
                        token.ThrowIfCancellationRequested(); var row = Location.Read(buffer.AsSpan(at * RecordBytes, RecordBytes));
                        if (row.High == 0 && row.Low == 0 || row.Archive < 0 || row.Archive >= manifest.Sources.Length ||
                            row.Offset is < 0 or > uint.MaxValue || row.Size == 0 ||
                            previous is { } prior && (LocationComparer.Instance.Compare(prior, row) >= 0 ||
                                prior.CompareKey(row) == 0 && prior.Size != row.Size))
                            throw Error("BULK_LOCATOR_INTEGRITY", "The sealed archive map contains an invalid, conflicting or unordered location.");
                        if (previous is null || previous.Value.CompareKey(row) != 0) members++;
                        previous = row;
                    }
                }
                remaining -= count;
            }
            if (members != artifact.LocatedMembers || await map.ReadAsync(buffer.AsMemory(0, 1), token) != 0)
                throw Error("BULK_LOCATOR_INTEGRITY", "The sealed archive map has inconsistent membership or trailing bytes.");
            return new(map, manifest.Sources.Select(source => source.ArchiveKey).ToArray(), artifact.LocationCount);
        }
        catch { await map.DisposeAsync(); throw; }
    }

    /// <summary>
    /// A complete, verified map lease. An empty result says only that this key was not located in
    /// the sealed supported archive-list/DL scope. It does not assert upstream absence.
    /// </summary>
    internal sealed class BulkArchiveLocatorSession(FileStream input, string[] archives, long count) : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool disposed;
        internal async Task<IReadOnlyList<ContentArchiveLocation>> FindAsync(string encodingKey, long encodedBytes, CancellationToken token)
        {
            if (!Canonical(encodingKey, 32) || encodedBytes is < 1 or > 0xffffffffffL)
                throw Error("BULK_LOCATOR_CONTEXT", "Choose an exact supported encoded identity and length.");
            var key = LedgerValue.Parse(encodingKey, encodedBytes);
            await gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var bytes = new byte[RecordBytes]; long lower = 0, upper = count;
                async Task<Location> Read(long at)
                {
                    input.Position = checked(HeaderBytes + at * RecordBytes);
                    await input.ReadExactlyAsync(bytes, token); return Location.Read(bytes);
                }
                while (lower < upper)
                {
                    token.ThrowIfCancellationRequested(); var middle = lower + (upper - lower) / 2;
                    if (Compare(await Read(middle), key) < 0) lower = middle + 1; else upper = middle;
                }
                var result = new List<ContentArchiveLocation>();
                for (var at = lower; at < count; at++)
                {
                    var row = await Read(at); if (Compare(row, key) != 0) break;
                    if (row.Size != encodedBytes) throw Error("CONTENT_SIZE_CONFLICT", "The sealed archive location differs from the requested exact encoded length.");
                    if (result.Count >= archives.Length) throw Error("BULK_LOCATOR_INTEGRITY", "An exact object exceeds the bounded native archive overlap count.");
                    result.Add(new(archives[row.Archive], row.Offset, row.Size));
                }
                return result;
            }
            finally { gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync();
            try { if (disposed) return; disposed = true; await input.DisposeAsync(); }
            finally { gate.Release(); }
        }
    }
}
