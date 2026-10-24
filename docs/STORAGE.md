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

## Settings

App-wide settings live in `settings-v1.json` in the engine data directory, next to the catalog, not in it. The engine writes a temporary file, flushes it and then replaces the file, so a failed write leaves the old one. A missing file means defaults; a malformed, oversized or newer-version file, or a single value it doesn't understand, is reported and that value uses its default. A newer file, or one whose bytes could not be read (too large, changed while read, locked or not permitted), is never overwritten. See [settings-v1](../contracts/settings-v1.md).

## Fetched table definitions

Definitions fetched from WoWDBDefs ([D081](DECISIONS.md)) live in `table-definitions-v1/wowdbdefs/` in the data folder, outside the catalog and CAS. Each table has `<tableId>-<sha256>.dbd` (at most 1 MiB, unmodified bytes) and `<tableId>.json`, the record that names them: format, table ID and name, SHA-256, length, source URL, commit, first fetch time and last check time. Both go through a flushed temporary file and a rename, bytes first, so the record never names bytes that aren't there; a later save removes unreferenced `.dbd` files. Every load checks the record, the exact source URL for that commit and table, the length, the SHA-256 and the DBD grammar; anything that doesn't verify counts as never fetched and the next fetch replaces it. A cached definition that lists the build is asked about again after 24 h; one that doesn't, at most every 15 minutes; after a failed attempt, re-checks wait 15 minutes too, but a first open or Retry asks again at once. Unchanged bytes keep their first commit and fetch time, so rows read with them stay current; only the check time moves. Row artifacts read with a fetched definition are keyed by its SHA-256 and commit like any other definition.

## Catalog

The catalog is `discovery-v1.sqlite3` in the engine data directory. It opens with `foreign_keys=ON`, `journal_mode=WAL`, `synchronous=FULL`, `busy_timeout=5000` and file-backed temp storage. One worker runs all SQLite work with at most 64 queued operations. The engine refuses a catalog with a newer `user_version` than it knows (`CATALOG_VERSION`).

The schema is at `user_version` 20, the first with saved Checks of the files Open did not assess ([D093](DECISIONS.md)); 19 was the first with retained Creature appearances ([D088](DECISIONS.md)), 18 the first with file catalogs prepared after Open ([D071](DECISIONS.md)), 17 the first with the Files columns and indexes ([D068](DECISIONS.md)), 16 the first with per-ledger stored coverage ([D065](DECISIONS.md)) and 15 the first with build identity v2 ([D061](DECISIONS.md)). Version-16 to version-19 catalogs upgrade in place. There is no migration from older catalogs: a fresh catalog writes `catalog_lineage` = `stored-coverage-v1`, and any catalog without it, including a complete version 15 `build-identity-v2` catalog, is refused with `CATALOG_RESET_REQUIRED`. The version and lineage are read on a read-only connection before any read-write open, so a refused catalog, including a pending `-wal` an older engine left behind, stays byte-for-byte unchanged (a read-write close would checkpoint it). The host exits with code 3 (code 4 for a newer catalog) and the code on stderr; the desktop shows it as a typed notice that names the catalog folder and the user's step (close VibeMiner, move or rename the folder, start again). It offers no delete, retry or refresh. Starting over means a fresh data directory; never erase the owner's profile without their OK.

Fresh creation still runs the steps below in order, each committing its own `user_version`, so a creation interrupted between steps resumes. Each lives with its feature:

| Version | Adds |
| --- | --- |
| 2 | Discovery: lineage, products, raw documents, builds, per-build CDN config history (`build_cdn_configs`, `build_cdn_config_regions`), observations with their CDN config, regions, baselines, advertisements per CDN config |
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
| 15 | Stored coverage: `stored_changes`, `object_absence`, `stored_coverage`, `stored_pages`, `root_scopes`; SHA indexes on `encoded_objects` and `content_objects` |
| 16 | Marks stored coverage complete |
| 17 | Files ([D068](DECISIONS.md)): numeric FileDataID, locale, content flags, install group and stored mark on `inventory_origins` (backfilled for existing rows), its Files indexes, `inventory_root_variants`, `inventory_file_stats` |
| 18 | File catalogs after Open ([D071](DECISIONS.md)): `file_catalogs`, one row per build with its state, stage, attempts, open order, staging or sealed generation and written-rows progress; the rows themselves are in per-build files |
| 19 | Retained Creature appearances ([D088](DECISIONS.md)): `geometry_appearances` |
| 20 | Checks of files Open did not assess ([D093](DECISIONS.md)): `download_checks`, one row per measured ledger with its manifest, completed and staging generations, check time and counts; `download_check_objects`, one row per checked file with a receipt (verified, missing or corrupt) per generation |

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

### CDN configuration per admission

A build's CDN config can be republished without a new build ([D061](DECISIONS.md)). Each observation keeps the CDN config of its own versions row, and every admission records its `cdnConfig` receipt. A context resolved for an open build uses the CDN config of the latest workspace admission for that build and region; a new open uses the newest one the region advertised. Retention plans and pinned downloads resolve their pinned observation, so they keep theirs. Commit and admission checks compare against the CDN config captured with the context, never the build's newest one. An object that config does not locate may still come from an archive listed only by an earlier CDN config of the same build; its encoded receipt names that config in `cdnConfig` ([D069](DECISIONS.md), [details](formats/tact.md#earlier-cdn-configurations)).

### Open acknowledgement

Only a real Open writes an acknowledgement (clears "new"). Selecting a build or refreshing discovery never does.

## Object store

[VerifiedObjectStore](../src/backend/VibeMiner.Infrastructure/Preparation/VerifiedObjectStore.cs) holds immutable objects keyed by SHA-256, up to 512 MiB each.

- `OpenVerifiedAsync` hashes the whole object on every open. A row reader (`TableRowArtifacts`) verifies on cold open and reuses that handle for the same artifact; switching artifacts reopens and rehashes.
- The one exception is the stored coverage worker's `OpenProvenAsync`: it reopens a download ledger it already proved in this process without hashing it again, only while the file at its CAS path keeps the proven length, creation and write times. It only decides which ledger ordinals count as stored; nothing it reads is served.
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

Files ([D068](DECISIONS.md), [inventory-v1](../contracts/inventory-v1.md#files), [InventoryFiles](../src/backend/VibeMiner.Infrastructure/Inventory/InventoryFiles.cs)) reads a sealed generation only:

- Each origin row stores `fdid_n`, `locale_n` and `flags_n` (ROOT) or `grp` (install: the first membership ID with the same exact name). They are written with the row (backfilled when a version-16 catalog upgrades) and checked against `fdid`, `locale`, `flags` and `name` at every seal (`grp` must equal the first membership ID with that name in the generation); the catalog digest covers those source columns, not the derived ones. `stored` mirrors `inventory_units.retained` of the row's EKey and is rewritten at every seal.
- Partial indexes: `inventory_root_files` (FileDataID order), `inventory_root_variant_files` (one FileDataID-ordered range per locale/content-flag pair), `inventory_stored_files` (stored rows only), `inventory_install_files`; `inventory_content` gains `fdid_n`. Every seal rebuilds `inventory_root_variants` (memberships per pair) and `inventory_file_stats` (file and member counts) for the planner, and rechecks the 4,096-memberships-per-file cap.
- A request drives from the narrowest index: exact FileDataID, CKey, merged variant ranges (at most 256 pairs, 8 then 64 rows per refill), stored rows, or the FileDataID-ordered scan when nothing narrower applies. A filter whose variant statistics have no member returns at once. A file has at most 4,096 memberships (sealing refuses more). Each request stops at the first file boundary after 65,536 membership rows, at most 90,112 in all, then returns `scanLimited` with a cursor.
- Two kinds of generation are Files sources, and a request names exactly one: a legacy full catalog (`home-inventory-v1`, rows in the main catalog, read only for its own build and region) or a file catalog prepared after Open (`files-root-install-v1`, rows in its own file, read only while `file_catalogs` names it as the build's ready one and its file reads back as that sealed generation, through any region the build was opened in).
- Measured with `tools/benchmarks/FilesQuery` on a retail ROOT (3,262,082 memberships, 1,932,853 FileDataIDs, 1,291 variant pairs), warm cache: first pages 0.1 to 3.6 ms median across 21 filter cases, no page above 6 ms, no scan-limited page. The two ROOT indexes take about 160 MiB; the stored index grows to 97 MiB when every membership is stored. A full-budget scan request took about 36 ms.

### File catalogs after Open

[FileCatalogWorker](../src/backend/VibeMiner.Infrastructure/Inventory/FileCatalogWorker.cs) ([D071](DECISIONS.md), [inventory-v1](../contracts/inventory-v1.md#file-catalog-after-open)) writes the ROOT and install memberships of a build into a `files-root-install-v1` generation. Each Open of a build with a download admission queues it in `file_catalogs` unless it is pending or ready, and makes it the most recently opened.

- **One file per catalog.** The rows live in `file-catalogs/files-<generation>.sqlite3` in the engine data folder, not in `discovery-v1.sqlite3`: the same `inventory_origins` columns and Files indexes, `inventory_root_variants`, `inventory_file_stats`, and a one-row `catalog_identity` (generation, build, exact source receipts, rows written, sealed). No DL, DS or EN rows; the text `fdid`/`locale`/`flags` copies are null (Files reads only the numeric ones). The file is attached to the catalog connection (`fc<generation>`) only while written or read, at most three at once, least recently used detached first, outside any transaction. The main catalog keeps `file_catalogs` and the generation's registry row in `inventory_generations`.
- **Bounds.** A file stops at 4 GiB (`max_page_count`); retail 12.1.5.70448 (3,262,082 memberships) needs about 1.2 GB. Before writing, the volume (the longest mount point containing the data folder on whole path segments; none found is a `STORAGE_UNAVAILABLE` refusal, never unlimited space) must have 512 bytes per remaining membership plus 256 MiB free, and before every 64th batch at least 256 MiB; otherwise the preparation fails with `STORAGE_FULL` and writes nothing more. These files do not count against the main catalog's 16 GiB `max_page_count`. Settings can free one build's catalog (see below).
- **Rows.** IDs follow the source order: ROOT entries first, then install entries. Every row is resolved before it is written, from an in-memory index of the build's distinct content keys (see [ASYNC-AND-JOBS](ASYNC-AND-JOBS.md#file-catalog-worker)). `stored` is the D065 rule for that EKey and size when the batch is written; it is not refreshed later.
- **Batches.** 1,024 rows, their stored marks and the file's own position commit in one transaction of that file, fenced by the main row's generation and attempt count. The main row's `written` is a progress copy for the status read; a restart continues from the file's position.
- **Resume.** The file records the exact source receipts it is derived from. A later run with the same sources derives the rows again, compares the saved ones with them and continues after them. Different sources, a difference in any row, a file that is missing, corrupt or not this generation, or a registry row that no longer says staging abandon it, and the attempt starts from the beginning.
- **Seal.** The planner statistics come from the derived rows (the 4,096-memberships-per-file cap is checked before any row is written). A transaction over two WAL databases is not atomic, so the seal is two: the file's (its rows end at the expected count, statistics recorded, marked sealed) commits first, then the main catalog makes the generation ready. A crash in between leaves a sealed file under a staging generation, which the next run checks and seals again. An Open of a `ready` build whose file is missing or does not read back as that sealed generation (read on its own read-only connection) queues it again from the start.
- **Cleanup.** An abandoned or unreferenced staging generation is removed by deleting its file (and `-wal`/`-shm`), then its registry row; a file no registry row names is deleted too. A file that can't be deleted (a lock, a permission) is skipped for that pass with its registry row kept (at most 256 per pass), so it never stops the worker, and is logged once, not at every retry; the worker then looks again after its retry delay (30 s) or at its next wake-up ([D085](DECISIONS.md)). Legacy staging cleanup leaves file catalog generations alone.
- **Free** ([D085](DECISIONS.md), [settings-v1](../contracts/settings-v1.md#prepared-data-per-build)). `settings.storage.free` removes one build's file catalog, in any state. In one catalog operation: one transaction sets the `file_catalogs` row to `removed` (new generation, no inventory, no rows written) and marks every `files-root-install-v1` generation of the build `abandoned`; a running preparation is then cancelled and its late writes are fenced out; then the generations' files are measured, each is detached, its files deleted and its registry row deleted. Freed bytes are that measurement less what is left, taken before the detach merges the `-wal` into the catalog file, so they match the size `settings.storage.builds` showed. Doing it all in one operation means nothing attaches the file between the commit and the delete. Every Free wakes the worker, whose cleanup removes generations past the newest 16 and any file the Free couldn't delete; such a file keeps its abandoned registry row (`STORAGE_FREE_INCOMPLETE`) and is retried as above. The next Open or Resume queues the build again from the start. Table rows and downloaded objects stay: they are shared ([#65](https://github.com/aircan80/vibeminer/issues/65)).
- **Measured.** `settings.storage.builds` reports each opened build's file catalog as the summed lengths of its generations' files on disk at the time of the read, beside the verified lengths of the table row files its admissions use and its stored download figure.

## Build inventory and retention

Readiness is scoped to an explicit capability/profile. Production opening uses `home-download-v1`: authenticated configurations and a supported DL manifest yield a scoped download measurement without full research indexing. A 24-byte-per-object immutable sorted ledger lives in CAS; SQLite stores its source/artifact associations and dated assessment. Building the ledger uses an external sorter with at most 1 GiB of temporary files. There is no per-DL-membership SQL insert. Missing or unsupported DL size is explicit and does not imply an empty build. See [the download contract](../contracts/download-v1.md).

Historical `home-configurations-v1` and legacy `home-inventory-v1` keep their meanings. `files-root-install-v1` is a file catalog only (ROOT and install memberships, no download accounting), prepared after Open and never a workspace profile ([D071](DECISIONS.md)). The latter requires complete supported EN/ROOT/IN/DL/declared-DS proofs and a sealed query catalog; its checkpoints remain for optional full research work. Legacy staging cleanup is outside the Home path.

Unknown required families, missing mappings, ambiguous DS prefixes and conflicting required lengths block a complete inventory/accounting claim. Metadata archive locators are required only to acquire those source objects; payload locations/availability may stay unassessed. Keep separate unique logical CKey and exact encoded EKey ledgers. Exact advertised representations stay distinct even when decoded content matches. Verified current shared objects reduce remaining encoded demand once; metadata/database overhead is separate. Reuse assessment considers a bounded set of metadata receipts (at most 10,000 retained-object candidates per assessment). See [the inventory contract](../contracts/inventory-v1.md).

Download all follows the ledger standing for the admitted one: the published download manifest plus the encoded object of every ROOT-listed file it does not cover ([D066](DECISIONS.md)), preserving variants. Open admits the manifest; `root_scopes` holds each build's measurement of the rest (pending, measured with the extended ledger, manifest-only with the reason, or failed after three attempts) with a generation that fences late results. ROOT content keys ENCODING cannot resolve are reported, not retained. The extended ledger's binding records the exact DL, ROOT and ENCODING sources, the scope and that count. It is not every EN record, the IN executable list alone, or a small research subset. Initial scope avoids unproven locale/platform filtering. Missing or ambiguous mappings stay unresolved, never an empty success.

Encoded payload retention can succeed without decoding every asset. Resolver metadata must still be interpretable enough to establish the inventory. Retaining one representative build/table proves nothing universal.

On demand changes future bulk intent and leaves retained bytes intact. Shared consumers and references stay valid. Full coverage is checked against current physical evidence; queued jobs and past success are not enough.

### Stored figure per ledger

The Downloaded figure ([D065](DECISIONS.md), [D066](DECISIONS.md)) is how much of a build's scope is stored here (its admitted DL ledger, then the extended one once ROOT is measured), kept current as objects arrive, with no presence check needed. A member counts when a verified receipt of its exact EKey and length exists in `encoded_objects`, `content_objects` or `metadata_objects`, no `object_invalidity` row covers those bytes, and no `object_absence` row says a later presence check found it missing. `stored_coverage` holds one row per ledger (counts, the last applied change, the first-count cursor, a problem) and `stored_pages` one 512-byte bitmap per 4096 members, only for pages with a stored member.

Every transaction that can change the rule above appends the EKey to `stored_changes`: a new receipt row, a re-verified receipt that clears an absence, corruption recorded or cleared (all EKeys of those bytes), and a presence check that finds a member missing or present again. The [stored coverage worker](ASYNC-AND-JOBS.md#stored-coverage-worker) applies each change to every admitted ledger by exact key lookup, so one download moves every build that shares the object. Admission registers the ledger with a fence at the newest change; a first count walks retained EKeys in key order against the ledger, and later changes replay on top, so the result does not depend on ordering. Applied changes are pruned below the oldest maintained fence. Recounting is idempotent per bit. Receipt credit (`retention_bits`) and the dated presence pass keep their meanings; this figure is neither. Metadata counts only when it is itself a build file: ROOT is a manifest member on retail, so a build is at "<0.1%" once its file list has been read.

## Accounting and cleanup

Report logical build scope, retained encoded coverage, unique physical payload bytes, shared bytes, additional download bytes, database/temporary overhead and reclaimable bytes separately. A complete denominator is required for a retained percentage. Shared bytes are a subset of physical bytes, not an extra copy.

The only deletion is freeing one build's file catalog ([D085](DECISIONS.md)), which no other build uses. There is no garbage collection of shared objects yet ([#65](https://github.com/aircan80/vibeminer/issues/65)). When it arrives, release references first and reclaim only objects unreachable from every retained build, artifact, pin, active job and read lease. No naive per-build directory delete, and freed SQLite pages are not returned filesystem space.

## Known limitations

- CAS publication of staged files does not fsync the file or its directory; small `PublishAsync` writes use write-through but no directory fsync. Durability across power loss is not qualified.
- Generic CAS publication does not repair a corrupt native or derived artifact at an existing SHA path. Such artifacts are reported as corrupt; only the encoded-object repair path replaces bytes.
- Reuse assessment covers a bounded set of metadata receipts, not every object in the store. Bulk receipts will need paged or change-sequence assessment.

## Durability and performance

Start from correctness-preserving durable settings; don't disable synchronization, foreign keys or verification to hit a benchmark. Measure transaction size, redundant indexes, compact keys and query plans before blaming the language.

The Java authored EN benchmark exceeded one million inserts and showed material batching/index effects. Its numbers shape workloads; they are not .NET performance claims. See [MIGRATION.md](MIGRATION.md).
