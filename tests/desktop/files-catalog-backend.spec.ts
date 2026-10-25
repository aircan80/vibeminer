import { test, expect, _electron as electron } from '@playwright/test';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { root, size, text } from './helpers';

// D071 (#35) through the real C# engine: Open queues the file catalog, the background worker seals it from the authored
// ROOT and install manifests, and Files lists one row per FileDataID from that generation. A restart keeps it.
const fixture = path.join(root, '.local/backend-fixture-build/bin/BackendFixture/debug/BackendFixture.dll');
async function launch(data: string) {
  const app = await electron.launch({ args: [path.join(root, 'apps/desktop'), '--ozone-platform=headless', `--user-data-dir=${path.join(data, 'electron')}`],
    env: { ...process.env, VIBEMINER_ENGINE_PATH: fixture, VIBEMINER_DATA_DIR: path.join(data, 'catalog'), VIBEMINER_AUTHORED_FIXTURES: '1' } });
  return { app, page: await app.firstWindow() };
}

test('the real engine prepares the file catalog after Open and Files lists one row per FileDataID', async () => {
  test.setTimeout(90000);
  const data = await mkdtemp(path.join(tmpdir(), 'vibeminer-files-native-'));
  let session: Awaited<ReturnType<typeof launch>> | null = null;
  try {
    session = await launch(data);
    const open = async () => {
      await session!.page.locator('.build-row').first().click();
      await session!.page.getByRole('button', { name: 'Open build', exact: true }).click();
      await expect(session!.page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible();
    };
    await open();
    const panel = session.page.locator('section[aria-labelledby="home-catalog"]');
    await expect(panel).toContainText('Ready to browse', { timeout: 30000 });
    await expect(panel).toContainText('ROOT FileDataIDs');
    await session.page.getByRole('button', { name: 'Browse in Files' }).click();
    const list = session.page.getByRole('listbox', { name: 'Files, one row per FileDataID' });
    await expect(list.getByRole('option')).toHaveCount(3);
    await expect(list.getByRole('option').nth(0)).toContainText('1001'); await expect(list.getByRole('option').nth(1)).toContainText('1002');
    await expect(session.page.locator('.dh-statusbar')).toContainText('File catalog generation');
    await session.app.close(); session = null;
    // After a restart the sealed catalog is read straight away; nothing is prepared again.
    session = await launch(data); await open();
    await expect(session.page.locator('section[aria-labelledby="home-catalog"]')).toContainText('Ready to browse');
  } finally { if (session) await session.app.close(); await rm(data, { recursive: true, force: true }); }
});

// D085 (#46) through the real C# engine: Settings › Storage measures the build's file list on disk and frees it; the files
// are gone, Home and Files say so, and Files prepares it again when asked.
test('Settings frees the real engine’s file list and Files prepares it again', async () => {
  test.setTimeout(120000);
  const data = await mkdtemp(path.join(tmpdir(), 'vibeminer-free-native-'));
  const folder = path.join(data, 'catalog', 'file-catalogs');
  const catalogFiles = async () => (await readdir(folder).catch(() => [] as string[])).filter(name => /^files-\d+\.sqlite3/.test(name));
  let session: Awaited<ReturnType<typeof launch>> | null = null;
  try {
    session = await launch(data);
    const page = session.page;
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    const panel = page.locator('section[aria-labelledby="home-catalog"]');
    await expect(panel).toContainText('Ready to browse', { timeout: 30000 });
    expect((await catalogFiles()).length).toBeGreaterThan(0);

    await page.getByRole('button', { name: 'Settings' }).click();
    await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeFocused();
    const row = page.locator('#settings-row-prepared .st-build').first();
    await expect(row).toContainText(/[\d,]+ bytes · ready/, { timeout: 20000 });
    await expect(row).toContainText('Table rows');
    await expect(row).toContainText('Downloaded files');
    // At 200% text the entry stacks and nothing overflows sideways.
    await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });

    // Keyboard: Free asks first, Escape keeps it and stays in Settings.
    await row.getByRole('button', { name: 'Free file list…' }).focus();
    await page.keyboard.press('Enter');
    const confirm = row.getByRole('group');
    await expect(confirm.getByRole('button', { name: /^Free / })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(row.getByRole('button', { name: 'Free file list…' })).toBeFocused();
    await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(confirm.getByRole('button', { name: /^Free / })).toBeFocused();
    // The confirm says what Files loses and what stays, with the regular 14 px buttons of the Storage row.
    await expect(confirm).toContainText('Files has no FileDataID search or names for it. Table rows and downloaded files stay.');
    for (const button of await confirm.getByRole('button').all())
      expect(await button.evaluate(element => getComputedStyle(element).fontSize)).toBe(await page.getByRole('button', { name: 'Copy path' }).evaluate(element => getComputedStyle(element).fontSize));
    // The amount freed is the amount the confirm named: the files on disk, -wal included.
    const asked = ((await confirm.getByRole('button', { name: /^Free / }).textContent()) ?? '').replace(/^Free /, '');
    await page.keyboard.press('Enter');
    await expect(row).toContainText(`Freed ${asked}`, { timeout: 20000 });
    await expect(row).toContainText('Freed · prepared again on the next Open');
    expect(await catalogFiles()).toEqual([]);

    await page.getByRole('button', { name: /^← / }).click();
    await expect(panel).toContainText('Freed');
    await page.getByRole('button', { name: 'Browse in Files' }).click();
    const note = page.getByRole('note', { name: 'File list freed' });
    await expect(note).toBeVisible();
    // At 600×480 and 200% the action, which names the build, wraps inside the note.
    await size(session.app, 600, 480); await text(page, 2);
    // The note re-renders for the compact layout after the text-size change, so wait for its button before measuring.
    const prepare = note.getByRole('button', { name: /^Prepare file list again/ });
    await expect(prepare).toBeVisible();
    await expect.poll(async () => { const [box, again] = await Promise.all([note.boundingBox(), prepare.boundingBox()]); return !!box && !!again && again.x + again.width <= box.x + box.width + 0.5; }).toBe(true);
    await text(page, 1); await size(session.app, 1280, 800);
    await note.getByRole('button', { name: /^Prepare file list again/ }).click();
    await page.getByRole('button', { name: 'Show file list' }).click({ timeout: 30000 });
    await expect(page.getByRole('listbox', { name: 'Files, one row per FileDataID' }).getByRole('option')).toHaveCount(3);
    expect((await catalogFiles()).length).toBeGreaterThan(0);
  } finally { if (session) await session.app.close(); await rm(data, { recursive: true, force: true }); }
});
