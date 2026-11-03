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

The publisher bounds active work at two and admitted work at sixteen; options can lower these limits. Declared encoded counts must be between zero and 50 million. Full admission fails explicitly. Each publication exposes a completion task and bounded latest-only progress with processed/total EKeys; it does not queue callbacks or retain an unbounded progress history. Results distinguish Published, Canceled, Superseded and Failed and retain their exact context.

New work for a canonical build supersedes older work. Completion waits for source, write and cancellation cleanup. Cancellation callbacks run outside publisher locks, with at most one observed cancellation task per admitted request; a callback failure cannot discard a replacement request’s handle. Shutdown rejects new requests and drains accepted work without closing the borrowed mixed library or unrelated consumers. Progress reports ENCODING key work only, never downloaded bytes or full-build readiness.

## Remaining B03 scope

This slice provides explicit fresh publication. Completed-source-version reuse after reopen is deferred: the source identity would need to be committed atomically with its generation before it could justify skipping work. No separate receipt, schema widening, automatic scheduling, app cutover, preservation/import, retention policy or garbage collection is introduced.
