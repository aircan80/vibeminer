# M2 and SKIN

Our original static model readers in [`src/backend/VibeMiner.Formats.Wow/Models/`](../../src/backend/VibeMiner.Formats.Wow/Models/). They parse geometry and declarations. They do not fetch dependencies, evaluate animation or pick an appearance. The application proves source and dependency identity separately.

| Reader | Revision | Output |
| --- | --- | --- |
| `M2ModelReader` | `m2-static-native-v1` | `M2Model`: vertices, textures, materials, bones, lookups, dependency IDs, chunk list, bounds |
| `M2SkinReader` | `m2-skin-native-v2` | `M2Skin`: geometry plus parsed batches and shadow batches, all references checked |
| `M2GeometrySkinReader` | `m2-skin-geometry-v1` | `M2GeometrySkin`: geometry only; batches kept as uninterpreted array declarations |
| `M2StaticGeometry.Create` | `native-m2-position-normal-uv-winding-v1` | Resolved triangle indices and section-referenced bounds |
| `M2StaticMaterials.AdmitOpaque` | `m2-opaque-shader0-flags0-uv0-absent-channels-v1` | Draw list for the narrow opaque profile, or `MODEL_MATERIAL_UNSUPPORTED` |
| `M2Appearance.ReadModelAsync` / `AdmitAsync` | `m2-appearance-batches-v1`, `m2-one-or-two-stage-combiners-v1` | Texture-weight proofs, SKIN batch records and the textured draw plan (one or two stages per batch), or the first refused batch |

Input streams must be readable, seekable, start at 0 and be complete. A BLTE projection with holes is unsupported. Every read is asynchronous and bounded; nothing is published until EOF and length checks pass.

## Supported profiles

| Container | Version | SKIN header |
| --- | --- | --- |
| Unchunked: file starts `MD20` | 264 | 48 bytes |
| Chunked: `MD21` chunk containing `MD20` | 272, 274 | 56 bytes |

Everything else is `MODEL_UNSUPPORTED`, and the message names the version and container. No "version ≥ 264" layout is assumed: 273, an unchunked 272 or 274 and a chunked 264 stay refused. The SKIN profile is taken from the parsed model, never guessed from offsets.

**Chunked 272** (D097, #75) reads with the 274 layout. Checked on the five wowxptr 12.1.5.70340 creature models that refused on it (Tiny Green Dragon, Crystalwing Stone Drake, Phoenix Hatchling, Dark Phoenix Hatchling, Spirit Wolf 3524): MD21 with `SFID`/`TXID`/`AFID`, header flags `0x80`/`0x81`/`0x90`, and in every vertex the bone weights sum to 255 and the normal has unit length. Their main SKINs have the 56-byte header (data starts at 64; shadow batches at 48, count 0-5, inside the file) and a maximum-bones field of 0 with 27-76 bones per section, the normal modern value (see the bone-palette rule). pywowlib's version branches change nothing we read after Cataclysm (its WoD sequence change splits the u32 at sequence offset 28 in two; flags stay at 12), and wow.export reads every `MD21` model with one layout.

## Chunked container

- Chunks are `fourCC` (bytes `0`–`Z`), u32 size, payload. They must tile the file exactly. Duplicate chunk IDs are rejected. Order does not matter. At most 128 chunks.
- Exactly one `MD21`, whose payload must begin with `MD20`. **All MD20 offsets are relative to the start of that nested MD20**, not the file.
- Dependency chunks:

| Chunk | Content |
| --- | --- |
| `SFID` | SKIN FDIDs. The first `viewCount` entries are main profiles; the rest are LOD SKINs. |
| `TXID` | One FDID per texture ordinal, zeros included. Count must equal the texture count. Required when the model has textures. |
| `SKID` | One skeleton FDID (exactly 4 bytes). |
| `BFID` | Bone file FDIDs. |
| `AFID` | 8-byte records: u16 animation ID, u16 variation, u32 FDID. |

Other chunks are listed as `(id, offset, size)` and not interpreted. An unchunked MD20 has no SFID, so the caller must resolve its SKIN some other proven way; never guess `<model>00.skin`.

## MD20 header

304 bytes, or 312 when flag `0x8` declares the texture-combiner array at 304. Each array is a (u32 count, u32 offset) pair; offsets are relative to MD20, must not point into the header, and `count × stride` must fit.

| Offset | Array | Stride |
| --- | --- | --- |
| 4 | version (u32) | |
| 8 | name (NUL-terminated strict UTF-8) | 1 |
| 16 | global flags (u32) | |
| 20 | global sequences | 4 |
| 28 | sequences | 64 |
| 36 | sequence lookup | 2 |
| 44 | bones | 88 |
| 52 | key-bone lookup | 2 |
| 60 | vertices | 48 |
| 68 | view count (u32) | |
| 72 | colors | 40 |
| 80 | textures | 16 |
| 88 | texture weights | 20 |
| 96 | texture transforms | 60 |
| 104 | replaceable texture lookup | 2 |
| 112 | materials | 4 |
| 120 | bone lookup | 2 |
| 128 | texture lookup (combos) | 2 |
| 136 | texture-coordinate lookup | 2 |
| 144 | texture-weight lookup | 2 |
| 152 | texture-transform lookup | 2 |
| 160 | bounding box + radius (28 bytes) | |
| 188 | collision box + radius (28 bytes) | |
| 216 | collision triangles | 2 |
| 224 | collision vertices | 12 |
| 232 | collision normals | 12 |
| 240 | attachments | 40 |
| 248 | attachment lookup | 2 |
| 256–296 | events, lights, cameras, camera lookup, ribbons, particles | span only |

Vertex, 48 bytes: position (3 f32), 4 bone weights (u8 each), 4 bone indices (u8 each), normal (3 f32), UV0 (2 f32), UV1 (2 f32). All floats must be finite. Texture, 16 bytes: u32 type, u32 flags, filename span. Material, 4 bytes: u16 flags, u16 blend mode. Bone parent must be −1 or another bone.

Collision triangles are never used as visible geometry.

## SKIN

| Offset | Field |
| --- | --- |
| 0 | `SKIN` |
| 4 | vertex lookup (u16 → M2 vertex) |
| 12 | triangle indices (u16 → vertex lookup), count divisible by 3 |
| 20 | bone properties, **4 bytes per lookup entry** (count must equal lookup count) |
| 28 | sections (submeshes), 48 bytes each |
| 36 | batches, 24 bytes each |
| 44 | u32 maximum bones |
| 48 | shadow batches, 12 bytes each (56-byte header only) |

**Triangles index the SKIN lookup, which indexes the M2 vertices.** Both levels are bounds-checked, and every triangle in a section must stay within that section's lookup span.

Section, 48 bytes: u16 ID (geoset), u16 level, u16 vertex start, u16 vertex count, u16 triangle start, u16 triangle count, u16 bone count, u16 bone start, u16 bone influences, u16 center bone, center (3 f32), sort center (3 f32), f32 sort radius (≥0). **The effective triangle start is `start + (level << 16)`**; start and count must be multiples of 3.

Batch, 24 bytes: u8 flags, i8 priority, u16 shader ID, u16 section index, u16 second raw field, i16 color index, u16 material index, u16 material layer, u16 texture count, u16 texture-lookup index, u16 texture-coordinate-lookup index, u16 texture-weight-lookup index, u16 texture-transform-lookup index. The strict reader checks section/material/color indices and every lookup range; `0xFFFF` in the weight and transform lookups means "no channel". The second raw field keeps its exact bytes; no general flag meaning is assigned.

### Bone-palette rule

With a positive header maximum (≤256), each section's bone count must be ≤ it, influences ≤4, and `boneStart + boneCount` must fit the M2 bone lookup.

**Chunked 272 or 274 with a header maximum of 0**, revision `m2-skin-native-v2`. This is the normal value in modern SKINs, not a rare case: a Retail 274 model (33,915 vertices, 178 bone-lookup entries) has header 0 while nine sections each declare 60 bones, and every live chunked 272 SKIN checked has 0 too (D097). The raw 0 is kept (no inferred maximum), each section palette is capped at 256, influences at 4, and spans must still fit the actual bone lookup. This rule does not apply to 264, where 0 is still checked as a maximum. It does not mean 0 is "unlimited" in general.

## Static geometry

`M2StaticGeometry.Create` resolves every SKIN triangle to an M2 vertex index (`uint`), keeps native positions, normals, UVs and winding, keeps every declared section (including overlaps and duplicates), and computes bounds over section-referenced positions only. A SKIN parsed against another model object is rejected even when counts match. No pose is applied: bind/rest positions only.

The geometry-only reader exists for models whose materials fail the strict profile but whose geometry is valid. It requires chunked 272 or 274 with no external `SKID`/`BFID`; the refusal names which one (version, `SKID` and its FileDataID, or `BFID`). It still validates batch and shadow array framing but publishes their declarations only. An `M2GeometrySkin` cannot be passed to `AdmitOpaque`; that is a compile-time boundary. Geometry-ready never means appearance-ready.

## Opaque material profile

`AdmitOpaque` admits every draw or fails. It requires:

- model global flags 0; no external skeleton or bone files; no billboard bones (flags `0x78`); no lights, ribbons or particles; only `MD21`, `SFID`, `TXID`, `SKID`, `AFID`, `BFID` chunks; no shadow batches; at least one batch;
- per batch: flags, priority, shader, second raw field and material layer 0; color index −1; exactly one texture; material flags 0 and blend 0; one batch per section;
- texture-coordinate lookup resolves to 0 (UV0); weight and transform lookups resolve to `0xFFFF`. An empty lookup or out-of-range index is not a default;
- texture flags only `0x1`/`0x2` (wrap U/V);
- texture type 0 with a positive TXID, or types **11, 12, 13**: creature replacement slots, returned unresolved for the application to bind from the selected display. Type 14 is a different replacement (not creature slot 4); it and every other type are unsupported;
- usable lit normals: finite squared length > 0 (a finite tiny vector can underflow to 0).

Render flags: the pinned wow.export renderer uses `0x01` unlit, `0x04` two-sided, `0x08` no depth test, `0x10` no depth write. Old pywowlib enum labels disagree. With flags 0 our preview is lit, back-face culled, depth tested and written. Do not import historical labels or interpret new bits.

Keep all four `TextureVariationFileDataID` slots from CreatureDisplayInfo, zeros included. Never compact them or pick the first nonzero.

## Combiner notes

How a batch's texture stages combine (#69). Read from the pinned wow.export `ShaderMapper.js`, `m2.vertex.shader` and `m2.fragment.shader` (1b625d0) and checked against live wowxptr 12.1.5.70340 files.

**Selection.** The batch's texture count and shader ID name a pixel shader (the combiner) and a vertex shader (where each stage's coordinates come from):

- Shader ID with bit `0x8000`: index `ID & 0x7FFF` into a fixed table of (pixel, vertex) pairs. `0x8000` is `Combiners_Opaque_Mod2xNA_Alpha` with `Diffuse_T1_Env`, `0x8002` is `Combiners_Opaque_AddAlpha_Alpha` with `Diffuse_T1_Env`, `0x8017` is `Combiners_Opaque_Alpha` with `Diffuse_T1_T1`.
- Otherwise, one texture: `Combiners_Mod` if any of `0x70` is set, else `Combiners_Opaque`; vertex `Diffuse_Env` (`0x80`), `Diffuse_T2` (`0x4000`) or `Diffuse_T1`.
- Otherwise, two textures: `0x70` set picks the `Combiners_Mod_*` family, clear the `Combiners_Opaque_*` family, and `ID & 7` picks the member (`0x4011` is `Combiners_Mod_Mod`, `0x4014` `Combiners_Mod_Mod2x`). Vertex: `0x80` gives `Diffuse_Env_*`, `0x8` `Diffuse_T1_Env`, `0x4000` `Diffuse_T1_T2`, else `Diffuse_T1_T1`.

In a vertex shader name, `T1` is UV0, `T2` is UV1 and `Env` is a sphere map from the view-space normal: `r = reflect(normalize(viewPosition), normal)`, `m = 2·√(r.x² + r.y² + (r.z + 1)²)`, `uv = r.xy / m + 0.5`. Stage *i* reads `TextureCombos[index + i]`, `TextureCoordCombos[index + i]` and `TextureTransformCombos[index + i]`. Stage *i* has weight lookup entry `index + i`; wow.export reads only the first, but the client may scale a stage's term by its own weight, so every present entry must be a constant full opacity. Live two-stage bodies ship a one-entry weight lookup, so their second stage has no entry.

**Formulas** (T0, T1 are the stage samples; the colour is then lit, and an added term is added after lighting):

| Combiner | Colour | Texture alpha (for blend 1) |
| --- | --- | --- |
| `Combiners_Opaque` | T0 | none |
| `Combiners_Mod` | T0 | T0.a |
| `Combiners_Opaque_Mod2xNA_Alpha` | mix(T0 × T1 × 2, T0, T0.a): the env map shows, doubled, where the base alpha is low | none |
| `Combiners_Opaque_AddAlpha_Alpha` | T0, plus T1 × T1.a × (1 − T0.a) added | none |
| `Combiners_Opaque_Alpha` | mix(T0, T1, T1.a): a decal over the base, same UVs | none |
| `Combiners_Mod_Mod`, `Combiners_Mod_Mod2x` | T0 × T1 (× 2) | T0.a × T1.a (× 2) |

**Live creature bodies** (wowxptr 12.1.5.70340; 17 models, 52 batches: Infernal, Voidwalker, Felhunter, Felguard, Imp, Succubus, Chicken, Lord Piglet, Jadefire Felsworn, Tree Frog, Archaedas, Siamese Cat, White Stallion and four others):

| Batches | Blend | Combiner | Use |
| --- | --- | --- | --- |
| 19 | 0 | `Combiners_Opaque` (`0x0`) | body |
| 7 | 1 | `Combiners_Mod` (`0x10`) | alpha-keyed hair, fins, wings |
| 9 | 0 | `0x8000` `Combiners_Opaque_Mod2xNA_Alpha` + `Diffuse_T1_Env` | body with an env-map sheen in the low-alpha areas (Voidwalker, Succubus, Chicken, Lord Piglet, Felsworn) |
| 3 | 0 | `0x8002` `Combiners_Opaque_AddAlpha_Alpha` + `Diffuse_T1_Env` | body with an added env-map glint (Voidwalker, Felhunter, Felguard) |
| 3 | 0 | `0x8017` `Combiners_Opaque_Alpha` + `Diffuse_T1_T1` | body with a decal (Tree Frog) |
| 4 | 7 | `0x4011` `Combiners_Mod_Mod` + `Diffuse_T1_T2` | Infernal fire layers, UV-scrolled |
| 3 | 2, 4 | `0x4014` `Combiners_Mod_Mod2x` + `Diffuse_T1_T2` | Voidwalker glow layers |
| 4 | 2, 4 | `Combiners_Mod` | glow and shadow layers |

Every two-texture batch has batch flags `0x80`, or `0x90` when both stages' transform lookup entries are `0xFFFF`. No meaning is assigned to `0x80`; wow.export ignores the batch flags byte. All these models ship an empty coordinate lookup, one texture-weight lookup entry and a constant 32767 weight. The env stage often names a transform lookup entry: Voidwalker's scrolls (track 0 translation, global sequence), while Felhunter's and Felguard's entry is 0 with no transform tracks declared, which wow.export treats as no transform. Every two-texture body batch is opaque; the `T1_T2` (UV1) combiners appear only on blended layers.

## Textured appearance profile

`M2Appearance` (#63, #69) reads what the geometry-only reader leaves uninterpreted, from the same verified bytes, and never fails geometry. Model phase: sequence flags and, per texture-weight track, a proof that every key stored in this model is `32767`. A sequence without flag `0x20` keeps its keys in an `.anim` file, so keys there leave the track unproven; a track with no keys at all has the default full opacity, as the pinned wow.export renderer evaluates it. SKIN phase: the 24-byte batch records, then every batch in order until the first refusal:

| Check | Refusal |
| --- | --- |
| Section, material and color indices exist | `MaterialUnsupported` |
| Material blend is 0-7. Blend 2-7 is a blended layer: counted, not drawn, no further checks (D094) | `BlendUnsupported` (blend > 7) |
| Texture count is 1 or 2 | `MultiTexture` |
| Render flags only `0x01`, `0x04`, `0x08`, `0x10`; batch flags 0 or `0x10`, or also `0x80`/`0x90` with two textures; second raw field and material layer 0 | `MaterialUnsupported` |
| Combiner, from texture count and shader ID: `Combiners_Opaque` or `Combiners_Mod` with `Diffuse_T1`; `0x8000`, `0x8002` or `0x8017` with two textures. Shader bits outside the selection rules refuse | `CombinerUnsupported`, naming the pixel and vertex shader and the ID |
| Each stage's texture combo names a declared texture; texture flags only U/V wrap | `MaterialUnsupported` |
| A non-empty coordinate lookup says 0 for a `T1` stage and −1 for an `Env` stage. Retail models usually ship an empty lookup | `MaterialUnsupported` |
| Each stage's weight lookup entry is `0xFFFF` or a proven constant full opacity. The first entry must exist; a later stage's entry past the lookup's end names no weight channel | `TextureWeight` / `MaterialUnsupported` |
| Each stage's transform lookup entry exists. `0xFFFF`, or an entry past the declared transform tracks, is no transform. A real track on a `T1` stage refuses; on an `Env` stage it is drawn at rest and counted (D094) | `TextureTransform` / `MaterialUnsupported` |
| Each stage's texture is type 0 (TXID) or 11/12/13 (display variation 0/1/2) | `MaterialUnsupported` |
| One opaque or alpha-key batch per section; blended layers do not count | `MaterialUnsupported` |
| At least one opaque or alpha-key batch draws a triangle | `BlendUnsupported` when only blended layers are left, else `MaterialUnsupported` |

A `MaterialUnsupported` refusal for flags or layer names the first value outside the profile (render flags, batch flags, second raw field, material layer, in that order), and `BlendUnsupported` for blended layers names the first layer's blend mode.

Particles, ribbons, lights, shadow batches and blended layers are counted, not drawn, and do not refuse (D090, D094). Priority planes are ignored: nothing blends. Color tracks (vertex color/alpha animation, often used to hide alternate geosets) are not evaluated, like pose. Alpha key applies only to `Combiners_Mod` (any `0x70` bit, texture alpha), at 128/255; the other drawn combiners have no texture alpha. Render flag `0x01` (unlit) has no effect because the preview is unlit; `0x04` draws both sides; `0x08`/`0x10` turn off depth test/write. The renderer applies the formulas above to the stored texel values, unlit, with the added term added directly ([VMA1 renderer](../../contracts/geometry-vma1-v2.md#renderer)).

Live on wowxptr 12.1.5.70340 (#69): Infernal, Voidwalker, Felhunter, Felguard, Imp, Succubus, Chicken, Lord Piglet, Jadefire Felsworn, Tree Frog, Archaedas and Siamese Cat draw textured. Voidwalker's alternate bracers show because colour tracks are not evaluated. Ragnaros' lava batches animate their weights and UVs.

## Coordinates

Readers keep native coordinates. The preview transform is `(x, y, z) → (x, z, −y)`: a proper rotation with determinant +1, applied once to positions and normals. Winding is preserved; do not flip it again. UVs are unchanged.

## Limits

| Limit | Value |
| --- | --- |
| Source (model or SKIN) | 64 MiB |
| Combined retained output (model + SKIN + assembly) | 128 MiB |
| Vertices | 1,000,000 |
| SKIN lookup entries | 65,536 |
| Triangle indices | 3,000,000 |
| Sections / materials | 256 / 256 |
| Batches | 512 |
| Textures / SKIN profiles | 32 / 32 |
| Chunks | 128 |
| Name bytes | 4,096 |
| Other metadata entries | 65,536 |

Reads are at most 64 KiB; loops check cancellation at least every 4,096 entries.

## Errors

`MODEL_INVALID` (malformed), `MODEL_UNSUPPORTED` (outside the profile), `MODEL_LIMIT`, `MODEL_MATERIAL_UNSUPPORTED` (parsed fine, appearance not admitted). Missing dependencies and unsupported appearances are application states, not malformed files.

## References

Read for layout facts only; nothing was copied, installed or executed. The Java project had no M2 reader.

- pywowlib `55276dc5c2195da7fe136638a2a59716622f8c65` (MIT): `m2_format.py`, `skin_format.py`, `wow_common_types.py`, `m2_chunks.py`, `enums/m2_enums.py`, `m2_file.py` (its maximum-bones writer buckets are marked uncertain).
- wow.export `1b625d0f9943be4d8f8e124dbb7a04f1fc7da8f3` (MIT): `M2Loader.js`, `Skin.js`, `ShaderMapper.js`, `M2RendererGL.js`, `m2.vertex.shader`, `m2.fragment.shader`. Its filename, first-choice and default-material conveniences are not our policy.
- WoWFormatLib `1abf1bfad68705a2064b90fba1cb8bcd22588ff0`: `M2Reader.cs`, `SKINReader.cs`, cross-check only. Its fallback textures are not acceptable for a real appearance.
