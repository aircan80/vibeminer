import { test, expect, type ElectronApplication, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import { readRequests } from './fixtures/requests.mjs';
import { launch, root, size, text } from './helpers';
// #205: the Discovery build list from the layer 3 catalog, against the authored engine with build-catalog-v1.
const start = (mode: '1' | 'empty' | 'readOnly' = '1') => launch(true, false, false, false, false, false, false, false, false, false, false, false, false, false, { env: { VIBEMINER_FIXTURE_BUILD_CATALOG: mode } });
const outcome = (data: string, value: 'ok' | 'partial' | 'offline' | 'busy' | 'slow') => writeFile(path.join(data, 'build-catalog-refresh.json'), JSON.stringify({ outcome: value }));
const options = (page: Page) => page.getByRole('listbox', { name: 'Builds' }).getByRole('option');
const refreshButton = (page: Page) => page.locator('.dv-pane-head .dv-refresh');
/** Build rows fully on screen and not covered: inside the window between toolbar and status bar, and hit at their middle. */
const visibleRows = (page: Page) => page.evaluate(() => {
  const top = document.querySelector('.dv-toolbar')!.getBoundingClientRect().bottom, bottom = document.querySelector('.dv-statusbar')!.getBoundingClientRect().top;
  return [...document.querySelectorAll<HTMLElement>('[role="option"]')].filter(row => { const r = row.getBoundingClientRect(); if (r.top < top || r.bottom > bottom || r.height === 0) return false; const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); return !!hit && row.contains(hit); }).length;
});
async function capture(app: ElectronApplication, page: Page, state: string, scales: number[] = [1]) {
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await size(app, width, height);
    for (const scale of scales) {
      await text(page, scale);
      await page.mouse.move(1, 1);
      await expect(page.getByRole('heading', { name: 'Builds', exact: true })).toBeVisible();
      const fit = await page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight, width: innerWidth, height: innerHeight, status: document.querySelector('.dv-statusbar')!.getBoundingClientRect().bottom }));
      const label = `${state} ${width}x${height} at ${scale * 100}%`;
      expect(fit.scrollWidth, label).toBeLessThanOrEqual(fit.width);
      expect(fit.scrollHeight, label).toBeLessThanOrEqual(fit.height);
      expect(fit.status, label).toBeLessThanOrEqual(fit.height + 1);
      await page.screenshot({ path: path.join(root, `artifacts/electron-tests/build-catalog-${state}-${width}x${height}-${scale * 100}.png`) });
    }
    await text(page, 1);
  }
}

test('lists stored builds by product with versions, regions and times, and Open disabled with its reason', async () => {
  const session = await start();
  try {
    const { page, app } = session;
    await expect(page.getByText('Authored test data', { exact: true })).toBeVisible();
    await expect(options(page)).toHaveCount(5);
    const groups = page.getByRole('listbox', { name: 'Builds' }).getByRole('group');
    await expect(groups).toHaveCount(14);
    await expect(groups.nth(2)).toHaveAccessibleName(/^PTR 2 wowxptr/);
    await expect(page.locator('#catalog-group-wow_beta')).toContainText('No history yet');
    const first = options(page).first();
    await expect(first).toContainText('11.9.0.90002');
    await expect(first.locator('.dv-pill.current')).toHaveText('Current');
    await expect(first).toContainText('US, EU, KR, TW');
    await expect(first).toHaveClass(/current/);
    await expect(options(page).nth(1)).not.toHaveClass(/current/);
    await expect(page.locator('.dv-thead')).toHaveText(/Version\s*Regions\s*Last seen/);
    // D476: no summary note; the summary's new products are their own list, collapsed after the reviewed ones.
    await expect(page.getByText(/New in the .+ summary/)).toHaveCount(0);
    const others = page.getByRole('button', { name: /Other WoW products \(not reviewed\)/ });
    await expect(others).toHaveAttribute('aria-expanded', 'false');
    await expect(others).toContainText('1 product, 1 build');
    await others.click();
    const otherList = page.getByRole('listbox', { name: 'Other WoW products, not reviewed' });
    await expect(otherList.getByRole('option')).toHaveCount(1);
    await expect(otherList.getByRole('group')).toHaveAccessibleName(/^wow_example_new/);
    await others.click();
    await expect(otherList).toHaveCount(0);
    await options(page).nth(1).click();
    const inspector = page.getByRole('complementary');
    await expect(inspector.getByRole('heading', { name: '11.9.0.90001' })).toBeVisible();
    await expect(inspector).toContainText('22222222222222222222222222222222');
    await expect(inspector).toContainText('US, EU, KR, TW, CN');
    await expect(inspector).toContainText('First seen');
    const open = inspector.getByRole('button', { name: 'Open build' });
    await expect(open).toBeDisabled();
    await expect(open).toHaveAccessibleDescription('Opening on the new storage comes next. The Old build list still opens builds.');
    await expect(inspector.getByText('Opening on the new storage comes next. The Old build list still opens builds.')).toBeVisible();
    // The old discovery reads are never made.
    expect((await readRequests(session.data)).filter(r => r.method.startsWith('discovery.'))).toEqual([]);
    await capture(app, page, 'list', [1, 2]);
  } finally { await session.close(); }
});

test('refresh shows progress, keeps the selection and focus, and updates the list and the other products', async () => {
  const session = await start();
  try {
    const { page, app, data } = session;
    await expect(options(page)).toHaveCount(5);
    const listbox = page.getByRole('listbox', { name: 'Builds' });
    await listbox.focus();
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    await expect(options(page).nth(1)).toHaveAttribute('aria-selected', 'true');
    await outcome(data, 'slow');
    await page.keyboard.press('F5');
    await expect(page.locator('.dv-statusbar')).toContainText('Checking the version service (2 of 15 products)');
    await expect(refreshButton(page)).toHaveText(/Stop refresh/);
    await expect(listbox).toBeFocused();
    await capture(app, page, 'refreshing');
    await listbox.focus();
    await writeFile(path.join(data, 'release-catalog-refresh'), '');
    await expect(page.locator('.dv-statusbar')).toContainText('Refresh complete. Checked 15 products and the summary.');
    await expect(options(page)).toHaveCount(6);
    await expect(options(page).first()).toContainText('11.9.1.90100');
    await expect(options(page).nth(1)).not.toHaveClass(/current/);
    // The selection is the same build, now second-but-one, and focus stayed on the list.
    await expect(page.locator('[role="option"][aria-selected="true"]')).toHaveAttribute('data-build-id', 'b'.repeat(64));
    await expect(listbox).toBeFocused();
    await expect(page.getByRole('button', { name: /Other WoW products \(not reviewed\)/ })).toContainText('2 products, 1 build');
    await outcome(data, 'partial');
    await refreshButton(page).click();
    await expect(page.getByText('Refresh incomplete')).toBeVisible();
    await expect(page.getByText('Classic Era (wow_classic_era): CDN_OFFLINE')).toBeVisible();
    await capture(app, page, 'partial');
  } finally { await session.close(); }
});

test('offline, busy and stopped refreshes keep the stored list', async () => {
  const session = await start();
  try {
    const { page, app, data } = session;
    await expect(options(page)).toHaveCount(5);
    await options(page).first().click();
    await outcome(data, 'offline');
    await refreshButton(page).click();
    await expect(page.getByText("Couldn't reach the version service. Showing the stored list.")).toBeVisible();
    await expect(options(page)).toHaveCount(5);
    await capture(app, page, 'offline');
    await outcome(data, 'busy');
    await refreshButton(page).click();
    await expect(page.getByText(/Another VibeMiner process, such as vm browse, is writing the build store\. Close it, then refresh\./)).toBeVisible();
    await expect(options(page)).toHaveCount(5);
    await capture(app, page, 'busy');
    await outcome(data, 'slow');
    const button = refreshButton(page);
    await button.click();
    await expect(button).toHaveText(/Stop refresh/);
    await button.click();
    await expect(page.locator('.dv-statusbar')).toContainText('Refresh stopped. Products checked before stopping keep their new results.');
    await expect(button).toHaveText(/Refresh builds/);
    await expect(button).toBeFocused();
    await expect(options(page)).toHaveCount(5);
    await expect(options(page).first()).toHaveAttribute('aria-selected', 'true');
    await expect.poll(async () => (await readRequests(data)).filter(r => r.method === 'catalog.list').length).toBe(2);
    expect((await readRequests(data)).filter(r => r.method === 'request.cancel')).toHaveLength(1);
  } finally { await session.close(); }
});

test('a read-only store says so up front, and refresh works once the other process is gone', async () => {
  const session = await start('readOnly');
  try {
    const { page, app, data } = session;
    await expect(options(page)).toHaveCount(5);
    await expect(page.getByText('Build store in use')).toBeVisible();
    await capture(app, page, 'readonly');
    await refreshButton(page).click();
    await expect(page.locator('.dv-statusbar')).toContainText('the build store is in use');
    await expect(page.getByText('Build store in use')).toBeVisible();
    await outcome(data, 'ok');
    await refreshButton(page).click();
    await expect(page.locator('.dv-statusbar')).toContainText('Refresh complete');
    await expect(page.getByText('Build store in use')).toHaveCount(0);
  } finally { await session.close(); }
});

test('an empty store says how to fill it', async () => {
  const session = await start('empty');
  try {
    const { page, app } = session;
    await expect(page.locator('.dv-empty')).toHaveText('No builds stored yetRefresh asks the version service.');
    await expect(page.getByRole('complementary')).toHaveCount(0);
    await expect(page.locator('.dv-statusbar')).toContainText('Not checked yet');
    await capture(app, page, 'empty', [1, 2]);
    await refreshButton(page).click();
    await expect(options(page)).toHaveCount(6);
  } finally { await session.close(); }
});

test('switches to the old build list for the session and back, with focus on the switch', async () => {
  const session = await start();
  try {
    const { page, data } = session;
    await expect(options(page)).toHaveCount(5);
    await page.getByRole('button', { name: 'Old build list' }).click();
    const back = page.getByRole('button', { name: 'New build list' });
    await expect(back).toBeFocused();
    await expect(page.locator('.build-row')).toHaveCount(2);
    await expect(page.getByRole('searchbox', { name: /Filter version or build number/ })).toBeVisible();
    expect((await readRequests(data)).some(r => r.method === 'discovery.readAll')).toBe(true);
    // Settings stays reachable and comes back to the same list.
    await page.locator('.dv-toolbar').getByRole('button', { name: /Settings/ }).click();
    await page.getByRole('button', { name: 'Back to Discovery' }).click();
    await expect(back).toBeVisible();
    await back.click();
    await expect(page.getByRole('button', { name: 'Old build list' })).toBeFocused();
    await expect(options(page)).toHaveCount(5);
  } finally { await session.close(); }
});

test('keyboard: one tab stop for the list, arrow keys move the selection with a visible focus ring', async () => {
  const session = await start();
  try {
    const { page } = session;
    await expect(options(page)).toHaveCount(5);
    const listbox = page.getByRole('listbox', { name: 'Builds' });
    await refreshButton(page).focus();
    await page.keyboard.press('Tab');
    await expect(listbox).toBeFocused();
    await page.keyboard.press('End');
    await expect(options(page).last()).toHaveAttribute('aria-selected', 'true');
    await expect(options(page).last()).toHaveClass(/active/);
    expect(await options(page).last().evaluate(el => getComputedStyle(el).outlineStyle)).toBe('solid');
    await page.keyboard.press('ArrowUp');
    await expect(options(page).nth(3)).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Home');
    await expect(options(page).first()).toHaveAttribute('aria-selected', 'true');
    await text(page, 2);
    await page.keyboard.press('ArrowDown');
    await expect(options(page).nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(listbox).toBeFocused();
    await expect(page.getByRole('complementary').getByRole('heading', { name: '11.9.0.90001' })).toBeVisible();
  } finally { await session.close(); }
});

test('stacked at 200% text the list shows several rows; short windows share one scroller with jumps', async () => {
  const session = await start();
  try {
    const { page, app } = session;
    await expect(options(page)).toHaveCount(5);
    await options(page).nth(1).click();
    await text(page, 2);
    // 1280x1000 at 200% is stacked with its own panes; 1280x800 is under 30rem tall and shares one scroller.
    await size(app, 1280, 1000);
    await expect(page.locator('.dv-window')).toHaveClass(/dv-compact/);
    await expect(page.locator('.dv-window')).not.toHaveClass(/dv-shared/);
    await expect.poll(() => visibleRows(page)).toBeGreaterThanOrEqual(2);
    await page.screenshot({ path: path.join(root, 'artifacts/electron-tests/build-catalog-stacked-1280x1000-200.png') });
    await size(app, 1280, 800);
    await expect(page.locator('.dv-window')).toHaveClass(/dv-shared/);
    await expect.poll(() => visibleRows(page)).toBeGreaterThanOrEqual(3);
    for (const [width, height, scale] of [[1280, 800, 2], [600, 480, 1.5]] as const) {
      await size(app, width, height); await text(page, scale);
      const label = `${width}x${height} at ${scale * 100}%`;
      await expect(page.locator('.dv-window'), label).toHaveClass(/dv-shared/);
      const jump = page.getByRole('button', { name: 'View selected build' });
      await jump.click();
      const heading = page.getByRole('complementary').getByRole('heading', { name: '11.9.0.90001' });
      await expect(heading, label).toBeFocused();
      await expect(heading, label).toBeInViewport();
      await page.getByRole('button', { name: 'Return to build list' }).click();
      await expect(page.getByRole('listbox', { name: 'Builds' }), label).toBeFocused();
      await expect(options(page).nth(1), label).toBeInViewport();
      const fit = await page.evaluate(() => ({ w: document.documentElement.scrollWidth, h: document.documentElement.scrollHeight, iw: innerWidth, ih: innerHeight }));
      expect(fit.w, label).toBeLessThanOrEqual(fit.iw);
      expect(fit.h, label).toBeLessThanOrEqual(fit.ih);
      await page.screenshot({ path: path.join(root, `artifacts/electron-tests/build-catalog-shared-${width}x${height}-${scale * 100}.png`) });
    }
    // Leaving the shared area with focus on a jump moves focus to what it pointed at.
    await page.getByRole('button', { name: 'View selected build' }).focus();
    await size(app, 1440, 1100); await text(page, 1);
    await expect(page.locator('.dv-window')).not.toHaveClass(/dv-shared/);
    await expect(page.getByRole('button', { name: 'View selected build' })).toHaveCount(0);
    await expect(page.getByRole('complementary').getByRole('heading', { name: '11.9.0.90001' })).toBeFocused();
  } finally { await session.close(); }
});

test('switching lists keeps the last refresh outcome, and says when the switch stopped a refresh', async () => {
  const session = await start();
  try {
    const { page, data } = session;
    await expect(options(page)).toHaveCount(5);
    await options(page).nth(1).click();
    await outcome(data, 'partial');
    await refreshButton(page).click();
    await expect(page.getByText('Refresh incomplete')).toBeVisible();
    await page.getByRole('button', { name: 'Old build list' }).click();
    await page.getByRole('button', { name: 'New build list' }).click();
    await expect(page.getByText('Refresh incomplete')).toBeVisible();
    await expect(page.locator('.dv-statusbar')).toContainText('Refresh finished with problems. Checked 15 products and the summary.');
    await expect(page.locator('[role="option"][aria-selected="true"]')).toHaveAttribute('data-build-id', 'b'.repeat(64));
    await outcome(data, 'slow');
    await refreshButton(page).click();
    await expect(page.locator('.dv-statusbar')).toContainText('Checking the version service (2 of 16 products)');
    await page.getByRole('button', { name: 'Old build list' }).click();
    // The cancel names the running refresh (the old list may cancel its own waits too).
    await expect.poll(async () => { const all = await readRequests(data), refresh = all.filter(r => r.method === 'catalog.refresh').at(-1)!; return all.some(r => r.method === 'request.cancel' && (r.params as { requestId?: string }).requestId === refresh.id); }).toBe(true);
    await page.getByRole('button', { name: 'New build list' }).click();
    await expect(page.locator('.dv-statusbar')).toContainText('Refresh stopped when you switched lists.');
    await expect(refreshButton(page)).toHaveText(/Refresh builds/);
    await expect(options(page)).toHaveCount(6);
  } finally { await session.close(); }
});
