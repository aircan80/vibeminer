# VibeMiner UI design references

The first screen follows **[desktop discovery](desktop-discovery/index.html)** ([D059](../DECISIONS.md)), with every product in one grid as in [all-products.html](desktop-discovery/all-products.html) ([D062](../DECISIONS.md)). It replaced the layout of **guided build discovery** ([`build-discovery/index.html?concept=guided&state=ready&product=wow`](build-discovery/index.html?concept=guided&state=ready&product=wow)), whose meaning it keeps. The repository keeps that exact HTML and its local navigation dependencies. The other concepts in its selector are historical alternatives. The older standalone `mockups/atlas` prototype is superseded and is not a reference. Home, Tables and Files follow **[desktop home](desktop-home/index.html)** ([D063](../DECISIONS.md)). Settings follows **[desktop settings](desktop-settings/index.html)** ([D070](../DECISIONS.md)).

Cross-screen rules (tokens, states, progress, focus, verification) are in [UI guidelines](../UI-GUIDELINES.md).

## Reference views

| View | Reference | Role |
| --- | --- | --- |
| Desktop discovery | [`desktop-discovery/all-products.html`](desktop-discovery/all-products.html) on top of [`desktop-discovery/index.html`](desktop-discovery/index.html) (states via `?state=`; see its [README](desktop-discovery/README.md)) | Selected first screen |
| Guided discovery | [`concept=guided&state=ready&product=wow`](build-discovery/index.html?concept=guided&state=ready&product=wow) | Superseded layout; its discovery contract still applies |
| Preparation | [`option=dedicated&state=indexUnknown`](build-preparation/index.html?option=dedicated&state=indexUnknown) | Selected in Java; carried forward |
| Desktop home | [`desktop-home/index.html`](desktop-home/index.html) (areas via `?area=home\|tables\|files`, states via `?state=`; see its [README](desktop-home/README.md)) | Selected Home, Tables and Files |
| C build overview (Home) | [`concept=archive&state=ready`](build-mining-home/index.html?concept=archive&state=ready) | Superseded layout; its Home facts and wording still apply |
| Home and Tables with area navigation | [Home](top-navigation/home.html), [Tables](top-navigation/tables.html) | Superseded layout; its Tables meaning still applies |
| Desktop settings | [`desktop-settings/index.html?view=settings`](desktop-settings/index.html?view=settings) (states via `?state=`; see its [README](desktop-settings/README.md)) | Selected Settings view and its top-bar entry |
| Downloads and activity | [Option A: side panel](download-activity/index.html?concept=drawer&state=active&panel=open) | Option A selected as the default; B kept for comparison |
| Items view | [`items-view/index.html`](items-view/index.html) (states via `?state=`; see its [README](items-view/README.md)) | Selected Items view, approved on [#61](https://github.com/aircan80/vibeminer/issues/61) ([D098](../DECISIONS.md)) |

The HTML files can contain older alternatives. Only the views above guide the app. Their selectors, demo controls, counters, product aliases and quantities are authored examples, not production controls or facts.

## Horizontal navigation and Tables

- Area navigation is horizontal under the build header: Home · Tables · Files | Items · Creatures. There is no global sidebar.
- Tables uses the workbench layout: catalogue on the left, then records with Data, Structure and Relationships, then a 340px inspector. Files is a list and an inspector.
- The desktop-home reference draws work that is not built yet: the community listfile ([#21](https://github.com/aircan80/vibeminer/issues/21)). The app doesn't draw it until that issue lands: Files has no community names. Files is per FileDataID with filters when a research catalog is prepared ([#20](https://github.com/aircan80/vibeminer/issues/20)); otherwise a note says the file catalog isn't prepared ([#35](https://github.com/aircan80/vibeminer/issues/35)) and what works today, and the download manifest's encoded objects are listed below it.
- Follow by column value ([#19](https://github.com/aircan80/vibeminer/issues/19), [D072](../DECISIONS.md)) follows the reference: reference cells are clickable by mouse, the record inspector has **Show ItemClass rows where ClassID = 12**, the trail reads `Item › ItemClass`, and the filter chip says **Followed from Item** with **ClassID = 12 ✕**. Departures, and why:
  - **Files hand-off.** **Find FileDataID 800001 in Files** opens Files searching FileDataID 800001, other filters cleared, with **← Return to Item record 900001** as in the reference. Without a prepared file catalog there is no FileDataID search, so Files says **FileDataID 800001 can't be looked up yet** above the encoded-object list instead of presenting that list as the file.
  - **States the reference doesn't draw.** A value outside the target column's range (**No ItemClass row can have ClassID = 300**), a non-ID target too large to search (**ItemClass can't be searched by ClassID**), an unusable target layout, and a target whose rows aren't available (**No search ran**) each get a calm notice that claims no absence; the chip and value stay visible. One readable row of a partly readable table is listed but not picked (**1 readable ItemClass row has ClassID = 7**), because protected sections may hold more. Older engines without `tables-follow-v1` keep definition navigation, and the inspector says the engine can't follow by value.
  - **One Tab stop in the grid.** Only the selected row's button (or the first row's) is in the Tab order; Up/Down/Home/End move between rows. That puts the inspector's Follow action a few Tabs away instead of one per row.
  - **No reference text on followed rows.** Reference text reads a `tables.rows` page itself (D067), so a followed page shows raw values only.
- Reference text ([#17](https://github.com/aircan80/vibeminer/issues/17)) follows the reference: every declared reference cell has the teal, 600, dotted-underline value, and allowlisted ones add the muted `· text`, `· …` while pending, and the inspector source line. Departures, and why:
  - **Load text notice.** The owner chose to load only text tables of at most 1 MiB encoded automatically ([D067](../DECISIONS.md)). A larger one (SpellName, ItemSparse) gets a calm notice above the grid, **ItemSparse text not loaded** with **Load text (≈8.0 MB)**, and the same button in the inspector. The reference has no such state because it assumes every text table loads by itself. After Stop or a failed load (any text table, ItemClass included) the notice stays with the outcome and a scoped Retry such as **Retry ItemSparse text**; if the text table's definition can't be read, it says so with **Retry ItemSparse definition**. When another table is loading, the reason shows as text next to the disabled button. Notices with an action never become scroll boxes: at enlarged text the heading and button share one line, and when even that would cut the first record row, the notice goes to one line with the heading and reason shortened by an ellipsis and the button whole. If that still isn't enough (a wider system font wraps the records toolbar, as on Linux CI), only **Readable rows available · Partial** steps aside, because the records foot always ends with `· Partial` for a partial table. Errors, the selection note and every notice with an action stay. A failed page read with **Retry table read** gets the same one-line treatment, so when it and a text action both need the room at that size, they win over the first row; the previous rows stay scrollable.
  - **Codes.** Every reference-text notice keeps its heading in plain words (**ItemClass text could not be loaded**, **Reference text unavailable for this page**) and shows the code as `<code>` at normal weight at the start of the detail line. On one line the heading and detail shorten with an ellipsis (full text in their title) while the code and the button stay whole; the inspector field always has the full sentence with the code. A focus left on a Load text or Retry that disappears goes to the grid (notice) or back to the field (inspector).
  - **`· not loaded` cell.** Before any request a large text table's cells read `· not loaded`; after Stop they read `· loading stopped`. The reference only has `· …`, which here means "loading now" and would be untrue for a table nobody asked for.
  - **18rem ellipsis on `.dh-rt`.** Text can be up to 4,096 characters, and the grid cells don't wrap. The text part is capped at 18rem with an ellipsis so one long name can't widen its column past the window; the full text is in the cell title and the inspector. The raw value is never cut.
  - **Batch failure notice.** If a page's text can't be read (for example `BUSY` or `ENGINE_UNAVAILABLE`), a calm **Reference text unavailable for this page** notice names the code, with **Retry reference text** when retrying can help. The reference doesn't draw this failure.
- Implementation differences from the reference: the fixture label sits in the status bar; Data has a one-line **Table data** bar like Structure's **DBD structure** bar, naming the research locale and admission scope (at enlarged text the **Table data** heading joins the title row instead); the record inspector heading stays **Record #ID** with the table name below it; Relationships looks each target up in the local provider catalogue and says **Can’t follow: no bundled definition** when it isn't bundled. At enlarged text the calm “A field uses raw physical values…” notice on Data is hidden (each column header still names its raw physical type), and the record pager shows **First / Previous / Next** (accessible names “First page of records” and so on); at 100% it reads **First page / Previous page / Next page** as in the reference.
- Prototype rows, controls and coverage figures are examples. What production shows is defined in [tables-v1](../../contracts/tables-v1.md) and [table-data-v1](../../contracts/table-data-v1.md).
- [Table research](tables/RESEARCH.md) records the definition sources. The historical Java [Tables stories](../migration/java-tables/TABLES-STORIES.md) and [relationship-label rules](../migration/java-tables/TABLE-RELATION-LABELS.md) keep the larger phased scope.

## Discovery contract

- One grid lists every registered product ([D062](../DECISIONS.md)); a Product filter narrows it. Show the exact product ID and a reviewed label.
- Refresh builds is the explicit metadata check, for every product at once. Selecting a row doesn't download payloads or open the build.
- All / Prepared / Current share one list. Publication, local evidence and remote availability stay separate facts.
- Group regions only when every identity-bearing reference agrees. Keep the original receipts, and show the chosen acquisition route separately.
- There's one primary Open Build action, with bounded automatic preparation and explicit failure/retry.
- Keep the exact selection through async work. Stale results can't navigate to another context.
- Show readiness separately from activity and transfer progress.

## Preparation and overview

- Use the dedicated preparation screen for real configuration/DL acquisition, verification and download-size preparation. The [size-first flow](../SIZE-AND-DEMAND-ARCHITECTURE.md) means Home doesn't wait for full research-catalog indexing.
- Healthy saved artifacts skip the screen after revalidation.
- Back detaches without cancelling. Explicit Cancel shows Stopping until cleanup completes.
- Show numbered actual stages with short explanations. Keep manifest encoded size, dated local credit and unprepared research data distinct.
- The C overview keeps exact product/build/locale labels, plain readiness, a separate Retained measurement and the On demand / Download all policy. The mode control changes real retention policy. Fixture percentages never stand in for current evidence. See [product](../PRODUCT.md), [async work](../ASYNC-AND-JOBS.md) and [download activity](download-activity/README.md).

## Fidelity

- Carry the selected mockups' HTML structure, fonts, sizes, weights and colours into React faithfully ([D021](../DECISIONS.md)).
- Discovery follows the desktop reference: toolbar, build listbox, pinned-action inspector and status bar in a window that never scrolls. Home, Tables and Files follow the desktop-home reference in the same kind of window.
- Fix real accessibility or usability defects on purpose and record them. Don't drift silently.
- Record future redesigns as new design references instead of editing the imported source. Then compare the implementation against the new reference.
- Compare real Electron captures with the reference at 1280×800 and 1440×1100, 100% and 200% text. A copied HTML file is a reference asset, not implemented UI.
- Material visual or component decisions get UI review. Workflow, wording, navigation and recovery get usability review. Data meaning behind status, scope and research controls gets WoW datamining review. These reviews don't add an approval step for routine work on agreed behaviour.

## Source provenance

[The import manifest](../migration/java-reference.json) records original paths, the source commit and SHA-256 values. Keep imported HTML unchanged until a design revision is recorded. Production UI lives under `apps/desktop/`. The mockups do no real network, parsing or persistence.
