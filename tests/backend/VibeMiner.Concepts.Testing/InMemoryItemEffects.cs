using VibeMiner.Builds;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Files;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Testing;

/// <summary>
/// One item of a fake build and its effects in link order. <see cref="State"/> other than Ok (MissingItem, BridgeNotReady...)
/// answers every page with no entries; <see cref="Bridge"/> then says why. A Ready <see cref="Effect"/> and <see cref="Name"/>
/// come back only on a page that needs them, as the real side does; null means Ready.
/// </summary>
public sealed record FakeItemEffects(uint ItemId, IReadOnlyList<ItemEffectEntry> Entries, ItemEffectsLink Link = ItemEffectsLink.ItemXItemEffect,
    ItemEffectsPageState State = ItemEffectsPageState.Ok, ItemEffectsSource? Bridge = null, ItemEffectsSource? Effect = null, ItemEffectsSource? Name = null,
    string? ProtectedKey = null);

/// <summary>
/// The standard <see cref="IItemEffects"/> test double for layer 7 and up: effects are seeded per build and locale, nothing is
/// read. A build or locale not added answers NeedsNetwork. An item not seeded is MissingItem; a seeded item above int32 is
/// ItemKeyOutOfRange. Pages use <see cref="ItemEffectQueries"/> for limits and cursors. Calls are counted per method.
/// </summary>
public sealed class InMemoryItemEffects : IItemEffects
{
    private readonly Lock gate = new();
    private readonly Dictionary<(BuildRef Build, string Locale), Dictionary<uint, FakeItemEffects>> builds = [];
    private readonly Dictionary<BuildRef, (FilesOpenState State, string Code)> failures = [];
    private readonly Dictionary<string, int> calls = [];

    /// <summary>Adds (or replaces) a build and locale with exactly these items. Item IDs are unique.</summary>
    public InMemoryItemEffects Add(BuildRef build, string locale, IEnumerable<FakeItemEffects> items)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(items);
        FileLocales.BitOf(locale);
        var byId = new Dictionary<uint, FakeItemEffects>();
        foreach (var item in items)
            if (!byId.TryAdd(item.ItemId, item)) throw new ArgumentException("An item ID is given twice.", nameof(items));
        lock (gate)
        {
            builds[(build, locale)] = byId;
            failures.Remove(build);
        }
        return this;
    }

    /// <summary>Makes opening <paramref name="build"/> fail with a layer 4 state and code.</summary>
    public InMemoryItemEffects AddFailing(BuildRef build, FilesOpenState state, string code)
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

    public Task<ItemEffectsOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        Count(nameof(ForAsync));
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (failures.TryGetValue(build, out var failure))
                return Task.FromResult(new ItemEffectsOpenResult(build, locale, failure.State, null, failure.Code, null, null));
            if (!builds.TryGetValue((build, locale), out var items))
                return Task.FromResult(new ItemEffectsOpenResult(build, locale, FilesOpenState.NeedsNetwork, null, "NEEDS_NETWORK", null, null));
            return Task.FromResult(new ItemEffectsOpenResult(build, locale, FilesOpenState.Open, new Handle(this, build, locale, items), null, null, null));
        }
    }

    private sealed class Handle(InMemoryItemEffects owner, BuildRef build, string locale, Dictionary<uint, FakeItemEffects> items) : IBuildItemEffects
    {
        private const string Identity = "fake";
        private static readonly ItemEffectsSource NoBridge = new(ItemEffectsRole.Bridge, ItemEffectsSourceState.NotChecked);
        private static readonly ItemEffectsSource NoEffect = new(ItemEffectsRole.Effect, ItemEffectsSourceState.NotChecked);
        private static readonly ItemEffectsSource NoName = new(ItemEffectsRole.Name, ItemEffectsSourceState.NotChecked);
        private int disposed;

        public BuildRef Build => build;
        public string Locale => locale;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            return ValueTask.CompletedTask;
        }

        public Task<ItemEffectsPage> GetAsync(uint itemId, ItemEffectsQuery query, TablePriority priority, CancellationToken cancellationToken)
        {
            owner.Count(nameof(GetAsync));
            ItemEffectQueries.Validate(query);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
            cancellationToken.ThrowIfCancellationRequested();
            ItemEffectsPage Stop(ItemEffectsPageState state, ItemEffectsLink? link = null, ItemEffectsSource? bridge = null, string? key = null, string? code = null) =>
                new(state, itemId, link, [], null, 0, null, bridge ?? NoBridge, NoEffect, NoName, key, code);
            if (!items.TryGetValue(itemId, out var item)) return Task.FromResult(Stop(ItemEffectsPageState.MissingItem));
            if (item.State is ItemEffectsPageState.ItemNotReadable or ItemEffectsPageState.MissingItem or ItemEffectsPageState.ItemUnknown or ItemEffectsPageState.ItemProtected)
                return Task.FromResult(Stop(item.State, key: item.ProtectedKey));
            if (itemId > int.MaxValue) return Task.FromResult(Stop(ItemEffectsPageState.ItemKeyOutOfRange));
            var bridge = item.Bridge ?? new(ItemEffectsRole.Bridge, ItemEffectsSourceState.Ready);
            if (item.State != ItemEffectsPageState.Ok) return Task.FromResult(Stop(item.State, item.Link, bridge));
            var binding = ItemEffectQueries.Binding(build, locale, itemId, query.Limit);
            var first = 0;
            if (query.After is { } after)
            {
                var cursor = ItemEffectQueries.DecodeCursor(after, binding);
                if (cursor is null || cursor.I != Identity || cursor.A >= item.Entries.Count)
                    return Task.FromResult(Stop(ItemEffectsPageState.InvalidCursor, item.Link, bridge, code: "INVALID_CURSOR"));
                if (cursor.L != item.Link.ToString()) return Task.FromResult(Stop(ItemEffectsPageState.Stale, item.Link, bridge));
                first = (int)cursor.A + 1;
            }
            var entries = item.Entries.Skip(first).Take(query.Limit).ToArray();
            var last = first + entries.Length - 1;
            var next = last + 1 < item.Entries.Count ? ItemEffectQueries.EncodeCursor(new(binding, last, Identity, item.Link.ToString())) : null;
            var effect = entries.Any(entry => entry.Effect.State != ItemEffectState.EffectKeyOutOfRange)
                ? item.Effect ?? new(ItemEffectsRole.Effect, ItemEffectsSourceState.Ready) : NoEffect;
            var name = entries.Any(entry => entry.Name.State is not (ItemEffectNameState.EffectNotReady or ItemEffectNameState.SpellKeyOutOfRange))
                ? item.Name ?? new(ItemEffectsRole.Name, ItemEffectsSourceState.Ready) : NoName;
            return Task.FromResult(new ItemEffectsPage(ItemEffectsPageState.Ok, itemId, item.Link, entries, next, item.Entries.Count, null, bridge, effect, name));
        }
    }
}
