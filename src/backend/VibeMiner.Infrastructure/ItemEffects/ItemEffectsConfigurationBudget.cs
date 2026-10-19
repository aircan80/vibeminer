using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Optional-effects resource preflight; all configuration grammar remains with the TACT parser.</summary>
internal static class ItemEffectsConfigurationBudget
{
    // All four limits form one parse-phase envelope; native build configurations
    // may contain thousands of small assignments unrelated to selected effects.
    internal const int MaxBuildBytes = 128 << 10;
    internal const int MaxAssignments = 2048;
    internal const int MaxTokens = 8192;
    internal const int MaxSeparators = 16384;

    internal static TactConfiguration ParseBuild(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaxBuildBytes) throw Limit();
        var assignments = 0; var tokens = 0; var separators = 0; var insideToken = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var value = bytes[i];
            // Split must discover even repeated separators which do not start new tokens.
            // Charge comments too, before any UTF8/string/parser allocation.
            if ((value is (byte)' ' or (byte)'\t') && ++separators > MaxSeparators) throw Limit();
            // Equals is also a boundary so compact key=value assignments still charge their key and values.
            // Comments are deliberately counted too; this is a conservative resource bound, not another grammar.
            if (value == (byte)'=')
            {
                if (++assignments > MaxAssignments) throw Limit();
                insideToken = false;
            }
            else if (value is (byte)' ' or >= 9 and <= 13) insideToken = false;
            else if (!insideToken)
            {
                if (++tokens > MaxTokens) throw Limit();
                insideToken = true;
            }
        }
        token.ThrowIfCancellationRequested();
        return TactConfigurationParser.Parse(bytes, token);
    }

    private static DiscoveryException Limit() => new("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded.");
}
