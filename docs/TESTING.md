# Unit testing and verification

Unit testing is a core requirement. New testable behavior gets meaningful tests, and testable defects get regression tests. Use test-first development where practical, especially for parsers, identities, state transitions and cancellation races. A passing Java suite is reference material, not evidence that the C# implementation passes.

The solution uses xUnit for backend behavior, Vitest/Testing Library for desktop behavior, Node/Ajv for cross-process schema checks and Playwright for real Electron checks. Use the actual commands in [BUILDING.md](../BUILDING.md). Documentation and copied-design validation do not count as passing application tests.

## Unit test boundaries

Keep domain, application policies, pure parsers and protocol translation directly testable without Electron, network, user files or a real database. Use explicit clocks, controlled asynchronous gates and authored fixtures. Prefer observable results and invariants over mocks of every internal call.

| Area | Important unit cases |
| --- | --- |
| Identity and scope | Same version/different configs, edition separation, locale, region grouping without lost receipts |
| Parsers | Valid records; every meaningful truncation; overflow; malformed framing; exact EOF; unsupported/encrypted modes; allocation bounds; cancellation |
| Acquisition policy | Candidate/source selection, bounded retries, verification requirements, shared-consumer cancellation |
| Jobs | Admission bounds, one terminal result, progress sequence/units, stale generations, cancellation/commit races, Back/reattach |
| Readiness | Unknown denominator, incomplete metadata, preparing inventory, missing/corrupt content, retained versus readable |
| Queries/comparison | Stable pagination, exact integer values, incomplete coverage, explicit relationship target columns, absent versus unknown |
| Assets | Missing texture/skin dependencies, unsupported materials, deterministic portrait identity, invalid dimensions and counts |
| IPC | Version mismatch, malformed/oversized messages, duplicate/stale responses, typed errors, log/protocol separation |

Port Java fixtures and acceptance expectations selectively. Preserve original authorship and licensing when transferring tests; record the source path/revision. Don't copy tests that only mirror an abandoned Java implementation detail. Coverage percentages can reveal gaps but don't replace behavioral evidence; no getter tests or boilerplate to inflate counts.

## Async tests

Tests must await real completion and observe failures. Avoid `.Result`, `.Wait()`, arbitrary sleeps, timing-sensitive assertions and live services. Bounded timeouts are deadlock guards, not performance claims.

Exercise cancellation before admission, during transport, inside parse loops, during database publication and after a durable commit. Test that one subscriber's cancellation leaves another shared consumer running. Completion must wait for the producer's actual cleanup. Test offline and retry waits with controlled clocks.

Priority tests cover saturated bulk capacity, promotion of queued/in-flight shared work without duplicate transfer, inherited dependency priority, priority release when a screen detaches and surviving consumers when bulk mode changes. Controlled slow parser/disk/database work must show foreground service beyond HTTP queue ordering. See [the priority scenarios](ASYNC-AND-JOBS.md#priority-acceptance-scenarios).

### Async test pitfalls

These caused real flaky tests:

- **Registration disposed before the callback runs.** A `using` cancellation registration can be disposed when the canceled producer finishes, before the callback you meant to observe ever runs. Have the callback signal that it ran, and keep the registration alive with an independent gate until you've seen that signal.
- **Drain covers only posted continuations.** A pump's `Drain` runs what is queued now. A success continuation that hasn't been posted yet can still be canceled by a following `Dispose`. Await the actual terminal tasks (for example the producer tasks themselves) before disposing.
- **Best-effort progress drops frames.** Coalesced progress can skip an intermediate report. To assert a specific frame, hold the producer on a test-owned gate after `Report` until the test has received and checked that frame, then release it.
- **Time-sliced batches move progress counts.** Ordered inventory matching ends a batch after 40 ms, so under load its "Resolving" frame can read 16,414 instead of 16,384. Don't match a literal count. Cancel at the first frame past the threshold, read its count back and assert the committed rows equal it.
- **Advance a manual clock only after the timer is armed.** A throttled pump reads the elapsed time and then creates its delay timer. A callback signal fires before both. If the test advances in between, the timer is armed relative to the new time and never fires. Have the test clock signal when the timer is armed (see `ManualTimerClock.Armed` or `RetentionDriverTests.NextTimer`) and wait for that before calling `Advance`.
- **Don't count on a spare thread-pool thread.** If a test needs a thread blocked on purpose (an observer that holds a lock or waits synchronously), use a dedicated `Thread`. The pool may not inject another worker in time.
- **Races show up only in full cohorts.** A focused filter can pass while the full suite fails. Run the full backend suite with `--blame-hang-timeout 30s`, and loop suspicious tests repeatedly before calling a race fixed.
- **Process-wide gates leak across classes.** `RetentionDriverTests`, `BulkArchiveLocatorTests` and `SourceIntegrityOrderingTests` share the `Bulk archive locator` xUnit collection because the bulk map builder is one process-wide gate. Any new test that builds a bulk archive map must join that collection.
- **Heap samples are process-wide.** `GC.GetTotalMemory` counts every test running in the process. A delta between two samples once read -43,895,784 bytes because a parallel test freed its objects in between. Tests that sample it must sit in a `DisableParallelization` collection, usually `Managed heap budget`. `ManagedHeapBudgetTests` fails if one doesn't. `GC.GetTotalAllocatedBytes` is process-wide too; use it for logged numbers only, or isolate the test.

### Float32 over the wire

To test a value that crosses from C# to JavaScript as a Float32, run the real Host route and parse its actual NDJSON frame in Node. For each value assert `typeof value === 'number'`, `value === Math.fround(value)`, equality with `Math.fround(expected)`, and identical Float32 bits through a `DataView`. Include the negative control `0.1 !== Math.fround(0.1)` so the check can tell the shortest-Single spelling apart from the exact widened value. Serializing a standalone DTO is not enough.

## Integration and architecture tests

Use controlled local HTTP, temporary app-owned files/SQLite databases and the real adapters to prove streaming, Range response validation, checksums, byte bounds, source receipts, shared transfers, migrations, staged publication, corruption and restart recovery.

Exercise actual engine process launch and framed messages, stderr logging, unexpected exit, canceled requests and shutdown. Enforce project boundaries and the ban on game parsing/download dependencies through project/dependency checks.

Simulated I/O failures, process termination and physical full-volume/power-loss behavior establish different things. State which was actually exercised. Normal CI uses authored synthetic data with no external CDN dependency.

### Environment variables

| Variable | Used by | Effect |
| --- | --- | --- |
| `VIBEMINER_DATA_DIR` | Desktop main | Engine data directory instead of the app profile's `engine-v1`. Every test and QA run sets it. |
| `VIBEMINER_ENGINE_PATH` | Desktop main | Engine binary or DLL to launch |
| `VIBEMINER_DOTNET` | Desktop main, contract tests, `tools/dotnet.mjs` | `dotnet` executable to use |
| `VIBEMINER_CONTRACT_HOST_ASSEMBLY` | `tests/contracts/engine-contract.test.mjs` | Host DLL for the actual-Host contract check. Set it when testing an isolated build, or the check runs against (or refuses) the normal output. |
| `VIBEMINER_REPO_ROOT` | `tests/backend` (`RepositoryPaths`) | Fallback source tree for authored files such as `contracts/examples/`. Backend tests normally use the root embedded at build time, so only set it if that tree has moved or gone. |
| `VIBEMINER_AUTHORED_FIXTURES` | Desktop main, Electron tests | `1` makes the UI label its data "Authored test data" when running against the authored backend fixture |

### Offline checks

Setting `HTTP_PROXY`/`HTTPS_PROXY` to `http://127.0.0.1:9` makes HTTP fail fast and exercises retained/offline paths. It is not OS-level network denial; say which one you ran.

## Qualifying a commit

When a result must be attributable to an exact commit:

- Test an archive of that commit (`git archive <sha>`), not the working tree.
- Build with a separate `--artifacts-path` and `-p:RestoreLockedMode=true`, with a private `TMPDIR`.
  Backend tests read authored files from the source tree they were built from (embedded as assembly metadata), not from a walk up from the test binary, so the artifacts path can sit anywhere.
- Never write to normal build outputs or the owner's app profile. Use `VIBEMINER_DATA_DIR` and point `VIBEMINER_CONTRACT_HOST_ASSEMBLY`/`VIBEMINER_ENGINE_PATH` at the isolated build.
- Report each run's own pass/fail counts. Never add up overlapping cohorts into a total.
- A failed broad run stays failed until the same broad run is repeated and passes. A focused rerun of the failing test doesn't turn it into a pass.

## UI and package checks

Use component tests for view state and interaction; use real Electron checks for focus, keyboard, navigation, stale results and responsive progress. Compare screenshots with the guided discovery reference at 1280×800 and 1440×1100, at 100% and 200% text. Verify offline, empty, loading, failure/retry, unsupported and canceled states.

Electron tests reach tasks through the persistent header activity trigger (`.download-activity-trigger`) and its panel; see `tests/desktop/activity-helpers.ts`. Don't add per-screen progress selectors that the unified activity replaced.

Native packages must launch the bundled C# engine and UI on each advertised OS/architecture. Verify paths with spaces, data locations, offline reopen, shutdown, version agreement and applicable notices. Signing/notarization and assistive-technology testing need their own actual results.

Specialist review roles (WoW datamining, UI, usability, QA, reviewer) are defined in `.agents/rules/`. Expert review is not testing with real users or assistive technology.

## Performance evidence

Keep timing benchmarks separate from deterministic correctness tests. Measure large EN indexing, DB2 decoding, query latency under bulk downloads, comparison and model/portrait preparation. Record fixture shape, runtime, hardware, filesystem, cache state, elapsed time, peak memory, temporary disk, database size and cancellation observations.

Read benchmark numbers carefully:

- Sampling working set every 5 ms only keeps a maximum and can miss short peaks.
- Reservation ledgers (staging, geometry payload) are accounting ceilings, not process RSS.
- Warm-cache results say nothing about cold-cache or disk-bound behavior.
- A 1 ms timer request is not 1 ms of elapsed time.
- Request-to-admission timing for one idle competitor is not end-to-end feature latency, and a busy workstation adds unrelated scheduling load.

The Java synthetic million-insert workload is a useful baseline shape. Don't assume a language speedup, relax verification/durability to hit a target, or assert stable milliseconds in ordinary unit tests.
