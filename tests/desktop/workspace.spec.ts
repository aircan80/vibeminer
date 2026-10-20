import { test, expect } from '@playwright/test';
import { readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { launch, root } from './helpers';

async function requests(directory: string) {
  return (await readFile(path.join(directory, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
}

test('Open verifies an exact context, preserves discovery on Back, and exposes honest Home', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.getByRole('listbox', { name: 'Builds' }).focus(); await page.keyboard.press('Enter');
    await expect(page.locator('.build-row').first()).toContainText('New to VibeMiner');
    await page.getByLabel('Version or build number').fill('70448');
    await expect(page.getByLabel('Download source', { exact: true })).toHaveValue('us');
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await expect.poll(() => existsSync(path.join(session.data, 'waiting-open'))).toBe(true);
    await expect(page.locator('.preparation-view')).toBeVisible();
    await page.getByRole('button', { name: /Back to build list/ }).click();
    await expect(page.getByLabel('Version or build number')).toHaveValue('70448');
    await expect(page.locator('.build-row').first()).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('button', { name: 'Open build', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await expect(page.locator('.preparation-view')).toBeVisible();
    expect((await requests(session.data)).filter(request => request.method === 'workspace.open')).toHaveLength(1);
    await writeFile(path.join(session.data, 'release-open'), 'release');
    const heading = page.getByRole('heading', { name: 'Build overview', exact: true });
    await expect(heading).toBeVisible(); await expect(heading).toBeFocused();
    await expect(page.getByText('Published download content', { exact: true })).toBeVisible(); await expect(page.locator('.dh-side')).toContainText('Download manifestVerified');
    await expect(page.getByRole('button', { name: 'Download all', exact: true })).toHaveCount(0);
    await expect(page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Files', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'View metadata details' }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.locator('.configuration-receipt')).toHaveCount(3);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: 'View metadata details' })).toBeFocused();
    await page.getByRole('button', { name: /Choose build/ }).click();
    await expect(page.getByLabel('Version or build number')).toHaveValue('70448');
    await expect(page.locator('.build-row').first()).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('.build-row').first()).not.toContainText('New to VibeMiner');
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await expect(heading).toBeVisible();
    const calls = await requests(session.data);
    expect(calls.filter(request => request.method === 'workspace.open')).toHaveLength(2);
    expect(calls.filter(request => request.method === 'discovery.refresh')).toHaveLength(0);
  } finally { await session.close(); }
});

test('detached preparation cannot navigate while the Product filter and a refresh change the list', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await expect(page.locator('.preparation-view')).toBeVisible();
    await page.getByRole('button', { name: /Back to build list/ }).click();
    await page.getByLabel('Product', { exact: true }).selectOption('wowt');
    await expect(page.getByRole('heading', { name: 'No saved builds for wowt' })).toBeVisible();
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await expect.poll(() => existsSync(path.join(session.data, 'waiting-wowt'))).toBe(true);
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await expect(page.getByRole('status')).toContainText('problems for 1 of 2 products');
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('button', { name: 'View verified overview' })).toBeVisible();
    await expect(page.getByLabel('Product', { exact: true })).toHaveValue('wowt');
    await expect(page.locator('.desktop-home')).toHaveCount(0);
    // The Product filter only narrows the list, so the verified result opens when asked for.
    await page.getByRole('button', { name: 'View verified overview' }).click();
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await page.getByRole('button',{name:'Product Retail wow',exact:true}).click();await expect(page.locator('.download-info-body dl>div').filter({has:page.getByText('Exact product ID',{exact:true})}).locator('dd')).toHaveText('wow');
    expect((await requests(session.data)).filter(request => request.method === 'workspace.open')).toHaveLength(1);
  } finally { await session.close(); }
});

test('cancel waits for preparation retirement and a failed exact build can be retried', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'hold-open-cancel'), 'hold');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await page.getByRole('button', { name: 'Cancel preparation', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Stopping preparation…', exact: true })).toHaveAttribute('aria-disabled', 'true');
    await expect(page.locator('.desktop-home')).toHaveCount(0);
    await writeFile(path.join(session.data, 'retire-open'), 'retire');
    await expect(page.getByRole('status')).toHaveText(/Preparation canceled/);
    await writeFile(path.join(session.data, 'open-error'), 'error');
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await page.getByRole('button', { name: 'Retry this build', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('CONFIGURATION_INTEGRITY');
    await expect(page.locator('.desktop-home')).toHaveCount(0);
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/preparation-failure.png'), fullPage: true });
  } finally { await session.close(); }
});

test('preparation and C Home preserve reference layout with enlarged text', async () => {
  const session = await launch(true);
  try {
    const { page, app } = session;
    await page.locator('.build-row').first().click();
    const baseFontSize = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await expect(page.locator('.preparation-view')).toBeVisible();
    for (const view of ['preparation', 'home']) {
      if (view === 'home') {
        await writeFile(path.join(session.data, 'release-open'), 'release');
        await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
      }
      for (const [width, height] of [[1280, 800], [1440, 1100]]) {
        await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
        for (const scale of [1, 2]) {
          await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, baseFontSize * scale);
          expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${view} ${width}x${height} ${scale * 100}%`).toBe(false);
          await page.screenshot({ path: path.join(root, `artifacts/electron-tests/${view}-${width}x${height}-${scale * 100}.png`), fullPage: true });
        }
      }
    }
  } finally { await session.close(); }
});

test('Download objects pages a pinned local ledger and returns focus to Home', async () => {
  const session = await launch(true);
  try {
    const { page, app } = session;
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Browse in Files', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    await expect(page.getByText(/^50 encoded objects on this page/)).toBeVisible();
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Next page', exact: true }).click();
    await expect(page.getByText(/^10 encoded objects on this page/)).toBeVisible();
    await expect(page.getByText('Object 51', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Previous page', exact: true }).click();
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    const base = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
    for (const [width,height] of [[1280,800],[1440,1100]]) {
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width,size.height), {width,height});
      for (const scale of [1,2]) {
        await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, base*scale);
        expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth+1)).toBe(false);
        await page.screenshot({path:path.join(root,`artifacts/electron-tests/download-objects-${width}x${height}-${scale*100}.png`),fullPage:true});
      }
    }
    await page.getByRole('button', { name: /Return to build overview/ }).click();
    await expect(page.getByRole('button', { name: 'Browse in Files', exact: true })).toBeFocused();
    const calls = await requests(session.data);
    expect(calls.filter(call => call.method === 'download.read')).toHaveLength(3);
    expect(calls.filter(call => call.method === 'discovery.refresh')).toHaveLength(0);
  } finally { await session.close(); }
});
