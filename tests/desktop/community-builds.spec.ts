import { test, expect, type ElectronApplication, type Page } from '@playwright/test';
import { mkdir, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { launch, root, size, text } from './helpers';

// D106 (#9): community builds from wago.tools and Check availability, with the authored engine (VIBEMINER_FIXTURE_COMMUNITY).
async function launchCommunity(marks: string[] = []) {
  process.env.VIBEMINER_FIXTURE_COMMUNITY = '1';
  let session;
  try { session = await launch(true); }
  finally { delete process.env.VIBEMINER_FIXTURE_COMMUNITY; }
  for (const mark of marks) await writeFile(path.join(session.data, mark), mark);
  return session;
}
const communityRow = (page: Page) => page.locator('.build-row', { hasText: '11.2.7.67542' });
const availability = (page: Page) => page.locator('#inspector .dv-availability');
// Both sizes and text scales: nothing scrolls sideways and the File availability row stays inside the inspector.
async function captures(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts', 'community-builds'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await size(app, width, height);
    for (const scale of [1, 2]) {
      await text(page, scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width} ${scale}`).toBe(false);
      expect(await page.evaluate(() => { const row = document.querySelector<HTMLElement>('#inspector .dv-availability'); return !row || row.scrollWidth <= row.clientWidth + 1; }), `${state} ${width} ${scale} availability fits`).toBe(true);
      await page.evaluate(() => document.querySelector('#inspector .dv-availability')?.scrollIntoView({ block: 'center' }));
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`) });
    }
  }
  await text(page, 1); await size(app, 1280, 800);
}

test('a community build has the Community badge, names wago.tools and can’t be opened', async () => {
  const { page, app, close } = await launchCommunity();
  try {
    await size(app, 1280, 800);
    const row = communityRow(page);
    await expect(row.locator('.dv-pill.community')).toHaveText('Community');
    await expect(page.locator('.build-row').first().locator('.dv-pill.community')).toHaveCount(0);
    await row.click();
    const inspector = page.locator('#inspector');
    await expect(inspector).toContainText('Community build history from wago.tools (not a Blizzard observation)');
    await expect(inspector).toContainText('Never seen by VibeMiner on Blizzard’s version service');
    await expect(page.getByRole('button', { name: 'Open build', exact: true })).toBeDisabled();
    await expect(inspector).toContainText('Opening community builds isn’t supported yet');
    await expect(availability(page)).toContainText('Not checked');
    await captures(page, app, 'community-selected');
    // Prepared and Current never list community builds.
    await page.getByRole('button', { name: 'Current', exact: true }).click();
    await expect(page.locator('.build-row', { hasText: '11.2.7.67542' })).toHaveCount(0);
  } finally { await close(); }
});

test('Check availability shows progress, stops only when asked, keeps focus on its button and shows the result', async () => {
  const { page, app, data, close } = await launchCommunity(['hold-availability']);
  try {
    await size(app, 1280, 800);
    await communityRow(page).click();
    const button = availability(page).getByRole('button', { name: 'Check availability' });
    await button.focus(); await page.keyboard.press('Enter');
    await expect(availability(page).getByRole('status')).toContainText('Checking archive indexes · 350 of 1,412…');
    const stop = availability(page).getByRole('button', { name: 'Stop check' });
    await expect(stop).toBeFocused();
    await captures(page, app, 'community-checking');
    await page.keyboard.press('Enter');
    await expect(availability(page)).toContainText('Check stopped. Nothing new was saved.');
    await expect(availability(page)).toContainText('Not checked');
    await expect(availability(page).getByRole('button', { name: 'Check availability' })).toBeFocused();
    await rm(path.join(data, 'hold-availability'));
    await availability(page).getByRole('checkbox', { name: 'Also every archive' }).check();
    await availability(page).getByRole('button', { name: 'Check availability' }).click();
    await expect(availability(page)).toContainText('Available · checked');
    await expect(availability(page)).toContainText('all 1,412 archive indexes present; all 1,412 archives present.');
    await expect(availability(page).getByRole('alert')).toHaveCount(0);
    await captures(page, app, 'community-available');
    // The saved result comes back with the page after a read.
    await page.getByRole('button', { name: 'Prepared', exact: true }).click();
    await page.getByRole('button', { name: 'All', exact: true }).click();
    await communityRow(page).click();
    await expect(availability(page)).toContainText('Available · checked');
  } finally { await close(); }
});

test('a community build missing on every host reads as Gone', async () => {
  const { page, app, close } = await launchCommunity(['availability-gone']);
  try {
    await size(app, 1280, 800);
    await communityRow(page).click();
    await availability(page).getByRole('button', { name: 'Check availability' }).click();
    await expect(availability(page)).toContainText('Gone · checked');
    await expect(availability(page)).toContainText('The build config is missing on every host asked (us.cdn.blizzard.com, level3.ssl.blizzard.com).');
    await captures(page, app, 'community-gone');
  } finally { await close(); }
});

test('a failed list keeps the earlier community builds and says when wago.tools is asked again', async () => {
  const { page, app, close } = await launchCommunity(['community-failed']);
  try {
    await size(app, 1280, 800);
    const notice = page.locator('.dv-notice', { hasText: 'Community build history not updated' });
    await expect(notice).toContainText('(COMMUNITY_BUILDS_OFFLINE)');
    await expect(notice).toContainText('Refresh builds asks wago.tools again after');
    await expect(communityRow(page).locator('.dv-pill.community')).toBeVisible();
    await captures(page, app, 'community-failed');
  } finally { await close(); }
});

test('Settings says the community build list is on by default, names wago.tools and what off does', async () => {
  const { page, close } = await launchCommunity();
  try {
    await page.getByRole('button', { name: 'Settings' }).first().click();
    const row = page.locator('#settings-row-community');
    await expect(row.getByRole('switch', { name: /Community build history/ })).toHaveAttribute('aria-checked', 'true');
    await expect(row).toContainText('Default: On');
    await expect(row).toContainText('wago.tools/api/builds');
    await expect(row).toContainText('at most once a day');
    await expect(row).toContainText('VibeMiner doesn’t contact wago.tools');
  } finally { await close(); }
});

test('at 600x480 and 200% text the community build inspector wraps with no sideways scroll', async () => {
  const { page, app, close } = await launchCommunity();
  try {
    await size(app, 600, 480); await text(page, 2);
    await communityRow(page).click();
    await expect(availability(page)).toContainText('Not checked');
    const overflow = () => page.evaluate(() => {
      const wide = (el: Element | null) => !!el && (el as HTMLElement).scrollWidth > (el as HTMLElement).clientWidth + 1;
      return { page: wide(document.documentElement), inspector: wide(document.getElementById('inspector')), body: wide(document.querySelector('.dv-insp-body')), area: wide(document.querySelector('.dv-body')) };
    });
    expect(await overflow()).toEqual({ page: false, inspector: false, body: false, area: false });
    await availability(page).scrollIntoViewIfNeeded();
    const directory = path.join(root, 'artifacts', 'community-builds'); await mkdir(directory, { recursive: true });
    await page.screenshot({ path: path.join(directory, 'community-600x480-200.png') });
  } finally { await close(); }
});

test('stopping a check is a calm stopped state, not an error', async () => {
  const { page, app, close } = await launchCommunity(['hold-availability']);
  try {
    await size(app, 1280, 800);
    await communityRow(page).click();
    await availability(page).getByRole('button', { name: 'Check availability' }).click();
    await expect(availability(page).getByRole('status').first()).toContainText('Checking archive indexes');
    // The running dot is the teal the list uses.
    const teal = await page.evaluate(() => getComputedStyle(document.querySelector('.discovery-screen')!).getPropertyValue('--teal').trim());
    expect(await availability(page).locator('.dv-pulse').evaluate(el => getComputedStyle(el).backgroundColor)).toBe(await page.evaluate(color => { const probe = document.createElement('span'); probe.style.color = color; document.body.append(probe); const value = getComputedStyle(probe).color; probe.remove(); return value; }, teal));
    await availability(page).getByRole('button', { name: 'Stop check' }).click();
    await expect(availability(page)).toContainText('Check stopped. Nothing new was saved.');
    await expect(availability(page).getByRole('alert')).toHaveCount(0);
  } finally { await close(); }
});

test('turning Community build history off hides community builds as soon as Settings is left', async () => {
  const { page, app, close } = await launchCommunity();
  try {
    await size(app, 1280, 800);
    await expect(communityRow(page)).toBeVisible();
    await page.getByRole('button', { name: 'Settings' }).first().click();
    const toggle = page.locator('#settings-row-community').getByRole('switch', { name: /Community build history/ });
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-checked', 'false');
    await page.getByRole('button', { name: '← Back to Discovery' }).click();
    await expect(page.locator('.build-row', { hasText: '11.2.7.67542' })).toHaveCount(0);
    await expect(page.locator('.build-row').first()).toBeVisible();
    await page.getByRole('button', { name: 'Settings' }).first().click();
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-checked', 'true');
    await page.keyboard.press('Escape');
    await expect(communityRow(page)).toBeVisible();
  } finally { await close(); }
});
