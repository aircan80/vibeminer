# Item facts v1

Capability `item-facts-v1` adds `items.facts` and `items.labels` ([#61](https://github.com/aircan80/vibeminer/issues/61)). It is advertised whenever [Items](items-v1.md) is. Facts are a reviewed projection of the rows `items.inspect` already returns, so the desktop presents an item without picking columns or naming values itself. Policy revision `item-facts-v1`, label revision `item-labels-v1`.

## items.facts

Request: exactly the `items.inspect` parameters (`productId`, `buildIdentity`, `sourceRegion`, `researchLocale` `enUS`, `artifactId`, canonical uint32-string `itemId`). Anything else is `INVALID_REQUEST`. An artifact that isn't the current Items admission for that exact context is `ITEMS_ARTIFACT_STALE`. The read is local: it never prepares or downloads.

Result `ItemFactsResult`: the echoed `context`, `artifactId`, `itemId`, both revisions, `sparse` and `facts`.

- `sparse` is this item's ItemSparse row: `present`; `missing` when absent from a complete ItemSparse; `unknown` when absent from a Partial one (encrypted sections not loaded). It is never `present` without a row.
- `facts` is null exactly when the item is outside the admitted Item scope (inspect's `detail: null`). ItemSparse-only rows exist, so `sparse` is then looked up by the same ID, not assumed: a Sparse-only row is `present` with null facts. Otherwise `facts` holds all 48 keys, always in this order: description, display1–3, icon, quality, itemLevel, itemSquishEra, contentTuning, playerLevelCurve, itemLevelOffsetCurve, itemLevelOffset, class, subclass, inventoryType, requiredLevel, requiredSkill, requiredSkillRank, requiredAbility, minFaction, minReputation, allowableClass, allowableRaces, bonding, stackable, maxCount, containerSlots, buyPrice, sellPrice, vendorStackCount, durationInInventory, statType, statAmount, statPercentEditor, statSocketShare, socketType, socketBonus, gemProperties, damageType, itemDelay, dmgVariance, itemRange, flags, zoneBound, instanceBound, requiredHoliday, priceVariance, priceRandomValue.

A key reads every column of Item, then ItemSparse, whose base field name (without `[i]`) matches its field: `allowableRaces` takes Retail `AllowableRaces[0–1]`, Classic 1.15.9 `AllowableRace[0–1]` and a single unarrayed `AllowableRace`; `socketBonus` and `gemProperties` take both spellings. Arrays keep their index order. `statAmount` is `StatModifier_bonusAmount[0–9]`: Classic layouts (for example `B51F7C79`) store flat stat amounts there, beside the stat types; Retail layouts have none (it is `unsupported`) and store allocation inputs in `statPercentEditor` and `statSocketShare` instead.

Each value is one exact source cell: `tableId` and `table` (Item `50238ec2` or ItemSparse `919be54e`), flattened `column`, `columnIndex` into that source snapshot's columns, the emitted `recordId`, the exact `value` string and `label`. That cell is what a Tables link opens.

| State | Meaning |
| --- | --- |
| `value` | At least one cell. A `0` is a value read from the source. |
| `missing` | Only ItemSparse holds the field and `sparse` is `missing`. No cells. |
| `unknown` | Only ItemSparse holds the field and `sparse` is `unknown`. No cells; nothing is inferred. |
| `unsupported` | Neither admitted layout has the field. No cells; never zero. |
| `invalidText` | A text cell's source bytes aren't valid UTF-8 (`TableTextIssue`). Its value is `""`; the raw bytes stay in `items.inspect`. |

`agreement` says how the two tables' copies relate. Cells are always Item first, then ItemSparse, each with its own table.

| Agreement | Meaning |
| --- | --- |
| `single` | Only one table holds the field (or the ItemSparse row is absent). |
| `agree` / `disagree` | Both hold it with the same number of cells, compared in order: InventoryType, ContentTuningID and ItemSquishEraID on Retail; RequiredLevel on Classic. |
| `separate` | Both hold it with a different number of cells, so the copies aren't compared: Classic `Item.DamageType[0–4]` beside one `ItemSparse.DamageType`. |

Both sides stay; none is picked.

## items.labels

No parameters. Returns `ItemLabelLists`: `labelRevision` and four lists of `{value, label}` in ascending value order, `value` a canonical decimal string.

| List | Values |
| --- | --- |
| `quality` | 0 Poor, 1 Common, 2 Uncommon, 3 Rare, 4 Epic, 5 Legendary, 6 Artifact, 7 Heirloom, 8 WoW Token |
| `inventoryType` | 0 Not equippable through 30 Profession gear (31 entries) |
| `bonding` | 0 No binding, 1 Binds when picked up, 2 Binds when equipped, 3 Binds when used, 4 Quest item |
| `trigger` | ItemEffect.TriggerType 0 On use, 1 On equip, 2 Chance on hit |

A fact value carries `label` only for `quality`, `inventoryType` and `bonding`, and only when the value is exactly a listed one; every other value has `label: null`. Bonding 7–9, TriggerType 3–7, stat types, socket types and masks stay unlabelled. The lists are app-owned and WoW-reviewed, not client text. One table in the engine serves both methods.

## Bounds and what it never does

At most 64 facts and 16 cells per fact; columns 0–511; labels 1–64 characters. The whole reply shares the 512 KiB Items response budget, less the same envelope reserve as `items.inspect`. A fact with more cells or a reply over budget fails with `ITEMS_FACTS_LIMIT`; nothing is truncated. It calculates nothing: no final stats, armor, damage, scaled item level, vendor price or eligibility, and it reads no ItemClass, ItemSubClass, ItemEffect or SpellName rows; those stay with [classifications](item-classifications-v1.md) and [effects](item-effects-v1.md).
