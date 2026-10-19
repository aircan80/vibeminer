using System.Text.Json;
using System.Text.Json.Nodes;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class CreatureGeometryStore
{
    private async Task CheckCaptureAsync(GeometryDisplayAuthorizationResult proof, CancellationToken token)
    {
        // The16MiB child is held BEFORE the C2 helper runs. Accepted anchors are at most
        //2MiB conservative retained graph each;2 anchors+one bounded helper candidate and
        //one comparison/serializer overlap remain in that phase. Raw documents are never JSON/base64.
        long bytes=128<<10; // Fixed DTO/container/serializer overlap, in addition to8x text/object estimate.
        foreach(var document in new[]{proof.Core.Opening.Versions,proof.Core.Opening.Cdns})
        {
            token.ThrowIfCancellationRequested();
            if(document?.Bytes is null||document.Bytes.Length>(1<<20))throw CaptureLimit();
            bytes=checked(bytes+document.Bytes.Length);
        }
        await CountAsync(proof.Core.Opening.Build);await CountAsync(proof.Core.Artifact);
        await CountAsync(proof.Core.Snapshot);await CountAsync(proof.Core.Summary);
        if(proof.Delivered.Count>2)throw CaptureLimit();
        foreach(var target in proof.Delivered)await CountAsync(target);
        async Task CountAsync<T>(T value)
        {
            using(await cpu.EnterAsync(token))
            {token.ThrowIfCancellationRequested();bytes=checked(bytes+8L*ProofBytes(value).Length);if(bytes>(2L<<20))throw CaptureLimit();}
            await Task.Yield();
        }
    }
    private async Task CompareAsync(GeometryDisplayAuthorizationResult original, GeometryDisplayAuthorizationResult current,
        bool intersectOnly, CancellationToken token)
    {
        await CheckCaptureAsync(current, token);
        if(original.Core.Opening.Reference!=current.Core.Opening.Reference||original.Selected!=current.Selected)throw Stale();
        await EqualAsync(original.Core.Opening.Build,current.Core.Opening.Build);
        await EqualAsync(original.Core.Artifact,current.Core.Artifact);
        await EqualAsync(original.Core.Snapshot,current.Core.Snapshot);
        await EqualAsync(original.Core.Summary,current.Core.Summary);
        async Task EqualAsync<T>(T first,T second)
        {
            using(await cpu.EnterAsync(token)){token.ThrowIfCancellationRequested();if(!Equal(first,second))throw Stale();}
            await Task.Yield();
        }
        foreach (var pair in new[] { (original.Core.Opening.Versions, current.Core.Opening.Versions), (original.Core.Opening.Cdns, current.Core.Opening.Cdns) })
        {
            if (pair.Item1.Role != pair.Item2.Role || pair.Item1.Url != pair.Item2.Url || pair.Item1.RetrievedAt != pair.Item2.RetrievedAt ||
                pair.Item1.Sha256 != pair.Item2.Sha256 || pair.Item1.ParserRevision != pair.Item2.ParserRevision || pair.Item1.Bytes.Length != pair.Item2.Bytes.Length) throw Stale();
            for (var at = 0; at < pair.Item1.Bytes.Length; at += 65536)
            {
                using (await cpu.EnterAsync(token))
                { token.ThrowIfCancellationRequested(); if (!pair.Item1.Bytes.AsSpan(at, Math.Min(65536, pair.Item1.Bytes.Length-at)).SequenceEqual(pair.Item2.Bytes.AsSpan(at, Math.Min(65536, pair.Item1.Bytes.Length-at)))) throw Stale(); }
                await Task.Yield();
            }
        }
        foreach (var target in original.Delivered)
        {
            var other = current.Delivered.SingleOrDefault(p => p.Target.Role == target.Target.Role);
            if (other is null) { if (intersectOnly) continue; throw Stale(); }
            using (await cpu.EnterAsync(token)) { token.ThrowIfCancellationRequested(); if (!Equal(target, other)) throw Stale(); }
            await Task.Yield();
        }
    }
    private async Task<GeometryDisplayAuthorizationResult> ReauthorizeProducerAsync(CreatureDisplayMetadataRequest request,
        GeometryDisplayAuthorizationResult original,CancellationToken token,GeometryDisplayAuthorizationResult? resolved=null)
    {
        await producerCapture.WaitAsync(token);
        try{var current=await ReauthorizeAsync(request,original,token);if(resolved is not null)await CompareAsync(resolved,current,false,token);return current;}
        finally{producerCapture.Release();}
    }
    private async Task<GeometryDisplayAuthorizationResult> ReauthorizeAsync(CreatureDisplayMetadataRequest request,
        GeometryDisplayAuthorizationResult original, CancellationToken token)
    {
        var current = await work.AuthorizeAsync(request, token); await CompareAsync(original, current, false, token);
        var targets = current.Delivered.Select(p => original.Delivered.SingleOrDefault(q => q.Target.Role == p.Target.Role) ?? p).ToArray();
        GeometryDisplayAuthorization? authorization = current.Authorization is null ? null : new(original.Core, original.Selected!, targets[0], targets[1], current.Authorization.ModelFileDataId);
        return current with { Core = original.Core, Selected = original.Selected, Delivered = Array.AsReadOnly(targets), Authorization = authorization,
            Metadata = current.Metadata with { Selected = original.Selected, CreatureCoverage = original.Metadata.CreatureCoverage, CreatureSource = original.Metadata.CreatureSource } };
    }
    private sealed class SelectionFence(CreatureGeometryStore owner, CreatureDisplayMetadataRequest request, GeometryDisplayAuthorizationResult original) : IGeometrySelectionFence
    {
        public GeometryDisplayAuthorization Original => original.Authorization ?? throw Stale();
        public async Task RevalidateAsync(CancellationToken token)
        { var current = await owner.ReauthorizeProducerAsync(request, original, token); if (current.Authorization is null) throw Stale(); }
    }
    private static bool Equal<T>(T first, T second)
    {
        var a = JsonNode.Parse(ProofBytes(first)); var b = JsonNode.Parse(ProofBytes(second)); Clear(a); Clear(b); return JsonNode.DeepEquals(a,b);
        static void Clear(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var pair in obj.ToArray()) { if (pair.Key == "verifiedAt") obj[pair.Key] = ""; else Clear(pair.Value); }
            else if (node is JsonArray array) foreach (var value in array) Clear(value);
        }
    }
    private static byte[] ProofBytes<T>(T value)
    { using var buffer = new CaptureBuffer(); JsonSerializer.Serialize(buffer,value,CreaturesPolicy.Json); return buffer.ToArray(); }
    private sealed class CaptureBuffer : MemoryStream
    {
        public override void Write(byte[] b,int at,int count) { Check(count); base.Write(b,at,count); }
        public override void Write(ReadOnlySpan<byte> b) { Check(b.Length); base.Write(b); }
        private void Check(int count) { if (count > (256<<10)-Length) throw CaptureLimit(); }
    }
    private static DiscoveryException CaptureLimit() => new("CREATURE_GEOMETRY_RESOURCE_LIMIT", "The complete original geometry selection exceeds its reserved metadata allowance.", true);
    private static DiscoveryException Stale() => new("CREATURE_GEOMETRY_STALE", "The original Creature selection or a delivered source changed. Retry the selected slot.", true);
}
