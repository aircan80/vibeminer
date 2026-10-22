import { test, expect, _electron as electron } from '@playwright/test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { root } from './helpers';

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
