# Local storage and readiness

C# owns one embedded SQLite catalog and one app-owned immutable object store (CAS). Builds, policies and derived artifacts reference shared bytes instead of keeping separate archive/cache/per-build copies.

## Identity and provenance

Physical payload identity is local SHA-256 plus exact length. Blizzard content keys, encoding keys, configuration identities and locator references stay distinct typed protocol values. A header EKey is not automatically a whole-file MD5; encoded integrity and decoded CKey verification establish different facts.

Keep immutable source receipts with URL, observation/retrieval time, original source digest, route, protocol identities, verifier semantics/version and actual result. Reuse must not restamp an old object as a fresh download or replace original evidence with a hypothetical source.

Group build displays without merging source observations. Research locale and host OS do not select a game payload platform. Derived artifact identity includes exact source digests, schema/provider and decoder revisions, locale, transformation settings and all dependencies.

### Timestamps in derived identity

Derived content keys and CAS metadata bytes drop `PreparedAt` and nested receipt `RetrievedAt`/`VerifiedAt`, so re-verification alone never produces a new SHA for the same logical artifact. Descriptors and admissions keep the original full receipts and times.

Source authority checks are stricter: they compare original `RetrievedAt`, URLs, locations, hashes, lengths and interpretation policy, and ignore only `VerifiedAt`. Never substitute normalized data identity for admission authority.

### Legacy receipt locations

A legacy metadata receipt with a null `Location` means a loose object at offset 0, source length equal to the encoded length, and no archive key, under the validated official exact-EKey URL (see `DownloadCatalog.MetadataSourceLocation`). This is a historical representation, not permission to treat any missing asset evidence as a loose locator.

## Catalog

The catalog is `discovery-v1.sqlite3` in the engine data directory. It opens with `foreign_keys=ON`, `journal_mode=WAL`, `synchronous=FULL`, `busy_timeout=5000` and file-backed temp storage. One worker runs all SQLite work with at most 64 queued operations. The engine refuses a catalog with a newer `user_version` than it knows (`CATALOG_VERSION`).

The schema is at `user_version` 14. Migrations are additive and each lives with its feature:

| Version | Adds |
| --- | --- |
| 2 | Discovery: products, raw documents, builds, observations, regions, baselines, advertisements |
| 3 | Configuration objects, workspace admissions, Open acknowledgements |
| 4–5 | Legacy full inventory tables and preparation checkpoints |
| 6 | `home-download-v1`: download artifacts/sources/admissions, resolver artifacts |
| 7 | Table data: `content_objects`, `content_locators`, `table_artifacts`, `table_admissions` |
| 8 | `encoded_objects` receipts |
| 9 | Retention jobs/coverage/bits/problems, native sources, object invalidity; `items_artifacts`, `items_admissions` |
| 10 | `icon_artifacts`, `item_icon_admissions` |
| 11 | `item_classification_artifacts`, `item_classification_admissions` |
| 12 | `creatures_artifacts`, `creatures_admissions` |
| 13 | `item_effects_bridge_artifacts`, `item_effects_bridge_admissions` |
| 14 | `geometry_artifacts`, `geometry_source_locators` |

The catalog stores observed builds and raw source references; per-region observations; first-seen/baseline/Open acknowledgements; verified object aliases and build memberships; artifact generations and source bindings; admitted inventories; retention policy and job checkpoints; and bounded query indexes.

Large game payloads and binary derived artifacts live in CAS, not SQLite. Small original metadata documents can be deduplicated database blobs. One migration owner uses parameterized SQL, explicit transactions, foreign keys and bounded waits.

The port does not open Java catalogs in place. The Java schema and serialized proofs need a separate compatibility decision. Never point an experimental writer at an existing user archive; use isolated storage (`VIBEMINER_DATA_DIR`).

### Discovery and configuration bounds

- Discovery HTTP: 20 s per request, 10 s connect timeout.
- Configuration documents and `raw_documents.body`: 1 byte to 1 MiB.
- Configuration preparation has a 2-minute overall deadline.

`raw_documents.body` type (BLOB) and length (1..1 MiB) are checked in SQL before the value is materialized; invalid values are masked to NULL and refused with a typed error. Text fields are bounded the same way before `GetString`. Do not add a 64-region history cap: 64 limits one discovery attempt, but retained regional history grows across observations.

### Gotcha: reading BLOBs

`SqliteDataReader.GetBytes` aborted the native test host when used to probe and incrementally read cast BLOB columns; the exact cause was not established. Check `length()` in SQL first, then read one bounded `byte[]` with `GetFieldValue<byte[]>`. Don't use `GetBytes`.

### Open acknowledgement

Only a real Open writes an acknowledgement (clears "new"). Selecting a build or refreshing discovery never does.

## Object store

[VerifiedObjectStore](../src/backend/VibeMiner.Infrastructure/Preparation/VerifiedObjectStore.cs) holds immutable objects keyed by SHA-256, up to 512 MiB each.

- `OpenVerifiedAsync` hashes the whole object on every open. A row reader (`TableRowArtifacts`) verifies on cold open and reuses that handle for the same artifact; switching artifacts reopens and rehashes.
- `ReadAsync` and `PublishAsync(byte[])` are for small configuration bytes only: at most 1 MiB.
- Corruption found on open is journaled centrally while holding the SHA stripe. Repair can replace a bad encoded object only after independent complete verification of the incoming bytes. A durable epoch stops an older proof from clearing a newer corruption record.

Hashing order and the CPU/stripe deadlock rule are in [ASYNC-AND-JOBS.md](ASYNC-AND-JOBS.md#lock-order-and-deadlock-rules).

## Publication and recovery

1. Acquire exact source/work ownership and reserve bounded resources.
2. Stream to an app-owned temporary file while measuring and checking declared limits and integrity.
3. Finish required whole-source/decoded verification before publication. Retire canceled or failed staging without reporting a ready object.
4. Flush according to the durability policy and promote immutable bytes atomically where the filesystem supports it.
5. Commit catalog references and actual proof facts transactionally under the owning generation.
6. Recover interruptions between these steps without claiming missing or corrupt bytes are complete.

A filesystem promotion and SQLite commit are not one atomic transaction. Test orphan bytes, interrupted association, conflicting content, stale owners, disk errors and process death.

Derived indexes use STAGING generations and batched writes. Complete authenticated input, grammar/EOF, count checks and final publication are prerequisites for READY. Readers never see a half-populated ready generation. A late failure preserves an earlier valid generation.

### Admission revisions

Table admissions carry a decoder/admission revision. Revision bumps are scoped per table, definition and provider: the scoped Item effect revision applies only to its exact pinned definition/provider. An admission with a stale but recognized revision stays readable as "not prepared", is rebuilt lazily when a feature needs it, and is never backfilled in bulk. An unrecognized revision (invented combination, `v999`, or a scoped revision on another table) is unsupported, never silently upgraded.

Current revisions are `wdc5-values-v7+table-admission-v3` and `wdc5-values-v7+table-admission-projection-v3`, plus `-v4` of each for the pinned Item effect source only ([TableDataStore](../src/backend/VibeMiner.Infrastructure/TablesData/TableDataStore.cs)). Recognized historical revisions:

- Full: `wdc5-fixed-values-v1+table-admission-v1`, `wdc5-fixed-values-v3+table-admission-v1`, `wdc5-values-v5+table-admission-v1`, `wdc5-values-v6+table-admission-v1`, `wdc5-values-v7+table-admission-v2`, `wdc5-values-v7+table-admission-v3` (historical for Effect).
- Projected: `wdc5-fixed-values-v3+table-admission-projection-v1`, `wdc5-values-v4+table-admission-projection-v1`, `wdc5-values-v5+table-admission-projection-v1`, `wdc5-values-v6+table-admission-projection-v1`, `wdc5-values-v7+table-admission-projection-v2`, `wdc5-values-v7+table-admission-projection-v3`.

There was never a full `values-v4` or `fixed-values-v2` admission.

### CDN routes

Acquisition falls back across at most 4 captured, validated HTTPS CDN routes (the original plus up to 3 from the CDN `Servers` field). Only availability failures (missing, timeout, network, not located) try the next route. Integrity errors never trigger a mirror retry. Archive proofs are retained independently of content publication, so an encrypted or failed payload keeps its locator work without claiming readable bytes.

### Legacy full research preparation

Legacy full research preparation keeps versioned stage checkpoints with immutable source receipts, phase row counts and SHA-256 proofs. Exact build/region/profile/original discovery context must match before restore. Completed source indexes survive interruptions; an incomplete source index alone is rebuilt from reverified cached metadata. EN content and encoded-object indexing have separate checkpoints. Saved derived columns do not invalidate immutable-source proofs; completed resolution has its own proof.

DL/DS complete matches use durable pending-result rows and a matched/saved state. Publishing a batch inserts required units, updates memberships, removes pending results and advances its saved count in one SQLite transaction. ROOT/IN batch output and resume cursor commit together. Resuming verifies retained source bytes, completed proofs and saved mappings. Final consistency, canonical digest, current shared-object verification and sealing still gate READY. Legacy interrupted generations without checkpoints cannot be inferred complete; cleanup is bounded and reports actual removed/total rows.

Bounds ([InventoryCatalog](../src/backend/VibeMiner.Infrastructure/Inventory/InventoryCatalog.cs), [InventoryOrderedResolution](../src/backend/VibeMiner.Infrastructure/Inventory/InventoryOrderedResolution.cs)):

- ROOT resolution considers EN candidate ordinals 0..254; a content record with more than 255 candidates is refused.
- Ordered merge slices stop at 8,192 transitions, 1,024 output rows or about 40 ms, whichever comes first.
- The read snapshot refuses to continue when the WAL exceeds 2 GiB.
- Cleanup deletes at most 8,192 rows per statement.
- ROOT/IN resolution cursors read and commit in batches of 256.

## Build inventory and retention

Readiness is scoped to an explicit capability/profile. Production opening uses `home-download-v1`: authenticated configurations and a supported DL manifest yield a scoped download measurement without full research indexing. A 24-byte-per-object immutable sorted ledger lives in CAS; SQLite stores its source/artifact associations and dated assessment. Building the ledger uses an external sorter with at most 1 GiB of temporary files. There is no per-DL-membership SQL insert. Missing or unsupported DL size is explicit and does not imply an empty build. See [the download contract](../contracts/download-v1.md).

Historical `home-configurations-v1` and legacy `home-inventory-v1` keep their meanings. The latter requires complete supported EN/ROOT/IN/DL/declared-DS proofs and a sealed query catalog; its checkpoints remain for optional full research work. Legacy staging cleanup is outside the Home path.

Unknown required families, missing mappings, ambiguous DS prefixes and conflicting required lengths block a complete inventory/accounting claim. Metadata archive locators are required only to acquire those source objects; payload locations/availability may stay unassessed. Keep separate unique logical CKey and exact encoded EKey ledgers. Exact advertised representations stay distinct even when decoded content matches. Verified current shared objects reduce remaining encoded demand once; metadata/database overhead is separate. Reuse assessment considers a bounded set of metadata receipts (at most 10,000 retained-object candidates per assessment). See [the inventory contract](../contracts/inventory-v1.md).

Download all follows the complete supported published-manifest inventory and dependency closure, preserving variants. It is not every EN record, the IN executable list alone, or a small research subset. Initial scope avoids unproven locale/platform filtering. Missing or ambiguous mappings stay unresolved, never an empty success.

Encoded payload retention can succeed without decoding every asset. Resolver metadata must still be interpretable enough to establish the inventory. Retaining one representative build/table proves nothing universal.

On demand changes future bulk intent and leaves retained bytes intact. Shared consumers and references stay valid. Full coverage is checked against current physical evidence; queued jobs and past success are not enough.

## Accounting and cleanup

Report logical build scope, retained encoded coverage, unique physical payload bytes, shared bytes, additional download bytes, database/temporary overhead and reclaimable bytes separately. A complete denominator is required for a retained percentage. Shared bytes are a subset of physical bytes, not an extra copy.

There is no deletion or garbage collection yet. When it arrives, release references first and reclaim only objects unreachable from every retained build, artifact, pin, active job and read lease. No naive per-build directory delete, and freed SQLite pages are not returned filesystem space.

## Known limitations

- CAS publication of staged files does not fsync the file or its directory; small `PublishAsync` writes use write-through but no directory fsync. Durability across power loss is not qualified.
- Generic CAS publication does not repair a corrupt native or derived artifact at an existing SHA path. Such artifacts are reported as corrupt; only the encoded-object repair path replaces bytes.
- Reuse assessment covers a bounded set of metadata receipts, not every object in the store. Bulk receipts will need paged or change-sequence assessment.

## Durability and performance

Start from correctness-preserving durable settings; don't disable synchronization, foreign keys or verification to hit a benchmark. Measure transaction size, redundant indexes, compact keys and query plans before blaming the language.

The Java authored EN benchmark exceeded one million inserts and showed material batching/index effects. Its numbers shape workloads; they are not .NET performance claims. See [MIGRATION.md](MIGRATION.md).
