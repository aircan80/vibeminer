namespace VibeMiner.Primitives.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
