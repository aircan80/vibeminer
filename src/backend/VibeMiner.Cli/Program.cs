using Spectre.Console;
using VibeMiner.Cli;
using VibeMiner.Cli.Browse;

// vm: the developer CLI (#170). Ctrl+C cancels the running command; a second Ctrl+C ends the process.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cancel.IsCancellationRequested) return;
    e.Cancel = true;
    cancel.Cancel();
};
// Spectre detects colour support and NO_COLOR. It keeps ANSI codes when a stream is redirected, so a redirected stream gets
// plain text here, wide enough that paths in tables and panels don't wrap at 80 columns.
static IAnsiConsole Open(TextWriter writer, bool redirected) =>
    redirected ? Consoles.Plain(writer, 200) : AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
var output = Open(Console.Out, Console.IsOutputRedirected);
var error = Open(Console.Error, Console.IsErrorRedirected);
// vm browse takes over the terminal, so it needs one on both ends.
var terminal = !Console.IsInputRedirected && !Console.IsOutputRedirected;
return await new VmCommands(CliEnvironment.Current(), new RealStores(), output, error, Console.Out, terminal ? BrowseShell.RunAsync : null)
    .RunAsync(args, cancel.Token);
