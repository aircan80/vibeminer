using System.Buffers.Binary;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;

namespace VibeMiner.Desktop.BackendFixture;

/// <summary>Creature geometry limit messages as the real engine words them (#114), so an e2e test can check engine text,
/// not fixture text. The vertex refusal comes from the real M2 reader over an authored 304-byte header; the named limit
/// is the BLTE reader's chunk-count refusal, and the build-list limit an encoding manifest cap, through the same failure
/// mapping. No game data.</summary>
internal static class GeometryLimitMessages
{
    internal static async Task<string> ReadAsync()
    {
        // An authored unchunked MD20 v264 header declaring one more vertex than geometry reads; every other count is zero.
        var header = new byte[304];
        "MD20"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 264);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), (uint)GeometrySourceClosure.Limits.MaxVertices + 1);
        DiscoveryException vertices;
        try { using var source = new MemoryStream(header, false); await M2ModelReader.ReadAsync(source, GeometrySourceClosure.Limits); throw new InvalidOperationException("The authored over-count model was admitted."); }
        catch (DiscoveryException e) { vertices = e; }
        var count = GeometrySourceClosure.Failure(vertices, CreatureGeometryDependency.Model, []).Problem;
        var named = GeometrySourceClosure.Failure(new("BLTE_LIMIT", "BLTE exceeds the chunk count limit."), CreatureGeometryDependency.Skin, []).Problem;
        var list = GeometrySourceClosure.Failure(new("EN_LIMIT", "EN specification count exceeds its limit."), CreatureGeometryDependency.Model, []).Problem;
        return JsonSerializer.Serialize(new
        {
            vertexCount = new { code = count.Code, message = count.Message, retryable = count.Retryable, dependency = "model" },
            namedLimit = new { code = named.Code, message = named.Message, retryable = named.Retryable, dependency = "skin" },
            buildListLimit = new { code = list.Code, message = list.Message, retryable = list.Retryable, dependency = "model" }
        });
    }
}
