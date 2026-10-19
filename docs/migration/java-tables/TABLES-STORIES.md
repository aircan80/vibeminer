# Tables exploration and top area navigation — delivery stories

**Status: approved milestone-6 planning. Production implementation starts only after milestone 5 is complete.** Research and contract preparation may proceed now. GitHub tracks [milestone 6](https://github.com/aircan80/vibeminer_java/milestone/6): top navigation #112, Explorer tables #113 and optional relationship labels #109. These document labels are delivery slices, not duplicate issues.

Use the [selected Home/Tables top navigation](../../design/top-navigation/README.md), Explorer A within Tables, an internal left table catalogue and the 340 px reference inspector. Other table/home/sidebar alternatives remain design history. Higher-level Items/Spells and other future areas stay disabled. See [architecture sequencing](https://github.com/aircan80/vibeminer_java/blob/8f353d046d961108f7acc86bfe1381004467afd5/docs/BUILD-BROWSER-IMPLEMENTATION.md) and [relationship-label contract](TABLE-RELATION-LABELS.md).

## Shared acceptance boundaries

A schema catalogue describes metadata, not acquired rows. Actual source/header/layout, required decoder modes, locale and coverage must be admitted before presenting readable data. Preserve exact build/configuration, actual source receipts, schema/provider/decoder revisions and physical/copy/excluded scope throughout navigation. Missing schema, unknown filename, unavailable/encrypted source, unsupported format, corrupt evidence and absent target are separate outcomes.

Table providers sit above the shared build-content facade and reuse the same verified bytes/CAS. JavaFX performs no HTTP, SQL, indexing or decoding. No second per-build payload cache, generic relational engine or speculative plugin framework is needed. Existing partial physical Item/name coverage does not become arbitrary table support.

## T1 — Switch Home and Tables without losing the build

As a researcher, I want top-level Home/Tables navigation, so I can change areas while studying the same exact build.

Maps #112; depends on completed milestone 5.

- Top navigation changes areas. Tables alone owns its left catalogue; no duplicate global sidebar or user-named workspace flow.
- Preserve build/locale, Home readiness/download mode, active shared jobs and area selections. Navigating away does not cancel unrelated content work.
- Future areas remain disabled; no Items/Spells/Files/Assets/Compare/findings implementation.
- TDD covers context transitions and stale responses; native keyboard/focus and Home regression checks establish real behavior.

## T2 — Admit one exact table-provider cohort

As a table explorer, I want to distinguish available definitions from readable records, so I understand what the catalogue supports.

Maps #113 research/provider foundation. Research now; implementation after milestone 5.

- Expose attributed table names, stable file identities, selected schema structures and separate source/decoder capability states. A definition never fabricates rows or a ready badge.
- Match actual build/table/layout and pinned provider revision; missing, ambiguous or incompatible definitions do not fall back to latest or a similar filename.
- First useful vertical is the admitted Item grid plus separately verified ItemClass target and reviewed relationship. Material/ItemGroupSounds remain candidates until independently admitted, not mandatory expansion of this slice.
- Validate ItemClass actual header/layout `35680EB8`, required storage features, locale and proof scope before advertising readable target records. Synthetic fixture success alone does not establish a real source.
- Tests cover wrong build/layout/locale, malformed schema, unknown names, unsupported modes and explicit offline missing states. Separate official evidence is bounded and explicitly authorized, not implicit in planning.

## T3 — Browse bounded table records and inspect structure

As a researcher, I want an Explorer grid and inspector, so I can inspect supported rows and their interpretation.

Maps #113; depends T2.

- Selecting a catalogue table opens bounded pages with a stable declared key/tie-breaker and schema-bound query context. Table-name search filters metadata, not all record content.
- Add only reviewed allowlisted filters/sorts needed by the admitted table; no arbitrary SQL or general query DSL. Distinguish not prepared, no admitted rows and whole-table absence.
- Inspector retains raw values, widths/sign where relevant, candidate names/types/annotations, attribution and physical/copy/excluded coverage. Noninline ID, storage relationship and foreign-key declarations remain distinct.
- Shared derived artifacts are keyed by exact source/interpretation; builds reference them without copied table payloads. Cancellation or late corruption cannot expose a ready partial artifact.
- Test pagination, tied/null/wide values, corrupt/missing sources, unsupported storage, stale cursors/generations and offline retained queries. Rendering or selecting a cell never implies consent to acquire or prepare another table.

## T4 — Follow a declared relationship to its exact target column

As a researcher, I want linked raw values to open the declared target, so I can investigate evidence without guessed joins.

Maps #113; depends T2/T3 and admitted source/target schemas.

- Pin source table/row/field/raw value and declared target table/column in the same exact build/locale. Initial edge: `Item.ClassID → ItemClass.ClassID`, never `ItemClass.ID`.
- Compare source int32 and target signed-8 values exactly without truncation/wrap. Unannotated SubclassID does not invent a composite relationship; zero is not universally null.
- A non-primary-key lookup finding one physical row does not prove uniqueness if keyed sections/copies/excluded scope might contain others. Require complete validated lookup domain and copy policy, or return PARTIAL_TARGET instead of unique success.
- No match, multiple matches, partial target domain, unindexed copy, incompatible schema and unavailable/encrypted target remain distinct. Multiple matches require explicit user choice, never arbitrary first row or hidden truncation.
- Tests include deliberately different target ID/ClassID, duplicate keys, out-of-range numeric value, partial/keyed/copy domain, zero, orphan and locale/source mismatch.

## T5 — Preserve an investigation trail

As a researcher, I want breadcrumbs and Back/Forward, so I can return from related records without losing context.

Maps #113; depends T3/T4.

- Catalogue selection starts a fresh trail; following a relationship extends it with source field/value and explicit target choice.
- Restore exact table, selected row, allowed query/filter/sort, page and inspector focus. Build/locale/schema/artifact changes invalidate stale entries rather than opening replacement data.
- User-driven cycles have bounded history; no recursive graph expansion. Cancelled or superseded lookup cannot repaint selection or steal focus.
- Test reset/append, ambiguous choices, cycles, page restoration, invalidation and asynchronous races with native keyboard review.

## T6 — Validate one optional descriptive-label mapping

As a researcher, I want a description beside an FK value, so I understand it without losing exact identity.

Maps #109; depends T4.

- First mapping is explicitly reviewed application display policy: Item.ClassID → ItemClass.ClassID, display ClassName_lang. It is not a generic rule derived from any string column.
- Preserve raw FK and separate target record ID, both copyable/navigable. Candidate provider attribution and requested locale remain visible; no fallback locale/build or gameplay claim.
- Mapping includes source/target schema and decoder applicability, exact target column, display field, locale rules, complete lookup-domain/copy policy and display-policy revision.
- Empty physical string, missing locale, no match, duplicate/partial target, unsupported display type, unindexed copy and corruption stay distinct. No inherited name for an unindexed copy.
- Tests prove exact non-ID matching and enough target-domain evidence for uniqueness, alongside missing/empty/ambiguous outcomes.

## T7 — Resolve visible labels lazily with bounded shared work

As a researcher, I want optional labels without hidden downloads or blocked scrolling.

Maps #109; depends T6 and bounded table-query ports.

- Preference off performs no label lookup. On resolves a bounded visible/prefetch page in deduplicated batches; repeated keys share work rather than per-cell queries.
- No FX-thread I/O/decoding/network or automatic target preparation. Missing dependencies show a requirement, not a background download from the renderer.
- Derived label identity includes actual target artifact, schema/provider/decoder interpretation, locale, relationship and display-policy revision. Each consumer still validates exact source association/generation; wall-clock/build labels are not cache identity.
- Context changes, corruption, rebuild and preference toggles cancel/detach stale consumers without damaging shared work. Raw values remain visible while labels load/fail.
- Test bounded admission/results/ambiguity, repeated keys, off-screen cancellation, invalidation, no HTTP and no silent duplicate-target suppression. Do not invent latency promises before measurement.

## T8 — Qualify the integrated Explorer on native JavaFX

As a desktop researcher, I want navigation and table investigation to work by keyboard at usable scales.

Maps #112/#113/#109 qualification; depends accepted preceding slices.

- Compare actual JavaFX to approved Home/Tables references at reference size and 100%/200%: top areas, left catalogue, trail and 340 px reference inspector. Responsive layouts must not clip raw values or rely on tooltip-only evidence.
- Authored offline flow: Home → Tables → admitted table → page/structure/record → non-ID edge → explicit ambiguous choice → Back/Forward → labels on/off → Home, preserving readiness/download mode.
- Cover loading/empty/missing-schema/unsupported/encrypted/corrupt/no-target/partial-domain/ambiguity, keyboard focus and full plain-text copying. No markup execution or fabricated production rows.
- Record exact revision and actual native/platform checks. Four packaged GUI smokes do not imply every research interaction or assistive technology passed on all OSes. Official-source cohort acceptance remains separate from synthetic UI evidence.

## Order and completion

Research T2/T4/T6 now; freeze source/admission, provider/query, relationship-domain and display-policy contracts. Complete milestone 5 before production navigation/table changes. Then deliver T1 with narrow T2/T3, T4/T5, and T6/T7/T8 in reviewable tested increments.

Units establish identity, schema selection, exact numeric joins and failure states. Real SQLite/content-provider integration establishes query/coverage behavior. Native screenshot/usability checks establish presentation. Independent mining review establishes honest interpretation. A completed sub-slice cannot close broader arbitrary-table or complete-WoW decoding acceptance. Higher-level Items/Spells, future monitoring, GC and broad comparison remain excluded.

## T9 — Open data asynchronously with request progress

Maps [#115](https://github.com/aircan80/vibeminer_java/issues/115); shares acquisition/priority behavior with #96. Applies to #113, #109 and later data areas.

- Blocking acquisition, verified local reads, parsing, indexing and queries run outside JavaFX. Publish bounded, coalesced immutable progress updates and fence them by exact build/locale/request generation.
- Show locating, downloading, verifying and decoding/indexing stages. Byte percentages require a trustworthy request denominator; unknown totals use indeterminate progress. Download completion is not table readiness. Keep per-view progress separate from build-wide Retained percentage.
- Interactive requests attach to/promote shared transfers while bulk retention continues. No second cache/download path. Cached reopening performs no gratuitous download or fake animation.
- Detaching/canceling one view preserves other consumers and actual worker/publication ownership. Loading, partial admitted data, unavailable/keyed/unsupported, canceled, offline and retry states are distinct. Partial target coverage cannot prove uniqueness or exhaustive absence.
- TDD uses controlled slow transfers, known/unknown lengths, cached zero-network reopen, shared-flight priority, context changes and error stages; real JavaFX checks cover responsiveness, keyboard and progress screenshots.
