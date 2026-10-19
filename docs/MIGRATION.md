# Continuing the Java application

This is a language and presentation port of VibeMiner, with unfinished product work continuing afterward. Keep the Java source available as a behavioral reference while establishing independent C# and Electron tests.

## Source checkpoint

The inspected Java checkout is `vibeminer_java`, commit `8f353d046d961108f7acc86bfe1381004467afd5`, with no tracked modifications reported at foundation import. The selected browser assets are copied byte for byte and recorded in [the reference manifest](migration/java-reference.json). This repository must remain buildable without that sibling checkout.

Some older Java specifications contain historical status paragraphs superseded by later code and plans. Resolve contradictions using actual source, tests, and scoped acceptance evidence; do not inherit every statement that something is implemented, current, or complete.

## Behavioral mapping

| Java source area | Continuation destination | Preserve |
| --- | --- | --- |
| `domain` | `src/backend/VibeMiner.Domain/` | Exact identity, provenance, readiness/capability distinctions |
| `application` | `src/backend/VibeMiner.Application/` | Use cases, policy, jobs, consumer cancellation, bounded queries |
| `infrastructure` | `src/backend/VibeMiner.Infrastructure/` | Official discovery, own transport, SQLite/CAS, staged indexes and source evidence |
| `protocol-tact` | `src/backend/VibeMiner.Protocols.Tact/` | Original bounded configuration/BLTE/EN/IN/DL/DS/locator readers and fixtures |
| `protocol-db2`, `format-wow` | `src/backend/VibeMiner.Formats.Wow/` | Original DB2/WoW interpretation and exact format limits; keep internal boundaries |
| `desktop` | `apps/desktop/` | Approved interactions, focus, context continuity, honest states |
| `bootstrap` | `src/backend/VibeMiner.Host/` and Electron main | Composition and owned lifetimes; replace JavaFX/Gradle/jpackage integration |
| Java/Python tests and review tools | `tests/`, `tools/` | Behavioral fixtures, failure cases, workload shapes, and acceptance scenarios |

Do not generate one C# class per Java class. Preserve real responsibility boundaries and simplify adapters where the new runtime makes that appropriate. Domain/application code must remain testable without the desktop.

## Useful existing behavior to port

The source contains product/build discovery and durable history, exact metadata opening, verified shared storage/catalog publication, background retention work, bounded jobs/cancellation, protocol parsers, and limited Item/name research. Test each claimed slice against the source and port its meaningful fixture cases. Decoder breadth, whole-build capacity/accounting, broader research, comparisons, and models remain scoped delivery work.

Java release counts, native screenshots, live source observations, and million-row timings establish only their recorded Java scope. Repeat the required unit/integration/desktop/native checks here; use old evidence to choose workloads, not to claim parity.

## Important source documents

Paths below are origin identifiers inside the pinned Java repository, not runtime dependencies or executable instructions for this port.

| Origin | What to carry forward |
| --- | --- |
| `README.md`, `docs/ARCHITECTURE.md` | Product, boundaries, identities and independent use cases |
| `docs/BUILD-BROWSER-IMPLEMENTATION.md` | Product-first discovery, exact Open, C overview and retention semantics |
| `docs/BUILD-OPENING.md`, `docs/BUILD-METADATA-DECLARATIONS.md` | Required metadata closure and exact declaration policy |
| `docs/BUILD-PREPARATION.md` | Conditional preparation, Back/reattach, work readiness and job ownership |
| `docs/STORAGE.md`, `docs/PUBLISHER-OWNERSHIP.md` | Shared immutable bytes, authority, recovery and leases |
| `docs/BUILD-INVENTORY-FORMATS.md`, `docs/FORMATS.md` | Bounded original parsing, complete inventory, interpretation limits |
| `docs/SQLITE-INVENTORY-PERFORMANCE.md` | Measured batching/index effects and authored benchmark shapes |
| `docs/TABLES-STORIES.md`, `docs/TABLE-RELATION-LABELS.md` | Table Explorer, source-target column proofs, optional labels and navigation |
| `docs/RESEARCH-AREAS.md` | Future readable research backed by exact evidence |
| `docs/TESTING.md`, `BUILDING.md` | Deterministic and native verification responsibilities; replace runtime-specific commands |

Historical Java issue numbers identify source discussions. They are not issues, approvals or delivery status in this repository.

## Data and fixture transfer

Transfer original authored fixture generators and expected outputs as needed, preserving provenance. Do not copy downloaded payloads, user databases, private logs, Java build output, Gradle caches, native packages, credentials, or unused schema bundles.

Existing Java catalog/proof formats need explicit migration design and backups before any compatibility claim. The new engine must not open an old archive for writing by accident. Original software remains under the inherited project license; third-party materials keep their own terms.
