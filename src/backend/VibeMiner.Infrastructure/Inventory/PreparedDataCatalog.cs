using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Prepared data per opened build (D085). The list measures, for each opened build, its file catalog on disk, the table row
/// files its admissions use and its stored download figure, one build per catalog operation so interactive SQL interleaves.
/// Free removes the build's file catalog only: it is the one prepared part no other build uses. Table rows and downloaded
/// objects live in the shared object store and are kept.
/// </summary>
public sealed partial class DiscoveryCatalog : IPreparedDataCatalog
{
    /// <summary>The list names at most this many opened builds, most recently opened first.</summary>
    internal const int PreparedDataListBound = 256;
    /// <summary>File catalog generations of one build looked at by a measurement or a free; cleanup removes abandoned ones.</summary>
    private const int PreparedGenerationsBound = 16;
    private static readonly string[] FileCatalogSuffixes = ["", "-wal", "-shm", "-journal"];

    public async Task<PreparedDataList> ListPreparedDataAsync(CancellationToken token)
    {
        var keys = await Schedule(() =>
        {
            var found = new List<(string Product, string Build, string OpenedAt)>();
            // SQLite returns the other columns from the row with the largest id.
            using var command = Command("""
                SELECT product,build_id,opened_at,max(id) m FROM workspace_admissions GROUP BY product,build_id ORDER BY m DESC LIMIT $n
                """, null, ("$n", PreparedDataListBound + 1));
            using var r = command.ExecuteReader();
            while (r.Read()) { token.ThrowIfCancellationRequested(); found.Add((r.GetString(0), r.GetString(1), r.GetString(2))); }
            return found;
        }, token);
        var builds = new List<PreparedBuild>(Math.Min(keys.Count, PreparedDataListBound));
        foreach (var key in keys.Take(PreparedDataListBound))
        {
            token.ThrowIfCancellationRequested();
            builds.Add(await Schedule(() => ReadPreparedBuild(key.Product, key.Build, key.OpenedAt, token), token));
        }
        return new(builds, keys.Count > PreparedDataListBound, DateTimeOffset.UtcNow.ToString("O"));
    }

    private PreparedBuild ReadPreparedBuild(string product, string build, string openedAt, CancellationToken token)
    {
        (string, object?)[] key = [("$p", product), ("$b", build)];
        var version = Scalar("SELECT json_extract(identity_fields,'$.VersionsName') FROM builds WHERE id=$b AND product=$p", null, key) as string;
        var tables = ReadPreparedTables(build);
        PreparedDownloads downloads;
        try
        {
            var summary = ReadDownloadSummary(new(product, build), token);
            downloads = summary.Download is not { } d ? new("notMeasured", null, null) : new(d.CountState switch
            {
                StoredCountState.Current => "current", StoredCountState.Counting => "counting", _ => "unavailable",
            }, Exact(d.StoredObjects), Exact(d.StoredBytes));
        }
        catch (Exception failure) when (failure is DiscoveryException or System.Text.Json.JsonException)
        {
            downloads = new("unavailable", null, null);
        }
        return new(product, build, version, openedAt, MeasureFileCatalog(product, build), tables, downloads);
    }

    /// <summary>
    /// Each row file once, by its SHA-256; shared when another build's admission uses the same interpretation. The other
    /// builds' interpretation keys are read in one pass and joined, not looked up per file (table_admissions has no
    /// content_key index).
    /// </summary>
    private PreparedTables ReadPreparedTables(string build)
    {
        using var command = Command("""
            SELECT count(*),coalesce(sum(n),0),coalesce(sum(CASE WHEN s THEN n ELSE 0 END),0),coalesce(min(n),1) FROM (
                SELECT json_extract(t.descriptor,'$.sha256') h,max(json_extract(t.descriptor,'$.byteLength')) n,max(o.content_key IS NOT NULL) s
                FROM (SELECT DISTINCT content_key FROM table_admissions WHERE build_id=$b) m
                JOIN table_artifacts t ON t.content_key=m.content_key
                LEFT JOIN (SELECT DISTINCT content_key FROM table_admissions WHERE build_id<>$b) o ON o.content_key=m.content_key
                GROUP BY h)
            """, null, ("$b", build));
        using var r = command.ExecuteReader();
        r.Read();
        if (r.GetInt64(3) < 1 || r.GetInt64(2) > r.GetInt64(1)) throw TableIntegrity("A saved table row file has an invalid length.");
        return new((int)r.GetInt64(0), Exact(r.GetInt64(1)), Exact(r.GetInt64(2)));
    }

    /// <summary>The build's file catalog generations, the newest 16, so the current one is always among them.</summary>
    private List<long> FileCatalogGenerations(string build)
    {
        var generations = new List<long>();
        using var command = Command("SELECT id FROM inventory_generations WHERE build_id=$b AND profile=$profile ORDER BY id DESC LIMIT $n", null,
            ("$b", build), ("$profile", FileCatalogProfile), ("$n", PreparedGenerationsBound));
        using var r = command.ExecuteReader();
        while (r.Read()) generations.Add(r.GetInt64(0));
        return generations;
    }

    /// <summary>The state and the files on disk of one build's file catalog, every generation of it, measured now.</summary>
    private PreparedFileCatalog MeasureFileCatalog(string product, string build)
    {
        var state = Scalar("SELECT state FROM file_catalogs WHERE product=$p AND build_id=$b", null, ("$p", product), ("$b", build)) as string ?? "none";
        var (bytes, files) = MeasureGenerations(FileCatalogGenerations(build));
        return new(state, Exact(bytes), files);
    }

    /// <summary>The lengths and count of the catalog, -wal, -shm and -journal files of these generations, as on disk now.</summary>
    private (long Bytes, int Files) MeasureGenerations(IEnumerable<long> generations)
    {
        long bytes = 0; var files = 0;
        foreach (var generation in generations)
            foreach (var suffix in FileCatalogSuffixes)
            {
                var info = new FileInfo(FileCatalogPath(generation) + suffix);
                if (!info.Exists) continue;
                bytes = checked(bytes + info.Length); files++;
            }
        return (bytes, files);
    }

    /// <summary>
    /// Frees one build's file catalog (D085). One catalog operation, so no other one can attach the file meanwhile: the
    /// row becomes removed under a new generation and every file catalog generation of the build is abandoned, in one
    /// transaction; a running preparation of the build is then cancelled (its late writes are fenced out), and the files
    /// are measured, detached and deleted, then their registry rows. A file that cannot be deleted stays registered as
    /// abandoned and the worker is woken, so cleanup deletes it. The next Open, or Resume, prepares the catalog again from the beginning.
    /// </summary>
    public Task<PreparedDataFreed> FreePreparedDataAsync(PreparedDataFreeRequest request, CancellationToken token) => Schedule(() =>
    {
        PreparedDataService.Validate(request);
        (string, object?)[] key = [("$p", request.ProductId), ("$b", request.BuildIdentity)];
        if (Scalar("SELECT 1 FROM workspace_admissions WHERE product=$p AND build_id=$b LIMIT 1", null, key) is null)
            throw new DiscoveryException("PREPARED_DATA_NOT_FOUND", "This build was never opened here, so it has no prepared data.");
        var state = Scalar("SELECT state FROM file_catalogs WHERE product=$p AND build_id=$b", null, key) as string;
        var generations = FileCatalogGenerations(request.BuildIdentity);
        using (var transaction = Db.BeginTransaction())
        {
            if (state is not null and not "removed")
                Execute("UPDATE file_catalogs SET state='removed',generation=generation+1,attempts=0,inventory=NULL,written=0,total=NULL,stage='queued',problem=NULL,updated_at=$t WHERE product=$p AND build_id=$b",
                    transaction, [.. key, ("$t", DateTimeOffset.UtcNow.ToString("O"))]);
            Execute("UPDATE inventory_generations SET state='abandoned' WHERE build_id=$b AND profile=$profile AND state<>'abandoned'", transaction,
                ("$b", request.BuildIdentity), ("$profile", FileCatalogProfile));
            // Cancelled before this commit, nothing changes. After it the files go, whatever the request's token says.
            token.ThrowIfCancellationRequested(); transaction.Commit();
        }
        if (state == "pending") FileCatalogStopped?.Invoke(request.ProductId, request.BuildIdentity);
        // Measured before the detach, which merges the -wal into the catalog file and deletes it: the freed figure is what
        // was on disk before (the same files the list measures) less what is left after, so it matches the size shown.
        var before = MeasureGenerations(generations);
        Exception? failure = null;
        foreach (var generation in generations)
        {
            var gone = true;
            try { DetachFileCatalog(generation); }
            catch (SqliteException detach) { failure ??= detach; continue; }
            foreach (var suffix in FileCatalogSuffixes)
            {
                var info = new FileInfo(FileCatalogPath(generation) + suffix);
                if (!info.Exists) continue;
                try { info.Delete(); }
                catch (Exception delete) when (delete is IOException or UnauthorizedAccessException) { failure ??= delete; gone = false; }
            }
            if (gone) Execute("DELETE FROM inventory_generations WHERE id=$g AND profile=$profile AND state='abandoned' AND NOT EXISTS(SELECT 1 FROM file_catalogs WHERE inventory=$g)",
                null, ("$g", generation), ("$profile", FileCatalogProfile));
        }
        var after = MeasureGenerations(generations);
        // Every generation of the build is abandoned, but this deletes the newest 16 only, and any it could not delete
        // stay too: wake the worker so its cleanup removes the rest now, not at some later wake-up.
        WakeFileCatalogs();
        if (failure is not null)
        {
            var left = Math.Max(after.Files, 1);
            EngineLog.Write($"Freeing the file list of build {request.BuildIdentity[..12]} left {left} file(s) ({Cause(failure)}).");
            throw new DiscoveryException("STORAGE_FREE_INCOMPLETE",
                $"The file list is freed, but {left} {(left == 1 ? "file" : "files")} of it couldn’t be deleted yet. VibeMiner deletes {(left == 1 ? "it" : "them")} later.", true)
            { Cause = Cause(failure) };
        }
        return new PreparedDataFreed(request.ProductId, request.BuildIdentity, Exact(Math.Max(0, before.Bytes - after.Bytes)), Math.Max(0, before.Files - after.Files),
            MeasureFileCatalog(request.ProductId, request.BuildIdentity));
    }, token);

    private static string Exact(long value) => value.ToString(CultureInfo.InvariantCulture);
}
