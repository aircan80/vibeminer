# Files query benchmark

Developer-only timing of the Files queries ([D068](../../../docs/DECISIONS.md), [inventory-v1](../../../contracts/inventory-v1.md#files)) over a retail-sized inventory. No HTTP, game-install access or app launch.

It copies the ROOT and install memberships of one generation from an existing app-owned catalog into a fresh catalog in a new scratch directory. The source is opened read-only and `immutable`, so its `-wal`/`-shm` files are not touched. The copy keeps every real FileDataID, CKey, locale mask, content flag, name hash and raw block ordinal; EKeys, sizes and a 2% stored sample are synthetic and derived from the CKey. It then records the Files statistics and times 18 filter cases with 2% stored and three with every membership stored: the first page five times (median and max), then up to 200 pages, counting scan-limited pages. It also times four inspector reads.

```sh
source tools/dev-env.sh
dotnet restore tools/benchmarks/FilesQuery --locked-mode
dotnet build tools/benchmarks/FilesQuery --configuration Release --no-restore
dotnet tools/benchmarks/FilesQuery/bin/Release/net10.0/VibeMiner.FilesQuery.Benchmark.dll '/absolute/app-owned/engine-directory' 1 "$PWD/.local/files-query/run-1"
```

Arguments: the source engine data directory, its inventory generation (staging or ready; only `inventory_origins` is read), and a scratch directory that must not exist yet. The copy takes about a minute for 3.3 M memberships and writes about 1.3 GiB. Results are warm-cache numbers from one process; they say nothing about a cold disk or a busy database worker.
