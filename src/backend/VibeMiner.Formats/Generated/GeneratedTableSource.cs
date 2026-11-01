using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Files;

namespace VibeMiner.Formats.Generated;

/// <summary>
/// What a generated table is decoded from, and nothing else, so builds whose DB2 file and definition agree share one table (#187,
/// D350): the DB2 file's content key (CKey, 32 lower hex), the locale (a <see cref="FileLocales"/> name) only when the table has
/// locstring columns (null otherwise), the resolved definition (opaque from the caller, such as <see cref="ResolvedDefinition.Hash"/>
/// of the block the reader selects; 1-512 printable ASCII characters, space included) and the reader limits the rows are decoded
/// with. No build, product or region, except <see cref="EncodingScope"/> for a table read through encrypted BLTE chunks. The limits count through <see cref="ReaderOptions"/>, a SHA-256 of every field taken when the
/// source is made, so any option change files the table apart.
/// </summary>
public sealed record GeneratedTableSource
{
    /// <summary>v2 dropped the build (#187): v1 entries are never found again and are left for a later collection.</summary>
    public const string IdentityFormat = "vibeminer-generated-table v2";
    public const string ReaderRevision = "db2-values-v3";
    public const string RowEncoding = "rows-v1";

    public GeneratedTableSource(string? Locale, string ContentKey, string Definition, Db2Limits ReaderLimits, string? EncodingScope = null)
    {
        ArgumentNullException.ThrowIfNull(ReaderLimits);
        if (Locale is not null && !FileLocales.IsKnown(Locale)) throw new ArgumentException("The locale isn't registered.", nameof(Locale));
        if (ContentKey is not { Length: 32 } || !ContentKey.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            throw new ArgumentException("A content key is 32 lower-case hexadecimal digits.", nameof(ContentKey));
        if (Definition is not { Length: > 0 and <= 512 } || Definition.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("A definition is 1-512 printable ASCII characters, space included.", nameof(Definition));
        if (EncodingScope is not null && (EncodingScope.Length is 0 or > 512 || EncodingScope.Any(c => c is < ' ' or > '~')))
            throw new ArgumentException("An encoding scope is 1-512 printable ASCII characters, space included.", nameof(EncodingScope));
        this.Locale = Locale; this.ContentKey = ContentKey; this.Definition = Definition; this.EncodingScope = EncodingScope;
        this.ReaderLimits = ReaderLimits with { VerifiedRawIntegerRules = ReaderLimits.VerifiedRawIntegerRules is { } rules ? [.. rules] : null };
        ReaderOptions = OptionsHash(this.ReaderLimits);
    }

    /// <summary>The source of a table read with <paramref name="block"/>: the locale counts only when the block has a locstring column.</summary>
    public static GeneratedTableSource For(DbdBlock block, string locale, string contentKey, Db2Limits readerLimits, string? encodingScope = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new(ResolvedDefinition.HasLocstrings(block) ? locale : null, contentKey, ResolvedDefinition.Hash(block), readerLimits, encodingScope);
    }

    /// <summary>Null for a table without locstring columns.</summary>
    public string? Locale { get; }
    public string ContentKey { get; }
    public string Definition { get; }
    /// <summary>
    /// Where a table read through encrypted BLTE chunks (holes or decrypted) is filed, such as the build: which chunks are encrypted
    /// belongs to the build's encoding of the file, not its content key, and a decrypted chunk's bytes depend on its key, so such a
    /// table is never shared past it. A table read from clear chunks only ignores it. Null: such a table can't be stored.
    /// </summary>
    public string? EncodingScope { get; }
    /// <summary>A copy of the limits given; the rules list is copied too.</summary>
    public Db2Limits ReaderLimits { get; }
    /// <summary>SHA-256 (lower hex) of the canonical JSON of <see cref="ReaderLimits"/>: every field in declaration order, rules in order.</summary>
    public string ReaderOptions { get; }

    public void Deconstruct(out string? locale, out string contentKey, out string definition, out Db2Limits readerLimits, out string? encodingScope)
    { locale = Locale; contentKey = ContentKey; definition = Definition; readerLimits = ReaderLimits; encodingScope = EncodingScope; }

    // The limits count through their hash, as in the identity.
    public bool Equals(GeneratedTableSource? other) => other is not null && Locale == other.Locale && ContentKey == other.ContentKey &&
        Definition == other.Definition && ReaderOptions == other.ReaderOptions && EncodingScope == other.EncodingScope;

    public override int GetHashCode() => HashCode.Combine(Locale, ContentKey, Definition, ReaderOptions, EncodingScope);

    /// <summary>The identity text, one ingredient per line; a table without locstrings has "-" for its locale.</summary>
    public string IdentityText => string.Join('\n', IdentityFormat, Locale ?? "-", ContentKey, Definition, "reader-options=" + ReaderOptions,
        ReaderRevision, RowEncoding);

    /// <summary>The identity text of a table read through encrypted chunks: <see cref="IdentityText"/> and "encoding-in=" <see cref="EncodingScope"/>.</summary>
    public string? ScopedIdentityText => EncodingScope is null ? null : IdentityText + "\nencoding-in=" + EncodingScope;

    /// <summary>SHA-256 of <see cref="IdentityText"/>, or of <see cref="ScopedIdentityText"/>: the base every node key is derived from.</summary>
    internal byte[] BaseHash(bool scoped) => SHA256.HashData(Encoding.UTF8.GetBytes(scoped ? ScopedIdentityText! : IdentityText));

    internal TableIdentity Echo(bool scoped) => new(IdentityFormat, Locale, ContentKey, Definition, ReaderOptions, ReaderRevision, RowEncoding,
        scoped ? EncodingScope : null);

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    private static string OptionsHash(Db2Limits limits) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(limits, Canonical)));
}
