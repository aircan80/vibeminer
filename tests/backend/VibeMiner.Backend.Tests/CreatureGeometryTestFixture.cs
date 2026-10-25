using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Backend.Tests;

internal static class CreatureGeometryTestFixture
{
    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static readonly CreatureDisplayMetadataRequest Selection = new(new("wow", new('a', 64), "us", "enUS"), new('b', 64), "123", 0);
    internal static readonly TableDataCoverage Coverage = new("complete", "1", "1", "0", "0", "0", "0", "0", "0");
    internal static CreatureGeometryResult Result(CreatureDisplayMetadataRequest? selection = null, bool ready = false)
    {
        var s = selection ?? Selection;
        var slot = new CreatureDisplaySlot(s.Slot, "10", "0.5", CreatureDisplayState.Declared, new("c9d6b6b3", 8 + s.Slot), new("c9d6b6b3", 12 + s.Slot));
        CreatureGeometryTarget Target(CreatureDisplayTargetRole role, string table, char artifact, string row, string edge) =>
            new(role, table, new(artifact, 64), Coverage, "1", new(new(table, new(artifact, 64), row, 2), false, null, new(table, 2, edge)));
        var metadata = new CreatureGeometryMetadata(ready ? CreatureDisplayMetadataState.MetadataResolved : CreatureDisplayMetadataState.NotPrepared,
            new("c9d6b6b3", new('c', 64), s.CreatureId), Coverage, slot, ready ? [Target(CreatureDisplayTargetRole.Display, "bfdaf9f1", 'd', "10", "20"),
                Target(CreatureDisplayTargetRole.Model, "7f695fa5", 'e', "20", "800")] : [],
            ModelId: ready ? "20" : null, ModelFileDataId: ready ? "800" : null, RequiredTableId: ready ? null : "bfdaf9f1");
        CreatureGeometrySource Source(CreatureGeometrySourceRole role, string id) => new(role, id, new('1', 32), new('2', 32), new('3', 64),
            100, 516, "content-blte-v1", new('4', 64), "all-root-variants-consistent-asset-v1", "encoding-native-file-v1", "root-native-file-v1", "lexicographic-complete-encoding-v1");
        return new(s.Context, s.ArtifactId, s.CreatureId, s.Slot, CreatureGeometryValidation.Policy, new("1", "us", new('f', 64), 0, new('a', 64), 0),
            ready ? CreatureGeometryState.Ready : CreatureGeometryState.NotPrepared, metadata, ready ? [Source(CreatureGeometrySourceRole.Model, "800"), Source(CreatureGeometrySourceRole.Skin, "900")] : [],
            ready ? Manifest() : null);
    }
    internal static CreatureGeometryManifest Manifest() => new(new('4', 64), new('5', 64), new('6', 64), 320, 1, 128,
        "unfiltered-declared-sections", "native-m2-position-normal-uv-winding-v1", new("notEvaluated", "notEvaluated", "notEvaluated", "notEvaluated"),
        4, 3, 6, 6, 12, 3, new(128, 48), new(176, 48), new(224, 96), new(new float[] { -6, -4, 0 }, new float[] { 3, 7, 8 }),
        800, 900, 0, "m2-static-native-v1", "m2-skin-geometry-v1", "skin-unfiltered-source-geometry-v1", "model-geometry-vmg1-v1", "all-sections-triangle-edges-v1");

    // Authored VMA1 manifest for the four-vertex geometry above: three drawn indices, one 2x2 slot.
    internal static CreatureGeometryAppearanceManifest AppearanceManifest(float[]? min = null, float[]? max = null) => new(new('7', 64), new('8', 64), new('9', 64),
        160 + 48 + 32 + 12 + 32 + 32 + 16, 1, 160, "native-m2-position-normal-uv-winding-v1", 4, 3, 1, 1, new(160, 48), new(208, 32), new(240, 12), new(252, 32), new(284, 32),
        new(316, 16), new(min ?? [-6, -4, 0], max ?? [3, 7, 8]), 128, 10, new('5', 64), "m2-single-texture-opaque-alphakey-v1", "blp2-largest-mip-within-1024-rgba8-v1",
        "m2-appearance-batches-v1", "blp2-rgba8-v2", "model-appearance-vma1-v1");
    internal static CreatureGeometryAppearance Appearance(CreatureGeometryAppearanceManifest? manifest = null) => new("creature-textured-appearance-v1",
        CreatureGeometryAppearanceState.Ready, new("10", "0", ["779", "0", "0", "0"]),
        [new(0, 11, CreatureGeometryTextureBinding.Variation, 0, "779", CreatureGeometryTextureState.Decoded, new(2, 2, 0, 2, 2, "bc1"),
            new(new('a', 32), new('b', 32), new('c', 64), 100, 200, "content-blte-v1"))], new(4, 0, 0, 2, 0), manifest ?? AppearanceManifest());

    internal sealed class Store : ICreatureGeometryStore
    {
        internal int Reads, Prepares, Opens;
        internal readonly List<Task<CreatureGeometryResult>> PreparationTasks = [];
        internal Func<CreatureGeometryReadRequest, CancellationToken, Task<CreatureGeometryResult>> Read = (r, _) => Task.FromResult(Result(r.Selection));
        internal Func<CreatureGeometryRequest, Func<CreatureGeometryProgress, Task>, CancellationToken, Task<CreatureGeometryResult>> Prepare = (r, _, _) => Task.FromResult(Result(r.Selection));
        internal Func<CreatureGeometryOpenRequest, CancellationToken, Task<ICreatureGeometryReadLease>> Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(new Lease());
        public Task<CreatureGeometryResult> ReadAsync(CreatureGeometryReadRequest r, CancellationToken t) { Reads++; return Read(r, t); }
        public Task<CreatureGeometryResult> PrepareAsync(CreatureGeometryRequest r, Func<CreatureGeometryProgress, Task> p, CancellationToken t)
        { Prepares++; var task = Prepare(r, p, t); PreparationTasks.Add(task); return task; }
        public Task<ICreatureGeometryReadLease> OpenAsync(CreatureGeometryOpenRequest r, CancellationToken t) { Opens++; return Open(r, t); }
    }
    internal sealed class Lease : ICreatureGeometryReadLease
    {
        internal readonly TaskCompletionSource Closing = Signal();
        internal Task Release = Task.CompletedTask;
        internal bool Fail;
        internal int Closes, RetirementReads;
        private readonly TaskCompletionSource retired = Signal();
        public Task Retirement { get { RetirementReads++; return retired.Task; } }
        public Stream Content { get; set; } = new MemoryStream(new byte[320], writable: false);
        public CreatureGeometryManifest Manifest { get; set; } = CreatureGeometryTestFixture.Manifest();
        public CreatureGeometryAppearanceManifest? Appearance { get; set; }
        public async ValueTask DisposeAsync()
        {
            Closes++; Closing.TrySetResult();
            try { await Release; if (Fail) throw new IOException("Authored close failure."); await Content.DisposeAsync(); retired.TrySetResult(); }
            catch (Exception error) { retired.TrySetException(error); throw; }
        }
    }
}
