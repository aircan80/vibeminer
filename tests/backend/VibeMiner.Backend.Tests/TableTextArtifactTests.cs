using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TableTextArtifactTests
{
    private static readonly TableArtifactBinding Binding = new(new('a',64), new('b',64), new('c',64),
        "synthetic", "enUS", "wdc5-values-v5+table-admission-v1", new('d',64));
    private static readonly Wdc5TableHeader Header = new(5,"12345678","87654321",1,1,16,
        [new("ID","integer",true),new("Description","string",false),new("Name","string",false)]);
    private static Db2Row Row(uint id, bool copy = false) => new(id,copy,copy?1u:null,
        [id.ToString(System.Globalization.CultureInfo.InvariantCulture),"","A valid name"],
        [new(1,TableTextEvidence.InvalidUtf8,"61ff62")]);

    [Fact]
    public async Task Immutable_rows_reopen_exact_byte_evidence_and_count_copy_occurrences()
    {
        var directory=Path.Combine(Path.GetTempPath(),"vibeminer-text-artifact-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var objects=new VerifiedObjectStore(directory);
            TableRowsArtifact artifact;
            await using(var rows=new TableRowArtifacts(objects))
            {
                await using var writer=await rows.CreateWriterAsync(Binding,default);
                await writer.HeaderAsync(Header,default);
                await writer.RowAsync(Row(1),default);await writer.RowAsync(Row(2,true),default);
                artifact=await writer.CompleteAsync(new(1,1,1,0,0,0,"complete",32,2),default);
            }
            await using var reopened=new TableRowArtifacts(objects);
            var page=await reopened.ReadPageAsync(artifact,0,50,null,default);
            Assert.Equal(2,artifact.Completion.TextIssueCount);Assert.Equal(2,page.Rows.Count);
            Assert.All(page.Rows,row=>
            {
                Assert.Equal("",row.Values[1]);Assert.Equal("A valid name",row.Values[2]);
                Assert.Equal(new Db2TextIssue(1,TableTextEvidence.InvalidUtf8,"61ff62"),Assert.Single(row.TextIssues!));
            });
            var copy=Assert.Single((await reopened.ReadPageAsync(artifact,0,50,2,default)).Rows);
            Assert.True(copy.IsCopy);Assert.Equal(1u,copy.CopySourceId);
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }

    [Fact]
    public async Task Wrong_aggregate_issue_count_cannot_publish_ready_rows()
    {
        var directory=Path.Combine(Path.GetTempPath(),"vibeminer-text-count-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var objects=new VerifiedObjectStore(directory);await using var rows=new TableRowArtifacts(objects);
            await using var writer=await rows.CreateWriterAsync(Binding,default);
            await writer.HeaderAsync(Header with{DeclaredCopyRecords=0},default);await writer.RowAsync(Row(1),default);
            var error=await Assert.ThrowsAsync<DiscoveryException>(()=>writer.CompleteAsync(new(1,1,0,0,0,0,"complete",16,0),default));
            Assert.Equal("TABLE_ARTIFACT_INVALID",error.Code);
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData("validUtf8")][InlineData("guessedText")][InlineData("integerColumn")]
    public async Task Publication_rejects_forged_text_issue_evidence(string problem)
    {
        var directory=Path.Combine(Path.GetTempPath(),"vibeminer-text-invalid-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var objects=new VerifiedObjectStore(directory);await using var rows=new TableRowArtifacts(objects);
            await using var writer=await rows.CreateWriterAsync(Binding,default);await writer.HeaderAsync(Header,default);
            var row=Row(1);
            if(problem=="validUtf8")row=row with{TextIssues=[new(1,TableTextEvidence.InvalidUtf8,"6162")]};
            if(problem=="guessedText")row=row with{Values=["1","guessed","A valid name"]};
            if(problem=="integerColumn")row=row with{Values=["","","A valid name"],TextIssues=[new(0,TableTextEvidence.InvalidUtf8,"ff")]};
            Assert.Equal("TABLE_TEXT_EVIDENCE_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>writer.RowAsync(row,default).AsTask())).Code);
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
}
