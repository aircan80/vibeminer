using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// Layer 5 for one view load: the tables and what closes them (the store and anything else the composition root opened for
/// them). A load opens one, uses it and disposes it before it returns, so nothing stays open between keys.
/// </summary>
internal sealed class TablesLease(ITables tables, Func<ValueTask>? close = null) : IAsyncDisposable
{
    public ITables Tables { get; } = tables;
    public ValueTask DisposeAsync() => close?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>What the <c>:tables</c> views share: opening one build and locale's tables for a load, and the words they show.</summary>
internal static class TablesAccess
{
    public const string Locale = "enUS";
    public const string NotWired = "Tables aren't available in vm browse yet: layer 5 isn't wired into the CLI.";
    /// <summary>What a grid shows for an absent external parent (D291) instead of its "" placeholder: no parent, never 0.</summary>
    public const string NoParent = "(no parent)";

    /// <summary>The row's cells as shown: an absent external parent reads <see cref="NoParent"/>, every other cell as is.</summary>
    public static IReadOnlyList<string> Shown(Db2Row row) => row.TextIssues is not { Count: > 0 } issues ? row.Values
        : [.. row.Values.Select((value, i) => issues.Any(t => t.ColumnIndex == i && t.Code == Db2Reader.RelationAbsent) ? NoParent : value)];

    /// <summary>
    /// Opens the build's tables through <see cref="BrowseSession.OpenTables"/>, runs <paramref name="load"/> on them and closes
    /// everything before returning. A missing seam or an open that isn't Open is a note, never an exception.
    /// </summary>
    public static async Task<ViewContent> WithTablesAsync(BrowseSession session, BuildRef build, string locale,
        Func<IBuildTables, Task<ViewContent>> load, CancellationToken ct)
    {
        if (session.OpenTables is not { } open) return new([], [], NotWired);
        await using var lease = await open(session, ct).ConfigureAwait(false);
        var opened = await lease.Tables.ForAsync(build, locale, ct).ConfigureAwait(false);
        if (opened is not { State: FilesOpenState.Open, Tables: { } tables })
            return new([], [], $"Can't open the build's files for {locale}: {opened.State} ({opened.Code ?? "no code"}){(opened.Message is { } m ? $": {m}" : "")}");
        await using (tables) return await load(tables).ConfigureAwait(false);
    }

    public static string Name(TableEntry entry) => entry.Name ?? "-";

    /// <summary>The breadcrumb for a table: its definition name, else its FileDataID.</summary>
    public static string Title(TableEntry entry) => entry.Name ?? Fdid(entry.FileDataId);

    public static string Fdid(uint id) => id.ToString(CultureInfo.InvariantCulture);

    public static string Rows(TableCounts? counts) => counts is null ? "-"
        : string.Create(CultureInfo.InvariantCulture, $"{counts.ReadableRows:N0}/{counts.DeclaredRows:N0}");

    /// <summary>
    /// The 1-based rows a page after <paramref name="after"/> shows, e.g. <c>101-120</c>: at most a page, never past
    /// <paramref name="total"/> when it is known.
    /// </summary>
    public static string Range(long after, long? total)
    {
        var last = after + 1 + TableQueries.DefaultLimit;
        if (total is { } known && known > after + 1) last = Math.Min(last, known);
        return string.Create(CultureInfo.InvariantCulture, $"{after + 2:N0}-{last:N0}");
    }

    public static string Reference(ForeignKey key) => $"{key.Table}.{key.Column}";

    /// <summary>The schema column a flattened header name (<c>Flags[1]</c>) comes from, if the schema has it.</summary>
    public static SchemaColumn? SchemaColumnOf(TableSchema? schema, string flattened)
    {
        if (schema is null) return null;
        var bracket = flattened.IndexOf('[', StringComparison.Ordinal);
        var name = bracket < 0 ? flattened : flattened[..bracket];
        return schema.Columns.FirstOrDefault(c => TableQueries.NamesEqual(c.Name, name));
    }

    /// <summary>The value columns of a table: the header's once opened, else the schema flattened, else none.</summary>
    public static IReadOnlyList<string> ColumnNames(TableSchemaResult schema) =>
        schema.Columns is { } header ? header.Select(c => c.Name).ToArray()
        : schema.Schema is { } typed ? typed.Columns.SelectMany(c => c.IsArray ? Enumerable.Range(0, c.ArrayLength).Select(i => $"{c.Name}[{i}]") : [c.Name]).ToArray()
        : [];

    /// <summary>Field/value rows saying why a table can't be read: its state, whether checked, its reason and counts.</summary>
    public static List<BrowseRow> StateRows(TableEntry entry)
    {
        var rows = new List<BrowseRow>
        {
            new(["FileDataID", Fdid(entry.FileDataId)]), new(["Name", Name(entry)]), new(["State", entry.State.ToString()]),
            new(["Checked", entry.Checked ? "yes" : "no"]), new(["Rows", Rows(entry.Counts)]),
            new(["Reason code", entry.Reason?.Code ?? "-"]), new(["Message", entry.Reason?.Message ?? "-"]),
        };
        if (entry.Reason?.ObservedAt is { } at) rows.Add(new(["Observed", $"{BrowseSession.Time(at)} UTC"]));
        if (entry.Reason?.DefinitionState is { } definition) rows.Add(new(["Definition", definition.ToString()]));
        if (entry.Reason?.Db2OpenState is { } db2) rows.Add(new(["DB2 open", db2.ToString()]));
        if (entry.Reason is { KeyNames.Count: > 0 } reason) rows.Add(new(["Keys needed", string.Join(' ', reason.KeyNames)]));
        if (entry.ContentKey is { } ckey) rows.Add(new(["Content key", ckey]));
        return rows;
    }

    public static readonly string[] FieldValue = ["Field", "Value"];
}

/// <summary>
/// <c>:tables</c>: the current builds of every reviewed product, from the catalog (read-only, as <c>:builds</c>), each in the
/// region <see cref="BrowseSession.ChooseRegionAsync"/> picks. Enter lists a build's tables in enUS.
/// </summary>
internal sealed class TablesView : BrowseView
{
    public override string Title => "tables";

    private static readonly string[] Columns = ["Product", "Version", "Region", "Build config"];

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        IStoreHandle store;
        try { store = await session.OpenReadAsync(ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Missing)
        {
            return new(Columns, [], $"No object store at {session.StoreFolder} yet. r in :builds fetches builds and creates it.");
        }
        var rows = new List<BrowseRow>();
        await using (store)
        {
            var catalog = await store.CatalogAsync(ct).ConfigureAwait(false);
            foreach (var product in Products.Reviewed)
            {
                var list = await catalog.ListBuildsAsync(product.Id, ct).ConfigureAwait(false);
                foreach (var build in list.Builds.Where(b => b.Current))
                {
                    // The region f, d and a open in (D280): the first with a usable CDN route.
                    if ((await session.ChooseRegionAsync(store.Objects, build, ct).ConfigureAwait(false)).Region is not { } region)
                    {
                        rows.Add(new([product.Id, build.VersionsName, "-", build.BuildConfig]));
                        continue;
                    }
                    var buildRef = new BuildRef(build.Product, build.BuildConfig, region.CdnConfig, region.Region);
                    var title = $"{product.Id} {build.VersionsName}";
                    rows.Add(new([product.Id, build.VersionsName, region.Region, build.BuildConfig],
                        () => new TableListView(buildRef, title, TablesAccess.Locale)));
                }
            }
        }
        var note = rows.Count == 0 ? "No current builds in the history log. r in :builds fetches them."
            : $"{rows.Count:N0} current builds. Enter lists a build's tables ({TablesAccess.Locale}).";
        if (session.OpenTables is null) note += " " + TablesAccess.NotWired;
        return new(Columns, rows, note);
    }
}

/// <summary>
/// One build and locale's tables, in their listed (shallow) state: one row per table, ascending by FileDataID. The note counts
/// them per state. Enter opens a table.
/// </summary>
internal sealed class TableListView(BuildRef build, string title, string locale) : BrowseView
{
    /// <summary>The most tables one load lists; a build has about two thousand.</summary>
    internal const int MaxListed = 20_000;

    public override string Title => $"{title} {locale}";

    private static readonly string[] Columns = ["FDID", "Name", "State", "Checked", "Rows", "Reason"];

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct) =>
        TablesAccess.WithTablesAsync(session, build, locale, async tables =>
        {
            var rows = new List<BrowseRow>();
            var counts = new SortedDictionary<TableState, int>();
            var more = false;
            await foreach (var entry in tables.ListAsync(TableListFilter.All, TablePriority.Interactive, ct).ConfigureAwait(false))
            {
                if (rows.Count == MaxListed) { more = true; break; }
                counts[entry.State] = counts.GetValueOrDefault(entry.State) + 1;
                rows.Add(new([TablesAccess.Fdid(entry.FileDataId), TablesAccess.Name(entry), entry.State.ToString(), entry.Checked ? "yes" : "no",
                    TablesAccess.Rows(entry.Counts), entry.Reason?.Code ?? ""], () => new TableRowsView(build, locale, entry)));
            }
            var note = rows.Count == 0 ? "No tables listed for this build."
                : $"{rows.Count:N0} tables: {string.Join(", ", counts.Select(c => $"{c.Value:N0} {c.Key}"))}."
                  + (more ? $" Only the first {MaxListed:N0} are listed." : "") + " Enter opens a table.";
            return new ViewContent(Columns, rows, note);
        }, ct);
}

/// <summary>
/// One table: a row that opens its schema, then one page of rows (<see cref="TableQueries.DefaultLimit"/>), a column per value,
/// and a "more" row that opens the next page. Enter on a row opens it. A table that can't be read shows its state and reason.
/// A later page passes back the identity the first gave, so a table that changed in between says Stale instead of re-basing.
/// </summary>
internal sealed class TableRowsView(BuildRef build, string locale, TableEntry entry, long after = -1, string? identity = null) : BrowseView
{
    public override string Title => after < 0 ? TablesAccess.Title(entry)
        : $"rows {TablesAccess.Range(after, entry.Counts?.ReadableRows)}";

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct) =>
        TablesAccess.WithTablesAsync(session, build, locale, async tables =>
        {
            var table = TableRef.Id(entry.FileDataId);
            var schema = await tables.SchemaAsync(table, TablePriority.Interactive, ct).ConfigureAwait(false);
            if (!TableQueries.IsReadable(schema.Table.State))
                return new ViewContent(TablesAccess.FieldValue, TablesAccess.StateRows(schema.Table),
                    $"{TablesAccess.Title(schema.Table)} can't be read: {schema.Table.State}{(schema.Table.Reason?.Code is { } code ? $" ({code})" : "")}.");
            var page = await tables.PageAsync(new TablePageRequest(table, after, TableQueries.DefaultLimit, Identity: identity),
                TablePriority.Interactive, ct).ConfigureAwait(false);
            var names = TablesAccess.ColumnNames(schema);
            var width = Math.Max(1, Math.Max(names.Count, page.Rows.Count == 0 ? 0 : page.Rows.Max(r => r.Row.Values.Count)));
            var columns = Enumerable.Range(0, width).Select(i => i < names.Count ? names[i] : $"#{i}").ToArray();
            var rows = new List<BrowseRow>
            {
                new(Pad(["(schema)", $"{(schema.Schema?.Columns.Count ?? schema.Columns?.Count ?? 0):N0} columns; Enter shows them"], width),
                    () => new TableSchemaView(schema)),
            };
            if (page.State != TablePageState.Ok)
                return new ViewContent(columns, rows, page.State switch
                {
                    TablePageState.Stale => "The table changed since the first page (Stale). Esc back to the first page and reopen it.",
                    TablePageState.InvalidCursor => "No rows past this point (InvalidCursor).",
                    _ => $"No rows: {page.State}{(page.Code is { } c ? $" ({c})" : "")}{(page.Message is { } m ? $": {m}" : "")}.",
                });
            foreach (var row in page.Rows)
            {
                var id = row.Row.Id;
                rows.Add(new(Pad(TablesAccess.Shown(row.Row), width), () => new TableRowView(build, locale, schema.Table, id)));
            }
            if (page.NextAfter is { } next)
                rows.Add(new(Pad(["(more)", $"Enter shows rows {TablesAccess.Range(next, schema.Table.Counts?.ReadableRows)}"], width),
                    () => new TableRowsView(build, locale, schema.Table, next, page.Identity)));
            var shown = page.Rows.Count == 0 ? "no rows"
                : string.Create(CultureInfo.InvariantCulture, $"rows {page.Rows[0].Ordinal + 1:N0}-{page.Rows[^1].Ordinal + 1:N0}");
            var note = $"{TablesAccess.Title(schema.Table)} ({TablesAccess.Fdid(schema.Table.FileDataId)}): {schema.Table.State}, {TablesAccess.Rows(schema.Table.Counts)} rows readable/declared; {shown}."
                + (schema.Table.State == TableState.Partial ? " Partial: keyed sections are left out." : "")
                + (page.NextAfter is null ? "" : " Enter on (more) shows the next page.");
            return new ViewContent(columns, rows, note);
        }, ct);

    private static string[] Pad(IReadOnlyList<string> cells, int width) =>
        Enumerable.Range(0, width).Select(i => i < cells.Count ? cells[i] : "").ToArray();
}

/// <summary>A table's columns: the resolved schema's when a definition resolves, else the header's. Shows what a load already read.</summary>
internal sealed class TableSchemaView(TableSchemaResult schema) : BrowseView
{
    public override string Title => "schema";

    private static readonly string[] Columns = ["Column", "Kind", "Bits", "Array", "Key", "References"];

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        if (schema.Schema is { } typed)
        {
            var rows = typed.Columns.Select(c => new BrowseRow([
                c.Name + (c.VerifiedName ? "" : "?"), c.Kind.ToString(),
                c.Bits is { } bits ? $"{bits} {(c.Unsigned == true ? "unsigned" : "signed")}" : "",
                c.IsArray ? $"[{c.ArrayLength}]" : "",
                c.Id != KeyStorage.None ? (c.Id == KeyStorage.NonInline ? "id, noninline" : "id")
                    : c.Relation != KeyStorage.None ? (c.Relation == KeyStorage.NonInline ? "relation, noninline" : "relation") : "",
                c.ForeignKey is { } fk ? TablesAccess.Reference(fk) : ""])).ToList();
            return Task.FromResult(new ViewContent(Columns, rows,
                $"{typed.Columns.Count:N0} columns, matched by {typed.Match}, strings in {typed.Locale}." + (typed.Comment is { } note ? $" {note}" : "")));
        }
        var header = (schema.Columns ?? []).Select(c => new BrowseRow([c.Name, c.Kind, "", "", c.IsId ? "id" : "", ""])).ToList();
        return Task.FromResult(new ViewContent(Columns, header, "No definition resolves for this build: the header's columns only, no references."));
    }
}

/// <summary>
/// One row by ID: its column/value pairs, copy info and text issues. A column with a declared foreign key shows its target;
/// Enter on it follows the reference. The last row lists the rows that point at this one (background priority).
/// </summary>
internal sealed class TableRowView(BuildRef build, string locale, TableEntry table, uint id) : BrowseView
{
    public override string Title => $"ID {id.ToString(CultureInfo.InvariantCulture)}";

    private static readonly string[] Columns = ["Column", "Value", "References"];

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct) =>
        TablesAccess.WithTablesAsync(session, build, locale, async tables =>
        {
            var tableRef = TableRef.Id(table.FileDataId);
            var schema = await tables.SchemaAsync(tableRef, TablePriority.Interactive, ct).ConfigureAwait(false);
            var got = await tables.GetAsync(tableRef, id, TablePriority.Interactive, ct).ConfigureAwait(false);
            if (got is not { Outcome: TableRowOutcome.Found, Row: { } found })
            {
                var rows = new List<BrowseRow> { new(["Outcome", got.Outcome.ToString(), ""]) };
                if (got.ProtectedKey is { } key) rows.Add(new(["Key", key, ""]));
                rows.AddRange(TablesAccess.StateRows(got.Table).Select(r => new BrowseRow([r.Cells[0], r.Cells[1], ""])));
                return new ViewContent(Columns, rows, got.Outcome switch
                {
                    TableRowOutcome.Absent => $"No row {id} in {TablesAccess.Title(got.Table)}; the table is complete, so it is absent.",
                    TableRowOutcome.Protected => $"Row {id} is in an encrypted section (key {got.ProtectedKey}).",
                    _ => $"Row {id} is unknown: not in the readable rows of {TablesAccess.Title(got.Table)} ({got.Table.State}). That isn't proof it's absent.",
                });
            }
            var row = found.Row;
            var names = TablesAccess.ColumnNames(schema);
            var list = new List<BrowseRow>
            {
                new(["(ID)", row.Id.ToString(CultureInfo.InvariantCulture), ""]),
                new(["(ordinal)", found.Ordinal.ToString(CultureInfo.InvariantCulture), ""]),
                new(["(copy)", row.IsCopy ? $"copy of {row.CopySourceId?.ToString(CultureInfo.InvariantCulture) ?? "?"}" : "no", ""]),
            };
            var shown = TablesAccess.Shown(row);
            for (var i = 0; i < row.Values.Count; i++)
            {
                var name = i < names.Count ? names[i] : $"#{i}";
                var value = shown[i];
                if (TablesAccess.SchemaColumnOf(schema.Schema, name)?.ForeignKey is { } fk)
                {
                    var column = name;
                    list.Add(new([name, value, $"{TablesAccess.Reference(fk)}; Enter follows"], () => new TableFollowView(build, locale, table, id, column, fk)));
                }
                else list.Add(new([name, value, ""]));
            }
            foreach (var issue in row.TextIssues ?? [])
                list.Add(new(["(text issue)", $"{(issue.ColumnIndex < names.Count ? names[issue.ColumnIndex] : $"#{issue.ColumnIndex}")}: {issue.Code}",
                    issue.Code == Db2Reader.RelationAbsent ? "no parent (not 0)" : $"raw {issue.RawBytesHex}"]));
            list.Add(new(["(referenced by)", "rows of other tables that point here", "Enter searches"], () => new TableReferencedByView(build, locale, table, id)));
            var note = $"{TablesAccess.Title(got.Table)} row {id}." + (row.TextIssues is { Count: > 0 } issues ? $" {issues.Count} text issue(s)." : "")
                + (schema.Schema is null ? " No definition resolves, so no references." : "");
            return new ViewContent(Columns, list, note);
        }, ct);
}

/// <summary>
/// Follows one foreign key column of a row: the target's matching rows (Enter opens one), or the outcome when there are none.
/// Several matches page like a table, with a "more" row.
/// </summary>
internal sealed class TableFollowView(BuildRef build, string locale, TableEntry table, uint id, string column, ForeignKey reference, long after = -1,
    long? targetRows = null) : BrowseView
{
    public override string Title => after < 0 ? $"{column} -> {TablesAccess.Reference(reference)}"
        : $"matches {TablesAccess.Range(after, targetRows)}";

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct) =>
        TablesAccess.WithTablesAsync(session, build, locale, async tables =>
        {
            var result = await tables.FollowAsync(new TableFollowRequest(TableRef.Id(table.FileDataId), id, column, after, TableQueries.DefaultLimit),
                TablePriority.Interactive, ct).ConfigureAwait(false);
            var target = result.Reference is { } declared ? TablesAccess.Reference(declared) : TablesAccess.Reference(reference);
            if (result.Outcome != TableFollowOutcome.Found)
            {
                var rows = new List<BrowseRow> { new(["Outcome", result.Outcome.ToString()]), new(["Reference", target]) };
                if (result.Target is { } entry)
                {
                    rows.Add(new(["Target", $"{TablesAccess.Title(entry)} ({TablesAccess.Fdid(entry.FileDataId)})"]));
                    rows.Add(new(["Target state", entry.State.ToString()]));
                    if (entry.Reason?.Code is { } reason) rows.Add(new(["Target reason", reason]));
                }
                if (result.Code is { } code) rows.Add(new(["Code", code]));
                if (result.Message is { } message) rows.Add(new(["Message", message]));
                if (result.Outcome == TableFollowOutcome.SourceRow) rows.Add(new(["Source row", result.Source.Outcome.ToString()]));
                return new ViewContent(TablesAccess.FieldValue, rows, result.Outcome switch
                {
                    TableFollowOutcome.Absent => $"No row of {target} matches; the target is complete, so the reference points nowhere.",
                    TableFollowOutcome.Unknown => $"No match in the readable rows of {target} ({result.Target?.State.ToString() ?? "not looked at"}); that isn't proof it's absent.",
                    TableFollowOutcome.KeyOutOfRange => $"The value doesn't fit {target}'s declared width and sign, so nothing can match.",
                    TableFollowOutcome.NotAReference => $"{column} declares no foreign key in the resolved schema.",
                    TableFollowOutcome.SourceRow => $"Row {id} itself wasn't found ({result.Source.Outcome}).",
                    TableFollowOutcome.Unindexed => $"{target} isn't keyed by that column and has too many rows to scan.",
                    _ => result.Outcome.ToString(),
                });
            }
            var targetEntry = result.Target;
            var names = targetEntry is null ? [] : TablesAccess.ColumnNames(
                await tables.SchemaAsync(TableRef.Id(targetEntry.FileDataId), TablePriority.Interactive, ct).ConfigureAwait(false));
            var width = Math.Max(1, Math.Max(names.Count, result.Rows.Count == 0 ? 0 : result.Rows.Max(r => r.Row.Values.Count)));
            var columns = Enumerable.Range(0, width).Select(i => i < names.Count ? names[i] : $"#{i}").ToArray();
            var list = new List<BrowseRow>();
            foreach (var row in result.Rows)
            {
                var targetId = row.Row.Id;
                list.Add(new(Pad(TablesAccess.Shown(row.Row), width), targetEntry is null ? null : () => new TableRowView(build, locale, targetEntry, targetId)));
            }
            if (result.NextAfter is { } next)
                list.Add(new(Pad(["(more)", "Enter shows the next matches"], width), () => new TableFollowView(build, locale, table, id, column, reference, next, targetEntry?.Counts?.ReadableRows)));
            var note = $"{result.Rows.Count:N0} row(s) of {target} match {column} of row {id}."
                + (targetEntry?.State == TableState.Partial ? " The target is partial: there may be more." : "");
            return new ViewContent(columns, list, note);
        }, ct);

    private static string[] Pad(IReadOnlyList<string> cells, int width) =>
        Enumerable.Range(0, width).Select(i => i < cells.Count ? cells[i] : "").ToArray();
}

/// <summary>
/// The rows of other tables whose declared foreign keys point at one row: one line per matching row (Enter opens it), and one
/// per source that has none or couldn't be searched, and one per table whose schema couldn't be resolved without opening it.
/// Runs at background priority: it reads every referring table.
/// </summary>
internal sealed class TableReferencedByView(BuildRef build, string locale, TableEntry table, uint id) : BrowseView
{
    public override string Title => "referenced by";

    private static readonly string[] Columns = ["Table", "Column", "Scope", "Row ID"];

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct) =>
        TablesAccess.WithTablesAsync(session, build, locale, async tables =>
        {
            var result = await tables.ReferencedByAsync(new TableReferencesRequest(TableRef.Id(table.FileDataId), id), TablePriority.Background, ct)
                .ConfigureAwait(false);
            if (result.Outcome == TableReferencesOutcome.SourceRow)
                return new ViewContent(Columns, [], $"Row {id} itself wasn't found ({result.Row.Outcome}), so nothing was searched.");
            var rows = new List<BrowseRow>();
            var truncated = 0;
            foreach (var source in result.Sources)
            {
                var name = $"{TablesAccess.Title(source.Table)} ({TablesAccess.Fdid(source.Table.FileDataId)})";
                var scope = source.Scope == TableReferenceScope.NotChecked ? $"NotChecked ({source.Table.State})" : source.Scope.ToString();
                if (source.Truncated) truncated++;
                if (source.RowIds.Count == 0) { rows.Add(new([name, source.Column, scope, "-"])); continue; }
                var entry = source.Table;
                foreach (var rowId in source.RowIds)
                    rows.Add(new([name, source.Column, scope, rowId.ToString(CultureInfo.InvariantCulture)], () => new TableRowView(build, locale, entry, rowId)));
            }
            foreach (var unresolved in result.Unresolved)
                rows.Add(new([$"{TablesAccess.Title(unresolved)} ({TablesAccess.Fdid(unresolved.FileDataId)})", "-", $"NotResolved ({unresolved.State})", "-"]));
            var note = result.Outcome == TableReferencesOutcome.NoReferences ? $"Nothing points at row {id}: every source was searched completely."
                : $"{rows.Count(r => r.Drill is not null):N0} row(s) from {result.Sources.Count:N0} source column(s)."
                  + (truncated > 0 ? $" {truncated} source(s) had more than {TableQueries.DefaultLimit} matches; only those are shown." : "")
                  + (result.Sources.Any(s => s.Scope != TableReferenceScope.Complete) ? " Partial or unchecked sources: the list is a lower bound." : "")
                  + (result.Unresolved.Count > 0
                      ? $" {result.Unresolved.Count:N0} table(s) couldn't be searched without opening them, so they may point here too." : "");
            return new ViewContent(Columns, rows, note);
        }, ct);
}
