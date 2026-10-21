import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, readFile, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';
import { activityTask, closeActivity } from './activity-helpers';

async function captures(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/reference-text'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
    for (const scale of [1, 2]) {
      await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width}x${height} ${scale * 100}%`).toBe(false);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`), fullPage: true });
    }
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
}
const classCell = (page: Page, record: string) => page.getByRole('button', { name: `Inspect record ${record}`, exact: true }).locator('xpath=ancestor::tr').locator('td').nth(1);
// Slightly wider text reproduces Linux CI, where the system-ui font wraps the records toolbar and leaves a 209px pane.
const ciFont = '.desktop-home *{letter-spacing:.07em!important}';
async function setCiFont(page: Page, on: boolean) {
  await page.evaluate(([css, enable]) => { document.getElementById('ci-font')?.remove(); if (enable) { const style = document.createElement('style'); style.id = 'ci-font'; style.textContent = css; document.head.append(style); } }, [ciFont, on] as const);
}
async function frames(page: Page) { await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done)))); }
// Every action notice keeps its whole button inside the notice and the window, without its own scroll box; the code stays whole.
async function actionsFit(page: Page, label: string) {
  await frames(page);
  const report = await page.locator('.dh-data:not([hidden])').evaluate(box => [...box.querySelectorAll<HTMLElement>(':scope>.dh-notice.dh-keep')].map(notice => {
    const n = notice.getBoundingClientRect(), buttons = [...notice.querySelectorAll('button')].map(b => b.getBoundingClientRect()), code = notice.querySelector('.reference-code');
    return { name: notice.getAttribute('aria-label'), scroll: notice.scrollHeight - notice.clientHeight, buttons: buttons.map(b => ({ left: b.left - n.left, right: n.right - b.right, out: innerWidth - b.right })),
      code: code ? code.scrollWidth - code.clientWidth : 0 };
  }));
  expect(report.length, label).toBeGreaterThan(0);
  for (const notice of report) {
    expect(notice.scroll, `${label} ${notice.name} scroll`).toBeLessThanOrEqual(1);
    expect(notice.code, `${label} ${notice.name} code`).toBeLessThanOrEqual(1);
    for (const button of notice.buttons) { expect(button.right, `${label} ${notice.name} button`).toBeGreaterThanOrEqual(-1); expect(button.out, `${label} ${notice.name} window`).toBeGreaterThanOrEqual(-1); }
  }
}
// Both fonts at 1280x800 and 200%: the action notices fit, and the header and first row stay whole unless an error notice needs the room.
async function bothFonts(app: ElectronApplication, page: Page, label: string, rowMustFit = true) {
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
  await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([1280, 800]);
  await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
  for (const ci of [false, true]) {
    await setCiFont(page, ci); await actionsFit(page, `${label} ${ci ? 'ci' : 'local'} font`);
    if (rowMustFit) await expect.poll(() => page.locator('.dh-data:not([hidden]) .dh-grid-scroll').evaluate(grid => grid.querySelector('tbody tr')!.getBoundingClientRect().bottom <= grid.getBoundingClientRect().top + grid.clientHeight + 1), { message: `${label} ${ci ? 'ci' : 'local'} font row` }).toBe(true);
  }
  await setCiFont(page, false);
  await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
}

test('Item ClassID shows pending text, prepares ItemClass automatically and fills in without moving focus', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    await writeFile(path.join(data, 'hold-table-b977271e'), 'hold');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible();
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await expect(page.locator('#table-title')).toHaveText('Item');
    await writeFile(path.join(data, 'release-table'), 'release');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();

    // ItemClass isn't prepared: the raw number stays, with a pending hint, and the normal table flow loads it.
    await expect(await activityTask(page, 'ItemClass table')).toContainText('Locating selected table content');
    await closeActivity(page);
    await expect(classCell(page, '1')).toHaveText('3 · …, ItemClass text loading');
    await expect(classCell(page, '1').locator('.dh-ref')).toHaveText('3');
    await expect(classCell(page, '1')).toHaveAttribute('title', /Text not prepared yet · ItemClass.ClassName_lang fills in when ItemClass is ready. ItemClass is loading now./);
    await captures(page, app, 'pending');

    const record = page.getByRole('button', { name: 'Inspect record 0', exact: true }); await record.focus();
    await writeFile(path.join(data, 'release-table-b977271e'), 'release');
    await expect(classCell(page, '1')).toHaveText('3 · Authored clear value 0');
    await expect(classCell(page, '0')).toHaveText('2 · not found in ItemClass');
    await expect(record).toBeFocused();
    // Every declared reference cell has the reference style; only ClassID has text beside it.
    const refCells = page.getByRole('button', { name: 'Inspect record 1', exact: true }).locator('xpath=ancestor::tr').locator('td.dh-refcell');
    await expect(refCells).toHaveCount(8);
    await expect(refCells.locator('.dh-rt')).toHaveCount(1);

    await page.getByRole('button', { name: 'Inspect record 1', exact: true }).click();
    const inspector = page.locator('.dh-tables .dh-inspector');
    await expect(inspector.locator('[data-source-column="1"] .dh-fval')).toHaveText('3');
    await expect(inspector.locator('[data-source-column="1"] .dh-rtext b')).toHaveText('Authored clear value 0');
    await expect(inspector.locator('[data-source-column="1"]')).toContainText('ItemClass.ClassName_lang · row ID 0 · enUS');
    await expect(inspector.locator('[data-source-column="3"]')).toContainText('Material has no reviewed text column; number only.');
    await captures(page, app, 'resolved');

    await page.getByRole('button', { name: 'Next page of records', exact: true }).click();
    await expect(classCell(page, '50')).toHaveText('52 · Authored clear value 49');
    await page.getByRole('textbox', { name: 'Raw record ID (exact match)' }).fill('7');
    await page.getByRole('button', { name: 'Apply record ID', exact: true }).click();
    await expect(classCell(page, '7')).toHaveText('9 · Authored clear value 6');

    const requests = (await readFile(path.join(data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    expect(requests.filter(r => r.method === 'tables.prepare' && r.params.tableId === 'b977271e')).toHaveLength(1);
    const text = requests.filter(r => r.method === 'tables.referenceText');
    expect(text.every(r => r.params.tableId === '50238ec2' && r.params.researchLocale === 'enUS' && !('values' in r.params))).toBe(true);
    expect(text.map(r => [r.params.after ?? '0', r.params.recordId ?? null])).toEqual(expect.arrayContaining([['0', null], ['50', null], ['0', '7']]));
  } finally { await session.close(); }
});

test('a failed text table latches as unavailable and raw values stay usable', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await writeFile(path.join(data, 'hold-table-b977271e'), 'hold');
    await writeFile(path.join(data, 'release-table'), 'release');
    await expect(classCell(page, '1')).toHaveText('3 · …, ItemClass text loading');
    await writeFile(path.join(data, 'table-load-error'), 'encrypted');
    await writeFile(path.join(data, 'release-table-b977271e'), 'release');
    await expect(classCell(page, '1')).toHaveText('3 · not readable (protected)');
    await page.getByRole('button', { name: 'Inspect record 1', exact: true }).click();
    await expect(page.locator('.dh-tables .dh-inspector [data-source-column="1"]')).toContainText('ItemClass is protected and could not be read (BLTE_ENCRYPTED).');
    // Plain heading, code at normal weight, no Retry because it wouldn't help; it fits at 200% in both fonts.
    const notice = page.getByRole('region', { name: 'ItemClass text', exact: true });
    await expect(notice.locator('strong')).toHaveText('ItemClass text not readable (protected)');
    await expect(notice.locator('.reference-code code')).toHaveText('(BLTE_ENCRYPTED)');
    await expect(notice.getByRole('button')).toHaveCount(0);
    await bothFonts(app, page, 'protected');
    await page.getByRole('button', { name: 'Next page of records', exact: true }).click();
    await expect(classCell(page, '50')).toHaveText('52 · not readable (protected)');
    const requests = (await readFile(path.join(data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    expect(requests.filter(r => r.method === 'tables.prepare' && r.params.tableId === 'b977271e')).toHaveLength(1);
  } finally { await session.close(); }
});

test('an offline text load keeps a plain heading, its code and a Retry that fits at 200% in both fonts', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await writeFile(path.join(data, 'hold-table-b977271e'), 'hold');
    await writeFile(path.join(data, 'release-table'), 'release');
    await expect(classCell(page, '1')).toHaveText('3 · …, ItemClass text loading');
    await writeFile(path.join(data, 'table-offline'), 'offline');
    await writeFile(path.join(data, 'release-table-b977271e'), 'release');
    const notice = page.getByRole('region', { name: 'ItemClass text', exact: true });
    await expect(notice.locator('strong')).toHaveText('ItemClass text could not be loaded');
    await expect(notice.locator('.reference-code code')).toHaveText('(NETWORK_UNAVAILABLE)');
    await expect(classCell(page, '1')).toHaveText('3 · text unavailable');
    await bothFonts(app, page, 'offline');
    // Retry from the notice; when it succeeds the notice goes and focus lands on the grid, not the page body.
    await unlink(path.join(data, 'table-offline'));
    const retry = notice.getByRole('button', { name: 'Retry ItemClass text', exact: true });
    await retry.focus(); await page.keyboard.press('Enter');
    await expect(classCell(page, '1')).toHaveText('3 · Authored clear value 0');
    await expect(notice).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Item admitted table rows', exact: true })).toBeFocused();
  } finally { await session.close(); }
});

test('a large text table waits for Load text, then loads at interactive priority and keeps focus usable', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    await writeFile(path.join(data, 'reftext-large'), 'authored size above the automatic threshold');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await writeFile(path.join(data, 'hold-table-b977271e'), 'hold');
    await writeFile(path.join(data, 'release-table'), 'release');
    await expect(classCell(page, '1')).toHaveText('3 · not loaded');
    const notice = page.getByRole('region', { name: 'ItemClass text', exact: true });
    const load = notice.getByRole('button', { name: 'Load text (≈8.0 MB)', exact: true });
    await expect(load).toHaveAttribute('aria-disabled', 'false');
    await captures(page, app, 'on-request');
    const before = (await readFile(path.join(data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    expect(before.filter(r => r.method === 'tables.prepare' && r.params.tableId === 'b977271e')).toHaveLength(0);
    // At 200% text, in both fonts, Load text stays whole without a scroll box and the first row stays whole.
    await bothFonts(app, page, 'load text');
    // Stop keeps the outcome and a scoped Retry in place.
    await load.click();
    const task = await activityTask(page, 'ItemClass table');
    await task.getByRole('button', { name: 'Stop ItemClass table', exact: true }).click();
    await expect(notice.getByText('ItemClass text not loaded · loading stopped', { exact: true })).toBeVisible();
    await expect(classCell(page, '1')).toHaveText('3 · loading stopped');
    await closeActivity(page);
    await bothFonts(app, page, 'stopped');
    const retry = notice.getByRole('button', { name: 'Retry ItemClass text', exact: true });
    await retry.focus(); await page.keyboard.press('Enter');
    const loading = notice.getByRole('button', { name: 'Loading ItemClass text…', exact: true });
    await expect(loading).toBeFocused(); await expect(loading).toHaveAttribute('aria-disabled', 'true');
    await expect(classCell(page, '1')).toHaveText('3 · …, ItemClass text loading');
    await expect(await activityTask(page, 'ItemClass table')).toContainText('Locating selected table content');
    await closeActivity(page);
    await loading.focus();
    await writeFile(path.join(data, 'release-table-b977271e'), 'release');
    await expect(classCell(page, '1')).toHaveText('3 · Authored clear value 0');
    await expect(notice).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Item admitted table rows', exact: true })).toBeFocused();
    const after = (await readFile(path.join(data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    expect(after.filter(r => r.method === 'tables.prepare' && r.params.tableId === 'b977271e')).toHaveLength(2);
  } finally { await session.close(); }
});

test('a failed text definition read stays visible with its retry, and at 200% the notice leaves the header and one full row', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    await writeFile(path.join(data, 'reftext-large'), 'authored size above the automatic threshold');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await expect(page.locator('#table-title')).toHaveText('Item');
    await writeFile(path.join(data, 'table-definition-error'), 'error');
    // Enlarge first, so the rows, the notice and its failure reason all arrive after the text-size change.
    // Slightly wider text reproduces Linux CI, where the system-ui font wraps the records toolbar and leaves a 209px pane.
    await setCiFont(page, true);
    await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
    await writeFile(path.join(data, 'release-table'), 'release');
    const notice = page.getByRole('region', { name: 'ItemClass text', exact: true });
    await expect(notice.locator('.reference-code code')).toHaveText('(DEFINITION_READ_FAILED)');
    await expect(notice.locator('strong')).toHaveText('ItemClass text not loaded');
    await expect(classCell(page, '1')).toHaveText('3 · text unavailable');
    const retry = notice.getByRole('button', { name: 'Retry ItemClass definition', exact: true });
    await expect(retry).toBeInViewport({ ratio: 1 });
    const rowFits = () => page.locator('.dh-data:not([hidden]) .dh-grid-scroll').evaluate(grid => {
      const row = grid.querySelector('tbody tr')!.getBoundingClientRect(), box = grid.getBoundingClientRect();
      return row.bottom <= box.top + grid.clientHeight + 1;
    });
    try { await expect.poll(rowFits).toBe(true); } catch (error) {
      // Report the actual geometry so a platform-only failure says which part ran out of room.
      console.log('Records pane geometry', JSON.stringify(await page.locator('.dh-data:not([hidden])').evaluate(box => ({ box: box.getBoundingClientRect().height, compact: !!box.closest('.dh-compact'),
        children: [...box.children].filter(c => (c as HTMLElement).offsetParent).map(c => ({ cls: c.className, h: c.getBoundingClientRect().height, margin: getComputedStyle(c).marginTop })),
        head: box.querySelector('thead')?.getBoundingClientRect().height, row: box.querySelector('tbody tr')?.getBoundingClientRect().height }))));
      throw error;
    }
    expect(await notice.evaluate(el => el.scrollHeight <= el.clientHeight + 1)).toBe(true);
    // The partial notice has no action, so at this size it steps aside; the Partial fact stays in the records foot.
    await expect(page.locator('.dh-data:not([hidden])>.dh-notice:not(.dh-keep)').filter({ hasText: 'Readable rows available · Partial' })).toBeHidden();
    await expect(page.locator('.dh-tables .dh-foot-count')).toContainText('· enUS · Partial');
    await captures(page, app, 'definition-failed');
    await bothFonts(app, page, 'definition failure');
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    await unlink(path.join(data, 'table-definition-error'));
    await retry.click();
    await expect(notice.getByRole('button', { name: 'Load text (≈8.0 MB)', exact: true })).toHaveAttribute('aria-disabled', 'false');
    await expect(classCell(page, '1')).toHaveText('3 · not loaded');
  } finally { await session.close(); }
});

test('at 200% in both fonts a batch failure and a row error keep their Retry, and Partial is the only notice that steps aside', async () => {
  const session = await launch(true, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([1280, 800]);
    await writeFile(path.join(data, 'reftext-large'), 'authored size above the automatic threshold');
    await writeFile(path.join(data, 'reference-text-error'), 'error');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await writeFile(path.join(data, 'release-table'), 'release');
    const batch = page.getByRole('region', { name: 'Reference text', exact: true });
    await expect(batch.locator('strong')).toHaveText('Reference text unavailable for this page');
    await expect(batch.locator('.reference-code code')).toHaveText('(LOCAL_READ_FAILED)');
    await bothFonts(app, page, 'batch failure');
    // Retry reference text succeeds and the Load text notice takes its place.
    await unlink(path.join(data, 'reference-text-error'));
    const retryText = batch.getByRole('button', { name: 'Retry reference text', exact: true });
    await retryText.focus(); await page.keyboard.press('Enter');
    await expect(batch).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Item admitted table rows', exact: true })).toBeFocused();
    const load = page.getByRole('region', { name: 'ItemClass text', exact: true }).getByRole('button', { name: 'Load text (≈8.0 MB)', exact: true });
    await expect(load).toHaveAttribute('aria-disabled', 'false');
    // A failed page read at the tightest size keeps its error and Retry; it never steps aside like Partial.
    await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; }); await setCiFont(page, true);
    await writeFile(path.join(data, 'table-rows-error'), 'error');
    await page.getByRole('button', { name: 'Next page of records', exact: true }).click();
    const rowError = page.locator('.dh-data:not([hidden])>.dh-notice.dh-error').filter({ hasText: 'LOCAL_READ_FAILED' });
    await expect(rowError).toBeVisible();
    await expect(rowError.getByRole('button', { name: 'Retry table read', exact: true })).toBeInViewport({ ratio: 1 });
    await expect(load).toBeVisible();
    await expect(page.locator('.dh-data:not([hidden])>.dh-notice.dh-yieldable')).toBeHidden();
    await actionsFit(page, 'row error ci font');
    await captures(page, app, 'row-error');
    await setCiFont(page, false);
    await unlink(path.join(data, 'table-rows-error'));
    await rowError.getByRole('button', { name: 'Retry table read', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Inspect record 50', exact: true })).toBeVisible();
    await expect(rowError).toHaveCount(0);
  } finally { await session.close(); }
});
