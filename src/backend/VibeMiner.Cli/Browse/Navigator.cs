namespace VibeMiner.Cli.Browse;

/// <summary>One open view in the stack: its last content, its filter and where the cursor was (a row, or a text line).</summary>
internal sealed class BrowseFrame(BrowseView view)
{
    public BrowseView View { get; } = view;
    public ViewContent? Content { get; set; }
    public string? Error { get; set; }
    public bool Loading { get; set; }
    public string Filter { get; set; } = "";
    public int Selected { get; set; }
    /// <summary>Set when a key asked for a reload while opening a view on top; Esc back to this view reloads it.</summary>
    public bool Stale { get; set; }

    private bool Filtering => Filter.Length > 0 && !View.FiltersOnLoad;

    /// <summary>The rows the filter lets through (all of them with no filter, or when the view filtered them as it loaded).</summary>
    public IReadOnlyList<BrowseRow> Visible => Content is not { } content ? []
        : !Filtering ? content.Rows : content.Rows.Where(r => r.Matches(Filter)).ToList();

    /// <summary>The text with the filter applied to its lines.</summary>
    public string? VisibleText => Content?.Text is not { } text ? null
        : !Filtering ? text : string.Join('\n', text.Split('\n').Where(l => l.Contains(Filter, StringComparison.OrdinalIgnoreCase)));

    /// <summary>How many places the cursor can be: text lines in a text view, visible rows in a table.</summary>
    public int Count => VisibleText is { } visible ? visible.Split('\n').Length : Visible.Count;
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
        session.Preloads.Changed += OnPreload;
    }

    // Set while a preload redraw is posted and not yet run: progress arrives off the UI thread, many times a second.
    private int preloadPosted;

    private void OnPreload()
    {
        if (Interlocked.Exchange(ref preloadPosted, 1) != 0) return;
        post(() =>
        {
            Volatile.Write(ref preloadPosted, 0);
            if (frames.Count > 0) Raise();
        });
    }

    /// <summary>The status line's preload part: the current view's build, else the session's latest; null for none.</summary>
    public PreloadLine? Preload => (frames.Count > 0 ? Current.View.PreloadLine(session.Preloads) : null) ?? session.Preloads.Status;

    /// <summary>The preload part's whole line; empty for none.</summary>
    public string PreloadStatus => Preload?.Text ?? "";

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
    /// <summary>The question a key asked, open on the status line until <see cref="Answer"/> or <see cref="CancelAsk"/>.</summary>
    public KeyAsk? Asking { get; private set; }
    /// <summary>A one-off line from the last key (an unknown view, a key with nothing to do); cleared by the next key that works.</summary>
    public string? Message { get; private set; }

    /// <summary><c>:name</c>: replaces the stack with that top view. False, with a message, for a name the registry doesn't know.</summary>
    public bool Go(string name)
    {
        var matches = registry.Matching(name);
        if (matches.Count > 1)
        {
            Say($"{name.Trim()} is ambiguous: {string.Join(", ", matches.Select(e => e.Name))}. Type more of the name.");
            return false;
        }
        if (matches is not [var entry])
        {
            Say($"No view {name.Trim()}. Views: {string.Join(", ", registry.Entries.Select(e => e.Name))}.");
            return false;
        }
        var view = entry.Create();
        CancelLoad();
        Message = null;
        Asking = null;
        // Before the stack empties: clearing an ended task's line raises Changed, and a listener reads Current.
        Tasks.ClearEnded();
        session.Preloads.ClearEnded();
        frames.Clear();
        Open(view);
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
        Asking = null;
        Tasks.ClearEnded();
        session.Preloads.ClearEnded();
        Open(drill());
        return true;
    }

    /// <summary>Esc: clears the filter first, then goes back one view. False at the top with no filter.</summary>
    public bool Back()
    {
        Asking = null;
        if (Current.Filter.Length > 0) { SetFilter(""); return true; }
        if (frames.Count < 2) return false;
        CancelLoad();
        frames.RemoveAt(frames.Count - 1);
        Message = null;
        Tasks.ClearEnded();
        session.Preloads.ClearEnded();
        // The view below may have been left mid-load, or a key asked for its reload; it loads again.
        if (Current.Stale || (Current.Content is null && Current.Error is null)) Load(Current);
        else Raise();
        return true;
    }

    /// <summary>Sets the filter; a view that filters on load loads again with it.</summary>
    public void SetFilter(string filter)
    {
        Current.Filter = filter.Trim();
        Current.Selected = 0;
        if (Current.View.FiltersOnLoad) Load(Current);
        else Raise();
    }

    public void Reload() => Load(Current);

    /// <summary>The keys the shell takes before a view's: <c>: / ? q x</c> (Esc and Enter aren't characters).</summary>
    public const string GlobalKeys = ":/?qx";

    /// <summary>The current view's quick keys that count: not a global key, not an action's key, the first of a repeated key.</summary>
    public IReadOnlyList<ViewKey> Keys
    {
        get
        {
            var actions = Current.View.Actions;
            return Current.View.Keys.Where(k => !GlobalKeys.Contains(k.Key) && actions.All(a => a.Key != k.Key))
                .DistinctBy(k => k.Key).ToList();
        }
    }

    /// <summary>The selected visible row of a table, or null (a text view, an empty table).</summary>
    public BrowseRow? SelectedRow => Current.VisibleText is null && Current.Visible is { Count: > 0 } rows && Current.Selected < rows.Count
        ? rows[Math.Max(0, Current.Selected)] : null;

    /// <summary>A view's own key: an action, then a quick key. False when the current view has none for it.</summary>
    public bool TryAction(char key)
    {
        if (Current.View.Actions.FirstOrDefault(a => a.Key == key) is { } action)
        {
            Message = null;
            StartTask(action.Task, action.Run);
            return true;
        }
        if (Keys.FirstOrDefault(k => k.Key == key) is not { } viewKey) return false;
        Message = null;
        Apply(viewKey.Press(SelectedRow));
        return true;
    }

    /// <summary>Enter on the open question: closes it and does what the answer says.</summary>
    public void Answer(string typed)
    {
        if (Asking is not { } ask) return;
        Asking = null;
        Message = null;
        Apply(ask.Answer(typed));
    }

    /// <summary>Esc on the open question: closes it, doing nothing.</summary>
    public void CancelAsk()
    {
        Asking = null;
        Raise();
    }

    /// <summary>
    /// Down or PgDn on the last line of a text view: asks the view to read further and loads it again, the cursor staying where it
    /// is. False when the view has no more or is loading.
    /// </summary>
    public bool More()
    {
        var frame = Current;
        if (frame.Loading || frame.Content?.Text is null || !frame.View.HasMore) return false;
        frame.View.More();
        Message = null;
        Load(frame);
        return true;
    }

    private void Apply(KeyOutcome outcome)
    {
        if (outcome.Say is { } say) Message = say;
        if (outcome.Act?.Invoke(session) is { } said) Message = said;
        if (outcome.Start is { } task) StartTask(task.Name, task.Run);
        if (outcome.Ask is { } ask) Asking = ask;
        if (outcome.Open is { } view)
        {
            Current.Stale |= outcome.Reload;
            CancelLoad();
            Open(view);
        }
        else if (outcome.Reload) Load(Current);
        else Raise();
    }

    private void StartTask(string name, Func<BrowseSession, IProgress<string>, CancellationToken, Task<string>> run) =>
        // Whatever view is showing when it ends reloads, so it shows what the task wrote; the views under it reload when Esc
        // comes back to them (a file stored from its detail shows as stored in the list).
        Tasks.Start(name, (progress, ct) => run(session, progress, ct), () =>
        {
            if (frames.Count == 0) return;
            for (var i = 0; i < frames.Count - 1; i++) frames[i].Stale = true;
            Load(Current);
        });

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
        var keys = Keys;
        if (Current.View.Actions.Count > 0 || keys.Count > 0)
        {
            lines.Add("");
            lines.Add("This view");
            lines.AddRange(Current.View.Actions.Select(a => $"  {a.Key,-9}{a.Help}"));
            lines.AddRange(keys.Select(k => $"  {k.Key,-9}{k.Help}"));
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
        frame.Stale = false;
        frame.View.Filter = frame.Filter;
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
                if (frame.Selected >= frame.Count) frame.Selected = Math.Max(0, frame.Count - 1);
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
        session.Preloads.Changed -= OnPreload;
        CancelLoad();
        Task[] loads;
        lock (running) loads = [.. running, Loading];
        await Task.WhenAll(loads).ConfigureAwait(false);
        await Tasks.DisposeAsync().ConfigureAwait(false);
    }
}
