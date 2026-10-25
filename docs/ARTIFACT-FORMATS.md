# App artifact formats

Binary files VibeMiner itself writes. Game file formats are in [FORMATS-AND-ASSETS.md](FORMATS-AND-ASSETS.md). Storage rules are in [STORAGE.md](STORAGE.md).

## Common rules

- Every artifact is written to app-owned staging, then published to the CAS under its SHA-256. SQLite registration happens after publication. The two are separate steps, never one atomic transaction.
- Opening re-hashes the whole file through the CAS (`OpenVerifiedAsync`) before reading, then checks the header, every region boundary, reserved zero bytes and exact EOF.
- Most files embed canonical JSON metadata. On open it must byte-equal the metadata recomputed from the stored descriptor.
- **Content keys** are SHA-256 of canonical web JSON of the revision, policies and source binding. Mutable timestamps are blanked first, so re-verifying a source does not change the key:

| Artifact | Fields blanked |
| --- | --- |
| Creatures, item-effects bridge, classification labels | Config receipt `RetrievedAt`/`VerifiedAt`; source snapshot `PreparedAt`; source receipt `RetrievedAt`/`VerifiedAt`; EN and ROOT receipt `RetrievedAt`/`VerifiedAt` |
| Items projection | Source snapshot `PreparedAt` |
| Table rows | None needed; the binding has no timestamps |

- Same key with a different SHA-256 or length is an integrity conflict. The first registered descriptor is kept.
- Integers are little-endian, except the bulk archive map and DL ledger, which are big-endian.

## Table row artifact (VMTR001)

[`TableRowArtifacts`](../src/backend/VibeMiner.Infrastructure/TablesData/TableRowArtifacts.cs), revision `table-rows-v1`. One per admitted DB2 table interpretation.

| Region | Content |
| --- | --- |
| Header, 64 bytes | `VMTR001\n`, i32 version 1, i32 metadata length, i64 metadata offset, i64 index offset, i64 row count; bytes 40–63 zero |
| Rows | One JSON-serialized `Db2Row` each, in emission order, ≤256 KiB per row |
| Metadata | JSON of binding, WDC5 header and completion, ≤1 MiB; index starts right after it |
| Index | `count × 32` bytes, then EOF |

Index entry: u32 row ID, u32 copy flag (0/1), u32 copy source ID, i32 row length, i64 row offset, i64 emission ordinal. Sorted by **unsigned ID**, then physical before copy, then emission ordinal. Exact-ID lookups binary-search this index. Pages return at most 50 rows and 512 KiB.

Limits: 1,000,000 rows; values ≤65,536 characters.

Content key: SHA-256 of `{revision, binding}`. The binding is source SHA-256, decoded SHA-256 (or projection identity), definition SHA-256, provider revision, locale, decoder revision and schema-selection SHA-256. The schema-selection key hashes only the selected interpretations (sorted layout hashes plus fields) of the blocks matching the build. **BUILD selector text is excluded**, so a provider edit that only widens a build range does not invalidate rows.

A reader keeps the opened, SHA-verified file and reuses it for later pages of the same artifact. Only the first open of a session pays for the full hash.

## Creatures projection (VMCRTR01)

[`CreaturesProjection`](../src/backend/VibeMiner.Infrastructure/Creatures/CreaturesProjection.cs), feature revision `creatures-core-v1`, query policy `primary-name-ordinal-ignorecase-contains-v1`, semantic profile `creatures-direct-display-v1`.

| Region | Content |
| --- | --- |
| Header, 64 bytes | `VMCRTR01`, u32 version 1, u32 rows, u32 metadata length, u32 directory entry size (32), i64 summary bytes, i64 name bytes; bytes 40–63 zero |
| Metadata | JSON: normalized binding, counts, revisions |
| Directory | `rows × 32` |
| Summaries | JSON per row: 4 facts, 4 display IDs, 4 probabilities, name issue |
| Names | UTF-8 names of rows whose name is valid |

Directory entry: u32 Creature ID, u8 name state (0 valid, 1 empty, 2 invalid), 3 zero bytes, i64 name offset, i32 name length, i64 summary offset, i32 summary length. Offsets are relative to their region. IDs strictly increase.

Limits: 1,000,000 rows, 128 MiB file, 64 MiB names, 256 KiB per name, 128 KiB metadata, 2,048 bytes per summary.

## Items projection (VMITEM01)

[`ItemsProjection`](../src/backend/VibeMiner.Infrastructure/Items/ItemsProjection.cs), revision `items-projection-v2` (legacy `-v1` still readable), query policy `ordinal-ignorecase-contains-v1`. An Item-anchored merge join of the admitted Item and ItemSparse row artifacts on row ID.

| Region | Content |
| --- | --- |
| Header, 64 bytes | `VMITEM01`, i32 version 1, i32 directory entry size (32), i64 item count, i64 metadata offset, i32 metadata length, i64 names offset, i64 name bytes, i64 directory offset; bytes 60–63 zero |
| Summaries | JSON per item: base source values, icon FDID; ≤8,192 bytes each |
| Metadata | JSON: binding, join counts, property groups, revision; ≤1 MiB |
| Names | UTF-8 names of items whose name is valid |
| Directory | `items × 32`, then EOF |

Directory entry: u32 Item ID, i32 name state (0 valid, 1 empty, 2 invalid, 3 missing ItemSparse row), i64 name offset (relative to names), i32 name length, i32 summary length, i64 summary offset (absolute).

Limits: 1,000,000 rows, 512 MiB file, 256 MiB names, 256 KiB per name. The builder yields and reports progress every 4,096 rows or 1 MiB of encoded output. One builder runs at a time.

## Item-effects bridge index (VMIEFX01)

[`ItemEffectsBridgeArtifacts`](../src/backend/VibeMiner.Infrastructure/ItemEffects/ItemEffectsBridgeArtifacts.cs), revision `item-effect-bridge-index-v1`, order policy `signed-item-unsigned-bridge-source-ordinal-v1`. Indexes ItemXItemEffect (table `00cb674f`, layout `96F083AD`, FDID 3177687) by parent Item.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | `VMIEFX01` |
| 8 | 4 | u32 version 1 |
| 12 | 4 | u32 row count |
| 16 | 4 | u32 metadata length (1–131,072) |
| 20 | 4 | u32 tuple size, 32 |
| 24 | 8 | u64 tuple bytes, exactly `count × 32` |
| 32 | 32 | zero |

Then metadata, then tuples, then EOF. Tuple, 32 bytes:

| Offset | Field |
| --- | --- |
| 0 | i32 parent Item ID |
| 4 | u32 bridge row ID |
| 8 | i32 ItemEffect ID |
| 12 | u32 flags: bit 0 = copy; nothing else |
| 16 | u32 copy source ID (0 when not a copy) |
| 20 | u64 source ordinal in the row artifact's sorted index |
| 28 | u32 zero |

Sorted by signed parent, then unsigned bridge ID, then source ordinal. Source ordinals are an exact permutation of `0..count−1`; duplicates and zero values are kept. Limits: 65,536 rows, 4 MiB file.

**Content key = SHA-256 of the metadata bytes**, which are canonical JSON of `{revision, orderPolicy, binding, rowCount}` with the normalized binding. The file stores exactly those bytes.

## Classification labels (VMCLASS1, VMCLASS2)

[`ClassificationIndex`](../src/backend/VibeMiner.Infrastructure/ItemClassifications/ClassificationIndex.cs) and [`CompositeClassificationIndex`](../src/backend/VibeMiner.Infrastructure/ItemClassifications/CompositeClassificationIndex.cs).

| | VMCLASS1 | VMCLASS2 |
| --- | --- | --- |
| Revision | `classification-label-index-v1` | `classification-label-index-v2` |
| Source | ItemClass (table `b977271e`, FDID 1140189) | ItemSubClass (table `95d8638f`, FDID 1261604) |
| Key | ClassID, −128..127 | `((classId + 128) << 8) \| (subClassId + 128)`, 0..65,535 |

Header, 64 bytes: 8-byte magic, i32 version (1 or 2), i32 metadata length, i32 key count, i32 row count, i64 payload offset; bytes 32–63 zero. Then normalized-binding metadata, a sparse directory of present keys only, then label payloads.

Directory entry, 32 bytes, sorted by key: i32 key, count (i32 in v1, u32 in v2), i64 payload offset, i32 payload length; bytes 20–31 zero. A label payload (JSON) is stored only when the key occurs exactly once; a repeated key has length 0 and no label. Lookups binary-search the directory.

Limits: 16 MiB file, 65,536 source rows.

## Bulk archive map (VMBARCM1)

[`BulkArchiveLocator`](../src/backend/VibeMiner.Infrastructure/Acquisition/BulkArchiveLocator.cs), revision `bulk-archive-locator-v1`, native index revision `archive-index-v1`. Big-endian.

- Header, 32 bytes: `VMBARCM1`, i32 version 1, i32 record size 32, i64 location count, i64 located DL members.
- Record, 32 bytes: EKey (16), i32 archive ordinal (position in the CDN config `archives` list), i64 archive offset (≤ u32 max), u32 encoded size (nonzero).
- Records sorted by EKey then archive ordinal, unique. One EKey may have several same-size locations; different sizes are an integrity failure.

A separate JSON manifest (≤2 MiB, also in CAS) binds the map to the CDN config SHA-256, ledger revision and descriptor, every native index receipt, map SHA-256/length and counts. Opening checks all of that. Limits: 4,096 archives, 512 MiB aggregate native indexes, 10 M locations. See [formats/tact.md](formats/tact.md#bulk-archive-map).

## DL ledger (VMDLEDG1)

[`DownloadLedger`](../src/backend/VibeMiner.Infrastructure/Downloads/DownloadLedger.cs), revision `download-ledger-v1`. Big-endian. The distinct full EKeys of one DL manifest, built by bounded external merge sort (≤1 GiB temporary, ≤128 run files, fan-in 32).

- Header, 32 bytes: `VMDLEDG1`, i32 version 1, i32 record size 24, i64 object count, i64 total encoded bytes.
- Record, 24 bytes: EKey (16), i64 encoded size. Sorted by EKey. A repeated EKey with a different size is `DOWNLOAD_SIZE_CONFLICT`.

Limit: 10 M entries. Tags, priorities and ordinals stay in the retained DL manifest.

## Model geometry (VMG1)

See [contracts/geometry-vmg1-v1.md](../contracts/geometry-vmg1-v1.md).

## Creature appearance (VMA1)

See [contracts/geometry-vma1-v1.md](../contracts/geometry-vma1-v1.md). Retained in `geometry_appearances`, keyed by display.
