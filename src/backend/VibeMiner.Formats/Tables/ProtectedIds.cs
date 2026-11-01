namespace VibeMiner.Formats.Tables;

/// <summary>
/// The protected IDs of opened tables, by generated table identity, so a missed row ID tells Protected from Unknown without
/// opening the DB2 file again. Holds at most <see cref="MaxTables"/> tables and <see cref="MaxIds"/> IDs in all, dropping the
/// least recently used; a table with more than <see cref="MaxIds"/> isn't kept (each miss then reopens the file). Thread safe.
/// </summary>
internal sealed class ProtectedIds
{
    public const int MaxTables = 32;
    public const int MaxIds = 1 << 18;

    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Identity, IReadOnlyDictionary<uint, string> Ids)>> byIdentity = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Identity, IReadOnlyDictionary<uint, string> Ids)> recent = new();
    private long held;

    public IReadOnlyDictionary<uint, string>? Get(string identity)
    {
        lock (gate)
        {
            if (!byIdentity.TryGetValue(identity, out var node)) return null;
            recent.Remove(node);
            recent.AddFirst(node);
            return node.Value.Ids;
        }
    }

    public void Put(string identity, IReadOnlyDictionary<uint, string> ids)
    {
        if (ids.Count > MaxIds) return;
        lock (gate)
        {
            if (byIdentity.ContainsKey(identity)) return;
            while (recent.Count > 0 && (recent.Count >= MaxTables || held + ids.Count > MaxIds))
            {
                var last = recent.Last!;
                recent.RemoveLast();
                byIdentity.Remove(last.Value.Identity);
                held -= last.Value.Ids.Count;
            }
            byIdentity[identity] = recent.AddFirst((identity, ids));
            held += ids.Count;
        }
    }
}
