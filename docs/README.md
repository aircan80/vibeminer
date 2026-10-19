# VibeMiner documentation

Permanent docs for the Electron + C# VibeMiner. Work in progress lives in GitHub Issues, not here.

## Product and decisions

| Document | Purpose |
| --- | --- |
| [Product](PRODUCT.md) | User outcomes, boundaries and scope |
| [Decisions](DECISIONS.md) | Confirmed choices and implementation defaults |
| [Java migration](MIGRATION.md) | Java source baseline and port map; reference data in [migration/](migration/) |
| [Design guide](design/README.md) | Selected mockup and flow references |
| [UI guidelines](UI-GUIDELINES.md) | Rules every screen follows |

## Engineering

| Document | Purpose |
| --- | --- |
| [Architecture](ARCHITECTURE.md) | Process ownership, dependency boundaries, Electron boundary |
| [Engine contracts](../contracts/README.md) | Versioned engine protocol, schemas and message rules |
| [Async work and jobs](ASYNC-AND-JOBS.md) | Resource budgets, lock order, cancellation, progress, priority scenarios |
| [Storage](STORAGE.md) | SQLite catalog, object store, provenance, readiness and durability |
| [Size-first and on-demand storage](SIZE-AND-DEMAND-ARCHITECTURE.md) | Why opening is size-first and research data is fetched on demand |
| [Formats and assets](FORMATS-AND-ASSETS.md) | Original parsers, admission, model viewing and portraits |
| [Testing](TESTING.md) | Unit, integration, UI, performance and package evidence; async test pitfalls |
| [Building](../BUILDING.md) | Install, build, run, test and package commands |

## Formats and WoW data

| Document | Purpose |
| --- | --- |
| [TACT](formats/tact.md), [ROOT](formats/root.md), [WDC5](formats/wdc5.md), [BLP](formats/blp.md), [M2/SKIN](formats/m2-skin.md) | Format notes for our own readers |
| [Artifact formats](ARTIFACT-FORMATS.md) | Our derived binary artifacts |
| [WoW data reference](WOW-DATA-REFERENCE.md) | Tables, joins and interpretation rules |
| [research/](research/) | Investigations of specific data questions |

## Licensing and security

| Document | Purpose |
| --- | --- |
| [Licensing](LICENSING.md) | Project license and separately licensed materials |
| [third-party/](third-party/) | Notices for bundled dependencies |
| [Security policy](../SECURITY.md) | How to report vulnerabilities |

Agent role definitions live in `.agents/rules/`; see [AGENTS.md](../AGENTS.md).

## Status words

**Decided** means the owner selected it or it carries forward explicitly from the Java app. **Working default** is a starting point that can change through normal technical discussion. **Implemented** requires source and relevant passing checks in this repository. A mockup or Java test result does not establish implementation here.
