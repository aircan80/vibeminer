import {test,expect,type Page,type ElectronApplication} from '@playwright/test';
import {mkdir,writeFile} from 'node:fs/promises';
import path from 'node:path';
import {launch,root} from './helpers';
// Items view (docs/design/items-view, D098) in real Electron: two panes or one pane, facts with source links, roving sections,
// the Tables round trip, and no horizontal page scroll at 1280×800 and 600×480, 100% and 200% text.
async function opened(cached=true,flags:string[]=[]){
 const s=await launch(true,false,false,true,true,'subclass',false,false,true);
 await s.page.locator('.build-row').first().click();await s.page.getByRole('button',{name:'Open build',exact:true}).click();
 await writeFile(path.join(s.data,'release-open'),'release');await expect(s.page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();
 for(const flag of[...(cached?['cached-items']:[]),...flags,'icons-auto','labels-auto','cached-effects-links','cached-effects-inputs','cached-effects-names'])await writeFile(path.join(s.data,flag),'cached');
 await s.page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Items',exact:true}).click();
 if(cached)await expect(s.page.locator('#iv-opt-0')).toBeVisible();return s;
}
async function size(app:ElectronApplication,page:Page,width:number,height:number,font:number){
 await app.evaluate(({BrowserWindow},s)=>BrowserWindow.getAllWindows()[0].setContentSize(s.width,s.height),{width,height});
 await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(width);
 await page.evaluate(f=>document.documentElement.style.fontSize=`${f}px`,font);
 await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
}
// The window and the shell never scroll sideways, and the list head's Search button stays inside its pane.
const overflow=(page:Page)=>page.evaluate(()=>{const d=document.documentElement,app=document.querySelector('.dh-app')!,pane=document.querySelector('.iv-list'),button=document.querySelector('.iv-search button');const p=pane?.getBoundingClientRect(),b=button?.getBoundingClientRect();return d.scrollWidth>d.clientWidth+1||app.scrollWidth>app.clientWidth+1||!!(p&&b&&p.width>0&&b.right>p.right+1);});
const out=path.join(root,'artifacts/items-view');
async function shot(page:Page,name:string){await mkdir(out,{recursive:true});await page.screenshot({path:path.join(out,`${name}.png`)});}
const sizes=[[1280,800],[600,480]] as const;

test('Items view lays out as two panes or one, without page overflow, at 1280×800 and 600×480, 100% and 200% text',async()=>{
 test.setTimeout(120_000);const s=await opened();
 try{const{page,app}=s;
  await page.locator('#iv-opt-3').click();await expect(page.getByRole('heading',{name:'Authored sword 3',level:2})).toBeVisible();
  await expect(page.getByRole('region',{name:'Item',exact:true})).toContainText('Rare');
  for(const[width,height]of sizes)for(const font of[15,30]){
   await size(app,page,width,height,font);const tag=`${width}x${height}-${font/15*100}`;
   expect(await overflow(page),tag).toBe(false);
   const compact=await page.locator('.iv-switch').isVisible();
   if(width===1280&&font===15){expect(compact,tag).toBe(false);const list=await page.locator('.iv-list').boundingBox(),detail=await page.locator('.iv-detail').boundingBox();expect(detail!.x,tag).toBeGreaterThan(list!.x+list!.width-2);}
   else expect(compact,tag).toBe(true);
   if(compact){await page.getByRole('button',{name:'Items list',exact:true}).click();await expect(page.locator('#items-listbox')).toBeFocused();expect(await overflow(page),`list ${tag}`).toBe(false);await shot(page,`list-${tag}`);
    await page.keyboard.press('Enter');await expect(page.getByRole('heading',{name:'Authored sword 3',level:2})).toBeFocused();
    expect(await page.locator('.iv-grid>*').first().evaluate(el=>el.querySelector('section')?.getAttribute('aria-label')),tag).toBe('Effects');}
   expect(await overflow(page),tag).toBe(false);await shot(page,`detail-${tag}`);
  }
 }finally{await s.close();}
});

// D101 (#84): Items read with definitions fetched from WoWDBDefs says so above the list, in Tables' words, and the notice
// fits without page overflow in both layouts at 100% and 200% text.
test('Items read with fetched definitions say which tables they read, without page overflow at 1280×800 and 600×480, 100% and 200% text',async()=>{
 test.setTimeout(120_000);const s=await opened(true,['items-fetched']);
 try{const{page,app}=s;await size(app,page,1280,800,15);
  const notice=page.getByRole('region',{name:/^Item and ItemSparse · Definition from WoWDBDefs \(fetched .*, a50ca6f\)$/});
  await expect(notice).toBeVisible();await expect(notice).toContainText('the newest WoWDBDefs definitions do. Checked field by field.');
  await expect(notice).toContainText('Turn off Fetch newer table definitions in Settings');
  for(const[width,height]of sizes)for(const font of[15,30]){
   await size(app,page,width,height,font);const tag=`${width}x${height}-${font/15*100}`;
   expect(await overflow(page),tag).toBe(false);
   // At 200% the notice is Tables' one-line compact form, so the list keeps its rows.
   const box=page.locator('.feature-definition');await expect(box,tag).toBeVisible();
   if(font===30){await expect(box.locator('strong'),tag).toHaveText(/^WoWDBDefs · .* · a50ca6f$/);expect((await box.boundingBox())!.height,tag).toBeLessThan(width===1280?130:160);}
   await shot(page,`fetched-${tag}`);
  }
 }finally{await s.close();}
});

test('Items detail sections are one Tab stop each, arrows move between a section\'s links, and a link opens Tables and returns to it',async()=>{
 test.setTimeout(90_000);const s=await opened();
 try{const{page,app}=s;await size(app,page,1280,800,15);
  await page.locator('#items-listbox').focus();await page.keyboard.press('ArrowDown');await expect(page.locator('#iv-opt-0')).toHaveAttribute('aria-selected','true');
  await expect(page.locator('#items-listbox')).toBeFocused();await expect(page.getByRole('heading',{name:'Authored sword 0',level:2})).toBeVisible();
  await expect(page.getByRole('region',{name:'Item',exact:true}).locator('.iv-src').first()).toBeVisible();
  // From the list, Tab reaches the record buttons within the page buttons and the head.
  let stops=0;for(;stops<12;stops++){await page.keyboard.press('Tab');if((await page.evaluate(()=>document.activeElement?.textContent??'')).startsWith('ItemSparse #0'))break;}
  expect(stops).toBeLessThan(8);
  const item=page.getByRole('region',{name:'Item',exact:true}),first=item.locator('.iv-src[tabindex="0"]');await expect(first).toHaveCount(1);
  const before=await first.textContent();await first.focus();await page.keyboard.press('ArrowDown');const moved=await page.evaluate(()=>document.activeElement?.textContent);
  expect(moved).not.toBe(before);await expect(item.locator('.iv-src[tabindex="0"]')).toHaveCount(1);
  await page.keyboard.press('Home');const link=item.getByRole('button',{name:/^ItemSparse\.OverallQualityID/});await expect(link).toBeFocused();
  await page.keyboard.press('Enter');await expect(page.getByRole('heading',{name:'Record #0'})).toBeVisible();
  await page.getByRole('button',{name:/Return to Items selection/}).click();await expect(link).toBeFocused();
  await expect(page.locator('#iv-opt-0')).toHaveAttribute('aria-selected','true');
  await expect(page.getByText("Tab moves between sections; arrow keys move between a section's links.")).toBeVisible();
 }finally{await s.close();}
});

test('Items shows Missing for an Item row without ItemSparse, and a bad exact ID stays in the field with its message',async()=>{
 test.setTimeout(60_000);const s=await opened();
 try{const{page}=s;
  await page.getByRole('button',{name:'Item ID',exact:true}).click();const box=page.getByRole('searchbox',{name:'Exact Item ID'});
  await box.fill('01');await page.getByRole('button',{name:'Search',exact:true}).click();await expect(box).toHaveAttribute('aria-invalid','true');
  await expect(page.getByRole('alert')).toContainText('whole Item ID');
  await box.fill('58');await page.getByRole('button',{name:'Search',exact:true}).click();await expect(page.locator('.iv-applied')).toContainText('Exact Item ID 58');
  await page.locator('#iv-opt-58').click();await expect(page.getByRole('heading',{name:'Name unavailable',level:2})).toBeVisible();
  await expect(page.getByRole('region',{name:'Requirements',exact:true})).toContainText('Missing');
  await expect(page.getByRole('button',{name:/^ItemSparse #58/})).toHaveCount(0);await shot(page,'missing-1280x800-100');
 }finally{await s.close();}
});

test('Items preparing fills the area down to the status bar in both layouts, and the list keeps that height when ready',async()=>{
 test.setTimeout(60_000);const s=await opened(false);
 try{const{page,app}=s;
  const gap=()=>page.evaluate(()=>{const panes=document.querySelector('.iv-panes')!.getBoundingClientRect(),bar=document.querySelector('.dh-statusbar')!.getBoundingClientRect();return Math.round(bar.top-panes.bottom);});
  for(const font of[15,30]){await size(app,page,1280,800,font);await expect(page.getByText('Preparing Items…')).toBeVisible();await expect(page.getByRole('button',{name:'Stop preparing Items',exact:true})).toBeVisible();expect(Math.abs(await gap()),`preparing ${font}`).toBeLessThanOrEqual(1);}
  await shot(page,'preparing-1280x800-200');await size(app,page,1280,800,15);await shot(page,'preparing-1280x800-100');
  await writeFile(path.join(s.data,'release-items'),'release');await expect(page.locator('#iv-opt-0')).toBeVisible();
  expect(Math.abs(await gap()),'ready').toBeLessThanOrEqual(1);
 }finally{await s.close();}
});

test('Items preparing offers Open Item in Tables, and at 600×480 and 200% the area clears the floating activity entry',async()=>{
 test.setTimeout(60_000);const s=await opened(false);
 try{const{page,app}=s;await expect(page.getByText('Preparing Items…')).toBeVisible();
  // The floating entry appears once the header has scrolled away; the area's end padding then keeps the last control clear of it.
  await size(app,page,600,480,30);
  await page.evaluate(()=>{const app=document.querySelector('.dh-app')!;app.scrollTop=app.scrollHeight;});
  await expect(page.locator('.download-activity-floating')).toBeVisible();
  expect(await page.evaluate(()=>parseFloat(getComputedStyle(document.querySelector('.dh-area')!).paddingBottom))).toBeGreaterThan(0);
  const clear=await page.evaluate(()=>{const app=document.querySelector('.dh-app')!;app.scrollTop=app.scrollHeight;const last=[...document.querySelectorAll<HTMLElement>('.iv button:not([disabled])')].filter(b=>b.getClientRects().length).at(-1)!.getBoundingClientRect(),entry=document.querySelector('.download-activity-floating')!.getBoundingClientRect();return last.bottom<=entry.top+1||last.right<=entry.left+1;});
  expect(clear).toBe(true);
  await size(app,page,1280,800,15);
  const open=page.getByRole('button',{name:'Open Item in Tables',exact:true});await expect(open).toBeVisible();await open.click();
  await expect(page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Tables',exact:true})).toHaveAttribute('aria-current','page');
  await expect(page.locator('.dh-records').getByRole('heading',{name:'Item',exact:true})).toBeVisible();
 }finally{await s.close();}
});

test('Items: Ctrl+F focuses the search, effects cost a few Tab stops, and icons show their state',async()=>{
 test.setTimeout(90_000);const s=await opened();
 try{const{page,app}=s;await size(app,page,1280,800,15);
  await page.locator('#iv-opt-1').click();await expect(page.getByRole('heading',{name:'Authored sword 1',level:2})).toBeVisible();
  await page.keyboard.press('Control+f');await expect(page.getByRole('searchbox',{name:'Name contains'})).toBeFocused();
  // Effects: one Tab stop for its links, one per card's Raw evidence, one for the sources disclosure; no disabled page buttons.
  const effects=page.locator('.iv-effects');await expect(effects.locator('.iv-effect').first()).toBeVisible();
  await expect(effects.locator('.effect-pages')).toHaveCount(0);
  const cards=await effects.locator('.iv-effect').count();
  const stops=await effects.evaluate(el=>[...el.querySelectorAll<HTMLElement>('button,summary,[tabindex]')].filter(e=>e.tabIndex>=0&&!e.closest('details:not([open]) > :not(summary)')&&!(e as HTMLButtonElement).disabled&&e.getClientRects().length>0).length);
  expect(stops).toBeLessThanOrEqual(cards+2);
  await expect(effects.getByText('Effects sources and status')).toBeVisible();
  await expect(effects.locator('.effect-activity.iv-idle')).toHaveCount(1);
  // Icons: a ready image sits on the dark tile; IconFileDataID 0 is a dashed box.
  const tile=await page.locator('#iv-opt-1 .iv-icon-cell').evaluate(el=>({bg:getComputedStyle(el).backgroundColor,img:!!el.querySelector('img')}));
  if(tile.img)expect(tile.bg).toBe('rgb(36, 50, 58)');
  await expect(page.locator('#iv-opt-3 .iv-icon-cell')).toHaveClass(/iv-icon-none/);
  expect(await page.locator('#iv-opt-3 .iv-icon-cell').evaluate(el=>getComputedStyle(el).borderTopStyle)).toBe('dashed');
  expect(await page.locator('#iv-opt-3 .iv-icon-cell').evaluate(el=>el.textContent?.trim()&&getComputedStyle(el.querySelector('.item-icon-box>span')!).visibility)).not.toBe('visible');
 }finally{await s.close();}
});

test('Items: after Stop, Enter on Retry Items preparation moves focus to the Preparing status, not the page',async()=>{
 test.setTimeout(60_000);const s=await opened(false);
 try{const{page}=s;await expect(page.getByText('Preparing Items…')).toBeVisible();
  await page.getByRole('button',{name:'Stop preparing Items',exact:true}).click();
  const list=page.locator('.iv-list');await expect(list.getByText('Items loading stopped')).toBeVisible();await expect(list.getByText('Stopped.',{exact:true})).toBeVisible();
  const retry=list.getByRole('button',{name:'Retry Items preparation',exact:true});await retry.focus();await page.keyboard.press('Enter');
  await expect.poll(()=>page.evaluate(()=>{const el=document.activeElement;return el===document.body?'body':`${el?.getAttribute('role')}:${el?.textContent?.slice(0,40)}`;})).toMatch(/^status:(Checking retained Items…|Preparing Items…)/);
  // When the list becomes ready the status goes away: focus moves to the search, not the page.
  await writeFile(path.join(s.data,'release-items'),'release');await expect(page.locator('#iv-opt-0')).toBeVisible();
  await expect(page.getByRole('searchbox',{name:'Name contains'})).toBeFocused();
 }finally{await s.close();}
});
