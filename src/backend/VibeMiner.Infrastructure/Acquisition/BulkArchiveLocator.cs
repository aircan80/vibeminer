using System.Buffers.Binary;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact.Archives;

namespace VibeMiner.Infrastructure;

internal sealed record BulkArchiveLocatorLimits(int MaxArchives = 4096, long MaxNativeIndexBytes = 512L << 20,
    long MaxLocations = 10_000_000, int MaximumOpenFiles = 32, long MaxWorkingBytes = 32L << 20);
internal sealed record BulkArchiveIndexSource(string CdnConfigurationSha256, string ArchiveKey, string Sha256, long ByteLength,
    string SourceUrl, DateTimeOffset RetrievedAt, string ParserRevision);
internal sealed record BulkArchiveLocatorCheckpoints(
    Func<string, string, CancellationToken, Task<BulkArchiveIndexSource?>> FindAsync,
    Func<string, BulkArchiveIndexSource, CancellationToken, Task> RecordAsync);
internal sealed record BulkArchiveLocatorArtifact(string MapSha256, long MapBytes, string ManifestSha256,
    long ManifestBytes, long LocationCount, long LocatedMembers);
internal sealed record BulkArchiveLocatorManifest(string Revision, string CdnConfigurationSha256,
    string LedgerRevision, DownloadLedgerArtifact Ledger, BulkArchiveIndexSource[] Sources, string MapSha256, long MapBytes,
    long LocationCount, long LocatedMembers);

/// <summary>
/// Download-all-only authenticated native-index merge. A returned descriptor is sealed CAS evidence;
/// durable job association remains a separate catalog operation. Missing sources never seal a map.
/// </summary>
internal sealed partial class BulkArchiveLocator
{
    internal const string Revision = "bulk-archive-locator-v1";
    internal const string NativeRevision = "archive-index-v1";
    internal const int HeaderBytes = 32, RecordBytes = 32;
    private static readonly byte[] Magic = "VMBARCM1"u8.ToArray();
    private static readonly SemaphoreSlim Building = new(1, 1);
    private readonly VerifiedObjectStore objects;
    private readonly OfficialSourceTransport transport;
    private readonly AcquisitionResources resources;
    private readonly IAcquisitionWorkContext? context;
    private readonly BulkArchiveLocatorLimits limits;
    private readonly TimeProvider clock;

    internal BulkArchiveLocator(VerifiedObjectStore objects, OfficialSourceTransport transport,
        AcquisitionResources resources, IAcquisitionWorkContext? context = null,
        BulkArchiveLocatorLimits? limits = null, TimeProvider? clock = null)
    {
        this.objects = objects; this.transport = transport; this.resources = resources;
        this.context = context; this.limits = limits ?? new(); this.clock = clock ?? TimeProvider.System;
        if (this.limits.MaxArchives is < 1 or > 4096 || this.limits.MaxNativeIndexBytes is < 28 or > 536870912 ||
            this.limits.MaxLocations is < 0 or > 10_000_000 || this.limits.MaximumOpenFiles is < 1 or > 32 ||
            this.limits.MaxWorkingBytes is < 65536 or > 33554432)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    internal async Task<BulkArchiveLocatorArtifact> BuildAsync(Uri route, string cdnConfigurationSha256,
        IReadOnlyList<string> archiveKeys, DownloadLedgerArtifact ledger, Func<string, Task> progress, CancellationToken token,
        Func<CancellationToken, Task<DownloadLedgerOrderedCursor>>? openLedger = null,
        BulkArchiveLocatorCheckpoints? checkpoints = null)
    {
        ValidateContext(cdnConfigurationSha256, archiveKeys, ledger);
        archiveKeys = archiveKeys.ToArray();
        ValidateContext(cdnConfigurationSha256, archiveKeys, ledger);
        ArgumentNullException.ThrowIfNull(progress);
        if (!await Building.WaitAsync(0, token)) throw Error("BULK_LOCATOR_BUSY", "Another bounded bulk archive map is being prepared.");
        string? mapPath = null;
        try
        {
            // Reserve the complete largest phase before creating private staging. Native sources are
            // published one at a time; the later map is independently capped at 320,000,032 bytes.
            var stagingBytes = Math.Max(limits.MaxNativeIndexBytes, checked(HeaderBytes + limits.MaxLocations * RecordBytes));
            using var staging = await resources.ReserveStagingAsync(stagingBytes, context, token);
            var sources = new List<BulkArchiveIndexSource>(archiveKeys.Count); long nativeBytes = 0;
            for (var i = 0; i < archiveKeys.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                await progress(InventoryProgress.Format("Preparing archive locator sources", i, archiveKeys.Count, "archives"));
                BulkArchiveIndexSource? source = null;
                if (checkpoints is not null)
                {
                    var saved = await checkpoints.FindAsync(cdnConfigurationSha256, archiveKeys[i], token);
                    if (saved is not null)
                    {
                        ValidateSourceReceipt(saved, cdnConfigurationSha256, archiveKeys[i]);
                        if (saved.ByteLength > limits.MaxNativeIndexBytes - nativeBytes)
                            throw Error("BULK_LOCATOR_LIMIT", "Retained native archive metadata exceeds the aggregate locator-source budget.");
                        try
                        {
                            await using var verified = await OpenVerifiedFileAsync(saved.Sha256, saved.ByteLength, token);
                            source = saved;
                        }
                        catch (DiscoveryException failure) when (failure.Code is "LOCAL_OBJECT_MISSING" or "LOCAL_OBJECT_CORRUPT")
                        { /* The old receipt gets no current credit; acquire and record a fresh exact source. */ }
                    }
                }
                if (source is null)
                {
                    source = await AcquireIndexAsync(route, cdnConfigurationSha256, archiveKeys[i], limits.MaxNativeIndexBytes - nativeBytes, progress, token);
                    if (checkpoints is not null) await checkpoints.RecordAsync(cdnConfigurationSha256, source, token);
                }
                nativeBytes = checked(nativeBytes + source.ByteLength); sources.Add(source);
            }
            await progress(InventoryProgress.Format("Preparing archive locator sources", sources.Count, sources.Count, "archives"));
            await using var cursors = await NativeCursors.OpenAsync(objects, sources, resources, context, limits, token);
            var ledgerReader = new DownloadLedger(objects);
            await using var dl = openLedger is null ? await ledgerReader.OpenOrderedAsync(ledger, token, resources, context) : await openLedger(token);
            if (dl.Artifact != ledger) throw Error("BULK_LOCATOR_CONTEXT", "The validated ordered cursor belongs to a different exact download ledger.");
            var queue = new PriorityQueue<NativeCursor, Location>(LocationComparer.Instance);
            foreach (var cursor in cursors.Items)
                if (await cursor.NextAsync(token)) queue.Enqueue(cursor, cursor.Current);
            var hasMember = await dl.NextAsync(token); long visitedMembers = 0, locations = 0, locatedMembers = 0;
            Location? previous = null; var memberLocated = false;
            await using (var output = await objects.CreateStagingFileAsync(token))
            {
                mapPath = output.Name;
                await output.WriteAsync(new byte[HeaderBytes], token);
                var writer = new MapWriter(output);
                IDisposable? mergeCpu = await resources.EnterCpuAsync(context, token); var work = 0;
                async Task WorkSlice()
                {
                    if (++work < 4096) return;
                    work = 0; mergeCpu?.Dispose(); mergeCpu = null;
                    mergeCpu = await resources.EnterCpuAsync(context, token);
                }
                try
                {
                    while (queue.TryDequeue(out var cursor, out _))
                    {
                        await WorkSlice();
                        token.ThrowIfCancellationRequested();
                        var row = cursor.Current;
                        // Conflicting authenticated claims fail even when the key is outside this DL.
                        if (previous is { } prior && prior.CompareKey(row) == 0 && prior.Size != row.Size)
                            throw Error("CONTENT_SIZE_CONFLICT", "Authenticated archive indexes disagree about an encoded object's length.");
                        previous = row;
                        while (hasMember && Compare(row, dl.Current) > 0)
                        {
                            if (memberLocated) locatedMembers++;
                            visitedMembers++; memberLocated = false;
                            hasMember = await dl.NextAsync(token);
                            await WorkSlice();
                            if (visitedMembers % 65536 == 0)
                                await progress(InventoryProgress.Format("Matching archive locations", visitedMembers, ledger.ObjectCount, "download objects"));
                        }
                        if (hasMember && Compare(row, dl.Current) == 0)
                        {
                            if (row.Size != dl.Current.Bytes)
                                throw Error("CONTENT_SIZE_CONFLICT", "An authenticated archive length differs from the exact download-ledger member.");
                            if (locations >= limits.MaxLocations)
                                throw Error("BULK_LOCATOR_LIMIT", "Valid overlapping archive locations exceed the admitted immutable-map bound.");
                            await writer.WriteAsync(row, token); locations++; memberLocated = true;
                        }
                        if (await cursor.NextAsync(token, cpuOwned: true)) queue.Enqueue(cursor, cursor.Current);
                    }
                    while (hasMember)
                    {
                        if (memberLocated) locatedMembers++;
                        visitedMembers++; memberLocated = false; hasMember = await dl.NextAsync(token);
                        await WorkSlice();
                        if (visitedMembers % 65536 == 0)
                            await progress(InventoryProgress.Format("Matching archive locations", visitedMembers, ledger.ObjectCount, "download objects"));
                    }
                }
                finally { mergeCpu?.Dispose(); }
                if (visitedMembers != ledger.ObjectCount) throw Error("BULK_LOCATOR_INTEGRITY", "The complete DL member traversal differs from its sealed descriptor.");
                await writer.FlushAsync(token);
                output.Position = 0; await output.WriteAsync(Header(locations, locatedMembers), token);
                await output.FlushAsync(token);
                if (output.Length != HeaderBytes + locations * RecordBytes)
                    throw Error("BULK_LOCATOR_INTEGRITY", "The private archive map changed length before sealing.");
            }
            await progress(InventoryProgress.Format("Matching archive locations", visitedMembers, ledger.ObjectCount, "download objects"));
            var mapBytes = checked(HeaderBytes + locations * RecordBytes);
            var mapSha = await PublishVerifiedFileAsync(mapPath, mapBytes, token); mapPath = null;
            var manifest = new BulkArchiveLocatorManifest(Revision, cdnConfigurationSha256, DownloadLedger.Revision, ledger, sources.ToArray(), mapSha, mapBytes, locations, locatedMembers);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (manifestBytes.Length > 2 << 20) throw Error("BULK_LOCATOR_LIMIT", "Archive provenance exceeds its bounded manifest size.");
            string? manifestPath = null;
            try
            {
                await using (var output = await objects.CreateStagingFileAsync(token))
                { manifestPath = output.Name; await output.WriteAsync(manifestBytes, token); await output.FlushAsync(token); }
                var manifestSha = await PublishVerifiedFileAsync(manifestPath, manifestBytes.Length, token); manifestPath = null;
                return new(mapSha, mapBytes, manifestSha, manifestBytes.Length, locations, locatedMembers);
            }
            finally { if (manifestPath is not null && File.Exists(manifestPath)) File.Delete(manifestPath); }
        }
        finally
        {
            try { if (mapPath is not null && File.Exists(mapPath)) File.Delete(mapPath); }
            finally { Building.Release(); }
        }
    }

    private async Task<BulkArchiveIndexSource> AcquireIndexAsync(Uri route, string cdnSha, string key, long remainingBytes,
        Func<string, Task> progress, CancellationToken token)
    {
        var url = ArchivedContentLocator.Url(route, key, index: true);
        var tail = await transport.ReadRangeAsync(url, null, null, ArchiveIndexParser.FooterBytes, token);
        ArchiveIndexFooter footer;
        using (await resources.EnterCpuAsync(context, token))
            footer = ArchiveIndexParser.AuthenticateFooter(tail.Bytes, key, tail.Total, new(), token);
        if (footer.IndexBytes > remainingBytes)
            throw Error("BULK_LOCATOR_LIMIT", "Authenticated native archive metadata exceeds the aggregate locator-source budget.");
        string? path = null;
        try
        {
            await using (var output = await objects.CreateStagingFileAsync(token))
            {
                path = output.Name;
                for (long offset = 0; offset < footer.IndexBytes;)
                {
                    var length = (int)Math.Min(4 << 20, footer.IndexBytes - offset);
                    var part = await transport.ReadRangeAsync(url, footer.IndexBytes, offset, length, token);
                    await output.WriteAsync(part.Bytes, token); offset += length;
                    await progress(InventoryProgress.Format("Downloading archive locator metadata", offset, footer.IndexBytes, "bytes"));
                }
                await output.FlushAsync(token);
            }
            await using (var input = OpenNative(path))
            {
                if (input.Length != footer.IndexBytes) throw Error("BULK_LOCATOR_INTEGRITY", "The staged native index changed length.");
                input.Position = footer.IndexBytes - ArchiveIndexParser.FooterBytes;
                var actualFooter = new byte[ArchiveIndexParser.FooterBytes]; await input.ReadExactlyAsync(actualFooter, token);
                if (!actualFooter.AsSpan().SequenceEqual(tail.Bytes))
                    throw Error("BULK_LOCATOR_INTEGRITY", "The complete native index differs from its authenticated footer probe.");
                input.Position = footer.TocOffset; var tocBytes = new byte[footer.TocBytes]; await input.ReadExactlyAsync(tocBytes, token);
                ArchiveIndexToc toc;
                using (await resources.EnterCpuAsync(context, token)) toc = ArchiveIndexParser.AuthenticateToc(footer, tocBytes, token);
                var pageBytes = new byte[ArchiveIndexParser.PageBytes]; input.Position = 0;
                for (var page = 0; page < footer.PageCount; page++)
                {
                    await input.ReadExactlyAsync(pageBytes, token);
                    using (await resources.EnterCpuAsync(context, token))
                        _ = ArchiveIndexParser.EnumeratePage(toc, ArchiveIndexParser.GetPage(toc, page, token), pageBytes, token);
                    if ((page + 1) % 128 == 0 || page + 1 == footer.PageCount)
                        await progress(InventoryProgress.Format("Authenticating archive locator metadata", page + 1, footer.PageCount, "pages"));
                }
            }
            var sha = await PublishVerifiedFileAsync(path, footer.IndexBytes, token); path = null;
            return new(cdnSha, key, sha, footer.IndexBytes, url.AbsoluteUri, clock.GetUtcNow(), NativeRevision);
        }
        finally { if (path is not null && File.Exists(path)) File.Delete(path); }
    }

    private void ValidateContext(string cdnSha, IReadOnlyList<string> keys, DownloadLedgerArtifact ledger)
    {
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(ledger);
        if (!Canonical(cdnSha, 64) || keys.Count > limits.MaxArchives || keys.Any(key => !Canonical(key, 32)) ||
            keys.Distinct(StringComparer.Ordinal).Count() != keys.Count || !Canonical(ledger.Sha256, 64) ||
            ledger.ObjectCount is < 0 or > 10_000_000 || ledger.ByteLength != DownloadLedger.HeaderBytes + ledger.ObjectCount * DownloadLedger.RecordBytes ||
            ledger.TotalEncodedBytes < 0)
            throw Error("BULK_LOCATOR_CONTEXT", "A complete bounded exact CDN configuration and download-ledger context is required.");
    }
    private static bool Canonical(string? value, int length) => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateSourceReceipt(BulkArchiveIndexSource? source, string cdnSha, string archiveKey)
    {
        if (source is null) throw Error("BULK_LOCATOR_INTEGRITY", "A completed native archive source receipt is missing.");
        if (source.CdnConfigurationSha256 != cdnSha || source.ArchiveKey != archiveKey)
            throw Error("BULK_LOCATOR_CONTEXT", "A native archive receipt differs from the pinned configuration and archive ordinal.");
        if (!Canonical(source.Sha256, 64) || source.ByteLength is < 28 or > VerifiedObjectStore.MaxObjectBytes ||
            source.ParserRevision != NativeRevision || source.RetrievedAt == default ||
            !Uri.TryCreate(source.SourceUrl, UriKind.Absolute, out var url))
            throw Error("BULK_LOCATOR_INTEGRITY", "A native archive source receipt has invalid identity or provenance.");
        OfficialSourceTransport.ValidateUri(url);
        if (!url.AbsolutePath.EndsWith($"/{archiveKey[..2]}/{archiveKey[2..4]}/{archiveKey}.index", StringComparison.Ordinal))
            throw Error("BULK_LOCATOR_INTEGRITY", "A native source URL differs from its immutable archive key.");
    }
    private async Task<string> PublishVerifiedFileAsync(string path, long bytes, CancellationToken token)
    {
        return await objects.PublishFileAsync(path, bytes, resources, context, token);
    }
    private async Task<FileStream> OpenVerifiedFileAsync(string sha256, long bytes, CancellationToken token)
    {
        return await objects.OpenVerifiedAsync(sha256, bytes, resources, context, token);
    }
    private static int Compare(Location row, LedgerValue member) => row.High != member.High ? row.High.CompareTo(member.High) : row.Low.CompareTo(member.Low);
    private static FileStream OpenNative(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
    private static byte[] Header(long locations, long members)
    {
        var header = new byte[HeaderBytes]; Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 1); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), RecordBytes);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), locations); BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(24), members); return header;
    }
    private static DiscoveryException Error(string code, string message) => new(code, message);
}
