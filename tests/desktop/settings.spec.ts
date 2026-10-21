import { test, expect, _electron as electron, type ElectronApplication, type Page } from '@playwright/test';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { launch, root } from './helpers';

// Settings (docs/design/desktop-settings): saved by the real C# engine in its data folder, surviving restart.
async function realEngine(data: string) {
  const app = await electron.launch({
    args: [process.env.VIBEMINER_DESKTOP_TEST_APP_PATH ?? path.join(root, 'apps/desktop'), '--ozone-platform=headless', `--user-data-dir=${path.join(data, 'electron-profile')}`],
    env: { ...process.env, VIBEMINER_DATA_DIR: path.join(data, 'catalog') },
  });
  const page = await app.firstWindow();
  await expect(page.getByRole('heading', { name: 'Builds', level: 1 })).toBeVisible({ timeout: 20_000 });
  return { app, page };
}
async function size(app: ElectronApplication, width: number, height: number) { await app.evaluate(({ BrowserWindow }, s) => BrowserWindow.getAllWindows()[0].setContentSize(s.width, s.height), { width, height }); }
async function text(page: Page, scale: number) { await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale); }
const community = (page: Page) => page.getByRole('switch', { name: /Community build history/ });
// The window never scrolls; the status bar stays at the bottom and nothing overflows sideways.
async function fixedWindow(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/desktop-settings'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await size(app, width, height);
    for (const scale of [1, 2]) {
      await text(page, scale);
      await expect.poll(() => page.evaluate(() => document.querySelector('.settings-screen')?.classList.contains('st-compact'))).toBe(scale === 2);
      const layout = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth, sh: document.documentElement.scrollHeight, ch: document.documentElement.clientHeight, y: window.scrollY, status: document.querySelector('.st-statusbar')!.getBoundingClientRect().bottom }));
      const label = `${state} ${width}x${height} ${scale * 100}%`;
      expect(layout.sw, `${label} horizontal`).toBeLessThanOrEqual(layout.cw);
      expect(layout.sh, `${label} vertical`).toBeLessThanOrEqual(layout.ch);
      expect(layout.y, `${label} window scroll`).toBe(0);
      expect(Math.round(layout.status), `${label} status bar at the window bottom`).toBe(height);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`) });
    }
  }
  await text(page, 1); await size(app, 1280, 800);
}

test('the real engine saves Community build history in its data folder and it survives a restart', async () => {
  const data = await mkdtemp(path.join(tmpdir(), 'vibeminer-settings-'));
  const file = path.join(data, 'catalog', 'settings-v1.json');
  try {
    let { app, page } = await realEngine(data);
    try {
      await size(app, 1280, 800);
      const toolbar = page.locator('.dv-toolbar');
      await expect(toolbar.locator('> *').last()).toHaveText('Settings');
      await toolbar.getByRole('button', { name: 'Settings' }).click();
      await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeFocused();
      await expect(community(page)).toHaveAttribute('aria-checked', 'false');
      await expect(page.getByText('Default: Off', { exact: true })).toBeVisible();
      await expect(page.getByText('Never changed')).toBeVisible();
      expect(existsSync(file), 'Reading never writes the settings file.').toBe(false);
      await expect(page.locator('#settings-storage-path')).toHaveText(path.join(data, 'catalog'));
      await expect(page.locator('#settings-row-storage')).toContainText(/\d[\d,]* bytes/, { timeout: 20_000 });
      await expect(page.locator('#settings-row-storage')).toContainText('when Settings opened');
      await expect(page.locator('#settings-row-limits')).toContainText('Up to 4 at once, at most 2 of them for background downloads');
      await expect(page.locator('#settings-row-limits')).toContainText('Fixed for now');
      await fixedWindow(page, app, 'idle');
      await page.getByRole('button', { name: 'Copy path' }).click();
      await expect(page.getByText('Path copied')).toBeVisible();
      await community(page).focus();
      await page.keyboard.press('Space');
      await expect(community(page)).toHaveAttribute('aria-checked', 'true');
      await expect(community(page)).toBeFocused();
      await expect(page.locator('#settings-community-save')).toContainText('Saved');
      expect(JSON.parse(await readFile(file, 'utf8'))).toMatchObject({ version: 1, values: { communityBuildHistory: true } });
      await page.keyboard.press('Escape');
      await expect(page.getByRole('heading', { name: 'Builds', level: 1 })).toBeVisible();
      await expect(page.locator('.dv-toolbar').getByRole('button', { name: 'Settings' })).toBeFocused();
    } finally { await app.close(); }
    ({ app, page } = await realEngine(data));
    try {
      await page.keyboard.press('Control+Comma');
      await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeFocused();
      await expect(community(page)).toHaveAttribute('aria-checked', 'true');
      await expect(community(page)).toHaveText('On');
    } finally { await app.close(); }
    // A value VibeMiner doesn't understand falls back to the default and says so; switching replaces it.
    await writeFile(file, JSON.stringify({ version: 1, values: { communityBuildHistory: 'yes' } }));
    ({ app, page } = await realEngine(data));
    try {
      await page.keyboard.press('Control+Comma');
      await expect(community(page)).toHaveAttribute('aria-checked', 'false');
      await expect(page.getByText('Default: Off · in use now')).toBeVisible();
      await expect(page.getByRole('note')).toContainText('(SETTING_UNREADABLE)');
      await expect(page.getByText('1 setting is using its default')).toBeVisible();
      await fixedWindow(page, app, 'unreadable');
      // A failed write leaves the saved file as it was and is never replayed on its own.
      await mkdir(path.join(data, 'catalog', 'settings-v1.json.tmp'));
      await community(page).click();
      const alert = page.getByRole('alert');
      await expect(alert).toContainText('Couldn’t save Community build history. It’s still Off.');
      await expect(alert).toContainText('(SETTINGS_WRITE_FAILED)');
      // Notice text uses the normal ink colour, not the global .error colour.
      expect(await page.locator('#settings-row-community .st-notice.error').evaluate(el => getComputedStyle(el).color)).toBe('rgb(36, 59, 58)');
      await expect(community(page)).toHaveAttribute('aria-checked', 'false');
      await fixedWindow(page, app, 'failed');
      expect(JSON.parse(await readFile(file, 'utf8'))).toEqual({ version: 1, values: { communityBuildHistory: 'yes' } });
      await rm(path.join(data, 'catalog', 'settings-v1.json.tmp'), { recursive: true });
      await page.getByRole('button', { name: 'Retry turning on' }).click();
      await expect(community(page)).toHaveAttribute('aria-checked', 'true');
      await expect(community(page)).toBeFocused();
      await expect(page.getByRole('alert')).toHaveCount(0);
    } finally { await app.close(); }
    // A newer settings file locks the switch with its reason and is never overwritten.
    const newer = JSON.stringify({ version: 2, values: { communityBuildHistory: true } });
    await writeFile(file, newer);
    ({ app, page } = await realEngine(data));
    try {
      await page.keyboard.press('Control+Comma');
      await expect(community(page)).toHaveAttribute('aria-disabled', 'true');
      await expect(page.getByRole('note')).toContainText('Saved by a newer VibeMiner. Using the default, Off.');
      await expect(page.getByText('Can’t be changed here: saved by a newer VibeMiner.')).toBeVisible();
      await community(page).click({ force: true });
      await expect(page.getByRole('alert')).toHaveCount(0);
      // The disabled switch keeps full opacity with muted text; only the track fades.
      expect(await community(page).evaluate(el => ({ switch: getComputedStyle(el).opacity, track: getComputedStyle(el.querySelector('.st-track')!).opacity, color: getComputedStyle(el).color }))).toEqual({ switch: '1', track: '0.6', color: 'rgb(88, 106, 100)' });
      await fixedWindow(page, app, 'newer');
      expect(await readFile(file, 'utf8')).toBe(newer);
    } finally { await app.close(); }
    // A settings file that can't be opened (a folder in its place) locks the switch until it is read again.
    await rm(file); await mkdir(file);
    ({ app, page } = await realEngine(data));
    try {
      await page.keyboard.press('Control+Comma');
      await expect(community(page)).toHaveAttribute('aria-disabled', 'true');
      await expect(page.getByRole('note')).toContainText('Settings file not read. Using the default, Off.');
      await expect(page.locator('.st-side')).toContainText('Not known');
      await expect(page.getByText(/Your choice is saved now/)).toHaveCount(0);
      const again = page.getByRole('note').getByRole('button', { name: 'Read again' });
      // The reference button height, with a gap above it.
      const box = await again.evaluate(el => ({ height: parseFloat(getComputedStyle(el).height), gap: getComputedStyle(el).marginTop }));
      expect(box.height).toBeCloseTo(26.6, 1);
      expect(box.gap).toBe('8px');
      await fixedWindow(page, app, 'not-read');
      await rm(file, { recursive: true });
      await again.focus(); await page.keyboard.press('Enter');
      await expect(community(page)).not.toHaveAttribute('aria-disabled', 'true');
      await expect(community(page)).toBeFocused();
    } finally { await app.close(); }
  } finally { await rm(data, { recursive: true, force: true }); }
});

test('Settings from Home names the area it returns to and gives it back unchanged', async () => {
  const session = await launch(true);
  try {
    const { page, app } = session;
    await size(app, 1280, 800);
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    const header = page.locator('.dh-toolbar'), entry = header.getByRole('button', { name: 'Settings' });
    await expect(page.locator('.dh-hgroup > *').last()).toHaveText('Settings');
    const areas = page.getByRole('navigation', { name: 'Build areas' });
    await areas.getByRole('button', { name: 'Tables', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Tables', exact: true })).toBeFocused();
    await page.locator('.tableitem').filter({ has: page.getByText('Item', { exact: true }) }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Item', exact: true })).toBeFocused();
    await entry.click();
    await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeFocused();
    await expect(page.getByRole('button', { name: '← Back to Tables' })).toBeVisible();
    await expect(page.locator('.st-statusbar')).toContainText('Open build kept:');
    await expect(page.locator('.st-statusbar')).toContainText('Authored test data');
    await expect(community(page)).toHaveAttribute('aria-checked', 'false');
    await fixedWindow(page, app, 'from-home');
    // Tab order: skip link, Back, then the switch; About has no controls.
    await page.getByRole('heading', { name: 'Settings', level: 1 }).focus();
    await page.keyboard.press('Tab');
    await expect(community(page)).toBeFocused();
    await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Shift+Tab');
    await expect(page.getByRole('button', { name: '← Back to Tables' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 2, name: 'Item', exact: true })).toBeVisible();
    await expect(areas.getByRole('button', { name: 'Tables', exact: true })).toHaveAttribute('aria-current', 'page');
    await expect(entry).toBeFocused();
    await text(page, 2);
    await expect.poll(() => page.evaluate(() => document.querySelector('.desktop-home')?.classList.contains('dh-compact'))).toBe(true);
    const layout = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
    expect(layout.sw).toBeLessThanOrEqual(layout.cw);
    await expect(entry).toBeVisible();
    await page.keyboard.press('Control+Comma');
    await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(entry).toBeFocused();
  } finally { await session.close(); }
});
