namespace VibeMiner.Backend.Tests;

/// <summary>Timers fire only from <see cref="Advance"/>. <see cref="Armed"/> lets a test wait until a throttled pump has armed its delay before advancing.</summary>
internal sealed class ManualTimerClock : TimeProvider
{
    private readonly object gate = new(); private readonly List<Timer> timers = []; private readonly List<long> armed = [];
    private readonly List<(Func<long, bool> Match, TaskCompletionSource<long> Done)> waiting = []; private long ticks;
    internal int TimerCount { get { lock (gate) return timers.Count; } }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    { var timer = new Timer(this, callback, state); lock (gate) { timers.Add(timer); timer.Change(dueTime, period); } return timer; }
    /// <summary>Completes with the absolute due tick of the first finite arm that matches, counting arms made before the call.</summary>
    internal Task<long> Armed(Func<long, bool>? match = null)
    {
        match ??= _ => true;
        lock (gate)
        {
            foreach (var due in armed) if (match(due)) return Task.FromResult(due);
            var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously); waiting.Add((match, done)); return done.Task;
        }
    }
    internal void Advance(TimeSpan amount)
    {
        Timer[] ready; lock (gate) { ticks += amount.Ticks; ready = timers.Where(t => t.Due <= ticks).ToArray(); foreach (var timer in ready) timer.Due = long.MaxValue; }
        foreach (var timer in ready) timer.Callback(timer.State);
    }
    // Caller holds gate. Waiters run their continuations asynchronously, so completing them here is safe.
    private void Arm(Timer timer, long due)
    {
        timer.Due = due; if (due == long.MaxValue) return; armed.Add(due);
        foreach (var waiter in waiting.Where(w => w.Match(due)).ToArray()) { waiting.Remove(waiter); waiter.Done.TrySetResult(due); }
    }
    private sealed class Timer(ManualTimerClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal readonly TimerCallback Callback = callback; internal readonly object? State = state; internal long Due = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { lock (clock.gate) clock.Arm(this, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks); return true; }
        public void Dispose() { lock (clock.gate) Due = long.MaxValue; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
