import { test, expect, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';

// #66: at 600x480 and 200% text the Home shell scrolls as a whole (D086). Tables and Files then take their natural height, and the
// Records grid, the Tables list and the Files list keep their own scroller, capped at the shell's visible height. Each shows at least
// three whole rows below its sticky header, fits in the shell's view above the status bar, and every focused title, row or active
// option is wholly visible in each scroller that holds it, not under a sticky header, the status bar or anything else. Wheeling a grid
// past its end scrolls the shell on, and the focused row it scrolled away is never pulled back.
const out = path.join(root, 'artifacts/shell-panes');
const settle = (page: Page) => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));

// Rows wholly visible in a list's scroller below its sticky header, once its first row is scrolled to the top, and whether the whole
// scroller fits in the shell's view above the status bar once it is brought there.
async function rows(page: Page, list: string, row: string, head: string | null) {
  return page.evaluate(({ list, row, head }) => {
    const scroller = [...document.querySelectorAll<HTMLElement>(list)].find(el => el.getClientRects().length > 0)!;
    const app = document.querySelector<HTMLElement>('.dh-app')!, bar = document.querySelector('.dh-statusbar')!;
    const items = [...scroller.querySelectorAll<HTMLElement>(row)], header = head ? scroller.querySelector<HTMLElement>(head) : null;
    const headH = header?.getBoundingClientRect().height ?? 0;
    scroller.scrollTop += items[0].getBoundingClientRect().top - scroller.getBoundingClientRect().top - headH;
    // Bring the scroller's top to the top of the shell's view (its scroll padding is the gap kept there).
    const pad = parseFloat(getComputedStyle(app).scrollPaddingTop) || 0;
    app.scrollTop += scroller.getBoundingClientRect().top - app.getBoundingClientRect().top - pad;
    const view = scroller.getBoundingClientRect(), top = view.top + headH, bottom = view.top + scroller.clientHeight;
    const whole = items.filter(item => { const r = item.getBoundingClientRect(); return r.height > 0 && r.top >= top - 1 && r.bottom <= bottom + 1; }).length;
    return { whole, height: Math.round(view.height), rowHeight: Math.round(items[0].getBoundingClientRect().height), fits: view.top >= -1 && view.bottom <= bar.getBoundingClientRect().top + 1 };
  }, { list, row, head });
}

// The focused control, or a listbox's active option, is wholly visible: inside every ancestor that clips it, below a sticky table or
// list header in its scroller, above the status bar, inside the window, and the control itself is what is drawn there.
async function focusVisible(page: Page) {
  return page.evaluate(() => {
    const focused = document.activeElement as HTMLElement, id = focused.getAttribute('aria-activedescendant');
    const el = (id && document.getElementById(id)) || focused, r = el.getBoundingClientRect();
    let top = 0, bottom = innerHeight;
    for (let up = el.parentElement; up; up = up.parentElement) {
      const cs = getComputedStyle(up);
      if (/auto|scroll|hidden|clip/.test(cs.overflowY)) { const s = up.getBoundingClientRect(); top = Math.max(top, s.top); bottom = Math.min(bottom, s.top + up.clientHeight); }
      const sticky = up.matches('.dh-grid-scroll,.dh-list-scroll') ? up.querySelector<HTMLElement>('thead,.dh-fthead') : null;
      if (sticky && !sticky.contains(el)) top = Math.max(top, sticky.getBoundingClientRect().bottom);
    }
    if (!el.closest('.dh-statusbar')) bottom = Math.min(bottom, document.querySelector('.dh-statusbar')!.getBoundingClientRect().top);
    // Drawn at its start and at its middle: nothing (the floating activity entry, say) lies over it.
    const drawn = [r.left + 6, (r.left + r.right) / 2].every(x => { const hit = document.elementFromPoint(x, (r.top + r.bottom) / 2); return !!hit && (el === hit || el.contains(hit)); });
    return { name: (el.textContent ?? '').trim().slice(0, 30), at: `${Math.round(r.top)}-${Math.round(r.bottom)} in ${Math.round(top)}-${Math.round(bottom)}`, whole: r.height > 0 && r.top >= top - 1 && r.bottom <= bottom + 1, drawn };
  });
}
async function expectFocusVisible(page: Page, label: string) {
  await settle(page);
  const f = await focusVisible(page);
  expect.soft(f.whole, `${label}: "${f.name}" not wholly visible (${f.at})`).toBe(true);
  expect.soft(f.drawn, `${label}: "${f.name}" is covered (${f.at})`).toBe(true);
}
async function shellScrolls(page: Page) {
  expect(await page.evaluate(() => { const app = document.querySelector('.dh-app')!; return app.scrollHeight > app.clientHeight + 1 && document.documentElement.scrollTop === 0; }), 'the shell scrolls, not the window').toBe(true);
}

test('Records, the Tables list and Files keep three whole rows and keep focus in view at 600x480 and 200% text', async () => {
  test.setTimeout(90_000);
  await mkdir(out, { recursive: true });
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await writeFile(path.join(data, 'release-table'), 'release');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    await size(app, 600, 480); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([600, 480]);
    await text(page, 2);
    await page.waitForFunction(() => !!document.querySelector('.desktop-home.dh-compact.dh-shell-scroll'));
    const nav = page.getByRole('navigation', { name: 'Build areas' });

    // Tables list: at least three whole entries, and Tab through them keeps each focused entry visible.
    await nav.getByRole('button', { name: 'Tables', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Tables', exact: true })).toBeFocused();
    await expect(page.locator('.tableitem')).toHaveCount(50);
    await shellScrolls(page);
    const catalog = await rows(page, '.dh-cat-list', '.tableitem', null);
    expect.soft(catalog.whole, `Tables list shows ${catalog.whole} whole entries in ${catalog.height}px (entry ${catalog.rowHeight}px)`).toBeGreaterThanOrEqual(3);
    expect.soft(catalog.fits, 'Tables list fits in the shell view').toBe(true);
    await page.screenshot({ path: path.join(out, 'tables-list-600x480-200.png') });
    await page.locator('.tableitem').first().focus(); await expectFocusVisible(page, 'first Tables entry');
    for (let i = 2; i <= 8; i++) { await page.keyboard.press('Tab'); await expectFocusVisible(page, `Tables entry ${i}`); }
    // The page controls under the list sit on the right, where the floating activity entry is once the header has scrolled away.
    await expect(page.locator('.download-activity-floating')).toBeVisible();
    // The shell's bottom scroll padding clears it exactly, with the same gap as at the top.
    const clear = await page.evaluate(() => { const cs = getComputedStyle(document.querySelector('.dh-app')!), entry = document.querySelector('.download-activity-floating')!.getBoundingClientRect(); return { padding: parseFloat(cs.scrollPaddingBottom), needed: innerHeight - entry.top + parseFloat(cs.scrollPaddingTop) }; });
    expect(Math.abs(clear.padding - clear.needed), `bottom scroll padding ${clear.padding}px, floating entry needs ${clear.needed}px`).toBeLessThanOrEqual(1.5);
    await page.getByRole('button', { name: 'Previous catalogue page', exact: true }).focus(); await page.keyboard.press('Tab');
    await expect(page.getByRole('button', { name: 'Next catalogue page', exact: true })).toBeFocused();
    await expectFocusVisible(page, 'Tables list Next page');
    await page.screenshot({ path: path.join(out, 'tables-next-600x480-200.png') });

    // Enter on a table shows Records and focuses its title, which is visible (it was under the status bar, #66).
    const entry = page.locator('.tableitem').filter({ has: page.getByText('ItemClass', { exact: true }) });
    await entry.focus(); await page.keyboard.press('Enter');
    const title = page.locator('#table-title');
    await expect(title).toHaveText('ItemClass'); await expect(title).toBeFocused();
    const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    await expect(row(0)).toBeVisible();
    await expectFocusVisible(page, 'table title after Enter');
    await page.screenshot({ path: path.join(out, 'records-title-600x480-200.png') });
    const grid = await rows(page, '.dh-data .dh-grid-scroll', 'tbody tr', 'thead');
    expect.soft(grid.whole, `Records grid shows ${grid.whole} whole rows in ${grid.height}px (row ${grid.rowHeight}px)`).toBeGreaterThanOrEqual(3);
    expect.soft(grid.fits, 'Records grid fits in the shell view').toBe(true);
    await page.screenshot({ path: path.join(out, 'records-grid-600x480-200.png') });
    // Record rows by keyboard: each focused row is visible below the sticky header and above the status bar.
    await row(0).click(); await expect(row(0)).toBeFocused(); await expectFocusVisible(page, 'record 0 after click');
    for (let i = 1; i <= 9; i++) { await page.keyboard.press('ArrowDown'); await expect(row(i)).toBeFocused(); await expectFocusVisible(page, `record ${i} after ArrowDown`); }
    await page.keyboard.press('End'); await expect(row(49)).toBeFocused(); await expectFocusVisible(page, 'record 49 after End');
    await page.keyboard.press('Home'); await expect(row(0)).toBeFocused(); await expectFocusVisible(page, 'record 0 after Home');
    // Wheeling the grid past its end carries on into the shell. A focused row scrolled away on purpose is never pulled back.
    const shell = page.locator('.dh-app'), before = await shell.evaluate(el => el.scrollTop);
    // The grid's visible part, between the window top and the status bar, left of the floating activity entry.
    const point = await page.locator('.dh-data .dh-grid-scroll').evaluate(el => { const r = el.getBoundingClientRect(), floor = document.querySelector('.dh-statusbar')!.getBoundingClientRect().top; return { x: r.left + r.width / 4, y: (Math.max(r.top, 0) + Math.min(r.bottom, floor)) / 2 }; });
    await page.mouse.move(point.x, point.y);
    // Recorded before the shell's own handler runs (window, capture phase), so a pull-back it makes can't hide the position.
    await shell.evaluate(el => { const w = window as unknown as { shellTops: number[] }; w.shellTops = []; window.addEventListener('scroll', event => { if (event.target === el) w.shellTops.push(el.scrollTop); }, true); });
    for (let i = 0; i < 12; i++) { await page.mouse.wheel(0, 600); await settle(page); }
    await expect.poll(() => page.locator('.dh-data .dh-grid-scroll').evaluate(el => el.scrollTop + el.clientHeight >= el.scrollHeight - 1), 'grid wheeled to its end').toBe(true);
    const wheeled = await shell.evaluate(el => el.scrollTop);
    await settle(page); await page.waitForTimeout(200); await settle(page);
    expect(wheeled, 'wheeling past the grid end scrolled the shell').toBeGreaterThan(before);
    expect(await shell.evaluate(el => el.scrollTop), `shell pulled back after wheeling (${before} → ${wheeled})`).toBe(wheeled);
    const tops = [before, ...await page.evaluate(() => (window as unknown as { shellTops: number[] }).shellTops)];
    expect(tops.filter((top, i) => i > 0 && top < tops[i - 1] - 1), `shell moved back while wheeling down: ${tops.join(', ')}`).toEqual([]);
    // Focusing the title again brings it back into view.
    await title.focus(); await expectFocusVisible(page, 'table title focused again');

    // Files, with the file list still preparing: the object list under its note, then the prepared file list.
    await nav.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    const pane = page.locator('.dh-files .dh-pane.dh-list');
    await expect(pane.getByText('Object 1', { exact: true })).toBeVisible();
    await shellScrolls(page);
    const objects = await rows(page, '.dh-files .dh-list-scroll', '[role=option]', '.dh-fthead');
    expect.soft(objects.whole, `Files objects show ${objects.whole} whole rows in ${objects.height}px (row ${objects.rowHeight}px)`).toBeGreaterThanOrEqual(3);
    expect.soft(objects.fits, 'Files object list fits in the shell view').toBe(true);
    await page.screenshot({ path: path.join(out, 'files-objects-600x480-200.png') });
    const listbox = pane.getByRole('listbox');
    await listbox.focus(); await expectFocusVisible(page, 'first object');
    for (let i = 1; i <= 7; i++) { await page.keyboard.press('ArrowDown'); await expectFocusVisible(page, `object row ${i + 1} after ArrowDown`); }
    await page.keyboard.press('End'); await expectFocusVisible(page, 'last object after End');
    await page.keyboard.press('Home'); await expectFocusVisible(page, 'first object after Home');

    await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
    const show = page.getByRole('button', { name: 'Show file list' }).first();
    await expect(show).toBeVisible(); await show.click();
    const files = pane.getByRole('listbox', { name: 'Files, one row per FileDataID' });
    await expect(files.getByRole('option').first()).toContainText('800001');
    const list = await rows(page, '.dh-files .dh-list-scroll', '[role=option]', '.dh-fthead');
    expect.soft(list.whole, `file list shows ${list.whole} whole rows in ${list.height}px (row ${list.rowHeight}px)`).toBeGreaterThanOrEqual(3);
    expect.soft(list.fits, 'file list fits in the shell view').toBe(true);
    await page.screenshot({ path: path.join(out, 'files-list-600x480-200.png') });
    await files.focus(); await expectFocusVisible(page, 'first file');
    for (let i = 1; i <= 7; i++) { await page.keyboard.press('ArrowDown'); await expectFocusVisible(page, `file row ${i + 1} after ArrowDown`); }
    await page.keyboard.press('End'); await expectFocusVisible(page, 'last file after End');

    // Nothing scrolls sideways, and the status bar stays last at the window bottom.
    const end = await page.evaluate(() => { const app = document.querySelector<HTMLElement>('.dh-app')!, bar = document.querySelector<HTMLElement>('.dh-statusbar')!; return { sideways: document.documentElement.scrollWidth - document.documentElement.clientWidth, last: app.lastElementChild === bar, bottom: Math.round(bar.getBoundingClientRect().bottom) }; });
    expect(end.sideways).toBeLessThanOrEqual(0); expect(end.last).toBe(true); expect(end.bottom).toBe(480);
    await text(page, 1);
  } finally { await session.close(); }
});

// #78: with the shell scrolling as a whole, one click on a visible Files row selects it, in the object list and in the file
// list, although the list wasn't focused and its active row was scrolled out of view: focusing the list on mousedown must
// not scroll the shell to that row and move another row under the pointer.
test('one click on a visible Files row selects it at 600x480 and 200% text (#78)', async () => {
  test.setTimeout(90_000);
  const session = await launch(true, false, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    await size(app, 600, 480); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([600, 480]);
    await text(page, 2);
    await page.waitForFunction(() => !!document.querySelector('.desktop-home.dh-compact.dh-shell-scroll'));
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Files', exact: true }).click();
    const heading = page.getByRole('heading', { name: 'Files', exact: true });
    await expect(heading).toBeFocused();
    const pane = page.locator('.dh-files .dh-pane.dh-list');
    await expect(pane.getByText('Object 1', { exact: true })).toBeVisible();
    // The list isn't focused, its active row (the first) is scrolled out of its scroller, and the list is in the shell's
    // view. One click on the row now at the top of the scroller selects it.
    async function clickVisibleRow(label: string) {
      await heading.focus(); await settle(page);
      const target = await page.evaluate(() => {
        const scroller = [...document.querySelectorAll<HTMLElement>('.dh-files .dh-list-scroll')].find(el => el.getClientRects().length > 0)!;
        const app = document.querySelector<HTMLElement>('.dh-app')!, options = [...scroller.querySelectorAll<HTMLElement>('[role=option]')];
        const head = scroller.querySelector<HTMLElement>('.dh-fthead')!.getBoundingClientRect().height;
        scroller.scrollTop += options[6].getBoundingClientRect().top - scroller.getBoundingClientRect().top - head;
        app.scrollTop += scroller.getBoundingClientRect().top - app.getBoundingClientRect().top - (parseFloat(getComputedStyle(app).scrollPaddingTop) || 0);
        const r = options[6].getBoundingClientRect(), x = r.left + r.width / 3, y = (r.top + r.bottom) / 2;
        return { x, y, id: options[6].id, hit: options[6].contains(document.elementFromPoint(x, y)), hidden: options[0].getBoundingClientRect().bottom <= scroller.getBoundingClientRect().top + head + 1 };
      });
      expect(target.hit, `${label}: the seventh row is under the pointer`).toBe(true);
      expect(target.hidden, `${label}: the active first row is scrolled out of the list`).toBe(true);
      await page.mouse.click(target.x, target.y);
      await expect(page.locator(`[id="${target.id}"]`), `${label}: one click selects the row`).toHaveAttribute('aria-selected', 'true');
      await expect(pane.getByRole('listbox')).toBeFocused();
    }
    await clickVisibleRow('object list');
    await expect(page.locator('#files-inspector-heading')).toHaveText('Encoded object 7');
    await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
    const show = page.getByRole('button', { name: 'Show file list' }).first();
    await expect(show).toBeVisible(); await show.click();
    const files = pane.getByRole('listbox', { name: 'Files, one row per FileDataID' });
    await expect(files.getByRole('option').first()).toContainText('800001');
    await clickVisibleRow('file list');
    await expect(page.locator('#files-inspector-heading')).not.toHaveText('FileDataID 800001');
    await text(page, 1);
  } finally { await session.close(); }
});

// #83: a right-click or middle-click on a row of an unfocused Files list moves nothing: the shell, the list and its active row stay.
test('right-click and middle-click on a Files row never scroll the shell at 600x480 and 200% text (#83)', async () => {
  test.setTimeout(90_000);
  const session = await launch(true, false, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    await size(app, 600, 480); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([600, 480]);
    await text(page, 2);
    await page.waitForFunction(() => !!document.querySelector('.desktop-home.dh-compact.dh-shell-scroll'));
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Files', exact: true }).click();
    const heading = page.getByRole('heading', { name: 'Files', exact: true });
    await expect(heading).toBeFocused();
    const pane = page.locator('.dh-files .dh-pane.dh-list');
    await expect(pane.getByText('Object 1', { exact: true })).toBeVisible();
    for (const button of ['right', 'middle'] as const) {
      // The list isn't focused and its active row (the first) is scrolled out of its scroller, with the list in the shell's view.
      await heading.focus(); await settle(page);
      const target = await page.evaluate(() => {
        const scroller = [...document.querySelectorAll<HTMLElement>('.dh-files .dh-list-scroll')].find(el => el.getClientRects().length > 0)!;
        const app = document.querySelector<HTMLElement>('.dh-app')!, options = [...scroller.querySelectorAll<HTMLElement>('[role=option]')];
        const head = scroller.querySelector<HTMLElement>('.dh-fthead')!.getBoundingClientRect().height;
        scroller.scrollTop += options[6].getBoundingClientRect().top - scroller.getBoundingClientRect().top - head;
        app.scrollTop += scroller.getBoundingClientRect().top - app.getBoundingClientRect().top - (parseFloat(getComputedStyle(app).scrollPaddingTop) || 0);
        const r = options[6].getBoundingClientRect();
        return { x: r.left + r.width / 3, y: (r.top + r.bottom) / 2, hidden: options[0].getBoundingClientRect().bottom <= scroller.getBoundingClientRect().top + head + 1 };
      });
      expect(target.hidden, `${button}: the active first row is scrolled out of the list`).toBe(true);
      await settle(page);
      const where = () => page.evaluate(() => { const scroller = [...document.querySelectorAll<HTMLElement>('.dh-files .dh-list-scroll')].find(el => el.getClientRects().length > 0)!; return { shell: Math.round(document.querySelector<HTMLElement>('.dh-app')!.scrollTop), list: Math.round(scroller.scrollTop), selected: scroller.querySelector('[role=option][aria-selected=true]')?.id ?? null }; });
      const before = await where();
      await page.mouse.click(target.x, target.y, { button });
      await settle(page); await page.waitForTimeout(150); await settle(page);
      expect.soft(await where(), `${button}-click moved the shell, the list or the selection`).toEqual(before);
    }
    await text(page, 1);
  } finally { await session.close(); }
});

// #87: at 200% text, a list left by Shift+Tab and entered again by Tab shows its active row, in the Files object list and file list.
// Tab focuses the whole listbox, and the active row (here the 14th) must come back into its list and the shell's view with it.
for (const [width, height] of [[600, 480], [1280, 800]] as const) test(`Tab back into a Files list keeps its active row in view at ${width}x${height} and 200% text (#87)`, async () => {
  test.setTimeout(90_000);
  const session = await launch(true, false, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
    await text(page, 2);
    await page.waitForFunction(() => !!document.querySelector('.desktop-home.dh-compact'));
    await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Files', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    const pane = page.locator('.dh-files .dh-pane.dh-list');
    await expect(pane.getByText('Object 1', { exact: true })).toBeVisible();
    async function tabBack(label: string) {
      const listbox = pane.getByRole('listbox'), twelfth = listbox.getByRole('option').nth(11);
      await twelfth.scrollIntoViewIfNeeded(); await twelfth.click();
      await expect(listbox).toBeFocused(); await expectFocusVisible(page, `${label}: row 12 clicked`);
      for (let i = 13; i <= 14; i++) { await page.keyboard.press('ArrowDown'); await expectFocusVisible(page, `${label}: row ${i} after ArrowDown`); }
      await page.keyboard.press('Shift+Tab'); await expect(listbox).not.toBeFocused(); await settle(page);
      // The object list keeps its notices above the rows in the same scroller, so Shift+Tab to the notice's button scrolls the
      // active row out of the list. Tab back in must bring it back, not just show the listbox (#87).
      if (label === 'object list') expect(await page.evaluate(() => { const focused = document.activeElement!, scroller = focused.closest<HTMLElement>('.dh-list-scroll'), row = scroller?.querySelectorAll('[role=option]')[13]; return !!row && row.getBoundingClientRect().top >= scroller!.getBoundingClientRect().top + scroller!.clientHeight; }), 'Shift+Tab went to a control in the list scroller and scrolled row 14 out of it').toBe(true);
      await page.keyboard.press('Tab'); await expect(listbox).toBeFocused();
      await expect(listbox.getByRole('option').nth(13)).toHaveAttribute('aria-selected', 'true');
      await expectFocusVisible(page, `${label}: row 14 after Tab back in`);
    }
    await tabBack('object list');
    await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
    const show = page.getByRole('button', { name: 'Show file list' }).first();
    await expect(show).toBeVisible(); await show.click();
    await expect(pane.getByRole('listbox', { name: 'Files, one row per FileDataID' }).getByRole('option').first()).toContainText('800001');
    await tabBack('file list');
    await text(page, 1);
  } finally { await session.close(); }
});

// #87: the Items and Creatures lists go through the same shell focus handling, so Tab back into them keeps their active row in view too.
for (const [width, height] of [[600, 480], [1280, 800]] as const) test(`Tab back into the Items and Creatures lists keeps the active row in view at ${width}x${height} and 200% text (#87)`, async () => {
  test.setTimeout(90_000);
  const session = await launch(true, false, false, true, false, false, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    for (const flag of ['cached-items', 'cached-creatures']) await writeFile(path.join(data, flag), 'cached');
    await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
    await text(page, 2);
    await page.waitForFunction(() => !!document.querySelector('.desktop-home.dh-compact'));
    const nav = page.getByRole('navigation', { name: 'Build areas' });
    for (const [area, name] of [['Items', 'Items on this page'], ['Creatures', 'Creatures on this page']] as const) {
      await nav.getByRole('button', { name: area, exact: true }).click();
      const listbox = page.getByRole('listbox', { name });
      await expect(listbox.getByRole('option').nth(13)).toBeAttached();
      await listbox.focus(); await page.keyboard.press('Home');
      for (let i = 2; i <= 14; i++) await page.keyboard.press('ArrowDown');
      await expect(listbox.getByRole('option').nth(13)).toHaveAttribute('aria-selected', 'true');
      await expectFocusVisible(page, `${area}: row 14 after ArrowDown`);
      await page.keyboard.press('Shift+Tab'); await expect(listbox).not.toBeFocused(); await settle(page);
      await page.keyboard.press('Tab'); await expect(listbox).toBeFocused();
      await expectFocusVisible(page, `${area}: row 14 after Tab back in`);
    }
    await text(page, 1);
  } finally { await session.close(); }
});

