using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using VibeMiner.Protocols.Tact.Archives;

namespace VibeMiner.Infrastructure;

internal sealed record ContentLocatorArtifact(string EncodingKey, string CdnConfigurationSha256,
    string ArchiveKey, string Sha256, long ByteLength);
/// <summary>CdnConfig is set when an earlier CDN configuration of the build, not the searched one, listed the archive.</summary>
internal sealed record ContentArchiveLocation(string ArchiveKey, long Offset, long EncodedBytes, string? CdnConfig = null);
/// <summary>
/// The build and CDN configuration a lookup belongs to. When the object is not in that configuration's archives,
/// archives listed only by earlier CDN configurations of the same build are searched too, newest first.
/// CurrentSearched skips the per-object search of the current archives because a sealed map already covered
/// them; CurrentIncomplete says that coverage could not read every candidate.
/// </summary>
internal sealed record EarlierArchiveScope(string BuildId, string CdnConfig, bool CurrentSearched = false,
    bool CurrentIncomplete = false);

/// <summary>Authenticated bounded archive lookup, with immutable successful proofs rather than a global EKey index.</summary>
internal sealed class ArchivedContentLocator(DiscoveryCatalog catalog, VerifiedObjectStore objects,
    OfficialSourceTransport transport, ArchiveProofCache? sharedCache = null, string? sourceScope = null,
    AcquisitionResources? resources = null)
{
    private sealed record DirectoryProof(ArchiveIndexFooter Footer, ArchiveIndexToc Toc, byte[] FooterBytes, byte[] TocBytes);
    private sealed record LocatorProof(long IndexBytes, byte[] Footer, byte[] Toc, int PageOrdinal, byte[] Page);
    private sealed record EarlierArchives(string CdnConfig, string Sha256, IReadOnlyList<string> ArchiveKeys);
    private sealed record SearchResult(ContentArchiveLocation? Location, LocatorProof? Proof, int Unavailable);
    // One request and wire budget per lookup, including every earlier configuration it falls back to.
    private sealed class Budget { internal int Requests; internal long Wire; }
    private readonly ArchiveProofCache cache = sharedCache ?? new();
    private const long MaxProofBytes = 8L << 20;
    internal const int MaxEarlierArchives = 4096;

    internal async Task<ContentArchiveLocation> LocateAsync(Uri route, TactConfiguration cdn,
        string cdnSha256, string encodingKey, long encodedBytes, Func<string, Task> progress, CancellationToken token,
        EarlierArchiveScope? earlier = null)
    {
        var archives = Archives(cdn)
            ?? throw Error("CONTENT_LOCATOR_UNSUPPORTED", "The pinned CDN configuration has no bounded supported archive list.");
        var budget = new Budget();
        var unavailable = 0;
        if (earlier is not { CurrentSearched: true })
        {
            var saved = await catalog.FindContentLocatorAsync(encodingKey, cdnSha256, token);
            if (saved is not null)
            {
                if (!archives.Contains(saved.ArchiveKey, StringComparer.Ordinal))
                    throw Error("CONTENT_LOCATOR_INTEGRITY", "A saved locator differs from its pinned CDN configuration.");
                return await ReadSavedAsync(saved, encodingKey, encodedBytes, progress, token);
            }
            var found = await SearchAsync(route, archives, cdnSha256, encodingKey, encodedBytes,
                "Locating selected file in archives", progress, budget, token);
            if (found.Location is not null)
            {
                await SaveAsync(encodingKey, cdnSha256, found, progress, token);
                return found.Location;
            }
            if (earlier is null) throw NotLocated(found.Unavailable, false);
            unavailable = found.Unavailable;
        }
        else if (earlier.CurrentIncomplete) unavailable = 1;
        return await LocateEarlierAsync(route, archives, earlier, encodingKey, encodedBytes, unavailable, progress, budget, token);
    }

    private async Task<ContentArchiveLocation> LocateEarlierAsync(Uri route, IReadOnlyList<string> current,
        EarlierArchiveScope earlier, string encodingKey, long encodedBytes, int unavailable, Func<string, Task> progress,
        Budget budget, CancellationToken token)
    {
        var configs = await catalog.ReadEarlierCdnConfigsAsync(earlier.BuildId, earlier.CdnConfig, token);
        var searched = new HashSet<string>(current, StringComparer.Ordinal);
        var added = 0;
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            for (var index = 0; index < configs.Count; index++)
            {
                phase.Token.ThrowIfCancellationRequested();
                await progress(InventoryProgress.Format("Checking earlier CDN configurations", index, configs.Count, "configurations"));
                EarlierArchives source;
                // An earlier configuration the CDN does not serve intact (missing, wrong MD5, bad grammar, over a
                // limit, no supported archive list, network) leaves the lookup unresolved, not absent, and never
                // stops the remaining ones. Local storage and catalog failures, cancellation, the lookup budget
                // and shared capacity are not unavailability: they propagate and are reported.
                try { source = await LoadEarlierAsync(route, configs[index], budget, phase.Token); }
                catch (DiscoveryException failure) when (failure.Data[CdnSideFailure] is true)
                { unavailable++; continue; }
                var saved = await catalog.FindContentLocatorAsync(encodingKey, source.Sha256, phase.Token);
                if (saved is not null)
                {
                    if (!source.ArchiveKeys.Contains(saved.ArchiveKey, StringComparer.Ordinal))
                        throw Error("CONTENT_LOCATOR_INTEGRITY", "A saved locator differs from its earlier CDN configuration.");
                    return (await ReadSavedAsync(saved, encodingKey, encodedBytes, progress, phase.Token)) with { CdnConfig = source.CdnConfig };
                }
                var only = source.ArchiveKeys.Where(searched.Add).ToArray();
                if (only.Length == 0) continue;
                if ((added += only.Length) > MaxEarlierArchives)
                    throw Error("CONTENT_LOCATOR_LIMIT", "Earlier CDN configurations list more archives than one lookup may search.");
                var found = await SearchAsync(route, only, source.Sha256, encodingKey, encodedBytes,
                    "Locating selected file in earlier CDN configuration archives", progress, budget, phase.Token);
                if (found.Location is not null)
                {
                    await SaveAsync(encodingKey, source.Sha256, found, progress, phase.Token);
                    return found.Location with { CdnConfig = source.CdnConfig };
                }
                unavailable += found.Unavailable;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Error("CONTENT_LOCATOR_LIMIT", "Earlier CDN configuration lookup stopped at its bounded time limit."); }
        throw NotLocated(unavailable, true);
    }

    /// <summary>
    /// An earlier CDN configuration, fetched only when a lookup needs it. Like archive index proofs it is shared
    /// and coalesced through the proof cache, and retained like any configuration: raw MD5 and grammar are checked
    /// before publication, and a saved copy is reverified on every load.
    /// </summary>
    private Task<EarlierArchives> LoadEarlierAsync(Uri route, string key, Budget budget, CancellationToken token)
    {
        var url = new Uri(route, $"config/{key[..2]}/{key[2..4]}/{key}");
        OfficialSourceTransport.ValidateConfigurationUri(url);
        return cache.GetAsync<EarlierArchives>($"{sourceScope ?? "unscoped"}|{url.AbsoluteUri}|cdn-config", transport.WorkContext,
            async (work, requestToken) =>
            {
                var saved = await catalog.FindConfigurationAsync(key, requestToken);
                byte[] bytes; var retrievedAt = "";
                if (saved is not null)
                {
                    ConfigurationWorkspacePreparer.ValidateReceipt(saved);
                    bytes = await objects.ReadAsync(saved.Sha256, saved.ByteLength, requestToken);
                }
                else
                {
                    if (Interlocked.Increment(ref budget.Requests) > 16_384)
                        throw Error("CONTENT_LOCATOR_LIMIT", "Archive lookup exceeded its bounded request or wire budget.");
                    bytes = await FromCdn(() => transport.WithContext(work).ReadConfigurationAsync(url, requestToken));
                    if (Interlocked.Add(ref budget.Wire, bytes.Length) > 128L << 20)
                        throw Error("CONTENT_LOCATOR_LIMIT", "Archive lookup exceeded its bounded request or wire budget.");
                    retrievedAt = DateTimeOffset.UtcNow.ToString("O");
                }
                IReadOnlyList<string> archives;
                using (resources is null ? null : await resources.EnterCpuAsync(work, requestToken))
                {
                    try { archives = Interpret(bytes, key, requestToken); }
                    // A saved copy was verified before it was retained, so failing now is local corruption.
                    catch (DiscoveryException failure) when (saved is not null)
                    { throw Error("LOCAL_CONFIGURATION_CORRUPT", "A saved earlier CDN configuration no longer verifies: " + failure.Message); }
                    catch (DiscoveryException failure) { failure.Data[CdnSideFailure] = true; throw; }
                }
                var sha = saved?.Sha256 ?? Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (saved is null)
                {
                    // CAS first, then the catalog row; either can be retried on its own.
                    await objects.PublishAsync(bytes, sha, requestToken);
                    await catalog.RecordConfigurationAsync(new(key, sha, bytes.Length, url.AbsoluteUri, retrievedAt), requestToken);
                }
                return (new EarlierArchives(key, sha, archives), 256L + archives.Count * 40L);
            }, token);
    }

    // Marks a failure of the bytes the CDN served for an earlier configuration, as opposed to local storage.
    private const string CdnSideFailure = "VibeMiner.EarlierCdnConfigSourceFailure";
    private static async Task<T> FromCdn<T>(Func<Task<T>> fetch)
    {
        try { return await fetch(); }
        catch (DiscoveryException failure) { failure.Data[CdnSideFailure] = true; throw; }
    }
    private static IReadOnlyList<string> Interpret(byte[] bytes, string key, CancellationToken token)
    {
        if (Convert.ToHexStringLower(MD5.HashData(bytes)) != key)
            throw Error("CONFIG_INTEGRITY", "An earlier CDN configuration differs from its advertised raw MD5.");
        var parsed = TactConfigurationParser.Parse(bytes, token);
        if (parsed.Fields.Count > 4096) throw Error("CONFIG_LIMIT", "The configuration has too many fields.");
        return Archives(parsed)
            ?? throw Error("CONTENT_LOCATOR_UNSUPPORTED", "An earlier CDN configuration has no bounded supported archive list.");
    }

    private async Task<SearchResult> SearchAsync(Uri route, IReadOnlyList<string> archives, string cdnSha256,
        string encodingKey, long encodedBytes, string stage, Func<string, Task> progress, Budget budget, CancellationToken token)
    {
        using var search = CancellationTokenSource.CreateLinkedTokenSource(token);
        search.CancelAfter(TimeSpan.FromMinutes(5));
        var gate = new object(); var next = -1; var completed = 0;
        var unavailable = 0;
        ContentArchiveLocation? result = null; LocatorProof? resultProof = null;
        using var reporting = new SemaphoreSlim(1, 1);
        await progress(InventoryProgress.Format(stage, 0, archives.Count, "archives"));

        async Task<(byte[] Bytes, long Total)> Read(Uri url, long? total, long? offset, int length,
            VibeMiner.Application.IAcquisitionWorkContext work, CancellationToken requestToken)
        {
            if (Interlocked.Increment(ref budget.Requests) > 16_384 || Interlocked.Add(ref budget.Wire, length) > 128L << 20)
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
                        try { await progress(InventoryProgress.Format(stage, count, archives.Count, "archives")); }
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
        return new(result, resultProof, unavailable);
    }

    private async Task<ContentArchiveLocation> ReadSavedAsync(ContentLocatorArtifact saved, string encodingKey,
        long encodedBytes, Func<string, Task> progress, CancellationToken token)
    {
        if (saved.ByteLength is < 1 or > MaxProofBytes)
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

    private async Task SaveAsync(string encodingKey, string cdnSha256, SearchResult found,
        Func<string, Task> progress, CancellationToken token)
    {
        await progress("Saving authenticated archive locator");
        string? path = null;
        try
        {
            long length;
            await using (var output = await objects.CreateStagingFileAsync(token))
            {
                path = output.Name; await JsonSerializer.SerializeAsync(output, found.Proof!, cancellationToken: token);
                length = output.Length;
                if (length > MaxProofBytes) throw Error("CONTENT_LOCATOR_LIMIT", "An archive proof exceeds its retained artifact bound.");
                await output.FlushAsync(token);
            }
            var sha = resources is null
                ? await objects.PublishFileAsync(path, length, token)
                : await objects.PublishFileAsync(path, length, resources, transport.WorkContext, token);
            await catalog.RecordContentLocatorAsync(new(encodingKey, cdnSha256, found.Location!.ArchiveKey, sha, length), token);
        }
        finally { if (path is not null && File.Exists(path)) File.Delete(path); }
    }

    private static IReadOnlyList<string>? Archives(TactConfiguration cdn) =>
        !cdn.Fields.TryGetValue("archives", out var archives) || archives.Count > 4096 ||
        archives.Any(key => key.Length != 32 || key.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) ||
        archives.Distinct(StringComparer.Ordinal).Count() != archives.Count ? null : archives;
    private static DiscoveryException NotLocated(int unavailable, bool earlier) => unavailable == 0
        ? Error("CONTENT_NOT_LOCATED", earlier
            ? "The selected exact object is not in this CDN configuration's archives or in archives listed only by its earlier ones."
            : "The selected exact object was not located in this supported archive list.")
        : Error("CONTENT_LOCATOR_INCOMPLETE", earlier
            ? "Some archive indexes or earlier CDN configurations were unavailable; location remains unresolved."
            : "Some archive indexes were unavailable; location remains unresolved.");

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
