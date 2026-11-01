using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ModelComparison;

/// <summary>One synthetic model/SKIN pair. <see cref="Expect"/> lists "stage:outcome" facts the summary must contain, so a
/// fixture can't silently stop reaching the reader path its name claims.</summary>
internal sealed record ModelCase(string Name,byte[] Model,byte[] Skin,params string[] Expect)
{
    public override string ToString()=>Name;
}

/// <summary>Authored M2/SKIN bytes only; no game content. The builders are copies of VibeMiner.Formats.Tests/Models/ModelFixtures.cs,
/// with a header-size switch so the flagged 312-byte combiner header can carry a combiner span.</summary>
internal static class ModelFixtures
{
    public static readonly ModelCase[] All=Build();

    /// <summary>The fixtures where layer 5 deliberately differs from the legacy copy (D401): the rule each must match, and
    /// the legacy outcome it replaces. Every other fixture must give identical parts.</summary>
    public static readonly IReadOnlyDictionary<string,(string Rule,string LegacyExpect)> Deliberate=new Dictionary<string,(string,string)>(StringComparer.Ordinal)
    {
        ["274-empty-coordinate-lookup"]=(ModelDifferences.EmptyCoordinates,"model:ok skin:MODEL_INVALID geometry:ok appearance.plan:ok artifact:ok"),
        ["272-empty-coordinate-lookup"]=(ModelDifferences.EmptyCoordinates,"model:ok skin:MODEL_INVALID geometry:ok appearance.plan:ok artifact:ok"),
        ["274-empty-coordinate-lookup-index-5"]=(ModelDifferences.EmptyCoordinates,"model:ok skin:MODEL_INVALID geometry:ok artifact:ok"),
        ["274-skin-header-cdcd-60-bones"]=(ModelDifferences.UnsetMaximumBones,"model:ok skin:MODEL_LIMIT geometry:MODEL_LIMIT"),
        ["272-skin-header-cdcd-60-bones"]=(ModelDifferences.UnsetMaximumBones,"model:ok skin:MODEL_LIMIT geometry:MODEL_LIMIT"),
        ["274-skin-header-cdcd-257-bones"]=(ModelDifferences.UnsetMaximumBones,"model:ok skin:MODEL_LIMIT geometry:MODEL_LIMIT"),
        ["274-skin-header-cdcd-palette-past-lookup"]=(ModelDifferences.UnsetMaximumBones,"model:ok skin:MODEL_LIMIT geometry:MODEL_LIMIT"),
        ["274-skin-header-cdcd-empty-coordinate-lookup"]=(ModelDifferences.UnsetMaximumBones,"model:ok skin:MODEL_LIMIT geometry:MODEL_LIMIT"),
    };

    private const string Full="model:ok skin:ok skin.static:ok skin.materials:ok geometry:ok geometry.static:ok appearance.model:ok appearance.plan:ok appearance.read:ok artifact:ok";

    private static ModelCase[] Build()
    {
        var list=new List<ModelCase>();
        void Add(string name,byte[] model,byte[]? skin=null,params string[] expect)=>list.Add(new(name,model,skin??Skin(),expect));
        // Readable models along every compared path.
        Add("274-chunked",Model(),null,Full);
        Add("272-chunked",Model(272),Skin(272),Full);
        Add("264-unchunked",Model(264),Skin(264),"model:ok","skin:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","geometry:MODEL_UNSUPPORTED");
        Add("274-active-channels",Model(activeChannels:true),null,Full);
        Add("274-two-combos-two-batches",Model(twoCombos:true),Skin(twoBatches:true),"skin:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:MaterialUnsupported@1");
        Add("274-shadow-batches",Model(),Skin(shadows:true),"skin:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","artifact:ok");
        Add("274-creature-texture-type-11",Model(firstTextureType:11),null,"skin.materials:ok","appearance.plan:ok");
        Add("274-texture-type-1",Model(firstTextureType:1),null,"skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:MaterialUnsupported@0");
        Add("274-five-vertices",Model(vertices:5),null,Full);
        Add("274-six-triangle-indices",Model(),Skin(triangleCount:6),Full);
        Add("274-sfid-with-lod",Chunks(("SFID",Words32(900,950,951)),("TXID",Words32(700,0)),("MD21",Body())),null,Full);
        Add("274-txid-all-zero",Chunks(("SFID",Words32(900)),("TXID",Words32(0,0)),("MD21",Body())),null,"skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:ok");
        Add("274-skid",Model().Concat(Chunks(("SKID",Words32(800)))).ToArray(),null,"model:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","geometry:MODEL_UNSUPPORTED");
        Add("274-bfid-zeros",Model().Concat(Chunks(("BFID",Words32(0,0)))).ToArray(),null,Full);
        Add("274-bfid-file",Model().Concat(Chunks(("BFID",Words32(0,801)))).ToArray(),null,"geometry:MODEL_UNSUPPORTED");
        Add("274-afid",Model().Concat(Chunks(("AFID",new byte[]{5,0,2,0,0x22,3,0,0,6,0,0,0,0,0,0,0}))).ToArray(),null,Full);
        Add("274-skid-bfid-afid",Model().Concat(Chunks(("SKID",Words32(800)),("BFID",Words32(0,801)),("AFID",new byte[]{5,0,2,0,0x22,3,0,0}))).ToArray(),null,"model:ok","geometry:MODEL_UNSUPPORTED");
        Add("274-unknown-chunk",Model().Concat(Chunks(("LDV1",new byte[8]))).ToArray(),null,"model:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","artifact:ok");
        Add("274-combiner-header-empty",Modern(Body(header:312)),null,Full.Replace("skin.materials:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED",StringComparison.Ordinal));
        Add("274-combiner-header-two",Modern(Body(header:312,combiners:2)),null,"model:ok","artifact:ok");
        Add("274-combiner-header-only",Chunks(("MD21",Header312())),Skin56Empty(),"model:ok","skin:ok","skin.static:MODEL_UNSUPPORTED","geometry:ok","artifact:GEOMETRY_ARTIFACT_INVALID");
        // One fixture per remaining appearance refusal. Batch, material and lookup positions come from the descriptors.
        Add("274-blend-mode-8",Modern(Mutate(Body(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,116)+2),8))),null,"skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:BlendUnsupported@0","appearance.read:BlendUnsupported@0");
        Add("274-only-blended-layer",Modern(Mutate(Body(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,116)+2),2))),null,"skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:BlendUnsupported@0");
        Add("274-unproven-texture-weight",Modern(Mutate(Body(activeChannels:true),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,148)),0))),null,"skin:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:TextureWeight@0");
        Add("274-uv-transform",Modern(Mutate(Body(activeChannels:true),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,156)),0))),null,"skin:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:TextureTransform@0");
        Add("274-unsupported-combiner",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,40)+2),0x8001)),"skin:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","appearance.plan:CombinerUnsupported@0");
        Add("274-three-texture-stages",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,40)+14),3)),"skin:MODEL_INVALID","appearance.plan:MultiTexture@0");
        var bone=new byte[88];BinaryPrimitives.WriteUInt16LittleEndian(bone.AsSpan(8),ushort.MaxValue);BinaryPrimitives.WriteSingleLittleEndian(bone.AsSpan(76),9);
        Add("274-root-bone",Modern(Append(Body(),44,bone,1)),null,Full);
        var billboard=(byte[])bone.Clone();U32(billboard,4,8);
        Add("274-billboard-bone",Modern(Append(Body(),44,billboard,1)),null,"skin.materials:MODEL_MATERIAL_UNSUPPORTED","artifact:ok");
        var flagged=Body();U32(flagged,16,1);
        Add("274-global-flag",Modern(flagged),null,"skin.materials:MODEL_MATERIAL_UNSUPPORTED","artifact:ok");
        Add("274-particle-declared",Modern(Append(Body(),296,[1],1)),null,"skin.materials:MODEL_MATERIAL_UNSUPPORTED","artifact:ok");

        // D097 bone palettes: chunked 272/274 SKINs may store header maximum 0 with 60-bone sections; a 264 one is checked
        // against its header. The model declares a 60-entry bone lookup so the palette range itself is valid.
        Add("274-skin-header-0-60-bones",Palette(274),Skin(bones:60),Full);
        Add("272-skin-header-0-60-bones",Palette(272),Skin(272,bones:60),Full);
        Add("264-skin-header-60-60-bones",Palette(264),Skin(264,bones:60,maxBones:60),"model:ok","skin:ok","geometry:MODEL_UNSUPPORTED");
        Add("264-skin-header-0-60-bones",Palette(264),Skin(264,bones:60),"model:ok","skin:MODEL_INVALID","geometry:MODEL_UNSUPPORTED");
        Add("264-skin-header-59-60-bones",Palette(264),Skin(264,bones:60,maxBones:59),"skin:MODEL_INVALID");
        Add("274-skin-header-59-60-bones",Palette(274),Skin(bones:60,maxBones:59),"skin:MODEL_INVALID","geometry:MODEL_INVALID");
        Add("274-skin-header-60-60-bones",Palette(274),Skin(bones:60,maxBones:60),Full);
        Add("274-skin-header-0-257-bones",Palette(274,257),Skin(bones:257),"skin:MODEL_LIMIT","geometry:MODEL_LIMIT");
        Add("274-skin-palette-past-lookup",Palette(274,59),Skin(bones:60),"skin:MODEL_INVALID","geometry:MODEL_INVALID");

        // D401 empty texture-coordinate lookup (MD20 136): chunked 272/274 now read the SKIN, 264 and a short lookup still refuse.
        Add("274-empty-coordinate-lookup",Modern(Mutate(Body(),b=>U32(b,136,0))),null,Full.Replace("skin.materials:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED",StringComparison.Ordinal));
        Add("272-empty-coordinate-lookup",Modern(Mutate(Body(272),b=>U32(b,136,0))),Skin(272),Full.Replace("skin.materials:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED",StringComparison.Ordinal));
        Add("274-empty-coordinate-lookup-index-5",Modern(Mutate(Body(),b=>U32(b,136,0))),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,40)+18),5)),"skin:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED","artifact:ok");
        Add("264-empty-coordinate-lookup",Mutate(Body(264),b=>U32(b,136,0)),Skin(264),"model:ok","skin:MODEL_INVALID","geometry:MODEL_UNSUPPORTED");
        Add("274-short-coordinate-lookup",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,40)+18),1)),"skin:MODEL_INVALID","geometry:ok","artifact:ok");
        // D401 SKIN maximum-bones fill 0xCDCDCDCD: read like 0 on chunked 272/274 only; any other large value still refuses.
        Add("274-skin-header-cdcd-60-bones",Palette(274),Skin(bones:60,maxBones:0xCDCDCDCD),Full);
        Add("272-skin-header-cdcd-60-bones",Palette(272),Skin(272,bones:60,maxBones:0xCDCDCDCD),Full);
        Add("274-skin-header-cdcd-257-bones",Palette(274,257),Skin(bones:257,maxBones:0xCDCDCDCD),"skin:MODEL_LIMIT","geometry:MODEL_LIMIT");
        Add("274-skin-header-cdcd-palette-past-lookup",Palette(274,59),Skin(bones:60,maxBones:0xCDCDCDCD),"skin:MODEL_INVALID","geometry:MODEL_INVALID");
        Add("274-skin-header-cdcd-empty-coordinate-lookup",Modern(Mutate(Body(),b=>U32(b,136,0))),Skin(maxBones:0xCDCDCDCD),Full.Replace("skin.materials:ok","skin.materials:MODEL_MATERIAL_UNSUPPORTED",StringComparison.Ordinal));
        Add("264-skin-header-cdcd-60-bones",Palette(264),Skin(264,bones:60,maxBones:0xCDCDCDCD),"model:ok","skin:MODEL_LIMIT","geometry:MODEL_UNSUPPORTED");
        Add("274-skin-header-cdcdcdcc",Palette(274),Skin(bones:60,maxBones:0xCDCDCDCC),"skin:MODEL_LIMIT","geometry:MODEL_LIMIT");
        Add("272-skin-header-cdcdcdce",Palette(272),Skin(272,bones:60,maxBones:0xCDCDCDCE),"skin:MODEL_LIMIT","geometry:MODEL_LIMIT");

        // Model refusals.
        Add("model-empty",[],null,"model:MODEL_INVALID");
        Add("model-3-bytes",Model()[..3],null,"model:MODEL_INVALID");
        Add("model-chunk-header-cut",Model()[..6],null,"model:MODEL_INVALID");
        Add("model-md20-header-cut",Model(264)[..200],null,"model:MODEL_INVALID");
        Add("model-chunked-trailing-3",Model().Concat(new byte[3]).ToArray(),null,"model:MODEL_INVALID");
        Add("model-unchunked-274",Body(274),null,"model:MODEL_UNSUPPORTED");
        Add("model-unchunked-272",Body(272),null,"model:MODEL_UNSUPPORTED");
        Add("model-chunked-264",Modern(Body(264)),null,"model:MODEL_UNSUPPORTED");
        Add("model-chunked-273",Modern(Body(273)),null,"model:MODEL_UNSUPPORTED");
        Add("model-chunked-275",Modern(Body(275)),null,"model:MODEL_UNSUPPORTED");
        Add("model-duplicate-sfid",Model().Concat(Chunks(("SFID",Words32(900)))).ToArray(),null,"model:MODEL_INVALID");
        Add("model-lowercase-chunk-id",Model().Concat(Chunks(("sfid",Words32(900)))).ToArray(),null,"model:MODEL_UNSUPPORTED");
        Add("model-no-md21",Chunks(("SFID",Words32(900)),("TXID",Words32(700,0))),null,"model:MODEL_UNSUPPORTED");
        Add("model-md21-not-md20",Chunks(("MD21",Mutate(Body(),b=>b[0]=(byte)'X'))),null,"model:MODEL_INVALID");
        Add("model-chunk-past-source",Mutate(Model(),b=>U32(b,4,1000)),null,"model:MODEL_INVALID");
        Add("model-partial-txid",Chunks(("SFID",Words32(900)),("TXID",Words32(700,0)[..6]),("MD21",Body())),null,"model:MODEL_INVALID");
        Add("model-short-txid",Chunks(("SFID",Words32(900)),("TXID",Words32(700)),("MD21",Body())),null,"model:MODEL_INVALID");
        Add("model-long-txid",Chunks(("SFID",Words32(900)),("TXID",Words32(700,0,1)),("MD21",Body())),null,"model:MODEL_INVALID");
        Add("model-no-txid",Chunks(("SFID",Words32(900)),("MD21",Body())),null,"model:MODEL_UNSUPPORTED");
        Add("model-no-sfid",Chunks(("TXID",Words32(700,0)),("MD21",Body())),null,"model:MODEL_UNSUPPORTED");
        Add("model-empty-sfid",Chunks(("SFID",[]),("TXID",Words32(700,0)),("MD21",Body())),null,"model:MODEL_INVALID");
        Add("model-partial-sfid",Chunks(("SFID",Words32(900)[..3]),("TXID",Words32(700,0)),("MD21",Body())),null,"model:MODEL_INVALID");
        Add("model-sfid-over-limit",Chunks(("SFID",Words32([..Enumerable.Range(900,33).Select(i=>(uint)i)])),("TXID",Words32(700,0)),("MD21",Body())),null,"model:MODEL_LIMIT");
        Add("model-partial-afid",Model().Concat(Chunks(("AFID",[1]))).ToArray(),null,"model:MODEL_INVALID");
        Add("model-two-skid",Model().Concat(Chunks(("SKID",Words32(1,2)))).ToArray(),null,"model:MODEL_INVALID");
        Add("model-partial-bfid",Model().Concat(Chunks(("BFID",[1,0]))).ToArray(),null,"model:MODEL_INVALID");
        Add("model-129-chunks",Model().Concat(Chunks([..Enumerable.Range(0,126).Select(i=>("X"+i.ToString("D3",System.Globalization.CultureInfo.InvariantCulture),Array.Empty<byte>()))])).ToArray(),null,"model:MODEL_LIMIT");
        Add("model-vertices-over-limit",Modern(Mutate(Body(),b=>U32(b,60,1_000_001))),null,"model:MODEL_LIMIT");
        Add("model-vertices-past-source",Modern(Mutate(Body(),b=>U32(b,60,1000))),null,"model:MODEL_INVALID");
        Add("model-textures-over-limit",Modern(Mutate(Body(),b=>U32(b,80,33))),null,"model:MODEL_LIMIT");
        Add("model-views-over-limit",Modern(Mutate(Body(),b=>U32(b,68,33))),null,"model:MODEL_LIMIT");
        Add("model-name-over-limit",Modern(Mutate(Body(),b=>U32(b,8,4097))),null,"model:MODEL_LIMIT");
        Add("model-span-in-header",Modern(Mutate(Body(),b=>U32(b,84,100))),null,"model:MODEL_INVALID");
        Add("model-name-no-nul",Modern(Mutate(Body(),b=>b[(int)U32(b,12)+8]=(byte)'x')),null,"model:MODEL_INVALID");
        Add("model-name-inner-nul",Modern(Mutate(Body(),b=>b[(int)U32(b,12)+2]=0)),null,"model:MODEL_INVALID");
        Add("model-name-not-utf8",Modern(Mutate(Body(),b=>b[(int)U32(b,12)]=0xFF)),null,"model:MODEL_UNSUPPORTED");
        Add("model-vertex-nan",Modern(Mutate(Body(),b=>BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan((int)U32(b,64)+4),float.NaN))),null,"model:MODEL_INVALID");
        Add("model-bound-inverted",Modern(Mutate(Body(),b=>BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(160),5))),null,"model:MODEL_INVALID");
        Add("model-bound-negative-radius",Modern(Mutate(Body(),b=>BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(184),-1))),null,"model:MODEL_INVALID");
        Add("model-collision-box-empty-with-triangles",Modern(Mutate(Body(),b=>{BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(188),1);U32(b,216,3);})),null,"model:MODEL_INVALID");
        var badParent=(byte[])bone.Clone();BinaryPrimitives.WriteUInt16LittleEndian(badParent.AsSpan(8),1);
        Add("model-bone-parent-out-of-range",Modern(Append(Body(),44,badParent,1)),null,"model:MODEL_INVALID");
        var selfParent=(byte[])bone.Clone();BinaryPrimitives.WriteUInt16LittleEndian(selfParent.AsSpan(8),0);
        Add("model-bone-parent-self",Modern(Append(Body(),44,selfParent,1)),null,"model:MODEL_INVALID");
        Add("model-flagged-header-cut",Modern(Mutate(Body(),b=>U32(b,16,8))),null,"model:MODEL_INVALID");
        Add("model-combiners-over-limit",Modern(Mutate(Body(header:312),b=>U32(b,304,65_537))),null,"model:MODEL_LIMIT");

        // SKIN refusals, against the readable 274 model.
        Add("skin-empty",Model(),[],"skin:MODEL_INVALID","geometry:MODEL_INVALID");
        Add("skin-header-cut",Model(),Skin()[..40],"skin:MODEL_INVALID");
        Add("skin-body-cut",Model(),Skin()[..^4],"skin:MODEL_INVALID");
        Add("skin-bad-magic",Model(),Mutate(Skin(),b=>b[0]=(byte)'X'),"skin:MODEL_UNSUPPORTED");
        Add("skin-264-for-274-model",Model(),Skin(264),"skin:MODEL_INVALID");
        Add("skin-triangles-not-triples",Model(),Skin(triangleCount:4),"skin:MODEL_INVALID");
        Add("skin-lookup-past-vertices",Model(vertices:3),null,"skin:MODEL_INVALID");
        Add("skin-properties-mismatch",Model(),Mutate(Skin(),b=>U32(b,20,3)),"skin:MODEL_INVALID");
        Add("skin-max-bones-over-limit",Model(),Mutate(Skin(),b=>U32(b,44,257)),"skin:MODEL_LIMIT");
        Add("skin-section-past-lookup",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,32)+6),5)),"skin:MODEL_INVALID");
        Add("skin-section-negative-radius",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan((int)U32(b,32)+44),-1)),"skin:MODEL_INVALID");
        Add("skin-section-influences",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,32)+16),5)),"skin:MODEL_INVALID");
        Add("skin-batch-bad-material",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,40)+10),7)),"skin:MODEL_INVALID","geometry:ok","appearance.plan:MaterialUnsupported@0");
        Add("skin-batch-bad-section",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,40)+4),3)),"skin:MODEL_INVALID","geometry:ok");
        Add("skin-batch-textures-over-limit",Model(),Mutate(Skin(),b=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)U32(b,40)+14),33)),"skin:MODEL_LIMIT","geometry:ok");
        Add("skin-batches-over-limit",Model(),Mutate(Skin(),b=>U32(b,36,513)),"skin:MODEL_LIMIT","geometry:MODEL_LIMIT");
        Add("skin-trailing-byte",Model(),Skin().Concat(new byte[1]).ToArray(),"skin:ok","geometry:ok");
        return [..list];
    }

    // Bases for the systematic truncations and byte flips.
    public static readonly (string Name,byte[] Model,byte[] Skin)[] Bases=
    [
        ("274",Model(),Skin()),("272",Model(272),Skin(272)),("264",Model(264),Skin(264)),
        ("274-combiner-header",Modern(Body(header:312,combiners:2)),Skin()),
        ("274-palette-header-0",Palette(274),Skin(bones:60)),("272-palette-header-0",Palette(272),Skin(272,bones:60)),
        ("264-palette-header-60",Palette(264),Skin(264,bones:60,maxBones:60)),
        ("274-empty-coordinate-lookup",Modern(Mutate(Body(),b=>U32(b,136,0))),Skin()),("274-palette-header-cdcd",Palette(274),Skin(bones:60,maxBones:0xCDCDCDCD)),
        ("274-deps",Chunks(("SFID",Words32(900,950)),("TXID",Words32(700,0)),("MD21",Append(Body(twoCombos:true),44,RootBone(),1)),("BFID",Words32(0,0)),("AFID",new byte[]{5,0,2,0,0x22,3,0,0})),Skin(twoBatches:true,shadows:true)),
    ];

    // A model whose bone lookup has <paramref name="entries"/> entries, all naming bone 0.
    private static byte[] Palette(uint version,int entries=60)
    {var body=Append(Body(version),120,new byte[entries*2],entries);return version==264?body:Modern(body);}
    private static byte[] RootBone(){var bone=new byte[88];BinaryPrimitives.WriteUInt16LittleEndian(bone.AsSpan(8),ushort.MaxValue);return bone;}
    private static byte[] Header312(){var body=new byte[312];"MD20"u8.CopyTo(body);U32(body,4,274);U32(body,16,8);return body;}
    private static byte[] Skin56Empty(){var skin=new byte[56];"SKIN"u8.CopyTo(skin);return skin;}
    private static byte[] Mutate(byte[] bytes,Action<byte[]> change){change(bytes);return bytes;}

    internal static byte[] Modern(byte[] body)=>Chunks(("SFID",Words32(900,901)),("TXID",Words32(700,0)),("MD21",body));
    internal static byte[] Append(byte[] body,int at,byte[] payload,int count)
    {var result=body.Concat(payload).ToArray();U32(result,at,(uint)count);U32(result,at+4,(uint)body.Length);return result;}
    // Authored files: offsets are explicit native-format facts, with deliberately asymmetric geometry.
    internal static byte[] Model(uint version=274,uint firstTextureType=0,bool activeChannels=false,bool twoCombos=false,int vertices=4)
    {
        var body=Body(version,firstTextureType,activeChannels,twoCombos,vertices);
        return version==264?body:Chunks(("SFID",Words32(900,901)),("TXID",Words32(700,0)),("MD21",body));
    }
    /// <summary>The Formats.Tests body. <paramref name="header"/> 312 sets global flag 8 and writes the combiner span
    /// descriptor at 304 (<paramref name="combiners"/> words).</summary>
    internal static byte[] Body(uint version=274,uint firstTextureType=0,bool activeChannels=false,bool twoCombos=false,int vertices=4,int header=304,int combiners=0)
    {
        using var output=new MemoryStream();output.SetLength(header);output.Position=header;using var w=new BinaryWriter(output,Encoding.UTF8,true);
        var descriptors=new List<(int At,int Count,int Offset)>();
        void A(int at,int count,Action<BinaryWriter> write){descriptors.Add((at,count,(int)output.Position));write(w);}
        A(8,9,x=>x.Write(Encoding.UTF8.GetBytes("authored\0")));
        A(60,vertices,x=>{for(var i=0;i<vertices;i++){var p=i switch{0=>Vector3.Zero,1=>new Vector3(2,0,0),2=>new Vector3(0,3,0),_=>new Vector3(50,60,70)};x.Write(p.X);x.Write(p.Y);x.Write(p.Z);x.Write(0x04030201u);x.Write(0x08070605u);x.Write(0f);x.Write(0f);x.Write(1f);x.Write(i==1?1f:0f);x.Write(i==2?1f:0f);x.Write(0.25f);x.Write(0.75f);}});
        A(80,2,x=>{x.Write(firstTextureType);x.Write(3u);x.Write(0L);x.Write(11u);x.Write(0u);x.Write(0L);});
        A(112,1,x=>x.Write(0u));
        A(128,twoCombos?2:1,x=>{x.Write((ushort)0);if(twoCombos)x.Write((ushort)1);});
        A(136,twoCombos?2:1,x=>{x.Write((ushort)0);if(twoCombos)x.Write((ushort)0);});
        A(144,1,x=>x.Write(ushort.MaxValue));
        A(152,twoCombos?2:1,x=>{x.Write(ushort.MaxValue);if(twoCombos)x.Write(ushort.MaxValue);});
        if(activeChannels){A(72,1,x=>x.Write(new byte[40]));A(88,1,x=>x.Write(new byte[20]));A(96,1,x=>x.Write(new byte[60]));}
        if(header==312&&combiners>0)A(304,combiners,x=>{for(var i=0;i<combiners;i++)x.Write((ushort)i);});
        var bytes=output.ToArray();"MD20"u8.CopyTo(bytes);U32(bytes,4,version);U32(bytes,68,1);if(header==312)U32(bytes,16,8);
        foreach(var d in descriptors){U32(bytes,d.At,(uint)d.Count);U32(bytes,d.At+4,(uint)d.Offset);}return bytes;
    }
    /// <summary><paramref name="bones"/> is the one section's bone count; <paramref name="maxBones"/> is the raw header maximum (D097).</summary>
    internal static byte[] Skin(uint version=274,int triangleCount=3,bool shadows=false,bool twoBatches=false,ushort bones=0,uint maxBones=0)
    {
        var header=version==264?48:56;using var output=new MemoryStream();output.SetLength(header);output.Position=header;
        using var w=new BinaryWriter(output,Encoding.UTF8,true);var ds=new List<(int At,int Count,int Offset)>();
        void A(int at,int count,Action<BinaryWriter> write){ds.Add((at,count,(int)output.Position));write(w);}
        A(4,4,x=>{foreach(var v in new ushort[]{2,0,1,3})x.Write(v);});
        A(12,triangleCount,x=>{for(var i=0;i<triangleCount;i++)x.Write((ushort)(i%3));});
        A(20,4,x=>{for(var i=0;i<4;i++)x.Write(0x03020100u);});
        A(28,1,x=>{var s=new byte[48];U16(s,6,4);U16(s,10,3);U16(s,12,bones);x.Write(s);});
        A(36,twoBatches?2:1,x=>{var batch=new byte[24];U16(batch,8,ushort.MaxValue);U16(batch,14,1);x.Write(batch);if(twoBatches){batch[1]=unchecked((byte)-3);x.Write(batch);}});
        if(shadows&&header==56)A(48,1,x=>x.Write(new byte[12]));
        var bytes=output.ToArray();"SKIN"u8.CopyTo(bytes);U32(bytes,44,maxBones);foreach(var d in ds){U32(bytes,d.At,(uint)d.Count);U32(bytes,d.At+4,(uint)d.Offset);}return bytes;
    }
    internal static byte[] Chunks(params (string Id,byte[] Payload)[] chunks)
    {using var output=new MemoryStream();using var w=new BinaryWriter(output);foreach(var c in chunks){w.Write(Encoding.ASCII.GetBytes(c.Id));w.Write((uint)c.Payload.Length);w.Write(c.Payload);}return output.ToArray();}
    internal static byte[] Words32(params uint[] words){var bytes=new byte[words.Length*4];for(var i=0;i<words.Length;i++)U32(bytes,i*4,words[i]);return bytes;}
    private static void U32(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
    private static uint U32(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void U16(byte[] bytes,int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at),value);
}
