using System.Text;

namespace VibeMiner.Files.Names;

/// <summary>
/// The names of one parsed listfile (D105), in memory and read-only, so safe to share between threads. Names sit in
/// one UTF-8 blob with one end offset per name, beside the ascending FileDataIDs; a name becomes a string only when
/// looked up. Paths are matched ignoring ASCII case, with <c>/</c> and <c>\</c> equal, through a sorted array of
/// 64-bit FNV-1a hashes of the normalised name, its offset basis seeded at random per process; a hash hit is
/// confirmed by comparing the normalised bytes, so collisions can't give a wrong answer. Several FileDataIDs can share a
/// normalised name; <see cref="IdsOf"/> returns them all and the caller picks.
/// Only ASCII letters are folded: listfile paths are ASCII in practice, and folding must not depend on culture.
/// </summary>
internal sealed class ListfileIndex
{
    /// <summary>A hash of a name that must ignore ASCII case and slash direction, like <see cref="Hash"/>.</summary>
    internal delegate ulong NameHasher(ReadOnlySpan<byte> name);

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    // Per process, so nobody can author names that all land in one hash run of a running engine.
    private static readonly ulong Seed = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    private static readonly UTF8Encoding Strict = new(false, true);
    private readonly uint[] ids;
    private readonly int[] ends;
    private readonly byte[] blob;
    private readonly ulong[] hashes;
    // Entry positions parallel to hashes; within one hash, ascending, so the lowest FileDataID is met first.
    private readonly int[] byHash;
    private readonly NameHasher hasher;

    /// <summary>Takes ownership of the arrays; only the first <paramref name="count"/> entries are used.
    /// <paramref name="hasher"/> replaces the name hash, for collision tests.</summary>
    internal ListfileIndex(uint[] ids, int[] ends, byte[] blob, int count, NameHasher? hasher = null)
    {
        this.ids = ids; this.ends = ends; this.blob = blob;
        this.hasher = hasher ?? Hash;
        Count = count;
        hashes = new ulong[count];
        byHash = new int[count];
        for (var i = 0; i < count; i++) { hashes[i] = this.hasher(Name(i)); byHash[i] = i; }
        Array.Sort(hashes, byHash);
        // Array.Sort isn't stable: put each run of equal hashes back in entry order.
        for (var i = 0; i < count;)
        {
            var j = i + 1;
            while (j < count && hashes[j] == hashes[i]) j++;
            if (j - i > 1) Array.Sort(byHash, i, j - i);
            i = j;
        }
    }

    public int Count { get; }

    /// <summary>The name of a FileDataID, or null when the listfile doesn't name it.</summary>
    public string? NameOf(uint fileDataId)
    {
        var at = Array.BinarySearch(ids, 0, Count, fileDataId);
        return at < 0 ? null : Decode(at);
    }

    /// <summary>Every FileDataID named <paramref name="path"/> (ASCII case and slash direction ignored), ascending; empty when none.</summary>
    public IReadOnlyList<uint> IdsOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] query;
        try { query = Strict.GetBytes(path); }
        catch (EncoderFallbackException) { return []; }
        if (query.Length is 0 or > ListfileParser.MaxNameBytes) return [];
        var hash = hasher(query);
        List<uint>? found = null;
        // Within one hash, entries are in entry order, so matches come out ascending.
        for (var at = LowerBound(hash); at < Count && hashes[at] == hash; at++)
            if (SameName(Name(byHash[at]), query)) (found ??= []).Add(ids[byHash[at]]);
        return found ?? (IReadOnlyList<uint>)[];
    }

    /// <summary>Whether <paramref name="name"/> starts with <paramref name="prefix"/>, ignoring ASCII case and slash direction,
    /// as <see cref="IdsOf"/> matches.</summary>
    public static bool HasPrefix(string name, string prefix)
    {
        if (prefix.Length > name.Length) return false;
        for (var i = 0; i < prefix.Length; i++) if (Fold(name[i]) != Fold(prefix[i])) return false;
        return true;
    }

    /// <summary>Every named FileDataID from <paramref name="fromId"/> up, ascending, with its name.</summary>
    public IEnumerable<(uint Id, string Name)> From(uint fromId)
    {
        var at = Array.BinarySearch(ids, 0, Count, fromId);
        if (at < 0) at = ~at;
        for (; at < Count; at++) yield return (ids[at], Decode(at));
    }

    private ReadOnlySpan<byte> Name(int at) => blob.AsSpan(at == 0 ? 0 : ends[at - 1], ends[at] - (at == 0 ? 0 : ends[at - 1]));

    // The parser checked every name as strict UTF-8.
    private string Decode(int at) => Encoding.UTF8.GetString(Name(at));

    private int LowerBound(ulong hash)
    {
        int lo = 0, hi = Count;
        while (lo < hi) { var mid = (int)((uint)(lo + hi) >> 1); if (hashes[mid] < hash) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static byte Fold(byte value) => value switch
    {
        >= (byte)'a' and <= (byte)'z' => (byte)(value - 32),
        (byte)'/' => (byte)'\\',
        _ => value,
    };

    private static char Fold(char value) => value switch
    {
        >= 'a' and <= 'z' => (char)(value - 32),
        '/' => '\\',
        _ => value,
    };

    internal static ulong Hash(ReadOnlySpan<byte> name)
    {
        var hash = FnvOffset ^ Seed;
        foreach (var value in name) hash = (hash ^ Fold(value)) * FnvPrime;
        return hash;
    }

    private static bool SameName(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (Fold(a[i]) != Fold(b[i])) return false;
        return true;
    }
}
