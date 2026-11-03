using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace VibeMiner.Builds;

/// <summary>An explicitly located authoritative metadata-load registry, independent of discovery caches and app composition.</summary>
public sealed partial class BuildRegistry : IBuildRegistry
{
    public const string FileName = "registry.sqlite3", MarkerName = "registry-v1";
    public const int ApplicationId = 0x564d6272, SchemaVersion = 1;
    private sealed record Stored(BuildRegistryEntry Entry, string? Nonce);
    private sealed record Request(Func<object?> Work, CancellationToken Token, TaskCompletionSource<object?> Done);
    private readonly Channel<Request> requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(64) { SingleReader = true });
    private readonly SqliteConnection connection;
    private readonly FileStream writerLock;
    private readonly BuildRegistryOptions options;
    private readonly string registryId;
    private readonly object lifecycle = new();
    private Task loop = Task.CompletedTask;
    private Task? closing;
    private int closed;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };

    private BuildRegistry(SqliteConnection connection, FileStream writerLock, BuildRegistryOptions options, string registryId)
    {
        this.connection = connection; this.writerLock = writerLock; this.options = options; this.registryId = registryId;
    }

    public static Task<BuildRegistry> OpenAsync(string directory, BuildRegistryOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw Invalid("The registry directory must be an explicit absolute path.");
        try { directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (ArgumentException e) { throw Invalid(e.Message); }
        ct.ThrowIfCancellationRequested(); options ??= new();
        if (options.Time is null) throw Invalid("A registry clock is required.");
        return Task.Run(() => OpenCore(directory, options, ct), ct);
    }

    private async Task RunAsync()
    {
        await foreach (var request in requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { request.Token.ThrowIfCancellationRequested(); request.Done.TrySetResult(request.Work()); }
            catch (OperationCanceledException) { request.Done.TrySetCanceled(request.Token); }
            catch (Exception e) { request.Done.TrySetException(Map(e)); }
        }
    }
    private async Task<T> UseAsync<T>(Func<T> work, CancellationToken ct)
    {
        if (Volatile.Read(ref closed) != 0) throw new BuildRegistryException(BuildRegistryCodes.Closed, "The build registry is closed.");
        ct.ThrowIfCancellationRequested();
        var request = new Request(() => work(), ct, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!requests.Writer.TryWrite(request))
            throw new BuildRegistryException(Volatile.Read(ref closed) != 0 ? BuildRegistryCodes.Closed : BuildRegistryCodes.Busy,
                "The registry is closed or its bounded admission queue is full.");
        return (T)(await request.Done.Task.ConfigureAwait(false))!;
    }
    private static Exception Map(Exception e) => e switch
    {
        BuildRegistryException or OperationCanceledException => e,
        JsonException or BuildIdentityException => Unreadable("Registry metadata is malformed.", e),
        IOException or UnauthorizedAccessException or SqliteException => new BuildRegistryException(BuildRegistryCodes.Storage,
            "Registry storage failed; an entered commit may have completed. Inspect or reopen the registry before retrying.", e),
        _ => Unreadable("Registry metadata cannot be used.", e),
    };

    public Task<BuildRegistryEntry> ObserveAsync(BuildRegistryObservation observation, CancellationToken ct = default)
    {
        if (observation is null) throw Invalid("An observation is required.");
        var identity = Identity(observation.Identity); Selection(identity, observation.Selection, observation.Locale);
        var copy = observation with { Identity = identity };
        return UseAsync(() =>
        {
            var old = Read(identity.Id, ct);
            var entry = old is null
                ? new BuildRegistryEntry(identity, copy.Selection, copy.Locale, copy.ObservedAt, copy.ObservedAt, BuildLoadState.Discovered,
                    0, options.Time.GetUtcNow(), null, null, null, null, null)
                : old.Entry with
                {
                    FirstObservedAt = copy.ObservedAt < old.Entry.FirstObservedAt ? copy.ObservedAt : old.Entry.FirstObservedAt,
                    LastObservedAt = copy.ObservedAt > old.Entry.LastObservedAt ? copy.ObservedAt : old.Entry.LastObservedAt,
                    ObservedBuild = copy.ObservedAt >= old.Entry.LastObservedAt ? copy.Selection : old.Entry.ObservedBuild,
                    ObservedLocale = copy.ObservedAt >= old.Entry.LastObservedAt ? copy.Locale : old.Entry.ObservedLocale,
                };
            Save(new(entry, old?.Nonce), ct); return entry;
        }, ct);
    }
    public Task<BuildRegistryAttempt> BeginLoadAsync(string buildId, BuildRef selection, string locale, CancellationToken ct = default)
    {
        Id(buildId); Text(locale, BuildRegistryLimits.MaxLocaleBytes, "locale");
        if (selection is null) throw Invalid("A build selection is required.");
        return UseAsync(() =>
        {
            var old = Read(buildId, ct) ?? throw new BuildRegistryException(BuildRegistryCodes.Missing, "Observe the canonical build before starting a load.");
            Selection(old.Entry.Identity, selection, locale);
            var now = options.Time.GetUtcNow(); var generation = checked(old.Entry.Generation + 1); var nonce = Guid.NewGuid().ToString("N");
            var entry = old.Entry with { State = BuildLoadState.Loading, Generation = generation, UpdatedAt = now,
                AttemptBuild = selection, AttemptLocale = locale, AttemptStartedAt = now, FinishedAt = null, Diagnostic = null };
            Save(new(entry, nonce), ct); return new BuildRegistryAttempt(registryId, buildId, generation, nonce);
        }, ct);
    }
    public Task<BuildRegistryEntry> FinishAsync(BuildRegistryAttempt attempt, BuildLoadState state, BuildRegistryDiagnostic? diagnostic = null, CancellationToken ct = default)
    {
        if (attempt is null || state is not (BuildLoadState.Loaded or BuildLoadState.Failed or BuildLoadState.Interrupted)) throw Invalid("A terminal load outcome and attempt are required.");
        Diagnostic(diagnostic);
        return UseAsync(() =>
        {
            var old = Read(attempt.BuildId, ct);
            if (attempt.RegistryId != registryId || old is null || old.Entry.Generation != attempt.Generation || old.Nonce != attempt.Nonce || old.Entry.State != BuildLoadState.Loading)
                throw new BuildRegistryException(BuildRegistryCodes.Stale, "The attempt is foreign, superseded or already terminal.");
            var now = options.Time.GetUtcNow();
            var entry = old.Entry with { State = state, UpdatedAt = now, FinishedAt = now, Diagnostic = diagnostic };
            Save(new(entry, old.Nonce), ct); return entry;
        }, ct);
    }
    public Task<BuildRegistryEntry?> GetAsync(string buildId, CancellationToken ct = default)
    { Id(buildId); return UseAsync(() => Read(buildId, ct)?.Entry, ct); }
    public Task<BuildRegistryPage> ListAsync(int limit = 128, BuildRegistryCursor? after = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > BuildRegistryLimits.MaxPage) throw Invalid("A registry page is 1 to 1024 entries.");
        if (after is not null) { Id(after.AfterId); if (after.RegistryId != registryId) throw new BuildRegistryException(BuildRegistryCodes.Stale, "The registry cursor belongs to another registry."); }
        return UseAsync(() =>
        {
            var rows = Page(limit, after?.AfterId, ct); var entries = rows.Values.Select(s => s.Entry).ToArray();
            return new BuildRegistryPage(entries, rows.More ? new BuildRegistryCursor(registryId, entries[^1].Identity.Id) : null);
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (closing is null)
            {
                Volatile.Write(ref closed, 1); requests.Writer.TryComplete(); closing = CloseAsync();
            }
            return new(closing);
        }
    }
    private async Task CloseAsync()
    {
        try { await loop.ConfigureAwait(false); }
        finally { connection.Dispose(); writerLock.Dispose(); }
    }
}
