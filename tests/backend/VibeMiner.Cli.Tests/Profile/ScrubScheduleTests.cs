using System.Threading.Channels;
using VibeMiner.Cli.Profile;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests.Profile;

/// <summary>The background scrub: when it runs, how it yields to interactive work, and what it records. A fake clock, no sleeps.</summary>
public sealed class ScrubScheduleTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan First = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly ScrubReport CleanReport = new(null, false, new VerifyReport(42, 4200, 0, 0, [], []));

    private readonly string folder = Path.Combine(Path.GetTempPath(), "vibeminer-scrub-test-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock clock = new(Start);
    private readonly FakeScrub scrub = new();
    private volatile bool busy;
    private volatile bool busyThrows;

    public ScrubScheduleTests() => Directory.CreateDirectory(folder);
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    private ScrubSchedule Schedule(ScrubScheduleOptions? options = null) =>
        new(scrub.Run, folder, clock, () => busyThrows ? throw new InvalidOperationException("busy check broke") : busy, options);
    private string RecordFile => ScrubRecords.PathIn(folder);

    [Fact]
    public async Task WithoutARecordItRunsFirstDelayAfterStartAndRecordsTheOutcome()
    {
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        Assert.Equal(ScrubRecordState.Absent, schedule.Last?.State);
        clock.Advance(First);
        var call = await scrub.Next();
        Assert.True(schedule.Running);
        call.Result.SetResult(CleanReport);
        await clock.NextTimer(Day); // next due a week later, slept a day at a time

        Assert.False(schedule.Running);
        var read = await ScrubRecords.ReadAsync(folder);
        Assert.Equal(ScrubRecordState.Present, read.State);
        Assert.Equal(ScrubOutcome.Clean, read.Record!.Outcome);
        Assert.Equal(Start + First, read.Record.FinishedAt);
        Assert.Equal(42, read.Record.Checked);
        Assert.Equal(read.Record.FinishedAt, schedule.Last?.Record?.FinishedAt);
        Assert.Equal(1, scrub.Calls);
        Assert.Equal(1, clock.Pending); // only the day's sleep: the run's busy poll was canceled when it ended
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-1)] // DateTimeOffset.MaxValue
    public async Task ARecordFinishedInTheFutureIsDueNoLaterThanAnIntervalFromNow(int daysAhead)
    {
        var finished = daysAhead < 0 ? DateTimeOffset.MaxValue : Start + TimeSpan.FromDays(daysAhead);
        await ScrubRecords.WriteAsync(folder, ScrubRecord.FromReport(CleanReport, finished));
        await using var schedule = Schedule(new ScrubScheduleOptions { Interval = TimeSpan.FromHours(1) });
        schedule.Start();
        await clock.NextTimer(TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromHours(1));
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(TimeSpan.FromHours(1));
        Assert.Equal(Start + TimeSpan.FromHours(1), (await ScrubRecords.ReadAsync(folder)).Record!.FinishedAt);
    }

    [Fact]
    public async Task ANewerRecordIsOverwrittenByTheNextScrub()
    {
        await File.WriteAllTextAsync(RecordFile, """{"version":2}""");
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        Assert.Equal(ScrubRecordState.Newer, schedule.Last?.State);
        clock.Advance(First);
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(Day);
        var read = await ScrubRecords.ReadAsync(folder);
        Assert.Equal(ScrubRecordState.Present, read.State);
        Assert.Equal(Start + First, read.Record!.FinishedAt);
    }

    [Fact]
    public async Task AFailedWriteKeepsTheRecordInLastAndTimesTheNextScrubFromIt()
    {
        Directory.CreateDirectory(RecordFile); // the rename onto it fails
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        Assert.Equal(ScrubRecordState.Unreadable, schedule.Last?.State);
        clock.Advance(First);
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(Day);
        Assert.Equal(ScrubRecordState.Present, schedule.Last?.State);
        Assert.Equal(Start + First, schedule.Last!.Record!.FinishedAt);
        for (var day = 1; day < 7; day++)
        {
            clock.Advance(Day);
            await clock.NextTimer(Day);
        }
        Assert.Equal(1, scrub.Calls);
        clock.Advance(Day);
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(Day);
        Assert.Equal(2, scrub.Calls);
        Assert.Equal(Start + First + TimeSpan.FromDays(7), schedule.Last!.Record!.FinishedAt);
    }

    [Fact]
    public async Task ABusyCheckThatThrowsCountsAsBusy()
    {
        busyThrows = true;
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        for (var i = 0; i < 3; i++)
        {
            await clock.NextTimer(Idle);
            clock.Advance(Idle);
        }
        await clock.NextTimer(Idle);
        Assert.Equal(0, scrub.Calls);

        busyThrows = false;
        clock.Advance(Idle);
        await scrub.Next();
        await clock.NextTimer(Idle); // the check during the run
        busyThrows = true;
        clock.Advance(Idle); // a throwing check pauses the run too
        await clock.NextTimer(Idle);
        Assert.False(schedule.Running);
        Assert.False(File.Exists(RecordFile));
    }

    [Fact]
    public async Task ItWaitsForTheIntervalAfterTheLastRecordAndNotLonger()
    {
        var finished = Start - TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
        await ScrubRecords.WriteAsync(folder, ScrubRecord.FromReport(CleanReport, finished));
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, scrub.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(Day);
        Assert.Equal(Start + TimeSpan.FromHours(1), (await ScrubRecords.ReadAsync(folder)).Record!.FinishedAt);
    }

    [Fact]
    public async Task AnUnreadableRecordCountsAsNoRecord()
    {
        await File.WriteAllTextAsync(RecordFile, "{ not json");
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        Assert.Equal(ScrubRecordState.BadJson, schedule.Last?.State);
    }

    [Fact]
    public async Task ItWaitsWhileInteractiveWorkIsRunning()
    {
        busy = true;
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        await clock.NextTimer(Idle);
        clock.Advance(Idle);
        await clock.NextTimer(Idle);
        Assert.Equal(0, scrub.Calls);
        busy = false;
        clock.Advance(Idle);
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(Day);
        Assert.Equal(Start + First + Idle + Idle, (await ScrubRecords.ReadAsync(folder)).Record!.FinishedAt);
    }

    [Fact]
    public async Task InteractiveWorkDuringAScrubCancelsItAndItRerunsFromTheStartRecordingOnce()
    {
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        var first = await scrub.Next();
        await clock.NextTimer(Idle); // the check during the run
        busy = true;
        clock.Advance(Idle);
        await first.Result.Task.ContinueWith(_ => { }); // canceled through its token
        Assert.True(first.Token.IsCancellationRequested);
        await clock.NextTimer(Idle); // waiting for idle again
        Assert.False(schedule.Running);
        Assert.False(File.Exists(RecordFile));
        busy = false;
        clock.Advance(Idle);
        var second = await scrub.Next();
        Assert.False(second.Token.IsCancellationRequested);
        second.Result.SetResult(CleanReport);
        await clock.NextTimer(Day);

        Assert.Equal(2, scrub.Calls);
        var record = (await ScrubRecords.ReadAsync(folder)).Record!;
        Assert.Equal(Start + First + Idle + Idle, record.FinishedAt);
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task AnExceptionAfterAPauseRequestCountsAsAPause()
    {
        scrub.ThrowOnCancel = new ObjectStoreException("STORE_CLOSED", "Stopped mid-read.");
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        await scrub.Next();
        await clock.NextTimer(Idle);
        busy = true;
        clock.Advance(Idle);
        await clock.NextTimer(Idle); // waiting for idle, nothing recorded
        Assert.False(File.Exists(RecordFile));
        busy = false;
        scrub.ThrowOnCancel = null;
        clock.Advance(Idle);
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(Day);
        Assert.Equal(ScrubOutcome.Clean, (await ScrubRecords.ReadAsync(folder)).Record!.Outcome);
    }

    [Fact]
    public async Task StartWhileStoppingThrowsAndWorksOnceTheStopEnds()
    {
        scrub.IgnoreCancel = true;
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        var call = await scrub.Next();
        var stopping = schedule.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.Throws<InvalidOperationException>(schedule.Start);
        call.Result.SetResult(CleanReport); // finishes after the stop: not recorded
        await stopping.WaitAsync(ManualClock.Deadline);
        Assert.False(File.Exists(RecordFile));
        scrub.IgnoreCancel = false;
        schedule.Start();
        await clock.NextTimer(First);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AThrowingScrubIsRecordedAsAProblem(bool storeError)
    {
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        Exception error = storeError ? new ObjectStoreException("STORE_BROKEN", "The store is broken.") : new InvalidOperationException("Something went wrong.");
        (await scrub.Next()).Result.SetException(error);
        await clock.NextTimer(Day);

        var record = (await ScrubRecords.ReadAsync(folder)).Record!;
        Assert.Equal(ScrubOutcome.Problem, record.Outcome);
        Assert.Equal(storeError ? "STORE_BROKEN" : typeof(InvalidOperationException).FullName, record.FailureCode);
        Assert.Equal(error.Message, record.FailureMessage);
    }

    [Fact]
    public async Task StopCancelsARunningScrubAndWritesNothing()
    {
        var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        var call = await scrub.Next();
        await schedule.StopAsync().WaitAsync(ManualClock.Deadline);
        Assert.True(call.Token.IsCancellationRequested);
        Assert.False(schedule.Running);
        Assert.False(File.Exists(RecordFile));
        await schedule.StopAsync(); // again: still fine
        await schedule.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(schedule.Start);
    }

    [Fact]
    public async Task StopWhileWaitingEndsTheLoopAndStartRunsItAgain()
    {
        await using var schedule = Schedule();
        schedule.Start();
        await clock.NextTimer(First);
        await schedule.StopAsync().WaitAsync(ManualClock.Deadline);
        schedule.Start();
        await clock.NextTimer(First);
        clock.Advance(First);
        (await scrub.Next()).Result.SetResult(CleanReport);
        await clock.NextTimer(Day);
        Assert.Equal(1, scrub.Calls);
    }

    private sealed class FakeScrub
    {
        private readonly Channel<Call> calls = Channel.CreateUnbounded<Call>();
        private int count;
        public int Calls => Volatile.Read(ref count);
        /// <summary>Calls made while set fail with this when canceled, instead of being canceled.</summary>
        public volatile Exception? ThrowOnCancel;
        /// <summary>Calls made while set ignore cancellation.</summary>
        public volatile bool IgnoreCancel;

        public Task<ScrubReport> Run(IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref count);
            var call = new Call(ct, IgnoreCancel ? null : ThrowOnCancel, IgnoreCancel);
            calls.Writer.TryWrite(call);
            return call.Result.Task;
        }

        public async Task<Call> Next() => await calls.Reader.ReadAsync().AsTask().WaitAsync(ManualClock.Deadline);
    }

    private sealed class Call
    {
        public Call(CancellationToken token, Exception? throwOnCancel, bool ignoreCancel)
        {
            Token = token;
            if (ignoreCancel) return;
            token.Register(() => { if (throwOnCancel is null) Result.TrySetCanceled(token); else Result.TrySetException(throwOnCancel); });
        }

        public CancellationToken Token { get; }
        public TaskCompletionSource<ScrubReport> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
