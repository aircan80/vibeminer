namespace VibeMiner.Objects.Testing;

/// <summary>
/// The fixed crash-test workload: mixed Put batches, a Delete, a Compact, a staged large object, a history observation and every
/// third pass a Scan, so every StorePoint is reached. Each confirmation is reported as a line the checker can replay.
/// </summary>
public static class Workload
{
    public static PutObject Make(string kind, int seed) => kind switch
    {
        "G" => Synthetic.Config(seed, 100 + seed % 100 * 13 % 1000),
        "I" => Synthetic.Index(seed, 40 + seed % 100),
        "F" => Synthetic.Index(seed, 30 + seed % 100, offsetWidth: 0),
        "C" => Synthetic.Chunked(seed, 3000, 70_000, 900),
        "H" => Synthetic.Headerless(seed, 200 + seed % 5000),
        "S" => Synthetic.Chunked(seed, Enumerable.Repeat(65_536, 40).ToArray()),
        _ => throw new ArgumentException(kind),
    };

    private static readonly string[] Kinds = ["G", "I", "F", "C", "H", "H"];

    public static async Task RunAsync(ObjectStore store, Action<string> say, int iterations, int first = 0)
    {
        for (var it = first; it < first + iterations; it++)
        {
            var deletable = new List<ObjectKey>();
            for (var b = 0; b < 6; b++)
            {
                var batch = Enumerable.Range(0, 12).Select(j => (kind: Kinds[j % 6], seed: it * 10_000 + b * 100 + j)).Select(x => (x.kind, x.seed, o: Make(x.kind, x.seed))).ToList();
                foreach (var x in batch) say($"PUTTING {x.o.Key.KeyHex}");
                var results = await store.PutAsync(batch.Select(x => x.o).ToList());
                for (var j = 0; j < batch.Count; j++)
                    if (results[j].Outcome is PutOutcome.Stored or PutOutcome.AlreadyPresent) say($"COMMITTED {batch[j].kind} {batch[j].seed} {batch[j].o.Key.KeyHex}");
                    else throw new InvalidOperationException($"{batch[j].o.Key}: {results[j].Outcome} {results[j].Code} {results[j].Message}");
                if (b == 0) deletable.AddRange(batch.Take(4).Select(x => x.o.Key));
            }
            foreach (var d in deletable) say($"DELETING {d.KeyHex}");
            foreach (var d in await store.DeleteAsync(deletable)) say($"DELETED {d.Key.KeyHex}");
            await store.CompactAsync(new CompactOptions(0.001, 4));
            say("COMPACTED");
            var bigSeed = it * 10_000 + 9_000;
            var big = Make("S", bigSeed);
            await using (var staging = await store.StageAsync(big.Key, big.DeclaredLength))
            {
                for (var at = (int)staging.Length; at < big.Bytes.Length; at += 1_000_000)
                    await staging.AppendAsync(big.Bytes.Slice(at, Math.Min(1_000_000, big.Bytes.Length - at)));
                var staged = await staging.CommitAsync();
                if (staged.Outcome is not (PutOutcome.Stored or PutOutcome.AlreadyPresent)) throw new InvalidOperationException($"staged {staged.Outcome} {staged.Code}");
                say($"COMMITTED S {bigSeed} {big.Key.KeyHex}");
            }
            var history = HistoryRecordFormat.Objects(Synthetic.Observation(it));
            var h = await store.PutAsync(history);
            if (h.All(r => r.Outcome is PutOutcome.Stored or PutOutcome.AlreadyPresent)) say($"COMMITTED Y {it} {history[1].Key.KeyHex}");
            if (it % 3 == 2) { await store.ScanAsync(); say("SCANNED"); }
        }
    }

    /// <summary>The options the workload runs with: small commits and small packs, so it rolls packs and commits often.</summary>
    public static ObjectStoreOptions Options => new() { CommitObjects = 5, MaxPackBytes = 512 << 10, CommitDelay = TimeSpan.FromMilliseconds(30) };
}
