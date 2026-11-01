using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary>
/// One region's listing of a build with one CDN config. A CDN config republished for the same build is a second entry
/// (D061). <see cref="Current"/>: the newest readable versions reply (highest <c>## seqn</c>, then latest observed) from at
/// least one version-service host the catalog has ever read lists exactly this. A host no longer asked keeps its last word.
/// <see cref="LastRecord"/> is the history record of the newest sighting, so the exact source can be read again.
/// </summary>
public sealed record BuildRegion(string Region, string CdnConfig, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Current,
    ObjectKey LastRecord);

/// <summary>
/// One build of one product. <see cref="Id"/> is build identity v2 (product plus every versions field except Region and
/// CDNConfig, D061); <see cref="Fields"/> are those identity fields exactly, so an empty ProductConfig cell and a reply with no
/// ProductConfig column are different builds (<see cref="ProductConfig"/> is null for both). Regions are the raw row values. Locales aren't in a versions reply, so a build
/// here has none yet; opening it (#155) reads them from the build's own manifests.
/// </summary>
public sealed record CatalogBuild(string Product, string Id, string VersionsName, string BuildConfig, string? ProductConfig,
    IReadOnlyDictionary<string, string> Fields, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Current,
    IReadOnlyList<BuildRegion> Regions);

/// <summary>Why a history entry, or one row of it, isn't in the list. <see cref="Row"/> is the reply's data row ordinal (0-based).</summary>
public sealed record CatalogProblem(string Code, string Message, ObjectKey? Record = null, int? Row = null);

/// <summary>
/// The builds of one product, newest first sighting first (then by <see cref="CatalogBuild.Id"/>). Read from the catalog
/// store, which holds what layer 1's history log says; <see cref="LastObservedAt"/> is the newest versions reply for the product.
/// </summary>
public sealed record BuildList(string Product, IReadOnlyList<CatalogBuild> Builds, IReadOnlyList<CatalogProblem> Problems,
    DateTimeOffset? LastObservedAt);

/// <summary>What a refresh observed (versions and cdns, each appended to the history log when it worked) and the list after it.</summary>
public sealed record CatalogRefresh(string Product, ObservationResult Versions, ObservationResult Cdns, BuildList List);

/// <summary>
/// What a summary refresh observed (appended to the history log when it worked) and what it says.
/// <see cref="NewProducts"/>: products the summary lists whose id starts with "wow" and that aren't reviewed, sorted. Their builds
/// are never read; adding one stays a reviewed change (D017). <see cref="Listed"/> is every product the summary lists, sorted.
/// Both are empty when the observation failed or the reply couldn't be read (see <see cref="Problems"/>).
/// </summary>
public sealed record SummaryRefresh(ObservationResult Summary, IReadOnlyList<string> NewProducts, IReadOnlyList<string> Listed,
    IReadOnlyList<CatalogProblem> Problems, string? Sequence);

/// <summary>
/// Layer 3's build catalog. The list comes only from the history log, through layer 2, so it works offline and survives
/// losing the catalog store. Implementations: <see cref="Catalog"/> (SQLite catalog store) and the in-memory fake in
/// <c>VibeMiner.Builds.Testing</c>; one contract suite runs against both.
/// </summary>
public interface ICatalog
{
    /// <summary>
    /// The product's builds, older ones included. Picks up history entries the catalog hasn't read yet first. Never touches
    /// the network. Throws <see cref="ArgumentException"/> for a product that isn't registered.
    /// </summary>
    Task<BuildList> ListBuildsAsync(string product, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the version service for the product's versions and cdns (through layer 2, which appends each reply to the
    /// history log), then lists. A failed observation is reported in the result and the list is still served.
    /// </summary>
    Task<CatalogRefresh> RefreshAsync(string product, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the version service for its summary of every product (through layer 2, which appends the reply to the history log)
    /// and reports the WoW products that aren't reviewed. A failed observation is reported in the result.
    /// </summary>
    Task<SummaryRefresh> RefreshSummaryAsync(CancellationToken cancellationToken);
}
