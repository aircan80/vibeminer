using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;
public sealed class ContentProjectionIdentityTests
{
    private static readonly string CKey = new('a',32), EKey = new('b',32);
    private static ContentProjection Proof() => new(ContentProjectionIdentity.Policy,"not-authoritative",
        [new("0","8",new('c',64)),new("12","4",new('d',64))], [new("8","4",1,"0123456789ABCDEF")]);
    private static string Hash(ContentProjection proof) => ContentProjectionIdentity.Compute(CKey,EKey,"16",proof);
    [Fact]
    public void Projection_identity_binds_source_spans_holes_and_policy_without_claiming_full_decoded_hash()
    {
        var proof=Proof();var identity=Hash(proof);Assert.Equal(64,identity.Length);
        Assert.Equal(identity,Hash(proof with{IdentitySha256=identity}));
        Assert.NotEqual(identity,ContentProjectionIdentity.Compute(new('1',32),EKey,"16",proof));
        Assert.NotEqual(identity,ContentProjectionIdentity.Compute(CKey,new('2',32),"16",proof));
        Assert.NotEqual(identity,Hash(proof with{ReadableRanges=[proof.ReadableRanges[0] with{Sha256=new('e',64)},proof.ReadableRanges[1]]}));
        Assert.NotEqual(identity,Hash(proof with{UnavailableRanges=[proof.UnavailableRanges[0] with{KeyName="1123456789ABCDEF"}]}));
    }
    [Fact]
    public void Empty_clear_chunks_may_leave_gaps_in_original_encrypted_chunk_ordinals()
    {
        var proof=Proof();var later=proof with{UnavailableRanges=[proof.UnavailableRanges[0] with{ChunkOrdinal=3780}]};
        Assert.NotEqual(Hash(proof),Hash(later));
    }
    [Theory]
    [InlineData("gap")][InlineData("overlap")][InlineData("order")][InlineData("ordinal")]
    [InlineData("ordinalOverflow")][InlineData("zero")][InlineData("noncanonical")][InlineData("badkey")][InlineData("badsha")]
    [InlineData("overflow")][InlineData("policy")][InlineData("noholes")][InlineData("budget")]
    public void Incomplete_or_misbound_projection_geometry_is_refused(string problem)
    {
        var p=Proof();p=problem switch
        {
            "gap"=>p with{ReadableRanges=[p.ReadableRanges[0] with{ByteLength="7"},p.ReadableRanges[1]]},
            "overlap"=>p with{UnavailableRanges=[p.UnavailableRanges[0] with{Offset="7"}]},
            "order"=>p with{ReadableRanges=p.ReadableRanges.Reverse().ToArray()},
            "ordinal"=>p with{UnavailableRanges=[p.UnavailableRanges[0] with{ChunkOrdinal=0}]},
            "ordinalOverflow"=>p with{UnavailableRanges=[p.UnavailableRanges[0] with{ChunkOrdinal=65_536}]},
            "zero"=>p with{UnavailableRanges=[p.UnavailableRanges[0] with{ByteLength="0"}]},
            "noncanonical"=>p with{UnavailableRanges=[p.UnavailableRanges[0] with{Offset="08"}]},
            "badkey"=>p with{UnavailableRanges=[p.UnavailableRanges[0] with{KeyName="0123456789abcdef"}]},
            "badsha"=>p with{ReadableRanges=[p.ReadableRanges[0] with{Sha256=new('C',64)},p.ReadableRanges[1]]},
            "overflow"=>p with{UnavailableRanges=[p.UnavailableRanges[0] with{ByteLength="9223372036854775807"}]},
            "policy"=>p with{Policy="invented"},
            "noholes"=>p with{UnavailableRanges=[]},
            _=>p with{ReadableRanges=Enumerable.Repeat(p.ReadableRanges[0],129).ToArray()}
        };
        Assert.Equal("CONTENT_PROJECTION_INVALID",Assert.Throws<DiscoveryException>(()=>Hash(p)).Code);
    }
    [Fact]
    public void Fully_encrypted_encoded_retention_can_have_no_readable_spans_without_becoming_readable_data()
    {
        var p=Proof() with{ReadableRanges=[],UnavailableRanges=[new("0","16",0,"0123456789ABCDEF")]};
        Assert.Equal(64,Hash(p).Length);
    }
}
