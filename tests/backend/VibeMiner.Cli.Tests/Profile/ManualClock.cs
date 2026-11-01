using System.Threading.Channels;

namespace VibeMiner.Cli.Tests.Profile;

/// <summary>A clock that moves only when a test advances it. Each timer it creates is announced, so a test can wait for the code under test to start a wait.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly List<Timer> timers = [];
    private readonly Channel<TimeSpan> created = Channel.CreateUnbounded<TimeSpan>();
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override DateTimeOffset GetUtcNow() => start.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        lock (gate) timers.Add(timer);
        created.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>Waits until a timer with this due time is created, skipping others.</summary>
    public async Task NextTimer(TimeSpan due)
    {
        while (await created.Reader.ReadAsync().AsTask().WaitAsync(Deadline) != due) { }
    }

    /// <summary>Timers that are armed: created, not yet fired (one-shot) and not disposed.</summary>
    public int Pending { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }

    public void Advance(TimeSpan by)
    {
        Timer[] ready;
        lock (gate)
        {
            ticks += by.Ticks;
            ready = timers.Where(timer => timer.Due <= ticks).ToArray();
            foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
        }
        foreach (var timer in ready) timer.Callback(timer.State);
    }

    private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal readonly TimerCallback Callback = callback;
        internal readonly object? State = state;
        internal long Due = long.MaxValue;
        internal TimeSpan Period;
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                Period = period;
                return true;
            }
        }

        public void Dispose() { lock (clock.gate) { disposed = true; Due = long.MaxValue; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
