using System.Globalization;

namespace VibeMiner.Formats.Definitions;

/// <summary>The Settings switch for fetching newer definitions from WoWDBDefs (D081). Only true reads as on.</summary>
public interface IDefinitionFetchSwitch
{
    Task<bool> FetchEnabledAsync(CancellationToken token);
}

/// <summary>Where a definition comes from: the pinned bundled provider or a WoWDBDefs fetch.</summary>
public enum DefinitionSource { Bundled, Fetched }

/// <summary>Why a fetched definition was chosen over the bundled one (D081).</summary>
public enum FetchedBecause
{
    /// <summary>It lists the exact build and the bundled definition doesn't: exact build first.</summary>
    ExactBuild,
    /// <summary>The bundled definition lists the build but has no block for the file's layout; the fetched one has one,
    /// by build or by layout.</summary>
    LayoutNotBundled,
    /// <summary>Neither lists the build; only its block matches the file's layout on the version line (D080).</summary>
    LayoutMatch,
}

/// <summary>A definition a table can read with. Revision is the bundled provider revision or the fetched commit;
/// Fetched is set only for a fetched definition.</summary>
public sealed record TableDefinition(DefinitionSource Source, string Sha256, string SourceUrl, string Revision, DbdDocument Document,
    FetchedDefinitionInfo? Fetched)
{
    internal static TableDefinition Bundled(BundledDefinition definition) =>
        new(DefinitionSource.Bundled, definition.Sha256, definition.SourceUrl, BundledDefinitions.Revision, definition.Document, null);
    internal static TableDefinition From(FetchedDefinition definition) =>
        new(DefinitionSource.Fetched, definition.Sha256, definition.SourceUrl, definition.Revision, definition.Document, definition.Info);
    /// <summary>How messages name this definition, with dates in <paramref name="zone"/>.</summary>
    internal string Described(TimeZoneInfo zone) => Fetched is null ? "pinned definition" : TableDefinitions.Describe(Fetched, zone);
}

/// <summary>What <see cref="TableDefinitions.ResolveAsync"/> found.</summary>
public enum DefinitionState
{
    /// <summary>A definition reads the build (and the layout, when known).</summary>
    Resolved,
    /// <summary>The pinned provider doesn't list the table id.</summary>
    NotInProvider,
    /// <summary>The bundled file is corrupt, unsupported (a placeholder, say) or missing; nothing is fetched for it.</summary>
    Unreadable,
    /// <summary>Fetching is off and the bundled definition doesn't cover the build or layout.</summary>
    FetchingOff,
    /// <summary>Fetching is on, nothing failed, and no definition lists the build or has the layout.</summary>
    NoMatch,
    /// <summary>A WoWDBDefs fetch failed (DEFINITION_FETCH_*) and nothing local covers the build or layout.</summary>
    FetchFailed,
    /// <summary>The chosen definition selects more than one block.</summary>
    Ambiguous,
    /// <summary>The selected block has a field the WoW rules don't admit (DEFINITION_BLOCK_UNSUPPORTED).</summary>
    BlockUnsupported,
}

/// <summary>
/// One table's definition on one build. Definition is the one chosen (also for Ambiguous and BlockUnsupported), with
/// Because when it is fetched; Fetched is the fetched definition considered, chosen or not. Match and Schema are set when
/// a definition is chosen: with a layout, Schema is its one selected block; without one, the build's block when the build
/// selects exactly one. Problem says why the table can't be read; FetchFailure is a failed fetch even when something
/// local still reads the table. DefinitionFetch is how the switch read ("on" or "off"); Asked is true when this call
/// asked WoWDBDefs. BundledFallback, for a fetched definition with a layout, says whether the bundled definition alone
/// would still read the file by layout.
/// </summary>
public sealed record DefinitionResolution(DefinitionState State, DefinitionTable? Table, TableDefinition? Definition, FetchedBecause? Because,
    TableDefinition? Fetched, SchemaMatch? Match, TableSchema? Schema, DefinitionException? Problem, DefinitionException? FetchFailure,
    string DefinitionFetch, bool Asked, bool? BundledFallback);

/// <summary>One prepare asks WoWDBDefs at most once (D081). The first lookup that asks records its outcome here; every
/// later lookup with the same budget reuses it instead of asking again. Not for concurrent use.</summary>
public sealed class DefinitionFetchBudget
{
    public bool Asked { get; private set; }
    public DefinitionException? Failure { get; private set; }
    internal void Record(DefinitionException? failure) { Asked = true; Failure = failure; }
}

/// <summary>How a resolution may look for a fetched definition: <see cref="Local"/> uses only the cache; <see cref="Ask"/>
/// may ask WoWDBDefs once per budget. A deliberate lookup (a first open or Retry) asks even inside the wait after a
/// failed attempt.</summary>
public sealed record DefinitionLookup
{
    private DefinitionLookup(DefinitionFetchBudget? budget, Func<string, Task>? progress, bool deliberate) =>
        (Budget, Progress, Deliberate) = (budget, progress, deliberate);
    public DefinitionFetchBudget? Budget { get; }
    public Func<string, Task>? Progress { get; }
    public bool Deliberate { get; }
    public static DefinitionLookup Local { get; } = new(null, null, false);
    public static DefinitionLookup Ask(DefinitionFetchBudget budget, Func<string, Task> progress, bool deliberate = false)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(progress);
        return new(budget, progress, deliberate);
    }
}

/// <summary>
/// The layer 5 entry point: picks bundled or fetched definitions (D081). Exact build first: the bundled definition when it
/// lists the build (and the file's layout when known), otherwise a fetched one that lists it. Only when neither does does
/// D080's layout match apply, again bundled first. Fetching applies to every named table (D120), only while the switch
/// reads as on: a null switch or fetched source, false, or a throw read as off (fail closed). A bundled file that is
/// corrupt or unsupported never fetches a replacement. Dates in messages use the clock's local time zone (system by default).
/// </summary>
public sealed class TableDefinitions(BundledDefinitions bundled, FetchedDefinitions? fetched, IDefinitionFetchSwitch? fetchSwitch,
    TimeProvider? clock = null)
{
    private readonly BundledDefinitions bundled = bundled ?? throw new ArgumentNullException(nameof(bundled));
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>How the switch reads now. Cancellation of <paramref name="token"/> propagates; any other failure reads as off.</summary>
    public async Task<bool> FetchEnabledAsync(CancellationToken token)
    {
        if (fetched is null || fetchSwitch is null) return false;
        try { return await fetchSwitch.FetchEnabledAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    // The fetch date in the machine's local time, as the desktop shows its dates (#81).
    internal static string Describe(FetchedDefinitionInfo definition, TimeZoneInfo zone) =>
        $"WoWDBDefs definition fetched {TimeZoneInfo.ConvertTime(definition.FetchedAt, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({definition.Revision[..7]})";

    /// <summary>Lists the build, and when the file's layout is known, a block of that build carries it.</summary>
    internal static bool Exact(DbdDocument document, string build, string? layout, CancellationToken token) =>
        document.ListsBuild(build, token) && (layout is null || document.SelectBuild(build, layout, token).Blocks.Count > 0);

    /// <summary>Doesn't list the build, but opens it by layout on its version line (D080).</summary>
    internal static bool ByLayout(DbdDocument document, string build, string? layout, CancellationToken token) =>
        !document.ListsBuild(build, token) && (layout is null
            ? document.LayoutCandidates(build, token).Count > 0
            : document.SelectFile(build, layout, token).Blocks.Count > 0);

    /// <summary>Lists the build or opens it by some layout on its version line.</summary>
    internal static bool OpensBuild(DbdDocument document, string build, CancellationToken token) =>
        document.ListsBuild(build, token) || document.LayoutCandidates(build, token).Count > 0;

    /// <summary>
    /// The definition and schema a table reads with on <paramref name="build"/>. A null layout means before the file is
    /// read. Feature names the feature that needs the table (D101) for the problem's words; null is the Tables view.
    /// </summary>
    public async Task<DefinitionResolution> ResolveAsync(string tableId, string build, string? layout, WowLocale locale,
        DefinitionLookup lookup, CancellationToken token, string? feature = null)
    {
        Check(build, layout);
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(lookup);
        var enabled = await FetchEnabledAsync(token).ConfigureAwait(false);
        var reading = enabled ? "on" : "off";
        var table = bundled.Tables.FirstOrDefault(entry => entry.TableId == tableId);
        if (table is null)
            return Fail(DefinitionState.NotInProvider, null, new("TABLE_NOT_IN_PROVIDER", "This exact table identity is not listed by the pinned definition provider."));
        // #144: a bundled file that fails its check or doesn't parse stops only this table and never fetches a replacement.
        if (!bundled.TryGet(tableId, out var pinned, out var unreadable))
            return Fail(DefinitionState.Unreadable, table, Problem(table, reading, unreadable ?? NotBundled()));
        var choice = await ChooseAsync(table, pinned, build, layout, enabled, lookup, token).ConfigureAwait(false);
        if (choice.Definition is not { } definition)
        {
            var problem = Unusable(table, build, layout, choice.Fetched, choice.Failure, enabled, feature);
            var state = !enabled ? DefinitionState.FetchingOff : choice.Failure is not null ? DefinitionState.FetchFailed : DefinitionState.NoMatch;
            return new(state, table, null, null, choice.Fetched, null, null, problem, choice.Failure, reading, choice.Asked, null);
        }
        var listed = definition.Document.ListsBuild(build, token);
        var bundledLists = pinned.Document.ListsBuild(build, token);
        DbdSelection? selection = layout is null ? (listed ? definition.Document.SelectBuild(build, token: token) : null)
            : definition.Document.SelectFile(build, layout, token);
        var match = layout is null ? (listed ? SchemaMatch.Build : SchemaMatch.Layout)
            : selection!.Match == "layout" ? SchemaMatch.Layout : SchemaMatch.Build;
        FetchedBecause? because = definition.Source == DefinitionSource.Bundled ? null
            : bundledLists ? FetchedBecause.LayoutNotBundled
            : match == SchemaMatch.Layout ? FetchedBecause.LayoutMatch : FetchedBecause.ExactBuild;
        bool? fallback = definition.Source == DefinitionSource.Fetched && layout is not null ? ByLayout(pinned.Document, build, layout, token) : null;
        DefinitionResolution Result(DefinitionState state, TableSchema? schema, DefinitionException? problem) =>
            new(state, table, definition, because, choice.Fetched, match, schema, problem, choice.Failure, reading, choice.Asked, fallback);
        if (layout is not null && selection!.Blocks.Count > 1)
            return Result(DefinitionState.Ambiguous, null, Problem(table, reading,
                new("DB2_SCHEMA_AMBIGUOUS", "The actual layout and exact build do not select one definition block.")));
        if (selection is not { Blocks.Count: 1 }) return Result(DefinitionState.Resolved, null, null);
        try { return Result(DefinitionState.Resolved, TableSchema.FromBlock(selection.Blocks[0], match, locale), null); }
        catch (DefinitionException refused) when (refused.Code == "DEFINITION_BLOCK_UNSUPPORTED")
        { return Result(DefinitionState.BlockUnsupported, null, Problem(table, reading, refused)); }

        DefinitionResolution Fail(DefinitionState state, DefinitionTable? entry, DefinitionException problem) =>
            new(state, entry, null, null, null, null, null, problem, null, reading, false, null);
    }

    private static DefinitionException Problem(DefinitionTable table, string reading, DefinitionException failure) =>
        new(failure.Code, failure.Message, failure.Retryable) { Definition = new(table.TableId, reading) };

    private sealed record Choice(TableDefinition? Definition, TableDefinition? Fetched, DefinitionException? Failure, bool Asked);

    // The choice itself, as the legacy TableDefinitionSources.ChooseAsync made it.
    private async Task<Choice> ChooseAsync(DefinitionTable table, BundledDefinition pinned, string build, string? layout, bool enabled,
        DefinitionLookup lookup, CancellationToken token)
    {
        var own = TableDefinition.Bundled(pinned);
        if (Exact(pinned.Document, build, layout, token)) return new(own, null, null, false);
        if (!enabled || fetched is null || table.Name is not { } name)
            return new(ByLayout(pinned.Document, build, layout, token) ? own : null, null, null, false);
        FetchedDefinition? found; DefinitionException? failure = null; var asked = false;
        if (lookup.Budget is not { } budget || lookup.Progress is not { } progress) found = await fetched.CachedAsync(table.TableId, name, token).ConfigureAwait(false);
        else if (budget.Asked) { found = await fetched.CachedAsync(table.TableId, name, token).ConfigureAwait(false); failure = budget.Failure; }
        else
        {
            var outcome = await fetched.EnsureAsync(table.TableId, name, document => Exact(document, build, layout, token), progress, token, lookup.Deliberate)
                .ConfigureAwait(false);
            (found, failure, asked) = (outcome.Definition, outcome.Failure, outcome.Asked);
            if (asked) budget.Record(failure);
        }
        var other = found is null ? null : TableDefinition.From(found);
        if (other is not null && Exact(other.Document, build, layout, token)) return new(other, other, failure, asked);
        if (ByLayout(pinned.Document, build, layout, token)) return new(own, other, failure, asked);
        if (other is not null && ByLayout(other.Document, build, layout, token)) return new(other, other, failure, asked);
        return new(null, other, failure, asked);
    }

    /// <summary>True when rows read without an exact bundled definition should be prepared again because WoWDBDefs is due
    /// to be asked: a cache that doesn't list the build after the minimum gap, or any cache after the refresh interval.
    /// Local only. False when fetching is off or doesn't apply.</summary>
    public async Task<bool> RefreshDueAsync(string tableId, string build, string layout, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Check(build, layout);
        if (fetched is null || Named(tableId) is not { } table || !bundled.TryGet(tableId, out var pinned, out _) ||
            Exact(pinned.Document, build, layout, token) || !await FetchEnabledAsync(token).ConfigureAwait(false)) return false;
        return await fetched.RefreshDueAsync(tableId, table.Name!, document => Exact(document, build, layout, token), token).ConfigureAwait(false);
    }

    /// <summary>The last WoWDBDefs check for a table the bundled definition doesn't list exactly (#81, D113), from local
    /// state only. Null when fetching is off or doesn't apply, or WoWDBDefs was never asked.</summary>
    public async Task<DefinitionCheck?> LastCheckAsync(string tableId, string build, string? layout, CancellationToken token)
    {
        Check(build, layout);
        if (fetched is null || Named(tableId) is not { } table || !bundled.TryGet(tableId, out var pinned, out _) ||
            Exact(pinned.Document, build, layout, token) || !await FetchEnabledAsync(token).ConfigureAwait(false)) return null;
        return await fetched.LastCheckAsync(tableId, table.Name!, document => Exact(document, build, layout, token), token).ConfigureAwait(false);
    }

    /// <summary>Before a feature prepares or reads (D101): with fetching off, the bundled definition must list the build or
    /// open it by layout, else this throws as Tables would, naming the feature and the Settings switch, before anything is
    /// downloaded or fetched. With fetching on, preparing decides. An unknown table throws DEFINITION_NOT_BUNDLED and an
    /// unreadable bundled file its own failure.</summary>
    public async Task RequireBuildAsync(string tableId, string feature, string build, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(feature);
        Check(build, null);
        var table = bundled.Tables.FirstOrDefault(entry => entry.TableId == tableId) ?? throw NotBundled();
        var pinned = bundled.Require(tableId);
        if (!OpensBuild(pinned.Document, build, token) && !await FetchEnabledAsync(token).ConfigureAwait(false))
            throw Unusable(table, build, null, null, null, false, feature);
    }

    private DefinitionTable? Named(string tableId) => bundled.Tables.FirstOrDefault(entry => entry.TableId == tableId && entry.Name is not null);

    private static DefinitionException NotBundled() => new("DEFINITION_NOT_BUNDLED", "This table's definition is not bundled yet.");

    private static void Check(string build, string? layout)
    {
        try { DbdBuildVersion.Parse(build); }
        catch (Db2FormatException) { throw new ArgumentException("An exact four-component numeric build is required.", nameof(build)); }
        if (layout is not null && !DbdDefinitionReader.IsLayout(layout))
            throw new ArgumentException("A complete eight-hex layout is required.", nameof(layout));
    }

    /// <summary>Why nothing can read this build or layout, in words that say what still works and what to do next.
    /// Feature names the feature that needs the table (D101); null is the Tables view.</summary>
    internal DefinitionException Unusable(DefinitionTable table, string build, string? layout, TableDefinition? considered,
        DefinitionException? failed, bool fetchEnabled, string? feature = null)
    {
        var name = table.Name ?? table.TableId;
        var lacks = layout is null ? $"doesn’t list build {build} or any layout on its version line" : $"has no block for this file’s layout {layout} on build {build}";
        // Tables says what still opens; a feature says that it can't open, since it needs this table.
        var others = feature is null ? " Tables the bundled definitions cover still open." : "";
        var stops = feature is null ? "this table can’t open" : $"{feature} can’t open on this build";
        var featureStops = feature is null ? "" : $", so {stops}";
        // Which table and how the switch read travel with the failure, so views decide from them, not from the words.
        var problem = new DefinitionProblem(table.TableId, fetchEnabled ? "on" : "off");
        if (!fetchEnabled)
            return new(layout is null ? "SCHEMA_BUILD_NOT_LISTED" : "DB2_SCHEMA_MISSING",
                $"The pinned {name} definition {lacks}{featureStops}. Fetching newer table definitions from WoWDBDefs is off in Settings; turn it on to try the newest {name} definition.{others}")
            { Definition = problem };
        if (failed is { } failure)
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
        return new(layout is null ? "SCHEMA_BUILD_NOT_LISTED" : "DB2_SCHEMA_MISSING", considered is null
            ? $"The pinned {name} definition {lacks}{featureStops}.{others}"
            : $"Neither the pinned definition nor the {considered.Described(time.LocalTimeZone)} {missing}{featureStops}.{others} A later WoWDBDefs update may cover it.")
            { Definition = problem };
    }
}
