using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>
/// The community build list and availability checks (#9, D106), catalog version 22. Community builds live in their own
/// table and identity namespace, never in builds, observations or receipts, so nothing can open one as if Blizzard
/// had advertised it. A community row that names a build Blizzard also advertised (same product, build config and
/// version) is linked to it and not listed twice.
/// </summary>
public sealed partial class DiscoveryCatalog : ICommunityBuildStore, IBuildAvailabilityStore
{
    /// <summary>At most this many community builds are kept; an import that would pass it is refused.</summary>
    internal const int MaxCommunityBuilds = 200_000;

    private void UpgradeCommunityBuilds()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE community_build_list(id INTEGER PRIMARY KEY CHECK(id=1), generation INTEGER NOT NULL,
                last_attempt TEXT, fetched_at TEXT, problem TEXT, source_url TEXT, sha256 TEXT, byte_length INTEGER, skipped INTEGER);
            INSERT INTO community_build_list(id,generation) VALUES(1,0);
            CREATE TABLE community_builds(id TEXT PRIMARY KEY, product TEXT NOT NULL, version TEXT NOT NULL,
                build_number INTEGER NOT NULL, build_config TEXT NOT NULL, cdn_config TEXT NOT NULL, product_config TEXT,
                created_at TEXT, first_imported TEXT NOT NULL, last_imported TEXT NOT NULL, official_build TEXT);
            CREATE INDEX community_builds_official ON community_builds(product,build_config,version);
            CREATE TABLE build_availability(build_id TEXT PRIMARY KEY, product TEXT NOT NULL, checked_at TEXT NOT NULL, result TEXT NOT NULL);
            PRAGMA user_version=22;
            """, transaction);
        transaction.Commit();
    }

    public Task<CommunityBuildsState> ReadCommunityBuildsStateAsync(CancellationToken token) => Schedule(ReadCommunityState, token);
    private CommunityBuildsState ReadCommunityState()
    {
        using var command = Command("SELECT last_attempt,fetched_at,problem,(SELECT count(*) FROM community_builds WHERE official_build IS NULL) FROM community_build_list WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new DiscoveryException("CATALOG_INTEGRITY", "The saved community build list state is missing.");
        SettingsProblem? problem = null;
        if (!reader.IsDBNull(2))
        {
            try { problem = JsonSerializer.Deserialize<SettingsProblem>(reader.GetString(2), Json); }
            catch (JsonException) { }
            if (problem?.Code is null || problem.Message is null)
                problem = new("COMMUNITY_BUILDS_FAILED", "The last community build list attempt failed.", true);
        }
        return new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(3), problem);
    }
    private long CommunityGeneration() => Convert.ToInt64(Scalar("SELECT generation FROM community_build_list WHERE id=1"));

    public Task RecordCommunityAttemptAsync(string attemptedAt, CancellationToken token) => Schedule(() =>
    {
        Execute("UPDATE community_build_list SET last_attempt=$t WHERE id=1", null, ("$t", attemptedAt));
        return true;
    }, token);

    public Task RecordCommunityFailureAsync(SettingsProblem problem, CancellationToken token) => Schedule(() =>
    {
        Execute("UPDATE community_build_list SET problem=$p WHERE id=1", null, ("$p", Serialize(problem)));
        return true;
    }, token);

    public Task<int> ImportCommunityBuildsAsync(CommunityBuildFetch fetch, string fetchedAt, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        // Builds Blizzard advertised, by product, build config and version, so the list never repeats one of them.
        var official = new Dictionary<(string, string, string), string>();
        using (var command = Command("SELECT id,product,identity_fields FROM builds", transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var fields = Deserialize<Dictionary<string, string>>(reader.GetString(2));
                if (fields.TryGetValue("BuildConfig", out var config) && fields.TryGetValue("VersionsName", out var version))
                    official.TryAdd((reader.GetString(1), config, version), reader.GetString(0));
            }
        foreach (var row in fetch.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (!CommunityBuildRow.Valid(row)) throw new DiscoveryException("COMMUNITY_BUILDS_INVALID", "A community build row isn’t one VibeMiner can keep.");
            Execute("""
                INSERT INTO community_builds VALUES($id,$p,$v,$n,$b,$c,$pc,$created,$t,$t,$official)
                ON CONFLICT(id) DO UPDATE SET last_imported=$t,official_build=COALESCE(official_build,$official)
                """, transaction, ("$id", row.Id), ("$p", row.ProductId), ("$v", row.Version), ("$n", row.BuildNumber),
                ("$b", row.BuildConfig), ("$c", row.CdnConfig), ("$pc", row.ProductConfig), ("$created", row.CreatedAt), ("$t", fetchedAt),
                ("$official", official.GetValueOrDefault((row.ProductId, row.BuildConfig, row.Version))));
        }
        var count = Convert.ToInt32(Scalar("SELECT count(*) FROM community_builds", transaction));
        if (count > MaxCommunityBuilds)
            throw new DiscoveryException("COMMUNITY_BUILDS_INVALID", $"The community build list would keep more than {MaxCommunityBuilds:N0} builds, so this import wasn’t saved.");
        Execute("""
            UPDATE community_build_list SET generation=generation+1,fetched_at=$t,problem=NULL,source_url=$url,sha256=$sha,
                byte_length=$length,skipped=$skipped WHERE id=1
            """, transaction, ("$t", fetchedAt), ("$url", fetch.Url), ("$sha", fetch.Sha256), ("$length", fetch.ByteLength), ("$skipped", fetch.Skipped));
        transaction.Commit();
        return count;
    }, token);

    /// <summary>A newly saved Blizzard build takes over any community row for the same product, build config and version.</summary>
    private void LinkCommunityBuild(BuildIdentity identity, SqliteTransaction transaction)
    {
        if (!identity.Fields.TryGetValue("BuildConfig", out var config) || !identity.Fields.TryGetValue("VersionsName", out var version)) return;
        Execute("UPDATE community_builds SET official_build=$id WHERE product=$p AND build_config=$b AND version=$v AND official_build IS NULL",
            transaction, ("$id", identity.Id), ("$p", identity.ProductId), ("$b", config), ("$v", version));
    }

    private Build ProjectCommunityBuild(string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var command = Command("""
            SELECT product,version,build_number,build_config,cdn_config,product_config,created_at,first_imported,last_imported,
                (SELECT source_url FROM community_build_list WHERE id=1)
            FROM community_builds WHERE id=$id
            """, null, ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new DiscoveryException("CATALOG_INTEGRITY", "A saved community build is missing.");
        var row = new CommunityBuildRow(reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
        // The identity is recomputed from the saved fields on every read, as Blizzard builds are.
        if (!CommunityBuildRow.Valid(row) || row.Id != id || row.BuildNumber != reader.GetInt64(2))
            throw new DiscoveryException("CATALOG_INTEGRITY", "A saved community build failed integrity verification.");
        var first = reader.GetString(7); var last = reader.GetString(8);
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        { ["BuildConfig"] = row.BuildConfig, ["BuildId"] = row.BuildNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ["VersionsName"] = row.Version };
        if (row.ProductConfig is not null) fields["ProductConfig"] = row.ProductConfig;
        return new Build(id, row.ProductId, row.Version, fields["BuildId"], row.BuildConfig, row.CdnConfig, row.ProductConfig,
            fields, [], [], [], first, last, false, false, false, "unknown", [],
            [new CdnConfigObservation(row.CdnConfig, first, last, [], [], [])], 1)
        {
            Community = new(CommunityBuildsStatus.SourceName, reader.IsDBNull(9) ? WagoBuildList.ListUrl.AbsoluteUri : reader.GetString(9), row.CreatedAt, first, last),
        };
    }

    /// <summary>The build's saved availability check, or null. A saved check that can't be read counts as never checked.</summary>
    private BuildAvailability? ReadAvailability(string buildId)
    {
        if (Scalar("SELECT result FROM build_availability WHERE build_id=$id", null, ("$id", buildId)) is not string text || text.Length > 64 * 1024) return null;
        try
        {
            var value = JsonSerializer.Deserialize<BuildAvailability>(text, Json);
            return value is { State: BuildAvailability.Available or BuildAvailability.PartlyAvailable or BuildAvailability.Gone or BuildAvailability.Unknown,
                CheckedAt: not null, Scope: "configs" or "indexes" or "archives", BuildConfig: not null, CdnConfig: not null, Hosts: not null,
                BuildConfigState: not null, CdnConfigState: not null } ? value : null;
        }
        catch (JsonException) { return null; }
    }

    public Task<AvailabilityTarget> ResolveAvailabilityTargetAsync(string productId, string buildId, CancellationToken token) => Schedule(() =>
    {
        ProductRegistry.Validate(productId);
        string buildConfig, cdnConfig;
        if (Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM builds WHERE id=$id AND product=$p)", null, ("$id", buildId), ("$p", productId))) == 1)
        {
            var build = ReadCore(productId, "all", 1, null, token, buildId).Builds.Single();
            buildConfig = build.BuildConfig; cdnConfig = build.CdnConfig;
        }
        else if (Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM community_builds WHERE id=$id AND product=$p)", null, ("$id", buildId), ("$p", productId))) == 1)
        {
            var build = ProjectCommunityBuild(buildId, token);
            buildConfig = build.BuildConfig; cdnConfig = build.CdnConfig;
        }
        else throw new DiscoveryException("BUILD_NOT_FOUND", "This exact saved build is unavailable. Refresh discovery and select it again.", true);
        return new AvailabilityTarget(productId, buildId, buildConfig, cdnConfig, AvailabilityRoutes(productId, token));
    }, token);

    /// <summary>
    /// At most two official HTTPS CDN bases from the newest saved Blizzard cdns document of the product, or of any product
    /// when it has none (every WoW product shares the same path and hosts): the first <c>*.cdn.blizzard.com</c> host, US
    /// row first, asked first because it answers fastest, then a level3 host so the second opinion is another network.
    /// </summary>
    private List<Uri> AvailabilityRoutes(string productId, CancellationToken token)
    {
        byte[]? body = null;
        foreach (var scoped in new[] { true, false })
        {
            using var command = Command("""
                SELECT d.sha256,b.body FROM documents d JOIN events e ON e.generation=d.generation JOIN raw_documents b ON b.sha256=d.sha256
                WHERE d.role='cdns' AND ($scoped=0 OR e.product=$p) AND typeof(b.body)='blob' AND length(b.body) BETWEEN 1 AND 1048576
                ORDER BY d.generation DESC LIMIT 1
                """, null, ("$scoped", scoped ? 1 : 0), ("$p", productId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) continue;
            body = (byte[])reader[1];
            if (Convert.ToHexStringLower(SHA256.HashData(body)) != reader.GetString(0))
                throw new DiscoveryException("CATALOG_INTEGRITY", "Saved source metadata failed integrity verification.");
            break;
        }
        if (body is null) throw new DiscoveryException("AVAILABILITY_NO_ROUTE",
            "VibeMiner doesn’t know Blizzard’s CDN hosts yet. Refresh builds once, then check again.", true);
        MetadataDocument cdns;
        try { cdns = MetadataParser.Parse(body, token); }
        catch (DiscoveryException) { throw new DiscoveryException("AVAILABILITY_NO_ROUTE", "The saved Blizzard CDN list couldn’t be read. Refresh builds, then check again.", true); }
        var candidates = new List<Uri>();
        foreach (var row in cdns.Rows.OrderBy(row => row.Fields.GetValueOrDefault("Name") == "us" ? 0 : 1).ThenBy(row => row.Ordinal))
        {
            if (!row.Fields.TryGetValue("Path", out var path) || !Regex.IsMatch(path, "^tpr/[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant) ||
                !row.Fields.TryGetValue("Servers", out var servers) || servers.Length > 16384) continue;
            foreach (var server in servers.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidates.Count >= 64) break;
                if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !ConfigurationWorkspacePreparer.SupportedHost(uri.Host) ||
                    !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || candidates.Any(route => route.Host == uri.Host)) continue;
                candidates.Add(new Uri($"https://{uri.Host}/{path}/"));
            }
        }
        static bool Level3(Uri route) => route.Host.StartsWith("level3.", StringComparison.Ordinal);
        var first = candidates.FirstOrDefault(route => !Level3(route)) ?? candidates.FirstOrDefault()
            ?? throw new DiscoveryException("AVAILABILITY_NO_ROUTE", "The saved Blizzard CDN list has no official HTTPS host VibeMiner can use.", true);
        var second = candidates.FirstOrDefault(route => route != first && Level3(route) != Level3(first)) ?? candidates.FirstOrDefault(route => route != first);
        return second is null ? [first] : [first, second];
    }

    public Task RecordAvailabilityAsync(AvailabilityTarget target, BuildAvailability result, CancellationToken token) => Schedule(() =>
    {
        if (result.BuildConfig != target.BuildConfig || result.CdnConfig != target.CdnConfig)
            throw new DiscoveryException("CATALOG_INTEGRITY", "An availability check doesn’t match the build it checked.");
        Execute("""
            INSERT INTO build_availability VALUES($id,$p,$t,$r)
            ON CONFLICT(build_id) DO UPDATE SET product=$p,checked_at=$t,result=$r
            """, null, ("$id", target.BuildId), ("$p", target.ProductId), ("$t", result.CheckedAt), ("$r", Serialize(result)));
        return true;
    }, token);
}
