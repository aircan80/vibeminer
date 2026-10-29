import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch } from './helpers';

// Desktop Home review regressions: pane scroll returns, inert coverage, enlarged-text Tables focus and pinned records chrome.
// A resize reaches the renderer before the frame that lays it out, and the notice clamp re-fits from a ResizeObserver in that frame.
// So wait for the new viewport, then two frames: the second starts only after the first frame's observers have run.
async function size({ app, page }: { app: ElectronApplication; page: Page }, width: number, height: number) {
  await app.evaluate(({ BrowserWindow }, s) => BrowserWindow.getAllWindows()[0].setContentSize(s.width, s.height), { width, height });
  await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
}
async function text(page: Page, scale: number) { await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale); }
async function opened(...options: Parameters<typeof launch>) {
  const session = await launch(...options);
  await size(session, 1280, 800);
  await session.page.locator('.build-row').first().click();
  await session.page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(session.data, 'release-open'), 'release');
  await expect(session.page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
  return session;
}
const paneTop = (page: Page) => page.locator('.dh-area-scroll:visible').evaluate(el => el.scrollTop);

// Areas scroll inside their own pane, so returns restore that pane, never the window.
test('Items and Creatures restore their own pane scroll after a source return and an area round trip', async () => {
  const session = await opened(true, false, false, true, false, false, true);
  try {
    const { page, data } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await writeFile(path.join(data, 'cached-items'), 'cached'); await writeFile(path.join(data, 'cached-creatures'), 'cached');
    await nav.getByRole('button', { name: 'Items', exact: true }).click();
    // Items keeps two scrollers of its own (docs/design/items-view): the list and the item detail.
    const row = page.locator('#iv-opt-40'); await row.scrollIntoViewIfNeeded(); await row.click();
    const source = page.getByRole('region', { name: 'Inventory and price', exact: true }).getByRole('button', { name: /^ItemSparse\.SellPrice/ }); await source.scrollIntoViewIfNeeded();
    const tops = () => page.evaluate(() => [document.querySelector('.iv-list-scroll')!.scrollTop, document.querySelector('.iv-dbody')!.scrollTop]);
    const before = await tops(); expect(before[0]).toBeGreaterThan(0); expect(before[1]).toBeGreaterThan(0);
    await source.focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Record #40' })).toBeVisible();
    await page.getByRole('button', { name: /Return to Items selection/ }).click();
    await expect(source).toBeFocused();
    const after = await tops(); expect(Math.abs(after[0] - before[0])).toBeLessThan(2); expect(Math.abs(after[1] - before[1])).toBeLessThan(2);
    expect(await page.evaluate(() => window.scrollY)).toBe(0);
    await nav.getByRole('button', { name: 'Creatures', exact: true }).click();
    await expect(page.locator('.creatures-browser tbody tr')).toHaveCount(50);
    await page.getByRole('button', { name: /^Inspect Creature 40:/ }).scrollIntoViewIfNeeded();
    await page.waitForTimeout(150); const saved = await paneTop(page); expect(saved).toBeGreaterThan(0);
    await nav.getByRole('button', { name: 'Home', exact: true }).click(); await nav.getByRole('button', { name: 'Creatures', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Creatures Client records', exact: true })).toBeFocused();
    expect(Math.abs(await paneTop(page) - saved)).toBeLessThan(2);
    expect(await page.evaluate(() => window.scrollY)).toBe(0);
  } finally { await session.close(); }
});

test('everything the docked activity panel covers on Home is inert, at 100% and at 200%', async () => {
  const session = await opened(true, false, true);
  try {
    const { page } = session;
    await page.getByRole('switch', { name: /^Download mode / }).click();
    const entry = page.locator('.dv-chip:visible'); await expect(entry).toBeVisible(); await expect(entry.locator('.dv-count')).toHaveText('1');
    await entry.click(); await expect(page.getByRole('heading', { name: 'Downloads & activity', exact: true })).toBeFocused();
    expect(await page.locator('.dh-utilities').evaluate(el => (el as HTMLElement).inert)).toBe(true);
    expect(await page.locator('.dh-side').evaluate(el => (el as HTMLElement).inert)).toBe(true);
    // Every control still reachable by Tab lies outside the panel's rectangle.
    const covered = await page.evaluate(() => { const panel = document.getElementById('download-activity-panel')!.getBoundingClientRect(); return [...document.querySelectorAll<HTMLElement>('.dh-area button, .dh-area input, .dh-area [tabindex="0"]')].filter(el => !el.closest('[inert],[hidden]') && el.getClientRects().length).filter(el => { const r = el.getBoundingClientRect(); return r.right > panel.left + 1 && r.bottom > panel.top && r.top < panel.bottom; }).map(el => el.textContent); });
    expect(covered).toEqual([]);
    await page.keyboard.press('Escape'); await expect(entry).toBeFocused();
    expect(await page.locator('.dh-utilities').evaluate(el => (el as HTMLElement).inert)).toBe(false);
    await text(page, 2); await entry.click(); await expect(page.getByRole('heading', { name: 'Downloads & activity', exact: true })).toBeFocused();
    expect(await page.locator('.dh-area').evaluate(el => (el as HTMLElement).inert)).toBe(true);
    await page.keyboard.press('Escape'); await text(page, 1);
  } finally { await session.close(); }
});

test('at 200% a picked or followed table title is focused in view and the records pane itself never scrolls', async () => {
  const session = await opened(true);
  try {
    const { page } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await nav.getByRole('button', { name: 'Tables' }).click(); await text(page, 2);
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    const title = page.locator('#table-title'); await expect(title).toBeFocused();
    const visible = () => page.evaluate(() => { const t = document.getElementById('table-title')!.getBoundingClientRect(), pane = document.querySelector<HTMLElement>('.dh-records')!, head = pane.querySelector(':scope>.dh-pane-head')!.getBoundingClientRect(); return { below: t.top >= head.bottom - 1, inside: t.bottom <= pane.getBoundingClientRect().bottom, tall: t.height > 10, scroll: pane.scrollTop }; });
    expect(await visible()).toEqual({ below: true, inside: true, tall: true, scroll: 0 });
    await page.getByRole('button', { name: 'Relationships', exact: true }).click();
    await page.getByRole('button', { name: 'Open ItemClass.ClassID definition', exact: true }).first().click();
    await expect(title).toHaveText('ItemClass'); await expect(title).toBeFocused();
    expect(await visible()).toEqual({ below: true, inside: true, tall: true, scroll: 0 });
    await text(page, 1);
  } finally { await session.close(); }
});

test('at 200% an Items source record keeps the records head, notices and Return link pinned above the body', async () => {
  const session = await opened(true, false, false, true);
  try {
    const { page, data } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await writeFile(path.join(data, 'cached-items'), 'cached'); await nav.getByRole('button', { name: 'Items', exact: true }).click();
    await page.locator('#iv-opt-9').click();
    await text(page, 2);
    // One pane at a time at 200%: show the selected item, then open its whole Item record from the head.
    // The switch keeps focus once its pane shows (#93); act only after that, so the app's focus move can't land after ours.
    const shown = page.getByRole('button', { name: 'Selected item', exact: true }); await shown.click(); await expect(shown).toBeFocused();
    const source = page.getByRole('button', { name: /^Item #9/ }); await source.focus(); await expect(source).toBeFocused(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Table data', exact: true })).toBeFocused();
    await expect(page.locator('.dh-records .dh-return-link')).toBeVisible();
    const layout = await page.evaluate(() => { const records = document.querySelector<HTMLElement>('.dh-records')!, foot = records.querySelector(':scope>.dh-pane-foot')!.getBoundingClientRect(), head = records.querySelector(':scope>.dh-pane-head')!.getBoundingClientRect(), ret = records.querySelector('.dh-return-link')!.getBoundingClientRect(), notices = [...records.querySelectorAll('.dh-data:not([hidden])>.dh-notice')].map(n => n.getBoundingClientRect()); return { scroll: records.scrollTop, headTop: Math.round(head.top - records.getBoundingClientRect().top), retPinned: ret.top >= head.bottom - 1 && ret.bottom <= foot.top, noticesClear: notices.every(n => n.bottom <= foot.top + 1) }; });
    const row = await page.locator('.dh-data:not([hidden]) tbody tr').first().evaluate(el => { const r = el.getBoundingClientRect(), g = el.closest('.dh-grid-scroll')!.getBoundingClientRect(); const head = el.closest('table')!.querySelector('thead')!.getBoundingClientRect(), data = el.closest('.dh-data')!.getBoundingClientRect(), foot = document.querySelector('.dh-records>.dh-pane-foot')!.getBoundingClientRect(); return r.top >= head.bottom - 1 && r.bottom <= Math.min(g.bottom, data.bottom, foot.top) + 1 && r.height > 20; }); expect(row).toBe(true);
    expect(layout).toEqual({ scroll: 0, headTop: 0, retPinned: true, noticesClear: true });
    // Nothing is dropped at enlarged text: the whole Partial notice (never a scroll box, D099), the Record ID label and the pager count stay.
    const partial = page.locator('.dh-data:not([hidden])>.dh-notice').filter({ hasText: 'Readable rows available · Partial' });
    await expect(partial).toBeVisible(); await expect(partial).toContainText('Protected sections not loaded; results cover admitted rows only.'); await expect(partial).toContainText('Declared physical records');
    await expect(page.locator('.dh-records .dh-idfilter label')).toBeVisible(); await expect(page.locator('.dh-records .dh-idfilter label')).toHaveText('Record ID');
    await expect(page.locator('.dh-records .dh-foot-count')).toBeVisible(); await expect(page.locator('.dh-records .dh-foot-count')).toContainText('admitted');
    await expect(page.getByRole('button', { name: 'Next page of records' })).toBeVisible();
    // The records pane never scrolls as a whole, not even for a frame while a resize re-fits the notices.
    await page.evaluate(() => { const records = document.querySelector<HTMLElement>('.dh-records')!; (window as any).__recordsScrolled = records.scrollTop; records.addEventListener('scroll', () => { (window as any).__recordsScrolled = Math.max((window as any).__recordsScrolled, records.scrollTop); }); });
    await size(session, 1440, 1100);
    expect(await page.evaluate(() => document.querySelector<HTMLElement>('.dh-records')!.scrollTop)).toBe(0);
    await expect(partial).toContainText('Declared physical records'); await expect(page.locator('.dh-records .dh-return-link')).toBeVisible();
    // The notice takes the space it needs, whether the window fits it or the shell scrolls instead (D099).
    await expect(page.getByRole('region', { name: 'Readable rows available · Partial' })).toBeVisible();
    expect(await partial.evaluate(el => el.scrollHeight - el.clientHeight)).toBeLessThanOrEqual(1);
    await size(session, 1280, 800);
    const measure = () => partial.evaluate(el => { const style = getComputedStyle(el), line = parseFloat(getComputedStyle(el.querySelector('.dh-text')!).lineHeight), pad = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) + parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth), lines = (el.getBoundingClientRect().height - pad) / line; return { clamped: el.scrollHeight > el.clientHeight + 1, whole: Math.abs(lines - Math.round(lines)) < .05, lines: Math.round(lines) }; });
    // 1280x800 at 200% cannot fit the whole Partial notice above three record rows, so the shell scrolls and the notice stays whole.
    await expect(page.locator('.desktop-home.dh-shell-scroll')).toHaveCount(1);
    const whole = await measure(); expect(whole).toMatchObject({ clamped: false, whole: true }); expect(whole.lines).toBeGreaterThanOrEqual(3);
    expect(await page.evaluate(() => [(window as any).__recordsScrolled, document.querySelector<HTMLElement>('.dh-records')!.scrollTop])).toEqual([0, 0]);
    // 100%: the pager is one line with the buttons on the right and single spaces.
    await text(page, 1);
    const pager = await page.evaluate(() => { const foot = document.querySelector('.dh-records>.dh-pane-foot')!, buttons = [...foot.querySelectorAll<HTMLButtonElement>('.dh-pages button')], r = foot.getBoundingClientRect(), last = buttons.at(-1)!.getBoundingClientRect(); return { labels: buttons.map(b => b.innerText), oneLine: buttons.every(b => Math.abs(b.getBoundingClientRect().top - buttons[0].getBoundingClientRect().top) < 1) && r.height < 50, right: r.right - last.right < 20 }; });
    expect(pager).toEqual({ labels: ['First page', 'Previous page', 'Next page'], oneLine: true, right: true });
    await text(page, 1);
  } finally { await session.close(); }
});

test('Up, Down, Home and End keep the focused Data row in view at 100% and 200%, and page past the edges', async () => {
  const session = await opened(true);
  try {
    const { page, data } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await nav.getByRole('button', { name: 'Tables' }).click();
    await writeFile(path.join(data, 'release-table'), 'release');
    await page.locator('.tableitem').filter({ has: page.getByText('ItemClass', { exact: true }) }).click();
    const row = (id: string) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    const inView = (id: string) => row(id).evaluate(el => { const r = el.closest('tr')!.getBoundingClientRect(), grid = el.closest('.dh-grid-scroll')!.getBoundingClientRect(), head = el.closest('table')!.querySelector('thead')!.getBoundingClientRect(); return r.top >= head.bottom - 1 && r.bottom <= grid.bottom + 1; });
    for (const scale of [1, 2]) {
      await text(page, scale);
      await row('0').click(); await expect(row('0')).toBeFocused();
      await page.keyboard.press('End'); await expect(row('49')).toBeFocused(); await expect(row('49')).toHaveAttribute('aria-pressed', 'true'); expect(await inView('49')).toBe(true);
      await page.keyboard.press('Home'); await expect(row('0')).toBeFocused(); expect(await inView('0')).toBe(true);
      for (let i = 1; i <= 12; i++) { await page.keyboard.press('ArrowDown'); await expect(row(String(i))).toBeFocused(); expect(await inView(String(i))).toBe(true); }
      await page.keyboard.press('ArrowUp'); await expect(row('11')).toBeFocused(); expect(await inView('11')).toBe(true);
      await page.keyboard.press('End'); await page.keyboard.press('ArrowDown');
      await expect(row('50')).toBeFocused(); await expect(row('50')).toHaveAttribute('aria-pressed', 'true'); expect(await inView('50')).toBe(true);
      await page.keyboard.press('ArrowUp'); await expect(row('49')).toBeFocused(); expect(await inView('49')).toBe(true);
    }
    await text(page, 1);
  } finally { await session.close(); }
});

test('a held keyboard page read neither reflows the grid nor undoes a later move, and the corner header stays on top', async () => {
  const session = await opened(true);
  try {
    const { page, data } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await nav.getByRole('button', { name: 'Tables' }).click();
    await writeFile(path.join(data, 'release-table'), 'release');
    await page.locator('.tableitem').filter({ has: page.getByText('ItemClass', { exact: true }) }).click();
    const row = (id: string) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    const inView = (id: string) => row(id).evaluate(el => { const r = el.closest('tr')!.getBoundingClientRect(), grid = el.closest('.dh-grid-scroll')!.getBoundingClientRect(), head = el.closest('table')!.querySelector('thead')!.getBoundingClientRect(); return r.top >= head.bottom - 1 && r.bottom <= grid.bottom + 1; });
    const gridBox = () => page.locator('.dh-data:not([hidden]) .dh-grid-scroll').evaluate(el => { const r = el.getBoundingClientRect(); return [Math.round(r.top), Math.round(r.height)]; });
    for (const scale of [1, 2]) {
      await text(page, scale);
      await row('0').click(); await page.keyboard.press('End'); await expect(row('49')).toBeFocused();
      // At 200% the shell scrolls the header out of view, so the floating activity entry appears and the shell re-reveals the row above it.
      // That arrives from an IntersectionObserver a frame or more later (#93): measure only once it has.
      await expect(page.locator('.download-activity-floating')).toHaveCount(scale === 2 ? 1 : 0);
      // The scrolled grid keeps its corner header above the sticky Record ID cells.
      expect(await page.locator('.dh-data:not([hidden]) thead th').first().evaluate(el => { const r = el.getBoundingClientRect(); return document.elementFromPoint(r.left + 8, r.top + r.height / 2) === el || el.contains(document.elementFromPoint(r.left + 8, r.top + r.height / 2)); })).toBe(true);
      const before = await gridBox();
      await writeFile(path.join(data, 'hold-table-rows'), 'hold');
      await page.keyboard.press('ArrowDown');
      await expect(page.locator('.dh-records .dh-foot-count')).toHaveText('Reading verified local rows…');
      expect(await gridBox()).toEqual(before); await expect(row('49')).toBeFocused(); expect(await inView('49')).toBe(true);
      await expect(page.locator('.dh-data:not([hidden])>.dh-notice', { hasText: 'Reading verified local rows' })).toHaveCount(0);
      await page.keyboard.press('ArrowUp'); await expect(row('48')).toBeFocused(); await expect(row('48')).toHaveAttribute('aria-pressed', 'true');
      await writeFile(path.join(data, 'release-table-rows'), 'release'); await unlink(path.join(data, 'hold-table-rows'));
      await page.waitForTimeout(300);
      await expect(row('50')).toHaveCount(0); await expect(row('48')).toBeFocused(); await expect(row('48')).toHaveAttribute('aria-pressed', 'true'); expect(await inView('48')).toBe(true);
      await unlink(path.join(data, 'release-table-rows'));
    }
    await text(page, 1);
  } finally { await session.close(); }
});

// #68: the window or text size changing while the activity panel is open never drops focus to the page.
const focusInView = (page: Page) => page.evaluate(() => { const el = document.activeElement!, r = el.getBoundingClientRect(), bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect(), floor = el.closest('.dh-statusbar') || !el.closest('.dh-app') ? innerHeight : bar.top; return r.height > 0 && r.top >= -1 && r.bottom <= floor + 1; });
const settle = (page: Page) => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
async function openFromFloating(session: Awaited<ReturnType<typeof opened>>) {
  const { page } = session, floating = page.locator('.download-activity-floating');
  await size(session, 600, 480); await text(page, 2); await settle(page);
  await page.locator('.dh-app').evaluate(el => { el.scrollTop = el.scrollHeight; });
  await expect(floating).toBeVisible(); await floating.click();
  await expect(page.getByRole('heading', { name: 'Downloads & activity', exact: true })).toBeFocused();
  await expect(floating).toHaveCount(0);
}

test('closing the panel after its floating opener is gone returns focus to the header entry, else to the area heading, in view', async () => {
  const session = await opened(true, false, true);
  try {
    const { page } = session, panel = page.locator('#download-activity-panel');
    await page.getByRole('switch', { name: /^Download mode / }).click(); await expect(page.locator('.dv-chip:visible')).toBeVisible();
    // Opened from the floating entry, then the window grows: the header is back, so there is no floating entry to return to.
    await openFromFloating(session);
    await size(session, 1280, 800); await settle(page);
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden();
    await expect(page.locator('.download-activity-floating')).toHaveCount(0);
    await expect(page.locator('.dv-chip:visible')).toBeFocused();
    expect(await focusInView(page), 'header entry in view').toBe(true);
    // The same at 600x480 when the text goes back to 100%: the shell stops scrolling and the header entry takes focus.
    await openFromFloating(session);
    await text(page, 1); await settle(page);
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden();
    await expect(page.locator('.dv-chip:visible')).toBeFocused();
    expect(await focusInView(page), 'header entry in view at 600x480 100%').toBe(true);
    // With the header entry retired too (the pass completed while the panel was open), focus goes to the area heading, in view.
    await openFromFloating(session);
    await writeFile(path.join(session.data, 'retention-state.json'), JSON.stringify({ state: 'completed', pendingObjects: 0, progress: null, problem: null, unresolvedObjects: '0', coverage: { receiptObjects: '60', receiptBytes: '6000', receiptedAt: '2026-10-19T00:00:00Z', presenceState: 'assessed', presenceAssessedAt: '2026-10-19T01:00:00Z', observedObjects: '60', presentObjects: '60', presentBytes: '6000', missingObjects: '0', corruptObjects: '0' } }));
    await panel.getByRole('button', { name: 'Check download status', exact: true }).click();
    await expect(page.locator('.retention-state')).toHaveText('Encoded retention pass complete');
    await size(session, 1280, 800); await settle(page);
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden();
    await expect(page.locator('.dv-chip')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    expect(await focusInView(page), 'area heading in view').toBe(true);
    await text(page, 1);
  } finally { await session.close(); }
});

test('the header turning inert under focus when the open panel becomes a sheet moves focus to the panel heading', async () => {
  const session = await opened(true, false, true);
  try {
    const { page } = session, panel = page.locator('#download-activity-panel'), heading = page.getByRole('heading', { name: 'Downloads & activity', exact: true });
    const settings = page.locator('.dh-toolbar').getByRole('button', { name: 'Settings', exact: true }), inert = () => settings.evaluate(el => !!el.closest('[inert]'));
    await page.getByRole('switch', { name: /^Download mode / }).click(); await expect(page.locator('.dv-chip:visible')).toBeVisible();
    // 1280x800 doesn't switch to the sheet at 200%: the panel stays nonmodal and focus moved to the header stays there.
    await page.locator('.dv-chip:visible').click(); await expect(heading).toBeFocused();
    await settings.focus(); await text(page, 2); await settle(page);
    await expect(settings).toBeFocused(); expect(await inert()).toBe(false);
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden(); await expect(settings).toBeFocused();
    // 600x480: 200% text makes the open panel a sheet and the header inert, so focus moves into the panel, to its heading, in view.
    await text(page, 1); await size(session, 600, 480);
    await page.locator('.dv-chip:visible').click(); await expect(heading).toBeFocused();
    await settings.focus(); await expect(settings).toBeFocused();
    await text(page, 2); await settle(page);
    expect(await inert(), 'header inert under the sheet').toBe(true);
    await expect(heading).toBeFocused();
    expect(await heading.evaluate(el => { const r = el.getBoundingClientRect(); return r.height > 0 && r.top >= -1 && r.bottom <= innerHeight + 1; }), 'panel heading in view').toBe(true);
    // Escape from there still returns to the opener, in view.
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden(); await expect(page.locator('.dv-chip:visible')).toBeFocused();
    expect(await focusInView(page)).toBe(true);
    await text(page, 1);
  } finally { await session.close(); }
});
