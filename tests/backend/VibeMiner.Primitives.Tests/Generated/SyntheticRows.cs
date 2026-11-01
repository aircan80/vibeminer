using System.Globalization;
using System.Text;

namespace VibeMiner.Primitives.Tests.Generated;

/// <summary>
/// Synthetic table-like records for the record file latency test and the GeneratedStore benchmark (#179): DB2-shaped rows encoded as
/// text, 25 tab-separated values (small ints, a few larger ints, floats, short strings, some empty). Ids ascend with gaps. Deterministic
/// per seed; no game content.
/// </summary>
internal static class SyntheticRows
{
    private static readonly string[] Words =
        ["Stone", "Ember", "Frost", "Gale", "Thorn", "Ash", "Moon", "Iron", "Silk", "Brine", "Dusk", "Vale", "Oak", "Flint", "Mire", "Glade"];

    public static IEnumerable<(uint Id, byte[] Bytes)> Generate(int count, int seed = 179)
    {
        var r = new Random(seed);
        var sb = new StringBuilder(256);
        uint id = 0;
        for (var i = 0; i < count; i++)
        {
            id += r.NextDouble() < 0.9 ? 1u : (uint)r.Next(2, 60);
            yield return (id, Row(id, r, sb));
        }
    }

    private static byte[] Row(uint id, Random r, StringBuilder sb)
    {
        var c = CultureInfo.InvariantCulture;
        sb.Clear();
        sb.Append(id.ToString(c));
        for (var f = 1; f < 25; f++)
        {
            sb.Append('\t');
            switch (f)
            {
                case 1: sb.Append(Words[r.Next(Words.Length)]).Append(' ').Append(Words[r.Next(Words.Length)]); break;
                case 2: if (r.Next(3) > 0) sb.Append("Of the ").Append(Words[r.Next(Words.Length)]); break;
                case 3: if (r.Next(4) == 0) sb.Append(Words[r.Next(Words.Length)].ToLowerInvariant()).Append('_').Append(r.Next(100).ToString(c)); break;
                case 4 or 5 or 6: sb.Append(r.Next(int.MaxValue).ToString(c)); break; // larger ints: foreign ids, flags
                case 7: sb.Append(((uint)r.Next() | 0x80000000u).ToString(c)); break;
                case 8 or 9 or 10: sb.Append((r.NextDouble() * 200 - 50).ToString("0.####", c)); break; // floats as text
                case 11 or 12: break; // always empty
                default: sb.Append(r.Next(10) < 6 ? "0" : r.Next(256).ToString(c)); break; // small ints, mostly zero
            }
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
