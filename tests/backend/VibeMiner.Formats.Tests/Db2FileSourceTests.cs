using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats;
using Xunit;

namespace VibeMiner.Formats.Tests;

// The DB2 reader over layer 4, through the in-memory file system fake.
public sealed class Db2FileSourceTests
{
    private static readonly BuildRef Build=new("wow",new string('a',32),new string('b',32));
    private const string Key="000000000000007B";

    [Fact]
    public async Task A_clear_table_opens_by_id_and_by_path_and_reads_every_row()
    {
        var (bytes,definition)=Db2TableTests.ScalarFixture();
        await using var files=await Open(new FakeFile(10,"dbfilesclient/scalar.db2",Content:bytes,Storage:FileStorage.Stored));
        foreach(var file in new[]{FileRef.Id(10),FileRef.ForPath("DBFilesClient\\Scalar.db2")})
        {
            await using var opened=await Db2Reader.OpenAsync(files,file,definition,"1.2.3.4",new());
            Assert.Equal(Db2OpenState.Open,opened.State);Assert.Null(opened.Code);
            var rows=new List<Db2Row>();await foreach(var row in opened.Table!.ReadAllAsync())rows.Add(row);
            Assert.Equal([1u,uint.MaxValue,8u],rows.Select(row=>row.Id));
            Assert.Equal("hello",rows[1].Values[3]);
        }
    }

    [Fact]
    public async Task An_encrypted_section_becomes_a_hole_named_by_its_key_and_the_clear_rows_still_read()
    {
        var fixture=Db2HoleTests.Build();var hole=new FileHole(fixture.Starts[1],fixture.Ends[1]-fixture.Starts[1],Key);
        await using var files=await Open(new FakeFile(20,Content:fixture.Bytes,Holes:[hole]));

        var source=await Db2FileSource.OpenAsync(files,20,default);
        await using(source.Source)
        {
            Assert.Equal(Db2OpenState.Open,source.State);
            Assert.Equal(fixture.Bytes.Length,source.Source!.Length);
            Assert.Equal([new Db2Hole(hole.Offset,hole.Length,Key)],source.Source.Holes);
            // A direct read into the hole is a typed Protected failure, never the hole's bytes or a layer 4 exception.
            var into=await Assert.ThrowsAsync<Db2SourceException>(async()=>await source.Source.ReadRangeAsync(hole.Offset-2,new byte[4],default));
            Assert.Equal(Db2OpenState.Protected,into.State);Assert.Contains(Key,into.Message);
        }

        await using var opened=await Db2Reader.OpenAsync(files,20,Db2HoleTests.Definition(),"1.2.3.4",new(AllowPartialEncryptedSections:true));
        Assert.Equal(Db2OpenState.Open,opened.State);
        Assert.True(opened.Table!.Sections[1].IsEncrypted);
        var rows=new List<Db2Row>();await foreach(var row in opened.Table.ReadAllAsync())rows.Add(row);
        Assert.Equal([7u,8u],rows.Select(row=>row.Id));
        Assert.IsType<Db2Lookup.Protected>(await opened.Table.FindAsync(21));

        // Without partial admission the reader's refusal is a state, not an exception.
        await using var refused=await Db2Reader.OpenAsync(files,20,Db2HoleTests.Definition(),"1.2.3.4",new());
        Assert.Equal(Db2OpenState.Rejected,refused.State);Assert.Equal("DB2_ENCRYPTED",refused.Code);Assert.Null(refused.Table);
    }

    [Fact]
    public async Task A_file_wholly_in_holes_is_protected()
    {
        var fixture=Db2HoleTests.Build();
        await using var files=await Open(new FakeFile(21,Content:fixture.Bytes,Holes:[new FileHole(0,fixture.Bytes.Length,Key)]));
        await using var opened=await Db2Reader.OpenAsync(files,21,Db2HoleTests.Definition(),"1.2.3.4",new(AllowPartialEncryptedSections:true));
        Assert.Equal(Db2OpenState.Protected,opened.State);Assert.Contains(Key,opened.Message);Assert.Null(opened.Table);
    }

    [Fact]
    public async Task A_missing_file_keeps_its_state_and_time()
    {
        var seen=new DateTimeOffset(2026,11,1,0,0,0,TimeSpan.Zero);
        await using var files=await Open(new FakeFile(30,Size:100,ReadFailure:new(FileReadState.Missing,"OBJECT_ACCESS_MISSING",ObservedAt:seen)),
            new FakeFile(31,Locales:["deDE"]));
        await using var missing=await Db2Reader.OpenAsync(files,30,Db2HoleTests.Definition(),"1.2.3.4",new());
        Assert.Equal((Db2OpenState.Missing,"OBJECT_ACCESS_MISSING",seen),(missing.State,missing.Code,missing.ObservedAt));
        await using var unknown=await Db2Reader.OpenAsync(files,99,Db2HoleTests.Definition(),"1.2.3.4",new());
        Assert.Equal(Db2OpenState.UnknownFile,unknown.State);
        await using var otherLocale=await Db2Reader.OpenAsync(files,31,Db2HoleTests.Definition(),"1.2.3.4",new());
        Assert.Equal(Db2OpenState.NotInLocale,otherLocale.State);
    }

    [Fact]
    public async Task A_file_that_needs_the_network_says_so()
    {
        await using var files=await Open(new FakeFile(40,Size:500));
        await using var opened=await Db2Reader.OpenAsync(files,40,Db2HoleTests.Definition(),"1.2.3.4",new());
        Assert.Equal(Db2OpenState.NeedsNetwork,opened.State);Assert.Equal("OBJECT_ACCESS_NEEDS_NETWORK",opened.Code);Assert.Null(opened.Table);
    }

    [Fact]
    public async Task A_read_that_fails_during_open_is_a_state_and_after_open_a_typed_exception()
    {
        var (bytes,definition)=Db2TableTests.ScalarFixture();
        await using var files=await Open(new FakeFile(50,Content:bytes,ReadFailure:new(FileReadState.WrongKey,"FILES_WRONG_KEY")));
        await using var opened=await Db2Reader.OpenAsync(files,50,definition,"1.2.3.4",new());
        Assert.Equal((Db2OpenState.WrongKey,"FILES_WRONG_KEY"),(opened.State,opened.Code));

        var source=await Db2FileSource.OpenAsync(files,50,default);
        await using(source.Source)
        {
            var failure=await Assert.ThrowsAsync<Db2SourceException>(async()=>await source.Source!.ReadRangeAsync(0,new byte[4],default));
            Assert.Equal(Db2OpenState.WrongKey,failure.State);
        }
    }

    private static async Task<IBuildFiles> Open(params FakeFile[] files)
    {
        var opened=await new InMemoryFileSystem().Add(Build,["enUS","deDE"],files).ForAsync(Build,"enUS",default);
        return opened.Files!;
    }
}
