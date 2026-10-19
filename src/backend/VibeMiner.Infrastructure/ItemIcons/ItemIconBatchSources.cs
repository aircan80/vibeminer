namespace VibeMiner.Infrastructure;

/// <summary>Conservative retained-association allowance, including cached PNG regeneration.
/// An over-limit optional target is refused whole; membership variants are never truncated.</summary>
internal sealed class ItemIconBatchSources
{
    internal const int MaximumMemberships = 32_768;
    internal const long MaximumBytes = 16L << 20;
    private const long SourceCharge = 16_384, MembershipCharge = 512;
    private int memberships;
    private long bytes;
    internal bool TryAdmit(AssetResolutionResult result)
    {
        var count = result.Association.Memberships.Count;
        var charge = checked(SourceCharge + (long)count * MembershipCharge);
        if (count > MaximumMemberships - memberships || charge > MaximumBytes - bytes) return false;
        memberships += count; bytes += charge; return true;
    }
}
