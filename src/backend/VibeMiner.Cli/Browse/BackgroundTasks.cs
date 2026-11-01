namespace VibeMiner.Cli.Browse;

/// <summary>
/// The one long task <c>vm browse</c> runs at a time (a refresh today), off the UI thread, with a status line. Every state change is
/// posted to the UI thread through <c>post</c>. Terminal outcomes: the task's own last line, "canceled" or "failed: ...". Cancel
/// stops it at its next await; a second start while one runs is refused with a line saying so.
/// </summary>
internal sealed class BackgroundTasks(Action<Action> post) : IAsyncDisposable
{
    private CancellationTokenSource? cancel;
    private int generation;

    /// <summary>Raised on the UI thread after <see cref="Running"/> or <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The running task's name, or null.</summary>
    public string? Running { get; private set; }
    /// <summary>The running task's progress, or the last task's outcome; empty before any.</summary>
    public string Status { get; private set; } = "";
    /// <summary>The running (or last) task, for tests and shutdown. Never faults.</summary>
    public Task Current { get; private set; } = Task.CompletedTask;

    /// <summary>Starts <paramref name="work"/> unless a task is running. <paramref name="done"/> runs on the UI thread after it ends, however it ends.</summary>
    public bool Start(string name, Func<IProgress<string>, CancellationToken, Task<string>> work, Action? done = null)
    {
        if (Running is not null)
        {
            Set(Running, $"{Running} is already running; x cancels it.");
            return false;
        }
        // The last task has ended (Running is null), so its source can go.
        cancel?.Dispose();
        var token = (cancel = new CancellationTokenSource()).Token;
        var run = ++generation;
        Set(name, $"{name}: starting");
        var progress = new Line(line => post(() => { if (run == generation && Running is not null) Set(Running, $"{name}: {line}"); }));
        Current = Task.Run(async () =>
        {
            string outcome;
            try { outcome = await work(progress, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { outcome = $"{name} canceled."; }
            catch (Exception e) { outcome = $"{name} failed: {BrowseSession.Explain(e)}"; }
            post(() =>
            {
                if (run != generation) return;
                Set(null, outcome);
                done?.Invoke();
            });
        }, CancellationToken.None);
        return true;
    }

    /// <summary>Asks the running task to stop. False when none runs.</summary>
    public bool Cancel()
    {
        if (Running is null || cancel is null) return false;
        cancel.Cancel();
        Set(Running, $"{Running}: canceling");
        return true;
    }

    private void Set(string? running, string status)
    {
        Running = running;
        Status = status;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        cancel?.Cancel();
        await Current.ConfigureAwait(false);
        cancel?.Dispose();
    }

    /// <summary>Progress that posts every line; <see cref="Progress{T}"/> would capture whatever context started it.</summary>
    private sealed class Line(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
