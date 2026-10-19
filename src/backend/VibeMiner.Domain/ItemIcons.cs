namespace VibeMiner.Domain;

public enum ItemIconStatus
{
    Ready, MissingItem, FieldUnavailable, NoIcon, MissingMembership, AmbiguousVariants,
    EncodingUnresolved, Protected, Unsupported, Unavailable, Corrupt
}
public sealed record ItemIconImage(string ArtifactId, int Width, int Height, string ByteLength);
public sealed record ItemIconProblem(string Code, string Message, bool Retryable);
public sealed record ItemIconOutcome(string ItemId, string? FileDataId, ItemIconStatus Status,
    ItemIconImage? Image = null, ItemIconProblem? Problem = null);
public sealed record ItemIconBatch(ItemsContext Context, string ArtifactId, IReadOnlyList<ItemIconOutcome> Outcomes);
public sealed record ItemIconProgress(string Stage, string Unit, long? Completed = null, long? Total = null);
