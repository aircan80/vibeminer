using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>Explicit isolated acquisition over one borrowed library and one shared physical pipeline.</summary>
public interface ILibraryObjectAccess : IAsyncDisposable
{
    ILibraryObjectSource ForOwner(string owner, LibraryOwnerScope scope, string producer);
    StreamScheduler Streams { get; }
}
public interface ILibraryObjectSource : IObjectSource, IStoreCommits, IAsyncDisposable { }
public sealed record LibraryObjectAccessOptions
{
    public int MaxViews { get; init; } = 32;
    public int MaxOperations { get; init; } = 64;
    public int MaxBatch { get; init; } = 1024;
    public int MaxFlights { get; init; } = 128;
    public int MaxKnownDamage { get; init; } = 1024;
    public int MaxConsumersPerFlight { get; init; } = 32;
    public int MaxSubscriptions { get; init; } = 64;
    public int MaxSubscriptionCapacity { get; init; } = 1024;
    public int MaxStageRetargets { get; init; } = 4;
}
public static class LibraryObjectAccessCodes
{
    public const string Closed = "LIBRARY_ACQUISITION_CLOSED", Busy = "LIBRARY_ACQUISITION_BUSY", Limit = "LIBRARY_ACQUISITION_LIMIT";
    public const string Corrupt = "LIBRARY_ACQUISITION_CORRUPT", Lost = "LIBRARY_ACQUISITION_LOST", DamageLimit = "LIBRARY_ACQUISITION_DAMAGE_LIMIT";
    public const string ContextUntried = "LIBRARY_ACQUISITION_CONTEXT_UNTRIED";
    public const string ObservationsUnsupported = "LIBRARY_ACQUISITION_OBSERVATIONS_UNSUPPORTED";
}
public sealed class LibraryObjectAccessException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

internal interface ILibraryDamagePolicy
{
    ObjectResult? Known(ObjectKey key);
    ObjectResult Mark(ObjectKey key, ReadState state, string? problem);
}
