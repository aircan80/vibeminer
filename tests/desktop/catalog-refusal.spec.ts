import { test, expect, _electron as electron } from '@playwright/test';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { DatabaseSync } from 'node:sqlite';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { root } from './helpers';

const NAME = 'discovery-v1.sqlite3';
// A catalog as an older engine leaves it when it is killed: identity v1 schema at user_version 14, no
// build-identity-v2 lineage, and its last changes still only in a pending -wal. The snapshot is copied while
// the writer is open, so nothing is checkpointed into the main file.
async function pendingWalCatalog(scratch: string, catalog: string) {
  const source = path.join(scratch, 'writer');
  await mkdir(source, { recursive: true }); await mkdir(catalog, { recursive: true });
  const db = new DatabaseSync(path.join(source, NAME));
  try {
    db.exec('PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;');
    db.exec(`CREATE TABLE builds(id TEXT PRIMARY KEY, product TEXT NOT NULL, canonical TEXT NOT NULL, identity_fields TEXT NOT NULL,
      first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, first_generation INTEGER NOT NULL, last_generation INTEGER NOT NULL, is_new INTEGER NOT NULL);
      INSERT INTO builds VALUES('${'d'.repeat(64)}','wow','authored-v1','{"CDNConfig":"${'b'.repeat(32)}"}','2026-10-18T12:00:00Z','2026-10-18T12:00:00Z',1,1,0);
      PRAGMA user_version=14;`);
    for (const suffix of ['', '-wal', '-shm']) if (existsSync(path.join(source, NAME + suffix))) await copyFile(path.join(source, NAME + suffix), path.join(catalog, NAME + suffix));
  } finally { db.close(); }
  expect((await stat(path.join(catalog, NAME + '-wal'))).size).toBeGreaterThan(0);
}

test('an older catalog with a pending WAL is refused unchanged with a typed fresh-catalog notice and nothing to delete', async () => {
  const data = await mkdtemp(path.join(tmpdir(), 'vibeminer-old-catalog-'));
  const catalog = path.join(data, 'catalog'), main = path.join(catalog, NAME), wal = main + '-wal';
  try {
    await pendingWalCatalog(data, catalog);
    const hash = async (file: string) => createHash('sha256').update(await readFile(file)).digest('hex');
    const before = { main: await hash(main), wal: await hash(wal) };
    const app = await electron.launch({
      args: [path.join(root, 'apps/desktop'), '--ozone-platform=headless', `--user-data-dir=${path.join(data, 'electron-profile')}`],
      env: { ...process.env, VIBEMINER_DATA_DIR: catalog },
    });
    try {
      const page = await app.firstWindow();
      // The product list and the saved discovery read report the same typed refusal; it is shown once.
      const alert = page.getByRole('alert').filter({ hasText: 'CATALOG_RESET_REQUIRED' });
      await expect(alert).toBeVisible({ timeout: 20_000 });
      await expect(page.getByRole('alert')).toHaveCount(1);
      await expect(alert).toContainText('Saved catalog not opened');
      await expect(alert).toContainText('made by an older VibeMiner');
      await expect(alert).toContainText('A fresh catalog is needed');
      await expect(alert).toContainText(`The catalog folder is ${catalog}.`);
      await expect(alert).toContainText(`Close VibeMiner, move or rename that folder (for example to ${path.basename(catalog)}.old), then start VibeMiner again.`);
      await expect(alert).toContainText('left unchanged');
      await expect(alert.locator('code')).toHaveText('(CATALOG_RESET_REQUIRED)');
      // Nothing on the screen contradicts the notice.
      await expect(page.getByRole('button', { name: /Retry discovery|Refresh builds/ })).toHaveCount(0);
      await expect(page.getByText(/Saved observations remain available|No saved observations|Previous observations are still available/)).toHaveCount(0);
      await expect(page.getByRole('status')).toHaveText('The saved catalog was not opened.');
      await expect(page.getByRole('searchbox', { name: 'Filter version or build number' })).toBeDisabled();
      await expect(page.locator('#inspector')).toHaveCount(0);
      // The owner moves their own catalog deliberately; the app never offers to change it.
      await expect(page.getByRole('button', { name: /delete|erase|reset|remove|clear catalog/i })).toHaveCount(0);
      await expect(page.getByText(/ENGINE_EXITED/)).toHaveCount(0);
      // The folder can be selected to copy it, and at 200% text the whole notice is shown, not clipped.
      const folder = alert.locator('[data-catalog-path]');
      await expect(folder).toHaveText(catalog);
      expect(await folder.evaluate(element => getComputedStyle(element).userSelect)).toBe('text');
      expect(await alert.evaluate(element => getComputedStyle(element.firstElementChild!).userSelect)).toBe('text');
      await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
      await page.evaluate(() => { document.documentElement.style.fontSize = `${parseFloat(getComputedStyle(document.documentElement).fontSize) * 2}px`; });
      await expect(page.locator('.dv-window.dv-compact')).toHaveCount(1);
      const box = await alert.evaluate(element => ({ scroll: element.scrollHeight, client: element.clientHeight, overflow: getComputedStyle(element).overflowY }));
      expect(box.scroll).toBeLessThanOrEqual(box.client); expect(box.overflow).toBe('visible');
      await expect(alert).toBeInViewport();
      await page.keyboard.press('F5');
      await expect(page.getByRole('status')).toHaveText('The saved catalog was not opened.');
    } finally { await app.close(); }
    // Byte-for-byte: the main file and the pending -wal are as the old engine left them.
    expect(existsSync(wal)).toBe(true);
    expect({ main: await hash(main), wal: await hash(wal) }).toEqual(before);
  } finally { await rm(data, { recursive: true, force: true }); }
});
