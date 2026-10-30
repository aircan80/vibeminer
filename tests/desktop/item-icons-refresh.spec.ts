import {test,expect,type Page} from '@playwright/test';
import {writeFile,readFile} from 'node:fs/promises';
import path from 'node:path';
import {launch} from './helpers';
// #92: a shown icon outlives its image URL. Main hands out 4 s URLs here (90 s in the app) so the refresh is observable.
const rows='#items-listbox [role="option"]',lifetime=4000;
async function opened(){process.env.VIBEMINER_FIXTURE_ICON_URL_LIFETIME_MS=String(lifetime);try{const s=await launch(true,false,false,true,true);await s.page.locator('.build-row').first().click();await s.page.getByRole('button',{name:'Open build',exact:true}).click();await writeFile(path.join(s.data,'release-open'),'release');await expect(s.page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();await writeFile(path.join(s.data,'cached-items'),'cached');await writeFile(path.join(s.data,'icons-auto'),'auto');await s.page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Items',exact:true}).click();await expect(s.page.locator(rows)).toHaveCount(50);return s;}finally{delete process.env.VIBEMINER_FIXTURE_ICON_URL_LIFETIME_MS;}}
async function batches(data:string){return(await readFile(path.join(data,'requests.ndjson'),'utf8')).trim().split('\n').map(x=>JSON.parse(x)).filter(r=>r.method==='items.icons').length;}
const frame=(page:Page)=>page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
const loaded=(page:Page,id:string)=>expect.poll(()=>page.locator(`#iv-opt-${id} .item-icon-box img`).evaluateAll(i=>i.length===1&&(i[0] as HTMLImageElement).complete&&(i[0] as HTMLImageElement).naturalWidth===64),{timeout:8000}).toBe(true);
const src=(page:Page,id:string)=>page.locator(`#iv-opt-${id} .item-icon-box img`).getAttribute('src');
// Records whether row 0's shown image ever left the page while armed.
async function watch(page:Page){await page.evaluate(()=>{const w=window as unknown as{__lost:boolean;__armed:boolean};w.__lost=false;w.__armed=true;new MutationObserver(()=>{if(w.__armed&&!document.querySelector('#iv-opt-0 .item-icon-box img'))w.__lost=true;}).observe(document.body,{subtree:true,childList:true,attributes:true});});}
const lost=(page:Page)=>page.evaluate(()=>(window as unknown as{__lost:boolean}).__lost);
const arm=(page:Page,on:boolean)=>page.evaluate(on=>{const w=window as unknown as{__lost:boolean;__armed:boolean};w.__armed=on;if(on)w.__lost=false;},on);
test('a shown icon reloads by itself after its URL lifetime and stays shown through re-renders and Clear search',async()=>{test.setTimeout(90_000);const s=await opened();try{const{page}=s,retry=page.getByRole('button',{name:'Retry visible icons',exact:true});
 await page.locator('#iv-opt-0').scrollIntoViewIfNeeded();await loaded(page,'0');await watch(page);
 // No interaction: the visible icon is asked for again and swaps to its new URL without ever going to —.
 const first=await src(page,'0'),before=await batches(s.data);
 await expect.poll(()=>batches(s.data),{timeout:lifetime*2}).toBeGreaterThan(before);await expect.poll(()=>src(page,'0'),{timeout:lifetime}).not.toBe(first);await loaded(page,'0');
 await page.waitForTimeout(lifetime+1500);await loaded(page,'0');expect(await lost(page)).toBe(false);await expect(retry).toHaveCount(0);
 // A refresh per lifetime, not a spin.
 expect(await batches(s.data)-before).toBeLessThanOrEqual(6);
 // 200% text re-renders the list after the first URL has long expired; it keeps refreshing there.
 // The list pane may be hidden for a moment while the layout switches; the watch starts once it shows again.
 await arm(page,false);await page.evaluate(()=>document.documentElement.style.fontSize='30px');await frame(page);const list=page.getByRole('button',{name:'Items list',exact:true});if(await list.isVisible())await list.click();await page.locator('#iv-opt-0').scrollIntoViewIfNeeded();await loaded(page,'0');await arm(page,true);
 await page.waitForTimeout(lifetime+1500);await loaded(page,'0');expect(await lost(page)).toBe(false);await expect(retry).toHaveCount(0);
 await page.evaluate(()=>document.documentElement.style.fontSize='15px');await frame(page);
 // Clear search after a search: the row shown again keeps its icon past another lifetime.
 await arm(page,false);await page.getByRole('button',{name:'Item ID',exact:true}).click();await page.getByRole('searchbox',{name:'Exact Item ID',exact:true}).fill('9');await page.getByRole('button',{name:'Search',exact:true}).click();await expect(page.locator('.iv-applied')).toContainText('Exact Item ID 9');await loaded(page,'9');
 await page.getByRole('button',{name:'Clear search',exact:true}).click();await expect(page.locator(rows)).toHaveCount(50);await page.locator('#iv-opt-0').scrollIntoViewIfNeeded();await loaded(page,'0');await arm(page,true);
 await page.waitForTimeout(lifetime*2);await loaded(page,'0');expect(await lost(page)).toBe(false);await expect(retry).toHaveCount(0);
 expect(await page.locator('.item-icon-box img').count()).toBeLessThanOrEqual(12);
}finally{await s.close();}});
