# Electron and C# engine protocol

Discovery v1 is implemented in [the canonical contract](discovery-v1.md), [JSON schema](discovery.schema.json) and generated TypeScript projections. Real host/desktop compatibility checks exercise it. The C# application layer remains independent of serialization and Electron types; the host translates messages into use cases.

## Transport and ownership

Electron main launches one known bundled engine executable and manages its lifetime. Use a versioned handshake followed by UTF-8 newline-delimited JSON for bounded control messages as the starting point. Standard input/output are protocol-only; diagnostic logging uses stderr. Implement bounded incremental framing rather than an unbounded line read.

The handshake advertises protocol compatibility and engine capabilities. Refuse incompatible versions explicitly. The general control-frame limit is 1 MiB; some methods use smaller limits. Reject oversized or malformed messages before unbounded allocation. A protocol frame is not permission to pass arbitrary paths, URLs, SQL, or process commands.

## Message categories

| Message | Required information |
| --- | --- |
| Request | Version, request ID, operation name, validated payload, exact context/generation where applicable |
| Reply | Matching request ID and either a typed result or structured error |
| Job accepted | Request ID, stable job ID, exact context, initial observable state |
| Job progress | Job ID, context/generation, sequence, stage, measured unit/counters |
| Job terminal | Job ID, one succeeded/failed/canceled outcome, typed result/error |
| Cancel request | Consumer/job identity and exact scope; acknowledgement differs from actual retirement |
| Shutdown | Stop admission, retire/cancel owned work, commit valid checkpoints, close resources, exit |

Discovery refresh keeps its request active, emits bounded request-correlated progress and returns one terminal success/error; it does not introduce a general accepted-job service. A cancellation acknowledgement never fabricates completion. Future shared jobs separate producer/consumer identities and return accepted-job/terminal events as needed. Retries of state-changing requests need explicit idempotency behavior; do not automatically replay mutations after an engine restart.

## Data representation

Use canonical strings for hashes, opaque IDs, and exact wide integers that JavaScript cannot safely represent as numbers. Define signedness/width at the field/schema level; do not silently truncate values. Timestamps carry explicit UTC offsets. Errors contain stable codes, retryability, stage, safe context, and an optional diagnostic reference, not raw exception text as control flow.

Validate messages at runtime on both sides. Define one canonical schema and generate or verify C#/TypeScript projections during bootstrap; maintain compatibility tests and authored examples. Do not maintain two independent handwritten definitions that silently drift.

Use bounded cursor-based pages for catalogs/tables. Do not send complete inventories, per-row progress events, base64 textures, or per-frame animation data over control JSON. Binary data travels through engine-approved handles served by main-owned custom protocols: `vibeminer-image` for [item icons](item-icons-v1.md) and `vibeminer-geometry` for [Creature geometry](creature-geometry-v1.md), whose bytes use the [VMG1 format](geometry-vmg1-v1.md).

## Failure and acceptance

Handle partial frames, malformed JSON, unknown operations, oversized arrays/strings, version mismatch, unsolicited/duplicate/stale replies, bounded event pressure, engine death, and shutdown races. UI consumers fence results by request generation and preserve current build selection.

Bootstrap acceptance includes real child-process handshake, one successful use case, typed failure, progress/cancellation, protocol/log separation, and cleanup on normal/unexpected exit. No HTTP listener or external local service is required for the initial design.

Production build opening and bounded object pages use [download-size Home](download-v1.md). Legacy [configuration admission](workspace-v1.md) and [full research inventory](inventory-v1.md) remain separately scoped compatibility capabilities.

[Tables definitions](tables-v1.md) provide local pinned provider catalogue and exact-build candidate structures, with source/row readiness explicitly separate.

[Table data](table-data-v1.md) adds explicit demand preparation, verified local readiness and bounded exact artifact row pages with enUS scope, source receipts and coverage.

[Settings](settings-v1.md) are app-wide values the engine saves in its data folder, plus a cancellable measurement of that folder's size and of each opened build's prepared data, and freeing one build's file catalog.

[Table reference text](table-reference-text-v1.md) adds batched, local text for declared references on one row page, from a reviewed allowlist.

[Table follow](table-follow-v1.md) lists the target rows of one declared reference by exact target column value.

[Item facts](item-facts-v1.md) is the reviewed, source-attributed projection of one item's Item and ItemSparse rows (`items.facts`) and its label lists (`items.labels`).
