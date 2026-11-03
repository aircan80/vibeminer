# Headless ENCODING inventory publication

Issue #224 starts capability B03 with explicit publication of an opened build's ENCODING EKeys. It uses the isolated mixed library and never selects the current app profile or starts work automatically. Metadata opening remains independent of inventory work.

## Identity and boundaries

`EncodingInventoryContext` validates and freezes the established canonical BuildIdentity v2 and matching BuildRef product/BuildConfig. It captures the exact selected route, ENCODING content key and expected encoded-row count. Region and CDNConfig stay outside canonical ownership. The ENCODING content key is source provenance in the operation context/result, not a separately committed cache receipt.

Builds owns the publisher and the narrow `IEncodingInventoryStore`/write port. Composition's `LibraryEncodingInventoryStore` binds writes to canonical build Id, Build scope, Original role and producer `encoding-ekeys-v1`. Builds never queries the library database. The source and library remain borrowed resources; publication retains its source lease until accepted work drains.

## Streaming and publication

`IEncodingRowSource` exposes a lease over immutable opened ENCODING data. The real source streams sorted EKeys and rechecks the ENCODING page checksum. It does not use the materialized Downloaded ledger, resolve files, acquire objects, read payload bytes or prepare tables.

The publisher validates unique increasing keys and exact total count, appending at most 1024 namespace-zero Encoded keys per batch. Only complete validated enumeration may publish. A supported empty source publishes measured empty intent; unavailable or unsupported sources cannot turn into empty success. Expected keys may be absent from the committed object index.

Each explicit request starts a fresh generation. Failed refresh, cancellation, source corruption and append failure before publication leave the previous active generation authoritative. Supersession fences older work through cancellation and inventory generation tokens. Once actual publication commits, the operation reports its real outcome; later cancellation cannot imply rollback. Expected intent never assigns physical membership or changes BuildRegistry metadata-load state.

## Resources and lifecycle

The publisher bounds active work at two and admitted work at sixteen; options can lower these limits. Declared encoded counts must be between zero and 50 million. Full admission fails explicitly. Each publication exposes a completion task and bounded latest-only progress with processed/total EKeys; it does not queue callbacks or retain an unbounded progress history. Results distinguish Published, Reused, Canceled, Superseded and Failed and retain their exact context. Explicit reuse results also carry their bounded source descriptor.

New work for a canonical build supersedes older work. Completion waits for source, write and cancellation cleanup. Cancellation callbacks run outside publisher locks, with at most one observed cancellation task per admitted request; a callback failure cannot discard a replacement request’s handle. Shutdown rejects new requests and drains accepted work without closing the borrowed mixed library or unrelated consumers. Progress reports ENCODING key work only, never downloaded bytes or full-build readiness.

## Explicit completed-source reuse

Issue #225 adds `StartReuse` without changing the fresh `Start` path. Its descriptor uses format `encoding-ekeys-source-v1`, revision `encoding-ekeys-r1`, the exact ENCODING CKey and expected EKey count. Composition maps it to the generic source descriptor for the same canonical Build/Original/encoding producer. Descriptor, generation completion and active pointer commit together; no separate cache receipt is written.

A matching active descriptor returns Reused with the actual generation before opening a source lease or enumerating rows. Missing proof or changed CKey/revision/count publishes a new complete generation with its descriptor. Route and locale differences do not invalidate an all-locale ENCODING inventory for the same canonical owner/source. Older inventory metadata requires the explicit source-format upgrade; it is never silently migrated by the publisher.

Before matching, reuse drains all older still-live requests for the same canonical build. This wait is bounded by admitted requests and holds neither a worker permit nor the coordinator/Begin gate; unrelated builds can proceed. An older entered commit reports its real result before the newer request decides whether its source still matches. The match has a read-snapshot linearization point. Cancellation and the latest-request fence reject a superseded response before accepting it; a later independent publication may replace a generation already returned. If publication acknowledgement fails, the adapter may reconcile only an active matching descriptor whose generation equals its own attempted generation. Another matching generation cannot confirm that attempt.

[Object-store section 17](formats/object-store-v1.md#17-atomic-inventory-source-descriptors-225) defines generic bounds, checksum and refusal. Reuse says nothing about current payload verification, complete producer discovery or physical retention.

## Remaining B03 scope

This slice provides explicit fresh publication. Fresh publication and explicit completed-source reuse are available headlessly. Automatic inventory scheduling, preparation composition and app adoption remain separate. No separate receipt, preservation/import, retention policy or garbage collection is introduced.
