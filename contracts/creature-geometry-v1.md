# Selected Creature geometry v1

Capability `creature-source-geometry-v1` turns one explicitly selected Creature display slot into a verified [VMG1 geometry artifact](geometry-vmg1-v1.md): the model's native vertex positions and the triangle edges of its main SKIN, as a line set. It is a diagnostic source-geometry view, not an appearance. Material, pose, visibility and appearance are always `notEvaluated`.

**Not wired into the app.** The Host, store, lease registry, main transport, renderer scene and React view exist, but `Program.cs` does not compose a `CreatureGeometryService`, so the capability is never advertised, and no route renders `CreatureGeometryView`. Requests to an ordinary Host return `CREATURE_GEOMETRY_UNAVAILABLE`.

Prerequisites: `creatures-v1` and `creature-display-metadata-v1`. The Host advertises this capability only when Creatures, display metadata, the geometry service and the lease registry are all composed. The desktop additionally requires every geometry bridge method.

## Methods

All selection requests use the flattened display-metadata selection: `productId`, `buildIdentity`, `sourceRegion`, `researchLocale` (`enUS`), `artifactId` (the Creatures C1 artifact, never a VMG1 ID), `creatureId` (canonical decimal string) and `slot` (JSON integer 0–3). Unknown or duplicate members are refused. No method accepts a FileDataID, display ID, model ID, manifest, receipt, path or URL from the caller.

| Method | Extra parameters | Result | Work |
| --- | --- | --- | --- |
| `creatures.geometry.read` | optional `expectedGeometryArtifactId` (string or null) | `CreatureGeometryResult` | Local only |
| `creatures.geometry.prepare` | none | `CreatureGeometryResult`, with progress | May acquire |
| `creatures.geometry.open` | required `geometryArtifactId` | `{leaseId, manifest}` | Local only |
| `creatures.geometry.close` | only `leaseId` (32 lowercase hex) | `{closed: true}` | Cleanup |

- **read** authorizes the selection, finds the exact retained artifact, fully re-verifies VMG1 bytes and runs the final catalog fence. It never prepares metadata, opens an asset session, scans ROOT/ENCODING or touches the network. No retained artifact means `notPrepared`. If `expectedGeometryArtifactId` is given and a different artifact is current, the reply is `CREATURE_GEOMETRY_STALE`, never the other artifact.
- **prepare** may first run the display-metadata prerequisite, then resolves, acquires and verifies the model and SKIN, builds and verifies VMG1, publishes it and registers it. An already ready selection is still re-authorized and re-verified.
- **open** requires the exact artifact ID, re-verifies like read and returns a lease over the verified bytes. A missing artifact is `CREATURE_GEOMETRY_NOT_PREPARED`, never an empty lease. Lease IDs are random 128-bit values.
- **close** retires the lease. An unknown or already retired ID succeeds. It runs on the general request path, so it can make progress while geometry publication slots are full.

Every geometry reply frame (envelope and newline included) is at most 65,536 UTF-8 bytes. A result that cannot fit returns `CREATURE_GEOMETRY_RESOURCE_LIMIT` instead of partial output. Schema and generated types: `CreatureGeometryResult`, `CreatureGeometryManifest`, `CreatureGeometrySource`, `CreatureGeometryMetadata`, `CreatureGeometryTarget`, `CreatureGeometryProblem`, `CreatureGeometryLeaseDescriptor` in [discovery.schema.json](discovery.schema.json). Authored examples: [creature-geometry-ready.json](examples/creature-geometry-ready.json), [creature-geometry-lease.json](examples/creature-geometry-lease.json).

## Result

| Field | Meaning |
| --- | --- |
| `context`, `artifactId`, `creatureId`, `slot`, `opening` | The original selection and its captured opening source reference |
| `policy` | Always `creature-source-geometry-v1` |
| `state` | See states below |
| `metadata` | Display-metadata state, Creature source and coverage, `selected` slot (or null when the Creature row is missing), up to two `targets` (CDI table `bfdaf9f1`, CMD table `7f695fa5`) with coverage, `matchCount` and row evidence, `absence`, `modelId`, `modelFileDataId`, `requiredTableId`, `appearance: notEvaluated` |
| `sources` | Exactly two (`model`, then `skin`) for `ready` and `noDeclaredTriangles`; empty for every other state |
| `manifest` | VMG1 manifest; present only for `ready` |
| `problem` | `code` (≤64 uppercase chars), `message` (≤512), `retryable`, `dependency` (`metadata`/`model`/`skin`/`artifact`), `target` (only for metadata problems) |

Each source record: `role`, `fileDataId`, `contentKey`, `encodingKey`, `encodedSha256` (of the original encoded bytes), `encodedBytes`, `decodedBytes`, `fullVerifierRevision` (`content-blte-v1`), `associationSha256`, `variantPolicy` (`all-root-variants-consistent-asset-v1`), `encodingLookupRevision`, `rootLookupRevision`, `encodingSelectionPolicy` (`lexicographic-complete-encoding-v1`). No URLs, routes or paths.

### States

| State | Meaning |
| --- | --- |
| `notPrepared` | No retained artifact; no problem. May include metadata already reached. |
| `metadataNotResolved` | The slot→CDI→CMD chain stops (zero/negative slot, missing or ambiguous target, metadata not prepared). Mirrors display metadata; no sources. |
| `ready` | Positive full chain, both sources verified, VMG1 manifest present. |
| `noDeclaredTriangles` | Same verified pair as ready, but the SKIN's sections declare zero triangle indices. No VMG1 file, no bounds, not cached as a negative. |
| `missingMembership` | The model or SKIN FDID has no ROOT membership. |
| `ambiguousVariants` | ROOT memberships for the FDID disagree on the CKey. |
| `encodingUnresolved` | The CKey has no ENCODING entry. |
| `protected` | BLTE encrypted; no full receipt. |
| `unsupported` | Model/SKIN outside the reader's finite profile, or an external skeleton/bone dependency. |
| `unavailable` | CDN unavailable. |
| `corrupt` | Hash, key, grammar or receipt conflict. |
| `resourceLimit` | A size, count or reservation limit was exceeded. |

Busy, stale authority, deadline and cleanup failure are request errors, not states: `CREATURE_GEOMETRY_BUSY`, `CREATURE_GEOMETRY_STALE`, `CREATURE_GEOMETRY_DEADLINE_EXCEEDED`, `CREATURE_GEOMETRY_CLEANUP_FAILED`, `CREATURES_BUSY`, `CREATURES_QUERY_BUSY`. Cancellation and busy outcomes are never cached.

### Progress

Prepare progress keeps the normal envelope (honest label, unit `stage`/`bytes`/`entries`, optional completed/total) and adds `geometryStage` and `geometryDependency`. Stages: `metadata`, `sourceContext`, `modelResolution`, `modelAcquisition`, `modelVerification`, `modelParsing`, `skinResolution`, `skinAcquisition`, `skinVerification`, `skinParsing`, `artifactBuild`, `artifactVerification`, `publication`. At most one update per 100 ms. No overall percentage.

### Float32 bounds

Manifest `bounds.min`/`max` are Float32 values sent as JSON doubles (exact widening). The shortest Float32 spelling (`0.1`) would parse in JavaScript as a different number than the Float32 value, and the renderer requires exact Float32 equality with the binary header.

## Source resolution

1. Re-authorize the selection through the display-metadata store: `Creature.DisplayID[slot]` → `CreatureDisplayInfo.ID` → `ModelID` → `CreatureModelData.ID` → positive `FileDataID`. Capture the original opening, Creature core, slot and delivered CDI/CMD bindings before any wait; later checks compare against these and never adopt a newer opening.
2. Open one feature handle on the shared `AssetResolutionSessions` registry and resolve the model FDID under `all-root-variants-consistent-asset-v1`: every ROOT membership must agree on one CKey, all memberships are kept (max 256). It never uses `SelectedFileAcquirer`, whose enUS table policy is a different source certificate.
3. Require encoded length 9 B–4 MiB and decoded length 1 B–8 MiB before any payload demand. Acquire through the shared pipeline at interactive priority, then strict BLTE decode with EKey, chunk, CKey and EOF verification. No partial projections, holes or zero fill.
4. Parse the model under the geometry-only profile (chunked MD21/MD20 version 274, no external skeleton or bone files) with M2 limits of 8 MiB source and output, 65,536 vertices, 131,070 triangle indices, 256 sections.
5. Use only `SkinFileDataIds[0]` (main SKIN ordinal 0). A missing or zero SFID[0] is unsupported. No guessed `00.skin`, other LOD, textures or animation files.
6. Resolve, acquire and verify the SKIN through the **same** handle with the same limits. Exactly two asset nodes.
7. Seal the closure, build VMG1 into ≤2 MiB staging, verify it, publish it to CAS, then run one final catalog transaction.

### Two digests

- `DataBindingSha256` is the VMG1 binding digest (manifest `bindingSha256`): normalized source keys, lengths, FDIDs and policy revisions. Different source contexts can share the same artifact bytes.
- `AuthoritySha256 = SHA256(UTF8("vibeminer-geometry-source-authority-v1\0") ‖ u32le(length) ‖ capsule)`, where the capsule is the canonical UTF-8 JSON of the original context, configs, memberships, receipts, resolver bindings, SFID0 and counts. Each source capsule is ≤128 KiB, the pair ≤256 KiB. It seals the locator; it is never compared with the binding digest and is not an artifact ID.

### Final fence

One short SQL transaction checks together: the original opening and current C1 artifact, both delivered CDI/CMD admissions, both source associations and canonical receipts, the EN/ROOT resolver bindings, `DataBindingSha256` and `AuthoritySha256`, and `object_invalidity` for every authorizing object (C1 projection, Creature/CDI/CMD rows and sources, configs, EN/ROOT and lookups, model/SKIN sources and the VMG file). It then inserts or validates the immutable `geometry_artifacts` row (unique `data_binding_sha256`) and `geometry_source_locators` row. A commit that wins a cancellation race stays committed.

Retained reuse: missing original model/SKIN CAS bytes do not block read/open if the canonical receipts and certificate are authentic, current and not known-invalid. Missing VMG bytes is `notPrepared` (read) or `CREATURE_GEOMETRY_NOT_PREPARED` (open). Same-size VMG corruption is `corrupt` and is recorded centrally.

## Resources and lifetime

| Owner | Bound |
| --- | --- |
| Engine payload ledger | 64 MiB total = 48 MiB producer + two 8 MiB read slots (local read verification, opening, held or draining leases). A third read is immediately busy; no queue. |
| Producer capture | 16 MiB child of the producer. Conservative retained-graph estimate per anchor: 128 KiB + raw versions + raw CDNs + 8× each serialized record, ≤2 MiB. |
| Source staging | 32 MiB aggregate; one decoded source (≤8 MiB) held at a time; ≤2 MiB VMG staging. |
| Producer | One per selection, ≤2 compatible consumers. A different selection is busy. |
| Service | ≤2 prepares, ≤2 local calls, ≤2 leases. Host shares the Creature preparation (2) and publication (4) credits. |
| Deadlines | Preparation 15 minutes per consumer. Read lease 30 seconds absolute, starting at admission, before the asynchronous open. |
| Lease stream | Seekable, read-only, at position 0, one read at a time, ≤64 KiB per read. |

**Known limit:** two maximum 1 MiB opening documents push the capture estimate past 2 MiB, so geometry for such a build returns `CREATURE_GEOMETRY_RESOURCE_LIMIT` before any acquisition.

Stop withdraws only that consumer. The last consumer's cancellation waits for the producer, staging and observers to retire. Cleanup failure keeps its reservation charged and closes geometry admission; deadlines never turn it into success. Shutdown stops admission, cancels, and awaits every open, late open and lease.

## Desktop transport

Main owns all engine geometry calls. The renderer never sees a lease ID, path or FDID.

Bridge (preload) methods: `creatureGeometryRead`, `creatureGeometryPrepare`, `geometryViewEnter`, `geometryViewOpen`, `geometryViewBodyRetired`, `geometryViewSceneDisposed`, `geometryViewRelease`, plus `onGeometryRetirement`. Main IPC: `desktop.geometry.enter/open/bodyRetired/sceneDisposed/release`.

Flow:

1. **Enter** reserves the view and blocks icon presentation, then drains icon bodies before returning.
2. **Open** checks the exact artifact against main's saved ready manifest and mints a one-use grant before calling engine `open`. Grant deadline: 30 s from minting.
3. The renderer fetches `vibeminer-geometry://body/<32 lowercase hex>` with `credentials: 'omit'`, `redirect: 'error'`, `cache: 'no-store'`.
4. Main opens the engine lease, copies the CAS object (`objects-v1/<sha[0..2]>/<sha[2..]>`, no symlinks, regular file, exact length) into one ≤2 MiB snapshot, verifies SHA-256 and the VMG1 header against the manifest, closes the file and lease, and only then answers 200. The body streams from the snapshot in 64 KiB windows.
5. **BodyRetired** returns a one-use acknowledgement after the body, file and lease have actually retired. Only that acknowledgement allows GPU upload.
6. **SceneDisposed** (`scope: 'scene'` or `'view'`) and **Release** retire the renderer side; icons unblock only after both sides are clean.

Body request rules: GET only; `Range` → 416; credential headers → 403; no query, fragment or aliases. Response: `application/octet-stream`, exact `Content-Length`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Access-Control-Allow-Origin: null`, `Vary: Origin`. Scheme privileges: standard, secure, `supportFetchAPI`, `corsEnabled`; no CSP bypass or service workers.

Desktop presentation budget: 16 MiB of reachable app buffers and graphics reservations (8 MiB canvas surfaces + 2 MiB each for snapshot, response, received buffer and margin while fetching). It is accounting, not a Chromium or driver memory cap.

### Electron 44 provenance gotchas

- `initiatorOrigin` for the app's `file://` document is exactly `"file://"` in both `webRequest` and `protocol.handle`, not `"null"`. The protocol `Request` has no `Origin` header. Main accepts only that spelling and absence. The `webRequest` check also requires `resourceType: 'xhr'`, method GET, the live main frame and the app session.
- Dedicated workers and inherited `about:blank` frames bypass the session `webRequest` listener. The CSP therefore sets `worker-src 'none'` and `frame-src 'none'`, and main latches the document as ineligible for geometry on any child `frame-created` event, even if the child is removed. Only a document reload clears the latch.
- CSP for binary fetch is `connect-src vibeminer-geometry:`; nothing else (no `file:`, `data:`, `blob:` or HTTP) is allowed.

Source: [CreatureGeometryWire.cs](../src/backend/VibeMiner.Host/CreatureGeometryWire.cs), [CreatureGeometryService.cs](../src/backend/VibeMiner.Application/CreatureGeometryService.cs), [CreatureGeometry/](../src/backend/VibeMiner.Infrastructure/CreatureGeometry/), [geometry-binary-protocol.ts](../apps/desktop/src/main/geometry-binary-protocol.ts), [geometry-provenance.ts](../apps/desktop/src/main/geometry-provenance.ts), [geometry-view-controller.ts](../apps/desktop/src/main/geometry-view-controller.ts).
