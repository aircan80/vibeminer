# Size-first preparation and demand-driven data storage

This is the design rationale behind opening a build quickly and fetching research data on demand (decision D033). [STORAGE.md](STORAGE.md) and [ARCHITECTURE.md](ARCHITECTURE.md) describe the current implementation.

## Approach

Use a hybrid: **verified immutable files and native/compact indexes for large source metadata; SQLite for mutable durable state and useful research queries; bounded memory caches for active lookups.** Separate build identity, published download size, resolver readiness, file-catalog coverage and feature readiness. A fully expanded research catalog is not a prerequisite for showing a supported download-manifest size or requesting a few tables.

The big win is removing work from the opening path. Changing storage while still expanding and cross-resolving every EN/ROOT/IN/DL/DS record would keep most of the delay: on the old full path, DL/DS matching plus SQL publication alone took about two minutes, before indexing, integrity and sealing.

## Data ownership

| Data | Storage | Reason |
| --- | --- | --- |
| Original metadata and downloaded game objects | App-owned immutable object store, shared across builds | Preserve original bytes/proofs and keep identical physical content once |
| EN resolver metadata | Verified seekable decoded artifact and its native page directories | Target CKey/EKey lookups without expanding the whole source into relational rows |
| ROOT lookup support | Retained verified source; targeted scans, then a small block/FDID index if repeated use justifies it | Resolve requested IDs and all relevant variants; avoid pre-resolving every file |
| Published download identities/sizes | Immutable compact sorted full-EKey/size ledger plus a small source-bound summary | Deduplicate and total once; support reuse matching and membership checks without per-origin SQL expansion |
| CDN locations | Verified locator metadata and indexes, acquired when requested objects need them | Don't scan every archive before a feature needs a few files |
| Discovery history, source receipts, builds and artifact registrations | SQLite | Transactional relationships, provenance, migration and queries |
| Object completion receipts, jobs, consumers, priorities, retention intent and checkpoints | SQLite | Small incremental updates and reliable restart; no custom mutable journal |
| Decoded Items tables and other research views | Feature-specific artifacts and/or SQLite tables | Build queryable data when the feature needs it; exact schema/source identity stays attached |
| Hot resolver pages and active availability summaries | Bounded disposable memory cache | Fast repeated access without loading every build into RAM |

This is not a replacement database engine. Immutable indexes are reconstructible artifacts with narrow formats. SQLite keeps the changing state that would otherwise need our own transactions, journaling and migrations. New hot-path representations use compact binary keys, not repeated hex strings.

Native EN lookup needs a retained verified decoded artifact with bounded random-access reads and page caching. Native EN directories do not by themselves permit trustworthy arbitrary CDN ranges. ROOT is structured differently: block-local delta-coded IDs and multiple variants rule out an assumed global binary search.

## What opening a build does

1. Verify the exact saved build/source and its small configurations.
2. Acquire or reuse the supported size metadata directly when its exact keys and lengths are declared. EN is conditional bootstrap work when those facts need resolution; ROOT and IN are not size prerequisites.
3. Fully verify and parse the admitted download manifest. Create only the compact deduplicated full-EKey/encoded-size ledger and scoped summary that capability needs.
4. Show Home with independent size, resolver and catalog statuses. Research metadata acquisition/indexing runs on demand or as explicit background work. A research failure never erases an independently valid size result.

The metric is **Published download content · all variants**: the complete supported DL manifest, counting each full encoded identity once and rejecting conflicting sizes. It is not installed size, HTTP transfer overhead, or a claim that every ROOT/IN/DS reference belongs to that set. Unresolved ROOT entries therefore do not invalidate an independently established DL total; see [the Classic beta ROOT counterexample](research/root-unresolved-classic-beta.md).

### DS prefix semantics

DS can provide a separately named publisher-declared/verified row total. DS serializes only nine-byte EKey prefixes. If every prefix in a complete scan is distinct, that scan also gives a unique DS-scoped size without knowing suffixes or consulting EN. If any prefix repeats, that unique total is not valid without full-key resolution. Exact cache reuse and DL/DS overlap always need admitted full-key identity. Never add DL and DS totals, assume they are equal, pad prefixes, or relabel a narrower total as complete all-manifest coverage. If a supported source is absent or unavailable, its metric is unknown, not zero.

The small configuration's `download-size` fields describe the DL metadata object's own lengths, not the game payload total. Reading and authenticating the actual manifest is still real work; dropping SQL expansion does not make a first metadata download instant.

## How a feature screen gets its data

A feature declares its admitted table dependencies and exact build/variant/schema context. The engine resolves the requested FileDataIDs through ROOT, then the selected CKeys through EN, locates the exact encoded objects, reuses or downloads them, verifies them and runs our own BLTE/DB2 readers. Relationship tables, icons and assets are requested when the feature actually needs them.

All work goes through one shared acquisition service. The request and its metadata/table dependency chain inherit foreground priority across HTTP, parsing, hashing, disk and database. Matching queued or active bulk work is promoted, not duplicated. Navigation releases that view's consumer; surviving background consumers keep their work. Unsupported, missing, encrypted and corrupt dependencies stay explicit outcomes.

A first ROOT lookup may still scan the complete verified metadata to collect all matching variants or prove absence. The benefit is avoiding millions of unrelated SQL writes and global CKey resolution. Cache selected results and add a compact FDID/variant index only when repeated lookups justify it. A broad Files search or all-build comparison may still deserve a complete query index as its own resumable capability.

## Download completions and shared storage

Publish verified object bytes first, then commit the exact object alias/receipt and durable completion/change sequence in SQLite. A file move and SQL commit stay separate recoverable actions. The immutable build ledger is not rewritten per completion.

Active jobs already know their requested objects. Update or invalidate their bounded coverage/dependency caches idempotently after receipt publication; replay durable change sequences or reconcile against receipts after restart or a missed notification. A compact availability bitmap/set can be disposable. Reopening a build may do a bounded ordered intersection of its ledger and retained-object identities; it must not resolve every ROOT membership or scan every known build for every completed download.

Historical receipts and current physical verification are different evidence. Game-payload reuse needs an explicit assurance/freshness contract: presence/change detection, verification on access, background checking and corruption invalidation. A dated receipt-backed retained amount never silently becomes freshly verified bytes. Unknown coverage stays unknown. Rehashing the entire game cache on every Home entry is not an acceptable cost for responsiveness.

## Publication, recovery and migration

Every immutable derived artifact binds source identities/receipts, decoder/index revisions, exact scope, counts, length and checksum/block-proof information. Build it in app-owned staging with bounded buffers, validate, publish immutable bytes, then register it in a short SQLite transaction. Handle orphan bytes and missing registrations explicitly. A saved digest alone does not prove current file contents; be clear whether whole-file or authenticated block/page verification ran and what it cost.

Completed source/artifact capabilities survive interruptions. Unfinished artifact work retries independently or uses a tested chunk checkpoint. Stage checkpoints stay useful for the legacy full-catalog capability; future features don't depend on that catalog.

Preserve discovery history, verified objects, receipts and existing completed catalog generations. A new capability gets a new profile rather than reinterpreting `home-inventory-v1` READY. Abandoned relational staging cleanup is bounded low-priority maintenance after old producers/leases are fenced; it never blocks Home. No full reset or automatic VACUUM.

## Alternatives considered

| Choice | Assessment |
| --- | --- |
| Keep the complete SQL graph as the universal opening gate | Useful for broad catalog queries, but does unnecessary upfront work for size and targeted research |
| Replace everything with custom mutable files | Adds transaction, update, recovery and query complexity with no evidence that receipts/jobs need a different store |
| Hybrid (above) | Fits immutable bulk sources plus incremental mutable state; keeps SQL where queries benefit |

Before accepting a new binary artifact format, compare it with compact SQLite on equivalent source verification, duplicate/conflict handling, DS prefix refusal, memory/temp bounds, crash windows and incremental receipts. Measurements decide individual physical layouts, not a speculative rewrite of every database function.
