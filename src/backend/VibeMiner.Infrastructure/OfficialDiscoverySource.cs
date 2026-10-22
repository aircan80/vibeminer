using System.Net;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Own bounded async metadata acquisition; never follows advertised payload routes.</summary>
public sealed class OfficialDiscoverySource(HttpClient client, TimeProvider? timeProvider = null, AcquisitionResources? resources = null) : IDiscoverySource, IDisposable
{
    private readonly SemaphoreSlim network = new(4, 4);
    private readonly SemaphoreSlim cpu = new(2, 2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken cancellationToken)
    {
        ProductRegistry.Validate(product);
        var versionsTask = DownloadAsync(product, "versions", cancellationToken);
        var cdnsTask = DownloadAsync(product, "cdns", cancellationToken);
        var pair = await Task.WhenAll(versionsTask, cdnsTask);
        cancellationToken.ThrowIfCancellationRequested();
        using var permit = resources is null ? null : await resources.EnterCpuAsync(null, cancellationToken);
        await cpu.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Interpret(product, pair[0], pair[1], cancellationToken), cancellationToken);
        }
        finally { cpu.Release(); }
    }

    private sealed record Download(SourceDocument? Document, DiscoveryProblem? Problem);
    private async Task<Download> DownloadAsync(string product, string role, CancellationToken cancellationToken)
    {
        await network.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var url = $"https://us.version.battle.net/v2/products/{product}/{role}";
            using var permit = resources is null ? null : await resources.EnterHttpAsync(null, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("VibeMiner/0.1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.RequestMessage?.RequestUri?.AbsoluteUri != url)
                return new(null, Problem("SOURCE_REDIRECT", "Official metadata redirected to an unsupported source."));
            if (!response.IsSuccessStatusCode)
                return new(null, Problem("HTTP_STATUS", "Official metadata returned an unsuccessful HTTP status."));
            if (response.Content.Headers.ContentLength > MetadataParser.MaxBytes)
                return new(null, Problem("METADATA_LIMIT", "Official metadata exceeds the document limit."));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && media is not ("text/plain" or "application/octet-stream"))
                return new(null, Problem("CONTENT_TYPE", "Official metadata has an unsupported content type."));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, timeout.Token);
                if (count == 0) break;
                if (buffer.Length + count > MetadataParser.MaxBytes)
                    return new(null, Problem("METADATA_LIMIT", "Official metadata exceeds the document limit."));
                await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token);
            }
            var bytes = buffer.ToArray();
            if (response.Content.Headers.ContentLength is long expectedLength && expectedLength != bytes.Length)
                return new(null, Problem("CONTENT_LENGTH", "Official metadata ended before its declared response length."));
            return new(new(role, url, clock.GetUtcNow().ToString("O"),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(null, Problem("TIMEOUT", "The official metadata request timed out.")); }
        catch (HttpRequestException)
        { return new(null, Problem("NETWORK_UNAVAILABLE", "Official metadata could not be reached.")); }
        catch (IOException)
        { return new(null, Problem("NETWORK_IO", "The official metadata response could not be read.")); }
        finally { network.Release(); }
    }
    /// <summary>Worded to read for one region or a grouped list of them.</summary>
    public const string MissingRouteMessage = "Advertised without a CDN route, so not usable as a download source.";
    private static DiscoveryProblem Problem(string code, string message) => new(null, code, message);
    private static MetadataDocument? Parse(Download download, List<DiscoveryProblem> problems, CancellationToken token)
    {
        if (download.Problem is not null) problems.Add(download.Problem);
        if (download.Document is null) return null;
        try { return MetadataParser.Parse(download.Document.Bytes, token); }
        catch (DiscoveryException ex) { problems.Add(Problem(ex.Code, ex.Message)); return null; }
    }
    private static DiscoveryAttempt Interpret(string product, Download versionsDownload, Download cdnsDownload, CancellationToken token)
    {
        var errors = new List<DiscoveryProblem>();
        var documents = new[] { versionsDownload.Document, cdnsDownload.Document }.OfType<SourceDocument>().ToArray();
        var versions = Parse(versionsDownload, errors, token);
        var cdns = Parse(cdnsDownload, errors, token);
        if (versions is not null && !new[] { "Region", "BuildConfig", "CDNConfig", "VersionsName" }
            .All(name => versions.Columns.Any(column => column.Name == name)))
        { errors.Add(Problem("INVALID_METADATA", "Versions metadata is missing required columns.")); versions = null; }
        if (cdns is not null && !new[] { "Name", "Path", "Hosts" }.All(name => cdns.Columns.Any(column => column.Name == name)))
        { errors.Add(Problem("INVALID_METADATA", "CDN metadata is missing required columns.")); cdns = null; }
        if (versions is null)
            return new(documents, [], [], [], errors, documents.Length == 0 && errors.All(error => error.Code is "NETWORK_UNAVAILABLE" or "NETWORK_IO" or "TIMEOUT") ? "offline" : "partial");
        var regions = versions.Rows.Select(row => row.Fields["Region"]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (regions.Length > 64 || regions.Any(region => !BuildIdentity.IsValidRegion(region)))
        {
            errors.Add(Problem("METADATA_LIMIT", "Versions metadata has invalid or excessive regional scope."));
            return new(documents, [], [], [], errors, "partial");
        }
        var observations = new List<BuildObservation>();
        var successful = new List<string>();
        foreach (var region in regions)
        {
            token.ThrowIfCancellationRequested();
            var rows = versions.Rows.Where(row => row.Fields["Region"] == region).ToArray();
            if (rows.Length > 64)
            { errors.Add(new(region, "METADATA_LIMIT", "A region has too many advertised builds.")); continue; }
            var regional = new List<BuildObservation>();
            var routes = cdns?.Rows.Where(row => row.Fields["Name"] == region).ToArray() ?? [];
            MetadataRow? route = null;
            if (cdns is null) errors.Add(new(region, "ROUTE_UNAVAILABLE", "CDN route metadata could not be checked."));
            // Blizzard advertises some regions (wow, wowt and wow_beta: sg, xx) with no cdns row at all. That is a fact
            // about the source, not a failed check, so it is a regional note (D076). Ambiguous or unusable routes stay problems.
            else if (routes.Length == 0) errors.Add(DiscoveryProblem.RegionNote(region, "ROUTE_MISSING", MissingRouteMessage));
            else if (routes.Length != 1) errors.Add(new(region, "ROUTE_AMBIGUOUS", "This region has multiple matching CDN routes."));
            else if (routes[0].Fields["Path"].Length == 0 || string.IsNullOrWhiteSpace(routes[0].Fields["Hosts"]))
                errors.Add(new(region, "ROUTE_INVALID", "This region has no usable CDN path and hosts."));
            else route = routes[0];
            try
            {
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    regional.Add(new(BuildIdentity.Create(product, row.Fields), region, row.Ordinal,
                        route?.Ordinal, row.Fields, route?.Fields));
                }
                observations.AddRange(regional);
                successful.Add(region);
            }
            catch (DiscoveryException ex) { errors.Add(new(region, ex.Code, ex.Message)); }
        }
        return new(documents, observations, successful, regions, errors, DiscoveryProblem.CheckedConnection(errors));
    }
    public void Dispose() { network.Dispose(); cpu.Dispose(); }
}
