# Developing VibeMiner

VibeMiner now has a runnable Electron/React interface and a C# discovery engine backed by SQLite. Discovery reads official metadata. Open Build verifies the exact build/CDN configuration files before showing the Build overview; larger metadata and game-content acquisition remain later work.

## Development tools

- .NET SDK **10.0.402**, pinned in `global.json`; target runtime .NET 10 / C# 14.
- Node.js **24.22.0**, npm **11.19.1**, pinned in `.nvmrc` and `package.json`.
- Electron **44.9.0**, React **19.3.1**, TypeScript **7.0.2**, Vite **8.3.4**; exact dependencies are locked in `package-lock.json` and the NuGet project lockfiles.
- Git and native desktop libraries required by Electron on the development platform.

If you have local SDKs under the ignored `.local/toolchains/`, load them with:

```bash
source tools/dev-env.sh
```

That helper also puts temporary files and Electron's download cache under `.local/`, avoiding this machine's restricted `/tmp` quota. It works from any directory and is safe to source twice. In a git worktree without its own `.local/toolchains` (such as `.claude/worktrees/*`), it uses the main checkout's toolchains and Electron download cache, found through `git rev-parse --git-common-dir`; `TMPDIR` stays in the worktree's own `.local/tmp`. A `TMPDIR` or `electron_config_cache` you already set is kept. On another checkout, install the declared SDKs normally; local toolchains are optional and are not committed. Electron includes its own runtime for end users.

## Install, build and run

From the repository root:

```bash
npm ci
node tools/dotnet.mjs restore VibeMiner.slnx --locked-mode
npm run build
npm start
```

Click **Refresh builds** to explicitly check official sources for every product. Startup reads saved local observations for every product without an automatic remote scan; the **Product** filter narrows the list. Discovery observations do not mean game content is downloaded or prepared. Select a build and its saved **Download source**, then choose **Open Build**. This downloads only the two small configuration files when needed and verifies them before Home entry. Previously admitted configurations are revalidated locally; missing/corrupt retained bytes fail explicitly.

Electron normally stores its catalog beneath its platform user-data directory in `engine-v1/`. To isolate a development catalog, set `VIBEMINER_DATA_DIR` to an absolute app-owned directory. `VIBEMINER_DOTNET` can override the SDK executable; `VIBEMINER_ENGINE_PATH` can override the development host. Never point this port at Java application storage or a game installation.

## Verification

```bash
npm test
npm run test:e2e
```

Build before running these from a fresh checkout. `npm test` checks generated contracts, the xUnit backend suite, Vitest desktop behavior, and actual C# process/schema integration. Tests use authored fixtures, temporary catalogs and controlled loopback HTTP, with no live Blizzard dependency. The Electron suite launches the actual application with isolated profiles and writes captures/reports to ignored `artifacts/electron-tests/`. It currently uses Chromium's Linux headless platform; other desktop environments need their own qualification.

Useful individual checks:

```bash
npm run test:backend
npm run test:desktop
npm run test:contracts
npm run contracts:check
```

Edit `contracts/discovery.schema.json` as the canonical wire schema and run `npm run contracts:generate` when intentionally changing its TypeScript projection. Backend/frontend changes must agree with the versioned contract.

## Local native package

```bash
npm run package:desktop
```

The package helper publishes a self-contained C# engine for the host (`linux-x64`, `win-x64`, `osx-arm64` or `osx-x64`) and bundles it with Electron. There is no cross-compilation: each target is packaged on its own OS. Executables:

| Host | Executable |
| --- | --- |
| Linux x64 | `artifacts/desktop/VibeMiner-linux-x64/vibeminer` |
| Windows x64 | `artifacts/desktop/VibeMiner-win32-x64/vibeminer.exe` |
| macOS | `artifacts/desktop/VibeMiner-darwin-<arm64\|x64>/VibeMiner.app/Contents/MacOS/vibeminer` |

Generated packages, staging files and downloaded dependencies stay ignored. Licenses, notices and `SOURCE.txt` go to `resources/legal/` (`Contents/Resources/legal/` on macOS): LICENSE, NOTICE, WoWDBDefs and Three.js notices, Electron's licenses, the .NET SDK's `LICENSE.txt`/`ThirdPartyNotices.txt` for the bundled runtime, and inventories plus license texts for bundled NuGet and npm runtime packages (NuGet texts come from `docs/third-party/licenses/`). `SOURCE.txt` records the base revision and whether packaging included uncommitted changes. On macOS the engine and app are ad-hoc signed so Apple silicon will run them; nothing is Developer ID signed or notarized.

```bash
npm run package:installers
npm run smoke:installers
```

`package:installers` wraps the existing package with electron-builder in prepackaged mode and writes the installer and portable archive to `artifacts/release/`: `.exe` (NSIS, per-user) and `.zip` on Windows, `.dmg` and `.zip` on macOS, AppImage, `.deb` and `.tar.gz` on Linux. Each target is built from its own copy of the package, so `artifacts/desktop` stays as `smoke:package` checked it and only the `.deb` carries the `resources/apparmor-profile` its target adds. electron-builder downloads its NSIS/AppImage/fpm/dmg helpers into its cache on first use. On Fedora its bundled fpm needs `libcrypt.so.1` (`libxcrypt-compat`) to build the `.deb`. `smoke:installers` runs the package smoke check against the extracted archive and the installer: AppImage (extract-and-run) and `.deb` payload on Linux, the mounted `.dmg` on macOS, and a silent per-user NSIS install into a temporary directory on Windows (uninstalled afterwards).

Run `npm run smoke:package` after packaging for a fresh launch and proxy-blocked local reopen check. `npm run smoke:package -- --live` additionally runs one explicit official refresh of every product, opens a `wow` build and checks Home with its Metadata and Source details, then reopens with HTTP proxies blocked and checks the saved builds, the exact selection and Home again; this live check is separate from normal offline tests. Its UI steps live in `tools/smoke-steps.mjs`, and `tests/desktop/package-smoke.spec.ts` runs them against the unpackaged app in `npm run test:e2e` (real engine for the fresh launch, authored engine for the refresh and open), so screen changes break the e2e suite rather than the release. Both use an isolated temporary catalog and omit the local SDK from the engine-launch environment. Package-specific NuGet locks stay in staging so native publication does not rewrite development lockfiles.

`node tools/capture-discovery-reference.mjs` renders the immutable guided mockup using Electron at the same reference sizes, excluding only its design concept/state controls. Pass `desktop` for the current desktop discovery reference, or `home` or `preparation` for the related immutable references. Compare those captures under `artifacts/design-reference/` with the actual application captures from `npm run test:e2e`.

`npm start` loads built files and has no hot reload. After changing frontend sources, run `npm run build:desktop`, close the application and start it again. After backend changes, also run `npm run build:backend`.

## CI

`.github/workflows/ci.yml` runs on every push to `main` and from **Actions > CI > Run workflow**, on ubuntu-latest only (D074). It's a clean-machine safety net, not the gate: QA's local `npm ci`, `npm run build`, `npm test` and `npm run test:e2e` on the exact sha in a clean worktree is. A failing run on `main` becomes a `bug` issue right away. Windows and macOS are parked and get tested again when they're un-parked; release still builds every platform. The job checks the pinned toolchain, then runs `npm ci`, the locked NuGet restore, `npm run build`, then each part of `npm test` (`contracts:check`, `test:backend`, `test:desktop`, `test:contracts`) as its own step, so a failing suite doesn't hide the others, and `npm run test:e2e` (headless Electron, no xvfb). It uploads `test-logs-<os>` with the step logs and the Playwright report. It has `contents: read`, uses no secrets and pins the same action SHAs as release.yml; `tests/contracts/workflows.test.mjs` checks that.

## Release

`.github/workflows/release.yml` runs only from **Actions > Release > Run workflow** with a version such as `0.2.0`. It never runs on push or pull request.

1. Commit the version bump in `package.json` and `apps/desktop/package.json` (plus `package-lock.json`) first. The workflow refuses a version that differs from `package.json` or a `v<version>` tag that already exists.
2. Windows x64 (`windows-2025`), macOS arm64 (`macos-15`), macOS x64 (`macos-15-intel`) and Linux x64 (`ubuntu-24.04`) each run `npm ci`, locked NuGet restore, `npm run build` and `npm test`, then `node tools/package-desktop.mjs --release`, `smoke:package`, `package:installers` and `smoke:installers`. Release mode refuses a dirty checkout or a missing notice, and `SOURCE.txt` names the public source URL for the exact commit.
3. The release job collects the nine installers and archives, adds `LICENSE`, `NOTICE` and `VibeMiner-<version>-source.tar.gz` (`git archive` of the commit), writes `SHA256SUMS` and creates a **draft** release `v<version>` targeting that commit. The tag only exists once the owner publishes the draft by hand.

Only the release job has `contents: write`; everything else is read-only. Actions are pinned to full commit SHAs. Builds are unsigned and the release notes say so.

Adding signing later doesn't change the workflow's shape. macOS: replace the ad-hoc `adHocSign` calls in `tools/package-desktop.mjs` with Developer ID signing plus hardened runtime and notarization (`@electron/packager`'s `osxSign`/`osxNotarize`), and drop `identity: null` in `tools/make-installers.mjs` so the `.dmg` is signed. Windows: sign the app with `windowsSign` and the installer with electron-builder's `CSC_LINK`/`CSC_KEY_PASSWORD`. Secrets go into the `env` of the "Package app" and "Make installers" steps; the workflow comments mark the spots. `CSC_IDENTITY_AUTO_DISCOVERY=false` stays until then so no keychain identity gets picked up by accident.

## Design reference

Open [guided discovery](docs/design/build-discovery/index.html?concept=guided&state=ready&product=wow) to inspect the preserved original mockup. It uses synthetic data and performs no real acquisition. The running app removes prototype controls and shows actual engine results. See [docs/TESTING.md](docs/TESTING.md) for testing and remaining qualification.
