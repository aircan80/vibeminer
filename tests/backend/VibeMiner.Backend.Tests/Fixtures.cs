using System.Net;
using System.Text;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

internal static class Fixtures
{
    public const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string C = "cccccccccccccccccccccccccccccccc";
    public const string Header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16|Opaque!STRING:0";
    public static string Versions(params string[] rows) => Header + "\n## seqn = 00012\n" + string.Join('\n', rows) + "\n";
    public static string Row(string region = "us", string config = A, string unknown = "opaque", string productConfig = C, string cdn = B) =>
        $"{region}|{config}|{cdn}||00123|1.2.3.00123|{productConfig}|{unknown}";
    public static string Cdns(params string[] regions) => "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\n" +
        string.Join('\n', regions.Select(region => $"{region}|tpr/wow|cdn.invalid|https://cdn.invalid/?opaque=1")) + "\n";
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    public static OfficialDiscoverySource Source(string versions, string? cdns = null) => new(new HttpClient(new Handler((request, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal) ? versions : cdns ?? Cdns("us", "eu")) }))));
    /// <summary>Rows of every table a prepared Creature, display or geometry stands on, and the stored object count (#120).</summary>
    public static IReadOnlyDictionary<string, long> DerivedRows(string dataDirectory)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={System.IO.Path.Combine(dataDirectory, "discovery-v1.sqlite3")};Pooling=False"); db.Open();
        var rows = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[] { "build_regions", "build_cdn_config_regions", "table_artifacts", "table_admissions", "creatures_artifacts", "creatures_admissions",
            "geometry_artifacts", "geometry_source_locators", "geometry_appearances" })
        { using var command = db.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {table}"; rows[table] = (long)command.ExecuteScalar()!; }
        var objects = System.IO.Path.Combine(dataDirectory, "objects-v1");
        rows["objects"] = Directory.Exists(objects) ? Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).LongCount() : 0;
        return rows;
    }
    public static Dictionary<string, string> Fields(string config = A, string unknown = "opaque") => new(StringComparer.Ordinal)
    { ["Region"] = "us", ["BuildConfig"] = config, ["CDNConfig"] = B, ["VersionsName"] = "1.2.3", ["BuildId"] = "000123", ["Opaque"] = unknown };
    public static async Task<DiscoveryAttempt> Attempt(string versions, string? cdns = null)
    { using var source = Source(versions, cdns); return await source.FetchAsync("wow", CancellationToken.None); }
    /// <summary>The same rows in a versions document with other bytes, so a refresh moves the opening reference (#120).
    /// A byte-identical refresh keeps it.</summary>
    public static DiscoveryAttempt Republished(DiscoveryAttempt attempt, int edition) => attempt with
    {
        Documents = attempt.Documents.Select(d => d.Role != "versions" ? d : d with
        {
            Bytes = [.. d.Bytes, .. Utf8($"## republished {edition}\n")],
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([.. d.Bytes, .. Utf8($"## republished {edition}\n")]))
        }).ToArray()
    };
}

internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    public int Calls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Interlocked.Increment(ref Calls); var response = await action(request, cancellationToken); response.RequestMessage ??= request; return response; }
}
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
