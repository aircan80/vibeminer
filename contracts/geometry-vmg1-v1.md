# VMG1 geometry artifact v1

VMG1 is VibeMiner's own binary derivative of one M2 model plus its main SKIN: all native vertex positions and the triangle edges of every declared SKIN section, as line pairs. It is not a WoW format and not an appearance. No normals, UVs, bones, materials, textures, strings or row values are stored.

Used by [selected Creature geometry](creature-geometry-v1.md). Builder and verifier: [ModelGeometryArtifacts.cs](../src/backend/VibeMiner.Formats.Wow/GeometryArtifacts/ModelGeometryArtifacts.cs), [GeometryArtifactEncoding.cs](../src/backend/VibeMiner.Formats.Wow/GeometryArtifacts/GeometryArtifactEncoding.cs). Renderer admission: [geometry-vmg.ts](../apps/desktop/src/renderer/geometry-vmg.ts), [geometry-camera.ts](../apps/desktop/src/renderer/geometry-camera.ts). M2/SKIN source format: [docs/formats/m2-skin.md](../docs/formats/m2-skin.md).

## Fixed revisions

| Name | Value |
| --- | --- |
| Artifact revision | `model-geometry-vmg1-v1` |
| Edge policy | `all-sections-triangle-edges-v1` |
| Scope | `unfiltered-declared-sections` (code 1) |
| Coordinate policy | `native-m2-position-normal-uv-winding-v1` (code 1: native M2 XYZ, no transform) |
| Model reader | `m2-static-native-v1` |
| SKIN reader | `m2-skin-geometry-v1` |
| Interpretation policy | `skin-unfiltered-source-geometry-v1` |
| Evaluations | material, pose, visibility, appearance: all `notEvaluated` |

## Limits

| Count | Limit |
| --- | --- |
| Original vertices V | 1–65,536 |
| SKIN vertex-lookup entries K | 1–65,536 |
| Global triangle-index entries G | 3–131,070, multiple of 3 |
| Declared sections S | 1–256 |
| Summed section index entries Q | 3–131,070, multiple of 3 (overlapping sections count again) |
| Line-index entries L | exactly 2Q, ≤262,140 |
| File length | `128 + 12V + 4L + 32S`, ≤2,097,152 bytes |

The largest file the count limits allow is 1,843,312 bytes, so no valid file reaches the 2 MiB cap. Larger valid geometry is a resource limit; sections are never dropped. Q = 0 produces no file (the caller reports `noDeclaredTriangles`).

Each source (model and SKIN) must be ≤4 MiB encoded and ≤8 MiB decoded, and its decoded length must equal what the reader consumed.

## File layout

Little-endian throughout. Offsets are absolute. Header is exactly 128 bytes. Regions follow in order with no padding, no optional chunks, no compression and no trailing bytes. Every offset and length is determined by the counts; a stored value that disagrees is invalid.

| Offset | Size | Type | Value |
| ---: | ---: | --- | --- |
| 0 | 4 | ASCII | `VMG1` (`56 4D 47 31`) |
| 4 | 2 | u16 | format version `1` |
| 6 | 2 | u16 | header length `128` |
| 8 | 4 | u32 | total file length |
| 12 | 4 | u32 | scope `1` |
| 16 | 32 | bytes | binding SHA-256 (raw) |
| 48 | 4 | u32 | V |
| 52 | 4 | u32 | L, positive, divisible by 6 |
| 56 | 4 | u32 | S |
| 60 | 4 | u32 | K |
| 64 | 4 | u32 | positions offset = 128 |
| 68 | 4 | u32 | positions length = 12V |
| 72 | 4 | u32 | line-index offset = 128 + 12V |
| 76 | 4 | u32 | line-index length = 4L |
| 80 | 4 | u32 | section-directory offset = 128 + 12V + 4L |
| 84 | 4 | u32 | section-directory length = 32S |
| 88 | 12 | 3×f32 | bounds minimum XYZ |
| 100 | 12 | 3×f32 | bounds maximum XYZ |
| 112 | 4 | u32 | Q |
| 116 | 4 | u32 | coordinate code `1` |
| 120 | 4 | u32 | G |
| 124 | 4 | u32 | reserved, `0` |

**Bounds** are the tight min/max over positions referenced by section triangles only; unused vertices do not count. They must be finite with min ≤ max. Negative zero in the header bounds is written as positive zero. Min may equal max.

**Positions**: 12-byte Float32 XYZ for **all** V original vertices, in original order, with their exact source bits (negative zero kept). Every component must be finite, including unused vertices.

**Line indices**: u32, each < V. For each section in declared order, take its index range `TriangleStart .. TriangleStart + TriangleCount` in the global resolved index list, in groups of three. Each triangle `(a, b, c)` emits exactly `a, b, b, c, c, a`. Degenerate and duplicate triangles, empty sections and overlapping or duplicated ranges are kept. No welding, repair, batch selection or visibility filtering. Render the buffer as line pairs directly; no edge-expansion pass.

**Section directory**: 32 bytes per section.

| Offset | Size | Type | Meaning |
| ---: | ---: | --- | --- |
| 0 | 4 | u32 | ordinal, equal to the record index |
| 4 | 2 | u16 | section ID |
| 6 | 2 | u16 | Level |
| 8 | 4 | u32 | VertexStart in the SKIN lookup (not the position buffer), ≤65,535 |
| 12 | 4 | u32 | VertexCount in the SKIN lookup, ≤65,535 |
| 16 | 4 | u32 | effective TriangleStart in the global index list |
| 20 | 4 | u32 | TriangleCount (index entries, not triangles), ≤65,535 |
| 24 | 4 | u32 | first line-index ordinal for this section |
| 28 | 4 | u32 | line-index count = 2 × TriangleCount |

Rules: `TriangleStart >> 16 == Level`; TriangleStart and TriangleCount divisible by 3; VertexStart + VertexCount ≤ K; TriangleStart + TriangleCount ≤ G; a positive TriangleCount requires a positive VertexCount. Line ranges are contiguous cumulative sums from 0 and end exactly at L. The directory is provenance only; the lookup-to-vertex mapping was resolved in C# and is not stored.

## Binding digest and artifact ID

The binding SHA-256 hashes, in order:

1. ASCII `vibeminer-model-geometry-binding-v1` and one NUL byte.
2. Model source, then SKIN source, each as: CKey (16 bytes), EKey (16), encoded SHA-256 (32), encoded length (u64), decoded length (u64), verifier revision as a length-prefixed string. The verifier is always `content-blte-v1`.
3. Model FDID (u32), SKIN FDID (u32), main SKIN ordinal (u32, always 0). The SKIN FDID must equal the model's `SkinFileDataIds[0]`.
4. Length-prefixed strings: model reader revision, SKIN reader revision, interpretation policy, coordinate policy, artifact revision, edge policy.
5. Scope code (u32, 1), coordinate code (u32, 1), format version (u16, 1).

Hex values are hashed as decoded bytes. Strings are printable ASCII, 1–128 bytes, prefixed with a u16 length. Routes, timestamps and CAS locations are not part of the binding, so a source or policy change changes the digest and a Creature context change does not.

File SHA-256 covers every byte of the file. Artifact ID = lowercase hex of `SHA256(ASCII("vibeminer-model-geometry-artifact-v1") ‖ NUL ‖ binding digest (32) ‖ file SHA-256 (32) ‖ file length (u64))`. Same-length corruption must fail the SHA check.

## Builder and verifier

- `BuildAsync(skin, binding, output, token)` validates the binding, profile and limits before allocating, then writes sequentially through ≤64 KiB buffers. The output stream is caller-owned and provisional until success. It never fetches, opens paths or touches SQL.
- `VerifyAsync(source, expected, token)` checks the full SHA, header, binding, revisions, framing, finite positions, index/directory ranges, tight bounds and EOF in one sequential pass. It holds one position buffer of at most 786,432 bytes (12 × 65,536), reads ≤64 KiB at a time, never rereads consumed bytes, and reads at most the file length plus one EOF probe byte.
- Both have overloads taking an `IGeometryArtifactWorkBudget`; production uses them so each ≤64 KiB CPU slice runs under the shared CPU scope. Cancellation is checked at least every 4,096 items.
- The verifier cannot reconstruct the omitted lookup or global triangle list, and a valid file does not prove its source association. That authority comes from the caller's catalog.

## Control manifest

The engine returns this JSON object with each artifact. Every object rejects unknown keys; arrays have exact lengths; hashes are 64 lowercase hex.

| Member | Value |
| --- | --- |
| `artifactId`, `bindingSha256`, `sha256` | Artifact ID, binding digest, file SHA-256 |
| `byteLength`, `formatVersion`, `headerBytes` | File length, `1`, `128` |
| `scope`, `coordinatePolicy` | `unfiltered-declared-sections`, `native-m2-position-normal-uv-winding-v1` |
| `evaluations` | `{material, pose, visibility, appearance}`, each `notEvaluated` |
| `vertexCount`, `vertexLookupCount`, `sourceTriangleIndexCount` | V, K, G |
| `sectionIndexCount`, `lineIndexCount`, `sectionCount` | Q, L, S |
| `positions`, `lineIndices`, `sections` | `{offset, byteLength}`, matching the header equations |
| `bounds` | `{min: [x,y,z], max: [x,y,z]}`, equal to the header bounds; sent as doubles that exactly equal the Float32 values |
| `modelFileDataId`, `skinFileDataId`, `mainSkinOrdinal` | Positive u32, positive u32, `0` |
| `modelReaderRevision`, `skinReaderRevision`, `interpretationPolicy`, `artifactRevision`, `edgePolicy` | The fixed revisions above |

All counts, offsets and lengths are JSON integers, far below the safe-integer limit. The manifest is engine/main-owned; the renderer never sends one back as authority. Main verifies the file SHA-256 before serving bytes.

## Renderer admission and camera fit

The renderer receives one `ArrayBuffer` and the main-owned manifest. `validateGeometry(buffer, manifest, work)` returns `valid` or `refused` with `invalidArtifact`, `unsupportedEndian` or `cancelled`. It never hashes the buffer; SHA verification is main's job.

Admission:

- Refuse non-`ArrayBuffer`, resizable or detached input, length < 128 or > 2,097,152, and big-endian runtimes (checked with a 4-byte probe).
- Snapshot the manifest with own-key reads: exact keys, known policy strings, `notEvaluated` evaluations, safe integers, Float32-exact finite bounds with no negative zero. The snapshot prevents mutation during awaits.
- Header: magic, version 1, header 128, total length equal to manifest and buffer, scope 1, coordinate 1, reserved 0, all 32 binding bytes equal to `bindingSha256`, every count and region equal to the manifest and the equations, each region 4-byte aligned.
- Scan every position for finite Float32 (unused vertices included). Scan each six-index group for the `a,b,b,c,c,a` pattern with every index < V. Accumulate bounds over referenced positions and require exact equality with the header.
- Scan every directory record with the rules above.
- Work is charged by bytes examined (including repeated position lookups). Before a step would exceed 65,536 charged bytes, or after 4,096 items, yield a macrotask (`setTimeout` by default) and re-check cancellation and currentness. No workers, no per-vertex callbacks.
- Only after the full scan, create `Float32Array(buffer, 128, 3V)` and `Uint32Array(buffer, lineOffset, L)` on the same buffer. No copies, slices or header stripping. `sectionPage(offset, limit)` decodes at most 16 directory records on demand.

`geometry-camera-fit-v1` is an app viewing convention, not the creature's facing, pose or world size. Source data is never changed.

- Presentation rotation is native `(x, y, z) → (x, z, −y)` (determinant +1, winding kept), applied once as an exact axis/sign object matrix. Bounds transform to `[minX, minZ, −maxY] .. [maxX, maxZ, −minY]`.
- Admission: every nonzero bound component must be a normal Float32 (|v| ≥ 2⁻¹²⁶). Half extents h = (max − min) / 2, radius r = hypot(hx, hy, hz). Require r > 0 and normal, and max(|target|) / r ≤ 4,096. Zero-extent, subnormal, extreme or badly off-centre bounds return `cameraFitUnsupported`; the artifact stays valid.
- Perspective: vertical FOV 45°, aspect 4:3, no pan or roll. a = min(FOV/2, atan(tan(FOV/2) × aspect)). dMin = 1.10 r / sin(a), dMax = 8 dMin, initial distance 1.5 dMin. Initial yaw π/4, elevation π/6. Near = 0.5 (d − r), far = 1.5 (d + r); both positive and normal, near < far.
- Validation: build projection and view matrices in doubles, round to Float32, then project all eight bound corners with Float32-rounded arithmetic. Refuse if w ≤ 0, |x| or |y| > 0.999, z outside (−1, 1), or any term is non-finite or underflows. The scene installs exactly these matrices with automatic transform updates off.
- Actions: rotate by finite yaw/elevation deltas each within ±π; elevation clamps to ±80°, yaw wraps to [−π, π). Zoom by integer ticks in [−8, 8], factor 1.1^ticks (positive is farther), clamped to [dMin, dMax]. Reset restores the initial yaw, elevation and distance. An invalid action or an unfittable result leaves the current camera unchanged.

The scene ([geometry-scene.ts](../apps/desktop/src/renderer/geometry-scene.ts)) draws one `LineSegments` from the two typed views on a fixed 588×441 WebGL2 canvas (pixel ratio 1, no antialias, no alpha, depth on, no stencil), teal `#176e66` on `#fffefa`. It sets `frustumCulled = false` and `renderer.sortObjects = false` so Three.js never computes bounds over unused vertices. Upload happens only after main's body-retired acknowledgement.
