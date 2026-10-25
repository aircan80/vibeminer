import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, writeFile, unlink, readFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';
import { activityTask, closeActivity } from './activity-helpers';

async function capture(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/size-first'); await mkdir(directory, { recursive: true });
  const base = 15;
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
    for (const scale of [1, 2]) {
      await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, base * scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width}x${height} ${scale * 100}%`).toBe(false);
      expect(await page.locator('.dh-side dd').evaluateAll(elements => elements.filter(element => element.textContent === 'Unknown' && element.getClientRects().length > 0).every(element => { const range = document.createRange(); range.selectNodeContents(element); return range.getClientRects().length === 1; })), `Unknown stays a whole word at ${width}x${height} ${scale * 100}%`).toBe(true);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`), fullPage: true });
    }
  }
}
test('unknown download sizing enters honest Home and restores Discovery without a network refresh', async () => {
  const session = await launch(true);
  try {
    await writeFile(path.join(session.data, 'download-unavailable'), 'unavailable');
    await session.page.locator('.build-row').first().click();
    const open = session.page.getByRole('button', { name: 'Open build', exact: true }); await open.focus(); await session.page.keyboard.press('Enter');
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(session.page.getByRole('heading', { name: 'Build overview' })).toBeFocused();
    await expect(session.page.getByText('Published download size is unknown', { exact: true })).toBeVisible();
    await expect(session.page.getByText('DOWNLOAD_UNDECLARED', { exact: true })).toBeVisible();
    await expect(session.page.getByRole('switch')).toHaveCount(0); await expect(session.page.getByText('On demand only · Download all unavailable')).toBeVisible();
    await expect(session.page.getByRole('button', { name: 'Download all', exact: true })).toHaveCount(0);
    await capture(session.page, session.app, 'download-unavailable');
    await session.page.getByRole('button', { name: /Choose build/ }).click(); await expect(open).toBeFocused();
    const calls = (await readFile(path.join(session.data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    expect(calls.filter(call => call.method === 'discovery.refresh' || call.method === 'download.read')).toHaveLength(0);
  } finally { await session.close(); }
});
test('Downloaded on Discovery picks up an object stored while you watch, without leaving the screen (#48)', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await page.getByRole('button', { name: /Choose build/ }).click();
    const row = page.locator('.build-row').first(); await expect(row).toContainText('0%');
    const calls = async (method: string) => (await readFile(path.join(session.data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line)).filter(call => call.method === method);
    // Nothing is moving: one engine wait stands open and the figures are not read again on a timer.
    await expect.poll(async () => (await calls('download.changes')).length).toBeGreaterThan(0);
    await page.waitForTimeout(1500);
    const reads = (await calls('download.summaries')).length, waits = (await calls('download.changes')).length;
    await page.waitForTimeout(2500);
    expect((await calls('download.summaries')).length).toBe(reads);
    expect((await calls('download.changes')).length).toBe(waits);
    // An object lands (as a Load text would store one): the engine's revision moves and the row follows.
    await writeFile(path.join(session.data, 'stored-summary.json'), JSON.stringify({ storedObjects: '30', storedBytes: '3000' }));
    await expect(row).toContainText('50%');
    expect((await calls('download.summaries')).length).toBe(reads + 1);
    await expect.poll(async () => (await calls('download.changes')).length).toBe(waits + 1);
  } finally { await session.close(); }
});
test('Downloaded on Discovery is marked not current while summary reads fail, and is current again once they succeed (#50)', async () => {
  test.setTimeout(90_000);
  const session = await launch(true);
  try {
    const { page, app } = session;
    await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await page.getByRole('button', { name: /Choose build/ }).click();
    const row = page.locator('.build-row').first(), downloaded = page.locator('#inspector dt', { hasText: 'Downloaded' }).locator('xpath=following-sibling::dd[1]');
    await expect(row).toContainText('0%'); await expect(row).not.toContainText('not current');
    await expect(downloaded).toHaveText(/^[\d,]+ bytes of [\d,]+ bytes · /);
    const note = page.getByText(/^Not current\. These are the figures last read at /);
    await expect(note).toHaveCount(0);
    // The engine starts failing summary reads: the figure stays, muted and labelled, with the reason in the inspector.
    await writeFile(path.join(session.data, 'summaries-error'), 'fail');
    await expect(row).toContainText(', not current');
    await expect(row.locator('.dv-stale .dv-meter')).toBeVisible();
    await expect(page.locator('.dv-thead .dv-stale-tag')).toHaveText('not current');
    await expect(note).toBeVisible();
    await expect(note).toContainText('The latest read failed: Authored stored download figures could not be read. (LOCAL_READ_FAILED)');
    await expect(downloaded).toHaveText(/^[\d,]+ bytes of [\d,]+ bytes · /);
    expect(await row.locator('.dv-meter').evaluate(element => getComputedStyle(element).color)).toBe(await page.locator('.dv-insp-body dt').first().evaluate(element => getComputedStyle(element).color));
    await capture(page, app, 'discovery-summaries-not-current');
    // The header's second line fits, from the smallest window to the default, at 100% and 200% text.
    for (const [width, height] of [[600, 480], [1280, 800]]) for (const font of [15, 30]) {
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
      await page.evaluate(size => { document.documentElement.style.fontSize = `${size}px`; }, font);
      await expect.poll(() => page.locator('.dv-thead .dv-stale-head').evaluate(element => element.scrollHeight <= element.parentElement!.clientHeight && element.scrollWidth <= element.clientWidth), `${width}x${height} at ${font}px`).toBe(true);
    }
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    // Reads succeed again: the next try (at most 30 s after the last) shows the figure as current.
    await unlink(path.join(session.data, 'summaries-error'));
    await expect(row).not.toContainText('not current', { timeout: 40_000 });
    await expect(note).toHaveCount(0); await expect(row.locator('.dv-stale')).toHaveCount(0);
    await expect(page.locator('.dv-thead')).not.toContainText('not current');
    await expect(row).toContainText('0%');
  } finally { await session.close(); }
});
test('download object paging retains rows and keyboard anchor during local reads and recoverable failures', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await capture(page, session.app, 'download-home');
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    const browse = page.getByRole('button', { name: 'Browse in Files' }); await browse.focus(); await page.keyboard.press('Enter');
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await writeFile(path.join(session.data, 'hold-download-page'), 'hold'); await writeFile(path.join(session.data, 'download-page-error'), 'error');
    const next = page.getByRole('button', { name: 'Next page', exact: true }); await next.focus(); await page.keyboard.press('Enter');
    await expect(next).toBeFocused(); await expect(next).toHaveAttribute('aria-disabled', 'true'); await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await capture(page, session.app, 'download-objects-loading');
    await writeFile(path.join(session.data, 'release-download-page'), 'release');
    await expect(page.getByRole('alert')).toContainText('LOCAL_READ_FAILED'); await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await capture(page, session.app, 'download-objects-error');
    await unlink(path.join(session.data, 'download-page-error')); const retry = page.getByRole('button', { name: 'Retry page' }); await retry.focus(); await page.keyboard.press('Enter');
    await expect(page.getByText('Object 51', { exact: true })).toBeVisible(); await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    await page.getByRole('button', { name: /Return to build overview/ }).click(); await expect(browse).toBeFocused();
  } finally { await session.close(); }
});
test('Files switches to the measured ledger in place and a Check covers the files Open never assessed (#38, D093)', async () => {
  test.setTimeout(90_000);
  const session = await launch(true);
  try {
    const { page, app } = session;
    const calls = async (method: string) => (await readFile(path.join(session.data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line)).filter(call => call.method === method);
    // Wide enough that the list and the object inspector sit side by side.
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 1100));
    await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await page.getByRole('button', { name: 'Browse in Files' }).click();
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Next page', exact: true }).click();
    await expect(page.getByText('Object 51', { exact: true })).toBeVisible();
    // Pick manifest object 55, then let the build's file list be measured while the page is on screen.
    await page.getByText('Object 55', { exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Encoded object 55' })).toBeVisible();
    const notChecked = page.getByRole('group', { name: '5 files not checked at Open' });
    await expect(notChecked).toHaveCount(0);
    await writeFile(path.join(session.data, 'measured-scope'), 'measured');
    // The page starts at the same first row, renumbered in the measured ledger, with the two files ROOT adds after it.
    await expect(page.getByText('12 encoded objects on this page · 65 total · objects 54–65')).toBeVisible({ timeout: 40_000 });
    await expect(page.getByRole('heading', { name: 'Encoded object 58' })).toBeVisible();
    await expect(page.getByRole('option', { selected: true })).toContainText('Object 58');
    await expect(page.getByRole('option').filter({ hasText: 'Not checked at Open' })).toHaveCount(2);
    await expect(notChecked).toBeVisible();
    const switched = (await calls('download.read')).at(-1);
    expect(switched.params).toMatchObject({ ledgerId: 'e'.repeat(64), from: (51 * 16).toString(16).padStart(32, '0') });
    await capture(page, app, 'download-objects-measured');
    // At 200% the rows keep their room: the Check note is in the Selected object pane instead of above them.
    await expect(page.locator('.dh-list-scroll').getByRole('group', { name: '5 files not checked at Open' })).toHaveCount(0);
    // Every row's local state shows whole: wrapped at 1280×800, and on the second line of a two-line row at 600×480 (#52).
    for (const [width, height] of [[1280, 800], [600, 480]] as const) {
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
      await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
      await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
      const cut = await page.locator('.dh-list-scroll [role="option"] .dh-ostate').evaluateAll(cells => cells.filter(cell => {
        const r = cell.getBoundingClientRect(), list = cell.closest('.dh-list-scroll')!.getBoundingClientRect();
        return r.width <= 0 || r.left < list.left - 1 || r.right > list.right + 1 || cell.scrollWidth > cell.clientWidth + 1;
      }).map(cell => cell.textContent));
      expect(cut, `${width}×${height} at 200%`).toEqual([]);
      await page.locator('.dh-list-scroll [role="option"]').filter({ hasText: 'Not checked at Open' }).first().scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(root, 'artifacts/size-first', `download-objects-states-${width}x${height}-200.png`) });
      await expect(page.getByRole('option').filter({ hasText: 'Not checked at Open' }).first()).toBeVisible();
    }
    // The pane head points there. Its pane hides as Selected object shows, so Enter on it moves focus to the Check note
    // there, never to the body, at 600×480 and at 1280×800.
    const pointer = page.locator('.dh-files .dh-pane-head .dh-check-pointer'), listPane = page.getByRole('button', { name: 'Files list', exact: true });
    for (const [width, height] of [[600, 480], [1280, 800]] as const) {
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
      if (await listPane.getAttribute('aria-pressed') !== 'true') await listPane.click();
      await expect(pointer).toContainText('5 not checked at Open');
      await pointer.getByRole('button', { name: 'Check them in Selected object' }).focus(); await page.keyboard.press('Enter');
      await expect(page.locator('.dh-check-inspector').getByRole('button', { name: 'Check 5 files', exact: true }), `${width}×${height}`).toBeFocused();
      await page.screenshot({ path: path.join(root, 'artifacts/size-first', `download-check-pointer-focus-${width}x${height}-200.png`) });
    }
    await listPane.click();
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 1100));
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    // A held Check reports progress; its one button turns into Stop and keeps focus.
    await writeFile(path.join(session.data, 'hold-download-check'), 'hold');
    const button = notChecked.getByRole('button', { name: 'Check 5 files' });
    await button.focus(); await page.keyboard.press('Enter');
    const stop = notChecked.getByRole('button', { name: 'Stop checking' });
    await expect(stop).toBeFocused();
    await expect(notChecked.getByText('Checking 1 of 5 files…')).toBeVisible();
    await capture(page, app, 'download-check-running');
    // At 200% the note sits in the Selected object pane, so the rows keep their room; back at 100% it is above them again.
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    await expect(notChecked.getByText('Checking 1 of 5 files…')).toBeVisible();
    // Like file list preparation, the running Check is a task in Downloads & activity, linked from its mint note.
    await expect(notChecked).toContainText('Progress is in Downloads & activity.');
    await expect(notChecked.getByRole('button', { name: 'View task' })).toBeVisible();
    await expect(page.locator('.dh-statusbar')).toContainText('Check of 5 files not checked at Open');
    const task = await activityTask(page, 'Check of 5 files not checked at Open');
    await expect(task.getByRole('button', { name: 'Stop Check of 5 files not checked at Open' })).toBeVisible();
    await closeActivity(page);
    await stop.focus();
    await writeFile(path.join(session.data, 'release-download-check'), 'release');
    await expect(notChecked.getByRole('status')).toHaveText(/^Checked .*: 1 verified · 3 not stored · 1 missing · 0 corrupt\.$/);
    const again = notChecked.getByRole('button', { name: 'Check 5 files again' });
    await expect(again).toBeFocused();
    // Its task gives the counts, not just that the request completed.
    await expect(await activityTask(page, 'Check of 5 files not checked at Open')).toContainText('Checked: 1 verified · 3 not stored · 1 missing · 0 corrupt');
    await closeActivity(page);
    await expect(page.getByRole('option').filter({ hasText: 'Not stored at check' })).toHaveCount(2);
    await expect(page.getByRole('heading', { name: 'Encoded object 58' })).toBeVisible();
    await page.getByRole('button', { name: 'First page', exact: true }).click();
    await expect(page.getByRole('option').filter({ has: page.getByText('Object 4', { exact: true }) })).toContainText('Verified at check');
    await expect(page.getByRole('option').filter({ has: page.getByText('Object 27', { exact: true }) })).toContainText('Missing at check');
    await expect(page.getByRole('option').filter({ has: page.getByText('Object 1', { exact: true }) })).toContainText('Verified locally');
    // Stop a second Check: nothing new is recorded and the earlier outcome stays on the rows.
    await unlink(path.join(session.data, 'release-download-check'));
    await again.click();
    await notChecked.getByRole('button', { name: 'Stop checking' }).click();
    await expect(notChecked.getByRole('status')).toHaveText('Check stopped. Nothing new was recorded; the earlier check stays.');
    // The earlier Check's counts stay in the note.
    await expect(notChecked.locator('.dh-check-earlier')).toHaveText(/^Last checked .*: 1 verified · 3 not stored · 1 missing · 0 corrupt\.$/);
    await expect(page.getByRole('option').filter({ has: page.getByText('Object 4', { exact: true }) })).toContainText('Verified at check');
    // With the outcome dismissed, the note still gives the counts the engine saved with the last completed Check.
    await (await activityTask(page, 'Check of 5 files not checked at Open')).getByRole('button', { name: 'Dismiss outcome' }).click();
    await closeActivity(page);
    await expect(notChecked.getByRole('status')).toHaveText(/^Last checked .*: 1 verified · 3 not stored · 1 missing · 0 corrupt\.$/);
    // Leaving Files doesn't stop a Check: it finishes while Home is shown, and Files gives its outcome on return.
    await notChecked.getByRole('button', { name: 'Check 5 files again' }).click();
    await expect(notChecked.getByRole('button', { name: 'Stop checking' })).toBeVisible();
    const areas = page.getByRole('navigation', { name: 'Build areas' });
    await areas.getByRole('button', { name: 'Home', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await writeFile(path.join(session.data, 'release-download-check'), 'release');
    await expect(page.locator('.dh-statusbar')).not.toContainText('Checking files not checked at Open');
    await areas.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(notChecked.getByRole('status')).toHaveText(/^Checked .*: 1 verified · 3 not stored · 1 missing · 0 corrupt\.$/);
    const checks = await calls('download.check'), cancels = await calls('request.cancel');
    expect(checks).toHaveLength(3);
    expect(cancels.map(call => call.params.requestId)).toContain(checks[1].id);
    expect(cancels.map(call => call.params.requestId)).not.toContain(checks[2].id);
  } finally { await session.close(); }
});
test('focus on View task in a Check note moves to Check when the Check ends, in its own and in another build\'s note (#38, D093)', async () => {
  test.setTimeout(90_000);
  const session = await launch(true);
  try {
    const { page, app } = session;
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 1100));
    const openFiles = async (row: number) => {
      await page.locator('.build-row').nth(row).click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
      await writeFile(path.join(session.data, 'release-open'), 'release');
      await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
      await page.getByRole('button', { name: 'Browse in Files' }).click();
      await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    };
    await writeFile(path.join(session.data, 'measured-scope'), 'measured');
    await openFiles(0);
    const note = page.getByRole('group', { name: '5 files not checked at Open' });
    await expect(note).toBeVisible({ timeout: 40_000 });
    // This build's Check: focus on View task, the Check ends, focus goes to Check, not the body.
    await writeFile(path.join(session.data, 'hold-download-check'), 'hold');
    await note.getByRole('button', { name: 'Check 5 files' }).click();
    const view = note.getByRole('button', { name: 'View task' });
    await view.focus(); await expect(view).toBeFocused();
    await writeFile(path.join(session.data, 'release-download-check'), 'release');
    await expect(note.getByRole('status')).toHaveText(/^Checked /);
    await expect(note.getByRole('button', { name: 'Check 5 files again' })).toBeFocused();
    // Another build's note while this one's Check runs: it names the build (the same version here, so its build config
    // too), and focus on View task goes to Check when that Check ends.
    await unlink(path.join(session.data, 'release-download-check'));
    await note.getByRole('button', { name: 'Check 5 files again' }).click();
    await expect(note.getByRole('button', { name: 'Stop checking' })).toBeVisible();
    await page.getByRole('button', { name: /Choose build/ }).click();
    await openFiles(1);
    await expect(note).toBeVisible({ timeout: 40_000 });
    await expect(note.getByRole('status')).toHaveText(/^A check of wow 12\.1\.5\.70448 \(build config [0-9a-f]{8}\) is running; one runs at a time\.$/);
    await expect(note.getByRole('button', { name: /^Check 5 files/ })).toHaveCount(0);
    const other = note.getByRole('button', { name: 'View task' });
    await other.focus(); await expect(other).toBeFocused();
    await writeFile(path.join(session.data, 'release-download-check'), 'release');
    await expect(note.getByRole('button', { name: /^Check 5 files/ })).toBeFocused();
    expect(await page.evaluate(() => document.activeElement === document.body)).toBe(false);
  } finally { await session.close(); }
});
test('Download objects stay reachable once the file list is ready: the Files view switch, the overview Check line and a kept choice (#38, D093)', async () => {
  test.setTimeout(120_000);
  // The authored engine prepares file lists; the scope is measured before Files is shown, the file list a little later.
  const session = await launch(true, false, false, false, false, false, false, false, false, false, false, true);
  try {
    const { page, app } = session;
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 1100));
    await writeFile(path.join(session.data, 'measured-scope'), 'measured');
    const open = async () => {
      await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open build', exact: true }).click();
      await writeFile(path.join(session.data, 'release-open'), 'release');
      await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    };
    await open();
    const line = page.locator('.dh-check-line');
    await expect(line).toHaveText('5 files not checked at Open · Check', { timeout: 40_000 });
    expect(await line.evaluate(element => getComputedStyle(element).fontSize)).toBe(await page.locator('.dh-facts dd').first().evaluate(element => getComputedStyle(element).fontSize));
    const areas = page.getByRole('navigation', { name: 'Build areas' });
    const view = (name: string) => page.locator('.dh-files:not([hidden])').getByRole('group', { name: 'Files view' }).getByRole('button', { name, exact: true });
    // While the file list is prepared, All files is offered but disabled: its label fades, not its focus ring.
    await areas.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(view('All files')).toHaveAttribute('aria-disabled', 'true');
    await view('Download objects').focus(); await page.keyboard.press('Shift+Tab');
    await expect(view('All files')).toBeFocused();
    expect(await view('All files').evaluate(element => { const style = getComputedStyle(element); return [style.opacity, style.cursor, style.outlineColor]; })).toEqual(['1', 'default', 'rgb(230, 173, 70)']);
    // The file list becomes ready while Build overview is shown: Files adopts it once, and its switch leads back.
    await areas.getByRole('button', { name: 'Home', exact: true }).click();
    await writeFile(path.join(session.data, 'file-catalog-ready'), 'ready');
    await expect(page.locator('section[aria-labelledby="home-catalog"]')).toContainText('Ready to browse', { timeout: 40_000 });
    await areas.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(page.getByRole('listbox', { name: 'Files, one row per FileDataID' })).toBeVisible();
    await expect(view('All files')).toHaveAttribute('aria-pressed', 'true');
    // The switch keeps the search at its reference width, on the row under the title and switch, at 1280×800 too.
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
    await page.screenshot({ path: path.join(root, 'artifacts/size-first', 'files-view-switch-1280x800-100.png') });
    expect(await page.getByRole('searchbox', { name: 'Search by FileDataID or content key' }).evaluate(element => element.getBoundingClientRect().width)).toBeGreaterThanOrEqual(11.666667 * 15 - 1);
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 1100));
    await view('Download objects').focus(); await page.keyboard.press('Enter');
    const note = page.getByRole('group', { name: '5 files not checked at Open' });
    await expect(note).toBeVisible();
    await expect(view('Download objects')).toHaveAttribute('aria-pressed', 'true');
    await expect(view('Download objects')).toBeFocused();
    // Picked on purpose: no second offer of the file list beside the switch.
    await expect(page.getByRole('button', { name: 'Show file list' })).toHaveCount(0);
    await expect(page.getByRole('note', { name: 'File list ready' })).toHaveCount(0);
    await note.getByRole('button', { name: 'Check 5 files' }).click();
    await expect(note.getByRole('status')).toHaveText(/^Checked .*: 1 verified · 3 not stored · 1 missing · 0 corrupt\.$/);
    // Back to all files and to the objects again, by keyboard; the outcome is still there.
    await view('All files').focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('listbox', { name: 'Files, one row per FileDataID' })).toBeVisible();
    await expect(view('All files')).toBeFocused();
    await view('Download objects').click();
    await expect(note.getByRole('status')).toHaveText(/^Checked .*: 1 verified/);
    // Reopened, Files keeps the choice for this build, and the note gives the saved counts once the outcome is dismissed.
    await (await activityTask(page, 'Check of 5 files not checked at Open')).getByRole('button', { name: 'Dismiss outcome' }).click();
    await closeActivity(page);
    await page.getByRole('button', { name: /Choose build/ }).click();
    await open();
    await areas.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(view('Download objects')).toHaveAttribute('aria-pressed', 'true');
    await expect(note.getByRole('status')).toHaveText(/^Last checked .*: 1 verified · 3 not stored · 1 missing · 0 corrupt\.$/);
    // Browse in Files on the File catalog panel always opens the file list.
    await areas.getByRole('button', { name: 'Home', exact: true }).click();
    await page.getByRole('button', { name: 'Browse in Files' }).click();
    await expect(page.getByRole('listbox', { name: 'Files, one row per FileDataID' })).toBeVisible();
    await expect(view('All files')).toHaveAttribute('aria-pressed', 'true');
    // Build overview's Check line opens Download objects with focus on the Check, once: switching views afterwards
    // leaves focus on the switch.
    await areas.getByRole('button', { name: 'Home', exact: true }).click();
    await line.getByRole('button', { name: 'Check', exact: true }).click();
    await expect(note.getByRole('button', { name: 'Check 5 files again' })).toBeFocused();
    await expect(view('Download objects')).toHaveAttribute('aria-pressed', 'true');
    await view('All files').click();
    await view('Download objects').focus(); await page.keyboard.press('Enter');
    await expect(view('Download objects')).toBeFocused();
    await page.waitForTimeout(1500);
    await expect(view('Download objects')).toBeFocused();
  } finally { await session.close(); }
});
