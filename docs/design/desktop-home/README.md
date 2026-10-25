# Desktop Home

Redesign of the screen shown after a build is opened, so it matches [desktop discovery](../desktop-discovery/README.md) instead of scrolling like a web page under a tall header. It replaces the layout of [top navigation](../top-navigation/README.md) and the [C overview](../build-mining-home/README.md) but keeps their meaning. Status: approved by the owner on [#6](https://github.com/aircan80/vibeminer/issues/6): Home, the Tables tabs, Files and reference text ([#17](https://github.com/aircan80/vibeminer/issues/17)).

Open [index.html](index.html). All values are authored samples, not game data. Query options:

- `area=home|tables|files`. Items and Creatures are placeholders.
- `state=idle|loading|stopped|downloading|attention|partial`, `panel=open` while there is activity, `popover=status|identity`.
- Tables: `tab=data|structure|relations`, `table=Item|ItemClass|Material|ItemGroupSounds`, `tdata=ready|definition|partial|unsupported|failed` (Waiting is `state=loading`, Stopped is `state=stopped`), `reftext=resolved|pending|unreadable`, `follow=12|7|5` (Item.ClassID followed to ItemClass), `from=items`.
- Files: `catalog=ready|notprepared|preparing|partial|empty`, `listfile=1` (community names), `q=` (FileDataID or content key), `ff=1` (two filters on), `fopen=1` (Filters disclosure open at 200%).
- `game=0` (no Items or Creatures), `cdn=newer`, `text=200`, `chrome=0` (hides the mockup state control).

## Window

- Same window rules as Discovery: no visible menu bar, application menu kept for accelerators, and the window never scrolls at any size or text scale. Panes scroll on their own.
- **Shortcuts:** Ctrl+F focuses the search on Tables and Files. Escape closes the open popover or the activity panel and returns focus to its trigger.

## Layout

- **Build header**, one row (about 46px): logo, then a strip of small label over value: **Product**, **Build**, **Source**, **Locale**, **Status**, **Download mode**. Then the activity entry (only when there is one) and **← Choose build**.
  - Product and Build open the identity popover. Status opens the Local content & storage popover. Source and Locale are plain values.
  - The popovers are nonmodal `role="dialog"` surfaces labelled by their heading, placed in the DOM right after their trigger, so Tab continues from the popover to the next header control.
  - The identity popover keeps the per-region note from `BuildHeader` when the source region now advertises a different CDN configuration (`cdn=newer`).
  - Download mode is a labelled switch, **On demand ↔ Download all**. It shows **Checking…** while the policy is unknown and **On demand only** without a DL ledger, with no switch in either case.
- **Choose build** uses the primary teal style, the same as Open build on Discovery (owner decision).
- **Area bar** directly under the header row: Home · Tables · Files | Items · Creatures. Tables and Files are the raw source layer, exactly as the build ships it; a separator divides them from Items and Creatures, which VibeMiner builds by joining tables (owner decision). Each side is a labelled group for screen readers (Source data, Game data). `aria-current="page"`, teal text and a 3px rule for the active area.
- **Unavailable Items and Creatures are hidden**, with their separator, as `BuildAreas` does today (`game=0`). Build readiness on Home says "Game data areas: Not available for this build", so the reason is visible.
- **Status bar.** One line, starting with the exact build, source and locale on every area, so Source stays visible at 200%. Then, per area: assessment time (Home), definitions revision and admitted rows (Tables), catalog generation and memberships (Files). The right end summarises background work, as on Discovery.

## Home

Four tiled panels with 1px splits instead of cards on a long page. Each panel body scrolls on its own; actions are pinned at the panel foot.

- **Download sizes:** published download content (readable and exact bytes), unique encoded objects, scope, decoded/installed size Unknown. Foot: **Scope and measurement details**.
- **File catalog:** catalog memberships, ROOT memberships, unique ROOT/IN contents, readiness. Foot: **Browse in Files**.
- **Local content & shared storage** (spans both columns): stacked bar, then a ledger with readable and exact bytes. Next to it, the Download mode explanation for the current state. When coverage is Unknown there is no bar at all, because an empty track reads as 0%.
- **Build readiness** (side column, same width as the Discovery inspector): a state box, the readiness facts, and **View metadata details** pinned at the bottom.
- The pane head holds **Metadata** and **Source details**.
- **Focus:** the area bar focuses the destination heading. A local return restores the control that left Home: **Browse in Files**, then **← Return to build overview** in Files, puts focus back on Browse in Files (as `useHomeFocus` does).

## Tables

The workbench is three panes: catalogue (17rem), records, inspector (24.3rem, 340px). The inspector head ends exactly where the records title row ends.

- **Catalogue:** title with match count, search, and provider entries (long names wrap only at CamelCase boundaries) with bundled definitions first, then provider name. Each entry shows its table hash. Page controls are pinned at the bottom. Counts are provider counts (1,346 entries at WoWDBDefs `b9a6309`), not tables in the build. A selection outside the current page says so.
- **Trail row:** back/forward, the breadcrumb (`Item › ItemClass`), **Record ID** on Data, and **Data / Structure / Relationships**. A catalogue pick starts a new trail; Follow extends it (at most 64 entries). Back restores the source table, tab, record and field.
- **Title row:** table name, provider table hash and DB2 FileDataID. When Tables was reached from an Items source, **← Return to Items selection** comes first.
- **Data:** paged admitted enUS rows, every field of the selected-build layout, the ID column sticky on the left, and a sticky two-line header (field, then type and declared target). Up/Down/Home/End move the selection.
  - **Reference cells** (declared references only) show the raw value in teal with a dotted underline. Plain numbers stay plain. Clicking a reference cell, or its action in the record inspector, shows the target rows where the target column equals the raw value: **Show ItemClass rows where ClassID = 12** ([#19](https://github.com/aircan80/vibeminer/issues/19)). The match uses the same build, source and locale.
  - Follow outcomes: one row is selected; several rows (`ClassID = 7`) are listed and none is picked, because ClassID isn't the declared key; no row says "No admitted ItemClass row has ClassID = 5" for complete scope, or "Not in the readable rows… this isn't an absence" when the target is partial.
  - Material has no records yet, so following Material.ID lands on its "No records yet" state. IconFileDataID → FileData.ID has no table definition, so **Find FileDataID 800001 in Files** opens Files at that FileDataID, with **← Return to Item record 900001** back to the exact record. Targets whose definition isn't bundled (ContentTuning, CraftingQuality, SoundKit and others) show a plain number and can't be followed.
  - At 200%, following a reference or picking a table from the Tables list switches to the Records pane and focuses its title. Clearing the filter chip focuses the Record ID filter; a breadcrumb keeps focus on itself.
- **Reference text ([#17](https://github.com/aircan80/vibeminer/issues/17)):** a reference cell shows the referenced row's localized text beside the raw value, visually secondary: `12 · Authored class A` from ItemClass.ClassName_lang. The raw value stays first and is what sorting, filtering and copying use.
  - resolved: `12 · Authored class A`
  - text table not prepared yet (`reftext=pending`): `12 · …` with "text not prepared yet" for screen readers
  - not found: `5 · not found in ItemClass`
  - target protected (`reftext=unreadable`): `5 · not readable (protected)`
  - several matches: `7 · 2 matches`
  - no reviewed text column (Material, ItemGroupSounds): the number only
  - The inspector shows the text with its source: "Authored class A · ItemClass.ClassName_lang · row ID 301 · enUS".
- **Structure**, labelled **DBD structure**: one row per declared field with index, name, type, width and sign, array size, `$id$` key, declared reference and annotations (`$noninline$`, unverified name `?`). The strip above says which candidate layout matches the build and whether the physical layout is verified. The foot credits WoWDBDefs contributors, the revision and CC BY-SA 4.0, and says no UNIQUE, NOT NULL or CASCADE is implied.
- **Relationships:** only the references this definition declares, outgoing, each as exact `Table.column → Target.column` with the target's state (definition bundled and records admitted, records not prepared, not a DB2 table, definition not bundled). **Open ItemClass.ClassID definition** opens the target definition at the target column; it runs no row query. The two kinds of Follow have distinct names: "Open … definition" here, "Show … rows where …" in the record inspector. The selected reference shows mint with the teal edge, like Structure rows. Incoming references are not listed: the definition contract returns only the table's own declarations, and the tab says so. Fields with no declared reference are listed as plain values.
- **Inspector:** on Data, the selected record with each field's raw value, type, width and sign, `$id$`/`$noninline$`, declared reference, reference text and Follow. On Structure and Relationships, the selected field's declaration. Both end with the source (exact build, provider table, DB2 FileDataID, candidate layout, physical source, decoded key, definitions revision and SHA-256).
- **Data states:**
  - definition only (`tdata=definition`): "No records yet"; Structure and Relationships work now, and Data prepares the table automatically
  - Waiting for required data (`state=loading`): the stage, **Stop loading Item** and **View task**, no progress bar of its own
  - Loading stopped (`state=stopped`): **Retry table data**, and a Recent outcome in the activity panel that the badge doesn't count
  - encrypted sections (`tdata=partial`): "Readable rows available · Partial" with declared, emitted and excluded counts, and a warning not to subtract them
  - unsupported layout (`tdata=unsupported`): a calm capability limit (`SCHEMA_MISMATCH`), no Retry
  - failed (`tdata=failed`): **Table data needs attention** with **Retry table data**

## Files

A file explorer for the build's prepared catalog: a filter row, a list with one row per FileDataID, and a file inspector. The inspector head ends where the list column header ends. Grouping by FileDataID and the filtered query came with [#20](https://github.com/aircan80/vibeminer/issues/20) (`inventory.files` and `inventory.file`, [D068](../../DECISIONS.md)).

- **Filter row:** search by FileDataID or content key, locale flag (enUS 0x2, enGB 0x200, esES 0x80, esMX 0x1000), content flags (raw masks), source family (All, ROOT, Install) and **Stored locally**. No platform filter: platform is never inferred from content flags.
- **At 200% on small windows** the search stays visible and the other filters collapse behind **Filters**, a nonmodal disclosure (`aria-expanded`). It shows the active filter count ("Filters 2", read as "2 active"). Tab moves from it into the controls, and Escape closes it and returns focus to it.
- **List:** one row per FileDataID: FileDataID, name, membership count (and distinct CKeys when they differ), encoded size summary (one size, a range, or Unresolved), locales and local status (Stored, Not stored, or "1 of 2 stored"). It's a listbox, like Discovery's build list: one Tab stop, then arrow keys.
- **Names:** ROOT has no file names, so the default is **Name not known**. With the opt-in community listfile ([#21](https://github.com/aircan80/vibeminer/issues/21), `listfile=1`), names show with the grey-blue **Community** badge from Discovery's community builds, and the inspector says they don't come from Blizzard. Install (IN) memberships carry their actual install names; the sample page shows ROOT rows only.
- **Inspector:** FileDataID, name and source, then every membership of that FileDataID: family with block and entry, raw locale flags with known bit names, content flags with the three raw parts, name hash, CKey, EKey, decoded and encoded length, and local object status with its verification time.
- **States:** catalog not prepared (**Prepare file catalog**, filters disabled); preparing (stage, **Stop preparing catalog** and **View task**, progress in the activity panel); partial (10 ROOT content keys with no encoding entry, `CONTENT_UNRESOLVED`, shown as Unresolved, not corrupt or absent); no match, which names the actual search ("No file in this build's prepared catalog has FileDataID 99999999", or "…has a content key starting abc123") and offers **Clear filters**.
- **As built ([#35](https://github.com/aircan80/vibeminer/issues/35), [D071](../../DECISIONS.md)):** the catalog is prepared on its own after every Open (owner decision), so there is no **Prepare file catalog**. Until it is sealed, Files keeps the download-object list under the tinted **Preparing file list…** state box: the stage on its own dim line (writing shows memberships written of the total; a queued or waiting one names the build it waits for; a paused Download all pauses it), what's missing, **Stop preparing file list for <version>** and **View task**. Each preparation is one Downloads & activity task with its stage, a named Stop, Resume once stopped and Retry once failed. A catalog that becomes ready while Files is open is offered in the footer (**Show file list**, announced politely); the state box keeps its height, so rows, focus, selection and scroll stay. A failure says why once, with its code and cause, and offers Retry and **Reopen this build**; an unreadable file list says retrying won't help and offers neither. A failed status read says so, with **Retry status**; one that fails after an earlier success keeps the last status marked "Status not confirmed" with **Retry status**. When an action replaces its own button (Stop, Resume, Retry, Retry status), focus moves to the button that takes its place, or to the note; on Home, Retry status in the File catalog panel hands focus to the panel heading. At 200% text on small windows every Files note (checking, preparing, not confirmed, stopped, ready, failed, unsupported, status unavailable) uses a short form that keeps a whole object row in view: each note puts its one short line beside its title with the actions below, the ready note carries **Show file list** itself so the footer keeps one row, "Not confirmed ·" marks the stage line with Retry status beside the note's actions, an earlier failed attempt is folded in as "Attempt 1 failed, retrying ·" (its code stays in the long form), a failed or unsupported note keeps a short message with its next step ("Disk full. Free some space, then retry.", "Can't be read. Retrying won't help.", "Failed three times. Retry or reopen the build.") and drops its code first when the note is narrow, View task moves to Downloads & activity, and the footer count shortens to "Objects 1–50 of 60" so Return, the count and the page controls share one row. A FileDataID followed from Tables while the list is preparing is one line at 200% ("FileDataID 9 can't be looked up yet · no file list yet"); once the list is ready it says the FileDataID can be looked up now, and **Show file list** opens the list searching it. The notice sits in the list's scroll area, so it lines up with the note below. When a status read asked for by a button fails again, focus stays on that button and no later automatic read moves it. If focus is on a note's control when a new state removes it (Stop when the list turns ready), focus moves to the control that took its place (**Show file list**, in the note at 200% or the footer at 100%) or to the note. The e2e `files-fit.spec.ts` checks every entry (Browse, Follow) × state (preparing, retrying, not confirmed, stopped, failed, full, unsupported, ready offered, ready list) × size (1280×800, 1440×1100) × text (100%, 200%): 72 combinations. Home's readiness says "Checking…" for the file list until the first status read answers. The activity task's stage names the step only; written counts are its progress line and a failure's message its problem line, each once.
- **Returns:** **← Return to build overview** when Files was opened from Home, **← Return to Item record 900001** when it was opened from a Tables reference.
- **Save file** is left out: it isn't built and has no contract.

## Activity entry

- It's visible only while there is something to show: a current research request, saved Download all work, or an outcome to review. Otherwise there's no entry, no badge and no floating button. Owner decision on #6: Home hides it when idle, like Discovery.
- The panel is the same nonmodal side panel as on Discovery. It sits below the header and covers exactly the side column (Home) or the inspector (Tables, Files), which become inert while it is open. It and the popovers are one surface at a time.
- **At 200% the panel covers the whole area, and the whole area becomes inert.** This replaces download-activity's "no inert background" rule for this layout. There is still no modal trap: the header (area bar, activity entry, Choose build) and the status bar stay reachable, and Close or Escape returns focus to the entry. What's inert is only content the panel hides, so Tab never lands on a control nobody can see.

## States

- `idle`: On demand, everything measured, no activity entry.
- `loading`: Download mode **Checking…**, and an Item table load in progress (Needed by your research, Waiting for required data on Tables).
- `stopped`: the Item table load was stopped. Tables shows **Loading stopped** with **Retry table data**; the activity entry shows a Recent outcome without a badge count.
- `downloading`: Download all saved, `[2/3] Downloading encoded content`, "28.6 of 114.2 GiB · 25% of current stage". Home's storage figures stay labelled as the last assessment.
- `attention`: failed Download all, amber **Needs attention** entry, **Resume build download** and **Dismiss outcome**. Download all stays selected.
- `partial`: download manifest unsupported (`DOWNLOAD_MANIFEST_UNSUPPORTED`). Sizes and local coverage are Unknown with no bar and no percentage, the catalog is Not prepared, Download mode is **On demand only**, and Status is **Configuration verified**. On Tables: **Readable rows available · Partial**.

## Carried over

- Discovery tokens, type scale (11 / 13 / 14 / 19px at a 14px base), muted `#586a64`, toolbar, panes, pills, notices, state boxes, status bar, focus ring and selection rules, and the activity chip and panel.
- Header fields and Download mode semantics from [download activity](../download-activity/README.md). Status popover rules: dated opening assessment, no complement or percentage without complete coverage, fresh verification "Not assessed by this view".
- Home facts and wording from the current `DownloadHome` and `HomeView`: verified versus additional bytes, shared bytes counted once, metadata kept separate, no grand disk total, Partial explained.
- Tables meaning from `TablesExplorer`, `TableDataView`, [table research](../tables/RESEARCH.md) and the `tables-v1` and `table-data-v1` contracts. Definitions and records are separate. `?` marks an unverified name. Values are raw. Relationship navigation follows definitions only. Partial admission keeps its counts. The selected-build layouts are the real bundled `b9a6309` definitions for Item (`996192AA`), ItemClass (`35680EB8`), Material (`BE3E0E4C`) and ItemGroupSounds (`A7CEF530`). The rows are authored, and ItemClass ID differs from ClassID on purpose.
- Files meaning from `FilesCatalog`, `DownloadObjects`, the `inventory-v1` and `download-v1` contracts and the [ROOT format](../../formats/root.md): a membership isn't a unique file, a name hash isn't a filename, and content flags are raw facts.

## Deliberate departures

- **Files without a file catalog (the app today, not drawn here):** no **Prepare file catalog** button and no disabled filter row. Production opening can't prepare the ROOT/install catalog yet ([#35](https://github.com/aircan80/vibeminer/issues/35)), so there is nothing to start. Instead a note spans the list width: "File catalog not prepared", what works today (paging the download manifest's encoded objects and their local status) and that FileDataID and CKey search and per-file rows need that catalog. It sits at the top of the list's scroll area and shrinks to one line at 200% text so rows stay in view.

- **The activity entry hides when idle on Home too**, as on Discovery. Decided by the owner on #6.
- **The brand is bigger than on the first Discovery reference:** a 17px (`1.214rem`) wordmark and a 28px (`2rem`) logo instead of 14px and `1.57rem`. It's an owner decision shared with the approved all-products Discovery mockup (#10). It's the one size off the 11/13/14/19 scale.
- **Source details moves out of the build header** into the Home pane head next to Metadata. The header keeps exactly the fields from the spec. The Home **Storage** jump is gone because every panel is visible.
- **Header fields are label-over-value**, not Discovery's inline labels, so all eight items fit on one row at 1280px.
- **The area bar uses 13px** (the scale's small-body size), not 12px.
- **Tables uses tiled panes**, not one rounded card. The catalogue is 17rem (238px), not 224px, so names like `ItemModifiedAppearance` fit without eliding.
- **Home and Tables take Discovery's muted `#586a64`**, replacing `#657870` and `#697b72`. That closes the AA gap listed in the UI guidelines for these views.
- **Below 84rem** (about 1176px at 100%), the activity entry uses its short label.
- **Enlarged text or a narrow window (below 70rem, like `.dv-compact`):**
  - The header becomes two rows. Row 1 has the logo and identity. Product and Build are named by their buttons, and Status and Download mode have inline labels. Source and Locale move to the status bar and the identity popover. Row 2 holds the area bar, then the activity entry and Choose build. Since D086 the area bar wraps rather than scrolls, and the actions take a line of their own when they don't fit beside it. When the header and a 10rem area don't both fit, the shell scrolls as a whole.
  - Home becomes one scrolling pane with the panels stacked.
  - Tables shows one pane at a time with a **Tables list / Records / Selected record** switcher, and Files with **Files list / Selected file**. Files filters collapse behind **Filters**. Stacking all three left about one record row visible at 1280×800 and 200%. The Tables title row is visually hidden; the breadcrumb shows the table name.
  - The activity panel covers the whole area, which becomes inert (see Activity entry).
- **Reference cells are clickable by mouse only.** Each row keeps one selection control. The keyboard path is the Follow action on each reference in the record inspector.
- **Engine work this mockup assumes, not built today:**
  - [#19](https://github.com/aircan80/vibeminer/issues/19): Follow by column value, a target-column lookup (built as `tables.follow`, D072)
  - [#17](https://github.com/aircan80/vibeminer/issues/17): batched reference text
  - [#21](https://github.com/aircan80/vibeminer/issues/21): the opt-in community listfile, with its own setting and a decision like #9
- Once approved, a decision row supersedes D021 for Home and its areas, as D059 did for Discovery.

Items and Creatures are not drawn. They use the same shell: header, area bar, their own panes and the status bar.
