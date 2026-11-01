using VibeMiner.Cli.Profile;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests.Profile;

public sealed class ScrubRecordTests : IDisposable
{
    private static readonly DateTimeOffset Finished = new(2026, 11, 1, 12, 30, 0, TimeSpan.Zero);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "vibeminer-scrub-record-test-" + Guid.NewGuid().ToString("N"));

    public ScrubRecordTests() => Directory.CreateDirectory(folder);
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    private static ObjectKey Key(int n)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes, n);
        return new ObjectKey(ObjectKeyType.Encoded, bytes);
    }

    [Fact]
    public async Task ARecordRoundTripsAndReplacesTheOldOne()
    {
        var damaged = new ScrubReport("row count mismatch", true,
            new VerifyReport(100, 5000, 30, 2, Enumerable.Range(0, 30).Select(Key).ToArray(), [Key(99), Key(98)]));
        await ScrubRecords.WriteAsync(folder, ScrubRecord.FromReport(new ScrubReport(null, false, new VerifyReport(1, 2, 0, 0, [], [])), Finished));
        var record = ScrubRecord.FromReport(damaged, Finished);
        await ScrubRecords.WriteAsync(folder, record);

        var read = await ScrubRecords.ReadAsync(folder);
        Assert.Equal(ScrubRecordState.Present, read.State);
        var back = read.Record!;
        Assert.Equal(ScrubOutcome.Problem, back.Outcome);
        Assert.Equal("row count mismatch", back.IndexProblem);
        Assert.True(back.IndexRebuilt);
        Assert.Equal((100L, 5000L, 30L, 2L), (back.Checked, back.Bytes, back.CorruptCount, back.LostCount));
        Assert.Equal(ScrubRecord.MaxProblems, back.Problems.Count);
        Assert.Equal($"corrupt {Key(0)}", back.Problems[0]);
        Assert.Equal(record.Problems, back.Problems);
        Assert.Equal(Finished, back.FinishedAt);
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void ACleanReportIsCleanAndAnUncheckedIndexIsNot()
    {
        Assert.Equal(ScrubOutcome.Clean, ScrubRecord.FromReport(new ScrubReport(null, false, new VerifyReport(1, 2, 0, 0, [], [])), Finished).Outcome);
        var unchecked_ = ScrubRecord.FromReport(new ScrubReport(null, false, new VerifyReport(1, 2, 0, 0, [], []), IndexChecked: false), Finished);
        Assert.Equal(ScrubOutcome.Problem, unchecked_.Outcome);
        Assert.NotNull(unchecked_.IndexProblem);
    }

    [Fact]
    public async Task NoFileIsAbsent() => Assert.Equal(ScrubRecordState.Absent, (await ScrubRecords.ReadAsync(folder)).State);

    [Fact]
    public async Task AMissingFolderIsAbsent() =>
        Assert.Equal(ScrubRecordState.Absent, (await ScrubRecords.ReadAsync(Path.Combine(folder, "nope"))).State);

    [Theory]
    [InlineData("not json", "BadJson")]
    [InlineData("[1]", "BadJson")]
    [InlineData("""{"version":1}""", "BadJson")]
    [InlineData("""{"version":0}""", "BadJson")]
    [InlineData("""{"version":"1"}""", "BadJson")]
    [InlineData("""{"version":2,"anything":"new"}""", "Newer")]
    public async Task UnreadableRecordsAreTypedNotThrown(string text, string state)
    {
        await File.WriteAllTextAsync(ScrubRecords.PathIn(folder), text);
        var read = await ScrubRecords.ReadAsync(folder);
        Assert.Equal(Enum.Parse<ScrubRecordState>(state), read.State);
        Assert.Null(read.Record);
        Assert.NotNull(read.Reason);
    }

    [Fact]
    public async Task AWrongFieldOrOutcomeIsBadJson()
    {
        var good = File.ReadAllText(await Written());
        foreach (var bad in new[] { good.Replace("\"clean\"", "\"great\""), good.Replace("{\"version\":1,", "{\"version\":1,\"extra\":1,"), good.Replace("\"problems\":[]", "\"problems\":null") })
        {
            Assert.NotEqual(good, bad);
            await File.WriteAllTextAsync(ScrubRecords.PathIn(folder), bad);
            Assert.Equal(ScrubRecordState.BadJson, (await ScrubRecords.ReadAsync(folder)).State);
        }
    }

    [Fact]
    public async Task ANullProblemEntryIsBadJsonSaidSo()
    {
        var good = File.ReadAllText(await Written());
        await File.WriteAllTextAsync(ScrubRecords.PathIn(folder), good.Replace("\"problems\":[]", "\"problems\":[null]"));
        var read = await ScrubRecords.ReadAsync(folder);
        Assert.Equal(ScrubRecordState.BadJson, read.State);
        Assert.Contains("null problem", read.Reason);
    }

    [Fact]
    public async Task AWriteReplacesALeftoverTempFile()
    {
        await File.WriteAllTextAsync(ScrubRecords.PathIn(folder) + ".tmp", "half a record from a killed write, longer than the real one will be " + new string('x', 4000));
        await Written();
        Assert.Equal([ScrubRecords.PathIn(folder)], Directory.GetFiles(folder));
        Assert.Equal(ScrubRecordState.Present, (await ScrubRecords.ReadAsync(folder)).State);
    }

    [Fact]
    public async Task ARecordOver64KiBIsTooLarge()
    {
        await File.WriteAllTextAsync(ScrubRecords.PathIn(folder), "{\"version\":1,\"pad\":\"" + new string('x', ScrubRecords.MaxBytes) + "\"}");
        Assert.Equal(ScrubRecordState.TooLarge, (await ScrubRecords.ReadAsync(folder)).State);
    }

    private async Task<string> Written()
    {
        await ScrubRecords.WriteAsync(folder, ScrubRecord.FromReport(new ScrubReport(null, false, new VerifyReport(1, 2, 0, 0, [], [])), Finished));
        return ScrubRecords.PathIn(folder);
    }
}
