using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Check availability on Blizzard's CDN (#9, D106), in stages that each stop the check once they decide it:
/// <list type="number">
/// <item>Both configs: a HEAD on every host (at most two), then a GET of each from a host that has it, checked against
/// its MD5 and our config grammar. Build config missing on every host: Gone. CDN config missing: Partly available.</item>
/// <item>Each archive index (<c>data/…/&lt;key&gt;.index</c>) the CDN config lists, HEAD only, on the first host, then
/// the second when the first doesn't have it. Patch archives (<c>/patch/</c>) are never asked.</item>
/// <item>Optionally each archive, HEAD only, the same way.</item>
/// </list>
/// Only config bytes (at most 1 MiB each) are read; no game content is downloaded. Requests go through the shared CDN
/// transport and its HTTP permits, at most <see cref="Workers"/> at a time. 5xx, timeouts and no connection are
/// Unknown, never Missing. Cancellation stops every request.
/// </summary>
public sealed class CdnAvailabilityProbe(HttpClient client, AcquisitionResources? resources = null, TimeProvider? clock = null,
    TimeSpan? headTimeout = null) : IBuildAvailabilityProbe
{
    /// <summary>HEADs in flight at once: below the shared four HTTP permits, so interactive downloads keep one.</summary>
    public const int Workers = 3;
    /// <summary>More archives than any CDN config lists today (about 1,400), so a bigger list is refused, not trusted.</summary>
    public const int MaxArchives = 8192;
    private const int ReportEvery = 50;
    private readonly OfficialSourceTransport transport = new(client, resources) { HeadTimeout = headTimeout ?? TimeSpan.FromSeconds(15) };
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>Unreadable is set for an invalid config whose MD5 matched but whose text our grammar refused.</summary>
    private sealed record ConfigOutcome(string State, TactConfiguration? Parsed, bool Unreadable = false);

    public async Task<BuildAvailability> CheckAsync(AvailabilityTarget target, bool archives, Func<AvailabilityProgress, Task> progress, CancellationToken token)
    {
        if (target.Routes.Count is < 1 or > 2) throw new DiscoveryException("AVAILABILITY_NO_ROUTE", "No official CDN host is known for this product.");
        var hosts = target.Routes.Select(route => route.Host).ToArray();
        BuildAvailability Result(string state, string scope, string buildState, string cdnState, AvailabilityCounts? indexes = null,
            AvailabilityCounts? archived = null, SettingsProblem? problem = null) =>
            new(state, time.GetUtcNow().ToString("O"), scope, target.BuildConfig, target.CdnConfig, hosts, buildState, cdnState, indexes, archived, problem);

        await progress(new("Checking build and CDN configs", 0, 2));
        var build = await ConfigAsync(target, target.BuildConfig, token);
        await progress(new("Checking build and CDN configs", 1, 2));
        var cdn = await ConfigAsync(target, target.CdnConfig, token);
        await progress(new("Checking build and CDN configs", 2, 2));
        switch (build.State)
        {
            case "missing": return Result(BuildAvailability.Gone, "configs", build.State, cdn.State);
            case "invalid": return Result(BuildAvailability.Unknown, "configs", build.State, cdn.State, problem: Invalid("build", build.Unreadable));
            case "unknown": return Result(BuildAvailability.Unknown, "configs", build.State, cdn.State, problem: Unanswered());
        }
        switch (cdn.State)
        {
            case "missing": return Result(BuildAvailability.PartlyAvailable, "configs", build.State, cdn.State);
            case "invalid": return Result(BuildAvailability.Unknown, "configs", build.State, cdn.State, problem: Invalid("CDN", cdn.Unreadable));
            case "unknown": return Result(BuildAvailability.Unknown, "configs", build.State, cdn.State, problem: Unanswered());
        }
        // Only the archives list: patch-archives live under /patch/, which the official transport never requests.
        if (!cdn.Parsed!.Fields.TryGetValue("archives", out var listed)) listed = [];
        if (listed.Count > MaxArchives || listed.Any(key => key.Length != 32 || key.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            return Result(BuildAvailability.Unknown, "configs", build.State, cdn.State, problem: new("AVAILABILITY_CONFIG_UNSUPPORTED",
                "The CDN config lists archives VibeMiner doesn’t check (an unusual key or more than 8,192 archives).", false));
        var keys = listed.Distinct(StringComparer.Ordinal).ToArray();
        var indexes = await HeadAllAsync(target, keys, ".index", "Checking archive indexes", progress, token);
        if (indexes.Missing > 0) return Result(BuildAvailability.PartlyAvailable, "indexes", build.State, cdn.State, indexes);
        if (indexes.Unknown > 0) return Result(BuildAvailability.Unknown, "indexes", build.State, cdn.State, indexes, problem: Unanswered());
        if (!archives) return Result(BuildAvailability.Available, "indexes", build.State, cdn.State, indexes);
        var data = await HeadAllAsync(target, keys, "", "Checking archives", progress, token);
        if (data.Missing > 0) return Result(BuildAvailability.PartlyAvailable, "archives", build.State, cdn.State, indexes, data);
        if (data.Unknown > 0) return Result(BuildAvailability.Unknown, "archives", build.State, cdn.State, indexes, data, Unanswered());
        return Result(BuildAvailability.Available, "archives", build.State, cdn.State, indexes, data);
    }

    private static Uri Config(Uri route, string key) => new(route, $"config/{key[..2]}/{key[2..4]}/{key}");
    private static Uri Data(Uri route, string key, string suffix) => new(route, $"data/{key[..2]}/{key[2..4]}/{key}{suffix}");

    /// <summary>verified, missing (on every host), invalid (bytes that don't match the key), or unknown.</summary>
    private async Task<ConfigOutcome> ConfigAsync(AvailabilityTarget target, string key, CancellationToken token)
    {
        var heads = new CdnPresence[target.Routes.Count];
        for (var i = 0; i < heads.Length; i++) heads[i] = await transport.HeadAsync(Config(target.Routes[i], key), token);
        var invalid = false; var unreadable = false; var missing = heads.Count(head => head == CdnPresence.Missing);
        for (var i = 0; i < heads.Length; i++)
        {
            if (heads[i] != CdnPresence.Present) continue;
            byte[] bytes;
            try { bytes = await transport.ReadConfigurationAsync(Config(target.Routes[i], key), token); }
            catch (DiscoveryException error) when (error.Code == "CONTENT_SOURCE_MISSING") { missing++; continue; }
            catch (DiscoveryException) { continue; }
            var (parsed, matched) = await VerifyAsync(bytes, key, token);
            if (parsed is not null) return new("verified", parsed);
            invalid = true; unreadable |= matched;
        }
        return new(invalid ? "invalid" : missing == heads.Length ? "missing" : "unknown", null, invalid && unreadable);
    }

    /// <summary>The parsed config, or null; Matched says whether the bytes matched the key, so a refusal can be named right.</summary>
    private async Task<(TactConfiguration? Parsed, bool Matched)> VerifyAsync(byte[] bytes, string key, CancellationToken token)
    {
        using var permit = resources is null ? null : await resources.EnterCpuAsync(null, token);
        return await Task.Run<(TactConfiguration?, bool)>(() =>
        {
            if (Convert.ToHexStringLower(MD5.HashData(bytes)) != key) return (null, false);
            try { return (TactConfigurationParser.Parse(bytes, token), true); }
            catch (DiscoveryException) { return (null, true); }
        }, token);
    }

    private async Task<AvailabilityCounts> HeadAllAsync(AvailabilityTarget target, IReadOnlyList<string> keys, string suffix, string stage,
        Func<AvailabilityProgress, Task> progress, CancellationToken token)
    {
        int present = 0, missing = 0, unknown = 0, done = 0;
        using var reporting = new SemaphoreSlim(1, 1);
        await progress(new(stage, 0, keys.Count));
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, async (key, ct) =>
        {
            // Missing only when every host says so; one that can't tell leaves it unknown.
            var outcome = CdnPresence.Missing;
            foreach (var route in target.Routes)
            {
                var head = await transport.HeadAsync(Data(route, key, suffix), ct);
                if (head == CdnPresence.Present) { outcome = head; break; }
                if (head == CdnPresence.Unknown) outcome = CdnPresence.Unknown;
            }
            if (outcome == CdnPresence.Present) Interlocked.Increment(ref present);
            else if (outcome == CdnPresence.Missing) Interlocked.Increment(ref missing);
            else Interlocked.Increment(ref unknown);
            var count = Interlocked.Increment(ref done);
            if (count % ReportEvery != 0 && count != keys.Count) return;
            await reporting.WaitAsync(ct);
            try { await progress(new(stage, Volatile.Read(ref done), keys.Count)); }
            finally { reporting.Release(); }
        });
        return new(keys.Count, present, missing, unknown);
    }

    private static SettingsProblem Unanswered() => new("AVAILABILITY_UNANSWERED",
        "Blizzard’s CDN didn’t answer every request (a server error, a timeout or no connection), so this can’t be decided. Try again later.", true);
    private static SettingsProblem Invalid(string which, bool unreadable) => new("AVAILABILITY_CONFIG_INVALID", unreadable
        ? $"Blizzard’s CDN sent a {which} config that matches its hash but isn’t a config VibeMiner can read, so it wasn’t used."
        : $"Blizzard’s CDN sent a {which} config whose bytes don’t match its hash, so it wasn’t used.", true);
}
