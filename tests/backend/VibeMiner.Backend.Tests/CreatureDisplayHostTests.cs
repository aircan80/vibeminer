using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureDisplayHostTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static readonly CreaturesContext Context = new("wow", new('d', 64), "us", "enUS");
    private static readonly string Artifact = new('b', 64);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    private static Dictionary<string, object?> Scope(bool metadata = true) => metadata
        ? new() { ["productId"] = "wow", ["buildIdentity"] = Context.BuildIdentity, ["sourceRegion"] = "us", ["researchLocale"] = "enUS", ["artifactId"] = Artifact, ["creatureId"] = "7", ["slot"] = 0 }
        : new() { ["productId"] = "wow", ["buildIdentity"] = Context.BuildIdentity, ["sourceRegion"] = "us", ["researchLocale"] = "enUS" };
    private static Dictionary<string, object?> PageScope() { var value = Scope(false); value["artifactId"] = Artifact; return value; }
    private static string FixturePath(string name) => RepositoryPaths.File(Path.Combine("contracts/examples", name));
    private static T Fixture<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(FixturePath(name)), Json)!;
    private static CreatureDisplayMetadataResult Result() => Fixture<CreatureDisplayMetadataResult>("creature-display-metadata.json");
    private static CreaturesSnapshot CoreSnapshot() => Fixture<CreaturesSnapshot>("creatures-snapshot.json");

    [Fact]
    public async Task Positive_metadata_preserves_exact_role_copy_field_and_partial_proofs_without_appearance()
    {
        var coreStore = new CoreStore(); var target = new DisplayStore(); await using var core = new CreaturesService(coreStore); await using var metadata = new CreatureDisplayMetadataService(target);
        await using var session = await Session.Start(core, metadata);
        await session.Send("hello", "hello", new { }); var hello = (await session.Receive()).GetProperty("result");
        Assert.Contains(hello.GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "creature-display-metadata-v1");
        await session.Send("local", "creatures.display.read", Scope()); var frame = await session.Receive(); Assert.False(frame.TryGetProperty("type", out _));
        var result = frame.GetProperty("result"); Assert.Equal("metadataResolved", result.GetProperty("state").GetString()); Assert.Equal("notEvaluated", result.GetProperty("appearance").GetString());
        Assert.Equal("creature-display-metadata-v1", result.GetProperty("metadataPolicy").GetString());
        Assert.Equal("c9d6b6b3", result.GetProperty("creatureSource").GetProperty("tableId").GetString()); Assert.Equal("declared", result.GetProperty("selected").GetProperty("state").GetString());
        var sources = result.GetProperty("sources"); Assert.Equal(new[] { "display", "model" }, sources.EnumerateArray().Select(v => v.GetProperty("role").GetString()));
        Assert.Equal("bfdaf9f1", sources[0].GetProperty("row").GetProperty("source").GetProperty("tableId").GetString());
        Assert.Equal("7f695fa5", sources[1].GetProperty("row").GetProperty("edgeValue").GetProperty("tableId").GetString());
        Assert.True(sources[0].GetProperty("row").GetProperty("isCopy").GetBoolean()); Assert.Equal("11", sources[0].GetProperty("row").GetProperty("copySourceId").GetString());
        Assert.False(sources[0].GetProperty("row").TryGetProperty("values", out _)); Assert.False(result.TryGetProperty("path", out _));
        Assert.Equal("unencryptedSections", result.GetProperty("creatureCoverage").GetProperty("scope").GetString());
        Assert.Equal("unencryptedSections", sources[0].GetProperty("snapshot").GetProperty("coverage").GetProperty("scope").GetString());
        Assert.Equal("complete", sources[1].GetProperty("snapshot").GetProperty("coverage").GetProperty("scope").GetString());
        Assert.Equal("19", result.GetProperty("modelId").GetString()); Assert.Equal("123456", result.GetProperty("modelFileDataId").GetString());
        Assert.Equal(1, target.Reads); Assert.Equal(0, target.Prepares + coreStore.Prepares);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Capability_and_methods_require_both_core_and_metadata_services(bool haveCore, bool haveMetadata)
    {
        await using var core = new CreaturesService(new CoreStore()); await using var metadata = new CreatureDisplayMetadataService(new DisplayStore());
        await using var session = await Session.Start(haveCore ? core : null, haveMetadata ? metadata : null);
        await session.Send("hello", "hello", new { });
        Assert.DoesNotContain((await session.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "creature-display-metadata-v1");
        await session.Send("read", "creatures.display.read", Scope()); Assert.Equal("CREATURE_DISPLAY_UNAVAILABLE", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("slot", "-1")]
    [InlineData("slot", "4")]
    [InlineData("slot", "0.5")]
    [InlineData("slot", "\"0\"")]
    [InlineData("slot", "null")]
    [InlineData("creatureId", "7")]
    [InlineData("creatureId", "\"01\"")]
    [InlineData("creatureId", "\"4294967296\"")]
    [InlineData("artifactId", "\"../feature\"")]
    [InlineData("researchLocale", "\"us\"")]
    [InlineData("displayId", "\"23\"")]
    [InlineData("modelFileDataId", "\"123456\"")]
    [InlineData("url", "\"https://example.org/model\"")]
    public async Task Invalid_authority_fails_before_any_core_or_metadata_work(string field, string raw)
    {
        var target = new DisplayStore(); var coreStore = new CoreStore(); await using var core = new CreaturesService(coreStore); await using var metadata = new CreatureDisplayMetadataService(target);
        await using var session = await Session.Start(core, metadata);
        var parameters = JsonSerializer.SerializeToNode(Scope(), Json)!; parameters[field] = System.Text.Json.Nodes.JsonNode.Parse(raw);
        await session.Send("bad", "creatures.display.prepare", parameters);
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString()); Assert.Equal(0, target.Reads + target.Prepares + coreStore.Reads + coreStore.Prepares);
    }

    [Fact]
    public async Task Mixed_network_waits_share_two_long_credits_and_leave_core_queries_and_local_metadata_read_usable()
    {
        var coreEntered = Signal(); var coreStopping = Signal(); var coreCleanup = Signal(); var targetEntered = Signal(); var targetStopping = Signal(); var targetCleanup = Signal();
        CancellationToken coreToken = default;
        var coreStore = new CoreStore { Prepare = async (_, _, token) =>
        { coreToken = token; coreEntered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { coreStopping.TrySetResult(); await coreCleanup.Task; } return CoreSnapshot(); } };
        var target = new DisplayStore { Read = _ => NotPrepared(), Prepare = async (_, report, token) =>
        { await report(new("Waiting for display source", "tables", 0, 1, "bfdaf9f1")); targetEntered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { targetStopping.TrySetResult(); await targetCleanup.Task; } return Result(); } };
        await using var core = new CreaturesService(coreStore); await using var metadata = new CreatureDisplayMetadataService(target); await using var session = await Session.Start(core, metadata);
        try
        {
            await session.Send("core", "creatures.prepare", Scope(false)); await coreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("metadata", "creatures.display.prepare", Scope()); var progress = await session.Receive();
            Assert.Equal("metadata", progress.GetProperty("requestId").GetString()); Assert.Equal(1, progress.GetProperty("sequence").GetInt32()); Assert.Equal(1, progress.GetProperty("total").GetInt32());
            await targetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("third-core", "creatures.prepare", Scope(false)); Assert.Equal("CREATURES_BUSY", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
            await session.Send("third-metadata", "creatures.display.prepare", Scope()); Assert.Equal("CREATURES_BUSY", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
            await session.Send("page", "creatures.page", PageScope()); Assert.True((await session.Receive()).TryGetProperty("result", out _));
            var inspect = PageScope(); inspect["creatureId"] = "7"; await session.Send("inspect", "creatures.inspect", inspect); Assert.True((await session.Receive()).TryGetProperty("result", out _));
            await session.Send("read", "creatures.display.read", Scope()); var local = await session.Receive(); Assert.Equal("notPrepared", local.GetProperty("result").GetProperty("state").GetString()); Assert.Equal("bfdaf9f1", local.GetProperty("result").GetProperty("requiredTableId").GetString());
            Assert.Equal(1, target.Prepares); Assert.Equal(1, target.Reads); Assert.Equal(1, coreStore.Prepares);
            await session.Send("stop-metadata", "request.cancel", new { requestId = "metadata" }); Assert.Equal("stop-metadata", (await session.Receive()).GetProperty("id").GetString());
            await targetStopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(coreToken.IsCancellationRequested);
            await session.Send("during-cleanup", "creatures.page", PageScope()); Assert.Equal("during-cleanup", (await session.Receive()).GetProperty("id").GetString());
            targetCleanup.TrySetResult(); Assert.Equal("CANCELED", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
            await session.Send("stop-core", "request.cancel", new { requestId = "core" }); await session.Receive(); await coreStopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coreCleanup.TrySetResult(); Assert.Equal("CANCELED", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        }
        finally { coreCleanup.TrySetResult(); targetCleanup.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Core_and_metadata_union_holds_four_results_and_two_long_credits_through_actual_stdout_flush(bool shutdown)
    {
        var previous = SynchronizationContext.Current; var context = new QueuedContext(); var coreStore = new CoreStore(); var target = new DisplayStore();
        var visited = 0; var template = Result(); target.Read = _ => template with { Sources = new CountedTargets(template.Sources, () => visited++) };
        target.Prepare = (request, _, _) => Task.FromResult(target.Read(request));
        var methods = new[] { "creatures.prepare", "creatures.display.prepare", "creatures.page", "creatures.display.read", "creatures.display.prepare", "creatures.page", "creatures.prepare", "creatures.display.read" };
        var requests = methods.Select((method, i) => Request($"r{i}", method, method.StartsWith("creatures.display", StringComparison.Ordinal) ? Scope() : method == "creatures.page" ? PageScope() : Scope(false))).ToArray();
        using var input = new HeldInput(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(r => JsonSerializer.Serialize(r, Json))) + "\n")); using var output = new HeldFlush(); using var stop = new CancellationTokenSource();
        SynchronizationContext.SetSynchronizationContext(context); var core = new CreaturesService(coreStore); var metadata = new CreatureDisplayMetadataService(target); Task? running = null;
        try
        {
            running = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), creatures: core, creatureDisplays: metadata).RunAsync(input, output, stop.Token);
            SynchronizationContext.SetSynchronizationContext(previous); await context.DrainUntilAsync(() => input.Waiting.Task.IsCompleted && output.Started.Task.IsCompleted);
            SynchronizationContext.SetSynchronizationContext(context); context.Drain();
            Assert.Equal(1, coreStore.Prepares); Assert.Equal(1, coreStore.Pages); Assert.Equal(1, target.Prepares); Assert.Equal(1, target.Reads); Assert.Equal(0, visited); Assert.False(running.IsCompleted);
            if (shutdown) stop.Cancel(); output.Release.TrySetResult(); input.Release.TrySetResult(); SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => running.IsCompleted); await running;
        }
        finally
        {
            output.Release.TrySetResult(); input.Release.TrySetResult(); SynchronizationContext.SetSynchronizationContext(previous); await context.DrainUntilAsync(() => running?.IsCompleted ?? true);
            SynchronizationContext.SetSynchronizationContext(context); var md = metadata.DisposeAsync().AsTask(); var cd = core.DisposeAsync().AsTask(); SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => md.IsCompleted && cd.IsCompleted); await md; await cd;
        }
        var frames = Frames(output.ToArray()); Assert.Equal(8, frames.Length); Assert.Equal(4, visited);
        foreach (var id in new[] { "r0", "r1", "r2", "r3" }) Assert.True(Reply(frames, id).TryGetProperty("result", out _));
        foreach (var id in new[] { "r4", "r6" }) Assert.Equal("CREATURES_BUSY", Reply(frames, id).GetProperty("error").GetProperty("code").GetString());
        foreach (var id in new[] { "r5", "r7" }) Assert.Equal("CREATURES_QUERY_BUSY", Reply(frames, id).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Shutdown_awaits_both_independent_core_and_metadata_producer_cleanup_and_terminals()
    {
        var coreEntered = Signal(); var coreStopping = Signal(); var coreCleanup = Signal(); var targetEntered = Signal(); var targetStopping = Signal(); var targetCleanup = Signal();
        var coreStore = new CoreStore { Prepare = async (_, _, token) => { coreEntered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { coreStopping.TrySetResult(); await coreCleanup.Task; } return CoreSnapshot(); } };
        var target = new DisplayStore { Prepare = async (_, _, token) => { targetEntered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { targetStopping.TrySetResult(); await targetCleanup.Task; } return Result(); } };
        await using var core = new CreaturesService(coreStore); await using var metadata = new CreatureDisplayMetadataService(target); await using var session = await Session.Start(core, metadata);
        try
        {
            await session.Send("core", "creatures.prepare", Scope(false)); await coreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("metadata", "creatures.display.prepare", Scope()); await targetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("shutdown", "shutdown", new { }); Assert.True((await session.Receive()).GetProperty("result").GetProperty("stopping").GetBoolean());
            await Task.WhenAll(coreStopping.Task, targetStopping.Task).WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(session.Completion.IsCompleted);
            coreCleanup.TrySetResult(); targetCleanup.TrySetResult(); var frames = new[] { await session.Receive(), await session.Receive() };
            Assert.All(frames, frame => Assert.Equal("CANCELED", frame.GetProperty("error").GetProperty("code").GetString())); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { coreCleanup.TrySetResult(); targetCleanup.TrySetResult(); }
    }

    [Fact]
    public async Task Typed_metadata_failures_preserve_retryability_and_oversized_errors_remain_whole_bounded_frames()
    {
        var target = new DisplayStore { Read = _ => throw new DiscoveryException("CREATURE_DISPLAY_STALE", "Authored source replacement.", true) };
        await using var core = new CreaturesService(new CoreStore()); await using var metadata = new CreatureDisplayMetadataService(target); await using var session = await Session.Start(core, metadata);
        await session.Send("stale", "creatures.display.read", Scope()); var stale = await session.Receive(); Assert.Equal("CREATURE_DISPLAY_STALE", stale.GetProperty("error").GetProperty("code").GetString()); Assert.True(stale.GetProperty("error").GetProperty("retryable").GetBoolean());
        target.Read = _ => throw new DiscoveryException("AUTHORED_OVERSIZED", new string('<',100_000), true);
        await session.Send("oversize", "creatures.display.read", Scope()); var oversized = await session.Receive(); Assert.Equal("CREATURES_RESULT_LIMIT", oversized.GetProperty("error").GetProperty("code").GetString()); Assert.False(oversized.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Both_target_proofs_and_escaped_request_id_count_at_exact_frame_limit_without_truncation()
    {
        // Authored shape stress, not a claim that these synthetic columns match an admitted target schema.
        var template = Result(); var columns = template.Sources.Select(source => Enumerable.Range(0, 512)
            .Select(i => i == 0 ? source.Snapshot.Columns[0] : new TableDataColumn("a", "integer", false, null)).ToArray()).ToArray();
        var sources = template.Sources.Select((source, i) => source with { Snapshot = source.Snapshot with { Columns = columns[i] } }).ToArray();
        var target = new DisplayStore { Read = _ => template with { Sources = sources } };
        await using var core = new CreaturesService(new CoreStore()); await using var metadata = new CreatureDisplayMetadataService(target); await using var session = await Session.Start(core, metadata);
        // The distinct IDs have equal escaped byte lengths; stdout arrival does not assert request retirement.
        await session.Send(new string('<', 128), "creatures.display.read", Scope()); var sample = await session.Receive();
        var remaining = ProtocolHost.MaxCreaturesFrameBytes - Encoding.UTF8.GetByteCount(sample.GetRawText()) - 1;
        foreach (var group in columns)
            for (var i = 1; i < group.Length && remaining > 0; i++)
            {
                var escaped = Math.Min(143, remaining / 6); var plain = Math.Min(143 - escaped, remaining - escaped * 6);
                group[i] = group[i] with { Name = "a" + new string('<', escaped) + new string('a', plain) }; remaining -= escaped * 6 + plain;
            }
        Assert.Equal(0, remaining); await session.Send(new string('>', 128), "creatures.display.read", Scope()); var exact = await session.Receive();
        Assert.Equal(ProtocolHost.MaxCreaturesFrameBytes, Encoding.UTF8.GetByteCount(exact.GetRawText()) + 1);
        Assert.Equal(2, exact.GetProperty("result").GetProperty("sources").GetArrayLength());
        Assert.Equal("123456", exact.GetProperty("result").GetProperty("modelFileDataId").GetString());
        var spare = columns.SelectMany(group => group.Select((column, i) => (group, column, i))).First(v => v.i > 0 && v.column.Name.Length < 144);
        spare.group[spare.i] = spare.column with { Name = spare.column.Name + "a" };
        await session.Send(new string('&', 128), "creatures.display.read", Scope()); var refused = await session.Receive();
        Assert.Equal("CREATURES_RESULT_LIMIT", refused.GetProperty("error").GetProperty("code").GetString()); Assert.False(refused.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Oversized_target_snapshots_stop_lazy_column_enumeration_before_full_expansion()
    {
        var visited = 0; var template = Result(); var sources = template.Sources.Select(source => source with
        { Snapshot = source.Snapshot with { Columns = new CountedColumns(Enumerable.Range(0, 512).Select(i => i == 0 ? source.Snapshot.Columns[0] : new TableDataColumn(new string('<',144), "integer", false, null)).ToArray(), () => visited++) } }).ToArray();
        var target = new DisplayStore { Read = _ => template with { Sources = sources } };
        await using var core = new CreaturesService(new CoreStore()); await using var metadata = new CreatureDisplayMetadataService(target); await using var session = await Session.Start(core, metadata);
        await session.Send("large", "creatures.display.read", Scope()); var refused = await session.Receive();
        Assert.Equal("CREATURES_RESULT_LIMIT", refused.GetProperty("error").GetProperty("code").GetString()); Assert.InRange(visited, 1, 1023);
        Assert.False(refused.TryGetProperty("result", out _));
        target.Read = _ => Result(); await session.Send("after", "creatures.display.read", Scope()); Assert.Equal("metadataResolved", (await session.Receive()).GetProperty("result").GetProperty("state").GetString());
    }

    private static CreatureDisplayMetadataResult NotPrepared() => Result() with { State = CreatureDisplayMetadataState.NotPrepared, Sources = [], ModelId = null, ModelFileDataId = null, RequiredTableId = "bfdaf9f1" };
    private static JsonElement Reply(IEnumerable<JsonElement> frames, string id) => Assert.Single(frames, frame => frame.TryGetProperty("id", out var value) && value.GetString() == id);
    private static JsonElement[] Frames(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToArray();
    private sealed class DisplayStore : ICreatureDisplayMetadataStore
    {
        internal int Reads, Prepares;
        internal Func<CreatureDisplayMetadataRequest, CreatureDisplayMetadataResult> Read = _ => Result();
        internal Func<CreatureDisplayMetadataRequest, Func<CreaturesPreparationProgress, Task>, CancellationToken, Task<CreatureDisplayMetadataResult>>? Prepare;
        public Task<CreatureDisplayMetadataResult> ReadDisplayAsync(CreatureDisplayMetadataRequest request, CancellationToken token) { Reads++; return Task.FromResult(Read(request)); }
        public Task<CreatureDisplayMetadataResult> PrepareDisplayAsync(CreatureDisplayMetadataRequest request, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token) { Prepares++; return Prepare is null ? Task.FromResult(Result()) : Prepare(request, progress, token); }
    }
    private sealed class CoreStore : ICreaturesStore
    {
        internal int Reads, Prepares, Pages;
        internal Func<CreaturesContext, Func<CreaturesPreparationProgress, Task>, CancellationToken, Task<CreaturesSnapshot>>? Prepare;
        public Task<CreaturesReadiness> ReadAsync(CreaturesContext context, CancellationToken token) { Reads++; return Task.FromResult(new CreaturesReadiness(CoreSnapshot(), CreatureDependencyState.Ready, CoreSnapshot().CreatureSource.ArtifactId)); }
        public Task<CreaturesSnapshot> PrepareAsync(CreaturesContext context, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token) { Prepares++; return Prepare is null ? Task.FromResult(CoreSnapshot()) : Prepare(context, progress, token); }
        public Task<CreaturesPage> ReadPageAsync(CreaturesPageRequest request, CancellationToken token) { Pages++; return Task.FromResult(new CreaturesPage(request.Context, request.ArtifactId, request.Query, request.ExactCreatureId, [], null)); }
        public Task<CreatureInspectResult> InspectAsync(CreatureInspectRequest request, CancellationToken token) => Task.FromResult(new CreatureInspectResult(request.Context, request.ArtifactId, request.CreatureId, CreatureInspectState.NotFoundInAdmittedScope, CoreSnapshot().CreatureSource.Coverage, null));
    }
    private sealed class CountedTargets(IReadOnlyList<CreatureDisplayTarget> values, Action visit) : IReadOnlyList<CreatureDisplayTarget>
    {
        public int Count => values.Count;
        public CreatureDisplayTarget this[int index] => values[index];
        public IEnumerator<CreatureDisplayTarget> GetEnumerator() { foreach (var value in values) { visit(); yield return value; } }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class CountedColumns(IReadOnlyList<TableDataColumn> values, Action visit) : IReadOnlyList<TableDataColumn>
    {
        public int Count => values.Count;
        public TableDataColumn this[int index] { get { visit(); return values[index]; } }
        public IEnumerator<TableDataColumn> GetEnumerator() { foreach (var value in values) { visit(); yield return value; } }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class HeldInput(byte[] bytes) : MemoryStream(bytes)
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            Waiting.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return 0;
        }
    }
    private sealed class HeldFlush : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Flushes;
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { if (++Flushes == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); } }
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> pending = new();
        private readonly SemaphoreSlim posted = new(0, 1);
        public override void Post(SendOrPostCallback callback, object? state)
        { lock (pending) { pending.Enqueue((callback, state)); if (posted.CurrentCount == 0) posted.Release(); } }
        internal async Task DrainUntilAsync(Func<bool> completed)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!completed())
            {
                Drain();
                if (!completed()) await posted.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        internal void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (pending) { if (!pending.TryDequeue(out work)) return; }
                var previous = Current; SetSynchronizationContext(this);
                try { work.Callback(work.State); } finally { SetSynchronizationContext(previous); }
            }
        }
    }
    private sealed class Session : IAsyncDisposable
    {
        private readonly TcpClient client, server; private readonly StreamWriter writer; private readonly StreamReader reader; private readonly Task completion;
        internal Task Completion => completion;
        private Session(TcpClient client, TcpClient server, CreaturesService? creatures, CreatureDisplayMetadataService? metadata)
        {
            this.client = client; this.server = server; writer = new(client.GetStream(), new UTF8Encoding(false), leaveOpen: true); reader = new(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            completion = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), creatures: creatures, creatureDisplays: metadata).RunAsync(server.GetStream(), server.GetStream());
        }
        internal static async Task<Session> Start(CreaturesService? creatures, CreatureDisplayMetadataService? metadata)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var server = await listener.AcceptTcpClientAsync(); return new(client, server, creatures, metadata);
        }
        internal Task Send(string id, string method, object parameters) => SendRaw(JsonSerializer.Serialize(Request(id, method, parameters), Json));
        internal async Task SendRaw(string frame) { await writer.WriteLineAsync(frame); await writer.FlushAsync(); }
        internal async Task<JsonElement> Receive() { using var document = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!); return document.RootElement.Clone(); }
        public async ValueTask DisposeAsync() { client.Dispose(); try { await completion.WaitAsync(TimeSpan.FromSeconds(10)); } finally { server.Dispose(); reader.Dispose(); await writer.DisposeAsync(); } }
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
