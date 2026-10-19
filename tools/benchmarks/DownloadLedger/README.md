# Download-ledger publication comparison

Explicit developer benchmark for [size-first hybrid storage](../../../docs/SIZE-AND-DEMAND-ARCHITECTURE.md). Compare the production immutable compact DL ledger with a narrow SQLite table storing only full binary EKey and encoded length. This does **not** repeat the old multi-table research catalog, claim equivalent crash durability, or measure download scheduling or first-source network latency.

Invoke from the repository root. Every write is fenced to a new named run beneath an ignored `.local` scratch directory; symbolic output ancestors are refused. Retained source catalogs/objects are opened read-only. There is no HTTP, game-install/cache access, source mutation, database migration, app launch or content admission. Existing runs are never overwritten. The ten-minute deadline and Ctrl-C propagate cancellation. Failed runs remain isolated for inspection; use another run name.

```sh
source tools/dev-env.sh
dotnet restore tools/benchmarks/DownloadLedger --locked-mode
dotnet build tools/benchmarks/DownloadLedger -c Release --no-restore
```

Prepare an already retained exact app-owned DL once. Arguments are the source engine data directory, its exact download-metadata EKey, and a new scratch directory. The source must have its original receipt and CAS object. Preparation verifies encoded length/SHA-256 and our original BLTE EKey/CKey proofs before retaining a decoded copy plus receipt/decoded SHA-256 in scratch. Configuration/network acquisition is excluded and no game payloads are acquired.

```sh
dotnet tools/benchmarks/DownloadLedger/bin/Release/net10.0/VibeMiner.DownloadLedger.Benchmark.dll prepare '/absolute/app-owned/engine-directory' FULL_DOWNLOAD_EKEY "$PWD/.local/download-ledger-comparison/cohort"
```

Run strategies sequentially in separate processes against the same prepared source. Rotate their order over at least three repetitions, with no concurrent benchmark workload. New run names are required. Neither this tool nor its sample commands evict the OS filesystem cache; explicitly describe these as retained-source/warm-cache measurements. Do not label a fresh process as a cold filesystem run.

```sh
dotnet tools/benchmarks/DownloadLedger/bin/Release/net10.0/VibeMiner.DownloadLedger.Benchmark.dll compact "$PWD/.local/download-ledger-comparison/cohort" compact-1
dotnet tools/benchmarks/DownloadLedger/bin/Release/net10.0/VibeMiner.DownloadLedger.Benchmark.dll sqlite "$PWD/.local/download-ledger-comparison/cohort" sqlite-1
dotnet tools/benchmarks/DownloadLedger/bin/Release/net10.0/VibeMiner.DownloadLedger.Benchmark.dll reopen-compact "$PWD/.local/download-ledger-comparison/cohort" compact-1
dotnet tools/benchmarks/DownloadLedger/bin/Release/net10.0/VibeMiner.DownloadLedger.Benchmark.dll reopen-sqlite "$PWD/.local/download-ledger-comparison/cohort" sqlite-1
```

Both publication strategies first check the exact decoded input SHA/length, then run the same original complete DL parser through `LedgerSourceStream`, checking decoded MD5/CKey and byte count before accepting output. Full keys are deduplicated; equal-key/equal-size repetitions collapse and conflicting lengths fail. Tags and complete EOF are parsed, but raw memberships/tags are preserved in the original metadata rather than expanded into SQL origins. The output comparison hashes canonical sorted records `(EKey16, signed big-endian Int64 encoded length)` and checks strict key order, count and checked total. This SHA-256 is an equivalence proof for this workload, not the proof that source acquisition was authorized or fresh.

- `compact` calls the actual production `DownloadLedger`: bounded 349,525-record chunks, at most 32-way merging, 1 GiB temporary reservation and 128 private run files, source checks, conflict checks, complete output SHA and CAS publication. Its production final publication currently hashes and renames without an explicit file/directory fsync. The benchmark separately measures an additional final-file `Flush(true)` barrier; parent-directory durability after rename and actual power loss remain unqualified. File and SQL registration are separate recovery resources.
- `sqlite` uses `objects(ekey BLOB PRIMARY KEY, bytes INTEGER NOT NULL) WITHOUT ROWID`, with full 16-byte key/length constraints, prepared parameterized inserts, WAL, `synchronous=FULL`, a 32 MiB page-cache limit, mmap disabled and 1 GiB database page bound. One private transaction covers the complete source; interrupted or invalid input cannot commit a complete output. This intentionally does not force 1,024 durability commits when the compact artifact also becomes usable only after full publication. Summary aggregation and WAL truncation/checkpoint are included. A whole-database SHA-256 is reported separately. SQLite SQL calls are synchronous in this standalone benchmark process; this is not production orchestration code.
- Reopen modes run in fresh processes. Compact runs the production full artifact SHA/header/record validator and then the separate canonical proof. SQLite verifies whole database SHA/length then scans canonical rows against the saved result. These are whole-file verified warm reopens; they do not establish a later incremental receipt-verification policy.

JSON reports per-phase elapsed time, exact object count/encoded byte sum, canonical record SHA, output SHA, logical final file bytes, process peak working set, managed heap and allocations. Counts/sizes are exact integers. Final output bytes exclude prepared inputs, temporary runs, run descriptors, `.owner`, filesystem allocation overhead and metadata registrations. Peak working set includes CLR/native/parser/cache memory; managed heap alone is not process memory. Temporary high-water bytes, actual block I/O, CPU isolation, cold cache and directory/power-loss durability are **not** instrumented. Keep final-file size separate from “bytes written.”

The initial Classic beta/retail matrix ran each benchmark strategy sequentially, but the coordinator's build, test and Electron verification overlapped on the same machine. It is therefore a warm, shared-workstation comparison rather than an idle or isolated-host result. Preserve the ranges and this condition when reporting its medians.

The fast authored check proves duplicate equivalence and conflicting-size refusal in both implementations. It writes only synthetic data and does not substitute for normal production tests:

```sh
dotnet tools/benchmarks/DownloadLedger/bin/Release/net10.0/VibeMiner.DownloadLedger.Benchmark.dll check "$PWD/.local/download-ledger-comparison/authored-check"
```

Do not commit prepared metadata, scratch catalogs, receipts or generated benchmark outputs. Record cohort identity, runtime, hardware/filesystem and actual results in the repository's verification documentation; do not extrapolate these measurements into a universal SQLite-versus-files or whole-application performance claim.
