using System.Text.Json;
using System.Text.RegularExpressions;
using VibeMiner.Builds;
using VibeMiner.Composition;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Host;

/// <summary>
/// The <c>build-catalog-v1</c> shapes (contracts/build-catalog-v1.md): camel-case names, UTC times, and bounded lists, so a reply fits
/// the general frame whatever the history log holds.
/// </summary>
internal static partial class BuildCatalogWire
{
    public const int MaxBuilds = 100, MaxProblems = 20, MaxFailures = 64, MaxNewProducts = 64, MaxRegions = 64, MaxMessage = 512;
    /// <summary>Builds stop being added once their JSON would pass this, across all products.</summary>
    public const int BuildsBudgetBytes = 750_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Z0-9_]{1,64}$")]
    private static partial Regex CodePattern();
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ConfigPattern();

    /// <summary>
    /// The whole reply, envelope included, fits the general frame: the frame less this much is left for <c>result</c>. The envelope is
    /// under 100 bytes plus the request id, at most 128 chars that escape to 6 bytes each.
    /// </summary>
    public const int EnvelopeReserveBytes = 1024;
    public const int ReplyBudgetBytes = ProtocolHost.MaxFrameBytes - EnvelopeReserveBytes;

    /// <summary>One element with its serialized size.</summary>
    internal readonly record struct Sized(object Wire, int Bytes);

    /// <summary>One product before the reply is fitted to the frame.</summary>
    internal sealed record ProductParts(ReviewedProduct Product, List<Sized> Builds, int BuildCount, DateTimeOffset? LastObservedAt,
        List<Sized> Problems, int ProblemCount);

    /// <summary>The list before it is fitted to the frame.</summary>
    internal sealed record CatalogParts(bool Writer, IReadOnlyList<ProductParts> Products, object? Summary);

    /// <summary>The list: every reviewed product in registry order, then the newest stored summary.</summary>
    internal static async Task<CatalogParts> CatalogAsync(IBuildCatalogHandle handle, bool writer, CancellationToken cancellationToken)
    {
        var budget = BuildsBudgetBytes;
        var products = new List<ProductParts>(Products.Reviewed.Count);
        foreach (var product in Products.Reviewed)
        {
            var list = await handle.Catalog.ListBuildsAsync(product.Id, cancellationToken).ConfigureAwait(false);
            products.Add(Product(product, list, ref budget));
        }
        var stored = await StoredSummary.NewestSummaryAsync(handle.Objects, cancellationToken).ConfigureAwait(false);
        return new(writer, products, stored is { Entry: var entry, Read: var read } ? new
        {
            observedAt = entry.ObservedAt.ToUniversalTime(),
            readable = read.Summary.State == ObjectState.Present
                && !read.Problems.Any(p => p.Code is "SUMMARY_UNREADABLE" or "SUMMARY_COLUMNS_MISSING"),
            newProducts = NewProducts(read.NewProducts),
        } : null);
    }

    /// <summary>The <c>catalog.list</c> reply, fitted to the frame.</summary>
    internal static object List(CatalogParts parts) => Fit(parts, ReplyBudgetBytes);

    /// <summary>
    /// Drops builds from the last products first, then problems the same way, until the reply's JSON (as the host writes it, escapes
    /// included) is at most <paramref name="limit"/> bytes. buildCount and problemCount still say how many there are.
    /// </summary>
    internal static object Fit(CatalogParts parts, int limit)
    {
        // The reply with no builds or problems, then each element and its comma.
        var size = (long)Size(Shape(parts, empty: true));
        size += parts.Products.Sum(p => p.Builds.Sum(b => (long)b.Bytes + 1) + p.Problems.Sum(q => (long)q.Bytes + 1));
        foreach (var pick in new Func<ProductParts, List<Sized>>[] { p => p.Builds, p => p.Problems })
            for (var i = parts.Products.Count - 1; i >= 0 && size > limit; i--)
            {
                var items = pick(parts.Products[i]);
                while (items.Count > 0 && size > limit) { size -= items[^1].Bytes + 1; items.RemoveAt(items.Count - 1); }
            }
        return Shape(parts, empty: false);
    }

    private static object Shape(CatalogParts parts, bool empty) => new
    {
        access = parts.Writer ? "writer" : "readOnly",
        products = parts.Products.Select(p => new
        {
            productId = p.Product.Id,
            label = p.Product.Label,
            builds = empty ? [] : p.Builds.Select(b => b.Wire).ToArray(),
            buildCount = p.BuildCount,
            lastObservedAt = p.LastObservedAt?.ToUniversalTime(),
            problems = empty ? [] : p.Problems.Select(q => q.Wire).ToArray(),
            problemCount = p.ProblemCount,
        }).ToArray(),
        summary = parts.Summary,
    };

    private static int Size(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Json).Length;

    private static ProductParts Product(ReviewedProduct product, BuildList list, ref int budget)
    {
        var builds = new List<Sized>();
        foreach (var build in list.Builds)
        {
            if (builds.Count == MaxBuilds || budget <= 0) break;
            if (Build(build) is not { } wire) continue;
            var size = Size(wire);
            if (size + 1 > budget) { budget = 0; break; }
            budget -= size + 1;
            builds.Add(new(wire, size));
        }
        var problems = list.Problems.Take(MaxProblems).Select(p => (object)new
        {
            code = CodePattern().IsMatch(p.Code) ? p.Code : "CATALOG_PROBLEM",
            message = Message(p.Message) ?? p.Code,
        }).Select(q => new Sized(q, Size(q))).ToList();
        return new(product, builds, list.Builds.Count, list.LastObservedAt, problems, list.Problems.Count);
    }

    /// <summary>One build; null when its identity fields can't cross the wire unchanged (it still counts in buildCount).</summary>
    private static object? Build(CatalogBuild build)
    {
        if (build.Id.Length is < 1 or > 128 || build.VersionsName.Length is < 1 or > 128 || !ConfigPattern().IsMatch(build.BuildConfig)) return null;
        // A current build lists only the regions whose listing is current, as vm browse shows it.
        var regions = build.Regions.Where(r => !build.Current || r.Current).Select(r => r.Region)
            .Where(r => r.Length is >= 1 and <= 32).Distinct(StringComparer.Ordinal).Take(MaxRegions).ToArray();
        return new
        {
            id = build.Id,
            versionsName = build.VersionsName,
            buildConfig = build.BuildConfig,
            current = build.Current,
            regions,
            firstSeenAt = build.FirstSeen.ToUniversalTime(),
            lastSeenAt = build.LastSeen.ToUniversalTime(),
        };
    }

    /// <summary>The refresh reply: the list after it, and every document that didn't answer (NotFound is an answer).</summary>
    internal static object Refresh(CatalogParts catalog, DateTimeOffset refreshedAt, IReadOnlyList<CatalogRefresh> products, SummaryRefresh summary)
    {
        var failures = new List<object>();
        foreach (var product in products)
        {
            if (Failure(product.Product, "versions", product.Versions) is { } versions) failures.Add(versions);
            if (Failure(product.Product, "cdns", product.Cdns) is { } cdns) failures.Add(cdns);
        }
        var summaryFailure = Failure(null, "summary", summary.Summary);
        if (summaryFailure is not null) failures.Add(summaryFailure);
        var kept = failures.Take(MaxFailures).ToArray();
        var newProducts = summaryFailure is null ? NewProducts(summary.NewProducts) : [];
        object Reply(object? list) => new
        {
            catalog = list,
            refreshedAt = refreshedAt.ToUniversalTime(),
            @checked = Products.Reviewed.Count,
            failures = kept,
            newProducts,
        };
        // The catalog gets what the rest of the reply leaves; "null" stands in for it while measuring.
        return Reply(Fit(catalog, ReplyBudgetBytes - (Size(Reply(null)) - 4)));
    }

    private static object? Failure(string? product, string document, ObservationResult observed)
    {
        var state = observed.State switch
        {
            ObjectState.Unavailable => "unavailable",
            ObjectState.Refused => "refused",
            ObjectState.StoreFailed => "storeFailed",
            ObjectState.Unsupported => "unsupported",
            ObjectState.NeedsNetwork => "needsNetwork",
            _ => null,
        };
        if (state is null) return null;
        return new
        {
            productId = product,
            document,
            state,
            code = observed.Code is { } code && CodePattern().IsMatch(code) ? code : null,
            message = Message(observed.Message),
        };
    }

    private static string[] NewProducts(IReadOnlyList<string> products) =>
        products.Where(p => p.Length is >= 1 and <= 64).Take(MaxNewProducts).ToArray();

    private static string? Message(string? text) => string.IsNullOrEmpty(text) ? null : text.Length <= MaxMessage ? text
        : text[..(char.IsHighSurrogate(text[MaxMessage - 1]) ? MaxMessage - 1 : MaxMessage)];
}
