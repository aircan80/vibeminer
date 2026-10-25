# UI guidelines

Rules every screen follows. Use them when building or reviewing renderer changes in `apps/desktop/src/renderer/`. The selected references live in [design](design/README.md); product meaning lives in [product](PRODUCT.md) and [async work](ASYNC-AND-JOBS.md). Where this page and the CSS disagree, the CSS is the current truth. Fix the page.

## Visual

### Copy the reference, don't reinterpret it

- Each view copies its reference HTML/CSS literally: structure, font sizes, weights, line heights, letter spacing, colours, borders, padding. A first "polished" reinterpretation of Discovery was rejected for this reason ([D021](DECISIONS.md)).
- Fonts: `system-ui, sans-serif` for text and `ui-monospace, monospace` for IDs and hashes. No Inter or other substitutes.
- Convert px to rem only when the result is identical at 100%. The root is 15px, so `rem = px / 15`. Check decimals: `-.8px` is `-.053333rem`, not `-.0.533333rem`.
- Compare structure before values: column grid, where the inspector starts, header height, which column each element sits in. Getting the colours right does not fix a wrong layout.
- Remove prototype-only things: concept/state selectors, "Next observed state", 200% preview buttons, rationale panels, simulation dialogs, design footers, PLANNED/disabled future destinations. Authored fixture data appears only in test fixture mode and carries a visible label.

### Per-view tokens

Views keep their own scoped tokens. Don't merge them into one palette, and don't let one view's stylesheet override another's.

Discovery has its own reference ([desktop discovery](design/desktop-discovery/README.md)): 14px base (`.933333rem`), 11/13/14/19px scale, muted `#586a64`, scoped in `discovery.css`. Home, Tables and Files follow [desktop home](design/desktop-home/README.md) with the same tokens and scale, scoped to `.desktop-home` in `desktop-home.css` ([D063](DECISIONS.md)). The columns below cover the shared `:root` tokens and Preparation, which still follows D021.

| Token | Shared `:root` (`styles.css`) | Preparation (`.preparation-view`) |
| --- | --- | --- |
| Body | 15px / 1.5 | 15px / 1.5 |
| bg / paper | `#f5f3ed` / `#fffefa` | same |
| ink | `#243b3a` | same |
| line | `#dce2d9` | same |
| teal | `#176e66` | same |
| muted | `#697b76` | `#697b76` |
| mint | `#e4f1e9` | `#e4f1e9` |
| gold / amber | gold `#a67729` | amber `#8a6425` |
| Focus ring | `#e6ad46` | `#e6ad46` |

Items, Effects and the geometry view keep their own stylesheets inside the Home shell and use the `#dda33a` ring.

### Focus and selection

- Focus ring: 3px solid gold outline with a 3px offset. It must look different from selection.
- Selection is teal/mint: mint surface, teal leading edge or text, `aria-pressed="true"`. Hover, focus and selection are three separate states.

### Shell and navigation

- The build header is one row: brand, then label-over-value Product / Build / Source / Locale / Status and a labelled **Download mode**, the **Downloads & activity** entry while there is something to show, and **← Choose build** as a primary teal button. Below 65.333333rem of window width (200% text at 1280px) it takes two rows: identity first, with Source and Locale moved to the status bar and the identity popover, then the area bar with the entry, Choose build and Settings beside it. When they don't fit beside the tabs, the actions take their own line below the area bar. The entry's widest width in the current area stays reserved (an empty cell once it retires), so its narrowing or retiring never re-wraps the header. Tab follows the visual order ([D086](DECISIONS.md)).
- Discovery, Home, Tables and Files are fixed windows: a toolbar, panes that scroll on their own, a one-line status bar. The window never scrolls; at very small sizes the list pane scrolls as a whole. On Discovery, a stacked window under 25rem tall (the smallest window from about 130% text) can't show the list and inspector at once: both take their natural height in one scrolling area between toolbar and status bar, the list header sticks to it, scroll padding keeps focused rows and focus rings clear of the header and status bar, and **View selected build** / **Return to build list** jump between the selected row and the inspector. Below 28rem of width each build row takes two lines. The Home shell's area keeps at least 10rem: when the header and that minimum don't both fit (600×480 at 200% text), the shell scrolls vertically as a whole and the header scrolls away with it. The area then grows with content that takes its natural height, as Home does, so there is one scroller and the status bar stays last; focus in the shell is kept in view, the status bar stays at the window bottom, the open activity panel is fixed from the window top to the status bar and locks the shell (it stays nonmodal; on close the scroll is restored only when focus returns to the invoker), and the floating activity entry is one compact line 1rem above the status bar ([D086](DECISIONS.md)). The menu bar is hidden but the application menu stays for edit, zoom, close and quit accelerators. The Home shell's status bar starts with the exact build, source and locale on every area.
- Area navigation is a horizontal bar directly under the build header: Home · Tables · Files | Items · Creatures, 13px text, muted `#586a64`, active item teal with weight 700 and a 3px bottom rule. Tables and Files (the raw source layer) and Items and Creatures (joined views) are labelled groups split by a separator. Items and Creatures are hidden when unavailable, and Build readiness says why. It uses `aria-current="page"`. When narrow it wraps onto more lines, with the group separator hidden when it would end a line; it never scrolls sideways, and no tab is clipped or covered by the header actions.
- There is no global sidebar. Areas own their internal navigation, such as the Tables catalogue on the left. Home utilities (Metadata, Source details) sit in the Home pane head.
- The Tables workbench is three tiled panes: catalogue `17rem` (238px at 100%), records with Data, Structure and Relationships, inspector `340px`. Files is a list and a `340px` inspector. Below 65.333333rem they show one pane at a time with a switcher (**Tables list / Records / Selected record**, **Files list / Selected …**). Other inspectors follow their own reference (Discovery and Preparation use `330px`).

### Enlarged text and long values

- Breakpoints must be text-relative: container queries or rem/em media queries. A px media query alone won't trigger at 200% text on a wide window.
- At 200%, columns stack and toolbars wrap. Never shrink type to keep columns side by side. Vertical scrolling is fine.
- The page never scrolls horizontally. Wide data tables scroll inside a labelled, keyboard-reachable region.
- Long IDs, hashes, URLs and raw hex wrap (`overflow-wrap:anywhere`), stay selectable or copyable, and are never elided. A short prefix (`a1a1a1a1…`) is fine as a visual cue in a list, as long as the full value is in the inspector.
- Short status words and control labels never break mid-word.
- When the inspector stacks below the content, show a **View selected …** jump (build, field, record, item) near the list heading, plus a return to the invoking row. Show only the jump for the active inspector mode. Tables and Files don't stack: at enlarged text they show one pane at a time with a switcher, and picking or following a table shows Records and focuses its title.

## State and wording

- These states are all different, and each needs its own label: missing, unknown, not prepared, not checked, not assessed, unsupported, protected/encrypted, corrupt, partial, failed, stopped/canceled, absent, known zero.
- Unknown is never shown as `0`, `0%` or an empty chart. Show zero only when the engine actually established it.
- Empty states name their scope: "No builds advertised in US, EU at 14:02", "No match in admitted readable items", "No locally prepared builds". Never say "no builds exist" before a successful check.
- Don't show a bare "Verified". Say what was checked, for example "Complete encoded object verified" or "Expected full content key — not verified".
- Saved or old evidence keeps its date: "Saved observations · last checked …". Don't say "You're offline" without a classified offline result. Use "Not refreshed this session" instead.
- Unavailable actions are either omitted (no producer exists) or disabled with a visible reason next to them. A hover-only tooltip doesn't count. Omit future-feature placeholders.
- No demo or placeholder controls that look like they work: CSS-only toggles, fake Download all, dead Load buttons.
- No toasts for durable errors or outcomes. Put the error at the operation, with exact context and Retry when retry is real.
- Capability limits are calm, static explanations ("Records unavailable: DB2 decoding is not implemented"), visually and verbally distinct from errors. No Retry that pretends to fix a missing capability.
- Keep the exact context through failure: same product/build/source/selection. Never fall back to a nearby build, region, locale or schema.

## Progress and actions

### Progress

- Show a percentage only when the denominator is known and nonzero, and only in the same unit and the same stage. Label it with counts: "25 of 100 bytes · 25% of current stage".
- No overall percentage, no ETA, no weighted stages. Don't sum per-job totals into a global figure.
- Transfer 100% isn't ready. Verification, decoding and publication can still be running.
- Label parsing, hashing and indexing as **Processing**, not Downloading at 0 B/s. Show rates only for measured network work.
- Show numbered stages as `[i/n] Stage label`, with one short explanation underneath. Before the plan is known, show `[1/…]`. The `i/n` prefix is an ordinal: never derive a percentage from it.
- Don't add fake minimum durations, elapsed fixture clocks or invented counters. A fast cache hit can skip the progress view.

### Stop, Retry, Back

- Stop names its target: **Stop loading Item**, **Stop waiting**, **Cancel preparation**. No global Stop all.
- After Stop, show **Stopping…** until the work actually retires. Acknowledgement alone isn't enough. A durable success can still win the race; report it truthfully.
- Hide Retry while the same producer is pending or stopping.
- Stop and error outcomes stay latched across rerenders and area changes until the user deliberately retries. Never auto-retry or restart from an effect, a poll or a terminal event.
- Retry labels its scope: "Retry discovery" (with "checks the complete product scope" nearby), "Retry this build", "Retry visible icons".
- Back/navigation detaches observation; it never cancels. Back and Cancel are separate controls with separate labels.

### Download policy

- Policy commands show **Saving download policy…** while committing, then the committed state. A mode acknowledgement is saved intent, not transfer progress.
- A stale command shows "Download policy changed; current status is shown. Choose the action again if still wanted." Never replay it automatically.
- A lost acknowledgement offers **Check download status** (a local read) before any resend.
- Download mode semantics live in [download activity](design/download-activity/README.md).

### Activity

- There's one compact **Downloads & activity** entry and one nonmodal side panel for all background work. Individual screens don't add their own progress bars.
- On Discovery and Home the entry exists only while something is active, paused or waiting, needs attention (it then reads **Needs attention**), or has an outcome to dismiss. Finished or canceled work with nothing to dismiss doesn't keep it. With nothing to show there is no entry, no badge and no floating button.
- The panel docks below the header and above the status bar. On Home it covers the side column or inspector, which are inert while it is open; in the compact layout it covers the whole area, which is inert instead. The header and status bar stay reachable and there is no modal trap.
- A screen that needs missing data says so in place ("Waiting for required data") and links to the exact task with **View task**.
- The activity entry and panel never open themselves, steal focus, navigate or reorder rows while you read.

## Keyboard, focus and announcements

### Focus

- Async completion, progress or failure never moves focus, scroll or selection.
- Deliberate navigation focuses the destination heading once (`tabIndex=-1`) and scrolls it into view.
- Returning restores the invoking control. If it's gone, use a stable nearby control or the section heading. Never fall back to `body`.
- Hand focus off after a control disappears only if that control (or `body`) still owns focus. If the user moved focus on purpose, leave it.
- Typing keeps focus and caret. Filtering and refresh never clear a selection that is still valid.
- Keep the visual order and the DOM/Tab order the same. Provide a skip link or main landmark.

### Controls and ARIA

- Use native `<button>`, `<select>`, `<input>`, `<details>`. Use `aria-pressed` for toggles and row selection, and `aria-current` for navigation.
- Use full ARIA patterns or none. Tabs need arrow/Home/End roving focus, otherwise use plain buttons in a labelled group. Don't add tree/grid/listbox roles without their keyboard model.
- Each row has one selection control. Don't put copy buttons or links inside a row-wide button.
- Accessible names include the visible identity. Don't override a visible name with "Inspect Item 52".

### Dialogs and panels

- Modal dialogs (Source details): named heading, initial focus inside, Tab trap, Escape, inert background, focus returns to the invoker.
- Nonmodal panels and popovers (activity, Status, identity): no trap, ordinary Tab exit, Close/Escape returns to the invoker and its window position, and an outside click leaves focus on whatever was clicked. Only one surface is open at a time. Clicking the same trigger again closes it.
- The one exception: while the Home shell scrolls as a whole (600×480 at 200% text), the open activity panel covers everything, so it is a sheet. The header, the area and the floating entry are inert, and Tab stays in the panel and the status bar. Escape and Close return to the opener with its scroll position ([D086](DECISIONS.md)).

### Announcements

- Use one polite live region per producer. Announce stage changes and outcomes, not byte ticks. Keep the status node stable while counters change.
- For numbered stages, put "Stage i of n" in visually hidden text and mark the visual `[i/n]` with `aria-hidden`.
- Use assertive alerts only for actionable errors that need attention now.
- Honour `prefers-reduced-motion` (the global CSS disables animation and transition).

### Selection and validation

- Keep a hidden selection and say so: "Selected build is hidden by these filters", "Selected definition is outside these results". Never silently select another item.
- Show invalid input inline with `aria-invalid` and an associated message. Keep focus in the form and keep the last good results.
- Never use colour, icon or hover alone to carry meaning. Pair it with text.

## Layout stability

- Background updates must not push a focused control. Reserve fixed geometry for status and action regions whose content changes. An empty reserved cell is fine. A fake button or invisible tab stop is not.

## Resources

- At most 12 decoded images mounted per view, counting the detail image. Don't mount images for hidden pages or areas.
- Reserve image dimensions before load (icons are 40px). Text must not shift when they arrive.
- Decorative icons next to a visible name have empty `alt` and no tab stop. No per-row spinners or image buttons.
- Page lists (50 rows) and keep histories bounded (definition trail: 64 entries).
- Poll status at most once per second, only while visible and active, without overlapping requests. Stop polling on stable states.
- The renderer never downloads, decodes or parses game data. It doesn't hold whole tables, manifests or receipt lists either.

## Verifying UI changes

- Render the reference HTML in the same Electron/Chromium as the app (strip only prototype chrome). Capture both at **1280×800** and **1440×1100**, at **100%** and **200%** text.
- For 200% text, double the root font size. That is text enlargement, not OS scaling or browser zoom. Report zoom separately if used.
- The references use px fonts, so their 200% capture scales differently from the app's rem layout. Don't reproduce the reference's enlarged-text crowding.
- Assert no document horizontal overflow at every size and scale.
- Chromium full-page captures taller than 16,384px wrap and repeat pixels below that line. Use viewport or clipped captures for anything that far down.
- Cover loading, empty, offline, error, stopping/canceled, partial and long-value states, not just ready.
- Screenshots don't prove keyboard behaviour. Exercise Tab/Shift+Tab, Enter/Space, Escape and focus return in real Electron tests.
- Record explained differences (fixture label, truthful content replacing mockup claims) next to the comparison. They never excuse structural drift.
- Material visual or interaction changes get UI and usability review. Data meaning gets WoW domain review.

## Known gaps

- Discovery's and Home's muted `#586a64` meets AA on every surface it uses. Elsewhere, muted text is below WCAG AA for normal text: `#697b76` on `#f5f3ed` is about 4.0:1 (shared `:root`, Preparation). The colours are kept for mockup fidelity ([D021](DECISIONS.md)). Changing them is an open decision; don't fold it silently into fidelity work.
- `retention.css` uses a 2px teal focus outline on retention activity controls, which breaks the gold-ring rule above.
