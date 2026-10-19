# Desktop discovery (proposed)

Redesign of the first screen so VibeMiner feels like a desktop app instead of a scrolling web page. It replaces the layout of [guided discovery](../build-discovery/index.html?concept=guided&state=ready&product=wow) but keeps its meaning: one list of advertised and saved builds, one selected build, one Open action, no game download from browsing. Status: approved by the owner ([D059](../../DECISIONS.md)).

Open [index.html](index.html). Query options: `state=idle|refreshing|stopping|offline|partial|filtered|opening|downloading|attention|empty`, `panel=open` (with `downloading` or `attention`), `text=200`, `chrome=0` (hides the mockup state control). All values are authored samples.

## Window

- **No visible menu bar.** Hide it with `setMenuBarVisibility(false)` (not auto-hide, so Alt doesn't pop it up). Keep the application menu so standard roles keep working: copy/paste/select all, zoom (Ctrl+= / Ctrl+- / Ctrl+0), close and quit, and the macOS app menu.
- **Shortcuts:** Ctrl+F focuses the build filter. F5 starts Refresh builds when no refresh is running; it never cancels one. Escape closes the activity panel.
- **Fixed window, scrolling panes.** The window never scrolls, at any size or text scale. The build list and the inspector scroll on their own.

## Layout

- **Toolbar** (about 44px): logo, Product, Refresh builds, and the activity entry when there is one. Refresh becomes **Cancel refresh**, then **Stopping discovery…** (`aria-disabled`, so focus stays on it), and **Retry discovery** after a failed or partial check.
- **Build list.** One row per exact build identity. Columns: Version, Build, Advertised in, Publication, Downloaded, In VibeMiner. Build config, CDN config and the full identity are in the inspector only. It's a listbox: one Tab stop, then Up/Down/Home/End/PageUp/PageDown move the selection. The column header stays put and rows never scroll under it. **Load more builds** sits at the end of the loaded rows.
- **Notices** go between the filters and the list: refresh unavailable (per-region errors, Retry discovery), incomplete evidence (per-region errors), and a hidden selection (Clear filters). Errors never live only in the status bar.
- **Inspector** has three parts. The header lines up with the bottom of the list header. Identity and observation details scroll. Download source, **Open build** and **Source details** are pinned at the bottom at every text size.
- **Status bar.** One line: checked regions, last check, refresh progress, and a summary of background work. It drops the secondary items when text is enlarged.
- **Empty state.** Filters are disabled and the inspector collapses until there is something to select.

## Activity entry

- It's visible only while there is something to show: active, paused or waiting work, or an outcome still to review (failed, stopped or canceled, until **Dismiss outcome**). With nothing to show there is no entry, no badge and no floating button.
- **Needs attention** is the amber variant for outcomes to review. The status bar echoes it while the panel is closed.
- The panel is nonmodal and comes right after the entry in Tab order. It covers the inspector column and makes the inspector inert while open. Close or Escape returns focus to the entry.
- Opening a build shows its progress in the inspector, with **View preparation** and **Cancel opening**.

## Data rules

- Regions merge into one row only when the whole versions row matches: BuildConfig, CDNConfig, ProductConfig, KeyRing and unknown columns. Two rows with the same version (the TW row in the sample) are different builds. Their regions and the inspector's full identity tell them apart. Most `wow` builds show as one row.
- **Advertised in** lists the regions with a versions row at the last check. Saved rows say "None at last check".
- **Publication** (Advertised / Not advertised) and **In VibeMiner** (Prepared, New to VibeMiner) are separate facts in separate columns.
- **Downloaded** is the share of the build's download manifest (all variants, encoded bytes) present locally at the last complete check. 100% means every file in that manifest is stored. A build that was never opened has no manifest and shows "Not opened", not 0%. An incomplete or missing check shows "Not checked", never a partial percentage. A pulsing dot marks a build with a running download. The inspector shows bytes, scope and check time. The app currently has this summary only for builds with a saved download job (at most nine). Other prepared builds show "Not checked" until the engine can return a summary for every listed build.
- **Download source** offers only regions that have both a versions row and a matching cdns row (SG in the sample has none). With no recorded route it shows "No recorded source route", disables Open build and says why. It never falls back to US.
- The inspector shows Product config and Key ring. A missing column shows "Not supplied"; a present but empty one shows "(empty value)". A key ring reference doesn't mean its keys are available.
- **Not prepared**: "Opening verifies configs and measures download size." **Prepared locally**: "Local evidence recorded. Game content stays on demand."
- Product labels come from the registry in `src/backend/VibeMiner.Domain/Discovery.cs`.

## Deliberate departures from the old references

- Type scale is 11 / 13 / 14 / 19px, with a 14px base instead of 15px, for desktop density.
- Muted text is `#586a64`, at least 4.7:1 on every surface here including the status bar and mint, instead of `#697b76`, which was below AA.
- Pills share one outlined style. **Prepared** is outlined so it doesn't look like the Open build button.
- Inspector is about 370px (`26.5rem`), capped at 44% of the window, so 32-character hashes fit on one line.
- At enlarged text, the list drops the Build column, and the inspector moves below the list as a second pane.
- Once approved, this supersedes the literal-copy rule (D021) for this screen.
