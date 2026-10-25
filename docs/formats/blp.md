# BLP2 images

Our original BLP2 decoder ([`src/backend/VibeMiner.Formats.Wow/Images/`](../../src/backend/VibeMiner.Formats.Wow/Images/), revision `blp2-rgba8-v2`) and the icon PNG pipeline built on it ([`src/backend/VibeMiner.Infrastructure/Assets/`](../../src/backend/VibeMiner.Infrastructure/Assets/)).

The decoder takes a readable, seekable, caller-owned stream, a mip level (default 0) or a largest side (`DecodeLargestWithinAsync`), and limits. It returns owned, tightly packed **RGBA8, top-left origin, straight alpha**, plus the encoding, alpha facts, raw mip byte and all mip descriptors. No color-space conversion, premultiplication, resizing or PNG work happens inside it. It does not authenticate the source; the caller does.

## Header (148 bytes, little-endian)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `BLP2` |
| 4 | 4 | Type. Must be 1 (direct). 0 is JPEG content and unsupported. |
| 8 | 1 | Color encoding: 1 palette, 2 DXT/BC, 3 raw BGRA |
| 9 | 1 | Alpha depth |
| 10 | 1 | Alpha encoding |
| 11 | 1 | Mip byte |
| 12 | 4 | Width |
| 16 | 4 | Height |
| 20 | 64 | 16 mip offsets |
| 84 | 64 | 16 mip lengths |

Palette images carry a 1,024-byte BGRA palette at offset 148. Other encodings may start their data at 148; unused palette padding is not required.

## Supported profiles

| Encoding | Alpha depth | Alpha encoding | Decoded as |
| --- | --- | --- | --- |
| 1 (palette) | 0, 1, 4, 8 | 0 | Paletted |
| 2 | 0, 1 | 0 | BC1 (DXT1) |
| 2 | 4, 8 | 1 | BC2 (DXT3) |
| 2 | 8 | 7 | BC3 (DXT5) |
| 3 | 8 | 0 | Raw BGRA, stored alpha kept |

Every other combination is `BLP_UNSUPPORTED`, including BLP1, JPEG and raw BGRA with depth 0 (not guessed as opaque).

## Mips

- Mip byte must be 0, 1 or `0x11`. Its upper bits get no meaning. The value is returned as `MipFlags`.
- The mip **count comes from the descriptors**, not the mip byte. Byte 0 with more than one declared level is an error.
- All 16 descriptor pairs are checked: offset and length both zero or both nonzero, present levels form a contiguous prefix, no level beyond the terminal 1×1, exact length for that level's geometry, inside the source, no overlap with metadata or each other.
- Exact lengths: palette `w×h + ceil(w×h×depth/8)`; BC1 8 bytes per 4×4 block; BC2/BC3 16 bytes per block; BGRA `w×h×4`.
- A missing requested mip is `BLP_MIP_UNAVAILABLE`. No other level is substituted. Bytes outside declared mips are not interpreted.
- Observed Retail icon: 64×64 BC1, alpha depth 1, mip byte `0x11`, seven contiguous mips 64 → 1.

## Pixel rules

- Palette: BGRA palette entries, alpha from a separate plane after the indices, packed low bit first. Depth 1 → 0/255, depth 4 → value × 17, depth 8 → byte, depth 0 → 255.
- RGB565 endpoints are expanded by bit replication (`r<<3 | r>>2`, `g<<2 | g>>4`).
- BC1: four colors when `c0 > c1`, else three colors plus a fourth that is transparent black when alpha depth > 0 and opaque black when depth is 0.
- BC2 and BC3 always use four-color mode. BC2 alpha is 4-bit explicit × 17. BC3 alpha uses both branches (`a0 > a1`: 8 interpolated values; else 6 plus 0 and 255).
- Interpolation uses integer division, so results **floor**. This is a deterministic CPU policy, not a claim of bit equality with every GPU.
- Blocks are decoded in raster order; edge blocks are clipped to the image.

## Limits

| Limit | Decoder default | Icon pipeline | Model textures |
| --- | --- | --- | --- |
| Source bytes | 64 MiB | 8 MiB | 16 MiB |
| Width or height | 16,384 | 1,024 | 16,384 (container) |
| Selected mip pixels | 16,777,216 | 1,048,576 | 1,048,576 |
| Selected mip bytes | 64 MiB | 4 MiB | 4 MiB |
| Selected RGBA bytes | 64 MiB | 4 MiB | 4 MiB |
| PNG output | | 5 MiB | |

Reads are sliced at 64 KiB and loops check cancellation per row or block.

## Model textures

Creature textures (#63, D091) first read only the mip descriptors (`ReadMipsAsync`) and start at the first declared mip, in level order, whose width and height are both ≤ 1,024. While one display's slots together exceed 8 MiB of RGBA8, the image with the largest current level steps down one declared level; then each image is decoded once at its planned level with `DecodeAsync`. Nothing is resized. A container whose declared levels never get within 1,024 px is `BLP_MIP_UNAVAILABLE`, and slots that exceed 8 MiB even at their smallest declared levels are `CREATURE_APPEARANCE_TEXEL_LIMIT`; both report `resourceLimit`. The images go into a [VMA1 artifact](../../contracts/geometry-vma1-v2.md) as raw RGBA8, not PNG; the renderer uploads them without decoding.

## Icon pipeline

[`IconArtifacts`](../../src/backend/VibeMiner.Infrastructure/Assets/IconArtifacts.cs) (revision `icon-png-proof-v1`):

- Source must have a full `content-blte-v1` receipt. Partial projections and anything claiming a full proof without one are refused.
- Decodes through the BLP decoder with the icon limits, then writes PNG with our own encoder (`png-rgba8-filter0-v1`).
- Pixel policy `rgba8-top-left-straight-no-colorspace-conversion-v1`.
- The PNG embeds a proof binding source keys and sizes, decoder, encoder and pixel-policy revisions and the mip level. A stored PNG whose proof revisions differ from the current code is rejected.
- At most four concurrent read leases.

## References

- Kanma BLPConverter `38fc3229fd95040f5030e735e00c16a39ada7f94`: `blp_internal.h`, `blp.h`, `blp.cpp` (header fields, BGRA palette, alpha planes, encoding combinations).
- Khronos OpenGL Registry `5f8b6c01d4b486527d66b247984221a82e5b4594`: `EXT_texture_compression_s3tc.txt` (block order, endpoints, BC1 modes, BC3 alpha).
- Microsoft Direct3D block-compression documentation (mip/block padding, interpolation).

The Java project had no BLP decoder. Our decoder and fixtures are original.
