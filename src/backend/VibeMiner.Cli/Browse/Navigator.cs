namespace VibeMiner.Cli.Browse;

/// <summary>One open view in the stack: its last content, its filter and where the cursor was.</summary>
internal sealed class BrowseFrame(BrowseView view)
{
    public BrowseView View { get; } = view;
    public ViewContent? Content { get; set; }
    public string? Error { get; set; }
    public bool Loading { get; set; }
    public string Filter { get; set; } = "";
    public int Selected { get; set; }

    /// <summary>The rows the filter lets through (all of them with no filter).</summary>
    public IReadOnlyList<BrowseRow> Visible => Content is not { } content ? []
        : Filter.Length == 0 ? content.Rows : content.Rows.Where(r => r.Matches(Filter)).ToList();

    /// <summary>The text with the filter applied to its lines.</summary>
    public string? VisibleText => Content?.Text is not { } text ? null
        : Filter.Length == 0 ? text : string.Join('\n', text.Split('\n').Where(l => l.Contains(Filter, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// The browser's state, without Terminal.Gui: a stack of views, each loaded off the UI thread. Every method is called on the UI
/// thread; a load's result comes back through <c>post</c>. A newer load or leaving the view cancels a load, and a result that
/// arrives after that is dropped. Long work goes to <see cref="Tasks"/>, so browsing goes on while it runs.
/// </summary>
internal sealed class Navigator : IAsyncDisposable
{
    private readonly ViewRegistry registry;
    private readonly BrowseSession session;
    private readonly Action<Action> post;
    private readonly List<BrowseFrame> frames = [];
    private CancellationTokenSource? loadCancel;
    private int generation;
    // Every load still running, canceled ones too, so shutdown waits until none holds the store open.
    private readonly HashSet<Task> running = [];

    public Navigator(ViewRegistry registry, BrowseSession session, Action<Action> post)
    {
        this.registry = registry;
        this.session = session;
        this.post = post;
        Tasks = new BackgroundTasks(post);
        Tasks.Changed += Raise;
    }

    /// <summary>Raised on the UI thread after anything shown changes.</summary>
    public event Action? Changed;

    public BackgroundTasks Tasks { get; }
    public BrowseSession Session => session;
    public ViewRegistry Registry => registry;
    public BrowseFrame Current => frames[^1];
    public IReadOnlyList<BrowseFrame> Frames => frames;
    public string Path => string.Join(" > ", frames.Select(f => f.View.Title));
    /// <summary>The running (or last) load, for tests. Never faults.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;
    /// <summary>A one-off line from the last key (an unknown view, a key with nothing to do); cleared by the next key that works.</summary>
    public string? Message { get; private set; }

    /// <summary><c>:name</c>: replaces the stack with that top view. False, with a message, for a name the registry doesn't know.</summary>
    public bool Go(string name)
    {
        if (registry.Find(name) is not { } entry)
        {
            Say($"No view {name.Trim()}. Views: {string.Join(", ", registry.Entries.Select(e => e.Name))}.");
            return false;
        }
        CancelLoad();
        frames.Clear();
        Message = null;
        Open(entry.Create());
        return true;
    }

    /// <summary>Enter on a visible row: opens what it drills into, if anything.</summary>
    public bool Drill(int visibleIndex)
    {
        var rows = Current.Visible;
        if (visibleIndex < 0 || visibleIndex >= rows.Count || rows[visibleIndex].Drill is not { } drill) return false;
        Current.Selected = visibleIndex;
        CancelLoad();
        Message = null;
        Open(drill());
        return true;
    }

    /// <summary>Esc: clears the filter first, then goes back one view. False at the top with no filter.</summary>
    public bool Back()
    {
        if (Current.Filter.Length > 0) { SetFilter(""); return true; }
        if (frames.Count < 2) return false;
        CancelLoad();
        frames.RemoveAt(frames.Count - 1);
        Message = null;
        // The view below may have been left mid-load; it loads again.
        if (Current.Content is null && Current.Error is null) Load(Current);
        else Raise();
        return true;
    }

    public void SetFilter(string filter)
    {
        Current.Filter = filter.Trim();
        Current.Selected = 0;
        Raise();
    }

    public void Reload() => Load(Current);

    /// <summary>A view's own key. False when the current view has none for it.</summary>
    public bool TryAction(char key)
    {
        if (Current.View.Actions.FirstOrDefault(a => a.Key == key) is not { } action) return false;
        Message = null;
        // Whatever view is showing when it ends reloads, so it shows what the task wrote.
        Tasks.Start(action.Task, (progress, ct) => action.Run(session, progress, ct), () => { if (frames.Count > 0) Load(Current); });
        return true;
    }

    /// <summary>The <c>?</c> text: the keys, then the views, then the current view's own keys.</summary>
    public string Help()
    {
        var lines = new List<string>
        {
            "Keys",
            "  :name    go to a view (a unique prefix works)",
            "  /text    filter the rows (Esc clears it)",
            "  Enter    open the selected row",
            "  Esc      clear the filter, then go back",
            "  arrows, PgUp, PgDn, Home, End   move (Left/Right scroll a reply)",
            "  x        cancel the background task",
            "  ?        this help",
            "  q        quit",
            "",
            "Views",
        };
        lines.AddRange(registry.Entries.Select(e => $"  :{e.Name,-9}{e.Help}"));
        if (Current.View.Actions.Count > 0)
        {
            lines.Add("");
            lines.Add("This view");
            lines.AddRange(Current.View.Actions.Select(a => $"  {a.Key,-9}{a.Help}"));
        }
        lines.Add("");
        lines.Add("Read-only, except where a key says it writes.");
        return string.Join('\n', lines);
    }

    public void Say(string message)
    {
        Message = message;
        Raise();
    }

    private void Open(BrowseView view)
    {
        var frame = new BrowseFrame(view);
        frames.Add(frame);
        Load(frame);
    }

    private void Load(BrowseFrame frame)
    {
        CancelLoad();
        loadCancel = new CancellationTokenSource();
        var token = loadCancel.Token;
        var run = ++generation;
        frame.Loading = true;
        Raise();
        Loading = Task.Run(async () =>
        {
            ViewContent? content = null;
            string? error = null;
            try { content = await frame.View.LoadAsync(session, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e) { error = BrowseSession.Explain(e); }
            post(() =>
            {
                if (run != generation) return;
                frame.Content = content;
                frame.Error = error;
                frame.Loading = false;
                if (frame.Selected >= frame.Visible.Count) frame.Selected = Math.Max(0, frame.Visible.Count - 1);
                Raise();
            });
        }, CancellationToken.None);
        lock (running) running.Add(Loading);
        var mine = Loading;
        _ = mine.ContinueWith(t => { lock (running) running.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CancelLoad()
    {
        generation++;
        loadCancel?.Cancel();
        loadCancel?.Dispose();
        loadCancel = null;
        foreach (var f in frames) f.Loading = false;
    }

    private void Raise() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        CancelLoad();
        Task[] loads;
        lock (running) loads = [.. running, Loading];
        await Task.WhenAll(loads).ConfigureAwait(false);
        await Tasks.DisposeAsync().ConfigureAwait(false);
    }
}
