# Inventory matching experiment

Developer-only, explicit local benchmark of an already acquired app-owned EN/DS workload. No HTTP, game-install access or admission of inventories. Comparison modes open the source catalog read-only; `integrated` writes only an explicitly named disposable clone. It uses our original readers and BLTE verification. Do not point this at a running application's catalog; use isolated development data.

```sh
source tools/dev-env.sh
dotnet restore tools/benchmarks/InventoryMatching --locked-mode
dotnet build tools/benchmarks/InventoryMatching -c Release --no-restore
```

The positional arguments are mode, absolute app-owned data directory, generation, and absolute scratch directory. Example using the retained isolated qualification data:

```sh
dotnet tools/benchmarks/InventoryMatching/bin/Release/net10.0/VibeMiner.InventoryMatching.Benchmark.dll prepare "$PWD/.local/inventory-native-check/catalog with spaces" 1 "$PWD/.local/inventory-matching-benchmark"
dotnet tools/benchmarks/InventoryMatching/bin/Release/net10.0/VibeMiner.InventoryMatching.Benchmark.dll map "$PWD/.local/inventory-native-check/catalog with spaces" 1 "$PWD/.local/inventory-matching-benchmark"
dotnet tools/benchmarks/InventoryMatching/bin/Release/net10.0/VibeMiner.InventoryMatching.Benchmark.dll merge "$PWD/.local/inventory-native-check/catalog with spaces" 1 "$PWD/.local/inventory-matching-benchmark"
dotnet tools/benchmarks/InventoryMatching/bin/Release/net10.0/VibeMiner.InventoryMatching.Benchmark.dll direct "$PWD/.local/inventory-native-check/catalog with spaces" 1 "$PWD/.local/inventory-matching-benchmark"
```

- `prepare` saves only counts, width, source-ordinal offset and exact retained receipts in ignored scratch `inputs.json`. It requires one EN/DS receipt pair and checks that every explicit DL key is in EN. This is a focused comparison, not the general inventory resolver: other builds can require the union of EN and explicit DL objects.
- `map` reads EN objects into compact binary prefix buckets; reads DS requests in source order; checks complete-key uniqueness and sizes. The map has a conservative 384 MiB charged-index admission bound. Source reading and matching are fused and reported as combined phases.
- `merge` lets SQLite order DS by reference, then merges bounded readers with EN's existing full-key order. Sort/first-row cost is reported separately from the remaining streaming match. No whole-catalog C# collection is retained.
- `direct` never opens SQLite. It reads `inputs.json`, SHA/length-checks retained encoded sources, verifies/decodes BLTE into temporary files, parses full EN and DS, then sorts compact DS requests and merges. Each compact array has a 128 MiB bound; EN capacity is derived from its header, not SQLite's record count. Decoded temporary files are removed even on failure.
- `integrated` invokes the actual production ordered resolver and batch publication for DL and DS. It refuses a catalog directory unless it is exactly `<scratch-dir>/integration-catalog`. Supply a disposable clone containing a STAGING generation, never live app data. It checks for unresolved memberships and source-size disagreement afterward, but does not parse sources, compute the canonical digest, seal a generation or open Home.
- `root` uses the same disposable-clone guard and exercises production ROOT/IN forward lookup and batch publication. It checks remaining mappings and ledger sizes afterward. A missing mapping fails the run and rolls back its current batch; partial progress is not successful complete inventory qualification.
- `baseline` measures just 10,000 per-record SQL range lookups. It omits the original workflow's writes, optional reverse CKey queries and worker scheduling; it is not a full-build baseline or an extrapolation.
- `publish` uses a separate disposable SQLite database, WAL and synchronous FULL. It measures 100,000 shuffled complete EN identities in 1,024-row transactions, then 1,000 separate completion transactions, each inserting an object association and receipt-shaped row. It does not publish game bytes or claim production receipt verification. This simplified two-table workload is not the actual multi-index inventory publication.

Run strategies sequentially in separate processes with an idle benchmark workload. Rotate order and repeat; keep performance measurements separate from unit tests. The process has a five-minute deadline and cooperative Ctrl-C; no per-row tasks. Array sorting is a synchronous bounded CPU phase in this standalone tool. Query sorting can do SQLite temporary-file work; total temporary disk was not instrumented in the first experiment.

Results contain counts, sums, an order-independent 64-bit workload fingerprint, phase timings, process peak working set, managed heap and allocation totals. The fingerprint is an equivalence diagnostic, not a cryptographic publication proof. Peak working set includes CLR, SQLite and parser memory; managed heap alone understates memory use. `notes` describes the matching timings; the `publish` mode reports its separate explicit writes.

The application now uses the resumable ordered cursor for DL/DS. The three original comparison modes remain isolated algorithm experiments; their few-second timings exclude production publication. Full preparation also performs source acquisition/indexing, ROOT/IN resolution, retained-object verification, canonical integrity and sealing. See [storage notes](../../../docs/STORAGE.md).
