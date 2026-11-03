using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace VibeMiner.Builds.Testing;

/// <summary>Independent in-memory registry model; shared contracts also run against the authoritative registry.</summary>
public sealed class InMemoryBuildRegistry(TimeProvider? time = null) : IBuildRegistry
{
    private readonly object gate = new();
    private readonly string registryId = Guid.NewGuid().ToString("N");
    private readonly SortedDictionary<string, (BuildRegistryEntry Entry, string? Nonce)> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private bool closed;

    private void Open() { if (closed) throw new BuildRegistryException(BuildRegistryCodes.Closed, "The registry is closed."); }
    private static BuildRegistryException Invalid() => new(BuildRegistryCodes.Validation, "Invalid registry input.");
    private static void Text(string? value, int max, bool empty = false)
    {
        if (value is null || (!empty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > max) throw Invalid();
    }
    private static void Id(string? id)
    {
        if (id is not { Length: 64 } || id.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) throw Invalid();
    }
    private static void Locale(string locale)
    {
        Text(locale, BuildRegistryLimits.MaxLocaleBytes);
        try { _ = BuildLocales.Bit(locale); } catch (ArgumentException) { throw Invalid(); }
    }
    private static BuildIdentity CopyIdentity(BuildIdentity identity)
    {
        if (identity is null || identity.Fields is null || identity.Fields.Count > BuildRegistryLimits.MaxFields) throw Invalid();
        Text(identity.Canonical, BuildRegistryLimits.MaxIdentityBytes); Id(identity.Id);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in identity.Fields)
        {
            Text(name, BuildRegistryLimits.MaxFieldNameBytes); Text(value, BuildRegistryLimits.MaxFieldValueBytes, true); fields.Add(name, value);
        }
        BuildIdentity normalized;
        try { normalized = BuildIdentity.FromFields(identity.ProductId, fields); }
        catch (Exception e) when (e is ArgumentException or BuildIdentityException) { throw Invalid(); }
        if (normalized.Id != identity.Id || normalized.Canonical != identity.Canonical || normalized.ProductId != identity.ProductId ||
            normalized.Fields.Count != fields.Count || normalized.Fields.Any(pair => fields[pair.Key] != pair.Value)) throw Invalid();
        return normalized with { Fields = new ReadOnlyDictionary<string, string>(fields) };
    }
    private static void Selection(BuildIdentity identity, BuildRef selection)
    {
        if (selection is null || selection.Product != identity.ProductId || selection.BuildConfig != identity.Fields["BuildConfig"]) throw Invalid();
    }
    private static void Diagnostic(BuildRegistryDiagnostic? diagnostic)
    {
        if (diagnostic is null) return;
        Text(diagnostic.Code, BuildRegistryLimits.MaxDiagnosticCodeBytes); Text(diagnostic.Message, BuildRegistryLimits.MaxDiagnosticMessageBytes, true);
    }

    public Task<BuildRegistryEntry> ObserveAsync(BuildRegistryObservation observation, CancellationToken ct = default)
    {
        lock (gate)
        {
            Open(); ct.ThrowIfCancellationRequested(); if (observation is null) throw Invalid();
            var identity = CopyIdentity(observation.Identity); Selection(identity, observation.Selection); Locale(observation.Locale);
            BuildRegistryEntry entry;
            if (entries.TryGetValue(identity.Id, out var saved))
            {
                var current = saved.Entry; var newer = observation.ObservedAt >= current.LastObservedAt;
                entry = current with
                {
                    FirstObservedAt = observation.ObservedAt < current.FirstObservedAt ? observation.ObservedAt : current.FirstObservedAt,
                    LastObservedAt = newer ? observation.ObservedAt : current.LastObservedAt,
                    ObservedBuild = newer ? observation.Selection : current.ObservedBuild,
                    ObservedLocale = newer ? observation.Locale : current.ObservedLocale,
                };
                entries[identity.Id] = (entry, saved.Nonce);
            }
            else
            {
                entry = new(identity, observation.Selection, observation.Locale, observation.ObservedAt, observation.ObservedAt,
                    BuildLoadState.Discovered, 0, clock.GetUtcNow(), null, null, null, null, null);
                entries.Add(identity.Id, (entry, null));
            }
            return Task.FromResult(entry);
        }
    }

    public Task<BuildRegistryAttempt> BeginLoadAsync(string buildId, BuildRef selection, string locale, CancellationToken ct = default)
    {
        lock (gate)
        {
            Open(); ct.ThrowIfCancellationRequested(); Id(buildId); Locale(locale);
            if (!entries.TryGetValue(buildId, out var current)) throw new BuildRegistryException(BuildRegistryCodes.Missing, "Observe the build before loading.");
            Selection(current.Entry.Identity, selection); var generation = checked(current.Entry.Generation + 1); var now = clock.GetUtcNow();
            var nonce = Guid.NewGuid().ToString("N");
            entries[buildId] = (current.Entry with { State = BuildLoadState.Loading, Generation = generation, UpdatedAt = now,
                AttemptBuild = selection, AttemptLocale = locale, AttemptStartedAt = now, FinishedAt = null, Diagnostic = null }, nonce);
            return Task.FromResult(new BuildRegistryAttempt(registryId, buildId, generation, nonce));
        }
    }

    public Task<BuildRegistryEntry> FinishAsync(BuildRegistryAttempt attempt, BuildLoadState state, BuildRegistryDiagnostic? diagnostic = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            Open(); ct.ThrowIfCancellationRequested(); if (attempt is null) throw Invalid();
            if (state is not (BuildLoadState.Loaded or BuildLoadState.Failed or BuildLoadState.Interrupted)) throw Invalid(); Diagnostic(diagnostic);
            if (attempt.RegistryId != registryId || !entries.TryGetValue(attempt.BuildId, out var current) || current.Nonce != attempt.Nonce ||
                current.Entry.Generation != attempt.Generation || current.Entry.State != BuildLoadState.Loading)
                throw new BuildRegistryException(BuildRegistryCodes.Stale, "The attempt is stale or belongs to another registry.");
            var now = clock.GetUtcNow(); var entry = current.Entry with { State = state, UpdatedAt = now, FinishedAt = now, Diagnostic = diagnostic };
            entries[attempt.BuildId] = (entry, null); return Task.FromResult(entry);
        }
    }

    public Task<BuildRegistryEntry?> GetAsync(string buildId, CancellationToken ct = default)
    {
        lock (gate) { Open(); ct.ThrowIfCancellationRequested(); Id(buildId); return Task.FromResult(entries.TryGetValue(buildId, out var entry) ? entry.Entry : null); }
    }

    public Task<BuildRegistryPage> ListAsync(int limit = 128, BuildRegistryCursor? after = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            Open(); ct.ThrowIfCancellationRequested();
            if (limit is < 1 or > BuildRegistryLimits.MaxPage) throw Invalid();
            if (after is not null && after.RegistryId != registryId) throw new BuildRegistryException(BuildRegistryCodes.Stale, "The cursor belongs to another registry.");
            var page = new List<BuildRegistryEntry>(limit); var bytes = 0; var more = false;
            foreach (var (id, saved) in entries)
            {
                ct.ThrowIfCancellationRequested(); if (after is not null && string.CompareOrdinal(id, after.AfterId) <= 0) continue;
                var size = JsonSerializer.SerializeToUtf8Bytes(saved.Entry).Length;
                if (page.Count == limit || page.Count > 0 && bytes + size > BuildRegistryLimits.MaxPageBytes) { more = true; break; }
                page.Add(saved.Entry); bytes = checked(bytes + size);
            }
            return Task.FromResult(new BuildRegistryPage(page, more ? new BuildRegistryCursor(registryId, page[^1].Identity.Id) : null));
        }
    }

    public ValueTask DisposeAsync() { lock (gate) closed = true; return ValueTask.CompletedTask; }
}
