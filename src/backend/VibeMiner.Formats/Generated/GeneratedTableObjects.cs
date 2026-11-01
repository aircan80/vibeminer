using System.Text.Json;
using VibeMiner.Generated;

namespace VibeMiner.Formats.Generated;

/// <summary>The identity echo stored in every node and table's metadata; checked against the source on open.</summary>
internal sealed record TableIdentity(string Format, string Product, string BuildConfig, string CdnConfig, string Locale, string ContentKey,
    string DefinitionVersion, string ReaderOptions, string Reader, string Rows);

/// <summary>
/// A lookup node (JSON): the identity echo and the sorted key names the table depends on as far as this node knows. Keys equal to
/// the node's own set mean "the table is here"; a strict superset points further down the chain.
/// </summary>
internal sealed record TableNode(string Format, TableIdentity Identity, IReadOnlyList<string> Keys)
{
    public const string FormatName = "vibeminer-generated-table-node v1";
    public const int MaxBytes = 1 << 20;
}

/// <summary>A table's record file metadata (JSON).</summary>
internal sealed record TableMetadata(string Format, TableIdentity Identity, Db2TableHeader Header, IReadOnlyList<Db2Section> Sections,
    Db2Completion Completion, IReadOnlyList<string> KeysUsed)
{
    public const string FormatName = "vibeminer-generated-table-metadata v1";
}

internal static class TableJson
{
    private static readonly JsonSerializerOptions Options = new() { RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>Parses a node or metadata blob; anything malformed throws <see cref="GeneratedStoreException"/> (Corrupt).</summary>
    public static T Decode<T>(ReadOnlySpan<byte> bytes, string what) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw Corrupt($"{what} is null.");
        }
        catch (JsonException e) { throw Corrupt($"{what} isn't valid JSON of its shape.", e); }
        catch (NotSupportedException e) { throw Corrupt($"{what} isn't valid JSON of its shape.", e); }
        catch (ArgumentException e) { throw Corrupt($"{what} isn't valid JSON of its shape.", e); }
    }

    public static GeneratedStoreException Corrupt(string problem, Exception? inner = null) =>
        new(GeneratedStoreCodes.Corrupt, $"Generated table: {problem}", inner);
}
