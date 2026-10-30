using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// The wago.tools build list (#9, D106): one GET of <see cref="ListUrl"/>, read as untrusted bytes. HTTPS to that one
/// host only, no redirects, at most <see cref="MaxResponseBytes"/>, <see cref="DefaultBudget"/> for the whole answer,
/// cancellable, and a User-Agent that names VibeMiner and its repository. The JSON is read with bounded depth and only
/// rows of registered products with a four-part version and 32-hex configs are kept. The rows are a build index only:
/// nothing here is ever downloaded from wago.tools beyond this list.
/// </summary>
public sealed class WagoBuildList : ICommunityBuildFetcher
{
    public const string Host = "wago.tools";
    public static readonly Uri ListUrl = new($"https://{Host}/api/builds");
    public const string UserAgent = "VibeMiner/0.1 (+https://github.com/aircan80/vibeminer)";
    public const int MaxResponseBytes = 16 << 20;
    public const int MaxRows = 50_000;
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);
    private readonly HttpClient client;
    private readonly Uri? origin;
    private readonly TimeSpan budget;

    public WagoBuildList(HttpClient client, Uri? testOrigin = null, TimeSpan? readBudget = null)
    {
        // Tests only: a loopback origin the request goes to instead, so no test run reaches wago.tools (D106).
        if (testOrigin is not null && !CommunityListfile.LoopbackOrigin(testOrigin))
            throw new ArgumentException("A community build list test origin must be a loopback http(s) origin.");
        this.client = client; origin = testOrigin; budget = readBudget ?? DefaultBudget;
    }

    /// <summary>The list's own HTTP handler: no redirects or decompression, one connection, a 10 s connect timeout.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.None,
    };

    public async Task<CommunityBuildFetch> FetchAsync(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(budget);
        var target = origin is null ? ListUrl : new Uri(origin, ListUrl.PathAndQuery);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        byte[] bytes;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri != target)
                throw Failed("wago.tools redirected the request, and VibeMiner doesn’t follow redirects.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw Failed("wago.tools is limiting requests right now.");
            if (!response.IsSuccessStatusCode)
                throw Failed($"wago.tools answered HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}.");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw Failed("wago.tools sent an encoded answer VibeMiner doesn’t read.");
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && media is not "application/json")
                throw Failed("wago.tools didn’t answer with JSON.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw TooLarge();
            bytes = await ReadAsync(response, limit.Token);
            if (response.Content.Headers.ContentLength is { } declared && declared != bytes.Length)
                throw Failed("The answer from wago.tools ended early.");
        }
        catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
        { throw Offline(); }
        catch (HttpRequestException) { throw Failed("The connection to wago.tools failed."); }
        catch (IOException) { throw Failed("The answer from wago.tools was interrupted."); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            // A connect timeout is a cancellation wrapping a TimeoutException; the budget running out is the other case.
            if (error.InnerException is TimeoutException) throw Offline();
            throw Failed($"wago.tools didn’t answer within {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
        }
        var (rows, skipped) = Parse(bytes, token);
        return new(rows, ListUrl.AbsoluteUri, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, skipped);
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (body.Length + read > MaxResponseBytes) throw TooLarge();
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    /// <summary>
    /// The list's JSON: an object keyed by product, each an array of rows with product, version, build_config,
    /// cdn_config, product_config (string or null) and created_at. Unknown products, unusable rows and duplicates are
    /// skipped and counted; a document that isn't that shape is refused whole.
    /// </summary>
    public static (IReadOnlyList<CommunityBuildRow> Rows, int Skipped) Parse(byte[] bytes, CancellationToken token = default)
    {
        // Text the reader accepts can still fail later: a lone surrogate escape ("\uD800") makes GetString and Name throw
        // InvalidOperationException. Anything the bounded reader can't turn into rows refuses the whole list (#9 review).
        try { return ParseCore(bytes, token); }
        catch (Exception error) when (error is not (DiscoveryException or OperationCanceledException))
        { throw Invalid("The build list has text VibeMiner can’t read."); }
    }

    private static (IReadOnlyList<CommunityBuildRow> Rows, int Skipped) ParseCore(byte[] bytes, CancellationToken token)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw Invalid("The build list isn’t valid JSON."); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("The build list isn’t keyed by product.");
            var rows = new List<CommunityBuildRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            foreach (var group in document.RootElement.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (group.Value.ValueKind != JsonValueKind.Array) throw Invalid("A product in the build list isn’t a list of builds.");
                foreach (var item in group.Value.EnumerateArray())
                {
                    if (rows.Count + skipped >= MaxRows) throw Invalid($"The build list has more than {MaxRows.ToString(CultureInfo.InvariantCulture)} builds.");
                    var row = Row(group.Name, item);
                    if (row is null || !seen.Add(row.Id)) { skipped++; continue; }
                    rows.Add(row);
                }
            }
            return (rows, skipped);
        }
    }

    private static CommunityBuildRow? Row(string product, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (Text("product") is { } named && named != product) return null;
        var productConfig = item.TryGetProperty("product_config", out var config) ? config.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => config.GetString()?.ToLowerInvariant() ?? "",
            _ => "",
        } : null;
        var row = new CommunityBuildRow(product, Text("version") ?? "", Text("build_config")?.ToLowerInvariant() ?? "",
            Text("cdn_config")?.ToLowerInvariant() ?? "", productConfig, Created(Text("created_at")));
        return CommunityBuildRow.Valid(row) ? row : null;
    }

    /// <summary>The row's date as a UTC instant; a date without an offset is read as UTC. Anything else is dropped.</summary>
    private static string? Created(string? text)
    {
        if (text is null || text.Length > 40) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value.ToString("O", CultureInfo.InvariantCulture) : null;
    }

    internal static DiscoveryException Offline() => new("COMMUNITY_BUILDS_OFFLINE",
        "VibeMiner couldn’t reach wago.tools to get the community build list.", true);
    internal static DiscoveryException Failed(string detail) => new("COMMUNITY_BUILDS_FAILED",
        $"Getting the community build list from wago.tools failed. {detail}", true);
    internal static DiscoveryException TooLarge() => new("COMMUNITY_BUILDS_TOO_LARGE",
        $"The wago.tools build list is larger than VibeMiner allows ({MaxResponseBytes >> 20} MiB), so it wasn’t read.");
    internal static DiscoveryException Invalid(string detail) => new("COMMUNITY_BUILDS_INVALID",
        $"The wago.tools build list couldn’t be read safely, so it wasn’t used. {detail}");
}
