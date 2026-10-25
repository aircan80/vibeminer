# Items view

Design for [#61](https://github.com/aircan80/vibeminer/issues/61): pick an item and see what it is, with its icon, quality, stats, effects and source records together. It replaced the Items screen that read like a raw table page. It uses the [desktop home](../desktop-home/README.md) tokens, type scale, shell and status bar. Status: approved on #61 after two rounds of WoW, UI and usability review ([D098](../../DECISIONS.md)); labels for InventoryType 0–30 are confirmed. The app builds it; see [What the app does differently](#what-the-app-does-differently).

Open [index.html](index.html). Every name, ID, number, text and icon is authored; nothing is game content. The status bar says **Sample data**. The header is a simplified copy; its popovers stay in desktop-home. Query options:

- `state=ready|loading|preparing|failed|missing|unknown|encrypted|unsupported|zero`. Each state selects the item that shows it.
- `item=<id>` selects another row: `900006` (Item and ItemSparse disagree), `900013` (two ItemClass rows match), `900019` (quality with no reviewed label).
- `q=<text>` applies a search and `mode=id` searches by exact Item ID. A URL search is validated like typed input: `mode=id&q=12a` shows the field marked invalid and keeps browsing.
- `pane=detail` opens the detail pane in the compact layout, `panel=open` (while there is activity), `text=200`, `chrome=0` (hides the mockup state control).

## Layout

- **Two panes** under the build header, as Files has: the item list (`--side-w`, 26.5rem) and the item detail. Both scroll on their own; the window never scrolls.
- **List pane:** **Items** heading with a **Name | Item ID** switch, then one search field and **Search**. Under it, what's applied ("Name contains “coat” · 3 on this page", or "All admitted Items · ascending Item ID") and **Clear search**. The list is a listbox like Files: icon, name in quality ink, `ID · quality` below, item level on the right. A row without a readable name wraps its reason instead of cutting it. The foot has the page buttons: 50 per page, no total count, **Next** when more exist.
- **Detail head:** the 40px icon, the name, `Item ID | quality | Item level | inventory type | binding`, and **Item #ID** / **ItemSparse #ID** at the right, which open the whole record in Tables. Below: the description and the icon's FileDataID, each with its source link.
- **Detail sections**, two columns when the window is wide enough for both panes:
  - left: **Item** (quality, base item level, class, subclass, inventory type, then the item level inputs disclosure last, so Shift+Tab from its links leaves the section), **Requirements**, **Inventory and price**;
  - right: **Effects**, **Stat allocation, not final stats**, **Sockets**;
  - full width: **Other source inputs · raw**, closed by default.
- In the compact layout (one pane at a time) the sections are one column with **Effects first**, so its actions aren't below twenty facts.
- **Status bar:** exact build, source and locale, then Items counts (Item rows, rows with ItemSparse, Partial when it is), then background work.

## Values and sources

- **Every value links to its source record.** A fact shows its label, its source as `Table.Field`, and the value. The link uses the Tables reference-cell style (13px, weight 600, teal, dotted underline). It sits beside the label when both fit and drops under the label when they don't, so the label never wraps to make room.
- A link opens Tables at that record with the field selected, same build, source and locale, with **← Return to Items selection** ([desktop-home](../desktop-home/index.html?area=tables&from=items)). Return restores the list, search, page, selection and scroll, and puts focus back on the link. In the mockup a link shows a note instead of leaving the page.
- **Follow by value where there is no record.** With no linked effects, the link reads **Show ItemXItemEffect rows with ItemID 900005**, the Tables follow from D072, not a record that doesn't exist.
- **Array groups link once:** `StatModifier_bonusStat[0–9]`, `StatPercentEditor[0–9]`, `StatPercentageOfSocket[0–9]`, `SocketType[0–2]`. `AllowableRaces[0]` and `[1]` link separately.
- **Effects link each step:** `ItemXItemEffect row`, `ItemEffect #`, `SpellName #`.
- **Number first, label after.** `5 · Chest`, `12 · Authored class A`. The number is the source value. The label is ItemClass or ItemSubClass text (each with its own link) or VibeMiner's reviewed label list. Without a label: `11 · no reviewed label`, in the head, the list and the fact.
- **Two sources, one fact.** When Item and ItemSparse agree (InventoryType), one value with both links. When they disagree, two facts and "Item and ItemSparse disagree. Both values are kept; neither is picked."

## States

Every value that isn't a value is a word, never `0` or blank. On 12.1.5.70448 ItemEffect and SpellName are always Partial (encrypted sections not loaded), so Effects says so in every state; ItemXItemEffect is complete.

| State | Fixture | What it shows |
| --- | --- | --- |
| Ready | `ready` | Item 900003 with every section filled, two effects with spell names. |
| Loading while the build downloads | `loading` | Download all runs. Core Items are ready; ItemClass and ItemSubClass labels read `12 … ItemClass loading`, icons show `…` in a reserved box, Effects shows `[2/4] Downloading ItemEffect table data, then SpellName text` with **Stop loading effects** and **View task**, and the cards it already knows from ItemXItemEffect. The activity panel lists the effects request under "Needed by your research", ahead of Download all. |
| Preparing | `preparing` | Item and ItemSparse aren't both ready. The list shows **Preparing Items…** with each prerequisite's state, **Stop preparing Items** and **View task**; search is disabled with its reason. The detail offers **Open Item in Tables**. |
| Failed | `failed` | **Items need attention** with the code and cause, **Retry Items preparation** and **View task**. |
| Missing | `missing` | Item 900011 has an Item row but ItemSparse, complete, has no row for it. The name reads "Name unavailable · no ItemSparse row"; ItemSparse sections say **Missing**; Item fields still show. Its icon is missing: the FileDataID has no ROOT entry. Its effect's spell name is **Unknown**: SpellName is Partial, so a missing row there is never Missing. |
| Unknown | `unknown` | ItemSparse is Partial: 2 encrypted sections aren't loaded. A notice says so above the list. Item 900017's ItemSparse row isn't readable, so its name and ItemSparse values are **Unknown**, "nothing is inferred". |
| Encrypted | `encrypted` | Item 900008's icon file is encrypted: "Encrypted icon · TACT key … isn't available". Its ItemEffect row isn't in the readable rows, so its effect card reads **Unknown**, with the encrypted sections as the reason. |
| Unsupported | `unsupported` | Stat allocation: "Not decoded for this layout" with `ITEM_STATS_UNSUPPORTED`, the fields shown as raw inputs. Effects: "Effects aren't available from this engine" with `ITEM_EFFECTS_UNAVAILABLE`. Icon: unsupported BLP encoding. Calm, no Retry. |
| Known zero | `zero` | Item 900005: zero prices and requirements show as `0` in normal ink, stat slots read `-1 / 0 / 0` in normal ink, `Item.IconFileDataID is 0` with its link (not "No icon"), an empty description says it's an empty string, and Effects says "0 ItemXItemEffect rows have ItemID 900005 in the complete source". |
| No match | `q=zz` | "No admitted item name contains “zz”", with **Clear search**. The selected item stays open with "Selected item 900003 is outside these results". |

## Keyboard and focus

- **Tab cost:** from the list, Tab reaches **Item #ID** and **ItemSparse #ID** in 5 stops (3 page buttons, then the two), and the whole detail in 9 more.
- **Each section is one Tab stop.** Its first source link (or the last one used) takes Tab; arrow keys and Home/End move between that section's links; Tab and Shift+Tab leave the section. The head's description and icon links are one group. A visible line above the sections says "Tab moves between sections; arrow keys move between a section's links." in secondary text, and each link is described by it.
- A disclosure (**Item level inputs**, **Other source inputs**) has its summary as its own stop. While it is closed its links are skipped by Tab and the arrows; when it opens they join the section's group in reading order.
- A table region is a Tab stop only when it actually scrolls sideways.
- Tab order: skip link, header, area bar, **Name | Item ID**, search field, **Search**, **Clear search**, the list (one stop), page buttons, record buttons, then the detail groups in reading order.
- In the list, Up/Down/Home/End/PageUp/PageDown move the selection and the detail follows; the list's own scroller keeps the row in view. Opening the page never scrolls the window. While the shell scrolls as a whole, a key move also scrolls the window just enough to keep the row and its focus ring above the sticky status bar, and Tab focus stops above it too. In the compact layout Enter (or a click) shows **Selected item** and focuses the item heading.
- **Ctrl+F** focuses the search field, switching to the list pane in the compact layout. Escape closes the activity panel.
- Typing never runs a search; Enter or **Search** applies it. A bad ID ("12a", "-1", leading zeros), typed or from the URL, is marked `aria-invalid` with the message under the field, and the last results stay.
- Async completion (labels, icons, effects arriving) never moves focus, scroll or selection.

## Activity panel

Same rules as on Home and Tables (D086). With both panes showing, the panel covers exactly the item detail, which is inert. In the compact layout it covers the whole area, which is inert. While the shell scrolls as a whole (600×480 at 200%), it is a sheet over everything: the header and area are inert, Tab stays in the panel and the status bar, and Close or Escape returns to the activity entry.

## 200% text and narrow windows (below 70rem)

- desktop-home's two-row header grid: logo and identity, then the area bar with the actions beside it. Below 36rem the actions take their own row. The logo never sits alone, and the Source data / Game data separator hides when it would end or start a line (D086).
- One pane at a time with **Items list | Selected item**, as Tables and Files do. The selected item's name stays next to the switcher.
- The list keeps its rows in view: the heading is named by the switcher, mode and search share one row, there's no column header, and the level moves into the second line.
- The item head scrolls with the facts, and a `|` in its subline hides when it would end or start a line. Sections are one column, Effects first.
- At 600×480 and 200% the shell scrolls as a whole (D086, D092): the header scrolls away and the list keeps its own scroller capped at the visible height.

## Decisions this design makes

1. **Two panes, not three.** Item detail is the inspector. A separate source inspector would push the facts into 25rem; source links per fact do the same job.
2. **Record buttons in the head, one Tab stop per section.** Per-fact links with a roving tabindex keep "each value links to its source" without thirty Tab stops to the same record.
3. **Effects before stats**, and first in the one-column layout. Stats are allocation inputs without amounts; effects carry the readable facts and the loading actions.
4. **Quality ink is our own palette**, not the in-game colours: every shade is at least 4.5:1 on paper, side, hover and mint. Poor is a warm grey (`#6b6560`) and Heirloom/WoW Token a blue (`#0072a3`), so neither reads as the muted grey or the link teal; Rare moved to `#2350b8` to stay apart from Heirloom. The quality is always written next to the colour.
5. **Reviewed labels, number first.** VibeMiner's list covers quality 0–8, InventoryType 0–30, Bonding 0–4 and TriggerType 0–2. Bonding 7–9, TriggerType 3–7, stat types, socket types and class/race masks stay unlabelled.
6. **"Inventory type", not "equip slot".** The field is InventoryType and isn't only equippable slots.
7. **Stat allocation, not final stats.** All ten slots stay as read, zeros and −1 included, in normal ink. "Final +N depends on item level, slot and bonuses." No +N is calculated.
8. **Item level is "base, before bonus lists and scaling"**, with the raw level inputs one disclosure away: Item.ItemSquishEraID, Item.ContentTuningID, ItemSparse.PlayerLevelToItemLevelCurveID, ItemLevelOffsetCurveID and ItemLevelOffsetItemLevel.
9. **Prices show gold/silver/copper and the copper value.** Unit formatting only, and "Source value, not a vendor quote".
10. **Effect inputs stay raw.** Cooldown −1 shows as `-1 · raw value, not a duration`, never "−1 ms". Charges, SpellCategoryID and ChrSpecializationID say "raw 32-bit, read as 16-bit"; PlayerConditionID is an unverified candidate.
11. **Unknown vs Encrypted.** The engine knows when a whole file is encrypted (an icon) but not which record IDs an encrypted DB2 section holds. A row outside the readable sections is **Unknown**, with the encryption as the reason, never **Encrypted** or **Missing**.
12. **Missing only with complete scope.** "No ItemSparse row" appears only when ItemSparse is complete. ItemEffect and SpellName are always Partial on this build, so their absent rows are Unknown.
13. **IconFileDataID 0 isn't "no icon".** The client can also take an icon from the item's appearance (ItemModifiedAppearance → ItemAppearance.DefaultIconFileDataID), which this view doesn't read. The head says `Item.IconFileDataID is 0` with its link.
14. **Zero is a value.** `0` is shown in normal ink, never dimmed, because dimming reads as absent.
15. **Search is one field with a mode switch.** An empty search browses. Name search skips items without a readable name; exact ID finds them.
16. **Icons are 40px everywhere**, in a reserved box with a quality frame. A missing, encrypted or unsupported icon shows `—` in the list (the reason is in its accessible name and in the detail head); IconFileDataID 0 is an empty dashed box.

## Open questions

- **WoW:** whether the appearance icon path (ItemModifiedAppearance → ItemAppearance) belongs in the first Items build or stays a follow-up; the table each item level input comes from (ContentTuningID is drawn from Item).
- **12-image cap.** At 1440×1100 about 17 list rows are visible, more than the 12 images a view may mount ([UI guidelines](../../UI-GUIDELINES.md#resources)). Rows past the cap keep the reserved box without an image. Raise the cap for 40px icons or accept the gap.

## Engine work

- [item-facts-v1](../../../contracts/item-facts-v1.md) adds `items.facts` (the selected item's reviewed facts, each value with its table, column and record) and `items.labels` (the reviewed label lists). The renderer only presents them.
- Effects, Class/Subclass labels and icons use their existing contracts (`item-effects-v1`, `item-classifications-v1`, `item-icons-v1`).
- Tables opens a record with one field selected and returns to Items with focus on the link, as before.

## What the app does differently

- **No Tables link for "no linked effects".** The mockup's "Show ItemXItemEffect rows with ItemID 900005" can't be built: `tables.follow` only follows a reference declared on the source cell (ItemXItemEffect.ItemID → Item.ID is declared on the bridge, not on Item) and never scans a target over 4,096 rows. Effects says how many rows were found and where, without a link that would land somewhere else.
- **No `ITEM_STATS_UNSUPPORTED` code.** A layout without stat fields reads "This ItemSparse layout has no stat fields, so they are shown as unsupported, not as zero." Each fact the layout lacks says **Unsupported · not in this layout**.
- **Classic stats are stored amounts.** Classic layouts (e.g. 1.15.9, B51F7C79) store flat amounts in `StatModifier_bonusAmount[0–9]`. The section is then **Stats · stored amounts** with an Amount column and "These are the stat amounts this layout stores for each slot, as read"; the "Final +N depends on item level, slot and bonuses" note is only for retail allocation inputs. Only the columns the layout has are shown.
- **Copies stored differently stay apart.** When Item and ItemSparse hold the same field with different shapes (Classic Item.DamageType[0–4] and ItemSparse.DamageType), it is one fact per table with "Item and ItemSparse store this field differently. Both are kept as read." RequiredLevel, which Classic has in both tables, is a shared fact too.
- **Effect cards keep a "Raw evidence" disclosure** with the bridge ordinal, the full name outcome and every ItemEffect input with its physical type. Page buttons (16 occurrences per page) appear only when there is another page.
- **One "Effects sources and status" disclosure** holds the phase status and the ItemXItemEffect, ItemEffect and SpellName source receipts, each with its state in words ("Not prepared yet", not `notPrepared`), so one effect costs about three Tab stops (its links, Raw evidence, sources).
- **Effect loading uses the shared effects request.** Its status line names the phase ("Names preparing…") and **Stop loading effects** stops that phase; the status line shows only while a phase runs, waits or has a problem; effects that were ready from the start show no status box, and one that showed loading keeps its place until another item is chosen, so finishing never moves the cards. After Stop it says "Links stopped" and the disclosure says "Stopped.". The Download all task stays in Downloads & activity.
- **Open Item in Tables** while Items prepares or needs attention opens the Item table on its Data tab (no record is selected yet).
- **Icon and label controls** (Stop waiting, Retry visible icons, Retry labels) appear under the list only while there is something to stop, wait for or retry. Once any visible icon is stopped, only **Retry visible icons** shows, and when every visible icon is stopped the status says "Visible icons stopped". Retry moves focus to that status; Retry Items preparation moves it to the Preparing status, and from there to the search once the list is ready.
- **Engines without item-facts-v1** show the head and record buttons, then "Item facts aren't available from this engine" with `ITEM_FACTS_UNAVAILABLE`.
- **Preparation lists each prerequisite** from `items.data` (Item, ItemSparse) as Ready or "Not ready yet · needed by Items, at interactive priority" (it doesn't claim a Download all that may not be on); its stage text is the engine's own.
