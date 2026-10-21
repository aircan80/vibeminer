import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';

// Desktop Home shell (docs/design/desktop-home): fixed window, compact header, grouped areas, panes that scroll on their own.
async function opened(...options: Parameters<typeof launch>) {
  const session = await launch(...options);
  await size(session.app, 1280, 800);
  await session.page.locator('.build-row').first().click();
  await session.page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(session.data, 'release-open'), 'release');
  await expect(session.page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
  return session;
}
async function size(app: ElectronApplication, width: number, height: number) { await app.evaluate(({ BrowserWindow }, s) => BrowserWindow.getAllWindows()[0].setContentSize(s.width, s.height), { width, height }); }
async function text(page: Page, scale: number) { await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale); }
// The window itself never scrolls; only panes do.
async function fixedWindow(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/desktop-home'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await size(app, width, height);
    for (const scale of [1, 2]) {
      await text(page, scale);
      await expect.poll(() => page.evaluate(() => document.querySelector('.desktop-home')?.classList.contains('dh-compact'))).toBe(scale === 2);
      const layout = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth, sh: document.documentElement.scrollHeight, ch: document.documentElement.clientHeight, y: window.scrollY, status: document.querySelector('.dh-statusbar')!.getBoundingClientRect().bottom }));
      const label = `${state} ${width}x${height} ${scale * 100}%`;
      expect(layout.sw, `${label} horizontal`).toBeLessThanOrEqual(layout.cw);
      expect(layout.sh, `${label} vertical`).toBeLessThanOrEqual(layout.ch);
      expect(layout.y, `${label} window scroll`).toBe(0);
      expect(Math.round(layout.status), `${label} status bar at the window bottom`).toBe(height);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`) });
    }
  }
  await text(page, 1); await size(app, 1280, 800);
}

test('Home, Tables and Files keep a fixed window at 1280x800 and 1440x1100, 100% and 200% text', async () => {
  const session = await opened(true);
  try {
    const { page, app } = session, areas = page.getByRole('navigation', { name: 'Build areas' });
    await fixedWindow(page, app, 'home');
    await areas.getByRole('button', { name: 'Tables', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Tables', exact: true })).toBeFocused();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Item', exact: true })).toBeFocused();
    await fixedWindow(page, app, 'tables');
    await areas.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await fixedWindow(page, app, 'files');
  } finally { await session.close(); }
});

// Files per FileDataID (#20) needs a sealed research catalog: the authored engine opens the legacy inventory profile.
async function openedCatalog() {
  const session = await launch(true);
  await size(session.app, 1280, 800);
  await writeFile(path.join(session.data, 'inventory-workspace'), '1');
  await session.page.locator('.build-row').first().click();
  await session.page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(session.data, 'release-open'), 'release');
  await expect(session.page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
  return session;
}

test('Files lists one row per FileDataID, filters single memberships and marks matching memberships in the inspector', async () => {
  const session = await openedCatalog();
  try {
    const { page, app } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await nav.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    const list = page.getByRole('listbox', { name: 'Files, one row per FileDataID' });
    await expect(list.getByRole('option')).toHaveCount(42);
    await expect(list.getByRole('option').first()).toContainText('800001');
    await expect(list.getByRole('option').first()).toContainText('Name not known');
    await expect(list.getByRole('option').filter({ hasText: 'authored/install/launcher.bin' })).toContainText('2');
    await expect(page.locator('.dh-files .dh-pane-foot')).toContainText('42 files on this page · 42 files in this catalog');
    await expect(page.locator('.dh-statusbar')).toContainText('Catalog generation');
    // One Tab stop, arrow keys move the selection shown in the inspector.
    await list.focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown');
    const inspector = page.getByRole('complementary', { name: 'FileDataID 800004' });
    await expect(inspector.getByText('Membership 1207004')).toBeVisible(); await expect(inspector.getByText('Membership 1207005')).toBeVisible();
    await expect(inspector).toContainText('2 in ROOT · 2 different content keys'); await expect(inspector).toContainText('raw parts 0x00000000 · 0x10000000 · 0');
    await expect(list).toBeFocused();
    await expect(list.getByRole('option').nth(30)).toContainText('+1 unresolved');
    // A locale filter keeps every membership of a file but marks the one that matched.
    await page.getByRole('combobox', { name: 'Locale flag' }).selectOption({ label: 'enGB · 0x00000200' });
    await expect(list.getByRole('option')).toHaveCount(5); await expect(page.locator('.dh-files .dh-pane-foot')).toContainText('5 matching files on this page');
    await expect(page.getByRole('complementary', { name: 'FileDataID 800004' })).toContainText('1 matches the filters');
    await expect(page.locator('.dh-member.dh-current')).toHaveCount(1); await expect(page.locator('.dh-member.dh-current')).toContainText('Matches the filters');
    // Search by FileDataID names the search when nothing matches; Clear filters returns to the search box.
    const search = page.getByRole('searchbox', { name: 'Search by FileDataID or content key' });
    await search.fill('99999999'); await search.press('Enter');
    await expect(page.getByText("No file in this build's prepared catalog has FileDataID 99999999 with these filters (1 active).")).toBeVisible();
    await page.getByRole('button', { name: 'Clear filters' }).click(); await expect(search).toBeFocused();
    await expect(list.getByRole('option')).toHaveCount(42);
    await page.getByRole('group', { name: 'Source family' }).getByRole('button', { name: 'Install' }).click();
    await expect(list.getByRole('option')).toHaveCount(2); await expect(page.locator('.dh-files .dh-pane-foot')).toContainText('2 files in this install manifest');
    await page.getByRole('group', { name: 'Source family' }).getByRole('button', { name: 'All', exact: true }).click();
    await page.getByRole('checkbox', { name: 'Stored locally' }).check(); await expect(list.getByRole('option').first()).toContainText('800001');
    await page.getByRole('checkbox', { name: 'Stored locally' }).uncheck(); await expect(list.getByRole('option')).toHaveCount(42);
    // A picked file that still matches stays selected; one the filters exclude stays in the inspector, labelled.
    await list.getByRole('option').nth(21).click(); await expect(list.getByRole('option').nth(21)).toHaveAttribute('aria-selected', 'true');
    await page.getByRole('combobox', { name: 'Locale flag' }).selectOption({ label: 'enGB · 0x00000200' });
    await expect(list.getByRole('option')).toHaveCount(5);
    await expect(list.getByRole('option').filter({ hasText: '800022' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('complementary', { name: 'FileDataID 800022' })).toContainText('1 matches the filters');
    await page.getByRole('combobox', { name: 'Content flags' }).selectOption({ label: 'Includes 0x08000000' });
    await expect(page.getByRole('complementary', { name: 'FileDataID 800022' }).getByText('Selected file is outside these results')).toBeVisible();
    await expect(list.locator('[aria-selected="true"]')).toHaveCount(0);
    await page.getByRole('combobox', { name: 'Locale flag' }).selectOption({ label: 'Any locale' }); await page.getByRole('combobox', { name: 'Content flags' }).selectOption({ label: 'Any content flags' });
    await expect(list.getByRole('option')).toHaveCount(42);
    // The segmented group clips overflow, so its focus ring is drawn inside the button.
    const all = page.getByRole('group', { name: 'Source family' }).getByRole('button', { name: 'All', exact: true });
    await page.getByRole('checkbox', { name: 'Stored locally' }).focus(); await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Shift+Tab');
    await expect(all).toBeFocused();
    expect(await all.evaluate(el => { const style = getComputedStyle(el); return [style.outlineStyle, style.outlineWidth, style.outlineOffset]; })).toEqual(['solid', '3px', '-3px']);
    await fixedWindow(page, app, 'files-catalog');
  } finally { await session.close(); }
});

test('at 200% text the Files filters collapse behind a Filters disclosure and the search stays visible', async () => {
  const session = await openedCatalog();
  try {
    const { page } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await nav.getByRole('button', { name: 'Files', exact: true }).click();
    await text(page, 2); await expect(page.locator('.desktop-home')).toHaveClass(/dh-compact/);
    const search = page.getByRole('searchbox', { name: 'Search by FileDataID or content key' }), toggle = page.getByRole('button', { name: /^Filters/ });
    const locale = page.getByRole('combobox', { name: 'Locale flag' });
    await expect(search).toBeVisible(); await expect(toggle).toBeVisible(); await expect(toggle).toHaveAttribute('aria-expanded', 'false'); await expect(locale).toBeHidden();
    await toggle.focus(); await page.keyboard.press('Enter');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true'); await expect(locale).toBeVisible();
    await page.keyboard.press('Tab'); await expect(locale).toBeFocused();
    await locale.selectOption({ label: 'enGB · 0x00000200' }); await page.getByRole('checkbox', { name: 'Stored locally' }).check();
    await expect(toggle).toHaveAccessibleName(/^Filters ?, 2 active$/);
    await page.screenshot({ path: path.join(root, 'artifacts/desktop-home/files-filters-open-1280x800-200.png') });
    await page.getByRole('checkbox', { name: 'Stored locally' }).focus(); await page.keyboard.press('Escape');
    await expect(toggle).toBeFocused(); await expect(toggle).toHaveAttribute('aria-expanded', 'false'); await expect(locale).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollHeight <= document.documentElement.clientHeight && document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    // One pane at a time; Ctrl+F returns to the list's search.
    await page.getByRole('group', { name: 'Show pane' }).filter({ visible: true }).getByRole('button', { name: 'Selected file' }).click();
    await expect(page.locator('.dh-files .dh-inspector')).toBeVisible(); await expect(page.locator('.dh-files .dh-list')).toBeHidden();
    await page.keyboard.press('Control+f'); await expect(search).toBeFocused(); await expect(page.locator('.dh-files .dh-list')).toBeVisible();
    await text(page, 1);
    await expect(toggle).toBeHidden(); await expect(locale).toBeVisible();
  } finally { await session.close(); }
});

test('the header is one row with exact identity, a labelled Download mode and a primary Choose build', async () => {
  const session = await opened(true);
  try {
    const { page } = session, header = page.locator('.dh-toolbar');
    await expect(header.getByRole('button', { name: /^Product Retail wow/ })).toBeVisible();
    await expect(header.getByRole('button', { name: /^Build 12\./ })).toBeVisible();
    await expect(header.locator('.dh-idf').filter({ hasText: 'Source' })).toContainText('US');
    await expect(header.locator('.dh-idf').filter({ hasText: 'Locale' })).toContainText('English · enUS');
    await expect(header.getByRole('button', { name: /^Status / })).toBeVisible();
    await expect(header.getByText('Download mode', { exact: true })).toBeVisible();
    const choose = header.getByRole('button', { name: '← Choose build' });
    await expect(choose).toHaveClass(/dh-primary/);
    await expect(header.getByRole('button', { name: 'Source details' })).toHaveCount(0);
    const rows = await header.evaluate(el => { const fields = el.querySelector('.dh-idfields')!.getBoundingClientRect(), action = el.querySelector('.dh-hgroup')!.getBoundingClientRect(), nav = el.querySelector('.dh-areas')!.getBoundingClientRect(); return { sameRow: Math.abs(fields.top + fields.height / 2 - (action.top + action.height / 2)) < 12, navBelow: nav.top >= Math.max(fields.bottom, action.bottom) - 1, height: el.getBoundingClientRect().height }; });
    expect(rows.sameRow).toBe(true); expect(rows.navBelow).toBe(true);
    // Idle: no activity entry, badge or floating button on Home, as on Discovery.
    await expect(page.locator('.dv-chip')).toHaveCount(0); await expect(page.locator('.download-activity-floating')).toHaveCount(0);
    // Enlarged text: two rows, Source and Locale move to the status bar, identity stays named.
    await text(page, 2);
    await expect(page.locator('.desktop-home')).toHaveClass(/dh-compact/);
    await expect(header.locator('.dh-idf.dh-moved').first()).toBeHidden();
    await expect(page.locator('.dh-statusbar')).toContainText('· US · enUS');
    await expect(header.getByRole('button', { name: /^Product Retail wow/ })).toBeVisible();
    const compact = await header.evaluate(el => { const fields = el.querySelector('.dh-idfields')!.getBoundingClientRect(), action = el.querySelector('.dh-hgroup')!.getBoundingClientRect(); return action.top >= fields.bottom - 1; });
    expect(compact).toBe(true);
    await text(page, 1);
    await choose.click(); await expect(page.getByRole('button', { name: 'Open build', exact: true })).toBeFocused();
  } finally { await session.close(); }
});

test('areas are grouped in order and Items and Creatures are hidden when unavailable', async () => {
  const plain = await opened(true);
  try {
    const nav = plain.page.getByRole('navigation', { name: 'Build areas' });
    await expect(nav.getByRole('button')).toHaveText(['Home', 'Tables', 'Files']);
    await expect(nav.getByRole('group', { name: 'Source data' }).getByRole('button')).toHaveText(['Tables', 'Files']);
    await expect(nav.getByRole('group', { name: 'Game data' })).toHaveCount(0);
    await expect(nav.locator('.dh-area-sep')).toHaveCount(0);
    await expect(plain.page.locator('.dh-side')).toContainText('Not provided by this engine');
  } finally { await plain.close(); }
  const game = await opened(true, false, false, true, false, false, true);
  try {
    const nav = game.page.getByRole('navigation', { name: 'Build areas' });
    await expect(nav.getByRole('button')).toHaveText(['Home', 'Tables', 'Files', 'Items', 'Creatures']);
    await expect(nav.getByRole('group', { name: 'Game data' }).getByRole('button')).toHaveText(['Items', 'Creatures']);
    await expect(nav.locator('.dh-area-sep')).toHaveCount(1);
    await expect(nav.getByRole('button', { name: 'Home' })).toHaveAttribute('aria-current', 'page');
    await expect(game.page.locator('.dh-side')).toContainText('Items, Creatures');
  } finally { await game.close(); }
});

test('Browse in Files returns focus to its button; the area bar focuses the destination heading', async () => {
  const session = await opened(true);
  try {
    const { page } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    const browse = page.getByRole('button', { name: 'Browse in Files' }); await browse.focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    await expect(nav.getByRole('button', { name: 'Files' })).toHaveAttribute('aria-current', 'page');
    const back = page.getByRole('button', { name: '← Return to build overview' }); await back.focus(); await page.keyboard.press('Enter');
    await expect(browse).toBeFocused();
    await nav.getByRole('button', { name: 'Files' }).click();
    await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    await expect(page.getByRole('button', { name: '← Return to build overview' })).toHaveCount(0);
    await nav.getByRole('button', { name: 'Home' }).click();
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    // Files list is a listbox: one Tab stop, arrow keys move the selection shown in the inspector.
    await nav.getByRole('button', { name: 'Files' }).click(); await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    const note = page.getByRole('note', { name: 'File catalog not prepared' });
    await expect(note).toContainText('see which are stored locally'); await expect(note).toContainText('FileDataID (ROOT) or install name');
    // 200% text on a 1280x800 window: the one-line form keeps object rows in view under the note.
    await text(page, 2); await expect(note).toContainText('No FileDataID or CKey search yet');
    const fits = await page.evaluate(() => { const scroll = document.querySelector('.dh-files .dh-list-scroll')!.getBoundingClientRect(), row = document.querySelector('.dh-files [role=option]')!.getBoundingClientRect(); return row.bottom <= scroll.bottom; });
    expect(fits).toBe(true); await text(page, 1);
    const list = page.getByRole('listbox'); await list.focus(); await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('heading', { name: 'Encoded object 2', exact: true })).toBeVisible(); await expect(list).toBeFocused();
  } finally { await session.close(); }
});

test('Tables tabs, focus rules and the 200% one-pane switcher', async () => {
  const session = await opened(true);
  try {
    const { page } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await nav.getByRole('button', { name: 'Tables' }).click();
    const item = page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }); await item.focus(); await page.keyboard.press('Enter');
    const title = page.getByRole('heading', { level: 2, name: 'Item', exact: true }); await expect(title).toBeFocused();
    const tabs = page.getByRole('group', { name: 'Table definition view' });
    await expect(tabs.getByRole('button')).toHaveText(['Data', 'Structure', 'Relationships']);
    const structure = tabs.getByRole('button', { name: 'Structure' }); await structure.focus(); await page.keyboard.press('Enter');
    await expect(structure).toHaveAttribute('aria-pressed', 'true'); await expect(structure).toBeFocused();
    await expect(page.getByText('DBD structure', { exact: true })).toBeVisible();
    const field = page.getByRole('button', { name: 'Inspect field ClassID' }); await field.click();
    await expect(page.getByRole('heading', { name: 'Item.ClassID', exact: true })).toBeVisible(); await expect(field).toBeFocused();
    await tabs.getByRole('button', { name: 'Relationships' }).click();
    await expect(page.getByText('Outgoing · declared by Item', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open ItemClass.ClassID definition' }).first()).toBeVisible();
    await expect(page.locator('.dh-relcard.dh-current')).toContainText('Item.ClassID');
    await tabs.getByRole('button', { name: 'Data' }).click();
    await expect(page.getByRole('heading', { name: 'Table data', exact: true })).toBeVisible();
    await expect(page.getByLabel('Raw record ID (exact match)')).toBeVisible();
    // 200%: one pane at a time; picking a table shows Records and focuses its title.
    await text(page, 2);
    const panes = page.getByRole('group', { name: 'Show pane' }).filter({ visible: true });
    await expect(panes.getByRole('button')).toHaveText(['Tables list', 'Records', /Inspector|Selected record/]);
    await expect(page.locator('.dh-records')).toBeVisible(); await expect(page.locator('.dh-catalog')).toBeHidden(); await expect(page.locator('.dh-tables .dh-inspector')).toBeHidden();
    const list = panes.getByRole('button', { name: 'Tables list' }); await list.focus(); await page.keyboard.press('Enter');
    await expect(list).toBeFocused(); await expect(list).toHaveAttribute('aria-pressed', 'true'); await expect(page.locator('.dh-catalog')).toBeVisible(); await expect(page.locator('.dh-records')).toBeHidden();
    await page.locator('.tableitem').filter({ has: page.getByText('ItemClass', { exact: true }) }).click();
    await expect(page.locator('.dh-records')).toBeVisible(); await expect(page.getByRole('heading', { level: 2, name: 'ItemClass', exact: true })).toBeFocused();
    await panes.getByRole('button', { name: /Inspector|Selected record/ }).click(); await expect(page.locator('.dh-tables .dh-inspector')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollHeight <= document.documentElement.clientHeight)).toBe(true);
    // Ctrl+F returns to the Tables list search.
    await page.keyboard.press('Control+f'); await expect(page.getByRole('searchbox', { name: 'Search definitions' })).toBeFocused();
    await text(page, 1);
  } finally { await session.close(); }
});
