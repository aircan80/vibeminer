using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Db2Comparison;

/// <summary>#177: the layer 5 Db2Reader must give the same canonical bytes as the legacy Wdc5TableReader on every base
/// fixture and on every truncation, short append and single-byte mutation of it.</summary>
// Intentional differences and why none of them can be reached here, so no mutation is skipped:
// - The legacy "source changed length" EOF probes: both inputs are immutable byte arrays, so the probe always passes.
// - The legacy CanSeek check: LegacyStream is always seekable.
// - Hole ordinal uniqueness vs non-empty name: holes are not mutated and always get unique ordinals and non-empty names.
// - A read touching a hole: LegacyStream throws the BlteProjectionStream code and message, which the new reader copies.
// - The magic: the new reader also reads WDC3 and WDC4, so an input starting with "WDC3" or "WDC4" is skipped and counted
//   under MagicSkip. For any other magic the error message is left out only when the legacy outcome is DB2_UNSUPPORTED
//   "Only WDC5 is supported." and the new outcome is also DB2_UNSUPPORTED: the new reader names the refused magic. Every
//   other outcome, and every input shorter than 4 bytes, is compared in full.
// Skips is a counted list per reason; MagicSkip is the only reason allowed.
public sealed class ReaderComparisonTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names()
    {var data=new TheoryData<string>();foreach(var fixture in Fixtures.All)data.Add(fixture.Name);return data;}

    [Fact]
    public void Corpus_has_at_least_25_distinct_base_fixtures()
    {
        Assert.True(Fixtures.All.Length>=25);
        Assert.Equal(Fixtures.All.Length,Fixtures.All.Select(f=>f.Name).Distinct().Count());
        Assert.Equal(Fixtures.All.Length,Fixtures.All.Select(Key).Distinct().Count());
    }

    // Everything that changes a read: bytes, definition, every limit and rule value, the holes and the build.
    private static string Key(Fixture f)
    {
        var limits=f.Limits??new Lim();
        return string.Join("|",Convert.ToHexString(f.Bytes),f.Dbd,(limits with{Rules=null}).ToString(),
            string.Join(";",(limits.Rules??[]).Select(r=>r.ToString())),string.Join(";",(f.Holes??[]).Select(h=>h.Offset.ToString(CultureInfo.InvariantCulture)+"+"+h.Length.ToString(CultureInfo.InvariantCulture))),f.Build);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Base_fixture_gives_the_expected_outcome_and_identical_canonical_bytes(string name)
    {
        var fixture=Fixtures.All.Single(f=>f.Name==name);var comparer=new Comparer(fixture);
        var legacy=await comparer.Legacy(fixture.Bytes);var current=await comparer.New(fixture.Bytes);
        output.WriteLine(string.Join("\n",current));
        Assert.Equal(string.Join("\n",legacy),string.Join("\n",current));
        Assert.Equal(Canon.Bytes(legacy),Canon.Bytes(current));
        Assert.Equal(fixture.Expect,Canon.Outcome(current));
    }

    private const string MagicSkip="magic is WDC3 or WDC4: only the new reader reads them (#177)";
    // The legacy reader's only magic error is DB2_UNSUPPORTED "Only WDC5 is supported."; the new one names the refused magic.
    // Only that pair loses its message; any other outcome is compared in full.
    private const string CodeUnsupported=" code=\"DB2_UNSUPPORTED\"";
    private static readonly string LegacyMagicError=CodeUnsupported+" message="+System.Text.Json.JsonSerializer.Serialize("Only WDC5 is supported.");
    private static bool WithoutMagicMessage(List<string> legacy,List<string> current)
    {
        if(legacy.Count==0||current.Count==0)return false;
        string l=legacy[^1],c=current[^1];
        if(!l.StartsWith("E ",StringComparison.Ordinal)||!l.EndsWith(LegacyMagicError,StringComparison.Ordinal))return false;
        if(!c.StartsWith("E ",StringComparison.Ordinal))return false;
        var at=c.IndexOf(" message=",StringComparison.Ordinal);
        if(at<0||!c[..at].EndsWith(CodeUnsupported,StringComparison.Ordinal))return false;
        legacy[^1]=l[..^(LegacyMagicError.Length-CodeUnsupported.Length)];current[^1]=c[..at];
        return true;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_truncation_and_single_byte_mutation_gives_identical_canonical_bytes(string name)
    {
        var fixture=Fixtures.All.Single(f=>f.Name==name);var comparer=new Comparer(fixture);var bytes=fixture.Bytes;
        var inputs=new List<(string Mutation,byte[] Bytes)>();
        for(var length=0;length<bytes.Length;length++)inputs.Add(("truncate "+length.ToString(CultureInfo.InvariantCulture),bytes[..length]));
        foreach(var extra in new[]{1,4})foreach(var value in new[]{(byte)0x00,(byte)0xFF})
            inputs.Add(($"append {extra.ToString(CultureInfo.InvariantCulture)}x{value:X2}",[..bytes,..Enumerable.Repeat(value,extra)]));
        var duplicates=0;
        for(var at=0;at<bytes.Length;at++)
        {
            var original=bytes[at];var seen=new HashSet<byte>{original};
            foreach(var value in new[]{(byte)0x00,(byte)0xFF,(byte)(original^0x01),(byte)(original^0x80)})
            {
                // 0x00 or 0xFF on a byte that already has that value would re-run the base input.
                if(!seen.Add(value)){duplicates++;continue;}
                var mutated=(byte[])bytes.Clone();mutated[at]=value;
                inputs.Add(($"byte {at.ToString(CultureInfo.InvariantCulture)} {original:X2}->{value:X2}",mutated));
            }
        }
        var disagreements=new ConcurrentBag<(string Mutation,int Part,string Legacy,string New)>();
        var skips=new ConcurrentDictionary<string,int>();var outcomes=new ConcurrentDictionary<string,int>();var messageFree=0;
        await Parallel.ForEachAsync(inputs,new ParallelOptions{MaxDegreeOfParallelism=Environment.ProcessorCount},async (input,_)=>
        {
            var magic=input.Bytes.Length>=4?input.Bytes.AsSpan(0,4):[];
            if(magic.SequenceEqual("WDC3"u8)||magic.SequenceEqual("WDC4"u8)){skips.AddOrUpdate(MagicSkip,1,(_,n)=>n+1);return;}
            var otherMagic=magic.Length==4&&!magic.SequenceEqual("WDC5"u8);
            var legacy=await comparer.Legacy(input.Bytes);var current=await comparer.New(input.Bytes);
            outcomes.AddOrUpdate(Canon.Outcome(current),1,(_,n)=>n+1);
            if(otherMagic&&WithoutMagicMessage(legacy,current))Interlocked.Increment(ref messageFree);
            if(Canon.Bytes(legacy).AsSpan().SequenceEqual(Canon.Bytes(current)))return;
            var part=0;while(part<legacy.Count&&part<current.Count&&legacy[part]==current[part])part++;
            disagreements.Add((input.Mutation,part,part<legacy.Count?legacy[part]:"<end>",part<current.Count?current[part]:"<end>"));
        });
        output.WriteLine($"COMPARED {name} inputs={inputs.Count} duplicates={duplicates} skips={skips.Values.Sum()} otherMagicWithoutMessage={messageFree} disagreements={disagreements.Count}");
        output.WriteLine("OUTCOMES "+string.Join(", ",outcomes.OrderBy(o=>o.Key,StringComparer.Ordinal).Select(o=>o.Key+"="+o.Value.ToString(CultureInfo.InvariantCulture))));
        output.WriteLine("SKIPS "+(skips.IsEmpty?"none":string.Join(", ",skips.Select(s=>s.Key+"="+s.Value.ToString(CultureInfo.InvariantCulture)))));
        Assert.All(skips.Keys,reason=>Assert.Equal(MagicSkip,reason));
        if(disagreements.IsEmpty)return;
        // Group by the first differing part with numbers kept, so each distinct disagreement shows once with an example.
        var report=new StringBuilder();
        foreach(var group in disagreements.GroupBy(d=>(Kind(d.Legacy),Kind(d.New))).OrderByDescending(g=>g.Count()))
        {
            var example=group.OrderBy(d=>d.Mutation,StringComparer.Ordinal).First();
            report.AppendLine($"[{group.Count()}x] e.g. {example.Mutation} part {example.Part}\n  legacy: {Cut(example.Legacy)}\n  new:    {Cut(example.New)}");
        }
        Assert.Fail($"{name}: {disagreements.Count} of {inputs.Count} inputs disagree\n{report}");
    }

    // An error part is grouped by code and message; a header, row or completion part by its leading tag.
    private static string Kind(string part)=>part.StartsWith("E ",StringComparison.Ordinal)?part[(part.IndexOf(" code=",StringComparison.Ordinal)+1)..]:part.Length==0?"":part[..1];
    private static string Cut(string part)=>part.Length<=400?part:part[..400]+"...";
}
