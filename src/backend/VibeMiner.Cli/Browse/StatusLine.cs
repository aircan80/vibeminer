namespace VibeMiner.Cli.Browse;

/// <summary>
/// A table preload's status line part: <c>preload</c>, the version, the locale unless it is enUS, then what it says
/// (<see cref="Body"/>, its stage and counts or its end). <see cref="Running"/> while the preload works. <see cref="Fit"/> cuts the
/// version first and the body only when nothing else is left.
/// </summary>
internal sealed record PreloadLine(string Label, string Locale, string Body, bool Running)
{
    private string LocalePart => Locale == FilesView.DefaultLocale ? "" : $" {Locale}";

    /// <summary>The whole line, e.g. <c>preload 1.15.9.70576: preparing 310/1132 tables</c>.</summary>
    public string Text => $"preload {Label}{LocalePart}: {Body}";

    /// <summary>The shortest form that still says the body whole: the version left out.</summary>
    public string Shortest => $"preload{LocalePart}: {Body}";

    /// <summary>The line in at most <paramref name="width"/> columns: whole, or the version cut from its front, then left out.</summary>
    public string Fit(int width)
    {
        var text = Text;
        if (text.Length <= width) return text;
        // Keep the version's end (the build number) and at least four of its characters behind "...".
        var keep = width - Shortest.Length - " ...".Length;
        if (keep >= 4 && keep < Label.Length) return $"preload ...{Label[^keep..]}{LocalePart}: {Body}";
        return StatusLine.Clip(Shortest, width);
    }

    public override string ToString() => Text;
}

/// <summary>
/// Composes <c>vm browse</c>'s status line in the screen's width (#159). Running work's progress comes first, a running preload
/// ahead of a running task, then the filter, loading and message, an ended preload, an ended task's line and the keys hint; what
/// comes later is what gets cut.
/// A running preload keeps at least its <see cref="PreloadLine.Shortest"/> form, so its stage and counts always show.
/// </summary>
internal static class StatusLine
{
    public const string Separator = "  |  ";
    public const string KeysHint = "? keys  q quit";

    /// <param name="width">The screen's columns.</param>
    /// <param name="task">The background task's line, or empty.</param>
    /// <param name="taskRunning">Whether that task is still running.</param>
    /// <param name="preload">The preload part, or null.</param>
    /// <param name="others">The other parts in their order (filter, loading, message).</param>
    public static string Compose(int width, string task, bool taskRunning, PreloadLine? preload, IReadOnlyList<string> others)
    {
        width = Math.Max(1, width);
        var parts = new List<string>();
        var used = 0;
        void Add(string text)
        {
            if (text.Length == 0) return;
            var room = width - used - (parts.Count > 0 ? Separator.Length : 0);
            if (room <= 0) return;
            text = Clip(text, room);
            used += (parts.Count > 0 ? Separator.Length : 0) + text.Length;
            parts.Add(text);
        }

        var livePreload = preload is { Running: true };
        if (livePreload) Add(preload!.Fit(width));
        if (taskRunning) Add(task);
        foreach (var other in others) Add(other);
        // An ended preload's line before an ended task's: the preload m starts ends after the m task, and P's stop is newer
        // than either.
        if (preload is not null && !livePreload)
        {
            var room = width - used - (parts.Count > 0 ? Separator.Length : 0);
            if (room > 0) Add(preload.Fit(room));
        }
        if (!taskRunning) Add(task);
        Add(KeysHint);
        return string.Join(Separator, parts);
    }

    /// <summary>Cuts a line to <paramref name="width"/> columns, ending in "..." when there is room for it.</summary>
    internal static string Clip(string line, int width)
    {
        if (line.Length <= width) return line;
        if (width <= 3) return line[..Math.Max(0, width)];
        return line[..(width - 3)] + "...";
    }
}
