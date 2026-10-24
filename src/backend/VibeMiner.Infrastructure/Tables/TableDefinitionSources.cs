using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>The definition a table is read with on one build: bundled, or fetched from WoWDBDefs (D081).
/// Revision is the bundled provider revision or the fetched definition's commit.</summary>
internal sealed record TableDefinition(string Sha256, string SourceUrl, string Revision, DbdDocument Document, FetchedTableDefinition? Fetched)
{
    internal static TableDefinition Bundled(BundledTableDefinition definition) =>
        new(definition.Sha256, definition.SourceUrl, BundledTableDefinitions.Revision, definition.Document, null);
    internal static TableDefinition From(FetchedTableDefinition definition) =>
        new(definition.Sha256, definition.SourceUrl, definition.Revision, definition.Document, definition);
    /// <summary>How messages name this definition.</summary>
    internal string Described => Fetched is null ? "pinned definition" : TableDefinitionSources.Describe(Fetched);
}

/// <summary>What <see cref="TableDefinitionSources.ChooseAsync"/> found. Definition is null when nothing can read the
/// build (or the file's layout); Fetched is the fetched definition it considered, and Failure why a fetch didn't happen.
/// Asked is true when it contacted WoWDBDefs.</summary>
internal sealed record TableDefinitionChoice(TableDefinition? Definition, TableDefinition? Fetched, DiscoveryException? Failure, bool Asked = false);

/// <summary>One prepare asks WoWDBDefs at most once (D081). The first check that asks records its outcome here;
/// every later check in the same prepare reuses it instead of asking again.</summary>
internal sealed class TableDefinitionFetchBudget
{
    internal bool Asked { get; private set; }
    internal DiscoveryException? Failure { get; private set; }
    internal void Record(DiscoveryException? failure) { Asked = true; Failure = failure; }
}

/// <summary>
/// Picks bundled or fetched definitions (D081). Exact build first: the bundled definition when it lists the build,
/// otherwise a fetched one that lists it. Only when neither lists the build does #54's layout match (D080) apply, again
/// bundled first. Fetched definitions are used only by Tables, only while the Settings switch reads as on, and only for
/// tables with a bundled definition and a provider name. Everything else uses bundled definitions and no network.
/// </summary>
public sealed class TableDefinitionSources(WoWDBDefsDefinitions? fetched = null, ITableDefinitionFetchPolicy? policy = null)
{
    internal async Task<bool> FetchEnabledAsync(CancellationToken token) =>
        fetched is not null && policy is not null && await policy.FetchEnabledAsync(token);

    internal static string Describe(FetchedTableDefinition definition) =>
        $"WoWDBDefs definition fetched {definition.FetchedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({definition.Revision[..7]})";

    /// <summary>Lists the build, and when the file's layout is known, a block of that build carries it.</summary>
    internal static bool Exact(DbdDocument document, string build, string? layout, CancellationToken token) =>
        document.ListsBuild(build, token) && (layout is null || document.SelectBuild(build, layout, token).Blocks.Count > 0);

    /// <summary>Doesn't list the build, but opens it by layout on its version line (D080).</summary>
    internal static bool ByLayout(DbdDocument document, string build, string? layout, CancellationToken token) =>
        !document.ListsBuild(build, token) && (layout is null
            ? document.LayoutCandidates(build, token).Count > 0
            : document.SelectFile(build, layout, token).Blocks.Count > 0);

    /// <summary>
    /// The definition to read with. A null layout means before the file is read. With a budget, a missing or not
    /// covering fetched definition is asked for upstream, once per budget; without one the choice is local and uses only
    /// the cache. A deliberate check (a first open or Retry) asks even inside the wait after a failed attempt.
    /// </summary>
    internal async Task<TableDefinitionChoice> ChooseAsync(TableCatalogEntry entry, BundledTableDefinition bundled, string build,
        string? layout, bool fetchEnabled, Func<string, Task>? progress, CancellationToken token, TableDefinitionFetchBudget? budget = null,
        bool deliberate = false)
    {
        var own = TableDefinition.Bundled(bundled);
        if (Exact(bundled.Document, build, layout, token)) return new(own, null, null);
        if (!fetchEnabled || fetched is null || entry.Name is null)
            return new(ByLayout(bundled.Document, build, layout, token) ? own : null, null, null);
        FetchedTableDefinition? found; DiscoveryException? failure = null; var asked = false;
        if (progress is null || budget is null) found = await fetched.CachedAsync(entry.TableId, entry.Name, token);
        else if (budget.Asked) { found = await fetched.CachedAsync(entry.TableId, entry.Name, token); failure = budget.Failure; }
        else
        {
            var outcome = await fetched.EnsureAsync(entry.TableId, entry.Name, document => Exact(document, build, layout, token), progress, token, deliberate);
            (found, failure, asked) = (outcome.Definition, outcome.Failure, outcome.Asked);
            if (asked) budget.Record(failure);
        }
        var other = found is null ? null : TableDefinition.From(found);
        if (other is not null && Exact(other.Document, build, layout, token)) return new(other, other, failure, asked);
        if (ByLayout(bundled.Document, build, layout, token)) return new(own, other, failure, asked);
        if (other is not null && ByLayout(other.Document, build, layout, token)) return new(other, other, failure, asked);
        return new(null, other, failure, asked);
    }

    /// <summary>True when rows read without an exact bundled definition should be prepared again because WoWDBDefs is
    /// due to be asked: a cache that doesn't list the build after the minimum gap, or any cache after the refresh
    /// interval. Local only; prepare then applies the same rules as a first load.</summary>
    internal async Task<bool> RefreshDueAsync(TableCatalogEntry entry, BundledTableDefinition bundled, string build, string layout,
        bool fetchEnabled, CancellationToken token) =>
        fetchEnabled && fetched is not null && entry.Name is not null && !Exact(bundled.Document, build, layout, token) &&
        await fetched.RefreshDueAsync(entry.TableId, entry.Name, document => Exact(document, build, layout, token), token);

    /// <summary>For admitted rows: the definition chosen now for their layout, from the cache only. Rows read with any
    /// other definition are then stale for the caller, never an interpretation error.</summary>
    internal async Task<TableDefinition> ForRowsAsync(TableCatalogEntry entry, BundledTableDefinition bundled, string build,
        TableRowsArtifact artifact, bool fetchEnabled, CancellationToken token) =>
        (await ChooseAsync(entry, bundled, build, artifact.Header.LayoutHash, fetchEnabled, null, token)).Definition ?? TableDefinition.Bundled(bundled);

    /// <summary>Before any file is read, from the cache only: what Structure shows for a table without admitted rows.</summary>
    internal async Task<TableDefinition> BeforeFileAsync(TableCatalogEntry entry, BundledTableDefinition bundled, string build,
        bool fetchEnabled, CancellationToken token) =>
        (await ChooseAsync(entry, bundled, build, null, fetchEnabled, null, token)).Definition ?? TableDefinition.Bundled(bundled);

    /// <summary>Why nothing can read this build or layout, in words that say what still works and what to do next.</summary>
    internal static DiscoveryException Unusable(TableCatalogEntry entry, string build, string? layout, TableDefinitionChoice choice, bool fetchEnabled)
    {
        var name = entry.Name ?? entry.TableId;
        var lacks = layout is null ? $"doesn’t list build {build} or any layout on its version line" : $"has no block for this file’s layout {layout} on build {build}";
        var others = "Tables the bundled definitions cover still open.";
        if (!fetchEnabled)
            return new(layout is null ? "SCHEMA_BUILD_NOT_LISTED" : "DB2_SCHEMA_MISSING",
                $"The pinned {name} definition {lacks}. Fetching newer table definitions from WoWDBDefs is off in Settings; turn it on to try the newest {name} definition. {others}");
        if (choice.Failure is { } failure)
        {
            var next = failure.Code switch
            {
                "DEFINITION_FETCH_OFFLINE" => "Check the connection, then use Retry table data, or turn off Fetch newer table definitions in Settings.",
                "DEFINITION_FETCH_FAILED" => "Retry table data asks again; if it still fails, try later.",
                _ => $"A later VibeMiner or WoWDBDefs update may cover it.",
            };
            return new(failure.Code, $"{failure.Message} The pinned {name} definition {lacks}, so this table can’t open. {others} {next}", failure.Retryable);
        }
        var missing = layout is null ? $"lists build {build} or any layout on its version line" : $"has a block for layout {layout} on build {build}";
        return new(layout is null ? "SCHEMA_BUILD_NOT_LISTED" : "DB2_SCHEMA_MISSING", choice.Fetched is null
            ? $"The pinned {name} definition {lacks}. {others}"
            : $"Neither the pinned definition nor the {choice.Fetched.Described} {missing}. {others} A later WoWDBDefs update may cover it.");
    }
}
