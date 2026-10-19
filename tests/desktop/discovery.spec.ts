import { test, expect } from '@playwright/test';
import { writeFile, readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { launch, root } from './helpers';

test('real Electron and C# open fresh discovery without an implicit remote check', async () => {
  const session = await launch();
  try {
    await expect(session.page.getByRole('heading', { name: 'Which builds do you want to explore?' })).toBeVisible();
    await expect(session.page.getByLabel('Product', { exact: true })).toHaveValue('wow');
    await expect(session.page.getByRole('heading', { name: 'Your research starts with a build' })).toBeVisible();
    await expect(session.page.getByRole('button', { name: 'Open Build', exact: true })).toBeDisabled();
    await expect(session.page.getByText('Select a build with a recorded download source to open its prepared build overview.')).toBeVisible();
    await expect(session.page.getByRole('button', { name: 'Refresh builds', exact: true })).toBeEnabled();
    expect(await session.page.evaluate(() => typeof (window as any).require)).toBe('undefined');
  } finally { await session.close(); }
});

test('authored exact builds preserve selection, filters, source dialog and keyboard focus', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.getByText('Authored test data', { exact: true })).toBeVisible();
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.locator('.build-row').first().focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('.build-row').first()).toHaveAttribute('aria-pressed', 'true');
    const search = page.getByLabel('Version or build number');
    await search.fill('70448');
    await expect(search).toBeFocused();
    await expect(page.locator('.build-row').first()).toHaveAttribute('aria-pressed', 'true');
    const source = page.getByRole('button', { name: 'Source details', exact: true });
    await source.click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Close', exact: true })).toBeFocused();
    await page.keyboard.press('Tab');
    expect(await page.getByRole('dialog').evaluate(element => element.contains(document.activeElement))).toBe(true);
    await page.keyboard.press('Shift+Tab');
    expect(await page.getByRole('dialog').evaluate(element => element.contains(document.activeElement))).toBe(true);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).not.toBeVisible();
    await expect(source).toBeFocused();
    await page.getByRole('button', { name: 'Local', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'No locally prepared builds' })).toBeVisible();
    await page.getByRole('button', { name: 'All', exact: true }).click();
    await expect(page.locator('.build-row')).toHaveCount(2);
    const requests = (await readFile(path.join(session.data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(x => JSON.parse(x));
    expect(requests.filter(x => x.method === 'discovery.refresh')).toHaveLength(0);
  } finally { await session.close(); }
});

test('refresh cancellation and product changes never substitute stale build data', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await expect.poll(() => existsSync(path.join(session.data, 'waiting-wow'))).toBe(true);
    await page.getByRole('button', { name: 'Cancel discovery', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Discovery canceled');
    await expect(page.getByRole('button', { name: 'Refresh builds', exact: true })).toBeFocused();
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await page.getByLabel('Product', { exact: true }).selectOption('wowt');
    await expect.poll(() => existsSync(path.join(session.data, 'waiting-wowt'))).toBe(true);
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.getByRole('status')).toContainText('regional problems');
    await expect(page.locator('.inspector-product')).toHaveCount(0);
    await page.locator('.build-row').first().click();
    await expect(page.locator('.inspector-product')).toContainText('wowt');
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await expect(page.getByLabel('Product', { exact: true })).toHaveValue('wowt');
    await expect(page.locator('.inspector-product')).toContainText('wowt');
  } finally { await session.close(); }
});

test('offline discovery with no history offers honest retry at enlarged text', async () => {
  const session = await launch(true, true);
  try {
    const { page } = session;
    await expect(page.getByRole('heading', { name: 'Your research starts with a build' })).toBeVisible();
    await page.evaluate(() => { document.documentElement.style.fontSize = `${parseFloat(getComputedStyle(document.documentElement).fontSize) * 2}px`; });
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await expect.poll(() => existsSync(path.join(session.data, 'waiting-wow'))).toBe(true);
    await expect(page.getByRole('button', { name: 'Cancel discovery', exact: true })).toBeEnabled();
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/loading-empty-200.png'), fullPage: true });
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await expect(page.getByRole('heading', { name: 'Could not check wow', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Retry discovery', exact: true })).toBeEnabled();
    await expect(page.getByText('Official metadata could not be reached, and no saved observations are available.', { exact: false })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1)).toBe(false);
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/offline-empty-200.png'), fullPage: true });
  } finally { await session.close(); }
});

test('guided discovery reflows at reference sizes and enlarged text', async () => {
  const session = await launch(true);
  try {
    const { page, app } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.locator('.build-row').first().click();
    const baseFontSize = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
    for (const [width, height] of [[1280, 800], [1440, 1100]]) {
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
      for (const scale of [1, 2]) {
        await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, baseFontSize * scale);
        await expect(page.getByRole('heading', { name: 'Which builds do you want to explore?' })).toBeVisible();
        if (scale === 1) {
          const layout = await page.evaluate(() => {
            const header = document.querySelector<HTMLElement>('.app-header')!;
            const inspector = document.querySelector<HTMLElement>('.inspector')!;
            const product = document.querySelector<HTMLElement>('.product-picker')!;
            const question = document.querySelector<HTMLElement>('h1')!;
            return {
              headerBottom: header.getBoundingClientRect().bottom, inspectorTop: inspector.getBoundingClientRect().top,
              inspectorWidth: inspector.getBoundingClientRect().width, inspectorLeft: inspector.getBoundingClientRect().left,
              productRight: product.getBoundingClientRect().right,
              background: getComputedStyle(document.documentElement).backgroundColor,
              ink: getComputedStyle(document.body).color,
              family: getComputedStyle(document.body).fontFamily,
              questionSize: getComputedStyle(question).fontSize,
              secondary: getComputedStyle(document.querySelector('.intro p')!).color,
              surface: getComputedStyle(inspector).backgroundColor,
            };
          });
          expect(Math.abs(layout.inspectorTop - layout.headerBottom)).toBeLessThanOrEqual(1);
          expect(layout.inspectorWidth).toBeCloseTo(330, 0);
          expect(layout.productRight).toBeLessThan(layout.inspectorLeft);
          expect(layout.family).toBe('system-ui, sans-serif');
          expect(layout.questionSize).toBe('30px');
          expect(layout.background).toBe('rgb(245, 243, 237)');
          expect(layout.ink).toBe('rgb(36, 59, 58)');
          expect(layout.secondary).toBe('rgb(105, 123, 118)');
          expect(layout.surface).toBe('rgb(250, 250, 244)');
        }
        const overflowing = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);
        expect(overflowing, `${width}x${height} at ${scale * 100}% text`).toBe(false);
        await page.screenshot({ path: path.join(root, `artifacts/electron-tests/discovery-${width}x${height}-${scale * 100}.png`), fullPage: true });
      }
    }
  } finally { await session.close(); }
});
