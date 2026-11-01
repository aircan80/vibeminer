using System.Globalization;
using System.Runtime.CompilerServices;

namespace VibeMiner.Formats;

/// <summary>An opened DB2 table: structure checked and ID indexes built, rows decoded on demand. Immutable, and safe for
/// concurrent reads when its byte source is. Row decoding errors surface when that row is read.</summary>
public sealed class Db2Table
{
    private readonly Db2Reader.TableState state;
    internal Db2Table(Db2Reader.TableState state,Db2TableHeader header,IReadOnlyList<Db2Section> sections,Db2Completion completion,int unresolvedCopies)
    {this.state=state;Header=header;Sections=sections;Completion=completion;UnresolvedCopies=unresolvedCopies;}

    public Db2TableHeader Header{get;}
    public IReadOnlyList<Db2Section> Sections{get;}
    /// <summary>The completion facts known at open, in the legacy shape. TextIssueCount is 0 here: text issues are counted
    /// by each read. Its row counts are final only after a full read succeeds: while <see cref="UnresolvedCopies"/> is
    /// nonzero, Scope can say "complete" although a read will fail at the first broken copy.</summary>
    public Db2Completion Completion{get;}
    /// <summary>Readable copies whose chain is broken (a cycle, a source that is neither readable nor protected, or
    /// deeper than MaxCopyDepth). Found at open; each throws its legacy error only when that copy is read, so any full
    /// read fails when this is nonzero. Per section in <see cref="Db2Section.UnresolvedCopyCount"/>.</summary>
    public int UnresolvedCopies{get;}

    /// <summary>One section's physical rows in file order, then the copies it declares (copies whose chain ends at an
    /// encrypted ID are skipped). A bad argument or an excluded keyed section (DB2_ENCRYPTED) throws at call time; row and copy
    /// errors throw during enumeration, after the rows before them.</summary>
    public IAsyncEnumerable<Db2Row> ReadSectionAsync(int section,CancellationToken token=default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(section);ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(section,Sections.Count);
        var info=Sections[section];
        if(info.IsEncrypted&&!info.IsDecrypted)throw new Db2FormatException("DB2_ENCRYPTED",string.Create(CultureInfo.InvariantCulture,
            $"Section {section} is keyed with {info.Key}; its records are not readable."));
        return ReadRange(state.RowStart[section],state.RowStart[section+1],state.CopyStart[section],state.CopyStart[section+1],new(),token);
    }

    /// <summary>Every readable row in the legacy order: all physical rows, then all resolved copies.</summary>
    public IAsyncEnumerable<Db2Row> ReadAllAsync(CancellationToken token=default)=>ReadAllCore(new(),token);

    internal IAsyncEnumerable<Db2Row> ReadAllCore(Db2Reader.TextBudget budget,CancellationToken token)=>
        ReadRange(0,state.Rows.Length,0,state.Copies.Length,budget,token);

    /// <summary>The row or copy with this ID, or why there is none. Reads only that record and its strings.</summary>
    public async Task<Db2Lookup> FindAsync(uint id,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(state.RowIndex.TryGetValue(id,out var row))return new Db2Lookup.Found(await Db2Reader.DecodeRow(state,row,id,null,new(),token));
        if(state.CopyIndex.TryGetValue(id,out var copy))
        {
            var resolution=state.Resolutions[copy];
            if(resolution.IsProtected)return Protected(resolution.ProtectedId);
            return new Db2Lookup.Found(await Db2Reader.DecodeCopy(state,copy,new(),token));
        }
        if(state.EncryptedIds.ContainsKey(id))return Protected(id);
        return state.Partial?new Db2Lookup.NotInReadableSections():new Db2Lookup.Absent();
    }

    /// <summary>Every ID <see cref="FindAsync"/> answers Protected for, with its keyed section's key: the encrypted-ID lists and
    /// the readable copies whose chain ends at one. Null when there are more than <paramref name="max"/>.</summary>
    internal IReadOnlyDictionary<uint,string>? ProtectedIds(int max)
    {
        if(state.EncryptedIds.Count>max)return null;
        var ids=new Dictionary<uint,string>(state.EncryptedIds.Count);
        foreach(var (id,section) in state.EncryptedIds)ids[id]=Sections[section].Key;
        foreach(var (target,copy) in state.CopyIndex)
        {
            var resolution=state.Resolutions[copy];
            if(!resolution.IsProtected)continue;
            if(ids.Count>=max)return null;
            ids[target]=Sections[state.EncryptedIds[resolution.ProtectedId]].Key;
        }
        return ids;
    }

    private Db2Lookup.Protected Protected(uint protectedId)
    {var section=state.EncryptedIds[protectedId];return new(section,Sections[section].Key,protectedId);}

    private async IAsyncEnumerable<Db2Row> ReadRange(int firstRow,int endRow,int firstCopy,int endCopy,Db2Reader.TextBudget budget,
        [EnumeratorCancellation]CancellationToken token)
    {
        for(var row=firstRow;row<endRow;row++)
            yield return await Db2Reader.DecodeRow(state,row,state.Rows[row].PhysicalId,null,budget,token);
        for(var copy=firstCopy;copy<endCopy;copy++)
        {
            if(state.Resolutions[copy].IsProtected)continue;
            yield return await Db2Reader.DecodeCopy(state,copy,budget,token);
        }
    }
}

public static partial class Db2Reader
{
    // copySource is null for a physical row; a copy is decoded from its terminal row with its own ID.
    internal static async Task<Db2Row> DecodeRow(TableState state,int row,uint id,uint? copySource,TextBudget budget,CancellationToken token)
    {
        var location=state.Rows[row];var isCopy=copySource is not null;
        if(state.Sparse)
        {
            var (values,issues)=await DecodeSparseValues(state.Bytes,location,id,state.Fields,state.Storage,state.Limits,token,state.Relationships,!isCopy,state.Alignment);
            ChargeText(issues,budget,state.Limits,token);
            return new(id,isCopy,copySource,values,issues);
        }
        var (fixedValues,fixedIssues)=await DecodeValues(state.Bytes,location,id,state.Fields,state.Storage,state.Sections,state.RecordSize,state.DeclaredRecords,state.Limits,token,state.Relationships,!isCopy);
        ChargeText(fixedIssues,budget,state.Limits,token);
        return new(id,isCopy,copySource,fixedValues,fixedIssues);
    }
    internal static Task<Db2Row> DecodeCopy(TableState state,int copy,TextBudget budget,CancellationToken token)
    {
        var resolution=state.Resolutions[copy];
        if(resolution.Failure!=CopyFailure.None)throw CopyError(resolution.Failure,state.Sparse);
        var entry=state.Copies[copy];
        return DecodeRow(state,resolution.Row,entry.Target,entry.Source,budget,token);
    }
}
