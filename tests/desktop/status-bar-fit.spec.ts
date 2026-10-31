import { test, expect, type ElectronApplication, type Page } from '@playwright/test';
import { existsSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';

// #134: a status bar is one line inside the window. Each fact either fits or ends in an ellipsis, with its whole text as a tooltip
// and as a tab stop that shows it whole on focus; nothing runs past the bar's edge or is clipped without an ellipsis, by the bar or
// by a box inside it. Facts give way in a fixed order, and the fixture badge is never cut. At 600x480 and 200% the Home shell's
// identity and fixture badge overran the bar in any font, at 1280x800 and 200% in DejaVu (e2e's pinned font, #138) the activity
// summary was cut with no way to read it, and Discovery's facts were all cut together and its busy line clipped mid-word.
// VIBEMINER_E2E_SYSTEM_FONTS=1 runs the same checks in the machine's fonts.

// Facts in the order they give way; each must be gone before the next is cut. Facts listed under never are never cut.
const rules: Record<string, { order: string[]; never: string[] }> = {
  '.dh-statusbar': { order: [':scope>.dh-secondary, .dh-status-slot>span', ':scope>.dh-status-end', ':scope>span:first-child'], never: [':scope>.dh-fixture'] },
  '.st-statusbar': { order: [':scope>.st-secondary', ':scope>.st-status-end', ':scope>span:first-child'], never: [':scope>.st-fixture'] },
  '.dv-statusbar': { order: [':scope>.dv-secondary', ':scope>.dv-status-end', ':scope>.dv-message', ':scope>.dv-latest', ':scope>.dv-busy'], never: [] },
};
type Fit = { problems: string[]; cut: string[] };
const measure = (page: Page, bar: string) => page.evaluate(([sel, rule]): Fit => {
  // Any cut at all shows an ellipsis, so this measures the text against the box rather than trusting rounded scrollWidth.
  const isCut = (f: HTMLElement) => { const c = getComputedStyle(f), range = document.createRange(); range.selectNodeContents(f); return range.getBoundingClientRect().width > f.getBoundingClientRect().width - parseFloat(c.paddingLeft) - parseFloat(c.paddingRight) - parseFloat(c.borderLeftWidth) - parseFloat(c.borderRightWidth) + .1; };
  const el = document.querySelector<HTMLElement>(sel)!, cs = getComputedStyle(el), r = el.getBoundingClientRect();
  const start = r.left + parseFloat(cs.paddingLeft), end = r.left + el.clientWidth - parseFloat(cs.paddingRight);
  const whole = (f: Element) => f.textContent!.replace(/\s+/g, ' ').trim(), shown = (f: HTMLElement) => f.getBoundingClientRect().width > .5;
  // Every box inside the bar, not only its direct facts: a box inside a fact can clip the text too.
  const boxes = [...el.querySelectorAll<HTMLElement>('*')].filter(f => !['inline', 'contents'].includes(getComputedStyle(f).display) && f.getClientRects().length && whole(f));
  const problems = [`bar overflows by ${el.scrollWidth - el.clientWidth}px`].filter(() => el.scrollWidth > el.clientWidth + 1), cut: string[] = [];
  if (r.right > innerWidth + 1) problems.push('bar past the window');
  for (const f of boxes) {
    const b = f.getBoundingClientRect(), text = whole(f), c = getComputedStyle(f);
    if (b.width > 0 && (b.left < start - 1 || b.right > end + 1)) problems.push(`"${text}" outside the bar (${Math.round(b.left)}-${Math.round(b.right)} of ${Math.round(start)}-${Math.round(end)})`);
    if (!isCut(f)) { if (f.title === text && shown(f)) problems.push(`"${text}" fits but has a tooltip`); continue; }
    cut.push(text);
    if (f.title !== text) problems.push(`"${text}" cut without its tooltip`);
    if (shown(f) && f.tabIndex !== 0) problems.push(`"${text}" cut and not focusable`);
    if (!shown(f) && f.hasAttribute('tabindex')) problems.push(`"${text}" hidden but a tab stop`);
    // text-overflow only works in a block container: a flex or grid fact clips mid-word.
    if (shown(f) && (c.textOverflow !== 'ellipsis' || !['block', 'inline-block'].includes(c.display))) problems.push(`"${text}" cut without an ellipsis (${c.display})`);
  }
  for (const q of rule.never) for (const f of el.querySelectorAll<HTMLElement>(q)) if (isCut(f)) problems.push(`"${whole(f)}" is never cut`);
  const ranked = rule.order.map(q => [...el.querySelectorAll<HTMLElement>(q)].filter(f => f.getClientRects().length));
  ranked.forEach((group, i) => { for (const f of group) if (isCut(f) && shown(f)) for (const before of ranked.slice(0, i).flat()) if (shown(before)) problems.push(`"${whole(f)}" cut while "${whole(before)}" still shows`); });
  return { problems, cut };
}, [bar, rules[bar]] as const);

type Layout = [width: number, height: number, scale: number];
// Sets the window and text size, then waits for the bar to settle: titles and tab stops follow the layout a frame later.
async function settle(page: Page, app: ElectronApplication, bar: string, name: string, [width, height, scale]: Layout) {
  await size(app, width, height);
  await expect.poll(() => page.evaluate(() => innerWidth)).toBe(width);
  await text(page, scale);
  await expect.poll(async () => (await measure(page, bar)).problems, name).toEqual([]);
}

async function check(page: Page, app: ElectronApplication, bar: string, label: string) {
  const out = path.join(root, 'artifacts/status-bar-fit'); await mkdir(out, { recursive: true });
  await expect(page.locator(bar)).toBeVisible();
  const cuts: string[] = [];
  for (const [width, height] of [[600, 480], [1280, 800], [1440, 1100]]) {
    for (const scale of [1, 2]) {
      const name = `${label} ${width}x${height} ${scale * 100}%`;
      await settle(page, app, bar, name, [width, height, scale]);
      cuts.push(...(await measure(page, bar)).cut.map(c => `${name}: ${c}`));
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth), `${name} page scrolls sideways`).toBeLessThanOrEqual(0);
      await page.locator(bar).screenshot({ path: path.join(out, `${name.replace(/[ %]+/g, '-')}.png`) });
    }
  }
  await text(page, 1); await size(app, 1280, 800);
  return cuts;
}

// Runs each animation frame the page asks for ms late (0 restores them), so a layout the hook hasn't caught up with yet lasts.
const slowFrames = (page: Page, ms: number) => page.evaluate(delay => {
  const w = window as typeof window & { realFrame?: typeof requestAnimationFrame; realCancel?: typeof cancelAnimationFrame };
  w.realFrame ??= requestAnimationFrame.bind(window); w.realCancel ??= cancelAnimationFrame.bind(window);
  if (!delay) { window.requestAnimationFrame = w.realFrame; window.cancelAnimationFrame = w.realCancel; return; }
  const frame = w.realFrame;
  window.requestAnimationFrame = callback => window.setTimeout(() => frame(callback), delay);
  window.cancelAnimationFrame = id => window.clearTimeout(id);
}, ms);

// Tab reaches each cut fact, and focus shows its whole text on a line above the bar, inside the window. The tab stops are counted
// only once the bar has settled at this layout: before that they are the last layout's, and a fact gone since is still one (#143).
async function readByKeyboard(page: Page, app: ElectronApplication, bar: string, label: string, layout: Layout) {
  await settle(page, app, bar, label, layout);
  const facts = page.locator(`${bar} [data-cut-title][tabindex="0"]`);
  const count = await facts.count();
  expect(count, `${label}: something is cut here`).toBeGreaterThan(0);
  for (let i = 0; i < count; i++) {
    const fact = facts.nth(i), whole = await fact.getAttribute('title');
    await expect(fact, `${label}: "${whole}" shows`).toBeVisible();
    await fact.focus();
    const full = page.locator('.vm-status-full');
    await expect(full, `${label}: focus shows "${whole}"`).toHaveText(whole!);
    const fit = await full.evaluate((el, sel) => { const r = el.getBoundingClientRect(), b = document.querySelector(sel)!.getBoundingClientRect(); return { inside: r.left >= 0 && r.right <= innerWidth && r.top >= 0, above: r.bottom <= b.top + 1 }; }, bar);
    expect(fit, `${label}: "${whole}" in the window, above the bar`).toEqual({ inside: true, above: true });
  }
  await page.keyboard.press('Shift+Tab');
  await expect(page.locator(`${bar} :focus`), `${label}: Shift+Tab leaves the bar`).toHaveCount(0);
  await expect(page.locator('.vm-status-full')).toHaveCount(0);
}

test('status bar facts stay inside the window, give way in order, and a cut fact can be read whole by pointer and keyboard', async () => {
  test.setTimeout(180_000);
  const session = await launch(true, false, false, false, false, false, false, false, false, false, true, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await expect(page.locator('.build-row')).toHaveCount(2);
    // Discovery while a refresh is held: the busy line, then the check outcome.
    await page.getByRole('button', { name: 'Refresh builds', exact: true }).click();
    await expect.poll(() => existsSync(path.join(data, 'waiting-wow')) && existsSync(path.join(data, 'waiting-wowt'))).toBe(true);
    const discovery = page.locator('.dv-statusbar');
    await expect(discovery.locator('.dv-busy')).toHaveText('Checking 2 products · 0 of 2 done…');
    await check(page, app, '.dv-statusbar', 'Discovery busy');
    // Once the other facts are gone the busy line fits at every size, so a narrower bar forces it to be cut: it must end in an
    // ellipsis (as a flex row it was clipped mid-word, "0 of 2 don") and be focusable.
    await size(app, 600, 480); await text(page, 2);
    await page.evaluate(() => { const s = document.createElement('style'); s.id = 'narrow-bar'; s.textContent = '.discovery-screen .dv-statusbar{padding-right:50%}'; document.head.append(s); });
    await expect.poll(async () => (await measure(page, '.dv-statusbar')).problems, 'forced busy cut').toEqual([]);
    expect((await measure(page, '.dv-statusbar')).cut, 'the busy line is cut').toContain('Checking 2 products · 0 of 2 done…');
    await readByKeyboard(page, app, '.dv-statusbar', 'Discovery busy, forced cut', [600, 480, 2]);
    // The busy line ending while it has focus takes its full-text line with it.
    await discovery.locator('.dv-busy').focus();
    await expect(page.locator('.vm-status-full')).toHaveText('Checking 2 products · 0 of 2 done…');
    await writeFile(path.join(data, 'release-wowt'), 'release'); await writeFile(path.join(data, 'release-wow'), 'release');
    await expect(discovery.locator('.dv-message')).toContainText('Check completed with problems for 1 of 2 products');
    await expect(discovery.locator('.dv-busy')).toHaveCount(0);
    await expect(page.locator('.vm-status-full')).toHaveCount(0);
    await page.evaluate(() => document.getElementById('narrow-bar')!.remove());
    await text(page, 1); await size(app, 1280, 800);
    await check(page, app, '.dv-statusbar', 'Discovery checked');

    // A failed file list keeps the activity summary ("File list · ROOT and install · needs attention") in the status bar.
    await writeFile(path.join(data, 'file-catalog-failed'), 'failed');
    await writeFile(path.join(data, 'release-table'), 'release');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await page.getByRole('button', { name: 'Browse in Files' }).click();
    const status = page.locator('.dh-statusbar');
    await expect(status.locator('.dh-fixture')).toContainText('Authored test data');
    await expect(status.locator('.dh-warn')).toHaveText('File list · ROOT and install · needs attention');
    await check(page, app, '.dh-statusbar', 'Files');
    // At 600x480 and 200% the summary is gone, the identity is cut and the fixture badge is whole.
    await size(app, 600, 480); await text(page, 2);
    await expect(status.locator(':scope>.dh-status-end')).toHaveAttribute('title', 'File list · ROOT and install · needs attention');
    await expect(status.locator(':scope>span').first()).toHaveAttribute('title', 'wow 12.1.5.70448 · US · enUS');
    await expect(status.locator(':scope>.dh-fixture')).not.toHaveAttribute('title');
    await readByKeyboard(page, app, '.dh-statusbar', 'Files 600x480 200%', [600, 480, 2]);
    await text(page, 1); await size(app, 1280, 800);

    await page.locator('.dh-toolbar').getByRole('button', { name: 'Settings' }).click();
    await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeFocused();
    await check(page, app, '.st-statusbar', 'Settings');
    await page.getByRole('button', { name: /← Back to / }).first().click();

    // Discovery with the check outcome and the activity summary side by side.
    await page.locator('.dh-toolbar').getByRole('button', { name: /Choose build/ }).click();
    await expect(discovery.locator('.dv-status-end')).toHaveText('File list · ROOT and install · needs attention');
    await check(page, app, '.dv-statusbar', 'Discovery with activity');
    // #143: at 1280x800 and 200% the summary is a cut tab stop, and at 600x480 it is squeezed to nothing. The hook drops that tab
    // stop a frame after the resize, so a step that counted tab stops straight away focused the hidden summary instead of the facts
    // that are cut there. Slow frames keep that stale layout up long enough to be counted on every run.
    await settle(page, app, '.dv-statusbar', 'Discovery 1280x800 200%', [1280, 800, 2]);
    await expect(discovery.locator(':scope>.dv-status-end')).toHaveAttribute('tabindex', '0');
    await slowFrames(page, 500);
    await readByKeyboard(page, app, '.dv-statusbar', 'Discovery 600x480 200%', [600, 480, 2]);
    await slowFrames(page, 0);
    await text(page, 1); await size(app, 1280, 800);
  } finally { await session.close(); }
});
