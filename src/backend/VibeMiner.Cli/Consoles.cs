using Spectre.Console;

namespace VibeMiner.Cli;

/// <summary>Spectre consoles for the <c>vm</c> commands.</summary>
internal static class Consoles
{
    /// <summary>
    /// A plain, non-interactive console over a writer: no ANSI codes, no colour, a fixed width. Spectre's profile enrichers
    /// (GitHub Actions, other CI hosts) turn ANSI back on after the settings are applied, so they are off here and the
    /// environment can't change the output (#183).
    /// </summary>
    internal static IAnsiConsole Plain(TextWriter writer, int width, Dictionary<string, string>? environment = null)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            EnvironmentVariables = environment,
        });
        console.Profile.Width = width;
        return console;
    }
}
