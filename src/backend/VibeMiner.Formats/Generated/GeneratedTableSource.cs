using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Builds;
using VibeMiner.Files;

namespace VibeMiner.Formats.Generated;

/// <summary>
/// What a generated table is decoded from: the build (its region doesn't count), the locale (a <see cref="FileLocales"/> name),
/// the DB2 file's content key (CKey, 32 lower hex), the definition version (opaque from the caller, such as the definition file's
/// SHA-256 plus the selected layout; 1-512 printable ASCII characters, space included) and the reader limits the rows are decoded
/// with. The limits count through <see cref="ReaderOptions"/>, a SHA-256 of every field taken when the source is made, so any
/// option change files the table apart.
/// </summary>
public sealed record GeneratedTableSource
{
    public const string IdentityFormat = "vibeminer-generated-table v1";
    public const string ReaderRevision = "db2-values-v2";
    public const string RowEncoding = "rows-v1";

    public GeneratedTableSource(BuildRef Build, string Locale, string ContentKey, string DefinitionVersion, Db2Limits ReaderLimits)
    {
        ArgumentNullException.ThrowIfNull(Build);
        ArgumentNullException.ThrowIfNull(ReaderLimits);
        if (!FileLocales.IsKnown(Locale)) throw new ArgumentException("The locale isn't registered.", nameof(Locale));
        if (ContentKey is not { Length: 32 } || !ContentKey.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            throw new ArgumentException("A content key is 32 lower-case hexadecimal digits.", nameof(ContentKey));
        if (DefinitionVersion is not { Length: > 0 and <= 512 } || DefinitionVersion.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("A definition version is 1-512 printable ASCII characters, space included.", nameof(DefinitionVersion));
        this.Build = Build; this.Locale = Locale; this.ContentKey = ContentKey; this.DefinitionVersion = DefinitionVersion;
        this.ReaderLimits = ReaderLimits with { VerifiedRawIntegerRules = ReaderLimits.VerifiedRawIntegerRules is { } rules ? [.. rules] : null };
        ReaderOptions = OptionsHash(this.ReaderLimits);
    }

    public BuildRef Build { get; }
    public string Locale { get; }
    public string ContentKey { get; }
    public string DefinitionVersion { get; }
    /// <summary>A copy of the limits given; the rules list is copied too.</summary>
    public Db2Limits ReaderLimits { get; }
    /// <summary>SHA-256 (lower hex) of the canonical JSON of <see cref="ReaderLimits"/>: every field in declaration order, rules in order.</summary>
    public string ReaderOptions { get; }

    public void Deconstruct(out BuildRef build, out string locale, out string contentKey, out string definitionVersion, out Db2Limits readerLimits)
    { build = Build; locale = Locale; contentKey = ContentKey; definitionVersion = DefinitionVersion; readerLimits = ReaderLimits; }

    // Region is left out of equality as it is out of the identity.
    public bool Equals(GeneratedTableSource? other) => other is not null && Build.Product == other.Build.Product &&
        Build.BuildConfig == other.Build.BuildConfig && Build.CdnConfig == other.Build.CdnConfig && Locale == other.Locale &&
        ContentKey == other.ContentKey && DefinitionVersion == other.DefinitionVersion && ReaderOptions == other.ReaderOptions;

    public override int GetHashCode() => HashCode.Combine(Build.Product, Build.BuildConfig, Build.CdnConfig, Locale, ContentKey, DefinitionVersion, ReaderOptions);

    /// <summary>The identity text, one ingredient per line.</summary>
    public string IdentityText => string.Join('\n', IdentityFormat, Build.Product, Build.BuildConfig, Build.CdnConfig, Locale, ContentKey,
        DefinitionVersion, "reader-options=" + ReaderOptions, ReaderRevision, RowEncoding);

    /// <summary>SHA-256 of <see cref="IdentityText"/>: the base every node key is derived from.</summary>
    internal byte[] BaseHash() => SHA256.HashData(Encoding.UTF8.GetBytes(IdentityText));

    internal TableIdentity Echo() => new(IdentityFormat, Build.Product, Build.BuildConfig, Build.CdnConfig, Locale, ContentKey, DefinitionVersion,
        ReaderOptions, ReaderRevision, RowEncoding);

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    private static string OptionsHash(Db2Limits limits) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(limits, Canonical)));
}
