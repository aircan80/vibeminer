import {test,expect,type Page,type ElectronApplication,type Locator} from '@playwright/test';
import {writeFile,readFile,mkdir,unlink} from 'node:fs/promises';
import path from 'node:path';
import {launch,root,size,text} from './helpers';
// #59: Creatures → slot metadata → Geometry → verified VMG1 body drawn by the real main protocol and WebGL scene.
const methods=async(data:string)=>(await readFile(path.join(data,'requests.ndjson'),'utf8')).trim().split('\n').map(line=>JSON.parse(line) as {method:string;params:Record<string,unknown>});
async function opened(state='ready',textured:boolean|'notPrepared'=false){
 const s=await launch(true,false,false,true,false,false,true,true,false,false,false,false,true);
 try{
  await size(s.app,1280,800);
  const{page}=s;await page.locator('.build-row').first().click();await page.getByRole('button',{name:'Open build',exact:true}).click();await writeFile(path.join(s.data,'release-open'),'release');
  await expect(page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();
  await writeFile(path.join(s.data,'cached-creatures'),'cached');await writeFile(path.join(s.data,'cached-display'),'cached');await writeFile(path.join(s.data,'geometry-state.json'),JSON.stringify({state,textured}));
  await displays(page);return s;
 }catch(error){await s.close();throw error;}
}
// #62: the geometry view opens from the model preview in the Creatures view's display card.
async function displays(page:Page){await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();await expect(page.locator('.cv-option')).toHaveCount(50);
 await page.getByRole('option',{name:/^Authored creature 0/}).click();await expect(card(page)).toContainText('FileDataID 123456');}
const card=(page:Page)=>page.getByRole('article',{name:/^Slot 0 · Display 100/});
const show=(page:Page)=>card(page).getByRole('button',{name:'Show model preview for display 100',exact:true});
const openView=(page:Page)=>card(page).getByRole('button',{name:'Open geometry view',exact:true});
async function capture(page:Page,app:ElectronApplication,name:string,anchor:Locator,detail=false){const out=path.join(root,'artifacts/creature-geometry');await mkdir(out,{recursive:true});for(const[width,height]of[[1280,800],[1440,1100]]){await app.evaluate(({BrowserWindow},size)=>BrowserWindow.getAllWindows()[0].setContentSize(size.width,size.height),{width,height});await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(width);for(const scale of[1,2]){await page.evaluate(f=>document.documentElement.style.fontSize=`${f}px`,15*scale);await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));if(detail){const pane=page.getByRole('button',{name:'Selected creature',exact:true});if(await pane.isVisible())await pane.click();}await anchor.scrollIntoViewIfNeeded();await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));expect(await page.evaluate(()=>document.documentElement.scrollWidth>document.documentElement.clientWidth+1)).toBe(false);await page.screenshot({path:path.join(out,`${name}-${width}x${height}-${scale*100}.png`)});}}await page.evaluate(()=>document.documentElement.style.fontSize='15px');}
test('Show model prepares and draws the preview, Open geometry view shows the full view, and Return draws the kept model again',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');
 // Nothing is kept yet: the preview looked locally and asks before preparing anything.
 await expect(show(page)).toBeVisible();expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(0);
 await show(page).click();const stop=card(page).getByRole('button',{name:'Stop loading model',exact:true});await expect(stop).toBeFocused();
 await expect(card(page).getByRole('status')).toContainText('Loading model');
 await writeFile(path.join(s.data,'release-geometry-prepare'),'release');
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});await expect(openView(page)).toBeFocused();
 await capture(page,s.app,'preview',card(page),true);
 await openView(page).click();
 await expect(page.getByRole('heading',{name:'Diagnostic source geometry',exact:true})).toBeFocused();await expect(page.locator('.creatures-browser')).toBeHidden();
 await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible({timeout:20000});
 await expect(page.locator('.creature-geometry canvas')).toHaveCount(1);await expect(page.getByText('Unposed source edges · appearance not evaluated',{exact:true})).toBeVisible();
 await expect(page.getByText('Original vertices',{exact:true}).locator('xpath=following-sibling::dd[1]')).toHaveText('6');await expect(page.getByText('Emitted edge pairs',{exact:true}).locator('xpath=following-sibling::dd[1]')).toHaveText('24');
 const canvas=page.locator('.creature-geometry canvas'),before=await canvas.screenshot();
 await page.getByRole('button',{name:'Rotate left',exact:true}).click();await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible();
 await expect.poll(async()=>Buffer.compare(before,await canvas.screenshot())).not.toBe(0);
 await capture(page,s.app,'ready',canvas);
 await page.getByRole('button',{name:'← Return to Creatures selection',exact:true}).click();
 await expect(page.locator('.creature-geometry')).toHaveCount(0);await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});await expect(openView(page)).toBeFocused();
 // One Prepare for the whole round trip; every view's lease is closed once main has the body.
 let calls=await methods(s.data);expect(calls.filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(1);
 await expect.poll(async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.close').length).toBe(3);
 // Leaving the area closes the preview; coming back, the kept model draws by itself without preparing.
 await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Home',exact:true}).click();
 await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 calls=await methods(s.data);expect(calls.filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(1);
}finally{await s.close();}});
test('A kept model draws by itself for the first resolved display',async()=>{test.setTimeout(90000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'cached-geometry'),'cached');
 await page.getByRole('option',{name:/^Authored creature 6/}).click();await page.getByRole('option',{name:/^Authored creature 0/}).click();
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(0);
}finally{await s.close();}});
test('Show model on a kept model that wasn\'t drawn by itself moves focus to Open geometry view (usability review on #62)',async()=>{test.setTimeout(90000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'cached-geometry'),'cached');
 await page.getByRole('option',{name:/^Authored creature 6/}).click();await page.getByRole('option',{name:/^Authored creature 0/}).click();
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 const second=page.getByRole('article',{name:/^Slot 1 · Display 100/});const show1=second.getByRole('button',{name:'Show model preview for display 100',exact:true});
 await show1.focus();await page.keyboard.press('Enter');
 await expect(second.getByRole('button',{name:'Open geometry view',exact:true})).toBeFocused({timeout:20000});
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(0);
}finally{await s.close();}});
test('An encrypted model says so with its code, keeps the facts and draws nothing',async()=>{test.setTimeout(90000);const s=await opened('encrypted');try{
 const{page}=s;await show(page).click();
 await expect(card(page)).toContainText('Encrypted model',{timeout:20000});await expect(card(page)).toContainText('BLTE_KEY_UNAVAILABLE');await expect(card(page)).toContainText('FileDataID 123456');
 await expect(card(page).locator('canvas')).toHaveCount(0);await capture(page,s.app,'encrypted',card(page),true);
 expect((await methods(s.data)).some(r=>r.method==='creatures.geometry.open')).toBe(false);
}finally{await s.close();}});
test('Opening the geometry view from a scrolled-down detail starts at its top with the heading focused and Return in view',async()=>{test.setTimeout(90000);const s=await opened();try{
 const{page}=s;await size(s.app,1280,800);await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(1280);
 const second=page.getByRole('article',{name:/^Slot 1 · Display 100/});await second.getByRole('button',{name:'Show model preview for display 100',exact:true}).click();
 const open=second.getByRole('button',{name:'Open geometry view',exact:true});await expect(open).toBeVisible({timeout:20000});
 const body=page.locator('.cv .iv-dbody');await body.evaluate(e=>{e.scrollTop=e.scrollHeight;});await open.scrollIntoViewIfNeeded();expect(await body.evaluate(e=>e.scrollTop)).toBeGreaterThan(100);
 await writeFile(path.join(s.data,'hold-geometry-open'),'hold');
 await open.click();const heading=page.getByRole('heading',{name:'Diagnostic source geometry',exact:true});await expect(heading).toBeFocused();
 await expect.poll(async()=>(await readFile(path.join(s.data,'waiting-geometry-open'),'utf8').catch(()=>''))).not.toBe('');
 const back=page.getByRole('button',{name:'← Return to Creatures selection',exact:true});
 const visible=async(target:Locator)=>target.evaluate(el=>{const r=el.getBoundingClientRect();return r.top>=-1&&r.bottom<=innerHeight+1;});
 for(const target of[heading,back,page.locator('.geometry-status [role=status]')])expect(await visible(target)).toBe(true);
 await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toHaveCount(0);
 await writeFile(path.join(s.data,'release-geometry-open'),'release');
 await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible({timeout:20000});
 // Finishing never moves the scroll or the focus the user now owns.
 const pane=page.locator('.cv-geometry');const before=await pane.evaluate(e=>e.scrollTop);await expect(heading).toBeFocused();expect(await pane.evaluate(e=>e.scrollTop)).toBe(before);expect(await page.evaluate(()=>scrollY)).toBe(0);
 await capture(page,s.app,'entered-scrolled',heading);
}finally{await s.close();}});
test('An unsupported source is a clear textual outcome with no canvas or body request',async()=>{test.setTimeout(90000);const s=await opened('unsupported');try{
 const{page}=s;await show(page).click();
 await expect(card(page)).toContainText('Unsupported model',{timeout:20000});await expect(card(page)).toContainText('AUTHORED_UNSUPPORTED');
 await expect(card(page).locator('canvas')).toHaveCount(0);await capture(page,s.app,'unsupported',card(page),true);
 expect((await methods(s.data)).some(r=>r.method==='creatures.geometry.open')).toBe(false);
}finally{await s.close();}});
test('A model with no triangles names its state instead of a bare "Model not drawn" (#98)',async()=>{test.setTimeout(90000);const s=await opened('noDeclaredTriangles');try{
 const{page}=s;await show(page).click();
 await expect(card(page).getByRole('img',{name:'Nothing to draw',exact:true})).toBeVisible({timeout:20000});
 await expect(card(page)).toContainText('noDeclaredTriangles');await expect(card(page)).toContainText('The main SKIN declares no triangles, so this model has no surface to draw. The file is not corrupt.');
 await expect(card(page)).not.toContainText('Model not drawn');await expect(card(page).locator('canvas')).toHaveCount(0);
 await expect(card(page).getByRole('button',{name:'Retry model'})).toHaveCount(0);await capture(page,s.app,'no-triangles',card(page),true);
 expect((await methods(s.data)).some(r=>r.method==='creatures.geometry.open')).toBe(false);
}finally{await s.close();}});
test('A model over a size limit says so with its code (#98)',async()=>{test.setTimeout(90000);const s=await opened('resourceLimit');try{
 const{page}=s;await show(page).click();
 await expect(card(page).getByRole('img',{name:'Model too large',exact:true})).toBeVisible({timeout:20000});
 await expect(card(page)).toContainText('AUTHORED_RESOURCE_LIMIT');await expect(card(page)).not.toContainText('Model not drawn');
 await capture(page,s.app,'too-large',card(page),true);
}finally{await s.close();}});
test('Switching builds closes the open view lease, and no geometry block is left above the panes',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s,closes=async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.close').length;
 const switchTo=async(row:number)=>{await page.getByRole('button',{name:/Choose build/}).click();await page.locator('.build-row').nth(row).click();await page.getByRole('button',{name:'Open build',exact:true}).click();await expect(page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();};
 await expect(show(page)).toBeVisible();await writeFile(path.join(s.data,'hold-geometry-open'),'hold');await writeFile(path.join(s.data,'cached-geometry'),'cached');
 await show(page).click();
 await expect.poll(async()=>(await readFile(path.join(s.data,'waiting-geometry-open'),'utf8').catch(()=>''))).not.toBe('');
 await switchTo(1);expect(await closes()).toBe(0);
 await writeFile(path.join(s.data,'release-geometry-open'),'release');
 await expect.poll(closes).toBe(1);expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.open')).toHaveLength(1);
 await unlink(path.join(s.data,'hold-geometry-open'));await displays(page);
 // QA on #62: the panes sit right under the build header; nothing names another creature or build above them.
 await expect(page.locator('.geometry-activity')).toHaveCount(0);await expect(page.getByText(/Dismiss geometry activity/)).toHaveCount(0);
 const top=async()=>page.evaluate(()=>document.querySelector('.cv .iv-panes')!.getBoundingClientRect().top-document.querySelector('.dh-toolbar')!.getBoundingClientRect().bottom);
 expect(await top()).toBeLessThan(4);
}finally{await s.close();}});
test('Show model on another creature waits for the model already loading, which it can stop, and nothing is left above the panes',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');
 await show(page).click();await expect(card(page).getByRole('button',{name:'Stop loading model',exact:true})).toBeVisible();
 await page.locator('#cv-opt-2').click();await expect(page.getByRole('heading',{level:2,name:'Authored creature 2'})).toBeVisible();await expect(card(page)).toContainText('FileDataID 123456');
 await expect(page.locator('.geometry-activity')).toHaveCount(0);
 await unlink(path.join(s.data,'hold-geometry-prepare'));
 await show(page).click();await expect(card(page)).toContainText('Waiting for another model');await expect(card(page)).toContainText('Creature 0 · slot 0 is loading first');
 await capture(page,s.app,'waiting-other',card(page),true);
 await card(page).getByRole('button',{name:'Stop that model',exact:true}).click();
 await expect.poll(async()=>(await methods(s.data)).some(r=>r.method==='request.cancel')).toBe(true);
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 // Back on creature 0, its stopped preparation isn't called finished.
 await page.locator('#cv-opt-0').click();await expect(page.getByRole('heading',{level:2,name:'Authored creature 0'})).toBeVisible();
 await expect(page.getByText(/preparation finished/)).toHaveCount(0);
}finally{await s.close();}});
test('A kept model draws by itself again after Home and back and after Return from Tables (QA on #62)',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'cached-geometry'),'cached');
 await page.locator('#cv-opt-6').click();await page.locator('#cv-opt-0').click();await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 const nav=page.getByRole('navigation',{name:'Build areas'});
 await nav.getByRole('button',{name:'Home',exact:true}).click();await nav.getByRole('button',{name:'Creatures',exact:true}).click();
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 await card(page).getByRole('button',{name:/^CreatureModelScale/}).click();await page.getByRole('button',{name:'← Return to Creatures selection',exact:true}).click();
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(0);
}finally{await s.close();}});
test('A busy engine is never "Model not drawn": the automatic look leaves Show model, and Show model waits and asks again (QA on #62)',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'cached-geometry'),'cached');
 await page.locator('#cv-opt-6').click();await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 await writeFile(path.join(s.data,'geometry-read-busy'),'1');await page.locator('#cv-opt-0').click();
 await expect(show(page)).toBeVisible({timeout:20000});await expect(card(page)).not.toContainText('Model not drawn');await expect(card(page)).not.toContainText('CREATURES_QUERY_BUSY');
 await writeFile(path.join(s.data,'geometry-read-busy'),'2');await show(page).click();
 await expect(card(page)).toContainText('Waiting for the engine');await expect(card(page)).not.toContainText('Model not drawn');
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});await expect(openView(page)).toBeFocused();
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(0);
}finally{await s.close();}});
test('A model stopped after leaving and coming back keeps keyboard focus in its card (usability review on #62)',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');
 await show(page).click();await expect(card(page).getByRole('button',{name:'Stop loading model',exact:true})).toBeVisible();
 await page.locator('#cv-opt-2').click();await expect(page.getByRole('heading',{level:2,name:'Authored creature 2'})).toBeVisible();
 await page.locator('#cv-opt-0').click();const stop=card(page).getByRole('button',{name:'Stop loading model',exact:true});await expect(stop).toBeVisible();
 await stop.focus();await page.keyboard.press('Enter');
 await expect.poll(()=>page.evaluate(()=>{const a=document.activeElement;return !!a&&a!==document.body&&!!a.closest('article.cv-display');})).toBe(true);
 await expect(card(page).getByRole('button',{name:/^(Show model preview for display 100|Retry model)$/})).toBeFocused({timeout:20000});
 // Show model after that stop starts a new load at once; it never replays the Stop (usability review on #62).
 const before=(await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare').length;
 await page.locator('#cv-opt-2').click();await page.locator('#cv-opt-0').click();await show(page).click();
 await expect(card(page).getByRole('button',{name:'Stop loading model',exact:true})).toBeVisible();await expect(card(page)).not.toContainText('Loading stopped');
 await expect.poll(async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare').length).toBe(before+1);
}finally{await s.close();}});
// #99: after Stop the automatic look reads before it answers. Focus handed to Show model waits on the card's status during that
// read and goes on to what it shows, unless the user moved it meanwhile. An outcome with no button ends the handoff on its status.
type FocusStep=(page:Page,data:string,app:ElectronApplication)=>Promise<void>;
const home=(page:Page)=>page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Home',exact:true});
const cachedAs=async(data:string,state:string)=>{await writeFile(path.join(data,'cached-geometry'),'cached');await writeFile(path.join(data,'geometry-state.json'),JSON.stringify({state,textured:false}));};
const focusCases:[string,FocusStep,FocusStep][]=[
 ['returns to Show model when nothing is kept',async()=>{},async page=>{await expect(show(page)).toBeFocused({timeout:20000});}],
 ['goes on to Open geometry view when the kept model draws',async(_page,data)=>cachedAs(data,'ready'),async page=>{await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});await expect(openView(page)).toBeFocused({timeout:20000});}],
 ['stays where the user moved it during the read',async page=>{await home(page).focus();},async page=>{await expect(show(page)).toBeVisible({timeout:20000});await expect(home(page)).toBeFocused();}],
 ['ends on the status of an outcome with no button, and nothing takes focus later',async(_page,data)=>cachedAs(data,'unsupported'),async(page,_data,app)=>{
  await expect(card(page).getByRole('img',{name:'Unsupported model',exact:true})).toBeFocused({timeout:20000});
  await expect(card(page).locator('.cv-preview button')).toHaveCount(0);
  // The card renders again (a narrower window, then back) with focus on the page: the ended handoff leaves it there.
  await page.evaluate(()=>(document.activeElement as HTMLElement).blur());
  await size(app,900,700);await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(900);await size(app,1280,800);await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(1280);
  await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));expect(await page.evaluate(()=>document.activeElement===document.body)).toBe(true);}],
];
for(const[name,during,after]of focusCases)test(`After Stop, focus during the automatic read ${name} (#99)`,async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');
 await show(page).click();await expect(card(page).getByRole('button',{name:'Stop loading model',exact:true})).toBeVisible();
 await page.locator('#cv-opt-2').click();await expect(page.getByRole('heading',{level:2,name:'Authored creature 2'})).toBeVisible();
 await page.locator('#cv-opt-0').click();const stop=card(page).getByRole('button',{name:'Stop loading model',exact:true});await expect(stop).toBeVisible();
 await writeFile(path.join(s.data,'hold-geometry-read'),'hold');await stop.focus();await page.keyboard.press('Enter');
 await expect.poll(()=>readFile(path.join(s.data,'waiting-geometry-read'),'utf8').then(()=>true,()=>false),{timeout:20000}).toBe(true);
 await expect.poll(()=>page.evaluate(()=>!!document.activeElement?.matches('article.cv-display .cv-pending[role=status]'))).toBe(true);
 await during(page,s.data,s.app);await unlink(path.join(s.data,'hold-geometry-read'));await writeFile(path.join(s.data,'release-geometry-read'),'release');
 await after(page,s.data,s.app);
}finally{await s.close();}});
test('After its tries a busy engine reads "Engine still busy" with Retry model, which draws once the engine answers',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'cached-geometry'),'cached');await page.locator('#cv-opt-6').click();await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 await page.locator('#cv-opt-0').click();await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 await page.locator('#cv-opt-6').click();await writeFile(path.join(s.data,'geometry-read-busy'),'100');await page.locator('#cv-opt-0').click();
 await show(page).click();await expect(card(page)).toContainText('Waiting for the engine');
 await expect(card(page)).toContainText('Engine still busy',{timeout:60000});await expect(card(page)).toContainText('CREATURES_QUERY_BUSY');await expect(card(page)).not.toContainText('Model not drawn');
 await writeFile(path.join(s.data,'geometry-read-busy'),'0');await card(page).getByRole('button',{name:'Retry model',exact:true}).click();
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
}finally{await s.close();}});
// #99: a remembered Stop of the texture load never hides the kept lines: Return reads what is kept and draws it.
test('Return after stopping the texture load in the geometry view draws the kept lines again',async()=>{test.setTimeout(120000);const s=await opened('ready','notPrepared');try{
 const{page}=s;await writeFile(path.join(s.data,'cached-geometry'),'cached');
 await page.locator('#cv-opt-6').click();await page.locator('#cv-opt-0').click();await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(0);
 await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');await openView(page).click();
 const stop=page.getByRole('button',{name:'Stop preparing geometry',exact:true});await expect(stop).toBeVisible({timeout:20000});await stop.click();
 await expect.poll(async()=>(await methods(s.data)).some(r=>r.method==='request.cancel')).toBe(true);
 await expect(page.getByRole('status').filter({hasText:'Geometry preparation stopped'})).toBeVisible({timeout:20000});
 const reads=(await methods(s.data)).filter(r=>r.method==='creatures.geometry.read').length;
 await page.getByRole('button',{name:'← Return to Creatures selection',exact:true}).click();
 await expect(card(page).locator('.cv-pbox canvas')).toHaveCount(1,{timeout:20000});await expect(card(page)).not.toContainText('No preview yet');await expect(openView(page)).toBeFocused({timeout:20000});
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.read').length).toBeGreaterThan(reads);
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(1);
}finally{await s.close();}});
test('A ready display appearance draws the textured surfaces from its verified VMA1 body and shows the texture chain (#63)',async()=>{test.setTimeout(120000);const s=await opened('ready',true);try{
 const{page}=s;await show(page).click();await expect(openView(page)).toBeVisible({timeout:20000});await openView(page).click();
 await expect(page.getByRole('status').filter({hasText:'Unposed textured surfaces drawn'})).toBeVisible({timeout:20000});
 await expect(page.getByText('Unposed textured surfaces · bind pose, unlit',{exact:true})).toBeVisible();
 const chain=page.getByRole('region',{name:'Texture chain'});await expect(chain).toContainText('Textured');
 await expect(chain).toContainText('Texture 0 · type 11 → TextureVariationFileDataID[0] → FileDataID 779 · BC1, mip 0 4×4 of 4×4');
 await expect(chain).toContainText('Texture 1 · type 0 → TXID[1] → FileDataID 780 · BC1, mip 0 4×4 of 4×4');
 await expect(chain).toContainText('Not drawn: 2 particle emitters, 0 ribbons, 0 lights, 1 shadow batch, 1 blended layer.');
 const canvas=page.locator('.creature-geometry canvas'),before=await canvas.screenshot();
 await page.getByRole('button',{name:'Rotate left',exact:true}).click();await expect.poll(async()=>Buffer.compare(before,await canvas.screenshot())).not.toBe(0);
 await capture(page,s.app,'textured',canvas);
 // 600×480 at 200%: field names and their indexes never break inside, and nothing overflows the inspector.
 await size(s.app,600,480);await text(page,2);await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(600);
 const names=page.locator('.geometry-inspector .geometry-name');await names.last().scrollIntoViewIfNeeded();
 const fragments=await names.evaluateAll(els=>els.map(e=>({text:e.textContent,lines:e.getClientRects().length})));
 expect(fragments.map(f=>f.text)).toEqual(['TextureVariationFileDataID','[0–3]','TextureVariationFileDataID[0]','TXID[1]']);
 for(const fragment of fragments)expect(fragment.lines,fragment.text!).toBe(1);
 // #69: in chain rows a label never wraps apart from its number.
 const pairs=await chain.locator('li').evaluateAll(items=>items.flatMap(li=>{const out:{text:string;lines:number}[]=[];const walker=document.createTreeWalker(li,NodeFilter.SHOW_TEXT);
  for(let node=walker.nextNode();node;node=walker.nextNode())for(const m of node.textContent!.matchAll(/(Texture|type|FileDataID|mip)\u00a0\d+/g)){const range=document.createRange();range.setStart(node,m.index!);range.setEnd(node,m.index!+m[0].length);
   out.push({text:m[0],lines:new Set([...range.getClientRects()].map(r=>Math.round(r.top))).size});}return out;}));
 expect(pairs.length).toBe(8);for(const pair of pairs)expect(pair.lines,pair.text).toBe(1);
 // Each arrow is glued to the text before it, so it never starts a line.
 for(const row of await chain.locator('li').allTextContents())expect(row.match(/\u00a0→ /g)?.length,row).toBe(2);
 expect(await page.locator('.geometry-inspector').evaluate(e=>e.scrollWidth-e.clientWidth)).toBeLessThanOrEqual(0);
 await text(page,1);
 const opens=(await methods(s.data)).filter(r=>r.method==='creatures.geometry.open');expect(opens.length).toBeGreaterThanOrEqual(2);expect(opens.at(-1)!.params.geometryArtifactId).not.toBe(opens[0].params.artifactId);
 await page.getByRole('button',{name:'← Return to Creatures selection',exact:true}).click();
 await expect.poll(async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.close').length).toBeGreaterThanOrEqual(2);
}finally{await s.close();}});
