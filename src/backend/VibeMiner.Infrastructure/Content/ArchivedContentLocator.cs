using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using VibeMiner.Protocols.Tact.Archives;

namespace VibeMiner.Infrastructure;

internal sealed record ContentLocatorArtifact(string EncodingKey, string CdnConfigurationSha256,
    string ArchiveKey, string Sha256, long ByteLength);
internal sealed record ContentArchiveLocation(string ArchiveKey, long Offset, long EncodedBytes);

/// <summary>Authenticated bounded archive lookup, with immutable successful proofs rather than a global EKey index.</summary>
internal sealed class ArchivedContentLocator(DiscoveryCatalog catalog, VerifiedObjectStore objects,
    OfficialSourceTransport transport, ArchiveProofCache? sharedCache = null, string? sourceScope = null,
    AcquisitionResources? resources = null)
{
    private sealed record DirectoryProof(ArchiveIndexFooter Footer, ArchiveIndexToc Toc, byte[] FooterBytes, byte[] TocBytes);
    private sealed record LocatorProof(long IndexBytes, byte[] Footer, byte[] Toc, int PageOrdinal, byte[] Page);
    private readonly ArchiveProofCache cache = sharedCache ?? new();
    private const long MaxProofBytes = 8L << 20;

    internal async Task<ContentArchiveLocation> LocateAsync(Uri route, TactConfiguration cdn,
        string cdnSha256, string encodingKey, long encodedBytes, Func<string, Task> progress, CancellationToken token)
    {
        if (!cdn.Fields.TryGetValue("archives", out var archives) || archives.Count > 4096 ||
            archives.Any(key => key.Length != 32 || key.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) ||
            archives.Distinct(StringComparer.Ordinal).Count() != archives.Count)
            throw Error("CONTENT_LOCATOR_UNSUPPORTED", "The pinned CDN configuration has no bounded supported archive list.");
        var saved = await catalog.FindContentLocatorAsync(encodingKey, cdnSha256, token);
        if (saved is not null)
        {
            if (!archives.Contains(saved.ArchiveKey, StringComparer.Ordinal) || saved.ByteLength is < 1 or > MaxProofBytes)
                throw Error("CONTENT_LOCATOR_INTEGRITY", "A saved locator differs from its pinned CDN configuration.");
            await progress("Verifying saved archive locator");
            await using var input = resources is null
                ? await objects.OpenVerifiedAsync(saved.Sha256, saved.ByteLength, token)
                : await objects.OpenVerifiedAsync(saved.Sha256, saved.ByteLength, resources, transport.WorkContext, token);
            using var cpu = resources is null ? null : await resources.EnterCpuAsync(transport.WorkContext, token);
            var proof = await JsonSerializer.DeserializeAsync<LocatorProof>(input, cancellationToken: token)
                ?? throw Error("CONTENT_LOCATOR_INTEGRITY", "A saved archive proof is missing.");
            return Validate(saved.ArchiveKey, encodingKey, encodedBytes, proof, token);
        }

        using var search = CancellationTokenSource.CreateLinkedTokenSource(token);
        search.CancelAfter(TimeSpan.FromMinutes(5));
        var gate = new object(); var next = -1; var completed = 0; var requests = 0;
        long wire = 0; var unavailable = 0;
        ContentArchiveLocation? result = null; LocatorProof? resultProof = null;
        using var reporting = new SemaphoreSlim(1, 1);
        await progress(InventoryProgress.Format("Locating selected file in archives", 0, archives.Count, "archives"));

        async Task<(byte[] Bytes, long Total)> Read(Uri url, long? total, long? offset, int length,
            VibeMiner.Application.IAcquisitionWorkContext work, CancellationToken requestToken)
        {
            if (Interlocked.Increment(ref requests) > 16_384 || Interlocked.Add(ref wire, length) > 128L << 20)
                throw Error("CONTENT_LOCATOR_LIMIT", "Archive lookup exceeded its bounded request or wire budget.");
            return await transport.WithContext(work).ReadRangeAsync(url, total, offset, length, requestToken);
        }
        async Task Worker()
        {
            try
            {
                while (true)
                {
                    search.Token.ThrowIfCancellationRequested();
                    lock (gate) if (result is not null) return;
                    var index = Interlocked.Increment(ref next); if (index >= archives.Count) return;
                    var key = archives[index]; var url = Url(route, key, index: true);
                    DirectoryProof directory;
                    try
                    {
                        var scope = sourceScope ?? cdnSha256;
                        directory = await cache.GetAsync<DirectoryProof>($"{scope}|{url.AbsoluteUri}|directory",
                            transport.WorkContext, async (work, requestToken) =>
                            {
                                var tail = await Read(url, null, null, 28, work, requestToken);
                                ArchiveIndexFooter footer;
                                using (resources is null ? null : await resources.EnterCpuAsync(work, requestToken))
                                    footer = ArchiveIndexParser.AuthenticateFooter(tail.Bytes, key, tail.Total, new(), requestToken);
                                var tocBytes = footer.TocBytes == 0 ? [] :
                                    (await Read(url, footer.IndexBytes, footer.TocOffset, footer.TocBytes, work, requestToken)).Bytes;
                                ArchiveIndexToc toc;
                                using (resources is null ? null : await resources.EnterCpuAsync(work, requestToken))
                                    toc = ArchiveIndexParser.AuthenticateToc(footer, tocBytes, requestToken);
                                return (new(footer, toc, tail.Bytes, tocBytes), 256L + tail.Bytes.Length + tocBytes.Length * 2L);
                            }, search.Token);
                        var page = directory.Toc.FindPage(encodingKey, search.Token);
                        if (page is not null)
                        {
                            var pageBytes = await cache.GetAsync<byte[]>($"{scope}|{url.AbsoluteUri}|{directory.Footer.IndexBytes}|page:{page.Ordinal}",
                                transport.WorkContext, async (work, requestToken) =>
                                {
                                    var response = await Read(url, directory.Footer.IndexBytes, page.Offset, page.Length, work, requestToken);
                                    using (resources is null ? null : await resources.EnterCpuAsync(work, requestToken))
                                        _ = ArchiveIndexParser.Lookup(directory.Toc, page, response.Bytes, encodingKey, requestToken);
                                    return (response.Bytes, response.Bytes.LongLength + 128);
                                }, search.Token);
                            ArchiveIndexMatch? match;
                            using (resources is null ? null : await resources.EnterCpuAsync(transport.WorkContext, search.Token))
                                match = ArchiveIndexParser.Lookup(directory.Toc, page, pageBytes, encodingKey, search.Token);
                            if (match is not null)
                            {
                                if (match.EncodedBytes != encodedBytes)
                                    throw Error("CONTENT_SIZE_CONFLICT", "An authenticated archive entry differs from its encoding metadata length.");
                                lock (gate)
                                {
                                    if (result is null)
                                    {
                                        result = new(key, match.ArchiveOffset, match.EncodedBytes);
                                        resultProof = new(directory.Footer.IndexBytes, directory.FooterBytes,
                                            directory.TocBytes, page.Ordinal, pageBytes);
                                    }
                                }
                                search.Cancel(); return;
                            }
                        }
                    }
                    catch (DiscoveryException failure) when (failure.Code == "CONTENT_SOURCE_MISSING")
                    { Interlocked.Increment(ref unavailable); }
                    var count = Interlocked.Increment(ref completed);
                    if (count % 16 == 0 || count == archives.Count)
                    {
                        await reporting.WaitAsync(search.Token);
                        try { await progress(InventoryProgress.Format("Locating selected file in archives", count, archives.Count, "archives")); }
                        finally { reporting.Release(); }
                    }
                }
            }
            catch { search.Cancel(); throw; }
        }
        try { await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Worker())); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && result is not null) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Error("CONTENT_LOCATOR_LIMIT", "Archive lookup stopped at its bounded time or work limit."); }
        token.ThrowIfCancellationRequested();
        if (result is null || resultProof is null)
            throw Error(unavailable == 0 ? "CONTENT_NOT_LOCATED" : "CONTENT_LOCATOR_INCOMPLETE",
                unavailable == 0 ? "The selected exact object was not located in this supported archive list." : "Some archive indexes were unavailable; location remains unresolved.");
        await progress("Saving authenticated archive locator");
        string? path = null;
        try
        {
            long length;
            await using (var output = await objects.CreateStagingFileAsync(token))
            {
                path = output.Name; await JsonSerializer.SerializeAsync(output, resultProof, cancellationToken: token);
                length = output.Length;
                if (length > MaxProofBytes) throw Error("CONTENT_LOCATOR_LIMIT", "An archive proof exceeds its retained artifact bound.");
                await output.FlushAsync(token);
            }
            var sha = resources is null
                ? await objects.PublishFileAsync(path, length, token)
                : await objects.PublishFileAsync(path, length, resources, transport.WorkContext, token);
            await catalog.RecordContentLocatorAsync(new(encodingKey, cdnSha256, result.ArchiveKey, sha, length), token);
        }
        finally { if (path is not null && File.Exists(path)) File.Delete(path); }
        return result;
    }

    private static ContentArchiveLocation Validate(string archiveKey, string encodingKey, long encodedBytes,
        LocatorProof proof, CancellationToken token)
    {
        if (proof.Footer is null || proof.Toc is null || proof.Page is null || proof.Toc.Length > 4 << 20)
            throw Error("CONTENT_LOCATOR_INTEGRITY", "A saved archive proof has invalid bounded fields.");
        var footer = ArchiveIndexParser.AuthenticateFooter(proof.Footer, archiveKey, proof.IndexBytes, new(), token);
        var toc = ArchiveIndexParser.AuthenticateToc(footer, proof.Toc, token);
        var page = toc.FindPage(encodingKey, token);
        if (page is null || page.Ordinal != proof.PageOrdinal)
            throw Error("CONTENT_LOCATOR_INTEGRITY", "A saved archive page does not identify the selected encoding key.");
        var match = ArchiveIndexParser.Lookup(toc, page, proof.Page, encodingKey, token);
        if (match is null || match.EncodedBytes != encodedBytes)
            throw Error("CONTENT_LOCATOR_INTEGRITY", "A saved archive proof no longer matches the exact selected representation.");
        return new(archiveKey, match.ArchiveOffset, match.EncodedBytes);
    }
    internal static Uri Url(Uri route, string key, bool index = false) =>
        new(route, $"data/{key[..2]}/{key[2..4]}/{key}" + (index ? ".index" : ""));
    private static DiscoveryException Error(string code, string message) => new(code, message);
}
