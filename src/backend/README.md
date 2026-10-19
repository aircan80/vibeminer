# C# discovery engine

The discovery slice runs on .NET 10 (SDK policy in root `global.json`). Build and test from the repository root:

```sh
dotnet build VibeMiner.slnx
dotnet test VibeMiner.slnx
dotnet src/backend/VibeMiner.Host/bin/Debug/net10.0/VibeMiner.Host.dll --data-dir /absolute/app-owned/development-directory
```

The host exchanges protocol-v1 NDJSON on stdin/stdout; diagnostics use stderr. See [the canonical contract](../../contracts/discovery-v1.md). Electron supplies its app-owned data path. With no override, standalone development uses the OS local-application-data directory under `VibeMiner/engine-v1`. It never opens the Java archive or a game installation.

`products.list` and `discovery.read` perform no network access. Explicit `discovery.refresh` fetches one fixed official `/versions` and `/cdns` pair, retains all valid bounded versions-advertised regions, and reports unavailable/ambiguous routes without losing advertisements. This slice acquires text metadata only. Discovery observations never establish local preparation or payload availability.

`workspace.open` is the owner-approved `home-configurations-v1` path. It takes the exact product/build identity and an explicit saved source region, verifies that original captured observation and its supported `text-metadata-v1` parser revision, and downloads only the small build/CDN config pair from its advertised supported official HTTPS route. Raw config MD5, local SHA-256, complete strict assignment grammar and bounded lengths are checked before immutable CAS publication. Only after both exact roles are durable does the catalog commit the scoped admission and successful-Open acknowledgement, then return Home. The snapshot explicitly reports configuration readiness, inventory not prepared, unknown retention, null locale/platform and Local false. EN/ROOT/IN/DL/DS and game files are not requested.

Healthy admitted reopening rechecks local CAS bytes/config grammar with zero HTTP and preserves original source URL/retrieval time while issuing a fresh verification time. Missing/corrupt admitted bytes fail explicitly; there is no implicit repair. Failed initial attempts can reuse already verified role objects on explicit retry. One application-owned producer survives Back; same-context consumers share it, other contexts receive OPEN_BUSY. Consumer cancellation and progress drain wait for actual retirement as appropriate; durable success wins cancellation races.

The projects enforce the domain/application/adapter split. The original text parser is pure. Infrastructure owns official HTTP and a single bounded SQLite worker. SQLite migrations (current version 3) store deduplicated verified raw documents, immutable per-attempt receipts and rows, full identity keys, historical groups, current per-region advertisements, regional baselines and product visits. Version 3 adds immutable configuration-object receipts, scoped workspace admissions/configuration proofs and atomic Open acknowledgements; migration from versions 1/2 is tested. A persisted admission generation fences older refreshes and binds cursors. First regional checks are neutral; later new identities stay New until a future real Open acknowledgement is implemented.

Resource limits: 32 host requests, 1 MiB frames/documents, 4 HTTP requests, 2 parser workers, 64 queued database operations and 1 database worker. Metadata limits are 64 columns, 10,000 rows, 16,384 characters per line, 64 regions and 64 advertised rows per region. HTTP timeouts are 20 seconds after network admission with a 10-second connect timeout; SQLite busy waits are at most 5 seconds. Progress is one bounded indeterminate stage per refresh. Cancellation waits for producer retirement; successful durable commits win cancellation races.

Pages contain at most 100 groups (default 50), with a 750,000-byte build-projection budget below the protocol frame ceiling. Source details project the newest exact observation per historical region; every earlier duplicate/event receipt remains in SQLite. Page cursors bind product, filter and generation, and become invalid when another refresh is admitted. Queries issue no HTTP and never acknowledge Open; only the successful configuration admission does.

Configuration opening has a two-minute operation ceiling, 20-second per-request timeout and at most two 1 MiB downloads; one bounded config parser worker and lazy exclusively owned CAS use asynchronous file I/O. It reuses the same catalog worker and HTTP handler.

The deterministic backend tests use authored synthetic metadata, reserved `.invalid` route hosts, temporary SQLite directories, controlled streaming HTTP and process launch. They do not call official services. Live checks run separately; see [docs/TESTING.md](../../docs/TESTING.md).
