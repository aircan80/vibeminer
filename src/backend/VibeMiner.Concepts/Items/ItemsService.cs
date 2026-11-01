using VibeMiner.Builds;
using VibeMiner.Concepts.Tables;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Items;

/// <summary>
/// Bounds of <see cref="ItemsService"/>. <see cref="MaxScanRows"/> is the most table rows one list call looks at (at least 1).
/// <see cref="MaxLabelScanRows"/> is the most rows one class or subclass label's key scan looks at, checked between pages (at least 1).
/// <see cref="MaxRelationIndexEntries"/> is the most rows a handle's relation indexes hold together (D471);
/// <see cref="MaxAppearanceRows"/> the most ItemModifiedAppearance rows one detail keeps, <see cref="MaxResourceFiles"/> the most
/// files one resource ID keeps, and <see cref="MaxModels"/> the most model files one detail reads (D467-D470). All at least 1.
/// </summary>
public sealed record ItemsOptions
{
    public const int DefaultMaxScanRows = 20_000;
    public const int DefaultMaxLabelScanRows = 200_000;
    private readonly int maxScanRows = DefaultMaxScanRows;
    private readonly int maxLabelScanRows = DefaultMaxLabelScanRows;
    public const int DefaultMaxRelationIndexEntries = RelationIndexes.DefaultMaxEntries, DefaultMaxAppearanceRows = 16, DefaultMaxResourceFiles = 32, DefaultMaxModels = 8;

    public int MaxRelationIndexEntries { get; init => field = AtLeastOne(value); } = DefaultMaxRelationIndexEntries;
    public int MaxAppearanceRows { get; init => field = AtLeastOne(value); } = DefaultMaxAppearanceRows;
    public int MaxResourceFiles { get; init => field = AtLeastOne(value); } = DefaultMaxResourceFiles;
    public int MaxModels { get; init => field = AtLeastOne(value); } = DefaultMaxModels;

    private static int AtLeastOne(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }

    public int MaxLabelScanRows
    {
        get => maxLabelScanRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            maxLabelScanRows = value;
        }
    }

    public int MaxScanRows
    {
        get => maxScanRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            maxScanRows = value;
        }
    }
}

/// <summary>
/// The real <see cref="IItems"/> (D450): Item and ItemSparse through layer 5's <see cref="ITables"/>, and the icon header
/// through <see cref="BlpImages"/> on the build's layer 4 files, opened on the first detail that needs them.
/// <para>
/// An empty query pages Item in its row order and looks up each listed row's ItemSparse row by ID. A name query pages ItemSparse
/// (names live there), keeps Valid names that contain it, and lists those whose Item row is found: an ItemSparse-only row is
/// never an item. Each call looks at no more than <see cref="ItemsOptions.MaxScanRows"/> rows and returns a cursor to go on.
/// </para>
/// </summary>
public sealed class ItemsService : IItems
{
    private const int PageRows = 1000;
    /// <summary>The item's appearance rows name more distinct model files than <see cref="ItemsOptions.MaxModels"/>.</summary>
    public const string ModelCapCode = "ITEM_MODEL_CAP";
    private readonly ITables tables;
    private readonly IFileSystem files;
    private readonly BlpImages images;
    private readonly ItemsOptions options;

    public ItemsService(ITables tables, IFileSystem files, BlpImages images, ItemsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tables); ArgumentNullException.ThrowIfNull(files); ArgumentNullException.ThrowIfNull(images);
        this.tables = tables; this.files = files; this.images = images; this.options = options ?? new();
    }

    public async Task<ItemsOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        var opened = await tables.ForAsync(build, locale, cancellationToken).ConfigureAwait(false);
        if (opened.State != FilesOpenState.Open || opened.Tables is null)
            return new(build, locale, opened.State, null, opened.Code, opened.Message, opened.ObservedAt);
        return new(build, locale, FilesOpenState.Open, new Handle(this, opened.Tables), null, null, null);
    }

    private sealed class Handle(ItemsService owner, IBuildTables tables) : IBuildItems
    {
        private static readonly TableRef Item = TableRef.ForName(ItemLayout.ItemTable);
        private static readonly TableRef Sparse = TableRef.ForName(ItemLayout.SparseTable);
        private readonly SemaphoreSlim filesGate = new(1, 1);
        private IBuildFiles? buildFiles;
        private BuildModels? buildModels;
        // Relation indexes live as long as the tables handle (D471).
        private readonly RelationIndexes relations = new(tables, owner.options.MaxRelationIndexEntries);
        private int disposed;

        public BuildRef Build => tables.Build;
        public string Locale => tables.Locale;

        private void Enter(TablePriority priority, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await filesGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (buildFiles is not null) await buildFiles.DisposeAsync().ConfigureAwait(false);
                buildFiles = null;
                buildModels = null;
            }
            finally { filesGate.Release(); }
            await tables.DisposeAsync().ConfigureAwait(false);
        }

        // Both tables' checked entries and header columns. A table that isn't readable has no columns.
        private async Task<(TableEntry Item, TableEntry Sparse, IReadOnlyList<Db2Column>? ItemColumns, IReadOnlyList<Db2Column>? SparseColumns)> SchemasAsync(
            TablePriority priority, CancellationToken cancellationToken)
        {
            var item = await tables.SchemaAsync(Item, priority, cancellationToken).ConfigureAwait(false);
            var sparse = await tables.SchemaAsync(Sparse, priority, cancellationToken).ConfigureAwait(false);
            return (item.Table, sparse.Table, TableQueries.IsReadable(item.Table.State) ? item.Columns ?? [] : null,
                TableQueries.IsReadable(sparse.Table.State) ? sparse.Columns ?? [] : null);
        }

        private static ItemsPageState PageState(string code) => code switch
        {
            ItemsSourceException.NameUnsupported => ItemsPageState.NameUnsupported,
            ItemsSourceException.SchemaAmbiguous => ItemsPageState.SchemaAmbiguous,
            _ => ItemsPageState.SourceInvalid,
        };

        private static ItemResultState ResultState(string code) => code switch
        {
            ItemsSourceException.NameUnsupported => ItemResultState.NameUnsupported,
            ItemsSourceException.SchemaAmbiguous => ItemResultState.SchemaAmbiguous,
            _ => ItemResultState.SourceInvalid,
        };

        public async Task<ItemsPage> ListAsync(ItemQuery query, TablePriority priority, CancellationToken cancellationToken)
        {
            ItemQueries.Validate(query);
            Enter(priority, cancellationToken);
            var (item, sparse, itemColumns, sparseColumns) = await SchemasAsync(priority, cancellationToken).ConfigureAwait(false);
            if (itemColumns is null) return new(ItemsPageState.ItemNotReadable, [], null, 0, item, sparse);
            var search = query.Query.Length != 0;
            if (search && sparseColumns is null) return new(ItemsPageState.SparseNotReadable, [], null, 0, item, sparse);
            var binding = ItemQueries.Binding(Build, Locale, query);
            ItemCursor? cursor = null;
            if (query.After is { } after)
            {
                cursor = ItemQueries.DecodeCursor(after, binding);
                // An exact ID never makes a cursor, so none belongs to it.
                if (cursor is null || query.ExactId is not null)
                    return new(ItemsPageState.InvalidCursor, [], null, 0, item, sparse, "INVALID_CURSOR",
                        "The Items cursor differs from this exact query, page size or build.");
                if (cursor.O != (search ? item : sparse).ContentKey) return new(ItemsPageState.Stale, [], null, 0, item, sparse);
            }
            try
            {
                var layout = ItemLayout.Resolve(itemColumns, sparseColumns);
                if (query.ExactId is { } id) return await ExactAsync(layout, id, item, sparse, priority, cancellationToken).ConfigureAwait(false);
                return await ScanAsync(layout, query, binding, cursor, search, item, sparse, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (ItemsSourceException problem) { return new(PageState(problem.Code), [], null, 0, item, sparse, problem.Code, problem.Message); }
        }

        private async Task<ItemsPage> ExactAsync(ItemLayout layout, uint id, TableEntry item, TableEntry sparse, TablePriority priority,
            CancellationToken cancellationToken)
        {
            var found = await tables.GetAsync(Item, id, priority, cancellationToken).ConfigureAwait(false);
            if (found.Outcome == TableRowOutcome.Absent) return new(ItemsPageState.Ok, [], null, 1, item, sparse);
            if (found.Outcome != TableRowOutcome.Found)
            {
                // Not proven absent: say what Item knows, with the ItemSparse row when there is one.
                TableRowResult? sparseRow = layout.SparseColumns is null ? null
                    : await tables.GetAsync(Sparse, id, priority, cancellationToken).ConfigureAwait(false);
                return new(ItemsPageState.Ok, [layout.Unread(id, found.Outcome, found.ProtectedKey, sparseRow?.Row?.Row,
                    sparseRow?.Outcome ?? TableRowOutcome.Unknown, sparseRow?.ProtectedKey)], null, 1, item, sparse);
            }
            var (summary, _) = await SummaryAsync(layout, found.Row!.Row, priority, cancellationToken).ConfigureAwait(false);
            return new(ItemsPageState.Ok, [summary], null, 1, item, sparse);
        }

        // The item's summary, with its ItemSparse row looked up by ID unless ItemSparse isn't readable.
        private async Task<(ItemSummary Summary, TableRow? Sparse)> SummaryAsync(ItemLayout layout, Db2Row row, TablePriority priority,
            CancellationToken cancellationToken)
        {
            if (layout.SparseColumns is null) return (layout.Summary(row, null, TableRowOutcome.Unknown, null), null);
            var sparse = await tables.GetAsync(Sparse, row.Id, priority, cancellationToken).ConfigureAwait(false);
            return (layout.Summary(row, sparse.Row?.Row, sparse.Outcome, sparse.ProtectedKey), sparse.Row);
        }

        private async Task<ItemsPage> ScanAsync(ItemLayout layout, ItemQuery query, string binding, ItemCursor? cursor, bool search,
            TableEntry item, TableEntry sparse, TablePriority priority, CancellationToken cancellationToken)
        {
            var scanned = search ? Sparse : Item;
            var entries = new List<ItemSummary>(query.Limit);
            var after = cursor?.A ?? -1;
            var identity = cursor?.I;
            long looked = 0;
            var end = false;
            while (entries.Count < query.Limit && looked < owner.options.MaxScanRows)
            {
                var budget = (int)Math.Min(PageRows, owner.options.MaxScanRows - looked);
                // An empty query lists every row it reads, so it reads no more than it can return.
                if (!search) budget = Math.Min(budget, query.Limit - entries.Count);
                var page = await tables.PageAsync(new TablePageRequest(scanned, after, budget, null, identity), priority, cancellationToken).ConfigureAwait(false);
                switch (page.State)
                {
                    case TablePageState.Ok: break;
                    case TablePageState.Stale: return new(ItemsPageState.Stale, [], null, looked, item, sparse);
                    case TablePageState.NotReadable:
                        return new(search ? ItemsPageState.SparseNotReadable : ItemsPageState.ItemNotReadable, [], null, looked,
                            search ? item : page.Table, search ? page.Table : sparse);
                    default:
                        return new(ItemsPageState.InvalidCursor, [], null, looked, item, sparse, page.Code ?? "INVALID_CURSOR", page.Message);
                }
                identity = page.Identity;
                var consumed = 0;
                foreach (var row in page.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    consumed++; looked++; after = row.Ordinal;
                    if (!search) entries.Add((await SummaryAsync(layout, row.Row, priority, cancellationToken).ConfigureAwait(false)).Summary);
                    else if (ItemQueries.Matches(layout.NameOf(row.Row), query.Query))
                    {
                        var found = await tables.GetAsync(Item, row.Row.Id, priority, cancellationToken).ConfigureAwait(false);
                        // Only a proven absence (sparse-only) is dropped; an Item row that can't be read is listed as such.
                        if (found.Outcome == TableRowOutcome.Found) entries.Add(layout.Summary(found.Row!.Row, row.Row, TableRowOutcome.Found, null));
                        else if (found.Outcome != TableRowOutcome.Absent)
                            entries.Add(layout.Unread(row.Row.Id, found.Outcome, found.ProtectedKey, row.Row, TableRowOutcome.Found, null));
                    }
                    if (entries.Count == query.Limit) break;
                }
                if (page.NextAfter is null && consumed == page.Rows.Count) { end = true; break; }
                if (page.Rows.Count == 0) { end = true; break; }
            }
            var next = end ? null : ItemQueries.EncodeCursor(new ItemCursor(binding, after, identity, (search ? item : sparse).ContentKey));
            return new(ItemsPageState.Ok, entries, next, looked, item, sparse);
        }

        public async Task<ItemResult> GetAsync(uint id, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(priority, cancellationToken);
            var (item, sparse, itemColumns, sparseColumns) = await SchemasAsync(priority, cancellationToken).ConfigureAwait(false);
            if (itemColumns is null) return new(ItemResultState.ItemNotReadable, null, item, sparse);
            ItemSummary summary;
            TableRow itemRow;
            TableRow? sparseRow;
            ItemFactSet facts;
            try
            {
                var layout = ItemLayout.Resolve(itemColumns, sparseColumns);
                var found = await tables.GetAsync(Item, id, priority, cancellationToken).ConfigureAwait(false);
                switch (found.Outcome)
                {
                    case TableRowOutcome.Found: break;
                    case TableRowOutcome.Absent: return new(ItemResultState.Absent, null, item, sparse);
                    case TableRowOutcome.Protected: return new(ItemResultState.Protected, null, item, sparse, found.ProtectedKey);
                    default: return new(ItemResultState.Unknown, null, item, sparse);
                }
                itemRow = found.Row!;
                (summary, sparseRow) = await SummaryAsync(layout, itemRow.Row, priority, cancellationToken).ConfigureAwait(false);
                facts = ItemFacts.Project(layout.ItemColumns, layout.SparseColumns, itemRow.Row, sparseRow?.Row, ItemFacts.SparseState(summary.Name.State));
            }
            catch (ItemsSourceException problem) { return new(ResultState(problem.Code), null, item, sparse, null, problem.Code, problem.Message); }
            var icon = await IconAsync(summary.IconFileDataId, cancellationToken).ConfigureAwait(false);
            var labels = await ItemClassifications.ResolveAsync(tables, summary, owner.options.MaxLabelScanRows, priority, cancellationToken).ConfigureAwait(false);
            var appearances = await new ItemAppearanceReader(tables, relations, owner.options, priority, cancellationToken).ReadAsync(id).ConfigureAwait(false);
            appearances = appearances with { Models = await ModelsAsync(appearances, cancellationToken).ConfigureAwait(false) };
            List<ItemIconSourced> icons = [new(ItemLayout.ItemTable, "IconFileDataID", id, icon)];
            // The default appearance's own icon, next to Item's, each with its field; neither is picked over the other (D469).
            if (appearances.DefaultIndex is { } at && appearances.Rows[at].Appearance is { State: ItemRefState.Found, Id: { } appearanceId } byDefault)
                icons.Add(new(ItemAppearanceReader.AppearanceTable, ItemAppearanceReader.IconColumn, appearanceId,
                    await IconAsync(byDefault.DefaultIconFileDataId, cancellationToken).ConfigureAwait(false)));
            return new(ItemResultState.Found, new ItemDetail(summary, itemRow, sparseRow, icon, facts, labels) { Icons = icons, Appearances = appearances }, item, sparse);
        }

        // The old icon rules (ItemIconsStore) on layer 5: no field, 0, else the BLP header through the build's files.
        private async Task<ItemIcon> IconAsync(uint? fileDataId, CancellationToken cancellationToken)
        {
            if (fileDataId is not { } id) return new(ItemIconState.FieldUnavailable, null);
            if (id == 0) return new(ItemIconState.NoIcon, 0);
            var (filesOf, _, failed) = await FilesAsync(cancellationToken).ConfigureAwait(false);
            if (filesOf is null) return new(ItemIconState.FilesNotOpen, id, Code: failed!.Code, Message: failed.Message, ObservedAt: failed.ObservedAt);
            var inspected = await owner.images.InspectAsync(filesOf, FileRef.Id(id), cancellationToken).ConfigureAwait(false);
            // The variant layer 4 picked for this locale (D160): a stat is a lookup, never a fetch.
            var stat = await filesOf.StatAsync(FileRef.Id(id), cancellationToken).ConfigureAwait(false);
            return new(IconState(inspected.State), id, inspected.State, inspected.Header, inspected.Encoding, inspected.Mips?.Count, inspected.FileState,
                inspected.HoleKeys, inspected.Code, inspected.Message, inspected.ObservedAt, stat.File?.ContentKey);
        }

        private static ItemIconState IconState(BlpImageState state) => state switch
        {
            BlpImageState.Ready => ItemIconState.Ready,
            BlpImageState.Unsupported => ItemIconState.Unsupported,
            BlpImageState.Invalid => ItemIconState.Invalid,
            BlpImageState.MipUnavailable => ItemIconState.MipUnavailable,
            BlpImageState.LimitExceeded => ItemIconState.LimitExceeded,
            BlpImageState.Protected => ItemIconState.Protected,
            _ => ItemIconState.FileFailed,
        };

        // Every distinct model file the rows' ModelFileData rows name, in first-seen order; past the cap listed, never read (D470).
        private async Task<ItemModelRefs> ModelsAsync(ItemAppearanceSet set, CancellationToken cancellationToken)
        {
            var ids = set.Rows.SelectMany(row => row.Appearance.Display?.Slots ?? []).SelectMany(slot => slot.Model.Files)
                .Select(file => file.FileDataId).Distinct().ToArray();
            if (ids.Length == 0) return ItemModelRefs.None;
            var cap = owner.options.MaxModels;
            var models = new List<ItemModelRef>(ids.Length);
            var (_, modelsOf, failed) = await FilesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var fdid in ids.Take(cap))
            {
                if (modelsOf is null) { models.Add(new(fdid, ItemModelRefState.FilesNotOpen, Code: failed!.Code, Message: failed.Message, ObservedAt: failed.ObservedAt)); continue; }
                var read = await modelsOf.ReadModelAsync(FileRef.Id(fdid), cancellationToken).ConfigureAwait(false);
                models.Add(new(fdid, ItemModelRefState.Read, read.State,
                    [.. read.References.Where(r => r.Kind == ModelReferenceKind.Texture).Select(r => new ItemModelTexture(r.Ordinal, r.TextureType, r.FileDataId, r.State))],
                    read.Model?.SkinFileDataIds.Count ?? 0, read.Code, read.Message, read.ObservedAt, read.KeyNames));
            }
            models.AddRange(ids.Skip(cap).Select(fdid => new ItemModelRef(fdid, ItemModelRefState.NotRead)));
            return ids.Length > cap
                ? new(ItemModelsState.Capped, models, ModelCapCode, $"The item names {ids.Length} model files; a detail reads at most {cap}.")
                : new(ItemModelsState.Complete, models);
        }

        // The build's layer 4 handle, opened once and held until dispose. A failed open isn't kept: the next detail tries again.
        private async Task<(IBuildFiles? Files, BuildModels? Models, FilesOpenResult? Failed)> FilesAsync(CancellationToken cancellationToken)
        {
            await filesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                if (buildFiles is not null) return (buildFiles, buildModels, null);
                var opened = await owner.files.ForAsync(Build, Locale, cancellationToken).ConfigureAwait(false);
                if (opened.State != FilesOpenState.Open || opened.Files is null) return (null, null, opened);
                buildFiles = opened.Files;
                buildModels = new BuildModels(buildFiles);
                return (buildFiles, buildModels, null);
            }
            finally { filesGate.Release(); }
        }
    }
}
