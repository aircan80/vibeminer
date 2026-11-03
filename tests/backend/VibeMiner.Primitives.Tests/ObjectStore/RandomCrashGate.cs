namespace VibeMiner.Primitives.Tests;

/// <summary>Starts the random crash interval only after the child's stdout confirms a durable object.</summary>
internal sealed class RandomCrashGate
{
    private readonly TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Observe(string line)
    {
        if (line.StartsWith("COMMITTED ", StringComparison.Ordinal)) committed.TrySetResult();
    }

    public void Complete() => committed.TrySetCanceled();

    public async Task WaitAsync(TimeSpan delay, TimeSpan confirmationTimeout)
    {
        await committed.Task.WaitAsync(confirmationTimeout);
        await Task.Delay(delay);
    }
}
