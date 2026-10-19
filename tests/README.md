# VibeMiner tests

Meaningful unit tests are required for new testable behavior and testable defects. Integration, UI, architecture, performance, and native-package checks complement them. See [the verification strategy](../docs/TESTING.md).

| Directory | Planned content |
| --- | --- |
| `backend/unit/` | Domain, application policy, pure parsers, jobs, protocol translation |
| `backend/integration/` | Controlled HTTP, temporary SQLite/CAS, engine process and recovery |
| `backend/architecture/` | Dependency direction and forbidden game parsing/download dependencies |
| `desktop/` | Relevant component, real Electron interaction, focus and screenshot checks |
| `fixtures/` | Authored synthetic binary/message/data fixtures and provenance |
| `performance/` | Explicit representative workloads and measurement tools |

No test manifests or test suites exist yet. Select frameworks and document real commands during runnable bootstrap. Do not count copied mockup interactions or Java test results as passing tests here.

Fixtures are deterministic and contain no downloaded game payloads, game-generated caches, user archives, secrets, or private paths. Include a small manifest or generator identifying author/source, format variant, expected outcome, and licensing when a fixture is added. Normal tests require no external network; live CDN verification is separate and bounded.
