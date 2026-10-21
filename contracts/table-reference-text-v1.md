# Table reference text v1

Capability `tables-reference-text-v1` adds `tables.referenceText`. It shows the localized text of the row a declared reference points at, next to the raw value, for one visible page of a prepared table. The reviewed rules are in [the WoW data reference](../docs/WOW-DATA-REFERENCE.md#reference-text-columns). Policy revision `table-reference-text-v1`.

## Request

The same flat parameters as `tables.rows`: `productId`, `buildIdentity`, `sourceRegion`, `tableId`, `researchLocale` (`enUS`), `artifactId`, optional `limit` (1–50), `after` and `recordId`. The engine reads that exact page itself, with the same byte trimming as `tables.rows`, and takes the keys from its admitted cells. The caller never supplies values, target tables or columns. Unknown parameters are `INVALID_REQUEST`.

The read is local. It never downloads or prepares anything, and an unprepared text table is reported as `notPrepared`. The desktop then prepares that table through the ordinary `tables.prepare` flow (the shared acquisition pipeline at interactive priority), automatically or on request depending on `loadPolicy`, and asks again.

## Result

`TableReferenceTextPage` echoes the context, `artifactId`, `after` (`"0"` when omitted) and `recordId`, plus `rowIds` in page order. The desktop applies a result only when all of these match the page on screen.

`columns` lists every column with a declared reference, by flattened column index. `state`:

| State | Meaning |
| --- | --- |
| `ready` | The text table is admitted and readable. `values` has one entry per distinct value of this column on the page. |
| `notPrepared` | The text table has no current admission for this exact build and locale. |
| `noTextRule` | The target is a bundled table without a reviewed text column. Number only. |
| `noDefinition` | The target's definition is not bundled. Number only. |
| `unsupported` | The text table's definition does not list this build, or its admitted layout lacks a usable match or text column. |
| `unindexed` | The match column is not the record ID and the target has more than 4,096 rows. No scan is attempted. |
| `unreadable` | The text table's artifact or source proof failed verification. `problem` has the code. |

`textSource` names the table the text actually comes from (`SpellName` for a `Spell.ID` reference), its text and match columns, the artifact and its coverage scope. It is null for `noTextRule` and `noDefinition`. `measuredEncodedBytes` is the reviewed encoded size of that text table on Retail 12.1.5.70448, and `loadPolicy` is `automatic` when it is at most 1 MiB, otherwise `onRequest`. The desktop prepares an `automatic` table by itself and an `onRequest` one only after **Load text (≈N MB)**.

Each value has the exact source `value`, `matchCount` (emitted rows, copies included) and `state`:

| State | Meaning |
| --- | --- |
| `resolved` | One row; nonempty `text`, at most 4,096 UTF-16 units. |
| `empty` | One row; the text cell is exactly `""`. No fallback. |
| `notFound` | No admitted row. With scope `unencryptedSections` this is a miss in the readable rows only, never an absence. |
| `keyOutOfRange` | The value can't exist in the match column (for example ClassID 300 against signed8, or a negative record ID). Never wrapped. |
| `multiple` | Several rows match; `matchCount` says how many and none is picked. |
| `unreadable` | One row, but its text cell has invalid UTF-8 evidence. |
| `textTooLong` | One row, text longer than 4,096 UTF-16 units. Not truncated. |

`row` gives the matched row's ID and copy identity for a single match, otherwise null.

## Bounds

One batch runs at a time engine-wide. Text tables are read through at most four dedicated verified readers, so a page read never re-verifies the source artifact. Record-ID targets use the binary ID directory. A non-ID match column is indexed by a bounded scan only when the target has at most 4,096 rows; the index keeps keys and row ordinals only. The serialized result is at most 512 KiB, otherwise `TABLE_REFERENCE_TEXT_LIMIT`; raw values stay usable. Cancellation stops the read at the next row or page boundary.
