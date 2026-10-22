namespace VibeMiner.Infrastructure;

/// <summary>
/// The engine's diagnostic log: one bounded line on standard error, which the desktop drains and never shows the renderer.
/// Only engine-owned facts go here (codes, exception types, SQLite results, app-owned paths), never game content.
/// </summary>
internal static class EngineLog
{
    internal static void Write(string line)
    {
        var text = line.Length <= 600 ? line : line[..600];
        try { Console.Error.WriteLine("vibeminer-engine: " + text.ReplaceLineEndings(" ")); }
        catch (IOException) { /* A closed diagnostic pipe never fails the work that logs. */ }
        catch (ObjectDisposedException) { }
    }
}
