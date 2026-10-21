using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    /// <summary>The open's CDN configuration plus at most this many earlier ones: the same 16 the build projection carries.</summary>
    internal const int MaxEarlierCdnConfigs = MaxProjectedCdnConfigs - 1;

    /// <summary>
    /// CDN configurations of this build that come after <paramref name="cdnConfig"/> in its newest-first history
    /// (last seen, then first seen, then key), newest first and at most 15. Only a build's own discovery history
    /// authorizes them; a newer one than the open's is never included.
    /// </summary>
    internal Task<IReadOnlyList<string>> ReadEarlierCdnConfigsAsync(string buildId, string cdnConfig, CancellationToken token) => Schedule(() =>
    {
        if (!DownloadHash(buildId, 64) || !DownloadHash(cdnConfig, 32))
            throw new DiscoveryException("INVALID_REQUEST", "Choose one exact build and CDN configuration identity.");
        if (Scalar("SELECT 1 FROM build_cdn_configs WHERE build_id=$id AND cdn_config=$cdn", null, ("$id", buildId), ("$cdn", cdnConfig)) is null)
            throw new DiscoveryException("CATALOG_INTEGRITY", "The open CDN configuration is missing from the build's history.");
        var result = new List<string>(MaxEarlierCdnConfigs);
        using var command = Command("""
            SELECT CASE WHEN typeof(e.cdn_config)='text' AND length(e.cdn_config)=32 AND e.cdn_config NOT GLOB '*[^0-9a-f]*' THEN e.cdn_config END
            FROM build_cdn_configs c JOIN build_cdn_configs e ON e.build_id=c.build_id
            WHERE c.build_id=$id AND c.cdn_config=$cdn AND e.cdn_config<>c.cdn_config AND (e.last_generation<c.last_generation OR
                e.last_generation=c.last_generation AND (e.first_generation<c.first_generation OR
                    e.first_generation=c.first_generation AND e.cdn_config>c.cdn_config))
            ORDER BY e.last_generation DESC,e.first_generation DESC,e.cdn_config LIMIT $limit
            """, null, ("$id", buildId), ("$cdn", cdnConfig), ("$limit", MaxEarlierCdnConfigs));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0)) throw new DiscoveryException("CATALOG_INTEGRITY", "A saved CDN configuration identity is invalid.");
            result.Add(reader.GetString(0));
        }
        return (IReadOnlyList<string>)result.AsReadOnly();
    }, token);
}
