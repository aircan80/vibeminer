# Async execution and background jobs

Async-first is a project requirement. It means nonblocking I/O and bounded orchestration, with CPU and synchronous database work scheduled explicitly. An `Async` suffix or a `Task.Run` wrapper alone does not satisfy it.

## Execution rules

- Use `HttpClient.SendAsync` and asynchronous response streaming with cancellation, byte bounds, explicit timeouts, and response validation. Use asynchronous file I/O where the API supports it.
- No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` in production orchestration. Avoid `async void` except framework event handlers that handle failures.
- CPU-bound parsing stays synchronous inside bounded worker slices. Calling it from an async use case does not make it nonblocking. Check cancellation inside long loops and between chunks/records.
- SQLite calls may run synchronously even through async-shaped APIs. Run them on the bounded database worker, batch writes, and keep transactions short enough for interactive work. No task per inserted row.
- Bound active downloads, parser workers, queued jobs, database commands, IPC events, byte buffers, temporary disk reservations, and retries. Set budgets by measurement.
- Preserve exceptions and typed errors; background failures must be observed. Shutdown awaits actual retirement within a documented bound.

## Resource budgets

These are the engine's current bounds. Code is the source of truth: [AcquisitionResources.cs](../src/backend/VibeMiner.Infrastructure/Acquisition/AcquisitionResources.cs), [AcquisitionPorts.cs](../src/backend/VibeMiner.Application/AcquisitionPorts.cs), [AcquisitionCoordinator.cs](../src/backend/VibeMiner.Application/AcquisitionCoordinator.cs), [ProtocolHost.cs](../src/backend/VibeMiner.Host/ProtocolHost.cs) and [engine-client.ts](../apps/desktop/src/main/engine-client.ts).

| Resource | Bound |
| --- | --- |
| HTTP permits | 4 total, at most 2 background |
| WoWDBDefs definition fetches (D081) | Own HTTP client, not the CDN permits; 2 at once, one per table; 1 MiB file, 256 KiB commit answer, 30 s per fetch; a covering cache refreshes after 24 h, a non-covering one at most every 15 min; Tables and feature prerequisites (D101), one ask per table preparation; cancelled with the preparation that asked |
| Community listfile (D105) | Own HTTP client, not the CDN permits, at most 6 new connections per attempt; one background loop, one download at a time; 1 MiB release answer and 30 s for it, 512 MiB asset with 30 s idle per read, resumed by HTTP range; checked whole against GitHub's SHA-256 before it is read; parsed in 1 MiB pieces (8 million names, 1,024-byte names); about 36 KiB of index in memory; first download only when Files asks for names, Settings asks, or the switch is turned on (a real engine that opens Files does download; e2e runs point `VIBEMINER_LISTFILE_ORIGIN` at a dead loopback port); one Downloads & activity task while it runs or failed; then a check at most weekly, 15 min apart after a failure; the switch turned off cancels it and keeps its bytes; shutdown cancels it |
| Community build list (D106) | Own HTTP client, one connection, not the CDN permits; one GET at most a day, from Refresh builds only, alongside the product checks and never failing them; 30 s and 16 MiB for the answer; at most 50,000 rows read, 200,000 kept; cancelled with the refresh (the attempt still counts) |
| Check availability (D106) | One check at a time; HEADs through the shared transport and HTTP permits, 3 at once, 15 s each; two config GETs of at most 1 MiB; at most 8,192 archives; progress every 50 files; cancelled only by Stop check or shutdown, and a cancelled check saves nothing |
| CPU permits | 2 total, at most 1 background |
| Staging (temporary disk) | 2 GiB total, at most 512 MiB background |
| Shared physical producers | 128 total; at most 32 pending background; 32 admissions reserved for interactive work |
| Consumers per producer | 32 |
| Priorities | Only `Interactive` and `Background`. A null work context counts as `Interactive`. |
| Progress delivery | At most 10 updates/s per producer (100 ms); stage or unit changes pass immediately |
| Progress observer callbacks | 32 in flight, engine-wide |
| SQLite bulk batches | At most 64 rows/members per write batch (for example retention credit batches) |
| Database worker queue | One worker, at most 64 queued operations |
| ROOT scope worker | One build at a time after Open, at background priority; fixed-size arrays of 16 bytes per ROOT entry (at most 8,000,000) and per distinct file (refused above 6,000,000), 8 per uncovered file and 24 per EKey of multi-object files (at most 1,000,000): at most about 300 MiB, 94 MiB on retail 12.1.5; engine peak about 306 MB RSS there, see below; 3 attempts, 30 s apart |
| File catalog worker | One build at a time after Open, at background priority; shares one gate with the ROOT scope worker, so their arrays never coexist. Fixed-size arrays: 16 bytes per ROOT and install membership while ROOT is first read (refused above 8,000,000) and 4 per ROOT membership for the FileDataID statistics, then 16 + 16 + 8 + 8 + 4 per distinct content key and 24 per EKey of content with several EKeys (at most 1,000,000): about 150 MiB on retail 12.1.5; engine peak 495 to 523 MB RSS there, see below. Rows written 1,024 per catalog operation into the build's own catalog file (at most 4 GiB; three attached at once); 512 bytes per membership plus 256 MiB must be free; 3 attempts, 30 s apart |
| Stored coverage worker | One loop; one slice of 1024 keys for each of up to 64 ledgers per round, round robin; rounds start at most once a second; 8 ledgers held open (closed after 30 s idle, proofs kept); a ledger given up after 3 failed rounds in a row; 8192 applied changes pruned per step |
| Check of files not checked at Open | One engine-wide (`DOWNLOAD_CHECK_BUSY` otherwise); at most 1,000,000 files outside the manifest; receipt lookups 128 keys at a time; findings written 64 rows per catalog operation; one background CPU permit per merge slice of at most 4,096 members, released before receipts and hashing; hashing through the store's integrity admission at background priority; progress at most every 100 ms ([D093](DECISIONS.md)) |
| Files query | At most 4,096 memberships per file (refused at seal). `inventory.files` stops at the first file boundary after 65,536 membership rows, so it reads at most 90,112; a CKey prefix may match at most 16,384 memberships. `inventory.file` reads at most 4,096 + 101 rows ([D068](DECISIONS.md)) |
| Engine requests (host) | 32 active; the 33rd gets `BUSY` |
| Storage measurement | One data-folder walk at a time, 512 entries per batch between yields and cancellation checks, 32 levels, 4096 queued folders, 5-minute budget (`STORAGE_MEASURE_TIMEOUT`). No acquisition permits |
| Prepared data per build | `settings.storage.builds`: at most 256 opened builds, one catalog operation per build, cancellation checked between builds; the newest 16 file catalog generations and 4 files each per build. `settings.storage.free`: one at a time (a second waits, cancellably), one catalog operation, the newest 16 generations ([D085](DECISIONS.md)) |
| Appearance cleanup | Once after start; one catalog operation per 16 rows; one file delete at a time under its SHA stripe; no acquisition permits; a pass that kept files repeats after 30 s, 8 passes at most ([D103](DECISIONS.md)) |
| Desktop `EngineClient` | 32 pending total. Ordinary requests are capped at 26, so 6 slots always remain for `request.cancel`, `shutdown`, `items.icon.close` and `creatures.geometry.close`. |

A grant is never revoked. If an interactive request is demoted to background, its HTTP and CPU permits run to the end of their current bounded unit; the next request boundary applies the new priority.

### Staging is nonpreemptive debt

A demoted staging reservation is not reclaimed. It may finish as background even when background reservations then exceed 512 MiB. The 2 GiB hard total always holds. While background usage is over its quota, no new background reservation is granted, and waiting foreground reservations get released space first.

A request waiting for staging reports `Waiting for temporary storage`. The UI shows "storage waiting" with no percentage or ETA. This policy does not promise unconditional foreground disk headroom after a wave of demotions.

### Progress throttling

The coordinator drops intermediate updates that arrive within 100 ms of the previous one with the same stage and unit. A stage or unit transition is delivered immediately. Each consumer holds only its latest value; at most 32 observer callbacks run at once across the engine, and a slow observer never blocks the producer. Payload byte accounting happens before throttling, so dropped frames never lose accounting.

### Finite parser CPU units

CPU permits cover one bounded verification/decode invocation. The existing BLTE, BLP, PNG, EN and ROOT parser units are not cooperative: once admitted they can hold CPU across their own I/O until they finish. Pure WDC5 invocation and the in-place row sort are also single units. This is known scheduling debt. Do not claim per-chunk priority yielding or a latency guarantee for these paths.

## Lock order and deadlock rules

### CAS integrity verification

[VerifiedObjectStore](../src/backend/VibeMiner.Infrastructure/Preparation/VerifiedObjectStore.cs) hashes objects in this order:

1. Feature operation, then a reader/session gate if needed (for example `TableRowArtifacts`).
2. Integrity admission: 128 slots. It never waits; a full queue fails with `CONTENT_STORE_CAPACITY`.
3. One of 64 SHA stripes (picked from the first SHA byte; distinct digests can collide).
4. Read up to 64 KiB with no CPU permit held.
5. Take CPU only for each append and for finalization, then release it. Yield to the scheduler at least every 1 MiB, including after short reads.
6. Journal any corruption while still holding the stripe.
7. Release the stripe and return the verified file lease.

Never hold a CPU permit while acquiring a reader gate, CAS admission, SHA stripe, HTTP permit or SQL transaction. With only two CPU permits, two callers holding CPU while waiting on a stripe that a hasher needs will deadlock. End your CPU scope before calling CAS or a resource-aware row reader. Raising CPU capacity, adding a second pool or releasing the stripe between chunks is not a fix.

### Database worker

`DiscoveryCatalog` runs every SQLite operation on one worker through `Schedule`. Never call an async catalog API from inside a `Schedule` callback: it queues behind the callback that is waiting for it and deadlocks. Use the synchronous helpers inside one scheduled operation.

Journal callbacks and catalog work never recurse into CAS or take CPU while a SQL transaction is open.

### Refresh across products

`DiscoveryService.RefreshAllAsync` ([D062](DECISIONS.md)) runs the per-product refresh for every registered product with `Parallel.ForEachAsync`, at most `MaxProductsInFlight` (3) at once. Each product still fetches its versions/cdns pair through the one `OfficialDiscoverySource` (4 network slots, shared HTTP and CPU permits) and commits in its own transaction. A typed per-product failure is recorded and the loop continues; anything else fails the request. Progress reports are serialized, one per finished product. Cancel stops new products, cancels the running ones and returns only after they retired; committed products stay committed. The desktop allows this request 10 minutes instead of the usual 3, because 13 products with a 20-second document timeout can take longer.

### Single-owner gates

- **Bulk archive map.** One process-wide builder (`BulkArchiveLocator.Building`). A second build fails with `BULK_LOCATOR_BUSY`. This is intentional: it bounds native index I/O and working memory (4096 archives, 512 MiB aggregate native indexes, 10 million locations, 32 open files, 32 MiB working set).
- **Table data.** One table producer engine-wide. A different table request gets `TABLE_DATA_BUSY`. Callers wait on the availability signal (`WaitForAvailabilityAsync`), which observes producer retirement without becoming a consumer, and retry at most 8 times. No polling.
- **Reference text.** One `tables.referenceText` batch at a time, waiting cancellation-aware. Text tables use at most 4 dedicated row readers (least recently used is closed first); non-ID value indexes cover at most 4,096 rows and keep keys and ordinals only. Never hold CPU while calling a row reader.
- **Follow.** `tables.follow` shares that gate and those readers, so one local lookup runs at a time. A non-ID scan covers at most 4,096 rows and keeps values and ordinals only; pages hold at most 50 rows and 512 KiB. Cancellation is checked per row. The desktop cancels a lookup when its view is left, and only the newest reply for the entry on screen applies.

## Operation contract

A job has a stable ID, exact context, request generation, operation kind, current immutable progress, cooperative cancellation request, and exactly one terminal outcome. The public request/reply completes after ownership and resource cleanup, not when the UI stops listening.

| Lifecycle state | Meaning |
| --- | --- |
| Queued | Admitted and waiting for bounded capacity |
| Running | Producer owns active work |
| Waiting | Explicit dependency, offline condition, or retry wait |
| Stopping | Cancellation requested; work and leases have not yet retired |
| Succeeded | Required durable publication completed |
| Failed | Typed failure with stage and safe context |
| Canceled | Producer retired without success |

Cancellation is cooperative and idempotent. A completed durable commit may win a cancellation race; never report a committed success as rolled back. Pausing downloads is its own policy, not an alias for killing a thread.

## Progress

Progress carries job ID, context/generation, increasing sequence, stage, unit, completed amount when measured, optional trustworthy total, and safe diagnostics. Units include wire bytes, decoded bytes, pages, rows or requirements; never mix units within a fraction.

Coalesce intermediate updates so a slow subscriber cannot build an unbounded queue. Terminal outcomes stay reliable. Unknown totals stay indeterminate: no weighted overall percentage, guessed ETA, or SQLite-file-size proxy. Transfer at 100% can still leave verification, indexing and finalization outstanding.

## Shared acquisition and priority

Identical physical-source requests share one transfer once exact identity and verification policy are established. Consumers keep separate request IDs, priorities, cancellation and required outcomes. Canceling a view does not stop a transfer another view or a retention job still needs.

Users keep exploring while Download all runs. A screen's missing data and its prerequisite chain get foreground priority automatically. Check verified reusable data first, then attach to and promote matching queued or active work instead of starting another transfer.

Effective priority follows the highest-priority remaining consumer and its dependencies. Detaching a screen releases its demand without canceling surviving bulk consumers. Navigation does not cancel retained background work. On demand withdraws bulk intent and keeps independent foreground requests.

Reserve capacity for interactive work across network, buffers, CPU parsing/hashing, disk publication and database batches. Stop admitting bulk work when it blocks foreground service; yield at safe request/chunk boundaries rather than discard valid work to change priority. A reserved HTTP slot alone cannot prevent database or CPU starvation.

Background work uses what is left and picks up as interactive pressure falls. Don't promise instant interruption or fixed bandwidth. Keep per-screen progress separate from overall retained coverage.

### Priority acceptance scenarios

This is the priority contract. Each scenario needs deterministic scheduler tests and controlled HTTP/filesystem/SQLite integration, then a real Electron task during bulk work. No sleep-based scheduler tests and no arbitrary timing targets.

1. With bulk work saturating its allowed capacity, an interactive request starts without waiting for the complete build or an unbounded bulk queue.
2. An interactive request for an already queued bulk object promotes that one transfer. A matching active transfer stays one producer with independently observable consumers.
3. A table/model request's required metadata or texture dependencies inherit foreground priority and complete before unrelated bulk queue entries.
4. Canceling or detaching the screen leaves a surviving bulk consumer running and restores the right remaining priority. Switching to On demand withdraws bulk consumers without canceling surviving foreground work.
5. Navigating during background retention preserves the job, verified data and current context. Late results never update a different build, locale or view.
6. Controlled slow disk, parser and SQLite work show bounded foreground service across the whole path, not just HTTP queue ordering.
7. Recovery preserves committed objects and exact intent; unverified partial data is never published as ready. Resuming partial transfer offsets needs separately tested checkpoint/range semantics.

## Archive proof cache

[ArchiveProofCache](../src/backend/VibeMiner.Infrastructure/Acquisition/ArchiveProofCache.cs) shares authenticated archive footer/TOC/page proofs and in-flight requests.

- 32 MiB and 4096 entries total; 32 pending loads, of which at most 24 background.
- Only `CONTENT_SOURCE_MISSING` is negative-cached, for 30 s. Other failures are not cached.
- Capacity stays charged until both the producer and its cancellation notification have retired. An expired negative entry can be removed only after both retire.
- It also holds the parsed archive lists of earlier CDN configurations that a lookup fell back to ([D069](DECISIONS.md)), so concurrent fallbacks share one fetch at the highest remaining priority.

## Shared asset contexts

[AssetResolutionRegistry](../src/backend/VibeMiner.Infrastructure/Content/AssetResolutionRegistry.cs) and [AssetResolutionSession](../src/backend/VibeMiner.Infrastructure/Content/AssetResolutionSession.cs) share exact-source EN/ROOT resolution between icon and model features.

| Bound | Value |
| --- | --- |
| Source contexts (opening, held or draining) | 2 |
| Feature handles (opening, open or draining) | 8 |
| Batch subscriptions (including stopping) | 8 |
| FileDataIDs per batch | 128 |
| Resolution cache per context | 1024 IDs, 32,768 memberships, 16 MiB |

Each context owns at most one EN lookup lease and uses the global staging budget; context count is not a staging-byte budget. Failed cleanup keeps its admission charged.

| Code | Meaning |
| --- | --- |
| `ASSET_CONTEXT_CAPACITY` | Context slots full, or this exact context is still retiring (retryable) |
| `ASSET_HANDLE_CAPACITY` | Feature handle slots full (retryable) |
| `ASSET_BATCH_CAPACITY` | Batch subscription slots full (retryable) |
| `ASSET_BATCH_LIMIT` | More than 128 FileDataIDs in one batch |
| `ASSET_INVALID` | Zero FileDataID |
| `ASSET_LOCALE_UNSUPPORTED` | Locale other than enUS |
| `ASSET_SOURCE_CHANGED` | Captured source changed during preparation; retry with the current context |
| `ASSET_SOURCE_LIMIT` | Decoded ROOT exceeds the resolver artifact limit |
| `ASSET_SOURCE_CLEANUP` | Staging or source cleanup could not prove retirement |
| `ASSET_SESSION_TIMEOUT`, `ASSET_RESOLUTION_TIMEOUT` | Consumer time budget exceeded |

## Background retention worker

[BuildRetentionDriver](../src/backend/VibeMiner.Infrastructure/Retention/BuildRetentionDriver.cs) is the single background loop for Download all.

**Members.** A retryable member failure gets 3 attempts with 1 s and 4 s delays. Per-pass problem details stop at 10,000 (`RETENTION_PROBLEM_LIMIT`). A member with encoded length below 9 or above 512 MiB (536,870,912 bytes), including 0, is a nonretryable `RETENTION_OBJECT_UNSUPPORTED` problem before any HTTP; supported peers still run.

**Storage faults.** A catalog exception must not kill the loop or drop later Resume requests:

- Retry catalog work 3 times (1 s, 4 s), then park as `RETENTION_STATE_RETRY` until a committed policy command or shutdown wakes it.
- Keep the cleaned-up pending settlement in memory. Never release or reuse the job slot before durable settlement is confirmed.
- A claim can commit and then throw before returning. Mark it uncertain and reconcile (bounded local restore) before claiming again.
- A monotonic notification version, checked before and after draining the wake channel and after publishing parked status, prevents a Resume landing at that boundary from being lost.
- The parked overlay changes only reported state/progress/problem. It never changes durable mode, pause, intent revision, scope, coverage or evidence.
- A generic retry never sends Resume. A paused user retries with Pause, an On demand user with On demand. Resume stays the explicit unpause.
- Shutdown uses the same 3-attempt policy, admits no new claims, and returns a typed recoverable failure if settlement still fails. A later shutdown call retries settlement only.

## Stored coverage worker

[StoredCoverageWorker](../src/backend/VibeMiner.Infrastructure/Retention/StoredCoverageWorker.cs) keeps the running Downloaded figure of every admitted ledger ([D065](DECISIONS.md), [storage](STORAGE.md#stored-figure-per-ledger)). It never transfers, stats files or runs a presence check. Catalog commits that change what is stored wake it through a one-slot signal. Each round takes one bounded slice for each of up to 64 ledgers with work, continuing after the last ledger of the previous round, so every ledger is reached and the slowest one cannot pin pruning. Each slice is two short operations on the shared catalog worker, so interactive SQL interleaves, and rounds start at most once a second even while work remains. Ledger reads use binary searches of 24-byte records outside the stream buffer. The first open of a ledger proves it under background CPU permits; later opens, including after the 30 s idle close or with more than eight ledgers in turn, only check its header while the CAS file keeps the proven length, creation and write times.

- **First count.** Admission registers the ledger as `counting`. Retained EKeys are read 1024 at a time in key order and matched against an ordered ledger page cache; the cursor is durable, so a restart resumes it.
- **Changes.** Up to 1024 logged changes per round, applied by exact key lookup. Each slice is fenced by the ledger's expected cursor, so a reset or failure in between writes nothing.
- **Faults.** A fault stays with its ledger. One that is missing or fails verification, has an inconsistent saved row, or fails three rounds in a row becomes `unavailable` (`STORED_COVERAGE_FAILED` for the last) and is no longer maintained or held back from pruning; the next admission of that ledger counts it again. Only a catalog that cannot be used at all (busy, full, failing) stops the round; the loop then retries after 1 s and 4 s and waits for the next change. The loop never ends on a fault.
- **Shutdown.** Disposal cancels the loop, awaits the running operation and closes the held ledgers.
- **Change waits.** Every logged stored change and every ledger admission also moves the in-memory summary revision and completes the one shared signal that `download.changes` waits on ([D077](DECISIONS.md), [contract](../contracts/download-v1.md#change-waits)). A wait holds no catalog operation, answers within 25 s, and is cancellable; the desktop keeps at most one open, and only while no figure is moving.

## ROOT scope worker

[RootScopeWorker](../src/backend/VibeMiner.Infrastructure/Retention/RootScopeWorker.cs) measures every file a build's ROOT lists outside its download manifest after Open ([D066](DECISIONS.md)), so Open stays as fast as before. It takes the oldest pending build, reads its configurations, ENCODING and ROOT through the shared acquisition pipeline at background priority, and does its one ROOT and one ENCODING pass under a background CPU permit. Every CAS proof (the manifest ledger session, the decoded ENCODING) is opened before that permit is taken. Both passes stream: neither ROOT nor ENCODING is held in memory, and the manifest is merged as 24-byte values rather than rows. It then publishes the manifest ledger extended with the uncovered files' encoded objects and records the scope in one fenced transaction.

- **Memory.** On 12.1.5.70448 the engine is at 140 MB RSS when Open returns and peaks at about 306 MB during the measurement. About 100 MiB of that is the idle runtime, 94 MiB the two key arrays (3,261,808 ROOT entries and 2,881,684 distinct files), and the rest short-lived per-entry key strings from the ROOT and ENCODING readers, which the GC reclaims.
- **Outcome.** `measured`; `manifestOnly` when the file list cannot be read (terminal, with the reason); otherwise, for a fault of any kind, a retry 30 s later, and `failed` after three attempts, which the next Open of that build resets to pending. A pending row whose saved inputs cannot be read is settled `failed` at once. Reopening a measured build resets an unavailable figure, and sets the scope pending again only when the measured ledger itself could not be read; a running Download all keeps the ledger it was claimed with.
- **Cancellation and shutdown.** The measurement stays pending; the next run resumes it from the start. Nothing partial is recorded.
- **Stale results.** A result commits only while its row is still pending with the same generation and attempt count.
- **Download all.** A queued job is not claimed while its build is pending; the retention driver is woken on every terminal outcome.
- **Gate.** Its key arrays are built under the gate it shares with the [file catalog worker](#file-catalog-worker); when both wait, the measurement goes first.

## Check of files not checked at Open

`download.check` ([D093](DECISIONS.md), [contract](../contracts/download-v1.md#check)) verifies the files a measured ledger adds to its manifest, which Open's assessment never covered. It is one request, not an engine background job: the desktop owns it above the build's views (like table and Items loading), shows it as a Downloads & activity task, and keeps it when Files or the build is left.

- **Work.** It proves both ledgers (background CAS and row proofs, before any CPU permit), then merges them in key order under one background CPU permit per slice. For each file outside the manifest it looks up receipts of every kind 128 at a time and opens the stored object through `VerifiedObjectStore` at background priority, so interactive reads and hashing come first. A file with no receipt is not stored here; a known or newly found corruption is corrupt (the store journals new corruption itself); a receipt whose file is gone is missing. It downloads nothing and records no absence, so the stored figure only changes through the corruption the store journals.
- **Priority.** Background CPU (one of two permits at most) and the integrity admission the store shares with everything else. Page reads, Tables and Items stay interactive meanwhile.
- **Outcome.** One reply with the counts, after one transaction makes its generation the completed one; the counts are saved with it and come back with every page. Progress counts files checked of the files outside the manifest. The desktop's deadline is 10 minutes without progress, restarted by each frame.
- **Cancellation and shutdown.** `request.cancel` (Stop in its note or in Downloads & activity) or shutdown stops it between files; one that lands after the last file still records nothing. Leaving Files or the build doesn't stop it: the desktop owns it above the build's views, as a Downloads & activity task. Its staging rows are removed (or by the next Check, after a crash); the completed Check stays as it was. A page never reads a staging generation.
- **Stale results.** Writes are fenced by the staging generation; a Check replaced by a later one writes nothing.

## File catalog worker

[FileCatalogWorker](../src/backend/VibeMiner.Infrastructure/Inventory/FileCatalogWorker.cs) prepares the ROOT and install memberships Files lists, after Open ([D071](DECISIONS.md), [storage](STORAGE.md#file-catalogs-after-open)), so Open stays as fast as before. It takes the pending build opened most recently and reads its configurations, ENCODING, install and ROOT through the shared acquisition pipeline at background priority, then:

1. **Reading.** Under one background CPU permit, after every CAS proof is opened: ROOT once for its content keys, FileDataID statistics and variant counts; install's few entries are already in memory; ENCODING once, merged in key order, for each content's sized EKeys, encoded and decoded size. Contents with several sized EKeys then prefer a stored one (one bounded stored-rule check per 256 keys). The encoding lookup is released afterwards.
2. **Writing.** ROOT is read a second time and every entry becomes one resolved row, then the install rows. Rows are derived under a background CPU permit that is released around every catalog operation; each operation writes one batch of 1,024 rows into the build's own catalog file with its stored marks and position, so interactive SQL interleaves between batches.
3. **Sealing.** Two short transactions, file first: the file records its planner statistics and is marked sealed, then the main catalog makes the generation the build's ready file catalog. A crash between them is sealed again by the next run; Open re-queues a ready build whose file is not sealed.

- **Gate.** Reading and writing hold the [BackgroundMetadataGate](../src/backend/VibeMiner.Infrastructure/Retention/BackgroundMetadataGate.cs) the ROOT scope worker takes for its key arrays, so the two never hold build-sized arrays together. A waiting ROOT scope measurement goes first, because Download all waits for it. The status says `waiting` meanwhile and names that build.
- **Order.** The most recently opened pending build goes first. Each stage change and each batch checks the turn: a newer Open of another build makes this one yield (it keeps its rows and goes back to `queued`, naming the build it waits for); a paused Download all of this build pauses it (`paused`), before it downloads anything and between batches. Pausing, resuming or switching a Download all wakes the worker.
- **Free.** `settings.storage.free` marks the row `removed`, cancels a running preparation the same way as Stop and deletes its files in the same catalog operation, so no later batch can attach them again; that preparation's late writes are fenced out by the new generation. It then wakes the worker, whose cleanup takes what it left; a file cleanup can't delete is skipped, not a fault, and retried after the retry delay or at the next wake-up ([D085](DECISIONS.md), [storage](STORAGE.md#file-catalogs-after-open)).
- **Stop and Resume.** `inventory.catalog.set` stop marks the row `stopped` and cancels a running preparation at once through its own token; its rows stay. Resume, Retry of a failed one, or the next Open queue it again first in line and it continues after its rows; a removed one starts from the beginning. Downloads & activity lists every pending, stopped or failed preparation (`inventory.catalogs`, at most 32) as one task per build.
- **Memory and time.** On 12.1.5.70448 the engine is at 151 MB RSS when Open returns and peaks at about 495 MB while writing: about 100 MiB of idle runtime, about 150 MiB of index arrays (3,262,082 memberships, 2,881,834 distinct contents), the attached file's 16 MiB page cache, and the rest per-entry strings and per-block ROOT buffers the GC reclaims later. Right after a ROOT scope measurement it reached 518 to 523 MB: the measurement's arrays were released but not yet collected. The catalog was ready 61 s after Open (47 s of it writing 3.26 M rows into a 1.15 GB file); `discovery.read` meanwhile took at most 3 ms.
- **Outcome.** `ready`; `unsupported` when the file list cannot be read (format, size, or a bound such as 4,096 memberships per file), with the reason and that retrying won't help; `failed` at once for `STORAGE_FULL`; otherwise, for a fault of any kind, a retry 30 s later (sooner when another build is opened or resumed) and `failed` after three attempts. The worker wakes through a one-slot signal; a retry wait cancels and awaits whichever of its delay and its wake-up lost, so no leftover wait takes the wake-up of a later Open or Retry. The typed code, message and underlying SQLite or I/O cause are kept on the problem and written to the engine log (standard error). The next Open queues a failed, unsupported or stopped one again.
- **Cancellation, shutdown and restart.** Shutdown leaves the preparation pending with its written rows. The next run derives the rows again from the same verified sources, checks the saved ones against them and continues after them; a difference, or a saved file or registry row that fails its check, abandons the generation and starts again, and cleanup deletes the abandoned file. A stage left by a process that ended is reset when the next run starts.
- **Stale results.** Every write commits only while the row is still pending with the same generation and attempt count; a reset or stopped preparation's late batch or seal writes nothing.

## Appearance cleanup

[GeometryAppearanceCleanup](../src/backend/VibeMiner.Infrastructure/CreatureGeometry/GeometryAppearanceCleanup.cs) removes superseded Creature appearances and the VMA1 files no remaining row names, once after start ([D103](DECISIONS.md), [storage](STORAGE.md#superseded-creature-appearances)). It takes no download, CPU or staging permits: every step is one short operation on the shared catalog worker (at most 16 rows) or one file delete under the store's SHA stripe, so interactive work interleaves.

- **Uses.** [GeometryAppearanceFileUses](../src/backend/VibeMiner.Infrastructure/CreatureGeometry/GeometryAppearanceFileUses.cs) counts the open appearance streams and the publications of each VMA1 file. Cleanup starts a delete only when the file has none; a use that starts during the delete waits for it (cancellably), then opens or publishes as usual. A Read that finds the file gone reports `notPrepared`; a publication writes it again.
- **Retries.** A pass that kept a file (in use or not deletable) runs again after 30 s, at most 8 passes in all; a catalog or store fault is logged and retried the same way. The loop never escapes a fault. The next start tries again.
- **Shutdown.** Disposal cancels the loop and awaits it; a delete already started finishes its file and its row on the next run.

## Preparation and navigation

The preparation flow has an application-owned job. Back to builds detaches observation and leaves work running. Reopening the same exact context attaches to the same active job. A valid prepared context skips the preparation screen after revalidation.

One active preparation context is admitted at a time; another gets a clear busy/current-job result. Supporting several needs a separate measured resource decision. Explicit cancellation or switching waits for retirement before replacing owned resources.

Completion navigates only the currently attached matching request generation. Late success still keeps durable valid results for reuse. Errors and retries stay attached to the original exact context.

## Persistence and application exit

Persist Download all/On demand policy and enough exact scope to reconstruct work. Keep verified completed objects across cancellation and restart. Recover abandoned staging safely; unfinished parsing/index transactions are not row-resumable.

Normal exit shuts the engine down gracefully. Continued downloads after every window closes, tray residency, launch-at-login and service behavior are open product decisions. Background work while the app is running implies none of them.

## Required unit and integration cases

Test admission bounds, cancellation before start/during I/O/during parse/at commit, one terminal outcome, observed exceptions, stale-generation refusal, Back/reattach, independent shared consumers, foreground priority, bounded progress, restart reconstruction and shutdown cleanup. Use deterministic signals and controlled clocks, not timing-sensitive sleeps. See [TESTING.md](TESTING.md) for async test pitfalls.
