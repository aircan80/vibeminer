import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, writeFile, unlink } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';
import { activityTask, closeActivity } from './activity-helpers';

// D081 (#55): definitions fetched from WoWDBDefs when no bundled one covers a table, with the authored fixture engine.
async function tables(marks: string[] = [], settings?: object) {
  const session = await launch(true);
  for (const mark of marks) await writeFile(path.join(session.data, mark), mark);
  if (settings) await writeFile(path.join(session.data, 'settings.json'), JSON.stringify(settings));
  await session.app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
  const { page } = session;
  await page.locator('.build-row').first().click();
  await page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(session.data, 'release-open'), 'release');
  await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible();
  await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
  await expect(page.locator('.tableitem')).toHaveCount(50);
  return session;
}
const pick = (page: Page, name: string) => page.locator('.tableitem').filter({ has: page.getByText(name, { exact: true }) }).click();
async function captures(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts', 'table-fetched'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
    for (const scale of [1, 2]) {
      await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width} ${scale}`).toBe(false);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`), fullPage: true });
    }
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
}
// The first record row stays whole and the notice never becomes a scroll box.
const rowVisible = (page: Page) => page.evaluate(() => {
  const grid = document.querySelector<HTMLElement>('.dh-data:not([hidden]) .dh-grid-scroll'), notice = document.querySelector<HTMLElement>('.dh-data:not([hidden]) .fetched-definition');
  if (!grid || !notice) return false;
  const row = grid.querySelector('tbody tr')!.getBoundingClientRect(), box = grid.getBoundingClientRect();
  return row.top >= box.top - 1 && row.bottom <= box.bottom + 1 && notice.offsetHeight > 0 && notice.scrollHeight <= notice.clientHeight + 1;
});

// At 1280×800 and 200% text, every action of a table that can't load is reachable: its scroll pane fits inside the
// Data area, and scrolling brings each button wholly into view.
async function actionsReachable(page: Page, app: ElectronApplication, names: string[]) {
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1280, 800));
  await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
  await expect.poll(() => page.evaluate(() => document.querySelector('.desktop-home')?.classList.contains('dh-compact'))).toBe(true);
  const records = page.getByRole('group', { name: 'Show pane' }).filter({ visible: true }).getByRole('button', { name: 'Records' });
  if (await records.count()) await records.click();
  for (const name of names) {
    const button = page.locator('.dh-data:not([hidden]) .dh-state-box').getByRole('button', { name, exact: true });
    await button.scrollIntoViewIfNeeded();
    expect(await button.evaluate(element => {
      const pane = element.closest<HTMLElement>('.dh-pane-scroll')!, data = pane.closest<HTMLElement>('.dh-data')!;
      const b = element.getBoundingClientRect(), p = pane.getBoundingClientRect(), d = data.getBoundingClientRect();
      return p.top >= d.top - 1 && p.bottom <= d.bottom + 1 && b.top >= p.top - 1 && b.bottom <= p.bottom + 1 && b.height > 0;
    }), `${name} reachable at 200%`).toBe(true);
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
}

test('a table no bundled definition covers opens with a fetched WoWDBDefs definition and says where it came from', async () => {
  const session = await tables(['table-fetched']);
  try {
    const { page } = session, inspector = page.locator('.dh-tables .dh-inspector');
    await pick(page, 'ItemClass');
    // Before Data loads: the bundled definition has no layout on this build's line, and opening Data fetches one.
    await expect(page.getByText('SCHEMA_BUILD_NOT_LISTED', { exact: true })).toBeVisible();
    await expect(inspector.locator('.definition-match')).toHaveText('Build not listed · no bundled layout on its version line; opening Data fetches a newer definition from WoWDBDefs');
    await expect(inspector.locator('.definition-source')).toHaveText('Bundled with VibeMiner');
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    // One notice names the source, date and revision; the bundled problem is gone.
    const notice = page.locator('.dh-data:not([hidden]) .fetched-definition');
    await expect(notice.locator('strong')).toHaveText(/^Definition from WoWDBDefs \(fetched .*2026, a50ca6f\)$/);
    await expect(notice).toContainText('CC BY-SA 4.0');
    await expect(page.getByText('SCHEMA_BUILD_NOT_LISTED', { exact: true })).toHaveCount(0);
    await expect(page.locator('.dh-foot-count').first()).toContainText('Definition from WoWDBDefs');
    await expect(inspector.locator('.definition-source')).toContainText('not bundled or verified · WoWDBDefs contributors, CC BY-SA 4.0');
    await expect(inspector).toContainText('a50ca6ffbcfe17d604327c9078993675f7ae831f');
    await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
    await expect.poll(() => rowVisible(page)).toBe(true);
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    await captures(page, session.app, 'fetched');
    await page.getByRole('button', { name: 'Inspect record 0', exact: true }).click();
    await expect(inspector).toContainText('Definition from WoWDBDefs (fetched');
    await captures(page, session.app, 'fetched-record');
    await page.getByRole('button', { name: 'Structure', exact: true }).click();
    await expect(page.locator('.dh-structure-bar')).toContainText('Candidate 35680EB8');
    await expect(page.locator('.dh-pane-foot').filter({ hasText: 'fetched revision a50ca6f' })).toBeVisible();
    await captures(page, session.app, 'fetched-structure');
  } finally { await session.close(); }
});

test('a failed fetch is its own state for that table only, other tables still open, and Retry fetches', async () => {
  const session = await tables(['table-fetched', 'table-fetch-offline']);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(await activityTask(page, 'ItemClass table')).toContainText('DEFINITION_FETCH_OFFLINE');
    await closeActivity(page);
    await expect(page.getByRole('button', { name: 'Retry table data', exact: true })).toBeVisible();
    const state = page.locator('.dh-data:not([hidden]) .dh-state-box');
    await expect(state).toContainText('Tables the bundled definitions cover still open.');
    await expect(state).toContainText('turn off Fetch newer table definitions in Settings');
    await expect(state.getByRole('button', { name: 'Open Settings', exact: true })).toBeVisible();
    await actionsReachable(page, session.app, ['View task', 'Retry table data', 'Open Settings']);
    await captures(page, session.app, 'fetch-offline');
    await pick(page, 'Item');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await unlink(path.join(session.data, 'table-fetch-offline'));
    await pick(page, 'ItemClass');
    await page.getByRole('button', { name: 'Retry table data', exact: true }).click();
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toBeVisible();
  } finally { await session.close(); }
});

test('with fetching turned off, a table no bundled definition covers stays closed and says so', async () => {
  const session = await tables(['table-fetched'], { values: { fetchTableDefinitions: false }, changedAt: '2026-10-23T12:00:00Z' });
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-tables .dh-inspector .definition-match')).toHaveText('Build not listed · no layout on its version line, so the table can’t open');
    await expect(await activityTask(page, 'ItemClass table')).toContainText('SCHEMA_BUILD_NOT_LISTED');
    await closeActivity(page);
    await expect(page.locator('.fetched-definition')).toHaveCount(0);
    // The next step is in Settings: it says so, offers no Retry that can't help, and opens Settings with Back returning here.
    const state = page.locator('.dh-data:not([hidden]) .dh-state-box');
    await expect(state).toContainText('Fetching newer table definitions from WoWDBDefs is off in Settings; turn it on');
    await expect(state.getByRole('button', { name: 'Retry table data', exact: true })).toHaveCount(0);
    const open = state.getByRole('button', { name: 'Open Settings', exact: true });
    await open.click();
    await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeFocused();
    await expect(page.getByRole('switch', { name: 'Fetch newer table definitions from WoWDBDefs' })).toHaveAttribute('aria-checked', 'false');
    await page.keyboard.press('Escape');
    await expect(open).toBeFocused();
    await actionsReachable(page, session.app, ['View task', 'Open Settings']);
    await captures(page, session.app, 'fetch-off');
  } finally { await session.close(); }
});

for (const [code, message, retryable] of [
  ['DEFINITION_FETCH_TOO_LARGE', 'The newest WoWDBDefs ItemClass definition is larger than VibeMiner allows (1 MiB), so it wasn’t used. The pinned ItemClass definition doesn’t list build 12.1.5.70448 or any layout on its version line, so this table can’t open. Tables the bundled definitions cover still open. A later VibeMiner or WoWDBDefs update may cover it.', false],
  ['DEFINITION_FETCH_INVALID', 'The newest WoWDBDefs ItemClass definition couldn’t be read safely (DBD_LIMIT), so it wasn’t used. The pinned ItemClass definition doesn’t list build 12.1.5.70448 or any layout on its version line, so this table can’t open. Tables the bundled definitions cover still open. A later VibeMiner or WoWDBDefs update may cover it.', false],
  ['DEFINITION_FETCH_NOT_FOUND', 'WoWDBDefs on GitHub has no ItemClass.dbd file to fetch, so only the bundled ItemClass definition can be used. The pinned ItemClass definition doesn’t list build 12.1.5.70448 or any layout on its version line, so this table can’t open. Tables the bundled definitions cover still open. A later VibeMiner or WoWDBDefs update may cover it.', false],
  ['DEFINITION_FETCH_FAILED', 'Fetching a newer ItemClass definition from WoWDBDefs failed. GitHub is limiting requests right now. The pinned ItemClass definition doesn’t list build 12.1.5.70448 or any layout on its version line, so this table can’t open. Tables the bundled definitions cover still open. Retry table data asks again; if it still fails, try later.', true],
] as const) test(`${code} says what still works and keeps its actions reachable at 200%`, async () => {
  const session = await tables(['table-fetched']);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'table-fetch-error.json'), JSON.stringify({ code, message, retryable }));
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    const state = page.locator('.dh-data:not([hidden]) .dh-state-box');
    await expect(state).toContainText(`(${code})`);
    await expect(state).toContainText('Tables the bundled definitions cover still open.');
    await actionsReachable(page, session.app, retryable ? ['View task', 'Retry table data'] : ['View task']);
  } finally { await session.close(); }
});

test('a fetched definition matched by layout keeps its date and commit visible at 200% with a record row', async () => {
  const session = await tables(['table-fetched', 'table-layout-only']);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    const heading = page.locator('.dh-data:not([hidden]) .fetched-definition strong');
    await expect(heading).toHaveText(/^Definition from WoWDBDefs \(fetched .*2026, a50ca6f\) · Matched by layout, build not listed$/);
    await page.evaluate(() => { document.documentElement.style.fontSize = '30px'; });
    await expect.poll(() => rowVisible(page)).toBe(true);
    // At 200% the source keeps its date and commit uncut, one short reason stays beside it, and a record row stays.
    // Turning fetching off wouldn't open this layout, so there's no Settings pointer.
    await expect(heading).toHaveText(/^WoWDBDefs · .*2026 · a50ca6f$/);
    await expect.poll(() => heading.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    const short = page.locator('.dh-data:not([hidden]) .fetched-short');
    await expect(short).toHaveText('Only its block matches layout 35680EB8.');
    await expect.poll(() => short.evaluate(element => element.getBoundingClientRect().width > 0)).toBe(true);
    // The code stays whole inside the notice; only the short reason may shorten.
    const code = page.locator('.dh-data:not([hidden]) .fetched-definition .reference-code');
    await expect(code).toHaveText('(SCHEMA_BUILD_NOT_LISTED)');
    await expect.poll(() => code.evaluate(element => {
      const notice = element.closest('.dh-notice')!.getBoundingClientRect(), box = element.getBoundingClientRect();
      return element.scrollWidth <= element.clientWidth + 1 && box.width > 0 && box.right <= notice.right + 1;
    })).toBe(true);
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).not.toContainText('turn off Fetch newer table definitions');
    await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
    await captures(page, session.app, 'fetched-layout');
    // The reason is the real rule: no definition lists the build, and only the fetched block matches the layout.
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toContainText('only the WoWDBDefs block matches this file’s layout 35680EB8');
  } finally { await session.close(); }
});

// Flip the fetch switch in Settings and come back where we were.
async function setFetching(page: Page, on: boolean) {
  await page.getByRole('button', { name: 'Settings' }).click();
  const toggle = page.getByRole('switch', { name: 'Fetch newer table definitions from WoWDBDefs' });
  await expect(toggle).toHaveAttribute('aria-checked', String(!on));
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-checked', String(on));
  await page.keyboard.press('Escape');
}

test('switching fetching off and on reaches a table that is already open, and the notice gives the real reason', async () => {
  const session = await tables(['table-fetched-exact']);
  try {
    const { page } = session, notice = page.locator('.dh-data:not([hidden]) .dh-notice.dh-keep');
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await expect(notice.locator('strong')).toHaveText(/^Definition from WoWDBDefs \(fetched .*, a50ca6f\)$/);
    // Fetched because it lists the exact build, not because the bundled one lacks the layout.
    await expect(notice).toContainText('The bundled ItemClass definition doesn’t list build 12.1.5.70448; the newest WoWDBDefs definition does');
    await expect(notice).not.toContainText('No bundled definition covers');
    await expect(notice).not.toContainText('(exact build first)');
    // Turning fetching off would still open it by layout, so the notice says where.
    await expect(notice).toContainText('To read it with the bundled definition only, turn off Fetch newer table definitions in Settings.');
    // Off, then reopen by picking another table and coming back: bundled rows by layout.
    await setFetching(page, false);
    await pick(page, 'Item');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await pick(page, 'ItemClass');
    await expect(notice.locator('strong')).toHaveText('Matched by layout, build not listed');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toHaveCount(0);
    await expect(page.locator('.dh-tables .dh-inspector .definition-source')).toHaveText('Bundled with VibeMiner');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    // On again, then Home and back: the fetched rows return.
    await setFetching(page, true);
    const areas = page.getByRole('navigation', { name: 'Build areas' });
    await areas.getByRole('button', { name: 'Home', exact: true }).click();
    await areas.getByRole('button', { name: 'Tables', exact: true }).click();
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition strong')).toHaveText(/^Definition from WoWDBDefs/);
    await expect(page.locator('.dh-tables .dh-inspector .definition-source')).toContainText('not bundled or verified');
  } finally { await session.close(); }
});

test('a reopened table waits for another table’s load, keeps its record filter and selection, and says why it re-read', async () => {
  const session = await tables(['table-fetched-exact']);
  try {
    const { page } = session, notice = page.locator('.dh-data:not([hidden]) .dh-notice.dh-keep');
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toBeVisible();
    const filter = page.getByRole('textbox', { name: 'Raw record ID (exact match)' });
    await filter.fill('7');
    await page.getByRole('button', { name: 'Apply record ID' }).click();
    const record = page.getByRole('button', { name: 'Inspect record 7', exact: true });
    await record.click();
    await expect(record).toHaveAttribute('aria-pressed', 'true');
    // Off behind the view's back (no Settings change event, so only the reopening can notice), then pick Item while
    // its load is held, and go back to ItemClass before it finishes.
    await writeFile(path.join(session.data, 'settings.json'), JSON.stringify({ values: { fetchTableDefinitions: false }, changedAt: '2026-10-25T12:00:00Z' }));
    await writeFile(path.join(session.data, 'hold-table-50238ec2'), 'hold');
    await pick(page, 'Item');
    await expect(await activityTask(page, 'Item table')).toBeVisible();
    await closeActivity(page);
    await pick(page, 'ItemClass');
    await expect(page.locator('#table-title')).toHaveText('ItemClass');
    // ItemClass has dropped its old rows and waits for Item, rather than keeping the fetched rows on screen.
    await expect(page.locator('.dh-data:not([hidden]) .dh-state-title')).toHaveText('Waiting for the active table');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toHaveCount(0);
    await writeFile(path.join(session.data, 'release-table-50238ec2'), 'release');
    await expect(notice.locator('strong')).toHaveText('Matched by layout, build not listed');
    // The record ID filter and the selected record stay; the footer says why the rows changed.
    await expect(filter).toHaveValue('7');
    await expect(page.getByRole('button', { name: 'Inspect record 7', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.dh-reread').first()).toHaveText('Re-read with the bundled definition because fetching newer table definitions is off in Settings.');
  } finally { await session.close(); }
});

test('switching fetching off while a table is checking WoWDBDefs opens it with bundled, never stuck', async () => {
  const session = await tables(['table-fetched-exact', 'hold-table-b977271e']);
  try {
    const { page } = session, notice = page.locator('.dh-data:not([hidden]) .dh-notice.dh-keep');
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(await activityTask(page, 'ItemClass table')).toContainText('Checking WoWDBDefs for the newest ItemClass definition');
    await closeActivity(page);
    // The load started with fetching on; it's switched off before the load finishes.
    await setFetching(page, false);
    await writeFile(path.join(session.data, 'release-table-b977271e'), 'release');
    await expect(notice.locator('strong')).toHaveText('Matched by layout, build not listed');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toHaveCount(0);
    await expect(page.getByText('TABLE_DATA_CONTEXT_MISMATCH', { exact: true })).toHaveCount(0);
    // Away and back, it stays open.
    await pick(page, 'Item');
    await pick(page, 'ItemClass');
    await expect(notice.locator('strong')).toHaveText('Matched by layout, build not listed');
  } finally { await session.close(); }
});

test('switching fetching on while a table loads with bundled re-checks it and opens with the fetched definition', async () => {
  const session = await tables(['table-fetched-exact', 'hold-table-b977271e'], { values: { fetchTableDefinitions: false }, changedAt: '2026-10-23T12:00:00Z' });
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(await activityTask(page, 'ItemClass table')).toContainText('Locating selected table content');
    await closeActivity(page);
    await setFetching(page, true);
    await writeFile(path.join(session.data, 'release-table-b977271e'), 'release');
    // It finishes with bundled, as it started, and the switch change then re-checks it under fetching on.
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition strong')).toHaveText(/^Definition from WoWDBDefs/);
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await expect(page.getByText('TABLE_DATA_CONTEXT_MISMATCH', { exact: true })).toHaveCount(0);
  } finally { await session.close(); }
});

test('rows whose definition can never be shown say so with a Retry that works, and never stick', async () => {
  const session = await tables(['table-fetched-exact', 'table-definition-stale']);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    const alert = page.locator('.dh-data:not([hidden]) .dh-error');
    await expect(alert).toContainText('TABLE_DATA_CONTEXT_MISMATCH');
    const retry = alert.getByRole('button', { name: 'Retry table data', exact: true });
    await expect(retry).toBeVisible();
    await unlink(path.join(session.data, 'table-definition-stale'));
    await retry.click();
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition strong')).toHaveText(/^Definition from WoWDBDefs/);
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
  } finally { await session.close(); }
});

test('a table left on a definition mismatch asks again when it is reopened, and opens', async () => {
  const session = await tables(['table-fetched-exact', 'table-definition-stale']);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .dh-error')).toContainText('TABLE_DATA_CONTEXT_MISMATCH');
    await unlink(path.join(session.data, 'table-definition-stale'));
    // Away and back, without Retry: the reopening asks again.
    await pick(page, 'Item');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition strong')).toHaveText(/^Definition from WoWDBDefs/);
    await expect(page.getByText('TABLE_DATA_CONTEXT_MISMATCH', { exact: true })).toHaveCount(0);
  } finally { await session.close(); }
});

test('a switch saved after Settings was left still reaches the open table', async () => {
  const session = await tables(['table-fetched-exact', 'hold-settings-set']);
  try {
    const { page } = session;
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toBeVisible();
    await page.getByRole('button', { name: 'Settings' }).click();
    const toggle = page.getByRole('switch', { name: 'Fetch newer table definitions from WoWDBDefs' });
    await toggle.click();
    await expect(toggle).toHaveText('Turning off…');
    // Back before the save replies; the table hears about it once the save commits.
    await page.keyboard.press('Escape');
    await expect(page.locator('#table-title')).toHaveText('ItemClass');
    await writeFile(path.join(session.data, 'release-settings-set'), 'release');
    await expect(page.locator('.dh-data:not([hidden]) .dh-notice.dh-keep strong')).toHaveText('Matched by layout, build not listed');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition')).toHaveCount(0);
  } finally { await session.close(); }
});

const offInSettings = 'Fetching newer table definitions from WoWDBDefs is off in Settings';

test('a table stopped because fetching is off opens once fetching is turned on in Settings', async () => {
  const session = await tables(['table-fetched'], { values: { fetchTableDefinitions: false }, changedAt: '2026-10-25T12:00:00Z' });
  try {
    const { page } = session, state = page.locator('.dh-data:not([hidden]) .dh-state-box');
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(state).toContainText(offInSettings);
    // QA's steps: Open Settings, switch on, Back.
    await state.getByRole('button', { name: 'Open Settings', exact: true }).click();
    await setFetchingHere(page, true);
    await page.keyboard.press('Escape');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition strong')).toHaveText(/^Definition from WoWDBDefs/);
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await expect(page.getByText(offInSettings, { exact: false })).toHaveCount(0);
  } finally { await session.close(); }
});

test('a table stopped by a failed fetch gives the switched-off answer once fetching is turned off', async () => {
  const session = await tables(['table-fetched', 'table-fetch-offline']);
  try {
    const { page } = session, state = page.locator('.dh-data:not([hidden]) .dh-state-box');
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(state).toContainText('(DEFINITION_FETCH_OFFLINE)');
    await state.getByRole('button', { name: 'Open Settings', exact: true }).click();
    await setFetchingHere(page, false);
    await page.keyboard.press('Escape');
    // Prepared again under the new setting: no stale offline answer, and the inspector agrees.
    await expect(state).toContainText(offInSettings);
    await expect(state).not.toContainText('DEFINITION_FETCH_OFFLINE');
    await expect(page.locator('.dh-tables .dh-inspector .definition-match')).toHaveText('Build not listed · no layout on its version line, so the table can’t open');
  } finally { await session.close(); }
});

test('a table stopped because fetching is off prepares again when reopened after the setting changed elsewhere', async () => {
  const session = await tables(['table-fetched'], { values: { fetchTableDefinitions: false }, changedAt: '2026-10-25T12:00:00Z' });
  try {
    const { page } = session;
    // The inspector reads the definition again after the load stops (#81). Held a while, that read sees the new
    // setting, so a reopening that compared with the definition instead of the stop missed the change (#132).
    await writeFile(path.join(session.data, 'table-definition-delay.json'), '1500');
    await writeFile(path.join(session.data, 'release-table'), 'release');
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .dh-state-box')).toContainText(offInSettings);
    // The setting changes without a Settings change event; the next opening notices.
    await writeFile(path.join(session.data, 'settings.json'), JSON.stringify({ values: { fetchTableDefinitions: true }, changedAt: '2026-10-25T12:05:00Z' }));
    // That read lands under the new setting; the stop still says what it stopped under and offers its Settings step.
    await expect(page.locator('.dh-tables .dh-inspector .definition-match')).toHaveText('Build not listed · no bundled layout on its version line; opening Data fetches a newer definition from WoWDBDefs');
    await expect(page.locator('.dh-data:not([hidden]) .dh-state-box').getByRole('button', { name: 'Open Settings', exact: true })).toBeVisible();
    await pick(page, 'Item');
    await expect(page.getByRole('button', { name: 'Inspect record 0', exact: true })).toBeVisible();
    await pick(page, 'ItemClass');
    await expect(page.locator('.dh-data:not([hidden]) .fetched-definition strong')).toHaveText(/^Definition from WoWDBDefs/);
  } finally { await session.close(); }
});

// Flip the fetch switch on the Settings screen that is already open.
async function setFetchingHere(page: Page, on: boolean) {
  const toggle = page.getByRole('switch', { name: 'Fetch newer table definitions from WoWDBDefs' });
  await expect(toggle).toHaveAttribute('aria-checked', String(!on));
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-checked', String(on));
}
