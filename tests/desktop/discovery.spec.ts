import { test, expect } from '@playwright/test';
import { writeFile, readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { launch, root } from './helpers';

test('real Electron and C# open fresh discovery without an implicit remote check', async () => {
  const session = await launch();
  try {
    await expect(session.page.getByRole('heading', { name: 'Builds', exact: true })).toBeVisible();
    await expect(session.page.locator('.download-activity-trigger, .dv-chip')).toHaveCount(0);
    await expect(session.page.locator('.download-activity-floating')).toHaveCount(0);
    expect(await session.app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].isMenuBarVisible())).toBe(false);
    await expect(session.page.getByLabel('Product', { exact: true })).toHaveValue('wow');
    await expect(session.page.getByRole('heading', { name: 'Your research starts with a build' })).toBeVisible();
    await expect(session.page.getByRole('complementary')).toHaveCount(0);
    await expect(session.page.getByRole('button', { name: 'Open build', exact: true })).toHaveCount(0);
    await expect(session.page.getByRole('searchbox', { name: /Filter version or build number/ })).toBeDisabled();
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
    await page.getByRole('button', { name: 'Cancel refresh', exact: true }).click();
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
    await expect(page.getByRole('button', { name: 'Cancel refresh', exact: true })).toBeEnabled();
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/loading-empty-200.png'), fullPage: true });
    await writeFile(path.join(session.data, 'release-wow'), 'release');
    await expect(page.getByRole('heading', { name: 'Could not check wow', exact: true })).toBeVisible();
    await expect(page.locator('.dv-toolbar').getByRole('button', { name: 'Retry discovery', exact: true })).toBeEnabled();
    await expect(page.getByText('Official metadata could not be reached, and no saved observations are available.', { exact: false })).toBeVisible();
    await page.locator('.dv-notice').getByRole('button', { name: 'Retry discovery', exact: true }).click();
    await expect(page.locator('.dv-toolbar').getByRole('button', { name: /Cancel refresh|Retry discovery/ })).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1)).toBe(false);
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/offline-empty-200.png'), fullPage: true });
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
        const layout = await page.evaluate(() => {
          const box = (selector: string) => document.querySelector<HTMLElement>(selector)!.getBoundingClientRect();
          const open = [...document.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent === 'Open build')!.getBoundingClientRect();
          return {
            scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight, width: innerWidth, height: innerHeight,
            toolbar: box('.dv-toolbar').height, status: box('.dv-statusbar'), list: box('.dv-list'), inspector: box('.dv-inspector'), listHead: box('.dv-thead'), inspectorHead: box('.dv-insp-head'), open,
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
        if (scale === 1) {
          expect(layout.toolbar).toBeLessThanOrEqual(48);
          expect(Math.abs(layout.inspector.left - layout.list.right)).toBeLessThanOrEqual(2);
          expect(Math.abs(layout.inspectorHead.bottom - layout.listHead.bottom)).toBeLessThanOrEqual(1);
          expect(layout.inspector.width).toBeCloseTo(371, 0);
          expect(layout.family).toBe('system-ui, sans-serif');
          expect(layout.base).toBe('14px');
          expect(layout.background).toBe('rgb(245, 243, 237)');
          expect(layout.ink).toBe('rgb(36, 59, 58)');
          expect(layout.muted).toBe('rgb(88, 106, 100)');
        } else expect(layout.inspector.top, label).toBeGreaterThanOrEqual(layout.list.bottom - 1);
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
