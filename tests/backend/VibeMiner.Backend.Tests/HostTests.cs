using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class HostTests
{
    [Fact]
    public async Task ProtocolRejectsMalformedVersionDuplicateFieldsAndOversizeThenContinues()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await using var session = await Session.Start(new DiscoveryService(new NeverSource(), catalog));
        await session.SendRaw("{broken}"); Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.SendRaw("{\"protocolVersion\":2,\"id\":\"bad-version\",\"method\":\"hello\",\"params\":{}}");
        Assert.Equal("PROTOCOL_VERSION", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.SendRaw("{\"protocolVersion\":1,\"id\":\"dup\",\"method\":\"hello\",\"params\":{},\"params\":{}}");
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.SendRaw(new string('x', ProtocolHost.MaxFrameBytes + 1));
        Assert.Equal("FRAME_LIMIT", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("invalid-limit", "discovery.read", new { productId = "wow", limit = "wrong" });
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("good", "hello"); var hello = await session.Receive();
        Assert.Equal("good", hello.GetProperty("id").GetString()); Assert.Equal(1, hello.GetProperty("result").GetProperty("protocolVersion").GetInt32());
    }
    [Fact]
    public async Task DuplicateActiveIdsAndCancelHaveSeparateRepliesAndWaitForRetirement()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var source = new GatedSource();
        await using var session = await Session.Start(new DiscoveryService(source, catalog));
        await session.Send("refresh", "discovery.refresh", new { productId = "wow" });
        var progress = await session.Receive(); Assert.Equal(1, progress.GetProperty("sequence").GetInt32());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Send("refresh", "hello");
        Assert.Equal("DUPLICATE_ID", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("cancel", "request.cancel", new { requestId = "refresh" });
        var ack = await session.Receive(); Assert.Equal("cancel", ack.GetProperty("id").GetString());
        Assert.True(ack.GetProperty("result").GetProperty("requested").GetBoolean());
        await source.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A local query can still complete while the producer is stopping.
        await session.Send("read", "discovery.read", new { productId = "wow" });
        var read = await session.Receive(); Assert.Equal("read", read.GetProperty("id").GetString());
        source.Cleanup.SetResult();
        var terminal = await session.Receive(); Assert.Equal("refresh", terminal.GetProperty("id").GetString());
        Assert.Equal("CANCELED", terminal.GetProperty("error").GetProperty("code").GetString());
        await session.Send("again", "request.cancel", new { requestId = "refresh" });
        Assert.False((await session.Receive()).GetProperty("result").GetProperty("requested").GetBoolean());
    }
    [Fact]
    public async Task CrossProductRefreshReportsOrderedProductProgressAndOneTerminalPage()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        using var source = Fixtures.Source(Fixtures.Versions(Fixtures.Row()));
        await using var session = await Session.Start(new DiscoveryService(source, catalog));
        await session.Send("bad-param", "discovery.refreshAll", new { productId = "wow" });
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("bad-product", "discovery.readAll", new { productId = "../wow" });
        Assert.Equal("INVALID_PRODUCT", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("bad-limit", "discovery.readAll", new { limit = "50" });
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("all", "discovery.refreshAll");
        var completed = new List<int>(); var sequences = new List<int>();
        JsonElement terminal;
        while (true)
        {
            var frame = await session.Receive();
            if (!frame.TryGetProperty("type", out _)) { terminal = frame; break; }
            Assert.Equal("all", frame.GetProperty("requestId").GetString());
            Assert.Equal("products", frame.GetProperty("unit").GetString());
            Assert.Equal(ProductRegistry.Products.Count, frame.GetProperty("total").GetInt32());
            completed.Add(frame.GetProperty("completed").GetInt32()); sequences.Add(frame.GetProperty("sequence").GetInt32());
        }
        Assert.Equal(Enumerable.Range(0, ProductRegistry.Products.Count + 1), completed);
        Assert.Equal(Enumerable.Range(1, ProductRegistry.Products.Count + 1), sequences);
        var result = terminal.GetProperty("result");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("productId").ValueKind);
        Assert.Equal(ProductRegistry.Products.Count, result.GetProperty("products").GetArrayLength());
        Assert.Equal(ProductRegistry.Products.Count, result.GetProperty("builds").GetArrayLength());
        Assert.Equal(0, result.GetProperty("refreshProblems").GetArrayLength());
        await session.Send("wowt", "discovery.readAll", new { productId = "wowt", filter = "advertised", limit = 5 });
        var read = (await session.Receive()).GetProperty("result");
        Assert.Equal("wowt", read.GetProperty("productId").GetString());
        Assert.Equal("wowt", read.GetProperty("builds")[0].GetProperty("productId").GetString());
        Assert.Equal(1, read.GetProperty("builds").GetArrayLength());
    }
    [Fact]
    public async Task CanceledCrossProductRefreshRepliesOnlyAfterTheRunningChecksRetired()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var source = new GatedSource();
        await using var session = await Session.Start(new DiscoveryService(source, catalog));
        await session.Send("all", "discovery.refreshAll");
        Assert.Equal(0, (await session.Receive()).GetProperty("completed").GetInt32());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Send("cancel", "request.cancel", new { requestId = "all" });
        Assert.True((await session.Receive()).GetProperty("result").GetProperty("requested").GetBoolean());
        await source.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Send("read", "discovery.readAll");
        Assert.Equal("read", (await session.Receive()).GetProperty("id").GetString());
        source.Cleanup.SetResult();
        var terminal = await session.Receive();
        Assert.Equal("all", terminal.GetProperty("id").GetString());
        Assert.Equal("CANCELED", terminal.GetProperty("error").GetProperty("code").GetString());
    }
    [Fact]
    public async Task AdmissionIsBoundedAndControlRequestsRemainAvailable()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var source = new GatedSource();
        await using var session = await Session.Start(new DiscoveryService(source, catalog));
        for (var i = 0; i < 32; i++)
        {
            await session.Send($"r{i}", "discovery.refresh", new { productId = "wow" });
            Assert.Equal("progress", (await session.Receive()).GetProperty("type").GetString());
        }
        await session.Send("too-many", "hello");
        Assert.Equal("BUSY", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("cancel-first", "request.cancel", new { requestId = "r0" });
        Assert.True((await session.Receive()).GetProperty("result").GetProperty("requested").GetBoolean());
        source.Cleanup.SetResult();
        Assert.Equal("r0", (await session.Receive()).GetProperty("id").GetString());
        await session.Send("stop", "shutdown");
        // Shutdown acknowledgement and the remaining terminal replies may interleave.
        var replies = new List<JsonElement>();
        for (var i = 0; i < 32; i++) replies.Add(await session.Receive());
        Assert.Contains(replies, reply => reply.GetProperty("id").GetString() == "stop" && reply.GetProperty("result").GetProperty("stopping").GetBoolean());
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task RealProcessHelloLocalReadAndShutdownUseOnlyProtocolStdout()
    {
        using var directory = new TempDirectory();
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = dotnetRoot is null ? "dotnet" : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var hostAssembly = typeof(ProtocolHost).Assembly.Location;
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(hostAssembly); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(directory.Path);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        foreach (var (id, method, parameters) in new[] { ("hello", "hello", new { }), ("products", "products.list", new { }) })
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reply = JsonDocument.Parse(line!); Assert.Equal(id, reply.RootElement.GetProperty("id").GetString());
            Assert.True(reply.RootElement.TryGetProperty("result", out _));
        }
        await process.StandardInput.WriteLineAsync("{\"protocolVersion\":1,\"id\":\"read\",\"method\":\"discovery.read\",\"params\":{\"productId\":\"wow\"}}");
        await process.StandardInput.FlushAsync();
        using (var read = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!))
        { Assert.Equal("notChecked", read.RootElement.GetProperty("result").GetProperty("connection").GetString()); Assert.Equal(0, read.RootElement.GetProperty("result").GetProperty("builds").GetArrayLength()); }
        await process.StandardInput.WriteLineAsync("{\"protocolVersion\":1,\"id\":\"stop\",\"method\":\"shutdown\",\"params\":{}}");
        await process.StandardInput.FlushAsync();
        using (var stop = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!))
            Assert.True(stop.RootElement.GetProperty("result").GetProperty("stopping").GetBoolean());
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode); Assert.Equal("", await stderr);
        Assert.Null(await process.StandardOutput.ReadLineAsync());
    }
    [Theory]
    [InlineData("DROP TABLE catalog_lineage; PRAGMA user_version=14;", 3, "CATALOG_RESET_REQUIRED", "fresh catalog")]
    [InlineData("PRAGMA user_version=16;", 4, "CATALOG_VERSION", "newer engine")]
    public async Task RealProcessRefusesAnOlderOrNewerCatalogWithItsTypedCode(string sql, int exitCode, string code, string text)
    {
        using var directory = new TempDirectory();
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path)) { }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = dotnetRoot is null ? "dotnet" : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(ProtocolHost).Assembly.Location); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(directory.Path);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(exitCode, process.ExitCode); Assert.Equal("", await stdout);
        var message = await stderr;
        Assert.Contains(code, message, StringComparison.Ordinal);
        Assert.Contains(text, message, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Table_data_dispatch_validates_exact_scope_and_reports_cancel_after_cleanup()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var store = new GatedTableStore(); await using var data = new TableDataService(store);
        await using var session = await Session.Start(new DiscoveryService(new NeverSource(), catalog), data);
        var scope = new { productId = "wow", buildIdentity = new string('a', 64), sourceRegion = "us", tableId = "b977271e", researchLocale = "enUS" };
        await session.Send("data", "tables.data", scope);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await session.Receive()).GetProperty("result").GetProperty("snapshot").ValueKind);
        await session.Send("invalid", "tables.rows", new { scope.productId, scope.buildIdentity, scope.sourceRegion, scope.tableId,
            scope.researchLocale, artifactId = new string('b', 64), limit = 51 });
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("load", "tables.prepare", scope);
        Assert.Equal("Locating selected table", (await session.Receive()).GetProperty("stage").GetString());
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Send("stop-load", "request.cancel", new { requestId = "load" });
        Assert.Equal("stop-load", (await session.Receive()).GetProperty("id").GetString());
        await store.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Send("while-stopping", "tables.data", scope);
        Assert.Equal("while-stopping", (await session.Receive()).GetProperty("id").GetString());
        store.Cleanup.TrySetResult();
        var terminal = await session.Receive(); Assert.Equal("load", terminal.GetProperty("id").GetString());
        Assert.Equal("CANCELED", terminal.GetProperty("error").GetProperty("code").GetString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_table_prepare_waits_for_cleanup_then_loads_its_own_table_even_after_unrelated_failure(bool failFirst)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var store = new SequencedTableStore { FailFirst = failFirst }; await using var data = new TableDataService(store);
        await using var session = await Session.Start(new DiscoveryService(new NeverSource(), catalog), data);
        try
        {
            await session.Send("first", "tables.prepare", TableScope("b977271e"));
            Assert.Equal("Preparing first table", (await session.Receive()).GetProperty("stage").GetString());
            await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("second", "tables.prepare", TableScope("50238ec2"));
            var waiting = await session.Receive();
            Assert.Equal("second", waiting.GetProperty("requestId").GetString());
            Assert.Equal("Waiting for shared table preparation", waiting.GetProperty("stage").GetString());
            Assert.False(store.SecondStarted.Task.IsCompleted);
            store.FinishFirst.TrySetResult(); await store.FirstRetiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("during-cleanup", "hello");
            Assert.Equal("during-cleanup", (await session.Receive()).GetProperty("id").GetString());
            Assert.False(store.SecondStarted.Task.IsCompleted);
            store.CleanupFirst.TrySetResult(); await store.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var transitioned = new[] { await session.Receive(), await session.Receive() };
            var first = Assert.Single(transitioned, reply => reply.TryGetProperty("id", out var id) && id.GetString() == "first");
            if (failFirst) Assert.Equal("AUTHORED_TABLE_FAILURE", first.GetProperty("error").GetProperty("code").GetString());
            else Assert.Equal("b977271e", first.GetProperty("result").GetProperty("tableId").GetString());
            var started = Assert.Single(transitioned, reply => reply.TryGetProperty("type", out var type) && type.GetString() == "progress");
            Assert.Equal("second", started.GetProperty("requestId").GetString());
            Assert.Equal("Preparing requested table", started.GetProperty("stage").GetString());
            Assert.Equal(2, started.GetProperty("sequence").GetInt32());
            store.FinishSecond.TrySetResult();
            var second = await session.Receive(); Assert.Equal("second", second.GetProperty("id").GetString());
            Assert.Equal("50238ec2", second.GetProperty("result").GetProperty("tableId").GetString());
            Assert.Equal(2, store.Prepares);
        }
        finally { store.ReleaseAll(); }
    }

    [Fact]
    public async Task Canceling_waiting_table_request_does_not_cancel_the_active_table_or_start_the_waiter()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var store = new SequencedTableStore(); await using var data = new TableDataService(store);
        await using var session = await Session.Start(new DiscoveryService(new NeverSource(), catalog), data);
        try
        {
            await session.Send("active", "tables.prepare", TableScope("b977271e")); await session.Receive();
            await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("waiting", "tables.prepare", TableScope("50238ec2"));
            Assert.Equal("Waiting for shared table preparation", (await session.Receive()).GetProperty("stage").GetString());
            await session.Send("cancel-wait", "request.cancel", new { requestId = "waiting" });
            var canceled = new[] { await session.Receive(), await session.Receive() };
            var ack = Assert.Single(canceled, reply => reply.GetProperty("id").GetString() == "cancel-wait");
            Assert.True(ack.GetProperty("result").GetProperty("requested").GetBoolean());
            var terminal = Assert.Single(canceled, reply => reply.GetProperty("id").GetString() == "waiting");
            Assert.Equal("CANCELED", terminal.GetProperty("error").GetProperty("code").GetString());
            Assert.False(store.FirstToken.IsCancellationRequested);
            Assert.False(store.SecondStarted.Task.IsCompleted);
            Assert.Equal(1, store.Prepares);
            store.FinishFirst.TrySetResult(); store.CleanupFirst.TrySetResult();
            var completed = await session.Receive(); Assert.Equal("active", completed.GetProperty("id").GetString());
            Assert.Equal("b977271e", completed.GetProperty("result").GetProperty("tableId").GetString());
            Assert.Equal(1, store.Prepares);
        }
        finally { store.ReleaseAll(); }
    }

    [Fact]
    public async Task Matching_table_prepare_consumers_still_share_one_host_producer()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var store = new SequencedTableStore(); await using var data = new TableDataService(store);
        await using var session = await Session.Start(new DiscoveryService(new NeverSource(), catalog), data);
        try
        {
            await session.Send("one", "tables.prepare", TableScope("b977271e")); await session.Receive();
            await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("two", "tables.prepare", TableScope("b977271e"));
            // A following control response establishes that both prepare frames were dispatched.
            await session.Send("after-attach", "hello");
            Assert.Equal("after-attach", (await session.Receive()).GetProperty("id").GetString());
            store.FinishFirst.TrySetResult(); store.CleanupFirst.TrySetResult();
            var replies = new[] { await session.Receive(), await session.Receive() };
            Assert.Equal(new[] { "one", "two" }, replies.Select(reply => reply.GetProperty("id").GetString()).Order());
            Assert.All(replies, reply => Assert.Equal("b977271e", reply.GetProperty("result").GetProperty("tableId").GetString()));
            Assert.Equal(1, store.Prepares);
        }
        finally { store.ReleaseAll(); }
    }

    [Fact]
    public async Task Repeated_table_busy_results_stop_after_eight_bounded_ownership_attempts()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var store = new BusyTableStore(); await using var data = new TableDataService(store);
        await using var session = await Session.Start(new DiscoveryService(new NeverSource(), catalog), data);
        await session.Send("busy", "tables.prepare", TableScope("50238ec2"));
        for (var attempt = 1; attempt < 8; attempt++)
        {
            var progress = await session.Receive();
            Assert.Equal("Waiting for shared table preparation", progress.GetProperty("stage").GetString());
            Assert.Equal(attempt, progress.GetProperty("sequence").GetInt32());
        }
        var terminal = await session.Receive();
        Assert.Equal("TABLE_DATA_BUSY", terminal.GetProperty("error").GetProperty("code").GetString());
        Assert.True(terminal.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.Equal(8, store.Prepares);
    }

    private static object TableScope(string tableId) => new { productId = "wow", buildIdentity = new string('a', 64),
        sourceRegion = "us", tableId, researchLocale = "enUS" };

    private sealed class SequencedTableStore : ITableDataStore
    {
        internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstRetiring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FinishFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FinishSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailFirst;
        internal int Prepares;
        internal CancellationToken FirstToken;
        internal void ReleaseAll() { FinishFirst.TrySetResult(); CleanupFirst.TrySetResult(); FinishSecond.TrySetResult(); }
        public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token) => Task.FromResult(new TableDataReadiness(null));
        public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("No row reads expected.");
        public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
        {
            var ordinal = Interlocked.Increment(ref Prepares);
            if (ordinal == 1)
            {
                FirstToken = token; await progress("Preparing first table"); FirstStarted.TrySetResult();
                try { await FinishFirst.Task.WaitAsync(token); }
                finally { FirstRetiring.TrySetResult(); await CleanupFirst.Task; }
                if (FailFirst) throw new DiscoveryException("AUTHORED_TABLE_FAILURE", "The unrelated authored table failed.");
            }
            else
            {
                await progress("Preparing requested table"); SecondStarted.TrySetResult();
                await FinishSecond.Task.WaitAsync(token);
            }
            const string time = "2026-10-19T00:00:00Z";
            var metadata = new MetadataReceipt("encoding", new string('c', 32), new string('d', 32), new string('e', 64),
                "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/authored", time, time, "authored-v1");
            return new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale,
                new string('b', 64), time, "996192AA", new string('f', 64), new string('a', 40), "authored-db2-v1",
                new(metadata.ContentKey, metadata.EncodingKey, metadata.Sha256, "10", "10", metadata.SourceUrl, time, time, "authored-v1", new("loose", "0", "10", null)),
                new("841626", "enUS", "authored-v1", metadata, metadata with { Role = "root" }, []), [],
                new("complete", "0", "0", "0", "0", "0", "0", "0", "0"), "0");
        }
    }
    private sealed class BusyTableStore : ITableDataStore
    {
        internal int Prepares;
        public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token) => throw new InvalidOperationException("No reads expected.");
        public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("No row reads expected.");
        public Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
        {
            Interlocked.Increment(ref Prepares);
            return Task.FromException<TableDataSnapshot>(new DiscoveryException("TABLE_DATA_BUSY", "Authored competing ownership.", true));
        }
    }
    private sealed class GatedTableStore : ITableDataStore
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token) => Task.FromResult(new TableDataReadiness(null));
        public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("Invalid pages must not reach storage.");
        public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
        {
            await progress("Locating selected table"); Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Stopping.TrySetResult(); await Cleanup.Task; }
            throw new InvalidOperationException("Only cancellation retires this authored source.");
        }
    }
    private sealed class NeverSource : IDiscoverySource
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException("Unexpected HTTP request.");
    }
    private sealed class GatedSource : IDiscoverySource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Stopping.TrySetResult(); await Cleanup.Task; }
            throw new InvalidOperationException("The gate only retires by cancellation.");
        }
    }
    private sealed class Session : IAsyncDisposable
    {
        private readonly TcpClient client;
        private readonly TcpClient server;
        private readonly StreamWriter writer;
        private readonly StreamReader reader;
        public Task Completion { get; }
        private Session(TcpClient client, TcpClient server, DiscoveryService service, TableDataService? tableData = null)
        {
            this.client = client; this.server = server;
            writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true);
            reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            Completion = new ProtocolHost(service, tableData: tableData).RunAsync(server.GetStream(), server.GetStream());
        }
        public static async Task<Session> Start(DiscoveryService service, TableDataService? tableData = null)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var server = await listener.AcceptTcpClientAsync(); return new(client, server, service, tableData);
        }
        public Task Send(string id, string method, object? parameters = null) => SendRaw(JsonSerializer.Serialize(new { protocolVersion = 1, id, method, @params = parameters ?? new { } }));
        public async Task SendRaw(string value) { await writer.WriteLineAsync(value); await writer.FlushAsync(); }
        public async Task<JsonElement> Receive()
        { using var value = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!); return value.RootElement.Clone(); }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { server.Dispose(); reader.Dispose(); await writer.DisposeAsync(); }
        }
    }
}
