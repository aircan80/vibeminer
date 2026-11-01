using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VibeMiner.Domain;
using Xunit;
using Xunit.Abstractions;
using L = VibeMiner.Formats.Wow;
using N = VibeMiner.Formats.Models;

namespace ModelComparison;

/// <summary>#200: the layer 5 M2/SKIN readers, appearance admission and VMG1 writer must give the same canonical outcome
/// as the legacy Formats.Wow copies on every fixture, every truncation and a fixed set of byte flips, except the two
/// deliberate D401 changes, which <see cref="ModelDifferences"/> pins exactly.</summary>
// Both sides read plain MemoryStreams. The one intended difference, the protected-range refusal (legacy: an
// IBlteProjectionSource with unavailable ranges; layer 5: a FileReadStream with holes), can't be reached with them and is
// out of scope here. Nothing is skipped: every outcome is compared in full.
public sealed class ModelComparisonTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names()
    {var data=new TheoryData<string>();foreach(var fixture in ModelFixtures.All)data.Add(fixture.Name);return data;}

    public static TheoryData<int> BaseIndices()
    {var data=new TheoryData<int>();for(var i=0;i<ModelFixtures.Bases.Length;i++)data.Add(i);return data;}

    [Fact]
    public void Corpus_has_at_least_60_distinct_fixtures()
    {
        Assert.True(ModelFixtures.All.Length>=60);
        Assert.Equal(ModelFixtures.All.Length,ModelFixtures.All.Select(f=>f.Name).Distinct().Count());
        Assert.Equal(ModelFixtures.All.Length,ModelFixtures.All.Select(f=>Convert.ToHexString(f.Model)+"|"+Convert.ToHexString(f.Skin)).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Fixture_gives_the_expected_outcome_and_identical_canonical_parts(string name)
    {
        var fixture=ModelFixtures.All.Single(f=>f.Name==name);
        var legacy=await ModelPipeline.Legacy(fixture.Model,fixture.Skin);var current=await ModelPipeline.New(fixture.Model,fixture.Skin);
        output.WriteLine(string.Join("\n",current));
        if(ModelFixtures.Deliberate.TryGetValue(name,out var deliberate))
        {
            // The legacy side must still give the old refusal, and the difference must be exactly the named rule.
            Assert.Equal(deliberate.Rule,await ModelDifferences.ClassifyAsync(fixture.Model,fixture.Skin,legacy,current));
            var old=" "+ModelPipeline.Summary(legacy)+" ";
            foreach(var fact in deliberate.LegacyExpect.Split(' '))Assert.Contains(" "+fact,old,StringComparison.Ordinal);
        }
        else Assert.Equal(string.Join("\n",legacy),string.Join("\n",current));
        var summary=" "+ModelPipeline.Summary(current)+" ";output.WriteLine(summary);
        foreach(var expect in fixture.Expect)foreach(var fact in expect.Split(' '))Assert.Contains(" "+fact,summary,StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BaseIndices))]
    public async Task Every_truncation_of_model_and_skin_gives_identical_canonical_parts(int index)
    {
        var (name,model,skin)=ModelFixtures.Bases[index];var cases=0;var outcomes=new SortedDictionary<string,int>(StringComparer.Ordinal);
        for(var length=0;length<model.Length;length++){await Same(name+" model["+length+"]",model[..length],skin,outcomes);cases++;}
        for(var length=0;length<skin.Length;length++){await Same(name+" skin["+length+"]",model,skin[..length],outcomes);cases++;}
        Report(name,cases,outcomes);
    }

    [Theory]
    [MemberData(nameof(BaseIndices))]
    public async Task Fixed_seed_byte_flips_of_model_and_skin_give_identical_canonical_parts(int index)
    {
        var (name,model,skin)=ModelFixtures.Bases[index];var cases=0;var outcomes=new SortedDictionary<string,int>(StringComparer.Ordinal);
        for(var seed=1;seed<=400;seed++)
        {
            var random=new Random(seed*31+index);var flipModel=seed%2==1;var bytes=(flipModel?model:skin).ToArray();
            var flips=1+seed%3;var label=name+(flipModel?" model":" skin")+" seed "+seed;
            for(var i=0;i<flips;i++){var at=random.Next(bytes.Length);bytes[at]^=(byte)random.Next(1,256);label+=" @"+at;}
            await Same(label,flipModel?bytes:model,flipModel?skin:bytes,outcomes);cases++;
        }
        Report(name,cases,outcomes);
    }

    [Theory]
    [InlineData("274-empty-coordinate-lookup")][InlineData("274-skin-header-cdcd-60-bones")]
    public async Task Deliberate_rules_refuse_any_other_difference(string name)
    {
        var fixture=ModelFixtures.All.Single(f=>f.Name==name);
        var legacy=await ModelPipeline.Legacy(fixture.Model,fixture.Skin);var current=await ModelPipeline.New(fixture.Model,fixture.Skin);
        Assert.Equal(ModelFixtures.Deliberate[name].Rule,await ModelDifferences.ClassifyAsync(fixture.Model,fixture.Skin,legacy,current));
        // A different legacy message, or a change outside the steps the rule frees, is not explained.
        var otherMessage=legacy.Select(p=>p.StartsWith("skin ",StringComparison.Ordinal)?p.Replace("lookup.","lookup!",StringComparison.Ordinal).Replace("256.","255.",StringComparison.Ordinal):p).ToList();
        Assert.Null(await ModelDifferences.ClassifyAsync(fixture.Model,fixture.Skin,otherMessage,current));
        var otherModel=current.Select((p,i)=>i==0?p+" ":p).ToList();
        Assert.Null(await ModelDifferences.ClassifyAsync(fixture.Model,fixture.Skin,legacy,otherModel));
        // Nothing after the freed refusal is unchecked: a changed later step (the artifact, or the strict skin's static
        // geometry) is not explained either.
        var otherArtifact=current.Select((p,i)=>i==current.Count-1?p+"00":p).ToList();
        Assert.Null(await ModelDifferences.ClassifyAsync(fixture.Model,fixture.Skin,legacy,otherArtifact));
        var otherStatic=current.Select(p=>p.StartsWith("skin.static ",StringComparison.Ordinal)?p.Replace("Indices=[","Indices=[9,",StringComparison.Ordinal):p).ToList();
        Assert.NotEqual(current,otherStatic);Assert.Null(await ModelDifferences.ClassifyAsync(fixture.Model,fixture.Skin,legacy,otherStatic));
        var otherSkin=(byte[])fixture.Skin.Clone();otherSkin[44]^=1;
        Assert.Null(await ModelDifferences.ClassifyAsync(name.Contains("cdcd",StringComparison.Ordinal)?fixture.Model:ModelFixtures.Model(),otherSkin,legacy,current));
    }

    [Fact]
    public void Combiner_names_and_admission_agree_for_every_shader_id_and_stage_count()
    {
        for(var count=0;count<=3;count++)
            for(var shader=0;shader<=ushort.MaxValue;shader++)
            {
                var id=(ushort)shader;
                var l=L.M2Combiners.Admit(count,id,out var legacyLabel);var n=N.M2Combiners.Admit(count,id,out var label);
                var legacy=ModelCanon.Value(L.M2Combiners.Name(count,id))+"|"+ModelCanon.Value(l)+"|"+legacyLabel;
                var current=ModelCanon.Value(N.M2Combiners.Name(count,id))+"|"+ModelCanon.Value(n)+"|"+label;
                if(legacy!=current)Assert.Fail($"count {count} shader {shader}: {legacy} != {current}");
            }
        foreach(var combiner in Enum.GetValues<N.M2Combiner>().Cast<int>().Append(0).Append(6))
            Assert.Equal(ModelCanon.Value(L.M2Combiners.Profile((L.M2Combiner)combiner)),ModelCanon.Value(N.M2Combiners.Profile((N.M2Combiner)combiner)));
    }

    [Fact]
    public void Published_revisions_and_policies_agree()
    {
        // D401 bumps only the strict SKIN reader; the geometry reader, VMG1 binding and material policy keep their values.
        Assert.Equal("m2-skin-native-v2",L.M2SkinReader.Revision);Assert.Equal("m2-skin-native-v3",N.M2SkinReader.Revision);
        Assert.Equal("m2-skin-geometry-v1",N.M2GeometrySkinReader.Revision);
        Assert.Equal("m2-opaque-shader0-flags0-uv0-absent-channels-v1",N.M2StaticDrawSet.MaterialPolicy);
        Assert.Equal(
            [L.M2ModelReader.Revision,L.M2GeometrySkinReader.Revision,L.M2GeometrySkin.InterpretationPolicy,L.M2StaticGeometry.CoordinatePolicy,
             L.M2StaticDrawSet.MaterialPolicy,L.M2Appearance.ReaderRevision,L.M2Appearance.MaterialPolicy,L.M2Appearance.AlphaReference.ToString(CultureInfo.InvariantCulture),
             L.ModelGeometryArtifacts.Revision,L.ModelGeometryArtifacts.LinePolicy,ModelCanon.Value(new L.M2Limits())],
            [N.M2ModelReader.Revision,N.M2GeometrySkinReader.Revision,N.M2GeometrySkin.InterpretationPolicy,N.M2StaticGeometry.CoordinatePolicy,
             N.M2StaticDrawSet.MaterialPolicy,N.M2Appearance.ReaderRevision,N.M2Appearance.MaterialPolicy,N.M2Appearance.AlphaReference.ToString(CultureInfo.InvariantCulture),
             N.ModelGeometryArtifacts.Revision,N.ModelGeometryArtifacts.LinePolicy,ModelCanon.Value(new N.M2Limits())]);
    }

    private static async Task Same(string label,byte[] model,byte[] skin,SortedDictionary<string,int> outcomes)
    {
        var legacy=await ModelPipeline.Legacy(model,skin);var current=await ModelPipeline.New(model,skin);
        // A truncation or flip may land on a D401 case (a flipped coordinate count of 0, say). It may differ only as
        // ModelDifferences allows: the legacy refusal is exactly the old message and the input has the exact D401 shape.
        var rule=await ModelDifferences.ClassifyAsync(model,skin,legacy,current);
        if(rule is null)Assert.Fail(label+"\nlegacy:\n"+string.Join("\n",legacy)+"\nnew:\n"+string.Join("\n",current));
        var summary=(rule==ModelDifferences.Identical?"":rule+" ")+ModelPipeline.Summary(current);outcomes[summary]=outcomes.GetValueOrDefault(summary)+1;
    }
    private void Report(string name,int cases,SortedDictionary<string,int> outcomes)
    {
        output.WriteLine(name+": "+cases.ToString(CultureInfo.InvariantCulture)+" cases");
        foreach(var (summary,count) in outcomes)output.WriteLine(count.ToString(CultureInfo.InvariantCulture).PadLeft(5)+"  "+summary);
    }
}

/// <summary>The two deliberate D401 differences between the legacy readers and layer 5. Each is keyed by the input's exact
/// shape and the legacy side's exact old refusal; anything else that differs is a failure.</summary>
internal static class ModelDifferences
{
    public const string Identical="identical",EmptyCoordinates="d401-empty-coordinate-lookup",UnsetMaximumBones="d401-unset-maximum-bones";
    private const uint Fill=0xCDCDCDCD;
    private static readonly string ComboRange=Line("skin","MODEL_INVALID","A SKIN batch combo range is outside its model lookup.");
    private static readonly string FillLimit="code="+JsonSerializer.Serialize("MODEL_LIMIT")+" message="
        +JsonSerializer.Serialize("The SKIN file's bone palette has 3,452,816,845 bones; geometry reads at most 256.");
    private static string Line(string stage,string code,string message)=>stage+" code="+JsonSerializer.Serialize(code)+" message="+JsonSerializer.Serialize(message);

    /// <summary><see cref="Identical"/>, the rule the difference matches, or null when no rule explains it.</summary>
    public static async Task<string?> ClassifyAsync(byte[] modelBytes,byte[] skinBytes,IReadOnlyList<string> legacy,IReadOnlyList<string> current)
    {
        if(legacy.SequenceEqual(current,StringComparer.Ordinal))return Identical;
        // Both rules need the same readable chunked 272/274 model on both sides (model and profile lines), and layer 5 never
        // turns either case into a non-format exception.
        if(legacy.Count<3||current.Count<3||legacy[0]!=current[0]||legacy[1]!=current[1]||!legacy[0].StartsWith("model ok ",StringComparison.Ordinal))return null;
        if(current.Any(p=>p.Split(' ',3)[1].StartsWith("code=\"!",StringComparison.Ordinal)))return null;
        var model=await N.M2ModelReader.ReadAsync(new MemoryStream(modelBytes));
        if(!model.IsChunked||model.Version is not (272 or 274))return null;
        // 0xCDCDCDCD at SKIN offset 44: legacy stops at the header limit in both SKIN readers (or at the unchanged geometry
        // profile refusal). Layer 5 must then give exactly what legacy gives for the same SKIN with 0 there, apart from the
        // raw MaximumBones value; that zero-header comparison may itself only be identical or the empty-lookup rule.
        if(skinBytes.Length>=48&&BinaryPrimitives.ReadUInt32LittleEndian(skinBytes.AsSpan(44))==Fill)
        {
            if(legacy.Count!=4||legacy[2]!="skin "+FillLimit||legacy[3]!="geometry "+FillLimit&&!current.Contains(legacy[3])||current[2]==legacy[2])return null;
            var zeroed=(byte[])skinBytes.Clone();BinaryPrimitives.WriteUInt32LittleEndian(zeroed.AsSpan(44),0);
            var reference=await ModelPipeline.Legacy(modelBytes,zeroed);
            var raw=current.Select(p=>p.Replace(FillBones,"MaximumBones=0;",StringComparison.Ordinal)).ToList();
            // Wherever layer 5 read the SKIN, the raw fill must be what it kept: the substitution is never a no-op there.
            if(current.Any(p=>(p.StartsWith("skin ok ",StringComparison.Ordinal)||p.StartsWith("geometry ok ",StringComparison.Ordinal))&&!p.Contains(FillBones,StringComparison.Ordinal)))return null;
            return await ClassifyAsync(modelBytes,zeroed,reference,raw) is Identical or EmptyCoordinates?UnsetMaximumBones:null;
        }
        // An empty coordinate lookup: legacy's strict SKIN reader gives exactly the combo-range refusal, layer 5's gets past
        // it. Every non-skin step must be identical; the strict skin must agree with the geometry skin read from the same
        // bytes, its batches with the raw records, its static geometry with geometry.static, and opaque materials must
        // refuse rather than read the empty lookup as UV0.
        if(model.TextureCoordinates.Count!=0||legacy[2]!=ComboRange||current[2]==ComboRange)return null;
        static bool SkinStep(string p)=>p.StartsWith("skin ",StringComparison.Ordinal)||p.StartsWith("skin.",StringComparison.Ordinal);
        if(!legacy.Where(p=>!SkinStep(p)).SequenceEqual(current.Where(p=>!SkinStep(p)),StringComparer.Ordinal))return null;
        if(current[2].StartsWith("skin ok ",StringComparison.Ordinal))
        {
            var strict=await N.M2SkinReader.ReadAsync(new MemoryStream(skinBytes),model);
            if(current[2]!="skin ok "+ModelCanon.Value(strict))return null;
            N.M2GeometrySkin geometry;
            try{geometry=await N.M2GeometrySkinReader.ReadAsync(new MemoryStream(skinBytes),model);}
            catch(N.ModelFormatException){return null;}
            if(ModelCanon.Value((strict.VertexLookup,strict.TriangleLookup,strict.BoneProperties,strict.Sections,strict.MaximumBones,strict.SourceBytes))
               !=ModelCanon.Value((geometry.VertexLookup,geometry.TriangleLookup,geometry.BoneProperties,geometry.Sections,geometry.MaximumBones,geometry.SourceBytes)))return null;
            if(geometry.BatchDeclaration.Count!=strict.Batches.Count||geometry.ShadowBatchDeclaration.Count!=strict.ShadowBatches.Count)return null;
            if(ModelCanon.Value(strict.Batches)!=ModelCanon.Value(RawBatches(skinBytes,geometry.BatchDeclaration))
               ||ModelCanon.Value(strict.ShadowBatches)!=ModelCanon.Value(RawShadows(skinBytes,geometry.ShadowBatchDeclaration)))return null;
            var skinStatic=current.FirstOrDefault(p=>p.StartsWith("skin.static ",StringComparison.Ordinal));
            var geometryStatic=current.FirstOrDefault(p=>p.StartsWith("geometry.static ",StringComparison.Ordinal));
            if(skinStatic is null||geometryStatic is null||skinStatic["skin.static ".Length..]!=geometryStatic["geometry.static ".Length..])return null;
        }
        else if(current.Any(p=>p.StartsWith("skin.",StringComparison.Ordinal)))return null;
        var materials=current.FirstOrDefault(p=>p.StartsWith("skin.materials ",StringComparison.Ordinal));
        if(materials is not null&&!materials.StartsWith("skin.materials code="+JsonSerializer.Serialize("MODEL_MATERIAL_UNSUPPORTED")+" ",StringComparison.Ordinal))return null;
        return EmptyCoordinates;
    }

    private static readonly string FillBones="MaximumBones="+Fill.ToString(CultureInfo.InvariantCulture)+";";
    // Independent decodes of the 24-byte batch and 12-byte shadow records at the spans the geometry reader declared.
    private static N.M2Batch[] RawBatches(byte[] skin,N.M2ArrayDeclaration span)=>[..Enumerable.Range(0,(int)span.Count).Select(i=>
    {
        var r=skin.AsMemory((int)span.Offset+i*24,24);ushort U(int at)=>BinaryPrimitives.ReadUInt16LittleEndian(r.Span[at..]);
        return new N.M2Batch(r.Span[0],unchecked((sbyte)r.Span[1]),U(2),U(4),U(6),BinaryPrimitives.ReadInt16LittleEndian(r.Span[8..]),U(10),U(12),U(14),U(16),U(18),U(20),U(22));
    })];
    private static N.M2ShadowBatch[] RawShadows(byte[] skin,N.M2ArrayDeclaration span)=>[..Enumerable.Range(0,(int)span.Count).Select(i=>
    {
        var r=skin.AsMemory((int)span.Offset+i*12,12);ushort U(int at)=>BinaryPrimitives.ReadUInt16LittleEndian(r.Span[at..]);
        return new N.M2ShadowBatch(r.Span[0],r.Span[1],U(2),U(4),U(6),U(8),U(10));
    })];
}

/// <summary>Runs one side's reader paths over a model/SKIN pair and renders every step. Each step reads a fresh MemoryStream.
/// Order: model; SKIN reader, its static geometry and opaque materials; geometry SKIN reader, its static geometry, the
/// appearance model phase, SKIN phase and one-call read, and the VMG1 artifact (manifest and bytes).</summary>
internal static class ModelPipeline
{
    // The binding is derived the same way on both sides from that side's own model, so the artifact path is reachable.
    private static (string,string,string,long,long,string) Source(char a,char b,char c,long encoded,long decoded)=>(new string(a,32),new string(b,32),new string(c,64),encoded,decoded,"content-blte-v1");

    public static async Task<List<string>> Legacy(byte[] modelBytes,byte[] skinBytes)
    {
        var parts=new List<string>();
        var model=await Step(parts,"model",()=>L.M2ModelReader.ReadAsync(new MemoryStream(modelBytes)));
        if(model is null)return parts;
        parts.Add("profile "+ModelCanon.Value(L.M2GeometrySkinReader.ProfileRefusal(model)));
        var skin=await Step(parts,"skin",()=>L.M2SkinReader.ReadAsync(new MemoryStream(skinBytes),model));
        if(skin is not null)
        {
            await Step(parts,"skin.static",()=>Task.FromResult(L.M2StaticGeometry.Create(model,skin)));
            await Step(parts,"skin.materials",()=>Task.FromResult(L.M2StaticMaterials.AdmitOpaque(model,skin)));
        }
        var geometry=await Step(parts,"geometry",()=>L.M2GeometrySkinReader.ReadAsync(new MemoryStream(skinBytes),model));
        if(geometry is null)return parts;
        await Step(parts,"geometry.static",()=>Task.FromResult(L.M2StaticGeometry.Create(model,geometry)));
        var facts=await Step(parts,"appearance.model",()=>L.M2Appearance.ReadModelAsync(new MemoryStream(modelBytes),model));
        if(facts is not null)await Step(parts,"appearance.plan",()=>L.M2Appearance.AdmitAsync(new MemoryStream(skinBytes),geometry,facts));
        await Step(parts,"appearance.read",()=>L.M2Appearance.ReadAsync(new MemoryStream(modelBytes),model,new MemoryStream(skinBytes),geometry));
        var (mk,me,ms,mn,md,mv)=Source('1','2','3',100,modelBytes.Length);var (sk,se,ss,sn,sd,sv)=Source('4','5','6',80,skinBytes.Length);
        var binding=new L.GeometrySourceBinding(new(mk,me,ms,mn,md,mv),new(sk,se,ss,sn,sd,sv),800,model.SkinFileDataIds.Count>0?model.SkinFileDataIds[0]:900,0);
        var artifact=new MemoryStream();
        if(await Step(parts,"artifact",()=>L.ModelGeometryArtifacts.BuildAsync(geometry,binding,artifact)) is not null)parts.Add("artifact.bytes "+Convert.ToHexString(artifact.ToArray()));
        else parts.Add("artifact.written "+artifact.Length.ToString(CultureInfo.InvariantCulture));
        return parts;
    }

    public static async Task<List<string>> New(byte[] modelBytes,byte[] skinBytes)
    {
        var parts=new List<string>();
        var model=await Step(parts,"model",()=>N.M2ModelReader.ReadAsync(new MemoryStream(modelBytes)));
        if(model is null)return parts;
        parts.Add("profile "+ModelCanon.Value(N.M2GeometrySkinReader.ProfileRefusal(model)));
        var skin=await Step(parts,"skin",()=>N.M2SkinReader.ReadAsync(new MemoryStream(skinBytes),model));
        if(skin is not null)
        {
            await Step(parts,"skin.static",()=>Task.FromResult(N.M2StaticGeometry.Create(model,skin)));
            await Step(parts,"skin.materials",()=>Task.FromResult(N.M2StaticMaterials.AdmitOpaque(model,skin)));
        }
        var geometry=await Step(parts,"geometry",()=>N.M2GeometrySkinReader.ReadAsync(new MemoryStream(skinBytes),model));
        if(geometry is null)return parts;
        await Step(parts,"geometry.static",()=>Task.FromResult(N.M2StaticGeometry.Create(model,geometry)));
        var facts=await Step(parts,"appearance.model",()=>N.M2Appearance.ReadModelAsync(new MemoryStream(modelBytes),model));
        if(facts is not null)await Step(parts,"appearance.plan",()=>N.M2Appearance.AdmitAsync(new MemoryStream(skinBytes),geometry,facts));
        await Step(parts,"appearance.read",()=>N.M2Appearance.ReadAsync(new MemoryStream(modelBytes),model,new MemoryStream(skinBytes),geometry));
        var (mk,me,ms,mn,md,mv)=Source('1','2','3',100,modelBytes.Length);var (sk,se,ss,sn,sd,sv)=Source('4','5','6',80,skinBytes.Length);
        var binding=new N.GeometrySourceBinding(new(mk,me,ms,mn,md,mv),new(sk,se,ss,sn,sd,sv),800,model.SkinFileDataIds.Count>0?model.SkinFileDataIds[0]:900,0);
        var artifact=new MemoryStream();
        if(await Step(parts,"artifact",()=>N.ModelGeometryArtifacts.BuildAsync(geometry,binding,artifact)) is not null)parts.Add("artifact.bytes "+Convert.ToHexString(artifact.ToArray()));
        else parts.Add("artifact.written "+artifact.Length.ToString(CultureInfo.InvariantCulture));
        return parts;
    }

    // Only the reader call is inside the try: a rendering exception is a harness bug and fails the test, never a shared error.
    private static async Task<T?> Step<T>(List<string> parts,string stage,Func<Task<T>> run) where T:class
    {
        T result;
        try{result=await run();}
        catch(Exception e){parts.Add(stage+" "+ModelCanon.Error(e));return null;}
        parts.Add(stage+" "+Status(result)+" "+ModelCanon.Value(result));return result;
    }
    // A declined appearance plan is a result, not an error; its status names the refusal and the batch ordinal ("-" for the whole model).
    private static string Status(object result)=>result switch
    {
        L.M2AppearancePlan{Decline:{} d}=>"declined="+d.Reason+"@"+(d.BatchOrdinal?.ToString(CultureInfo.InvariantCulture)??"-"),
        N.M2AppearancePlan{Decline:{} d}=>"declined="+d.Reason+"@"+(d.BatchOrdinal?.ToString(CultureInfo.InvariantCulture)??"-"),
        _=>"ok",
    };

    /// <summary>"stage:ok", "stage:CODE" or, for a declined plan, "stage:Reason@batch" per step, space separated.</summary>
    public static string Summary(IEnumerable<string> parts)=>string.Join(" ",parts.Select(p=>p.Split(' ',3)).Where(p=>p[0] is not ("profile" or "artifact.bytes" or "artifact.written"))
        .Select(p=>p[0]+":"+(p[1]=="ok"?"ok":p[1].StartsWith("declined=",StringComparison.Ordinal)?p[1]["declined=".Length..]:JsonSerializer.Deserialize<string>(p[1]["code=".Length..]))));
}

/// <summary>Canonical rendering by reflection over every instance property (public and internal), so both namespaces'
/// types render through the same code. Floats render as their bit patterns; strings as JSON literals.</summary>
internal static class ModelCanon
{
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

    /// <summary>A format error is its code and message; the legacy DiscoveryException and the new ModelFormatException
    /// compare equal. Anything else keeps its exception type, so it never matches a format error.</summary>
    public static string Error(Exception e)
    {
        var code=e switch{DiscoveryException d=>d.Code,N.ModelFormatException f=>f.Code,_=>"!"+e.GetType().FullName};
        return "code="+JsonSerializer.Serialize(code)+" message="+JsonSerializer.Serialize(e.Message);
    }

    public static string Value(object? value)=>value switch
    {
        null=>"null",
        string s=>JsonSerializer.Serialize(s),
        bool b=>b?"true":"false",
        float f=>"f"+BitConverter.SingleToInt32Bits(f).ToString("X8",Inv),
        double d=>"d"+BitConverter.DoubleToInt64Bits(d).ToString("X16",Inv),
        Enum e=>e.GetType().Name+"."+e.ToString()+"("+Convert.ToInt64(e,Inv).ToString(Inv)+")",
        Vector2 v=>"("+Value(v.X)+","+Value(v.Y)+")",
        Vector3 v=>"("+Value(v.X)+","+Value(v.Y)+","+Value(v.Z)+")",
        byte[] bytes=>"0x"+Convert.ToHexString(bytes),
        ITuple tuple=>"("+string.Join(",",Enumerable.Range(0,tuple.Length).Select(i=>Value(tuple[i])))+")",
        IFormattable f when value.GetType().IsPrimitive=>f.ToString(null,Inv),
        IEnumerable items=>"["+string.Join(",",items.Cast<object?>().Select(Value))+"]",
        _=>Object(value),
    };

    private static string Object(object value)
    {
        var type=value.GetType();
        var properties=type.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
            .Where(p=>p.GetIndexParameters().Length==0&&p.Name!="EqualityContract").OrderBy(p=>p.Name,StringComparer.Ordinal);
        return type.Name+"{"+string.Join(";",properties.Select(p=>p.Name+"="+Value(p.GetValue(value))))+"}";
    }
}
