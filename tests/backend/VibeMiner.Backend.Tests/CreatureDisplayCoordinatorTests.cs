using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureDisplayCoordinatorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly CreaturesContext Context = new("wow", new('a', 64), "us", "enUS");
    private static readonly CreatureDisplayMetadataRequest Request = new(Context, new('b', 64), "1", 0);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Progress(CreaturesPreparationProgress _) => Task.CompletedTask;

    [Theory]
    [InlineData("0", CreatureDisplayMetadataState.NoDeclaredDisplay)]
    [InlineData("-1", CreatureDisplayMetadataState.InvalidDisplayReference)]
    [InlineData(null, CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope)]
    public async Task NoOptionalWorkForMissingZeroOrNegativeDeclaredSlot(string? display, CreatureDisplayMetadataState state)
    {
        var source = new Sources(); source.DisplayByCreature["1"] = display;
        await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.PrepareDisplayAsync(Request, Progress, default);
        Assert.Equal(state, result.State); Assert.Empty(result.Sources); Assert.Equal(0, source.Probes); Assert.Empty(source.Prepared);
        Assert.Equal(1, source.Fences); Assert.Equal(CreatureDisplayAppearance.NotEvaluated, result.Appearance);
        if (display is null) Assert.Equal(CreatureDisplayAbsence.AbsentFromCompleteSource, result.Absence);
    }
    [Fact]
    public async Task LocalReadsReportOnlyTheNextNecessarySourceAndNeverPrepareOrJoin()
    {
        var source = new Sources(); await using var store = new CreatureDisplayMetadataStore(source);
        var missingDisplay = await store.ReadDisplayAsync(Request, default);
        Assert.Equal(CreatureDisplayMetadataState.NotPrepared, missingDisplay.State); Assert.Equal("bfdaf9f1", missingDisplay.RequiredTableId); Assert.Empty(missingDisplay.Sources);
        source.DisplayReady = true;
        var missingModel = await store.ReadDisplayAsync(Request, default);
        Assert.Equal("7f695fa5", missingModel.RequiredTableId); Assert.Equal("33", missingModel.ModelId); Assert.Single(missingModel.Sources);
        Assert.Empty(source.Prepared);
    }
    [Fact]
    public async Task CompatibleSelectionsShareDisplayAndEarlyStopAndLateJoinDoNotWaitForModelNetwork()
    {
        var source = new Sources(); source.DisplayByCreature["2"] = "200"; source.EdgeByDisplay["200"] = "0";
        var displayEntered = Signal(); var displayRelease = Signal(); var modelEntered = Signal(); var modelRelease = Signal();
        source.Prepare = async (role, token) =>
        {
            if (role == CreatureDisplayTargetRole.Display) { displayEntered.SetResult(); await displayRelease.Task.WaitAsync(token); source.DisplayReady = true; }
            else { modelEntered.SetResult(); await modelRelease.Task.WaitAsync(token); source.ModelReady = true; }
        };
        await using var store = new CreatureDisplayMetadataStore(source);
        try
        {
            var positive = store.PrepareDisplayAsync(Request, Progress, default); await displayEntered.Task.WaitAsync(Deadline);
            var early = store.PrepareDisplayAsync(Request with { CreatureId = "2" }, Progress, default);
            displayRelease.SetResult(); await modelEntered.Task.WaitAsync(Deadline);
            Assert.Equal(CreatureDisplayMetadataState.NoDeclaredModel, (await early.WaitAsync(Deadline)).State); Assert.False(positive.IsCompleted);
            Assert.Equal((1, false, true), store.Pressure);
            var late = await store.PrepareDisplayAsync(Request with { CreatureId = "2", Slot = 1 }, Progress, default).WaitAsync(Deadline);
            Assert.Equal(CreatureDisplayMetadataState.NoDeclaredModel, late.State); Assert.False(positive.IsCompleted);
            var local = await store.ReadDisplayAsync(Request, default).WaitAsync(Deadline); Assert.Equal("7f695fa5", local.RequiredTableId);
            Assert.Equal([CreatureDisplayTargetRole.Display, CreatureDisplayTargetRole.Model], source.Prepared);
            modelRelease.SetResult(); var ready = await positive.WaitAsync(Deadline);
            Assert.Equal(CreatureDisplayMetadataState.MetadataResolved, ready.State); Assert.Equal("777", ready.ModelFileDataId);
            Assert.Equal([CreatureDisplayTargetRole.Display, CreatureDisplayTargetRole.Model], ready.Sources.Select(s => s.Role));
        }
        finally { displayRelease.TrySetResult(); modelRelease.TrySetResult(); }
    }
    [Fact]
    public async Task CancelOnlyPositiveModelConsumerAtAdmissionBoundaryDoesNotStartModelForEarlyStopPeer()
    {
        var source = new Sources { DisplayReady = true }; source.DisplayByCreature["2"] = "200"; source.EdgeByDisplay["200"] = "0";
        var positiveProbe = Signal(); var releaseProbe = Signal();
        source.BeforeProbe = async (role, id, _) =>
        { if (role == CreatureDisplayTargetRole.Display && id == "100") { positiveProbe.SetResult(); await releaseProbe.Task; } };
        await using var store = new CreatureDisplayMetadataStore(source); using var stop = new CancellationTokenSource();
        try
        {
            var positive = store.PrepareDisplayAsync(Request, Progress, stop.Token); await positiveProbe.Task.WaitAsync(Deadline);
            var peer = store.PrepareDisplayAsync(Request with { CreatureId = "2" }, Progress, default);
            stop.Cancel(); releaseProbe.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => positive);
            Assert.Equal(CreatureDisplayMetadataState.NoDeclaredModel, (await peer.WaitAsync(Deadline)).State);
            Assert.DoesNotContain(CreatureDisplayTargetRole.Model, source.Prepared);
        }
        finally { releaseProbe.TrySetResult(); }
    }
    [Fact]
    public async Task OneStopPreservesSharedPrerequisiteButLastStopWaitsActualCleanup()
    {
        var source = new Sources(); var entered = Signal(); var cleanupEntered = Signal(); var cleanup = Signal(); var never = Signal();
        CancellationToken shared = default;
        source.Prepare = async (_, token) =>
        { shared = token; entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.SetResult(); await cleanup.Task; } };
        await using var store = new CreatureDisplayMetadataStore(source); using var firstStop = new CancellationTokenSource(); using var secondStop = new CancellationTokenSource();
        try
        {
            var first = store.PrepareDisplayAsync(Request, Progress, firstStop.Token); await entered.Task.WaitAsync(Deadline);
            var second = store.PrepareDisplayAsync(Request with { CreatureId = "2" }, Progress, secondStop.Token);
            firstStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.False(shared.IsCancellationRequested);
            secondStop.Cancel(); await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(second.IsCompleted);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => store.PrepareDisplayAsync(Request, Progress, default))).Code);
            cleanup.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        finally { cleanup.TrySetResult(); }
    }
    [Fact]
    public async Task LastStopDuringItsOwnLocalStepStillWaitsActualPrerequisiteCleanup()
    {
        // Regression for #11: the last owner is stopped while its own re-probe runs, so the stop surfaces
        // as that step's cancellation inside the worker instead of at the worker's cancellation check.
        var source = new Sources(); source.DisplayByCreature["2"] = "200";
        var entered = Signal(); var cleanupEntered = Signal(); var cleanup = Signal(); var never = Signal(); var reprobing = Signal();
        var secondProbes = 0; var released = false;
        source.Prepare = async (_, token) =>
        { entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.SetResult(); await cleanup.Task; } };
        source.BeforeProbe = async (_, id, token) =>
        { if (id == "200" && Interlocked.Increment(ref secondProbes) == 2) { reprobing.SetResult(); await never.Task.WaitAsync(token); } };
        await using var store = new CreatureDisplayMetadataStore(source); using var firstStop = new CancellationTokenSource(); using var secondStop = new CancellationTokenSource();
        try
        {
            var first = store.PrepareDisplayAsync(Request, Progress, firstStop.Token); await entered.Task.WaitAsync(Deadline);
            var second = store.PrepareDisplayAsync(Request with { CreatureId = "2" }, Progress, secondStop.Token);
            var completedAfterRelease = second.ContinueWith(_ => Volatile.Read(ref released), TaskContinuationOptions.ExecuteSynchronously);
            // The first stop wakes the worker, which re-probes the still-live second consumer and holds there.
            firstStop.Cancel(); await reprobing.Task.WaitAsync(Deadline); Assert.False(cleanupEntered.Task.IsCompleted);
            secondStop.Cancel(); await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(second.IsCompleted);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => store.PrepareDisplayAsync(Request, Progress, default))).Code);
            Volatile.Write(ref released, true); cleanup.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.True(await completedAfterRelease.WaitAsync(Deadline));
        }
        finally { cleanup.TrySetResult(); }
    }
    [Theory]
    [InlineData("complete", CreatureDisplayAbsence.AbsentFromCompleteSource)]
    [InlineData("unencryptedSections", CreatureDisplayAbsence.UnknownOutsideReadableScope)]
    public async Task MissingTargetUsesItsOwnCoverageAndNeverStartsModel(string scope, CreatureDisplayAbsence absence)
    {
        var source = new Sources { DisplayReady = true, DisplayMatch = "0", TargetScope = scope };
        await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.PrepareDisplayAsync(Request, Progress, default);
        Assert.Equal(CreatureDisplayMetadataState.DisplayMissingInAdmittedScope, result.State); Assert.Equal(absence, result.Absence);
        Assert.Null(result.Sources[0].Row); Assert.Equal("0", result.Sources[0].MatchCount); Assert.Empty(source.Prepared);
    }
    [Fact]
    public async Task DuplicateTargetNeverChoosesApparentlyUsefulFirstRow()
    {
        var source = new Sources { DisplayReady = true, DisplayMatch = "2" }; await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.PrepareDisplayAsync(Request, Progress, default);
        Assert.Equal(CreatureDisplayMetadataState.DisplayAmbiguous, result.State); Assert.Equal("2", result.Sources[0].MatchCount);
        Assert.Null(result.Sources[0].Row); Assert.Null(result.ModelId); Assert.Empty(source.Prepared);
    }
    [Fact]
    public async Task UnsupportedSourceIsDeliveredAndFencedWithoutInventingEdge()
    {
        var source = new Sources { DisplayReady = true, UnsupportedDisplay = true }; await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.PrepareDisplayAsync(Request, Progress, default);
        Assert.Equal(CreatureDisplayMetadataState.Unsupported, result.State); Assert.Single(result.Sources); Assert.Null(result.Sources[0].MatchCount);
        Assert.Null(result.Sources[0].Row); Assert.Null(result.ModelId); Assert.Single(source.FencedTargets.Single()); Assert.Empty(source.Prepared);
    }
    [Fact]
    public async Task OptionalModelFailureRetainsProvedDisplayEdgeAndCoreUsability()
    {
        var source = new Sources { DisplayReady = true }; source.Prepare = (_, _) => throw new IOException("private remote details");
        await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.PrepareDisplayAsync(Request, Progress, default);
        Assert.Equal(CreatureDisplayMetadataState.Unavailable, result.State); Assert.Equal("33", result.ModelId); Assert.Null(result.ModelFileDataId);
        Assert.Single(result.Sources); Assert.Equal(CreatureDisplayTargetRole.Model, result.Problem!.Target); Assert.DoesNotContain("private", result.Problem.Message);
        source.DisplayByCreature["2"] = "0";
        Assert.Equal(CreatureDisplayMetadataState.NoDeclaredDisplay, (await store.ReadDisplayAsync(Request with { CreatureId = "2" }, default)).State);
    }
    [Fact]
    public async Task ModelZeroIsEvidenceAndNeverClaimsAFileOrAppearance()
    {
        var source = new Sources { DisplayReady = true, ModelReady = true, FileEdge = "0" }; await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.ReadDisplayAsync(Request, default);
        Assert.Equal(CreatureDisplayMetadataState.NoDeclaredModelFile, result.State); Assert.Equal("0", result.Sources[1].Row!.EdgeValue.Value);
        Assert.Null(result.ModelFileDataId); Assert.Equal(CreatureDisplayAppearance.NotEvaluated, result.Appearance);
    }
    [Fact]
    public async Task FinalFenceFailureReturnsNoMixedGenerationResult()
    {
        var source = new Sources { DisplayReady = true, ModelReady = true }; source.Finish = (_, _) => throw CreatureDisplayPolicy.Stale();
        await using var store = new CreatureDisplayMetadataStore(source);
        Assert.Equal("CREATURE_DISPLAY_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => store.ReadDisplayAsync(Request, default))).Code);
        Assert.Equal(2, source.FencedTargets.Single().Count);
    }
    [Fact]
    public async Task OriginalCoreSlotCannotRetargetAfterPrerequisiteWait()
    {
        var source = new Sources(); source.Prepare = (role, _) =>
        { source.DisplayReady = true; source.DisplayByCreature["1"] = "999"; return Task.CompletedTask; };
        await using var store = new CreatureDisplayMetadataStore(source);
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => store.PrepareDisplayAsync(Request, Progress, default))).Code);
        Assert.DoesNotContain(CreatureDisplayTargetRole.Model, source.Prepared); Assert.Equal(0, source.Fences);
    }
    [Fact]
    public async Task PrerequisiteThatDoesNotPublishIsBoundedAndDoesNotLoopForever()
    {
        var source = new Sources(); source.Prepare = (_, _) => Task.CompletedTask;
        await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.PrepareDisplayAsync(Request, Progress, default).WaitAsync(Deadline);
        Assert.Equal(CreatureDisplayMetadataState.Unavailable, result.State); Assert.Single(source.Prepared);
    }
    [Fact]
    public async Task ShutdownWaitsActualSourceCleanupAndClosingObservers()
    {
        var source = new Sources(); var entered = Signal(); var cleanupEntered = Signal(); var cleanup = Signal(); var never = Signal();
        source.Prepare = async (_, token) =>
        { entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.SetResult(); await cleanup.Task; } };
        var store = new CreatureDisplayMetadataStore(source);
        var call = store.PrepareDisplayAsync(Request, Progress, default); await entered.Task.WaitAsync(Deadline);
        var close = store.DisposeAsync().AsTask(); await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(close.IsCompleted); Assert.False(source.Disposed);
        cleanup.SetResult(); await close.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.True(source.Disposed);
    }
    [Theory]
    [InlineData("authorize")]
    [InlineData("probe")]
    [InlineData("finish")]
    public async Task DirectStoreShutdownCancelsAcceptedLocalStepsAndWaitsTheirCleanup(string phase)
    {
        var entered = Signal(); var cleanupEntered = Signal(); var release = Signal(); var never = Signal();
        async Task Block(CancellationToken token)
        { entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.SetResult(); await release.Task; } }
        var source = new Sources { DisplayReady = true, ModelReady = true };
        if (phase == "authorize") source.BeforeAuthorize = (_, token) => Block(token);
        if (phase == "probe") source.BeforeProbe = (_, _, token) => Block(token);
        if (phase == "finish") source.Finish = (_, token) => Block(token);
        var store = new CreatureDisplayMetadataStore(source);
        try
        {
            var call = store.PrepareDisplayAsync(Request, Progress, default); await entered.Task.WaitAsync(Deadline);
            var closing = store.DisposeAsync().AsTask(); await cleanupEntered.Task.WaitAsync(Deadline);
            Assert.False(closing.IsCompleted); Assert.False(call.IsCompleted); Assert.False(source.Disposed);
            release.SetResult(); await closing.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.True(source.Disposed);
        }
        finally { release.TrySetResult(); await store.DisposeAsync(); }
    }

    [Fact]
    public async Task LocalFactsCarryOnlyUniquelyMatchedRowsAndNeverPrepare()
    {
        TableDataColumn C(string name, bool id = false) => new(name, "integer", id, null);
        var source = new Sources
        {
            DisplayReady = true, ModelReady = true,
            Rows = role => role == CreatureDisplayTargetRole.Display
                ? ([C("ID", true), C("ModelID"), C("CreatureModelScale"), C("SizeClass"), C("TextureVariationFileDataID[0]"), C("TextureVariationFileDataID[1]")], ["100", "33", "1.5", "2", "800", "0"])
                // Classic CreatureModelData has no ModelScale in this authored layout: the fact is unsupported, never zero.
                : ([C("ID", true), C("FileDataID"), C("CollisionHeight")], ["33", "777", "2.25"])
        };
        await using var store = new CreatureDisplayMetadataStore(source);
        var facts = await store.ReadFactsAsync(Request, default);
        Assert.Equal(CreatureDisplayMetadataState.MetadataResolved, facts.Metadata.State); Assert.Equal("creature-display-facts-v1", facts.FactsRevision);
        Assert.Equal([CreatureDisplayTargetRole.Display, CreatureDisplayTargetRole.Model], facts.Records.Select(r => r.Role));
        var display = facts.Records[0];
        Assert.Equal(("bfdaf9f1", "CreatureDisplayInfo", "100"), (display.TableId, display.Table, display.RecordId));
        Assert.Equal(["model", "scale", "textureVariations"], display.Facts.Select(f => f.Key));
        Assert.Equal(["33"], display.Facts[0].Cells.Select(c => c.Value)); Assert.Equal(2, display.Facts[1].Cells.Single().ColumnIndex);
        Assert.Equal(["TextureVariationFileDataID[0]:800", "TextureVariationFileDataID[1]:0"], display.Facts[2].Cells.Select(c => $"{c.Column}:{c.Value}"));
        Assert.Equal(["SizeClass"], display.Raw.Select(c => c.Column));
        var model = facts.Records[1];
        Assert.Equal(["file:Value", "scale:Unsupported"], model.Facts.Select(f => $"{f.Key}:{f.State}")); Assert.Empty(model.Facts[1].Cells);
        Assert.Equal(["CollisionHeight:2.25"], model.Raw.Select(c => $"{c.Column}:{c.Value}"));
        Assert.Empty(source.Prepared);

        // Two CreatureDisplayInfo rows have this ID: no row, so no record and no model lookup.
        source.DisplayMatch = "2";
        var ambiguous = await store.ReadFactsAsync(Request, default);
        Assert.Equal(CreatureDisplayMetadataState.DisplayAmbiguous, ambiguous.Metadata.State); Assert.Empty(ambiguous.Records);
        // Not prepared stays local and has no records.
        source.DisplayMatch = "1"; source.DisplayReady = false;
        var local = await store.ReadFactsAsync(Request, default);
        Assert.Equal(CreatureDisplayMetadataState.NotPrepared, local.Metadata.State); Assert.Empty(local.Records); Assert.Empty(source.Prepared);
    }

    [Fact]
    public void A_row_whose_values_were_not_kept_whole_has_no_facts_record()
    {
        var snapshot = Source("bfdaf9f1", new('d', 64)) with { Columns = [new("ID", "integer", true, null), new("ModelID", "integer", false, null)] };
        var row = new CreatureDisplayRowEvidence(new("bfdaf9f1", new('d', 64), "100", 1), false, null, new("bfdaf9f1", 1, "33"));
        var target = new CreatureDisplayTarget(CreatureDisplayTargetRole.Display, snapshot, "1", row);
        var metadata = new CreatureDisplayMetadataResult(Context, Request.ArtifactId, "1", 0, "creature-display-metadata-v1", null!, null!, null!, null,
            CreatureDisplayMetadataState.NoDeclaredModel, CreatureDisplayAppearance.NotEvaluated, [target]);
        var binding = new CreatureDisplayCatalogBinding("bfdaf9f1", new(null!, null!, null!, "original"));
        Assert.Empty(CreatureDisplayFacts.Project(metadata, [new(target, binding, "33")]).Records);
        Assert.Empty(CreatureDisplayFacts.Project(metadata, [new(target, binding, "33", Values: ["100"])]).Records);
        Assert.Single(CreatureDisplayFacts.Project(metadata, [new(target, binding, "33", Values: ["100", "0"])]).Records);
    }

    [Fact]
    public void A_fact_wider_than_sixteen_cells_is_refused_whole()
    {
        TableDataColumn C(string name, bool id = false) => new(name, "integer", id, null);
        var columns = new[] { C("ID", true), C("ModelID") }.Concat(Enumerable.Range(0, 17).Select(i => C($"TextureVariationFileDataID[{i}]"))).ToArray();
        var snapshot = Source("bfdaf9f1", new('d', 64)) with { Columns = columns };
        var row = new CreatureDisplayRowEvidence(new("bfdaf9f1", new('d', 64), "100", 1), false, null, new("bfdaf9f1", 1, "33"));
        var target = new CreatureDisplayTarget(CreatureDisplayTargetRole.Display, snapshot, "1", row);
        var values = new[] { "100", "33" }.Concat(Enumerable.Repeat("0", 17)).ToArray();
        Assert.Equal("CREATURES_RESULT_LIMIT", Assert.Throws<DiscoveryException>(() => CreatureDisplayFacts.Record(target, row, values)).Code);
        // Sixteen still fit.
        var fits = target with { Snapshot = snapshot with { Columns = columns[..18] } };
        Assert.Equal(16, CreatureDisplayFacts.Record(fits, row, values[..18]).Facts[2].Cells.Count);
    }

    private sealed class Sources : ICreatureDisplaySources
    {
        internal readonly Dictionary<string, string?> DisplayByCreature = new() { ["1"] = "100", ["2"] = "100" };
        internal readonly Dictionary<string, string> EdgeByDisplay = new() { ["100"] = "33" };
        internal readonly List<CreatureDisplayTargetRole> Prepared = [];
        internal readonly List<IReadOnlyList<CreatureDisplayProbe>> FencedTargets = [];
        internal bool DisplayReady, ModelReady, UnsupportedDisplay, Disposed;
        internal string TargetScope = "complete", DisplayMatch = "1", FileEdge = "777";
        internal int Probes, Fences;
        internal Func<CreatureDisplayTargetRole, CancellationToken, Task>? Prepare;
        internal Func<CreatureDisplayMetadataRequest, CancellationToken, Task>? BeforeAuthorize;
        internal Func<CreatureDisplayTargetRole, string, CancellationToken, Task>? BeforeProbe;
        internal Func<IReadOnlyList<CreatureDisplayProbe>, CancellationToken, Task>? Finish;
        /// <summary>The columns and row values a probe reads for its role, as the real source keeps them for facts.</summary>
        internal Func<CreatureDisplayTargetRole, (TableDataColumn[] Columns, string[] Values)>? Rows;
        public async Task<CreaturesDisplayAuthorization> AuthorizeAsync(CreatureDisplayMetadataRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (BeforeAuthorize is not null) await BeforeAuthorize(request, token); return Core(request, DisplayByCreature.GetValueOrDefault(request.CreatureId)); }
        public async Task<CreatureDisplayProbe?> ProbeAsync(CreaturesDisplayAuthorization core, CreatureDisplayTargetRole role, string id, CancellationToken token)
        {
            Probes++; if (BeforeProbe is not null) await BeforeProbe(role, id, token);
            if (!(role == CreatureDisplayTargetRole.Display ? DisplayReady : ModelReady)) return null;
            var table = CreatureDisplayPolicy.Table(role); var artifact = new string(role == CreatureDisplayTargetRole.Display ? 'd' : 'e', 64);
            var match = role == CreatureDisplayTargetRole.Display ? DisplayMatch : "1";
            var edge = role == CreatureDisplayTargetRole.Display ? EdgeByDisplay.GetValueOrDefault(id, "0") : FileEdge;
            var snapshot = Source(table, artifact, TargetScope);
            var row = Rows?.Invoke(role);
            if (row is { } kept) snapshot = snapshot with { Columns = kept.Columns };
            var binding = new CreatureDisplayCatalogBinding(table, new(null!, null!, null!, "original"));
            if (UnsupportedDisplay && role == CreatureDisplayTargetRole.Display)
                return new(new(role, snapshot, null), binding, null, CreatureDisplayMetadataState.Unsupported, new("UNSUPPORTED", "Unsupported exact edge.", false, role));
            return new(new(role, snapshot, match, match == "1" ? new(new(table, artifact, id, 1), false, null, new(table, 1, edge)) : null), binding, match == "1" ? edge : null,
                Values: match == "1" ? row?.Values : null);
        }
        public async Task EnsureAsync(CreaturesContext context, CreatureDisplayTargetRole role, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token)
        {
            Prepared.Add(role); await progress(new("Reading", "stage", TableId: CreatureDisplayPolicy.Table(role)));
            if (Prepare is not null) await Prepare(role, token);
            else if (role == CreatureDisplayTargetRole.Display) DisplayReady = true; else ModelReady = true;
        }
        public async Task FinishAsync(CreatureDisplayMetadataRequest request, CreaturesDisplayAuthorization core, IReadOnlyList<CreatureDisplayProbe> delivered, CancellationToken token)
        { Fences++; FencedTargets.Add(delivered.ToArray()); if (Finish is not null) await Finish(delivered, token); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static TableDataSnapshot Source(string table, string artifact, string scope = "complete") => new("wow", Context.BuildIdentity, "us", table, "enUS", artifact,
        "original", "12345678", new('f', 64), "provider", "decoder", null!, null!, [new("ID", "integer", true, null), new("Edge", "integer", false, null)],
        new(scope, "1", "1", "0", scope == "complete" ? "0" : "1", scope == "complete" ? "0" : "1", "0", scope == "complete" ? "0" : "1", "0"), "1");
    private static CreaturesDisplayAuthorization Core(CreatureDisplayMetadataRequest request, string? display)
    {
        var fields = new CreatureFieldMap(new("c9d6b6b3", 0), new("c9d6b6b3", 1), new("c9d6b6b3", 2), new("c9d6b6b3", 3), [],
            Enumerable.Range(0, 4).Select(i => new CreatureFieldReference("c9d6b6b3", i)).ToArray(),
            Enumerable.Range(4, 4).Select(i => new CreatureFieldReference("c9d6b6b3", i)).ToArray());
        var source = Source("c9d6b6b3", new('c', 64)) with { Columns = Enumerable.Range(0, 8).Select(i => new TableDataColumn("Field" + i, i < 4 ? "integer" : "float", false, null)).ToArray() };
        var snapshot = new CreaturesSnapshot(request.Context, request.ArtifactId, "original", "creatures-core-v1", "query", "profile", source, new("1", "1", "0", "0", "4", "0", "0"), fields);
        var artifact = new CreaturesArtifact(request.ArtifactId, new('f', 64), 64, null!, snapshot.Counts);
        var opening = new CapturedOpeningContext(null!, new("1", "us", new('a', 64), 0, new('b', 64), 0), null!, null!);
        CreatureSummary? summary = display is null ? null : new(request.CreatureId, new(CreatureNameState.Valid, "Authored"), [],
            Enumerable.Range(0, 4).Select(i => new CreatureDisplaySlot(i, display, "0.25", display[0] == '-' ? CreatureDisplayState.InvalidDisplayReference : display == "0" ? CreatureDisplayState.NoDeclaredDisplay : CreatureDisplayState.Declared,
                fields.DisplayIds[i], fields.Probabilities[i])).ToArray());
        return new(opening, artifact, snapshot, summary);
    }
}
