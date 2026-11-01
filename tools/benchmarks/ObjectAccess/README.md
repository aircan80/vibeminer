# Object access live measurement

Developer-only (#154, [STORAGE](../../../docs/STORAGE.md#getting-keys-the-cdn-clients-and-object-access)). It puts one real build's metadata through layer 2 (`ObjectSource`) into a new store from the official CDN, then measures merged ranges and interactive latency. It never reads a profile, a game install or a game cache, and nothing it downloads belongs in git: give it an empty folder under the checkout's ignored `.local/`.

```sh
dotnet build tools/benchmarks/ObjectAccess/ObjectAccess.csproj -c Release
B=tools/benchmarks/ObjectAccess/bin/Release/net10.0/VibeMiner.ObjectAccess.Benchmark.dll
dotnet $B "$PWD/.local/live" wow us 256 1024     # about 1.5 GB from the CDN on a fresh folder
```

- **Metadata pass**, twice: versions and cdns into the history log, the build and CDN configs, every archive index and the file-index, ENCODING (decoded to find ROOT), ROOT, DOWNLOAD, INSTALL and SIZE. The second pass must make 0 object requests.
- **Bulk**: half the entries in the first 32 MiB of 32 archives. Archives alternate between the gap settings given (KiB), so neither gets the warmer edge cache; the planner's request and gap figures for every setting over all of them come first.
- **Interactive**: 32 background streams on 512 KiB ranges, and the CDN config fetched at interactive priority 10 times meanwhile.

`VIBEMINER_BENCH_SKIP=N` starts the bulk and interactive archives at the Nth (pick fresh ones for a rerun on the same folder), `VIBEMINER_BENCH_ONLY=interactive` skips the bulk part, and `VIBEMINER_BENCH_SHARED_CONNECTIONS=1` sends interactive requests over the background client's connections. Say which line, region and time a number came from: the edge cache makes every run different.
