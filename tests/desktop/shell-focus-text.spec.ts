import { test, expect, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, size, text } from './helpers';

// #112: a focused shell control stays in view when the text size changes at 600x480. On the way to a larger text size the shell passes
// through layouts too short to reveal the control (it can't scroll far enough yet), and the scroll event of its own reveal there used to
// read as the reader scrolling away, so the layout after it left the control hidden. 400% text goes through such layouts every time,
// and at 1000% the control is taller than the shell's view.
const settle = (page: Page) => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
const steady = async (page: Page) => { await settle(page); await page.waitForTimeout(150); await settle(page); };

// The focused control is wholly inside the shell's view: inside the window and above the sticky status bar.
const inShellView = (page: Page) => page.evaluate(() => {
  const el = document.activeElement as HTMLElement, r = el.getBoundingClientRect(), app = document.querySelector<HTMLElement>('.dh-app')!, bar = document.querySelector('.dh-statusbar')!.getBoundingClientRect();
  return { whole: r.height > 0 && r.top >= -1 && r.bottom <= Math.min(bar.top, innerHeight) + 1, at: `${Math.round(r.top)}-${Math.round(r.bottom)} above ${Math.round(bar.top)}; shell scroll ${Math.round(app.scrollTop)} of ${app.scrollHeight - app.clientHeight}` };
});

test('a focused shell control stays in view after the text size changes at 600x480, through 400% and past the view', async () => {
  const session = await launch(true, false, true);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await size(app, 600, 480); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([600, 480]);
    for (const name of ['View metadata details', 'Browse in Files']) {
      const control = page.locator('.dh-area').getByRole('button', { name, exact: true });
      for (const steps of [[2], [4, 2], [4, 2, 4, 2]]) {
        await text(page, 1); await steady(page);
        // Focused and scrolled into view, as Tab would leave it (focus() alone doesn't scroll an already focused control, and at 100%
        // the control sits in the area's own pane, which the round trip before may have left scrolled).
        await control.focus(); await control.evaluate(el => el.scrollIntoView({ block: 'nearest' })); await steady(page);
        const before = await inShellView(page);
        expect(before.whole, `${name} in view at 100% (${before.at})`).toBe(true);
        let from = 100;
        for (const to of steps) {
          await text(page, to); await steady(page);
          await expect(control).toBeFocused();
          const after = await inShellView(page);
          expect.soft(after.whole, `${name} in view after ${from}% → ${to * 100}% (${after.at})`).toBe(true);
          from = to * 100;
        }
      }
    }
    // The scroll event of the shell's own reveal doesn't drop the control either. At 1000% text Browse in Files is taller than the
    // shell's view, so the reveal there can't show it, and its scroll event arrives in that same layout, the control not having moved.
    // Taken as the reader scrolling away, it would leave Browse in Files far above the view once the text goes back to 200%.
    const browse = page.locator('.dh-area').getByRole('button', { name: 'Browse in Files', exact: true });
    await text(page, 2); await steady(page);
    await browse.focus(); await browse.evaluate(el => el.scrollIntoView({ block: 'nearest' })); await steady(page);
    expect((await inShellView(page)).whole, 'Browse in Files in view at 200%').toBe(true);
    await text(page, 10); await steady(page);
    await text(page, 2); await steady(page);
    await expect(browse).toBeFocused();
    const back = await inShellView(page);
    expect.soft(back.whole, `Browse in Files in view after 200% → 1000% → 200% (${back.at})`).toBe(true);
    await text(page, 1);
  } finally { await session.close(); }
});
