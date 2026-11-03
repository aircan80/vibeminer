# Isolated metadata-load registry v1

Issue #223 implements B02 in `VibeMiner.Builds`. `BuildRegistry.OpenAsync` takes an explicit disposable or app-owned directory; no current profile or composition opens it automatically. `IBuildRegistry` records caller-reported metadata-load outcomes. It does not acquire bytes, open a build, prepare research data or establish download coverage.

## Identity and selection

Entries use the established BuildIdentity v2 canonical identity and SHA-256 Id. Every input and persisted identity is recomputed from its product and fields; inconsistent Id/canonical/fields tuples are refused. Region and CDNConfig remain observations, outside canonical identity. BuildRef product and BuildConfig must match that identity.

Observe keeps the latest selection separate from load state. Begin captures an immutable BuildRef and exact existing locale name (`BuildLocales.Bit` validation); observation changes cannot replace that attempt's selection. Caller-owned identity dictionaries are copied. The registry retains entries absent from current discovery.

## Load lifecycle

The states are Discovered, Loading, Loaded, Failed and Interrupted. Begin advances a durable generation and returns an opaque token bound to the registry, build, generation and attempt nonce. Only the current Loading attempt may finish, once. Old, foreign and duplicate tokens cannot overwrite the active attempt or a terminal outcome. Loaded means caller-reported successful metadata loading only.

A writer acquires exclusion before recovery. Reopen durably changes abandoned Loading entries to Interrupted before admitting operations; a second writer cannot interrupt the first. Retry starts a newer generation. Terminal outcomes and captured selections survive restart.

## Persistence and refusal

The isolated directory contains `registry.sqlite3`, `registry-v1` and `writer.lock`. SQLite application_id is `0x564d6272` (VMbr), user_version is 1. The marker binds the registry UUID. This authoritative intent is separate from discovery's rebuildable CatalogStore and in-memory OpenCache.

The `registry` singleton stores the UUID, committed entry count and XOR of entry digests. The `builds` WITHOUT ROWID table keys each bounded serialized entry by canonical Id and stores its SHA-256. Open streams and validates every row, recomputes identity and reconciles count/digest, detecting removed or altered entries. Normal saves update the entry and aggregate evidence in one transaction.

First creation commits the schema and UUID before writing and flushing the marker. Every validated open then syncs the supplied directory and its ancestors before WAL setup, recovery or admission, including retries after a failed initialization barrier. This persists the marker name and any newly created directory chain; the platform helper follows the object store’s directory-sync conventions. A crash leaving either authoritative file absent is refused as incomplete initialization; it is never silently promoted to an empty replacement. Existing successful mutations use SQLite WAL with synchronous FULL.

Existing metadata is probed read-only and validated before journal/schema/recovery mutations. Missing authoritative files, malformed identity/state/token data, foreign formats and newer versions are refused rather than rebuilt. Validation streams rows and checks scalar storage classes and encoded text sizes before allocating managed values. Refusal is not permission to reset the directory.

## Resources and outcomes

One worker admits at most 64 queued operations; a full queue fails Busy without retaining waiting admissions. List requests accept 1–1024 entries and use registry-bound keyset continuations. Serialized entries are capped at 512 KiB and each returned page at 8 MiB, so a page can end before its requested entry count. Identity fields are capped at 64, field names at 128 UTF-8 bytes, values at 2048 bytes and canonical identity at 32768 bytes. Locale is capped at 32 bytes; diagnostic code and message are capped at 128 and 4096 bytes.

Canceled uncommitted mutations change nothing. At the commit fence cancellation is checked; once publication starts, a successful durable commit returns its actual outcome. Shutdown rejects new calls and drains accepted operations before releasing the writer. Errors use `BUILD_REGISTRY_VALIDATION`, `BUSY`, `CLOSED`, `STORAGE`, `UNREADABLE`, `NEWER`, `STALE` and `MISSING` with the same `BUILD_REGISTRY_` prefix. The registry has no progress callbacks, automatic retry or implicit load jobs.
