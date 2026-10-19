# WoW ROOT (TSFM)

WoW's ROOT maps FileDataIDs (FDIDs) to content keys, per locale and content variant. Code: [`RootInventoryReader`](../../src/backend/VibeMiner.Formats.Wow/RootInventoryReader.cs), [`RootTargetLookup`](../../src/backend/VibeMiner.Formats.Wow/RootTargetLookup.cs), [`RootBatchLookup`](../../src/backend/VibeMiner.Formats.Wow/RootBatchLookup.cs). It is kept out of the generic TACT project on purpose. The ROOT object itself is found and verified through EN and BLTE; see [tact.md](tact.md).

## Layout

All integers are little-endian.

Header:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `TSFM` |
| 4 | 4 | Header size: 20 or 24 |
| 8 | 4 | Version: 1 or 2 |
| 12 | 4 | Total memberships |
| 16 | 4 | Named memberships (blocks with name hashes) |
| 20 | 4 | Only when header size is 24: extension, must be 0 |

Admitted profiles: v1 with header 20 or 24, v2 with header 24. Anything else is `ROOT_UNSUPPORTED`. The header counts are **memberships**, not unique files.

Blocks follow until EOF. Block header:

| Version | Fields, in order |
| --- | --- |
| 1 | u32 count, u32 content flags, u32 locale flags |
| 2 | u32 count, u32 locale flags, u32 first content flags, u32 second content flags, u8 third byte |

Derived content flags = `first | second | (third << 17)`. All three raw parts are kept, because the OR loses information.

Block body is columnar:

1. `count` × u32 FDID deltas. The first ID is `delta[0]`; each next ID is `previous + 1 + delta[i]`. IDs strictly increase inside a block. Overflow past u32 is `ROOT_INVALID`.
2. `count` × 16-byte CKeys.
3. `count` × u64 name hashes, unless the derived flags contain `NoNames` (`0x10000000`).

`NoNames` is the only flag that changes framing. At EOF the observed total and named counts must equal the header.

## Variants and flags

- The same FDID can appear in several blocks with different locale/content flags, and with the same or different CKeys. Every membership is kept with its block and entry ordinal. Nothing is filtered by locale or platform during parsing.
- Locale bits (from TACTSharp `RootInstance`): enUS `0x2`, esES `0x80`, esMX `0x1000`. enGB is `0x200`; an observed mask of 514 (`0x202`) means enUS plus enGB, not an enUS-only file. esES and esMX are different cultures.
- OR-ing every block's locale mask does not prove which locales a build supports.
- Reference content-flag names (LowViolence `0x80`, DoNotLoad `0x100`, Encrypted `0x08000000`, NoNames `0x10000000`) come from public tools. Apart from NoNames we treat content bits as opaque raw facts. Do not infer a Windows/platform selection from them.
- A name hash is not a filename. ROOT has no names.

## Memory and limits

The reader keeps one block in memory: a `uint[count]` of IDs plus a packed `byte[count × 16]` of CKeys, about **20 bytes per membership** (the Java reader used about 24). Sizes are checked before allocation.

| Limit | Value |
| --- | --- |
| Decoded ROOT | 512 MiB |
| Memberships | 50 M |
| Blocks | 1 M |
| Memberships per block | 2 M |
| Block working memory | 64 MiB |

Errors: `ROOT_INVALID`, `ROOT_UNSUPPORTED`, `ROOT_LIMIT`, with decoded offset and block/raw-flag context.

## Lookups

ROOT has no global FDID index. Finding one FDID, or proving it is absent, needs a **full scan** of a separately authenticated, complete ROOT. "Not found" is only meaningful after the scan reaches EOF.

| Helper | Bounds | Use |
| --- | --- | --- |
| `RootTargetLookup` | ≤1,024 target IDs, ≤4,096 matches | One-off lookups, e.g. a table's FDID |
| `RootBatchLookup` | ≤128 nonzero target IDs, ≤4,096 matches per ID, ≤32,768 total | Asset batches (icons, models): one pass for up to 128 IDs, results in ascending ID order |

Asset resolution sessions cache results per exact build/source: at most 1,024 FDID outcomes, 32,768 memberships and 16 MiB of charged memory. Results are committed to the cache only after the scan reaches EOF and all EN lookups succeed. Retained decoded ROOT bytes (`root-native-file-v1`) are re-checked on reuse: local SHA-256 and length, MD5 equal to the ROOT CKey, and a full grammar pass.

## Selection policies

ROOT gives memberships; a policy picks one CKey. The policy name is stored with every result.

| Policy | Used for | Rule |
| --- | --- | --- |
| `enus-consistent-root-variants-v1` | Table (DB2) files | Keep memberships with `LocaleFlags & 0x2`. None → `CONTENT_VARIANT_MISSING`. More than 256 → `CONTENT_VARIANT_LIMIT`. More than one distinct CKey → `CONTENT_VARIANT_AMBIGUOUS`. |
| `all-root-variants-consistent-asset-v1` | Assets (BLP, M2, SKIN) | Every membership of the FDID, across all locale and content flags, must share one CKey. Otherwise `AmbiguousVariants`. No memberships → `MissingMembership`. |

Neither policy uses the host OS, the source region or a zero-locale fallback. Ambiguity is a result, not something to resolve by sorting or taking the first row.

CKey → EKey uses `lexicographic-complete-encoding-v1`: sort the CKey's EN candidate EKeys by ordinal string order and take the first one that has an encoding-table row (and so a known encoded size). No candidate → `CONTENT_UNRESOLVED`.

## Unresolved CKeys

A complete ROOT can reference CKeys that the same build's EN does not contain. One Classic beta build has ten such references; see [root-unresolved-classic-beta.md](../research/root-unresolved-classic-beta.md). Treat them as unresolved, not as corrupt ROOT and not as absent files.

## References

- Original Java VibeMiner checkpoint `8f353d046d961108f7acc86bfe1381004467afd5`: `FullRootInventory`, `WowRootParser`.
- TACTSharp `67fd53ac852ca297e9bf95bb41d1a97da202eba4` (MIT): `RootInstance` locale and content-flag names.

Our reader is original code; neither reference was copied.
