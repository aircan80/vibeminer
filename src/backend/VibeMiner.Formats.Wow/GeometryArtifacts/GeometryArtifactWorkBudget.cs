namespace VibeMiner.Formats.Wow;

/// <summary>Caller-owned admission for finite CPU work. Scopes never cover stream I/O or yields.</summary>
public interface IGeometryArtifactWorkBudget
{
    ValueTask<IDisposable> EnterAsync(CancellationToken token);
}

internal static class GeometryArtifactWork
{
    internal static readonly IGeometryArtifactWorkBudget Unbudgeted = new NoBudget();

    internal static async ValueTask<IDisposable> EnterAsync(IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = await budget.EnterAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            return scope ?? throw new InvalidOperationException("The geometry work budget returned no scope.");
        }
        catch { scope?.Dispose(); throw; }
    }

    private sealed class NoBudget : IGeometryArtifactWorkBudget, IDisposable
    {
        public ValueTask<IDisposable> EnterAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IDisposable>(this); }
        public void Dispose() { }
    }
}
