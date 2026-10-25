import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, size, text } from './helpers';

// #82: in Download objects the list's column header ends where the object inspector's head ends, with the "not checked at Open"
// note showing as well as without it, at 1280x800 and 1440x1100 with 100% text (where the list and the inspector sit side by side).
test('the Download objects column header lines up with the inspector head while the not-checked note shows', async () => {
  test.setTimeout(90_000);
  const session = await launch(true);
  try {
    const { page, app, data } = session;
    await size(app, 1440, 1100);
    await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await page.getByRole('button', { name: 'Browse in Files' }).click();
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    const ends = () => page.evaluate(() => {
      const head = document.querySelector('.dh-files .dh-list-scroll .dh-fthead')!.getBoundingClientRect(), insp = document.querySelector('.dh-files .dh-insp-head')!.getBoundingClientRect();
      return { header: Math.round(head.bottom), inspector: Math.round(insp.bottom), scrolled: document.querySelector('.dh-files .dh-list-scroll')!.scrollTop };
    });
    const before = await ends();
    expect(Math.abs(before.header - before.inspector), `without the note: header ends at ${before.header}, inspector head at ${before.inspector}`).toBeLessThanOrEqual(1);
    // The build's file list gets measured: ROOT adds files Open never assessed, and the note about them shows in the list.
    await writeFile(path.join(data, 'measured-scope'), 'measured');
    const note = page.locator('.dh-files .dh-list-scroll').getByRole('group', { name: /files? not checked at Open$/ });
    await expect(note).toBeVisible({ timeout: 40_000 });
    for (const [width, height] of [[1280, 800], [1440, 1100]]) {
      await size(app, width, height); await expect.poll(() => page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
      await text(page, 1);
      await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
      await expect(note).toBeVisible();
      const at = await ends();
      expect(at.scrolled, `${width}x${height}: list at its top`).toBe(0);
      expect.soft(Math.abs(at.header - at.inspector), `${width}x${height} 100%: header ends at ${at.header}, inspector head at ${at.inspector}`).toBeLessThanOrEqual(1);
      // The note stays in the list, whole, under the header and above the rows.
      const order = await page.evaluate(() => { const list = document.querySelector('.dh-files .dh-list-scroll')!, head = list.querySelector('.dh-fthead')!.getBoundingClientRect(), n = list.querySelector('.dh-check-note')!.getBoundingClientRect(), row = list.querySelector('[role=option]')!.getBoundingClientRect(); return { below: n.top >= head.bottom - 1, above: n.bottom <= row.top + 1 }; });
      expect.soft(order, `${width}x${height}: note between the column header and the rows`).toEqual({ below: true, above: true });
    }
  } finally { await session.close(); }
});
