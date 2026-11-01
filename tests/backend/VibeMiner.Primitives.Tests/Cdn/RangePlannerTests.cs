using VibeMiner.Cdn;
using Xunit;

namespace VibeMiner.Primitives.Tests.Cdn;

public sealed class RangePlannerTests
{
    private static CdnKey K(int n) { var b = new byte[16]; b[0] = (byte)(n >> 8); b[1] = (byte)n; b[15] = 0xEE; return new(b); }
    private static readonly CdnKey A = K(1000), B = K(1001), C = K(1002);
    private static PlanObject At(int id, CdnKey archive, long offset, long size, long? declared = null) =>
        new(K(id), declared, [new ArchiveLocation(archive, offset, size)]);
    private static PlanObject Loose(int id, long? size = null) => new(K(id), size, []);
    private const long MiB = 1 << 20;

    [Fact]
    public void IndexMembershipAloneDecidesRangedOrLoose()
    {
        // A large indexed object is still ranged; a tiny unlisted one is still loose.
        var plan = RangePlanner.Plan([At(1, A, 0, 3 * MiB), Loose(2, 10), Loose(3)]);
        Assert.Equal(K(1), Assert.Single(Assert.Single(plan.Ranges).Members).EKey);
        Assert.Equal([new PlannedLoose(K(2), 10), new PlannedLoose(K(3), null)], plan.Loose);
        Assert.Empty(plan.Rejected);
    }

    [Fact]
    public void MembersAreSortedByOffsetAndMergedAcrossSmallGaps()
    {
        var plan = RangePlanner.Plan([At(3, A, 5000, 100), At(1, A, 0, 1000), At(2, A, 1000, 2000)], options: new() { GapBytes = 2000 });
        var range = Assert.Single(plan.Ranges);
        Assert.Equal((0L, 5100L), (range.Offset, range.Length));
        Assert.Equal([K(1), K(2), K(3)], range.Members.Select(m => m.EKey));
        Assert.Equal([0L, 1000L, 5000L], range.Members.Select(m => m.OffsetInRange));
        Assert.Equal(2000, range.GapBytes);
    }

    [Fact]
    public void AGapOverTheThresholdStartsANewRange()
    {
        var plan = RangePlanner.Plan([At(1, A, 0, 100), At(2, A, 100 + 4096 + 1, 100)], options: new() { GapBytes = 4096 });
        Assert.Equal(2, plan.Ranges.Count);
        var gapZero = RangePlanner.Plan([At(1, A, 0, 100), At(2, A, 100, 100), At(3, A, 201, 1)], options: new() { GapBytes = 0 });
        Assert.Equal([2, 1], gapZero.Ranges.Select(r => r.Members.Count));
    }

    [Fact]
    public void RangesCloseAtTheTargetAndNeverPassTheCap()
    {
        // 3 MiB objects back to back: 3, 6, then 9 (>= 8 MiB target) closes the range.
        var objects = Enumerable.Range(0, 7).Select(i => At(i, A, i * 3 * MiB, 3 * MiB)).ToArray();
        var plan = RangePlanner.Plan(objects);
        Assert.Equal([9 * MiB, 9 * MiB, 3 * MiB], plan.Ranges.Select(r => r.Length));
        Assert.All(plan.Ranges, r => Assert.True(r.Length <= 16 * MiB));

        // 7 MiB then 10 MiB: merging would make 17 MiB, over the cap.
        var capped = RangePlanner.Plan([At(1, A, 0, 7 * MiB), At(2, A, 7 * MiB, 10 * MiB)]);
        Assert.Equal(2, capped.Ranges.Count);

        // One object larger than the cap is a range on its own.
        var huge = RangePlanner.Plan([At(1, A, 0, 20 * MiB), At(2, A, 20 * MiB, 1)]);
        Assert.Equal([20 * MiB, 1L], huge.Ranges.Select(r => r.Length));
    }

    [Fact]
    public void OverlappingSpansAreBothRejectedAndTheRestStillPlans()
    {
        var plan = RangePlanner.Plan([At(1, A, 0, 100), At(2, A, 50, 100), At(3, A, 1000, 10), At(4, A, 140, 5)]);
        Assert.Equal([K(1), K(2), K(4)], plan.Rejected.Select(r => r.EKey).Order());
        Assert.All(plan.Rejected, r => Assert.Equal(RangePlanner.Overlap, r.Code));
        Assert.Equal(K(3), Assert.Single(Assert.Single(plan.Ranges).Members).EKey);
    }

    [Fact]
    public void TheSameSpanInTwoArchivesIsNotAnOverlap()
    {
        var plan = RangePlanner.Plan([At(1, A, 0, 100), At(2, B, 0, 100)]);
        Assert.Empty(plan.Rejected);
        Assert.Equal(2, plan.Ranges.Count);
    }

    [Fact]
    public void AnEKeyWithTwoSizesFailsAloneWhereverTheSizesComeFrom()
    {
        var plan = RangePlanner.Plan([
            At(1, A, 0, 100, declared: 101),                                             // EN vs index
            new(K(2), null, [new(A, 200, 50), new(B, 0, 51)]),                          // two indexes
            Loose(3, 10), Loose(3, 11),                                                  // listed twice
            At(4, A, 300, 10)]);
        Assert.Equal([K(1), K(2), K(3)], plan.Rejected.Select(r => r.EKey).Order());
        Assert.All(plan.Rejected, r => Assert.Equal(RangePlanner.SizeConflict, r.Code));
        Assert.Equal(K(4), Assert.Single(Assert.Single(plan.Ranges).Members).EKey);
        Assert.Empty(plan.Loose);
    }

    [Fact]
    public void AnEKeyInTwoArchivesGetsOneLocationTheEarliestInTheConfig()
    {
        var obj = new PlanObject(K(1), 100, [new(A, 500, 100), new(B, 0, 100), new(B, 900, 100)]);
        var byConfig = RangePlanner.Plan([obj, At(2, A, 0, 10)], archiveOrder: [C, B, A]);
        Assert.Equal([B, A], byConfig.Ranges.Select(r => r.Archive));
        Assert.Equal(K(1), Assert.Single(byConfig.Ranges[0].Members).EKey);
        Assert.Equal(0, byConfig.Ranges[0].Offset);
        Assert.Single(byConfig.Ranges.SelectMany(r => r.Members), m => m.EKey == K(1));

        // Repeating an identical location is not a second location.
        var repeated = RangePlanner.Plan([At(1, A, 0, 100), At(1, A, 0, 100, declared: 100)]);
        Assert.Empty(repeated.Rejected);
        Assert.Single(Assert.Single(repeated.Ranges).Members);
    }

    [Fact]
    public void InvalidLocationsAndOptionsAreRefused()
    {
        var plan = RangePlanner.Plan([At(1, A, -1, 10), At(2, A, long.MaxValue - 5, 10), At(3, A, 0, 0), Loose(4, 0)]);
        Assert.Equal(4, plan.Rejected.Count);
        Assert.All(plan.Rejected, r => Assert.Equal(RangePlanner.LocationInvalid, r.Code));
        Assert.Throws<ArgumentException>(() => RangePlanner.Plan([], options: new() { TargetBytes = 10, CapBytes = 5 }));
        Assert.Throws<ArgumentException>(() => RangePlanner.Plan([], options: new() { GapBytes = -1 }));
    }

    [Fact]
    public void SplitCutsTheReplyIntoExactMemberSlicesAndDropsTheGaps()
    {
        var plan = RangePlanner.Plan([At(1, A, 10, 4), At(2, A, 20, 3)], options: new() { GapBytes = 100 });
        var range = Assert.Single(plan.Ranges);
        var reply = Enumerable.Range(10, 13).Select(i => (byte)i).ToArray();   // archive bytes 10..22
        var parts = RangePlanner.Split(range, reply);
        Assert.Equal([10, 11, 12, 13], parts[0].Bytes.ToArray());
        Assert.Equal([20, 21, 22], parts[1].Bytes.ToArray());
        Assert.Equal([K(1), K(2)], parts.Select(p => p.EKey));
        Assert.Throws<ArgumentException>(() => RangePlanner.Split(range, reply.AsMemory(1)));
    }

    [Fact]
    public void ManyObjectsPlanDeterministically()
    {
        var rnd = new Random(5);
        var objects = new List<PlanObject>();
        long offset = 0;
        for (var i = 0; i < 5000; i++) { var size = rnd.Next(1, 300_000); offset += rnd.Next(0, 600_000); objects.Add(At(i, i % 2 == 0 ? A : B, offset, size)); offset += size; }
        var shuffled = objects.OrderBy(_ => rnd.Next()).ToArray();
        var one = RangePlanner.Plan(objects, [A, B]);
        var two = RangePlanner.Plan(shuffled, [A, B]);
        Assert.Equal(one.Ranges.Select(r => (r.Archive, r.Offset, r.Length)), two.Ranges.Select(r => (r.Archive, r.Offset, r.Length)));
        Assert.Equal(5000, one.Ranges.Sum(r => r.Members.Count));
        Assert.All(one.Ranges, r => Assert.True(r.Length <= 16 * MiB));
        Assert.All(one.Ranges.Zip(one.Ranges.Skip(1)).Where(p => p.First.Archive == p.Second.Archive), p => Assert.True(p.Second.Offset >= p.First.Offset + p.First.Length));
    }
}
