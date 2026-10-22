import { _electron as electron } from '@playwright/test';
import { mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
export const root = fileURLToPath(new URL('../../', import.meta.url));
export async function launch(fixtures = false, offlineEmpty = false, retention = false, items = false, icons = false, classifications: false|'class'|'subclass'=false, creatures=false,creatureDisplay=false,effects:false|true|'split'=false,referenceText=false,follow=false) {
  const data = await mkdtemp(path.join(tmpdir(), 'vibeminer-electron-'));
  const app = await electron.launch({
    args: [process.env.VIBEMINER_DESKTOP_TEST_APP_PATH??path.join(root, 'apps/desktop'), '--ozone-platform=headless', `--user-data-dir=${path.join(data, 'electron-profile')}`],
    env: { ...process.env, VIBEMINER_DATA_DIR: path.join(data, 'catalog'), ...(fixtures ? {
      VIBEMINER_ENGINE_PATH: path.join(root, 'tests/desktop/fixtures/engine.mjs'),
      VIBEMINER_FIXTURE_CONTROL_DIR: data, VIBEMINER_AUTHORED_FIXTURES: '1',
      ...(retention ? { VIBEMINER_FIXTURE_RETENTION: '1' } : {}),
      ...(items||icons||classifications||effects ? { VIBEMINER_FIXTURE_ITEMS: '1' } : {}),
      ...(creatures?{VIBEMINER_FIXTURE_CREATURES:'1'}:{}),
      ...(creatureDisplay?{VIBEMINER_FIXTURE_CREATURE_DISPLAY:'1'}:{}),
      ...(effects?{VIBEMINER_FIXTURE_EFFECTS:effects==='split'?'split':'1'}:{}),
      ...(icons ? { VIBEMINER_FIXTURE_ICONS: '1' } : {}),
      ...(referenceText ? { VIBEMINER_FIXTURE_REFERENCE_TEXT: '1' } : {}),
      ...(follow ? { VIBEMINER_FIXTURE_FOLLOW: '1' } : {}),
      ...(classifications?{VIBEMINER_FIXTURE_CLASSIFICATIONS:classifications}:{}),
      ...(offlineEmpty ? { VIBEMINER_FIXTURE_OFFLINE_EMPTY: '1' } : {}),
    } : {}) },
  });
  const page = await app.firstWindow();
  return { app, page, data, close: async () => { await app.close(); await rm(data, { recursive: true, force: true }); } };
}
