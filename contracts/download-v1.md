# Download-size Home and object query v1

Production `workspace.open` uses `home-download-v1`. Its exact product/build/source request and normal cancellation, progress, detach/reattach and stale-result rules remain unchanged. This profile replaces the universal full-research-inventory gate; the older configuration and inventory profiles retain their original meanings.

The engine authenticates the build/CDN configurations and published download manifest (DL). A direct, sized declaration avoids EN entirely. Otherwise it authenticates EN and uses its native page directories to resolve only the DL source. It never requires ROOT, IN or DS catalog expansion or interrupted-catalog cleanup before Home.

## Admission and size

Successful measured admission has readiness `partial`, metadataState `verified`, inventoryState `notPrepared`, retentionState `measured`, inventory null, downloadProblem null and a `download` summary. Metadata contains the DL source receipt and optional EN bootstrap receipt. Research locale and platform scope remain null.

`download` contains immutable ledgerId (SHA-256), scope `downloadManifestAllVariants`, exact decimal-string objectCount, totalEncodedBytes, verifiedEncodedBytes, remainingEncodedBytes, metadataEncodedBytes and an explicit assessedAt timestamp. Full EKeys are deduplicated; duplicate conflicting lengths fail. Verified plus remaining equals total. Metadata overhead counts distinct retained physical source/configuration bytes separately.

The total describes the complete authenticated DL's encoded-object scope across its published variants. It is not decoded or installed size, a ROOT/IN/DS union, a selected locale/platform footprint, available upstream bytes or a scheduled download. The initially supported local assessment re-verifies matching retained metadata objects. Payload retention is the separate [retention-v1](retention-v1.md) capability. Credit is dated evidence, not a continuously refreshed physical-cache claim.

An absent or authentic but unsupported DL opens a partial overview with configurationsVerified, inventory notPrepared, retention unknown, download null, metadata null and typed downloadProblem. It does not substitute a zero-byte build. Corrupt bytes, malformed supported grammar, conflicting sizes, missing required declared sources or storage failures remain typed failures.

SQLite v6 stores source/artifact/admission receipts and a bounded assessment, not a row for every DL membership. The immutable sorted ledger is published separately in the app-owned verified object store before its registration/admission transaction. Existing histories, metadata sources, legacy catalogs and checkpoints survive migration. File publication and SQL commit are separate recoverable operations.

### File list (D066)

Open admits the DL ledger only. Every file ROOT lists outside it is measured afterwards in the background ([async](../docs/ASYNC-AND-JOBS.md#root-scope-worker)): the engine reads ENCODING and ROOT, adds for every ROOT-listed file whose CKey has no EKey among the DL's members the first EKey ENCODING lists for that CKey with its exact encoded size, and publishes the DL ledger extended with those objects. From then on that ledger stands for the admitted one in stored figures and Download all plans; the Open snapshot, its object pages and the retention target keep naming the admitted DL ledger. ROOT content keys that ENCODING does not map (or whose EKey has no encoded size) are counted as `unresolvableRootContent`; they are not in the scope and are not missing. A file list that cannot be read keeps the scope `downloadManifestAllVariants` and records why.

## Local object pages

`download.read` accepts exactly `{productId, buildIdentity, sourceRegion, ledgerId, limit, after?}`; limit is 1..100 and after is a canonical nonnegative ordinal decimal string. Requests cannot provide paths, SQL or URLs. The engine validates the exact admitted context, ledger bytes and bounded saved reuse assessment without HTTP.

DownloadPage echoes that context, ledgerId and assessedAt, plus entries, totalCount and nextCursor. Entries have contiguous one-based ordinal id, full encodingKey, exact decimal encodedBytes and verifiedAtAssessment. A download object has no invented FileDataID, name, CKey or decoded size. nextCursor is the last returned ordinal only when more rows exist. Empty/terminal pages use null.

The renderer pins pages to the opening summary's ledger, count and assessment time. A newer valid admission for the same ledger requires reopening the overview rather than mixing old totals with newer row flags. Main-process validation distinguishes malformed protocol output from that legitimate stale-view recovery.

## Stored figures

`download.summaries` (capability `download-summary-v1`, [D065](../docs/DECISIONS.md)) accepts exactly `{builds:[{productId, buildIdentity}]}` with 1 to 100 distinct exact builds and returns `{summaries:[...], revision}` with the summaries in request order. It reads one local catalog snapshot; it never writes, transfers, counts or checks. The desktop reads the 500-build window in five requests.

Each summary has `state`: `notOpened` (no Open was admitted), `notMeasured` (opened without a measured DL, `download` null) or `measured`. A measured `download` names the newest admitted ledger of the build and every `sourceRegions` entry that admitted the same ledger; they share one figure. `objectCount`/`totalEncodedBytes` are the whole scope ([D066](../docs/DECISIONS.md)): the manifest plus every ROOT-listed file outside it, every locale and platform, and `unresolvableRootContent` the ROOT content keys left out because they cannot be resolved. `scopeState` is `measuring` until that file list is measured after Open (the figure is then `counting` and the scope still `downloadManifestAllVariants`), `measured` once the scope is `downloadManifestAndRootAllVariants`, or `manifestOnly` with a typed `scopeProblem` when the file list could not be read (`retryable` when the next Open retries it). `storedObjects`/`storedBytes` are verified stored members (exact decimal strings): never more than the scope, all objects only with all bytes. Open's metadata counts only when it is itself a build file such as ROOT. `changesPending` is true while verified changes are recorded that the engine has not applied to this figure yet, so it may still move (never with `unavailable`). `countState` is `counting` while content already on disk is first counted, `current` afterwards, or `unavailable` with a typed `countProblem` when the ledger could not be read. `presence` is the latest dated presence check of this ledger's Download all jobs (one in progress first, then the newest complete one, then an interrupted one): `notAssessed`, `assessing`, `assessed` or `interrupted`, with `assessedAt` only when assessed, and the missing and corrupt counts it found. Those objects are already subtracted from the stored figure: the check reads every receipt kind (payload, table content and metadata), so a member stored only as content or metadata is corrected like any other. Only Download all runs presence checks; a missing file that no check has seen still counts until one does. `downloadAll` is true while any job for the ledger is in unpaused Download all; `running` while one is preparing or running.

The renderer shows a percentage only for `current`, rounding down, with 100% only when every member is stored. It re-reads at most once a second, without overlap, while a figure is `counting`, `running` or has `changesPending`, about two seconds after a failed read, when the engine reports a change (below), and when the window, visibility or saved intents change.

### Change waits

`revision` ([D077](../docs/DECISIONS.md)) is `<16 lowercase hex>-<decimal>`: the engine run's random epoch and a count of events that can change a figure that is not already moving, which are a stored change recorded (a receipt, corruption or absence) and a ledger admitted. It is taken before the read, and it is compared for equality only.

`download.changes` (same capability) accepts exactly `{after}`, a revision, and returns `{revision}`: as soon as the current revision differs from `after`, or after at most 25 seconds with the same one. It never reads the catalog, writes, transfers or counts, and it ends on `request.cancel` (`CANCELED`) or shutdown. A revision from another engine run answers at once.

Once no figure is moving, the desktop keeps one wait open with the revision of its last read and cancels it when the figures are no longer shown. A new revision starts a read, no sooner than a second after the previous one; the same revision starts the next wait. A non-retryable refusal stops the waits and keeps the last figures; another failure reads again after about two seconds.

## Current bounds

The original ledger builder uses one serialized worker, bounded admissions, approximately 8 MiB sorting chunks, 32-way external merging, a 1 GiB staging budget and a ten-million-row ceiling. Records are 24 bytes (EKey16 plus Int64 length), with a 32-byte versioned header. Saved format validation is bounded to sixteen descriptors; physical SHA verification still runs on read. Assessment accepts at most 10,000 retained candidates and preparation caps decoded reuse verification at 2 GiB. These are explicit initial limits; bulk game retention needs a paged assessment/change-sequence design before exceeding them.

Native EN lookup and targeted ROOT scanning are backend foundations for later feature dependency resolution. They do not implement Items, DB2 interpretation, payload downloading, archive location or foreground-priority scheduling.
