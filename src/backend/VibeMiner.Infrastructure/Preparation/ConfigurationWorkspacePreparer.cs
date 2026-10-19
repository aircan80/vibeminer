using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

public sealed class ConfigurationWorkspacePreparer(IWorkspaceCatalog catalog, VerifiedObjectStore objects,
    HttpClient client, TimeProvider? timeProvider = null, AcquisitionResources? resources = null) : IWorkspacePreparer, IDisposable
{
    public const string Profile = "home-configurations-v1";
    public const string VerifierRevision = "configuration-raw-md5-sha256-grammar-v1";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim cpu = new(1, 1);
    private const int MaxBytes = 1024 * 1024;
    public async Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request, Func<string, Task> progress, CancellationToken token)
    {
        var inputs = await PrepareInputsAsync(request, progress, token);
        await progress("Finalizing workspace");
        return await catalog.CommitWorkspaceAsync(inputs.Context, inputs.Configurations, clock.GetUtcNow().ToString("O"), token);
    }
    internal sealed record ConfigurationInputs(CapturedOpeningContext Context, Uri Route,
        IReadOnlyList<ConfigurationReceipt> Configurations, TactConfiguration BuildConfiguration);
    internal async Task<ConfigurationInputs> PrepareInputsAsync(WorkspaceRequest request, Func<string, Task> progress, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await progress("Checking saved configurations");
            var context = await catalog.ResolveOpeningAsync(request, lifetime.Token);
            var route = await ParseAsync(() => ResolveRoute(context), lifetime.Token);
            var admitted = await catalog.HasAdmissionAsync(context, lifetime.Token);
            var configurations = new List<ConfigurationReceipt>(2);
            TactConfiguration? buildConfiguration = null;
            foreach (var (role, key) in new[] { ("buildConfig", context.Build.BuildConfig), ("cdnConfig", context.Build.CdnConfig) })
            {
                var saved = await catalog.FindConfigurationAsync(key, lifetime.Token);
                byte[] bytes;
                if (saved is not null)
                {
                    ValidateReceipt(saved);
                    await progress(role == "buildConfig" ? "Verifying saved build configuration" : "Verifying saved CDN configuration");
                    bytes = await objects.ReadAsync(saved.Sha256, saved.ByteLength, lifetime.Token);
                }
                else
                {
                    if (admitted) throw new DiscoveryException("LOCAL_CONFIGURATION_MISSING", "A required saved configuration proof is missing. No automatic repair was attempted.");
                    await progress(role == "buildConfig" ? "Downloading build configuration" : "Downloading CDN configuration");
                    var acquired = await DownloadAsync(route, key, lifetime.Token);
                    bytes = acquired.Bytes;
                    // Both raw hash and complete text grammar are checked before publishing any bytes.
                    await ParseAsync(() => ParseConfiguration(bytes, key, lifetime.Token), lifetime.Token);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    await objects.PublishAsync(bytes, hash, lifetime.Token);
                    saved = new(key, hash, bytes.Length, acquired.Url, acquired.RetrievedAt);
                    await catalog.RecordConfigurationAsync(saved, lifetime.Token);
                }
                var fields = await ParseAsync(() => ParseConfiguration(bytes, key, lifetime.Token), lifetime.Token);
                if (role == "buildConfig") buildConfiguration = await ParseAsync(() => TactConfigurationParser.Parse(bytes, lifetime.Token), lifetime.Token);
                configurations.Add(new(role, key, saved.Sha256, saved.ByteLength, saved.SourceUrl, saved.RetrievedAt,
                    clock.GetUtcNow().ToString("O"), fields));
            }
            return new(context, route, configurations, buildConfiguration!);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new DiscoveryException("OPEN_TIMEOUT", "Opening exceeded its bounded configuration preparation time.", true); }
        catch (UnauthorizedAccessException)
        { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Configuration storage could not be accessed.", true); }
        catch (IOException)
        { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Configuration storage could not complete this operation.", true); }
    }
    private async Task<T> ParseAsync<T>(Func<T> operation, CancellationToken token)
    {
        if (resources is not null)
        {
            using var permit = await resources.EnterCpuAsync(null, token);
            return await Task.Run(operation, token);
        }
        await cpu.WaitAsync(token);
        try { return await Task.Run(operation, token); }
        finally { cpu.Release(); }
    }
    private static int ParseConfiguration(byte[] bytes, string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Convert.ToHexStringLower(MD5.HashData(bytes)) != key)
            throw new DiscoveryException("CONFIG_INTEGRITY", "Configuration bytes do not match the exact advertised raw MD5.");
        var parsed = TactConfigurationParser.Parse(bytes, token);
        if (parsed.Fields.Count > 4096) throw new DiscoveryException("CONFIG_LIMIT", "The configuration has too many fields.");
        return parsed.Fields.Count;
    }
    private sealed record Acquired(byte[] Bytes, string Url, string RetrievedAt);
    private async Task<Acquired> DownloadAsync(Uri origin, string key, CancellationToken token)
    {
        var url = new Uri(origin, $"config/{key[..2]}/{key[2..4]}/{key}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var permit = resources is null ? null : await resources.EnterHttpAsync(null, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.RequestMessage?.RequestUri != url || (int)response.StatusCode is >= 300 and < 400)
                throw new DiscoveryException("SOURCE_REDIRECT", "Configuration redirects are not supported.");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new DiscoveryException("CONFIG_UNAVAILABLE", "An exact required configuration could not be downloaded.", true);
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new DiscoveryException("CONFIG_LIMIT", "The configuration exceeds its transfer limit.");
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && media is not ("text/plain" or "application/octet-stream" or "binary/octet-stream"))
                throw new DiscoveryException("CONFIG_CONTENT_TYPE", "The configuration response type is unsupported.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream(); var chunk = new byte[65536];
            while (true)
            {
                var count = await input.ReadAsync(chunk, timeout.Token);
                if (count == 0) break;
                if (body.Length + count > MaxBytes) throw new DiscoveryException("CONFIG_LIMIT", "The configuration exceeds its transfer limit.");
                await body.WriteAsync(chunk.AsMemory(0, count), timeout.Token);
            }
            if (body.Length == 0 || response.Content.Headers.ContentLength is long length && length != body.Length)
                throw new DiscoveryException("CONFIG_INTEGRITY", "The configuration response length is invalid.");
            return new(body.ToArray(), url.AbsoluteUri, clock.GetUtcNow().ToString("O"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new DiscoveryException("CONFIG_TIMEOUT", "The configuration request timed out.", true); }
        catch (HttpRequestException)
        { throw new DiscoveryException("NETWORK_UNAVAILABLE", "The official configuration source could not be reached.", true); }
        catch (IOException)
        { throw new DiscoveryException("NETWORK_IO", "The official configuration response could not be read.", true); }
    }
    internal static Uri ResolveRoute(CapturedOpeningContext context)
    {
        if (context.Versions.ParserRevision != "text-metadata-v1" || context.Cdns.ParserRevision != "text-metadata-v1")
            throw new DiscoveryException("SOURCE_UNSUPPORTED", "The saved discovery interpretation is unsupported for configuration acquisition.");
        var reference = context.Reference;
        var expectedBase = $"https://us.version.battle.net/v2/products/{context.Build.ProductId}/";
        if (context.Versions.Url != expectedBase + "versions" || context.Cdns.Url != expectedBase + "cdns")
            throw new DiscoveryException("SOURCE_UNSUPPORTED", "Only original official discovery receipts can authorize configuration acquisition.");
        var versions = MetadataParser.Parse(context.Versions.Bytes);
        var cdns = MetadataParser.Parse(context.Cdns.Bytes);
        if (reference.VersionsRowOrdinal < 0 || reference.VersionsRowOrdinal >= versions.Rows.Count || reference.CdnsRowOrdinal < 0 || reference.CdnsRowOrdinal >= cdns.Rows.Count)
            throw new DiscoveryException("CATALOG_INTEGRITY", "A saved source row ordinal is invalid.");
        var fields = versions.Rows[reference.VersionsRowOrdinal].Fields;
        var identity = BuildIdentity.Create(context.Build.ProductId, fields);
        var source = new Dictionary<string, string>(context.Build.IdentityFields, StringComparer.Ordinal) { ["Region"] = reference.Region };
        if (fields["Region"] != reference.Region || identity.Id != context.Build.Id || identity.Canonical != BuildIdentity.Create(context.Build.ProductId, source).Canonical)
            throw new DiscoveryException("CATALOG_INTEGRITY", "The complete selected identity differs from its saved source row.");
        var matches = cdns.Rows.Where(row => row.Fields.GetValueOrDefault("Name") == reference.Region).ToArray();
        if (matches.Length != 1 || matches[0].Ordinal != reference.CdnsRowOrdinal)
            throw new DiscoveryException("SOURCE_UNAVAILABLE", "The selected region's saved CDN route is missing or ambiguous.");
        var route = matches[0].Fields;
        if (!route.TryGetValue("Path", out var path) || !Regex.IsMatch(path, "^tpr/[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant) ||
            !route.TryGetValue("Servers", out var servers) || servers.Length > 16384)
            throw new DiscoveryException("SOURCE_UNSUPPORTED", "The saved configuration route is unsupported.");
        foreach (var server in servers.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !SupportedHost(uri.Host) ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") continue;
            if (uri.Query.Length > 0 && !Regex.IsMatch(uri.Query, "^\\?(?:maxhosts=[0-9]{1,3}|fallback=[01])(?:&(?:maxhosts=[0-9]{1,3}|fallback=[01]))*$", RegexOptions.CultureInvariant)) continue;
            return new Uri($"https://{uri.Host}/{path}/");
        }
        throw new DiscoveryException("SOURCE_UNSUPPORTED", "No advertised supported official HTTPS configuration server is available.");
    }
    private static bool SupportedHost(string host) => host is "level3.blizzard.com" or "level3.ssl.blizzard.com" ||
        Regex.IsMatch(host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant);
    private static void ValidateReceipt(StoredConfiguration saved)
    {
        if (!Uri.TryCreate(saved.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !SupportedHost(uri.Host) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !Regex.IsMatch(uri.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/config/" + saved.Key[..2] + "/" + saved.Key[2..4] + "/" + saved.Key + "$", RegexOptions.CultureInvariant))
            throw new DiscoveryException("CATALOG_INTEGRITY", "The original saved configuration receipt is invalid.");
    }
    public void Dispose() => cpu.Dispose();
}
