import { test, expect, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, size, text } from './helpers';

// #74: a focused control in the open activity panel stays in view inside the panel when the layout or text size changes under it,
// including the switch to the full-window sheet at 600x480 when the text goes from 100% to 200%.
const settle = (page: Page) => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));

// The focused control is wholly inside the panel's scroll view, below its sticky heading, above the status bar and inside the window.
async function inPanelView(page: Page) {
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement, panel = document.getElementById('download-activity-panel')!, r = el.getBoundingClientRect(), p = panel.getBoundingClientRect();
    const head = panel.querySelector<HTMLElement>('.download-panel-heading')?.getBoundingClientRect().bottom ?? p.top;
    const top = Math.max(p.top, head, 0), bottom = Math.min(p.top + panel.clientHeight, document.querySelector('.dh-statusbar')!.getBoundingClientRect().top, innerHeight);
    return { name: el.textContent?.trim(), inPanel: panel.contains(el), whole: r.height > 0 && r.top >= top - 1 && r.bottom <= bottom + 1, at: `${Math.round(r.top)}-${Math.round(r.bottom)} in ${Math.round(top)}-${Math.round(bottom)}; panel scroll ${Math.round(panel.scrollTop)} of ${panel.scrollHeight - panel.clientHeight}` };
  });
}

test('a focused panel control stays in view inside the panel after the text size changes at 600x480', async () => {
  const session = await launch(true, false, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    // A running build download, so its card carries Pause.
    await page.getByRole('switch', { name: /^Download mode / }).click();
    await expect(page.locator('.retention-state')).toHaveText('Download all queued');
    await writeFile(path.join(data, 'retention-state.json'), JSON.stringify({ state: 'running', progress: { stage: 'Transferring encoded object', unit: 'bytes', completed: '25', total: '100' }, wireBytesThisSession: '300' }));
    const status = page.getByRole('button', { name: /^Status / });
    await status.click(); await page.getByRole('dialog', { name: 'Local content & storage' }).getByRole('button', { name: 'Check download status' }).click(); await status.click();
    await size(app, 600, 480); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([600, 480]);
    const panel = page.locator('#download-activity-panel');
    if (!await panel.isVisible()) await page.locator('.download-activity-trigger:visible, .dv-chip:visible').click();
    const pause = panel.getByRole('button', { name: 'Pause build download' });
    await expect(pause).toBeVisible();
    // #122: the running state written above reaches the panel on the fixture's next poll. Waiting for it keeps that progress update out of
    // the steps below. Landing just after a step's settle, its commit grew the heading over Pause and the step measured before the next
    // frame, whose ResizeObserver run lets the keeper move Pause back below the heading. The step after the loop sends such an update on purpose.
    await expect(panel.getByText(/25 of 100 bytes/)).toBeVisible({ timeout: 10_000 });
    const steady = async () => { await settle(page); await page.waitForTimeout(150); await settle(page); };
    for (const [from, to] of [[1, 2], [2, 1], [1, 2]]) {
      await text(page, from); await settle(page);
      // Focused and scrolled into the panel's view, as Tab would leave it (focus() alone doesn't scroll an already focused control).
      await pause.focus(); await pause.evaluate(el => { const panel = el.closest<HTMLElement>('#download-activity-panel')!, head = panel.querySelector<HTMLElement>('.download-panel-heading')!.offsetHeight, p = panel.getBoundingClientRect(), r = el.getBoundingClientRect(); if (r.bottom > p.top + panel.clientHeight) panel.scrollTop += r.bottom - p.top - panel.clientHeight; else if (r.top < p.top + head) panel.scrollTop -= p.top + head - r.top; }); await settle(page);
      const before = await inPanelView(page);
      expect(before.whole, `Pause in view at ${from * 100}% (${before.at})`).toBe(true);
      await text(page, to); await settle(page); await page.waitForTimeout(150); await settle(page);
      await expect(pause).toBeFocused();
      const after = await inPanelView(page);
      expect.soft(after.whole, `Pause in view after ${from * 100}% → ${to * 100}% (${after.at})`).toBe(true);
    }
    // A progress update in the sheet whose stage wraps grows the heading and the card above Pause, which sits right under the heading: the
    // browser's scroll anchoring keeps Pause where it was on screen, now under the taller heading, and the keeper brings it back (#122).
    await writeFile(path.join(data, 'retention-state.json'), JSON.stringify({ state: 'running', progress: { stage: 'Transferring encoded objects from the authored archive', unit: 'bytes', completed: '40', total: '100' }, wireBytesThisSession: '300' }));
    await expect(panel.getByText(/40 of 100 bytes/)).toBeVisible({ timeout: 10_000 }); await steady();
    await expect(pause).toBeFocused();
    const updated = await inPanelView(page);
    expect.soft(updated.whole, `Pause in view after a progress update grew the sheet heading at 200% (${updated.at})`).toBe(true);
    // #106: a layout too short to hold Pause doesn't drop it, nor does the scroll event of the panel's own scroll there. On the way to the
    // sheet the compact panel passes through such a layout (its view below the window), and a slow machine could deliver that scroll
    // event after the sheet, leaving Pause under a heading that then grew. 400% text makes the heading taller than the window, so every
    // step here goes through that state; the stage update grows the heading while Pause can't show.
    await text(page, 4); await steady();
    await text(page, 2); await steady();
    const back = await inPanelView(page);
    expect.soft(back.whole, `Pause in view after 200% → 400% → 200% (${back.at})`).toBe(true);
    await text(page, 4); await steady();
    await writeFile(path.join(data, 'retention-state.json'), JSON.stringify({ state: 'running', progress: { stage: 'Transferring encoded objects from the authored archive while verifying each stored copy against its receipt', unit: 'bytes', completed: '50', total: '100' }, wireBytesThisSession: '300' }));
    await expect(panel.getByText(/50 of 100 bytes/)).toBeVisible({ timeout: 10_000 }); await steady();
    await text(page, 2); await steady();
    await expect(pause).toBeFocused();
    const grown = await inPanelView(page);
    expect.soft(grown.whole, `Pause in view after its heading grew at 400% and the text went back to 200% (${grown.at})`).toBe(true);
    // A layout change inside the panel (the window shrinking) keeps it in view too.
    await text(page, 2); await pause.focus(); await settle(page);
    await size(app, 600, 400); await expect.poll(() => page.evaluate(() => innerHeight)).toBe(400); await settle(page); await page.waitForTimeout(150); await settle(page);
    const shrunk = await inPanelView(page);
    expect.soft(shrunk.whole, `Pause in view after the window shrank (${shrunk.at})`).toBe(true);
    await text(page, 1);
  } finally { await session.close(); }
});

test('a panel scrolled away from its focused control stays where the reader left it through progress updates and resizes', async () => {
  const session = await launch(true, false, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await page.getByRole('switch', { name: /^Download mode / }).click();
    await expect(page.locator('.retention-state')).toHaveText('Download all queued');
    const progress = (stage: string, completed: string) => writeFile(path.join(data, 'retention-state.json'), JSON.stringify({ state: 'running', progress: { stage, unit: 'bytes', completed, total: '100' }, wireBytesThisSession: '300' }));
    await progress('Transferring encoded object', '25');
    const status = page.getByRole('button', { name: /^Status / });
    await status.click(); await page.getByRole('dialog', { name: 'Local content & storage' }).getByRole('button', { name: 'Check download status' }).click(); await status.click();
    await size(app, 600, 480); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([600, 480]);
    await text(page, 2); await settle(page);
    const panel = page.locator('#download-activity-panel');
    if (!await panel.isVisible()) await page.locator('.download-activity-trigger:visible, .dv-chip:visible').click();
    const pause = panel.getByRole('button', { name: 'Pause build download' });
    await pause.focus(); await settle(page);
    // The reader wheels the panel to its end, away from Pause.
    const box = (await panel.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    for (let i = 0; i < 10; i++) { await page.mouse.wheel(0, 400); await settle(page); }
    await page.waitForTimeout(100); await settle(page);
    const top = () => panel.evaluate(el => Math.round(el.scrollTop));
    const left = await top();
    expect(left, 'the panel scrolled').toBeGreaterThan(0);
    await expect(pause).toBeFocused();
    // A progress update whose stage label wraps, then a smaller window: the panel stays where the reader left it.
    await progress('Transferring encoded objects from the authored archive while verifying each stored copy against its receipt', '50');
    await expect(panel.getByText(/50 of 100 bytes/)).toBeVisible({ timeout: 10_000 });
    await settle(page); await page.waitForTimeout(150); await settle(page);
    const afterUpdate = await top();
    expect.soft(Math.abs(afterUpdate - left) <= 1 || afterUpdate >= left, `progress update pulled the panel back (${left} → ${afterUpdate})`).toBe(true);
    await size(app, 600, 440); await expect.poll(() => page.evaluate(() => innerHeight)).toBe(440);
    await settle(page); await page.waitForTimeout(150); await settle(page);
    const afterResize = await top();
    expect.soft(afterResize >= afterUpdate - 1, `resize pulled the panel back (${afterUpdate} → ${afterResize})`).toBe(true);
    await text(page, 1);
  } finally { await session.close(); }
});
