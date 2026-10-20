import { test, expect } from '@playwright/test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { root } from './helpers';
import { expectEmptyDiscovery, expectHome, expectHomeDetails, expectStoredDiscovery, launchApp, openBuild, readCatalog, refreshBuilds, selectBuild } from '../../tools/smoke-steps.mjs';

// Runs the package smoke check's own steps against the unpackaged app, so `npm run smoke:package` can't drift from the screens unnoticed.
const appPath = process.env.VIBEMINER_DESKTOP_TEST_APP_PATH ?? path.join(root, 'apps/desktop');

async function session<T>(work: (launch: (offline: boolean, env?: Record<string, string>) => ReturnType<typeof launchApp>, directory: string) => Promise<T>) {
  const directory = await mkdtemp(path.join(tmpdir(), 'vibeminer-smoke-steps-'));
  let current: Awaited<ReturnType<typeof launchApp>> | undefined;
  const launch = async (offline: boolean, env: Record<string, string> = {}) => {
    await current?.application.close();
    current = await launchApp({ appPath, directory, offline, env: { ...process.env, VIBEMINER_DATA_DIR: path.join(directory, 'catalog'), ...env } });
    return current;
  };
  try { return await work(launch, directory); } finally { await current?.application.close(); await rm(directory, { recursive: true, force: true }); }
}

test('fresh package smoke steps pass on the real engine, then again with HTTP proxies blocked', async () => {
  await session(async launch => {
    await expectEmptyDiscovery((await launch(false)).page);
    await expectEmptyDiscovery((await launch(true)).page);
  });
});

test('live package smoke steps pass on the authored engine: refresh, open, offline reopen with saved selection and Home', async () => {
  await session(async (launch, directory) => {
    const fixture = { VIBEMINER_ENGINE_PATH: path.join(root, 'tests/desktop/fixtures/engine.mjs'), VIBEMINER_FIXTURE_CONTROL_DIR: directory, VIBEMINER_AUTHORED_FIXTURES: '1', VIBEMINER_FIXTURE_PERSIST: '1' };
    for (const release of ['release-wow', 'release-wowt', 'release-open']) await writeFile(path.join(directory, release), 'release');
    let { page } = await launch(false, fixture);
    await refreshBuilds(page, { timeout: 8_000 });
    const opening = await selectBuild(page, { productId: 'wow' });
    expect(opening.productId).toBe('wow');
    await openBuild(page, { timeout: 8_000 });
    const workspace = await expectHome(page, opening);
    await expectHomeDetails(page, workspace);
    const catalog = await readCatalog(page);
    // The authored refresh adds a wowt build to the two saved wow builds.
    expect(catalog.builds.map(build => build.productId).sort()).toEqual(['wow', 'wow', 'wowt']);
    ({ page } = await launch(true, fixture));
    await expectStoredDiscovery(page, { catalog, selected: opening });
    await openBuild(page, { timeout: 8_000 });
    await expectHomeDetails(page, await expectHome(page, opening));
  });
});
