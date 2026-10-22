# Table follow v1

Capability `tables-follow-v1` adds `tables.follow`. It lists the target rows of one declared reference whose target column equals one exact source value, e.g. Item.ClassID = 12 to the ItemClass rows where ClassID = 12. Policy revision `table-follow-v1`, decision [D072](../docs/DECISIONS.md).

## Request

`productId`, `buildIdentity`, `sourceRegion`, `researchLocale` (`enUS`), the source `tableId` and `artifactId`, `columnIndex` (flattened source column, 0–511), `value` (canonical decimal integer: no `+`, no leading zeros, no `-0`, at most 20 digits), the target `targetTableId` and `targetArtifactId`, optional `limit` (1–50, default 50) and `after` (canonical ordinal into the matches). Unknown parameters are `INVALID_REQUEST`. The desktop sends a request ID so it can cancel the lookup with `request.cancel`.

The engine checks, in order:

- The source artifact is the current admission for this exact build, source and locale (`TABLE_NOT_PREPARED`, `TABLE_ARTIFACT_STALE`).
- The source column is an integer column whose pinned definition declares a reference (`TABLE_FOLLOW_NOT_DECLARED`). Columns with a raw physical interpretation declare none.
- The target table is the provider entry with exactly the declared table name (`TABLE_FOLLOW_TARGET_MISMATCH`), with a bundled definition that lists the build (`DEFINITION_NOT_BUNDLED`, `SCHEMA_BUILD_NOT_LISTED`).
- The target artifact is the target's current admission (`TABLE_NOT_PREPARED`, `TABLE_ARTIFACT_STALE`).

The read is local. It never downloads or prepares anything; the desktop prepares the target through the ordinary `tables.prepare` flow first.

## Result

`TableFollowPage` echoes the request, the declared `reference`, the source `column` name, `targetColumnIndex`, `targetIsId`, the target's coverage `scope` and `after` (`"0"` when omitted). `targetRange` is the target column's declared integer domain: 0 to 4294967295 for the record ID, otherwise the width and sign of the admitted layout's field, or null when the definition gives none.

| State | Meaning |
| --- | --- |
| `ready` | `page` is a `TableDataPage` of the target rows whose target column equals `value`. Its `totalCount` is the exact number of matching emitted rows, copies included. 0 is a miss: with scope `complete` the admitted table has no such row; with `unencryptedSections` the miss covers the readable rows only and is never an absence. |
| `keyOutOfRange` | The value can't be stored in the target column (`targetRange`), so nothing was searched. Never wrapped or truncated: Item.ClassID 200 doesn't match ItemClass.ClassID −56. |
| `unindexed` | The target column isn't the record ID and the target has more than 4,096 rows. No scan ran and nothing is claimed. `problem` has `TABLE_FOLLOW_UNINDEXED`. |
| `unsupported` | The admitted target layout has no exact integer column with the declared name. `problem` has `TABLE_FOLLOW_LAYOUT`. |

`page` is null unless the state is `ready`. Values compare as exact integers. A record-ID target uses the ID directory, like the `recordId` filter of `tables.rows`.

## Bounds

One lookup runs at a time, shared with `tables.referenceText`, through the same four verified readers. A non-ID target column is indexed by one bounded scan of at most 4,096 rows, kept as values and row ordinals only. Pages hold at most 50 rows and 512 KiB, trimmed like `tables.rows`. Cancellation stops the read at the next row.
