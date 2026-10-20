// Explicit native smoke check: fresh launch of the package and a proxy-blocked reopen.
// --live also refreshes every product from the official sources, opens one build and reopens it offline.
// The UI steps live in tools/smoke-steps.mjs; tests/desktop/package-smoke.spec.ts runs them against the unpackaged app.
import { expect } from '@playwright/test';
import { mkdtemp, rm, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expectEmptyDiscovery, expectHome, expectHomeDetails, expectStoredDiscovery, launchApp, openBuild, readCatalog, refreshBuilds, selectBuild } from './smoke-steps.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const live = process.argv.includes('--live');
const suppliedPath = process.argv.slice(2).find(value => value !== '--live');
const defaultPackage = {
  linux: 'VibeMiner-linux-x64/vibeminer',
  win32: 'VibeMiner-win32-x64/vibeminer.exe',
  darwin: `VibeMiner-darwin-${process.arch}/VibeMiner.app/Contents/MacOS/vibeminer`,
}[process.platform];
const executablePath = path.resolve(suppliedPath || path.join(root, 'artifacts/desktop', defaultPackage));
const directory = await mkdtemp(path.join(tmpdir(), 'vibeminer package '));
const evidence = path.join(root, 'artifacts/package-smoke');
await mkdir(evidence, { recursive: true });
const environment = { ...process.env, VIBEMINER_DATA_DIR: path.join(directory, 'catalog with spaces') };
for (const key of ['VIBEMINER_ENGINE_PATH', 'VIBEMINER_DOTNET', 'VIBEMINER_AUTHORED_FIXTURES', 'DOTNET_ROOT']) delete environment[key];
if (process.platform === 'linux') environment.PATH = '/usr/bin:/bin';
const launch = offline => launchApp({ executablePath, directory, env: environment, offline });
// Official CDN work is bounded by the engine; these are deadlock guards, not performance claims.
const refreshTimeout = 180_000, openTimeout = 900_000, offlineOpenTimeout = 120_000;
const record = { startedAt: new Date().toISOString(), executablePath, target: `${process.platform}-${process.arch}`, liveRequested: live };
let application;
try {
  let page;
  ({ application, page } = await launch(false));
  expect(await application.evaluate(({ app }) => app.isPackaged)).toBe(true);
  const icon = await application.evaluate(({ app, nativeImage }) => {
    const image = nativeImage.createFromPath([app.getAppPath(), 'dist', 'assets', 'vibeminer.png'].join(process.platform === 'win32' ? '\\' : '/'));
    return { empty: image.isEmpty(), size: image.getSize() };
  });
  expect(icon).toEqual({ empty: false, size: { width: 512, height: 512 } });
  await expectEmptyDiscovery(page);
  if (live) {
    await refreshBuilds(page, { timeout: refreshTimeout });
    record.opening = await selectBuild(page, { productId: 'wow' });
    await page.screenshot({ path: path.join(evidence, 'live-discovery.png'), fullPage: true });
    await openBuild(page, { timeout: openTimeout });
    record.workspace = await expectHome(page, record.opening);
    await expectHomeDetails(page, record.workspace);
    await page.screenshot({ path: path.join(evidence, 'live-home.png'), fullPage: true });
    record.catalog = await readCatalog(page);
    expect(record.catalog.builds.length).toBeGreaterThan(0);
  }
  await application.close(); application = undefined;
  ({ application, page } = await launch(true));
  if (live) {
    await expectStoredDiscovery(page, { catalog: record.catalog, selected: record.opening });
    await page.screenshot({ path: path.join(evidence, 'offline-reopen.png'), fullPage: true });
    await openBuild(page, { timeout: offlineOpenTimeout });
    const reopened = await expectHome(page, record.opening);
    // The same retained documents, verified again locally (verifiedAt moves on).
    const retained = receipts => receipts.map(({ verifiedAt, ...receipt }) => receipt);
    expect(retained(reopened.configurations)).toEqual(retained(record.workspace.configurations));
    await expectHomeDetails(page, reopened);
    await page.screenshot({ path: path.join(evidence, 'offline-home.png'), fullPage: true });
  } else await expectEmptyDiscovery(page);
  await application.close(); application = undefined;
  record.completedAt = new Date().toISOString(); record.outcome = 'passed';
  await writeFile(path.join(evidence, live ? 'live-result.json' : 'fresh-result.json'), JSON.stringify(record, null, 2) + '\n');
  process.stdout.write(`Packaged ${record.target} launch, bundled engine and ${live ? 'live discovery / opened build Home / saved-history' : 'fresh local'} offline reopen passed.\n`);
} catch (error) {
  record.completedAt = new Date().toISOString(); record.outcome = 'failed';
  record.error = error instanceof Error ? error.message : String(error);
  if (application) {
    const page = await application.firstWindow().catch(() => null);
    if (page) await page.screenshot({ path: path.join(evidence, 'failure.png'), fullPage: true }).catch(() => {});
  }
  await writeFile(path.join(evidence, live ? 'live-failure.json' : 'fresh-failure.json'), JSON.stringify(record, null, 2) + '\n');
  throw error;
} finally {
  if (application) await application.close();
  await rm(directory, { recursive: true, force: true });
}
