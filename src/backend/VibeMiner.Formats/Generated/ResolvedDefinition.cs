using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeMiner.Formats.Generated;

/// <summary>
/// The definition a table's rows are decoded with, as the generated store files it (#187, D350): the fields of the one block the
/// reader selects, in order. Each field counts with everything that shapes the decoded rows and header: column name, family,
/// reference, the verified-name mark (an unverified int stays raw, and raw-integer rules and relations follow it), integer bits,
/// signedness, array length, and the id, noninline and relation flags. Which builds or layouts select the block and comments don't
/// change a row, so they don't count: two builds, or two definition revisions, that read a file with the same fields share its table.
/// </summary>
public static class ResolvedDefinition
{
    public const string Format = "vibeminer-resolved-definition v1";

    private static readonly ConditionalWeakTable<DbdBlock, string> Hashes = new();

    /// <summary>SHA-256 (lower hex) of the format line and one canonical JSON array per field.</summary>
    public static string Hash(DbdBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return Hashes.GetValue(block, Compute);
    }

    public static bool HasLocstrings(DbdBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return block.Fields.Any(field => field.Column.Family == "locstring");
    }

    private static string Compute(DbdBlock block)
    {
        var text = new StringBuilder(Format).Append('\n');
        foreach (var field in block.Fields)
        {
            var column = field.Column;
            object?[] line = [column.Name, column.Family, column.Reference?.Table, column.Reference?.Column, column.VerifiedName, field.IntegerBits, field.Unsigned,
                field.ArrayLength, field.IsId, field.NonInline, field.RelationStorage];
            text.Append(JsonSerializer.Serialize(line)).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
