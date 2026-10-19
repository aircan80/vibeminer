using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed class CreatureGeometryStoreProgress(Func<CreatureGeometryProgress,Task> callback, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private CreatureGeometryProgress? latest;
    private Task? pump, retirement;
    private bool closed;
    private long? previous;
    internal void Post(CreatureGeometryProgress value)
    {
        CreatureGeometryValidation.ValidateProgress(value);
        lock(gate) { if(closed)return; latest=value; pump ??= PumpAsync(); }
    }
    private async Task PumpAsync()
    {
        await Task.Yield();
        try
        {
            while(true)
            {
                if(previous is {} at) { var remaining=TimeSpan.FromMilliseconds(100)-clock.GetElapsedTime(at); if(remaining>TimeSpan.Zero)await Task.Delay(remaining,clock,stop.Token); }
                CreatureGeometryProgress value;
                lock(gate) { if(closed||latest is null){pump=null;return;}value=latest;latest=null;previous=clock.GetTimestamp(); }
                await callback(value);
            }
        }
        catch(Exception error) when(error is not OutOfMemoryException) {lock(gate){closed=true;latest=null;pump=null;}}
    }
    internal Task CloseAsync()
    {
        lock(gate) { if(retirement is not null)return retirement;closed=true;latest=null;return retirement=CloseCoreAsync(pump??Task.CompletedTask); }
    }
    private async Task CloseCoreAsync(Task active)
    { await Task.Yield();await stop.CancelAsync();await active;stop.Dispose(); }
}
