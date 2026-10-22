import { test, expect, type ElectronApplication, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';

// D071 (#35): every way into Files × every file list state × both reference sizes × 100% and 200% text. Each combination
// keeps a whole row in view under the notes, nothing in the pane overflows, and the footer with its page controls sits
// above the status bar. The authored engine advertises file-catalog-v1 and tables-follow-v1.
const entries = ['browse', 'follow'] as const;
const states = ['preparing', 'retrying', 'not-confirmed', 'stopped', 'failed', 'full', 'unsupported', 'ready-offered', 'ready-list'] as const;
const sizes = [[1280, 800], [1440, 1100]] as const;
const scales = [1, 2] as const;
type Entry = typeof entries[number]; type State = typeof states[number];

// Written before Open: the fixture reports these states from the first status read.
const before: Partial<Record<State, [string, string]>> = {
  retrying: ['file-catalog-retrying', 'on'], failed: ['file-catalog-failed', 'failed'], full: ['file-catalog-failed', 'full'], unsupported: ['file-catalog-failed', 'unsupported'],
};
// What each note says at 100% and in its 200% form; the other form's words are hidden.
const words: Partial<Record<State, { wide: string; compact: string }>> = {
  retrying: { wide: 'it is tried again shortly', compact: 'Attempt 1 failed, retrying ·' },
  'not-confirmed': { wide: 'Status not confirmed', compact: 'Not confirmed ·' },
  stopped: { wide: 'The rows written so far are kept', compact: 'Rows written so far are kept.' },
  failed: { wide: 'It failed three times.', compact: 'Failed three times. Retry or reopen the build.' },
  full: { wide: 'Free some disk space, then retry.', compact: 'Disk full. Free some space, then retry.' },
  unsupported: { wide: 'Trying again reads the same bytes', compact: 'Can’t be read. Retrying won’t help.' },
};

async function enter(page: Page, app: ElectronApplication, data: string, entry: Entry) {
  await size(app, 1440, 1100);
  await writeFile(path.join(data, 'release-table'), 'release');
  await page.locator('.build-row').first().click();
  await page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(data, 'release-open'), 'release');
  await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
  if (entry === 'browse') { await page.getByRole('button', { name: 'Browse in Files' }).click(); return; }
  await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
  await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
  await page.getByRole('button', { name: 'Inspect record 1', exact: true }).click();
  await page.locator('.dh-tables .dh-inspector').getByRole('button', { name: 'Find FileDataID 9 in Files', exact: true }).click();
  await expect(page.locator('.file-target')).toContainText('FileDataID 9 can’t be looked up yet');
}

async function reach(page: Page, data: string, entry: Entry, state: State) {
  const pane = page.locator('.dh-files .dh-pane.dh-list');
  await expect(pane.getByText('Object 1', { exact: true })).toBeVisible();
  if (state === 'not-confirmed') {
    await expect(page.getByRole('note', { name: 'Preparing file list…' })).toBeVisible();
    await writeFile(path.join(data, 'file-catalog-status-error'), 'fail');
    await expect(pane).toContainText('Status not confirmed');
  }
  if (state === 'stopped') {
    await page.getByRole('note', { name: 'Preparing file list…' }).getByRole('button', { name: /^Stop preparing file list for / }).click();
    await expect(page.getByRole('note', { name: 'File list preparation stopped' })).toBeVisible();
  }
  if (state === 'ready-offered' || state === 'ready-list') {
    await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
    const show = page.locator('.dh-pane-foot').getByRole('button', { name: 'Show file list' });
    await expect(show).toBeVisible();
    if (entry === 'follow') await expect(page.locator('.file-target')).toContainText('FileDataID 9 can be looked up now');
    if (state === 'ready-list') {
      await show.click();
      if (entry === 'browse') await expect(page.getByRole('listbox', { name: 'Files, one row per FileDataID' }).getByRole('option').first()).toContainText('800001');
      else { await expect(page.getByRole('searchbox', { name: 'Search by FileDataID or content key' })).toHaveValue('9'); await expect(pane.getByText('No files match', { exact: true })).toBeVisible(); }
    }
  }
  if (state === 'retrying' || state === 'preparing') await expect(page.getByRole('note', { name: 'Preparing file list…' })).toBeVisible();
  if (state === 'failed' || state === 'full' || state === 'unsupported') await expect(page.getByRole('note', { name: 'File list not prepared' })).toBeVisible();
}

async function fits(page: Page, app: ElectronApplication, entry: Entry, state: State, width: number, height: number, scale: number) {
  const label = `${entry} · ${state} · ${width}×${height} · ${scale * 100}%`;
  await size(app, width, height); await text(page, scale);
  // The compact layout follows the text size on its next frame; measure it, not the frame before.
  await page.waitForFunction(compact => !!document.querySelector('.desktop-home.dh-compact') === compact, scale === 2);
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
  const g = await page.evaluate(() => {
    const pane = document.querySelector<HTMLElement>('.dh-files .dh-pane.dh-list')!, foot = pane.querySelector<HTMLElement>('.dh-pane-foot')!;
    const statusTop = document.querySelector<HTMLElement>('.dh-statusbar')!.getBoundingClientRect().top;
    const pages = pane.querySelector<HTMLElement>('[aria-label="Catalog pages"]');
    const scroll = pane.querySelector<HTMLElement>('.dh-list-scroll') ?? pane.querySelector<HTMLElement>('.dh-pane-scroll')!;
    scroll.scrollTop = 0;
    const view = scroll.getBoundingClientRect(), row = scroll.querySelector<HTMLElement>('[role=option]');
    // With no row (a FileDataID with no match), the state box saying so is what must be in view.
    const target = row ?? [...scroll.querySelectorAll<HTMLElement>('.dh-state-box')].at(-1) ?? null;
    const box = target?.getBoundingClientRect();
    const notice = pane.querySelector<HTMLElement>('.file-target'), note = pane.querySelector<HTMLElement>('.dh-catalog-missing>.dh-state-box');
    const line = notice?.querySelector<HTMLElement>('.dh-text');
    return {
      row: !!row, inView: !!box && box.top >= view.top - 1 && box.bottom <= view.bottom + 1,
      at: box ? `${Math.round(box.top)}-${Math.round(box.bottom)} in ${Math.round(view.top)}-${Math.round(view.bottom)}` : 'none',
      overflow: pane.scrollHeight - pane.clientHeight, footBottom: foot.getBoundingClientRect().bottom, pagesBottom: pages?.getBoundingClientRect().bottom ?? null, statusTop,
      pageScroll: document.documentElement.scrollHeight - innerHeight, sideways: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      edges: notice && note ? [notice.getBoundingClientRect().left - note.getBoundingClientRect().left, notice.getBoundingClientRect().right - note.getBoundingClientRect().right] : null,
      lines: line ? line.getBoundingClientRect().height / parseFloat(getComputedStyle(line).fontSize) : null,
      parts: [...pane.children].map(c => `${c.className}:${Math.round(c.getBoundingClientRect().height)}`).join(' '),
    };
  });
  const directory = path.join(root, 'artifacts/files-fit'); await mkdir(directory, { recursive: true });
  await page.screenshot({ path: path.join(directory, `${entry}-${state}-${width}x${height}-${scale * 100}.png`) });
  const pane = page.locator('.dh-files .dh-pane.dh-list'), shown = words[state];
  if (shown) {
    await expect(pane.getByText(scale === 2 ? shown.compact : shown.wide, { exact: false }).first(), label).toBeVisible();
    await expect(pane.getByText(scale === 2 ? shown.wide : shown.compact, { exact: false }).first(), label).toBeHidden();
  }
  // Show file list is in the footer at 100% and in the ready note at 200%, so the footer keeps one row.
  if (state === 'ready-offered') await expect(pane.locator(scale === 2 ? '.dh-catalog-missing' : '.dh-pane-foot').getByRole('button', { name: 'Show file list' }), label).toBeVisible();
  await text(page, 1);
  expect(g.row || (entry === 'follow' && state === 'ready-list'), `${label}: an object row exists`).toBe(true);
  expect(g.inView, `${label}: no whole row in view (${g.at}; ${g.parts})`).toBe(true);
  expect(g.overflow, `${label}: the pane overflows (${g.parts})`).toBeLessThanOrEqual(1);
  expect(g.footBottom, `${label}: footer under the status bar`).toBeLessThanOrEqual(g.statusTop + 1);
  if (g.pagesBottom !== null) expect(g.pagesBottom, `${label}: page controls under the status bar`).toBeLessThanOrEqual(g.statusTop + 1);
  expect(g.pageScroll, `${label}: the page scrolls`).toBeLessThanOrEqual(0);
  expect(g.sideways, `${label}: the page scrolls sideways`).toBeLessThanOrEqual(1);
  // A followed FileDataID's notice lines up with the note under it, and is one line at 200%.
  if (g.edges) for (const edge of g.edges) expect(Math.abs(edge), `${label}: notice and note edges differ by ${edge}px`).toBeLessThanOrEqual(1);
  if (g.lines !== null && scale === 2) expect(g.lines, `${label}: the Follow notice wraps`).toBeLessThan(1.8);
}

for (const entry of entries) for (const state of states) {
  test(`Files via ${entry}, ${state}: a whole row in view, no overflow and the page controls above the status bar at both sizes and text scales`, async () => {
    const session = await launch(true, false, false, false, false, false, false, false, false, false, true, true);
    try {
      const { page, app, data } = session, file = before[state];
      if (file) await writeFile(path.join(data, file[0]), file[1]);
      await enter(page, app, data, entry);
      await reach(page, data, entry, state);
      for (const [width, height] of sizes) for (const scale of scales) await fits(page, app, entry, state, width, height, scale);
      if (state === 'full' || state === 'unsupported') {
        // In a narrower window the failed note's code goes first; its message and next step stay.
        await size(app, 900, 800); await text(page, 2); await page.waitForFunction(() => !!document.querySelector('.desktop-home.dh-compact'));
        const note = page.getByRole('note', { name: 'File list not prepared' });
        await expect(note.getByText(words[state]!.compact)).toBeVisible(); await expect(note.locator('.dh-note-code')).toBeHidden();
        await text(page, 1);
      }
    } finally { await session.close(); }
  });
}

test('the fit matrix covers every entry, state, size and text scale', () => {
  expect(entries.length * states.length * sizes.length * scales.length).toBe(72);
});
