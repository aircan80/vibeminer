// UI steps shared by tools/smoke-package.mjs and tests/desktop/package-smoke.spec.ts.
// Every selector and bridge call the package smoke check depends on lives here, so `npm run test:e2e` fails when the screens change.
import { _electron as electron, expect } from '@playwright/test';
import path from 'node:path';

/** Products in the engine's registry. The Product filter lists them after "All products". */
export const registeredProducts = 13;
/** Discovery renders the first page of 50 builds and loads more only on request. */
const firstPage = 50;
const blockedProxy = { HTTPS_PROXY: 'http://127.0.0.1:9', HTTP_PROXY: 'http://127.0.0.1:9', ALL_PROXY: 'http://127.0.0.1:9', NO_PROXY: '' };

/**
 * Launch the app with an isolated Electron profile under `directory`. Pass `executablePath` for a package, or `appPath`
 * for the unpackaged app. `offline` points every HTTP proxy at a closed local port; it is not OS-level network denial.
 */
export async function launchApp({ executablePath, appPath, directory, env, offline = false }) {
  const application = await electron.launch({
    ...(executablePath ? { executablePath } : {}),
    args: [...(appPath ? [appPath] : []), '--ozone-platform=headless', `--user-data-dir=${path.join(directory, 'electron profile')}`],
    env: { ...env, ...(offline ? blockedProxy : {}) },
  });
  await application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
  return { application, page: await application.firstWindow() };
}

/** Discovery on an empty catalog: the full product filter, no saved builds and no implicit remote check. */
export async function expectEmptyDiscovery(page, { productCount = registeredProducts } = {}) {
  await expect(page.getByRole('heading', { name: 'Builds', exact: true })).toBeVisible();
  await expect(page.getByLabel('Product', { exact: true }).locator('option')).toHaveCount(productCount + 1);
  await expect(page.getByRole('heading', { name: 'No saved builds yet' })).toBeVisible();
  await expect(refreshButton(page)).toBeEnabled();
  await expect(page.getByRole('status')).toHaveText('Not refreshed this session.');
  await expect(page.locator('.dv-statusbar')).toContainText('Not checked yet');
}

const refreshButton = page => page.locator('.dv-pane-head').getByRole('button', { name: 'Refresh builds', exact: true });

/** One explicit check of every product. Regional problems still count as a completed check. */
export async function refreshBuilds(page, { timeout }) {
  await refreshButton(page).click();
  await expect(page.getByRole('status')).toContainText(/^(Discovery complete|Check completed with problems)/, { timeout });
  await expect(page.locator('.build-row').first()).toBeVisible();
}

/** The saved catalog as the engine returns it: every saved build and each product's latest check. */
export async function readCatalog(page) {
  return page.evaluate(async () => {
    const builds = [];
    let cursor = null, value;
    do {
      const reply = await window.discovery.readAll({ filter: 'all', limit: 50, ...(cursor ? { cursor } : {}) });
      if (!reply.ok) throw new Error(`${reply.error.message} (${reply.error.code})`);
      value = reply.value; cursor = value.nextCursor;
      builds.push(...value.builds.map(build => ({ productId: build.productId, id: build.id, version: build.version })));
    } while (cursor && builds.length < 500);
    return { builds, products: value.products.map(status => ({ productId: status.productId, lastCheckedAt: status.lastCheckedAt, connection: status.connection })) };
  });
}

/** Select the first build of `productId` (or the first build), returning the exact opening it describes. */
export async function selectBuild(page, { productId = 'wow' } = {}) {
  const rows = page.getByRole('listbox', { name: 'Builds' }).getByRole('option');
  const preferred = rows.and(page.locator(`[data-product-id="${productId}"]`));
  const row = await preferred.count() ? preferred.first() : rows.first();
  await row.click();
  await expect(row).toHaveAttribute('aria-selected', 'true');
  const inspector = page.locator('.dv-inspector');
  return {
    productId: await row.getAttribute('data-product-id'), buildIdentity: await row.getAttribute('data-build-id'),
    sourceRegion: await inspector.getByLabel('Download source', { exact: true }).inputValue(),
    version: (await inspector.locator('#inspector-heading').textContent()).trim(),
  };
}

/** Open the selected build and wait for Home. A failed preparation is reported with its own message. */
export async function openBuild(page, { timeout }) {
  await page.locator('.dv-inspector').getByRole('button', { name: 'Open build', exact: true }).click();
  const home = page.locator('.desktop-home').getByRole('heading', { name: 'Build overview', exact: true });
  const failure = page.locator('.preparation-view .notice[role="alert"]');
  await expect(home.or(failure)).toBeVisible({ timeout });
  if (await failure.isVisible()) throw new Error(`Opening failed: ${(await failure.textContent()).trim()}`);
}

/** Home for exactly this opening, with verified configurations; returns the engine's workspace snapshot. */
export async function expectHome(page, opening) {
  const header = page.locator('.dh-toolbar');
  await expect(header.getByRole('button', { name: `Build ${opening.version}`, exact: true })).toBeVisible();
  await expect(page.locator('.dh-statusbar')).toContainText(`${opening.productId} ${opening.version} · ${opening.sourceRegion.toUpperCase()}`);
  await expect(page.locator('.dh-side')).toContainText(/(Build and CDN configs|Required metadata)Verified/);
  const params = { productId: opening.productId, buildIdentity: opening.buildIdentity, sourceRegion: opening.sourceRegion };
  const reply = await page.evaluate(async value => window.discovery.open(value, crypto.randomUUID()), params);
  if (!reply.ok) throw new Error(`Reopening the workspace failed: ${reply.error.message} (${reply.error.code})`);
  const workspace = reply.value;
  expect(workspace.build.id).toBe(opening.buildIdentity);
  expect(workspace.sourceReference.region).toBe(opening.sourceRegion);
  expect(workspace.preparationProfile).toMatch(/^home-(download|inventory)-v1$/);
  expect(workspace.configurations.map(receipt => receipt.role).sort()).toEqual(['buildConfig', 'cdnConfig']);
  return workspace;
}

/** Home's Metadata and Source details dialogs show the verified receipts of this workspace. */
export async function expectHomeDetails(page, workspace) {
  await page.getByRole('button', { name: 'View metadata details', exact: true }).click();
  const metadata = page.getByRole('dialog', { name: 'Build metadata' });
  await expect(metadata).toBeVisible();
  await expect(metadata.locator('.configuration-receipt')).toHaveCount(workspace.configurations.length + (workspace.metadata?.length ?? 0));
  await page.keyboard.press('Escape');
  await expect(metadata).toBeHidden();
  await page.getByRole('group', { name: 'Home utilities' }).getByRole('button', { name: 'Source details', exact: true }).click();
  const source = page.getByRole('dialog', { name: 'Exact build & source details' });
  await expect(source).toBeVisible();
  await expect(source).toContainText(workspace.build.id);
  await expect(source.locator('.configuration-receipt')).toHaveCount(workspace.configurations.length + (workspace.metadata?.length ?? 0) + workspace.build.sourceReceipts.length);
  await page.keyboard.press('Escape');
  await expect(source).toBeHidden();
}

/** Discovery after a restart: the saved catalog and exact selection, without a new check. */
export async function expectStoredDiscovery(page, { catalog, selected }) {
  await expect(page.getByRole('status')).toHaveText('Not refreshed this session.');
  const rows = page.locator('.build-row');
  if (catalog.builds.length <= firstPage) await expect(rows).toHaveCount(catalog.builds.length);
  else await expect.poll(() => rows.count()).toBeGreaterThanOrEqual(firstPage);
  const chosen = page.locator('.build-row[aria-selected="true"]');
  await expect(chosen).toHaveCount(1);
  await expect(chosen).toHaveAttribute('data-build-id', selected.buildIdentity);
  await expect(chosen).toHaveAttribute('data-product-id', selected.productId);
  expect(await readCatalog(page)).toEqual(catalog);
}
