using VibeMiner.Builds;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Testing;

/// <summary>
/// One creature of a fake build, in list order. <see cref="Facts"/> are its four fact cells, <see cref="Slots"/> its display
/// slots (default: four NoDeclaredDisplay slots). <see cref="Displays"/> are what a detail finds; when null each slot follows
/// the chain through the build's <see cref="FakeCreatureDisplays"/>. <see cref="Row"/> defaults to a row of the ID alone.
/// <see cref="Outcome"/> Unknown or Protected (with <see cref="ProtectedKey"/>) is an ID a Partial Creature table can't read: it
/// never lists, an exact ID reports the outcome, and its detail has that state.
/// </summary>
public sealed record FakeCreature(uint Id, CreatureName Name, IReadOnlyList<CreatureSourceValue>? Facts = null, IReadOnlyList<CreatureDisplaySlot>? Slots = null,
    IReadOnlyList<CreatureDisplayChain>? Displays = null, string? ClassificationLabel = null, bool IsCopy = false, uint? CopySourceId = null,
    TableRow? Row = null, TableRowOutcome Outcome = TableRowOutcome.Found, string? ProtectedKey = null);

/// <summary>
/// A fake build's display chain: CreatureDisplayInfo ID to ModelID, CreatureModelData ID to FileDataID, and the model a resolved
/// FileDataID reads as. Every ID not listed is absent from a complete table; a FileDataID not in <see cref="Files"/> reads as a
/// file the build doesn't list (ReadFailed, UnknownFile).
/// </summary>
public sealed record FakeCreatureDisplays(IReadOnlyDictionary<uint, uint>? Displays = null, IReadOnlyDictionary<uint, uint>? Models = null,
    IReadOnlyDictionary<uint, CreatureModel>? Files = null)
{
    public static FakeCreatureDisplays None { get; } = new();
}

/// <summary>
/// The standard <see cref="ICreatures"/> test double for layer 7 and up: creatures are seeded per build and locale, nothing is
/// read. A build or locale not added answers NeedsNetwork (LocaleNotInBuild when only the build was added), as the real side
/// does offline. Lists use <see cref="CreatureQueries"/> and look at no more than <see cref="MaxScanRows"/> creatures per call.
/// An ID not seeded is Absent. Calls are counted per method.
/// </summary>
public sealed class InMemoryCreatures : ICreatures
{
    private readonly Lock gate = new();
    private readonly Dictionary<(BuildRef Build, string Locale), (FakeCreature[] Creatures, FakeCreatureDisplays Displays)> builds = [];
    private readonly Dictionary<BuildRef, (FilesOpenState State, string Code)> failures = [];
    private readonly Dictionary<string, int> calls = [];
    private readonly int maxScanRows = CreaturesOptions.DefaultMaxScanRows;

    public int MaxScanRows
    {
        get => maxScanRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            maxScanRows = value;
        }
    }

    /// <summary>Adds (or replaces) a build and locale with exactly these creatures and display chain. IDs are unique.</summary>
    public InMemoryCreatures Add(BuildRef build, string locale, IEnumerable<FakeCreature> creatures, FakeCreatureDisplays? displays = null)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(creatures);
        FileLocales.BitOf(locale);
        var list = creatures.ToArray();
        if (list.Select(creature => creature.Id).Distinct().Count() != list.Length) throw new ArgumentException("A creature ID is given twice.", nameof(creatures));
        lock (gate)
        {
            builds[(build, locale)] = (list, displays ?? FakeCreatureDisplays.None);
            failures.Remove(build);
        }
        return this;
    }

    /// <summary>Makes opening <paramref name="build"/> fail with a layer 4 state and code.</summary>
    public InMemoryCreatures AddFailing(BuildRef build, FilesOpenState state, string code)
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

    public Task<CreaturesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        Count(nameof(ForAsync));
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (failures.TryGetValue(build, out var failure))
                return Task.FromResult(new CreaturesOpenResult(build, locale, failure.State, null, failure.Code, "Scripted open failure.", null));
            if (builds.TryGetValue((build, locale), out var seeded))
                return Task.FromResult(new CreaturesOpenResult(build, locale, FilesOpenState.Open,
                    new Handle(this, build, locale, seeded.Creatures, seeded.Displays), null, null, null));
            if (builds.Keys.Any(key => key.Build == build))
                return Task.FromResult(new CreaturesOpenResult(build, locale, FilesOpenState.LocaleNotInBuild, null, "FILES_LOCALE_NOT_IN_BUILD", null, null));
        }
        return Task.FromResult(new CreaturesOpenResult(build, locale, FilesOpenState.NeedsNetwork, null, "BUILD_NEEDS_NETWORK", "The build isn't stored.", null));
    }

    private static IReadOnlyList<CreatureDisplaySlot> SlotsOf(FakeCreature creature) =>
        creature.Slots ?? [.. Enumerable.Range(0, 4).Select(slot => new CreatureDisplaySlot(slot, 0, 0, CreatureSlotState.NoDeclaredDisplay))];

    private static CreatureSummary Summary(FakeCreature creature) =>
        new(creature.Id, creature.IsCopy, creature.CopySourceId, creature.Name, creature.Facts ?? [], SlotsOf(creature));

    private static CreatureDisplayChain Chain(CreatureDisplaySlot slot, FakeCreatureDisplays displays)
    {
        if (slot.State == CreatureSlotState.NoDeclaredDisplay) return new(slot, CreatureDisplayChainState.NoDeclaredDisplay);
        if (slot.State == CreatureSlotState.InvalidDisplayReference) return new(slot, CreatureDisplayChainState.InvalidDisplayReference);
        if (displays.Displays?.TryGetValue((uint)slot.DisplayId, out var modelId) != true) return new(slot, CreatureDisplayChainState.DisplayAbsent);
        if (modelId == 0) return new(slot, CreatureDisplayChainState.NoDeclaredModel, ModelId: 0);
        if (displays.Models?.TryGetValue(modelId, out var file) != true) return new(slot, CreatureDisplayChainState.ModelAbsent, ModelId: modelId);
        if (file == 0) return new(slot, CreatureDisplayChainState.NoDeclaredModelFile, ModelId: modelId, ModelFileDataId: 0);
        var model = displays.Files?.GetValueOrDefault(file) ?? new CreatureModel(CreatureModelState.ReadFailed, file, ModelReadState.UnknownFile,
            new(CreatureAppearanceState.ModelNotRead), [], Message: "The model is UnknownFile.");
        return new(slot, CreatureDisplayChainState.MetadataResolved, ModelId: modelId, ModelFileDataId: file, Model: model);
    }

    private sealed class Handle(InMemoryCreatures owner, BuildRef build, string locale, FakeCreature[] creatures, FakeCreatureDisplays displays) : IBuildCreatures
    {
        private const string Identity = "fake-creatures";
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

        public Task<CreaturesPage> ListAsync(CreatureQuery query, TablePriority priority, CancellationToken cancellationToken)
        {
            CreatureQueries.Validate(query);
            Enter(nameof(ListAsync), priority, cancellationToken);
            var binding = CreatureQueries.Binding(build, locale, query);
            if (query.ExactId is { } id)
            {
                if (query.After is not null) return Task.FromResult(Invalid());
                var found = creatures.FirstOrDefault(creature => creature.Id == id);
                var outcome = found?.Outcome ?? TableRowOutcome.Absent;
                return Task.FromResult(new CreaturesPage(CreaturesPageState.Ok, outcome == TableRowOutcome.Found ? [Summary(found!)] : [], null, 1, null,
                    outcome, outcome == TableRowOutcome.Protected ? found!.ProtectedKey : null));
            }
            var readable = creatures.Where(creature => creature.Outcome == TableRowOutcome.Found).ToArray();
            long after = -1;
            if (query.After is { } text)
            {
                if (CreatureQueries.DecodeCursor(text, binding) is not { } cursor || cursor.After >= readable.Length) return Task.FromResult(Invalid());
                if (cursor.Identity != Identity) return Task.FromResult(new CreaturesPage(CreaturesPageState.Stale, [], null, 0, null));
                after = cursor.After;
            }
            var entries = new List<CreatureSummary>();
            long looked = 0;
            var at = after + 1;
            for (; at < readable.Length && entries.Count < query.Limit && looked < owner.MaxScanRows; at++)
            {
                looked++;
                if (CreatureQueries.Matches(readable[at].Name, query.Query)) entries.Add(Summary(readable[at]));
            }
            var next = at >= readable.Length ? null : CreatureQueries.EncodeCursor(binding, at - 1, Identity);
            return Task.FromResult(new CreaturesPage(CreaturesPageState.Ok, entries, next, looked, null));
        }

        private static CreaturesPage Invalid() =>
            new(CreaturesPageState.InvalidCursor, [], null, 0, null, Code: "INVALID_CURSOR", Message: "The Creatures cursor differs from this exact query, page size or build.");

        public Task<CreatureResult> GetAsync(uint id, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(GetAsync), priority, cancellationToken);
            for (var i = 0; i < creatures.Length; i++)
            {
                var creature = creatures[i];
                if (creature.Id != id) continue;
                if (creature.Outcome == TableRowOutcome.Protected) return Task.FromResult(new CreatureResult(CreatureResultState.Protected, null, null, creature.ProtectedKey));
                if (creature.Outcome != TableRowOutcome.Found) return Task.FromResult(new CreatureResult(CreatureResultState.Unknown, null, null));
                var row = creature.Row ?? new TableRow(i, new Db2Row(id, creature.IsCopy, creature.CopySourceId, [$"{id}"]));
                var chains = creature.Displays ?? [.. SlotsOf(creature).Select(slot => Chain(slot, displays))];
                return Task.FromResult(new CreatureResult(CreatureResultState.Found, new CreatureDetail(Summary(creature), row, creature.ClassificationLabel, chains), null));
            }
            return Task.FromResult(new CreatureResult(CreatureResultState.Absent, null, null));
        }
    }
}
