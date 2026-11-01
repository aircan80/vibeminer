using System.Text;
using VibeMiner.Files.Names;

namespace VibeMiner.Files.Testing;

/// <summary>
/// The <see cref="IFileNames"/> of <see cref="InMemoryFileSystem"/>: a settable FileDataID-to-name map, matched by the real
/// rules (ASCII case and slash direction ignored), and a settable state. Handles see a change on their next call. Names
/// serve only while <see cref="State"/> is <see cref="FileNamesState.Ready"/>; <see cref="UpdateAsync"/> fetches nothing and
/// only reports.
/// </summary>
public sealed class InMemoryFileNames : IFileNames
{
    private readonly Lock gate = new();
    private FileNamesState state = FileNamesState.Off;
    private ListfileIndex? index;
    private int count;

    /// <summary>The state <see cref="StatusAsync"/> reports. Off by default.</summary>
    public FileNamesState State { get { lock (gate) return state; } set { lock (gate) state = value; } }

    /// <summary>The code <see cref="StatusAsync"/> reports with a state that isn't Ready (the last failed attempt's).</summary>
    public string? Code { get { lock (gate) return code; } set { lock (gate) code = value; } }
    private string? code;

    /// <summary>How many times <see cref="UpdateAsync"/> was called.</summary>
    public int Updates { get; private set; }

    /// <summary>Replaces the names and makes them <see cref="FileNamesState.Ready"/>.</summary>
    public InMemoryFileNames Use(IReadOnlyDictionary<uint, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var sorted = names.OrderBy(n => n.Key).ToArray();
        var ids = new uint[sorted.Length];
        var ends = new int[sorted.Length];
        var blob = new List<byte>();
        for (var i = 0; i < sorted.Length; i++)
        {
            ArgumentException.ThrowIfNullOrEmpty(sorted[i].Value, nameof(names));
            ids[i] = sorted[i].Key;
            blob.AddRange(Encoding.UTF8.GetBytes(sorted[i].Value));
            ends[i] = blob.Count;
        }
        var built = new ListfileIndex(ids, ends, [.. blob], sorted.Length);
        lock (gate) { index = built; count = sorted.Length; state = FileNamesState.Ready; }
        return this;
    }

    /// <summary>The names handles use now, or null when they aren't Ready.</summary>
    internal ListfileIndex? Current() { lock (gate) return state == FileNamesState.Ready ? index : null; }

    public Task<FileNamesStatus> StatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(Status());
    }

    public Task<FileNamesStatus> UpdateAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) { Updates++; return Task.FromResult(Status()); }
    }

    private FileNamesStatus Status() => state == FileNamesState.Ready ? new(state, "fake", Count: count) : new(state, Code: code);
}
