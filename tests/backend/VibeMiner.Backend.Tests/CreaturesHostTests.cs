using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CreaturesHostTests
{
    private static readonly CreaturesContext Context = new("wow", new('d', 64), "us", "enUS");
    private static readonly string Artifact = new('b', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    private static Dictionary<string, object?> Scope(bool artifact = false) => artifact
        ? new() { ["productId"] = "wow", ["buildIdentity"] = Context.BuildIdentity, ["sourceRegion"] = "us", ["researchLocale"] = "enUS", ["artifactId"] = Artifact }
        : new() { ["productId"] = "wow", ["buildIdentity"] = Context.BuildIdentity, ["sourceRegion"] = "us", ["researchLocale"] = "enUS" };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Creature_protocol_fixture_uses_its_actual_bundled_definition_identity()
    {
        var source = Snapshot().CreatureSource;
        var provider = await TablesCatalog.Provider.Value;
        Assert.Equal(provider.Definitions[source.TableId].Sha256, source.DefinitionSha256);
        Assert.Equal(provider.Info.Revision, source.ProviderRevision);
        Assert.Equal(Assert.Single(provider.Entries, entry => entry.TableId == source.TableId).Db2FileDataId,
            source.Association.FileDataId);
    }

    [Fact]
    public async Task Exact_core_evidence_preserves_names_four_slots_partial_scope_and_copy_inspect()
    {
        var store = new Store(); await using var service = new CreaturesService(store);
        await using var session = await Session.Start(service);
        await session.Send("hello", "hello", new { });
        var capabilities = (await session.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Contains("creatures-v1", capabilities); Assert.DoesNotContain("creature-models-v1", capabilities);
        await session.Send("data", "creatures.data", Scope());
        var ready = (await session.Receive()).GetProperty("result");
        Assert.Equal("ready", ready.GetProperty("dependencyState").GetString());
        Assert.Equal(Context.BuildIdentity, ready.GetProperty("snapshot").GetProperty("context").GetProperty("buildIdentity").GetString());
        Assert.Equal(Context.BuildIdentity, ready.GetProperty("snapshot").GetProperty("creatureSource").GetProperty("buildIdentity").GetString());
        Assert.Equal("creatures-core-v1", ready.GetProperty("snapshot").GetProperty("featureRevision").GetString());
        Assert.Equal("c9d6b6b3", ready.GetProperty("snapshot").GetProperty("creatureSource").GetProperty("tableId").GetString());
        var pageParams = Scope(true); pageParams["query"] = "%_\\ Épée";
        await session.Send("page", "creatures.page", pageParams); var page = (await session.Receive()).GetProperty("result");
        Assert.Equal("%_\\ Épée", store.LastPage!.Query);
        var entries = page.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(new[] { "0", "7", "4294967295" }, entries.Select(v => v.GetProperty("id").GetString()));
        Assert.Equal(new[] { "valid", "empty", "invalid" }, entries.Select(v => v.GetProperty("name").GetProperty("state").GetString()));
        var invalid = entries[2].GetProperty("name"); Assert.Equal(JsonValueKind.Null, invalid.GetProperty("value").ValueKind);
        Assert.Equal("4294967295", invalid.GetProperty("issue").GetProperty("source").GetProperty("rowId").GetString());
        Assert.False(invalid.GetProperty("issue").TryGetProperty("rawBytesHex", out _));
        var slots = entries[0].GetProperty("displaySlots").EnumerateArray().ToArray();
        Assert.Equal(new[] { "23", "0", "-1", "23" }, slots.Select(v => v.GetProperty("displayId").GetString()));
        Assert.Equal(new[] { "declared", "noDeclaredDisplay", "invalidDisplayReference", "declared" }, slots.Select(v => v.GetProperty("state").GetString()));
        Assert.Equal("-0.5", slots[2].GetProperty("probability").GetString()); Assert.Equal(4, entries[0].GetProperty("facts").GetArrayLength());
        var inspect = Scope(true); inspect["creatureId"] = "7";
        await session.Send("inspect", "creatures.inspect", inspect); var detail = (await session.Receive()).GetProperty("result");
        Assert.Equal("found", detail.GetProperty("state").GetString()); Assert.Equal("unencryptedSections", detail.GetProperty("coverage").GetProperty("scope").GetString());
        Assert.True(detail.GetProperty("detail").GetProperty("creatureRow").GetProperty("isCopy").GetBoolean());
        Assert.Equal("3", detail.GetProperty("detail").GetProperty("creatureRow").GetProperty("copySourceId").GetString());
        inspect["creatureId"] = "8"; await session.Send("missing", "creatures.inspect", inspect);
        var missing = (await session.Receive()).GetProperty("result"); Assert.Equal("notFoundInAdmittedScope", missing.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("detail").ValueKind); Assert.Equal(0, store.Prepares);
        inspect["creatureId"] = "4294967295"; await session.Send("invalid-text", "creatures.inspect", inspect);
        var invalidDetail = (await session.Receive()).GetProperty("result").GetProperty("detail");
        Assert.Equal("c328", invalidDetail.GetProperty("creatureRow").GetProperty("textIssues")[0].GetProperty("rawBytesHex").GetString());
        Assert.Equal(16, invalidDetail.GetProperty("creatureRow").GetProperty("values").GetArrayLength());
    }

    [Theory]
    [InlineData("creatures.data", "researchLocale", "\"us\"")]
    [InlineData("creatures.prepare", "url", "\"https://example.org/creature\"")]
    [InlineData("creatures.page", "limit", "51")]
    [InlineData("creatures.page", "limit", "\"2\"")]
    [InlineData("creatures.page", "query", "null")]
    [InlineData("creatures.page", "query", "\"\\u0000\"")]
    [InlineData("creatures.page", "after", "\"unsafe/path\"")]
    [InlineData("creatures.page", "artifactId", "\"../feature\"")]
    [InlineData("creatures.inspect", "creatureId", "7")]
    [InlineData("creatures.inspect", "creatureId", "\"01\"")]
    [InlineData("creatures.inspect", "creatureId", "\"4294967296\"")]
    public async Task Invalid_shapes_are_refused_before_application_storage(string method, string field, string raw)
    {
        var store = new Store(); await using var service = new CreaturesService(store); await using var session = await Session.Start(service);
        var parameters = JsonSerializer.SerializeToNode(Scope(method is "creatures.page" or "creatures.inspect"), Json)!.AsObject();
        if (method == "creatures.inspect") parameters["creatureId"] = "7";
        parameters[field] = JsonNode.Parse(raw); await session.Send("invalid", method, parameters);
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, store.Reads + store.Pages + store.Inspects + store.Prepares);
    }

    [Fact]
    public async Task Missing_services_have_no_capability_or_simulated_data()
    {
        await using var session = await Session.Start(null); await session.Send("hello", "hello", new { });
        Assert.DoesNotContain((await session.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray(), v => v.GetString() == "creatures-v1");
        await session.Send("data", "creatures.data", Scope()); Assert.Equal("CREATURES_UNAVAILABLE", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("metadata", "creatures.display.prepare", Scope()); Assert.Equal("CREATURE_DISPLAY_UNAVAILABLE", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Malformed_unicode_and_mixed_search_modes_are_refused_before_storage()
    {
        var store = new Store(); await using var service = new CreaturesService(store); await using var session = await Session.Start(service);
        var scope = JsonSerializer.Serialize(Scope(true), Json).TrimEnd('}');
        await session.SendRaw("{\"protocolVersion\":1,\"id\":\"surrogate\",\"method\":\"creatures.page\",\"params\":" + scope + ",\"query\":\"\\uD800\"}}");
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        var mixed = Scope(true); mixed["query"] = "7"; mixed["exactCreatureId"] = "7";
        await session.Send("mixed", "creatures.page", mixed); Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, store.Pages);
    }

    [Fact]
    public async Task Typed_stale_errors_keep_retryability_and_oversized_failures_are_bounded()
    {
        var store = new Store { Page = _ => throw new DiscoveryException("CREATURES_ARTIFACT_STALE", "Authored source replacement.", true) };
        await using var service = new CreaturesService(store); await using var session = await Session.Start(service);
        await session.Send("stale", "creatures.page", Scope(true)); var stale = await session.Receive();
        Assert.Equal("CREATURES_ARTIFACT_STALE", stale.GetProperty("error").GetProperty("code").GetString()); Assert.True(stale.GetProperty("error").GetProperty("retryable").GetBoolean());
        store.Page = _ => throw new DiscoveryException("AUTHORED_OVERSIZED_ERROR", new string('<', 100_000), true);
        await session.Send("limit", "creatures.page", Scope(true)); var limited = await session.Receive();
        Assert.Equal("CREATURES_RESULT_LIMIT", limited.GetProperty("error").GetProperty("code").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(limited.GetRawText()) + 1 <= ProtocolHost.MaxCreaturesFrameBytes);
    }

    [Fact]
    public async Task Rejected_large_page_stops_encoding_before_all_source_names_are_expanded()
    {
        var visited = 0; var texts = Enumerable.Repeat(new string('<', 65_536), 50).ToArray();
        var store = new Store { Page = request => Page(request, texts) with { Entries = new CountedSummaries(Page(request, texts).Entries, () => visited++) } };
        await using var service = new CreaturesService(store); await using var session = await Session.Start(service);
        await session.Send("large", "creatures.page", Scope(true)); var refused = await session.Receive();
        Assert.Equal("CREATURES_RESULT_LIMIT", refused.GetProperty("error").GetProperty("code").GetString()); Assert.False(refused.TryGetProperty("result", out _));
        Assert.InRange(visited, 1, 49);
    }

    [Fact]
    public async Task Two_shared_prepares_preserve_core_queries_and_refuse_excess_without_queuing_32_requests()
    {
        var entered = Signal(); var stopping = Signal(); var cleanup = Signal(); CancellationToken producer = default;
        var store = new Store { Prepare = async (context, report, token) =>
        {
            producer = token; await report(new("Reading Creature source", "rows", 1, 3, "c9d6b6b3")); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { stopping.TrySetResult(); await cleanup.Task; }
            return Snapshot();
        } };
        await using var service = new CreaturesService(store); await using var session = await Session.Start(service);
        try
        {
            await session.Send("one", "creatures.prepare", Scope()); var progress = await session.Receive();
            Assert.Equal("one", progress.GetProperty("requestId").GetString()); Assert.Equal(1, progress.GetProperty("sequence").GetInt32());
            Assert.Equal("rows", progress.GetProperty("unit").GetString()); Assert.Equal(3, progress.GetProperty("total").GetInt32());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await session.Send("two", "creatures.prepare", Scope());
            await session.Send("barrier", "hello", new { }); Assert.Equal("barrier", (await session.Receive()).GetProperty("id").GetString());
            for (var i = 0; i < 35; i++)
            {
                await session.Send($"excess-{i}", "creatures.prepare", Scope()); var refused = await session.Receive();
                Assert.Equal("CREATURES_BUSY", refused.GetProperty("error").GetProperty("code").GetString()); Assert.True(refused.GetProperty("error").GetProperty("retryable").GetBoolean());
            }
            await session.Send("read", "creatures.data", Scope()); Assert.True((await session.Receive()).TryGetProperty("result", out _));
            await session.Send("page", "creatures.page", Scope(true)); Assert.True((await session.Receive()).TryGetProperty("result", out _));
            var inspect = Scope(true); inspect["creatureId"] = "7"; await session.Send("inspect", "creatures.inspect", inspect); Assert.True((await session.Receive()).TryGetProperty("result", out _));
            await session.Send("cancel-one", "request.cancel", new { requestId = "one" }); var first = new[] { await session.Receive(), await session.Receive() };
            Assert.Equal("CANCELED", Reply(first, "one").GetProperty("error").GetProperty("code").GetString()); Assert.False(producer.IsCancellationRequested);
            await session.Send("cancel-two", "request.cancel", new { requestId = "two" }); Assert.Equal("cancel-two", (await session.Receive()).GetProperty("id").GetString());
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("while-stopping", "creatures.data", Scope()); Assert.Equal("while-stopping", (await session.Receive()).GetProperty("id").GetString());
            cleanup.TrySetResult(); Assert.Equal("CANCELED", (await session.Receive()).GetProperty("error").GetProperty("code").GetString()); Assert.Equal(1, store.Prepares);
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task Protocol_shutdown_waits_for_shared_prepare_cleanup_and_both_terminal_writes()
    {
        var entered = Signal(); var stopping = Signal(); var cleanup = Signal();
        var store = new Store { Prepare = async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopping.TrySetResult(); await cleanup.Task; }
            return Snapshot();
        } };
        await using var service = new CreaturesService(store); await using var session = await Session.Start(service);
        try
        {
            await session.Send("one", "creatures.prepare", Scope()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("two", "creatures.prepare", Scope()); await session.Send("barrier", "hello", new { }); await session.Receive();
            await session.Send("shutdown", "shutdown", new { });
            Assert.True((await session.Receive()).GetProperty("result").GetProperty("stopping").GetBoolean());
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(session.Completion.IsCompleted);
            cleanup.TrySetResult(); var terminals = new[] { await session.Receive(), await session.Receive() };
            Assert.Equal(new[] { "one", "two" }, terminals.Select(frame => frame.GetProperty("id").GetString()).Order());
            Assert.All(terminals, frame => Assert.Equal("CANCELED", frame.GetProperty("error").GetProperty("code").GetString()));
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, store.Prepares);
        }
        finally { cleanup.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Four_result_credits_and_two_prepare_credits_survive_blocked_terminal_flush_and_shutdown(bool shutdown)
    {
        var previous = SynchronizationContext.Current; var context = new QueuedContext(); var store = new Store();
        var methods = new[] { "creatures.prepare", "creatures.prepare", "creatures.data", "creatures.page", "creatures.prepare", "creatures.page" };
        var requests = methods.Select((method, i) => Request($"r{i}", method, Scope(method == "creatures.page"))).ToArray();
        using var input = new HeldInput(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(r => JsonSerializer.Serialize(r, Json))) + "\n"));
        using var output = new HeldFlush(); using var stopping = new CancellationTokenSource();
        SynchronizationContext.SetSynchronizationContext(context); var service = new CreaturesService(store);
        Task? running = null;
        try
        {
            running = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), creatures: service).RunAsync(input, output, stopping.Token);
            SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => output.Started.Task.IsCompleted && input.Waiting.Task.IsCompleted);
            SynchronizationContext.SetSynchronizationContext(context); context.Drain();
            Assert.Equal(2, store.Prepares); Assert.Equal(1, store.Reads); Assert.Equal(1, store.Pages); Assert.False(running.IsCompleted);
            if (shutdown) stopping.Cancel();
            output.Release.TrySetResult(); input.Release.TrySetResult(); SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => running.IsCompleted); await running;
        }
        finally
        {
            output.Release.TrySetResult(); input.Release.TrySetResult(); SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => running?.IsCompleted ?? true);
            SynchronizationContext.SetSynchronizationContext(context); var disposal = service.DisposeAsync().AsTask(); SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => disposal.IsCompleted); await disposal;
        }
        var frames = Frames(output.ToArray()); Assert.Equal(6, frames.Length);
        foreach (var id in new[] { "r0", "r1", "r2", "r3" }) Assert.True(Reply(frames, id).TryGetProperty("result", out _));
        Assert.Equal("CREATURES_BUSY", Reply(frames, "r4").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("CREATURES_QUERY_BUSY", Reply(frames, "r5").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Entire_escaped_page_envelope_accepts_exact_512KiB_and_refuses_one_more_byte()
    {
        var texts = Enumerable.Repeat("a", 50).ToArray(); var store = new Store { Page = request => Page(request, texts) };
        await using var service = new CreaturesService(store); await using var session = await Session.Start(service);
        // Distinct IDs with equal escaped lengths avoid assuming stdout arrival also
        // means the prior ActiveRequest has already retired on the engine thread.
        await session.Send(new string('<', 128), "creatures.page", Scope(true)); var sample = await session.Receive(); var blank = JsonNode.Parse(sample.GetRawText())!;
        foreach (var entry in blank["result"]!["entries"]!.AsArray()) entry!["name"]!["value"] = "";
        var remaining = ProtocolHost.MaxCreaturesFrameBytes - JsonSerializer.SerializeToUtf8Bytes(blank, Json).Length - 1 - 50;
        for (var i = 0; i < texts.Length && remaining > 0; i++)
        { var escaped = Math.Min(4095, remaining / 6); texts[i] += new string('<', escaped); remaining -= escaped * 6; var plain = Math.Min(4095 - escaped, remaining); texts[i] += new string('a', plain); remaining -= plain; }
        Assert.Equal(0, remaining); await session.Send(new string('>', 128), "creatures.page", Scope(true)); var exact = await session.Receive();
        Assert.Equal(ProtocolHost.MaxCreaturesFrameBytes, Encoding.UTF8.GetByteCount(exact.GetRawText()) + 1); Assert.Equal(50, exact.GetProperty("result").GetProperty("entries").GetArrayLength());
        texts[Array.FindIndex(texts, value => value.Length < 4096)] += "a";
        await session.Send(new string('&', 128), "creatures.page", Scope(true)); var refused = await session.Receive(); Assert.Equal("CREATURES_RESULT_LIMIT", refused.GetProperty("error").GetProperty("code").GetString()); Assert.False(refused.TryGetProperty("result", out _));
    }

    private static JsonElement Reply(IEnumerable<JsonElement> frames, string id) => Assert.Single(frames, frame => frame.TryGetProperty("id", out var value) && value.GetString() == id);
    private static JsonElement[] Frames(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToArray();
    private static CreaturesPage Page(CreaturesPageRequest request, string[]? texts = null) => new(request.Context, request.ArtifactId, request.Query, request.ExactCreatureId,
        texts is null ? [Summary("0"), Summary("7"), Summary("4294967295")] : texts.Select((text, i) => Summary(i.ToString()) with { Name = new(CreatureNameState.Valid, text) }).ToArray(), null);
    private static CreatureFieldReference Field(int column) => new("c9d6b6b3", column);
    private static CreatureSummary Summary(string id) => new(id,
        id == "7" ? new(CreatureNameState.Empty, "") : id == "4294967295" ? new(CreatureNameState.Invalid, null, new("DB2_TEXT_ENCODING_INVALID", new("c9d6b6b3", new('c', 64), id, 0))) : new(CreatureNameState.Valid, " Authored name "),
        new[] { "2", "7", "9", "-1" }.Select((value, i) => new CreatureSourceValue("c9d6b6b3", 4 + i, value)).ToArray(),
        new[] { "23", "0", "-1", "23" }.Select((value, i) => new CreatureDisplaySlot(i, value, new[] { "0.25", "0", "-0.5", "1" }[i],
            i == 1 ? CreatureDisplayState.NoDeclaredDisplay : i == 2 ? CreatureDisplayState.InvalidDisplayReference : CreatureDisplayState.Declared, Field(8 + i), Field(12 + i))).ToArray());
    private static CreaturesSnapshot Snapshot()
    {
        return JsonSerializer.Deserialize<CreaturesSnapshot>(File.ReadAllText(RepositoryPaths.File("contracts/examples/creatures-snapshot.json")), Json)!;
    }
    private sealed class Store : ICreaturesStore
    {
        internal int Reads, Prepares, Pages, Inspects; internal CreaturesPageRequest? LastPage;
        internal Func<CreaturesContext, Func<CreaturesPreparationProgress, Task>, CancellationToken, Task<CreaturesSnapshot>>? Prepare;
        internal Func<CreaturesPageRequest, CreaturesPage>? Page;
        public Task<CreaturesReadiness> ReadAsync(CreaturesContext context, CancellationToken token) { Reads++; return Task.FromResult(new CreaturesReadiness(Snapshot(), CreatureDependencyState.Ready, new('c',64))); }
        public Task<CreaturesSnapshot> PrepareAsync(CreaturesContext context, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token) { Prepares++; return Prepare is null ? Task.FromResult(Snapshot()) : Prepare(context, progress, token); }
        public Task<CreaturesPage> ReadPageAsync(CreaturesPageRequest request, CancellationToken token) { Pages++; LastPage = request; return Task.FromResult(Page?.Invoke(request) ?? CreaturesHostTests.Page(request)); }
        public Task<CreatureInspectResult> InspectAsync(CreatureInspectRequest request, CancellationToken token)
        {
            Inspects++; var found = request.CreatureId != "8";
            var summary = Summary(request.CreatureId);
            var values = new[] { summary.Name.Value ?? "", "Authored alt", "Authored title", "" }.Concat(summary.Facts.Select(v => v.Value))
                .Concat(summary.DisplaySlots.Select(v => v.DisplayId)).Concat(summary.DisplaySlots.Select(v => v.Probability)).ToArray();
            var row = new TableDataRow(request.CreatureId, request.CreatureId == "7", request.CreatureId == "7" ? "3" : null, values,
                request.CreatureId == "4294967295" ? [new(0, "DB2_TEXT_ENCODING_INVALID", "c328")] : null);
            return Task.FromResult(new CreatureInspectResult(request.Context, request.ArtifactId, request.CreatureId, found ? CreatureInspectState.Found : CreatureInspectState.NotFoundInAdmittedScope, Snapshot().CreatureSource.Coverage,
                found ? new(Summary(request.CreatureId), row, new("c9d6b6b3", new('c',64), request.CreatureId)) : null));
        }
    }
    private sealed class CountedSummaries(IReadOnlyList<CreatureSummary> values, Action visit) : IReadOnlyList<CreatureSummary>
    {
        public int Count => values.Count;
        public CreatureSummary this[int index] => values[index];
        public IEnumerator<CreatureSummary> GetEnumerator() { foreach (var value in values) { visit(); yield return value; } }
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
        private Session(TcpClient client, TcpClient server, CreaturesService? creatures)
        {
            this.client = client; this.server = server; writer = new(client.GetStream(), new UTF8Encoding(false), leaveOpen: true); reader = new(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            completion = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), creatures: creatures).RunAsync(server.GetStream(), server.GetStream());
        }
        internal static async Task<Session> Start(CreaturesService? creatures)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var server = await listener.AcceptTcpClientAsync(); return new(client, server, creatures);
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
