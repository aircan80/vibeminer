using VibeMiner.Builds;
using VibeMiner.Concepts.Items;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Testing;

/// <summary>
/// One item of a fake build, in list order. <see cref="Values"/> are its summary cells. <see cref="Icon"/> is what a detail
/// finds; when null it follows the real rule for no field (null) and 0, and a nonzero FileDataID reads as a file the build
/// doesn't list (FileFailed, UnknownFile). <see cref="ItemRow"/> and <see cref="SparseRow"/> default to a row of the ID alone
/// and, for a name from a found ItemSparse row (Valid, Empty, Invalid), the same. <see cref="Item"/> other than Found is an entry a
/// Partial Item can't read: it lists, and its detail is Unknown or Protected (with <see cref="ItemKey"/>). Facts follow
/// <see cref="ItemFacts.Project"/> on those rows and <see cref="ItemColumns"/> / <see cref="SparseColumns"/> (by default an ID
/// column, then <c>Cell1</c>... that no fact reads). <see cref="Labels"/> is what a detail finds; when null the item-side key rules
/// apply and a lookable key reads as a build without the target tables (TableNotReadable, NoDefinition). <see cref="Appearances"/>
/// is what a detail finds; when null it reads as a build without ItemModifiedAppearance (TableNotReadable, NoDefinition, no
/// models). <see cref="Icons"/> defaults to the item's icon labelled Item.IconFileDataID, plus none from an appearance.
/// </summary>
public sealed record FakeItem(uint Id, ItemName Name, IReadOnlyList<ItemSourceValue>? Values = null, uint? IconFileDataId = null,
    ItemIcon? Icon = null, bool IsCopy = false, uint? CopySourceId = null, string? SparseKey = null, TableRow? ItemRow = null,
    TableRow? SparseRow = null, ItemRowState Item = ItemRowState.Found, string? ItemKey = null,
    IReadOnlyList<Db2Column>? ItemColumns = null, IReadOnlyList<Db2Column>? SparseColumns = null, ItemClassLabels? Labels = null,
    ItemAppearanceSet? Appearances = null, IReadOnlyList<ItemIconSourced>? Icons = null);

/// <summary>
/// The standard <see cref="IItems"/> test double for layer 7 and up: items are seeded per build and locale, nothing is read. A
/// build or locale not added answers NeedsNetwork (LocaleNotInBuild when only the build was added), as the real side does
/// offline. Lists use <see cref="ItemQueries"/> and look at no more than <see cref="MaxScanRows"/> items per call. An ID not
/// seeded is Absent. Calls are counted per method.
/// </summary>
public sealed class InMemoryItems : IItems
{
    private readonly Lock gate = new();
    private readonly Dictionary<(BuildRef Build, string Locale), FakeItem[]> builds = [];
    private readonly Dictionary<BuildRef, (FilesOpenState State, string Code)> failures = [];
    private readonly Dictionary<string, int> calls = [];
    private readonly int maxScanRows = ItemsOptions.DefaultMaxScanRows;

    public int MaxScanRows
    {
        get => maxScanRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            maxScanRows = value;
        }
    }

    /// <summary>Adds (or replaces) a build and locale with exactly these items. IDs are unique.</summary>
    public InMemoryItems Add(BuildRef build, string locale, IEnumerable<FakeItem> items)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(items);
        FileLocales.BitOf(locale);
        var list = items.ToArray();
        if (list.Select(item => item.Id).Distinct().Count() != list.Length) throw new ArgumentException("An item ID is given twice.", nameof(items));
        lock (gate)
        {
            builds[(build, locale)] = list;
            failures.Remove(build);
        }
        return this;
    }

    /// <summary>Makes opening <paramref name="build"/> fail with a layer 4 state and code.</summary>
    public InMemoryItems AddFailing(BuildRef build, FilesOpenState state, string code)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (state == FilesOpenState.Open) throw new ArgumentException("A failure isn't Open.", nameof(state));
        lock (gate) failures[build] = (state, code);
        return this;
    }

    public int Calls(string method)
    {
        lock (gate) return calls.GetValueOrDefault(method);
    }

    private void Count(string method)
    {
        lock (gate) calls[method] = calls.GetValueOrDefault(method) + 1;
    }

    public Task<ItemsOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        Count(nameof(ForAsync));
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (failures.TryGetValue(build, out var failure))
                return Task.FromResult(new ItemsOpenResult(build, locale, failure.State, null, failure.Code, "Scripted open failure.", null));
            if (builds.TryGetValue((build, locale), out var items))
                return Task.FromResult(new ItemsOpenResult(build, locale, FilesOpenState.Open, new Handle(this, build, locale, items), null, null, null));
            if (builds.Keys.Any(key => key.Build == build))
                return Task.FromResult(new ItemsOpenResult(build, locale, FilesOpenState.LocaleNotInBuild, null, "FILES_LOCALE_NOT_IN_BUILD", null, null));
        }
        return Task.FromResult(new ItemsOpenResult(build, locale, FilesOpenState.NeedsNetwork, null, "BUILD_NEEDS_NETWORK", "The build isn't stored.", null));
    }

    private static ItemSummary Summary(FakeItem item) =>
        new(item.Id, item.IsCopy, item.CopySourceId, item.Name, item.Values ?? [], item.IconFileDataId, item.SparseKey, item.Item,
            item.Item == ItemRowState.Protected ? item.ItemKey : null);

    private sealed class Handle(InMemoryItems owner, BuildRef build, string locale, FakeItem[] items) : IBuildItems
    {
        private const string Identity = "fake-items";
        private int disposed;

        public BuildRef Build => build;
        public string Locale => locale;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            return ValueTask.CompletedTask;
        }

        private void Enter(string method, TablePriority priority, CancellationToken cancellationToken)
        {
            owner.Count(method);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task<ItemsPage> ListAsync(ItemQuery query, TablePriority priority, CancellationToken cancellationToken)
        {
            ItemQueries.Validate(query);
            Enter(nameof(ListAsync), priority, cancellationToken);
            var binding = ItemQueries.Binding(build, locale, query);
            if (query.ExactId is { } id)
            {
                if (query.After is not null) return Task.FromResult(Invalid());
                var found = items.FirstOrDefault(item => item.Id == id);
                return Task.FromResult(new ItemsPage(ItemsPageState.Ok, found is null ? [] : [Summary(found)], null, 1, null, null));
            }
            long after = -1;
            if (query.After is { } text)
            {
                if (ItemQueries.DecodeCursor(text, binding) is not { } cursor || cursor.A >= items.Length) return Task.FromResult(Invalid());
                if (cursor.I != Identity) return Task.FromResult(new ItemsPage(ItemsPageState.Stale, [], null, 0, null, null));
                after = cursor.A;
            }
            var entries = new List<ItemSummary>();
            long looked = 0;
            var at = after + 1;
            for (; at < items.Length && entries.Count < query.Limit && looked < owner.MaxScanRows; at++)
            {
                looked++;
                if (ItemQueries.Matches(items[at].Name, query.Query)) entries.Add(Summary(items[at]));
            }
            var next = at >= items.Length ? null : ItemQueries.EncodeCursor(new ItemCursor(binding, at - 1, Identity, null));
            return Task.FromResult(new ItemsPage(ItemsPageState.Ok, entries, next, looked, null, null));
        }

        private static Db2Column[] DefaultColumns(int count) =>
            [new("ID", "integer", true), .. Enumerable.Range(1, Math.Max(count - 1, 0)).Select(i => new Db2Column($"Cell{i}", "integer", false))];

        private static ItemClassLabel DefaultLabel(FakeItem item, bool subclass)
        {
            var key = ItemClassifications.SourceKey(item.Values ?? [], subclass);
            if (key.State is { } state) return new(state, key.Keys);
            var table = subclass ? ItemClassifications.SubclassTable : ItemClassifications.ClassTable;
            return new(ItemClassLabelState.TableNotReadable, key.Keys, Table: new TableEntry(0, table, TableState.NoDefinition, true, null, null, null));
        }

        private static ItemsPage Invalid() =>
            new(ItemsPageState.InvalidCursor, [], null, 0, null, null, "INVALID_CURSOR", "The Items cursor differs from this exact query, page size or build.");

        public Task<ItemResult> GetAsync(uint id, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(GetAsync), priority, cancellationToken);
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item.Id != id) continue;
                if (item.Item == ItemRowState.Protected) return Task.FromResult(new ItemResult(ItemResultState.Protected, null, null, null, item.ItemKey));
                if (item.Item == ItemRowState.Unknown) return Task.FromResult(new ItemResult(ItemResultState.Unknown, null, null, null));
                var itemRow = item.ItemRow ?? new TableRow(i, new Db2Row(id, item.IsCopy, item.CopySourceId, [$"{id}"]));
                var sparseRow = item.SparseRow ?? (item.Name.State is ItemNameState.Valid or ItemNameState.Empty or ItemNameState.Invalid
                    ? new TableRow(i, new Db2Row(id, item.IsCopy, item.CopySourceId, [$"{id}"])) : null);
                var icon = item.Icon ?? item.IconFileDataId switch
                {
                    null => new ItemIcon(ItemIconState.FieldUnavailable, null),
                    0 => new ItemIcon(ItemIconState.NoIcon, 0),
                    { } fdid => new ItemIcon(ItemIconState.FileFailed, fdid, BlpImageState.FileFailed, FileState: FileReadState.UnknownFile, HoleKeys: []),
                };
                var sparseState = sparseRow is not null ? ItemSparseState.Present : item.Name.State switch
                {
                    ItemNameState.MissingSparse => ItemSparseState.Missing,
                    ItemNameState.SparseProtected => ItemSparseState.Protected,
                    ItemNameState.SparseUnknown => ItemSparseState.Unknown,
                    _ => ItemSparseState.Unknown,
                };
                var sparseColumns = sparseState == ItemSparseState.Unknown ? null : item.SparseColumns ?? DefaultColumns(sparseRow?.Row.Values.Count ?? 1);
                var facts = ItemFacts.Project(item.ItemColumns ?? DefaultColumns(itemRow.Row.Values.Count), sparseColumns, itemRow.Row, sparseRow?.Row, sparseState);
                var labels = item.Labels ?? new(DefaultLabel(item, false), DefaultLabel(item, true));
                var appearances = item.Appearances ?? new ItemAppearanceSet(ItemAppearancesState.TableNotReadable, [], ItemDefaultAppearanceState.Unknown, null, 0,
                    new TableEntry(0, "ItemModifiedAppearance", TableState.NoDefinition, true, null, null, null), ItemModelRefs.None);
                var detail = new ItemDetail(Summary(item), itemRow, sparseRow, icon, facts, labels)
                {
                    Icons = item.Icons ?? [new("Item", "IconFileDataID", id, icon)],
                    Appearances = appearances,
                };
                return Task.FromResult(new ItemResult(ItemResultState.Found, detail, null, null));
            }
            return Task.FromResult(new ItemResult(ItemResultState.Absent, null, null, null));
        }
    }
}
