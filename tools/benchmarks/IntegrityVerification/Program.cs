using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;

// Standalone synthetic developer measurement. No engine/shared-project changes or source acquisition.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var checking = args.Length == 2 && args[0] == "check";
if (!checking && (args.Length != 5 || args[0] != "run" || args[1] is not ("legacy" or "budgeted") || args[2] is not ("4" or "64")))
    throw new ArgumentException("Use check <new-run-name> or run <legacy|budgeted> <4|64> <new-run-name> <expected-Infrastructure-SHA256>; see README.md.");
var api = new StoreApi(); // Reflection/delegate binding is outside every measured phase.
var assemblyPath = typeof(VerifiedObjectStore).Assembly.Location;
string assemblySha;
await using (var assembly = File.OpenRead(assemblyPath))
    assemblySha = Convert.ToHexStringLower(await SHA256.HashDataAsync(assembly));
if (!checking && args[4] != assemblySha)
    throw new InvalidOperationException("The loaded Infrastructure assembly does not match the supplied frozen SHA256.");
var run = NewRun(checking ? args[1] : args[3]);
var length = checking ? (1L << 20) + 65536 : long.Parse(args[2]) << 20;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; deadline.Cancel(); };
Console.CancelKeyPress += cancel;
var token = deadline.Token;
var arrival = new ArrivalSlot();
await using var objects = api.Create(run, arrival.Observe);
var phases = new List<object>();
var lifecycleChecks = new List<object>();
try
{
    var setup = await PrepareAsync(objects, length, token);
    var path = Path.Combine(run, "objects-v1", setup.Sha256[..2], setup.Sha256[2..]);
    foreach (var strategy in checking ? new[] { "legacy", "budgeted" } : [args[1]])
    {
        phases.Add(await MeasureAsync("actual-cas-full-open", strategy, async () =>
        {
            var call = new Call(strategy);
            var hashing = Stopwatch.StartNew();
            await call.OpenAsync(api, objects, setup.Sha256, length, token);
            hashing.Stop();
            return new { sha256 = setup.Sha256, verifiedBytes = length, returnedPosition = 0, handleClosed = true,
                sourceReadCalls = (long?)null, sourceBytesRead = (long?)null, cpuMonitorRequests = call.Work.Requests,
                fullOpenMilliseconds = hashing.Elapsed.TotalMilliseconds, fullOpenMiBPerSecond = length / 1048576d / hashing.Elapsed.TotalSeconds };
        }));
        phases.Add(await MeasureAsync("instrumented-core-foreground", strategy, async () =>
        {
            var threshold = Signal();
            await using var input = new CountingInput(path, () => threshold.TrySetResult());
            var call = new Call(strategy); long requested = 0, admitted = 0;
            using var probeStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            async Task Probe()
            {
                await threshold.Task.WaitAsync(probeStop.Token);
                requested = Stopwatch.GetTimestamp();
                using var cpu = await call.Resources.EnterCpuAsync(null, probeStop.Token);
                admitted = Stopwatch.GetTimestamp();
            }
            var probe = Probe();
            var hashing = Stopwatch.StartNew();
            string digest;
            try { digest = await call.HashAsync(api, input, length, token); }
            catch { probeStop.Cancel(); try { await probe; } catch (OperationCanceledException) { } throw; }
            hashing.Stop();
            await probe;
            RequireProof(input, digest, setup.Sha256, length);
            return new { sha256 = digest, sourceReadCalls = input.Reads, sourceBytesRead = input.BytesRead, eofReads = input.EofReads,
                cpuMonitorRequests = call.Work.Requests, foregroundRequestedBeforeHashTerminal = requested < call.Terminal,
                foregroundEnteredBeforeHashTerminal = admitted < call.Terminal,
                hashCoreMilliseconds = hashing.Elapsed.TotalMilliseconds, instrumentedCoreMiBPerSecond = length / 1048576d / hashing.Elapsed.TotalSeconds,
                foregroundRequestToEntryMilliseconds = Milliseconds(requested, admitted),
                foregroundRequestToHashTerminalMilliseconds = Milliseconds(requested, call.Terminal) };
        }));
        phases.Add(await MeasureAsync("instrumented-core-cancellation-retirement", strategy, async () =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            long requested = 0;
            var input = new CountingInput(path, () => { requested = Stopwatch.GetTimestamp(); stop.Cancel(); });
            var call = new Call(strategy); var canceled = false;
            try
            {
                try { _ = await call.HashAsync(api, input, length, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { canceled = true; }
                if (!canceled || requested == 0) throw new InvalidOperationException("Byte-triggered cancellation did not refuse a proof.");
            }
            finally { await input.DisposeAsync(); }
            var closed = Stopwatch.GetTimestamp();
            ExclusiveReopen(path);
            return new { outcome = "canceled", sha256Proof = (string?)null, sourceReadCalls = input.Reads,
                sourceBytesRead = input.BytesRead, cpuMonitorRequests = call.Work.Requests,
                cancelToHashTerminalMilliseconds = Milliseconds(requested, call.Terminal),
                cancelToActualCloseMilliseconds = Milliseconds(requested, closed), exclusivelyReopenedAfterClose = true };
        }));
        phases.Add(await MeasureAsync("actual-cas-timed-cancellation-retirement", strategy, () =>
            TimedOpenAsync(api, objects, arrival, strategy, setup.Sha256, length, path, token)));
        if (checking)
        {
            try
            {
                _ = await TimedOpenAsync(api, objects, arrival, strategy, setup.Sha256, 0, path, token);
                throw new InvalidOperationException("An invalid length unexpectedly completed.");
            }
            catch (DiscoveryException error) when (error.Code == "CATALOG_INTEGRITY")
            {
                ExclusiveReopen(path);
                lifecycleChecks.Add(new { strategy, boundary = "invalid-length-before-observer", originalFailurePreserved = true,
                    exclusivelyReopenedAfterRetirement = true });
            }
            foreach (var boundary in new[] { "before-observer", "before-timer" })
            {
                using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (boundary == "before-observer") observation.Cancel();
                var canceled = false;
                try { _ = await TimedOpenAsync(api, objects, arrival, strategy, setup.Sha256, length, path, observation.Token,
                    boundary == "before-timer" ? () => observation.Cancel() : null); }
                catch (OperationCanceledException) when (observation.IsCancellationRequested) { canceled = true; }
                if (!canceled) throw new InvalidOperationException("Observation cancellation was not propagated.");
                ExclusiveReopen(path);
                lifecycleChecks.Add(new { strategy, boundary, observationCanceled = true, exclusivelyReopenedAfterRetirement = true });
            }
        }
    }
    var stagingBytes = Directory.EnumerateFiles(Path.Combine(run, "staging-v1")).Sum(file => new FileInfo(file).Length);
    if (stagingBytes != 0) throw new InvalidOperationException("Staging ownership did not retire.");
    var report = new { mode = checking ? "authored-check" : "measurement", strategy = checking ? "both" : args[1],
        cacheState = "warm; generated/published in this process; no OS cache eviction", runDirectory = run,
        runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessorCount = Environment.ProcessorCount,
        machine = Environment.MachineName, filesystem = Filesystem(run), infrastructureAssembly = assemblyPath,
        sourceRevisionDeclaredByRunner = Environment.GetEnvironmentVariable("VIBEMINER_INTEGRITY_SOURCE_REVISION") ?? "unreported",
        infrastructureSha256 = assemblySha, infrastructureMvid = typeof(VerifiedObjectStore).Assembly.ManifestModule.ModuleVersionId,
        setup, retainedCanonicalSourceBytes = length, finalStagingBytes = stagingBytes, phases, lifecycleChecks,
        limitations = "No physical block-I/O counts, fsync/durability, cold-cache claim or aggregate engine memory guarantee. Core counters are not actual-CAS-open counters. Foreground overlap and cancellation outcome must be inspected per sample." };
    var output = JsonSerializer.Serialize(report, json);
    await File.WriteAllTextAsync(Path.Combine(run, "result.json"), output, token); Console.WriteLine(output);
}
catch (Exception error)
{
    await File.WriteAllTextAsync(Path.Combine(run, "failure.json"), JsonSerializer.Serialize(new
        { error = error.GetType().Name, error.Message, infrastructureSha256 = assemblySha, phases }, json));
    throw;
}
finally { Console.CancelKeyPress -= cancel; }

static async Task<object> TimedOpenAsync(StoreApi api, VerifiedObjectStore objects, ArrivalSlot arrival, string strategy,
    string sha, long length, string path, CancellationToken token, Action? beforeTimer = null)
{
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
    var signal = arrival.Arm(); var call = new Call(strategy); var outcome = "succeeded";
    var opening = call.OpenAsync(api, objects, sha, length, stop.Token);
    long requested = 0; Exception? primaryFailure = null;
    try
    {
        var first = await Task.WhenAny(signal.Task, opening).WaitAsync(token);
        if (first == opening && !signal.Task.IsCompleted)
        {
            await opening; // An immediate pre-observer failure must not wait for the deadline.
            throw new InvalidOperationException("CAS completed without the expected stripe arrival signal.");
        }
        await signal.Task.WaitAsync(token);
        beforeTimer?.Invoke(); // Authored check seam outside the CAS observer/resource scopes.
        await Task.Delay(1, token); // Measurement timer, not a guaranteed mid-read point.
        requested = Stopwatch.GetTimestamp(); stop.Cancel();
        try { await opening; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { outcome = "canceled"; }
    }
    catch (Exception error) { primaryFailure = error; throw; }
    finally
    {
        arrival.Clear(); stop.Cancel();
        // Observation cancellation never stands in for actual operation/stream retirement.
        try { await opening; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { outcome = "canceled"; }
        catch (Exception) when (primaryFailure is not null) { /* Preserve the observation failure after observing terminal cleanup. */ }
    }
    var retired = Stopwatch.GetTimestamp(); ExclusiveReopen(path);
    return new { outcome, sha256Proof = outcome == "succeeded" ? sha : null,
        cancelRequestedBeforeOperationTerminal = requested < call.Terminal,
        cancelToOperationTerminalMilliseconds = Math.Max(0, Milliseconds(requested, call.Terminal)),
        cancelToActualRetirementMilliseconds = Milliseconds(requested, retired), exclusivelyReopenedAfterClose = true,
        sourceReadCalls = (long?)null, sourceBytesRead = (long?)null, timerMilliseconds = 1, cpuMonitorRequests = call.Work.Requests };
}

static async Task<Setup> PrepareAsync(VerifiedObjectStore objects, long length, CancellationToken token)
{
    var clock = Stopwatch.StartNew(); var buffer = new byte[65536];
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    string path;
    await using (var output = await objects.CreateStagingFileAsync(token))
    {
        path = output.Name;
        for (long at = 0; at < length; at += buffer.Length)
        {
            token.ThrowIfCancellationRequested(); var count = (int)Math.Min(buffer.Length, length - at);
            for (var i = 0; i < count; i++) { var position = at + i; buffer[i] = unchecked((byte)(position * 31 + (position >> 8))); }
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        await output.FlushAsync(token);
    }
    var sha = Convert.ToHexStringLower(hash.GetHashAndReset()); var generated = clock.Elapsed.TotalMilliseconds;
    clock.Restart(); var published = await objects.PublishFileAsync(path, length, token);
    if (published != sha) throw new InvalidOperationException("Setup publication differs from deterministic source SHA.");
    return new(sha, length, generated, clock.Elapsed.TotalMilliseconds, length,
        "One streaming generation SHA plus one actual CAS publication incoming SHA; setup excluded from measured lanes.");
}
static async Task<object> MeasureAsync(string lane, string strategy, Func<Task<object>> operation)
{
    await using var memory = new MemorySample(); var clock = Stopwatch.StartNew();
    var detail = await operation(); var elapsed = clock.Elapsed.TotalMilliseconds;
    await memory.StopAsync();
    return new { lane, strategy, elapsedMilliseconds = elapsed, memory = memory.Result(), detail };
}
static void RequireProof(CountingInput input, string actual, string expected, long length)
{
    if (actual != expected || input.BytesRead != length || input.Position != length || input.EofReads != 1)
        throw new InvalidOperationException("Full source SHA/length/EOF proof differs between paths.");
}
static void ExclusiveReopen(string path)
{ using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
static double Milliseconds(long start, long end) => (end - start) * 1000d / Stopwatch.Frequency;
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static string NewRun(string name)
{
    if (name.Length is < 1 or > 64 || name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')))
        throw new ArgumentException("Choose a fresh plain run name of at most64 letters/digits/hyphens/underscores.");
    var path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local", "integrity-verification", name));
    for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        if (new DirectoryInfo(ancestor).LinkTarget is not null) throw new ArgumentException("Symbolic output ancestors are refused.");
    if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "VibeMiner.slnx")))
        throw new ArgumentException("Invoke from the checkout root.");
    if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Run already exists; choose another name.");
    Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, ".synthetic-integrity-benchmark"), "Synthetic app-owned developer measurement; no source admission.\n");
    return path;
}
static object Filesystem(string path)
{
    var drive = DriveInfo.GetDrives().Where(candidate => path.StartsWith(candidate.Name, StringComparison.Ordinal))
        .OrderByDescending(candidate => candidate.Name.Length).FirstOrDefault();
    return new { mount = drive?.Name, format = drive?.DriveFormat };
}

sealed record Setup(string Sha256, long GeneratedBytes, double GenerationMilliseconds, double PublicationMilliseconds,
    long MaximumKnownSetupStagingBytes, string HashPassScope);

sealed class ArrivalSlot
{
    private TaskCompletionSource? current;
    internal TaskCompletionSource Arm()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref current, signal); return signal; }
    internal void Observe(string _) => Volatile.Read(ref current)?.TrySetResult();
    internal void Clear() => Volatile.Write(ref current, null);
}

sealed class StoreApi
{
    internal delegate Task<string> Core(Stream input, long length, string errorCode, AcquisitionResources? resources,
        IAcquisitionWorkContext? workContext, CancellationToken token);
    internal delegate Task<FileStream> Open(VerifiedObjectStore objects, string sha, long length, AcquisitionResources resources,
        IAcquisitionWorkContext? workContext, CancellationToken token);
    internal readonly Core Hash;
    internal readonly Open BudgetedOpen;
    private readonly ConstructorInfo constructor;
    internal StoreApi()
    {
        var type = typeof(VerifiedObjectStore);
        Hash = (type.GetMethod("HashFileAsync", BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(Stream), typeof(long), typeof(string), typeof(AcquisitionResources), typeof(IAcquisitionWorkContext), typeof(CancellationToken)], null)
            ?? throw new MissingMethodException("Exact CAS HashFileAsync signature unavailable.")).CreateDelegate<Core>();
        BudgetedOpen = (type.GetMethod("OpenVerifiedAsync", BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(string), typeof(long), typeof(AcquisitionResources), typeof(IAcquisitionWorkContext), typeof(CancellationToken)], null)
            ?? throw new MissingMethodException("Exact budgeted CAS open signature unavailable.")).CreateDelegate<Open>();
        constructor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(string), typeof(Func<string, long, string, CancellationToken, Task>), typeof(Action<string>)], null)
            ?? throw new MissingMethodException("Exact internal pre-stripe observer constructor unavailable.");
    }
    internal VerifiedObjectStore Create(string directory, Action<string> observer) =>
        (VerifiedObjectStore)constructor.Invoke([directory, null, observer]);
}

sealed class Call(string strategy)
{
    internal AcquisitionResources Resources { get; } = new(maximumCpu: 1, maximumBackgroundCpu: 1);
    internal Work Work { get; } = new();
    internal long Terminal { get; private set; }
    internal async Task<string> HashAsync(StoreApi api, Stream input, long length, CancellationToken token)
    {
        using var outer = strategy == "legacy" ? await Resources.EnterCpuAsync(Work, token) : null;
        try { return await api.Hash(input, length, "BENCHMARK_SOURCE_INTEGRITY", strategy == "legacy" ? null : Resources,
            strategy == "legacy" ? null : Work, token); }
        finally { Terminal = Stopwatch.GetTimestamp(); }
    }
    internal async Task OpenAsync(StoreApi api, VerifiedObjectStore objects, string sha, long length, CancellationToken token)
    {
        using var outer = strategy == "legacy" ? await Resources.EnterCpuAsync(Work, token) : null;
        try
        {
            await using var input = strategy == "legacy" ? await objects.OpenVerifiedAsync(sha, length, token)
                : await api.BudgetedOpen(objects, sha, length, Resources, Work, token);
            if (input.Length != length || input.Position != 0) throw new InvalidOperationException("CAS returned the wrong exact lease.");
        }
        finally { Terminal = Stopwatch.GetTimestamp(); }
    }
}

sealed class Work : IAcquisitionWorkContext
{
    private long requests;
    internal long Requests => Interlocked.Read(ref requests);
    public AcquisitionWorkSnapshot Snapshot => new(0, AcquisitionPriority.Background, []);
    public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
    public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default)
    { Interlocked.Increment(ref requests); return Task.Delay(Timeout.InfiniteTimeSpan, token); }
}

sealed class CountingInput(string path, Action threshold) : Stream
{
    private readonly FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    private bool reported;
    internal long Reads { get; private set; }
    internal long BytesRead { get; private set; }
    internal long EofReads { get; private set; }
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => input.CanSeek;
    public override bool CanWrite => false;
    public override long Length => input.Length;
    public override long Position { get => input.Position; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        Reads++; var count = await input.ReadAsync(buffer, token); BytesRead += count;
        if (count == 0) EofReads++;
        if (!reported && BytesRead >= 1L << 20) { reported = true; threshold(); }
        return count;
    }
    public override async ValueTask DisposeAsync() { await input.DisposeAsync(); GC.SuppressFinalize(this); }
    protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class MemorySample : IAsyncDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly CancellationTokenSource stop = new();
    private readonly long allocated = GC.GetTotalAllocatedBytes(precise: true);
    private readonly Task sampling;
    private long maximum, samples, finalAllocated, peak, managed;
    private bool stopped;
    internal MemorySample() { Observe(); sampling = SampleAsync(); }
    private void Observe() { process.Refresh(); maximum = Math.Max(maximum, process.WorkingSet64); samples++; }
    private async Task SampleAsync()
    {
        try { while (true) { await Task.Delay(5, stop.Token); Observe(); } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    internal async Task StopAsync()
    {
        if (stopped) return; stopped = true; stop.Cancel(); await sampling; Observe();
        finalAllocated = GC.GetTotalAllocatedBytes(precise: true); peak = process.PeakWorkingSet64; managed = GC.GetTotalMemory(false);
    }
    internal object Result() => new { processAllocatedBytes = finalAllocated - allocated, maximumSampledWorkingSetBytes = maximum,
        workingSetSampleCount = samples, processLifetimePeakWorkingSetBytes = peak, managedHeapBytesAfter = managed, sampleIntervalMilliseconds = 5 };
    public async ValueTask DisposeAsync() { await StopAsync(); stop.Dispose(); process.Dispose(); }
}
