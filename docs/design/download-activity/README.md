# Downloads and activity reference

Interactive HTML mockup of the build header and the one place where all background work is shown. All values are authored samples. The mockup does no network, parsing, task inspection or policy persistence.

- [A: compact header entry + nonmodal side panel](index.html?concept=drawer&state=active&panel=open). **Selected as the default.**
- [B: compact header entry + inline queue](index.html?concept=inline&state=active&panel=open). Kept for comparison only.
- [Closed entry](index.html?concept=drawer&state=active)

Query options: `state=active|paused|offline|error|idle`, `panel=open`, `text=200` (root text at 200%).

The two options differ only in where the queue goes. A keeps the research layout in place. B pushes the workspace down. Both use the selected palette, system typography, build header and horizontal areas.

## Build header

- Fields: **Product / Build / Source / Locale / Status**, then a visibly labelled **Download mode**, the **Downloads & activity** entry and **Choose build**.
- Product and Build open exact identity details for the current workspace. When the build changes, open identity or Status popovers close; they never relabel old facts as the new build.
- Source is the acquisition region. Locale is the research locale. They are separate.
- In production, Locale is static **English · enUS** with noninteractive label/value semantics. The mockup's locale picker (named cultures with codes, each task keeping its original locale) shows a future capability. It is not implemented, and the app doesn't show a disabled picker for it.

## Status popover

Opening Status runs no acquisition and no new assessment. **Check download status** is a local status read, not a rehash of every object.

- **Presence at assessment** comes from a matching retention snapshot that has been assessed. Show the encoded scope and object count, present objects/bytes, missing and known-corrupt counts, and the assessment time. Label it as an existence-and-length check at that time, not current hash proof.
- **The complement** (`total − present`, labelled **Not counted present at assessment**) appears only when the observed object scope equals the admitted ledger and nothing is unresolved or failed. It includes missing and known-corrupt members, and it isn't remaining network traffic. If coverage isn't complete, omit the complement and any percentage.
- **An incomplete assessment** shows its actual observed and present counts plus **Assessment incomplete**. Never extrapolate to members it didn't visit.
- **The opening assessment** is labelled separately: published encoded total, verified-at-assessment bytes, remaining-at-assessment bytes and its timestamp. Don't relabel it, or historical receipt credit, as a fresh presence scan.
- **Unknown versus zero.** With no measured scope, show **Encoded scope unknown** and the typed reason. A declared total of zero is a genuine zero and gets no percentage.
- **Fresh whole-scope hash check: Not assessed by this view.** No current contract provides one.
- Session payload (wire) bytes never feed local/required/remaining arithmetic. Encoded all-variants scope isn't installed size, disk usage, every ROOT file or decoded readability.
- Put the useful quantities first and the full identity and hashes after them, so the numbers are visible at 200% without paging.

## Download mode

- Download mode is a two-state switch, **On demand ↔ Download all**, for the exact build target. Clicking it while on requests On demand; clicking it while off requests Download all.
- A committed acknowledgement means saved intent. It doesn't mean the transfer is done or a worker is running.
- Download all stays selected while queued, preparing, running, stopping, paused, waiting, failed or completed. Pause never silently switches to On demand.
- **Resume build download** is a separate action for paused, waiting or failed work. Selecting a mode that's already selected is idempotent and never acts as Resume.
- Pause affects only this build's bulk consumer. Research and shared transfers continue.
- On demand withdraws bulk demand. It isn't offline mode or Stop all, and research can still fetch what it needs.
- While the policy is unknown, show **Checking…/Unknown** with no switch. Never show an OFF switch for an unknown policy.
- Without retention capability or a DL ledger, show **On demand only · Download all unavailable** with the reason, and no switch.
- Stale or lost acknowledgements reconcile through a local read before any deliberate resend. They are never replayed automatically.

## Activity entry and panel

- One App-level surface covers Discovery, preparation and Home. Individual screens don't add their own progress bars. A screen that's waiting shows a short local message and a **View task** link to its exact task.
- Panel sections: **Research**, **Build downloads**, **Recent outcome**. Rows keep a stable order and update in place.
- **Badge count:**
  - Counts current research requests, records that need attention, and saved Download all intents (paused and waiting included).
  - Doesn't count finished or stopped/cancelled Recent outcomes, dismissed records, or bulk records withdrawn by On demand.
  - Counts each logical owner once, and never counts a shared dependency again.
  - The count's meaning is stated in text, not colour alone.
- **Progress** is **% of current stage**, with exact units, and only when the denominator is known and positive. Unknown is indeterminate, and zero gives no percentage. No overall percentage, rate across tasks, or ETA. Parsing and verifying are **Processing**, not Downloading.
- **Summary priority:** attention or uncertain cleanup first, then the current active stage (naming the task it comes from), then paused/waiting, then idle.
- **Actions** belong to their original owner:
  - Stop applies to one exact research request and shows Stopping until it actually retires.
  - Stop waiting applies only to a visible unstarted intent.
  - Pause, Resume and On demand apply to the exact build target.
  - Retry appears only where the owner supports that exact intent and retirement has finished.
  - View appears only when a working route back to the same build exists.
  - Cross-build tasks keep their original identity and get no fake View.
- **Panel behaviour:**
  - At 100% the panel is a right side panel about 32rem wide, over the unchanged workbench.
  - At 200% it uses the available width, keeps Close sticky, and has no modal trap or inert background.
  - Opening focuses the panel heading once. Escape or Close returns focus to the invoker and restores the window position.
  - Async updates never open the panel, move focus or navigate.
- **Floating entry.** A compact floating entry appears only when the header entry has scrolled off-screen and the panel is closed. It opens the same panel, so two entries are never visible at once.
- **One surface at a time.** The activity panel and the identity/Status popovers are mutually exclusive. Clicking the same trigger again closes its surface.

See [UI guidelines](../../UI-GUIDELINES.md) for the shared progress, focus and announcement rules, and [async work](../../ASYNC-AND-JOBS.md) for lifecycle semantics.
