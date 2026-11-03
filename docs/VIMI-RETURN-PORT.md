# Vimi backend return port plan

Review date: 2026-11-03. Proposed implementation sequence; no implementation authorized by this document.

Complete the C# backend by carrying forward the implemented behavior in the newer Java `vimi` project. Keep Electron screen work separate. Start with shared storage and headless preparation, then close table/item gaps and add the new concepts, world, media and comparison capabilities. Preserve the existing C# strengths and tests instead of translating Java classes mechanically.

## Mandatory C# implementation

The backend must be C#. Under no circumstances import Java code into this repository. The sibling Java project is a read-only behavioral reference: study its responsibilities, algorithms, supported formats and test scenarios, then implement and test those behaviors in C#.

Do not copy Java source or test source, include Java bytecode/JARs, add a JVM worker, embed a Java runtime, or depend on the Java checkout for building, testing, packaging or running this app. Recreate meaningful test cases and authored synthetic fixtures with C# generators and assertions. Java class names and source paths in this plan identify reference behavior, not files to import. Importing stored user data, if separately approved, is data migration implemented in C#; it is never code import.

## Roadmap and milestones

These milestones group the detailed delivery slices below. They are ordered by dependencies rather than calendar estimates. Each milestone ends with demonstrated C# behavior before its dependent milestone begins.

| Milestone | Scope | Exit gate |
| --- | --- | --- |
| 1 Establish the target | Slice 1: freeze reference revisions, map capabilities and limitations, identify existing C# coverage. Resolve storage direction and record the C#-only requirement. | Every accepted capability has a destination, dependencies and a headless acceptance scenario. No Java files or runtime dependencies are introduced. |
| 2 Complete the storage foundation | Slices 2–4: shared packs, immutable ownership, inventory generations, build status, accounting and explicit preservation/import services. | C# recovery and isolated migration tests preserve bytes and durable intent, reject incomplete activation and survive restart. Existing profiles remain untouched. |
| 3 Complete headless build preparation | Slice 5 plus Files deltas: one composition, metadata opening, Files/Tables preparation, durable downloads, settings and lifecycle. | CLI integration demonstrates acquire → prepare → query → close → offline reopen, including independent consumers and interactive priority. |
| 4 Complete tables and existing concepts | Slices 6–7: preparation inventories, local row capture, text and relation helpers, Items catalog/stat calculations, Creatures/Effects deltas. | C# regression cases prove exact identities, bounded paging/capture and honest partial/unsupported outcomes. |
| 5 Add the remaining game services | Slices 8–9: display models, asset composition, talents, race metadata/appearance, mounts, professions and dungeons. | Each service is usable headlessly for its supported profiles, with real/fake contracts and dependency/provenance tests. Race voice support finishes with milestone 7. |
| 6 Add approved enrichment | Slices 10–11: provider services and refresh, NPCs, quests, locations and map artwork. Obtain source-policy decisions before provider implementation. | Approved providers retain exact versions, notices and offline data; merged results retain field provenance. Blizzard-only adapters and artwork can proceed earlier. Unapproved providers remain an explicit scope decision. |
| 7 Complete world and media | Slices 12–13: terrain formats/services and C# voice decoding/isolation; decide separately on CPU preview rendering. | Malformed input, memory/time limits, cancellation, stale contexts and offline artifacts pass without a Java runtime. Interactive viewer and playback controls can wait. |
| 8 Complete comparison and integration | Slices 14–15: delivered comparison scope, bounded Host adapters, replacement of existing legacy consumers and final acceptance. | Accepted backend capabilities work through C# CLI/engine contracts; existing desktop behavior survives cutover; milestone checks pass before legacy removal. New GUI screens remain deferred. |

After milestone 4, build-backed services, provider work and world/media work can progress independently where their prerequisites and decisions permit. Comparison can also begin once its local table/item inputs are ready; it need not wait for new providers or world services. Implement Host adapters incrementally as services settle, then perform the final cutover in milestone 8.

The first implementation tranche should cover milestones 1–3. This produces a usable, independently verified foundation before the larger game-service expansion. Convert the slices into testable feature issues when implementation is requested; this roadmap itself does not start coding or authorize profile migration.

## Baselines and scope

- Java checkout: `/Users/aircan/Development/vimi`, HEAD `767ec16311bc6d66f0dfde7c53237473d65f7ef4`. The requested `~/vimi` path was absent. An untracked `md` file was left untouched and is outside the baseline.
- C# checkout: this repository, HEAD `1d6632e94107eb3f295a1a7b3463ed69af6dc71c`, initially clean.
- The older baseline in [MIGRATION.md](MIGRATION.md) is a different Java source checkpoint. It does not describe these later changes. Keep its provenance rather than silently replacing it.
- Review perspectives: Java lower-layer expert, Java/C# software architect, backend developers covering formats/concepts/rendering, and the coordinating repository review. This is source and test inspection, not a passing-test or performance certification. Neither application's tests or live providers were run.

“Backend complete” means every implemented, in-scope Java capability has a C# service and a headless acceptance path, with exact identity, bounded resource use, cancellation, durable reopen and explicit unsupported states. It includes preparation, provider policy, migration and lifecycle behavior currently inside Java's `gui` module. It excludes JavaFX widgets, screen layouts, camera controls and desktop playback controls. Renderer-independent composition/geometry and terrain preparation remain in scope; interactive GPU presentation can follow later.

Do not absorb unfinished Java roadmap work into parity. In particular, the native comparison text-issue certificates are incomplete on separate recovery branches; the Java handoff explicitly warns that branch names and reviewed designs do not establish completed implementation.

## Layer review

Paths in this section are relative to the pinned Java checkout unless marked C#.

| Area | Current comparison | Port treatment |
| --- | --- | --- |
| 1 Primitives | C# already has pack storage, verification, generated records, recovery tests and clients. Java adds a mixed original/generated library, immutable owner capabilities and an authoritative ownership catalog. | Port the selected unified pack design and its recovery/ownership behavior. Keep legacy pack compatibility. Established authoritative indexes must be refused if missing or damaged, rather than rebuilt as disposable caches. Do not port the superseded SQLite BLOB experiment as the default. |
| 2 Object access | Both have local/CDN selection and bounded acquisition. Java's latest composition supplies owner-bound sources to the shared library. | Audit incremental differences, retain one acquisition pipeline, and propagate captured ownership through reads, cache hits, staging and shared downloads. |
| 3 Builds | Existing C# discovery/open/resolution provides a strong base. Java adds persisted load attempts/status and inventory publication tied to canonical owners. | Port `BuildRegistry`, `BuildObjectInventory` and accounting integration. Metadata loaded, inventory measured, research prepared and fully downloaded remain separate facts. |
| 4 Files | Both implement build/locale files, BLTE, protection states, names and Ensure. | Port `PreparedFileCatalog` and name-coverage deltas where missing, plus fixes and ownership integration; preserve names/delta formats, cancellation and interactive priority. Do not rewrite the layer merely to match module names. |
| 5 Tables | Existing C# DB2/DBD/generated tables cover much of the initial Java port. Later Java adds completed preparation inventories, bounded local row capture and shared game-text interpretation. | Port `PreparedTablesCache`, `CapturedRows`/`CapturedRowsHandle` and `GameTableText` before their item/comparison consumers. Keep schema, definitions and effective-key identity in cache admission. Extend shared relation warmup/indexes and spell-text interpolation rather than repeating scans in each concept. |
| 5 Assets | BLP/M2/SKIN and geometry already exist in C#. Java adds reusable mesh/material composition paths and concept consumers. | Compare supported profiles and synthetic vectors first. Port actual deltas and reusable codecs; preserve explicit static/unsupported animation limits. |
| 5 World/audio | Java has new terrain parsing and voice decoding; no corresponding new-layer C# services were found. | Add bounded format services, terrain data/artifacts and audio isolation. Select an audio implementation consistent with the own-decoder rule before coding it. |
| 6 Existing concepts | C# contains Items, Creatures and ItemEffects despite architecture documentation describing layer 6 as in progress. Java extends these with indexed item catalogs, attributes/stat calculations, armor provenance and lifecycle fixes. | Establish behavior/test deltas rather than treating these modules as wholly missing. Port catalog/stat behavior and regressions. |
| 6 New concepts | Java implements talents, races, shared display models, mounts, professions, dungeons, world, NPCs, quests, locations and comparison services with tests. | Add each as a separate testable deliverable. Scope results to the implemented profiles and provider evidence. |
| Supporting services | Accounting, source preferences/refresh and provider management have grown substantially. Much orchestration still lives in Java `gui`. | Extract C# headless services; keep SQLite and acquisition outside renderer/main. External-provider activation requires a policy decision. |
| 7 Composition | C# has `vm` plus a Host/Electron stack still largely using legacy projects. Java distinguishes metadata opening from preparation and now wires both, plus durable bulk downloads; older overview prose understates this. | Use CLI/headless integration to prove the full preparation path independently of either GUI. Move Host to layers after backend gates; defer new screens. |

### Evidence worth carrying forward

- Storage: `object-store/.../ObjectStore.java`, `LibraryCatalog.java`, `PackLibraryOwner.java`, `generated-store/.../PackGeneratedStore.java`; `PackLibraryTest`, `PackLibraryRecoveryTest`, `LibraryCatalogTest`, `UnifiedLibraryCompositionTest`; `accounting/.../UnifiedStorageAccountingTest`.
- Lifecycle/migration: `gui/.../BuildApplication.java`, `PackLibraryMigration.java`, `BuildPreparation.java`, `NativePreparationContexts.java`, `NativePreparationWork.java`, `SourcePreferences.java`, `SourceRefresh.java`, `SettingsService.java`, `DataSourcesService.java`, `BuildDownloads.java`, `NativeDownloadRunner.java`; `BuildDownloadsTest` and `NativeDownloadIntegrationTest`; `PackLibraryMigrationTest` and `FullPreparationLibraryIntegrationTest`.
- Tables/items: `formats-tables/.../PreparedTablesCache.java`, `CapturedRows.java`, `GameTableText.java`; `PreparedTablesCacheTest`, `CapturedRowsTest`; `concepts-items/.../ItemCatalog.java`, `ItemAttributes.java`, `ItemStatCalculator.java`, `ItemArmorMaterials.java`, `ItemStatCalculatorTest`.
- New concepts: each `concepts-*` module owns service tests, including `TalentsServiceTest`, `RacesServiceTest`, `MountsServiceTest`, `ProfessionsServiceTest`, `DungeonsServiceTest`, `LocationsServiceTest` and `WorldServiceTest`.
- World/media: `formats-world/.../WorldFormats.java` and `WorldFormatsTest`; `rendering-models/.../TextureComposer.java`, `PreviewMeshCodec.java`, `MeshAppearanceCodec.java`; `rendering-world/.../TerrainRenderer.java`; `formats-audio/.../VoiceDecoder.java`, `VorbisWorker.java` and `VoiceDecoderTest`.
- Comparisons: `concepts-comparison/.../ComparisonService.java`, `StreamingItemJoin.java`, `BudgetedTempFile.java`, `inputs/PreparedTableRowsSource.java`; `ComparisonServiceTest`, `ComparisonSharedBudgetTest`, `ComparisonMemoryPlanTest`; `gui/.../NativeRawComparisonIntegrationTest`.

The authoritative storage explanation is `docs/unified-storage-services.md`. `docs/unified-storage-rollout.md` retains superseded BLOB research. `architecture.md` and README sections also contain older scope descriptions; use code and scoped tests to settle conflicts.

## Proposed destination architecture

Keep the seven C# layers and downward-only interfaces. Start by extending existing projects: Objects/Generated/Cdn, ObjectAccess, Builds, Files, Formats and Concepts. Split a project only when an independent dependency or lifecycle warrants it; Java's Gradle module count is not a C# requirement.

Storage mechanics belong in Objects; generated facades in Generated; canonical build inventory/status and reporting projections in Builds. Keep domain-specific producer enumeration outside the store. Add narrow interfaces so upper layers never query the library database directly. Composition alone wires concrete instances.

Place the new concept services in separate namespaces under Concepts initially. Consider separate provider projects for Questie, Polestar and EG Link: they have independent acquisition, licenses, schemas and snapshot lifecycles. Give those projects approved interfaces and explicit architecture-test allowances, rather than allowing arbitrary cross-layer access. Renderer-neutral asset composition belongs with Formats; drawing and UI controls stay in the desktop.

Extract preparation, source-refresh and operation ownership from Java GUI classes into headless C# orchestration, with concrete wiring in Composition. Settings persist policy; each build operation captures an immutable snapshot. Long jobs expose cancellation, bounded progress and terminal outcomes. Avoid a global mutable current build/owner. Do not create another downloader for providers or media.

Java virtual threads, FFM SQLite and JavaFX scheduling are implementation details. Use asynchronous .NET I/O, bounded CPU/database workers and owned disposal. Audit unsigned keys, checked lengths, UTF-8, byte order, float encoding, hashes and first-committed-value behavior through shared vectors. Do not reproduce blocking Java `join()` patterns in C# orchestration.

## Decisions before the affected implementation

1. **Storage adoption.** Recommend the mixed pack library with immutable ownership and one authoritative physical-presence index. Java kind/application identity 2 is explicitly different from legacy kinds 0/1; existing C# cannot open it. Decide whether importing current Java libraries is required, alongside preserving existing C# profiles. Direct cross-runtime write compatibility needs its own specification and tests; legacy interop proves neither.
2. **External sources.** Current repo rules allow official CDN plus WoWDBDefs, listfile and wago build-list exceptions. Java additionally uses Questie, Polestar, EG Link and public TACTKeys. Recommend separate optional, consent-controlled providers with pinned revisions, bounded URLs, notices and offline retention. Owner approval and decision records must precede runtime activation. Until then, build-backed modules can proceed; provider parity remains explicitly pending.
3. **Preparation behavior.** Java separates metadata opening from preparation; its current `BuildApplication` wires full preparation and bulk downloads despite older builds-only overview text. C# already supports exploration while downloading. Recommend metadata open plus explicit independent preparation jobs, preserving interactive priority. Do not infer a target that omits Files/Tables/Items from that older prose. Confirm any changed automatic-preparation policy before desktop integration.
4. **Audio.** Java uses a J-Ogg child JVM, which must not be imported or used here. Choose an original bounded C# decoder or an explicitly approved exception for a vetted .NET/native codec implementation, preserving process isolation and licensing. Any app-owned worker/orchestration is C#; no Java runtime or Java implementation is permitted.

Record accepted choices by adding decisions, superseding older rows where needed. This plan does not change existing policy.

## Delivery sequence

Each numbered slice below should become a feature issue with acceptance criteria before coding. No issues, branches, commits or public comments were created during this review. Large rows can be split along the independently testable outcomes described here.

| Order | Deliverable and dependencies | Required acceptance evidence |
| --- | --- | --- |
| 1 | Pin Java return-port reference and establish a capability matrix. Inventory public APIs and Java regression fixtures against existing C# behavior, including unsupported profiles. | Authored synthetic vectors reproduce relevant behavior independently of the Java checkout. Every capability maps to a C# destination and acceptance case; no downloaded game fixtures. |
| 2 | Unified pack library and owner-bound original/generated access, after storage decision. Keep old openers intact. | Same bytes shared by multiple owners; ownership attached on cache hits; mixed generated/original records; writer exclusion, corrupt/newer-format refusal, crash recovery and cancellation. No unowned publication or second presence index. |
| 3 | Canonical ownership inventory, persisted build-load state and accounting, on slice 2. | Atomic producer-specific inventory generations; existing objects gain owners without rewrite; incomplete sharing stays provisional; logical payload totals separate from physical overhead and relational bytes; restart preserves load outcomes. |
| 4 | Explicit preservation/import service, on slices 2–3. Start only against disposable profile copies. | History, saved preparation intent, retained membership and settings survive retry/reopen; incomplete import never activates; source payloads/index remain intact; lazy reuse is read-only. An owner-authorized profile migration is a later action. |
| 5 | Headless composition and preparation lifecycle over new stores, on slices 2–4. Include existing allowed-source policy and persistence. | Open metadata, prepare Files/Tables, detach/reattach, cancel one consumer, continue another, close and drain, reopen offline. Inventory work never blocks interactive reads. CLI integration proves this without GUI. Separately port durable Download-all intent/pause/resume, with intent committed before transfers and shared interactive priority; use `BuildDownloadsTest` and `NativeDownloadIntegrationTest`. |
| 6 | Table preparation inventories, captured local rows and text interpretation, on slice 5. | Durable completed inventory and schema identities reopen correctly; disabled definitions or changed key snapshots cannot reuse incompatible caches; bounded capture performs no acquisition or repair; absent/protected/corrupt remain distinct. |
| 7 | Items catalog, attributes/stat calculations and remaining existing-concept deltas, on slice 6. | Indexed filtering/paging without per-row acquisition; exact build/locale/provider identity; item-stat golden vectors and malformed/missing joins; armor/material provenance; cancellation, stale-source retry and handle draining. Audit Creatures/Effects against Java regressions in separate slices. |
| 8 | Shared display-model service and neutral mesh/appearance composition, after assets delta audit. | Synthetic geometry/material outputs, dependency visibility, texture budgets, corrupt-input refusal and offline reuse. Static support remains explicit; no claim of complete animation. Decide separately whether CPU model/terrain rasterizers should ship as headless preview/export capabilities alongside Three.js; they are independent of JavaFX. |
| 9 | Talents, races, mounts, professions and dungeons as separate feature slices, after tables; model-dependent projections also need slice 8. | Per-service Java behavior vectors, supported build/profile limits, relationship/provenance checks, missing tables, bounded search and cancellation. Race-specific appearance support must match actual implemented profiles. |
| 10 | Approved provider foundations and source refresh, after source decision and slice 5. Deliver Questie, Polestar, EG Link and public keys separately. | Revision/hash/schema/license validation, failed-update retention, independent versioned snapshots, enable/disable cancellation, offline queries, shared refresh ownership and persisted policy. Public keys never enter logs; build keys win conflicts. |
| 11 | NPCs, quests, locations and map artwork, after provider services and tables/assets. | Explicit source precedence and flavor mapping; no invented exact-build compatibility; provider coordinates retain their coordinate system; retained-version queries, locale fallback and unknown mappings are honest. |
| 12 | World formats and world service, with renderer-neutral terrain preparation, after Files/assets. | Supported WDT/ADT structures and terrain dependencies; bounded tile working set, missing/corrupt tile outcomes, stale-build rejection, cancellation/offline reuse. Free-flight UI can wait. |
| 13 | Voice decoding service, after audio decision. | Input/output/time budgets; malformed/truncated streams; cancellation kills/drains the isolated worker; PCM metadata and synthetic vectors; no desktop playback requirement. |
| 14 | Implemented comparison scope, after table capture/items. | Files and Tables inventories, exact-compatible raw table rows and current Items comparison; local-only inputs, semantic fingerprints, bounded external work/temp cleanup, incomplete coverage and UNKNOWN outcomes. No acquisition, repair or implicit migration. |
| 15 | Host adapters and backend completion gate, followed by legacy retirement. | New services reachable through CLI and bounded versioned engine contracts. Existing desktop flows use the layers and preserve binary-asset authorization. Delete old projects only after all existing consumers and required behavior have replacements; new screens remain deferred. |

Slices 8–9, 10–11 and 12–14 can advance independently once their prerequisites hold. Do not run conflicting storage/schema work in parallel. Start with slices 1–3, then prove migration and headless preparation before expanding the concept surface.

## Limits to preserve

Java's completed comparison scope is narrower than its roadmap: inventory comparisons, compatible raw tables and the first Items increment. Additional concept adapters, general comparison persistence/resume and native text-issue certificates are separate work. See `docs/mac-handoff-2026-11-03.md` and `docs/comparison/` in Java.

The new game concepts are implemented services with authored tests, not universal coverage of every WoW era. Keep profile refusal, provider disagreement, unavailable translations, protected files and unsupported model bindings visible. Java measurements and historical live checks choose workloads; repeat relevant evidence in C# before making readiness or performance claims.

Do not consolidate provider relational databases into the shared library just to reduce file count. Java itself defers that phase. Their schemas and lifetimes need explicit design, and their bytes must not inflate per-build object totals. Do not add automatic ownership-based deletion while inventories or active-operation ownership remain incomplete.

### Targeted qualifications

Talents cover reviewed Classic major versions 1–4, not Retail/Mists. Professions currently target reviewed Forever layouts; unresolved yields and conditional outputs remain unresolved. World covers bounded WDT18 and split ADT18 terrain heights/holes, not WMO buildings, doodads, textures, water or collision. Races should split metadata, appearance and voice delivery. Blizzard-only NPC/quest adapters and map artwork can proceed independently of provider approval.

Audit `SessionHashes`, `ReadDays`, `PreparedFileCatalog` and `NameCoverage` as measured lower-layer improvements. Port session-bound verification reuse only with immutable-object/incarnation invalidation tests; performance caches must never weaken integrity.

## Verification and completion

For each slice, recreate meaningful regression cases in C# and add shared real/fake interface contracts. Run changed-layer tests plus affected consumers when an interface changes. Storage changes also require recovery/fault tests and cross-layer disk integration. Binary-format compatibility uses independently generated vectors with pinned provenance; any optional comparison executed in the sibling reference project stays outside this repository and must not become a required build/test dependency. All tests use isolated temporary data and `VIBEMINER_DATA_DIR` where an app/harness is launched.

Use the repo's documented environment and commands: `source tools/dev-env.sh` before npm/dotnet calls; `npm run build`, `npm run test:layers`, and `npm run contracts:check` when contracts change. Verify the layer selector covers any added projects before relying on it. Run focused native/integration checks for storage and lifecycle changes. Reserve the full `npm ci`, build, `npm test` and `npm run test:e2e` gate for milestones under the existing workflow. UI additions later need their own native acceptance.

Backend completion requires the capability matrix to have passing C# evidence for each accepted Java behavior, no hidden Java runtime/checkout dependency, isolated restart/offline/recovery acceptance, measured workloads within agreed budgets, and all deferred provider/product decisions explicitly resolved or marked out of scope. A backend left uncalled behind old Host handlers does not meet completion. Full GUI parity is not required.
