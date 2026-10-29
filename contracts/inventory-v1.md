# Published inventory Home and file query v1

Legacy full research-catalog capability. `workspace.open` retains its exact three parameters and lifecycle, but production opening now uses [download-size Home](download-v1.md). `home-inventory-v1` still means a complete sealed supported ROOT/IN/DL/declared-DS catalog; its older readiness is never reinterpreted as a narrower DL measurement.

## Shared result

The existing WorkspaceSnapshot gains nullable/default-null `inventory` and `metadata` fields for old configuration adapter tests/compatibility. The new result requires:

- preparationProfile `home-inventory-v1`, readiness `partial`, metadataState `verified`, inventoryState `ready`, retentionState `measured`.
- downloadPolicy onDemand; researchLocale/platformScope null. Inventory scope is explicitly `allPublishedVariants`; region is acquisition context, not locale selection.
- inventory: InventorySummary (Domain/Inventory.cs), including generation, preparedAt, scope, raw membership/root counts, distinct content/download-object counts, logical decoded bytes, exact expected encoded bytes, verified encoded bytes, remaining encoded bytes, shared verified encoded bytes and separately retained metadata encoded bytes.
- metadata: full BLTE source receipts for required encoding/root/install and declared download/size. Exact CKey/EKey/physical SHA, actual encoded/decoded lengths, original URL/retrieval and current verification/parser revision.
- existing exact build, pinned sourceReference and both configuration receipts remain required/coherent.

All potentially large counts/lengths are nonnegative decimal strings. Totals are checked arithmetic. `verifiedEncodedBytes + remainingEncodedBytes == totalEncodedBytes`; sharedVerifiedEncodedBytes ≤ verifiedEncodedBytes. Physical metadata overhead is separate from game-payload accounting, even when an inventory happens to reference one metadata object too.

The logical ledger covers complete ROOT+IN unique CKeys only: uniqueContentCount and logicalDecodedBytes must be captioned “Unique ROOT/IN contents” and “ROOT/IN decoded bytes.” DL/DS sources declare encoded identities, not CKeys. Their projections may remain encoded-only; a nullable CKey is not a claim that the separately retained EN graph has no association. Optional reverse enrichment is not required for exact encoded accounting. All EN forward/reverse facts remain retained, and required ROOT/IN content mappings must be resolved. Download ledger deduplicates exact full EKeys: explicit DL and uniquely resolved DS expectations remain exact; ROOT/IN-only CKeys add one reviewed deterministic complete EN representation (prefer an already exact expected candidate, then verified retained candidate, then lexicographically first full candidate with known consistent size). Different advertised encodings of the same CKey remain separate exact download expectations. Never infer a file inventory from all EN rows. Preserve raw source ordinals, flags/tags/variants. DS missing/ambiguous prefix mappings and required conflicting sizes block sealing; no zero-padding/first-match guess.

Known encoded lengths need no payload HEAD/index scan. Retained accounting requires verified app-owned bytes, full exact aliases and proof time; missing/corrupt objects do not count. Payload acquisition and location/availability assessment remain deferred. Required metadata can use an admitted loose source; unavailable archive-only metadata fails explicitly until its bounded locator path is implemented.

## Local query

`inventory.read` takes exactly `{productId, buildIdentity, sourceRegion, generation, limit, after?}`. Exact identity/source validation as workspace.open; generation is a positive decimal string, limit1..100, optional after is a nonnegative decimal source-row cursor. No URLs, SQL, paths, filters or unbounded searches. The backend resolves the exact READY generation/context; old/missing/mismatched generation fails without substituting current data or issuing HTTP.

InventoryPage echoes productId/buildIdentity/sourceRegion/generation, returns ≤limit entries, totalCount and nextCursor (decimal or null). Each InventoryEntry has id, family(root/install/download/size), name nullable, fileDataId nullable decimal, contentKey nullable full32hex, encodingKey full32hex, decodedBytes nullable decimal, encodedBytes decimal, retained boolean, localeFlags/contentFlags nullable decimal and nameHash nullable decimal. A row is an original membership, not necessarily a unique file/content/download object. ROOT rows show FileDataID; IN names are actual source names; encoded-only memberships show their identities without invented names. Preserve additional raw details in SQLite, not every bitmap over IPC. Per-page projection stays bounded below existing frame ceiling.

Home shows catalog memberships, unique content/download-object counts and exact scope with encoded size/verified-local/remaining figures. Files opens the real paginated local catalog. Download all and unsupported research actions remain disabled. No remaining-size value is a promise of upstream availability, selected-platform install footprint or a download already scheduled.

## Files

`inventory.read` stays as it is. Files uses two separate bounded methods over the same exact READY generation ([D068](../docs/DECISIONS.md)); capability `inventory-files-v1`. Schemas: `InventoryFilesParams`, `InventoryFilesPage`, `InventoryFileParams`, `InventoryFileDetail`.

A Files row is one ROOT FileDataID (`root:<FileDataID>`) or one exact install name (`install:<first membership ID>`). ROOT rows come first in FileDataID order, then install rows in manifest order. Download and size manifest memberships are encoded objects, not files, and are not listed. ROOT has no names, so `name` is null for ROOT rows; install rows carry the actual install name.

Community names ([D105](../docs/DECISIONS.md)): every row has `communityName`, a name from wow-listfile for a ROOT FileDataID while the `communityListfile` setting is on and a verified copy is kept, otherwise null; install rows always have null. It is a label beside the key and FileDataID, never a filter, cursor or identity. Each page and `inventory.file` reply has `communityNames`: `{state:"off"|"notReady"|"failed"|"unreadable"|"ready", tag, publishedAt, fetchedAt}`, the release only when `ready`. `off` also stands for an engine without a listfile. `notReady`: on, no verified copy yet and a download on its way. `failed`: on, no verified copy, the last attempt failed and nothing runs now. `unreadable`: on, but the kept names couldn't be read for this page; the page has no names and never fails for it. A page read again after the switch changes has the same rows with or without names; the first page read with the switch on and no copy starts the download (`notReady`).

Filters are optional flat fields. Each one tests a single membership, and a row is listed when at least one of its memberships passes all of them:

- `fileDataId`: exact ROOT FileDataID, 0..4294967295.
- `contentKey`: the full lowercase CKey, or a prefix of 4 to 31 hex digits. A prefix that matches more than 16,384 memberships fails with `INVENTORY_FILTER_BROAD`; the full key always works. Not combined with `fileDataId`.
- `localeFlags`: raw nonzero mask; a membership matches when its locale flags share a bit.
- `contentFlags`: `none` (derived content flags are zero) or a raw nonzero mask whose bits must all be set. Derived flags are `first | second | (third << 17)`.
- `family`: `root` or `install`. Install memberships have no ROOT locale or content flags, so either flag filter excludes them.
- `storedOnly`: the membership's resolved EKey was verified locally when the catalog was last sealed.

There is no platform filter. Content flags are raw facts and no platform is inferred from them.

`inventory.files` takes the context, `limit` 1..100 and optional `after` (a row key, or `install:0` for "after every ROOT row"). A file has at most 4,096 memberships: sealing refuses a catalog with a larger ROOT FileDataID or install name (`INVENTORY_LIMIT`). A request stops at the first file boundary after it has read 65,536 membership rows, so it reads at most 90,112 (65,536 plus 24,576: the rest of one file, the variant-stream refills meanwhile and that file's summary). A CKey prefix also counts at most 16,385 index entries before it is refused. A page that stops at that bound before it is full has `scanLimited: true` and a `nextCursor` that continues the search; it says nothing about the rows it did not reach. `nextCursor` is null only when the search reached the end. `totalCount` is the exact number of rows in the chosen family when no other filter is set, and null otherwise.

Each row has `membershipCount` (all memberships, not only matching ones), `matchingCount`, `contentKeyCount`, `encodedBytesMin`/`encodedBytesMax` over resolved memberships, `unresolvedCount` (memberships without an EKey; never sized as zero), `localeFlags` (union of the raw ROOT masks; null for install) and `storedCount`. Every count is exact, `contentKeyCount` included.

`inventory.file` takes the context, `file` (a row key), the same filters, `limit` 1..100 and optional `after` (membership ID). It returns `membershipCount`, `matchingCount` and memberships in ID order. Each membership has its family, ROOT block and entry ordinals or install ordinal, raw locale and derived content flags, the three raw content flag parts, name hash, CKey, EKey, decoded and encoded lengths (null when unknown), `stored`, and `matches` against the filters. A key with no memberships fails with `INVENTORY_FILE_NOT_FOUND`. Each request reads the file's memberships once to count them (at most 4,096) and then at most `limit` + 1 rows of the page.

Both pages stop below 750,000 serialized bytes. Neither method issues HTTP or changes local data.

## File catalog after Open

Production Open admits the download manifest only. The build's ROOT and install memberships are then prepared in the background into an inventory generation of profile `files-root-install-v1`, stored in its own file ([D071](../docs/DECISIONS.md)); capability `file-catalog-v1`. Schemas: `FileCatalogParams`, `FileCatalogStatus`, `FileCatalogSummary`, `FileCatalogProblem`, `FileCatalogBuild`, `FileCatalogList`, `FileCatalogSetParams`.

`inventory.catalog` takes exactly `{productId, buildIdentity, sourceRegion}` and reads one row; it never starts a transfer, count or check. It returns the context, the build's `buildVersion` and:

- `notOpened`: the build was never opened through that region.
- `pending`, with `stage`: `queued`, `paused` (the build's Download all is paused), `acquiring` (configurations, ENCODING, install and ROOT through the shared pipeline at background priority), `waiting` (for the ROOT scope measurement, which holds the same memory gate), `reading`, `writing` (with `completed` and `total` memberships) or `sealing`. `waitingFor` names the build a `queued` one waits for (the one being prepared now) or whose measurement a `waiting` one waits for; null otherwise. `attempts` and `problem` say a failed attempt is waiting to be retried.
- `stopped`: the user stopped it; the rows written so far are kept.
- `ready`, with `catalog`: the sealed generation and exact counts (ROOT FileDataIDs, install names, ROOT and install memberships, unique contents, unresolved and stored memberships).
- `failed` after three attempts, or at once for `STORAGE_FULL`, or `unsupported` when the file list cannot be read (format, size or a bound), each with its `problem`: `code`, `message`, `retryable` and `detail`, the underlying storage or I/O cause when there is one. The next Open tries again. Files offers Retry and Reopen for a failed one and neither for an unsupported one.
- `removed`: freed with `settings.storage.free` ([settings-v1](settings-v1.md#prepared-data-per-build), [D085](../docs/DECISIONS.md)); no rows or files are left. The next Open queues it again; Files offers to prepare it again.

`inventory.catalogs` takes `{}` and returns `{catalogs}`: every pending, stopped or failed preparation, one per build, most recently opened first, at most 32, each a `FileCatalogStatus` for the region of the build's newest Open. Downloads & activity shows each as one task.

`inventory.catalog.set` takes exactly `{productId, buildIdentity, action}`. `stop` needs a `pending` one and leaves it `stopped`, cancelling it if it runs. `resume` needs a `stopped`, `failed` or `removed` one and queues it again, first in line; it continues after its saved rows (a removed one has none). An `unsupported` one is refused (`FILE_CATALOG_STATE`): reading the same bytes again cannot help. A build never opened is `FILE_CATALOG_NOT_FOUND`. The reply is the new status.

The catalog belongs to the exact build, whichever region prepared it; Files may read it through any region the build was opened in. It holds ROOT and install memberships only: no download or size manifest rows and no ENCODING table. Each membership's EKey is, as in the legacy catalog, a stored one when its CKey has several sized EKeys, otherwise the lexicographically first one ENCODING lists for it that has an encoded size (the one on-demand file acquisition fetches). A CKey ENCODING does not list, or whose EKeys all lack a size, stays unresolved: no EKey and no encoded size, never zero; a missing ENCODING entry also leaves the decoded size unknown. `stored` is the D065 stored rule for that exact EKey and size, checked when the row was written.

Files picks its generation from the workspace, never from both: a `home-inventory-v1` workspace reads its own sealed generation (only through its own region), any other workspace reads the build's ready `files-root-install-v1` generation from `inventory.catalog`. `inventory.files` and `inventory.file` take that one generation and read only its rows; a file catalog generation that is no longer the build's ready one, or whose file does not read back as it, is `INVENTORY_NOT_FOUND`. A catalog that becomes ready while Files shows the download objects is offered in the footer (**Show file list**), not swapped in under the user.
