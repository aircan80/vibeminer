using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// The Terminal.Gui window for <c>vm browse</c> (D165): a header with the breadcrumb, a note line, the current view as a table
/// (or its text as a list of lines) and a status line that doubles as the <c>:</c> and <c>/</c> prompt and a view key's question. It only draws
/// <see cref="Navigator"/> and passes keys to it. Global keys are read from the application's keyboard before the focused view,
/// so the table's type-to-search never takes them; while the prompt has focus only Enter and Esc are taken.
/// </summary>
internal sealed class BrowseShell : Window
{
    private readonly IApplication app;
    private readonly Navigator navigator;
    private readonly Label header, note, status;
    private readonly TableView table;
    private readonly ListView text;
    private readonly TextField prompt;
    private char promptMode;
    // What the table or list was last built from, so a status change doesn't rebuild it and move the cursor.
    private (BrowseFrame Frame, ViewContent? Content, string Filter)? built;

    public BrowseShell(IApplication app, Navigator navigator, string start = "store")
    {
        this.app = app;
        this.navigator = navigator;
        Title = "vm browse";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.None;
        header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        note = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 1 };
        table = new TableView { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1), FullRowSelect = true };
        text = new ListView { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1), Visible = false };
        status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        prompt = new TextField { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Visible = false };
        table.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (table.Value is { } selection) navigator.Drill(selection.SelectedCell.Y);
        };
        table.ValueChanged += (_, _) =>
        {
            if (built?.Frame == navigator.Current && table.Value is { } selection) navigator.Current.Selected = selection.SelectedCell.Y;
        };
        text.ValueChanged += (_, _) =>
        {
            if (built?.Frame == navigator.Current && text.Value is { } line) navigator.Current.Selected = line;
        };
        // Product ids and paths have underscores, which a label would otherwise take as a hotkey marker and hide.
        foreach (var label in new[] { header, note, status }) label.HotKeySpecifier = (System.Text.Rune)0xFFFF;
        Add(header, note, table, text, status, prompt);
        navigator.Changed += Render;
        app.Keyboard.KeyDown += OnKey;
        navigator.Go(start);
        Render();
    }

    /// <summary>The status line as shown, for the smoke test.</summary>
    public string StatusText => status.Text;

    /// <summary>The header, note and status lines and a text view's lines as built, for the smoke test's width check.</summary>
    internal IEnumerable<string> ShownLines => [header.Text, note.Text, status.Text, .. textLines];
    private string[] textLines = [];
    /// <summary>The longest question shown; the prompt gets the rest of the line, at least 30 columns at 80.</summary>
    internal const int MaxQuestion = 49;

    private void OnKey(object? sender, Key key)
    {
        if (app.TopRunnableView != this) return;
        if (prompt.Visible)
        {
            if (key == Key.Enter) { key.Handled = true; ClosePrompt(true); }
            else if (key == Key.Esc) { key.Handled = true; ClosePrompt(false); }
            return;
        }
        if (key == Key.Esc) { key.Handled = true; navigator.Back(); return; }
        // The terminal is raw while the browser runs, so Ctrl+C arrives as a key, not a signal.
        if (key == Key.C.WithCtrl) { key.Handled = true; app.RequestStop(); return; }
        // A reply's lines are wider than the screen; the list doesn't scroll sideways itself.
        if (text.Visible && (key == Key.CursorRight || key == Key.CursorLeft))
        {
            key.Handled = true;
            var x = Math.Max(0, text.Viewport.X + (key == Key.CursorRight ? 8 : -8));
            text.Viewport = text.Viewport with { X = x };
            return;
        }
        // Down or PgDn on the last line reads further, when the view has more.
        if (text.Visible && (key == Key.CursorDown || key == Key.PageDown) && text.Value is { } at
            && at >= (text.Source?.Count ?? 0) - 1 && navigator.More())
        {
            key.Handled = true;
            return;
        }
        if (key.IsCtrl || key.IsAlt) return;
        var c = key.AsRune.Value;
        switch (c)
        {
            case ':' or '/': key.Handled = true; OpenPrompt((char)c); return;
            case '?': key.Handled = true; ShowHelp(); return;
            case 'q': key.Handled = true; app.RequestStop(); return;
            case 'x':
                key.Handled = true;
                if (!navigator.Tasks.Cancel()) navigator.Say("No background task is running.");
                return;
        }
        if (c is > ' ' and < 0x7f && navigator.TryAction((char)c)) key.Handled = true;
    }

    private void OpenPrompt(char mode)
    {
        promptMode = mode;
        var label = mode.ToString();
        var initial = mode == '/' ? navigator.Current.Filter : "";
        if (mode == '?' && navigator.Asking is { } ask)
        {
            var question = ask.Question.Length > MaxQuestion ? ask.Question[..(MaxQuestion - 3)] + "..." : ask.Question;
            label = question + " ";
            initial = ask.Initial;
        }
        status.Text = label;
        status.Width = label.Length;
        prompt.X = label.Length;
        prompt.Text = initial;
        prompt.Visible = true;
        prompt.SetFocus();
    }

    private void ClosePrompt(bool submit)
    {
        var typed = prompt.Text ?? "";
        prompt.Visible = false;
        status.Width = Dim.Fill();
        (text.Visible ? (View)text : table).SetFocus();
        if (promptMode == '?')
        {
            if (submit) navigator.Answer(typed);
            else navigator.CancelAsk();
        }
        else if (submit && promptMode == '/') navigator.SetFilter(typed);
        else if (submit && typed.Trim() is "q" or "quit") app.RequestStop();
        else if (submit && typed.Trim().Length > 0) navigator.Go(typed);
        Render();
    }

    private void ShowHelp()
    {
        using var dialog = new Dialog { Title = "Keys (Esc closes)", Width = Dim.Fill(2), Height = Dim.Fill(1) };
        var lines = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        lines.SetSource(new ObservableCollection<string>(navigator.Help().Split('\n')));
        dialog.Add(lines);
        dialog.AddButton(new Button { Text = "OK", IsDefault = true });
        app.Run(dialog);
    }

    private void Render()
    {
        var frame = navigator.Current;
        header.Text = $"vm browse  {navigator.Path}  [{(navigator.Tasks.Running is { } task ? $"writing: {task}" : "read-only")}]  {navigator.Session.Folder.Path}";
        note.Text = frame.Error ?? frame.Content?.Note ?? "";
        var isText = frame.Content?.Text is not null;
        if (built != (frame, frame.Content, frame.Filter))
        {
            built = null;
            text.Visible = isText;
            table.Visible = !isText;
            if (isText)
            {
                var lines = textLines = (frame.VisibleText ?? "").Split('\n');
                text.SetSource(new ObservableCollection<string>(lines));
                text.Value = Math.Clamp(frame.Selected, 0, lines.Length - 1);
                if (!prompt.Visible) text.SetFocus();
            }
            else
            {
                textLines = [];
                var content = frame.Content;
                var rows = frame.Visible;
                table.Table = new RowsSource(content?.Columns.Count > 0 ? [.. content.Columns] : [""], rows);
                table.Update();
                if (rows.Count > 0) table.Value = new TableSelection(new Point(0, Math.Clamp(frame.Selected, 0, rows.Count - 1)));
                if (!prompt.Visible) table.SetFocus();
            }
            built = (frame, frame.Content, frame.Filter);
        }
        // A key's question opens the prompt ('?' marks it; the help key never opens one).
        if (navigator.Asking is not null && !prompt.Visible) OpenPrompt('?');
        if (prompt.Visible) return;
        var parts = new List<string>();
        if (frame.Filter.Length > 0 && frame.View.FiltersOnLoad) parts.Add($"/{frame.Filter} ({frame.Count} rows)");
        else if (frame.Filter.Length > 0) parts.Add($"/{frame.Filter} ({frame.Visible.Count} of {frame.Content?.Rows.Count ?? 0})");
        if (frame.Loading) parts.Add("loading...");
        if (navigator.Message is { } message) parts.Add(message);
        if (navigator.Tasks.Status.Length > 0) parts.Add(navigator.Tasks.Status);
        parts.Add("? keys  q quit");
        status.Text = string.Join("  |  ", parts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            app.Keyboard.KeyDown -= OnKey;
            navigator.Changed -= Render;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Runs the browser in this terminal until <c>q</c> or Ctrl+C (a key in raw mode; <paramref name="ct"/> also stops it), then cancels what's still running
    /// and waits for it. Returns the exit code.
    /// </summary>
    public static async Task<int> RunAsync(BrowseSession session, CancellationToken ct)
    {
        using var app = Application.Create().Init();
        var closed = false;
        void Post(Action action) { if (!Volatile.Read(ref closed)) app.Invoke(() => { if (!closed) action(); }); }
        var navigator = new Navigator(ViewRegistry.Default(), session, Post);
        try
        {
            using var shell = new BrowseShell(app, navigator);
            using var stop = ct.Register(() => Post(app.RequestStop));
            app.Run(shell);
            Volatile.Write(ref closed, true);
        }
        finally
        {
            Volatile.Write(ref closed, true);
            await navigator.DisposeAsync().ConfigureAwait(false);
        }
        return ct.IsCancellationRequested ? VmCommands.Canceled : VmCommands.Ok;
    }

    /// <summary>The rows the table shows; the cells are the model's strings.</summary>
    private sealed class RowsSource(string[] columns, IReadOnlyList<BrowseRow> rows) : ITableSource
    {
        public string[] ColumnNames => columns;
        public int Columns => columns.Length;
        public int Rows => rows.Count;
        public object this[int row, int col] => col < rows[row].Cells.Count ? rows[row].Cells[col] : "";
    }
}
