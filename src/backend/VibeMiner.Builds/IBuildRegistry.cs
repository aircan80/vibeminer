namespace VibeMiner.Builds;

public interface IBuildRegistry : IAsyncDisposable
{
    Task<BuildRegistryEntry> ObserveAsync(BuildRegistryObservation observation, CancellationToken ct = default);
    Task<BuildRegistryAttempt> BeginLoadAsync(string buildId, BuildRef selection, string locale, CancellationToken ct = default);
    Task<BuildRegistryEntry> FinishAsync(BuildRegistryAttempt attempt, BuildLoadState state, BuildRegistryDiagnostic? diagnostic = null, CancellationToken ct = default);
    Task<BuildRegistryEntry?> GetAsync(string buildId, CancellationToken ct = default);
    Task<BuildRegistryPage> ListAsync(int limit = 128, BuildRegistryCursor? after = null, CancellationToken ct = default);
}

public enum BuildLoadState { Discovered, Loading, Loaded, Failed, Interrupted }
public sealed record BuildRegistryObservation(BuildIdentity Identity, BuildRef Selection, string Locale, DateTimeOffset ObservedAt);
public sealed record BuildRegistryDiagnostic(string Code, string Message);
public sealed record BuildRegistryEntry(BuildIdentity Identity, BuildRef ObservedBuild, string ObservedLocale,
    DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt, BuildLoadState State, long Generation, DateTimeOffset UpdatedAt,
    BuildRef? AttemptBuild, string? AttemptLocale, DateTimeOffset? AttemptStartedAt, DateTimeOffset? FinishedAt, BuildRegistryDiagnostic? Diagnostic);
public sealed record BuildRegistryPage(IReadOnlyList<BuildRegistryEntry> Entries, BuildRegistryCursor? Next);

public sealed record BuildRegistryAttempt
{
    internal BuildRegistryAttempt(string registryId, string buildId, long generation, string nonce)
    { RegistryId = registryId; BuildId = buildId; Generation = generation; Nonce = nonce; }
    public string BuildId { get; }
    public long Generation { get; }
    internal string RegistryId { get; }
    internal string Nonce { get; }
}
public sealed record BuildRegistryCursor
{
    internal BuildRegistryCursor(string registryId, string afterId) { RegistryId = registryId; AfterId = afterId; }
    public string AfterId { get; }
    internal string RegistryId { get; }
}
public static class BuildRegistryLimits
{
    public const int MaxFields = 64, MaxFieldNameBytes = 128, MaxFieldValueBytes = 2048, MaxIdentityBytes = 32768;
    public const int MaxDiagnosticCodeBytes = 128, MaxDiagnosticMessageBytes = 4096, MaxPage = 1024, MaxLocaleBytes = 32;
    public const int MaxEntryBytes = 512 << 10, MaxPageBytes = 8 << 20;
}
public static class BuildRegistryCodes
{
    public const string Validation = "BUILD_REGISTRY_VALIDATION", Busy = "BUILD_REGISTRY_BUSY", Closed = "BUILD_REGISTRY_CLOSED";
    public const string Storage = "BUILD_REGISTRY_STORAGE", Unreadable = "BUILD_REGISTRY_UNREADABLE", Newer = "BUILD_REGISTRY_NEWER";
    public const string Stale = "BUILD_REGISTRY_STALE", Missing = "BUILD_REGISTRY_MISSING";
}
public sealed class BuildRegistryException(string code, string message, Exception? inner = null) : Exception(message, inner)
{ public string Code { get; } = code; }
public sealed record BuildRegistryOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;
    internal Action<BuildRegistryPoint>? Fault { get; init; }
}
internal enum BuildRegistryPoint
{
    BeforeCommit, InsideCommit, AfterCommit, RecoveryInterrupted,
    BeforeDirectorySync, DirectorySynced, BeforeAncestorSync, AncestorSynced, InitializationDurable,
}
