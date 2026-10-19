using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    // These are comparison observations, not reusable artifact/hash normalization. Every original
    // receipt fact (including RetrievedAt/URL) and current PreparedAt remains authoritative.
    private static bool EffectsCompareAuthority<T>(T left, T right)
    {
        var a = EffectsNormalizeAuthority(left);
        var b = EffectsNormalizeAuthority(right);
        // Bound BOTH encodings before constructing either compact token index. The existing SQL
        // aggregate preflight remains unchanged; this is not a new source/input admission policy.
        var first = EffectsBytes(a, EffectsItemsBytes);
        var second = EffectsBytes(b, EffectsItemsBytes);
        return EffectsEqualEncoded(first, second);
    }

    private static bool EffectsEqualEncoded(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second)
    {
        if (first.Length > EffectsItemsBytes || second.Length > EffectsItemsBytes) throw EffectsLimit();
        using var a = JsonDocument.Parse(first, new JsonDocumentOptions { MaxDepth = 32 });
        using var b = JsonDocument.Parse(second, new JsonDocumentOptions { MaxDepth = 32 });
        // Object member order is immaterial; array order, duplicate occurrences and values are exact.
        return JsonElement.DeepEquals(a.RootElement, b.RootElement);
    }

    private static object EffectsNormalizeAuthority(object? value)
    {
        try
        {
            return value switch
            {
                ContentReceipt receipt => receipt with { VerifiedAt = "" },
                MetadataReceipt receipt => receipt with { VerifiedAt = "" },
                ConfigurationReceipt receipt => receipt with { VerifiedAt = "" },
                SelectedFileAssociation association => EffectsNormalizeAssociation(association),
                TableRowsArtifact rows => rows, // This descriptor contains no receipt observation.
                ItemsProjectionArtifact artifact => artifact with
                {
                    Binding = artifact.Binding with
                    { ItemSource = EffectsNormalizeSnapshot(artifact.Binding.ItemSource), SparseSource = EffectsNormalizeSnapshot(artifact.Binding.SparseSource) }
                },
                ItemsSnapshot snapshot => snapshot with
                { ItemSource = EffectsNormalizeSnapshot(snapshot.ItemSource), SparseSource = EffectsNormalizeSnapshot(snapshot.SparseSource) },
                ItemEffectsBridgeArtifact artifact => artifact with
                {
                    Binding = artifact.Binding with
                    { Source = EffectsNormalizeSnapshot(artifact.Binding.Source), Configurations = EffectsNormalizeConfigurations(artifact.Binding.Configurations) }
                },
                IReadOnlyList<ConfigurationReceipt> configurations => EffectsNormalizeConfigurations(configurations),
                _ => throw EffectsItemsInvalid()
            };
        }
        catch (Exception error) when (error is NullReferenceException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        { throw EffectsItemsInvalid(); }
    }

    private static TableDataSnapshot EffectsNormalizeSnapshot(TableDataSnapshot value) => value with
    { Source = value.Source with { VerifiedAt = "" }, Association = EffectsNormalizeAssociation(value.Association) };
    private static SelectedFileAssociation EffectsNormalizeAssociation(SelectedFileAssociation value) => value with
    { EncodingSource = value.EncodingSource with { VerifiedAt = "" }, RootSource = value.RootSource with { VerifiedAt = "" } };
    private static ConfigurationReceipt[] EffectsNormalizeConfigurations(IReadOnlyList<ConfigurationReceipt> values)
    {
        var count = values.Count;
        if (count > 2) throw EffectsLimit();
        if (count != 2) throw EffectsItemsInvalid();
        return [values[0] with { VerifiedAt = "" }, values[1] with { VerifiedAt = "" }];
    }
}
