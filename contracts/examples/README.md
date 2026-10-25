# Authored protocol examples

These JSON documents are synthetic contract fixtures, not acquired Blizzard metadata or application results. The repeated hashes, version labels, timestamps, and `.invalid` URL are authored examples. They exercise the canonical schema, generated TypeScript types, frontend tests, and C# serialization compatibility.

`discovery-page.json` contains one observed build without any locally prepared payload. `empty-page.json` represents a fresh catalog before an explicit remote check. `discovery-catalog-page.json` is a cross-product page: builds of two products, every registered product's saved status (checked with two regional notes for regions advertised without a CDN route, partial with an ambiguous route problem, offline and never checked) and one product whose refresh did not complete.

`download-workspace.json` is measured DL-scope Home; `download-unavailable-workspace.json` preserves unknown size when no DL is declared. `download-page.json` contains authored exact-EKey object rows, independent of legacy research memberships. `download-summaries.json` covers every `download.summaries` state, including a measured file-list scope with unresolvable ROOT files, one still measuring after Open and one limited to its manifest with the reason: a measured build with a current running figure and a dated check, one still counting content already on disk, one whose ledger could not be read, an opened build with no measured manifest and a never-opened build. Its `revision` is the summary revision the read was taken at, which `download.changes` waits on (D077).

`table-catalog-page.json` and `table-definition.json` model provider definition metadata with unchecked selected-build source and unavailable rows. The pinned community structure facts are attributed WoWDBDefs data; no game records are included.

`table-data-layout-only.json` is one authored ItemClass `tables.data` reply whose build no definition block lists, so the file's layout hash chose the block (`schemaMatch: layoutOnly`, D080). Values and receipts are synthetic.

`table-reference-text.json` is one authored Item page of reference text: a resolved, empty, several-match, not-found, out-of-range and unreadable ClassID value, plus a bundled target with no reviewed text column and an unbundled target. Its IDs and labels are synthetic.

`table-follow.json` is one authored [follow](../table-follow-v1.md): Item.ClassID = 7 followed to the two ItemClass rows whose ClassID is 7. Its IDs and labels are synthetic.

`creatures-snapshot.json` is the shared authored C1 Host/schema fixture: one partial Creature source, three emitted rows, name/slot counts and exact field references. It contains no acquired Creature rows or names and establishes protocol shape only.

`creature-display-metadata.json` is the authored C2 metadata-only wire fixture. It preserves an original partial Creature scope, a copy-target Display edge and a separate complete Model edge. IDs, values and receipts are synthetic; positive file metadata with `appearance: notEvaluated` establishes no acquired model, image or renderability.

`item-effects-readiness.json`, `item-effects-page.json`, `item-effect-details.json` and `item-effect-names.json` are authored selected-Item effects wire fixtures. They preserve a partial Bridge with separate index/source ordinals and a copy occurrence, partial Effect copy evidence with all nine strict/raw/unverified fields, and complete Name evidence for zero IDs. Repeated effect/spell values remain distinct edge keys. These establish wire shape and controlled App/Host behavior, not native source qualification or enabled production capabilities.

`settings-snapshot.json` is an authored [settings](../settings-v1.md) read: a settings file that was read, with one saved value the engine doesn't understand, so Community build history uses its default and says so. The folder path and time are samples.
`inventory-files-page.json` and `inventory-file-detail.json` are authored Files wire fixtures ([inventory-v1](../inventory-v1.md#files)): one ROOT FileDataID with two memberships, one of them unresolved (no EKey or encoded size), and one install name. They show shape only. A legacy full catalog resolves every ROOT/install membership before it is sealed; a file catalog prepared after Open keeps unresolved ones explicit ([D071](../../docs/DECISIONS.md)).

`file-catalog-status.json` lists one authored `inventory.catalog` reply per state ([inventory-v1](../inventory-v1.md#file-catalog-after-open)): pending while writing, pending after a failed attempt and waiting for another build, paused with the build's Download all, stopped, ready with exact counts, failed after three attempts with its cause, unsupported, a build never opened through that region, and one freed from Settings (`removed`, D085).

`prepared-data.json` is an authored `settings.storage.builds` reply ([settings-v1](../settings-v1.md#prepared-data-per-build)): a ready file list with table rows partly shared with another build and a current stored figure, one still being prepared and counting, one freed whose stored figure could not be read, and one opened build with nothing prepared and no measured manifest. `prepared-data-freed.json` is the matching `settings.storage.free` reply. Sizes are synthetic.

`creature-geometry-ready.json` is an authored ready geometry reply without the optional `appearance` member, which reads as no textured draw. `creature-geometry-textured.json` is the same reply with a ready textured appearance and its VMA1 manifest; `creature-geometry-lease.json` and `creature-geometry-appearance-lease.json` are the matching `creatures.geometry.open` replies. IDs, hashes and receipts are synthetic.
