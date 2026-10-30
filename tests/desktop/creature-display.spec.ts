import {test,expect,type Page,type ElectronApplication,type Locator} from '@playwright/test';
import {writeFile,readFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {launch,root,size,text} from './helpers';
import {activityTask} from './activity-helpers';
// Displays in the Creatures view (#62): each declared slot's CreatureDisplayInfo and CreatureModelData rows and model file,
// prepared through the shared pipeline at interactive priority, with their own states and source links.
async function opened({cachedDisplay=false,state}:{cachedDisplay?:boolean;state?:string}={}){
 const s=await launch(true,false,true,true,false,false,true,true,false,true);
 try{
  await size(s.app,1280,800);
  await s.page.locator('.build-row').first().click();await s.page.getByRole('button',{name:'Open build',exact:true}).click();await writeFile(path.join(s.data,'release-open'),'release');
  await expect(s.page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();
  await writeFile(path.join(s.data,'cached-creatures'),'cached');if(cachedDisplay)await writeFile(path.join(s.data,'cached-display'),'cached');
  if(state)await writeFile(path.join(s.data,'creature-display-state.json'),JSON.stringify({state}));
  return s;
 }catch(error){await s.close();throw error;}
}
async function creature(page:Page,id='0'){await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();await page.getByRole('option',{name:new RegExp(`^Authored creature ${id}`)}).click();return page.getByRole('region',{name:'Displays'});}
const card=(page:Page,slot:number)=>page.getByRole('article',{name:new RegExp(`^Slot ${slot} · Display`)});
const requests=async(data:string)=>(await readFile(path.join(data,'requests.ndjson'),'utf8')).trim().split('\n').map(line=>JSON.parse(line) as {method:string;params:Record<string,unknown>});
async function captures(page:Page,app:ElectronApplication,state:string,anchor:Locator){
 const out=path.join(root,'artifacts/creature-displays');await mkdir(out,{recursive:true});
 for(const[width,height]of[[1280,800],[1440,1100]]){await size(app,width,height);await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(width);
  for(const scale of[1,2]){await text(page,scale);const pane=page.getByRole('button',{name:'Selected creature',exact:true});if(!await anchor.isVisible()&&await pane.isVisible())await pane.click();await anchor.scrollIntoViewIfNeeded();await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
   expect(await page.evaluate(()=>document.documentElement.scrollWidth>document.documentElement.clientWidth+1)).toBe(false);
   await page.screenshot({path:path.join(out,`${state}-${width}x${height}-${scale*100}.png`)});}}
 await text(page,1);
}
test('displays load while the build downloads, ahead of Download all, and every declared slot fills in from one preparation',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await page.getByRole('switch').click();await expect(page.getByRole('switch')).toHaveAccessibleName('Download mode Download all');
 const displays=await creature(page);
 await expect(displays.getByText('Loading displays…',{exact:true})).toBeVisible();
 await expect(card(page,0)).toContainText('CreatureDisplayInfo loading');await expect(card(page,1)).toContainText('Same display as slot 0');
 await expect(card(page,1)).toContainText('Probability -0');
 await expect(page.getByText('Slot 2',{exact:true}).locator('..')).toContainText('No display declared');await expect(page.getByText('Slot 3',{exact:true}).locator('..')).toContainText('Invalid reference');
 await captures(page,s.app,'loading',displays.getByText('Loading displays…',{exact:true}));
 // The request is research work in Downloads & activity, next to the build download.
 await page.locator('.download-activity-trigger:visible, .dv-chip:visible').last().click();
 const panel=page.locator('#download-activity-panel');await expect(panel.getByRole('heading',{name:'Creature 0 slot 0 metadata',exact:true})).toBeVisible();
 await page.keyboard.press('Escape');
 await writeFile(path.join(s.data,'release-display'),'release');
 await expect(card(page,0).getByText('Model ID',{exact:true}).locator('xpath=ancestor::div[contains(@class,"iv-fact")]')).toContainText('19');
 await expect(card(page,0)).toContainText('1.25 · CreatureModelScale, as read');await expect(card(page,0)).toContainText('FileDataID 123456');
 await expect(card(page,1)).toContainText('FileDataID 123456');
 const calls=await requests(s.data);expect(calls.filter(r=>r.method==='creatures.display.prepare')).toHaveLength(1);
 expect(calls.filter(r=>r.method==='creatures.display.facts'&&r.params.slot===1).length).toBeGreaterThan(0);
 expect(calls.some(r=>r.method.startsWith('creatures.display.')&&(r.params.slot===2||r.params.slot===3))).toBe(false);
 await expect(page.getByRole('switch')).toHaveAttribute('aria-checked','true');
 await captures(page,s.app,'ready',card(page,0));
 // No geometry here, so no preview: the facts take the whole card and no word breaks inside (UI review on #62).
 for(const scale of[1,2]){await text(page,scale);const pane=page.getByRole('button',{name:'Selected creature',exact:true});if(await pane.isVisible())await pane.click();
  const fit=await card(page,0).evaluate(el=>{const body=el.querySelector('.cv-disp-body')!,chain=el.querySelector('.cv-chain')!;const words:number[]=[];for(const e of el.querySelectorAll('.iv-fl,.iv-src')){const node=e.firstChild;if(!node||node.nodeType!==3)continue;const t=node.textContent!;for(const m of t.matchAll(/\S+/g)){const r=document.createRange();r.setStart(node,m.index!);r.setEnd(node,m.index!+m[0].length);words.push(new Set([...r.getClientRects()].map(x=>Math.round(x.top))).size);}}return{columns:getComputedStyle(body).gridTemplateColumns.split(' ').length,preview:el.querySelectorAll('.cv-preview').length,chain:chain.getBoundingClientRect().width,body:body.getBoundingClientRect().width,broken:words.filter(n=>n>1).length};});
  expect(fit.preview).toBe(0);expect(fit.columns).toBe(1);expect(fit.chain).toBeGreaterThan(fit.body*0.9);expect(fit.broken).toBe(0);}
 await text(page,1);
}finally{await s.close();}});
test('Stop loading displays stops only that request; Retry loads them deliberately',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;const displays=await creature(page);
 await writeFile(path.join(s.data,'hold-display-cancel'),'hold');
 await displays.getByRole('button',{name:'Stop loading displays',exact:true}).click();await writeFile(path.join(s.data,'finish-display-cancel'),'finish');
 await expect(displays.getByText('Display loading stopped',{exact:true})).toBeVisible();
 await displays.getByRole('button',{name:'Retry display tables',exact:true}).click();
 await expect(displays.getByText('Loading displays…',{exact:true})).toBeVisible();await writeFile(path.join(s.data,'release-display'),'release');
 await expect(card(page,0)).toContainText('FileDataID 123456');
 expect((await requests(s.data)).filter(r=>r.method==='creatures.display.prepare')).toHaveLength(2);
}finally{await s.close();}});
test('the model file opens Files at its FileDataID and returns to the same link; a fact opens its exact Tables cell',async()=>{test.setTimeout(120000);const s=await opened({cachedDisplay:true});try{
 const{page}=s;await creature(page);
 const files=card(page,0).getByRole('button',{name:'Find FileDataID 123456 in Files',exact:true});await files.click();
 await expect(page.getByText(/FileDataID 123456 can.t be looked up yet/)).toBeVisible();
 await page.getByRole('button',{name:'← Return to Creatures selection',exact:true}).click();await expect(files).toBeFocused();
 const scale=card(page,0).getByRole('button',{name:/^CreatureModelScale/});await scale.click();
 await expect(page.getByRole('heading',{name:'Record #100'})).toBeVisible();const back=page.getByRole('button',{name:'← Return to Creatures selection',exact:true});await expect(back).toBeVisible();
 await back.click();await expect(scale).toBeFocused();
}finally{await s.close();}});
for(const[state,words] of [['displayAmbiguous','2 CreatureDisplayInfo rows have ID 100; none is picked'],['displayMissingInAdmittedScope','isn’t in the readable CreatureDisplayInfo rows'],['modelAmbiguous','2 CreatureModelData rows have ID 19; none is picked']] as const)
 test(`${state} keeps its own state and offers no model preview`,async()=>{test.setTimeout(90000);const s=await opened({cachedDisplay:true,state});try{
  const{page}=s;await creature(page);await expect(card(page,0)).toContainText(words.replace('isn’t','isn\'t'));
  await expect(card(page,0).getByRole('button',{name:/Show model/})).toHaveCount(0);
  if(state==='displayAmbiguous')await captures(page,s.app,state,card(page,0));
 }finally{await s.close();}});

test('View in Downloads & activity keeps the Displays heading focused and in view while a slow creature lookup arrives at enlarged text',async()=>{test.setTimeout(120000);for(const[width,height]of[[1280,800],[1440,1100]]){const s=await opened();try{
 const{page}=s;await size(s.app,width,height);await creature(page,'0');await expect(page.getByRole('region',{name:'Displays'}).getByRole('button',{name:'Stop loading displays',exact:true})).toBeVisible();
 await page.locator('#cv-opt-2').click();await expect(page.getByRole('heading',{level:2,name:'Authored creature 2'})).toBeVisible();
 await writeFile(path.join(s.data,'hold-creature-inspect-0'),'hold');await text(page,2);
 await(await activityTask(page,'Creature 0 slot 0 metadata')).getByRole('button',{name:'View research',exact:true}).click();
 const heading=page.getByRole('heading',{level:3,name:'Displays',exact:true});await expect(heading).toBeFocused();
 const inView=async()=>{const box=(await heading.boundingBox())!;expect(box.y).toBeGreaterThanOrEqual(-1);expect(box.y).toBeLessThan(await page.evaluate(()=>innerHeight));};
 await inView();
 await writeFile(path.join(s.data,'release-creature-inspect-0'),'release');
 await expect(page.getByRole('region',{name:'Creature',exact:true})).toContainText('Authored title 0');
 await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
 await expect(heading).toBeFocused();await inView();
 await page.screenshot({path:path.join(root,`artifacts/creature-displays/view-enlarged-${width}x${height}-200.png`)});
}finally{await s.close();}}});
