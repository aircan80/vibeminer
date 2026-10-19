// Explicit native smoke check; --live requests discovery and the two exact Home configurations.
import { _electron as electron, expect } from '@playwright/test';
import { mkdtemp, rm, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const live = process.argv.includes('--live');
const suppliedPath = process.argv.slice(2).find(value => value !== '--live');
const executablePath = path.resolve(suppliedPath || path.join(root, 'artifacts/desktop/VibeMiner-linux-x64/vibeminer'));
const directory = await mkdtemp(path.join(tmpdir(), 'vibeminer package '));
const evidence = path.join(root, 'artifacts/package-smoke');
await mkdir(evidence, { recursive: true });
const environment = { ...process.env, VIBEMINER_DATA_DIR: path.join(directory, 'catalog with spaces') };
for (const key of ['VIBEMINER_ENGINE_PATH', 'VIBEMINER_DOTNET', 'VIBEMINER_AUTHORED_FIXTURES', 'DOTNET_ROOT']) delete environment[key];
if (process.platform === 'linux') environment.PATH = '/usr/bin:/bin';
const launch = offline => electron.launch({ executablePath, args: ['--ozone-platform=headless', `--user-data-dir=${path.join(directory, 'electron profile')}`], env: {
  ...environment, ...(offline ? { HTTPS_PROXY: 'http://127.0.0.1:9', HTTP_PROXY: 'http://127.0.0.1:9', ALL_PROXY: 'http://127.0.0.1:9', NO_PROXY: '' } : {}),
} });
const record = { startedAt: new Date().toISOString(), executablePath, target: `${process.platform}-${process.arch}`, liveRequested: live };
let application;
try {
  application = await launch(false);
  await application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
  const page = await application.firstWindow();
  expect(await application.evaluate(({ app }) => app.isPackaged)).toBe(true);
  const icon = await application.evaluate(({ app, nativeImage }) => {
    const image = nativeImage.createFromPath([app.getAppPath(), 'dist', 'assets', 'vibeminer.png'].join(process.platform === 'win32' ? '\\' : '/'));
    return { empty: image.isEmpty(), size: image.getSize() };
  });
  expect(icon).toEqual({ empty: false, size: { width: 512, height: 512 } });
  await expect(page.getByRole('heading', { name: 'Which builds do you want to explore?' })).toBeVisible();
  await expect(page.getByLabel('Product', { exact: true }).locator('option')).toHaveCount(13);
  await expect(page.getByRole('heading', { name: 'Your research starts with a build' })).toBeVisible();
  if (live) {
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await expect(page.getByRole('status')).toContainText(/Discovery complete|Check completed with regional problems/, { timeout: 45_000 });
    expect(await page.locator('.build-row').count()).toBeGreaterThan(0);
    await page.locator('.build-row').first().click();
    record.observations = await page.evaluate(async () => {
      const reply = await window.discovery.read({ productId: 'wow', filter: 'all', limit: 50 });
      if (!reply.ok) throw new Error(reply.error.code);
      return reply.value;
    });
    await page.screenshot({ path: path.join(evidence, 'live-discovery.png'), fullPage: true });
    const opening = { productId: 'wow', buildIdentity: await page.locator('.build-row').first().getAttribute('data-build-id'), sourceRegion: await page.getByLabel('Download source', { exact: true }).inputValue() };
    await page.getByRole('button', { name: 'Open Build', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible({ timeout: 45_000 });
    await expect(page.getByText('Build and CDN configurations are verified.', { exact: true })).toBeVisible();
    const reopenedConfiguration = await page.evaluate(async params => window.discovery.open(params, crypto.randomUUID()), opening);
    expect(reopenedConfiguration.ok).toBe(true);
    record.workspace = reopenedConfiguration.value;
    expect(record.workspace.configurations).toHaveLength(2);
    expect(record.workspace.preparationProfile).toBe('home-configurations-v1');
    await page.screenshot({ path: path.join(evidence, 'live-home.png'), fullPage: true });
  }
  await application.close(); application = undefined;
  application = await launch(true);
  await application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
  const reopened = await application.firstWindow();
  await expect(reopened.getByLabel('Product', { exact: true }).locator('option')).toHaveCount(13);
  await expect(reopened.getByRole('status')).toContainText('Not refreshed this session.');
  if (live) {
    await expect(reopened.locator('.build-row')).toHaveCount(record.observations.builds.length);
    await expect(reopened.locator('.build-row[aria-pressed="true"]')).toHaveCount(1);
    const saved = await reopened.evaluate(async () => (await window.discovery.read({ productId: 'wow', filter: 'all', limit: 50 })));
    expect(saved.ok).toBe(true);
    expect(saved.value.lastCheckedAt).toBe(record.observations.lastCheckedAt);
    await reopened.screenshot({ path: path.join(evidence, 'offline-reopen.png'), fullPage: true });
    await reopened.getByRole('button', { name: 'Open Build', exact: true }).click();
    await expect(reopened.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible({ timeout: 30_000 });
    await expect(reopened.getByText('Build and CDN configurations are verified.', { exact: true })).toBeVisible();
    await reopened.getByRole('button', { name: 'View metadata details' }).click();
    await expect(reopened.locator('.configuration-receipt')).toHaveCount(2);
    await reopened.keyboard.press('Escape');
    await reopened.screenshot({ path: path.join(evidence, 'offline-home.png'), fullPage: true });
  }
  await application.close(); application = undefined;
  record.completedAt = new Date().toISOString(); record.outcome = 'passed';
  await writeFile(path.join(evidence, live ? 'live-result.json' : 'fresh-result.json'), JSON.stringify(record, null, 2) + '\n');
  process.stdout.write(`Packaged ${record.target} launch, bundled engine and ${live ? 'live discovery / verified configuration Home / saved-history' : 'fresh local'} offline reopen passed.\n`);
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
