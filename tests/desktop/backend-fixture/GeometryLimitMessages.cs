using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Desktop.BackendFixture;

/// <summary>Creature geometry limit problems as the real engine words them and picks their headline (#114, #116), so an
/// e2e test can check engine text, not fixture text. Each refusal comes from a real reader over authored bytes: the M2
/// reader's vertex count over a 304-byte header, the BLTE reader's chunk count over a 36-byte header, and the ROOT batch
/// lookup's per-file variant cap over a small authored ROOT. The build-list limit is an encoding manifest cap through the
/// same failure mapping. No game data.</summary>
internal static class GeometryLimitMessages
{
    internal static async Task<string> ReadAsync()
    {
        // An authored unchunked MD20 v264 header declaring one more vertex than geometry reads; every other count is zero.
        var header = new byte[304];
        "MD20"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 264);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), (uint)GeometrySourceClosure.Limits.MaxVertices + 1);
        var vertices = await RefusalAsync(() => M2ModelReader.ReadAsync(new MemoryStream(header, false), GeometrySourceClosure.Limits));
        // An authored BLTE header whose chunk table declares one more chunk than the reader takes.
        var blte = new byte[36]; "BLTE"u8.CopyTo(blte); BinaryPrimitives.WriteUInt32BigEndian(blte.AsSpan(4), 36);
        blte[8] = 15; var chunks = BlteDecoder.MaxChunks + 1; blte[9] = (byte)(chunks >> 16); blte[10] = (byte)(chunks >> 8); blte[11] = (byte)chunks;
        var key = new string('0', 32);
        var chunkCount = await RefusalAsync(() => BlteDecoder.DecodeAsync(new MemoryStream(blte, false), Stream.Null, key, key, 1000, 1));
        var variants = await RefusalAsync(() => RootBatchLookup.ReadAsync(new MemoryStream(Root(RootBatchLookup.MaxMatchesPerTarget + 1), false), [5u], new()));
        return JsonSerializer.Serialize(new
        {
            vertexCount = Problem(vertices, CreatureGeometryDependency.Model),
            namedLimit = Problem(chunkCount, CreatureGeometryDependency.Skin),
            buildListLimit = Problem(new("EN_LIMIT", "EN specification count exceeds its limit."), CreatureGeometryDependency.Model),
            rootVariants = Problem(variants, CreatureGeometryDependency.Model)
        });
    }

    private static object Problem(DiscoveryException error, CreatureGeometryDependency dependency)
    {
        var failure = GeometrySourceClosure.Failure(error, dependency, []);
        return new
        {
            code = failure.Problem.Code, message = failure.Problem.Message, retryable = failure.Problem.Retryable,
            dependency = JsonNamingPolicy.CamelCase.ConvertName(dependency.ToString()), headline = JsonNamingPolicy.CamelCase.ConvertName(failure.Headline.ToString())
        };
    }

    private static async Task<DiscoveryException> RefusalAsync(Func<Task> read)
    {
        try { await read(); }
        catch (DiscoveryException e) { return e; }
        throw new InvalidOperationException("The authored over-limit input was admitted.");
    }

    // A version-2 TSFM ROOT with one block per variant of FileDataID 5, no names.
    private static byte[] Root(int variants)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("TSFM"u8); writer.Write(24U); writer.Write(2U); writer.Write((uint)variants); writer.Write(0U); writer.Write(0U);
        for (var i = 0; i < variants; i++)
        {
            writer.Write(1U); writer.Write(2U); writer.Write(RootInventoryReader.NoNames); writer.Write(0U); writer.Write((byte)0);
            writer.Write(5U); writer.Write(Enumerable.Repeat((byte)0x11, 16).ToArray());
        }
        return output.ToArray();
    }
}
