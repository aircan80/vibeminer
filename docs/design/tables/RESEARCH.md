# Table browser research and mockup dataset

Research/design scope [#107](https://github.com/aircan80/vibeminer_java/issues/107), checked 2026-10-18. This supports three **table-level** browsing concepts, not high-level Items/Spells pages. No production table browser, new decoder capability, live CDN request, or actual game row is implied. The designs may show authored rows and clearly labelled candidate structures.

## Primary sources and immutable pin

Use WoWDBDefs revision **`b9a63096cdd99b26967f17f9515377ae62cadfd7`** throughout these examples:

- [README / DBD grammar](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/README.md)
- [manifest.json](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/manifest.json)
- [Item.dbd](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/Item.dbd)
- [ItemClass.dbd](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/ItemClass.dbd)
- [Material.dbd](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/Material.dbd)
- [ItemGroupSounds.dbd](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/ItemGroupSounds.dbd)

Pinned bytes were inspected for all four definitions. The three target files’ SHA-256 values are respectively `6e0517e1ae17d3528aefe3eac6ad2b417ae7361d8d5ad77d0c743ffd4d126151`, `94332b8ef22c9efa431f6ec35690a92896daf05c7f677fa6ce049006ab487ae7`, and `e108620add5ef881b6189ee7f8c0bb0962c59254ec0dfe405abc889c9061f6de`. Item’s hash is `076ea4c7e80c891b399cc77f285c807071080c0fed95701e447f7c98024ae120`.

Web review also checked the maintainers’ current [grammar](https://github.com/wowdev/WoWDBDefs/blob/master/README.md), [target definitions](https://github.com/wowdev/WoWDBDefs/blob/master/definitions/ItemClass.dbd), and [DBCD documentation](https://github.com/wowdev/DBCD). Moving master links are discovery/reference links; the immutable revision above controls the mockup field facts. DBCD’s pinned [WDC5 reader](https://github.com/wowdev/DBCD/blob/e732093f8864240fc5884bd1bba6b02f3dfc0d56/DBCD.IO/Readers/WDC5Reader.cs) is a format implementation reference, not VibeMiner’s runtime or proof that an arbitrary table is readable.

## What a definition can and cannot tell us

The manifest identifies known tables by table hash, optional name and optional DBC/DB2 FileDataID. Names can be placeholders; missing FileDataID is not a missing table. Its catalogue is not an inventory of the selected build. This pin has 1,346 manifest entries, including 1,270 with a DB2 FileDataID: **provider catalogue counts, not build table totals**.

DBD `COLUMNS` supplies candidate names, scalar families (`int`, `float`, `string`, `locstring`), explicit target-table/target-column annotations, comments and unverified-name markers. A selected BUILD/LAYOUT block supplies field order, declared integer signedness/width, array length and special annotations. `$id$` identifies the declared key; `$noninline$` means the value is outside the ordinary record fields; `$relation$` is a storage/relationship annotation, not permission to invent an arbitrary target table. Keep those distinctions visible in a structure inspector. The grammar is maintained by the [definition authors](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/README.md).

An unverified `?` remains a confidence marker, not a nullable-column declaration. A DBD foreign-key annotation is community interpretation, not an SQL constraint or guaranteed target row. It can target a non-ID column. It does not guarantee uniqueness, imply cascading actions, define a composite key, or turn every similarly named integer into a relation.

Actual values, record counts, copy rows, section encryption/readability, string bytes, physical compression, file presence and source authenticity come from acquired CDN objects and validated decoders—not these definitions. Definitions alone cannot populate the Data tab. Match the exact selected build/layout and physical header before claiming compatibility; missing/ambiguous definitions or unsupported storage modes need explicit states. DBCD documents broad format reading plus definition dependence; that breadth does not transfer automatically to VibeMiner. Its local-file/hotfix features are not part of this app.

## Four-table candidate cohort

All following definition blocks explicitly include **12.1.5.70448** at this pin. They are candidate structures for one same-build/enUS mockup. Target table headers and actual rows were **not acquired** for this design exercise. The known Item cohort does not prove ItemClass/Material/ItemGroupSounds body compatibility.

| Table | Table hash | DB2 FileDataID | Candidate layout | Key annotation |
| --- | --- | ---: | --- | --- |
| Item | `50238EC2` | 841626 | `996192AA` | `$noninline,id$ID<32>` |
| ItemClass | `B977271E` | 1140189 | `35680EB8` | `$noninline,id$ID<32>` |
| Material | `A4C11069` | 1294217 | `BE3E0E4C` | `$noninline,id$ID<32>` |
| ItemGroupSounds | `9F4206C9` | 1283023 | `A7CEF530` | `$noninline,id$ID<32>` |

`int32` below describes the signed DBD declaration; `uint32` reflects its `u32` notation. External/noninline row identity and raw file representation remain separate decoder facts. These are **structure-view formats**, not executable SQL DDL or enforced relational constraints.

### Item: useful selected columns

| Column | Declared selected-block type | Metadata |
| --- | --- | --- |
| ID | int32, noninline | Declared primary key |
| ClassID | int32 | Explicit reference to **ItemClass.ClassID**, not ItemClass.ID |
| SubclassID | uint8 | No target annotation in this definition; do not invent a composite ItemSubClass link |
| Material | uint8 | Explicit reference to Material.ID |
| InventoryType | int8 | Plain value; no relationship annotation |
| IconFileDataID | int32 | Explicit reference to FileData.ID; target outside this four-table mockup |
| ItemGroupSoundsID | uint32 | Explicit reference to ItemGroupSounds.ID |
| Field_12_0_0_63534_010 | uint8 | Name is marked unverified in COLUMNS; preserve that marker |
| RecraftReagentCountPercentage | float | Plain candidate field; no derived mechanics claim |

The complete selected Item block also contains SheatheType, Sound_override_subclassID, ContentTuningID, ModifiedCraftingReagentItemID, CraftingQualityID, ItemSquishEraID and OrderSource. A mockup can show a clearly labelled selected-column subset; it must not describe nine displayed columns as the complete physical schema.

### ItemClass: complete selected block

| Column | Declared type | Metadata |
| --- | --- | --- |
| ID | int32, noninline | Declared primary key |
| ClassName_lang | locstring | Localized candidate; requested locale retained |
| ClassID | int8 | Target of Item.ClassID; **not the declared primary key** |
| PriceModifier | float | Plain candidate |
| Flags | int32 | Preserve numeric/raw form; do not invent flag meanings |

`SubclassMapID` exists in historical definitions but is absent from this selected block. Do not mix it into the modern example.

### Material: complete selected block

| Column | Declared type | Metadata |
| --- | --- | --- |
| ID | int32, noninline | Declared primary key |
| Flags | int32 | Plain candidate |
| FoleySoundID | uint32 | Explicit reference to SoundKit.ID |
| SheatheSoundID | uint32 | Explicit reference to SoundKit.ID |
| UnsheatheSoundID | uint32 | Explicit reference to SoundKit.ID |

SoundKit is outside the four-table example; an attempted reference can demonstrate **target table not loaded** without fabricated row contents. The pinned [SoundKit definition](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/SoundKit.dbd) confirms the target table exists; that is not proof a requested record exists. Historical MaterialID is absent from this block—do not add its old self-reference.

### ItemGroupSounds: complete selected block

| Column | Declared type | Metadata |
| --- | --- | --- |
| ID | int32, noninline | Declared primary key |
| Sound | uint32[4] | Fixed array; **no DBD foreign-key annotation** |

The four Sound elements can expand inline or appear as Sound[0] through Sound[3]. They must not become SoundKit hyperlinks merely because their names suggest audio. This is an intentional contrast between a real annotated reference and a plain integer array.

## Illustrative authored relationship, never game data

The following illustrates the relationship semantics. Each prototype uses its own authored record identifiers while preserving these target-column distinctions; these numbers are not a transcription of every prototype. Keep **Authored rows · candidate DBD structure** clearly visible:

- Item: ID `900001`, ClassID `12`, SubclassID `1`, Material `21`, InventoryType `3`, IconFileDataID `800001`, ItemGroupSoundsID `41`; optional unverified field `0` and float `0.25`.
- ItemClass: ID `301`, ClassID `12`, ClassName_lang `Authored class A`, PriceModifier `1.25`, Flags `0`. The intentionally different ID proves navigation uses the annotated **ClassID** target column.
- Material: ID `21`, Flags `0`, FoleySoundID `501`, SheatheSoundID `502`, UnsheatheSoundID `0`.
- ItemGroupSounds: ID `41`, Sound `[701, 702, 0, 704]` with plain array elements.

These values are invented to exercise the UI and are not asserted to be semantically valid game records. Add an authored missing-target case (e.g. Item.Material `99`), an unloaded target (Material.FoleySoundID), and an optional duplicate ClassID fixture to show an ambiguous non-PK lookup. Do not label raw zero as null or “no relation” without field-specific evidence: it can be displayed as `0`, with no matching admitted target if that is what the mock dataset establishes.

## Cell navigation and structure presentation

Clicking a relationship cell should show the exact action, e.g. **Open ItemClass where ClassID = 12**. Search the annotated target column within the **same exact build, locale and compatible schema context**. A single admitted target can open/highlight that row; multiple targets show a chooser/result list; no admitted match says so without asserting global absence. Missing target table, unsupported schema, partial/keyed scope and source failure are different outcomes. Do not silently fetch a different build/locale, change the source, or expand copies.

Keep table-level navigation: table catalogue/search → paged Data grid; Structure lists columns/types/PK/reference/array/confidence and Relationships lists explicit references. A formatted schema panel may resemble DDL, but must say **DBD structure** and avoid unsupported UNIQUE/NOT NULL/CASCADE semantics. Numeric non-reference cells remain copyable/filterable without looking navigable.

Back/forward restores source table, selected row, filters, sort, page/cursor and scroll. New-tab actions retain the exact source context. A breadcrumb/trail can read `Item[900001] → Material[21]`; it should identify the target column on non-ID hops. Table name search is not a promise of cross-table value search. Missing data and definition-only previews are explicit, not successful empty queries.

## Attribution and licensing

Credit **WoWDBDefs contributors**, link the immutable definition revision and [upstream LICENSE.md](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/LICENSE.md), and retain CC BY-SA 4.0 attribution/share-alike requirements for reused/adapted definition data. The repository distinguishes its designated code’s BSD-3-Clause license from definition data’s CC BY-SA 4.0; do not label the whole repository BSD. [DBCD](https://github.com/wowdev/DBCD) has its separate MIT license. This research summarizes a narrow structure subset with authored row examples; it does not bundle game rows, full downloaded definitions, or imply Blizzard endorsement.
