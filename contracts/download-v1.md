# Download-size Home and object query v1

Production `workspace.open` uses `home-download-v1`. Its exact product/build/source request and normal cancellation, progress, detach/reattach and stale-result rules remain unchanged. This profile replaces the universal full-research-inventory gate; the older configuration and inventory profiles retain their original meanings.

The engine authenticates the build/CDN configurations and published download manifest (DL). A direct, sized declaration avoids EN entirely. Otherwise it authenticates EN and uses its native page directories to resolve only the DL source. It never requires ROOT, IN or DS catalog expansion or interrupted-catalog cleanup before Home.

## Admission and size

Successful measured admission has readiness `partial`, metadataState `verified`, inventoryState `notPrepared`, retentionState `measured`, inventory null, downloadProblem null and a `download` summary. Metadata contains the DL source receipt and optional EN bootstrap receipt. Research locale and platform scope remain null.

`download` contains immutable ledgerId (SHA-256), scope `downloadManifestAllVariants`, exact decimal-string objectCount, totalEncodedBytes, verifiedEncodedBytes, remainingEncodedBytes, metadataEncodedBytes and an explicit assessedAt timestamp. Full EKeys are deduplicated; duplicate conflicting lengths fail. Verified plus remaining equals total. Metadata overhead counts distinct retained physical source/configuration bytes separately.

The total describes the complete authenticated DL's encoded-object scope across its published variants. It is not decoded or installed size, a ROOT/IN/DS union, a selected locale/platform footprint, available upstream bytes or a scheduled download. The initially supported local assessment re-verifies matching retained metadata objects. Payload retention is the separate [retention-v1](retention-v1.md) capability. Credit is dated evidence, not a continuously refreshed physical-cache claim.

An absent or authentic but unsupported DL opens a partial overview with configurationsVerified, inventory notPrepared, retention unknown, download null, metadata null and typed downloadProblem. It does not substitute a zero-byte build. Corrupt bytes, malformed supported grammar, conflicting sizes, missing required declared sources or storage failures remain typed failures.

SQLite v6 stores source/artifact/admission receipts and a bounded assessment, not a row for every DL membership. The immutable sorted ledger is published separately in the app-owned verified object store before its registration/admission transaction. Existing histories, metadata sources, legacy catalogs and checkpoints survive migration. File publication and SQL commit are separate recoverable operations.

## Local object pages

`download.read` accepts exactly `{productId, buildIdentity, sourceRegion, ledgerId, limit, after?}`; limit is 1..100 and after is a canonical nonnegative ordinal decimal string. Requests cannot provide paths, SQL or URLs. The engine validates the exact admitted context, ledger bytes and bounded saved reuse assessment without HTTP.

DownloadPage echoes that context, ledgerId and assessedAt, plus entries, totalCount and nextCursor. Entries have contiguous one-based ordinal id, full encodingKey, exact decimal encodedBytes and verifiedAtAssessment. A download object has no invented FileDataID, name, CKey or decoded size. nextCursor is the last returned ordinal only when more rows exist. Empty/terminal pages use null.

The renderer pins pages to the opening summary's ledger, count and assessment time. A newer valid admission for the same ledger requires reopening the overview rather than mixing old totals with newer row flags. Main-process validation distinguishes malformed protocol output from that legitimate stale-view recovery.

## Current bounds

The original ledger builder uses one serialized worker, bounded admissions, approximately 8 MiB sorting chunks, 32-way external merging, a 1 GiB staging budget and a ten-million-row ceiling. Records are 24 bytes (EKey16 plus Int64 length), with a 32-byte versioned header. Saved format validation is bounded to sixteen descriptors; physical SHA verification still runs on read. Assessment accepts at most 10,000 retained candidates and preparation caps decoded reuse verification at 2 GiB. These are explicit initial limits; bulk game retention needs a paged assessment/change-sequence design before exceeding them.

Native EN lookup and targeted ROOT scanning are backend foundations for later feature dependency resolution. They do not implement Items, DB2 interpretation, payload downloading, archive location or foreground-priority scheduling.
