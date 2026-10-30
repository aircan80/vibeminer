# WDC5 (DB2) and DBD definitions

What our original WDC5 reader admits and how it turns physical storage into values. Code: [`src/backend/VibeMiner.Formats.Wow/Db2/`](../../src/backend/VibeMiner.Formats.Wow/Db2/) (reader revision `wdc5-values-v7`) and [`Definitions/DbdDefinitionReader.cs`](../../src/backend/VibeMiner.Formats.Wow/Definitions/DbdDefinitionReader.cs). Table files are found through ROOT ([root.md](root.md)) and verified with BLTE ([tact.md](tact.md)). Row artifacts are described in [ARTIFACT-FORMATS.md](../ARTIFACT-FORMATS.md#table-row-artifact-vmtr001).

The reader needs a readable, seekable, caller-owned decoded stream, the DBD document and the exact four-part build. It emits a header callback and then rows; all of it is provisional until the reader returns. Errors are `DB2_*`.

## File layout

All integers little-endian.

### Header (204 bytes)

| Offset | Size | Field | Rule |
| --- | --- | --- | --- |
| 0 | 4 | `WDC5` | |
| 4 | 4 | Schema version | Must be 5 |
| 8 | 128 | Schema string | Strict UTF-8, NUL padded, no interior NUL |
| 136 | 4 | Record count | |
| 140 | 4 | Field count | |
| 144 | 4 | Record size | Nominal for Sparse tables |
| 148 | 4 | String table size | Sum of section string sizes |
| 152 | 4 | Table hash | |
| 156 | 4 | Layout hash | Selects the DBD block |
| 160 | 12 | (not interpreted) | |
| 172 | 2 | Flags | See below |
| 174 | 2 | ID index | |
| 176 | 4 | Total field count | Must equal field count |
| 180 | 4 | Packed-data offset (bytes) | |
| 184 | 4 | Lookup (relationship) column count | 0 or 1 |
| 188 | 4 | Field storage info size | `field count × 24` |
| 192 | 4 | Common data size | |
| 196 | 4 | Pallet data size | |
| 200 | 4 | Section count | |

Then: section headers (40 bytes each), field structures (4 bytes each: i16 bits, u16 offset), storage infos (24 bytes each), pallet data, common data, then one encrypted-ID list per keyed section (u32 count + count × u32 IDs), then the sections. Gaps between regions must be zero. Trailing bytes are rejected.

### Section header (40 bytes)

| Offset | Field |
| --- | --- |
| 0 | u64 key. Nonzero means a keyed (encrypted) section. |
| 8 | File offset |
| 12 | Record count |
| 16 | String table size |
| 20 | Offset-records end (Sparse) |
| 24 | ID list size |
| 28 | Relationship data size |
| 32 | Offset map count |
| 36 | Copy table count |

### Flags

`0x1` Sparse, `0x2` SecondaryKey, `0x4` external ID list. Admitted values: **0, 4, 5, 6, 7**. Everything else is `DB2_UNSUPPORTED`.

### Section tail order

| Profile | After the record area |
| --- | --- |
| Fixed (0/4/6) | string table → ID list → copy table → relationship block |
| Sparse, flags 5 | ID list → copy table → offset map (6 B each) → **relationship block → map IDs** (4 B each) |
| Sparse, flags 7 | ID list → copy table → offset map → **map IDs → relationship block** |

Compute all tail offsets once per section. Relationship bytes must not move fixed strings: strings start at `offset + count × recordSize`.

## Storage kinds

| Kind | Name | Value | Admission |
| --- | --- | --- | --- |
| 0 | None | Inline bits, width `32 − bits` (or the info's width when that is ≤0) | Offset and width must match the field structure |
| 1 | Bitpacked | Unsigned, width 1–64 | Offset = packed-data offset × 8 + info offset |
| 2 | Common | 32-bit value per record ID, default otherwise | Not for an inline ID |
| 3 | Pallet | Index into a 32-bit value pallet | Same offset rule; cardinality 0 or 1 |
| 4 | Pallet array | Index into a 32-bit pallet of `arrayLength` values | Cardinality = array length |
| 5 | Bitpacked signed | Signed, width 1–64 | Same offset rule |

Pallet blocks are laid out kind 3 first, then kind 4, in field order; common data follows. Every block must be consumed exactly. Arrays are allowed only for None and Pallet array. Strings must be scalar None fields.

## Numbers

- Unsigned physical bits are **zero-extended**. Only kind 5 is **sign-extended**.
- Pallet and Common values are 32-bit. The record holds an index; the index width is not the value width.
- Inline storage (None, Bitpacked, Bitpacked signed) narrows to the DBD width only when the dropped high bits are a canonical sign extension. Otherwise `DB2_SCHEMA_MISMATCH`: the record gives the field exactly those bits, so a value that doesn't fit is corruption.
- Pallet and Common narrow by keeping the low bits ([D079](../DECISIONS.md)). When a verified non-ID, non-relationship integer field is narrower than 32 bits and any of its pallet or common values (the common default included) doesn't fit, the whole column keeps the low bits at the DBD width, signed or unsigned per the DBD, and its `Db2Column` carries `Narrowing` (32 stored bits, N used bits). It is chosen once the auxiliary blocks are loaded, before Header and before any row. A column whose values all fit gets no marker and reads exactly as before. Raw evidence (below) wins over narrowing; IDs and relationship keys stay strict.
- Narrowing needs one constant upper-bit pattern ([D087](../DECISIONS.md)). Every misfit value in the column (fitting values are ignored) must have the same bits above the DBD width, `value >> width`. If two differ, the definition may simply be too narrow, so the table fails with `DB2_SCHEMA_MISMATCH` before Header. The message names the field, its storage (pallet, pallet array or common), the DBD width and sign, and the patterns seen as `value & ~mask`, sorted, at most 4 (`at least 4` when the scan stopped there). Every Classic case seen has exactly one pattern per column.
- Narrowing must not merge values ([D109](../DECISIONS.md)). With one pattern, the column narrows only when no two different stored values (fitting ones and the common default included) have the same low bits at the DBD width. A fitting `0x00000005` next to `0xFFFF0005` would both read 5, so such a column keeps its physical 32-bit values as `raw-physical-integer-v1` evidence with no reference, and the table still opens. A sign-extended fit such as `0xFFFFFF9C` (-100 as int16) next to `0x00030005` has different low bits and still narrows.
- Examples: None8 `0xff` read as DBD signed32 is 255; as DBD signed8 it is −1. None32 `0x100` into unsigned8 fails. Kind 5 2-bit −1 into DBD unsigned8 is 255. Pallet `0x780107` into verified unsigned8 is 7; pallet `0x2ff` into verified signed8 is −1; pallet `0xffffffff` into signed8 is −1 with no marker, since it fits as a sign extension. Pallet `0x780107` and `0x790105` together into unsigned8 fail (patterns `0x780100`, `0x790100`).
- Floats need exactly 32 physical bits and must be finite.
- Fixed-record strings are 32-bit relative offsets into the concatenated string tables. 0 is the empty string. A string in a keyed section is `DB2_ENCRYPTED`. Strings are strict UTF-8, bounded at 65,536 bytes.

### Raw integer evidence

When a DBD width is narrower than the physical value width, the column can be published raw under `raw-physical-integer-v1`: the whole column (every row, every array element) keeps the full physical value, with the candidate width/sign and a warning. Raw columns lose their declared references and must never be used as join keys, filters or calculated facts.

- Automatic for integer columns with an **unverified** DBD name (`?`), not the ID, whose physical value width exceeds the candidate width.
- For **verified** names, only through `VerifiedRawIntegerRules`: at most 8 exact rules, each binding the definition SHA-256, table hash, layout hash, field ordinal and name, storage kind, physical width, candidate width and sign. Everything must match before the header; IDs, relations and floats can never be selected.

Known width mismatches:

| Build / table | Field | Physical | DBD | Handling |
| --- | --- | --- | --- | --- |
| Retail 12.1.5.70448 Item, layout `996192AA` | physical ordinal 10, `Field_12_0_0_63534_010` (unverified) | Pallet, 32-bit values (4 values, none fit u8) | u8 | Automatic raw |
| ItemSubClass, table `95D8638F`, layout `1E67DB87` | logical ordinal 6, `PrerequisiteProficiency` | Kind 3, 32-bit | signed8 | Exact rule |
| ItemEffect, table `4002A5B1`, layout `4CA77678` | `Charges` (logical 3) | Kind 3, 32-bit | signed16 | Exact rule, narrows when constant (D108) |
| same | `SpellCategoryID` (logical 6) | Kind 3, 32-bit | unsigned16 | Exact rule, narrows when constant (D108) |
| same | `ChrSpecializationID` (logical 8) | Kind 3, 32-bit | unsigned16 | Exact rule, narrows when constant (D108) |

For Charges, half the pallet values are canonical signed16 extensions and half are not, so neither "reject everything above 65,535" nor "treat all wide values as signed" works. On 12.1.5.70575 every Charges pallet value has `0xFFFF` above bit 16, so both halves are the same habit: the low 16 bits, signed, are the value (`0xFFFF0000` is 0).

D108: the three ItemEffect rules narrow when constant. A column whose values that don't fit share one upper-bit pattern narrows as D087 does; a column with two or more, or with low bits that would merge two stored values (D109), keeps its whole raw evidence, and the table is never refused for it. A rule without that flag (ItemSubClass, CreatureDisplayInfo) keeps its column raw as before.

These retail rules predate D079 and are kept so retail values don't change; since D108 the ItemEffect ones give way to narrowing when constant. The same retail fields show the pattern D079 narrows (NPCSoundID `0x20000+n`, ObjectEffectPackageID `0x560000+n`, SpellCategoryID `0x30000+n`); retiring a rule would let it narrow instead, and changes that table's values.

Known pallet/common fields that narrow (D079), all verified names with a pinned matching block and one upper-bit pattern each:

| Build / table, layout | Fields | Stored | DBD |
| --- | --- | --- | --- |
| 5.5.4.67849 Item `39911A3F` | Sound_override_subclassID (`0x200+n`), ItemGroupSoundsID (`0x780100+n`), DamageType[5] (`0x40000+n`) | Pallet / pallet array 32 | signed8, unsigned8, unsigned8 |
| 5.5.4.67849 CreatureDisplayInfo `F75F884C` | CreatureModelAlpha, BloodID (`0x1100+n`), NPCSoundID (`0x20000+n`), ObjectEffectPackageID, AnimReplacementSetID (`0xd0000+n`) | Common / pallet 32 | unsigned8 ×2, unsigned16 ×3 |
| 5.5.4.67849 ItemEffect `1BF9CF3A` | SpellCategoryID (`0x70000+n`) | Pallet 32 | unsigned16 |
| 5.5.4.70579 Item, CreatureDisplayInfo, ItemEffect | Same fields as 5.5.4.67849 | as above | as above |
| 2.5.6.70006 Item `39911A3F` | AmmunitionType (`0x100+n`) | Pallet 32 | unsigned8 |
| 2.5.6.70006 CreatureDisplayInfo `F75F884C`, ItemEffect `1BF9CF3A` | Same fields as 5.5.4.67849 | Common / pallet 32 | as above |

The same Item layout narrows different fields on the two builds, so which fields carry junk is a property of each file, not of the layout.

## IDs and copies

- External IDs (flag `0x4`) come from the section's ID list. Otherwise the ID is read from the inline ID field. IDs are never repaired from ordinals or minimum ID, and ID 0 is real data.
- Readable physical IDs must be unique. Copy targets must be unique and must not overlap physical IDs. Neither may repeat an encrypted-section ID (see [Keyed sections](#keyed-encrypted-sections)).
- Copy table entries are 8 bytes: target ID, source ID. Chains resolve to a physical row, up to depth 64; cycles fail. A copy whose chain ends at a keyed-section ID is excluded and counted. Any other unresolved source is an error.
- Copies are re-decoded with the copy's own ID, because Common values depend on the record ID.

## Sparse tables

Supported Sparse profile: flags 5 or 7, external IDs, no separate string table, no pallet or common data, offset map count = record count.

- Each map entry is 6 bytes: u32 absolute offset, u16 size. Maps must cover the record area contiguously with positive sizes; map IDs must equal the external IDs.
- The real record length comes from the map, not the header record size. The header record size is nominal: one Retail ItemSparse had 321 nominal field bytes and a 328-byte header size.
- Fields are byte-aligned None storage with contiguous nominal offsets in DBD order. Strings are inline NUL-terminated strict UTF-8.
- Fields are read with a cursor. After an inline string, nominal offsets no longer locate the next field.
- A record ends exactly where the fields end, or has the minimal zero padding to a multiple of its alignment ([D084](../DECISIONS.md)). The alignment is 8 bytes when any stored field is 64 bits wide, otherwise 4 (the DBCD writer calls `ResizeToMultiple(4)`). It is chosen once from the field storage, never from rows. Anything else is unsupported, and the error names the record ID, the leftover byte count and the alignment.
- A 64-bit field after an inline string is unsupported, refused before the header with both field names. The cursor doesn't model padding inside a record, which such a field might have, so it could be misread silently. None has been seen; `ABF517CD` stores `AllowableRace` before every string.
- Observed: wow_beta 12.0.1.66220 ItemSparse (layout `ABF517CD`, 64-bit `AllowableRace`) pads all 170,865 readable records to 8 with 0–7 zero bytes. Retail 12.1.5.70575 (`1C17D17F`) and Classic 5.5.4.67849 (`B51F7C79`) have no 64-bit field and pad to 4.

### Invalid text

Default is strict. Sparse callers can opt in with `AllowRawTextEvidence`: an invalid string becomes an empty placeholder paired with a `Db2TextIssue` (`DB2_TEXT_ENCODING_INVALID`, flattened column index, lowercase hex of the exact pre-NUL bytes). The placeholder must never be shown or searched as real empty text. Aggregate evidence is capped at 16 MiB. Known case: Retail 12.1.5.70448 ItemSparse, physical ordinal 173535, `Description_lang`, 121 bytes with one invalid byte; every other name and description in the clear scope is valid.

## Relationships

A section's relationship block is `12 + 8 × n` bytes: u32 count, u32 min, u32 max, then n pairs of (u32 parent value, u32 key). Count and size must agree exactly.

- **Key meaning:** without `0x2`, the key is the zero-based physical ordinal **inside this section**. With `0x2` (flags 6/7), it is a physical record ID in this section. Never a global ordinal, sorted position or copy target.
- min and max are kept as raw evidence. They are not bounds or completeness proof.
- Duplicate keys are rejected, even with equal parents. Parent 0 is real data; a missing pair is not 0.
- At most one relation field: a scalar non-ID integer with DBD width 8, 16 or 32.
- `$relation$` alone is a normal physical field. The block is an optional accelerator; any pair present must agree with the decoded field. A present accelerator on an **unverified** inline relation is unsupported.
- `$noninline,relation$` takes its value only from the block and consumes no record bits. It needs lookup count 1 and exactly one pair per readable physical row; missing coverage is unsupported, never a default.
- Copies inherit the external parent of their terminal physical row.
- A noninline relation before an inline ID is unsupported.
- Positive relationship bytes with lookup count 0 are malformed. A lookup column with no relation field selected is unsupported.
- Empty-table writer form: 0 records, 0 sections, storage info size 0, no pallet/common, only field structures, ending exactly there. It produces columns and no rows, with lookup count 0 or 1.
- Limits: 16 MiB of relationship bytes and 1 M entries across all sections, also charged to the 128 MiB index budget before allocation.

## Keyed (encrypted) sections

A nonzero section key declares a keyed section. Without `AllowPartialEncryptedSections` such a table fails with `DB2_ENCRYPTED`. With it:

- Keyed sections and their copies are excluded and counted. Completion scope is `unencryptedSections`; `complete` means nothing was excluded.
- The header, descriptors, pallet/common data, every encrypted-ID list, ordinary sections, relationship blocks and gaps must all be readable.
- Each BLTE hole must lie wholly inside one keyed section body. A hole anywhere else fails before the first callback.
- Partial rows never prove a row is absent from the table or that a target is unique.
- Encrypted-ID lists are clear, so they are checked: an ID may appear once across all lists (within one list too), and never as a readable physical ID or readable copy target. Otherwise `DB2_INVALID` before the header callback. Follow and reference text treat an ID match in a partial table as the one record with that ID; a file where a hidden record shares a readable ID would make that match wrong without anyone seeing it. The check reuses the encrypted-ID set the reader already builds and charges (64 B per ID against the index budget) and adds one lookup per readable ID and copy target, so no new allocation. The reader revision stays `wdc5-values-v7`: this only refuses files that were never valid. Retail 12.1.5.70448 ItemSparse (48 keyed sections) and Item (49) have no such overlap and still open.

A verified source receipt does not mean a complete table: a fully decoded file can still declare keyed sections, and a projection never verifies the full CKey.

Observed, not a format law: in Classic beta and Retail Item files, all global metadata and section 0 were clear, every BLTE hole matched one keyed section exactly, and each `E` chunk's key name equalled its section key (16/16 and 49/49). Our admission does not depend on the key names matching.

## Limits

| Limit | Default |
| --- | --- |
| Decoded source | 256 MiB |
| Physical + copy rows | 1 M |
| Fields / flattened columns | 256 / 512 |
| Sections | 128 |
| Record (or Sparse map span) | 65,536 bytes |
| Pallet + common data | 32 MiB |
| String | 65,536 bytes |
| ID/copy/auxiliary/relationship index budget | 128 MiB |
| Copy chain depth | 64 |
| Raw text evidence | 16 MiB |
| Relationship bytes / entries | 16 MiB / 1 M |

## DBD definitions

Definitions come from pinned WoWDBDefs (CC BY-SA 4.0) and are read by our own bounded reader.

- `COLUMNS` first: `int|float|string|locstring`, optional `<Table::Column>` reference, name, optional `?`. `?` means **unverified name**, not nullable.
- Version blocks: `LAYOUT` hashes, `BUILD` selectors (exact four-part builds or inclusive ranges, compared numerically), optional `COMMENT`, then fields.
- Field: optional annotations `$id$`, `$noninline$`, `$relation$` (comma-separated, no repeats), name, optional `<N>` or `<uN>` with N in 8/16/32/64, optional `[N]` array length.
- A DBD integer width is a candidate interpretation. It says nothing about physical storage width; pallet, common and bitpacked storage are separate facts.
- Selection needs the exact build **and** the actual layout hash: exactly one matching block, else `DB2_SCHEMA_MISSING` or `DB2_SCHEMA_AMBIGUOUS`. No latest fallback.
- Layout-only selection (D080): when no block lists the build at all, the actual layout hash selects among blocks that list a build with the same major version. Several such blocks: the one whose listed build number is nearest the file's wins; equally near blocks with different fields are `DB2_SCHEMA_AMBIGUOUS`. A listed build never falls back to another block's layout. Field count, widths, arrays and storage are still checked against the header, so a changed layout or field set fails as usual. `DbdDocument.SelectFile` is the one selector; `LayoutCandidates` lists the layouts an unlisted build can open by.
- Fetched definitions (D081), for Tables and, since D101, every feature that reads tables: exact build first. The definition that lists the build (and, once the file is read, carries its layout for that build) wins, bundled before fetched; only when neither lists the build does the layout match above apply, bundled before fetched. Same `SelectFile` rules inside each definition. The fetched file goes through this same bounded reader; its LAYOUT repeat check is a set, so one long LAYOUT line stays linear. `Wdc5TableReader.LayoutHash` reads the header's layout only to pick the definition; the full reader still checks everything.
- A `<Table::Column>` reference names a column, not necessarily the target's ID. `Item.ClassID` targets `ItemClass.ClassID`, not `ItemClass.ID`.
- Limits: 1 MiB, 16,384 lines of 16,384 characters, 512 columns, 4,096 blocks, 256 fields per block, 100,000 selectors, arrays up to 4,096, comments up to 2,000 characters, 8,192 layouts in all, and 4 MiB of estimated layout-selection work: each block's fields once per layout, with every serialized string (the field's comment and its column's name, family, comment and reference) charged 6 bytes per character, the most an escaped character costs. Every WoWDBDefs definition the reader accepted on 2026-10-24 stays under 1.3 MiB. Selection keys are computed once per definition and build.

## References

- Original Java VibeMiner checkpoint `8f353d046d961108f7acc86bfe1381004467afd5`: `Wdc5HeaderInspector`, `Wdc5MetadataInspector`, `FixedSectionIndexParser`, `FieldValueDecoder`, `RecordBitFields`, `SparseSectionIndexParser`, `ValidatedSparseSection`.
- DBCD `e732093f8864240fc5884bd1bba6b02f3dfc0d56` (MIT): `WDC5Reader`, `WDC5Writer`, `DBStructs`, `DB2Flags`, `BaseReader`, `BitReader`. Its zero-ID repair, negative string clamping, zero-filled encrypted records and dictionary overwrites are tool behavior we do not copy. Its reader's 200-byte header constant disagrees with its own reads; the header is 204 bytes.
- WoWDBDefs `b9a63096cdd99b26967f17f9515377ae62cadfd7` (CC BY-SA 4.0): definitions and grammar.

Our reader is original code.
