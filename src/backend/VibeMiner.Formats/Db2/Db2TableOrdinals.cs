using System.Runtime.CompilerServices;

namespace VibeMiner.Formats;

// Row ordinals: the 0-based position of a row in ReadAllAsync's order (physical rows, then the copies that are not
// protected). Unresolved copies have an ordinal too; reading one throws its copy error, as ReadAllAsync does.
public sealed partial class Db2Table
{
    // readableCopiesBefore[c] is the number of non-protected copies in [0,c); built once from Resolutions, no decoding.
    private int[]? readableCopiesBefore;

    private int[] ReadableCopiesBefore()=>LazyInitializer.EnsureInitialized(ref readableCopiesBefore,()=>
    {
        var before=new int[state.Copies.Length+1];
        for(var copy=0;copy<state.Copies.Length;copy++)before[copy+1]=before[copy]+(state.Resolutions[copy].IsProtected?0:1);
        return before;
    });

    /// <summary>The number of rows <see cref="ReadAllAsync"/> yields, including unresolved copies that throw when read.</summary>
    internal long ReadableRowCount=>state.Rows.Length+(long)ReadableCopiesBefore()[^1];

    /// <summary>Rows <paramref name="first"/> to first+count-1 in <see cref="ReadAllAsync"/> order, decoded the same way; fewer
    /// only at the end. Rows before <paramref name="first"/> are not decoded. Bad arguments throw at call time.</summary>
    internal IAsyncEnumerable<Db2Row> ReadOrdinalsAsync(long first,int count,CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(first,ReadableRowCount);
        return ReadOrdinals(first,Math.Min(first+count,ReadableRowCount),new(),token);
    }

    /// <summary>The same answer as <see cref="FindAsync"/>, with the row's ordinal when Found and -1 otherwise.</summary>
    internal async Task<(long Ordinal,Db2Lookup Lookup)> FindWithOrdinalAsync(uint id,CancellationToken token)
    {
        var lookup=await FindAsync(id,token);
        if(lookup is not Db2Lookup.Found)return(-1,lookup);
        if(state.RowIndex.TryGetValue(id,out var row))return(row,lookup);
        return(state.Rows.Length+(long)ReadableCopiesBefore()[state.CopyIndex[id]],lookup);
    }

    private async IAsyncEnumerable<Db2Row> ReadOrdinals(long first,long end,Db2Reader.TextBudget budget,[EnumeratorCancellation]CancellationToken token)
    {
        var rows=state.Rows.Length;var ordinal=first;
        for(;ordinal<end&&ordinal<rows;ordinal++)
            yield return await Db2Reader.DecodeRow(state,(int)ordinal,state.Rows[(int)ordinal].PhysicalId,null,budget,token);
        if(ordinal>=end)yield break;
        // The first copy at this ordinal: the lowest c whose count of readable copies up to and including it is k+1.
        var before=ReadableCopiesBefore();var k=(int)(ordinal-rows);
        int low=1,high=before.Length-1;
        while(low<high){var mid=low+(high-low)/2;if(before[mid]>k)high=mid;else low=mid+1;}
        var copy=low-1;
        for(;ordinal<end&&copy<state.Copies.Length;copy++)
        {
            if(state.Resolutions[copy].IsProtected)continue;
            yield return await Db2Reader.DecodeCopy(state,copy,budget,token);ordinal++;
        }
    }
}
