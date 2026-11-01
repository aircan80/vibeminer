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
// - The row, relationship-entry and index-budget defaults (D293): the harness gives the new reader the legacy values.
// - The legacy CanSeek check: LegacyStream is always seekable.
// - Hole ordinal uniqueness vs non-empty name: holes are not mutated and always get unique ordinals and non-empty names.
// - A read touching a hole: LegacyStream throws the BlteProjectionStream code and message, which the new reader copies.
// - An unverified relation column (D290): the legacy reader refuses an unverified external relation and an accelerator on an
//   unverified inline one; the new reader reads them. Every relation fixture here names Parent verified and the DBD text is
//   never mutated, so no input reaches it.
// - An absent external parent (D291): the legacy reader refuses a readable section with fewer pairs than rows or no block;
//   the new reader reads those rows as "" with DB2_RELATION_ABSENT. Every external relation fixture here pairs every row,
//   and a single-byte mutation can't drop a pair: the block size and the pair count must both change and agree.
// - The relationship pair count (D292): Blizzard writes one extra pair per copy row, so the new reader admits up to rows +
//   copies pairs per section and refuses a larger count as DB2_INVALID; the legacy reader refuses any count above the row
//   count as DB2_LIMIT "WDC5 relationship entries exceeds policy.". Mutating the count byte reaches this. Only that legacy
//   outcome paired with the new reader's DB2_INVALID count refusal (above rows + copies, or a count that no longer matches
//   its block size) at the same row count is accepted, counted under PairCountDiffers. A single-byte mutation can't make
//   the new reader open such an input: the block size and the count would both have to change and agree. Repeated keys
//   keep the legacy message, and no base fixture has copied rows with extra pairs, so nothing else differs.
// - The magic: the new reader also reads WDC3 and WDC4, so an input starting with "WDC3" or "WDC4" is skipped and counted
//   under MagicSkip. For any other magic the error message is left out only when the legacy outcome is DB2_UNSUPPORTED
//   "Only WDC5 is supported." and the new outcome is also DB2_UNSUPPORTED: the new reader names the refused magic. Every
//   other outcome, and every input shorter than 4 bytes, is compared in full.
// - Fixed string arrays (D295, #189): the legacy reader refuses every string array with DB2_UNSUPPORTED "Only scalar
//   uncompressed relative strings are admitted.". The new reader reads one in a non-Sparse table whose field has storage
//   kind 0 and 32-bit elements. An input is skipped and counted under StringArraySkip only when the fixture marks its
//   string array field, the legacy outcome is exactly that error with no rows, the new outcome is anything else, and the
//   input's own flags and storage for that field are the admitted shape. Every other outcome is compared in full.
// Skips is a counted list per reason; MagicSkip and StringArraySkip are the only reasons allowed. PairCountDiffers is counted and printed apart.
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
        if(StringArrayAdmitted(fixture,fixture.Bytes,legacy,current)){output.WriteLine("SKIP "+StringArraySkip);Assert.Equal(fixture.Expect,Canon.Outcome(current));return;}
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

    // D292: the legacy count-above-rows LIMIT against the new reader's INVALID count refusal, same row count, nothing else.
    private static readonly string LegacyPairCount=" code=\"DB2_LIMIT\" message="+System.Text.Json.JsonSerializer.Serialize("WDC5 relationship entries exceeds policy.");
    private static readonly string[] NewPairCount=["The relationship count exceeds the section's rows plus copies.","The relationship count does not consume its exact declared block."];
    private static bool PairCountDiffers(List<string> legacy,List<string> current)
    {
        if(legacy.Count!=1||current.Count!=1||!legacy[0].StartsWith("E ",StringComparison.Ordinal)||!legacy[0].EndsWith(LegacyPairCount,StringComparison.Ordinal))return false;
        var prefix=legacy[0][..^LegacyPairCount.Length];
        return NewPairCount.Any(m=>current[0]==prefix+" code=\"DB2_INVALID\" message="+System.Text.Json.JsonSerializer.Serialize(m));
    }

    private const string StringArraySkip="fixed 32-bit uncompressed string array: only the new reader reads it (D295, #189)";
    private static readonly string LegacyStringArrayError=CodeUnsupported+" message="+System.Text.Json.JsonSerializer.Serialize("Only scalar uncompressed relative strings are admitted.");
    // True only for the D295 difference: the legacy reader refused the marked string array before any row, the new reader did
    // not stop there, and the input is non-Sparse with kind 0 and 32-bit elements for that field.
    private static bool StringArrayAdmitted(Fixture fixture,byte[] bytes,List<string> legacy,List<string> current)
    {
        if(fixture.StringArray is not int field||legacy.Count!=1||!legacy[0].StartsWith("E rows=0 ",StringComparison.Ordinal)||!legacy[0].EndsWith(LegacyStringArrayError,StringComparison.Ordinal))return false;
        if(current.Count==1&&current[0]==legacy[0])return false;
        if(bytes.Length<204)return false;
        var flags=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(172));
        var sections=(long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(200));var fields=(long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140));
        var structure=204+sections*40+field*4L;var info=204+sections*40+fields*4+field*24L;
        if((flags&1)!=0||field>=fields||structure+4>bytes.Length||info+24>bytes.Length)return false;
        var width=32-System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan((int)structure));
        if(width<=0)width=(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)info+16));
        return width==32&&System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)info+8))==0;
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
        var skips=new ConcurrentDictionary<string,int>();var outcomes=new ConcurrentDictionary<string,int>();var messageFree=0;var pairCount=0;
        await Parallel.ForEachAsync(inputs,new ParallelOptions{MaxDegreeOfParallelism=Environment.ProcessorCount},async (input,_)=>
        {
            var magic=input.Bytes.Length>=4?input.Bytes.AsSpan(0,4):[];
            if(magic.SequenceEqual("WDC3"u8)||magic.SequenceEqual("WDC4"u8)){skips.AddOrUpdate(MagicSkip,1,(_,n)=>n+1);return;}
            var otherMagic=magic.Length==4&&!magic.SequenceEqual("WDC5"u8);
            var legacy=await comparer.Legacy(input.Bytes);var current=await comparer.New(input.Bytes);
            outcomes.AddOrUpdate(Canon.Outcome(current),1,(_,n)=>n+1);
            if(StringArrayAdmitted(fixture,input.Bytes,legacy,current)){skips.AddOrUpdate(StringArraySkip,1,(_,n)=>n+1);return;}
            if(otherMagic&&WithoutMagicMessage(legacy,current))Interlocked.Increment(ref messageFree);
            if(Canon.Bytes(legacy).AsSpan().SequenceEqual(Canon.Bytes(current)))return;
            if(PairCountDiffers(legacy,current)){Interlocked.Increment(ref pairCount);return;}
            var part=0;while(part<legacy.Count&&part<current.Count&&legacy[part]==current[part])part++;
            disagreements.Add((input.Mutation,part,part<legacy.Count?legacy[part]:"<end>",part<current.Count?current[part]:"<end>"));
        });
        output.WriteLine($"COMPARED {name} inputs={inputs.Count} duplicates={duplicates} skips={skips.Values.Sum()} otherMagicWithoutMessage={messageFree} pairCountDiffers={pairCount} disagreements={disagreements.Count}");
        output.WriteLine("OUTCOMES "+string.Join(", ",outcomes.OrderBy(o=>o.Key,StringComparer.Ordinal).Select(o=>o.Key+"="+o.Value.ToString(CultureInfo.InvariantCulture))));
        output.WriteLine("SKIPS "+(skips.IsEmpty?"none":string.Join(", ",skips.Select(s=>s.Key+"="+s.Value.ToString(CultureInfo.InvariantCulture)))));
        Assert.All(skips.Keys,reason=>Assert.Contains(reason,new[]{MagicSkip,StringArraySkip}));
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
