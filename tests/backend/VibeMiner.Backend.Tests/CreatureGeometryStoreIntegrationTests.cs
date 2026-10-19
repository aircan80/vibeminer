using System.Text.Json;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureGeometryStoreIntegrationTests
{
    private static readonly TimeSpan Guard=TimeSpan.FromSeconds(20);
    private static Task Progress(CreatureGeometryProgress _)=>Task.CompletedTask;
    private static CreatureGeometryStore Store(CreatureGeometrySourcesFixture f)=>new(f.Catalog,f.Projection.Objects,f.Displays,f.Sessions,f.Shared,f.Resources);
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LocalExactArtifactAssertionCannotReturnAnotherGeometry(bool open)
    {
        await using var f=await CreatureGeometrySourcesFixture.CreateAsync();await using var store=Store(f);
        var ready=await store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);Assert.NotNull(ready.Manifest);
        var requests=f.Http.Requests.Count;var different=new string('f',64);
        var error=open?await Assert.ThrowsAsync<DiscoveryException>(()=>store.OpenAsync(new(f.Request,different),default)):
            await Assert.ThrowsAsync<DiscoveryException>(()=>store.ReadAsync(new(f.Request,different),default));
        Assert.Equal("CREATURE_GEOMETRY_STALE",error.Code);Assert.Equal(requests,f.Http.Requests.Count);Assert.Equal(0,store.Pressure.Payload);
    }
    [Fact]
    public async Task ModelInvalidityDuringFailedSkinWaitCannotPublishUnfencedSourceClaim()
    {
        await using var f=await CreatureGeometrySourcesFixture.CreateAsync(encryptedSkin:true);await using var store=Store(f);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Before=async(request,token)=>
        {
            if(f.Http.Responses.TryGetValue(request.RequestUri!.AbsoluteUri,out var bytes)&&ReferenceEquals(bytes,f.SkinEncoded))
            {entered.TrySetResult();await release.Task.WaitAsync(token);}
        };
        var task=store.PrepareAsync(new(f.Request),Progress,default);await entered.Task.WaitAsync(Guard);
        await f.Catalog.RecordObjectInvalidityAsync(Convert.ToHexStringLower(SHA256.HashData(f.ModelEncoded)),f.ModelEncoded.Length,"LOCAL_OBJECT_CORRUPT",default);
        release.SetResult();var result=await task.WaitAsync(Guard);Assert.Equal(CreatureGeometryState.Protected,result.State);
        Assert.Empty(result.Sources);Assert.Null(result.Manifest);Assert.Equal(CreatureGeometryDependency.Skin,result.Problem!.Dependency);
    }
    [Fact]
    public async Task PublicConstructorPublishesRealSourceGeometryThenLocalOfflineReadAndOpenNeverAcquire()
    {
        await using var f=await CreatureGeometrySourcesFixture.CreateAsync();await using var store=Store(f);
        var before=await store.ReadAsync(new(f.Request),default);Assert.Equal(CreatureGeometryState.NotPrepared,before.State);
        await using var service=new CreatureGeometryService(store);
        var ready=await service.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);Assert.Equal(CreatureGeometryState.Ready,ready.State);
        Assert.NotNull(ready.Manifest);Assert.Equal(2,ready.Sources.Count);Assert.Equal(0,store.Pressure.Payload);
        var requests=f.Http.Requests.Count;f.Http.Offline=true;
        foreach(var source in ready.Sources)File.Delete(Path.Combine(f.Projection.DirectoryPath,"objects-v1",source.EncodedSha256[..2],source.EncodedSha256[2..]));
        var local=await service.ReadAsync(new(f.Request,ready.Manifest.ArtifactId),default);
        Assert.Equal(JsonSerializer.Serialize(ready,CreaturesPolicy.Json),JsonSerializer.Serialize(local,CreaturesPolicy.Json));
        var lease=await service.OpenAsync(new(f.Request,ready.Manifest.ArtifactId),default);var bytes=new byte[lease.Manifest.ByteLength];await lease.Content.ReadExactlyAsync(bytes);
        Assert.Equal(0,await lease.Content.ReadAsync(new byte[1]));await lease.DisposeAsync();await lease.Retirement;
        Assert.Equal(requests,f.Http.Requests.Count);Assert.Equal(0,store.Pressure.Payload);
        var reused=await store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);
        Assert.Equal(ready.Sources,reused.Sources);Assert.Equal(JsonSerializer.Serialize(ready.Manifest,CreaturesPolicy.Json),JsonSerializer.Serialize(reused.Manifest,CreaturesPolicy.Json));Assert.Equal(requests,f.Http.Requests.Count);
    }
    [Fact]
    public async Task ParsedZeroDeclaredTrianglesReturnsSourcesButNeverRegistersGeometry()
    {
        await using var f=await CreatureGeometrySourcesFixture.CreateAsync(empty:true);await using var store=Store(f);
        var result=await store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.NoDeclaredTriangles,result.State);Assert.Null(result.Manifest);Assert.Equal(2,result.Sources.Count);
        Assert.Equal(CreatureGeometryState.NotPrepared,(await store.ReadAsync(new(f.Request),default)).State);Assert.Equal(0,store.Pressure.Payload);
    }
    [Fact]
    public async Task ProtectedSkinKeepsExactFailureButPublishesNoUnfencedSourceClaims()
    {
        await using var f=await CreatureGeometrySourcesFixture.CreateAsync(encryptedSkin:true);await using var store=Store(f);
        var result=await store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.Protected,result.State);Assert.Null(result.Manifest);Assert.Empty(result.Sources);
        Assert.Equal(CreatureGeometryDependency.Skin,result.Problem!.Dependency);
        Assert.Equal(0,store.Pressure.Payload);
    }
    [Fact]
    public async Task MissingDerivedObjectIsNotPreparedLocallyWithoutNetworkAndCanBeRebuilt()
    {
        await using var f=await CreatureGeometrySourcesFixture.CreateAsync();await using var store=Store(f);
        var ready=await store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);var manifest=ready.Manifest!;
        File.Delete(Path.Combine(f.Projection.DirectoryPath,"objects-v1",manifest.Sha256[..2],manifest.Sha256[2..]));var requests=f.Http.Requests.Count;
        Assert.Equal(CreatureGeometryState.NotPrepared,(await store.ReadAsync(new(f.Request),default)).State);
        Assert.Equal("CREATURE_GEOMETRY_NOT_PREPARED",(await Assert.ThrowsAsync<DiscoveryException>(()=>store.OpenAsync(new(f.Request,manifest.ArtifactId),default))).Code);
        Assert.Equal(requests,f.Http.Requests.Count);
        var rebuilt=await store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);
        Assert.Equal(manifest.Sha256,rebuilt.Manifest!.Sha256);Assert.Equal(ready.Sources,rebuilt.Sources);Assert.Equal(0,store.Pressure.Payload);
    }
    [Fact]
    public async Task SameLengthCorruptionReturnsCorruptAndNeverOpensLease()
    {
        await using var f=await CreatureGeometrySourcesFixture.CreateAsync();await using var store=Store(f);
        var ready=await store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);var m=ready.Manifest!;
        var path=Path.Combine(f.Projection.DirectoryPath,"objects-v1",m.Sha256[..2],m.Sha256[2..]);var bytes=await File.ReadAllBytesAsync(path);bytes[^1]^=1;await File.WriteAllBytesAsync(path,bytes);
        var result=await store.ReadAsync(new(f.Request),default);Assert.Equal(CreatureGeometryState.Corrupt,result.State);Assert.Null(result.Manifest);
        await Assert.ThrowsAsync<DiscoveryException>(()=>store.OpenAsync(new(f.Request,m.ArtifactId),default));Assert.Equal(0,store.Pressure.Payload);
    }
}
