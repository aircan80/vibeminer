namespace VibeMiner.Files.Names;

/// <summary>
/// How community names match (D105): ASCII letters ignore case and <c>/</c> equals <c>\</c>; nothing else is folded, so matching
/// never depends on culture. Listfile paths are ASCII in practice.
/// </summary>
internal static class NameFolding
{
    internal static byte Fold(byte value) => value switch
    {
        >= (byte)'a' and <= (byte)'z' => (byte)(value - 32),
        (byte)'/' => (byte)'\\',
        _ => value,
    };

    internal static char Fold(char value) => value switch
    {
        >= 'a' and <= 'z' => (char)(value - 32),
        '/' => '\\',
        _ => value,
    };

    /// <summary>Whether two UTF-8 names are the same name.</summary>
    internal static bool SameName(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (Fold(a[i]) != Fold(b[i])) return false;
        return true;
    }

    /// <summary>Whether <paramref name="name"/> starts with <paramref name="prefix"/>, folded the same way.</summary>
    public static bool HasPrefix(string name, string prefix)
    {
        if (prefix.Length > name.Length) return false;
        for (var i = 0; i < prefix.Length; i++) if (Fold(name[i]) != Fold(prefix[i])) return false;
        return true;
    }
}
