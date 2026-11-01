# Handoff, 2026-11-01

How to continue VibeMiner on another machine or in another session. The owner (aircan80) asked for this file explicitly. Normally AGENTS.md says not to commit handoff notes, so treat this as a one-off. Durable facts belong in `docs/`, and open work belongs in GitHub issues.

## Where things stand

The storage rewrite has rebuilt the engine as seven code layers. Layers 1–6 and the CLI are on `main`. CI is green at b108726b. Nothing was in progress at handoff. The Electron app still runs on the old code, except for the Discovery build list, which now comes from layer 3 (#205).

| Layer | Project(s) | State |
| --- | --- | --- |
| 1 Primitives | `VibeMiner.Objects`, `VibeMiner.Cdn`, `VibeMiner.Generated` | Done. Object store with packs, index, history log and scrub; spec in [docs/formats/object-store-v1.md](docs/formats/object-store-v1.md) |
| 2 Object access | `VibeMiner.ObjectAccess` | Done. The only place that chooses between local and CDN: merged and single ranges, promotion, host ranking, next host after a break |
| 3 Builds | `VibeMiner.Builds` | Done. Catalog, open build, resolver, Downloaded figure, Download all, region choice |
| 4 Files | `VibeMiner.Files` | Done. A file system per build and locale: List, Stat, Get, ReadRange, OpenRead, Ensure, names, formats |
| 5 Formats | `VibeMiner.Formats` | Done. DB2 reader and definitions, ITables with direct reads and tables shared across builds, preload, BLP, M2/SKIN |
| 6 Game concepts | `VibeMiner.Concepts` | Done and closed (#204, b108726b) |
| 7 Features | `VibeMiner.Cli` (`vm`, `vm browse`), `VibeMiner.Composition` | `vm browse` has `:store :history :builds :files :tables :settings :items :creatures`. The app's Discovery list uses layer 3 |

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first. It has the layers, the rules, the stores and testing by layer. Decisions are in [docs/DECISIONS.md](docs/DECISIONS.md); the storage rewrite uses D121 onwards.

Live numbers at handoff:
- Retail 12.1.5.70688: 987 Ready tables out of 1,270 (142 Partial, 138 not in the build).
- Classic Era 1.15.9.70576: 962 Ready tables.
- Classic Era icons: 6,358 of 6,364 decode.
- Table pages: about 100–250 ms for a cold first page, a few ms after that.

## Run it

Toolchain: the .NET SDK pinned in `global.json` (10.0.402, `latestPatch`) and Node. `source tools/dev-env.sh` before every npm or dotnet command. Shell state doesn't persist between agent calls. If the system SDK is older, set `VIBEMINER_DOTNET=.local/toolchains/dotnet/dotnet` (see [BUILDING.md](BUILDING.md)).

- App: `npm run build`, then `npm start`. Run it **without** `VIBEMINER_DATA_DIR`. The data folder then defaults per D128 (Linux `~/.local/share/VibeMiner`), which the app and `vm` share. While the app runs, it holds the object store's writer lock, so `vm` reads still work and `vm` writes report busy.
- CLI: `node tools/dotnet.mjs run --project src/backend/VibeMiner.Cli -c Release -- browse`. Typical flow:
  - `:builds` then `r` refreshes the list.
  - Enter on a build, then `f`, opens its files.
  - `m` fetches metadata and starts the table preload; `P` stops or resumes it.
  - `N` fetches listfile names.
  - `:tables`, `:items`, `:creatures` browse the data.
  - `?` shows the keys.
- Tests:
  - Layer-scoped: `npm run test:layers` (D137).
  - Everything: `npm test` and `npm run test:e2e`, for milestones only.
  - Contracts: `npm run contracts:check`.
  - Package smoke: `npm run package:desktop && npm run smoke:package`.
- Test runs and QA must set `VIBEMINER_DATA_DIR` to a temp folder. Never use the owner's folder.

What doesn't come with a fresh clone. All of these are local and rebuild or re-download:
- `~/.local/share/VibeMiner`, the owner's new store.
- `.local/data-v16`, the old profile; slice 9 deletes it.
- `.local/scale-store`, cached 3M/10M test stores, which rebuild on first use.
- `.agents/workspace/`, scratch: Phase 0 measurements and the #201 scrub harness.

## How the work is run

The owner set these rules. They're in [AGENTS.md](AGENTS.md) and [.agents/rules/workflow.md](.agents/rules/workflow.md) where noted.

- Owner's word wins over the docs. The owner makes product calls; agents may decide inside an agreed issue and record the decision as a D-row.
- One issue is one testable slice. Commits end `Refs #N`, never Closes/Fixes. Identity is `aircan80 <329744803+aircan80@users.noreply.github.com>`. No Claude attribution lines at all. Never force-push.
- Merge to `main` once the reviewer (and any listed WoW, UI or usability expert) passes and the changed layer's unit tests are green. CI runs everything on `main`. QA checks on `main` in parallel and is the only role that closes issues. Full local gates run at milestones only (workflow steps 5–8).
- A red CI run on `main` gets a bug issue right away.
- Tests stay inside their layer, and lower layers are fakes (D137). A change to one layer runs that layer's tests, plus the callers' tests when an interface changes.
- Each layer boundary is an interface with an in-memory fake and a shared contract suite. Everything external goes through layer 2. Layer 1 is the foundation: don't change its on-disk format without the owner.
- Agents: give each subagent one small task with a narrow brief, and keep at most 3–4 running at once. The reviewer, QA and experts never review work written in the same session (`.claude/agents/`).
- Linux first. Windows and macOS are parked: #4, #30, #31, #33, #37.
- Never touch the owner's real profile without explicit instruction. Official CDN sources and app-owned storage only. Never read game installs. The only exceptions are WoWDBDefs (D081), wow-listfile (D105) and wago.tools (D106), each behind a switch.

Owner decisions that shape the design:
- Download model: an opened build gets its metadata and every DB2 table eagerly. Assets come on demand. Download all is a per-build switch (D360, D230).
- HTTP: 16–32 adaptive background streams, with 4 kept for interactive work.
- Reset keeps only the object store, and wipes the catalog, the generated store and settings. After a reset, reopening asks only the version service (#195 proves it).
- Start fresh: no import from the old store. Slice 9 (#158) deletes the old code and `.local/data-v16`. That deletion is granted once, with no prompt.
- Scale tests: 3M everyday, 10M for sign-off; 30M is optional.
- The terminal browser (`vm browse`, k9s-style) is the main CLI. Plain `vm` commands stay for scripts and QA.

## Open work

Waiting on the owner:
- **D260:** TACT keys come only from the build's own TactKey/TactKeyLookup tables. Applied as the default, pending confirmation. Most Retail Partial tables need keys the build doesn't ship.
- **#191:** tables with no stored IDs (BroadcastTextDuration, CharStartKit). Is it wanted?
- **#204 question:** Retail ItemModifiedAppearance is Partial, so the default appearance shows Unknown. Show a labelled "likely default" instead?
- **GUI:** when to resume moving the app onto the new layers. The owner said to start nothing new after #205.

GUI path, in order:
1. Open a build from the new list (not filed yet).
2. #181 Tables screen.
3. #188 Settings screen.
4. #196 preload activity panel.
5. #157 Items and Creatures screens.
6. #160 simplify the table screens.
7. #158 delete the old code and old data, last.

Follow-ups and bugs:
- **#202:** 6 BLPs with palette and 8-bit alpha.
- **#203:** 11 M2 skins whose UV lookup points at no channel.
- **#169:** shared-copy re-read leftover.
- **#182 notes:** Ctrl+C in prompts, help line widths, `:tables` paging keys.
- **#207:** at 80x24 the `?` help cuts off `:tables`, `:items` and `:creatures`.
- **#170:** CLI list.
- **#161:** flaky definition test.
- **Not filed yet:** VerifiedRawIntegerRules is never passed in layer 5. Armor textures via ItemDisplayInfoMaterialRes.

Parked: #65 (reclaim; it deletes data, so ask the owner before starting) and the Windows/macOS issues.

The plan page from the start of the rewrite: https://claude.ai/artifact/9dQnh4djxhHutg54pCjUJ6 (owner's account; it may be out of date, and the issues are authoritative).
