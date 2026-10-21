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
