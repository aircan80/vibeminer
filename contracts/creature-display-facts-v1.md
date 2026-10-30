# Creature display facts v1

Capability `creature-display-facts-v1` adds `creatures.display.facts` and `creatures.labels` ([#62](https://github.com/aircan80/vibeminer/issues/62)). It is advertised whenever [display metadata](creature-display-metadata-v1.md) is. The Creatures view presents a slot's CreatureDisplayInfo and CreatureModelData rows from it without picking columns itself. Policy revisions `creature-display-facts-v1` and `creature-labels-v1`.

## creatures.display.facts

Request: exactly the `creatures.display.read` parameters (`productId`, `buildIdentity`, `sourceRegion`, `researchLocale` `enUS`, `artifactId`, canonical `creatureId`, `slot` 0–3). Anything else is `INVALID_REQUEST`. The read is local, like `creatures.display.read`: it never prepares or downloads, and an unprepared target reads `notPrepared` in `metadata`. It shares that method's two local read credits and the Host's four Creature publication credits.

Result `CreatureDisplayFactsResult`:

- `metadata` is exactly what `creatures.display.read` returns for the same request at that moment, with every state, absence and proof.
- `factsRevision` is `creature-display-facts-v1`.
- `records` has one entry per chain role whose target matched exactly one row (`metadata.sources[i].row` is set), display before model. Missing, ambiguous, unknown, unsupported and every early stop have no record for that role; their meaning stays in `metadata`.

A record names its `role`, `tableId` (`bfdaf9f1` or `7f695fa5`), `table` and `recordId` (the matched row, the one `metadata` links), then:

| Role | `facts`, always in this order |
| --- | --- |
| display | `model` (ModelID), `scale` (CreatureModelScale), `textureVariations` (TextureVariationFileDataID[0–3]) |
| model | `file` (FileDataID), `scale` (ModelScale) |

A fact is picked by base field name, so Retail (`7275F5F6`, `0F5449F8`) and Classic (`F75F884C`, `3FFEE408`) layouts give the same keys. Each has `state` `value` with its cells, or `unsupported` with none when the admitted layout lacks the field; never zero. `raw` holds every other non-ID column of the row, in column order. Classic layouts don't have ConditionalCreatureModelID, MountMaxBankingAngle, WalkSpeed or RunSpeed, so those simply aren't in their `raw`.

A cell is `{column, columnIndex, value}`: the flattened column name, its index into that target snapshot's `columns`, and the value as the reader decoded it. A row whose values can't be kept whole (a text issue, or a value count that differs from the columns) has no record. Nothing is calculated: no scale is multiplied, no texture or model is resolved, nothing is defaulted.

## creatures.labels

No parameters. Returns `CreatureLabelLists`: `labelRevision` `creature-labels-v1` and `classification`, the reviewed list for Creature.Classification (signed8): 0 Normal, 1 Elite, 2 Rare elite, 3 World boss, 4 Rare, 5 Trivial, 6 Minus. Any other value has no label. CreatureType and CreatureFamily text isn't here: it comes from those tables' `Name_lang` of the exact build through [reference text](table-reference-text-v1.md).

## Bounds

At most two records, three facts per record, 16 cells per fact and 512 raw cells; columns 0–511. The whole reply shares the Creature 512 KiB frame and its one bounded encoder; an oversized reply is `CREATURES_RESULT_LIMIT`, never truncated. Stale authority is `CREATURES_STALE` or `CREATURE_DISPLAY_STALE` as for `creatures.display.read`. An engine without this capability leaves the Creatures view showing the chain from `creatures.display.read` and says the facts aren't available (`CREATURE_DISPLAY_FACTS_UNAVAILABLE`).
