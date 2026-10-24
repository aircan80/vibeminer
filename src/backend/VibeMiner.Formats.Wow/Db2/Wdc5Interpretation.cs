namespace VibeMiner.Formats.Wow;

public static partial class Wdc5TableReader
{
    private static int PhysicalValueBits(Storage storage)=>storage.Kind switch
    {0=>NoneWidth(storage),1 or 5=>storage.Width,_=>32};

    // Unverified names retain their existing raw policy. Verified non-ID/non-relation
    // columns require explicit metadata-selected opt-in for wider physical values.
    // This is metadata-selected before Header, never a fallback triggered by a row.
    private static bool UsesRawPhysical(Storage storage,DbdField field)=>field.Column.Family=="int"
        &&(!field.Column.VerifiedName||storage.VerifiedRawEvidence)&&!field.IsId&&PhysicalValueBits(storage)>(field.IntegerBits??32);

    private static Db2ValueInterpretation? RawInterpretation(Storage storage,DbdField field)=>!UsesRawPhysical(storage,field)?null:
        new("raw-physical-integer-v1",PhysicalValueBits(storage),storage.Kind==5,field.IntegerBits??32,field.Unsigned==true,
            storage.VerifiedRawEvidence
                ?"Candidate integer width is narrower than the physical value width. Explicit raw evidence preserves this entire column without narrowing; semantic references and calculations are not admitted."
                :"Unverified provider field: values preserve the physical integer without narrowing to the candidate schema. Names, relationships and semantic meaning are unverified.");

    // D079: pallet and common values are always stored as 32 bits. When a verified integer field is narrower and some stored
    // value doesn't fit it (not even as a sign extension), the whole column keeps the low bits at the definition's width.
    // Inline None/bitpacked storage stays strict: its width is the record's own, so a value that doesn't fit is corruption.
    // IDs and relationship keys stay strict too, and raw evidence (unverified names, exact rules) wins over narrowing.
    private static bool NarrowsToLowBits(Storage storage,DbdField field)=>storage.Kind is 2 or 3 or 4&&field.Column.Family=="int"
        &&!field.IsId&&!field.RelationStorage&&(field.IntegerBits??32)<32&&!UsesRawPhysical(storage,field);
    private static bool FitsDefinition(uint value,int width,bool unsigned)
    {
        var mask=(1u<<width)-1;if(value<=mask)return true;
        return !unsigned&&value==unchecked((uint)Signed(value&mask,width));
    }
    // Selected once the pallet and common values are loaded and before Header, never from the rows that happen to be read.
    private static bool SelectLowBitNarrowing(IReadOnlyList<BoundField> fields,Storage[] storage,CancellationToken token)
    {
        var any=false;
        foreach(var bound in fields)
        {
            if(bound.Source!=FieldSource.Physical||bound.StorageIndex<0)continue;
            var column=storage[bound.StorageIndex];var field=bound.Field;
            if(!NarrowsToLowBits(column,field))continue;
            var width=field.IntegerBits??32;var unsigned=field.Unsigned==true;var seen=0;
            bool Misfit(uint value){if((++seen&4095)==0)token.ThrowIfCancellationRequested();return !FitsDefinition(value,width,unsigned);}
            column.KeepsLowBits=column.Kind==2?Misfit(column.P1)||column.Common!.Values.Any(Misfit):column.Palette.Any(Misfit);
            any|=column.KeepsLowBits;
        }
        return any;
    }
    private static Db2ValueNarrowing? LowBitNarrowing(Storage storage,DbdField field)=>
        storage.KeepsLowBits?new(32,field.IntegerBits??32):null;
    private static Wdc5RawIntegerRule[] SnapshotRawIntegerRules(Wdc5Limits limits,CancellationToken token)
    {
        static bool Hex(string? value,int length,bool upper)=>value is not null&&value.Length==length
            &&value.All(c=>c is >= '0' and <= '9'||(upper?c is >= 'A' and <= 'F':c is >= 'a' and <= 'f'));
        if(limits.DefinitionSha256 is not null&&!Hex(limits.DefinitionSha256,64,false))
            throw new ArgumentException("The definition SHA256 context must be canonical lowercase hexadecimal.",nameof(limits));
        var source=limits.VerifiedRawIntegerRules;if(source is null)return [];
        var count=source.Count;
        if(count is <0 or >8)throw new ArgumentException("At most eight exact integer evidence rules are permitted.",nameof(limits));
        var result=new Wdc5RawIntegerRule[count];
        for(var i=0;i<result.Length;i++)
        {
            token.ThrowIfCancellationRequested();Wdc5RawIntegerRule rule;
            try{rule=source[i];}catch(Exception e)when(e is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {throw new ArgumentException("The integer evidence rule list changed during its snapshot.",nameof(limits),e);}
            if(rule is null||!Hex(rule.ExpectedDefinitionSha256,64,false)||!Hex(rule.TableHash,8,true)||!Hex(rule.LayoutHash,8,true)
                ||rule.FieldOrdinal<0||rule.FieldOrdinal>=limits.MaxFields||rule.FieldName is null||rule.FieldName.Length is <1 or >128
                ||!rule.FieldName.All(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                ||rule.FieldName[0] is >= '0' and <= '9'||rule.StorageKind>5||rule.PhysicalBits is <1 or >64
                ||rule.StorageKind is 2 or 3 or 4&&rule.PhysicalBits!=32
                ||rule.CandidateBits is not (8 or 16 or 32 or 64)||rule.PhysicalBits<=rule.CandidateBits)
                throw new ArgumentException("An integer evidence rule is outside the finite exact metadata profile.",nameof(limits));
            for(var j=0;j<i;j++)
                if(result[j].ExpectedDefinitionSha256==rule.ExpectedDefinitionSha256&&result[j].TableHash==rule.TableHash
                    &&result[j].LayoutHash==rule.LayoutHash&&result[j].FieldOrdinal==rule.FieldOrdinal)
                    throw new ArgumentException("Duplicate or conflicting integer evidence targets are forbidden.",nameof(limits));
            result[i]=rule;
        }
        return result;
    }
    private static void ApplyRawIntegerRules(IReadOnlyList<BoundField> fields,Storage[] storage,uint table,uint layout,
        string? definitionSha256,IReadOnlyList<Wdc5RawIntegerRule> rules,CancellationToken token)
    {
        if(definitionSha256 is null||rules.Count==0)return;
        var tableHash=table.ToString("X8",System.Globalization.CultureInfo.InvariantCulture);
        var layoutHash=layout.ToString("X8",System.Globalization.CultureInfo.InvariantCulture);
        for(var ordinal=0;ordinal<fields.Count;ordinal++)
        {
            token.ThrowIfCancellationRequested();var bound=fields[ordinal];var field=bound.Field;
            if(bound.Source!=FieldSource.Physical||bound.StorageIndex<0||field.IsId||field.RelationStorage
                ||!field.Column.VerifiedName||field.Column.Family!="int")continue;
            var column=storage[bound.StorageIndex];var bits=PhysicalValueBits(column);
            column.VerifiedRawEvidence=rules.Any(rule=>rule.ExpectedDefinitionSha256==definitionSha256&&rule.TableHash==tableHash
                &&rule.LayoutHash==layoutHash&&rule.FieldOrdinal==ordinal&&rule.FieldName==field.Column.Name
                &&rule.StorageKind==column.Kind&&rule.PhysicalBits==bits&&rule.CandidateBits==(field.IntegerBits??32)
                &&rule.CandidateUnsigned==(field.Unsigned==true));
        }
    }
}
