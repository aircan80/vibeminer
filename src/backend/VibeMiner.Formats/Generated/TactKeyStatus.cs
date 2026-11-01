namespace VibeMiner.Formats.Generated;

/// <summary>
/// Which TACT keys are known. A key name is 16 upper-case hex digits, like <see cref="Db2Section.Key"/>. A fingerprint is opaque
/// that changes with the key value, such as a hash of it, so a corrected wrong key rebuilds what it decoded: 1-128 printable ASCII
/// characters without spaces, and not "?". Null means unknown. Treat one instance as a
/// snapshot: decode and store a table with the same one, so its rows match the key set it is filed under.
/// </summary>
public interface ITactKeyStatus
{
    string? FingerprintOf(string keyName);
}

/// <summary>A fixed set of known keys. <see cref="None"/> knows none.</summary>
public sealed class TactKeyStatus : ITactKeyStatus
{
    private readonly Dictionary<string, string> fingerprints;

    public static TactKeyStatus None { get; } = new(new Dictionary<string, string>());

    public TactKeyStatus(IReadOnlyDictionary<string, string> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        this.fingerprints = new(StringComparer.Ordinal);
        foreach (var (name, fingerprint) in fingerprints)
        {
            TactKeyNames.Check(name, nameof(fingerprints));
            if (!TactKeyNames.IsFingerprint(fingerprint)) throw new ArgumentException("A fingerprint is 1-128 printable ASCII characters without spaces, and not \"?\".", nameof(fingerprints));
            this.fingerprints[name] = fingerprint;
        }
    }

    public string? FingerprintOf(string keyName) => fingerprints.GetValueOrDefault(keyName);
}

internal static class TactKeyNames
{
    public const int MaxKeys = 1024;

    public static bool IsName(string? name) => name is { Length: 16 } && name.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');

    public static void Check(string? name, string parameter)
    {
        if (!IsName(name)) throw new ArgumentException("A TACT key name is 16 upper-case hexadecimal digits.", parameter);
    }

    public static bool IsFingerprint(string? fingerprint) => fingerprint is { Length: > 0 and <= 128 } && fingerprint != "?" && fingerprint.All(c => c is > ' ' and <= '~');
}
