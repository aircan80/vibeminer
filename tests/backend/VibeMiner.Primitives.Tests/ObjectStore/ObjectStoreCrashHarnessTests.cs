using Xunit;

namespace VibeMiner.Primitives.Tests;

public sealed class ObjectStoreCrashHarnessTests
{
    [Fact]
    public async Task RandomKillWaitsForACommittedObjectRatherThanOpenOrPutAttempt()
    {
        var gate = new RandomCrashGate();
        // A zero random interval would previously kill as soon as OPEN was observed, before this child's slow first write.
        var kill = gate.WaitAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5));
        foreach (var line in new[] { "OPEN", "PUTTING 0123456789abcdef0123456789abcdef", "COMMITTEDLY not a confirmation" })
        {
            gate.Observe(line);
            Assert.False(kill.IsCompleted);
        }
        gate.Observe("COMMITTED G 0 0123456789abcdef0123456789abcdef");
        await kill.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AChildThatNeverConfirmsACommitHitsTheBoundedDeadlockGuard()
    {
        var gate = new RandomCrashGate();
        gate.Observe("OPEN");
        await Assert.ThrowsAsync<TimeoutException>(() => gate.WaitAsync(TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public async Task AChildThatExitsBeforeItsFirstCommitDoesNotStartTheRandomInterval()
    {
        var gate = new RandomCrashGate();
        var kill = gate.WaitAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5));
        gate.Observe("OPEN");
        gate.Complete();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kill);
    }
}
