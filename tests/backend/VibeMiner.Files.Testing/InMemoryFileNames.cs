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
    private FakeNames? index;
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
        var sorted = new SortedDictionary<uint, string>();
        foreach (var (id, name) in names)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(names));
            sorted[id] = name;
        }
        var built = new FakeNames(sorted);
        lock (gate) { index = built; count = sorted.Count; state = FileNamesState.Ready; }
        return this;
    }

    /// <summary>The names handles use now, or null when they aren't Ready.</summary>
    internal FakeNames? Current() { lock (gate) return state == FileNamesState.Ready ? index : null; }

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

/// <summary>A fixed FileDataID-to-name map matched by the real rules (<see cref="NameFolding"/>).</summary>
internal sealed class FakeNames(SortedDictionary<uint, string> names)
{
    public string? NameOf(uint id) => names.TryGetValue(id, out var name) ? name : null;

    public IReadOnlyList<uint> IdsOf(string path)
    {
        var query = Encoding.UTF8.GetBytes(path);
        return [.. names.Where(n => NameFolding.SameName(Encoding.UTF8.GetBytes(n.Value), query)).Select(n => n.Key)];
    }
}
