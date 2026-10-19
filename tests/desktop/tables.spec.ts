import { test, expect, type Page, type ElectronApplication } from '@playwright/test';
import { mkdir, readFile, writeFile, unlink } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';

async function opened() {
  const session = await launch(true);
  await session.page.locator('.build-row').first().click();
  await session.page.getByRole('button', { name: 'Open build', exact: true }).click();
  await writeFile(path.join(session.data, 'release-open'), 'release');
  await expect(session.page.getByRole('heading', { name: 'Build overview', exact: true })).toBeVisible();
  return session;
}
async function calls(directory: string) {
  return (await readFile(path.join(directory, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
}
function definition(page: Page, name: string) {
  return page.locator('.tableitem').filter({ has: page.getByText(name, { exact: true }) });
}
async function captures(page: Page, app: ElectronApplication, state: string) {
  const directory = path.join(root, 'artifacts/tables'); await mkdir(directory, { recursive: true });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
    for (const scale of [1, 2]) {
      await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale);
      expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1), `${state} ${width}x${height} ${scale * 100}%`).toBe(false);
      await page.screenshot({ path: path.join(directory, `${state}-${width}x${height}-${scale * 100}.png`), fullPage: true });
    }
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '15px'; });
}

test('horizontal areas preserve exact build, object pages and a keyboard definition investigation', async () => {
  const session = await opened();
  try {
    const { page } = session, areas = page.getByRole('navigation', { name: 'Build areas' });
    await expect(page.locator('.home-view .sidebar')).toHaveCount(0);
    await areas.getByRole('button', { name: 'Download objects', exact: true }).click();
    await page.getByRole('button', { name: 'Next page', exact: true }).click();
    await expect(page.getByText('Object 51', { exact: true })).toBeVisible();
    const tables = areas.getByRole('button', { name: 'Tables', exact: true }); await tables.focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Tables', exact: true })).toBeFocused();
    await expect(page.locator('.tableitem')).toHaveCount(50);
    const item = definition(page, 'Item'); await item.focus(); await page.keyboard.press('Enter');
    await expect(page.locator('.table-title h2')).toHaveText('Item');
    await page.getByRole('button',{name:'Structure',exact:true}).click();
    await expect(page.locator('.field-count')).toContainText('16 declared fields');
    await expect(page.locator('.candidate-choice')).toContainText('996192AA');
    await page.getByRole('button', { name: 'Relationships', exact: true }).click();
    const relation = page.locator('.declared-relations').getByRole('button', { name: 'Open ItemClass.ClassID definition', exact: true });
    await relation.focus(); await page.keyboard.press('Enter');
    await expect(page.locator('.table-title h2')).toHaveText('ItemClass');
    await expect(page.getByRole('heading', { name: 'ClassID', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Back through definitions' }).click();
    await expect(page.locator('.table-title h2')).toHaveText('Item'); await expect(relation).toBeFocused();
    await page.getByRole('button', { name: 'Structure', exact: true }).click();
    await captures(page, session.app, 'structure');
    const search = page.getByRole('searchbox', { name: 'Search definitions' }); await search.fill('no_such_table_0011');
    await expect(page.getByText('No provider definitions match this search.', { exact: false })).toBeVisible();
    await expect(search).toBeFocused(); await expect(page.locator('.table-title h2')).toHaveText('Item');
    await areas.getByRole('button', { name: 'Home', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build overview', exact: true })).toBeFocused();
    await tables.click(); await expect(search).toHaveValue('no_such_table_0011'); await expect(page.locator('.table-title h2')).toHaveText('Item');
    await areas.getByRole('button', { name: 'Download objects', exact: true }).click();
    await expect(page.getByText('Object 51', { exact: true })).toBeVisible();
    const requests = await calls(session.data);
    expect(requests.filter(request => request.method === 'workspace.open')).toHaveLength(1);
    expect(requests.filter(request => request.method === 'discovery.refresh')).toHaveLength(0);
    expect(requests.filter(request => request.method === 'download.read')).toHaveLength(2);
  } finally { await session.close(); }
});

test('catalogue failures preserve definitions and clearing a failed search removes its stale retry', async () => {
  const session = await opened();
  try {
    const { page } = session; await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await expect(page.locator('.tableitem')).toHaveCount(50); await definition(page, 'Item').click();
    await expect(page.locator('.table-title h2')).toHaveText('Item');
    await writeFile(path.join(session.data, 'hold-table-catalog'), 'hold'); await writeFile(path.join(session.data, 'table-catalog-error'), 'error');
    const search = page.getByRole('searchbox', { name: 'Search definitions' }); await search.fill('Item');
    await expect(page.getByText('Reading local provider metadata…', { exact: true })).toBeVisible();
    await captures(page, session.app, 'catalogue-loading');
    await writeFile(path.join(session.data, 'release-table-catalog'), 'release');
    await expect(page.getByRole('alert')).toContainText('PROVIDER_UNAVAILABLE');
    await expect(page.locator('.table-title h2')).toHaveText('Item'); await expect(page.locator('.tableitem')).toHaveCount(50);
    await captures(page, session.app, 'catalogue-error');
    await search.fill(''); await expect(page.getByRole('button', { name: 'Retry catalogue' })).toHaveCount(0);
    await expect(page.getByRole('alert')).toHaveCount(0); await expect(search).toBeFocused();
    await unlink(path.join(session.data, 'table-catalog-error'));
    await search.fill('no_such_table_0011'); await expect(page.locator('.tableitem')).toHaveCount(0);
    await captures(page, session.app, 'catalogue-empty');
    await page.getByRole('button', { name: 'Clear search', exact: true }).click(); await expect(page.locator('.tableitem')).toHaveCount(50);
    await definition(page, 'Achievement').click();
    await expect(page.locator('.table-title h2')).toHaveText('Achievement'); await expect(page.getByText('DEFINITION_NOT_BUNDLED', { exact: true })).toBeVisible();
    await captures(page, session.app, 'definition-unbundled');
    await page.getByRole('button', { name: 'Data', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Waiting for the active table', exact: true })).toBeVisible();
    await expect(page.locator('.tables-explorer tbody tr')).toHaveCount(0);
    await captures(page, session.app, 'data-unavailable');
  } finally { await session.close(); }
});

test('superseded definition reads cannot replace a newer selection and typed failures preserve it', async () => {
  const session = await opened();
  try {
    const { page } = session; await page.getByRole('navigation', { name: 'Build areas' }).getByRole('button', { name: 'Tables', exact: true }).click();
    await expect(page.locator('.tableitem')).toHaveCount(50); await definition(page, 'Item').click(); await expect(page.locator('.table-title h2')).toHaveText('Item');
    await writeFile(path.join(session.data, 'hold-definition-a4c11069'), 'hold');
    await definition(page, 'Material').click(); await expect(page.getByText('Reading a local definition… Previous selection remains visible.', { exact: true })).toBeVisible();
    await captures(page, session.app, 'definition-loading');
    await definition(page, 'ItemClass').click(); await expect(page.locator('.table-title h2')).toHaveText('ItemClass');
    await writeFile(path.join(session.data, 'release-definition-a4c11069'), 'release');
    // A subsequent exact query gives the older response a deterministic opportunity to arrive.
    await definition(page, 'ItemClass').click(); await expect(page.locator('.table-title h2')).toHaveText('ItemClass');
    await writeFile(path.join(session.data, 'table-definition-error'), 'error');
    await definition(page, 'Item').click(); await expect(page.getByRole('alert')).toContainText('DEFINITION_READ_FAILED');
    await expect(page.locator('.table-title h2')).toHaveText('ItemClass'); await captures(page, session.app, 'definition-error');
    await unlink(path.join(session.data, 'table-definition-error')); await writeFile(path.join(session.data, 'definition-no-candidate'), 'no candidate');
    await definition(page, 'Item').click(); await expect(page.locator('.table-title h2')).toHaveText('Item');
    await expect(page.getByText('SCHEMA_BUILD_NOT_LISTED', { exact: true })).toBeVisible();
    await page.getByRole('button',{name:'Structure',exact:true}).click();await expect(page.locator('.candidate-choice')).toContainText('Universal COLUMNS declarations');
    await captures(page, session.app, 'definition-no-candidate');
  } finally { await session.close(); }
});

test('Tables source layout follows dynamic text scaling and hidden activation without clipping its inspector',async()=>{
 const session=await opened();try{const{page,app}=session,areas=page.getByRole('navigation',{name:'Build areas'});await areas.getByRole('button',{name:'Tables',exact:true}).click();await definition(page,'Item').click();await page.getByRole('button',{name:'Structure',exact:true}).click();await page.getByRole('button',{name:'Inspect field ClassID',exact:true}).click();
 async function bounds(font:number){await page.evaluate(size=>document.documentElement.style.fontSize=`${size}px`,font);await expect.poll(async()=>page.locator('.tables-explorer').getAttribute('data-layout')).toBe(font===30?(await page.evaluate(()=>innerWidth))<1400?'compact':'medium':'wide');const geometry=await page.locator('.table-workbench').evaluate(el=>{const c=el.querySelector('.tables-center')!.getBoundingClientRect(),i=el.querySelector('.table-inspector')!.getBoundingClientRect();return{center:{x:c.x,y:c.y,bottom:c.bottom},inspector:{x:i.x,y:i.y,right:i.right},viewport:innerWidth};});expect(geometry.inspector.right).toBeLessThanOrEqual(geometry.viewport+1);if(font===30)expect(geometry.inspector.y).toBeGreaterThanOrEqual(geometry.center.bottom-1);else expect(geometry.inspector.x).toBeGreaterThan(geometry.center.x);}
 for(const[width,height]of[[1280,800],[1440,1100]]){await app.evaluate(({BrowserWindow},size)=>BrowserWindow.getAllWindows()[0].setContentSize(size.width,size.height),{width,height});await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(width);for(const font of[15,30,15])await bounds(font);await areas.getByRole('button',{name:'Home',exact:true}).click();await page.evaluate(()=>document.documentElement.style.fontSize='30px');await areas.getByRole('button',{name:'Tables',exact:true}).click();await bounds(30);await bounds(15);}
 }finally{await session.close();}
});
