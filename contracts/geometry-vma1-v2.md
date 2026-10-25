# VMA1 appearance artifact, format version 2

VMA1 is VibeMiner's own binary derivative of one Creature display's textured appearance: the model's native positions, normals when an env map needs them, UV0, the triangles of every drawn SKIN batch with its combiner, and one RGBA8 image per texture slot. It is not a WoW format. It is drawn instead of the [VMG1](geometry-vmg1-v1.md) line set when the display's texture chain is ready. Pose, visibility (geosets), colour and opacity animation, env-map UV animation, blended layers, particles, ribbons and lights are not evaluated.

Version 2 (#69, D094) adds the normals region, one- and two-stage combiners and 48-byte draw records. Version 1 (#63) had one texture per draw; its files are never read again, because the revisions below are part of the retained key.

Used by [selected Creature geometry](creature-geometry-v1.md#appearance). Builder and verifier: [ModelAppearanceArtifacts.cs](../src/backend/VibeMiner.Formats.Wow/GeometryArtifacts/ModelAppearanceArtifacts.cs). Batch admission: [M2Appearance.cs](../src/backend/VibeMiner.Formats.Wow/Models/M2Appearance.cs) ([m2-skin.md](../docs/formats/m2-skin.md#textured-appearance-profile)). Renderer admission: [geometry-vma.ts](../apps/desktop/src/renderer/geometry-vma.ts); shader: [geometry-combiners.ts](../apps/desktop/src/renderer/geometry-combiners.ts).

## Fixed revisions

| Name | Value |
| --- | --- |
| Artifact revision | `model-appearance-vma1-v2` |
| Material policy | `m2-one-or-two-stage-combiners-v1` |
| Batch reader | `m2-appearance-batches-v1` |
| Texture policy | `blp2-largest-mip-within-1024-rgba8-v1` |
| BLP decoder | `blp2-rgba8-v2` |
| Coordinate policy | `native-m2-position-normal-uv-winding-v1` (code 1) |
| Alpha reference | 128 of 255 (blend mode 1 with `Combiners_Mod`) |

## Limits

| Count | Limit |
| --- | --- |
| Vertices V | 1–65,536 |
| Drawn index entries N | 3–131,070, multiple of 3 |
| Draws D | 1–512 (one per drawn batch) |
| Texture slots T | 1–32 |
| Texture side | 1–1,024 px each way |
| Texel bytes | ≤ 8 MiB, sum of `width × height × 4` |
| File length | `160 + 20V (+ 12V with normals) + 4N + 48D + 32T + texels`, ≤ 10 MiB |

## File layout

Little-endian. Regions follow in order with no padding or trailing bytes.

| Offset | Size | Value |
| ---: | ---: | --- |
| 0 | 4 | `VMA1` |
| 4 | 2 | format version 2 |
| 6 | 2 | header length 160 |
| 8 | 4 | file length |
| 12 | 4 | coordinate code 1 |
| 16 | 32 | binding SHA-256 (raw) |
| 48–60 | 4×4 | V, N, D, T |
| 64–112 | 7×8 | offset and length of positions, normals, UVs, indices, draws, texture directory, texels |
| 120 | 12 | bounds minimum XYZ (Float32) |
| 132 | 12 | bounds maximum XYZ (Float32) |
| 144 | 4 | alpha reference 128 |
| 148 | 4 | CreatureDisplayInfo ID |
| 152 | 8 | reserved, zero |

- **Positions**: 12-byte Float32 XYZ for all V vertices, exact source bits, all finite.
- **Normals**: 12-byte Float32 XYZ for all V vertices, exact source bits, all finite, present exactly when some draw has an env-map stage. Otherwise the region is empty: length 0, offset where the UVs start.
- **UVs**: 8-byte Float32 UV0 for all V vertices, all finite. Top-left image origin, unchanged.
- **Indices**: u32 per drawn triangle corner, each < V; draws in order, each its section's resolved triangles (SKIN triangle → lookup → vertex).
- **Bounds**: tight over drawn positions only, canonical +0.
- **Draw record** (48 bytes):

| Offset | Size | Value |
| ---: | ---: | --- |
| 0 | 4 | ordinal |
| 4 | 4 | batch ordinal (strictly increasing) |
| 8 | 4 | section index |
| 12 | 4 | index start (contiguous from 0, ending at N) |
| 16 | 4 | index count |
| 20 | 4 | combiner code |
| 24 | 4 | stage 0 texture slot |
| 28 | 4 | stage 0 M2 texture ordinal |
| 32 | 4 | stage 1 texture slot, `0xFFFFFFFF` for a one-stage combiner |
| 36 | 4 | stage 1 M2 texture ordinal, `0xFFFFFFFF` for a one-stage combiner |
| 40 | 1 | stage 0 coordinate |
| 41 | 1 | stage 1 coordinate, `0xFF` for a one-stage combiner |
| 42 | 2 | zero |
| 44 | 4 | flags: `1` alpha key, `2` two-sided, `4` no depth test, `8` no depth write |

- **Combiner codes** fix the stage count, each stage's coordinate (0 UV0, 1 UV1, 2 env map) and whether alpha key may be set. A record that disagrees is invalid. No version 2 combiner uses UV1, so there is no UV1 region.

| Code | M2 combiner | Stages | Colour | Alpha key |
| ---: | --- | --- | --- | --- |
| 1 | `Combiners_Opaque` + `Diffuse_T1` | UV0 | T0 | no |
| 2 | `Combiners_Mod` + `Diffuse_T1` | UV0 | T0 | T0.a < 128/255 |
| 3 | `Combiners_Opaque_Mod2xNA_Alpha` + `Diffuse_T1_Env` | UV0, env | mix(T0 × T1 × 2, T0, T0.a) | no |
| 4 | `Combiners_Opaque_AddAlpha_Alpha` + `Diffuse_T1_Env` | UV0, env | T0 + T1 × T1.a × (1 − T0.a) | no |
| 5 | `Combiners_Opaque_Alpha` + `Diffuse_T1_T1` | UV0, UV0 | mix(T0, T1, T1.a) | no |

- **Texture record** (32 bytes): slot, FileDataID, width, height, absolute texel offset, byte length, u8 mip level, u8 encoding (1 palette, 2 BC1, 3 BC2, 4 BC3, 5 BGRA), u8 wrap (`1` U, `2` V), u8 zero, u16 base width, u16 base height. Slots are distinct (FileDataID, wrap U, wrap V) in texture-ordinal order; texels are contiguous in slot order.
- **Texels**: tightly packed RGBA8 rows, top-left origin, straight alpha, as `Blp2Decoder` produced them.

## Binding and artifact ID

The binding SHA-256 hashes `vibeminer-model-appearance-binding-v1\0`, the VMG1 binding digest (raw 32 bytes), the display ID, ExtendedDisplayInfoID (always 0) and the four TextureVariationFileDataID values, the slot count, then per slot its FileDataID, CKey, EKey, encoded SHA-256, encoded and decoded lengths, verifier revision, chosen mip size, level, base size, encoding and wrap bits, then the revisions above and format version 2. Artifact ID = `SHA256("vibeminer-model-appearance-artifact-v1\0" ‖ binding ‖ file SHA-256 ‖ u64 length)`. The binding is keyed by display, so two displays sharing a model and textures have different artifacts. The draws and normals are not in the binding: they follow from the verified model and SKIN (the VMG1 binding) and the material policy.

## Verification

`VerifyAsync` reads the file once: full SHA-256, exact header from the descriptor, finite positions, normals and UVs, every index in range, the exact draw partition, every draw record against its combiner, normals present exactly when an env stage exists, the texture directory against the binding, tight drawn bounds and EOF. It holds one positions buffer (≤ 768 KiB). Main checks the file SHA-256 and the header before serving the body. The renderer repeats every structural check with yielded work (≤ 64 KiB or 4,096 items per turn) before any GPU upload, and never decodes a game format.

## Renderer

The scene draws one `Mesh` with one `ShaderMaterial` per draw from [geometry-combiners.ts](../apps/desktop/src/renderer/geometry-combiners.ts), and a `DataTexture` per slot (`NoColorSpace`: the stored texel values, no sRGB decode; `flipY = false`; mipmaps generated; repeat or clamp per wrap bit). The vertex shader passes UV0 or the env-map coordinate per stage: `r = reflect(normalize(viewPosition), normalMatrix · normal)`, `m = 2·√(r.x² + r.y² + (r.z + 1)²)`, `uv = r.xy / m + 0.5`, and the map centre for a zero normal or `m = 0`. The fragment shader applies the colour formula above, discards alpha-key draws below 128/255, and writes the colour unlit with alpha 1 and no output colour-space conversion. The mesh colour is 1: colour, opacity and texture-weight tracks are not evaluated, and an env stage's UV transform is not applied. Back faces are culled unless two-sided, and the M2 depth flags apply. Camera fit uses the VMA1 bounds with the same `geometry-camera-fit-v1` rules as VMG1.
