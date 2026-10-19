using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;
using static VibeMiner.Backend.Tests.CreatureGeometryTestFixture;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureGeometryHostTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Dictionary<string, object?> Scope() => new()
    {
        ["productId"] = "wow", ["buildIdentity"] = Selection.Context.BuildIdentity, ["sourceRegion"] = "us", ["researchLocale"] = "enUS",
        ["artifactId"] = Selection.ArtifactId, ["creatureId"] = Selection.CreatureId, ["slot"] = 0
    };

    [Fact]
    public async Task Actual_service_read_prepare_open_close_preserve_control_identity_and_no_paths()
    {
        var store = new Store { Read = (r, _) => Task.FromResult(Result(r.Selection, true)), Prepare = (r, _, _) => Task.FromResult(Result(r.Selection, true)) };
        await using var app = new CreatureGeometryService(store); await using var registry = new CreatureGeometryLeases(app);
        await using var session = await Session.Start(app, registry);
        var read = Scope(); read["expectedGeometryArtifactId"] = null;
        await session.Send("read", "creatures.geometry.read", read); var frame = await session.Receive();
        Assert.Equal("ready", frame.GetProperty("result").GetProperty("state").GetString());
        Assert.Equal(320, frame.GetProperty("result").GetProperty("manifest").GetProperty("byteLength").GetInt32());
        Assert.DoesNotContain("sourceUrl", frame.GetRawText(), StringComparison.Ordinal);
        await session.Send("prepare", "creatures.geometry.prepare", Scope()); Assert.Equal("ready", (await session.Receive()).GetProperty("result").GetProperty("state").GetString());
        var open = Scope(); open["geometryArtifactId"] = new string('4', 64);
        await session.Send("open", "creatures.geometry.open", open); var descriptor = (await session.Receive()).GetProperty("result");
        Assert.Equal(new[] { "leaseId", "manifest" }, descriptor.EnumerateObject().Select(p => p.Name));
        var close = new { leaseId = descriptor.GetProperty("leaseId").GetString() };
        await session.Send("close", "creatures.geometry.close", close); Assert.True((await session.Receive()).GetProperty("result").GetProperty("closed").GetBoolean());
        await session.Send("close2", "creatures.geometry.close", close); Assert.True((await session.Receive()).GetProperty("result").GetProperty("closed").GetBoolean());
        Assert.Equal((1, 1, 1), (store.Reads, store.Prepares, store.Opens));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("prepare")]
    [InlineData("open")]
    public async Task Fractional_bounds_are_exact_float32_values_in_JSON_numbers(string operation)
    {
        var manifest = Manifest() with { Bounds = new(new float[] { -0.1f, 0.2f, 0.3f }, new float[] { 0.4f, 0.5f, 0.6f }) };
        var store = new Store
        {
            Read = (r, _) => Task.FromResult(Result(r.Selection, true) with { Manifest = manifest }),
            Prepare = (r, _, _) => Task.FromResult(Result(r.Selection, true) with { Manifest = manifest }),
            Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(new Lease { Manifest = manifest })
        };
        await using var app = new CreatureGeometryService(store); await using var registry = new CreatureGeometryLeases(app);
        await using var session = await Session.Start(app, registry);
        var parameters = Scope();
        if (operation == "open") parameters["geometryArtifactId"] = manifest.ArtifactId;
        await session.Send("fractional", "creatures.geometry." + operation, parameters);
        var wire = (await session.Receive()).GetProperty("result").GetProperty("manifest");
        Assert.Equal(manifest.ArtifactId, wire.GetProperty("artifactId").GetString());
        Assert.Equal(manifest.BindingSha256, wire.GetProperty("bindingSha256").GetString());
        Assert.Equal(manifest.Sha256, wire.GetProperty("sha256").GetString());
        foreach (var (name, expected) in new[] { ("min", manifest.Bounds.Min), ("max", manifest.Bounds.Max) })
        {
            var numbers = wire.GetProperty("bounds").GetProperty(name).EnumerateArray().ToArray();
            Assert.Equal(3, numbers.Length);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(JsonValueKind.Number, numbers[i].ValueKind);
                var number = numbers[i].GetDouble();
                // A JS number must already equal the exact widened Float32, not merely round back to it.
                Assert.Equal((double)expected[i], number);
                Assert.Equal(number, (double)(float)number);
                Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits((float)number));
            }
        }
    }

    [Theory]
    [InlineData("slot", "\"0\"")]
    [InlineData("slot", "4")]
    [InlineData("fileDataId", "\"800\"")]
    [InlineData("geometryArtifactId", "\"../file\"")]
    [InlineData("researchLocale", "\"us\"")]
    public async Task Invalid_authority_or_shape_never_reaches_store(string field, string raw)
    {
        var store = new Store(); await using var app = new CreatureGeometryService(store); await using var registry = new CreatureGeometryLeases(app);
        await using var session = await Session.Start(app, registry);
        var parameters = JsonSerializer.SerializeToNode(Scope(), Json)!; parameters["geometryArtifactId"] = new string('4', 64);
        parameters[field] = System.Text.Json.Nodes.JsonNode.Parse(raw);
        await session.Send("bad", "creatures.geometry.open", parameters);
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString()); Assert.Equal(0, store.Opens);
    }

    [Fact]
    public async Task Missing_service_refuses_all_routes_and_advertises_no_future_capability()
    {
        await using var session = await Session.Start(null, null);
        await session.Send("hello", "hello", new { });
        Assert.DoesNotContain((await session.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray(), v => v.GetString() == "creature-source-geometry-v1");
        foreach (var operation in new[] { "read", "prepare", "open", "close" })
        {
            var parameters = operation == "close" ? new Dictionary<string, object?> { ["leaseId"] = new string('7', 32) } : Scope();
            if (operation == "open") parameters["geometryArtifactId"] = new string('4', 64);
            await session.Send(operation, "creatures.geometry." + operation, parameters);
            Assert.Equal("CREATURE_GEOMETRY_UNAVAILABLE", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Escaped_complete_error_accepts_exactly_64KiB_then_whole_bounded_fallback()
    {
        var text = "a"; var store = new Store { Read = (_, _) => throw new DiscoveryException("AUTHORED_ERROR", text) };
        await using var app = new CreatureGeometryService(store); await using var registry = new CreatureGeometryLeases(app);
        await using var session = await Session.Start(app, registry);
        await session.Send(new string('<', 128), "creatures.geometry.read", Scope()); var sample = await session.Receive();
        var remaining = ProtocolHost.MaxCreatureGeometryFrameBytes - Encoding.UTF8.GetByteCount(sample.GetRawText()) - 1;
        text += new string('<', remaining / 6) + new string('a', remaining % 6);
        await session.Send(new string('>', 128), "creatures.geometry.read", Scope()); var exact = await session.Receive();
        Assert.Equal(65536, Encoding.UTF8.GetByteCount(exact.GetRawText()) + 1); Assert.Equal(text, exact.GetProperty("error").GetProperty("message").GetString());
        text += "a"; await session.Send(new string('&', 128), "creatures.geometry.read", Scope());
        Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Wire_rejects_duplicate_members_and_does_not_expand_original_source_evidence()
    {
        var raw = JsonSerializer.Serialize(Scope(), Json); raw = raw[..^1] + ",\"slot\":0}";
        using var document = JsonDocument.Parse(raw);
        Assert.Equal("INVALID_REQUEST", Assert.Throws<DiscoveryException>(() => CreatureGeometryWire.Request("creatures.geometry.read", document.RootElement)).Code);
        using var mapped = JsonDocument.Parse(JsonSerializer.Serialize(CreatureGeometryWire.Result(Result(ready: true)), Json));
        var source = mapped.RootElement.GetProperty("sources")[0];
        Assert.Equal("model", source.GetProperty("role").GetString()); Assert.Equal(100, source.GetProperty("encodedBytes").GetInt64());
        Assert.False(source.TryGetProperty("sourceUrl", out _)); Assert.Equal("metadataResolved", mapped.RootElement.GetProperty("metadata").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Two_long_and_four_publication_credits_survive_flush_and_close_bypasses_them()
    {
        var lease = new Lease(); var store = new Store { Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(lease),
            Read = (r, _) => Task.FromResult(Result(r.Selection, true)), Prepare = (r, _, _) => Task.FromResult(Result(r.Selection, true)) };
        await using var app = new CreatureGeometryService(store); await using var registry = new CreatureGeometryLeases(app);
        var descriptor = await registry.OpenAsync(new(Selection, new('4', 64)), default);
        var methods = new[] { "prepare", "prepare", "read", "read", "prepare", "read", "close" };
        var requests = methods.Select((m, i) => new { protocolVersion = 1, id = $"r{i}", method = "creatures.geometry." + m,
            @params = m == "close" ? (object)new { leaseId = descriptor.LeaseId } : Scope() });
        using var input = new HeldInput(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(r => JsonSerializer.Serialize(r, Json))) + "\n"));
        using var output = new HeldFlush();
        var running = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), creatureGeometry: app, geometryLeases: registry).RunAsync(input, output);
        try
        {
            await Task.WhenAll(input.Waiting.Task, output.Started.Task, lease.Closing.Task).WaitAsync(Guard);
            await lease.Retirement.WaitAsync(Guard); // General close actually retires while Creature publication is full and stdout held.
            Assert.Equal((2, 2), (store.Reads, store.Prepares)); Assert.False(running.IsCompleted);
            output.Release.TrySetResult(); input.Release.TrySetResult(); await running.WaitAsync(Guard);
        }
        finally { output.Release.TrySetResult(); input.Release.TrySetResult(); await running.WaitAsync(Guard); }
        var frames = Frames(output.ToArray()); Assert.Equal(7, frames.Length);
        for (var i = 0; i < 4; i++) Assert.True(Reply(frames, $"r{i}").TryGetProperty("result", out _));
        Assert.Equal("CREATURES_BUSY", Reply(frames, "r4").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("CREATURES_QUERY_BUSY", Reply(frames, "r5").GetProperty("error").GetProperty("code").GetString());
        Assert.True(Reply(frames, "r6").GetProperty("result").GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task Failed_open_terminal_flush_closes_pending_lease_and_shutdown_waits_actual_cleanup()
    {
        var cleanup = Signal(); var lease = new Lease { Release = cleanup.Task };
        var store = new Store { Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(lease) };
        await using var app = new CreatureGeometryService(store); await using var registry = new CreatureGeometryLeases(app);
        var parameters = Scope(); parameters["geometryArtifactId"] = new string('4', 64);
        using var input = new HeldInput(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { protocolVersion = 1, id = "open", method = "creatures.geometry.open", @params = parameters }, Json) + "\n"));
        using var output = new HeldFlush { FailFirst = true };
        var running = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), creatureGeometry: app, geometryLeases: registry).RunAsync(input, output);
        try
        {
            await Task.WhenAll(input.Waiting.Task, output.Started.Task).WaitAsync(Guard);
            output.Release.TrySetResult(); await lease.Closing.Task.WaitAsync(Guard);
            Assert.False(lease.Retirement.IsCompleted);
            input.Release.TrySetResult(); Assert.False(running.IsCompleted);
            cleanup.TrySetResult(); await running.WaitAsync(Guard); Assert.Equal(1, lease.Closes);
        }
        finally { cleanup.TrySetResult(); output.Release.TrySetResult(); input.Release.TrySetResult(); await running.WaitAsync(Guard); }
    }

    [Fact]
    public async Task Actual_progress_keeps_finite_geometry_codes_separate_from_label_and_terminal()
    {
        var release = Signal();
        var store = new Store { Prepare = async (r, report, _) =>
        {
            await report(new("modelVerification", "Verifying model source", "bytes", 12, 24, CreatureGeometryDependency.Model));
            await release.Task; return Result(r.Selection, true);
        } };
        await using var app = new CreatureGeometryService(store); await using var registry = new CreatureGeometryLeases(app);
        await using var session = await Session.Start(app, registry);
        try
        {
            await session.Send("prepare", "creatures.geometry.prepare", Scope());
            // Reporting accepts a latest-value observation; only receipt here proves actual wire dispatch.
            var progress = await session.Receive(); Assert.Equal("progress", progress.GetProperty("type").GetString());
            Assert.Equal("Verifying model source", progress.GetProperty("stage").GetString()); Assert.Equal("modelVerification", progress.GetProperty("geometryStage").GetString());
            Assert.Equal("model", progress.GetProperty("geometryDependency").GetString()); Assert.Equal(12, progress.GetProperty("completed").GetInt64());
            release.TrySetResult();
            Assert.Equal("ready", (await session.Receive()).GetProperty("result").GetProperty("state").GetString());
        }
        finally { release.TrySetResult(); } // Release before Session disposal even when receipt/assertion fails.
    }

    private static JsonElement Reply(IEnumerable<JsonElement> frames, string id) => Assert.Single(frames, f => f.GetProperty("id").GetString() == id);
    private static JsonElement[] Frames(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
    private sealed class HeldInput(byte[] bytes) : MemoryStream(bytes)
    {
        internal readonly TaskCompletionSource Waiting = Signal(), Release = Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (Position < Length) return await base.ReadAsync(buffer, cancellationToken); Waiting.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return 0; }
    }
    private sealed class HeldFlush : MemoryStream
    {
        internal readonly TaskCompletionSource Started = Signal(), Release = Signal(); internal bool FailFirst; private int flushes;
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { if (++flushes != 1) return; Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); if (FailFirst) throw new IOException("Authored failed terminal flush"); }
    }

    private sealed class Session(TcpClient client, Stream stream, Task running) : IAsyncDisposable
    {
        private readonly StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        private readonly StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        internal static async Task<Session> Start(CreatureGeometryService? app, CreatureGeometryLeases? registry)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync(); await connecting; listener.Stop();
            var host = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), creatureGeometry: app, geometryLeases: registry);
            async Task Run() { using (server) await host.RunAsync(server.GetStream(), server.GetStream()); }
            return new(client, client.GetStream(), Run());
        }
        internal Task Send(string id, string method, object parameters) => writer.WriteLineAsync(JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }, Json));
        internal async Task<JsonElement> Receive()
        { var line = await reader.ReadLineAsync().WaitAsync(Guard); Assert.NotNull(line); using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
        public async ValueTask DisposeAsync() { client.Client.Shutdown(SocketShutdown.Send); try { await running.WaitAsync(Guard); } finally { reader.Dispose(); writer.Dispose(); client.Dispose(); } }
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string p, CancellationToken t) => throw new NotSupportedException();
        public Task<long> BeginRefreshAsync(string p, CancellationToken t) => throw new NotSupportedException();
        public Task<DiscoveryPage> CommitAsync(string p, long g, DiscoveryAttempt a, string c, CancellationToken t) => throw new NotSupportedException();
        public Task<DiscoveryPage> ReadAsync(string p, string f, int l, string? c, CancellationToken t) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
