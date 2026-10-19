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
