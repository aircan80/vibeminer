---
paths:
  - "src/**"
  - "tests/backend/**"
  - "tools/benchmarks/**"
---
# C# engine rules

- Write our own acquisition, TACT/BLTE/DB2 parsing and WoW asset decoding.
  - TACTSharp, DBCD, TACT.Net, CascLib and similar projects are reference material only. Record the revision you studied in docs and don't copy their code.
  - Standard .NET HTTP, streams, cryptography, compression, JSON and threading APIs are allowed.
  - SQLite bindings must never become a game-download or parsing path.
- Async I/O end to end.
  - Never use `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` in production orchestration.
  - Avoid `async void`, except framework event handlers that observe failures.
  - `Task.Run` is not async I/O and not an unlimited scheduler.
- Bound everything: CPU workers, downloads, database work, queues, progress messages, temporary storage, decoded allocations. Budgets are in [ASYNC-AND-JOBS](../../docs/ASYNC-AND-JOBS.md).
- Domain and Application code never depends on Electron, SQL drivers, concrete HTTP or filesystem adapters. Pure parsers never fetch, open arbitrary paths, persist rows or schedule jobs.
- Every long-running operation defines cancellation, progress, a terminal outcome, shutdown, and what happens to stale results.
  - Navigation and cancellation are separate actions.
  - Shared consumers are preserved.
- Interactive work has priority through the one shared acquisition pipeline. Promote existing transfers rather than starting a second downloader.
- Preserve exact product/build/configuration/locale/scope identity and the original source receipts. Missing, unknown, unsupported, encrypted, corrupt, partial and absent are different states.
- Verify bytes before publishing them or marking anything ready.
  - Staging rows are never queryable as ready.
  - A database commit and a filesystem move are never one atomic transaction.
- C# owns all catalog migrations and object publication.
- Use official CDN sources and app-owned storage only. Never read game installs, Battle.net directories or game caches.
- New behaviour gets a meaningful test, and every fix gets a regression test. Async test pitfalls are in [TESTING](../../docs/TESTING.md).
