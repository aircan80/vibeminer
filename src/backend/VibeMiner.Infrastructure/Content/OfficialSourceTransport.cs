using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using VibeMiner.Domain;
using VibeMiner.Application;

namespace VibeMiner.Infrastructure;

/// <summary>Shared strict official-source byte transport; parsing, source authority and publication stay with callers.</summary>
internal sealed class OfficialSourceTransport(HttpClient client, AcquisitionResources? resources = null,
    IAcquisitionWorkContext? context = null)
{
    internal IAcquisitionWorkContext? WorkContext => context;
    internal OfficialSourceTransport WithContext(IAcquisitionWorkContext work) => new(client, resources, work);
    internal async Task DownloadLooseAsync(Uri url, Stream destination, long expectedBytes, string label,
        Func<string, Task> progress, CancellationToken token, bool metadata = false, Func<long, Task>? transferred = null)
    {
        ValidateUri(url);
        var prefix = metadata ? "METADATA" : "CONTENT";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var permit = resources is null ? null : await resources.EnterHttpAsync(context, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            ValidateSource(response, url);
            if (response.StatusCode != HttpStatusCode.OK)
                throw Error(!metadata && response.StatusCode == HttpStatusCode.NotFound ? "CONTENT_LOOSE_MISSING" : prefix + "_UNAVAILABLE",
                    $"The exact official loose source returned HTTP {(int)response.StatusCode}.", true);
            if (response.Content.Headers.ContentRange is not null || response.Content.Headers.ContentEncoding.Count != 0)
                throw Error(prefix + "_RESPONSE", "Wrapped or ranged loose responses are unsupported.");
            if (response.Content.Headers.ContentLength is { } declared && declared != expectedBytes)
                throw Error(prefix + "_INTEGRITY", "The source length differs from its exact expectation.");
            ValidateMedia(response, prefix);
            await CopyExactAsync(response, destination, expectedBytes,
                metadata ? $"Downloading {label} metadata" : "Downloading selected file", prefix, progress, timeout.Token, transferred);
            await destination.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Error(prefix + "_TIMEOUT", "The official source exceeded its transfer time budget.", true); }
        catch (HttpRequestException) { throw Error("NETWORK_UNAVAILABLE", "The official source could not be reached.", true); }
    }

    internal async Task<(byte[] Bytes, long Total)> ReadRangeAsync(Uri url, long? expectedTotal,
        long? offset, int length, CancellationToken token)
    {
        if (length is < 1 or > 4 * 1024 * 1024) throw Error("CONTENT_LIMIT", "An index read exceeds its bounded allocation.");
        using var destination = new MemoryStream(length);
        var total = await DownloadRangeAsync(url, destination, expectedTotal, offset, length, _ => Task.CompletedTask, token);
        return (destination.ToArray(), total);
    }

    internal async Task<long> DownloadRangeAsync(Uri url, Stream destination, long? expectedTotal,
        long? offset, long length, Func<string, Task> progress, CancellationToken token, Func<long, Task>? transferred = null)
    {
        ValidateUri(url);
        if (length < 1 || length > VerifiedObjectStore.MaxObjectBytes || offset is < 0 || expectedTotal is < 1)
            throw Error("CONTENT_LIMIT", "A source range exceeds its admitted bounds.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var permit = resources is null ? null : await resources.EnterHttpAsync(context, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = offset is { } at ? new RangeHeaderValue(at, checked(at + length - 1)) : new RangeHeaderValue(null, length);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            ValidateSource(response, url);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw Error("CONTENT_SOURCE_MISSING", "An official archive or index source is unavailable.", true);
            if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                throw Error("CONTENT_UNAVAILABLE", $"The official range source returned HTTP {(int)response.StatusCode}.", true);
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                range.Length is not > 0 || range.Length > 16L * 1024 * 1024 * 1024 ||
                range.From is null || range.To is null || range.To >= range.Length ||
                range.To - range.From + 1 != length || range.From != (offset ?? range.Length - length) ||
                expectedTotal is { } expected && range.Length != expected ||
                response.Content.Headers.ContentLength is { } declared && declared != length ||
                response.Content.Headers.ContentEncoding.Count != 0)
                throw Error("CONTENT_RANGE_INTEGRITY", "The official range response differs from the exact requested source span.");
            ValidateMedia(response, "CONTENT");
            await CopyExactAsync(response, destination, length, "Downloading selected file", "CONTENT", progress, timeout.Token, transferred);
            await destination.FlushAsync(timeout.Token);
            return range.Length.Value;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Error("CONTENT_TIMEOUT", "The official range exceeded its transfer time budget.", true); }
        catch (HttpRequestException) { throw Error("NETWORK_UNAVAILABLE", "The official range source could not be reached.", true); }
    }

    /// <summary>
    /// One exact official configuration document, at most 1 MiB, under this transport's priority. A 404 is
    /// CONTENT_SOURCE_MISSING (unavailable); the caller verifies raw MD5 and grammar before using any byte.
    /// </summary>
    internal async Task<byte[]> ReadConfigurationAsync(Uri url, CancellationToken token)
    {
        ValidateConfigurationUri(url);
        const int maximum = 1024 * 1024;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var permit = resources is null ? null : await resources.EnterHttpAsync(context, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            ValidateSource(response, url);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw Error("CONTENT_SOURCE_MISSING", "An earlier CDN configuration is unavailable on the official source.", true);
            if (response.StatusCode != HttpStatusCode.OK)
                throw Error("CONTENT_UNAVAILABLE", $"The official configuration source returned HTTP {(int)response.StatusCode}.", true);
            if (response.Content.Headers.ContentRange is not null || response.Content.Headers.ContentEncoding.Count != 0)
                throw Error("CONFIG_RESPONSE", "Wrapped or ranged configuration responses are unsupported.");
            if (response.Content.Headers.ContentLength is > maximum or < 1)
                throw Error("CONFIG_LIMIT", "The configuration exceeds its transfer limit.");
            ValidateMedia(response, "CONFIG");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[65_536];
            while (true)
            {
                int count;
                try { count = await input.ReadAsync(buffer, timeout.Token); }
                catch (IOException) { throw Error("NETWORK_IO", "The official configuration response could not be read.", true); }
                if (count == 0) break;
                if (body.Length + count > maximum) throw Error("CONFIG_LIMIT", "The configuration exceeds its transfer limit.");
                await body.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
            if (body.Length == 0 || response.Content.Headers.ContentLength is { } declared && declared != body.Length)
                throw Error("CONFIG_INTEGRITY", "The configuration response length is invalid.");
            return body.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Error("CONTENT_TIMEOUT", "The official configuration request timed out.", true); }
        catch (HttpRequestException) { throw Error("NETWORK_UNAVAILABLE", "The official configuration source could not be reached.", true); }
    }

    private static async Task CopyExactAsync(HttpResponseMessage response, Stream destination, long expected,
        string stage, string prefix, Func<string, Task> progress, CancellationToken token, Func<long, Task>? transferred)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[65_536]; long received = 0, reported = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            int count;
            try { count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - received + 1)), idle.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw Error(prefix + "_TIMEOUT", "The official source stopped providing bytes.", true); }
            catch (IOException) { throw Error("NETWORK_IO", "The official response could not be read.", true); }
            if (count == 0) break;
            if (transferred is not null) await transferred(count);
            if (count > expected - received) throw Error(prefix + "_INTEGRITY", "The official source exceeds its exact length.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token); received += count;
            if (received - reported >= 1024 * 1024 || received == expected)
            { await progress(InventoryProgress.Format(stage, received, expected, "bytes")); reported = received; }
        }
        if (received != expected) throw Error(prefix + "_INTEGRITY", "The official source ended before its exact length.");
    }

    internal static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" ||
                Regex.IsMatch(uri.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant)) ||
            !Regex.IsMatch(uri.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/data/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{32}(?:\\.index)?$", RegexOptions.CultureInvariant))
            throw Error("SOURCE_UNSUPPORTED", "Only an exact admitted official CDN object source can be requested.");
    }
    internal static void ValidateConfigurationUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" ||
                Regex.IsMatch(uri.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant)) ||
            !Regex.IsMatch(uri.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/config/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{32}$", RegexOptions.CultureInvariant))
            throw Error("SOURCE_UNSUPPORTED", "Only an exact admitted official CDN configuration source can be requested.");
    }
    private static void ValidateSource(HttpResponseMessage response, Uri expected)
    {
        if (response.RequestMessage?.RequestUri != expected || (int)response.StatusCode is >= 300 and < 400)
            throw Error("SOURCE_REDIRECT", "Official source redirects are unsupported.");
    }
    private static void ValidateMedia(HttpResponseMessage response, string prefix)
    {
        var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (media is not null && media is not ("text/plain" or "application/binary" or "application/octet-stream" or "binary/octet-stream"))
            throw Error(prefix + "_CONTENT_TYPE", "The official response media type is unsupported.");
    }
    private static DiscoveryException Error(string code, string message, bool retryable = false) => new(code, message, retryable);
}
