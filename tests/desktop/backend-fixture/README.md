# Real C# authored retention fixture

This test-only console composes the real `ProtocolHost`, discovery parser/catalog, configuration/DL preparation, CAS, shared acquisition/resources/source plans, durable retention driver, the stored-coverage worker behind `download.summaries`, the ROOT scope worker, the file catalog worker behind `inventory.catalog` ([D071](../../../docs/DECISIONS.md)), and Settings with prepared data per build behind `settings.storage.builds` and `settings.storage.free` ([D085](../../../docs/DECISIONS.md)). Its injected `HttpMessageHandler` has no network-backed inner handler. Metadata, configuration, DL and both encoded payloads come from the repository's authored `InventoryFixture`; it contains no downloaded game bytes. Official-looking URLs are dictionary keys only.

The versions source is cloned with the visible version `1.2.3.12345+AUTHORED-RETENTION-FIXTURE`, then interpreted and committed through the real discovery service. Original fixture helpers and production files are unchanged. Tables use the real services; this small synthetic cohort is not a claim of supported game table data or Items/icons.

Build from the repository root:

```bash
node tests/desktop/backend-fixture/build.mjs
```

The DLL is `.local/backend-fixture-build/bin/BackendFixture/debug/BackendFixture.dll`. The builder uses the existing `tools/dotnet.mjs` toolchain selector. All project build/intermediate outputs and generated package locks go under `.local/backend-fixture-build`; existing production package locks/outputs are untouched. An optional absolute argument selects another isolated artifact directory. Do not pass global `RestorePackagesWithLockFile=false`: production projects have existing lock files; the builder uses the repository's explicit isolated lock-directory property instead.

Run with `dotnet <absolute DLL> --data-dir <absolute directory>`. The directory must be new/empty or have this fixture's own versioned marker. A nonempty unmarked directory is refused before opening/migrating SQLite. Existing authored catalog/CAS/history is preserved; only a directory with no saved discovery history is seeded. Never use the owner's application profile.

For Electron, set these existing test overrides:

```text
VIBEMINER_ENGINE_PATH=<absolute BackendFixture.dll>
VIBEMINER_DATA_DIR=<absolute isolated authored engine-data directory>
VIBEMINER_AUTHORED_FIXTURES=1
```

Also isolate Electron's `--user-data-dir`. The authored-fixtures flag displays the app's test-data disclosure; the original source version suffix remains visible independently. Launch the current desktop normally, select the single saved authored build and Open Build. Opening prepares configurations and the two-member DL through real production code; it never starts bulk retention.

After Download all, the first EKey in ledger order streams 12 authored bytes, then waits cancelably. Every attempt for that one object is gated. The second payload may complete and remain reusable. The following app-owned files under `<data-dir>/authored-controls/` are outside the protocol:

| File | Meaning |
| --- | --- |
| `scenario.json` | Exact authored build identity, gated EKey, object/byte totals and control paths. |
| `gated-state.json` | Atomic latest waiting/canceled/released gate observation, persistent attempt number, prefix/total, process ID and time. This is test coordination, not retention proof. |
| `gated-attempts.txt` | Persistent attempt count; Pause/Resume and unpaused restart start a new attempt. |
| `release` | Create this file to release the current and subsequent gated attempts. No payload bytes/offset are published before actual production verification. |
| `http.ndjson` / `http-counts.json` | Bounded actual injected-handler request log/counters across process restarts, never stdout. |
| `verification.json` | Written by the protocol smoke verifier only, with actual final snapshot/request counts. |

Pause must cancel the active real transport and settle through the actual driver cleanup. Resume keeps complete objects and restarts unfinished staging from zero. A normal process exit preserves unpaused intent; restarting the same authored directory reconstructs it. A paused intent remains paused. Release is intentionally explicit; do not create it until the test has observed the desired pause/resume/restart states. The real transport's 30-second idle limit remains effective, so pause/release within that interval for the normal path; a longer hold exercises the real timeout outcome.

The console accepts protocol frames only on stdin and writes only actual `ProtocolHost` frames on stdout. Diagnostics use stderr. No mock retention snapshots, stage percentages, database rows or terminal outcomes are injected.

Reproduce the protocol/CAS/SQLite check (new destination required, no deletion):

```bash
source tools/dev-env.sh
node tests/desktop/backend-fixture/verify.mjs \
  "$DOTNET_ROOT/dotnet" \
  "$PWD/.local/backend-fixture-build/bin/BackendFixture/debug/BackendFixture.dll" \
  "$PWD/.local/backend-fixture-verification-new"
```

The verifier checks protocol-only output, authored source disclosure, on-demand opening, real Pause, paused restart, Resume, unpaused restart, release, two committed encoded receipts/current presence observations and no redownload of completed objects/metadata. It retains all authored evidence. Its bounded status polling follows the production retention protocol; timeouts are deadlock guards, not performance thresholds. Electron interaction/capture tests are a separate qualification owned by the desktop tests.
