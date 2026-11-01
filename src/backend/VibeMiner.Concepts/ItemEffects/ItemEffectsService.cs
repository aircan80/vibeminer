using VibeMiner.Builds;
using VibeMiner.Concepts.Tables;
using VibeMiner.Files;
using VibeMiner.Formats.Tables;
using static VibeMiner.Concepts.ItemEffects.ItemEffectProfiles;

namespace VibeMiner.Concepts.ItemEffects;

/// <summary>
/// Bounds of <see cref="ItemEffectsService"/>. <see cref="MaxRelationIndexEntries"/> is the most rows a handle's relation index
/// holds (D471): the link table's ItemID or ParentItemID index is built once per handle.
/// </summary>
public sealed record ItemEffectsOptions
{
    public const int DefaultMaxRelationIndexEntries = RelationIndexes.DefaultMaxEntries;
    private readonly int maxRelationIndexEntries = DefaultMaxRelationIndexEntries;

    public int MaxRelationIndexEntries
    {
        get => maxRelationIndexEntries;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            maxRelationIndexEntries = value;
        }
    }
}

/// <summary>
/// The real <see cref="IItemEffects"/> (D458) over layer 5's <see cref="ITables"/>. A page: the Item row by ID, the build's
/// link from the ItemEffect definition (D459), the link rows from the handle's relation index on ItemID or ParentItemID (D471,
/// paged by cursor over the item's matches), then each distinct ItemEffect by ItemEffectID and each distinct SpellName by SpellID. A role the request
/// doesn't reach, or whose step before it failed, is NotChecked.
/// </summary>
public sealed class ItemEffectsService : IItemEffects
{
    private readonly ITables tables;
    private readonly ItemEffectsOptions options;

    public ItemEffectsService(ITables tables, ItemEffectsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        this.tables = tables; this.options = options ?? new();
    }

    public async Task<ItemEffectsOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        var opened = await tables.ForAsync(build, locale, cancellationToken).ConfigureAwait(false);
        if (opened.State != FilesOpenState.Open || opened.Tables is null)
            return new(build, locale, opened.State, null, opened.Code, opened.Message, opened.ObservedAt);
        return new(build, locale, FilesOpenState.Open, new Handle(this, opened.Tables), null, null, null);
    }

    /// <summary>A row that doesn't line up with its profile: the role's source is Corrupt.</summary>
    private sealed class RowMismatch : Exception;

    private sealed class Handle(ItemEffectsService owner, IBuildTables tables) : IBuildItemEffects
    {
        private static readonly TableRef ItemRef = TableRef.ForName(ItemTable);
        private static readonly TableRef BridgeRef = TableRef.ForName(BridgeTable);
        private static readonly TableRef EffectRef = TableRef.ForName(EffectTable);
        private static readonly TableRef NameRef = TableRef.ForName(NameTable);
        private static readonly ItemEffectsSource NoBridge = new(ItemEffectsRole.Bridge, ItemEffectsSourceState.NotChecked);
        private static readonly ItemEffectsSource NoEffect = new(ItemEffectsRole.Effect, ItemEffectsSourceState.NotChecked);
        private static readonly ItemEffectsSource NoName = new(ItemEffectsRole.Name, ItemEffectsSourceState.NotChecked);
        private readonly RelationIndexes relations = new(tables, owner.options.MaxRelationIndexEntries);
        private int disposed;

        public BuildRef Build => tables.Build;
        public string Locale => tables.Locale;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await tables.DisposeAsync().ConfigureAwait(false);
        }

        public async Task<ItemEffectsPage> GetAsync(uint itemId, ItemEffectsQuery query, TablePriority priority, CancellationToken cancellationToken)
        {
            ItemEffectQueries.Validate(query);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
            cancellationToken.ThrowIfCancellationRequested();

            ItemEffectsPage Stop(ItemEffectsPageState state, TableEntry? item, ItemEffectsLink? link = null, ItemEffectsSource? bridge = null,
                string? key = null, string? code = null, string? message = null, int matches = 0) =>
                new(state, itemId, link, [], null, matches, item, bridge ?? NoBridge, NoEffect, NoName, key, code, message);

            // 1. The item. Only a found row goes on; the rest keep layer 5's outcome.
            var item = await tables.GetAsync(ItemRef, itemId, priority, cancellationToken).ConfigureAwait(false);
            if (!TableQueries.IsReadable(item.Table.State)) return Stop(ItemEffectsPageState.ItemNotReadable, item.Table);
            switch (item.Outcome)
            {
                case TableRowOutcome.Found: break;
                case TableRowOutcome.Absent: return Stop(ItemEffectsPageState.MissingItem, item.Table);
                case TableRowOutcome.Protected: return Stop(ItemEffectsPageState.ItemProtected, item.Table, key: item.ProtectedKey);
                default: return Stop(ItemEffectsPageState.ItemUnknown, item.Table);
            }
            if (itemId > int.MaxValue) return Stop(ItemEffectsPageState.ItemKeyOutOfRange, item.Table);

            // 2. The link (D459) and its source.
            var effectSchema = await tables.SchemaAsync(EffectRef, priority, cancellationToken).ConfigureAwait(false);
            TableSchemaResult? bridgeSchema = null;
            if (effectSchema.Schema is null) bridgeSchema = await tables.SchemaAsync(BridgeRef, priority, cancellationToken).ConfigureAwait(false);
            var link = Choose(effectSchema.Schema, bridgeSchema?.Schema);
            if (link == ItemEffectsLink.ParentItemId) bridgeSchema = effectSchema;
            else bridgeSchema ??= await tables.SchemaAsync(BridgeRef, priority, cancellationToken).ConfigureAwait(false);
            var bridgeProfile = Bridge(link);
            var bridge = Evaluate(ItemEffectsRole.Bridge, bridgeProfile, bridgeSchema);
            if (bridge.State != ItemEffectsSourceState.Ready) return Stop(ItemEffectsPageState.BridgeNotReady, item.Table, link, bridge);

            // 3. The link rows, from the relation index (D471), paged over the item's matches in layer 5 row order.
            var binding = ItemEffectQueries.Binding(Build, Locale, itemId, query.Limit);
            ItemEffectsCursor? cursor = null;
            if (query.After is { } after)
            {
                cursor = ItemEffectQueries.DecodeCursor(after, binding);
                if (cursor is null) return Stop(ItemEffectsPageState.InvalidCursor, item.Table, link, bridge, code: "INVALID_CURSOR",
                    message: "The effects cursor belongs to another item, page size or build.");
                if (cursor.L != link.ToString()) return Stop(ItemEffectsPageState.Stale, item.Table, link, bridge);
            }
            var skip = (int)Math.Min(cursor?.A ?? 0, int.MaxValue);
            var found = await relations.LookupAsync(bridgeProfile.Table, ParentColumnName(link), itemId, query.Limit, priority, cancellationToken, skip)
                .ConfigureAwait(false);
            switch (found.State)
            {
                case RelationLookupState.Ok: break;
                case RelationLookupState.IndexLimit:
                    return Stop(ItemEffectsPageState.IndexLimit, item.Table, link, bridge, code: found.Code, message: found.Message);
                case RelationLookupState.Stale: return Stop(ItemEffectsPageState.Stale, item.Table, link, bridge, message: found.Message);
                case RelationLookupState.Unsupported:
                    return Stop(ItemEffectsPageState.BridgeNotReady, item.Table, link, Failed(ItemEffectsRole.Bridge, ItemEffectsSourceState.Unsupported, found.Table),
                        code: found.Code, message: found.Message);
                default: return Stop(ItemEffectsPageState.BridgeNotReady, item.Table, link, Source(ItemEffectsRole.Bridge, found.Table!));
            }
            // A cursor reads the same index it was made from: a rebuilt index (new identity) is Stale, never re-based.
            if (cursor is not null && (cursor.I != found.Identity || cursor.A >= found.Matches))
                return cursor.I != found.Identity ? Stop(ItemEffectsPageState.Stale, item.Table, link, bridge)
                    : Stop(ItemEffectsPageState.InvalidCursor, item.Table, link, bridge, code: "INVALID_CURSOR", message: "The effects cursor points past the item's links.");
            var rows = found.Rows;
            var consumed = skip + rows.Count;
            var links = new List<(TableRow Row, string Parent, string EffectId, long? Key)>(rows.Count);
            try
            {
                foreach (var row in rows)
                {
                    var values = row.Row.Values;
                    if (values.Count != bridgeProfile.Columns.Count || values[0] != N(row.Row.Id) || values[ParentColumn(link)] != N(itemId)) throw new RowMismatch();
                    var effectId = link == ItemEffectsLink.ItemXItemEffect ? values[1] : values[0];
                    if (!Signed(effectId, int.MinValue, int.MaxValue, out var key)) throw new RowMismatch();
                    links.Add((row, values[ParentColumn(link)], effectId, key));
                }
            }
            catch (RowMismatch)
            {
                return Stop(ItemEffectsPageState.BridgeNotReady, item.Table, link, Failed(ItemEffectsRole.Bridge, ItemEffectsSourceState.Corrupt, bridge.Table),
                    matches: found.Matches);
            }

            // 4. Effects: each distinct non-negative ItemEffectID once.
            var effect = NoEffect;
            var effects = new Dictionary<long, ItemEffect>();
            if (links.Any(entry => entry.Key >= 0))
            {
                var effectProfile = Effect(link);
                effect = Evaluate(ItemEffectsRole.Effect, effectProfile, effectSchema);
                if (effect.State == ItemEffectsSourceState.Ready)
                {
                    try
                    {
                        foreach (var key in links.Select(entry => entry.Key!.Value).Where(key => key >= 0).Distinct())
                            effects[key] = await EffectAsync(effectProfile, effectSchema.Columns!, (uint)key, priority, cancellationToken).ConfigureAwait(false);
                    }
                    catch (RowMismatch)
                    {
                        effect = Failed(ItemEffectsRole.Effect, ItemEffectsSourceState.Corrupt, effect.Table);
                        effects.Clear();
                    }
                }
            }
            ItemEffect EffectOf(long key) => key < 0 ? new(ItemEffectState.EffectKeyOutOfRange)
                : effects.TryGetValue(key, out var found) ? found : new(ItemEffectState.SourceNotReady);

            // 5. Names: each distinct non-negative SpellID of a found effect once.
            var name = NoName;
            var names = new Dictionary<long, ItemEffectName>();
            var spells = effects.Values.Select(found => found.SpellId).Where(spell => spell is not null)
                .Select(spell => long.Parse(spell!, System.Globalization.CultureInfo.InvariantCulture)).Where(spell => spell >= 0).Distinct().ToArray();
            if (spells.Length > 0)
            {
                name = Evaluate(ItemEffectsRole.Name, SpellName, await tables.SchemaAsync(NameRef, priority, cancellationToken).ConfigureAwait(false));
                if (name.State == ItemEffectsSourceState.Ready)
                {
                    try
                    {
                        foreach (var spell in spells) names[spell] = await NameAsync((uint)spell, priority, cancellationToken).ConfigureAwait(false);
                    }
                    catch (RowMismatch)
                    {
                        name = Failed(ItemEffectsRole.Name, ItemEffectsSourceState.Corrupt, name.Table);
                        names.Clear();
                    }
                }
            }
            ItemEffectName NameOf(ItemEffect found)
            {
                if (found.SpellId is not { } text) return new(ItemEffectNameState.EffectNotReady);
                var spell = long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                if (spell < 0) return new(ItemEffectNameState.SpellKeyOutOfRange);
                return names.TryGetValue(spell, out var named) ? named : new(ItemEffectNameState.SourceNotReady);
            }

            var entries = links.Select(entry =>
            {
                var found = EffectOf(entry.Key!.Value);
                return new ItemEffectEntry(entry.Row, entry.Parent, entry.EffectId, found, NameOf(found));
            }).ToArray();
            var more = consumed < found.Matches ? ItemEffectQueries.EncodeCursor(new(binding, consumed, found.Identity, link.ToString())) : null;
            return new(ItemEffectsPageState.Ok, itemId, link, entries, more, found.Matches, item.Table, bridge, effect, name);
        }

        // Layer 5's state, then the reviewed profile.
        private static ItemEffectsSource Evaluate(ItemEffectsRole role, ItemEffectProfile profile, TableSchemaResult result)
        {
            var source = Source(role, result.Table);
            if (source.State == ItemEffectsSourceState.Ready && !Qualifies(profile, result))
                return Failed(role, ItemEffectsSourceState.Unsupported, result.Table);
            return source;
        }

        private async Task<ItemEffect> EffectAsync(ItemEffectProfile profile, IReadOnlyList<VibeMiner.Formats.Db2Column> columns, uint id, TablePriority priority,
            CancellationToken cancellationToken)
        {
            var found = await tables.GetAsync(EffectRef, id, priority, cancellationToken).ConfigureAwait(false);
            switch (found.Outcome)
            {
                case TableRowOutcome.Found: break;
                case TableRowOutcome.Absent: return new(ItemEffectState.NotFoundInAdmittedScope, Absence: ItemEffectsAbsence.AbsentFromCompleteSource);
                case TableRowOutcome.Protected: return new(ItemEffectState.Protected, ProtectedKey: found.ProtectedKey);
                default: return new(ItemEffectState.NotFoundInAdmittedScope, Absence: ItemEffectsAbsence.UnknownOutsideReadableScope);
            }
            var row = found.Row!;
            if (row.Row.Id != id || row.Row.Values.Count != profile.Columns.Count || row.Row.Values[0] != N(id) || row.Row.IsCopy != row.Row.CopySourceId.HasValue)
                throw new RowMismatch();
            var spell = SpellId(row.Row);
            var fields = Fields(profile, columns, row.Row);
            return fields is null ? new(ItemEffectState.FieldUnavailable, row, SpellId: spell) : new(ItemEffectState.ObservedReady, row, fields, spell);
        }

        private async Task<ItemEffectName> NameAsync(uint id, TablePriority priority, CancellationToken cancellationToken)
        {
            var found = await tables.GetAsync(NameRef, id, priority, cancellationToken).ConfigureAwait(false);
            switch (found.Outcome)
            {
                case TableRowOutcome.Found: break;
                case TableRowOutcome.Absent: return new(ItemEffectNameState.NotFoundInAdmittedScope, Absence: ItemEffectsAbsence.AbsentFromCompleteSource);
                case TableRowOutcome.Protected: return new(ItemEffectNameState.Protected, ProtectedKey: found.ProtectedKey);
                default: return new(ItemEffectNameState.NotFoundInAdmittedScope, Absence: ItemEffectsAbsence.UnknownOutsideReadableScope);
            }
            var row = found.Row!;
            var values = row.Row.Values;
            if (row.Row.Id != id || values.Count != 2 || values[0] != N(id) || row.Row.IsCopy != row.Row.CopySourceId.HasValue) throw new RowMismatch();
            var issues = row.Row.TextIssues?.Where(issue => issue.ColumnIndex == 1).ToArray() ?? [];
            if (issues.Length > 0)
            {
                if (issues.Length != 1 || issues[0].Code != TextInvalidCode) throw new RowMismatch();
                return new(ItemEffectNameState.InvalidText, row, IssueCode: TextInvalidCode);
            }
            var text = values[1];
            if (text.Length > ItemEffectQueries.MaxSourceNameUnits || !WellFormed(text)) throw new RowMismatch();
            if (text.Length > ItemEffectQueries.MaxNameUnits) return new(ItemEffectNameState.TextTooLarge, row, TextUnits: text.Length);
            return new(text.Length == 0 ? ItemEffectNameState.EmptyText : ItemEffectNameState.Ready, row, text);
        }
    }
}
