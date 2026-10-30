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
/// bundled first. Fetched definitions are used by Tables and by the features that read tables (D101), only while the
/// Settings switch reads as on, and only for tables with a bundled definition and a provider name.
/// </summary>
public sealed class TableDefinitionSources(WoWDBDefsDefinitions? fetched = null, ITableDefinitionFetchPolicy? policy = null)
{
    internal async Task<bool> FetchEnabledAsync(CancellationToken token) =>
        fetched is not null && policy is not null && await policy.FetchEnabledAsync(token);

    // The fetch date in the machine's local time, as the desktop shows its dates (#81).
    internal static string Describe(FetchedTableDefinition definition) => Describe(definition.Info);
    internal static string Describe(TableDefinitionFetched definition) =>
        $"WoWDBDefs definition fetched {definition.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({definition.Revision[..7]})";

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

    /// <summary>The last WoWDBDefs check for a table the bundled definition doesn't list exactly (#81), from local state
    /// only, so Tables can say what the last fetch found. Null when fetching doesn't apply or WoWDBDefs was never asked.
    /// The caller has checked the switch.</summary>
    internal async Task<TableDefinitionCheck?> LastCheckAsync(TableCatalogEntry entry, BundledTableDefinition bundled, string build,
        string? layout, CancellationToken token) =>
        fetched is null || entry.Name is null || Exact(bundled.Document, build, layout, token) ? null
            : await fetched.LastCheckAsync(entry.TableId, entry.Name, document => Exact(document, build, layout, token), token);

    /// <summary>Why nothing can read this build or layout, in words that say what still works and what to do next.
    /// Feature names the feature that needs the table (D101); null is the Tables view.</summary>
    internal static DiscoveryException Unusable(TableCatalogEntry entry, string build, string? layout, TableDefinitionChoice choice, bool fetchEnabled,
        string? feature = null)
    {
        var name = entry.Name ?? entry.TableId;
        var lacks = layout is null ? $"doesn’t list build {build} or any layout on its version line" : $"has no block for this file’s layout {layout} on build {build}";
        // Tables says what still opens; a feature says that it can't open, since it needs this table.
        var others = feature is null ? " Tables the bundled definitions cover still open." : "";
        var stops = feature is null ? "this table can’t open" : $"{feature} can’t open on this build";
        var featureStops = feature is null ? "" : $", so {stops}";
        // Which table and how the switch read travel with the failure, so views decide from them, not from the words.
        var problem = new TableDefinitionProblem(entry.TableId, fetchEnabled ? "on" : "off");
        if (!fetchEnabled)
            return new(layout is null ? "SCHEMA_BUILD_NOT_LISTED" : "DB2_SCHEMA_MISSING",
                $"The pinned {name} definition {lacks}{featureStops}. Fetching newer table definitions from WoWDBDefs is off in Settings; turn it on to try the newest {name} definition.{others}")
            { Definition = problem };
        if (choice.Failure is { } failure)
        {
            var next = failure.Code switch
            {
                "DEFINITION_FETCH_OFFLINE" => feature is null
                    ? "Check the connection, then use Retry table data, or turn off Fetch newer table definitions in Settings."
                    : $"Check the connection, then retry {feature}.",
                "DEFINITION_FETCH_FAILED" => feature is null ? "Retry table data asks again; if it still fails, try later." : $"Retrying {feature} asks again; if it still fails, try later.",
                _ => $"A later VibeMiner or WoWDBDefs update may cover it.",
            };
            return new(failure.Code, $"{failure.Message} The pinned {name} definition {lacks}, so {stops}.{others} {next}", failure.Retryable) { Definition = problem };
        }
        var missing = layout is null ? $"lists build {build} or any layout on its version line" : $"has a block for layout {layout} on build {build}";
        return new(layout is null ? "SCHEMA_BUILD_NOT_LISTED" : "DB2_SCHEMA_MISSING", choice.Fetched is null
            ? $"The pinned {name} definition {lacks}{featureStops}.{others}"
            : $"Neither the pinned definition nor the {choice.Fetched.Described} {missing}{featureStops}.{others} A later WoWDBDefs update may cover it.")
            { Definition = problem };
    }

    // D101: features read tables with the definition Tables would choose, under the same switch. These helpers give a
    // feature that choice for the rows it holds, from the cache only, and the same fail-closed start as Tables.

    /// <summary>The bundled entry and definition a feature table needs; a feature only names tables with both.</summary>
    internal static async Task<(TableCatalogEntry Entry, BundledTableDefinition Bundled)> FeatureTableAsync(string tableId, CancellationToken token)
    {
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        var entry = provider.Entries.SingleOrDefault(value => value.TableId == tableId);
        if (entry is null || !provider.Definitions.TryGetValue(tableId, out var bundled))
            throw new DiscoveryException("DEFINITION_NOT_BUNDLED", "This table's definition is not bundled yet.");
        return (entry, bundled);
    }

    /// <summary>The definition chosen now for a feature's admitted rows (D101), as Tables reads them.</summary>
    internal Task<TableDefinition> ForFeatureRowsAsync(string tableId, string build, TableRowsArtifact artifact, CancellationToken token) =>
        ForFeatureLayoutAsync(tableId, build, artifact.Header.LayoutHash, token);

    /// <summary>The same choice for a file's layout, when a feature holds only the rows' snapshot.</summary>
    internal async Task<TableDefinition> ForFeatureLayoutAsync(string tableId, string build, string layout, CancellationToken token)
    {
        var (entry, bundled) = await FeatureTableAsync(tableId, token);
        return (await ChooseAsync(entry, bundled, build, layout, await FetchEnabledAsync(token), null, token)).Definition ?? TableDefinition.Bundled(bundled);
    }

    /// <summary>Admitted rows a feature may use: read with the definition chosen now and the current reader revision for it.
    /// Null when they are stale, so the feature prepares again; never an interpretation error.</summary>
    internal async Task<TableDefinition?> CurrentForFeatureAsync(StoredTableAdmission admission, string tableId, string build, CancellationToken token)
    {
        var (entry, bundled) = await FeatureTableAsync(tableId, token);
        var definition = await ForRowsAsync(entry, bundled, build, admission.Artifact, await FetchEnabledAsync(token), token);
        try { TableDataStore.Validate(admission, entry, definition, bundled, build); }
        catch (DiscoveryException failure) when (failure.Code == "TABLE_INTERPRETATION_STALE") { return null; }
        return definition;
    }

    /// <summary>Before a feature prepares or reads (D101): with fetching off, the bundled definition must list the build
    /// or open it by layout, else it fails as Tables does, naming the Settings switch. With fetching on, preparing decides.</summary>
    internal async Task RequireFeatureBuildAsync(string tableId, string feature, string build, CancellationToken token)
    {
        var (entry, bundled) = await FeatureTableAsync(tableId, token);
        if (!TableDataStore.OpensBuild(bundled.Document, build, token) && !await FetchEnabledAsync(token))
            throw Unusable(entry, build, null, new(null, null, null), false, feature);
    }
}
