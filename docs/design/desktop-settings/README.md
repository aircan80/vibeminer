# Desktop Settings (proposed)

A small Settings view for [#8](https://github.com/aircan80/vibeminer/issues/8), reachable from Discovery and Home. It uses the [desktop home](../desktop-home/README.md) tokens, type scale and chrome. Status: approved by the owner on [#8](https://github.com/aircan80/vibeminer/issues/8) ([D070](../../DECISIONS.md)), with download limits fixed for now.

Open [index.html](index.html). All values are authored samples and carry a dashed **Sample** tag; the status bar says **Sample data**. Discovery and Home are simplified copies, only there to show the entry point and the return. Their approved layouts stay in [desktop-discovery](../desktop-discovery/all-products.html) and [desktop-home](../desktop-home/index.html). Query options:

- `screen=discovery|home`: where Settings is opened from. `area=home|tables|files|items|creatures` with `screen=home`.
- `view=settings` opens Settings directly.
- `state=reading|idle|saving|saved|failed|unreadable`.
- `limits=adjustable` shows the proposed download-limit controls instead of the fixed values.
- `activity=1` shows the Downloads & activity entry, `focus=1` numbers the Tab stops, `text=200`, `chrome=0` (hides the mockup state control).

## Entry point

- **Settings** is a plain toolbar button with a small sliders icon, always the last item in the top bar: after the activity entry on Discovery, after **← Choose build** on Home. It sits in the same top-right corner on both screens.
- At 200% on Home it moves to row 2 with the activity entry and Choose build.
- **Ctrl+,** opens Settings from any screen (`aria-keyshortcuts="Control+Comma"`).
- Settings replaces the window content below its own toolbar. The previous screen stays mounted but hidden, so its search text, filters, selection, area, Tables trail and scroll positions come back unchanged. Opening Settings closes an open popover or the activity panel.
- Background work keeps running. Settings never pauses, cancels or reorders anything.

## Layout

- **Toolbar** (Discovery's plain toolbar): logo, then **← Back to Discovery** or **← Back to Home**. The Home label names the area you left (**← Back to Files**). The activity entry sits at the right while there is one.
- **Pane head:** **Settings**, then "For every product and build. A change saves as soon as you make it."
- **Grouped list** on paper, groups with a small uppercase heading: **Builds**, **Storage**, **Downloads**. Each setting is one row: a name, one sentence of explanation, and a short **When on / When off / Takes effect** (or **Changes**) list. The control sits right after the text, not at the far edge. The text column is capped at 46rem; the control column is 16rem.
- **About settings** (side column, Home's side width, `--side-w`): saved by the engine, applies to every product and build, saved in the storage folder as settings version 1, and the time of the last change. It also says that a setting never deletes builds or files and never pauses work, and that Escape or Back returns you where you were.
- **Status bar:** "Settings · saved by the engine", then what Back returns to ("Open build kept: wow 12.1.5.70448 · US · enUS" or "Discovery kept as you left it"), then the usual background-work summary.

## Settings

- **Community build history (optional)** ([#9](https://github.com/aircan80/vibeminer/issues/9)). A switch, **Off** by default, with "Default: Off" under it.
  - When on: at each Refresh builds, Discovery also reads the build list from the community source. Only the list comes from there: product, version, build number and config hashes. All data still comes from Blizzard's CDN and is checked against its hash, so a wrong entry can only fail to open. These builds get the grey-blue **Community** badge.
  - When off: only builds seen on Blizzard's version service, and no contact with the community source. Proposed: community builds you haven't opened are hidden, and ones you opened stay with their badge. Owner to confirm.
  - Takes effect at the next Refresh builds. Switching fetches nothing.
  - The source name and host are samples (`build-history.example`). The owner hasn't picked the source yet.
- **Storage location** (read-only): the app data folder (`<userData>/engine-v1`, selectable, wraps), its size on disk in readable and exact bytes, and when it was measured. It says the size covers the whole folder, every build together, and isn't a build's download size from Home. **Copy path** and **Show in folder** sit in the control column.
- **Download limits:** today's real values, **Fixed for now**: up to 4 downloads at once, at most 2 of them background (the engine's HTTP permits), no bandwidth cap. `limits=adjustable` shows the proposed controls: **Parallel downloads** (1, 2, 3, 4 default, 6, 8) and **Bandwidth cap** (No cap default, 2 to 100 MiB/s). Each says what changes, and that the cap is an upper limit, not a promised speed.

## States

- `reading`: "Reading saved value…" with no switch, the same rule as Download mode's **Checking…**. Size on disk shows **Measuring…**. About says defaults are not shown as if they were saved.
- `idle`: the committed values.
- `saving`: the switch keeps its committed position, is `aria-disabled` (focus stays on it) and reads **Turning on…**. The reserved line under it says **Saving…**.
- `saved`: **On**, with "Saved · <time>" in the reserved line. The row never moves when this line changes.
- `failed`: the switch is back at **Off**. An error notice at the row, "Couldn't save Community build history. It's still Off.", names the cause (`SETTINGS_WRITE_FAILED`) and offers **Retry turning on**. It stays until the user retries or switches.
- `unreadable`: the saved value couldn't be read or isn't one VibeMiner understands. The switch shows the default, Off, with "Default: Off · in use now", and a warning box at the row says so (`SETTING_UNREADABLE`). About says "1 setting is using its default". Switching saves a new value.
- The error codes are proposals for the settings contract.

## Keyboard and focus

- Opening Settings focuses the **Settings** heading (`tabIndex=-1`). Escape or Back returns to the previous screen and puts focus back on its Settings button. If the activity panel is open, Escape closes it first.
- Tab order in Settings: skip link → **← Back** → activity entry (then its panel, when open) → Community build history switch → **Retry turning on** (failed only) → **Copy path** → **Show in folder** → Parallel downloads → Bandwidth cap (adjustable only). About has no controls.
- On Discovery the Settings button comes after the activity entry, before the filter row. On Home it comes after Choose build, before the area bar, which follows the header in DOM order as on Home.
- A save never moves focus. If **Retry** disappears while focused, focus moves to the switch.
- One polite status line per setting announces Saving and Saved. A failed save uses `role="alert"`, because the user just asked for that change.
- The switch is a native button with `role="switch"` and `aria-checked`, named by the setting's heading and described by its explanation and save line.

## 200% text and narrow windows (below 70rem)

- The Settings view becomes one scrolling pane. Each control moves below its text, and About follows the list. The window still never scrolls sideways.
- The toolbar keeps one row: the logo mark, Back and the activity entry (short label below 84rem).
- Home's header takes two rows as on Home, with Settings on row 2.

## Carried over

- Tokens, type scale (11 / 13 / 14 / 19px), toolbar, pane head, pills (including the Community badge), notices, state boxes, facts lists, the status bar, the focus ring and the activity chip and panel, from desktop-home and desktop-discovery.
- The labelled switch from Home's Download mode, in a control-sized frame.
- Download policy rules for committing: show the committed value, say Saving while committing, never replay a failed change.

## Deliberate departures

- **Settings has its own plain toolbar**, not the build header. Settings apply to every build, so the build identity moves to the status bar ("Open build kept: …").
- **No Settings button inside Settings.** Back takes its place at the left. A button that does nothing would be a dead control.
- **Download limits show real fixed values**, not a disabled control. That's honest today. If the owner wants controls, `limits=adjustable` is the proposal.

## Engine work this mockup assumes

- A versioned settings store in app storage, with read and set contract methods, bridge validation and typed failures (#8).
- A folder size measurement for Storage location, or the row drops the size.
- The community source, import and badge (#9). Adjustable limits would need the HTTP permits and a bandwidth cap to be configurable at runtime.

## What the app does differently

- No community source is picked yet ([#9](https://github.com/aircan80/vibeminer/issues/9)), so the app leaves out the sample source name and host and adds a **This version** line to Community build history: nothing changes until a source is added, and the choice is saved now.
- Size on disk is the sum of file lengths under the folder, measured when Settings opens. Items the walk couldn't read are named under the size, and a failed measurement offers **Measure again**.
- A failed read of the settings shows **Couldn’t read settings.** at the row with **Read again**, and no switch.
- `limits=adjustable` stays a proposal; the app shows the fixed values.
