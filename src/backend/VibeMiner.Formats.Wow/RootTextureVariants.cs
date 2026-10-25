namespace VibeMiner.Formats.Wow;

/// <summary>The standard-resolution variant chosen for a texture FileDataID whose ROOT memberships disagree only in the
/// HighResTexture bit, and the high-resolution variant left out. <see cref="Selected"/> is every membership of the chosen
/// content key, in ROOT order, LowViolence copies included.</summary>
public sealed record RootTextureVariantChoice(string ContentKey, uint ContentFlags, IReadOnlyList<RootTargetMatch> Selected,
    string HighResolutionContentKey, uint HighResolutionContentFlags);

/// <summary>D095. Geometry textures only: every other asset keeps the exact all-variants-consistent rule.</summary>
public static class RootTextureVariants
{
    public const string Policy = "root-texture-standard-resolution-v1";
    /// <summary>Content bit 0x1, named HighResTexture by the community TACT notes: the optional high-resolution asset pack.</summary>
    public const uint HighResTexture = 0x1;
    public const uint LowViolence = 0x80;
    private const uint Paired = HighResTexture | LowViolence;

    /// <summary>Returns the standard (0x1 clear) variant when, and only when: every membership is the same FileDataID with one
    /// locale mask and the same content flags (derived and raw) apart from 0x1 and 0x80; there are exactly two content keys, one
    /// held only by memberships with 0x1 set and the other only by memberships with it clear; and each of the two has a
    /// membership without 0x80, so LowViolence entries are copies of one of them. Anything else returns null and stays ambiguous.</summary>
    public static RootTextureVariantChoice? ChooseStandard(IReadOnlyList<RootTargetMatch> memberships)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        if (memberships.Count < 2) return null;
        var first = memberships[0]?.Entry;
        if (first?.RawContentFlags is null) return null;
        string? standard = null, high = null; uint? standardFlags = null, highFlags = null;
        foreach (var match in memberships)
        {
            var entry = match?.Entry;
            if (entry?.RawContentFlags is not { } raw || entry.FileDataId != first.FileDataId || entry.LocaleFlags != first.LocaleFlags ||
                (entry.ContentFlags & ~Paired) != (first.ContentFlags & ~Paired) || (raw.First & ~Paired) != (first.RawContentFlags.First & ~Paired) ||
                (raw.Second & ~Paired) != (first.RawContentFlags.Second & ~Paired) || raw.Third != first.RawContentFlags.Third) return null;
            var isHigh = (entry.ContentFlags & HighResTexture) != 0;
            var known = isHigh ? high : standard;
            if (known is not null && known != entry.ContentKey) return null;
            if (isHigh) high = entry.ContentKey; else standard = entry.ContentKey;
            if ((entry.ContentFlags & LowViolence) == 0)
            {
                if (isHigh) highFlags ??= entry.ContentFlags; else standardFlags ??= entry.ContentFlags;
            }
        }
        if (standard is null || high is null || standard == high || standardFlags is null || highFlags is null) return null;
        var selected = memberships.Where(m => (m.Entry.ContentFlags & HighResTexture) == 0).ToArray();
        return new(standard, standardFlags.Value, Array.AsReadOnly(selected), high, highFlags.Value);
    }
}
