import { test, expect, type ElectronApplication, type Page } from '@playwright/test';
import { mkdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';
import { activityTask, closeActivity } from './activity-helpers';

// D105 (#21): community file names from wow-listfile, with the authored engine's sample listfile (VIBEMINER_FIXTURE_LISTFILE).
async function launchNames(marks: string[] = []) {
  process.env.VIBEMINER_FIXTURE_LISTFILE = '1';
  let session;
  try { session = await launch(true, false, false, false, false, false, false, false, false, false, false, true); }
  finally { delete process.env.VIBEMINER_FIXTURE_LISTFILE; }
  for (const mark of marks) await writeFile(path.join(session.data, mark), mark);
  await writeFile(path.join(session.data, 'file-catalog-ready'), 'ready');
  return session;
}
async function files(data: string, page: Page) {
  await page.locator('.build-row').first().click();
  await page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(data, 'release-open'), 'release');
  await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Browse in Files' }).click();
  const list = page.getByRole('listbox', { name: 'Files, one row per FileDataID' });
  await expect(list.getByRole('option').first()).toContainText('800001');
  return list;
}
// Every size and text scale: no sideways scroll, and a long name wraps inside its cell beside its badge.
async function captures(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts', 'files-listfile'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await size(app, width, height);
    for (const scale of [1, 2]) {
      await text(page, scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width} ${scale}`).toBe(false);
      expect(await page.evaluate(() => [...document.querySelectorAll<HTMLElement>('.dh-fname-cell')].every(cell => cell.scrollWidth <= cell.clientWidth + 1)), `${state} ${width} ${scale} names wrap`).toBe(true);
      // Settings captures show the community file names row.
      await page.evaluate(() => document.getElementById('settings-row-listfile')?.scrollIntoView({ block: 'start' }));
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`) });
    }
  }
  await text(page, 1); await size(app, 1280, 800);
}

test('Files shows community names with the Community badge beside each FileDataID and the inspector says where they came from', async () => {
  const session = await launchNames();
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    const list = await files(data, page);
    const first = list.getByRole('option').first();
    await expect(first).toContainText('800001'); await expect(first).toContainText('authored/listfile/file_800001.blp');
    await expect(first.locator('.dh-pill.dh-community')).toHaveText('Community');
    // A FileDataID the listfile doesn't name keeps "Name not known"; install rows keep their own names and no badge.
    await expect(list.getByRole('option').nth(1)).toContainText('Name not known');
    const install = list.getByRole('option').filter({ hasText: 'authored/install/readme.txt' });
    await expect(install.locator('.dh-pill.dh-community')).toHaveCount(0);
    const inspector = page.getByRole('complementary', { name: 'FileDataID 800001' });
    await expect(inspector.getByRole('heading', { name: 'FileDataID 800001' })).toBeVisible();
    await expect(inspector).toContainText('From the wow-listfile community listfile, release 202601010000');
    await expect(inspector).toContainText('Not from Blizzard: ROOT has no names, and a name never changes what is downloaded or verified.');
    // Keyboard: arrows move through the list and the inspector follows, with the unnamed row saying why.
    await list.focus(); await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('complementary', { name: 'FileDataID 800002' })).toContainText('doesn’t name this FileDataID');
    await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('complementary', { name: 'FileDataID 800003' })).toContainText('and_a_long_authored_file_name_800003.m2');
    await expect(list).toBeFocused();
    await captures(page, app, 'names');
  } finally { await session.close(); }
});

test('turning the switch off in Settings removes the names in place and turning it on brings them back', async () => {
  const session = await launchNames();
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    const list = await files(data, page);
    await list.focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown');
    await expect(list.getByRole('option').nth(2)).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Control+Comma');
    const row = page.locator('#settings-row-listfile');
    const toggle = page.getByRole('switch', { name: 'Community file names from wow-listfile' });
    await expect(toggle).toHaveAttribute('aria-checked', 'true');
    await expect(row).toContainText(/Copy in useRelease 202601010000 · published .* · 2,275,783 names/);
    await toggle.focus(); await page.keyboard.press('Space');
    await expect(toggle).toHaveAttribute('aria-checked', 'false'); await expect(toggle).toBeFocused();
    await expect(row).toContainText('Off. Nothing is downloaded.');
    await page.keyboard.press('Escape');
    await expect(list.getByRole('option').first()).toContainText('Name not known');
    await expect(list.locator('.dh-pill.dh-community')).toHaveCount(0);
    await expect(list.getByRole('option').nth(2)).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('complementary', { name: 'FileDataID 800003' })).toContainText('ROOT has no names; a name hash is not a filename.');
    await page.keyboard.press('Control+Comma');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-checked', 'true');
    await page.keyboard.press('Escape');
    await expect(list.getByRole('option').first()).toContainText('authored/listfile/file_800001.blp');
  } finally { await session.close(); }
});

test('while names download Files says so and Settings shows progress; names appear in place once ready', async () => {
  const session = await launchNames(['listfile-downloading']);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    const list = await files(data, page);
    await expect(list.getByRole('option').first()).toContainText('Name not known');
    await expect(page.getByRole('complementary', { name: 'FileDataID 800001' })).toContainText('Community names are being downloaded and show here once they’re ready');
    await page.keyboard.press('Control+Comma');
    const row = page.locator('#settings-row-listfile');
    await expect(row).toContainText('Downloading release 202601010000 · 50.00 MiB of 146.66 MiB');
    await expect(row).toContainText('None yet');
    await captures(page, app, 'settings-downloading');
    // The download is background work in Downloads & activity, with its bytes and a Stop that turns the switch off.
    const task = await activityTask(page, 'Community file names · wow-listfile');
    await expect(task).toContainText('Downloading release 202601010000');
    await expect(task).toContainText('50.00 MiB of 146.66 MiB');
    await expect(task.getByRole('button', { name: 'Turn off community file names' })).toBeVisible();
    await closeActivity(page);
    // Importing is called Processing, with bytes beside the percentage.
    await writeFile(path.join(data, 'listfile-importing'), '1'); await rm(path.join(data, 'listfile-downloading'));
    await expect(row).toContainText('Processing release 202601010000 · 50% · 73.33 MiB of 146.66 MiB');
    await rm(path.join(data, 'listfile-importing'));
    await expect(row).toContainText(/Up to date as of/);
    await page.keyboard.press('Escape');
    await expect(list.getByRole('option').first()).toContainText('authored/listfile/file_800001.blp', { timeout: 15_000 });
  } finally { await session.close(); }
});

test('Settings says when names were never downloaded, offers Download now, and shows a failure with its code', async () => {
  const session = await launchNames(['listfile-not-started']);
  try {
    const { page, app } = session;
    await size(app, 1280, 800);
    await expect(page.locator('.build-row').first()).toBeVisible();
    await page.keyboard.press('Control+Comma');
    const row = page.locator('#settings-row-listfile');
    await expect(row).toContainText('Not downloaded yet. It downloads the first time Files lists a build’s files.');
    const download = row.getByRole('button', { name: 'Download now' });
    await download.focus(); await page.keyboard.press('Enter');
    await expect(row).toContainText('Downloading release 202601010000');
    await expect(download).toBeFocused();
    await writeFile(path.join(session.data, 'listfile-offline'), 'offline'); await rm(path.join(session.data, 'listfile-downloading'));
    await expect(row.getByRole('alert')).toContainText('(LISTFILE_FETCH_OFFLINE)');
    await expect(row.getByRole('button', { name: 'Try again now' })).toBeVisible();
    // At 200% text the one-column row puts the switch above the download state and its button, as in tab order.
    await text(page, 2);
    const order = await page.evaluate(() => {
      const sw = document.getElementById('settings-listfile-switch')!, button = [...document.querySelectorAll<HTMLElement>('#settings-listfile-state button')][0];
      return { above: sw.getBoundingClientRect().bottom <= button.getBoundingClientRect().top, before: !!(sw.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING) };
    });
    expect(order).toEqual({ above: true, before: true });
    await page.locator('#settings-listfile-switch').focus(); await page.keyboard.press('Tab');
    await expect(row.getByRole('button', { name: 'Try again now' })).toBeFocused();
    await text(page, 1);
    const task = await activityTask(page, 'Community file names · wow-listfile');
    await expect(task).toContainText('(LISTFILE_FETCH_OFFLINE)');
    await expect(task.getByRole('button', { name: 'Try again now' })).toBeVisible();
    await closeActivity(page);
    await captures(page, app, 'settings-offline');
  } finally { await session.close(); }
});

test('offline with no copy, Files says the names download failed and where to try again', async () => {
  const session = await launchNames(['listfile-offline']);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    const list = await files(data, page);
    await expect(list.getByRole('option').first()).toContainText('Name not known');
    const inspector = page.getByRole('complementary', { name: 'FileDataID 800001' });
    await expect(inspector).toContainText('Downloading the community names failed; Try again now is in Settings and in Downloads & activity.');
    await expect(inspector).not.toContainText('being downloaded');
  } finally { await session.close(); }
});

test('a failed refresh with a copy in use keeps the names and says when it tries again', async () => {
  const session = await launchNames(['listfile-refresh-failed']);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    const list = await files(data, page);
    await expect(list.getByRole('option').first()).toContainText('authored/listfile/file_800001.blp');
    await page.keyboard.press('Control+Comma');
    const row = page.locator('#settings-row-listfile');
    await expect(row).toContainText('Release 202601010000 is still in use. The last check for a newer one failed; VibeMiner tries again after 15 minutes.');
    await expect(row).not.toContainText('Up to date as of');
  } finally { await session.close(); }
});

test('a names download that fails while Files is open turns the page to the failed text in place (#21 QA)', async () => {
  const session = await launchNames(['listfile-downloading']);
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800);
    const list = await files(data, page);
    const inspector = page.getByRole('complementary', { name: 'FileDataID 800001' });
    await expect(inspector).toContainText('Community names are being downloaded');
    await list.focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowUp');
    await writeFile(path.join(data, 'listfile-offline'), 'offline'); await rm(path.join(data, 'listfile-downloading'));
    await expect(inspector).toContainText('Downloading the community names failed; Try again now is in Settings and in Downloads & activity.', { timeout: 15_000 });
    await expect(list.getByRole('option').first()).toHaveAttribute('aria-selected', 'true');
    await expect(list).toBeFocused();
    // A later attempt that gets a copy turns the same page to names, still in place.
    await rm(path.join(data, 'listfile-offline'));
    await expect(list.getByRole('option').first()).toContainText('authored/listfile/file_800001.blp', { timeout: 15_000 });
    await expect(list.getByRole('option').first()).toHaveAttribute('aria-selected', 'true');
  } finally { await session.close(); }
});
