# Object store benchmarks

Developer-only measurements for the layer 1 object store (#153, [STORAGE](../../../docs/STORAGE.md#layer-1-the-object-store)). Synthetic headerless BLTE objects only; it never reads a profile, a game install or the CDN. Each command needs a new or empty folder and leaves its store there, so put it under the checkout's ignored `.local/`.

```sh
dotnet build tools/benchmarks/ObjectStore/ObjectStore.csproj -c Release
B=tools/benchmarks/ObjectStore/bin/Release/net10.0/VibeMiner.ObjectStore.Benchmark.dll
dotnet $B append   "$PWD/.local/bench/append"        # about 3.3 GB written
dotnet $B recovery "$PWD/.local/bench/recovery"      # about 8 GB written
dotnet $B lookups  "$PWD/.local/bench/lookups" 3     # cached 3M scale store; 10 for sign-off, 30 only as an optional extreme; "rebuild", "scan"
```

- **append**: 20,000 objects in the phase 0 size shape (p50 23 KiB, p90 74 KiB, p99 800 KiB, capped at 16 MiB), default commit rule, with 1, 8 and 32 concurrent callers.
- **recovery**: 4 GiB in 1 GiB packs, then a crash (the test fault hook) with 16 MiB synced but uncommitted; times the reopen that recovers it, a clean reopen, Scan, Verify, and an open with the index deleted.
- **lookups**: N million 17-byte objects (3 by default; 10 for sign-off and for index, lookup or rebuild changes; 30 is an optional extreme run nothing requires) from the shared scale store in the main checkout's `.local/scale-store/<N>M/`, built once and reused while its format and count match (`rebuild` forces it). Times the read-only open with its index check, Has for present and absent keys and Read, after a warm-up. `scan` adds a Scan rebuild on a reflink copy in the work folder.

Page cache is warm in every case (the data was just written). Wait for other test or benchmark work to finish first, and say which machine, filesystem and cache state a number came from. The opt-in xUnit test `ObjectStoreScaleTests` (`VIBEMINER_OBJECT_STORE_SCALE=N`, and `VIBEMINER_OBJECT_STORE_SCALE_SCAN=1` for the rebuild) checks the lookup and Scan criteria themselves on the same cached store ([TESTING](../../../docs/TESTING.md#object-store-scale-store)).
