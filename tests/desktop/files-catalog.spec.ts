import { test, expect, type ElectronApplication, type Page } from '@playwright/test';
import { mkdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';

// D071 (#35): with the file catalog option the authored engine advertises file-catalog-v1 and reports a catalog prepared
// after Open, pending until the test writes file-catalog-ready (or failed with file-catalog-failed). How every Files state
// fits at each size and text scale is checked once, in files-fit.spec.ts.
const launchWithCatalog = () => launch(true, false, false, false, false, false, false, false, false, false, false, true);

async function capture(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/files-catalog'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) { await size(app, width, height); await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}.png`) }); }
  await text(page, 2); await size(app, 1280, 800); await page.screenshot({ path: path.join(directory, `${state}-1280x800-200.png`) }); await text(page, 1);
}
async function open(data: string, page: Page) {
  await page.locator('.build-row').first().click();
  await page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(data, 'release-open'), 'release');
  await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
}

test('Files says the file list is being prepared, hands it off in the footer without moving rows, then lists one row per FileDataID', async () => {
  const session = await launchWithCatalog();
  try {
    const { page, app, data } = session, nav = page.getByRole('navigation', { name: 'Build areas' });
    await size(app, 1280, 800); await open(data, page);
    const panel = page.locator('section[aria-labelledby="home-catalog"]');
    await expect(panel).toContainText('Preparing…'); await expect(panel).toContainText('Writing memberships: 1,048,576 of 3,262,082.');
    await nav.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    const note = page.getByRole('note', { name: 'Preparing file list…' });
    await expect(note.locator('.dh-dim').first()).toHaveText('Writing memberships: 1,048,576 of 3,262,082.');
    await expect(note.getByRole('button', { name: /^Stop preparing file list for / })).toBeVisible();
    await capture(page, app, 'preparing');
    // Sealed while Files shows the object list: offered in the footer; the rows, focus, selection and scroll stay put.
    const objects = page.getByRole('listbox'); await objects.focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown');
    await page.evaluate(() => { document.querySelector<HTMLElement>('.dh-files .dh-list-scroll')!.scrollTop = 60; });
    const before = await page.evaluate(() => ({ row: document.querySelector('.dh-files [aria-selected="true"]')!.getBoundingClientRect().top, scroll: document.querySelector<HTMLElement>('.dh-files .dh-list-scroll')!.scrollTop }));
    await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
    const show = page.locator('.dh-pane-foot').getByRole('button', { name: 'Show file list' });
    await expect(show).toBeVisible(); await expect(page.locator('.dh-catalog-ready [role=status]')).toContainText('File list ready');
    const after = await page.evaluate(() => ({ row: document.querySelector('.dh-files [aria-selected="true"]')!.getBoundingClientRect().top, scroll: document.querySelector<HTMLElement>('.dh-files .dh-list-scroll')!.scrollTop }));
    expect(after.scroll).toBe(before.scroll); expect(Math.abs(after.row - before.row)).toBeLessThan(0.5); await expect(objects).toBeFocused();
    await capture(page, app, 'ready-offered');
    await show.focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
    const list = page.getByRole('listbox', { name: 'Files, one row per FileDataID' });
    await expect(list.getByRole('option')).toHaveCount(42); await expect(list.getByRole('option').first()).toContainText('800001');
    await expect(page.locator('.dh-statusbar')).toContainText('File catalog generation 41');
    await capture(page, app, 'ready-list');
    await nav.getByRole('button', { name: 'Home', exact: true }).click();
    await expect(panel).toContainText('Ready to browse');
    await nav.getByRole('button', { name: 'Files', exact: true }).click(); await expect(list.getByRole('option')).toHaveCount(42);
  } finally { await session.close(); }
});

test('preparation is one Downloads & activity task with its stage; Stop names its build and Resume continues it', async () => {
  const session = await launchWithCatalog();
  try {
    const { page, app, data } = session;
    await size(app, 1280, 800); await open(data, page);
    await page.locator('.download-activity-trigger:visible, .dv-chip:visible').first().click();
    const panel = page.locator('#download-activity-panel');
    const task = panel.locator('article').filter({ has: page.getByRole('heading', { name: 'File list · ROOT and install' }) });
    await expect(task).toContainText('Writing memberships'); await expect(task).toContainText('1,048,576 of 3,262,082');
    // The counts appear once in the task; the stage names the step.
    expect(((await task.textContent()) ?? '').match(/1,048,576 of 3,262,082/g)).toHaveLength(1);
    await capture(page, app, 'activity-task');
    await task.getByRole('button', { name: /^Stop preparing file list for / }).focus(); await page.keyboard.press('Enter');
    await expect(task.getByRole('button', { name: /^Resume preparing file list for / })).toBeVisible();
    // The replaced action's task keeps focus (its heading), never the document body.
    await expect.poll(() => page.evaluate(() => document.activeElement !== document.body && !!document.activeElement?.closest('#download-activity-panel'))).toBe(true);
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Browse in Files' }).click();
    const stopped = page.getByRole('note', { name: 'File list preparation stopped' });
    await expect(stopped).toContainText('The rows written so far are kept');
    // Enter on Resume: the note's new action takes focus, not the document body.
    await stopped.getByRole('button', { name: /^Resume preparing file list for / }).focus(); await page.keyboard.press('Enter');
    const preparing = page.getByRole('note', { name: 'Preparing file list…' });
    await expect(preparing.getByRole('button', { name: /^Stop preparing file list for / })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('note', { name: 'File list preparation stopped' }).getByRole('button', { name: /^Resume preparing file list for / })).toBeFocused();
  } finally { await session.close(); }
});

test('a failed file list preparation states its cause once and offers Retry and Reopen', async () => {
  const session = await launchWithCatalog();
  try {
    const { page, app, data } = session;
    await writeFile(path.join(data, 'file-catalog-failed'), 'failed');
    await size(app, 1280, 800); await open(data, page);
    await expect(page.locator('section[aria-labelledby="home-catalog"]')).toContainText('STORAGE_UNAVAILABLE');
    await page.getByRole('button', { name: 'Browse in Files' }).click();
    const note = page.getByRole('note', { name: 'File list not prepared' });
    await expect(note).toContainText('It failed three times.'); await expect(note).toContainText('Cause: IOException: Input/output error');
    await expect(note.getByRole('button', { name: 'Reopen this build' })).toBeVisible();
    await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
    await capture(page, app, 'failed');
    await rm(path.join(data, 'file-catalog-failed'));
    await note.getByRole('button', { name: /^Retry preparing file list for / }).focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('note', { name: 'Preparing file list…' }).getByRole('button', { name: /^Stop preparing file list for / })).toBeFocused();
  } finally { await session.close(); }
});

test('on Home, Enter on Retry status keeps focus in the File catalog panel once the read succeeds', async () => {
  const session = await launchWithCatalog();
  try {
    const { page, app, data } = session;
    await writeFile(path.join(data, 'file-catalog-status-error'), 'fail');
    await size(app, 1280, 800); await open(data, page);
    const panel = page.locator('section[aria-labelledby="home-catalog"]');
    const retry = panel.getByRole('button', { name: 'Retry status' });
    await expect(panel).toContainText('Authored status read failure.'); await retry.focus();
    await rm(path.join(data, 'file-catalog-status-error'));
    await page.keyboard.press('Enter');
    await expect(panel).toContainText('Preparing…');
    await expect(page.getByRole('heading', { name: 'File catalog', exact: true })).toBeFocused();
  } finally { await session.close(); }
});

test('with focus on Stop when the list turns ready, focus moves to Show file list, at 100% in the footer and at 200% in the note', async () => {
  for (const scale of [1, 2]) {
    const session = await launchWithCatalog();
    try {
      const { page, app, data } = session;
      await size(app, 1280, 800); await open(data, page);
      await page.getByRole('button', { name: 'Browse in Files' }).click();
      await expect(page.getByText('Object 1', { exact: true })).toBeVisible();
      await text(page, scale);
      await page.getByRole('note', { name: 'Preparing file list…' }).getByRole('button', { name: /^Stop preparing file list for / }).focus();
      await writeFile(path.join(data, 'file-catalog-ready'), 'ready');
      const show = page.locator(scale === 2 ? '.dh-catalog-missing' : '.dh-pane-foot').getByRole('button', { name: 'Show file list' });
      await expect(show).toBeFocused();
      await page.keyboard.press('Enter');
      await expect(page.getByRole('heading', { name: 'Files', exact: true })).toBeFocused();
      await expect(page.getByRole('listbox', { name: 'Files, one row per FileDataID' }).getByRole('option').first()).toContainText('800001');
      await text(page, 1);
    } finally { await session.close(); }
  }
});
