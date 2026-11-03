using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class ManagedLibraryOwnerSourceTests : LibraryOwnerSourceContract
{
    private readonly InMemoryLibraryObjectAccess service = new();
    protected override ILibraryObjectAccess Service => service;
    protected override long JoinedConsumers => service.JoinedConsumers;
    protected override int FetchCount => service.Fetched.Count;
    protected override void BeforeFetch(Func<CancellationToken, Task> wait) => service.BeforeFetch = (_, _, ct) => wait(ct);
    protected override void BeforeFetchKey(Func<CdnKey, CancellationToken, Task> wait) => service.BeforeFetch = (request, _, ct) => wait(new(request.Key.KeyBytes()), ct);
    protected override Task SeedArchiveAsync(CdnKey key, byte[] bytes) { service.AddArchive(key, bytes); return Task.CompletedTask; }
    protected override void BeforeArchive(Func<CancellationToken, Task> wait) => service.BeforeRangeFetch = (_, ct) => wait(ct);
    protected override IReadOnlyList<(CdnKey Key, long Offset, long Length)> ArchiveRequests => service.ArchiveRequests.Select(request => (request.Archive, request.Offset, request.Length)).ToArray();
    protected override Task OpenAsync() => Task.CompletedTask;
    protected override async Task CloseAsync() => await service.DisposeAsync();
    protected override Task SeedAsync(PutObject item, bool stored, ObjectOrigin origin = ObjectOrigin.Cdn) { service.Add(item, stored, origin); return Task.CompletedTask; }
    protected override Task<bool> OwnsAsync(string owner, LibraryOwnerScope scope, string producer, ObjectKey key, LibraryObjectRole role = LibraryObjectRole.Original) => Task.FromResult(role == LibraryObjectRole.Original && service.Owns(owner, scope, producer, key));
}
