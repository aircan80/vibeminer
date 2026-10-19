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

That helper also puts temporary files and Electron's download cache under `.local/`, avoiding this machine's restricted `/tmp` quota. On another checkout, install the declared SDKs normally; local toolchains are optional and are not committed. Electron includes its own runtime for end users.

## Install, build and run

From the repository root:

```bash
npm ci
node tools/dotnet.mjs restore VibeMiner.slnx --locked-mode
npm run build
npm start
```

Select a product or click **Refresh builds** to explicitly check official sources. Startup restores the chosen product and reads saved local observations without an automatic remote scan. Discovery observations do not mean game content is downloaded or prepared. Select a build and its saved **Download source**, then choose **Open Build**. This downloads only the two small configuration files when needed and verifies them before Home entry. Previously admitted configurations are revalidated locally; missing/corrupt retained bytes fail explicitly.

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

The package helper publishes a self-contained C# engine and bundles it with Electron. On Linux x64 the executable is `artifacts/desktop/VibeMiner-linux-x64/vibeminer`. Generated packages, staging files and downloaded dependencies stay ignored. The helper targets the current supported host architecture; Windows x64 and macOS x64/arm64 are candidate targets, not tested release claims.

These are local development packages. Signing, notarization, complete dependency-notice/source-distribution qualification and native checks on each advertised OS remain release work. `SOURCE.txt` records the base revision and whether packaging included uncommitted changes. No remote, publishing or release workflow is configured.

Run `npm run smoke:package` after packaging for a fresh launch and proxy-blocked local reopen check. `npm run smoke:package -- --live` additionally performs one explicit official `wow` discovery refresh, verifies the selected build/CDN configurations and Home, then verifies saved-history/selection and Home reopening with HTTP proxies blocked; this live check is separate from normal offline tests. Both use an isolated temporary catalog and omit the local SDK from the engine-launch environment. Package-specific NuGet locks stay in staging so native publication does not rewrite development lockfiles.

`node tools/capture-discovery-reference.mjs` renders the immutable guided mockup using Electron at the same reference sizes, excluding only its design concept/state controls. Pass `desktop` for the current desktop discovery reference, or `home` or `preparation` for the related immutable references. Compare those captures under `artifacts/design-reference/` with the actual application captures from `npm run test:e2e`.

`npm start` loads built files and has no hot reload. After changing frontend sources, run `npm run build:desktop`, close the application and start it again. After backend changes, also run `npm run build:backend`.

## Design reference

Open [guided discovery](docs/design/build-discovery/index.html?concept=guided&state=ready&product=wow) to inspect the preserved original mockup. It uses synthetic data and performs no real acquisition. The running app removes prototype controls and shows actual engine results. See [docs/TESTING.md](docs/TESTING.md) for testing and remaining qualification.
