using VibeMiner.Files;
using VibeMiner.Formats.Tables;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>The handle's progress pump (#159): inside its interval it still delivers the download's end and the stage change.</summary>
public sealed class PreloadTallyTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheDownloadsEndAndThePreparingStageGoOutInsideTheInterval()
    {
        var reports = new Reports();
        var clock = new Clock();
        var tally = new PreloadTally(reports, clock, TimeSpan.FromHours(1));
        tally.Tables(10, 0);
        await reports.Until(r => r.TablesTotal == 10);

        tally.Report(new EnsureProgress(1, 4, 10, 40, 10, true)); // inside the interval: held back
        tally.Downloaded(new EnsureProgress(4, 4, 40, 40, 40, true), 0);
        await reports.Until(r => r.FilesDone == 4);
        tally.Preparing();
        await reports.Until(r => r.Stage == TablePreloadStage.Preparing);

        var result = await tally.FinishAsync(null).WaitAsync(Guard);
        Assert.Equal(result.Final, reports.All[^1]);
    }

    /// <summary>Time stands still, so every offer after the first is inside the interval.</summary>
    private sealed class Clock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }

    private sealed class Reports : IProgress<TablePreloadProgress>
    {
        private readonly List<TablePreloadProgress> seen = [];
        private readonly SemaphoreSlim changed = new(0);
        public TablePreloadProgress[] All { get { lock (seen) return [.. seen]; } }

        public void Report(TablePreloadProgress value)
        {
            lock (seen) seen.Add(value);
            changed.Release();
        }

        /// <summary>Waits until a delivered report matches; the timeout is the failure.</summary>
        public async Task Until(Func<TablePreloadProgress, bool> match)
        {
            var deadline = DateTime.UtcNow + Guard;
            while (!All.Any(match))
                Assert.True(await changed.WaitAsync(deadline - DateTime.UtcNow > TimeSpan.Zero ? deadline - DateTime.UtcNow : TimeSpan.Zero),
                    $"no matching report; saw {All.Length}");
        }
    }
}
