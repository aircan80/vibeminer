using System.Buffers.Binary;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class Wdc5RelationshipTests
{
    [Theory]
    [InlineData(0)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)]
    public async Task Exact_section_keys_external_middle_field_zero_parent_and_cross_section_copy_identity_are_preserved(ushort flags)
    {
        var f=Build(flags,[new([900,0],[7,0],Copies:[(100,901)]),new([901],[8],Copies:[(101,100)])]);var sink=new Sink();
        var done=await Read(f,sink);Assert.Equal("wdc5-values-v7",Wdc5TableReader.Revision);
        Assert.Equal(new[]{"ID","Before","Parent","After"},sink.Header!.Columns.Select(c=>c.Name));
        Assert.Equal(new[]{"7","0","8","8","8"},sink.Rows.Select(r=>r.Values[2]));
        Assert.Equal(new uint[]{900,0,901,100,101},sink.Rows.Select(r=>r.Id));
        Assert.Equal(901u,sink.Rows[3].CopySourceId);Assert.Equal(100u,sink.Rows[4].CopySourceId);Assert.True(sink.Rows[4].IsCopy);
        Assert.Equal(new Wdc5Completion(3,3,2,0,0,0,"complete",f.Bytes.Length),done);
    }

    [Theory]
    [InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)]
    public async Task Key_mode_cannot_be_guessed_from_plausible_ordinals_or_equal_IDs(ushort flags)
    {
        var wrongKey=(flags&2)==0?900u:0u;var f=Build(flags,[new([900],[7],Pairs:[(7,wrongKey)])]);
        Assert.Equal("DB2_INVALID",(await Error(f)).Code);
        var otherSection=Build(flags,[new([900],[7],Pairs:[(7,(flags&2)==0?1u:901u)]),new([901],[8])]);
        Assert.Equal("DB2_INVALID",(await Error(otherSection)).Code);
    }

    [Theory]
    [InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)]
    public async Task Inline_relation_stays_physical_and_optional_accelerator_is_cross_checked(ushort flags)
    {
        var f=Build(flags,[new([700,11],[2,3])],external:false);var sink=new Sink();await Read(f,sink);
        Assert.Equal(new[]{"2","3"},sink.Rows.Select(r=>r.Values[2]));
        f=Build(flags,[new([700,11],[2,3],Native:false)],external:false,lookup:0);sink=new();await Read(f,sink);Assert.Equal(2,sink.Rows.Count);
        var key=(flags&2)==0?1u:11u;
        f=Build(flags,[new([700,11],[2,3],Pairs:[(3,key)])],external:false);sink=new();await Read(f,sink);Assert.Equal(2,sink.Rows.Count);
        f=Build(flags,[new([700],[2],Pairs:[(3,(flags&2)==0?0u:700u)])],external:false);
        Assert.Equal("DB2_SCHEMA_MISMATCH",(await Error(f)).Code);
    }

    [Theory]
    [InlineData("<8>",255u,"-1")][InlineData("<8>",4294967295u,"-1")]
    [InlineData("<u16>",65535u,"65535")][InlineData("<u32>",4294967295u,"4294967295")]
    public async Task External_values_preserve_exact_signed_unsigned_wire_representation(string type,uint value,string expected)
    {var f=Build(4,[new([9],[value])],parentType:type);var sink=new Sink();await Read(f,sink);Assert.Equal(expected,Assert.Single(sink.Rows).Values[2]);}

    [Theory]
    [InlineData("<u8>",256u)][InlineData("<8>",511u)][InlineData("<u16>",65536u)]
    public async Task High_bits_do_not_disappear_from_external_relationships(string type,uint value)
    {Assert.Equal("DB2_SCHEMA_MISMATCH",(await Error(Build(4,[new([9],[value])],parentType:type))).Code);}

    [Theory]
    [InlineData("<8>",255u,"-1")][InlineData("<u16>",65535u,"65535")]
    public async Task Narrow_inline_relation_does_not_remove_its_physical_field(string type,uint value,string expected)
    {var sink=new Sink();await Read(Build(4,[new([9],[value])],external:false,parentType:type),sink);Assert.Equal(expected,sink.Rows[0].Values[2]);Assert.Equal("2000",sink.Rows[0].Values[3]);}

    [Theory]
    [InlineData(4)][InlineData(5)]
    public async Task Missing_external_mapping_is_unsupported_and_duplicates_are_not_dictionary_overwrites(ushort flags)
    {
        Assert.Equal("DB2_UNSUPPORTED",(await Error(Build(flags,[new([9],[0],Native:false)]))).Code);
        Assert.Equal("DB2_UNSUPPORTED",(await Error(Build(flags,[new([9,10],[0,1],Pairs:[(0,0)])]))).Code);
        Assert.Equal("DB2_INVALID",(await Error(Build(flags,[new([9,10],[0,1],Pairs:[(0,0),(0,0)])]))).Code);
        Assert.Equal("DB2_INVALID",(await Error(Build(flags,[new([9,10],[0,1],Pairs:[(0,0),(1,0)])]))).Code);
    }

    [Theory]
    [InlineData(0)][InlineData(1)]
    public async Task Genuine_empty_external_table_has_columns_and_zero_rows_with_lookup_zero_or_one(uint lookup)
    {
        var f=Build(4,[],lookup:lookup);var sink=new Sink();var done=await Read(f,sink);Assert.Empty(sink.Rows);Assert.Equal(4,sink.Header!.Columns.Count);Assert.Equal(0,done.PhysicalRows);
        // Pinned writer's actual empty profile retains field metadata but omits24-byte column descriptors.
        var fields=(int)Get(f.Bytes,140);var metadataOnly=f.Bytes[..(204+fields*4)];Put(metadataOnly,188,0);sink=new();
        await Read(new(metadataOnly,f.Definition),sink);Assert.Empty(sink.Rows);Assert.Equal(new[]{"ID","Before","Parent","After"},sink.Header!.Columns.Select(c=>c.Name));
    }

    [Fact]
    public async Task Header_schema_matrix_never_drops_an_unassigned_native_parent()
    {
        var f=Build(4,[new([9],[1])],lookup:0);Assert.Equal("DB2_INVALID",(await Error(f)).Code);
        f=Build(4,[new([9],[1],Native:false)],lookup:0);Assert.Equal("DB2_UNSUPPORTED",(await Error(f)).Code);
        f=Build(4,[new([9],[1])]);var noRelation=WithoutParent(f.Definition);Assert.Equal("DB2_UNSUPPORTED",(await Error(f with{Definition=noRelation})).Code);
        Put(f.Bytes,184,2);Assert.Equal("DB2_UNSUPPORTED",(await Error(f)).Code);
    }

    [Fact]
    public async Task Fixed_relative_strings_are_not_moved_by_relationship_tail_bytes()
    {
        var f=Build(4,[new([900],[2]),new([10],[3])],names:true);var sink=new Sink();await Read(f,sink);
        Assert.Equal(new[]{"a0","a0"},sink.Rows.Select(r=>r.Values[1]));Assert.Equal(new[]{"2","3"},sink.Rows.Select(r=>r.Values[2]));
    }

    [Theory]
    [InlineData(5)][InlineData(7)]
    public async Task Sparse_map_IDs_have_the_exact_flag_dependent_position_around_relationships(ushort flags)
    {
        var f=Build(flags,[new([900,10],[2,3])],names:true);var sink=new Sink();await Read(f,sink);
        Assert.Equal(new[]{"a0","aa1"},sink.Rows.Select(r=>r.Values[1]));Assert.Equal(new[]{"2000","2001"},sink.Rows.Select(r=>r.Values[3]));
        var map=f.Tails[0].MapIds;var relation=f.Tails[0].Relations;
        Assert.Equal(flags==7,map<relation);var swapped=(byte[])f.Bytes.Clone();var first=swapped.AsSpan(map,8).ToArray();swapped.AsSpan(relation,8).CopyTo(swapped.AsSpan(map));first.CopyTo(swapped.AsSpan(relation));
        await Assert.ThrowsAsync<DiscoveryException>(()=>Read(f with{Bytes=swapped},new Sink()));
    }

    [Fact]
    public async Task Inline_Common_is_checked_once_at_physical_origin_while_copy_target_override_is_preserved()
    {
        var f=Build(4,[new([900],[3],Copies:[(111,900)])],external:false,commonOverride:(111,9));var sink=new Sink();
        using var input=new CountedSource(f.Bytes);await Parse(input,f.Definition,sink);
        Assert.Equal(new[]{"3","9"},sink.Rows.Select(r=>r.Values[2]));
        Assert.Equal(20,input.BytesIn(f.Tails[0].Relations,20)); // no repeated relationship scan for the copy
    }

    [Fact]
    public async Task Protected_relationship_tails_are_excluded_without_header_reads_and_copy_scope_stays_explicit()
    {
        var f=Build(4,[new([900],[2],Copies:[(111,901)]),new([901],[3],Key:123,Protected:[901])]);
        var protectedStart=f.Tails[1].Start;Array.Clear(f.Bytes,protectedStart,f.Bytes.Length-protectedStart);
        using var backing=new CountedSource(f.Bytes);using var guarded=new BlteProjectionStream(backing,[new(protectedStart,f.Bytes.Length-protectedStart,0,"0123456789ABCDEF")]);
        var sink=new Sink();var done=await Parse(guarded,f.Definition,sink,new(AllowPartialEncryptedSections:true));
        Assert.Single(sink.Rows);Assert.Equal("2",sink.Rows[0].Values[2]);Assert.Equal(1,done.ExcludedPhysicalRecords);Assert.Equal(1,done.ExcludedCopyRecords);Assert.Equal("unencryptedSections",done.Scope);
        Assert.Equal(0,backing.BytesIn(f.Tails[1].Relations,20));
        using var ordinaryBacking=new MemoryStream(f.Bytes);using var ordinaryHole=new BlteProjectionStream(ordinaryBacking,[new(f.Tails[0].Relations,1,0,"0123456789ABCDEF")]);
        Assert.Equal("DB2_PROJECTION_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Parse(ordinaryHole,f.Definition,new Sink(),new(AllowPartialEncryptedSections:true)))).Code);
    }

    [Fact]
    public async Task External_relationship_and_copy_cycles_missing_sources_depth_and_protected_counts_remain_distinct()
    {
        var f=Build(4,[new([900],[2],Copies:[(111,112),(112,111)])]);Assert.Equal("DB2_INVALID",(await Error(f)).Code);
        f=Build(4,[new([900],[2],Copies:[(111,999)])]);Assert.Equal("DB2_INVALID",(await Error(f)).Code);
        f=Build(4,[new([900],[2],Copies:[(111,112),(112,900)])]);Assert.Equal("DB2_LIMIT",(await Error(f,new(MaxCopyDepth:1))).Code);
        f=Build(6,[new([900],[2],Pairs:[(2,111)],Copies:[(111,900)])]);Assert.Equal("DB2_INVALID",(await Error(f)).Code);
    }

    [Fact]
    public async Task Framing_mismatches_truncation_and_trailing_bytes_never_complete()
    {
        var f=Build(4,[new([900],[2])]);
        for(var length=0;length<f.Bytes.Length;length++)await Assert.ThrowsAsync<DiscoveryException>(()=>Read(f with{Bytes=f.Bytes[..length]},new Sink()));
        foreach(var size in new uint[]{1,11,13,19}){var b=(byte[])f.Bytes.Clone();Put(b,204+28,size);Assert.Equal("DB2_INVALID",(await Error(f with{Bytes=b})).Code);}
        var wrong=(byte[])f.Bytes.Clone();Put(wrong,f.Tails[0].Relations,0);Assert.Equal("DB2_INVALID",(await Error(f with{Bytes=wrong})).Code);
        wrong=f.Bytes.Concat(new byte[]{0}).ToArray();Assert.Equal("DB2_INVALID",(await Error(f with{Bytes=wrong})).Code);
        // These raw min/max words are not an inferred unsigned bounds rule.
        wrong=(byte[])f.Bytes.Clone();Put(wrong,f.Tails[0].Relations+4,0xffffffff);Put(wrong,f.Tails[0].Relations+8,0);await Read(f with{Bytes=wrong},new Sink());
    }

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(9)]
    public async Task Unadmitted_flag_combinations_do_not_guess_record_identity(ushort flags)
    {var f=Build(4,[new([900],[2])]);Set16(f.Bytes,172,flags);Assert.Equal("DB2_UNSUPPORTED",(await Error(f)).Code);}

    [Fact]
    public async Task Schema_relation_family_cardinality_ID_and_position_are_finite()
    {
        var f=Build(4,[new([900],[2])]);
        foreach(var replacement in new[]{"$noninline,relation$Parent<64>","$noninline,relation$Parent<u32>[2]","$noninline,id,relation$Parent<u32>","$noninline$Parent<u32>"})
            Assert.Equal("DB2_UNSUPPORTED",(await Error(f with{Definition=Definition(replacement)})).Code);
        var inlineId=Build(0,[new([900],[2])]);
        var definition=Definition("$noninline,relation$Parent<u32>\n$id$ID<u32>\nBefore<u32>\nAfter<u32>",whole:true);
        Assert.Equal("DB2_UNSUPPORTED",(await Error(inlineId with{Definition=definition})).Code);
    }

    [Fact]
    public async Task Large_multi_section_sparse_reads_are_batched_and_aggregate_caps_precede_pair_reads()
    {
        var ids=Enumerable.Range(10000,12000).Select(i=>(uint)i).ToArray();var parents=ids.Select(i=>i%7).ToArray();
        var f=Build(7,[new(ids,parents),new(ids.Select(i=>i+20000).ToArray(),parents)]);using var input=new CountedSource(f.Bytes);var sink=new Sink(false);
        await Parse(input,f.Definition,sink);Assert.Equal(24000,sink.Count);Assert.InRange(input.MaximumRequest,1,65536);
        Assert.True(f.Bytes.Length>600000);Assert.Equal(12+12000*8,input.BytesIn(f.Tails[0].Relations,12+12000*8));
        var oneBytes=12+12000*8;
        foreach(var limits in new[]{new Wdc5Limits(MaxRelationshipBytes:oneBytes*2-1),new(MaxRelationshipEntries:23999),new(MaxIndexBytes:2_500_000)})
        {
            using var limited=new CountedSource(f.Bytes);var failure=await Assert.ThrowsAsync<DiscoveryException>(()=>Parse(limited,f.Definition,new Sink(false),limits));
            Assert.Equal("DB2_LIMIT",failure.Code);Assert.Equal(0,limited.BytesIn(f.Tails[0].Relations,oneBytes));
        }
    }

    [Fact]
    public async Task Large_pair_scan_cancellation_and_row_backpressure_do_not_leak_or_advance_consumers()
    {
        var ids=Enumerable.Range(100,10000).Select(i=>(uint)i).ToArray();var f=Build(4,[new(ids,ids)]);
        using var cts=new CancellationTokenSource();using var input=new CountedSource(f.Bytes,cancel:cts,cancelAfter:f.Tails[0].Relations+65536);
        var sink=new Sink(false);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Parse(input,f.Definition,sink,token:cts.Token));Assert.Null(sink.Header);Assert.False(input.Closed);
        var small=Build(4,[new([9,10],[0,1])]);using var gatedInput=new CountedSource(small.Bytes);var gate=new GateSink();var running=Parse(gatedInput,small.Definition,gate);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var reads=gatedInput.Reads;await Task.Yield();Assert.Equal(reads,gatedInput.Reads);Assert.False(running.IsCompleted);
        gate.Release.SetResult();await running;Assert.Equal(2,gate.Count);Assert.False(gatedInput.Closed);
    }

    [Fact]
    public async Task Protected_declarations_are_budgeted_without_reading_their_unavailable_headers()
    {
        var f=Build(4,[new([900],[1]),new([901],[2],Key:123,Protected:[901])]);var start=f.Tails[1].Start;
        using var backing=new CountedSource(f.Bytes);using var guarded=new BlteProjectionStream(backing,[new(start,f.Bytes.Length-start,0,"0123456789ABCDEF")]);
        var exception=await Assert.ThrowsAsync<DiscoveryException>(()=>Parse(guarded,f.Definition,new Sink(),new(AllowPartialEncryptedSections:true,MaxRelationshipBytes:39)));
        Assert.Equal("DB2_LIMIT",exception.Code);Assert.Equal(0,backing.BytesIn(f.Tails[1].Relations,20));
        var allProtected=Build(4,[new([901],[2],Key:123,Protected:[901],Native:false)],lookup:0);
        Assert.Equal("DB2_UNSUPPORTED",(await Error(allProtected,new(AllowPartialEncryptedSections:true))).Code);
    }

    [Fact]
    public async Task Duplicate_relation_fields_and_noninteger_or_unverified_external_shapes_remain_unsupported()
    {
        var f=Build(4,[new([900],[1])]);
        foreach(var family in new[]{"float","string"})
        {
            var d=f.Definition with{Blocks=f.Definition.Blocks.Select(b=>b with{Fields=b.Fields.Select(v=>v.Column.Name=="Parent"?v with{Column=v.Column with{Family=family},IntegerBits=null,Unsigned=null}:v).ToArray()}).ToArray()};
            Assert.Equal("DB2_UNSUPPORTED",(await Error(f with{Definition=d})).Code);
        }
        var duplicate=f.Definition with{Blocks=f.Definition.Blocks.Select(b=>b with{Fields=b.Fields.Select(v=>v.Column.Name=="Before"?v with{RelationStorage=true}:v).ToArray()}).ToArray()};
        Assert.Equal("DB2_UNSUPPORTED",(await Error(f with{Definition=duplicate})).Code);
        var unverified=f.Definition with{Blocks=f.Definition.Blocks.Select(b=>b with{Fields=b.Fields.Select(v=>v.Column.Name=="Parent"?v with{Column=v.Column with{VerifiedName=false}}:v).ToArray()}).ToArray()};
        Assert.Equal("DB2_UNSUPPORTED",(await Error(f with{Definition=unverified})).Code);
    }

    [Fact]
    public async Task Consumer_failure_after_provisional_relationship_row_is_observed_without_closing_the_source()
    {
        var f=Build(7,[new([9,10],[1,2])]);using var input=new CountedSource(f.Bytes);var sink=new FailingSink();
        var exception=await Assert.ThrowsAsync<InvalidOperationException>(()=>Parse(input,f.Definition,sink));Assert.Equal("authored consumer failure",exception.Message);Assert.Equal(1,sink.Count);Assert.False(input.Closed);
    }
    private sealed class FailingSink:Sink
    {public override ValueTask RowAsync(Db2Row row,CancellationToken token){Count++;throw new InvalidOperationException("authored consumer failure");}}

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Unverified_inline_narrow_and_widened_raw_fields_keep_values_but_acceleration_requires_explicit_policy(bool widened)
    {
        DbdDocument Unverified(DbdDocument d)=>d with{Blocks=d.Blocks.Select(b=>b with{Fields=b.Fields.Select(v=>v.Column.Name=="Parent"?v with{Column=v.Column with{VerifiedName=false},IntegerBits=8,Unsigned=widened}:v).ToArray()}).ToArray()};
        var f=Build(4,[new([900],[widened?511u:255u],Native:false)],external:false,parentType:widened?"<u32>":"<8>",lookup:0);
        var sink=new Sink();await Read(f with{Definition=Unverified(f.Definition)},sink);
        Assert.Equal(widened?"511":"-1",sink.Rows[0].Values[2]);
        if(widened){Assert.Equal("raw-physical-integer-v1",sink.Header!.Columns[2].Interpretation!.Policy);Assert.Equal(32,sink.Header.Columns[2].Interpretation!.PhysicalBits);}
        else Assert.Null(sink.Header!.Columns[2].Interpretation);
        var accelerated=Build(4,[new([900],[widened?511u:255u],Pairs:[(widened?511u:uint.MaxValue,0)])],external:false,parentType:widened?"<u32>":"<8>");
        Assert.Equal("DB2_UNSUPPORTED",(await Error(accelerated with{Definition=Unverified(accelerated.Definition)})).Code);
    }

    [Fact]
    public async Task External_relationship_schema_width_is_strict_even_when_verified_raw_evidence_is_requested()
    {
        var f=Build(4,[new([9],[300])],parentType:"<8>");
        Assert.Equal("DB2_SCHEMA_MISMATCH",(await Error(f,new(DefinitionSha256:new('a',64),VerifiedRawIntegerRules:[new(new('a',64),"11223344","01020304",2,"Parent",0,32,8,false)]))).Code);
    }

    private sealed record Spec(uint[] Ids,uint[] Parents,(uint Parent,uint Key)[]? Pairs=null,bool Native=true,(uint Target,uint Source)[]? Copies=null,ulong Key=0,uint[]? Protected=null);
    private sealed record Tail(int Start,int Relations,int MapIds);
    private sealed record Fixture(byte[] Bytes,DbdDocument Definition,Tail[]? Layouts=null)
    {public Tail[] Tails=>Layouts??[];}
    private static Fixture Build(ushort flags,Spec[] sections,bool external=true,string parentType="<u32>",uint lookup=1,bool names=false,(uint Id,uint Value)? commonOverride=null)
    {
        var sparse=(flags&1)!=0;var indexed=(flags&4)!=0;var secondary=(flags&2)!=0;var parentWidth=parentType.Contains("8>")?1:parentType.Contains("16>")?2:4;
        var physical=new List<(int Width,bool Name,bool Common)>();if(!indexed)physical.Add((4,false,false));physical.Add((4,names,false));if(!external)physical.Add((parentWidth,false,commonOverride is not null));physical.Add((4,false,false));
        var recordSize=physical.Where(p=>!p.Common).Sum(p=>p.Width);var fields=physical.Count;var aux=commonOverride is null?0:8;
        var prefixBytes=sections.Where(s=>s.Key!=0).Sum(s=>4+(s.Protected?.Length??0)*4);var start=204+sections.Length*40+fields*28+aux+prefixBytes;
        var bodies=new List<byte[]>();var tails=new List<Tail>();var headers=new List<byte[]>();var totalRecords=sections.Sum(s=>s.Ids.Length);var global=0;var allStrings=0;
        foreach(var section in sections)
        {
            using var body=new MemoryStream();using var w=new BinaryWriter(body);var maps=new List<(uint Offset,ushort Size)>();var strings=new MemoryStream();strings.WriteByte(0);
            for(var i=0;i<section.Ids.Length;i++)
            {
                var before=body.Position;if(!indexed)w.Write(section.Ids[i]);
                var name=new string('a',i%3+1)+i;
                if(names&&sparse){w.Write(Encoding.UTF8.GetBytes(name));w.Write((byte)0);}
                else if(names){var stringAt=allStrings+(int)strings.Position;w.Write((totalRecords-(global+i))*recordSize+stringAt);strings.Write(Encoding.UTF8.GetBytes(name+"\0"));}
                else w.Write((uint)(1000+i));
                if(!external&&commonOverride is null){var value=section.Parents[i];if(parentWidth==1)w.Write((byte)value);else if(parentWidth==2)w.Write((ushort)value);else w.Write(value);}
                w.Write((uint)(2000+i));if(sparse)while((body.Position-before)%4!=0)w.Write((byte)0);
                maps.Add(((uint)(start+before),(ushort)(body.Position-before)));
            }
            var recordsEnd=(int)(start+body.Position);var stringBytes=names&&!sparse?strings.ToArray():[];w.Write(stringBytes);allStrings+=stringBytes.Length;
            if(indexed)foreach(var id in section.Ids)w.Write(id);
            foreach(var copy in section.Copies??[]){w.Write(copy.Target);w.Write(copy.Source);}
            if(sparse)foreach(var map in maps){w.Write(map.Offset);w.Write(map.Size);}
            var mapIdsAt=0;if(sparse&&secondary){mapIdsAt=start+(int)body.Position;foreach(var id in section.Ids)w.Write(id);}
            var relationsAt=start+(int)body.Position;var pairBytes=0;
            if(section.Native)
            {
                var pairs=section.Pairs??section.Ids.Select((id,i)=>(section.Parents[i],secondary?id:(uint)i)).ToArray();
                w.Write((uint)pairs.Length);w.Write(0u);w.Write(uint.MaxValue);foreach(var pair in pairs){w.Write(pair.Item1);w.Write(pair.Item2);}pairBytes=12+pairs.Length*8;
            }
            if(sparse&&!secondary){mapIdsAt=start+(int)body.Position;foreach(var id in section.Ids)w.Write(id);}
            var sh=new byte[40];BinaryPrimitives.WriteUInt64LittleEndian(sh,section.Key);Put(sh,8,(uint)start);Put(sh,12,(uint)section.Ids.Length);Put(sh,16,(uint)stringBytes.Length);Put(sh,20,sparse?(uint)recordsEnd:0);Put(sh,24,indexed?(uint)section.Ids.Length*4:0);Put(sh,28,(uint)pairBytes);Put(sh,32,sparse?(uint)section.Ids.Length:0);Put(sh,36,(uint)(section.Copies?.Length??0));headers.Add(sh);tails.Add(new(start,relationsAt,mapIdsAt));bodies.Add(body.ToArray());start+=(int)body.Length;global+=section.Ids.Length;
        }
        var bytes=new byte[start];"WDC5"u8.CopyTo(bytes);Put(bytes,4,5);Put(bytes,136,(uint)totalRecords);Put(bytes,140,(uint)fields);Put(bytes,144,(uint)recordSize);Put(bytes,148,(uint)allStrings);Put(bytes,152,0x11223344);Put(bytes,156,0x01020304);Set16(bytes,172,flags);Put(bytes,176,(uint)fields);Put(bytes,184,lookup);Put(bytes,188,(uint)fields*24);Put(bytes,192,(uint)aux);Put(bytes,200,(uint)sections.Length);
        for(var i=0;i<headers.Count;i++)headers[i].CopyTo(bytes,204+i*40);
        var metadata=204+sections.Length*40;var offset=0;for(var i=0;i<fields;i++)
        {
            var p=physical[i];Set16(bytes,metadata+i*4,(ushort)(32-p.Width*8));Set16(bytes,metadata+i*4+2,(ushort)offset);var at=metadata+fields*4+i*24;Set16(bytes,at,(ushort)(offset*8));Set16(bytes,at+2,(ushort)(p.Common?0:p.Width*8));Put(bytes,at+4,p.Common?8u:0);Put(bytes,at+8,p.Common?2u:0);Put(bytes,at+12,p.Common?sections[0].Parents[0]:0);if(!p.Common)offset+=p.Width;
        }
        var position=metadata+fields*28;if(commonOverride is{}co){Put(bytes,position,co.Id);Put(bytes,position+4,co.Value);position+=8;}
        foreach(var section in sections.Where(s=>s.Key!=0)){Put(bytes,position,(uint)(section.Protected?.Length??0));position+=4;foreach(var id in section.Protected??[]){Put(bytes,position,id);position+=4;}}
        for(var i=0;i<bodies.Count;i++)bodies[i].CopyTo(bytes,tails[i].Start);
        var fieldsText=(indexed?"$noninline,id$ID<u32>":"$id$ID<u32>")+"\nBefore"+(names?"":"<u32>")+"\n"+(external?"$noninline,relation$":"$relation$")+"Parent"+parentType+"\nAfter<u32>";
        return new(bytes,Definition(fieldsText,true,names),tails.ToArray());
    }
    private static DbdDocument Definition(string field,bool whole=false,bool names=false)
    {
        var body=whole?field:"$noninline,id$ID<u32>\nBefore<u32>\n"+field+"\nAfter<u32>";
        return DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\nint ID\n"+(names?"string":"int")+" Before\nint Parent\nint After\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n"+body+"\n"));
    }
    private static DbdDocument WithoutParent(DbdDocument d)=>d with{Blocks=d.Blocks.Select(b=>b with{Fields=b.Fields.Where(f=>f.Column.Name!="Parent").ToArray()}).ToArray()};
    private static Task<Wdc5Completion> Read(Fixture f,IWdc5Consumer sink,Wdc5Limits? limits=null)
    {return Parse(new CountedSource(f.Bytes),f.Definition,sink,limits);}
    private static Task<Wdc5Completion> Parse(Stream input,DbdDocument d,IWdc5Consumer sink,Wdc5Limits? limits=null,CancellationToken token=default)=>Wdc5TableReader.ReadAsync(input,d,"1.2.3.4",limits??new(),sink,token);
    private static Task<DiscoveryException> Error(Fixture f,Wdc5Limits? limits=null)=>Assert.ThrowsAsync<DiscoveryException>(()=>Read(f,new Sink(),limits));
    private static uint Get(byte[] b,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static void Put(byte[] b,int at,uint n)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),n);
    private static void Set16(byte[] b,int at,ushort n)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at),n);
    private class Sink(bool collect=true):IWdc5Consumer
    {
        public Wdc5TableHeader? Header;public List<Db2Row> Rows=[];public int Count;
        public ValueTask HeaderAsync(Wdc5TableHeader header,CancellationToken token){Header=header;return ValueTask.CompletedTask;}
        public virtual ValueTask RowAsync(Db2Row row,CancellationToken token){Count++;if(collect)Rows.Add(row);return ValueTask.CompletedTask;}
    }
    private sealed class GateSink:Sink
    {
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask RowAsync(Db2Row row,CancellationToken token){await base.RowAsync(row,token);if(Count==1){Entered.SetResult();await Release.Task.WaitAsync(token);}}
    }
    private sealed class CountedSource(byte[] bytes,CancellationTokenSource? cancel=null,int cancelAfter=int.MaxValue):Stream
    {
        private readonly MemoryStream inner=new(bytes);private readonly List<(long Start,int Count)> reads=[];public int Reads=>reads.Count;public int MaximumRequest;public bool Closed;
        public int BytesIn(int start,int count)=>(int)reads.Sum(r=>Math.Max(0,Math.Min(r.Start+r.Count,start+(long)count)-Math.Max(r.Start,start)));
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override async ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken token=default)
        {MaximumRequest=Math.Max(MaximumRequest,target.Length);var first=Position;var n=await inner.ReadAsync(target[..Math.Min(target.Length,8191)],token);reads.Add((first,n));if(Position>=cancelAfter)cancel?.Cancel();token.ThrowIfCancellationRequested();return n;}
        public override int Read(byte[] b,int o,int c)=>throw new InvalidOperationException("Sync read forbidden");public override long Seek(long o,SeekOrigin s)=>inner.Seek(o,s);public override void Flush()=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){Closed=true;if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
