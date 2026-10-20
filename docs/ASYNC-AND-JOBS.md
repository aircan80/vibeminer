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
| CPU permits | 2 total, at most 1 background |
| Staging (temporary disk) | 2 GiB total, at most 512 MiB background |
| Shared physical producers | 128 total; at most 32 pending background; 32 admissions reserved for interactive work |
| Consumers per producer | 32 |
| Priorities | Only `Interactive` and `Background`. A null work context counts as `Interactive`. |
| Progress delivery | At most 10 updates/s per producer (100 ms); stage or unit changes pass immediately |
| Progress observer callbacks | 32 in flight, engine-wide |
| SQLite bulk batches | At most 64 rows/members per write batch (for example retention credit batches) |
| Database worker queue | One worker, at most 64 queued operations |
| Engine requests (host) | 32 active; the 33rd gets `BUSY` |
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

## Preparation and navigation

The preparation flow has an application-owned job. Back to builds detaches observation and leaves work running. Reopening the same exact context attaches to the same active job. A valid prepared context skips the preparation screen after revalidation.

One active preparation context is admitted at a time; another gets a clear busy/current-job result. Supporting several needs a separate measured resource decision. Explicit cancellation or switching waits for retirement before replacing owned resources.

Completion navigates only the currently attached matching request generation. Late success still keeps durable valid results for reuse. Errors and retries stay attached to the original exact context.

## Persistence and application exit

Persist Download all/On demand policy and enough exact scope to reconstruct work. Keep verified completed objects across cancellation and restart. Recover abandoned staging safely; unfinished parsing/index transactions are not row-resumable.

Normal exit shuts the engine down gracefully. Continued downloads after every window closes, tray residency, launch-at-login and service behavior are open product decisions. Background work while the app is running implies none of them.

## Required unit and integration cases

Test admission bounds, cancellation before start/during I/O/during parse/at commit, one terminal outcome, observed exceptions, stale-generation refusal, Back/reattach, independent shared consumers, foreground priority, bounded progress, restart reconstruction and shutdown cleanup. Use deterministic signals and controlled clocks, not timing-sensitive sleeps. See [TESTING.md](TESTING.md) for async test pitfalls.
