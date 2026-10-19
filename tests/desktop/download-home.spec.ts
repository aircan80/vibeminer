import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, writeFile, unlink, readFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';

async function capture(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/size-first'); await mkdir(directory, { recursive: true });
  const base = 15;
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
    for (const scale of [1, 2]) {
      await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, base * scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width}x${height} ${scale * 100}%`).toBe(false);
      expect(await page.locator('.aside .row b').evaluateAll(elements => elements.filter(element => element.textContent === 'Unknown' && element.getClientRects().length > 0).every(element => { const range = document.createRange(); range.selectNodeContents(element); return range.getClientRects().length === 1; })), `Unknown stays a whole word at ${width}x${height} ${scale * 100}%`).toBe(true);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`), fullPage: true });
    }
  }
}
test('unknown download sizing enters honest Home and restores Discovery without a network refresh', async () => {
  const session = await launch(true);
  try {
    await writeFile(path.join(session.data, 'download-unavailable'), 'unavailable');
    await session.page.locator('.build-row').first().click();
    const open = session.page.getByRole('button', { name: 'Open Build', exact: true }); await open.focus(); await session.page.keyboard.press('Enter');
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(session.page.getByRole('heading', { name: 'Build overview' })).toBeFocused();
    await expect(session.page.getByText('Published download size is unknown.', { exact: true })).toBeVisible();
    await expect(session.page.getByText('DOWNLOAD_UNDECLARED', { exact: true })).toBeVisible();
    await expect(session.page.getByRole('button', { name: 'Browse download objects' })).toHaveCount(0);
    await expect(session.page.getByRole('button', { name: 'Download all', exact: true })).toHaveCount(0);
    await capture(session.page, session.app, 'download-unavailable');
    await session.page.getByRole('button', { name: /Choose build/ }).click(); await expect(open).toBeFocused();
    const calls = (await readFile(path.join(session.data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    expect(calls.filter(call => call.method === 'discovery.refresh' || call.method === 'download.read')).toHaveLength(0);
  } finally { await session.close(); }
});
test('download object paging retains rows and keyboard anchor during local reads and recoverable failures', async () => {
  const session = await launch(true);
  try {
    const { page } = session;
    await page.locator('.build-row').first().click(); await page.getByRole('button', { name: 'Open Build', exact: true }).click();
    await writeFile(path.join(session.data, 'release-open'), 'release');
    await expect(page.getByRole('heading', { name: 'Build overview' })).toBeVisible();
    await capture(page, session.app, 'download-home');
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    const browse = page.getByRole('button', { name: 'Browse download objects' }); await browse.focus(); await page.keyboard.press('Enter');
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await writeFile(path.join(session.data, 'hold-download-page'), 'hold'); await writeFile(path.join(session.data, 'download-page-error'), 'error');
    const next = page.getByRole('button', { name: 'Next page', exact: true }); await next.focus(); await page.keyboard.press('Enter');
    await expect(next).toBeFocused(); await expect(next).toHaveAttribute('aria-disabled', 'true'); await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await capture(page, session.app, 'download-objects-loading');
    await writeFile(path.join(session.data, 'release-download-page'), 'release');
    await expect(page.getByRole('alert')).toContainText('LOCAL_READ_FAILED'); await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await capture(page, session.app, 'download-objects-error');
    await unlink(path.join(session.data, 'download-page-error')); const retry = page.getByRole('button', { name: 'Retry page' }); await retry.focus(); await page.keyboard.press('Enter');
    await expect(page.getByText('Object 51', { exact: true })).toBeVisible(); await expect(page.getByRole('heading', { name: 'Download objects' })).toBeFocused();
    await page.getByRole('button', { name: /Return to build overview/ }).click(); await expect(browse).toBeFocused();
  } finally { await session.close(); }
});
