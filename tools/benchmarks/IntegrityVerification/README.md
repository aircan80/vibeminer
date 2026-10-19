# Synthetic integrity verification

Developer-only benchmark for work item0020. This uses deterministic synthetic files and the actual CAS implementation; it never reads application profiles, databases, installations, game bytes or CDN sources. Every run writes to a new named directory under this checkout's ignored `.local/integrity-verification/`, refuses symbolic output ancestors and refuses an existing run name. The source object is4MiB or64MiB; generation and CAS publication use one64KiB buffer. Ctrl-C and a ten-minute deadline propagate cancellation.

The project adds no engine API or shared-project changes. Exact internal hash/open delegates and the existing test-only pre-stripe observer constructor are bound with reflection once, outside measured phases; changed signatures fail. All object identity, SHA/length checks, gates and cleanup remain the actual implementation. `legacy` means the current compatibility hash/open under the former caller's outer CPU permit. Both current hash routes share the same algorithm and forced-turn logic; this is a scheduling comparison, not a recreation of a historic binary.

Build from an explicitly frozen source archive containing this benchmark directory. Keep outputs isolated. For the authored harness check, the coordinator-selected source revision was `ca0130b`; do not build ongoing feature edits into a measurement executable.

```sh
source tools/dev-env.sh
dotnet build /absolute/frozen-source/tools/benchmarks/IntegrityVerification/IntegrityVerification.csproj --artifacts-path "$PWD/.local/integrity-benchmark-frozen-build" -c Release -p:RestoreLockedMode=true -p:SourceRevisionId=FULL_FROZEN_COMMIT
```

The benchmark directory owns its package lock; there are no additional dependencies beyond the Infrastructure project graph. Pin `SourceRevisionId` explicitly: an archive inside another checkout can otherwise inherit its parent's changing Git revision in assembly informational metadata, despite unchanged frozen source files. Invoke from a checkout root containing `VibeMiner.slnx`. The small check writes a single1MiB+64KiB synthetic object and checks both strategies, digest/read/EOF equivalence, refusal of a canceled proof, actual file close/exclusive reopen and zero staging. It also checks canceled observation before stripe/timer and immediate pre-observer failure, preserving the original failure and awaiting actual operation retirement. It has no speed threshold and does not replace the production correctness suite.

```sh
dotnet .local/integrity-benchmark-frozen-build/bin/IntegrityVerification/release/VibeMiner.IntegrityVerification.Benchmark.dll check authored-check-1
```

Main mode requires the exact SHA-256 of the **loaded** `VibeMiner.Infrastructure.dll` beside the benchmark executable. It refuses a mismatch. The report records assembly path, SHA and MVID; the runner explicitly declares the frozen source revision using a process environment variable. Run each command in a separate process, rotate order over three repetitions and wait for other benchmark/test workloads to stop. Never describe generation/publication immediately before reading as cold filesystem I/O.

```sh
VIBEMINER_INTEGRITY_SOURCE_REVISION=ca0130b dotnet .local/integrity-benchmark-frozen-build/bin/IntegrityVerification/release/VibeMiner.IntegrityVerification.Benchmark.dll run legacy 4 legacy-4-r1 EXACT_LOADED_INFRASTRUCTURE_SHA256
VIBEMINER_INTEGRITY_SOURCE_REVISION=ca0130b dotnet .local/integrity-benchmark-frozen-build/bin/IntegrityVerification/release/VibeMiner.IntegrityVerification.Benchmark.dll run budgeted 4 budgeted-4-r1 EXACT_LOADED_INFRASTRUCTURE_SHA256
```

Repeat with64 and reverse strategy order for repetition2; use new names throughout. Reports are JSON on stdout and in each run's `result.json`; failures leave a `failure.json` and isolated scratch for inspection. Source/staging files and generated outputs are not repository fixtures or application admissions.

Four measured lanes have deliberately different scopes:

- **Actual CAS full open:** complete exact SHA/length proof, returned position zero and awaited returned-handle close. This is the primary throughput measurement. Source read counts/bytes are null because CAS owns an uninstrumented FileStream; do not infer physical I/O from length.
- **Instrumented core with foreground CPU:** the exact CAS hash delegate reads an asynchronous counting FileStream wrapper. It reports actual API read requests, bytes and EOF, same full SHA and instrumented throughput. A single foreground CPU consumer queues after a real source read reaches1MiB, using the same one-CPU resource gate. Request/entry/terminal timestamps yield admission latency and explicit overlap flags. Inspect those flags: scheduler timing can let the hash finish before the consumer requests or enters. Counting/sampling/probe overhead is included, not subtracted from actual-open throughput.
- **Instrumented core cancellation:** the read wrapper requests cancellation at1MiB, awaits hash termination and then actual `DisposeAsync`, then exclusively reopens the canonical file. No SHA proof is returned. Read counts measure input read before cancellation, not bytes admitted to SHA. Cancellation-to-terminal and cancellation-to-close are separate.
- **Actual CAS timed cancellation:** the actual pre-stripe arrival signal starts a1ms measurement timer. The report distinguishes canceled work from a verification that already won before cancellation, awaits terminal/returned-handle close and exclusively reopens the file. There is no claim that a timer interrupted a particular hash step. API-read counts remain null.

Every lane records process-wide allocated-byte delta, sampled working-set maximum/count, lifetime peak working set and managed heap after the phase. The bounded sampler retains only a maximum/count and runs at5ms; its allocations and scheduling are included. Lifetime peak includes generation/publication and earlier phases. These values are not an aggregate engine memory budget or a precise instantaneous RSS peak. Setup generation/publication timings, their SHA passes, known staging extent, final staging and retained canonical source bytes are separate fields.

No OS cache eviction, actual block-I/O counter, fsync/durability, foreground-feature workload, end-to-end engine latency or production multi-consumer memory bound is measured. Numeric results require an actual recorded run with runtime/revision/hardware/filesystem and concurrent-load conditions. See [benchmark caveats](../../../docs/TESTING.md).
