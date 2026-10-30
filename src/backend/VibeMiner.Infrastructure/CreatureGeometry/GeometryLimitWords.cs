using System.Globalization;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Who owns a cap a geometry read stopped at (#116): the file being read, one of the build's own lists, a shared
/// engine queue that is full now, or some other engine work bound that is neither.</summary>
internal enum GeometryLimitOwner { File, BuildList, Capacity, Engine }

internal sealed partial class GeometrySourceClosure
{
    /// <summary>The engine picks the headline the desktop shows, so the desktop keeps no copy of these codes (#116).
    /// Encrypted and unsupported read the code as the desktop used to; a too-large headline needs a limit that won't pass
    /// on a retry, and says whose cap it is.</summary>
    internal static CreatureGeometryHeadline Headline(CreatureGeometryState state, string code, bool retryable, GeometryLimitOwner owner)
    {
        if (state == CreatureGeometryState.Protected || code.Contains("ENCRYPT", StringComparison.Ordinal) ||
            code.Contains("KEY_UNAVAILABLE", StringComparison.Ordinal) || code.Contains("PROTECTED", StringComparison.Ordinal))
            return CreatureGeometryHeadline.Encrypted;
        if (state == CreatureGeometryState.Unsupported || code.Contains("UNSUPPORTED", StringComparison.Ordinal)) return CreatureGeometryHeadline.Unsupported;
        if (state != CreatureGeometryState.ResourceLimit || retryable) return CreatureGeometryHeadline.NotDrawn;
        return owner switch
        {
            GeometryLimitOwner.File => CreatureGeometryHeadline.ModelTooLarge,
            GeometryLimitOwner.BuildList => CreatureGeometryHeadline.BuildListTooLarge,
            _ => CreatureGeometryHeadline.NotDrawn
        };
    }

    internal static string NamedLimit(string code, CreatureGeometryDependency dependency, string detail = "") =>
        NamedLimitOwner(code, dependency, detail).Message;

    // A limit with no reader message of its own is named in plain words (#114), and names the owner of the cap (#116):
    // a cap on one of the build's own lists names that list, not the file being read; a cap on the file names the file;
    // a full shared queue says it is full; any other engine bound says the file was not read, without blaming it.
    internal static (string Message, GeometryLimitOwner Owner) NamedLimitOwner(string code, CreatureGeometryDependency dependency, string detail = "")
    {
        var file = dependency switch
        {
            CreatureGeometryDependency.Model => "model file", CreatureGeometryDependency.Skin => "SKIN file",
            CreatureGeometryDependency.Texture => "texture file", CreatureGeometryDependency.Artifact => "geometry artifact", _ => "display metadata"
        };
        string BuildList(string list) => $"The build's {list} is over its limit, so the {file} was not read.";
        string Full(string what) => $"The engine's {what} is full, so the {file} was not read.";
        string NotRead(string why) => $"{why}, so the {file} was not read.";
        (string, GeometryLimitOwner) Of(string message, GeometryLimitOwner owner) => (message, owner);
        switch (code)
        {
            case "EN_LIMIT" or "RESOLVER_LIMIT": return Of(BuildList("encoding manifest"), GeometryLimitOwner.BuildList);
            case "IN_LIMIT": return Of(BuildList("install manifest"), GeometryLimitOwner.BuildList);
            case "ROOT_LIMIT":
                // RootBatchLookup and RootTargetLookup also refuse a lookup by its files' variants; only the rest are the list.
                var variants = detail.Contains("per-file variant", StringComparison.Ordinal) ? RootBatchLookup.MaxMatchesPerTarget
                    : detail.Contains("target variant limit", StringComparison.Ordinal) ? RootTargetLookup.MaxMatches : 0;
                if (variants > 0)
                    return Of($"The {file} has more than {N(variants)} ROOT variants; geometry reads at most {N(variants)}.", GeometryLimitOwner.File);
                if (detail.Contains("batch membership bound", StringComparison.Ordinal))
                    return Of(NotRead($"The ROOT lookup shared with other files found more than {N(RootBatchLookup.MaxMatches)} variants in all"), GeometryLimitOwner.Engine);
                if (detail.Contains("Too many requested FileDataIDs", StringComparison.Ordinal))
                    return Of(NotRead($"The ROOT lookup asked for more than {N(RootBatchLookup.MaxTargets)} files at once"), GeometryLimitOwner.Engine);
                return Of(BuildList("ROOT file list"), GeometryLimitOwner.BuildList);
            case "ROOT_SCOPE_LIMIT" or "ASSET_SOURCE_LIMIT": return Of(BuildList("ROOT file list"), GeometryLimitOwner.BuildList);
            case "CONFIG_LIMIT": return Of(BuildList("CDN configuration"), GeometryLimitOwner.BuildList);
            case "ARCHIVE_INDEX_LIMIT" or "CONTENT_LOCATOR_LIMIT" or "BULK_LOCATOR_LIMIT": return Of(BuildList("archive index lookup"), GeometryLimitOwner.BuildList);
            case "METADATA_LIMIT": return Of(BuildList("metadata"), GeometryLimitOwner.BuildList);
            case "INVENTORY_LIMIT": return Of(BuildList("file inventory"), GeometryLimitOwner.BuildList);
            case "FILE_CATALOG_LIMIT": return Of(BuildList("file catalog"), GeometryLimitOwner.BuildList);
            case "DOWNLOAD_LEDGER_LIMIT": return Of(BuildList("download list"), GeometryLimitOwner.BuildList);
            case "BLTE_LIMIT": return Of($"The {file} is over the BLTE {BlteCap(detail)} limit.", GeometryLimitOwner.File);
            // OfficialSourceTransport caps an index read (the archive index) apart from a file's own download range.
            case "CONTENT_LIMIT" when detail.Contains("index read", StringComparison.Ordinal): return Of(BuildList("archive index lookup"), GeometryLimitOwner.BuildList);
            case "CONTENT_LIMIT": return Of($"The {file} is over the download size limit.", GeometryLimitOwner.File);
            case "CONTENT_VARIANT_LIMIT": return Of($"The {file} has more ROOT variants than one lookup keeps.", GeometryLimitOwner.File);
            case "GEOMETRY_ARTIFACT_LIMIT": return Of($"The {file} is over the geometry artifact size limit.", GeometryLimitOwner.File);
            case "ACQUISITION_CAPACITY": return Of(Full("download queue"), GeometryLimitOwner.Capacity);
            case "CONTENT_LOCATOR_CAPACITY": return Of(Full("archive lookup cache"), GeometryLimitOwner.Capacity);
            case "ENCODED_SOURCE_CAPACITY": return Of(Full("download source pool"), GeometryLimitOwner.Capacity);
            case "CONTENT_STORE_CAPACITY": return Of(Full("file check queue"), GeometryLimitOwner.Capacity);
            case "ASSET_CONTEXT_CAPACITY": return Of(Full("build source pool"), GeometryLimitOwner.Capacity);
            case "ASSET_HANDLE_CAPACITY": return Of(Full("asset handle pool"), GeometryLimitOwner.Capacity);
            case "ASSET_BATCH_CAPACITY": return Of(Full("asset lookup queue"), GeometryLimitOwner.Capacity);
        }
        // Any other code is read as words; it names no owner it can't know, e.g. ASSET_BATCH_LIMIT reads
        // "The engine's asset batch limit was reached, so the model file was not read."
        var words = string.Join(' ', code.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w is "BLTE" or "ROOT" or "CDN" or "DB2" or "TACT" or "M2" or "SKIN" ? w : w.ToLowerInvariant()));
        return code.EndsWith("_CAPACITY", StringComparison.Ordinal)
            ? Of(Full(words), GeometryLimitOwner.Capacity)
            : Of(NotRead($"The engine's {words} was reached"), GeometryLimitOwner.Engine);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    // The BLTE readers' messages are fixed; pick out which cap tripped (BlteDecoder, its projection reader and
    // BlteEncodedVerifier). A backend test drives every BLTE_LIMIT refusal of the real readers through this.
    private static string BlteCap(string detail) =>
        detail.Contains("chunk count", StringComparison.Ordinal) ? "chunk count"
        : detail.Contains("chunk-header", StringComparison.Ordinal) || detail.Contains("header exceeds the chunk", StringComparison.Ordinal) ? "chunk header size"
        : detail.Contains("working-memory", StringComparison.Ordinal) ? "chunk memory"
        : detail.Contains("sixteen-MiB", StringComparison.Ordinal) ? "chunk size"
        : detail.Contains("range bound", StringComparison.Ordinal) ? "readable range count"
        : detail.Contains("object", StringComparison.Ordinal) ? "object size" : "size";
}
