# Optional descriptive labels beside table references

Approved next-scope research/specification for [#109](https://github.com/aircan80/vibeminer_java/issues/109), checked 2026-10-18. Implement **after milestone 5**, then the approved top navigation and Tables sequence. This document changes no production capability. Labels are optional additions beside raw reference values; they never replace the values or turn VibeMiner into a generic automatic relationship/name resolver.

## Evidence and first mapping

Use WoWDBDefs revision `b9a63096cdd99b26967f17f9515377ae62cadfd7`. The pinned [Item definition](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/Item.dbd) explicitly annotates **ClassID → ItemClass.ClassID**. The target is **not ItemClass.ID**. The pinned [ItemClass definition](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/ItemClass.dbd) declares `ClassName_lang` as `locstring`; choosing that column as a descriptive label is **VibeMiner’s reviewed display policy**, not a DBD-provided preferred-name attribute.

For candidate build `12.1.5.70448`, Item layout `996192AA` declares source ClassID as signed32. ItemClass layout `35680EB8` declares noninline ID, localized ClassName_lang, signed8 ClassID, float PriceModifier and signed32 Flags. Only ID is annotated as the primary key. These exact selector facts and file hashes are recorded in [table research](../../design/tables/RESEARCH.md). They do not establish that the actual ItemClass file/header/sections have been acquired or admitted by VibeMiner.

The maintainers’ [DBD grammar](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/README.md) supplies field types, annotations and explicit target columns, but no universal label-selection convention. Their [DBCD documentation](https://github.com/wowdev/DBCD) demonstrates named-field access after loading a defined table, not automatic descriptive joins. The pinned [WDC5 reader](https://github.com/wowdev/DBCD/blob/e732093f8864240fc5884bd1bba6b02f3dfc0d56/DBCD.IO/Readers/WDC5Reader.cs) is a format reference, not a runtime dependency or proof of new Java decoder support. DBCD’s installation/hotfix features are excluded from VibeMiner.

Initial scope is therefore one explicit mapping:

| Source | Relation target | Display column | Policy |
| --- | --- | --- | --- |
| Item.ClassID | ItemClass.ClassID | ItemClass.ClassName_lang | Exact admitted source/target context, requested locale, unique supported match only |

No other table receives guessed labels from columns named Name, Display or Description. A plain integer or integer array without an explicit reviewed relation remains plain. For example, ItemGroupSounds.Sound has no annotated SoundKit relation at this pin.

## Admission and meaning

Both source and target must have independently admitted exact-build source association, actual header/layout, supported decoding, pinned schema, locale and coverage. The target requires new validated extraction/indexing before labels can be implemented; the current Item/ItemSparse projection does not supply ItemClass rows. A known definition or retained encoded file is insufficient.

A match uses the annotated **numeric target column**, not the primary-key index by assumption. Compare values exactly: source signed32 value `300` must not wrap to signed8 `44`; negative or zero values must not be reinterpreted as unsigned IDs or universal null sentinels. Field-specific sentinel behavior requires separate evidence.

Because ClassID is not the declared target PK, no uniqueness constraint is implied. Multiple candidate target rows mean **ambiguous**, even if their label text is identical. An indexed single match is insufficient to establish uniqueness when excluded/keyed sections or unindexed copy rows could add other matches. For the MVP, require an admitted lookup domain with sufficient completeness and explicit copy policy to establish one supported physical target; otherwise return partial/copy-unindexed status without a plain resolved label. Do not choose the first row or silently expand target copies.

A successful label remains an attributed client-data candidate, not authoritative game semantics, obtainability or server state. Retain source value, target physical ID/column, display column, mapping revision, schema/source provenance, coverage and locale. A raw value such as `12` may display as `12 · Authored class A`; the authored research example maps to target row ID `301` whose ClassID is `12`.

## Locale and empty values

MVP uses the exact requested research locale. Region is not locale. Raw root masks and DB2 locale metadata remain distinct decoder/source facts. There is **no implicit English/default-language/other-build fallback**.

An existing target with an empty physical ClassName_lang is **empty label**, not absent target or permission to borrow a name. Missing requested-locale data, an unindexed target and no match in a sufficiently checked lookup scope are different outcomes. A future fallback policy needs explicit user-visible locale attribution, cache identity and tests; it is not included here. Preserve strict Unicode exactly, without silent normalization/replacement, and render labels as plain text.

## Visible states and behavior

| Outcome | Cell behavior |
| --- | --- |
| Disabled / not requested | Raw value only; no lookup work |
| Loading | Raw value stays usable; small nonblocking indication |
| Resolved physical label | Raw value plus optional label; relation action names the actual target column |
| Empty physical label | Raw value; accessible indication that the matched label is empty |
| No configured display mapping | Raw value and ordinary supported relation navigation; no guessed text |
| Target not ready / not loaded | Raw value; explicit target-preparation action elsewhere if supported |
| No match in checked scope | Raw value; scoped nonmatch, not a global absence claim |
| Ambiguous / partial target / copy unindexed | Raw value; explain the specific coverage/match limitation |
| Keyed/unsupported target | Raw value; use actual capability result, not key declaration alone |
| Missing/corrupt local evidence | Remove any stale label; typed source failure, no automatic repair |

Labels do not change record identity, raw-value sorting/filtering or copy-value semantics. Copying the raw cell retains the original value; copying a label can be a separate explicit action. Opening a relation preserves the exact build/locale and supports existing table back/forward/tab behavior. A label failure must not blank a valid source row.

## Bounded lazy composition

Following architecture review, place a small mapping/label adapter above the exact-build table provider. The desktop requests deduplicated references for a visible page asynchronously; cells do not perform HTTP, SQL, decoding or blocking work. Lookup is retained-only. Opening the optional-label setting does not acquire a target table, prepare indexes or repair sources silently.

Bound visible values per batch, queued/actual jobs, target rows examined, UTF-8 label bytes, cache entries/bytes and cancellation intervals. Freeze concrete limits with the implementation contract and test refusal paths; a page-size limit alone does not bound an unindexed non-PK scan. Prefer a validated target-column index. Empty/ambiguous/negative results are scoped observations, not permanent facts.

Shared technical cache identity includes target artifact/projection identity, relation-definition/display-policy revision and locale; artifact identity already binds relevant physical source, schema and decoder revisions. Do not add observation timestamps merely to duplicate shared labels. Each consumer separately validates its exact source/target associations, target coverage and current SQL generation. Missing/corrupt/replaced targets invalidate old positive and negative results. Build/locale/page changes retire obsolete requests; late results cannot repaint a new context. No generic graph/query framework is required for this single mapping.

## Small implementation sequence and acceptance

1. Admit and test the exact ItemClass schema/header/required physical modes, source proof, localized projection and lookup-domain/copy coverage. Fail unsupported inputs honestly; do not require arbitrary tables to be supported.
2. Add the explicit mapping and bounded retained-only batched lookup with meaningful tests: non-PK target ID differs, signed-width mismatch, duplicates, zero/negative raw values, partial/keyed/copy coverage, missing target/locale, empty string, strict Unicode and source replacement.
3. Add optional labels to the approved Tables UI after milestone 5/navigation prerequisites. Native checks cover disabled/no work, lazy page batching, cancellation/late result isolation, raw values always visible, exact-context relation navigation and missing/corrupt target behavior.

No all-table auto-label promise, cross-build join, implicit target download, local install/cache import, copy expansion, full decoded-CKey upgrade or server-semantic claim belongs to this increment. Retain **WoWDBDefs contributors** attribution and the [CC BY-SA 4.0 definition-data license](https://github.com/wowdev/WoWDBDefs/blob/b9a63096cdd99b26967f17f9515377ae62cadfd7/LICENSE.md); DBCD has its separate MIT license.

## Future locale exploration

[Locale switching #114](https://github.com/aircan80/vibeminer_java/issues/114) is future scope: a build Home selector may change the active research locale while keeping the exact build. Table labels and other localized projections must refresh with context fencing and complete cache identity. Shared verified content remains deduplicated; unavailable translations are explicit. Switching research locale does not silently alter the authorized bulk-retention scope or grant full-build download consent. This does not expand milestone 5 or the initial label implementation.

Per-view asynchronous progress is tracked in [#115](https://github.com/aircan80/vibeminer_java/issues/115). Visible label batches must not block JavaFX or trigger a request per cell. Progress and results belong to the exact active consumer context; unavailable targets and partial lookup coverage retain explicit states.
