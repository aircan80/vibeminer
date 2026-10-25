# VMA1 appearance artifact v1

VMA1 is VibeMiner's own binary derivative of one Creature display's textured appearance: the model's native positions and UV0, the triangles of every drawn SKIN batch, and one RGBA8 image per texture slot. It is not a WoW format. It is drawn instead of the [VMG1](geometry-vmg1-v1.md) line set when the display's texture chain is ready. Pose, visibility (geosets), colour and opacity animation, particles, ribbons and lights are not evaluated.

Used by [selected Creature geometry](creature-geometry-v1.md#appearance). Builder and verifier: [ModelAppearanceArtifacts.cs](../src/backend/VibeMiner.Formats.Wow/GeometryArtifacts/ModelAppearanceArtifacts.cs). Batch admission: [M2Appearance.cs](../src/backend/VibeMiner.Formats.Wow/Models/M2Appearance.cs) ([m2-skin.md](../docs/formats/m2-skin.md#single-texture-appearance-profile)). Renderer admission: [geometry-vma.ts](../apps/desktop/src/renderer/geometry-vma.ts).

## Fixed revisions

| Name | Value |
| --- | --- |
| Artifact revision | `model-appearance-vma1-v1` |
| Material policy | `m2-single-texture-opaque-alphakey-v1` |
| Batch reader | `m2-appearance-batches-v1` |
| Texture policy | `blp2-largest-mip-within-1024-rgba8-v1` |
| BLP decoder | `blp2-rgba8-v2` |
| Coordinate policy | `native-m2-position-normal-uv-winding-v1` (code 1) |
| Alpha reference | 128 of 255 (blend mode 1 with shader `0x10`) |

## Limits

| Count | Limit |
| --- | --- |
| Vertices V | 1–65,536 |
| Drawn index entries N | 3–131,070, multiple of 3 |
| Draws D | 1–512 (one per drawn batch) |
| Texture slots T | 1–32 |
| Texture side | 1–1,024 px each way |
| Texel bytes | ≤ 8 MiB, sum of `width × height × 4` |
| File length | `160 + 20V + 4N + 32D + 32T + texels`, ≤ 10 MiB |

## File layout

Little-endian. Regions follow in order with no padding or trailing bytes.

| Offset | Size | Value |
| ---: | ---: | --- |
| 0 | 4 | `VMA1` |
| 4 | 2 | format version 1 |
| 6 | 2 | header length 160 |
| 8 | 4 | file length |
| 12 | 4 | coordinate code 1 |
| 16 | 32 | binding SHA-256 (raw) |
| 48–60 | 4×4 | V, N, D, T |
| 64–108 | 12×4 | offset and length of positions, UVs, indices, draws, texture directory, texels |
| 112 | 12 | bounds minimum XYZ (Float32) |
| 124 | 12 | bounds maximum XYZ (Float32) |
| 136 | 4 | alpha reference 128 |
| 140 | 4 | CreatureDisplayInfo ID |
| 144 | 16 | reserved, zero |

- **Positions**: 12-byte Float32 XYZ for all V vertices, exact source bits, all finite.
- **UVs**: 8-byte Float32 UV0 for all V vertices, all finite. Top-left image origin, unchanged.
- **Indices**: u32 per drawn triangle corner, each < V; draws in order, each its section's resolved triangles (SKIN triangle → lookup → vertex).
- **Bounds**: tight over drawn positions only, canonical +0.
- **Draw record** (32 bytes): ordinal, batch ordinal (strictly increasing), section index, index start, index count, texture slot, M2 texture ordinal, flags (`1` alpha key, `2` two-sided, `4` no depth test, `8` no depth write). Index ranges are contiguous from 0 and end at N.
- **Texture record** (32 bytes): slot, FileDataID, width, height, absolute texel offset, byte length, u8 mip level, u8 encoding (1 palette, 2 BC1, 3 BC2, 4 BC3, 5 BGRA), u8 wrap (`1` U, `2` V), u8 zero, u16 base width, u16 base height. Slots are distinct (FileDataID, wrap U, wrap V) in texture-ordinal order; texels are contiguous in slot order.
- **Texels**: tightly packed RGBA8 rows, top-left origin, straight alpha, as `Blp2Decoder` produced them.

## Binding and artifact ID

The binding SHA-256 hashes `vibeminer-model-appearance-binding-v1\0`, the VMG1 binding digest (raw 32 bytes), the display ID, ExtendedDisplayInfoID (always 0) and the four TextureVariationFileDataID values, the slot count, then per slot its FileDataID, CKey, EKey, encoded SHA-256, encoded and decoded lengths, verifier revision, chosen mip size, level, base size, encoding and wrap bits, then the revisions above and format version 1. Artifact ID = `SHA256("vibeminer-model-appearance-artifact-v1\0" ‖ binding ‖ file SHA-256 ‖ u64 length)`. The binding is keyed by display, so two displays sharing a model and textures have different artifacts.

## Verification

`VerifyAsync` reads the file once: full SHA-256, exact header from the descriptor, finite positions and UVs, every index in range, the exact draw partition, the texture directory against the binding, tight drawn bounds and EOF. It holds one positions buffer (≤ 768 KiB). Main checks the file SHA-256 and the header before serving the body. The renderer repeats every structural check with yielded work (≤ 64 KiB or 4,096 items per turn) before any GPU upload, and never decodes a game format.

## Renderer

The scene draws one `Mesh` with one `MeshBasicMaterial` per draw (unlit), a `DataTexture` per slot (`SRGBColorSpace`, `flipY = false`, mipmaps generated, repeat or clamp per wrap bit), `alphaTest = 128/255` for alpha-key draws, back faces culled unless two-sided, and the M2 depth flags. Camera fit uses the VMA1 bounds with the same `geometry-camera-fit-v1` rules as VMG1.
