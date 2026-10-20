# VibeMiner product requirements

VibeMiner is a standalone desktop research application for discovering and inspecting Blizzard CDN data. It retains exact builds locally, reuses identical physical content across builds, and exposes the source and interpretation behind research results. World of Warcraft is the first detailed format target.

The owner accepted [size-first opening and demand-driven storage](SIZE-AND-DEMAND-ARCHITECTURE.md): know the build's scoped published download size without preparing a complete research catalog, then acquire specific research dependencies when needed. SQLite stores mutable facts; verified metadata and compact immutable lookup files avoid unnecessary global expansion.

## Main workflows

1. Choose an officially identified WoW product, inspect advertised and locally known builds, and select an exact build.
2. Open the build with one action. Reuse or acquire the exact configurations and download manifest, verify them and prepare its scoped size before Home. Game files remain on demand.
3. Inspect build readiness and storage. Retain content on demand or enable a durable Download all intent for a supported complete inventory.
4. Browse supported files and tables, search admitted data, follow validated relationships, and export attributed evidence.
5. Compare exact builds with compatible identities and schemas, keeping incomplete coverage explicit.
6. Preview supported textures, icons, audio, models, and portraits in stages. Higher-level research views and findings build on verified underlying evidence.

## Discovery and opening

Discovery lists the builds of every registered product in one grid, with a Product filter that narrows it locally ([D062](DECISIONS.md)). Refresh builds is the explicit metadata check for every product; selecting a build row does not download payloads. One build list offers All, Prepared, and Current (advertised at the last check). Local presence, latest successful advertisement, and checked remote availability are separate facts.

Group regional observations only when all identity-bearing references agree. Version text alone is insufficient. Preserve original regional receipts and show the separately selected acquisition route. Unknown friendly names remain exact product IDs with honest labels.

Open Build is the single primary action. It authorizes bounded metadata and size preparation, not Download all. Preserve the selected context on failure and retry. Corrupt or invalid required proofs block entry. An absent or authentic unsupported download manifest opens an overview with explicit unavailable size. Verified Home configuration is separate from ready file catalogs, content locations, research decoders and payload retention; those features cannot become empty successes when their own prerequisites are missing.

Show preparation for real configuration/metadata acquisition, verification and compact size-ledger work. Healthy saved preparation reuses valid artifacts after local revalidation. Back detaches the view; it does not silently cancel the operation. ROOT/IN/DS catalog materialization and interrupted-catalog cleanup do not gate Home. Research-specific lookup/decoding remains on demand. Exact lifecycle details are in [async work](ASYNC-AND-JOBS.md).

## Identity and evidence

Keep game family, edition, raw product/channel, exact build/configuration references, region observations, acquisition route, research locale, and admitted content scope distinct. A display build number, region, or numeric record ID is not a universal identity.

Store original source URL, observation time, source digest, protocol identity, actual verification result, and decoder/schema revisions. Community filenames, definitions, or annotations remain attributed provider metadata. No silent fallback to another build, locale, schema, or source is allowed.

## Readiness and activity

| Status | Meaning |
| --- | --- |
| Not ready | Required metadata is missing, corrupt, incomplete, or unsupported |
| Partial | Required metadata is verified; payload coverage or inventory work is incomplete |
| Fully downloaded | The complete admitted inventory and dependency closure is known and all required encoded content is currently verified retained |

Work readiness is separate: complete metadata alone need not mean useful local indexes are ready. Downloading, waiting, stopping, canceled, and needs attention are operation activity, not extra coverage states. Fully downloaded does not establish installability, decryption, universal decoding, or complete server behavior.

Production `home-download-v1` establishes Partial with verified configurations and a supported DL measurement, or an explicit unavailable-size explanation. Its total deduplicates full DL EKeys across published variants. It does not establish ROOT/IN/DS union completeness, decoded/installed size, selected locale/platform footprint or upstream availability. Verified-local and remaining bytes describe a dated assessment; the implementation credits currently reverified retained metadata and selected-content objects, including replay-verified encoded partial BLTE sources. Download all retains this published encoded scope in the background; [download activity](design/download-activity/README.md) describes how its policy and status are shown. Legacy full-catalog admission keeps its stronger scope independently.

Percentages require matching known numerators and denominators. Unknown is not zero. Transfer completion, verified content coverage, and physical storage are different measurements. Presentation rules are in [UI guidelines](UI-GUIDELINES.md).

## Background downloads and exploration

Large build downloads run while the user browses and investigates. If the current screen needs missing data, its request and required dependencies receive priority automatically through the same downloader. The user does not need to click Load, cancel bulk work or change download modes to inspect a table, item, file, or supported model. Show an in-context explanation and stage/byte/row progress, including a percentage whenever a trustworthy denominator exists; unknown work never receives a fabricated percentage. Navigation remains usable. Stop and Retry are purposeful recovery actions, not prerequisites for normal navigation.

Identical work is shared and promoted rather than downloaded twice. Keep per-screen activity distinct from overall build retention. Navigation and cancellation release the relevant consumer without stopping independent requests. Background work uses remaining resources and continues as foreground pressure subsides. See [priority acceptance scenarios](ASYNC-AND-JOBS.md#priority-acceptance-scenarios).

## WoW Items

Provide a dedicated Items browser for the selected exact build and research locale, with names, source-derived icons, quality/classification, supported requirements/properties/stats and effects. This is a semantic research screen with links to underlying Tables, not a renamed raw Item grid. Decode as many applicable client-side attributes as the actual layouts support and expose source-backed raw inputs where a final gameplay quantity is not yet justified.

Original ItemSparse full-field decoding and BLP icons belong in the first useful Items vertical. Show available names/descriptions, base item level, equip slot, class/subclass, requirements, prices/stack/binding, socket and stat/effect information where decoded and validated. Bonus lists, scaling, effective level and calculated armor/weapon/stat values retain explicit variant context and versioned formula/input evidence. Missing, unknown, protected, unsupported and known zero remain different. CDN client data does not establish complete live-server behavior.

Navigation acquires declared feature prerequisites automatically. Missing visible icons are resolved through the same prioritized downloader in bounded batches; reuse exact verified assets and keep an item usable if its icon fails. C# prepares images; the renderer never downloads or decodes game formats. See the [items](../contracts/items-v1.md) and [item icons](../contracts/item-icons-v1.md) contracts.

The Creatures browser shows client names, literal-name and exact-ID search, and display declarations with source-record navigation. Model rendering and portraits are not available in the app.

## Storage and offline use

Retain payloads once in an app-owned content-addressed store. Builds and policies reference shared bytes. Keep configured retention, archive membership, cache policy, and current physical evidence separate. Offline reopening uses retained inputs without gratuitous network requests; missing content remains missing with an actionable explanation.

## Boundaries

- No installed game, Battle.net client, external database, or service is required by end users.
- No game-install discovery, Battle.net directory reads, or game-generated cache import.
- Official CDN data is the acquisition source. External APIs and community data acquisition need separate provider decisions.
- Published pointers are not a complete historical archive. A preload advertisement is not proof that payloads are available or readable.
- Discovery of a product does not promise parsing compatibility. Format support is exact and tested.
- Client data does not prove release timing, obtainability, spawn locations, server behavior, or final gameplay tuning.
- Existing Java packages and passing tests do not qualify this implementation.
