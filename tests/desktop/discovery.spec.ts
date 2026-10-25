import { test, expect, type Page } from '@playwright/test';
import { writeFile, readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { launch, root } from './helpers';

async function calls(directory: string) {
  return (await readFile(path.join(directory, 'requests.ndjson'), 'utf8')).trim().split('\n').map(x => JSON.parse(x) as { method: string; params: Record<string, unknown> });
}

test('real Electron and C# open fresh discovery without an implicit remote check', async () => {
  const session = await launch();
  try {
    await expect(session.page.getByRole('heading', { name: 'Builds', exact: true })).toBeVisible();
    await expect(session.page.locator('.download-activity-trigger, .dv-chip')).toHaveCount(0);
    await expect(session.page.locator('.download-activity-floating')).toHaveCount(0);
    expect(await session.app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].isMenuBarVisible())).toBe(false);
    await expect(session.page.locator('.dv-toolbar').getByRole('combobox')).toHaveCount(0);
    await expect(session.page.getByLabel('Product', { exact: true })).toHaveValue('all');
    await expect(session.page.getByLabel('Product', { exact: true })).toBeDisabled();
    // The real engine's registry lists every product in the filter.
    expect(await session.page.getByLabel('Product', { exact: true }).locator('option').count()).toBe(14);
    await expect(session.page.getByRole('heading', { name: 'No saved builds yet' })).toBeVisible();
    await expect(session.page.getByRole('complementary')).toHaveCount(0);
    await expect(session.page.getByRole('button', { name: 'Open build', exact: true })).toHaveCount(0);
    await expect(session.page.getByRole('searchbox', { name: /Filter version or build number/ })).toBeDisabled();
    await expect(session.page.locator('.dv-pane-head').getByRole('button', { name: 'Refresh builds', exact: true })).toBeEnabled();
    await expect(session.page.locator('.dv-statusbar')).toContainText('Not checked yet');
    expect(await session.page.evaluate(() => typeof (window as any).require)).toBe('undefined');
  } finally { await session.close(); }
});

test('authored exact builds preserve selection, filters, source dialog and keyboard focus', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.getByText('Authored test data', { exact: true })).toBeVisible();
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.getByRole('listbox', { name: 'Builds' }).focus();
    await page.keyboard.press('End');
    await expect(page.locator('.build-row').last()).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Home');
    await expect(page.getByRole('listbox', { name: 'Builds' })).toBeFocused();
    await expect(page.locator('.build-row').first()).toHaveAttribute('aria-selected', 'true');
    const search = page.getByLabel('Version or build number');
    await search.fill('70448');
    await expect(search).toBeFocused();
    await expect(page.locator('.build-row').first()).toHaveAttribute('aria-selected', 'true');
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
    await page.getByRole('button', { name: 'Prepared', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'No prepared builds' })).toBeVisible();
    await page.getByRole('button', { name: 'All', exact: true }).click();
    await expect(page.locator('.build-row')).toHaveCount(2);
    const requests = await calls(session.data);
    expect(requests.filter(x => x.method.startsWith('discovery.refresh'))).toHaveLength(0);
    expect(requests.filter(x => x.method === 'discovery.read')).toHaveLength(0);
    expect(requests.filter(x => x.method === 'discovery.readAll').map(x => x.params.filter)).toEqual(['all', 'local', 'all']);
  } finally { await session.close(); }
});

test('one cancellable refresh checks every product, shows progress across products and keeps the Product filter', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await expect.poll(() => existsSync(path.join(session.data, 'waiting-wow')) && existsSync(path.join(session.data, 'waiting-wowt'))).toBe(true);
    await expect(page.locator('.dv-statusbar')).toContainText('Checking 2 products · 0 of 2 done…');
    await page.getByRole('button', { name: 'Cancel refresh', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Discovery canceled');
    await expect(page.locator('.dv-checks')).toHaveText('Checked 0 of 2 products · wow, wowt not checked');
    await expect(page.getByRole('button', { name: 'Refresh builds', exact: true })).toBeFocused();
    await expect(page.locator('.build-row')).toHaveCount(2);
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await page.getByLabel('Product', { exact: true }).selectOption('wowt');
    await expect(page.getByRole('heading', { name: 'Checking official metadata…' })).toBeVisible();
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.locator('.dv-statusbar')).toContainText('Checking 2 products · 1 of 2 done…');
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await expect(page.getByRole('status')).toContainText('Check completed with problems for 1 of 2 products');
    await expect(page.getByLabel('Product', { exact: true })).toHaveValue('wowt');
    await expect(page.locator('.build-row')).toHaveCount(1);
    await expect(page.locator('.dv-notice')).toContainText('wowt XX: Authored missing regional route (ROUTE_UNAVAILABLE)');
    await page.locator('.build-row').first().click();
    await expect(page.locator('.inspector-product')).toContainText('wowt');
    await page.getByLabel('Product', { exact: true }).selectOption('all');
    await expect(page.locator('.build-row')).toHaveCount(3);
    await expect(page.locator('.build-row .dv-product')).toHaveText(['wow', 'wow', 'wowt']);
    await expect(page.locator('.dv-statusbar')).toContainText('Checked 2 products');
    const requests = await calls(session.data);
    expect(requests.filter(x => x.method === 'discovery.refreshAll')).toHaveLength(2);
    expect(requests.filter(x => x.method === 'discovery.refresh')).toHaveLength(0);
  } finally { await session.close(); }
});

test('offline discovery with no history offers honest retry at enlarged text', async () => {
  const session = await launch(true, true);
  try {
    const { page } = session;
    await expect(page.getByRole('heading', { name: 'No saved builds yet' })).toBeVisible();
    await page.evaluate(() => { document.documentElement.style.fontSize = `${parseFloat(getComputedStyle(document.documentElement).fontSize) * 2}px`; });
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await expect.poll(() => existsSync(path.join(session.data, 'waiting-wow'))).toBe(true);
    await expect(page.getByRole('button', { name: 'Cancel refresh', exact: true })).toBeEnabled();
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/loading-empty-200.png'), fullPage: true });
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.getByRole('heading', { name: 'Could not check any product', exact: true })).toBeVisible();
    await expect(page.locator('.dv-refresh')).toHaveText(/Retry discovery/);
    await expect(page.getByText('Official metadata could not be read for any product, and no saved observations are available.', { exact: false })).toBeVisible();
    await expect(page.locator('.dv-notice')).toContainText('wow, wowt: Authored unavailable metadata source (NETWORK_UNAVAILABLE)');
    await page.locator('.dv-notice').getByRole('button', { name: 'Retry discovery', exact: true }).click();
    await expect(page.locator('.dv-refresh')).toBeFocused();
    await expect(page.locator('.dv-refresh')).toHaveText(/Cancel refresh|Retry discovery/);
    expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1)).toBe(false);
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/offline-empty-200.png'), fullPage: true });
  } finally { await session.close(); }
});

test('products failing with HTTP 500 or offline read as could not be checked, never as checked with nothing advertised', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    await writeFile(path.join(session.data, 'offline-wow'), 'offline');
    await writeFile(path.join(session.data, 'http500-wowt'), 'http500');
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.getByRole('status')).toHaveText('Could not refresh. Earlier saved observations are kept for wow. wowt: no saved observations.');
    await expect(page.locator('.dv-checks')).toHaveText('Checked 0 of 2 products · wow, wowt could not be checked');
    await expect(page.locator('.dv-statusbar')).not.toContainText('Latest check');
    const notice = page.locator('.dv-notice').filter({ hasText: 'Refresh unavailable' });
    await expect(notice).toContainText('wow: Authored unavailable metadata source (NETWORK_UNAVAILABLE)');
    await expect(notice).toContainText('wowt: Official metadata returned an unsuccessful HTTP status. (HTTP_STATUS)');
    // A failed check keeps the earlier builds and their Current label; the inspector says the latest check failed.
    await expect(page.locator('.build-row')).toHaveCount(2);
    await expect(page.locator('.build-row').first().locator('.dv-pill.current')).toHaveText('Current');
    await page.locator('.build-row').first().click();
    await expect(page.locator('dt', { hasText: 'Publication' }).locator('xpath=following-sibling::dd[1]')).toHaveText(/^Current: advertised at the last completed check \(.+\) · latest check failed$/);
    await page.getByLabel('Product', { exact: true }).selectOption('wowt');
    await expect(page.getByRole('heading', { name: 'Could not check wowt', exact: true })).toBeVisible();
    await expect(page.locator('.dv-empty')).toContainText('The latest check of wowt failed: Official metadata returned an unsuccessful HTTP status. (HTTP_STATUS)');
    await page.getByRole('button', { name: 'Current', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Could not check wowt', exact: true })).toBeVisible();
    await expect(page.locator('.dv-empty')).toContainText('No build of it is known to be current.');
    await expect(page.locator('.dv-window')).not.toContainText(/no advertised builds|was advertised at the last check|Previous successful evidence|for every product/);
  } finally { await session.close(); }
});

test('regions advertised without a CDN route are a calm note: the refresh completes and Retry never appears (#47)', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    await writeFile(path.join(session.data, 'no-route-sg-xx'), 'retail');
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.getByRole('status')).toHaveText('Discovery complete. Checked 2 products.');
    await expect(page.locator('.dv-checks')).toHaveText('Checked 2 products');
    await expect(page.locator('.dv-notice')).toHaveCount(0);
    await expect(page.locator('.dv-window')).not.toContainText(/Refresh incomplete|Earlier saved observations|A failed check|ROUTE_MISSING/);
    await expect(page.locator('.dv-refresh')).toHaveText(/Refresh builds/);
    await expect(page.getByRole('button', { name: 'Retry discovery' })).toHaveCount(0);
    await page.locator('.build-row').first().click();
    const routes = page.locator('dt', { hasText: 'CDN routes' }).locator('xpath=following-sibling::dd[1]');
    await expect(routes).toContainText(/^US/);
    await expect(routes.locator('.dv-route-note')).toHaveText('SG, XX: Advertised without a CDN route, so not usable as a download source.');
    await expect(page.getByLabel('Download source')).toHaveValue('us');
    await page.getByRole('button', { name: 'Source details' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('.route-notes')).toHaveText('SG, XX: Advertised without a CDN route, so not usable as a download source. (ROUTE_MISSING)');
    await expect(dialog).not.toContainText('Current attempt problems');
    await dialog.getByRole('button', { name: 'Close' }).click();
    // F5 checks again; with only notes it still never turns into Retry.
    await page.keyboard.press('F5');
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await expect(page.getByRole('status')).toHaveText('Discovery complete. Checked 2 products.');
    await expect(page.locator('.dv-refresh')).toHaveText(/Refresh builds/);
  } finally { await session.close(); }
});

test('when only wowt fails with HTTP 500 and only wow has history, nothing claims evidence for wowt or every product', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    await writeFile(path.join(session.data, 'http500-wowt'), 'http500');
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.getByRole('status')).toHaveText('Check completed with problems for 1 of 2 products. wowt: no saved observations.');
    await expect(page.locator('.dv-checks')).toHaveText('Checked 1 of 2 products · wowt could not be checked');
    const notice = page.locator('.dv-notice').filter({ hasText: 'Refresh incomplete' });
    await expect(notice).toContainText('wowt: no saved observations.');
    await expect(notice).toContainText('wowt: Official metadata returned an unsuccessful HTTP status. (HTTP_STATUS)');
    await expect(page.locator('.dv-window')).not.toContainText(/for every product|kept for wowt|Previous successful evidence/);
    // wow's check completed, so its inspector has no failure note.
    await page.locator('.build-row').first().click();
    await expect(page.locator('dt', { hasText: 'Publication' }).locator('xpath=following-sibling::dd[1]')).toHaveText(/^Current: advertised at the last completed check \(.+\)$/);
  } finally { await session.close(); }
});

test('a fresh catalog where every product fails says no product could be checked', async () => {
  const session = await launch(true, true);
  try {
    const { page } = session;
    await expect(page.getByRole('heading', { name: 'No saved builds yet' })).toBeVisible();
    await writeFile(path.join(session.data, 'http500-wowt'), 'http500');
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.getByRole('heading', { name: 'Could not check any product', exact: true })).toBeVisible();
    await expect(page.getByRole('status')).toHaveText('Could not refresh. wow, wowt: no saved observations.');
    await expect(page.locator('.dv-checks')).toHaveText('Checked 0 of 2 products · wow, wowt could not be checked');
    await expect(page.locator('.dv-window')).not.toContainText(/No builds advertised|No builds were advertised|Previous successful evidence/);
  } finally { await session.close(); }
});

/** Tab from the search box through the filter row; returns the accessible names in focus order. */
async function filterRowTabOrder(page: Page) {
  await page.getByRole('searchbox', { name: /Filter version or build number/ }).focus();
  const names: string[] = [];
  for (let i = 0; i < 6; i++) {
    names.push(await page.evaluate(() => { const active = document.activeElement as HTMLElement; return active.getAttribute('aria-label') || active.textContent?.replace('↻', '').trim() || ''; }));
    await page.keyboard.press('Tab');
  }
  return names;
}

test('the filter row is one keyboard path from search to Refresh builds at 100% and 200% text', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    const base = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
    for (const scale of [1, 2]) {
      await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, base * scale);
      expect(await filterRowTabOrder(page), `${scale * 100}% text`).toEqual(['Filter version or build number', 'Product', 'All', 'Prepared', 'Current', 'Refresh builds']);
      // After Refresh builds, Tab reaches the build list.
      await expect(page.getByRole('listbox', { name: 'Builds' })).toBeFocused();
    }
    await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, base);
    const product = page.getByLabel('Product', { exact: true });
    await product.focus();
    await product.selectOption('wowt');
    await expect(product).toBeFocused();
    await expect(page.getByRole('heading', { name: 'No saved builds for wowt' })).toBeVisible();
    await page.keyboard.press('F5');
    await expect(page.locator('.dv-refresh')).toHaveText(/Cancel refresh/);
    await expect(product).toBeFocused();
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await writeFile(path.join(session.data, 'release-wowt'), 'release');
    await expect(page.getByRole('status')).toContainText('problems for 1 of 2 products');
    await expect(product).toBeFocused();
  } finally { await session.close(); }
});

test('desktop discovery keeps a fixed window with scrolling panes at reference sizes and enlarged text', async () => {
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
        await expect(page.getByRole('heading', { name: 'Builds', exact: true })).toBeVisible();
        // Keep the pointer off the rows so hover does not change their background.
        await page.mouse.move(1, 1);
        const layout = await page.evaluate(() => {
          const box = (selector: string) => document.querySelector<HTMLElement>(selector)!.getBoundingClientRect();
          const open = [...document.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent === 'Open build')!.getBoundingClientRect();
          return {
            scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight, width: innerWidth, height: innerHeight,
            toolbar: box('.dv-toolbar').height, status: box('.dv-statusbar'), paneHead: box('.dv-pane-head'), refresh: box('.dv-refresh'), search: box('#search'), brand: box('.dv-brand img'),
            current: [...document.querySelectorAll<HTMLElement>('.build-row')].map(row => ({ selected: row.getAttribute('aria-selected') === 'true', current: row.classList.contains('current'), label: !!row.querySelector('.dv-pill.current'), background: getComputedStyle(row).backgroundColor })),
            toolbarControls: document.querySelectorAll('.dv-toolbar select, .dv-toolbar .dv-refresh').length, list: box('.dv-list'), inspector: box('.dv-inspector'), listHead: box('.dv-thead'), inspectorHead: box('.dv-insp-head'), open,
            family: getComputedStyle(document.body).fontFamily, background: getComputedStyle(document.documentElement).backgroundColor, ink: getComputedStyle(document.body).color,
            muted: getComputedStyle(document.querySelector('.dv-insp-head p')!).color, base: getComputedStyle(document.querySelector('.discovery-screen')!).fontSize,
          };
        });
        const label = `${width}x${height} at ${scale * 100}% text`;
        expect(layout.scrollWidth, label).toBeLessThanOrEqual(layout.width);
        expect(layout.scrollHeight, label).toBeLessThanOrEqual(layout.height);
        expect(layout.status.bottom, label).toBeLessThanOrEqual(layout.height + 1);
        expect(layout.open.bottom, label).toBeLessThanOrEqual(layout.status.top + 1);
        expect(layout.open.top, label).toBeGreaterThanOrEqual(0);
        // Refresh builds ends the filter row, right-aligned in the list pane, and the toolbar has no product picker.
        expect(layout.toolbarControls, label).toBe(0);
        expect(layout.refresh.right, label).toBeLessThanOrEqual(layout.paneHead.right);
        expect(layout.paneHead.right - layout.refresh.right, label).toBeLessThanOrEqual(scale * 15);
        expect(layout.refresh.left, label).toBeGreaterThan(layout.search.left);
        // Current rows carry the Current label whether or not they are selected; only unselected ones are tinted warm.
        expect(layout.current.every(row => row.current && row.label), label).toBe(true);
        expect(layout.current.find(row => row.selected)?.background, label).toBe('rgb(228, 241, 233)');
        expect(layout.current.find(row => !row.selected)?.background, label).toBe('rgb(251, 247, 236)');
        if (scale === 1) {
          expect(layout.toolbar).toBeLessThanOrEqual(48);
          expect(layout.brand.height).toBeCloseTo(28, 0);
          // At 1280 and 1440 wide the whole filter row fits on one line.
          expect(Math.abs(layout.refresh.top - layout.search.top)).toBeLessThanOrEqual(2);
          expect(Math.abs(layout.inspector.left - layout.list.right)).toBeLessThanOrEqual(2);
          expect(Math.abs(layout.inspectorHead.bottom - layout.listHead.bottom)).toBeLessThanOrEqual(1);
          expect(layout.inspector.width).toBeCloseTo(371, 0);
          expect(layout.family).toBe('system-ui, sans-serif');
          expect(layout.base).toBe('14px');
          expect(layout.background).toBe('rgb(245, 243, 237)');
          expect(layout.ink).toBe('rgb(36, 59, 58)');
          expect(layout.muted).toBe('rgb(88, 106, 100)');
        } else {
          expect(layout.inspector.top, label).toBeGreaterThanOrEqual(layout.list.bottom - 1);
          // The product line stays in the stacked inspector header, so same-version builds of different products stay apart.
          await expect(page.locator('.inspector-product'), label).toBeVisible();
          await expect(page.locator('.inspector-product'), label).toContainText('World of Warcraft · Retail · wow');
        }
        await page.screenshot({ path: path.join(root, `artifacts/electron-tests/discovery-${width}x${height}-${scale * 100}.png`) });
      }
    }
  } finally { await session.close(); }
});

test('cancel opening from the inspector keeps focus through stopping, and the docked activity panel follows its entry', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'hold-open-cancel'), 'hold');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await page.getByRole('button', { name: /Back to build list/ }).click();
    await expect(page.getByRole('button', { name: 'Open build', exact: true })).toBeFocused();
    const cancel = page.locator('.dv-inspector').getByRole('button', { name: 'Cancel opening', exact: true });
    await cancel.focus();
    await page.keyboard.press('Enter');
    const stopping = page.locator('.dv-inspector').getByRole('button', { name: 'Stopping opening…', exact: true });
    await expect(stopping).toHaveAttribute('aria-disabled', 'true');
    await expect(stopping).toBeFocused();
    const entry = page.locator('.dv-chip');
    await entry.click();
    const panel = page.locator('#download-activity-panel');
    await expect(panel.getByRole('heading', { name: 'Downloads & activity', exact: true })).toBeFocused();
    const layout = await page.evaluate(() => {
      const box = (selector: string) => document.querySelector<HTMLElement>(selector)!.getBoundingClientRect();
      const entry = document.querySelector('.dv-chip')!, panel = document.getElementById('download-activity-panel')!;
      return { panel: box('#download-activity-panel'), toolbar: box('.dv-toolbar'), status: box('.dv-statusbar'), inspector: box('.dv-inspector'), follows: !!(entry.compareDocumentPosition(panel) & Node.DOCUMENT_POSITION_FOLLOWING), inert: document.querySelector<HTMLElement>('.dv-inspector')!.inert };
    });
    expect(layout.follows).toBe(true);
    expect(layout.inert).toBe(true);
    expect(Math.abs(layout.panel.top - layout.toolbar.bottom)).toBeLessThanOrEqual(1);
    expect(Math.abs(layout.panel.bottom - layout.status.top)).toBeLessThanOrEqual(1);
    expect(Math.abs(layout.panel.left - layout.inspector.left)).toBeLessThanOrEqual(1);
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/discovery-activity-panel.png') });
    await page.keyboard.press('Escape');
    await expect(entry).toBeFocused();
    await stopping.focus();
    await writeFile(path.join(session.data, 'retire-open'), 'retire');
    await expect(page.locator('.dv-inspector').getByRole('button', { name: /Cancel opening|Stopping opening/ })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Open build', exact: true })).toBeFocused();
    await expect(page.locator('.dv-chip')).toHaveCount(0);
    // After the panel has been used, enlarging text still switches to the stacked layout.
    await session.app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    const base = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
    await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, base * 2);
    await expect(page.locator('.dv-window')).toHaveClass(/dv-compact/);
    const stacked = await page.evaluate(() => ({ list: document.querySelector('.dv-list')!.getBoundingClientRect(), inspector: document.querySelector('.dv-inspector')!.getBoundingClientRect(), height: document.documentElement.scrollHeight, inner: innerHeight }));
    expect(stacked.inspector.top).toBeGreaterThanOrEqual(stacked.list.bottom - 1);
    expect(stacked.height).toBeLessThanOrEqual(stacked.inner);
  } finally { await session.close(); }
});

test('plain Ctrl+= zooms in like the menu accelerator and stops at the 500% maximum', async () => {
  const session = await launch(true);
  try {
    const { page, app } = session;
    await expect(page.locator('.build-row')).toHaveCount(2);
    const zoom = () => app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].webContents.getZoomLevel());
    const press = (modifiers: string[]) => app.evaluate(({ BrowserWindow }, mods) => { const contents = BrowserWindow.getAllWindows()[0].webContents; contents.sendInputEvent({ type: 'keyDown', keyCode: '=', modifiers: mods as never }); contents.sendInputEvent({ type: 'keyUp', keyCode: '=', modifiers: mods as never }); }, modifiers);
    expect(await zoom()).toBe(0);
    await press(['control']);
    await expect.poll(zoom).toBe(0.5);
    await press(['control', 'shift']);
    await expect.poll(zoom).toBeGreaterThan(0.5);
    // Chromium's maximum is 500%, zoom level log(5)/log(1.2).
    const maximum = Math.log(5) / Math.log(1.2);
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].webContents.setZoomLevel(8.6));
    await press(['control']);
    await expect.poll(zoom).toBeCloseTo(maximum, 3);
    await press(['control']);
    await page.waitForTimeout(200);
    expect(await zoom()).toBeCloseTo(maximum, 3);
  } finally { await session.close(); }
});

test('with nothing selected the inspector keeps its disabled actions pinned above the status bar', async () => {
  const session = await launch(true);
  try {
    const { page, app } = session;
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    await expect(page.locator('.build-row')).toHaveCount(2);
    await expect(page.getByRole('listbox', { name: 'Builds' }).getByRole('option', { selected: true })).toHaveCount(0);
    const layout = await page.evaluate(() => ({ foot: document.querySelector('.dv-insp-foot')!.getBoundingClientRect(), status: document.querySelector('.dv-statusbar')!.getBoundingClientRect(), empty: document.querySelector('.dv-insp-empty')!.getBoundingClientRect() }));
    expect(Math.abs(layout.foot.bottom - layout.status.top)).toBeLessThanOrEqual(1);
    expect(layout.empty.bottom).toBeLessThanOrEqual(layout.foot.top + 1);
  } finally { await session.close(); }
});

test('in the shared scrolling area the first selection leaves the clicked row where it was (#52)', async () => {
  for (const scale of [1.5, 2]) {
    const session = await launch(true);
    try {
      const { page, app } = session;
      const label = `600x480 at ${scale * 100}% text`;
      await expect(page.locator('.build-row')).toHaveCount(2);
      await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(600, 480));
      await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, 15 * scale);
      await expect(page.locator('.dv-shared'), label).toHaveCount(1);
      await expect(page.getByRole('listbox', { name: 'Builds' }).getByRole('option', { selected: true }), label).toHaveCount(0);
      // The jump's slot is reserved but is no control yet: not visible, not focusable, not announced.
      await expect(page.getByRole('button', { name: 'View selected build' }), label).toHaveCount(0);
      const row = page.locator('.build-row').first();
      await row.scrollIntoViewIfNeeded();
      const before = await row.boundingBox();
      // With nothing selected the page itself still never scrolls.
      expect(await page.evaluate(() => [document.documentElement.scrollWidth, document.documentElement.scrollHeight]), label).toEqual([600, 480]);
      await row.click();
      await expect(row, label).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByRole('button', { name: 'View selected build' }), label).toBeVisible();
      const after = await row.boundingBox();
      expect(Math.abs(after!.y - before!.y), label).toBeLessThanOrEqual(0.5);
      expect(Math.abs(after!.height - before!.height), label).toBeLessThanOrEqual(0.5);
    } finally { await session.close(); }
  }
});

test('nothing overlaps or is cut off from the smallest window to the reference sizes, at 100%, 150% and 200% text (#52)', async () => {
  test.setTimeout(90_000);
  // Two more products with long real-style ids (wow_classic_era_ptr, wow_classic_beta), so the Product column meets the longest ids.
  const session = await launch(true, false, false, false, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await expect(page.locator('.build-row')).toHaveCount(4);
    // A measured build, so the Downloaded column shows a meter rather than a short word.
    await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await page.getByRole('button', { name: /Choose build/ }).click();
    await writeFile(path.join(data, 'stored-summary.json'), JSON.stringify({ storedObjects: '30', storedBytes: '3000' }));
    await expect(page.locator('.build-row').first()).toContainText('50%');
    await page.locator('.build-row').first().click();
    const base = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
    // What part of an element (and of its focus ring, with ring) every clipping ancestor, the toolbar and the status bar leave visible.
    const visible = (selector: string, ring = false) => page.evaluate(([selector, ring]) => {
      const element = document.querySelector<HTMLElement>(selector)!, r = element.getBoundingClientRect(), style = getComputedStyle(element);
      const out = ring ? parseFloat(style.outlineWidth) + parseFloat(style.outlineOffset) : 0;
      let top = document.querySelector('.dv-toolbar')!.getBoundingClientRect().bottom, bottom = document.querySelector('.dv-statusbar')!.getBoundingClientRect().top;
      for (let parent = element.parentElement; parent; parent = parent.parentElement) {
        const clip = getComputedStyle(parent);
        if (!/auto|scroll|hidden|clip/.test(`${clip.overflowY} ${clip.overflowX}`)) continue;
        const inner = parent.getBoundingClientRect().top + parent.clientTop;
        top = Math.max(top, inner); bottom = Math.min(bottom, inner + parent.clientHeight);
      }
      return { top: r.top - out, bottom: r.bottom + out, viewTop: top, viewBottom: bottom, thead: document.querySelector('.dv-thead')!.getBoundingClientRect().bottom };
    }, [selector, ring] as const);
    const whole = (fit: Awaited<ReturnType<typeof visible>>, what: string) => {
      expect(fit.top, what).toBeGreaterThanOrEqual(fit.viewTop - 1);
      expect(fit.bottom, what).toBeLessThanOrEqual(fit.viewBottom + 1);
    };
    for (const [width, height] of [[600, 480], [1280, 800], [1440, 1100]]) for (const scale of [1, 1.5, 2]) {
      const label = `${width}x${height} at ${scale * 100}% text`, rem = base * scale;
      // Stacked below 65.333333rem of width, two-line rows below 28rem, one shared scrolling area below 25rem of height.
      const compact = width < 65.333333 * rem, expected = { compact, narrow: compact && width < 28 * rem, shared: compact && height < 25 * rem };
      expect(expected.shared, label).toBe(width === 600 && scale > 1);
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
      await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, rem);
      // Wait for the window size, the new text size and the layout state that follows from both, not just the width.
      await expect.poll(() => page.evaluate(() => ({ width: innerWidth, height: innerHeight, status: Math.round(document.querySelector('.dv-statusbar')!.getBoundingClientRect().height),
        compact: !!document.querySelector('.dv-compact'), narrow: !!document.querySelector('.dv-narrow'), shared: !!document.querySelector('.dv-shared') })), label)
        .toEqual({ width, height, status: Math.round(1.633333 * rem), ...expected });
      await page.evaluate(() => { for (const element of document.querySelectorAll('.dv-body, .dv-pane, .dv-list, .dv-insp-body')) element.scrollTop = 0; });
      await page.mouse.move(1, 1);
      const layout = await page.evaluate(() => {
        const box = (element: Element) => { const r = element.getBoundingClientRect(); return { left: r.left, top: r.top, right: r.right, bottom: r.bottom }; };
        const one = (selector: string) => document.querySelector<HTMLElement>(selector)!;
        const clipped = (element: HTMLElement) => element.scrollHeight > element.clientHeight + 1 || element.scrollWidth > element.clientWidth + 1;
        const list = one('.dv-list'), listBox = list.getBoundingClientRect(), listRight = listBox.left + list.clientLeft + list.clientWidth;
        // Every visible header and row cell, and everything inside it, stays within the list and is not cut short.
        const cells = [...document.querySelectorAll<HTMLElement>('.dv-thead > span, .build-row > span')].filter(cell => getComputedStyle(cell).display !== 'none');
        const cut = cells.flatMap(cell => [cell, ...cell.querySelectorAll<HTMLElement>('*')]).filter(element => element.getBoundingClientRect().width > 1)
          .filter(element => { const r = element.getBoundingClientRect(); return r.right > listRight + 1 || r.left < listBox.left - 1 || element.matches('.dv-cols > span') && element.scrollWidth > element.clientWidth + 1; })
          .map(element => element.textContent);
        // Every element that actually scrolls, so the shared area can be checked to be the only one.
        const scrollers = [...document.querySelectorAll<HTMLElement>('.dv-window *')].filter(element => {
          const style = getComputedStyle(element);
          return /auto|scroll/.test(style.overflowY) && element.scrollHeight > element.clientHeight + 1 || /auto|scroll/.test(style.overflowX) && element.scrollWidth > element.clientWidth + 1;
        }).map(element => element.className);
        const head = one('.dv-insp-head');
        return {
          scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight, cut, scrollers, products: cells.filter(cell => cell.matches('.dv-product')).map(cell => cell.textContent),
          pane: box(one('.dv-pane')), inspector: box(one('.dv-inspector')), head: box(head), title: box(one('#inspector-heading')), product: box(one('.inspector-product')),
          headClipped: clipped(head), footClipped: clipped(one('.dv-insp-foot')),
        };
      });
      expect(layout.scrollWidth, label).toBeLessThanOrEqual(width);
      expect(layout.scrollHeight, label).toBeLessThanOrEqual(height);
      expect(layout.products, label).toEqual(['wow', 'wow', 'wow_classic_era_ptr', 'wow_classic_beta']);
      expect(layout.cut, label).toEqual([]);
      // The inspector sits beside or below the list pane, never over it, and its title and product line stay inside its header.
      if (expected.compact) expect(layout.inspector.top, label).toBeGreaterThanOrEqual(layout.pane.bottom - 1);
      else expect(layout.inspector.left, label).toBeGreaterThanOrEqual(layout.pane.right - 1);
      for (const part of [layout.title, layout.product]) {
        expect(part.top, label).toBeGreaterThanOrEqual(layout.head.top - 1);
        expect(part.bottom, label).toBeLessThanOrEqual(layout.head.bottom + 1);
      }
      expect(layout.headClipped, label).toBe(false);
      expect(layout.footClipped, label).toBe(false);
      // In the shared area it is the only scroller; otherwise the area between toolbar and status bar never scrolls.
      if (expected.shared) expect(layout.scrollers, label).toEqual(['dv-body']);
      else expect(layout.scrollers, label).not.toContain('dv-body');
      // Keyboard to the selected row: the whole row shows, below the list header.
      await page.locator('#builds').focus();
      await page.keyboard.press('Home');
      await expect(page.locator('.build-row').first(), label).toHaveAttribute('aria-selected', 'true');
      const row = await visible('.build-row[aria-selected="true"]');
      whole(row, `selected row, ${label}`);
      expect(row.top, `selected row below the list header, ${label}`).toBeGreaterThanOrEqual(row.thead - 1);
      // Keyboard to Open build: its whole focus ring shows, clear of the status bar.
      await page.locator('#opening-source').focus();
      await page.keyboard.press('Tab');
      await expect(page.locator('.dv-open'), label).toBeFocused();
      expect(await page.locator('.dv-open').evaluate(element => element.matches(':focus-visible')), label).toBe(true);
      whole(await visible('.dv-open', true), `Open build focus ring, ${label}`);
      await page.screenshot({ path: path.join(root, `artifacts/electron-tests/discovery-fit-${width}x${height}-${scale * 100}.png`) });
      // The shared area has a jump to the inspector and a return to the selected row; the other layouts show both at once and need neither.
      const jump = page.getByRole('button', { name: 'View selected build', exact: true }), back = page.getByRole('button', { name: 'Return to build list', exact: true });
      if (!expected.shared) { await expect(jump, label).toHaveCount(0); await expect(back, label).toHaveCount(0); continue; }
      await page.evaluate(() => { document.querySelector('.dv-body')!.scrollTop = 0; });
      await jump.focus(); await page.keyboard.press('Enter');
      await expect(page.locator('#inspector-heading'), label).toBeFocused();
      const jumped = await visible('#inspector-heading');
      whole(jumped, `inspector heading after the jump, ${label}`);
      expect(jumped.top, `inspector heading clear of the list header, ${label}`).toBeGreaterThanOrEqual(jumped.thead - 1);
      await back.focus(); await page.keyboard.press('Enter');
      await expect(page.getByRole('listbox', { name: 'Builds' }), label).toBeFocused();
      const returned = await visible('.build-row[aria-selected="true"]');
      whole(returned, `selected row after the return, ${label}`);
      expect(returned.top, `returned row below the list header, ${label}`).toBeGreaterThanOrEqual(returned.thead - 1);
      // Leaving the shared area while a jump control has focus hands focus to what it pointed at, never to the page.
      await jump.focus();
      await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, base);
      await expect(jump, label).toHaveCount(0);
      await expect(page.locator('#inspector-heading'), label).toBeFocused();
    }
  } finally { await session.close(); }
});
