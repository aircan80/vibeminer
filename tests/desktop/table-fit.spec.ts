import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';

// #60 (D099): a table's Data view always shows its column header and at least three whole record rows, and a focused row is always
// wholly visible, at every reference size and text scale, whatever else takes height: the build header (three rows at 960x700 and
// 200%), the source notices (fetched definition, Partial, text issues, raw physical values), the re-read line, the pager and the status
// bar. Notices compact first and never become scroll boxes; when that isn't enough the shell scrolls as a whole (D086, D092). The
// column header stays on screen with a focused row, PageUp/PageDown move focus, the open activity sheet covers the grid's sticky
// header, closing it restores the shell exactly, and a record ID with no admitted row keeps its notices and box whole.
const out = path.join(root, 'artifacts/table-fit');
const sizes: [number, number, number][] = [[1280, 800, 1], [1280, 800, 2], [960, 700, 1], [960, 700, 2], [1440, 1100, 1], [1440, 1100, 2], [600, 480, 2]];
const settle = (page: Page) => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));

async function opened(marks: string[]) {
  const session = await launch(true);
  const { page, app, data } = session;
  for (const mark of marks) await writeFile(path.join(data, mark), mark);
  await size(app, 1280, 800);
  await page.locator('.build-row').first().click();
  await page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(data, 'release-open'), 'release');
  await writeFile(path.join(data, 'release-table'), 'release');
  await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible();
  await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
  await expect(page.locator('.tableitem')).toHaveCount(50);
  return session;
}
const pick = async (page: Page, name: string) => {
  await page.locator('.tableitem').filter({ has: page.getByText(name, { exact: true }) }).click();
  await expect(page.locator('#table-title')).toHaveText(name);
  await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
};

// Whole rows in the Data grid below its sticky header, once its first row is at the top of the grid and the grid's top is at the top
// of the shell's view (a no-op while the shell doesn't scroll), inside every clipping ancestor and above the status bar.
async function wholeRows(page: Page) {
  return page.evaluate(() => {
    const grid = document.querySelector<HTMLElement>('.dh-data:not([hidden]) .dh-grid-scroll')!, app = document.querySelector<HTMLElement>('.dh-app')!;
    const rows = [...grid.querySelectorAll<HTMLElement>('tbody tr')], head = grid.querySelector('thead')!.getBoundingClientRect().height;
    grid.scrollTop = 0;
    app.scrollTop += grid.getBoundingClientRect().top - app.getBoundingClientRect().top - (parseFloat(getComputedStyle(app).scrollPaddingTop) || 0);
    const box = grid.getBoundingClientRect();
    let top = box.top + head, bottom = box.top + grid.clientHeight;
    for (let up = grid.parentElement; up; up = up.parentElement) if (/auto|scroll|hidden|clip/.test(getComputedStyle(up).overflowY)) { const s = up.getBoundingClientRect(); top = Math.max(top, s.top); bottom = Math.min(bottom, s.top + up.clientHeight); }
    bottom = Math.min(bottom, document.querySelector('.dh-statusbar')!.getBoundingClientRect().top, innerHeight);
    const whole = rows.filter(r => { const b = r.getBoundingClientRect(); return b.height > 0 && b.top >= top - 1 && b.bottom <= bottom + 1; }).length;
    // A notice that scrolls inside itself, or is cut off by the Data view, hides part of what it says.
    const data = grid.closest<HTMLElement>('.dh-data')!.getBoundingClientRect();
    const boxed = [...document.querySelectorAll<HTMLElement>('.dh-data:not([hidden])>.dh-notice')].filter(n => n.getClientRects().length > 0 && (n.scrollHeight > n.clientHeight + 1 || n.getBoundingClientRect().bottom > data.bottom + 1)).map(n => n.textContent!.trim().slice(0, 40));
    return { whole, head: Math.round(head), grid: Math.round(box.height), scrolls: app.scrollHeight > app.clientHeight + 1, boxed, sideways: document.documentElement.scrollWidth - document.documentElement.clientWidth };
  });
}

// The focused row is wholly visible: inside every ancestor that clips it, below the sticky column header, above the status bar, and
// drawn there (nothing such as the floating activity entry lies over it). The column header itself is wholly on screen, between the
// window top and the status bar, so a focused row never shows without its column names.
async function focusVisible(page: Page) {
  await settle(page);
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement, row = el.closest('tr') ?? el, r = row.getBoundingClientRect();
    let top = 0, bottom = innerHeight;
    for (let up = row.parentElement; up; up = up.parentElement) {
      if (/auto|scroll|hidden|clip/.test(getComputedStyle(up).overflowY)) { const s = up.getBoundingClientRect(); top = Math.max(top, s.top); bottom = Math.min(bottom, s.top + up.clientHeight); }
      if (up.matches('.dh-grid-scroll')) top = Math.max(top, up.querySelector('thead th')!.getBoundingClientRect().bottom);
    }
    const floor = document.querySelector('.dh-statusbar')!.getBoundingClientRect().top;
    bottom = Math.min(bottom, floor);
    // The header cells are what stick (the thead itself scrolls away), so measure the corner cell.
    const head = row.closest('table')!.querySelector('thead th')!.getBoundingClientRect();
    const b = el.getBoundingClientRect(), hit = document.elementFromPoint(b.left + 4, (b.top + b.bottom) / 2);
    return { name: el.getAttribute('aria-label'), at: `${Math.round(r.top)}-${Math.round(r.bottom)} in ${Math.round(top)}-${Math.round(bottom)}, header ${Math.round(head.top)}-${Math.round(head.bottom)}`, whole: r.height > 0 && r.top >= top - 1 && r.bottom <= bottom + 1, header: head.top >= -1 && head.bottom <= floor + 1, drawn: !!hit && (hit === el || el.contains(hit)) };
  });
}

async function everySize(page: Page, app: ElectronApplication, state: string) {
  await mkdir(out, { recursive: true });
  const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
  for (const [width, height, scale] of sizes) {
    const label = `${state} ${width}x${height} ${scale * 100}%`;
    await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
    await text(page, scale); await settle(page);
    const records = page.getByRole('group', { name: 'Show pane' }).filter({ visible: true }).getByRole('button', { name: 'Records', exact: true });
    if (await records.count()) await records.click();
    await expect(row(0)).toBeVisible(); await settle(page);
    // The layout settles (the shell may switch to scrolling once the Data view reports what it needs).
    await expect.poll(async () => (await wholeRows(page)).whole, `${label}: whole record rows`).toBeGreaterThanOrEqual(3);
    const fit = await wholeRows(page);
    expect.soft(fit.boxed, `${label}: notices cut off or scrolling inside themselves`).toEqual([]);
    expect.soft(fit.sideways, `${label}: the window scrolls sideways`).toBeLessThanOrEqual(0);
    await page.screenshot({ path: path.join(out, `${state}-${width}x${height}-${scale * 100}.png`) });
    // Keyboard: every focused row stays in view below the sticky header and above the status bar.
    // Selected by click, as a reader would, so the arrow keys move from it.
    // PageDown and PageUp move by the rows the grid shows, at least one, as a listbox does.
    await row(0).click();
    const focusedId = () => page.evaluate(() => Number(/^Inspect record (\d+)/.exec(document.activeElement?.getAttribute('aria-label') ?? '')?.[1] ?? -1));
    const steps: [string, (id: number, before: number) => boolean][] = [['', id => id === 0],
      ...Array.from({ length: 12 }, (_, i) => ['ArrowDown', (id: number) => id === i + 1] as [string, (id: number) => boolean]),
      ['End', id => id === 49], ['ArrowUp', id => id === 48], ['PageUp', (id, before) => id < before && id >= 0], ['PageDown', (id, before) => id > before], ['Home', id => id === 0],
      ['PageDown', (id, before) => id > before + 1], ['PageDown', (id, before) => id > before], ['PageUp', (id, before) => id < before]];
    for (const [key, expected] of steps) {
      const before = await focusedId();
      if (key) await page.keyboard.press(key);
      await expect.poll(async () => expected(await focusedId(), before), `${label}: focus after ${key || 'click'} from record ${before}`).toBe(true);
      const f = await focusVisible(page);
      expect.soft(f.whole, `${label}: ${f.name} after ${key || 'click'} not wholly visible (${f.at})`).toBe(true);
      expect.soft(f.header, `${label}: column header off screen with ${f.name} focused after ${key || 'click'} (${f.at})`).toBe(true);
      expect.soft(f.drawn, `${label}: ${f.name} after ${key || 'click'} is covered (${f.at})`).toBe(true);
    }
  }
  await text(page, 1); await size(app, 1280, 800);
}

test('Item, with its long raw-physical column header and Partial notice, keeps three whole rows and focused rows in view at every size', async () => {
  test.setTimeout(120_000);
  const session = await opened([]);
  try {
    const { page, app } = session;
    await pick(page, 'Item');
    await expect(page.getByRole('columnheader', { name: /Field_12_0_0_63534_010 \?.*Raw physical uint32/ })).toBeVisible();
    await expect(page.locator('.dh-data:not([hidden]) .dh-notice strong').first()).toHaveText('Readable rows available · Partial');
    await everySize(page, app, 'item');
  } finally { await session.close(); }
});

test('the fetched-definition, Partial and text-issue notices together keep three whole rows and focused rows in view at every size', async () => {
  test.setTimeout(120_000);
  const session = await opened(['table-fetched', 'table-partial', 'table-text-issue']);
  try {
    const { page, app } = session;
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toBeVisible();
    await expect(page.getByText('Readable rows available · Partial', { exact: true })).toBeVisible();
    await expect(page.getByText('1 text value could not be decoded as UTF-8.', { exact: true })).toBeVisible();
    await everySize(page, app, 'notices');
    // Slightly wider text, as Linux CI's system-ui font, wraps the pane head and notices further.
    await page.addStyleTag({ content: '.desktop-home *{letter-spacing:.07em!important}' });
    await everySize(page, app, 'notices-ci-font');
  } finally { await session.close(); }
});

test('a re-read table with its re-read line and layout notice keeps three whole rows and focused rows in view at every size', async () => {
  test.setTimeout(120_000);
  const session = await opened(['table-fetched-exact']);
  try {
    const { page, app, data } = session;
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toBeVisible();
    // Fetching switched off behind the view's back: the next open re-reads ItemClass with the bundled definition and says why.
    await writeFile(path.join(data, 'settings.json'), JSON.stringify({ values: { fetchTableDefinitions: false }, changedAt: '2026-10-25T12:00:00Z' }));
    await pick(page, 'Item');
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .layout-only-match strong')).toHaveText('Matched by layout, build not listed');
    await expect(page.locator('.dh-reread').first()).toHaveText('Re-read with the bundled definition because fetching newer table definitions is off in Settings.');
    await everySize(page, app, 'reread');
  } finally { await session.close(); }
});

test('the activity sheet covers Tables Data, including its sticky column header, and closing it restores the shell exactly', async () => {
  test.setTimeout(120_000);
  const session = await opened(['table-partial', 'table-text-issue']);
  try {
    const { page, app } = session;
    await pick(page, 'ItemClass');
    const shellTop = () => page.evaluate(() => document.querySelector<HTMLElement>('.dh-app')!.scrollTop);
    for (const [width, height] of [[1280, 800], [960, 700], [600, 480]]) {
      await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
      await text(page, 2); await settle(page);
      const records = page.getByRole('group', { name: 'Show pane' }).filter({ visible: true }).getByRole('button', { name: 'Records', exact: true });
      if (await records.count()) await records.click();
      await expect(page.locator('.desktop-home.dh-shell-scroll')).toHaveCount(1);
      // With the grid's column header just under the window top, and at the end of the shell.
      for (const where of ['grid', 'end']) {
        const label = `${width}x${height} 200% at the ${where}`;
        await page.evaluate(where => { const app = document.querySelector<HTMLElement>('.dh-app')!, grid = document.querySelector<HTMLElement>('.dh-data:not([hidden]) .dh-grid-scroll')!; app.scrollTop = where === 'end' ? app.scrollHeight : app.scrollTop + grid.getBoundingClientRect().top - 2; }, where);
        await settle(page); await page.waitForTimeout(100); await settle(page);
        const floating = page.locator('.download-activity-floating');
        await expect(floating, label).toBeVisible();
        const before = await shellTop();
        // Opened by keyboard.
        await floating.focus(); await settle(page);
        await page.keyboard.press('Enter');
        await expect(page.getByRole('heading', { name: 'Downloads & activity', exact: true })).toBeFocused();
        // The panel's heading and its focus ring are drawn by the panel, not by the grid's sticky header cells under it. Inert content
        // isn't hit-tested, so the area's inert flag is lifted for the probe only; it doesn't change what is painted.
        const covered = await page.evaluate(() => {
          const h = document.querySelector('#download-surface-heading')!.getBoundingClientRect(), panel = document.getElementById('download-activity-panel')!;
          const inert = [...document.querySelectorAll<HTMLElement>('[inert]')];
          inert.forEach(el => { el.inert = false; });
          try {
            return [[h.left - 2, h.top - 2], [h.left + 4, (h.top + h.bottom) / 2], [h.right - 4, (h.top + h.bottom) / 2], [h.left + 4, h.bottom + 2]]
              .map(([x, y]) => ({ x, y, hit: document.elementFromPoint(x, y) })).filter(p => !p.hit || !panel.contains(p.hit))
              .map(p => `${Math.round(p.x)},${Math.round(p.y)} ${p.hit?.tagName}.${p.hit?.className}`);
          } finally { inert.forEach(el => { el.inert = true; }); }
        });
        expect.soft(covered, `${label}: area content paints over the open panel`).toEqual([]);
        await page.screenshot({ path: path.join(out, `sheet-${width}x${height}-200-${where}.png`) });
        await page.keyboard.press('Escape');
        await expect(page.locator('#download-activity-panel')).toBeHidden();
        await settle(page); await page.waitForTimeout(100); await settle(page);
        expect.soft(await shellTop(), `${label}: closing the panel didn't restore the shell`).toBe(before);
      }
    }
    await text(page, 1); await size(app, 1280, 800);
  } finally { await session.close(); }
});

test('a record ID with no admitted row keeps the Partial notice and the No admitted row box whole at every size', async () => {
  test.setTimeout(120_000);
  const session = await opened([]);
  try {
    const { page, app } = session;
    await pick(page, 'Item');
    const input = page.getByLabel('Raw record ID (exact match)');
    await input.fill('4294967295'); await input.press('Enter');
    const empty = page.getByText('No admitted row with ID 4294967295 in this scope. This does not establish global absence.', { exact: true });
    await expect(empty).toBeVisible();
    for (const [width, height, scale] of sizes) {
      const label = `no match ${width}x${height} ${scale * 100}%`;
      await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
      await text(page, scale); await settle(page);
      const records = page.getByRole('group', { name: 'Show pane' }).filter({ visible: true }).getByRole('button', { name: 'Records', exact: true });
      if (await records.count()) await records.click();
      await settle(page); await page.waitForTimeout(100); await settle(page);
      // Nothing in the Data view is cut off by it: every notice and the No admitted row box end inside it, at their full height.
      const cut = await page.evaluate(() => {
        const data = document.querySelector<HTMLElement>('.dh-data:not([hidden])')!, d = data.getBoundingClientRect();
        return [...data.querySelectorAll<HTMLElement>(':scope>.dh-notice, :scope>.dh-empty-result .dh-state-box')].filter(n => n.getClientRects().length > 0)
          .filter(n => { const r = n.getBoundingClientRect(); return r.height < 10 || r.bottom > d.bottom + 1 || r.top < d.top - 1 || n.scrollHeight > n.clientHeight + 1; })
          .map(n => n.textContent!.trim().slice(0, 40));
      });
      expect.soft(cut, `${label}: cut off`).toEqual([]);
      await empty.scrollIntoViewIfNeeded();
      expect.soft(await empty.evaluate(el => { const r = el.getBoundingClientRect(); return r.height > 0 && r.top >= -1 && r.bottom <= document.querySelector('.dh-statusbar')!.getBoundingClientRect().top + 1; }), `${label}: No admitted row can't be brought into view`).toBe(true);
      await page.screenshot({ path: path.join(out, `no-match-${width}x${height}-${scale * 100}.png`) });
    }
    await text(page, 1); await size(app, 1280, 800);
  } finally { await session.close(); }
});

test('the floating activity entry never covers the Data pager, by pointer or by keyboard, while the shell scrolls', async () => {
  test.setTimeout(120_000);
  const session = await opened(['table-partial', 'table-text-issue']);
  try {
    const { page, app } = session;
    await pick(page, 'ItemClass');
    const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    const pager = ['First', 'Previous', 'Next'].map(name => page.getByRole('button', { name: `${name} page of records`, exact: true }));
    // A page button is drawn at its centre (nothing, such as the floating entry, lies over it) and is wholly above the status bar.
    const clear = (button: typeof pager[number]) => button.evaluate(el => {
      const r = el.getBoundingClientRect(), hit = document.elementFromPoint((r.left + r.right) / 2, (r.top + r.bottom) / 2);
      const above = r.top >= -1 && r.bottom <= document.querySelector('.dh-statusbar')!.getBoundingClientRect().top + 1;
      return `${hit === el || el.contains(hit) ? 'clear' : `covered by ${hit?.className}`}${above ? '' : ' and cut off'}`;
    });
    for (const [width, height] of [[1280, 800], [1440, 1100], [600, 480]]) {
      const label = `${width}x${height} 200%`;
      await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
      await text(page, 2); await settle(page);
      const records = page.getByRole('group', { name: 'Show pane' }).filter({ visible: true }).getByRole('button', { name: 'Records', exact: true });
      if (await records.count()) await records.click();
      await expect(page.locator('.desktop-home.dh-shell-scroll')).toHaveCount(1);
      await page.evaluate(() => { document.querySelector<HTMLElement>('.dh-app')!.scrollTop = 0; }); await settle(page); await page.waitForTimeout(100); await settle(page);
      // Pointer: from the shell's top, where the header shows and the entry doesn't float yet, straight to its end. Every page button is
      // clear there, and Next turns the page rather than opening Activity.
      await page.evaluate(() => { const app = document.querySelector<HTMLElement>('.dh-app')!; app.scrollTop = app.scrollHeight; });
      await settle(page); await page.waitForTimeout(100); await settle(page);
      for (const button of pager) expect.soft(await clear(button), `${label}: ${await button.getAttribute('aria-label')} at the shell's end`).toBe('clear');
      await page.screenshot({ path: path.join(out, `pager-${width}x${height}-200.png`) });
      const box = (await pager[2].boundingBox())!;
      await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
      await expect(row(50), `${label}: Next turned the page`).toBeVisible();
      await expect(page.locator('#download-activity-panel')).toBeHidden();
      await pager[0].click(); await expect(row(0)).toBeVisible();
      await page.evaluate(() => { document.querySelector<HTMLElement>('.dh-app')!.scrollTop = 0; }); await settle(page); await page.waitForTimeout(100); await settle(page);
      // Keyboard, from the shell's top again: End, then Tab onto the pager. Each focused page button is revealed clear of the floating entry.
      await row(0).click(); await page.keyboard.press('End'); await expect(row(49)).toBeFocused();
      await expect(page.locator('.download-activity-floating'), label).toBeVisible();
      await page.keyboard.press('Tab'); await settle(page);
      expect.soft(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), `${label}: Tab after the grid`).toBe('First page of records');
      expect.soft(await clear(pager[0]), `${label}: First after End and Tab`).toBe('clear');
      for (const button of pager) {
        await button.focus(); await settle(page);
        expect.soft(await clear(button), `${label}: focused ${await button.getAttribute('aria-label')}`).toBe('clear');
      }
    }
    await text(page, 1); await size(app, 1280, 800);
  } finally { await session.close(); }
});

// A key that lands on the row already selected (End on the last, Home on the first, PageDown on the last, PageUp on the first) leaves
// no pending move behind: the next click on another row keeps focus and the grid where the click put them.
test('a key that stays on the selected row leaves no pending move for the next click', async () => {
  const session = await opened([]);
  try {
    const { page } = session;
    await pick(page, 'ItemClass');
    const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    const gridTop = () => page.locator('.dh-data:not([hidden]) .dh-grid-scroll').evaluate(el => el.scrollTop);
    for (const [start, key, other] of [[0, 'Home', 2], [49, 'End', 47], [49, 'PageDown', 46], [0, 'PageUp', 3]] as const) {
      await row(start).click(); await expect(row(start)).toBeFocused();
      await page.keyboard.press(key); await expect(row(start), `${key} on record ${start}`).toBeFocused();
      const scrolled = await gridTop();
      await row(other).click(); await settle(page);
      await expect(row(other), `click on record ${other} after ${key} on record ${start}`).toBeFocused();
      await expect(row(other)).toHaveAttribute('aria-pressed', 'true');
      expect(await gridTop(), `grid scroll after the click following ${key}`).toBe(scrolled);
    }
  } finally { await session.close(); }
});

// A press on the visible part of a page button turns the page (the shell doesn't scroll it from under the pointer), and when the
// selection notice then appears above the pager, the focused button is kept in view above the status bar and the floating entry.
test('a page button pressed where it shows turns the page and stays in view when the selection notice appears above it', async () => {
  test.setTimeout(60_000);
  const session = await opened([]);
  try {
    const { page, app } = session;
    await pick(page, 'ItemClass');
    await size(app, 1280, 800); await text(page, 2); await settle(page);
    await expect(page.locator('.desktop-home.dh-shell-scroll')).toHaveCount(1);
    const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    await row(3).click();
    for (const [name, shows] of [['Next', 50], ['Previous', 0]] as const) {
      const button = page.getByRole('button', { name: `${name} page of records`, exact: true });
      // Half of the button under the status bar; press its visible half.
      const at = await button.evaluate(el => { const app = document.querySelector<HTMLElement>('.dh-app')!, bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect().top, r = el.getBoundingClientRect(); app.scrollTop += r.top + r.height / 2 - bar; const n = el.getBoundingClientRect(); return { x: (n.left + n.right) / 2, y: Math.min(n.bottom, bar) - 6, top: n.top, bar }; });
      await settle(page);
      expect(at.top, `${name} starts partly hidden`).toBeLessThan(at.bar);
      await page.mouse.click(at.x, at.y);
      await expect(row(shows), `${name} turned the page`).toBeVisible();
      await expect(page.locator('#download-activity-panel')).toBeHidden();
      if (name === 'Next') await expect(page.locator('.dh-data:not([hidden]) .selection-note')).toBeVisible();
      await settle(page); await page.waitForTimeout(100); await settle(page);
      await expect(button).toBeFocused();
      const shown = await button.evaluate(el => { const r = el.getBoundingClientRect(), hit = document.elementFromPoint((r.left + r.right) / 2, (r.top + r.bottom) / 2); return { whole: r.top >= -1 && r.bottom <= document.querySelector('.dh-statusbar')!.getBoundingClientRect().top + 1, clear: hit === el || el.contains(hit), at: `${Math.round(r.top)}-${Math.round(r.bottom)}` }; });
      expect.soft(shown.whole, `${name} wholly above the status bar after the page turned (${shown.at})`).toBe(true);
      expect.soft(shown.clear, `${name} clear of the floating entry after the page turned (${shown.at})`).toBe(true);
    }
    await text(page, 1);
  } finally { await session.close(); }
});

// After the reader wheels the shell to its end, the page button they then activate (by click, or by Tab and Enter) is kept in view
// when the selection notice appears above the pager: activating it makes it wanted in view whatever the wheel did before.
test('a page button activated after wheeling the shell to its end stays in view when the selection notice appears', async () => {
  test.setTimeout(90_000);
  const session = await opened([]);
  try {
    const { page, app } = session;
    await pick(page, 'ItemClass');
    const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    const next = page.getByRole('button', { name: 'Next page of records', exact: true }), first = page.getByRole('button', { name: 'First page of records', exact: true });
    for (const [width, height] of [[1280, 800], [960, 700]]) {
      for (const how of ['click', 'Tab and Enter']) {
        const label = `${width}x${height} 200%, ${how}`;
        await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
        await text(page, 2); await settle(page);
        const records = page.getByRole('group', { name: 'Show pane' }).filter({ visible: true }).getByRole('button', { name: 'Records', exact: true });
        if (await records.count()) await records.click();
        await expect(page.locator('.desktop-home.dh-shell-scroll')).toHaveCount(1);
        await page.evaluate(() => { document.querySelector<HTMLElement>('.dh-app')!.scrollTop = 0; }); await settle(page);
        await row(5).click(); await expect(row(5)).toHaveAttribute('aria-pressed', 'true');
        // A real wheel over the window's middle, as a reader scrolls (the grid takes the wheel until its end, then the shell).
        await page.mouse.move(width / 2, height / 2);
        for (let i = 0; i < 40; i++) {
          await page.mouse.wheel(0, 400); await settle(page);
          if (await page.evaluate(() => { const app = document.querySelector<HTMLElement>('.dh-app')!; return app.scrollTop + app.clientHeight >= app.scrollHeight - 1; })) break;
        }
        await page.waitForTimeout(100); await settle(page);
        expect(await page.evaluate(() => { const app = document.querySelector<HTMLElement>('.dh-app')!; return app.scrollTop + app.clientHeight >= app.scrollHeight - 1; }), `${label}: wheeled to the shell's end`).toBe(true);
        if (how === 'click') await next.click();
        else { await expect(row(5)).toBeFocused(); for (let i = 0; i < 3; i++) await page.keyboard.press('Tab'); await expect(next).toBeFocused(); await page.keyboard.press('Enter'); }
        await expect(row(50), `${label}: Next turned the page`).toBeVisible();
        await expect(page.locator('.dh-data:not([hidden]) .selection-note')).toBeVisible();
        await settle(page); await page.waitForTimeout(150); await settle(page);
        await expect(next).toBeFocused();
        const shown = await next.evaluate(el => { const r = el.getBoundingClientRect(), hit = document.elementFromPoint((r.left + r.right) / 2, (r.top + r.bottom) / 2), app = document.querySelector<HTMLElement>('.dh-app')!; return { whole: r.top >= -1 && r.bottom <= document.querySelector('.dh-statusbar')!.getBoundingClientRect().top + 1, clear: hit === el || el.contains(hit), at: `${Math.round(r.top)}-${Math.round(r.bottom)}, shell ${Math.round(app.scrollTop)} of ${app.scrollHeight - app.clientHeight}` }; });
        expect.soft(shown.whole, `${label}: Next wholly above the status bar after the notice appeared (${shown.at})`).toBe(true);
        expect.soft(shown.clear, `${label}: Next clear of the floating entry after the notice appeared (${shown.at})`).toBe(true);
        await first.click(); await expect(row(0)).toBeVisible();
      }
    }
    await text(page, 1); await size(app, 1280, 800);
  } finally { await session.close(); }
});

// Next already has focus and the reader has scrolled the shell so it sits half under the status bar. Activating it there (Enter, or a
// click on its visible half) makes it wanted in view again, so the selection notice its page turn adds above it can't push it under.
test('activating a focused page button that was scrolled half out of view keeps it in view when the selection notice appears', async () => {
  test.setTimeout(60_000);
  const session = await opened([]);
  try {
    const { page, app } = session;
    await pick(page, 'ItemClass');
    await size(app, 1280, 800); await text(page, 2); await settle(page);
    await expect(page.locator('.desktop-home.dh-shell-scroll')).toHaveCount(1);
    const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    const next = page.getByRole('button', { name: 'Next page of records', exact: true }), first = page.getByRole('button', { name: 'First page of records', exact: true });
    await row(5).click();
    for (const how of ['Enter', 'click']) {
      await next.focus(); await settle(page);
      // The reader scrolls the shell so Next's lower half is under the status bar; focus stays on it.
      const at = await next.evaluate(el => { const app = document.querySelector<HTMLElement>('.dh-app')!, bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect().top, r = el.getBoundingClientRect(); app.scrollTop += r.top + r.height / 2 - bar; const n = el.getBoundingClientRect(); return { x: (n.left + n.right) / 2, y: Math.min(n.bottom, bar) - 6, top: n.top, bottom: n.bottom, bar }; });
      await settle(page); await page.waitForTimeout(100); await settle(page);
      await expect(next).toBeFocused();
      expect(at.top < at.bar && at.bottom > at.bar, `${how}: Next starts half under the status bar`).toBe(true);
      if (how === 'Enter') await page.keyboard.press('Enter'); else await page.mouse.click(at.x, at.y);
      await expect(row(50), `${how}: Next turned the page`).toBeVisible();
      await expect(page.locator('.dh-data:not([hidden]) .selection-note')).toBeVisible();
      await settle(page); await page.waitForTimeout(150); await settle(page);
      await expect(next).toBeFocused();
      const shown = await next.evaluate(el => { const r = el.getBoundingClientRect(); return { whole: r.top >= -1 && r.bottom <= document.querySelector('.dh-statusbar')!.getBoundingClientRect().top + 1, at: `${Math.round(r.top)}-${Math.round(r.bottom)}` }; });
      expect.soft(shown.whole, `${how}: Next wholly above the status bar after the notice appeared (${shown.at})`).toBe(true);
      await first.click(); await expect(row(0)).toBeVisible();
    }
    await text(page, 1);
  } finally { await session.close(); }
});

// Shift+Tab back from the pager to the focused row reveals the row with its sticky column header, as arrow moves do.
test('returning focus to a grid row brings its column header into view at 600x480 and 200% text', async () => {
  test.setTimeout(60_000);
  const session = await opened([]);
  try {
    const { page, app } = session;
    await pick(page, 'ItemClass');
    await size(app, 600, 480); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([600, 480]);
    await text(page, 2); await settle(page);
    const row = (id: number) => page.getByRole('button', { name: `Inspect record ${id}`, exact: true });
    await row(0).click(); await page.keyboard.press('End'); await expect(row(49)).toBeFocused();
    await page.keyboard.press('Tab'); await expect(page.getByRole('button', { name: 'First page of records', exact: true })).toBeFocused();
    for (const [key, id] of [['Shift+Tab', 49], ['ArrowUp', 48], ['ArrowUp', 47]] as const) {
      await page.keyboard.press(key); await expect(row(id)).toBeFocused();
      const f = await focusVisible(page);
      expect.soft(f.whole, `${f.name} after ${key} not wholly visible (${f.at})`).toBe(true);
      expect.soft(f.header, `column header off screen with ${f.name} focused after ${key} (${f.at})`).toBe(true);
    }
    await text(page, 1);
  } finally { await session.close(); }
});

