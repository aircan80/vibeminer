# WoW data reference

Game-data facts the engine depends on: table identities, correct joins, known quirks, definitions, discovery and locale. Byte-level formats live in [docs/formats/](formats/) ([TACT](formats/tact.md), [ROOT](formats/root.md), [WDC5](formats/wdc5.md), [BLP](formats/blp.md), [M2/SKIN](formats/m2-skin.md)). Wire shapes live in [contracts/](../contracts/README.md).

Unless a row says otherwise, layouts are the candidates that pinned WoWDBDefs selects for Retail `wow` `12.1.5.70448`. A candidate is not proof: the engine admits a table only when the actual WDC5 header's table hash and layout hash match the selected definition and the whole source parses to EOF. Other builds select their own layouts.

## Table identities

FileDataIDs and table hashes come from the pinned manifest. "Bundled" means the definition file ships in [the definitions resource](../src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/).

| Table | DB2 FDID | Table hash | Layout | Bundled | Notes |
| --- | ---: | --- | --- | --- | --- |
| Item | 841626 | `50238EC2` | `996192AA` | yes | Classic Beta `1.60.1.70450`: `9A2A4834` (adds ItemPetFoodID, AmmunitionType). |
| ItemSparse | 1572924 | `919BE54E` | `1C17D17F` | yes | Classic Beta `1.60.1.70450`: `6FCC3191`. Variable-length sparse records. |
| ItemClass | 1140189 | `B977271E` | `35680EB8` | yes | Same layout in both studied builds. Historical DBC FDID 841655 is a different source. |
| ItemSubClass | 1261604 | `95D8638F` | `1E67DB87` | yes | DBC FDID 841660. |
| ItemXItemEffect | 3177687 | `00CB674F` | `96F083AD` | yes | `$noninline,relation$ItemID`. |
| ItemEffect | 969941 | `4002A5B1` | `4CA77678` | yes | Same layout in Classic Beta `1.60.1.70450`. Classic layouts carry `$noninline,relation$ParentItemID` (see Item → effects). |
| SpellName | 1990283 | `46C66698` | `782EE721` | yes | |
| Spell | 1140089 | `E111669E` | `E3D134FB` | no | Description/aura text. DBC FDID 841662. |
| SpellEffect | 1140088 | `F04238A5` | `5362E3D4` | no | Keyed by relation SpellID + DifficultyID + EffectIndex. |
| SpellMisc | 1003144 | `C603EE28` | `434B3607` | no | Relational. |
| Creature | 841631 | `C9D6B6B3` | `6E14C900` | yes | |
| CreatureDisplayInfo | 1108759 | `BFDAF9F1` | `7275F5F6` | yes | DBC FDID 801605. Classic builds use `F75F884C`; display metadata accepts both where the CreatureModelData layout is also accepted (D089). |
| CreatureModelData | 1365368 | `7F695FA5` | `0F5449F8` | yes | DBC FDID 801608. Classic layout `3FFEE408` is accepted too: 1.15.9, 2.5.5, 2.5.6, 3.80.1–3.80.2, 5.5.4 and most 5.5.3 builds. 1.15.8, 3.4.5, 3.80.0 and 5.5.0–5.5.2 use another layout and still refuse (D089). |
| CreatureXDisplayInfo | 1864302 | `23CBE682` | `2EA19FCF` | no | `$noninline,relation$CreatureID`. |
| CreatureDisplayInfoGeosetData | 1720141 | `A82DCC8C` | `5E539080` | no | `$noninline,relation$CreatureDisplayInfoID`. |
| CreatureDisplayInfoExtra | 1264997 | `66A5C5B7` | `4D9FE25C` | no | Current layout has no historical hair/skin/equipment fields. |
| ConditionalCreatureModelData | 5525474 | `1D2C33DA` | `C974B48B` | no | Needs a PlayerCondition context to evaluate. |
| Material | 1294217 | `A4C11069` | `BE3E0E4C` | yes | |
| ItemGroupSounds | 1283023 | `9F4206C9` | `A7CEF530` | yes | |

Item calculation and bonus candidates. None is implemented; listed so nobody has to look them up again.

| Table | DB2 FDID | Table hash | Candidate layout |
| --- | ---: | --- | --- |
| RandPropPoints | 1310245 | `5D8CEFA6` | `4FD22743` |
| ItemArmorQuality / ItemArmorTotal / ArmorLocation | 1283021 / 1283022 / 1284818 | `B94A5807` / `029D50FE` / `B1911544` | `2935AA9D` / `EB155D51` / `FB67352F` |
| ItemArmorShield | 1277741 | `FFBB8DC2` | `7E6C94F9` |
| ItemDamageOneHand / OneHandCaster / TwoHand / TwoHandCaster / Ammo | 1277743 / 1277739 / 1277738 / 1277742 / 1277740 | `E0A6BB08` / `F2972767` / `D8C5FD43` / `80A1A0FA` / `9790C2EE` | all `56F30531` |
| ItemBonus / ItemBonusList | 959070 / 1805807 | `8318900A` / `96232523` | `79CD4525` / `22A28539` |
| ItemXBonusTree / ItemBonusTree / ItemBonusTreeNode | 987133 / 3055768 / 987134 | `325582EA` / `4E9D31DC` / `A67E0631` | `5A99E55B` / `DD92E4D6` / `4049606F` |
| ItemBonusListGroup / ItemBonusListGroupEntry | 3755382 / 3025306 | `44898C51` / `8B8247E8` | `FBF090DA` / `24840319` |
| ItemLevelSelector / Quality / QualitySet | 1588911 / 1624937 / 1624933 | `F2C15394` / `F135848A` / `6E85AD38` | `AEACAB9F` / `3016B2C0` / `6EF696F8` |
| ItemBonusListLevelDelta | 1320358 | `39606286` | `52DF470F` |
| ItemScalingConfig / ItemOffsetCurve / ItemSquishEra | 7322705 / 7322704 / 7322706 | `F6AA409E` / `899363A4` / `ACAAF53B` | `7BEABE68` / `6F2157CA` / `ED6F4ECF` |
| Curve / CurvePoint / ContentTuning | 892585 / 892586 / 1962930 | `4BD9DF7A` / `700ECA3A` / `BD3635FF` | `D362535A` / `7D4954A1` / `A3E13004` |
| GemProperties / SpellItemEnchantment | 1343604 / 1362771 | `9C00EA6D` / `E05AC589` | `86487AD2` / `952B72B2` |

A manifest `dbcFileDataID` for a GameTable such as `gtCombatRatings` or `gtItemSocketCostPerLevel` does not identify a current Retail file. Those identities are unresolved.

## Correct joins

Join on the declared semantic column, with exact mathematical values. Never join on record ID because two numbers happen to be equal, never wrap signed values to unsigned, and never pick "the first row" when a lookup is ambiguous.

| Join | Rule | Wrong versions to avoid |
| --- | --- | --- |
| Item → ItemSparse | Left join on the Item record ID. Item rows without a Sparse row stay in the list. Copy-target IDs from either table are included and keep their copy provenance. Sparse-only IDs are counted but are not list rows. | Inner join; physical-only rows; joining by FDID or IconFileDataID. |
| Item name | Only `ItemSparse.Display_lang`. `Display1_lang`, `Display2_lang`, `Display3_lang` are separate fields, not fallbacks. Empty, invalid and missing-Sparse are distinct states. | Inventing `Item 123`; falling back to another string. |
| Item.ClassID → ItemClass | Target is `ItemClass.ClassID` (signed8, not the primary key). Label is `ClassName_lang`. Item.ClassID is signed32. | `ItemClass.ID`. |
| (ClassID, SubclassID) → ItemSubClass | Composite key `(ItemSubClass.ClassID, ItemSubClass.SubClassID)`. Source domains are Item signed32 / unsigned8, targets signed8. A value outside the target range is a miss, not a wrapped match. Label is `DisplayName_lang`. | `SubClassID` alone (the same SubClassID exists under several classes); record ID. |
| Creature → model | `Creature.DisplayID[slot]` → `CreatureDisplayInfo.ID` → `CreatureDisplayInfo.ModelID` → `CreatureModelData.ID` → `CreatureModelData.FileDataID` (the M2). The caller picks the slot (0–3) explicitly. | First nonzero slot, random slot, probability-weighted choice. |
| Item → effects | `Item.ID` → `ItemXItemEffect.ItemID` → `ItemXItemEffect.ItemEffectID` → `ItemEffect.ID` → `ItemEffect.SpellID` → `Spell.ID`; names through `SpellName.ID`. Every bridge row, copy and repeated link stays a separate occurrence. Keys are signed32; zero is a real lookup. | `ItemEffect.ParentItemID` on Retail: the current layout has none. On Classic builds the link is that column instead (below). |
| Item → effects, Classic | `Item.ID` → `ItemEffect.ParentItemID` (`$noninline,relation`, signed32 reference to Item.ID) → that `ItemEffect` row → `ItemEffect.SpellID` → `Spell.ID`; names through `SpellName.ID`. The ItemEffect row is both the link and the effect: its own `ID` is the effect key. Every row with that ParentItemID is one occurrence, copies included. | Looking for ItemXItemEffect first. Treating ParentItemID 0 as "no item" without checking: no observed row has 0, so 0 is still a real lookup. |

Creature field shapes in the selected layouts: `DisplayID` signed32[4], `DisplayProbability` float32[4], Classification signed8, CreatureType unsigned8, CreatureFamily unsigned16, StartAnimState signed8, `CreatureDisplayInfo.ModelID` unsigned16, `CreatureModelData.FileDataID` unsigned32.

Creature display rules:

- `Name_lang` is the only primary/search name. `NameAlt_lang`, `Title_lang`, `TitleAlt_lang` are separate evidence.
- Keep all four slots with their paired probability, including zeros and duplicates. A positive DisplayID with zero or negative probability is still inspectable. A negative DisplayID is invalid reference evidence, never reinterpreted as unsigned.
- Zero slot means no direct candidate. A missing target row in a partial table means "missing in admitted scope", not "does not exist".
- The four direct slots are not the full display list. Complete enumeration also needs CreatureXDisplayInfo.
- A resolved model FileDataID says nothing about appearance. Geoset, conditional model, Extra, texture and material requirements are separate. Three zero DB2 fields do not prove there is no geoset override; that needs complete GeosetData coverage for the display.
- `CreatureModelData.ModelScale`, `GeoBox[6]` and the display scale are source facts. VibeMiner does not reconstruct world size from them.
- Portraits: `PortraitTextureFileDataID` (a texture), `PortraitCreatureDisplayInfoID` (another display) and an app-rendered image are three different things.

Item → effects on Classic (#86). The link table depends on the build. The bundled ItemEffect definition tells which for a listed build, and for an unlisted one whose version-line layouts agree. On the 1.x line they don't (Classic Beta 1.60's `4CA77678` has no ParentItemID), so an unlisted 1.x build such as 1.15.9.70576 is decided by its ItemEffect file's layout:

- No WoWDBDefs definition (pinned `b9a63096`, or the newest fetched `a50ca6f` on 2026-10-25) lists ItemXItemEffect for any 1.14–1.15, 2.5, 3.4/3.80, 4.4 or 5.5 build. Its only Classic-line builds are Classic Beta `1.60.1`.
- Every ItemEffect layout listed for those builds has `ParentItemID`. 5.5.0–5.5.3 (and 4.4.1–4.4.2, 3.4.4–3.4.5, 3.80.0, 1.15.3–1.15.8) use `EB717848`: ID and eight inputs (no PlayerConditionID, TriggerType signed8), then ParentItemID as column 9. Older Classic layouts (`95B79695`, `D696840A`, `F2A2E644`, 1.13–4.4) have ParentItemID too; only `54C10519` onward (9.1+ Retail) and Classic Beta 1.60's `4CA77678` drop it. 5.5.4 (67090–70449), 3.80.2, 2.5.5–2.5.6 and 1.15.9 use `1BF9CF3A`: the ten fields of Retail `4CA77678` in the same order and widths (LegacySlotIndex u8, TriggerType u8, Charges signed16, CoolDownMSec and CategoryCoolDownMSec signed32, SpellCategoryID u16, SpellID signed32, ChrSpecializationID u16, PlayerConditionID signed32, unverified name), then ParentItemID signed32 as column 10.
- Real files from the official CDN, 2026-10-25: MoP Classic `wow_classic` 5.5.4.70579 (not listed by the pinned definitions; it opens by layout) and Classic Era `wow_classic_era` 1.15.9.70003 both have ItemEffect (FDID 969941) with layout `1BF9CF3A`. 5.5.4.70579: 21,455 declared rows, 21,291 readable, 40 encrypted sections, no copies. 1.15.9.70003: 16,965 rows, complete, no copies. Neither file needs Retail's raw-width rule (D079) for Charges, SpellCategoryID or ChrSpecializationID. On 5.5.4.70631 SpellCategoryID's stored values carry constant junk upper bits; D087 narrowing reads it at its declared u16, so it still reaches effects as a declared-width value.
- ItemXItemEffect: 1.15.9.70003's ROOT has no enUS file for FDID 3177687 (`CONTENT_VARIANT_MISSING`); on 5.5.4.70579 no definition covers the build, so it is never downloaded.
- Observed links, same in both builds: Thunderfury (Item 19019) → ItemEffect 101091 (TriggerType 2, SpellID 21992). Sulfuras (17182) → 101343 (slot 1, TriggerType 2, SpellID 21162) and 101344 (slot 2, TriggerType 1, SpellID 21142). Hearthstone (6948) on 1.15.9 → 98264 (SpellID 8690). At most 3 rows share one ParentItemID on 5.5.4.70579, 5 on 1.15.9.70003; none is 0.
- SpellName is `782EE721` in both, as on Retail. Item is `39911A3F` and ItemSparse `B51F7C79` in both.

Effects rules:

- `SpellEffect` rows are selected by SpellID + DifficultyID + EffectIndex. Do not take the first difficulty or index, and `EffectBasePointsF` is not a final amount.
- Spell descriptions contain substitution tokens. Show them as source text until a bounded token evaluator exists.
- A `SpellItemEnchantment` EffectArg is interpreted per effect type. It is not always a Spell ID.

## Data quirks

- **Retail ItemSparse has no MinDamage/MaxDamage.** The names appear in the definition's COLUMNS union for older builds only. Layout `1C17D17F` has DamageType, ItemDelay, DmgVariance and ItemRange, and no armor result either.
- **The ten stat triples are allocation inputs, not amounts.** `StatModifier_bonusStat[10]`, `StatPercentEditor[10]` and `StatPercentageOfSocket[10]` are aligned by position. Keep all ten positions, zeros included. A calculated amount needs level, quality, slot budget (RandPropPoints), rounding and bonus context. Combined-primary-stat codes need a class/spec context.
- **Raw-evidence-only fields are never semantic or join keys.** Some verified field names declare a width narrower than the physical storage. The engine keeps the whole physical value as raw evidence, with a warning and no reference:

  | Table / layout | Field (flattened ordinal) | Declared | Physical |
  | --- | --- | --- | --- |
  | ItemEffect `4CA77678` | Charges (3) | signed16 | Pallet uint32, raw only when its upper bits vary (D108) |
  | ItemEffect `4CA77678` | SpellCategoryID (6) | unsigned16 | Pallet uint32, raw only when its upper bits vary (D108) |
  | ItemEffect `4CA77678` | ChrSpecializationID (8) | unsigned16 | Pallet uint32, raw only when its upper bits vary (D108) |
  | ItemSubClass `1E67DB87` | PrerequisiteProficiency (6) | signed8 | Pallet uint32 |
  | CreatureDisplayInfo `7275F5F6` | NPCSoundID (8) | unsigned16 | Pallet uint32 |
  | CreatureDisplayInfo `7275F5F6` | ObjectEffectPackageID (12) | unsigned16 | Common uint32 |
  | CreatureDisplayInfo `7275F5F6` | AnimReplacementSetID (13) | unsigned16 | Common uint32 |

  Retail 12.1.5.70575 stores all three with one constant pattern above bit 16 (Charges `0xFFFF`, SpellCategoryID `0x3`, ChrSpecializationID `0x14`), so they narrow to their declared width: a stored `0xFFFF0000` is Charges 0 (#95).

  Only these exact definition/layout/field tuples get the exception. ItemEffect `SpellID` (ordinal 7) keeps its normal signed32 `Spell.ID` reference. See [table data](../contracts/table-data-v1.md) and [item effects](../contracts/item-effects-v1.md).
- **Pallet and common values carry junk upper bits.** Any other verified 8- or 16-bit field stored as 32-bit pallet or common values keeps the low bits at its declared width when some value doesn't fit (D079), and keeps its reference: on 5.5.4.67849 Item.ItemGroupSoundsID `0x780107` is ItemGroupSounds 7. The column's `narrowing` says so. Only a column whose misfit values all share one upper-bit pattern narrows (D087), as in every case seen (see [WDC5 numbers](formats/wdc5.md#numbers)); varying upper bits and inline fields that don't fit still fail.
- **Creature replacement textures 11/12/13 map to CDI variation positions 0/1/2.** `CreatureDisplayInfo.TextureVariationFileDataID[4]` keeps all four positions, zeros included. Position 3 is source evidence; texture type 14 is not "slot 4". A required zero or missing texture is unresolved, not a reason to substitute another BLP. The textured appearance (#63) follows this rule: type 0 reads `TXID[ordinal]`, types 11–13 read variation 0–2 of the same admitted row, and `ExtendedDisplayInfoID ≠ 0` is a character model whose baked textures are not resolved.
- **Classic creature textures have several ROOT variants.** On wow_classic_ptr 5.5.4.67849 every creature texture checked (Imp, Felhunter, Rat, cats, frog, snake, owl, whelpling, Infernal) has ROOT memberships that disagree on the CKey, so `all-root-variants-consistent-asset-v1` reports them as `textureAmbiguous`. Each has two memberships with locale `0x173F6` split only by HighResTexture `0x1` (`0x2080001`/`0x2080000`), plus LowViolence `0x80` copies of the same CKeys; the `0x1` one is the optional high-resolution pack at 2× or 4× the side. Geometry textures use the standard one (D095): live on 5.5.4.67849 Imp (display 4449, BC3 256²), Infernal (169, BC1 256²) and Felhunter (850, BC2 256²), Rat, Murloc (two textures), Frog and Owl draw textured, all BC1–BC3 with no palette BLP; Voidwalker and Ragnaros stay `textureWeight`, Succubus `blendUnsupported`, Onyxia `multiTexture`. Snake 125970's standard BLP (BC2 128×32) declares its 8×2 mip as 16 bytes where 4×4 blocks need 32, so the exact-length mip check refuses it as `textureCorrupt`. The Classic Era 1.15.9.70003 Creature table holds a single row.
- **Header `recordSize` is not a Sparse record's length.** ItemSparse records are variable; read them through the offset map.
- **Partial tables are normal.** Keyed (encrypted) WDC5 sections are excluded by policy, and BLTE clear-chunk projections can admit readable rows without verifying the full decoded CKey. Retail `12.1.5.70448` examples:

  | Table | Readable rows (physical + copies) | Excluded |
  | --- | --- | --- |
  | SpellName | 195,096 + 220,986 | 204 + 47 in 44 keyed sections |
  | ItemEffect | 40,166 + 21,924 | 109 + 9 in 38 keyed sections |
  | ItemClass | 21 | complete |
  | ItemSubClass | 178 | complete |

  A missing row in a partial table proves nothing about the build.
- **Malformed text exists.** One invalid ItemSparse Description was seen among 175,625 emitted Retail rows. Text issues are kept per cell; the row stays usable.
- **Classic Beta `1.60.1.70450` table objects are archive-only.** Loose URLs for Item and ItemClass return 404, so archive index lookup and HTTP Range reads are required. Its Item table comes back encrypted.

## Definitions (WoWDBDefs)

Pinned revision `b9a63096cdd99b26967f17f9515377ae62cadfd7`. The bundled manifest is 149,833 bytes, SHA-256 `f7b752a991d6a0321d07ecb7f06e62cd94694c1594d2fe5ce0cb7b243c71559a`.

| Count | Value |
| --- | ---: |
| Manifest entries | 1,346 |
| With a DB2 FileDataID (all distinct) | 1,270 |
| Without a DB2 FileDataID | 76 |
| Also carrying a DBC FileDataID | 378 |
| Definitions bundled | 14 |

The 14 bundled definitions: Creature, CreatureDisplayInfo, CreatureFamily, CreatureModelData, CreatureType, Item, ItemClass, ItemEffect, ItemGroupSounds, ItemSparse, ItemSubClass, ItemXItemEffect, Material, SpellName. They are unmodified upstream bytes with recorded SHA-256 and length.

- **License:** definitions and manifest are CC BY-SA 4.0; WoWDBDefs code is BSD-3-Clause. Keep contributor attribution, revision, digests and modification status. See [LICENSING.md](LICENSING.md).
- **It is community metadata, not a per-build inventory.** The manifest lists known tables; names can be provisional. Which tables a build actually contains comes only from authenticated ROOT/ENCODING. ROOT carries FDIDs and CKeys, not paths, so "every DB2 in this build" cannot be derived from ROOT alone.
- **DBC FileDataIDs are historical identities**, not substitutes for the DB2 source. There is no DBC loader.
- **FileDataComplete is not a bootstrap.** Its definition (FDID 1375799) declares path columns only for specific older builds and has no `12.1.5.70448` layout.

Interpretation rules for the DBD reader:

- `?` marks an unverified name, not nullable data. `$noninline$` means outside the inline record. `$id$` is the identity. `$relation$` is a storage annotation, separate from a COLUMNS foreign-target declaration, and is not always noninline.
- A declared `int32` says nothing about physical storage; pallet, bitpacked and common storage are decoder facts.
- Builds are compared as four numeric components. Several matching layout blocks are an ambiguity, never "take the first". No latest-build fallback.
- A listed build uses exact build and layout. A build no block lists can still open by the file's layout hash, but only from blocks on its version line (same major version); several such blocks go to the one with a listed build number nearest the file's, and a tie between different field sets is ambiguous. The field checks still run, and the result says "matched by layout, build not listed" (D080).
- With fetching on, the Tables view uses exact build first (D081): a definition that lists the build wins, bundled before one fetched from WoWDBDefs, and only then the layout match above, bundled first. Rows read with a fetched definition say "Definition from WoWDBDefs (fetched <date>, <commit>)". Items, Creatures and the other feature views choose the same way and say so too (D101); their reviewed field profiles still have to match the chosen definition.
- Current reader support is WDC5 only, header flags 0/4/5/6/7. Limits: 256 MiB decoded source, 1,000,000 physical + copy rows, 256 fields, 512 flattened columns, 128 sections.

Table relationship rules (Tables explorer):

- Follow only declared COLUMNS targets, and keep the target column. `Item.ClassID` links to `ItemClass.ClassID`, never `ItemClass.ID`.
- Follow by value compares exact integers. `Item.ClassID` is unsigned 8-bit in older layouts and `ItemClass.ClassID` signed 8-bit, so 200 doesn't match −56; a value outside the target column's declared width and sign matches nothing ([D072](DECISIONS.md)).
- `FileData.ID` has no table definition. Its values are FileDataIDs and go to Files.
- Plain integers and arrays get no invented edges. `ItemGroupSounds.Sound` has no SoundKit relation at this pin. `Item.SubclassID` gets no implicit composite ItemSubClass edge in the explorer.
- A non-primary-key lookup can return several rows; show them, don't pick one. One readable row in a partial table does not prove uniqueness.
- The Back/Forward trail keeps the most recent 64 entries, for definition navigation and Follow alike ([TablesExplorer.tsx](../apps/desktop/src/renderer/TablesExplorer.tsx)).

## Reference text columns

The Tables grid shows `value · text` for a declared reference only when its target is on this reviewed allowlist ([#17](https://github.com/aircan80/vibeminer/issues/17); CreatureType and CreatureFamily added on [#62](https://github.com/aircan80/vibeminer/issues/62), where the Creatures view shows them too; [TableReferenceTextService.cs](../src/backend/VibeMiner.Application/TableReferenceTextService.cs)). Every other reference shows the number only. Nothing is inferred from column names or matching numbers.

| Declared target | Text from | Matched on | Key domain | Encoded size | Loads |
| --- | --- | --- | --- | ---: | --- |
| `ItemClass::ClassID` | `ItemClass.ClassName_lang` | `ItemClass.ClassID`, not `ItemClass.ID` | signed8, -128 to 127 | 453 B | automatically |
| `Creature::ID` | `Creature.Name_lang` | record ID | uint32 | 340,038 B | automatically |
| `CreatureType::ID` | `CreatureType.Name_lang` | record ID | uint32 | 326 B ¹ | automatically |
| `CreatureFamily::ID` | `CreatureFamily.Name_lang` | record ID | uint32 | 2,196 B ¹ | automatically |
| `Spell::ID` | `SpellName.Name_lang` (Spell isn't bundled; same ID) | record ID | uint32 | 2,754,982 B | on Load text |
| `Item::ID` | `ItemSparse.Display_lang` (same ID) | record ID | uint32 | 7,988,612 B | on Load text |

Encoded sizes are the text tables' encoded objects on Retail `12.1.5.70448`, measured on 2026-10-21; ¹ CreatureType and CreatureFamily on Retail `12.1.5.70575`, measured on 2026-10-29 (15 and 85 rows). A text table of at most 1 MiB (1,048,576 bytes) encoded loads automatically when a page needs it; a larger one shows the number plus **Load text (≈N MB)**, which loads it at interactive priority through the normal table preparation. Other builds can differ a little, hence the ≈.

- Exact values only. A source value outside the key domain is a miss (`keyOutOfRange`), never wrapped: Item.ClassID 300 or -129 matches nothing, a negative ID never becomes 4294967295. Zero is a real lookup.
- `Item.SubclassID` declares no reference, so it gets no ItemSubClass text.
- A non-ID match column and copy rows can match several rows. Show the count and pick none.
- An empty `_lang` is "empty". No fallback to Description, NameAlt, Display1–3 or another locale.
- Unbundled targets (Map, Faction, AreaTable, SkillLine, CurrencyTypes, ContentTuning…) are "no definition"; bundled targets without a rule (Material, ItemGroupSounds) are "no text column". Both show the number only.
- A miss in a partial or keyed text table is scoped to its readable rows, never "absent". `Item::ID` with no ItemSparse row is its own miss and names ItemSparse.
- Text follows the build's research locale (enUS today).

## Discovery

The engine fetches two documents per product from a fixed origin:

- `https://us.version.battle.net/v2/products/{product}/versions`
- `https://us.version.battle.net/v2/products/{product}/cdns`

Refresh builds fetches that pair for every registered product, at most three products at once ([D062](DECISIONS.md)). The cross-product list sorts by registry product, then by the raw `BuildId` as a number, newest first. That is display order only: BuildId is the build number upstream reports, not a release date, and `## seqn` is never used for ordering.

`https://us.version.battle.net/v2/summary` is the upstream product catalogue. The engine does not fetch it; it uses a fixed registry instead. The `us` in the host does not select the US region; each document contains rows for every region.

Product registry ([Discovery.cs](../src/backend/VibeMiner.Domain/Discovery.cs)):

| Product | Label |
| --- | --- |
| `wow` | Retail |
| `wowt` | PTR |
| `wowxptr` | PTR (additional) |
| `wow_beta` | Beta |
| `wow_classic` | Classic Progression |
| `wow_classic_ptr` | Classic Progression PTR |
| `wow_classic_era` | Classic Era |
| `wow_classic_era_ptr` | Classic Era PTR |
| `wow_anniversary` | Anniversary |
| `wow_classic_beta` | Classic Beta |
| `wow_cn_beta` | edition unconfirmed |
| `wowf` | edition unconfirmed |
| `wow_classic_titan` | edition unconfirmed |

Friendly labels are community names, not Blizzard-authenticated. Keep raw IDs visible.

IDs run up to 19 bytes (`wow_classic_era_ptr`). Storage checks derive their bound from the longest listed ID ([D075](DECISIONS.md)); never hard-code a shorter one.

Facts that break naive code:

- **Versions and CDN tables do not line up.** `wow`, `wowt` and `wow_beta` advertise `sg` and `xx` version rows with no matching CDN row. `wowf` has a `cn` CDN row with no version row. A region needs exactly one matching CDN `Name` row to be routable; a missing route does not invalidate the advertisement, and you never fall back to US. A missing route is permanent source data, so it is a regional note and the check is still complete; an ambiguous or unusable route is a problem ([D076](DECISIONS.md)).
- **Seqn is not build age.** `## seqn` is an upstream document sequence number. It is not a build number or a release order.
- **Region is not locale.** Version rows carry no locale. Never infer enUS from `us`.
- **The CN NetEase host is deliberately unsupported.** The transport accepts only HTTPS `level3.blizzard.com`, `level3.ssl.blizzard.com` and `<2–8 letters>.cdn.blizzard.com` with a `/tpr/<name>/` path. Anything else is an explicit refusal.
- **Build identity is every versions field except Region and CDNConfig** ([D061](DECISIONS.md); this replaces the earlier whole-row rule). Group key = product + VersionsName + BuildId + BuildConfig + ProductConfig + every other column (KeyRing and unknown columns included, absent vs present-empty kept apart). Region, route and CDNConfig are not part of it. Canonical revision `vibeminer-build-identity-v2`.
- **CDNConfig can be republished for the same build.** BuildConfig pins encoding, root, install and download; CDNConfig only lists archives. `wow_anniversary` 2.5.6.70006 was advertised with `f065eade…` (older) and then `ff64ecfb…`, otherwise identical, and both gave the same download ledger. The build keeps a newest-first CDN config history: newest is the one seen most recently, then the one that appeared later, then key order. Each entry keeps the newest versions-row receipt per region. A discovery projection carries the newest 16 entries plus the total count; older ones stay in the catalog. Configs seen in the same checks tie and are ordered only by key, and "a newer one exists" is judged per source region, never against another region's config. Retained objects and ledgers verify by EKey, not CDNConfig, but archive locators and native retention sources are keyed by CDN config, so every admission and pinned job keeps the CDN config it was made with. Archives that only an older CDN config lists stay reachable through admissions and pins made with it while the CDN serves them, and the shared pipeline falls back to them for an object the open's CDN config does not locate ([D069](DECISIONS.md)).
- `ConfigPath` in the CDN row is not authority to download ProductConfig. A KeyRing hash does not mean keys are available.

Metadata grammar: pipe-delimited header descriptors like `BuildConfig!HEX:16` (case varies: `VersionsName!String:0`), `#` comments, optional `## seqn = N`, LF or CRLF, fields matched by name. Parser limits ([MetadataParser.cs](../src/backend/VibeMiner.Protocols.Tact/MetadataParser.cs)): 1 MiB per document, 64 columns, 10,000 rows, 16,384 characters per line.

Required versions columns: Region, BuildConfig, CDNConfig, VersionsName. Required CDN columns: Name, Path, Hosts. BuildId, ProductConfig, KeyRing, Servers and ConfigPath are kept when present.

## Locale

The engine is enUS-only today. Table data, Items, Creatures and effects reject any `researchLocale` other than `enUS`.

- Table selection policy `enus-consistent-root-variants-v1`: every ROOT membership with the enUS bit (`0x2`) must agree on one CKey. Disagreement is ambiguity; there is no invented Windows/content-flag filter.
- Asset policy `all-root-variants-consistent-asset-v1` (icons, models): every membership for the FDID must agree on one CKey. It is not interchangeable with the table policy, because localized tables legitimately differ per locale.
- Geometry textures only: `root-texture-standard-resolution-v1` (D095) takes the HighResTexture-clear CKey when the memberships differ in nothing but `0x1` and LowViolence copies. See [root.md](formats/root.md#selection-policies).
- BuildConfig and CDNConfig carry no locale list. There is no authoritative "supported languages" source yet; ORing every ROOT locale mask over-reports because shared memberships carry broad masks.
- Other locale bits for reference: esES `0x80`, esMX `0x1000`. Spain and Latin American Spanish are different sources.

## References and licensing

All parsing is our own code. These were read as references only; no code, generated tables or numeric data was copied or run.

| Reference | Revision | License | Used for |
| --- | --- | --- | --- |
| [WoWDBDefs](https://github.com/wowdev/WoWDBDefs/tree/b9a63096cdd99b26967f17f9515377ae62cadfd7) | `b9a63096cdd99b26967f17f9515377ae62cadfd7` | CC BY-SA 4.0 (data), BSD-3 (code) | Bundled definitions and manifest |
| [pywowlib](https://github.com/wowdev/pywowlib/tree/55276dc5c2195da7fe136638a2a59716622f8c65) | `55276dc5c2195da7fe136638a2a59716622f8c65` | MIT | M2 chunks, texture type enums (11–13 vs 14) |
| [wow.export](https://github.com/Kruithne/wow.export/tree/1b625d0f9943be4d8f8e124dbb7a04f1fc7da8f3) | `1b625d0f9943be4d8f8e124dbb7a04f1fc7da8f3` | MIT | M2/SKIN loading, render flags |
| [WoWFormatLib](https://github.com/Marlamin/WoWFormatLib/tree/1abf1bfad68705a2064b90fba1cb8bcd22588ff0) | `1abf1bfad68705a2064b90fba1cb8bcd22588ff0` | not recorded | M2/SKIN readers |
| [DBCD](https://github.com/wowdev/DBCD/tree/e732093f8864240fc5884bd1bba6b02f3dfc0d56) | `e732093f8864240fc5884bd1bba6b02f3dfc0d56` | not recorded | WDC5 auxiliary data order |
| [TACTSharp](https://github.com/wowdev/TACTSharp/tree/67fd53ac852ca297e9bf95bb41d1a97da202eba4) | `67fd53ac852ca297e9bf95bb41d1a97da202eba4` | not recorded | ROOT locale bits, TVFS |
| [TrinityCore ItemTemplate.cpp](https://github.com/TrinityCore/TrinityCore/blob/545be4dddc11a88dff77a25c17954457c03fecf9/src/server/game/Entities/Item/ItemTemplate.cpp) | `545be4dddc11a88dff77a25c17954457c03fecf9` | GPL-2.0-or-later | Item stat formula dependencies |
| [SimulationCraft sc_item_data.cpp](https://github.com/simulationcraft/simc/blob/5537d8813dfc59707927722b1e23ffa6fd67ee59/engine/dbc/sc_item_data.cpp) | `5537d8813dfc59707927722b1e23ffa6fd67ee59` | GPL-3.0 | Item stat formula dependencies |

TrinityCore and SimulationCraft are GPL. They were read to understand which inputs and rounding steps item formulas need. No code was copied, and their approximations are not treated as client behaviour.
