# VibeMiner

VibeMiner explores Blizzard CDN data, retains exact builds in shared local storage, and helps researchers inspect files, tables, assets, and changes between builds.

This repository continues the unfinished Java VibeMiner application using **Electron and a C# backend on modern .NET**. The product and existing workflows carry forward; implementation and verification must be established again for this stack.

## Project decisions

- Electron presents the desktop application; C# owns acquisition, parsing, jobs, storage, indexing, and comparison.
- SQLite stores mutable history, receipts and admissions. Large verified sources and compact immutable indexes live in one app-owned content-addressed object store.
- We write our own acquisition code, WoW protocol parsers, and asset decoders. TACTSharp and DBCD are reference material only.
- Development is async first. Long-running operations have explicit cancellation, bounded progress, and bounded resource use.
- Users can explore while builds download in the background. Missing data needed by the current screen receives priority through the same shared acquisition service.
- Meaningful unit tests are a core delivery requirement, alongside integration and desktop checks where needed.
- The starting UI reference is [guided build discovery](docs/design/build-discovery/index.html?concept=guided&state=ready&product=wow). Deliberate redesigns and polish are encouraged with UI/usability review; the old standalone Atlas prototype is superseded for this flow.
- Model viewing and portraits are planned capabilities. The frontend uses React, TypeScript and Vite; Three.js is the planned rendering layer.
- Original project software remains GPL-3.0-or-later. See [licensing](docs/LICENSING.md).

## Current state

[Build Discovery](docs/PRODUCT.md) launches a real C# engine, checks official metadata on request and retains exact observations. Startup restores local history without an automatic network check. Open Build now verifies the build/CDN configurations and download manifest, then opens Home with its scoped encoded download size and dated local-reuse assessment. A compact immutable object ledger replaces full research-catalog indexing on this path; ROOT/IN/DS resolution and old interrupted-catalog cleanup do not block Home. Missing or unsupported size capability stays explicit. [The size-first design](docs/SIZE-AND-DEMAND-ARCHITECTURE.md) and [storage notes](docs/STORAGE.md) describe the flow and its limits.

The build workspace uses horizontal **Home / Tables / Items / Creatures / Download objects** navigation. [Tables](docs/WOW-DATA-REFERENCE.md) browses the pinned provider catalogue, build-candidate structures and declared definition relationships with an inspector and Back/Forward trail. Twelve bundled community definitions are available. Entering **Data** automatically acquires missing supported table content for enUS, validates its actual header/schema and displays paged real records with an inspector and raw-ID filter. Classic beta ItemClass and partial Retail/Classic Item are qualified; Retail ItemSparse now admits 175,625 rows and 103 columns. Readable sections of authenticated encrypted containers load with protected rows explicitly excluded. Malformed text has explicit unavailable-cell evidence instead of inferred replacement text. See [async and jobs](docs/ASYNC-AND-JOBS.md), [the table-data contract](contracts/table-data-v1.md) and [WDC5 notes](docs/formats/wdc5.md). Available definitions never stand in for acquired game rows.

The [size-first architecture](docs/SIZE-AND-DEMAND-ARCHITECTURE.md) preserves legacy catalogs and metadata while preparing targeted research lookup foundations. Shared physical acquisition and priority-aware resource gates are implemented; durable whole-build background retention has authored end-to-end qualification. The dedicated Items browser automatically prepares names, substantial supported properties/stat inputs, viewport icons and optional Class/Subclass labels, while preserving usable records when optional data is unavailable. Original Sparse and BLP2 readers, real-source offline icons and exact classification source navigation are qualified. The reviewed Creature browser automatically prepares client names, literal-name/exact-ID search and all four original display declarations, with exact source-record navigation. Selecting a positive Creature display declaration automatically resolves missing display/model metadata, preserves exact source navigation and keeps core browsing usable; appearance remains unevaluated. Selected-item client effects have a reviewed interface and an original-data-qualified backend; unified activity, the compact header and storage Status are delivered and reviewed. Development is paused for planning. Geometry modules and actual-source DPR1 rendering are qualified separately; production integration and unresolved DPR2 capture behavior remain future work. Broader assets, NPC models/portraits and comparisons remain on the roadmap. See [the issues](https://github.com/aircan80/vibeminer/issues).

On this workspace, run `source tools/dev-env.sh` followed by `npm start`. For a fresh checkout, use [the install/build instructions](BUILDING.md). Testing and platform limits are in [docs/TESTING.md](docs/TESTING.md); Java release results do not establish passing results for this port.

Legacy full research preparation retains verified stage checkpoints across restarts; see [storage notes](docs/STORAGE.md). Its unresolved Classic beta ROOT references no longer gate the independent DL size. The new opening contract and initial resource bounds are documented in [download-size Home](contracts/download-v1.md).

Start with [the documentation index](docs/README.md), [development setup](BUILDING.md), and [agent instructions](AGENTS.md).

## Repository layout

| Path | Responsibility |
| --- | --- |
| `apps/desktop/` | Electron main/preload code and React presentation |
| `src/backend/` | C# engine, use cases, infrastructure, and pure parsers |
| `contracts/` | Versioned Electron-to-engine protocol and compatibility rules |
| `tests/` | Unit, integration, architecture, fixture, and desktop verification |
| `docs/` | Product requirements, architecture, decisions, migration, and delivery |
| `docs/design/` | Selected design references and related browser mockups |
| `agents/` | Team roles, work-item format, and handoff format |
| `branding/` | Original VibeMiner artwork |
| `tools/` | Build, contract generation, toolchain and packaging helpers |

The application accesses official CDN sources and its own storage. It does not discover or import installed games, Battle.net directories, or game-generated caches. WoW is the first format target; discovering another product does not imply decoder support.
