using System.Buffers.Binary;
using VibeMiner.Builds.Testing;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Tests;

public static class EncodingInventoryTestKit
{
    public static CdnKey Key(int value) { var bytes = new byte[16]; BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), value); return new(bytes); }
    public static BuildIdentity Identity(int seed = 1) => BuildIdentity.FromFields("wow", new Dictionary<string, string>
    { ["BuildConfig"] = seed.ToString("x32", System.Globalization.CultureInfo.InvariantCulture), ["VersionsName"] = "authored encoding inventory" });
    public static BuildRef Route(BuildIdentity identity, string region = "us", char cdn = 'c') => new(identity.ProductId, identity.Fields["BuildConfig"], new string(cdn, 32), region);
    public static EncodingRow[] Rows(int count, int start = 1) => Enumerable.Range(start, count).Select(i => new EncodingRow(Key(i), 100 + i)).ToArray();
    public static InMemoryEncodingRows Source(BuildIdentity identity, int count = 3507, int start = 1) => new(Route(identity), Key(900_001), Rows(count, start));
    public static EncodingInventoryContext Context(int seed = 1, int count = 3) { var identity = Identity(seed); return new(identity, Route(identity), Key(900_001), count); }
}
