using VibeMiner.Objects;
using VibeMiner.Objects.Testing;

// Test child for the real SIGKILL tests: VibeMiner.Objects.CrashWriter <store dir> <point|none> <occurrence> <iterations>.
// Runs Workload and prints every confirmation the store gave. With a point, it stops there, prints "AT <point>" and waits to be
// killed. Synthetic data only.
if (args.Length != 4) { Console.Error.WriteLine("usage: <dir> <point|none> <occurrence> <iterations>"); return 2; }
StorePoint? target = args[1] == "none" ? null : Enum.Parse<StorePoint>(args[1]);
var occurrence = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
var iterations = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
var seen = 0;
var output = Console.Out;
void Say(string line) { lock (output) { output.WriteLine(line); output.Flush(); } }
var options = Workload.Options with
{
    Fault = point =>
    {
        if (point != target || Interlocked.Increment(ref seen) != occurrence) return;
        Say($"AT {point}");
        Thread.Sleep(Timeout.Infinite);
    },
};
await using var store = await ObjectStore.OpenAsync(args[0], options);
Say("OPEN");
await Workload.RunAsync(store, Say, iterations);
Say("DONE");
return 0;
