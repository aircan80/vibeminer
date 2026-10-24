using System.Globalization;
using System.Text;

namespace VibeMiner.Formats.Wow;

public static partial class Wdc5TableReader
{
    private static int NoneWidth(Storage storage)
    {var width=32-(int)storage.FieldBits;return width<=0?checked((int)storage.P2):width;}
    private static ulong Bits(ReadOnlySpan<byte> record,long offset,int width)
    {
        if(width is <1 or >64||offset<0||offset>record.Length*8L||width>record.Length*8L-offset)throw Fail("INVALID","A field extends outside its exact physical record.");
        ulong value=0;var written=0;
        while(written<width)
        {
            var position=offset+written;var within=(int)(position&7);var take=Math.Min(8-within,width-written);
            var part=(record[(int)(position/8)]>>within)&((1<<take)-1);value|=(ulong)part<<written;written+=take;
        }
        return value;
    }
    private static ulong Raw(byte[] record,Storage storage,DbdField field,int element,uint id)
    {
        switch(storage.Kind)
        {
            case 0:return Bits(record,storage.FieldOffset*8L+element*(long)NoneWidth(storage),NoneWidth(storage));
            case 1:case 5:return Bits(record,storage.Offset,storage.Width);
            case 2:return storage.Common!.TryGetValue(id,out var value)?value:storage.P1;
            case 3:case 4:
                var index=Bits(record,storage.Offset,storage.Width);var cardinality=storage.Kind==4?field.ArrayLength:1;
                var slot=index*(ulong)cardinality+(ulong)element;
                if(slot>=(ulong)storage.Palette.Length)throw Fail("INVALID","A pallet index is outside this field's exact auxiliary slice.");
                return storage.Palette[(int)slot];
            default:throw Fail("UNSUPPORTED","A field uses unsupported physical storage.");
        }
    }
    private static long Signed(ulong raw,int width)=>width==64?unchecked((long)raw):unchecked((long)(raw<<(64-width)))>>(64-width);
    private static string Number(ulong raw,Storage storage,DbdField field)
    {
        if(UsesRawPhysical(storage,field))
            return storage.Kind==5?Signed(raw,storage.Width).ToString(CultureInfo.InvariantCulture):raw.ToString(CultureInfo.InvariantCulture);
        if(field.Column.Family=="float")
        {
            if(storage.Kind==0&&NoneWidth(storage)!=32||storage.Kind is 1 or 5&&storage.Width!=32)throw Fail("UNSUPPORTED","A floating field requires exactly32 physical value bits.");
            var value=BitConverter.Int32BitsToSingle(unchecked((int)(uint)raw));
            if(!float.IsFinite(value))throw Fail("UNSUPPORTED","Non-finite floats are outside the admitted table value contract.");
            return value.ToString("R",CultureInfo.InvariantCulture);
        }
        var width=field.IntegerBits??32;
        if(storage.Kind==5)raw=unchecked((ulong)Signed(raw,storage.Width));
        if(width<64)
        {
            var mask=(1UL<<width)-1;
            if(raw>mask)
            {
                // Admit narrowing only when the discarded high bits are a canonical
                // signed representation. Kind5 remains explicitly signed even when
                // the selected schema renders the target integer as unsigned.
                // A pallet/common column selected for low-bit narrowing (D079) keeps its low bits.
                var sourceWidth=storage.Kind switch{0=>NoneWidth(storage),1=>storage.Width,5=>64,_=>32};
                var canonical=unchecked((ulong)Signed(raw&mask,width));
                if(sourceWidth<64)canonical&=(1UL<<sourceWidth)-1;
                if(!storage.KeepsLowBits&&(field.Unsigned==true&&storage.Kind!=5||raw!=canonical))
                    throw Fail("SCHEMA_MISMATCH",$"Field '{field.Column.Name}' uses storage kind {storage.Kind} with {sourceWidth} physical bits and cannot be represented by its selected {(field.Unsigned==true?"unsigned":"signed")}{width}-bit schema width.");
            }
            raw&=mask;
        }
        return field.Unsigned==true?raw.ToString(CultureInfo.InvariantCulture):Signed(raw,width).ToString(CultureInfo.InvariantCulture);
    }
    private static async Task<IReadOnlyList<string>> DecodeValues(Stream input,Location location,uint id,IReadOnlyList<BoundField> fields,
        Storage[] storage,IReadOnlyList<Section> sections,int recordSize,int declaredRecords,Wdc5Limits limits,CancellationToken token,RelationshipData?[] relationships,bool validateRelation)
    {
        var record=await ReadAt(input,location.Offset,recordSize,token);var values=new List<string>();
        foreach(var binding in fields)
        {
            token.ThrowIfCancellationRequested();var field=binding.Field;
            if(binding.Source==FieldSource.RecordId){values.Add(id.ToString(CultureInfo.InvariantCulture));continue;}
            if(binding.Source==FieldSource.Relationship){values.Add(ExternalRelationship(relationships,location.Section,location.LocalIndex,field));continue;}
            var column=storage[binding.StorageIndex];
            for(var i=0;i<field.ArrayLength;i++)
            {
                var raw=Raw(record,column,field,i,id);
                if(field.Column.Family is "string" or "locstring")
                {
                    if(NoneWidth(column)!=32)throw Fail("UNSUPPORTED","A relative string requires a32-bit displacement.");
                    if(raw==0){values.Add("");continue;}
                    var global=checked((location.GlobalIndex-(long)declaredRecords)*recordSize+column.FieldOffset+unchecked((int)(uint)raw));
                    values.Add(await StringAt(input,sections,global,limits.MaxStringBytes,recordSize,token));
                }
                else
                {
                    var value=field.IsId?id.ToString(CultureInfo.InvariantCulture):Number(raw,column,field);
                    if(validateRelation&&field.RelationStorage)CheckInlineRelationship(relationships,location.Section,location.LocalIndex,field,value);
                    values.Add(value);
                }
            }
        }
        return values.AsReadOnly();
    }
    private static async Task<string> StringAt(Stream input,IReadOnlyList<Section> sections,long offset,int maximum,int recordSize,CancellationToken token)
    {
        if(offset<0)throw Fail("INVALID","A relative string addresses before the concatenated string tables.");
        long first=0;
        foreach(var section in sections)
        {
            if(offset>=first+section.Strings){first+=section.Strings;continue;}
            if(section.Key!=0)throw Fail("ENCRYPTED","A readable record references a string in an unavailable keyed section.");
            var within=offset-first;var available=section.Strings-within;var bytes=new List<byte>(Math.Min(maximum,256));
            // Records end exactly at the beginning of this section's declared string block.
            var stringsAt=section.Offset+section.Count*(long)recordSize;
            while(available>0)
            {
                token.ThrowIfCancellationRequested();var count=(int)Math.Min(64,Math.Min(available,maximum-bytes.Count+1L));
                if(count<=0)throw Fail("LIMIT","A table string exceeds its byte bound.");
                var part=await ReadAt(input,stringsAt+within,count,token);
                foreach(var value in part)
                {
                    if(value==0)
                    {
                        try{return Utf8.GetString(bytes.ToArray());}catch(DecoderFallbackException){throw Fail("INVALID","A table value is not strict UTF-8.");}
                    }
                    if(bytes.Count>=maximum)throw Fail("LIMIT","A table string exceeds its byte bound.");bytes.Add(value);
                }
                within+=count;available-=count;
            }
            throw Fail("INVALID","A referenced table string is not NUL-terminated inside its section.");
        }
        throw Fail("INVALID","A relative string addresses outside the concatenated string tables.");
    }
}
