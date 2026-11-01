using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Creatures;

/// <summary>Bounds of <see cref="CreaturesService"/>. <see cref="MaxScanRows"/> is the most Creature rows one list call looks at (at least 1).</summary>
public sealed record CreaturesOptions
{
    public const int DefaultMaxScanRows = 20_000;
    private readonly int maxScanRows = DefaultMaxScanRows;

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
/// The real <see cref="ICreatures"/> (D462-D465): Creature, CreatureDisplayInfo and CreatureModelData through layer 5's
/// <see cref="ITables"/>, models through <see cref="BuildModels"/> and texture headers through <see cref="BlpImages"/> on the
/// build's layer 4 files, opened on the first detail that needs them. A list pages Creature in its row order and reads nothing
/// else; each call looks at no more than <see cref="CreaturesOptions.MaxScanRows"/> rows and returns a cursor to go on.
/// </summary>
public sealed class CreaturesService : ICreatures
{
    private const int PageRows = 1000;
    private readonly ITables tables;
    private readonly IFileSystem files;
    private readonly BlpImages images;
    private readonly CreaturesOptions options;

    public CreaturesService(ITables tables, IFileSystem files, BlpImages images, CreaturesOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tables); ArgumentNullException.ThrowIfNull(files); ArgumentNullException.ThrowIfNull(images);
        this.tables = tables; this.files = files; this.images = images; this.options = options ?? new();
    }

    public async Task<CreaturesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        var opened = await tables.ForAsync(build, locale, cancellationToken).ConfigureAwait(false);
        if (opened.State != FilesOpenState.Open || opened.Tables is null)
            return new(build, locale, opened.State, null, opened.Code, opened.Message, opened.ObservedAt);
        return new(build, locale, FilesOpenState.Open, new Handle(this, opened.Tables), null, null, null);
    }

    private sealed class Handle(CreaturesService owner, IBuildTables tables) : IBuildCreatures
    {
        private static readonly TableRef Creature = TableRef.ForName(CreatureLayout.Table);
        private readonly SemaphoreSlim filesGate = new(1, 1);
        private IBuildFiles? buildFiles;
        private BuildModels? models;
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
                buildFiles = null; models = null;
            }
            finally { filesGate.Release(); }
            await tables.DisposeAsync().ConfigureAwait(false);
        }

        private static CreaturesPageState PageState(string code) =>
            code == CreaturesSourceException.SchemaUnsupported ? CreaturesPageState.SchemaUnsupported : CreaturesPageState.SourceInvalid;

        private static CreatureResultState ResultState(string code) =>
            code == CreaturesSourceException.SchemaUnsupported ? CreatureResultState.SchemaUnsupported : CreatureResultState.SourceInvalid;

        public async Task<CreaturesPage> ListAsync(CreatureQuery query, TablePriority priority, CancellationToken cancellationToken)
        {
            CreatureQueries.Validate(query);
            Enter(priority, cancellationToken);
            var schema = await tables.SchemaAsync(Creature, priority, cancellationToken).ConfigureAwait(false);
            var creature = schema.Table;
            if (!TableQueries.IsReadable(creature.State)) return new(CreaturesPageState.CreatureNotReadable, [], null, 0, creature);
            var binding = CreatureQueries.Binding(Build, Locale, query);
            (long After, string? Identity)? cursor = null;
            if (query.After is { } after)
            {
                cursor = CreatureQueries.DecodeCursor(after, binding);
                // An exact ID never makes a cursor, so none belongs to it.
                if (cursor is null || query.ExactId is not null)
                    return new(CreaturesPageState.InvalidCursor, [], null, 0, creature, Code: "INVALID_CURSOR",
                        Message: "The Creatures cursor differs from this exact query, page size or build.");
            }
            try
            {
                var layout = CreatureLayout.Resolve(schema.Columns ?? [], schema.Schema);
                if (query.ExactId is { } id)
                {
                    var found = await tables.GetAsync(Creature, id, priority, cancellationToken).ConfigureAwait(false);
                    return new(CreaturesPageState.Ok, found.Outcome == TableRowOutcome.Found ? [layout.Summary(found.Row!.Row)] : [], null, 1, creature,
                        found.Outcome, found.ProtectedKey);
                }
                return await ScanAsync(layout, query, binding, cursor, creature, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (CreaturesSourceException problem) { return new(PageState(problem.Code), [], null, 0, creature, Code: problem.Code, Message: problem.Message); }
        }

        private async Task<CreaturesPage> ScanAsync(CreatureLayout layout, CreatureQuery query, string binding, (long After, string? Identity)? cursor,
            TableEntry creature, TablePriority priority, CancellationToken cancellationToken)
        {
            var search = query.Query.Length != 0;
            var entries = new List<CreatureSummary>(query.Limit);
            var after = cursor?.After ?? -1;
            var identity = cursor?.Identity;
            long looked = 0;
            var end = false;
            while (entries.Count < query.Limit && looked < owner.options.MaxScanRows)
            {
                var budget = (int)Math.Min(PageRows, owner.options.MaxScanRows - looked);
                // An empty query lists every row it reads, so it reads no more than it can return.
                if (!search) budget = Math.Min(budget, query.Limit - entries.Count);
                var page = await tables.PageAsync(new TablePageRequest(Creature, after, budget, null, identity), priority, cancellationToken).ConfigureAwait(false);
                switch (page.State)
                {
                    case TablePageState.Ok: break;
                    case TablePageState.Stale: return new(CreaturesPageState.Stale, [], null, looked, page.Table);
                    case TablePageState.NotReadable: return new(CreaturesPageState.CreatureNotReadable, [], null, looked, page.Table);
                    default:
                        return new(CreaturesPageState.InvalidCursor, [], null, looked, page.Table, Code: page.Code ?? "INVALID_CURSOR", Message: page.Message);
                }
                identity = page.Identity;
                var consumed = 0;
                foreach (var row in page.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    consumed++; looked++; after = row.Ordinal;
                    if (CreatureQueries.Matches(layout.NameOf(row.Row), query.Query)) entries.Add(layout.Summary(row.Row));
                    if (entries.Count == query.Limit) break;
                }
                if (page.NextAfter is null && consumed == page.Rows.Count) { end = true; break; }
                if (page.Rows.Count == 0) { end = true; break; }
            }
            return new(CreaturesPageState.Ok, entries, end ? null : CreatureQueries.EncodeCursor(binding, after, identity), looked, creature);
        }

        public async Task<CreatureResult> GetAsync(uint id, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(priority, cancellationToken);
            var schema = await tables.SchemaAsync(Creature, priority, cancellationToken).ConfigureAwait(false);
            var creature = schema.Table;
            if (!TableQueries.IsReadable(creature.State)) return new(CreatureResultState.CreatureNotReadable, null, creature);
            CreatureSummary summary;
            TableRow row;
            try
            {
                var layout = CreatureLayout.Resolve(schema.Columns ?? [], schema.Schema);
                var found = await tables.GetAsync(Creature, id, priority, cancellationToken).ConfigureAwait(false);
                switch (found.Outcome)
                {
                    case TableRowOutcome.Found: break;
                    case TableRowOutcome.Absent: return new(CreatureResultState.Absent, null, creature);
                    case TableRowOutcome.Protected: return new(CreatureResultState.Protected, null, creature, found.ProtectedKey);
                    default: return new(CreatureResultState.Unknown, null, creature);
                }
                row = found.Row!;
                summary = layout.Summary(row.Row);
            }
            catch (CreaturesSourceException problem) { return new(ResultState(problem.Code), null, creature, null, problem.Code, problem.Message); }
            var call = new Call(priority, cancellationToken);
            var displays = new CreatureDisplayChain[summary.Slots.Count];
            for (var i = 0; i < displays.Length; i++) displays[i] = await ChainAsync(call, summary.Slots[i]).ConfigureAwait(false);
            return new(CreatureResultState.Found, new CreatureDetail(summary, row, CreatureLayout.ClassificationLabel(summary.Facts[0].Value), displays), creature);
        }

        // What one detail call has read, so four slots naming the same display, model or texture read it once.
        private sealed class Call(TablePriority priority, CancellationToken cancellationToken)
        {
            public TablePriority Priority { get; } = priority;
            public CancellationToken Token { get; } = cancellationToken;
            public Dictionary<DisplayRole, Target> Targets { get; } = [];
            public Dictionary<uint, ModelRead> Models { get; } = [];
            public Dictionary<uint, SkinRead<M2Skin>> Skins { get; } = [];
            public Dictionary<uint, BlpInspection> Textures { get; } = [];
        }

        // A chain table's checked entry, header columns and edge column (null: unsupported, or the table isn't readable).
        private sealed record Target(DisplayRole Role, TableEntry Entry, IReadOnlyList<Db2Column> Columns, int? Edge)
        {
            public bool Readable => TableQueries.IsReadable(Entry.State);
        }

        private async Task<Target> TargetAsync(Call call, DisplayRole role)
        {
            if (call.Targets.TryGetValue(role, out var known)) return known;
            var schema = await tables.SchemaAsync(TableRef.ForName(CreatureDisplayRules.Table(role)), call.Priority, call.Token).ConfigureAwait(false);
            var columns = schema.Columns ?? [];
            var target = new Target(role, schema.Table, columns, TableQueries.IsReadable(schema.Table.State)
                ? CreatureDisplayRules.EdgeColumn(role, schema.Table.Counts?.LayoutHash, columns, schema.Schema) : null);
            call.Targets[role] = target;
            return target;
        }

        // CreatureDisplayCoordinator's chain on layer 5: slot, display row by DisplayID, model row by ModelID, model file.
        private async Task<CreatureDisplayChain> ChainAsync(Call call, CreatureDisplaySlot slot)
        {
            if (slot.State == CreatureSlotState.NoDeclaredDisplay) return new(slot, CreatureDisplayChainState.NoDeclaredDisplay);
            if (slot.State == CreatureSlotState.InvalidDisplayReference) return new(slot, CreatureDisplayChainState.InvalidDisplayReference);
            var displayId = (uint)slot.DisplayId;
            var display = await TargetAsync(call, DisplayRole.Display).ConfigureAwait(false);
            if (!display.Readable) return new(slot, CreatureDisplayChainState.DisplayNotReadable, display.Entry);
            if (display.Edge is not { } modelColumn)
                return new(slot, CreatureDisplayChainState.DisplayUnsupported, display.Entry, Code: CreatureDisplayRules.Unsupported,
                    Message: "This CreatureDisplayInfo layout doesn't qualify ModelID as one verified unsigned 16-bit scalar.");
            var found = await tables.GetAsync(TableRef.ForName(CreatureDisplayRules.DisplayTable), displayId, call.Priority, call.Token).ConfigureAwait(false);
            switch (found.Outcome)
            {
                case TableRowOutcome.Found: break;
                case TableRowOutcome.Absent: return new(slot, CreatureDisplayChainState.DisplayAbsent, found.Table);
                case TableRowOutcome.Protected: return new(slot, CreatureDisplayChainState.DisplayProtected, found.Table, ProtectedKey: found.ProtectedKey);
                default: return new(slot, CreatureDisplayChainState.DisplayUnknown, found.Table);
            }
            var displayRow = found.Row!;
            var displayFacts = CreatureDisplayRules.Facts(DisplayRole.Display, display.Columns, displayRow.Row);
            var evidence = CreatureDisplayRules.TextureEvidence(display.Columns, displayRow.Row);
            if (CreatureDisplayRules.Edge(displayRow.Row, modelColumn, DisplayRole.Display, displayId) is not { } modelId)
                return new(slot, CreatureDisplayChainState.DisplayInvalid, found.Table, displayRow, Code: CreatureDisplayRules.Integrity,
                    Message: "The CreatureDisplayInfo row's ModelID isn't a canonical unsigned value of at most 65535.", DisplayFacts: displayFacts, Textures: evidence);
            var chain = new CreatureDisplayChain(slot, CreatureDisplayChainState.NoDeclaredModel, found.Table, displayRow, modelId, DisplayFacts: displayFacts, Textures: evidence);
            if (modelId == 0) return chain;
            var model = await TargetAsync(call, DisplayRole.Model).ConfigureAwait(false);
            if (!model.Readable) return chain with { State = CreatureDisplayChainState.ModelNotReadable, ModelData = model.Entry };
            if (model.Edge is not { } fileColumn)
                return chain with { State = CreatureDisplayChainState.ModelUnsupported, ModelData = model.Entry, Code = CreatureDisplayRules.Unsupported,
                    Message = "This CreatureModelData layout doesn't qualify FileDataID as one verified unsigned 32-bit scalar." };
            var modelRow = await tables.GetAsync(TableRef.ForName(CreatureDisplayRules.ModelTable), modelId, call.Priority, call.Token).ConfigureAwait(false);
            chain = chain with { ModelData = modelRow.Table };
            switch (modelRow.Outcome)
            {
                case TableRowOutcome.Found: break;
                case TableRowOutcome.Absent: return chain with { State = CreatureDisplayChainState.ModelAbsent };
                case TableRowOutcome.Protected: return chain with { State = CreatureDisplayChainState.ModelProtected, ProtectedKey = modelRow.ProtectedKey };
                default: return chain with { State = CreatureDisplayChainState.ModelUnknown };
            }
            chain = chain with { ModelRow = modelRow.Row, ModelFacts = CreatureDisplayRules.Facts(DisplayRole.Model, model.Columns, modelRow.Row!.Row) };
            if (CreatureDisplayRules.Edge(modelRow.Row!.Row, fileColumn, DisplayRole.Model, modelId) is not { } fileDataId)
                return chain with { State = CreatureDisplayChainState.ModelInvalid, Code = CreatureDisplayRules.Integrity,
                    Message = "The CreatureModelData row's FileDataID isn't a canonical unsigned 32-bit value." };
            chain = chain with { ModelFileDataId = fileDataId };
            if (fileDataId == 0) return chain with { State = CreatureDisplayChainState.NoDeclaredModelFile };
            return chain with { State = CreatureDisplayChainState.MetadataResolved, Model = await ModelAsync(call, fileDataId, evidence).ConfigureAwait(false) };
        }

        // The model read (detail only), its main skin's drawn textures under D088, and their headers once no refusal stops it (D464).
        private async Task<CreatureModel> ModelAsync(Call call, uint fileDataId, CreatureTextureEvidence? evidence)
        {
            var (opened, failed) = await FilesAsync(call.Token).ConfigureAwait(false);
            if (opened is null)
                return new(CreatureModelState.FilesNotOpen, fileDataId, null, new(CreatureAppearanceState.ModelNotRead), [], Code: failed!.Code,
                    Message: failed.Message, ObservedAt: failed.ObservedAt);
            if (!call.Models.TryGetValue(fileDataId, out var read))
                call.Models[fileDataId] = read = await opened.Value.Models.ReadModelAsync(FileRef.Id(fileDataId), call.Token).ConfigureAwait(false);
            if (read.State != ModelReadState.Read || read.Model is not { } m2)
                return new(CreatureModelState.ReadFailed, fileDataId, read.State, new(CreatureAppearanceState.ModelNotRead), [], Code: read.Code,
                    Message: read.Message, ObservedAt: read.ObservedAt, KeyNames: read.KeyNames);
            // Geometry's checks came first: its model profile, no positive main skin, then the skin read, before any appearance rule.
            if (CreatureDisplayRules.GeometryRefusal(m2) is { } noSkin)
                return new(CreatureModelState.Read, fileDataId, read.State, noSkin, CreatureDisplayRules.Textures(read, evidence, null));
            var skin = m2.SkinFileDataIds[0];
            if (!call.Skins.TryGetValue(fileDataId, out var skinRead))
                call.Skins[fileDataId] = skinRead = await opened.Value.Models.ReadSkinAsync(read, 0, call.Token).ConfigureAwait(false);
            if (skinRead.State != ModelReadState.Read || skinRead.Skin is not { } m2Skin)
                return new(CreatureModelState.Read, fileDataId, read.State, new(CreatureAppearanceState.SkinNotRead, skinRead.Code, skinRead.Message),
                    CreatureDisplayRules.Textures(read, evidence, null), skin, SkinState: skinRead.State, SkinKeyNames: skinRead.KeyNames);
            var batches = CreatureDisplayRules.DrawnTextures(m2, m2Skin);
            var textures = CreatureDisplayRules.Textures(read, evidence, batches.Drawn);
            if (CreatureDisplayRules.Refusal(evidence, textures, batches.Refusal) is { } refusal)
                return new(CreatureModelState.Read, fileDataId, read.State, refusal with { BlendedBatches = batches.Blended }, textures, skin, SkinState: skinRead.State);
            var inspected = new CreatureTexture[textures.Count];
            CreatureAppearance? stop = null;
            for (var i = 0; i < inspected.Length; i++)
            {
                var texture = textures[i];
                if (!texture.Drawn || texture.State != CreatureTextureState.Resolved) { inspected[i] = texture; continue; }
                var id = texture.FileDataId!.Value;
                if (!call.Textures.TryGetValue(id, out var header))
                    call.Textures[id] = header = await owner.images.InspectAsync(opened.Value.Files, FileRef.Id(id), call.Token).ConfigureAwait(false);
                inspected[i] = texture with { Image = header.State, FileState = header.FileState, HoleKeys = header.HoleKeys, Code = header.Code };
                if (header.State != BlpImageState.Ready && stop is null)
                    stop = new(CreatureDisplayRules.TextureFailure(header.State), header.Code, header.Message, texture.Ordinal);
            }
            return new(CreatureModelState.Read, fileDataId, read.State, (stop ?? new(CreatureAppearanceState.Ready)) with { BlendedBatches = batches.Blended },
                inspected, skin, SkinState: skinRead.State);
        }

        // The build's layer 4 handle and its model reader, opened once and held until dispose. A failed open isn't kept.
        private async Task<((IBuildFiles Files, BuildModels Models)? Opened, FilesOpenResult? Failed)> FilesAsync(CancellationToken cancellationToken)
        {
            await filesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                if (buildFiles is not null) return ((buildFiles, models!), null);
                var opened = await owner.files.ForAsync(Build, Locale, cancellationToken).ConfigureAwait(false);
                if (opened.State != FilesOpenState.Open || opened.Files is null) return (null, opened);
                buildFiles = opened.Files;
                models = new BuildModels(buildFiles);
                return ((buildFiles, models), null);
            }
            finally { filesGate.Release(); }
        }
    }
}
