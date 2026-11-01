using System.Globalization;
using System.Text;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using L = VibeMiner.Formats.Wow;
using N = VibeMiner.Formats;

namespace Db2Comparison;

/// <summary>One verified raw integer rule, convertible to either reader's rule type.</summary>
internal sealed record RuleSpec(int FieldOrdinal,string FieldName,uint StorageKind,int PhysicalBits,int CandidateBits,bool CandidateUnsigned,bool NarrowsWhenConstant=false);

/// <summary>The limits a fixture reads with, convertible to both readers' limit records.</summary>
internal sealed record Lim(bool Partial=false,bool RawText=false,long MaxRawText=16L<<20,int MaxCopyDepth=64,bool Sha=false,RuleSpec[]? Rules=null)
{
    public L.Wdc5Limits Legacy(string sha)=>new(AllowPartialEncryptedSections:Partial,AllowRawTextEvidence:RawText,MaxRawTextEvidenceBytes:MaxRawText,
        MaxCopyDepth:MaxCopyDepth,DefinitionSha256:Sha?sha:null,
        VerifiedRawIntegerRules:Rules?.Select(r=>new L.Wdc5RawIntegerRule(sha,"11223344","01020304",r.FieldOrdinal,r.FieldName,r.StorageKind,r.PhysicalBits,r.CandidateBits,r.CandidateUnsigned,r.NarrowsWhenConstant)).ToArray());
    public N.Db2Limits New(string sha)=>new(AllowPartialEncryptedSections:Partial,AllowRawTextEvidence:RawText,MaxRawTextEvidenceBytes:MaxRawText,
        MaxCopyDepth:MaxCopyDepth,DefinitionSha256:Sha?sha:null,
        VerifiedRawIntegerRules:Rules?.Select(r=>new N.Db2RawIntegerRule(sha,"11223344","01020304",r.FieldOrdinal,r.FieldName,r.StorageKind,r.PhysicalBits,r.CandidateBits,r.CandidateUnsigned,r.NarrowsWhenConstant)).ToArray());
}

/// <summary>A synthetic base fixture. <see cref="Expect"/> is the base outcome both readers must give: a completion scope or an error code.</summary>
internal sealed record Fixture(string Name,byte[] Bytes,string Dbd,string Expect,Lim? Limits=null,(long Offset,long Length)[]? Holes=null,string Build="1.2.3.4")
{
    public override string ToString()=>Name;
}

/// <summary>Runs both readers over the same bytes and renders each read as a list of canonical parts.</summary>
internal sealed class Comparer
{
    private readonly Fixture fixture;private readonly L.DbdDocument legacyDefinition;private readonly N.DbdDocument newDefinition;
    private readonly L.Wdc5Limits legacyLimits;private readonly N.Db2Limits newLimits;
    private readonly BlteUnavailableRange[] legacyHoles;private readonly N.Db2Hole[] newHoles;
    public Comparer(Fixture fixture)
    {
        this.fixture=fixture;var dbd=Encoding.UTF8.GetBytes(fixture.Dbd);
        legacyDefinition=L.DbdDefinitionReader.Parse(dbd);newDefinition=N.DbdDefinitionReader.Parse(dbd);
        var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(dbd));var limits=fixture.Limits??new Lim();
        legacyLimits=limits.Legacy(sha);newLimits=limits.New(sha);
        // Unique chunk ordinals and non-empty names, so the ordinal-vs-name difference in hole validation never applies.
        var holes=fixture.Holes??[];
        legacyHoles=holes.Select((h,i)=>new BlteUnavailableRange(h.Offset,h.Length,i,"000000000000007B")).ToArray();
        newHoles=holes.Select((h,i)=>new N.Db2Hole(h.Offset,h.Length,"chunk"+i.ToString(CultureInfo.InvariantCulture))).ToArray();
    }

    public async Task<List<string>> Legacy(byte[] bytes)
    {
        var sink=new LegacySink();
        try{sink.Parts.Add(Canon.Completion(await L.Wdc5TableReader.ReadAsync(new LegacyStream(bytes,legacyHoles),legacyDefinition,fixture.Build,legacyLimits,sink)));}
        catch(Exception e){sink.Parts.Add(Canon.Error(e,sink.Rows));}
        return sink.Parts;
    }
    public async Task<List<string>> New(byte[] bytes)
    {
        var sink=new NewSink();
        try{sink.Parts.Add(Canon.Completion(await N.Db2Reader.ReadAsync(new NewSource(bytes,newHoles),newDefinition,fixture.Build,newLimits,sink)));}
        catch(Exception e){sink.Parts.Add(Canon.Error(e,sink.Rows));}
        return sink.Parts;
    }

    private sealed class LegacySink:L.IWdc5Consumer
    {
        public List<string> Parts=[];public int Rows;
        public ValueTask HeaderAsync(L.Wdc5TableHeader header,CancellationToken token){Parts.Add(Canon.Header(header));return ValueTask.CompletedTask;}
        public ValueTask RowAsync(L.Db2Row row,CancellationToken token){Parts.Add(Canon.Row(row));Rows++;return ValueTask.CompletedTask;}
    }
    private sealed class NewSink:N.IDb2Consumer
    {
        public List<string> Parts=[];public int Rows;
        public ValueTask HeaderAsync(N.Db2TableHeader header,CancellationToken token){Parts.Add(Canon.Header(header));return ValueTask.CompletedTask;}
        public ValueTask RowAsync(N.Db2Row row,CancellationToken token){Parts.Add(Canon.Row(row));Rows++;return ValueTask.CompletedTask;}
    }
}

/// <summary>Canonical rendering. Every string goes through a JSON string literal, so parts are unambiguous; the canonical
/// bytes are the UTF-8 parts, each prefixed with its byte length.</summary>
internal static class Canon
{
    private static string S(string? value)=>value is null?"null":JsonSerializer.Serialize(value);
    private static string I(long value)=>value.ToString(CultureInfo.InvariantCulture);
    private static string B(bool value)=>value?"1":"0";

    public static string Header(L.Wdc5TableHeader h)=>Header(h.SchemaVersion,h.TableHash,h.LayoutHash,h.DeclaredRecords,h.DeclaredCopyRecords,h.RecordSize,
        h.Columns.Select(c=>Column(c.Name,c.Kind,c.IsId,c.Interpretation is null?null:Interp(c.Interpretation.Policy,c.Interpretation.PhysicalBits,c.Interpretation.PhysicalSigned,c.Interpretation.CandidateBits,c.Interpretation.CandidateUnsigned,c.Interpretation.Warning),
            c.Narrowing is null?null:I(c.Narrowing.StoredBits)+","+I(c.Narrowing.UsedBits))));
    public static string Header(N.Db2TableHeader h)=>Header(h.SchemaVersion,h.TableHash,h.LayoutHash,h.DeclaredRecords,h.DeclaredCopyRecords,h.RecordSize,
        h.Columns.Select(c=>Column(c.Name,c.Kind,c.IsId,c.Interpretation is null?null:Interp(c.Interpretation.Policy,c.Interpretation.PhysicalBits,c.Interpretation.PhysicalSigned,c.Interpretation.CandidateBits,c.Interpretation.CandidateUnsigned,c.Interpretation.Warning),
            c.Narrowing is null?null:I(c.Narrowing.StoredBits)+","+I(c.Narrowing.UsedBits))));
    private static string Interp(string policy,int bits,bool signed,int candidate,bool unsigned,string warning)=>
        S(policy)+","+I(bits)+","+B(signed)+","+I(candidate)+","+B(unsigned)+","+S(warning);
    private static string Column(string name,string kind,bool id,string? interp,string? narrowing)=>
        "{"+S(name)+","+S(kind)+",id="+B(id)+",interp="+(interp??"-")+",narrow="+(narrowing??"-")+"}";
    private static string Header(uint schema,string table,string layout,long records,long copies,int size,IEnumerable<string> columns)=>
        "H schema="+I(schema)+" table="+S(table)+" layout="+S(layout)+" records="+I(records)+" copies="+I(copies)+" size="+I(size)+" columns=["+string.Join(";",columns)+"]";

    public static string Row(L.Db2Row r)=>Row(r.Id,r.IsCopy,r.CopySourceId,r.Values,r.TextIssues?.Select(t=>Issue(t.ColumnIndex,t.Code,t.RawBytesHex)));
    public static string Row(N.Db2Row r)=>Row(r.Id,r.IsCopy,r.CopySourceId,r.Values,r.TextIssues?.Select(t=>Issue(t.ColumnIndex,t.Code,t.RawBytesHex)));
    private static string Issue(int column,string code,string hex)=>I(column)+","+S(code)+","+S(hex);
    private static string Row(uint id,bool copy,uint? source,IReadOnlyList<string> values,IEnumerable<string>? issues)=>
        "R id="+I(id)+" copy="+B(copy)+" source="+(source is null?"-":I(source.Value))+" values=["+string.Join(",",values.Select(S))+"] issues="+(issues is null?"-":"["+string.Join(";",issues)+"]");

    public static string Completion(L.Wdc5Completion c)=>Completion(c.DeclaredRecords,c.PhysicalRows,c.CopyRows,c.ExcludedEncryptedSections,c.ExcludedPhysicalRecords,c.ExcludedCopyRecords,c.Scope,c.ConsumedBytes,c.TextIssueCount);
    public static string Completion(N.Db2Completion c)=>Completion(c.DeclaredRecords,c.PhysicalRows,c.CopyRows,c.ExcludedEncryptedSections,c.ExcludedPhysicalRecords,c.ExcludedCopyRecords,c.Scope,c.ConsumedBytes,c.TextIssueCount);
    private static string Completion(long declared,long physical,long copies,int excludedSections,long excludedRecords,long excludedCopies,string scope,long consumed,long issues)=>
        "C declared="+I(declared)+" physical="+I(physical)+" copies="+I(copies)+" excludedSections="+I(excludedSections)+" excludedRecords="+I(excludedRecords)
        +" excludedCopies="+I(excludedCopies)+" scope="+S(scope)+" consumed="+I(consumed)+" textIssues="+I(issues);

    /// <summary>A format error is its code and message; the legacy DiscoveryException and the new Db2FormatException compare equal.
    /// Anything else keeps its exception type, so an unexpected exception never matches a format error.</summary>
    public static string Error(Exception e,int rows)
    {
        var code=e switch{DiscoveryException d=>d.Code,N.Db2FormatException f=>f.Code,_=>"!"+e.GetType().FullName};
        return "E rows="+I(rows)+" code="+S(code)+" message="+S(e.Message);
    }

    public static byte[] Bytes(IReadOnlyList<string> parts)
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
        foreach(var part in parts){var bytes=Encoding.UTF8.GetBytes(part);writer.Write(bytes.Length);writer.Write(bytes);}
        writer.Flush();return output.ToArray();
    }
    /// <summary>Short outcome: the completion scope or the error code.</summary>
    public static string Outcome(IReadOnlyList<string> parts)
    {
        var last=parts[^1];
        if(last.StartsWith("C ",StringComparison.Ordinal))return JsonSerializer.Deserialize<string>(last[(last.IndexOf("scope=",StringComparison.Ordinal)+6)..last.IndexOf(" consumed=",StringComparison.Ordinal)])!;
        var at=last.IndexOf("code=",StringComparison.Ordinal)+5;return JsonSerializer.Deserialize<string>(last[at..last.IndexOf(" message=",StringComparison.Ordinal)])!;
    }
}

/// <summary>Seekable legacy input over fixed bytes with BLTE-style holes. A read that intersects a hole fails before copying,
/// with the code and message the real BlteProjectionStream uses. No constructor validation: the reader validates the holes.</summary>
internal sealed class LegacyStream(byte[] bytes,IReadOnlyList<BlteUnavailableRange> holes):Stream,IBlteProjectionSource
{
    private long position;
    public IReadOnlyList<BlteUnavailableRange> UnavailableRanges=>holes;
    public IReadOnlyList<BlteReadableRange>? ReadableRanges=>null;
    public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
    public override long Length=>bytes.Length;
    public override long Position{get=>position;set=>position=value;}
    public override int Read(Span<byte> buffer)
    {
        if(buffer.Length==0||position>=bytes.Length)return 0;
        var count=(int)Math.Min(buffer.Length,bytes.Length-position);var last=position+count;
        foreach(var hole in holes)
        {
            if(hole.Offset>=last)break;
            if(hole.Offset+hole.ByteLength>position)throw new DiscoveryException("BLTE_RANGE_UNAVAILABLE","The requested plaintext range intersects an unavailable encrypted chunk.");
        }
        bytes.AsSpan((int)position,count).CopyTo(buffer);position+=count;return count;
    }
    public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
    {cancellationToken.ThrowIfCancellationRequested();return ValueTask.FromResult(Read(buffer.Span));}
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken)=>ReadAsync(buffer.AsMemory(offset,count),cancellationToken).AsTask();
    public override long Seek(long offset,SeekOrigin origin)=>position=origin switch{SeekOrigin.Begin=>offset,SeekOrigin.Current=>position+offset,_=>bytes.Length+offset};
    public override void Flush(){}
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}

/// <summary>The new reader's input over the same bytes. Any read outside the source or touching a hole is a reader bug
/// here, so it throws a non-format exception that can never match the legacy output.</summary>
internal sealed class NewSource(byte[] bytes,IReadOnlyList<N.Db2Hole> holes):N.IDb2ByteSource
{
    public long Length=>bytes.Length;
    public IReadOnlyList<N.Db2Hole> Holes=>holes;
    public ValueTask ReadRangeAsync(long offset,Memory<byte> destination,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(offset<0||offset>bytes.Length||destination.Length>bytes.Length-offset)throw new InvalidOperationException("Read leaves the source.");
        foreach(var hole in holes)if(offset<hole.Offset+hole.Length&&hole.Offset<offset+destination.Length)throw new InvalidOperationException("Read touches a hole.");
        bytes.AsMemory((int)offset,destination.Length).CopyTo(destination);return ValueTask.CompletedTask;
    }
}
