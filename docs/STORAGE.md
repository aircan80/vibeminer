# Local storage and readiness

C# owns one embedded SQLite catalog and one app-owned immutable object store (CAS). Builds, policies and derived artifacts reference shared bytes instead of keeping separate archive/cache/per-build copies. A storage rewrite (#153-#160) replaces both; its first layer is below, and the rest of this page describes the store it replaces.

## Layer 1: the object store

The storage rewrite (#153-#160) splits local data into three layers. Layer 1, the object store, is a permanent copy of what the CDN served: configs, archive and loose-file indexes, encoded (BLTE) objects and dated version-service replies. Layer 2 (catalog and settings) and layer 3 (everything generated) are rebuilt from it and can be reset; layer 1 survives a reset. It knows nothing about builds or features ([D121](DECISIONS.md)). Code: [`src/backend/VibeMiner.Objects/`](../src/backend/VibeMiner.Objects/), a project that references no other engine project. The sections after this one describe the old store, which slices 2-6 replace.

**The full specification is [object-store-v1](formats/object-store-v1.md):** byte layouts, keys, verification, the index schema, every algorithm, durability, concurrency, codes and limits. In short:

- **Files.** `cdn-store-v1/` in the data folder holds:
  - append-only packs (`packs/pack-NNNNNN.vmp`, 2 GiB by default) of entries: a 96-byte header, the bytes as served, and a CRC-32C per 64 KiB block;
  - a rebuildable SQLite index, `objects.sqlite3`;
  - a `writer.lock`, and `staging/` for large objects arriving in pieces.
- **Verify first** ([D122](DECISIONS.md)). Every object is checked against its own key before it is appended: config MD5, index footer and page hashes, BLTE header and chunk MD5s, history record grammar.
- **Group commit** ([D123](DECISIONS.md)). Fsync the pack, then commit the batch's rows. A batch closes at 128 objects, 16 MiB, 250 ms, or when nothing else is queued. Pack rolls and compaction switches commit with `synchronous=FULL`.
- **Reads check** ([D124](DECISIONS.md)) the header, every block's CRC, and the SHA-256 on the first read of a small object in a session. A range read touches only its blocks. Damage reads as Corrupt, never as bytes.
- **Recovery and rebuild** ([D125](DECISIONS.md), [D126](DECISIONS.md)):
  - an open replays the synced tail past the last commit, keeping the first copy of a key;
  - every open runs a cheap index check (schema, CRC-checked store and pack rows); the full check (`integrity_check`, totals over every row) runs after an unclean shutdown or as a scrub, and a failing index is rebuilt from the packs;
  - Scan rebuilds it in place.
- **History log** ([D127](DECISIONS.md)): raw versions, cdns, bgdl and summary replies and WoWDBDefs table definitions, stored once, with dated records. A summary record has an empty product ([D152](DECISIONS.md)); a definition record is kind 5 ([D181](DECISIONS.md)).
- **Stats** ([D150](DECISIONS.md)): `StatsAsync` on the store and the read-only store gives packs, object and history counts and pack bytes from one index read, no pack touched.
- **Formats** ([D129](DECISIONS.md)): anything newer is refused by name and never truncated.
- **Second store** ([D130](DECISIONS.md)): a read-only store, such as the shared dev store, can be read alongside the profile's own.
- **Interfaces** ([D131](DECISIONS.md)): the layers above use `IObjectReader`, `IObjectWriter`, `IHistoryReader`/`IHistoryLog`, `IObjectStoreMaintenance` and `IObjectVerifier` (`ObjectVerification.Default`, the one instance Put uses). Tests use `InMemoryObjectStore`, kept in step with the real store by `ObjectStoreContractTests`.

### Data folder

Electron resolves the data folder once at start ([D128](DECISIONS.md)): `VIBEMINER_DATA_DIR`, then the choice saved in `data-folder-v1.json` (`{"version":1,"path":"<absolute>"}`) in Electron's userData folder, then the per-OS default.

| OS | Default | Extra |
| --- | --- | --- |
| Windows | `%LOCALAPPDATA%\VibeMiner` | |
| macOS | `~/Library/Application Support/VibeMiner` | `tmutil addexclusion` at each start; Spotlight still indexes it |
| Linux | `$XDG_DATA_HOME/VibeMiner`, else `~/.local/share/VibeMiner` | |

A choice file that can't be used (unreadable, not JSON, a newer version, an unknown field, not an absolute path) stops the start with a message naming the file. So does a folder that can't be created, a chosen folder whose parent is missing, or one that refuses writes: a small probe file is written and removed at each start. The test override is never excluded from backups. The engine's own default without `--data-dir` is the same folder. Choosing a folder in Settings arrives with #155.

### Measured

Synthetic headerless objects, i9-13900K (32 threads), btrfs on LUKS on NVMe, page cache warm; `tools/benchmarks/ObjectStore` ([README](../tools/benchmarks/ObjectStore/README.md)).

| Measure | Result |
| --- | --- |
| Put, 20,000 objects (mean 84 KiB, 1.6 GiB), 1 caller, batches of 128 | 2,997 obj/s, 246 MiB/s |
| Same, 8 callers of 16 / 32 callers of 4 | 4,752 obj/s, 390 MiB/s / 4,671 obj/s, 383 MiB/s |
| Index at 30M rows | 2,114 MiB, 73.9 B/row |
| Has at 30M rows, present / absent key | p50 4.7 / 4.1 µs, p99 6.6 / 5.8 µs |
| Load of 30M 17-byte objects (8 callers) | 1,238 s, 24,235 obj/s |
| Scan of the 30M store | 806 s, identical rows (`ObjectStoreScaleTests`) |
| The full index check (`integrity_check`, row checks, totals over every row), run by an open after an unclean shutdown or a scrub, 3M / 10M / 30M rows | 2.7 s / 7.6 s / 22.7 s, of which `integrity_check` 0.4 s / 1.2 s / about 3.6 s; the rest is the totals digest. An open after a clean close runs only the cheap check |
| Has at 3M / 10M rows (present key), from the cached scale store | p50 4.4 / 4.6 µs, p99 5.7 / 5.9 µs |
| A scale run on the cached store (`VIBEMINER_OBJECT_STORE_SCALE=3` / `10`), once built (58 s / 325 s) | 5 s / 11 s |
| Reopen recovering 128 synced, uncommitted entries | 0.06 s |
| Scan / Verify / open with the index deleted, 4 GiB in 48,896 objects | 4.2 s / 4.6 s / 4.2 s |

Every object pays three hashes on Put (its key's MD5, SHA-256, CRC-32C), about 520 MiB/s on one core; one fsync is about 6.6 ms here. The scale checks are opt-in, not part of `npm test` ([TESTING](TESTING.md#object-store-scale-store)).

## Getting keys: the CDN clients and object access

Slice 2 (#154) adds the layer 1 clients that talk to Blizzard and layer 2, object access, the only place that decides local or CDN ([D132](DECISIONS.md)-[D136](DECISIONS.md)). Nothing above layer 2 touches layer 1 except the composition root.

### Layer 1: CDN and version-service clients

Code: [`src/backend/VibeMiner.Cdn/`](../src/backend/VibeMiner.Cdn/), a project with no engine references. It knows keys, byte ranges and HTTP, nothing about stores or builds.

- **`ICdnClient`** (`CdnClient`) runs one request at a time over a `CdnRoute` (a `cdns` row's official hosts in advertised order, plus the product path). A request is a whole key or exactly one byte range of it, under `config/`, `data/` or `data/…​.index`. Multi-range requests are never sent: `us.cdn` answers them with the whole 256 MiB archive.
- **Reply checks**, all before the body is read: no redirect; 404 (and level3's 403 for a missing key) is `NotFound`; a 200 to a range request is `RangeIgnored` and the response is dropped at the headers, which closes the HTTP/1.1 connection or resets the HTTP/2 stream; a 206 must carry exactly `bytes from-to/total` for the request, and the total must equal an expected one when given; no `Content-Encoding`; media type one the CDN serves; `Content-Length`, the range length and the received length must match the declared length (`SizeConflict`). A whole in-memory reply (at most 64 MiB) can carry a check (`CdnCheck`), which layer 2 fills with `ObjectVerification.Default`; failing it is `VerificationFailed` with the store's refusal code.
- **Hosts** are tried in the order given. Every failure moves to the next host (a check failure too: the next host's bytes are checked again), except in a streamed fetch, which never moves once a byte has reached its sink, so a sink never sees two hosts' bytes. `CdnException` lists every attempt. Its overall state: `Offline` only when no host answered; `NotFound` when hosts answered 404 or not at all, a dated observation that is retryable and never means absent; size conflicts and check failures are not retryable, unless another host failed for a passing reason (no connection, timeout, 5xx), which wins: one host's bad copy doesn't prove the key is bad. Layer 2 does the same above the client: a merged-range member or a staged chunk that fails its check is fetched again from the remaining hosts in ranked order, never from a host that already sent bad bytes (a staging resumes from its proven bytes), and is refused only when no host is left.
- **Timeouts:** 20 s to the headers, 30 s without body bytes, plus the caller's cancellation. Cancellation is never a `CdnException`.
- **`IWoWDBDefsClient`** (`WoWDBDefsClient`) fetches one table's `.dbd`: the last commit on `master` that changed it from `api.github.com`, then the file at that commit from `raw.githubusercontent.com`. HTTPS to those two hosts only, no redirects, 256 KiB for the commits reply, 1 MiB for the file, 30 s and at most two fetches at once, with #124's connection grants; failures are `CdnException`s ([D181](DECISIONS.md)).
- **`IVersionClient`** (`VersionClient`) fetches `https://{region}.version.battle.net/v2/products/{product}/{versions|cdns|bgdl}`, or `/v2/summary` on the same host (no product), for regions us, eu, kr and tw: at most 1 MiB, `text/plain` or `application/octet-stream`, no redirects, the raw bytes with URL and observation time. `VersionTable` reads the table grammar (the same rules as `MetadataParser`, kept in layer 1 so it needs no engine project), and `CdnsRoutes` turns a `cdns` reply into one route per region, dropping hosts that aren't official and naming regions with no usable route (`ROUTE_AMBIGUOUS`, `ROUTE_INVALID`).
- **`RangePlanner`** is pure. Index membership alone decides ranged or loose, never size. Per archive it sorts members by offset; two members whose spans overlap are both refused (`RANGE_OVERLAP`); an EKey with two sizes from any sources (EN, indexes, repeated requests) is refused (`SIZE_CONFLICT`); an EKey that several archives list gets one location, the earliest archive in the CDN config, then the lowest offset. A range keeps taking the next member while the gap is at most 1 MiB, the range is under 8 MiB, and the result stays within 16 MiB; one object larger than that is a range on its own. `Split` cuts a reply back into member slices and drops the gap bytes.

### Layer 2: object access

Code: [`src/backend/VibeMiner.ObjectAccess/`](../src/backend/VibeMiner.ObjectAccess/), which references only `VibeMiner.Objects` and `VibeMiner.Cdn`.

`IObjectSource` is the whole boundary:

| Call | Does |
| --- | --- |
| `GetKeyAsync(request, cdn, priority)` | One object with its bytes (when it fits in 64 MiB) |
| `GetKeysAsync(requests, cdn, priority, progress)` | Makes every object present; one result per request, in order, no bytes |
| `GetRangeAsync(request, offset, destination, cdn, priority)` | A byte range, after making the object present |
| `ObserveAsync(request)` | One dated build observation, saved in the history log |
| `CountPresentAsync(keys)` | How many keys the profile's own store holds, for the Downloaded figure |
| `HasKeysAsync(keys)` | Whether the profile's own store holds each key, in order (index only, no CDN call), for layer 4's stored state ([D161](DECISIONS.md)) |
| `ReadHistoryAsync(query)` | The history log, for the build list |

A request names the key, its declared length (from EN, an index entry or a config size pair; never a CDN config `*-index-size` hint) and the archive index entries that list it, if any. A loose request with no declared length (a config, say) is charged 4 MiB of the in-flight byte budget and may be at most that; a longer reply is refused as `TooLarge`. `Order` groups requests: lower groups go first, which is how Download all puts DOWNLOAD priority 0 first. Within a group, ranges come in CDN-config archive order, then loose objects. The `CdnContext` is the route from the current `cdns` reply and the CDN config's archive list.

Results are `Present` (with where this call got it, `Store`, `SharedStore` or `Cdn`, and the origin the store recorded), `NotFound` (dated, retryable), `Unavailable` (offline, timeout, 5xx, broken connection; retryable, with a `CDN_*` code), `Refused` (wrong bytes or declarations, or a key the CDN doesn't serve), `NeedsNetwork` (local-only) or `StoreFailed` (for example `OBJECT_STORE_DISK_FULL`).

| Implementation | Behaviour |
| --- | --- |
| `ObjectSource` | Own store, then the shared dev store (read-only; copied in through Put, which checks it again, and recorded with origin `SharedStore`; never pointed into), then the CDN. A copy that fails its read checks is deleted and fetched again |
| `CdnObjectSource` | CDN only: fetches and checks every time, stores nothing, refuses objects over 64 MiB, counts 0 present, has no history |
| `LocalObjectSource` | Local only, a true offline mode: own store, then the shared store; anything missing is `NeedsNetwork`, and nothing touches the network |
| `InMemoryObjectSource` (tests) | Synthetic objects with scripted 404s, outages and refusals; the double for layer 3 and up |

`ObjectSourceContractTests` run one behaviour suite against `ObjectSource` and `InMemoryObjectSource`, and `CdnClientContractTests` against `CdnClient` and `ScriptedCdnClient`, so the doubles can't drift.

**Fetching.** Missing objects are planned (above) and run by a fixed set of workers, never one task per object:

1. A worker claims its members in an in-flight table. A key another caller is fetching is awaited instead; an interactive caller that joins promotes the waiting transfer. If the owner is canceled, the waiter fetches the key itself.
2. Just before the transfer, each member is checked against the store again, so an object an interactive call stored meanwhile isn't fetched twice; the range shrinks to the members left.
3. It takes its bytes from a 256 MiB in-flight budget, then a stream ([D134](DECISIONS.md)).
4. Each member of a range reply is checked with `ObjectVerification.Default`. Verified members are put in batches of up to 4,096; interactive batches commit at once, background ones join the store's group commit.
5. A member that fails its check gets one request of its own, which tries every host, before it is refused: one bad object fails only itself. A merged range that fails on every host for a passing reason (timeout, 5xx, broken connection, a 200 to the range) is retried one object at a time ([#150](https://github.com/aircan80/vibeminer/issues/150)); a 404 or no connection answers for every member.
6. An object over 64 MiB streams into the store's staging, which checks each BLTE chunk as it arrives. A broken transfer keeps the proven chunks, and the next call fetches only the rest as one range.

**Streams** ([D134](DECISIONS.md)). Background work starts at 16 streams and adapts between 16 and 32: one more after a full limit's worth of successes in a row, a quarter fewer on a timeout, connection failure or 5xx. Interactive work has 4 streams of its own, borrows a free background stream when those are busy, and is always granted before any waiting background request. Hosts that answered over HTTP/2 go first for background work, interactive work keeps the advertised order, and a host that just failed goes last for 30 s. 404s don't count against a host. The composition root can give interactive work its own `CdnClient` and connection pool.

**Observations** ([D135](DECISIONS.md)). An `IObservationSource` serves some `ObservationKind`s and returns raw bytes with URL and time. `VersionServiceObservations` serves versions, cdns, bgdl and the summary (product ""). `WoWDBDefsObservations` serves table definitions (kind 5: product the table id, region `wowdbdefs`, `Name` the exact table name, URL the raw `.dbd` at its commit; [D181](DECISIONS.md)). Layer 2 saves each one with `AppendHistoryAsync`. The wago.tools build list (#96) arrives as another source with its own kind, which also needs a layer 1 history kind; nothing else changes.

### Measured

Retail `wow`/us on 2026-11-01 from one machine on a 0.9 Gbit/s line, through `ObjectSource` into a throwaway store, with the official CDN. `tools/benchmarks/ObjectAccess` ([README](../tools/benchmarks/ObjectAccess/README.md)).

| Measure | Result |
| --- | --- |
| First pass: versions, cdns, both configs, 1,402 archive indexes and the file-index, ENCODING, ROOT, DOWNLOAD, INSTALL, SIZE | 1,410 object requests, 447 MiB, 34.8 s. Indexes 120 MiB in 3.7 s; ENCODING 179 MiB in 21.9 s on one stream |
| Second pass on the same store | 0 object requests |
| Half the entries in the first 32 MiB of archives, alternating archives between gaps, 4 runs | Median 15.7 MiB/s useful at 256 KiB (19-29% gap bytes), 19.1 MiB/s at 1 MiB (24-39%) |
| A config fetched at interactive priority while 32 background streams run (512 KiB ranges) | Median 15-20 ms, at most 37 ms over 20 fetches; 23-62 ms idle |
| The same at 16 streams with 8 MiB ranges, two earlier runs | 40-1,864 ms, median 244-338 ms; the slow ones weren't explained (the probe hits the same cached config each time) |

The gap result varied run to run (one run of four favoured 256 KiB); 1 MiB is the default and stays a parameter ([D133](DECISIONS.md)). A single large loose object (ENCODING) is limited by one stream's speed on an edge cache miss.

### Layer 3: the build catalog

`ICatalog` in `VibeMiner.Builds` lists a product's builds ([D138](DECISIONS.md), [D139](DECISIONS.md)). It references only layer 2 and keeps its own versions table reader and build identity v2 port (byte-identical Ids). The list comes only from versions entries in the history log, read through layer 2, so it works offline and keeps older builds. `RefreshAsync` observes versions and cdns from one version-service host (default `us`); layer 2 appends both to the history log, and only versions drive the list. A failed observation is reported (`CATALOG_OBSERVE_FAILED` if it throws) and the list is still served. `RefreshSummaryAsync` observes the version service's summary (`/v2/summary`, history kind 4 with an empty product, [D152](DECISIONS.md)) and reports products whose id starts with `wow` and that aren't reviewed as new; their builds are never read. Summary problems: `SUMMARY_UNREADABLE`, `SUMMARY_COLUMNS_MISSING` (no Product column; nothing listed) and `SUMMARY_ROW_INVALID` (a product that isn't 1-64 of `[a-z0-9_-]`; at most 50, then `CATALOG_PROBLEMS_TRUNCATED`). `Products.Reviewed` gives each reviewed id its friendly name, copied from the engine's registry.

A build is one build identity v2 ([D061](DECISIONS.md)); each (Region, CDN config) is one region listing. Current means listed by the newest readable versions reply from at least one host ever read: highest numeric `## seqn`, then latest observed. A reply with a whole-reply problem never counts.

Problems:
- `VERSIONS_ROW_INVALID` drops one row; `VERSIONS_REGION_AMBIGUOUS` drops a region listed twice.
- `VERSIONS_UNREADABLE`, `VERSIONS_WRONG_KIND`, `VERSIONS_UNKNOWN_PRODUCT` and `VERSIONS_COLUMNS_MISSING` skip the whole reply. Stored; a list returns at most 1000, newest entry first.
- `CATALOG_DOCUMENT_UNAVAILABLE`, `_REFUSED`, `_STORE_FAILED` and `_TOO_LARGE`: layer 2 couldn't serve the reply. Not stored, retried on the next list. At most 50 per list, then `CATALOG_PROBLEMS_TRUNCATED`.

No locales yet: opening a build reads them from its manifests.

The catalog store is a SQLite cache (`user_version` 1, application_id `0x564D6373`, WAL). Deleting it rebuilds the same list from layer 1. A newer (`CATALOG_NEWER`) or unreadable or foreign (`CATALOG_UNREADABLE`) file is refused after a read-only check and left byte-for-byte unchanged. Every list syncs first. The first sync per process reads the versions history from the start, skipping stored entries. Later syncs resume after a real entry at least 10 minutes older than the newest one read, so a late append with an older time is still seen. Failed entries are retried by key, up to 1000; past that the next sync starts from the beginning. The planned path is `catalog-v1/catalog.sqlite3` in the data folder, wired into the Host later in #155.

`InMemoryCatalog` in `VibeMiner.Builds.Testing` is the fake for layers 4 and up: no SQLite, no files, the same list rules. `CatalogContractTests` runs one suite against it and the real `Catalog`.

### Layer 3: opening a build

`IBuilds.OpenAsync(BuildRef)` opens one build ([D140](DECISIONS.md)): product, build config, CDN config and the `cdns` region whose route fetches what isn't stored (default `us`). Everything comes through layer 2 at interactive priority, in this order:

1. The route from the newest readable `cdns` reply in the history log that lists the region (of the 32 latest observed: highest numeric `## seqn` first, then latest observed, replies without a numeric seqn last). None means no route; stored objects still open.
2. The build config and CDN config.
3. ENCODING (whole-object BLTE, EKey and CKey checked), then ROOT through its ENCODING EKey.
4. Every archive index and the `file-index`, parsed four at a time, into one EKey → location map (D133 pick).

The first failure ends the open, with no retry: `NeedsNetwork`, `Unavailable`, `Missing` (a dated 404), `Encrypted`, `Corrupt`, `Unsupported` or `StoreFailed`, plus the `BuildPart` and the layer 2 or parser code. A never-fetched build offline stops at its first config request and returns `NeedsNetwork`.

The opened `IBuild` gives metadata (build name, keys, counts, the ROOT locale union as names) and resolves a FileDataID or a path to every ROOT variant: locale and content flags, CKey, the first EKey ENCODING lists, sizes, and an archive location or none (loose). Parsed state stays in memory for up to 4 opened builds, and at most 2 opens of different builds run at once (each may hold up to 512 MiB encoded and 1 GiB decoded metadata while parsing). A second open returns the same instance without asking layer 2, concurrent opens share one, and only a fully parsed build is kept, so a cancelled or failed open leaves nothing behind. Nothing is written to the catalog store; opening offline reparses what layer 1 holds.

The opened build also lists its FileDataIDs per locale: ascending, each once, from a starting ID, walking ROOT in place. A file is listed when any of its variants has that locale bit (all-locales variants included); content flags don't filter.

Layer 3 has its own ports of the config, BLTE (whole objects, for ENCODING and ROOT only), EN, ROOT and index parsers in `VibeMiner.Builds/Formats`. Ranged and keyed BLTE stay in layer 4. `InMemoryBuilds` in `VibeMiner.Builds.Testing` is the fake; `BuildsContractTests` runs one suite against it and `BuildOpener`.

## Layer 4: files

`IFileSystem.ForAsync(build, locale)` opens the build through layer 3 and returns an `IBuildFiles` handle bound to that locale ([D160](DECISIONS.md)). It fetches no file content. A locale the ROOT locale union doesn't name answers `LocaleNotInBuild`; layer 3's failures keep their state and code.

The handle lists files (`ListAsync(filter)`, ascending FileDataID, an async stream) and stats one by FileDataID or path (`StatAsync`, `ExistsAsync`). A filter takes an inclusive FileDataID range, a path prefix, formats, storage states and encrypted; to page, call again from one past the last FileDataID read. A file is `BuildFile`: FileDataID, listfile name (none until #173), decoded size, content key (CKey hex), format (`Unknown` until #173), storage (`Stored`, `NotStored`, `NoEncodedCopy`) and ROOT's encrypted flag. When a locale matches several ROOT variants, the first in ROOT order wins within the best of: encoded, not low violence and not a high-resolution texture; encoded and not low violence; encoded; any. Stored state checks the first EKey ENCODING lists for the CKey. Lookups are `Found`, `UnknownFile`, `NotInLocale` or `NotInEncoding`.

The handle keeps layer 3's opened build, so a file is an in-memory ROOT and ENCODING lookup. Stored state is never cached: listing asks layer 2's `HasKeysAsync` once per 4096 files, a stat once, `ExistsAsync` not at all. A disposed handle throws. `InMemoryFileSystem` in `VibeMiner.Files.Testing` is the fake; `FilesContractTests` runs one suite against it and `FileSystem`.

Measured on a synthetic 1,000,000-file build over the layer 3 and 2 fakes (`FileSystemTests`, tagged slow): open 155 ms, a stat 1.9 µs on the open handle, a full list 376 ms with 245 `HasKeysAsync` calls. The fakes stand in for ROOT and the index, so these are layer 4 costs only.

## Identity and provenance

Physical payload identity is local SHA-256 plus exact length. Blizzard content keys, encoding keys, configuration identities and locator references stay distinct typed protocol values. A header EKey is not automatically a whole-file MD5; encoded integrity and decoded CKey verification establish different facts.

Keep immutable source receipts with URL, observation/retrieval time, original source digest, route, protocol identities, verifier semantics/version and actual result. Reuse must not restamp an old object as a fresh download or replace original evidence with a hypothetical source.

Group build displays without merging source observations. Research locale and host OS do not select a game payload platform. Derived artifact identity includes exact source digests, schema/provider and decoder revisions, locale, transformation settings and all dependencies.

### Opening reference across refreshes

A build's opening reference names the discovery generation, region, versions and cdns digests and row ordinals that admitted it, and every admission bound to the opening (tables, Creatures, display and geometry locators, appearances) is fenced on it. A refresh whose documents are byte-identical to the ones the current reference names keeps that reference, so a later launch reuses everything already prepared ([D112](DECISIONS.md)). The check itself is still recorded. Any changed byte moves the reference, and work bound to the old one is prepared again.

### Timestamps in derived identity

Derived content keys and CAS metadata bytes drop `PreparedAt` and nested receipt `RetrievedAt`/`VerifiedAt`, so re-verification alone never produces a new SHA for the same logical artifact. Descriptors and admissions keep the original full receipts and times.

Source authority checks are stricter: they compare original `RetrievedAt`, URLs, locations, hashes, lengths and interpretation policy, and ignore only `VerifiedAt`. Never substitute normalized data identity for admission authority.

### Legacy receipt locations

A legacy metadata receipt with a null `Location` means a loose object at offset 0, source length equal to the encoded length, and no archive key, under the validated official exact-EKey URL (see `DownloadCatalog.MetadataSourceLocation`). This is a historical representation, not permission to treat any missing asset evidence as a loose locator.

## Settings

App-wide settings live in `settings-v1.json` in the engine data directory, next to the catalog, not in it. The engine writes a temporary file, flushes it and then replaces the file, so a failed write leaves the old one. A missing file means defaults; a malformed, oversized or newer-version file, or a single value it doesn't understand, is reported and that value reads as Off, whatever its default (D081, D105, D106). A newer file, or one whose bytes could not be read (too large, changed while read, locked or not permitted), is never overwritten. See [settings-v1](../contracts/settings-v1.md).

## Fetched table definitions

Definitions fetched from WoWDBDefs ([D081](DECISIONS.md)) live in `table-definitions-v1/wowdbdefs/` in the data folder, outside the catalog and CAS. Each table has `<tableId>-<sha256>.dbd` (at most 1 MiB, unmodified bytes) and `<tableId>.json`, the record that names them: format, table ID and name, SHA-256, length, source URL, commit, first fetch time and last check time. Both go through a flushed temporary file and a rename, bytes first, so the record never names bytes that aren't there; a later save removes unreferenced `.dbd` files. Every load checks the record, the exact source URL for that commit and table, the length, the SHA-256 and the DBD grammar; anything that doesn't verify counts as never fetched and the next fetch replaces it. A cached definition that lists the build is asked about again after 24 h; one that doesn't, at most every 15 minutes; after a failed attempt, re-checks wait 15 minutes too, but a first open or Retry asks again at once. Unchanged bytes keep their first commit and fetch time, so rows read with them stay current; only the check time moves. Row artifacts read with a fetched definition are keyed by its SHA-256 and commit like any other definition.

## Community listfile

Community file names from wow-listfile ([D105](DECISIONS.md)) live in `community-listfile-v1/` in the data folder, outside the catalog and CAS. `names-<tag>.vlf` is the index: a 32-byte header (`VMLF`, version 1, count, block size, blob offset and length), then one (u32 FileDataID, u32 end of its name) entry per name in ascending FileDataID order, then the UTF-8 names. `release.json` names it: format, tag, GitHub's publish time, first fetch and last check times, the CSV's SHA-256 and length as GitHub published them, the name count, the source URL, and the index's own SHA-256 and length. The index is written and flushed first, then the record; a crash between the two leaves an index no record names, which the next import replaces. At start the whole index is hashed and its entries checked before any name is served; anything that doesn't verify is treated as never downloaded. A download in progress is `download.csv.partial` with `download.json` (tag, SHA-256, length, URL); it resumes only for that exact release and is deleted once imported, or when its bytes don't match. While importing, `entries.tmp`, `names.tmp` and `names-<tag>.vlf.tmp` exist; the folder holds about three times the CSV at most (roughly 450 MB) during an import and about 150 MB after. Turning the switch off deletes nothing. Bidirectional formatting characters are removed from names before they are indexed.

## Community build list

The wago.tools build list ([D106](DECISIONS.md)) lives in the catalog (version 22), apart from Blizzard's observations: `community_build_list` (one row: last attempt, last import time, the answer's SHA-256 and length, skipped rows, the last problem, an import generation that pages bind), `community_builds` (one row per community identity: product, version, build number, configs, wago's date, first and last import, and the Blizzard build it was linked to, if any) and `build_availability` (the latest Check availability result per build, official or community, as JSON with its time). A community row's identity is recomputed from its fields on every read. No source document is kept: the list isn't a receipt for any build.

## Catalog

The catalog is `discovery-v1.sqlite3` in the engine data directory. It opens with `foreign_keys=ON`, `journal_mode=WAL`, `synchronous=FULL`, `busy_timeout=5000` and file-backed temp storage. One worker runs all SQLite work with at most 64 queued operations. The engine refuses a catalog with a newer `user_version` than it knows (`CATALOG_VERSION`).

The schema is at `user_version` 21, the first that records each retained Creature appearance's key revision and VMA1 file ([D103](DECISIONS.md)); 20 was the first with saved Checks of the files Open did not assess ([D093](DECISIONS.md)); 19 was the first with retained Creature appearances ([D088](DECISIONS.md)), 18 the first with file catalogs prepared after Open ([D071](DECISIONS.md)), 17 the first with the Files columns and indexes ([D068](DECISIONS.md)), 16 the first with per-ledger stored coverage ([D065](DECISIONS.md)) and 15 the first with build identity v2 ([D061](DECISIONS.md)). Version-16 to version-19 catalogs upgrade in place. There is no migration from older catalogs: a fresh catalog writes `catalog_lineage` = `stored-coverage-v1`, and any catalog without it, including a complete version 15 `build-identity-v2` catalog, is refused with `CATALOG_RESET_REQUIRED`. The version and lineage are read on a read-only connection before any read-write open, so a refused catalog, including a pending `-wal` an older engine left behind, stays byte-for-byte unchanged (a read-write close would checkpoint it). The host exits with code 3 (code 4 for a newer catalog) and the code on stderr; the desktop shows it as a typed notice that names the catalog folder and the user's step (close VibeMiner, move or rename the folder, start again). It offers no delete, retry or refresh. Starting over means a fresh data directory; never erase the owner's profile without their OK.

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
| 21 | Appearance cleanup ([D103](DECISIONS.md)): `key_revision`, `artifact_sha256` and `artifact_bytes` on `geometry_appearances`, with an index on the file; rows from before it keep them empty |

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

Current revisions are `wdc5-values-v7+table-admission-v3` and `wdc5-values-v7+table-admission-projection-v3`, plus `-v5` of each for the Item effect source read with its reviewed rules ([D108](DECISIONS.md)) and `-v4` of each for a fetched ItemSubClass or CreatureDisplayInfo definition read with reviewed raw rules ([D101](DECISIONS.md), [TableDataStore](../src/backend/VibeMiner.Infrastructure/TablesData/TableDataStore.cs)). Recognized historical revisions:

- Full: `wdc5-fixed-values-v1+table-admission-v1`, `wdc5-fixed-values-v3+table-admission-v1`, `wdc5-values-v5+table-admission-v1`, `wdc5-values-v6+table-admission-v1`, `wdc5-values-v7+table-admission-v2`, `wdc5-values-v7+table-admission-v3` (historical for Effect), `wdc5-values-v7+table-admission-v4` (historical for Effect since D108).
- Projected: `wdc5-fixed-values-v3+table-admission-projection-v1`, `wdc5-values-v4+table-admission-projection-v1`, `wdc5-values-v5+table-admission-projection-v1`, `wdc5-values-v6+table-admission-projection-v1`, `wdc5-values-v7+table-admission-projection-v2`, `wdc5-values-v7+table-admission-projection-v3`, `wdc5-values-v7+table-admission-projection-v4` (historical for Effect since D108).

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

Two things are deleted: one build's file catalog when it is freed ([D085](DECISIONS.md)), which no other build uses, and superseded Creature appearances with the VMA1 files no remaining appearance names ([below](#superseded-creature-appearances)). There is no garbage collection of other shared objects yet ([#65](https://github.com/aircan80/vibeminer/issues/65)). That includes what profiles gathered before [D112](DECISIONS.md): one Creatures projection, its admission and the table admissions, and a geometry locator and appearance per viewed model, for every launch, all bound to references no longer current. When it arrives, release references first and reclaim only objects unreachable from every retained build, artifact, pin, active job and read lease. No naive per-build directory delete, and freed SQLite pages are not returned filesystem space.

### Superseded Creature appearances

[GeometryAppearanceCleanup](../src/backend/VibeMiner.Infrastructure/CreatureGeometry/GeometryAppearanceCleanup.cs) ([D103](DECISIONS.md)) runs once after start. A `geometry_appearances` row is superseded when its `key_revision` is empty (written before version 21) or differs from the current one, a hash of every fixed input of the appearance key and of the geometry locator key inside it. Such a row can't be reached by any lookup this engine makes.

- **Naming.** Rows from before version 21 first get their file (`artifact_sha256`, `artifact_bytes`) from the stored descriptor's manifest, 16 rows per catalog operation. Until a row is named it counts as naming every file. A descriptor over 64 KiB, or one without a readable manifest file, is marked (`artifact_bytes` -1) and its row kept for good, so whatever file it uses keeps a row; it no longer stands for every file.
- **Files first.** Then, for each superseded row in rowid order: if no row names its file, the file is deleted, then the row. A file another row names stays. "Another row" includes the row itself once it is no longer superseded: a Prepare can make a pre-21 row current after the batch was read. A row whose key is not 64 hex digits (an oversized one is never read) or whose recorded file is malformed is skipped and logged; the rest of the pass goes on.
- **In use.** A VMA1 file is in use while an appearance stream is open (a geometry read lease, or Read's verification) and while a Prepare's publication holds it, from before the file is written until its row commits or the publication ends. Cleanup deletes only after marking the file as being deleted, which fails while it is in use; a use that starts meanwhile waits until the delete has finished and then finds the file or not. The reference check runs again inside that window. A file in use, with another length at its path, a link or one that can't be deleted keeps its row, and the pass is retried after 30 s, at most 8 times.
- **Crash.** File then row: a crash in between leaves a superseded row whose file is gone, which Read reports as `notPrepared` and the next run deletes. Nothing is ever left that a row would need.
- **Bounds.** Each catalog operation reads or changes at most 16 rows; the store deletes one file at a time under its SHA stripe.
- **Accounting.** Freed files and rows leave the data folder, so `settings.storage.measure` drops by them. The per-build prepared data list has no geometry figure, so nothing is counted there.
- A row whose current-revision key is no longer reached for another reason (a newer CreatureDisplayInfo admission, say) is not detected; such rows are bounded by the displays prepared.

## Known limitations

- CAS publication of staged files does not fsync the file or its directory; small `PublishAsync` writes use write-through but no directory fsync. Durability across power loss is not qualified.
- Generic CAS publication does not repair a corrupt native or derived artifact at an existing SHA path. Such artifacts are reported as corrupt; only the encoded-object repair path replaces bytes.
- Reuse assessment covers a bounded set of metadata receipts, not every object in the store. Bulk receipts will need paged or change-sequence assessment.

## Durability and performance

Start from correctness-preserving durable settings; don't disable synchronization, foreign keys or verification to hit a benchmark. Measure transaction size, redundant indexes, compact keys and query plans before blaming the language.

The Java authored EN benchmark exceeded one million inserts and showed material batching/index effects. Its numbers shape workloads; they are not .NET performance claims. See [MIGRATION.md](MIGRATION.md).
