using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class LibraryOwnerSourceContractOnRealLibrary : LibraryOwnerSourceContract
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-owned-source-contract-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCdn cdn = new(); private ObjectStore library = null!; private LibraryObjectAccessService service = null!;
    protected override ILibraryObjectAccess Service => service;
    protected override long JoinedConsumers => service.JoinedConsumers;
    protected override int FetchCount => cdn.Requests.Count;
    protected override void BeforeFetch(Func<CancellationToken, Task> wait) => cdn.BeforeAnswer = (_, ct) => wait(ct);
    protected override void BeforeFetchKey(Func<CdnKey, CancellationToken, Task> wait) => cdn.BeforeAnswer = (request, ct) => wait(request.Key, ct);
    protected override Task SeedArchiveAsync(CdnKey key, byte[] bytes) { cdn.Add(CdnObjectKind.Data, key, bytes); return Task.CompletedTask; }
    protected override void BeforeArchive(Func<CancellationToken, Task> wait) => cdn.BeforeAnswer = (_, ct) => wait(ct);
    protected override IReadOnlyList<(CdnKey Key, long Offset, long Length)> ArchiveRequests => cdn.Requests.Where(request => request.Offset is not null).Select(request => (request.Key, request.Offset!.Value, request.Length!.Value)).ToArray();
    protected override async Task OpenAsync()
    { library = await ObjectStore.OpenLibraryAsync(directory, new() { CommitDelay = TimeSpan.FromMilliseconds(5) }); service = new(library, new ScriptedCdnClient(cdn)); }
    protected override async Task CloseAsync() { await service.DisposeAsync(); await library.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    protected override async Task SeedAsync(PutObject item, bool stored, ObjectOrigin origin = ObjectOrigin.Cdn)
    {
        cdn.Add(item.Key.Type == ObjectKeyType.Config ? CdnObjectKind.Config : CdnObjectKind.Data, new(item.Key.KeyBytes()), item.Bytes.ToArray());
        if (stored) await library.ForOwner("seed", LibraryOwnerScope.Global, "seed", LibraryObjectRole.Original).PutAsync([item with { Origin = origin }], true);
    }
    protected override async Task<bool> OwnsAsync(string owner, LibraryOwnerScope scope, string producer, ObjectKey key, LibraryObjectRole role = LibraryObjectRole.Original) =>
        await library.ForOwner(owner, scope, producer, role).OwnsAsync(key);
}
