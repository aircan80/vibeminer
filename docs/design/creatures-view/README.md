# Creatures view

Design for [#62](https://github.com/aircan80/vibeminer/issues/62): pick an NPC and see its client record, its four display slots, each display's CreatureDisplayInfo and CreatureModelData rows and model file, and a small model preview that opens the geometry view ([#59](https://github.com/aircan80/vibeminer/issues/59)). It replaces the Creatures screen that showed slots and raw metadata JSON. It follows the [Items view](../items-view/README.md) (approved on #61, [D098](../../DECISIONS.md)): same shell, panes, fact and source-link styles, keyboard model and compact layout. Status: selected on #62. The design passed UI review; its WoW review asked for the label, field and probability fixes below, and the built feature passed WoW review. The kept-model behaviour goes to usability review on the built feature.

Open [index.html](index.html). Every name, ID, number and drawing is authored; nothing is game content. The status bar says **Sample data**. Query options:

- `state=ready|loading|preparing|failed|ambiguous|missing|unknown|none|encrypted|unsupported`. Each state selects the creature that shows it.
- `creature=<id>` selects another row: `700006` (the same display in two slots, probability 0), `700013` (ModelID 0, FileDataID 0 and a negative DisplayID), `700021` / `700022` (empty and invalid name), `700019` (no CreatureType row for its type, and a Classification outside the reviewed list).
- `q=<text>` applies a search and `mode=id` searches by exact Creature ID. A bad ID from the URL (`mode=id&q=12a`) is shown in the field, marked invalid, and not applied.
- `pane=detail` (compact layout), `panel=open` (while there is activity), `text=200`, `preview=0` (no preview kept), `chrome=0` (hides the mockup control).

## Layout

- **Two panes** under the build header, as Items: the creature list (`--side-w`, 26.5rem) and the creature detail. Both scroll on their own; the window never scrolls.
- **List pane:** **Creatures** heading with a **Name | Creature ID** switch, one search field and **Search**, the applied line with **Clear search**, a listbox, and the page buttons (50 per page, no total, **Next** when more exist). A row is the name, then `ID · ‹Title›` (or `ID · Type n · CreatureType text` when the title is empty), and the number of declared displays on the right (**None** when all four slots are 0). There are no icons: a creature has no icon of its own.
- **Detail head:** the name, then `Creature ID | ‹Title› | type label | classification label | N displays`, and **Creature #ID** at the right, which opens the whole record in Tables.
- **Detail sections**, two columns when both panes show (`1fr` and `2fr`): **Creature** on the left, **Displays** on the right. In the compact layout Displays comes first, then Creature.
- **Status bar:** exact build, source and locale, then Creatures counts (rows, named rows, declared display slots, `CreatureDisplayInfo Partial` when it is), then background work. Counts are exact, however small: Classic Era 1.15.9.70003 ships a one-row Creature table and says **1 row**.

## Creature section

Every field the Creature layout has, number first: Name_lang, NameAlt_lang, Title_lang, TitleAlt_lang, Classification, CreatureType, CreatureFamily, StartAnimState and `AlwaysItem[0–2]` (raw Item IDs, one link), each with its `Creature.Field` link.

- **Type and Family text** comes from `CreatureType.Name_lang` and `CreatureFamily.Name_lang` of this build, joined on ID, as Items reads ItemClass, with its own source link. The tables are read with the definition Tables chooses (bundled, or fetched from WoWDBDefs under the Settings switch, D101); with no definition the number stands alone. While a table loads the text reads `… CreatureType loading`; a value with no row reads "no CreatureType row has ID n" ("not in readable rows" when the table is Partial).
- **Classification** is VibeMiner's reviewed list (signed8): 0 Normal, 1 Elite, 2 Rare elite, 3 World boss, 4 Rare, 5 Trivial, 6 Minus. Any other value has no label. An empty string is **Empty string**, an invalid text is **Invalid text** with the reason, never blank. A field the layout doesn't have reads **Unsupported · not in this layout** (older layouts lack Title_lang, CreatureType or CreatureFamily). The section says this is the client record: no spawns, locations, loot or level.

## Displays section

- **All four slots, in slot order.** A declared slot (positive DisplayID) is a card. A slot with 0 is one line, **No display declared**; a negative DisplayID is one line, **Invalid reference**, and nothing is looked up. Each slot links `DisplayID[n]` and `DisplayProbability[n]`.
- **Probability as read.** DisplayProbability is a float32, printed as the shortest text that reads back to the same float32; `0` adds "0 as read". The four values aren't assumed to sum to anything: no share is worked out and no display is picked. A display that repeats an earlier slot says **Same display as slot N** and keeps its own card.
- **The chain, one block per table:** **CreatureDisplayInfo #id** (ModelID, Scale from `CreatureModelScale`, `TextureVariationFileDataID[0–3]`), then **CreatureModelData #id** (Model file `FileDataID` with **Find FileDataID N in Files**, Model scale from `ModelScale`). Scales are the source values; nothing multiplies them.
- **Only fields the layout has.** Classic CreatureDisplayInfo `F75F884C` has no ConditionalCreatureModelID or MountMaxBankingAngle, and Classic CreatureModelData `3FFEE408` has no WalkSpeed or RunSpeed; the raw list shows what the admitted layout has and nothing more.
- **Raw inputs** sit in a closed **Display and model inputs · raw** disclosure per card: the other fields of both rows (alpha, SizeClass, ExtendedDisplayInfoID, SoundID, Flags, collision sizes, CreatureGeosetDataID…), as read.
- **Links:** inside a card each block names its table, so a link shows the field only (`ModelID`, `CreatureModelScale`); its accessible name says `CreatureDisplayInfo.ModelID`. Each link opens Tables at that record with the field selected, with **← Return to Creatures selection**, which restores list, search, page, selection and scroll and focuses the link.

### Model preview

- A reserved 8.5rem square at the left of each resolved card (above the facts when the detail is narrower than 34rem).
- **On request:** **Show model** prepares that display's geometry through the shared pipeline at interactive priority, with **Stop loading model** while it runs. A model kept from an earlier look draws by itself.
- **Drawn:** unposed geometry in the geometry view's own renderer, no controls, with an "Unposed" tag. The square and **Open geometry view** open the full #59 view for that slot; its Return comes back to the same button.
- **No preview** when the chain stops before a model file (Ambiguous, Missing, Unknown, ModelID 0, FileDataID 0): "The chain stops before a model file." **Encrypted model** (`MODEL_ENCRYPTED`, the key named) and **Unsupported model** (`MODEL_UNSUPPORTED`, the reader's reason) appear only after Show model, because the engine finds them when it reads the file.
- One preview per card; they never start by themselves while the selection moves.

## States

| State | Fixture | What it shows |
| --- | --- | --- |
| Ready | `ready` | Creature 700003, two declared displays, both resolved; slot 0's model kept and drawn, slot 1 offers **Show model**. Slots 2 and 3 are one-line "No display declared". |
| Loading while the build downloads | `loading` | Download all runs. Creature is ready; Displays shows `[2/4] Downloading CreatureDisplayInfo table data, then CreatureModelData` with **Stop loading displays** and **View task**. The slots and their probabilities already show from Creature; table values read `… CreatureDisplayInfo loading`. The panel lists the displays request under "Needed by your research", ahead of Download all. |
| Preparing | `preparing` | The Creature table isn't ready. **Preparing Creatures…** with the prerequisite, **Stop preparing Creatures** and **View task**; search disabled with its reason. **Open Creature in Tables** in the detail. |
| Failed | `failed` | **Creatures need attention** with the code, **Retry Creatures preparation** and **View task**. |
| Ambiguous | `ambiguous` | 700008 slot 0: "2 CreatureDisplayInfo rows have ID 51021; none is picked, so no model is looked up", with a link to each row. Slot 1: two CreatureModelData rows match its ModelID, so it has no model file. |
| Missing | `missing` | 700011: CreatureDisplayInfo, complete, has no row 51099. **Missing**. |
| Unknown | `unknown` | CreatureDisplayInfo readable rows are Partial. 700017's display isn't in them: **Unknown**, "nothing is inferred", never Missing. |
| No display | `none` | 700005 declares 0 in all four slots: **No display declared**, the four slots still listed. |
| Known zero | `creature=700013` | ModelID 0 ("no model declared", CreatureModelData not looked up), FileDataID 0 ("no model file declared"), DisplayID −5 (**Invalid reference**). Zero is shown in normal ink. |
| Encrypted | `encrypted` | 700014 after Show model: **Encrypted model**, "TACT key … isn't available", `MODEL_ENCRYPTED`. The facts stay. |
| Unsupported | `unsupported` | 700016 after Show model: **Unsupported model**, `MODEL_UNSUPPORTED` with the reader's reason. Calm, no Retry. |
| No match | `q=zz` | "No admitted creature name contains “zz”. Search reads Name_lang only…", with **Clear search**; the selection stays open with "Selected creature … is outside these results". |

## Keyboard and focus

As Items:

- Tab order: skip link, header, area bar, **Name | Creature ID**, search, **Search**, **Clear search**, the list (one stop), page buttons, **Creature #ID**, then the detail groups in reading order.
- **The Creature section is one Tab stop, and so is each display card and each slot line.** Arrow keys and Home/End move between the group's links. A card's links are one stop (the first is `DisplayID[n]` in its head); its **Show model**, **Stop loading model**, the preview and **Open geometry view** follow as their own stops. A closed disclosure's links are skipped.
- In the list, Up/Down/Home/End/PageUp/PageDown move the selection and the detail follows; the list scrolls itself, never the window. In the compact layout Enter or a click shows **Selected creature** and focuses the creature heading.
- **Ctrl+F** focuses the search (switching to the list pane in the compact layout). Escape closes the activity panel. Typing never runs a search.
- **Show model** moves focus to **Stop loading model**; when the model is drawn, focus moves to **Open geometry view** only if it was still on Stop. Stop returns focus to **Show model**.
- Async completion (display rows, a preview) never moves focus, scroll or selection otherwise.

## 200% text and narrow windows

The Items rules apply unchanged: desktop-home's two-row header below 70rem, one pane at a time with **Creatures list | Selected creature**, the head scrolling with the facts, and the whole shell scrolling at 600×480 and 200% (D086, D092). The display count moves into the row's second line. Below 34rem of detail width the preview sits above the card's facts.

## Decisions this design makes

1. **Two panes, Displays as the main column.** The issue is about displays and models; the Creature record is a short list beside it.
2. **All four slots always listed**, declared ones as cards, 0 and negative as lines. The contract keeps all four ordinals; hiding empty ones would read as "this creature has two slots".
3. **Probability is the float as read.** No percentage, no normalisation, no pick (creatures-v1).
4. **One card per declared slot**, even when two slots name the same display: the slot, its probability and its links differ.
5. **Field-only links inside cards.** `CreatureDisplayInfo.TextureVariationFileDataID[0–3]` doesn't fit beside a label in a 30rem card; the block head names the table and the accessible name keeps it.
6. **Previews on request**, never while arrowing through the list, so moving the selection never starts downloads. A kept model draws by itself.
7. **Number first, then text.** Type and Family text from this build's CreatureType and CreatureFamily rows; Classification from the reviewed list; StartAnimState and AlwaysItem raw.
8. **Missing only with complete scope**, Unknown otherwise, as on Items. Ambiguous names how many rows matched and picks none.
9. **No icon column.** Creatures have no icon; the space goes to the title.

## Open questions

- **Usability:** whether a kept model drawing by itself while others need **Show model** reads as inconsistent. Reviewed on the built feature.
- A build whose Creature layout is refused (5.5.0's `AEA084C0` isn't admitted) keeps the Creatures failure state; that's intended.

## Engine work

- A new capability (proposed `creature-display-facts-v1`, method `creatures.display.facts`) returns, for one selected slot, the matched CreatureDisplayInfo and CreatureModelData rows as reviewed facts: each value with its table, column, column index and record, so the renderer only presents. Same parameters as `creatures.display.read`; local only. Retail (`7275F5F6`, `0F5449F8`) and Classic (`F75F884C`, `3FFEE408`) layouts name the reviewed fields the same way.
- CreatureType and CreatureFamily definitions are bundled from the pinned WoWDBDefs revision, with reference-text rules `CreatureType.ID` → `CreatureType.Name_lang` and `CreatureFamily.ID` → `CreatureFamily.Name_lang`; Creatures reads them with `tables.referenceText` on the selected Creature row and prepares the small tables through `tables.prepare`, as Tables does.
- Display rows load through `creatures.display.prepare` (the shared Tables producer at interactive priority), once for the first declared slot; the other slots then read locally. The Host's two preparation credits stay as they are.
- The preview uses the existing geometry session and contracts (`creature-geometry-v1`).

## What the app does differently

- **List rows show `ID · Type n`.** Page summaries carry no title and no CreatureType text, so the list doesn't show `‹Title›` or the type's text; the detail head and the Creature section do.
- **Type and Family text links through Creature.** The text's own row (`CreatureType #7`) is named next to the value, not linked: its Tables snapshot belongs to Tables, which reaches it by following `Creature.CreatureType` (D072). A value with no row reads "not found in CreatureType" (0 included: nothing says 0 means "none").
- **The preview is the geometry view's own drawing**, in its own colours, with **Drawing…** until the first frame and **Unposed** after. Only one preview is drawn at a time: **Show model** on another display replaces it. A preview survives the switch to the one-column layout.
- **A kept model draws by itself each time Creatures shows it** (first open, Home and back, Return from Tables or Files), for the first resolved display. That look never prepares anything; if the engine is busy it simply leaves **Show model**.
- **A busy engine is not an outcome.** When its bounded reads or preparations are full, **Show model** reads "Waiting for the engine" and asks again by itself (about 20 s), then "Engine still busy" with **Retry model**; never "Model not drawn".
- **No geometry activity block.** A model that is loading for another creature shows only where it matters: **Show model** on another display reads "Waiting for another model · Creature N · slot S is loading first" with **Stop that model**, and the display it belongs to keeps **Stop loading model** when you come back to it. A view that couldn't be closed cleanly is named in the Displays section.
- **Open geometry view and Return enter the view again.** A geometry view belongs to one canvas, so the full view (#59) is a new entry of the same selection, and Return draws the kept model in its preview again and puts focus back on **Open geometry view**.
- **Display loading** shows the engine's own stage text; while another request holds the preparation credits it reads "Waiting to load …" with **Stop waiting for displays**. After Stop or a failure the status offers **Retry display tables**.
- **Raw inputs** list every non-ID column of both rows as read (24 and 38 on Retail 12.1.5.70575, 22 and 36 on Classic 5.5.4.70631), each linked as `Table.Column`.
- **An engine without `creature-display-facts-v1`** shows each card's chain (Model ID and model file from `creatures.display.read`) with "Display facts aren’t available from this engine; only the chain is shown."
- **Ambiguous display rows** link the record once (`CreatureDisplayInfo #id`); Tables shows every row with that ID.
