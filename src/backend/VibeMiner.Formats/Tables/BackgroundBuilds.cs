using VibeMiner.Formats.Generated;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// The generated-table builds a direct read started in the background (D330), shared by every handle of one service. At most
/// <see cref="MaxRunning"/> run or wait for a Background slot at once, one per source; a direct read finding the limit reached
/// still answers, and a later one starts the build. A build that failed is remembered (at most <see cref="MaxFailed"/>, oldest
/// dropped) until a build of that source succeeds: its calls wait for the build again, so they answer the failure the generated
/// path gives. Thread safe.
/// </summary>
internal sealed class BackgroundBuilds
{
    public const int MaxRunning = 8;
    public const int MaxFailed = 64;

    private readonly Lock gate = new();
    private readonly HashSet<GeneratedTableSource> running = [];
    private readonly LinkedList<GeneratedTableSource> failedOrder = new();
    private readonly Dictionary<GeneratedTableSource, LinkedListNode<GeneratedTableSource>> failed = [];

    /// <summary>The builds started and not ended now.</summary>
    public int Running
    {
        get { lock (gate) return running.Count; }
    }

    /// <summary>Whether the last background build of this source failed: its calls take the generated path.</summary>
    public bool HasFailed(GeneratedTableSource source)
    {
        lock (gate) return failed.ContainsKey(source);
    }

    /// <summary>True when the caller should start the source's build: none runs for it and fewer than <see cref="MaxRunning"/> run.</summary>
    public bool TryStart(GeneratedTableSource source)
    {
        lock (gate) return running.Count < MaxRunning && running.Add(source);
    }

    /// <summary>
    /// Ends a started build. <paramref name="failure"/> true records the source as failed; false (stored, or never started for
    /// want of a slot) forgets any earlier failure only when <paramref name="stored"/>.
    /// </summary>
    public void End(GeneratedTableSource source, bool stored, bool failure)
    {
        lock (gate)
        {
            running.Remove(source);
            if (stored) Forget(source);
            else if (failure && !failed.ContainsKey(source))
            {
                if (failed.Count >= MaxFailed) Forget(failedOrder.First!.Value);
                failed[source] = failedOrder.AddLast(source);
            }
        }
    }

    /// <summary>A build of the source succeeded on the generated path.</summary>
    public void Stored(GeneratedTableSource source)
    {
        lock (gate) Forget(source);
    }

    private void Forget(GeneratedTableSource source)
    {
        if (failed.Remove(source, out var node)) failedOrder.Remove(node);
    }
}
