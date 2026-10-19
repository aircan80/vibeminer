# Unresolved ROOT CKeys in Classic Beta 1.60.1.70450

The ROOT of `wow_classic_beta` `1.60.1.70450` references ten CKeys that its own verified ENCODING does not contain. The bytes are intact and the parser is right. The mappings are simply absent, and nothing found so far explains why.

VibeMiner's full-inventory profile requires every all-variant ROOT CKey to resolve through ENCODING, so it can promise complete file sizes. This build breaks that assumption. See [ROOT](../formats/root.md) and [TACT](../formats/tact.md) for the formats.

## Build identity

This is the Classic Beta ("Forever Beta"), not Retail `wow`.

| Field | Value |
| --- | --- |
| Product / version / BuildId / region | `wow_classic_beta` / `1.60.1.70450` / `70450` / US |
| Display identity | `6cbc2080b7ec1927ebd35883c64c4ec5505f45e8a61430bd2b1e473300ad661d` |
| BuildConfig | `84bea4233331e390fda8724c6f91ffa5` (59,537 bytes, SHA-256 `d242f86b35c10307bf864d6118ca7d3aa2d801b274304d79da130311ba887870`) |
| CDNConfig | `f065eade5276a5ac05606e60a4c5a1f7` (59,333 bytes, SHA-256 `cd1efbd680adcc8f8a77dad07011920eaa6b38c32f53217d2acc5e37b44f2c26`) |
| ProductConfig | `fcfd1bc39031f5921d00aee2d869adb5` |
| KeyRing | present, empty |
| Versions document SHA-256 | `ab8dab6f16863c5b8a6186e3d851c335e3d6981c12c154e0956a3147be9cec43` |
| CDNs document SHA-256 | `94faf4759ef5397cdd36356bce9a9fd6a3f6e6c0d0c9a269efaaf1336361d6be` |

BuildConfig declares:

| Role | CKey | EKey | Decoded bytes | Encoded bytes |
| --- | --- | --- | ---: | ---: |
| ENCODING | `c441a5427cb86b23bbfb6ed9c6cf0114` | `9a0263e06fdace8ab940a4a45a8e28fb` | 141,574,945 | 141,273,036 |
| ROOT | `8a0ca94be2f6ace93991c04ca6e19b57` | `1330591ebe7f1259debd53c2f6e5a721` (via ENCODING) | 50,525,924 | 38,885,945 |
| INSTALL | `80ef2a1306d5a04503ebc484bf7a83b2` | `aa0442b23470404f83836132964d7f41` | 19,346 | 18,581 |
| DOWNLOAD | `2e6a3c7870f6a92a4fe71b9f7f6c66a1` | `fc5a0e12a41e47cc8aea447f36cc495b` | 56,870,749 | 48,612,067 |
| SIZE | `cb0e32e4c2e1c149fa8f386b016ac2d6` | `7d29929b407ea0f23fa7cfea8a224a17` | 36,993,686 | 31,289,352 |

The config also has `build-uid = wow_classic_beta`, a build name of `WOW-70450patch1.60.1_ForeverBeta`, `feature-placeholder = true`, patch entries and 1,166 VFS-related assignments. None of the ten missing CKeys appears as text in the config.

## The ten unresolved references

Each FDID appears exactly once in ROOT, so no other variant supplies a substitute CKey. No INSTALL CKey is missing.

| FDID | Block / entry | CKey | Offset in decoded ROOT |
| ---: | --- | --- | ---: |
| 5014017 | 287 / 10 | `1a77d6afc96e8e6305142245fe354a40` | 44,056,104 |
| 5014019 | 287 / 11 | `9f537bae38d70dec6a6347fe89dc14ab` | 44,056,120 |
| 5014022 | 287 / 13 | `e2826e2327524754a9fda9a21bbaf05b` | 44,056,152 |
| 5014025 | 287 / 14 | `7de732fabbdc4286fe167d13ae6d7eed` | 44,056,168 |
| 7018189 | 287 / 16 | `e9feb9bf64266300c8c174cf1974a561` | 44,056,200 |
| 7079572 | 287 / 18 | `f47b2fd3a021562a16c1398c604164c3` | 44,056,232 |
| 7576057 | 289 / 9 | `4db4d353fbfae4080f664de9f3894ed9` | 44,056,578 |
| 7576058 | 289 / 10 | `9dd8e5584d178485a43e797ebe4ac2d1` | 44,056,594 |
| 7576059 | 289 / 11 | `edbffc8e3cfb770e3090601f619705f9` | 44,056,610 |
| 7576060 | 289 / 12 | `3f61978c6e8678a3768e4a77661c845b` | 44,056,626 |

None is the zero key or the MD5 of empty input.

## The bytes are fine

Both metadata files were walked independently of the production parsers, verifying every BLTE chunk.

- **ENCODING:** 7 chunks verified. Header MD5 equals the EKey, full decoded MD5 equals the CKey, decoded length 141,574,945. Raw encoded SHA-256 `d88614d7aafb5d39c388876962253ca0b91d32daee84f57c074e640fbd2564bb`. None of the ten 16-byte CKeys occurs anywhere in the decoded file, including across chunk boundaries. It has 2,209,148 content records.
- **ROOT:** 193 chunks verified. Header MD5 equals the EKey, full decoded MD5 equals the CKey, decoded length 50,525,924. Raw encoded SHA-256 `022493d20dee3237bec8f9b0d5d6757bb1d611bd3677554090c9b39f4872fc23`. A v2 framing walk reaches exact EOF: header size 24, version 2, 2,433,102 memberships, 231,036 named memberships, 916 blocks. Blocks 287 and 289 start at decoded offsets 44,055,839 and 44,056,353.

So the missing mappings are not caused by a parser bug, dropped ENCODING rows or bad ROOT framing.

## Raw flags: 0x37 with NoNames

Both blocks have locale mask 95222 and no name hashes.

| Block | Memberships | Missing | First | Second | Third | Derived OR |
| --- | ---: | ---: | --- | --- | --- | --- |
| 287 | 22 | 6 | `0x4c` | `0x10000000` | `0x37` | `0x106e004c` |
| 289 | 16 | 4 | `0x800c` | `0x10000000` | `0x37` | `0x106e800c` |

`0x10000000` is NoNames, which only explains why there are no name hashes. The derived values do not have DoNotLoad (`0x100`), LowViolence (`0x80`) or Encrypted (`0x08000000`). The other bits are opaque. Nothing in them marks a tombstone, placeholder or optional entry.

## Why flag filtering is invalid

Blocks 287 and 289 are mixed. The other 16 CKeys in block 287 and the other 12 in block 289 resolve normally, under the same flags. Skipping blocks with third byte `0x37` would throw away valid content along with the ten unresolved references. There is no flag rule that separates them.

## How other tools behave

Public readers do not require every ROOT CKey to resolve at load time:

- [TACTSharp Build.cs](https://github.com/wowdev/TACTSharp/blob/67fd53ac852ca297e9bf95bb41d1a97da202eba4/TACTSharp/Build.cs) loads ENCODING and ROOT without a per-entry check; `OpenFileByCKey` fails later for the one file that has no mapping. [TACTTool](https://github.com/wowdev/TACTSharp/blob/67fd53ac852ca297e9bf95bb41d1a97da202eba4/TACTTool/Program.cs) reports an unavailable CKey for the requested FDID.
- [CascLib's WoW ROOT reader](https://github.com/ladislav-zezula/CascLib/blob/38a34665624b8775bb875274b36191b21c38d97b/src/CascRootFile_WoW.cpp) adds a file only when the CKey lookup succeeds and silently skips the rest.
- [TACTSharp RootInstance.cs](https://github.com/wowdev/TACTSharp/blob/67fd53ac852ca297e9bf95bb41d1a97da202eba4/TACTSharp/RootInstance.cs) uses the same v2 flag OR and columnar layout as our reader. Its named flags do not classify these entries as deleted.

That is why other tools open this build. Their success does not prove that every ROOT entry resolves, and nobody has observed what the game client does with these ten files. A release that is playable can still contain ROOT entries that our strict profile cannot size.

`feature-placeholder` is a boolean in [cascette-rs BuildConfig](https://github.com/wowemulation-dev/cascette-rs/blob/2d1294b9d8eba0c0d0ffc6387f0777f36ecae671/crates/cascette-formats/src/config/build_config.rs) (revision `2d1294b9d8eba0c0d0ffc6387f0777f36ecae671`). No source connects it to ROOT entries without an ENCODING mapping.

## The TVFS lead

This BuildConfig declares VFS metadata. Two public readers describe a TVFS model that could carry the missing association:

- [TACTSharp TVFSInstance.cs](https://github.com/wowdev/TACTSharp/blob/67fd53ac852ca297e9bf95bb41d1a97da202eba4/TACTSharp/TVFSInstance.cs) parses WoW virtual paths whose 40-hex suffix is a big-endian FDID followed by a 16-byte CKey. File spans carry an EKey, content offset and content length. TACTSharp labels its TVFS support unfinished.
- [CascLib CascRootFile_TVFS.cpp](https://github.com/ladislav-zezula/CascLib/blob/38a34665624b8775bb875274b36191b21c38d97b/src/CascRootFile_TVFS.cpp) also recognises WoW paths with locale/content flags and FDID+CKey, resolves spans by EKey and tolerates some spans missing from the central map. Its documented example of missing ENCODING data comes from another game.

Not established: whether any declared VFS manifest in this build contains these ten CKeys, whether it gives a unique supported encoding with matching sizes, or whether it fits the existing budgets. No TVFS file from this build has been read.

Archive indexes are keyed by EKey. They cannot bridge an unknown CKey to an EKey.

## What would resolve it

A bounded original TVFS reader that searches this build's own authenticated VFS closure for the exact FDID + CKey pairs, then:

- keeps every span and prefix together with its source proof;
- checks that the mapping is unique and the sizes match;
- handles multi-span files by their actual composition (one span's length is not the file's length);
- adds a resolver only after that verification.

These do not count as a resolution: matching the FDID alone, picking another content variant, guessing a full EKey from a prefix, using another build's ENCODING, or fetching ordinary payloads to guess.

Until then:

- Keep all ten memberships with their exact flags. Do not mark them size zero, deleted, encrypted, absent or resolved.
- Keep the strict complete-size gate for this profile. No complete ROOT/INSTALL logical total can be claimed for this build.
- Fail early: an indexed anti-join over all ROOT memberships finds the unresolved set before millions of per-row selections, and the error should name the count, product/version and a few FDID/block/flag examples.
- Regression tests should cover a fully verified synthetic ENCODING plus a ROOT block that mixes mapped and unmapped CKeys under identical flags, and a batch whose earlier valid rows are not blamed after rollback.

Primary source URLs for the two metadata objects, for anyone checking independently:

- ROOT: `https://level3.ssl.blizzard.com/tpr/wow/data/13/30/1330591ebe7f1259debd53c2f6e5a721`
- ENCODING: `https://level3.ssl.blizzard.com/tpr/wow/data/9a/02/9a0263e06fdace8ab940a4a45a8e28fb`
- BuildConfig: `https://level3.ssl.blizzard.com/tpr/wow/config/84/be/84bea4233331e390fda8724c6f91ffa5`
