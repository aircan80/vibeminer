import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';

// Captures at the reference sizes and text scales, with no horizontal page scroll (docs/design/desktop-home, follow=12|5).
async function captures(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/table-follow'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
    for (const scale of [1, 2]) {
      await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width}x${height} ${scale * 100}%`).toBe(false);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`), fullPage: true });
    }
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 1100));
}
const record = (page: Page, id: string) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
const crumbs = (page: Page) => page.locator('.tables-explorer .crumb');
const requests = async (data: string) => (await readFile(path.join(data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));

async function openItem(page: Page, app: ElectronApplication, data: string, catalog = false) {
  if (catalog) await writeFile(path.join(data, 'inventory-workspace'), 'on');
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 1100));
  await writeFile(path.join(data, 'release-table'), 'release');
  await page.locator('.build-row').first().click();
  await page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(data, 'release-open'), 'release');
  await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible();
  await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
  await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
  await expect(page.locator('#table-title')).toHaveText('Item');
  await expect(record(page, '0')).toBeVisible();
}

test('Follow Item.ClassID to the matching ItemClass row, then Back and Forward restore page and selection', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await openItem(page, app, data);
    // Generic authored rows: Item record N has ClassID N+2; ItemClass row M has ClassID M+3.
    await page.getByRole('button', { name: 'Next page of records', exact: true }).click();
    await record(page, '55').click();
    const inspector = page.locator('.dh-tables .dh-inspector');
    const follow = inspector.getByRole('button', { name: 'Show ItemClass rows where ClassID = 57', exact: true });
    await follow.focus(); await page.keyboard.press('Enter');
    await expect(page.locator('#table-title')).toHaveText('ItemClass');
    await expect(page.locator('#table-title')).toBeFocused();
    await expect(record(page, '54')).toHaveAttribute('aria-pressed', 'true');
    await expect(crumbs(page)).toHaveText(['Item', 'ItemClass']);
    await expect(crumbs(page).nth(1)).toHaveAttribute('aria-current', 'step');
    await expect(page.getByRole('group', { name: 'Follow filter' })).toContainText('Followed from Item');
    await expect(page.getByRole('button', { name: 'Clear filter ClassID = 57', exact: true })).toBeVisible();
    await expect(inspector.getByRole('heading', { name: 'Record #54' })).toBeVisible();
    await expect(page.locator('.dh-foot-slot')).toContainText('Rows 1–1 · 1 matching row · ClassID = 57 · enUS');
    await captures(page, app, 'one');

    // ←, → and a crumb keep focus on themselves.
    const back = page.getByRole('button', { name: 'Back through definitions', exact: true });
    await back.focus(); await page.keyboard.press('Enter');
    await expect(page.locator('#table-title')).toHaveText('Item');
    await expect(back).toBeFocused();
    await expect(record(page, '55')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.dh-foot-slot')).toContainText('Rows 51–60');
    const forward = page.getByRole('button', { name: 'Forward through definitions', exact: true });
    await forward.focus(); await page.keyboard.press('Enter');
    await expect(record(page, '54')).toHaveAttribute('aria-pressed', 'true');
    await expect(forward).toBeFocused();
    await crumbs(page).nth(0).focus(); await page.keyboard.press('Enter');
    await expect(record(page, '55')).toHaveAttribute('aria-pressed', 'true');
    await expect(crumbs(page).nth(0)).toBeFocused();
    await crumbs(page).nth(1).click();

    // A miss in the complete ItemClass table says so; it isn't scoped as partial.
    await page.getByRole('button', { name: 'Back through definitions', exact: true }).click();
    await page.getByRole('button', { name: 'First page of records', exact: true }).click();
    await record(page, '0').click();
    await inspector.getByRole('button', { name: 'Show ItemClass rows where ClassID = 2', exact: true }).click();
    await expect(page.getByText('No admitted ItemClass row has ClassID = 2', { exact: true })).toBeVisible();
    await expect(page.getByText('Complete admitted section scope for enUS. Nothing else was searched or fetched.')).toBeVisible();
    await expect(crumbs(page)).toHaveText(['Item', 'ItemClass']);
    await captures(page, app, 'none');

    const follows = (await requests(data)).filter(r => r.method === 'tables.follow');
    expect(follows.map(r => [r.params.tableId, r.params.columnIndex, r.params.value, r.params.targetTableId])).toEqual(expect.arrayContaining([['50238ec2', 1, '57', 'b977271e'], ['50238ec2', 1, '2', 'b977271e']]));
    expect(follows.every(r => r.params.researchLocale === 'enUS' && r.params.limit === 50)).toBe(true);
  } finally { await session.close(); }
});

test('A grid cell follows by mouse, a running lookup is cancelled by Back, and a FileDataID opens Files with a way back', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await openItem(page, app, data);
    const classCell = record(page, '3').locator('xpath=ancestor::tr').locator('td').nth(1);
    await expect(classCell).toHaveClass(/dh-follow/);
    await expect(classCell).toHaveAttribute('title', /^Show ItemClass rows where ClassID = 5/);
    await writeFile(path.join(data, 'hold-follow'), 'hold');
    await classCell.click();
    await expect(page.locator('#table-title')).toHaveText('ItemClass');
    await expect.poll(async () => (await requests(data)).some(r => r.method === 'tables.follow' && r.params.value === '5')).toBe(true);
    await page.getByRole('button', { name: 'Back through definitions', exact: true }).click();
    await expect(record(page, '3')).toHaveAttribute('aria-pressed', 'true');
    const held = (await requests(data)).find(r => r.method === 'tables.follow' && r.params.value === '5')!;
    await expect.poll(async () => (await requests(data)).some(r => r.method === 'request.cancel' && r.params.requestId === held.id)).toBe(true);

    // IconFileDataID declares FileData.ID: Files opens with the FileDataID and the record to return to.
    await record(page, '1').click();
    const files = page.locator('.dh-tables .dh-inspector').getByRole('button', { name: 'Find FileDataID 9 in Files', exact: true });
    await files.click();
    // This authored build has no file catalog, so there is no FileDataID search; Files says so and keeps the way back.
    await expect(page.getByText('FileDataID 9 can’t be looked up yet', { exact: true })).toBeVisible();
    await expect(page.getByText(/Followed from Item record 1\. The file catalog isn’t prepared/)).toBeVisible();
    await captures(page, app, 'files');
    await page.getByRole('button', { name: '← Return to Item record 1', exact: true }).click();
    await expect(page.locator('#table-title')).toHaveText('Item');
    await expect(record(page, '1')).toHaveAttribute('aria-pressed', 'true');
    await expect(files).toBeFocused();
  } finally { await session.close(); }
});

test('At 200% Follow from the Selected record pane shows the Records pane with its title focused', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await openItem(page, app, data);
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
    await record(page, '5').click();
    await page.getByRole('group', { name: 'Show pane' }).getByRole('button', { name: 'Selected record', exact: true }).click();
    const follow = page.locator('.dh-tables .dh-inspector').getByRole('button', { name: 'Show ItemClass rows where ClassID = 7', exact: true });
    await follow.focus(); await page.keyboard.press('Enter');
    await expect(page.locator('#table-title')).toBeFocused();
    await expect(page.locator('.dh-tables')).toHaveAttribute('data-pane', 'records');
    await expect(record(page, '4')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Clear filter ClassID = 7', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1)).toBe(false);
    await page.getByRole('button', { name: 'Clear filter ClassID = 7', exact: true }).click();
    await expect(page.getByRole('textbox', { name: 'Raw record ID (exact match)' })).toBeFocused();
  } finally { await session.close(); }
});

test('With a prepared file catalog a FileDataID opens Files searching that FileDataID, and Return goes back to the record', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await openItem(page, app, data, true);
    await record(page, '1').click();
    const files = page.locator('.dh-tables .dh-inspector').getByRole('button', { name: 'Find FileDataID 9 in Files', exact: true });
    await files.click();
    await expect(page.getByRole('searchbox', { name: 'Search by FileDataID or content key' })).toHaveValue('9');
    await expect.poll(async () => (await requests(data)).some(r => r.method === 'inventory.files' && r.params.fileDataId === '9' && !('family' in r.params) && !('localeFlags' in r.params))).toBe(true);
    await expect(page.getByText(/can’t be looked up yet/)).toHaveCount(0);
    // Files had not been opened: no unfiltered page was read first, so no unrelated row is selected.
    expect((await requests(data)).filter(r => r.method === 'inventory.files').every(r => r.params.fileDataId === '9')).toBe(true);
    await expect(page.getByRole('heading', { name: 'No file selected' })).toBeVisible();
    await captures(page, app, 'files-catalog');
    await page.getByRole('button', { name: '← Return to Item record 1', exact: true }).click();
    await expect(page.locator('#table-title')).toHaveText('Item');
    await expect(files).toBeFocused();
  } finally { await session.close(); }
});

test('With the file catalog prepared after Open (#35) a FileDataID opens Files searching that FileDataID in it', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true, true);
  try {
    const { page, app, data } = session;
    await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
    await openItem(page, app, data);
    await expect(page.locator('section[aria-labelledby="home-catalog"]')).toContainText('Ready to browse');
    await record(page, '1').click();
    const files = page.locator('.dh-tables .dh-inspector').getByRole('button', { name: 'Find FileDataID 9 in Files', exact: true });
    await files.click();
    await expect(page.getByRole('searchbox', { name: 'Search by FileDataID or content key' })).toHaveValue('9');
    // The search runs against the build's file catalog generation, not a legacy full catalog.
    await expect.poll(async () => (await requests(data)).some(r => r.method === 'inventory.files' && r.params.fileDataId === '9' && r.params.generation === '41')).toBe(true);
    // Every read of the list is the filtered one: no unfiltered first page is read and then replaced.
    expect((await requests(data)).filter(r => r.method === 'inventory.files').every(r => r.params.fileDataId === '9' && r.params.generation === '41')).toBe(true);
    await expect(page.getByText(/can’t be looked up yet/)).toHaveCount(0);
    await page.getByRole('button', { name: '← Return to Item record 1', exact: true }).click();
    await expect(page.locator('#table-title')).toHaveText('Item');
    await expect(files).toBeFocused();
  } finally { await session.close(); }
});

test('A FileDataID followed while the file list is preparing says it can’t be looked up yet, then that it can once ready', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true, true);
  try {
    const { page, app, data } = session;
    await openItem(page, app, data);
    await record(page, '1').click();
    await page.locator('.dh-tables .dh-inspector').getByRole('button', { name: 'Find FileDataID 9 in Files', exact: true }).click();
    const notice = page.locator('.file-target');
    await expect(notice).toContainText('FileDataID 9 can’t be looked up yet');
    await expect(page.getByRole('note', { name: 'Preparing file list…' })).toBeVisible();
    await captures(page, app, 'file-preparing');
    // Sealed while Files shows the notice: the notice says the FileDataID can be looked up now, and Show file list opens it.
    await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
    await expect(notice).toContainText('FileDataID 9 can be looked up now');
    await expect(page.getByText(/can’t be looked up yet/)).toHaveCount(0);
    await page.locator('.dh-pane-foot').getByRole('button', { name: 'Show file list' }).click();
    await expect(page.getByRole('searchbox', { name: 'Search by FileDataID or content key' })).toHaveValue('9');
    await expect.poll(async () => (await requests(data)).some(r => r.method === 'inventory.files')).toBe(true);
    expect((await requests(data)).filter(r => r.method === 'inventory.files').every(r => r.params.fileDataId === '9' && r.params.generation === '41')).toBe(true);
    await expect(notice).toHaveCount(0);
  } finally { await session.close(); }
});

test('Following into a table that can’t be loaded keeps the chip and says no search ran', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await openItem(page, app, data);
    // Item record N has Material N+4; the authored engine doesn't admit Material, so its Data needs attention.
    await record(page, '1').click();
    await page.locator('.dh-tables .dh-inspector').getByRole('button', { name: 'Show Material rows where ID = 5', exact: true }).click();
    await expect(page.locator('#table-title')).toHaveText('Material');
    await expect(page.getByRole('button', { name: 'Clear filter ID = 5', exact: true })).toBeVisible();
    await expect(page.getByText('No search ran', { exact: true })).toBeVisible();
    await expect(page.getByText(/^Material rows aren’t available, so nothing is claimed about ID = 5\./)).toBeVisible();
    await captures(page, app, 'unavailable');
    expect((await requests(data)).some(r => r.method === 'tables.follow')).toBe(false);
  } finally { await session.close(); }
});
