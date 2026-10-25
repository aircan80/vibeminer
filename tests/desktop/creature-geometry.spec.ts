import {test,expect,type Page,type ElectronApplication,type Locator} from '@playwright/test';
import {writeFile,readFile,mkdir,unlink} from 'node:fs/promises';
import path from 'node:path';
import {launch,root,size,text} from './helpers';
// #59: Creatures → slot metadata → Geometry → verified VMG1 body drawn by the real main protocol and WebGL scene.
const methods=async(data:string)=>(await readFile(path.join(data,'requests.ndjson'),'utf8')).trim().split('\n').map(line=>JSON.parse(line) as {method:string;params:Record<string,unknown>});
async function opened(state='ready',textured=false){
 const s=await launch(true,false,false,true,false,false,true,true,false,false,false,false,true);
 try{
  const{page}=s;await page.locator('.build-row').first().click();await page.getByRole('button',{name:'Open build',exact:true}).click();await writeFile(path.join(s.data,'release-open'),'release');
  await expect(page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();
  await writeFile(path.join(s.data,'cached-creatures'),'cached');await writeFile(path.join(s.data,'cached-display'),'cached');await writeFile(path.join(s.data,'geometry-state.json'),JSON.stringify({state,textured}));
  await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();await expect(page.locator('.creatures-browser tbody tr')).toHaveCount(50);
  await page.getByRole('button',{name:/^Inspect Creature 0:/}).click();await page.getByRole('button',{name:'Inspect metadata for display 100 from slot 0',exact:true}).click();
  await expect(page.getByText('Display metadata resolved',{exact:true})).toBeVisible();return s;
 }catch(error){await s.close();throw error;}
}
async function capture(page:Page,app:ElectronApplication,name:string,anchor:Locator){const out=path.join(root,'artifacts/creature-geometry');await mkdir(out,{recursive:true});for(const[width,height]of[[1280,800],[1440,1100]]){await app.evaluate(({BrowserWindow},size)=>BrowserWindow.getAllWindows()[0].setContentSize(size.width,size.height),{width,height});await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(width);for(const scale of[1,2]){await page.evaluate(f=>document.documentElement.style.fontSize=`${f}px`,15*scale);await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));await anchor.scrollIntoViewIfNeeded();await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));expect(await page.evaluate(()=>document.documentElement.scrollWidth>document.documentElement.clientWidth+1)).toBe(false);await page.screenshot({path:path.join(out,`${name}-${width}x${height}-${scale*100}.png`)});}}await page.evaluate(()=>document.documentElement.style.fontSize='15px');}
test('Geometry step prepares, receives and draws the verified mesh, then Return restores the invoker and releases the lease',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');
 const enter=page.getByRole('button',{name:'View geometry for display 100',exact:true});await expect(page.getByText(/Diagnostic geometry: the model's source edges, or its textured surfaces/)).toBeVisible();
 await enter.focus();await page.keyboard.press('Enter');
 await expect(page.getByRole('heading',{name:'Diagnostic source geometry',exact:true})).toBeFocused();await expect(page.locator('.creatures-browser')).toBeHidden();
 await expect(page.getByRole('status').filter({hasText:'Acquiring model source'})).toBeVisible();await expect(page.getByRole('button',{name:'Stop preparing geometry',exact:true})).toBeVisible();
 await writeFile(path.join(s.data,'release-geometry-prepare'),'release');
 await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible({timeout:20000});
 await expect(page.locator('.creature-geometry canvas')).toHaveCount(1);await expect(page.getByText('Unposed source edges · appearance not evaluated',{exact:true})).toBeVisible();
 await expect(page.getByText('Original vertices',{exact:true}).locator('xpath=following-sibling::dd[1]')).toHaveText('6');await expect(page.getByText('Emitted edge pairs',{exact:true}).locator('xpath=following-sibling::dd[1]')).toHaveText('24');
 // Something was actually drawn: rotating the camera changes the canvas pixels.
 const canvas=page.locator('.creature-geometry canvas'),before=await canvas.screenshot();
 await page.getByRole('button',{name:'Rotate left',exact:true}).click();await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible();
 await expect.poll(async()=>Buffer.compare(before,await canvas.screenshot())).not.toBe(0);
 await capture(page,s.app,'ready',canvas);
 await page.getByRole('button',{name:'Return to selected display metadata',exact:true}).click();
 await expect(enter).toBeFocused();await expect(page.locator('.creature-geometry')).toHaveCount(0);
 // Main closes the engine lease once it has copied the body, before the GPU upload.
 await expect.poll(async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.close').length).toBe(1);
 let calls=await methods(s.data);expect(calls.filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(1);expect(calls.filter(r=>r.method==='creatures.geometry.open')).toHaveLength(1);
 // A second entry reuses the retained artifact: read is ready, no second Prepare.
 await enter.click();await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible({timeout:20000});
 calls=await methods(s.data);expect(calls.filter(r=>r.method==='creatures.geometry.prepare')).toHaveLength(1);expect(calls.filter(r=>r.method==='creatures.geometry.open')).toHaveLength(2);
 await expect.poll(async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.close').length).toBe(2);
 // Leaving the area closes the view; main releases it, so a new entry is admitted and draws again.
 await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Home',exact:true}).click();
 await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();await expect(page.locator('.creature-geometry')).toHaveCount(0);
 await enter.click();await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible({timeout:20000});
}finally{await s.close();}});
test('An unsupported source is a clear textual outcome with no canvas or body request',async()=>{test.setTimeout(90000);const s=await opened('unsupported');try{
 const{page}=s;await page.getByRole('button',{name:'View geometry for display 100',exact:true}).click();
 await expect(page.getByRole('status').filter({hasText:'This source geometry is unsupported'})).toBeVisible();await expect(page.getByText('AUTHORED_UNSUPPORTED: Authored geometry outcome.')).toBeVisible();
 await expect(page.locator('.creature-geometry canvas')).toHaveCount(0);await capture(page,s.app,'unsupported',page.locator('.geometry-status'));
 const calls=await methods(s.data);expect(calls.some(r=>r.method==='creatures.geometry.open')).toBe(false);
 await page.getByRole('button',{name:'Return to selected display metadata',exact:true}).click();await expect(page.getByRole('button',{name:'View geometry for display 100',exact:true})).toBeFocused();
}finally{await s.close();}});
test('Switching builds closes the open view lease, and another build\'s Prepare offers Stop but no View',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s,closes=async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.close').length;
 const metadata=async()=>{await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();await expect(page.locator('.creatures-browser tbody tr')).toHaveCount(50);await page.getByRole('button',{name:/^Inspect Creature 0:/}).click();await page.getByRole('button',{name:'Inspect metadata for display 100 from slot 0',exact:true}).click();await expect(page.getByText('Display metadata resolved',{exact:true})).toBeVisible();};
 const switchTo=async(row:number)=>{await page.getByRole('button',{name:/Choose build/}).click();await page.locator('.build-row').nth(row).click();await page.getByRole('button',{name:'Open build',exact:true}).click();await expect(page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();};
 // Hold the engine open so the lease is still opening when the user switches builds.
 await writeFile(path.join(s.data,'hold-geometry-open'),'hold');
 await page.getByRole('button',{name:'View geometry for display 100',exact:true}).click();
 await expect.poll(async()=>(await readFile(path.join(s.data,'waiting-geometry-open'),'utf8').catch(()=>''))).not.toBe('');
 await switchTo(1);expect(await closes()).toBe(0);
 await writeFile(path.join(s.data,'release-geometry-open'),'release');
 // The late lease belongs to a view that was left: main closes it.
 await expect.poll(closes).toBe(1);expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.open')).toHaveLength(1);
 // On the second build, start a held Prepare and leave it running by switching back.
 await metadata();await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');
 await page.getByRole('button',{name:'View geometry for display 100',exact:true}).click();await expect(page.getByRole('button',{name:'Stop preparing geometry',exact:true})).toBeVisible();
 await switchTo(0);await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();
 const strip=page.getByRole('region',{name:'Geometry activity'});await expect(strip).toContainText('Geometry · Creature 0 · slot 0');
 await expect(strip.getByRole('button',{name:/^View Creature .* geometry$/})).toHaveCount(0);await capture(page,s.app,'other-build-activity',strip);
 await strip.getByRole('button',{name:'Stop preparing geometry',exact:true}).click();
 await expect.poll(async()=>(await methods(s.data)).some(r=>r.method==='request.cancel')).toBe(true);await expect(strip.getByRole('status')).toHaveText('Geometry preparation stopped');
 expect((await methods(s.data)).filter(r=>r.method==='creatures.geometry.open')).toHaveLength(1);
 // Main released both earlier views: this build admits a new entry and draws.
 await unlink(path.join(s.data,'hold-geometry-prepare'));await strip.getByRole('button',{name:'Dismiss geometry activity',exact:true}).click();
 await metadata();await page.getByRole('button',{name:'View geometry for display 100',exact:true}).click();await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible({timeout:20000});
}finally{await s.close();}});
test('Entering from a scrolled-down browser opens the view at its top with the focused heading, Return and Stop visible',async()=>{test.setTimeout(90000);const s=await opened();try{
 const{page}=s;await s.app.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows()[0].setContentSize(1280,800));await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(1280);
 await writeFile(path.join(s.data,'hold-geometry-prepare'),'hold');
 const pane=page.locator('.dh-area-scroll:not([hidden])'),enter=page.getByRole('button',{name:'View geometry for display 100',exact:true});
 await pane.evaluate(e=>{e.scrollTop=e.scrollHeight;});await enter.scrollIntoViewIfNeeded();expect(await pane.evaluate(e=>e.scrollTop)).toBeGreaterThan(200);
 await enter.click();const heading=page.getByRole('heading',{name:'Diagnostic source geometry',exact:true});await expect(heading).toBeFocused();
 const stop=page.getByRole('button',{name:'Stop preparing geometry',exact:true});await expect(stop).toBeVisible();
 const inPane=async(target:Locator)=>target.evaluate((el,box)=>{const r=el.getBoundingClientRect(),p=(document.querySelector(box) as HTMLElement).getBoundingClientRect();return r.top>=p.top-1&&r.bottom<=p.bottom+1;},'.dh-area-scroll:not([hidden])');
 for(const target of[heading,page.getByRole('button',{name:'Return to selected display metadata',exact:true}),page.locator('.geometry-status [role=status]'),stop])expect(await inPane(target)).toBe(true);
 await capture(page,s.app,'entered-scrolled',heading);
 await writeFile(path.join(s.data,'release-geometry-prepare'),'release');await expect(page.getByRole('status').filter({hasText:'Unposed source edges drawn'})).toBeVisible({timeout:20000});
 // Async completion never moves the scroll or focus the user now owns.
 const before=await pane.evaluate(e=>e.scrollTop);await expect(heading).toBeFocused();expect(await pane.evaluate(e=>e.scrollTop)).toBe(before);
}finally{await s.close();}});
test('A ready display appearance draws the textured surfaces from its verified VMA1 body and shows the texture chain (#63)',async()=>{test.setTimeout(120000);const s=await opened('ready',true);try{
 const{page}=s;await page.getByRole('button',{name:'View geometry for display 100',exact:true}).click();
 await expect(page.getByRole('status').filter({hasText:'Unposed textured surfaces drawn'})).toBeVisible({timeout:20000});
 await expect(page.getByText('Unposed textured surfaces · bind pose, unlit',{exact:true})).toBeVisible();
 const chain=page.getByRole('region',{name:'Texture chain'});await expect(chain).toContainText('Textured');
 await expect(chain).toContainText('Texture 0 · type 11 → TextureVariationFileDataID[0] → FileDataID 779 · BC1, mip 0 4×4 of 4×4');
 await expect(chain).toContainText('Not drawn: 2 particle emitters, 0 ribbons, 0 lights, 1 shadow batch.');
 const canvas=page.locator('.creature-geometry canvas'),before=await canvas.screenshot();
 await page.getByRole('button',{name:'Rotate left',exact:true}).click();await expect.poll(async()=>Buffer.compare(before,await canvas.screenshot())).not.toBe(0);
 await capture(page,s.app,'textured',canvas);
 // 600×480 at 200%: field names and their indexes never break inside, and nothing overflows the inspector.
 await size(s.app,600,480);await text(page,2);await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(600);
 const names=page.locator('.geometry-inspector .geometry-name');await names.last().scrollIntoViewIfNeeded();
 const fragments=await names.evaluateAll(els=>els.map(e=>({text:e.textContent,lines:e.getClientRects().length})));
 expect(fragments.map(f=>f.text)).toEqual(['TextureVariationFileDataID','[0–3]','TextureVariationFileDataID[0]']);
 for(const fragment of fragments)expect(fragment.lines,fragment.text!).toBe(1);
 expect(await page.locator('.geometry-inspector').evaluate(e=>e.scrollWidth-e.clientWidth)).toBeLessThanOrEqual(0);
 await text(page,1);
 const opens=(await methods(s.data)).filter(r=>r.method==='creatures.geometry.open');expect(opens).toHaveLength(1);expect(opens[0].params.geometryArtifactId).not.toBe(opens[0].params.artifactId);
 await page.getByRole('button',{name:'Return to selected display metadata',exact:true}).click();
 await expect.poll(async()=>(await methods(s.data)).filter(r=>r.method==='creatures.geometry.close').length).toBe(1);
}finally{await s.close();}});
