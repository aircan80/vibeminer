import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';

// #64: the build header's area tabs and actions never overlap, clip or hide behind a scroll. In the compact header
// the tabs wrap onto more lines and the actions take their own line when they don't fit beside the tabs. The area keeps at least
// 10rem: only where the header and that minimum don't both fit (600x480 at 200%) does the shell scroll vertically as a whole.
type Box = { name: string; left: number; right: number; top: number; bottom: number; clipped?: boolean };
async function measure(page: Page) {
  return page.locator('.dh-toolbar').evaluate(header => {
    const box = (el: Element, name = (el.querySelector('.dv-chip-short') ?? el).textContent!.replace('← ', '').trim()) => { const r = el.getBoundingClientRect(); return { name, left: r.left, right: r.right, top: r.top, bottom: r.bottom, clipped: el.scrollWidth > el.clientWidth + 1 }; };
    const nav = header.querySelector('.dh-areas')!, visible = (b: Element) => b.getClientRects().length > 0;
    return {
      tabs: [...nav.querySelectorAll('button')].map(b => box(b)),
      actions: [...header.querySelectorAll('.dh-hgroup button')].filter(visible).map(b => box(b)),
      separator: (() => { const sep = nav.querySelector('.dh-area-sep'), next = sep?.nextElementSibling; return sep && next ? { dangling: next.getBoundingClientRect().top >= sep.getBoundingClientRect().bottom, hidden: getComputedStyle(sep).visibility === 'hidden' } : null; })(),
      nav: box(nav, 'area bar'), navScroll: nav.scrollWidth - nav.clientWidth,
      // The app root and its shell never grow wider than the window, even where overflow is hidden.
      roots: ['#root', '.desktop-home', '.dh-app', '.dh-toolbar'].map(selector => { const el = document.querySelector(selector)!; return { selector, width: el.getBoundingClientRect().width, overflow: el.scrollWidth - el.clientWidth }; }),
      shell: (() => { const app = document.querySelector('.dh-app')!, area = document.querySelector<HTMLElement>('#dh-area')!, bar = document.querySelector<HTMLElement>('.dh-statusbar')!; return { statusLast: app.lastElementChild === bar && Math.abs(area.offsetTop + area.offsetHeight + bar.offsetHeight - app.scrollHeight) <= 1, spill: area.scrollHeight - area.clientHeight, overflow: app.scrollHeight - app.clientHeight, area: document.querySelector('#dh-area')!.getBoundingClientRect().height, rem: parseFloat(getComputedStyle(document.documentElement).fontSize), windowScroll: document.documentElement.scrollTop + document.documentElement.scrollLeft }; })(),
      width: document.documentElement.clientWidth, pageScroll: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    };
  });
}
const overlap = (a: Box, b: Box) => a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1;
const inside = (a: Box, b: Box) => a.left >= b.left - 1 && a.right <= b.right + 1 && a.top >= b.top - 1 && a.bottom <= b.bottom + 1;

async function checkFit(page: Page, app: ElectronApplication, state: string, actions: string[]) {
  const out = path.join(root, 'artifacts/header-fit'); await mkdir(out, { recursive: true });
  for (const [width, height] of [[600, 480], [1280, 800], [1440, 1100]]) {
    await size(app, width, height);
    await expect.poll(() => page.evaluate(() => innerWidth)).toBe(width);
    for (const scale of [1, 2]) {
      await text(page, scale);
      await page.evaluate(() => { document.querySelector('.dh-app')!.scrollTop = 0; return new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); });
      const label = `${state} ${width}x${height} ${scale * 100}%`, m = await measure(page);
      expect(m.tabs.map(t => t.name), label).toEqual(['Home', 'Tables', 'Files', 'Items', 'Creatures']);
      expect(m.actions.map(a => a.name), label).toEqual(actions);
      expect.soft(m.pageScroll, `${label} page scrolls sideways`).toBeLessThanOrEqual(0);
      for (const r of m.roots) { expect.soft(r.width, `${label} ${r.selector} wider than the window`).toBeLessThanOrEqual(m.width + 1); expect.soft(r.overflow, `${label} ${r.selector} overflows sideways`).toBeLessThanOrEqual(1); }
      expect.soft(m.shell.area, `${label} area below its 10rem minimum`).toBeGreaterThanOrEqual(10 * m.shell.rem - 1);
      expect.soft(m.shell.windowScroll, `${label} window scrolled`).toBe(0);
      expect.soft(m.shell.statusLast, `${label} status bar is not last in the shell`).toBe(true);
      expect.soft(await page.evaluate(() => Math.round(document.querySelector('.dh-statusbar')!.getBoundingClientRect().bottom)), `${label} status bar off the window bottom`).toBe(height);
      expect.soft(m.shell.spill, `${label} area content spills over the status bar`).toBeLessThanOrEqual(1);
      if (width === 600 && scale === 2) expect.soft(m.shell.overflow, `${label} shell should scroll`).toBeGreaterThan(0);
      else expect.soft(m.shell.overflow, `${label} shell scrolls although everything fits`).toBeLessThanOrEqual(1);
      if (m.separator) expect.soft(m.separator.hidden, `${label} group separator ${m.separator.dangling ? 'dangles' : 'hidden'}`).toBe(m.separator.dangling);
      expect.soft(m.navScroll, `${label} area tabs hidden behind a scroll`).toBeLessThanOrEqual(0);
      for (const tab of m.tabs) {
        expect.soft(tab.clipped, `${label} ${tab.name} text clipped`).toBe(false);
        expect.soft(inside(tab, m.nav), `${label} ${tab.name} outside the area bar`).toBe(true);
        expect.soft(tab.left >= -1 && tab.right <= m.width + 1, `${label} ${tab.name} off screen`).toBe(true);
        for (const action of m.actions) expect.soft(overlap(tab, action), `${label} ${action.name} covers ${tab.name}`).toBe(false);
      }
      for (const action of m.actions) {
        expect.soft(action.clipped, `${label} ${action.name} text clipped`).toBe(false);
        expect.soft(action.left >= -1 && action.right <= m.width + 1, `${label} ${action.name} off screen`).toBe(true);
        expect.soft(overlap(action, m.nav), `${label} ${action.name} over the area bar`).toBe(false);
      }
      await page.screenshot({ path: path.join(out, `${state}-${width}x${height}-${scale * 100}.png`) });
    }
  }
  await text(page, 1); await size(app, 1280, 800);
}

test('area tabs and header actions never overlap or clip at 600x480, 1280x800 and 1440x1100, 100% and 200% text', async () => {
  test.setTimeout(60_000);
  const session = await launch(true, false, false, true, false, false, true);
  try {
    const { page, app } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    await expect(page.locator('.dh-toolbar .dv-chip')).toHaveCount(0);
    await checkFit(page, app, 'idle', ['Choose build', 'Settings']);
    // Creatures preparation is held by the fixture, so the header also carries the activity entry (the #64 report).
    const nav = page.getByRole('navigation', { name: 'Build areas' });
    await nav.getByRole('button', { name: 'Creatures', exact: true }).click();
    await expect(page.locator('.dh-toolbar .dv-chip')).toBeVisible();
    await checkFit(page, app, 'activity', ['Activity', 'Choose build', 'Settings']);

    // Compact header: Tab follows the visual order (identity, area tabs, then the actions below them) both ways.
    await text(page, 2);
    const order = ['Status', 'Home', 'Tables', 'Files', 'Items', 'Creatures', 'Activity', 'Choose build', 'Settings'];
    const focused = () => page.evaluate(() => { const el = document.activeElement as HTMLElement; return el.closest('.dh-idf')?.querySelector('dt')!.textContent ?? (el.querySelector('.dv-chip-short') ?? el).textContent!.replace('← ', '').trim(); });
    await page.locator('.dh-toolbar').getByRole('button', { name: /^Status / }).focus();
    for (const name of order.slice(1)) { await page.keyboard.press('Tab'); expect(await focused(), 'Tab order').toBe(name); }
    for (const name of order.slice(0, -1).reverse()) { await page.keyboard.press('Shift+Tab'); expect(await focused(), 'Shift+Tab order').toBe(name); }

    // The entry's task finishing narrows the entry (its count goes), but its widest width stays reserved, so the header
    // doesn't re-wrap under the reader (item-effects covers the same on Items).
    const header = page.locator('.dh-toolbar'), height = () => header.evaluate(el => el.getBoundingClientRect().height);
    const chipWidth = () => header.locator('.dv-chip').evaluate(el => el.getBoundingClientRect().width);
    const busy = await height(), wide = await chipWidth();
    await writeFile(path.join(session.data, 'release-creatures'), 'release');
    await expect(page.locator('.cv-option')).toHaveCount(50);
    await expect(header.locator('.dv-chip .dv-count')).toHaveCount(0);
    expect(Math.abs(await chipWidth() - wide), 'entry width after its task finished').toBeLessThan(1);
    expect(Math.abs(await height() - busy), 'header height after the entry narrowed').toBeLessThan(1);

    // 600x480 at 200%: the shell scrolls as a whole. Tab from the last header action lands in the area, scrolled into view, and the
    // whole area (at least 10rem) can be brought into view; the window itself never scrolls.
    await size(app, 600, 480);
    await expect.poll(() => page.evaluate(() => innerWidth)).toBe(600);
    const shell = page.locator('.dh-app'), area = page.locator('#dh-area');
    await shell.evaluate(el => { el.scrollTop = 0; });
    await header.getByRole('button', { name: 'Settings' }).focus(); await page.keyboard.press('Tab');
    const reached = await page.evaluate(() => { const el = document.activeElement!, r = el.getBoundingClientRect(); return { inArea: !!el.closest('#dh-area'), visible: r.top >= 0 && r.bottom <= innerHeight && r.height > 0, scrolled: document.querySelector('.dh-app')!.scrollTop, window: document.documentElement.scrollTop }; });
    expect(reached.inArea, 'Tab after Settings reaches the area').toBe(true);
    expect(reached.visible, 'focused area control scrolled into view').toBe(true);
    expect(reached.scrolled, 'the shell scrolled, not the window').toBeGreaterThan(0); expect(reached.window).toBe(0);
    // Creatures takes its natural height here as Items does (D092, #62): its top comes into view and its list keeps its own scroller.
    await area.evaluate(el => el.scrollIntoView({ block: 'start' }));
    const inView = await area.evaluate(el => { const r = el.getBoundingClientRect(), list = document.querySelector('.cv .iv-list-scroll')!.getBoundingClientRect(); return { top: r.top, height: r.height, list: list.height, rem: parseFloat(getComputedStyle(document.documentElement).fontSize) }; });
    expect(inView.top).toBeGreaterThanOrEqual(-1); expect(inView.top).toBeLessThan(481); expect(inView.height).toBeGreaterThanOrEqual(10 * inView.rem - 1);
    expect(inView.list).toBeLessThanOrEqual(481);
    await page.screenshot({ path: path.join(root, 'artifacts/header-fit/shell-scrolled-600x480-200.png') });
    // While the shell scrolls, the open panel is fixed to the window: from the top to the status bar's height above the bottom, within the
    // shell's inner width. Opening it never moves the shell.
    await shell.evaluate(el => { el.scrollTop = 0; });
    const entry = header.locator('.dv-chip'); await entry.click();
    const panel = page.locator('#download-activity-panel'); await expect(panel).toBeVisible();
    await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
    const docked = await page.evaluate(() => { const p = document.querySelector('#download-activity-panel')!.getBoundingClientRect(), app = document.querySelector<HTMLElement>('.dh-app')!, bar = document.querySelector<HTMLElement>('.dh-statusbar')!; return { top: p.top, bottom: p.bottom, left: p.left, right: p.right, height: p.height, limit: innerHeight - bar.offsetHeight, inner: app.getBoundingClientRect().left + app.clientWidth, scroll: app.scrollTop }; });
    expect(Math.abs(docked.top), 'panel at the window top').toBeLessThan(1);
    expect(Math.abs(docked.bottom - docked.limit), 'panel ends the status bar\'s height above the bottom').toBeLessThan(1);
    expect(Math.abs(docked.left), 'panel at the left edge').toBeLessThan(1);
    expect(Math.abs(docked.right - docked.inner), 'panel within the shell\'s inner width').toBeLessThan(1);
    expect(docked.scroll, 'opening never moves the shell').toBe(0);
    // At this size the open panel covers everything, so it is a sheet: the header, the area and the floating entry are inert. Tab and
    // Shift+Tab stay in the panel (and the status bar), never land under it, every stop is visible, and the panel never moves.
    const sweep = async (label: string) => {
      const rect = () => panel.evaluate(el => { const r = el.getBoundingClientRect(); return [r.top, r.bottom, r.left, r.right].map(Math.round).join(','); });
      const start = await rect(), close = panel.getByRole('button', { name: 'Close downloads and activity' });
      for (const key of ['Tab', 'Shift+Tab']) {
        await close.focus(); let stops = 0;
        for (let i = 0; i < 30; i++) {
          await page.keyboard.press(key);
          const at = await page.evaluate(() => {
            const el = document.activeElement!, r = el.getBoundingClientRect(), p = document.querySelector('#download-activity-panel')!, pr = p.getBoundingClientRect(), head = p.querySelector('.download-panel-heading')!.getBoundingClientRect();
            const inPanel = p.contains(el), inBar = !!el.closest('.dh-statusbar'), below = inPanel && !el.closest('.download-panel-heading') ? head.bottom : pr.top;
            return { body: el === document.body, inPanel, inBar, name: el.textContent!.trim().slice(0, 40), visible: r.height > 0 && r.top >= -1 && r.bottom <= innerHeight + 1 && (!inPanel || r.top >= below - 1 && r.bottom <= pr.bottom + 1) };
          });
          if (at.body) continue;
          stops++;
          expect(at.inPanel || at.inBar, `${label}: ${key} ${i + 1} landed under the panel on "${at.name}"`).toBe(true);
          expect(at.visible, `${label}: ${key} ${i + 1} stop "${at.name}" not visible`).toBe(true);
          expect(await rect(), `${label}: panel moved at ${key} ${i + 1}`).toBe(start);
        }
        expect(stops, `${label}: ${key} reached focus stops`).toBeGreaterThan(2);
      }
    };
    await sweep('opened from the header entry');
    await page.screenshot({ path: path.join(root, 'artifacts/header-fit/panel-600x480-200.png') });
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden(); await expect(entry).toBeFocused();
    expect(await entry.evaluate(el => { const r = el.getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight; }), 'entry visible after focus returns').toBe(true);
    // In view: inside the window and, for controls in the shell, above the status bar that stays at the window bottom.
    const focusInView = () => page.evaluate(() => { const el = document.activeElement!, r = el.getBoundingClientRect(), bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect(), floor = el.closest('.dh-statusbar') || !el.closest('.dh-app') ? innerHeight : bar.top; return r.height > 0 && r.top >= -1 && r.bottom <= floor + 1; });
    const panelBox = () => panel.evaluate(el => { const r = el.getBoundingClientRect(), app = document.querySelector('.dh-app')!; return { height: r.height, right: r.right, shellRight: app.getBoundingClientRect().left + app.clientWidth }; });
    // The sheet stops at the shell's inner width rather than covering its scrollbar, and Close also returns to the opener, scroll unchanged.
    await entry.click(); await expect(panel).toBeVisible();
    const opened = await panelBox();
    expect(opened.right, 'panel stops at the shell scrollbar').toBeLessThanOrEqual(opened.shellRight + 1);
    await panel.getByRole('button', { name: 'Close downloads and activity' }).focus(); await page.keyboard.press('Enter');
    await expect(panel).toBeHidden(); await expect(entry).toBeFocused();
    expect(await shell.evaluate(el => el.scrollTop), 'scroll unchanged after Close').toBe(0);
    await entry.click(); await expect(panel).toBeVisible();
    // Dismissing the last outcome and pressing Escape retires the entry, so focus falls back to the area heading, which is scrolled into view.
    const dismiss = panel.getByRole('button', { name: 'Dismiss outcome' });
    while (await dismiss.count()) await dismiss.first().click();
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden();
    await expect(header.locator('.dv-chip')).toHaveCount(0);
    expect(await page.evaluate(() => document.activeElement!.tagName), 'fallback focus target').toBe('H1');
    expect(await focusInView(), 'fallback focus scrolled into view').toBe(true);
    // Home: the shell is the only scroller while it scrolls (the Home grid takes its natural height).
    await nav.getByRole('button', { name: 'Home', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    expect(await focusInView(), 'Home heading in view after Home').toBe(true);
    const scrollers = await page.evaluate(() => [...document.querySelectorAll<HTMLElement>('.desktop-home *')].filter(el => { const y = getComputedStyle(el).overflowY; return (y === 'auto' || y === 'scroll') && el.scrollHeight > el.clientHeight + 1; }).map(el => el.className));
    expect(scrollers, 'one scroller on Home').toEqual(['dh-app']);
    // The area grows with Home's natural height, so the status bar comes last, after all of it, and overlaps nothing.
    await page.locator('.dh-app').evaluate(el => { el.scrollTop = el.scrollHeight; });
    const ends = await page.evaluate(() => { const app = document.querySelector<HTMLElement>('.dh-app')!, area = document.querySelector<HTMLElement>('#dh-area')!, bar = document.querySelector<HTMLElement>('.dh-statusbar')!, content = area.firstElementChild!.getBoundingClientRect(), b = bar.getBoundingClientRect(); return { last: app.lastElementChild === bar, area: area.offsetHeight, spill: area.scrollHeight - area.clientHeight, contentBottom: content.bottom, barTop: b.top, end: Math.abs(area.offsetTop + area.offsetHeight + bar.offsetHeight - app.scrollHeight) }; });
    expect(ends.last, 'status bar is the shell\'s last child').toBe(true);
    expect(ends.area, 'area grows with Home').toBeGreaterThan(480);
    expect(ends.spill, 'nothing spills out of the area').toBeLessThanOrEqual(1);
    expect(ends.contentBottom, 'Home ends above the status bar').toBeLessThanOrEqual(ends.barTop + 1);
    expect(ends.end, 'status bar ends the shell').toBeLessThanOrEqual(1);
    await page.screenshot({ path: path.join(root, 'artifacts/header-fit/home-end-600x480-200.png') });
    // Opening Items for the first time: its heading takes focus, then its preparation shows the activity entry and the header grows.
    // The heading stays in view.
    await expect(header.locator('.dv-chip')).toHaveCount(0);
    const before = await height();
    await nav.getByRole('button', { name: 'Items', exact: true }).focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Items', exact: true })).toBeFocused();
    await expect(header.locator('.dv-chip')).toBeVisible();
    await expect.poll(height, 'header grew after the entry appeared').toBeGreaterThan(before + 20);
    await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
    expect(await focusInView(), 'Items heading still in view after the header grew').toBe(true);
    await page.screenshot({ path: path.join(root, 'artifacts/header-fit/items-entry-600x480-200.png') });
    // With the header scrolled away the floating entry appears: one compact line, clear of the status bar, still named by its summary,
    // and it opens and closes the panel with focus coming back to it.
    await nav.getByRole('button', { name: 'Home', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    await page.locator('.dh-app').evaluate(el => { el.scrollTop = el.scrollHeight; });
    const floating = page.locator('.download-activity-floating');
    await expect(floating).toBeVisible();
    await expect(floating).toHaveAccessibleName(/^Downloads & activity \S/);
    const fit = await floating.evaluate(el => { const r = el.getBoundingClientRect(), bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect(), cs = getComputedStyle(el), title = el.querySelector('strong')!, line = parseFloat(getComputedStyle(title).lineHeight), rem = parseFloat(getComputedStyle(document.documentElement).fontSize); return { gap: bar.top - r.bottom, rem, content: el.clientHeight - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom), line, width: r.width, natural: (() => { const range = document.createRange(); range.selectNodeContents(title); return range.getBoundingClientRect().width; })() + parseFloat(cs.paddingLeft) + parseFloat(cs.paddingRight) + parseFloat(cs.borderLeftWidth) + parseFloat(cs.borderRightWidth), left: r.left, right: r.right, window: innerWidth }; });
    expect(fit.gap, 'floating entry clear of the status bar').toBeGreaterThanOrEqual(fit.rem - 1);
    expect(fit.content, 'floating entry is one line').toBeLessThanOrEqual(fit.line + 1);
    expect(Math.abs(fit.width - fit.natural), 'floating entry at its natural width').toBeLessThanOrEqual(1);
    expect(fit.left >= 0 && fit.right <= fit.window, 'floating entry on screen').toBe(true);
    await page.screenshot({ path: path.join(root, 'artifacts/header-fit/floating-600x480-200.png') });
    await floating.focus(); await page.keyboard.press('Enter');
    await expect(panel).toBeVisible();
    const sheet = await panel.evaluate(el => { const r = el.getBoundingClientRect(), bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect(), rem = parseFloat(getComputedStyle(document.documentElement).fontSize); return { top: r.top, bottom: r.bottom, height: r.height, bar: bar.top, rem }; });
    expect(sheet.top >= -1 && sheet.bottom <= sheet.bar + 1, 'panel opened from the floating entry is in view, above the status bar').toBe(true);
    expect(sheet.height, 'panel opened from the floating entry has room').toBeGreaterThanOrEqual(10 * sheet.rem - 2);
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden();
    await expect(floating).toBeFocused();
    // While the panel is open the shell is locked: wheeling past the end of the panel's content, End and PageDown never scroll it away.
    const settle = () => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
    const openFromFloating = async () => {
      await shell.evaluate(el => { el.scrollTop = 1200; }); await expect(floating).toBeVisible();
      await floating.click(); await expect(panel).toBeVisible(); await settle();
      return shell.evaluate(el => el.scrollTop);
    };
    const panelInView = () => panel.evaluate(el => { const r = el.getBoundingClientRect(), bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect(); return r.height > 0 && r.top >= -1 && r.bottom <= Math.min(innerHeight, bar.top) + 1; });
    const locked = await openFromFloating();
    expect(locked, 'opened at 1200').toBe(1200);
    const sheetBox = (await panel.boundingBox())!;
    await page.mouse.move(sheetBox.x + sheetBox.width / 2, sheetBox.y + sheetBox.height / 2);
    for (let i = 0; i < 6; i++) await page.mouse.wheel(0, 2000);
    await expect.poll(() => panel.evaluate(el => el.scrollTop + el.clientHeight >= el.scrollHeight - 1), 'panel content scrolled to its end').toBe(true);
    await page.keyboard.press('End'); await page.keyboard.press('PageDown'); await settle();
    expect(await shell.evaluate(el => el.scrollTop), 'shell locked while the panel is open').toBe(locked);
    expect(await panelInView(), 'panel still fully in view').toBe(true);
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden(); await expect(floating).toBeFocused();
    expect(await shell.evaluate(el => el.scrollTop), 'scroll restored on close').toBe(locked);
    await openFromFloating(); await sweep('opened from the floating entry at 1200');
    await page.keyboard.press('Escape'); await expect(panel).toBeHidden(); await expect(floating).toBeFocused();
    expect(await shell.evaluate(el => el.scrollTop), 'scroll restored after Escape').toBe(1200);
    await openFromFloating();
    await panel.getByRole('button', { name: 'Close downloads and activity' }).click();
    await expect(panel).toBeHidden(); await expect(floating).toBeFocused();
    expect(await shell.evaluate(el => el.scrollTop), 'scroll restored after Close').toBe(1200);
    // Closed again, nothing stays inert.
    expect(await page.evaluate(() => document.querySelectorAll('.desktop-home [inert]').length), 'inert cleared on close').toBe(0);
    await page.screenshot({ path: path.join(root, 'artifacts/header-fit/home-600x480-200.png') });


    // Every tab stays reachable by keyboard at 200% text in the smallest window, and Enter opens it.
    await nav.getByRole('button', { name: 'Home', exact: true }).focus();
    for (const name of ['Tables', 'Files', 'Items', 'Creatures']) { await page.keyboard.press('Tab'); await expect(nav.getByRole('button', { name, exact: true })).toBeFocused(); }
    await nav.getByRole('button', { name: 'Tables', exact: true }).focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Tables', exact: true })).toBeFocused();
    expect(await focusInView(), 'Tables heading in view after Enter on its tab').toBe(true);
    await expect(nav.getByRole('button', { name: 'Tables', exact: true })).toHaveAttribute('aria-current', 'page');
  } finally { await session.close(); }
});
