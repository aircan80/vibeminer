import {test,expect,type Page,type ElectronApplication,type Locator} from '@playwright/test';
import {writeFile,readFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {launch,root,size,text} from './helpers';
// Creatures view (docs/design/creatures-view, #62): the list and the selected creature's record, with Tables links and returns.
async function opened({cached=true,partial=true,fetched=false}:{cached?:boolean;partial?:boolean;fetched?:boolean}={}){
 const s=await launch(true,false,true,true,false,false,true,true,false,true);
 try{
  await size(s.app,1280,800);
  await s.page.locator('.build-row').first().click();await s.page.getByRole('button',{name:'Open build',exact:true}).click();await writeFile(path.join(s.data,'release-open'),'release');
  await expect(s.page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();
  if(cached)await writeFile(path.join(s.data,'cached-creatures'),'cached');if(partial)await writeFile(path.join(s.data,'creatures-partial'),'partial');if(fetched)await writeFile(path.join(s.data,'creatures-fetched'),'fetched');
  await s.page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Creatures',exact:true}).click();return s;
 }catch(error){await s.close();throw error;}
}
const requests=async(data:string)=>(await readFile(path.join(data,'requests.ndjson'),'utf8')).trim().split('\n').map(line=>JSON.parse(line) as {method:string;params:Record<string,unknown>});
async function captures(page:Page,app:ElectronApplication,state:string,anchor:Locator){
 const out=path.join(root,'artifacts/creatures');await mkdir(out,{recursive:true});
 for(const[width,height]of[[1280,800],[1440,1100]]){await size(app,width,height);await expect.poll(()=>page.evaluate(()=>innerWidth)).toBe(width);
  for(const scale of[1,2]){await text(page,scale);const pane=page.getByRole('button',{name:'Selected creature',exact:true});if(!await anchor.isVisible()&&await pane.isVisible())await pane.click();await anchor.scrollIntoViewIfNeeded();await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
   expect(await page.evaluate(()=>document.documentElement.scrollWidth>document.documentElement.clientWidth+1)).toBe(false);
   await page.screenshot({path:path.join(out,`${state}-${width}x${height}-${scale*100}.png`)});}}
 await text(page,1);
}
const list=(page:Page)=>page.getByRole('listbox',{name:'Creatures on this page'});
test('lists creatures, shows the selected record with labels and Type and Family text, and a source link returns to itself',async()=>{test.setTimeout(120000);const s=await opened();try{
 const{page}=s;await expect(page.locator('.cv-option')).toHaveCount(50);
 await expect(page.getByRole('option',{name:/^Authored creature 0/})).toContainText('0 · Type 1');
 await page.getByRole('option',{name:/^Authored creature 0/}).click();
 const detail=page.getByRole('heading',{level:2,name:'Authored creature 0'});await expect(detail).toBeVisible();
 const head=page.locator('.cv .iv-dhead');await expect(head).toContainText('Creature 0');await expect(head).toContainText('Authored title 0');await expect(head).toContainText('Authored type A');await expect(head).toContainText('Normal');
 const creature=page.getByRole('region',{name:'Creature',exact:true});
 await expect(creature).toContainText('1 · Authored type A');await expect(creature).toContainText('2 · Authored family B');await expect(creature).toContainText('0 · Normal');
 await expect(page.locator('.dh-statusbar')).toContainText('Creatures · 60 rows · 58 named · 120 declared displays · Creature Partial');
 await captures(page,s.app,'ready',detail);
 // The section is one Tab stop: Tab from the record button reaches its first link, arrows move inside it.
 await page.getByRole('button',{name:'Creature #0',exact:true}).focus();await page.keyboard.press('Tab');
 const first=creature.getByRole('button',{name:/^Creature\.Name_lang/});await expect(first).toBeFocused();
 await page.keyboard.press('ArrowDown');await page.keyboard.press('ArrowDown');await page.keyboard.press('ArrowDown');await page.keyboard.press('ArrowDown');await page.keyboard.press('ArrowDown');
 const typeLink=creature.getByRole('button',{name:/^Creature\.CreatureType/});await expect(typeLink).toBeFocused();
 await page.keyboard.press('Enter');
 const back=page.getByRole('button',{name:'← Return to Creatures selection',exact:true});await expect(back).toBeVisible();await back.click();
 await expect(typeLink).toBeFocused();await expect(page.getByRole('heading',{level:2,name:'Authored creature 0'})).toBeVisible();
 expect((await requests(s.data)).filter(r=>r.method==='creatures.prepare')).toHaveLength(0);
 expect((await requests(s.data)).some(r=>r.method==='tables.referenceText'&&r.params.tableId==='c9d6b6b3'&&r.params.recordId==='0')).toBe(true);
}finally{await s.close();}});
test('the arrow keys move the selection, the detail follows, and the one-pane layout shows the selected creature at 200%',async()=>{test.setTimeout(90000);const s=await opened();try{
 const{page}=s;await expect(page.locator('.cv-option')).toHaveCount(50);await list(page).focus();
 await page.keyboard.press('ArrowDown');await expect(page.getByRole('heading',{level:2,name:'Authored creature 0'})).toBeVisible();
 await page.keyboard.press('ArrowDown');await expect(page.getByRole('heading',{level:2,name:'Empty source name'})).toBeVisible();await expect(list(page)).toBeFocused();
 await size(s.app,1280,800);await text(page,2);
 const switcher=page.getByRole('group',{name:'Show pane'});await expect(switcher).toBeVisible();await expect(page.locator('.cv .iv-switch-name')).toHaveText('Creature 1');
 await list(page).focus();await page.keyboard.press('ArrowDown');await page.keyboard.press('Enter');
 await expect(page.getByRole('heading',{level:2,name:'Authored creature 2'})).toBeFocused();
 const displays=page.getByRole('region',{name:'Displays'}),creature=page.getByRole('region',{name:'Creature',exact:true});
 // One column, Displays first.
 expect((await displays.boundingBox())!.y).toBeLessThan((await creature.boundingBox())!.y);
 await page.keyboard.press('Control+f');await expect(page.getByRole('searchbox',{name:'Name contains'})).toBeFocused();
 await text(page,1);
}finally{await s.close();}});
test('search refuses a non-canonical ID, says when nothing matches and clears back to browsing',async()=>{test.setTimeout(90000);const s=await opened();try{
 const{page}=s;await expect(page.locator('.cv-option')).toHaveCount(50);
 await page.getByRole('button',{name:'Creature ID',exact:true}).click();const id=page.getByRole('searchbox',{name:'Exact Creature ID'});
 await id.fill('01');await page.keyboard.press('Enter');await expect(id).toHaveAttribute('aria-invalid','true');await expect(page.getByRole('alert')).toContainText('Enter a whole Creature ID');await expect(page.locator('.cv-option')).toHaveCount(50);
 await id.fill('6');await page.keyboard.press('Enter');await expect(page.locator('.cv-option')).toHaveCount(1);await expect(page.locator('.cv .iv-applied')).toContainText('Exact Creature ID 6 · 1 on this page');
 await page.getByRole('button',{name:'Name',exact:true}).click();const name=page.getByRole('searchbox',{name:'Name contains'});await name.fill('zz');await page.keyboard.press('Enter');
 await expect(page.getByText(/No admitted creature name contains “zz”/)).toBeVisible();await captures(page,s.app,'no-match',page.getByText(/No admitted creature name contains/));
 await page.locator('.iv-empty').getByRole('button',{name:'Clear search',exact:true}).click();await expect(page.locator('.cv-option')).toHaveCount(50);
}finally{await s.close();}});
test('the Creature table prepares at interactive priority while the build downloads; Stop and Retry are deliberate',async()=>{test.setTimeout(120000);const s=await opened({cached:false,partial:false});try{
 const{page}=s;await page.getByRole('switch').click();await expect(page.getByRole('switch')).toHaveAccessibleName('Download mode Download all');
 await expect(page.getByText('Preparing Creatures…',{exact:true})).toBeVisible();await expect(page.getByRole('searchbox',{name:'Name contains'})).toBeDisabled();
 await expect(page.locator('.cv .iv-prereq')).toContainText('Not ready yet · needed by Creatures, at interactive priority');
 await captures(page,s.app,'preparing',page.getByText('Preparing Creatures…',{exact:true}));
 await writeFile(path.join(s.data,'hold-creatures-cancel'),'hold');
 await page.getByRole('button',{name:'Stop preparing Creatures',exact:true}).click();await writeFile(path.join(s.data,'finish-creatures-cancel'),'finish');
 await expect(page.getByText('Creatures loading stopped',{exact:true})).toBeVisible();await expect(page.getByRole('switch')).toHaveAttribute('aria-checked','true');
 const retry=page.getByRole('button',{name:'Retry Creatures preparation',exact:true});await retry.click();
 await expect(page.locator('.cv .dh-state-box[role=status]')).toBeFocused();
 await writeFile(path.join(s.data,'release-creatures'),'release');await expect(page.locator('.cv-option')).toHaveCount(50);
 expect((await requests(s.data)).filter(r=>r.method==='creatures.prepare')).toHaveLength(2);
}finally{await s.close();}});
test('typed acquisition failures say so, never list rows and offer Retry only when it can help',async()=>{test.setTimeout(90000);for(const[state,code]of[['unsupported','CREATURES_PROFILE_UNSUPPORTED'],['protected','BLTE_ENCRYPTED'],['corrupt','CONTENT_CHECKSUM_MISMATCH']]){const s=await opened({cached:false});try{
 await writeFile(path.join(s.data,'creatures-load-error.json'),JSON.stringify({code,message:`Authored ${state} source evidence; no client rows were admitted.`,retryable:false}));await writeFile(path.join(s.data,'release-creatures'),'release');
 await expect(s.page.getByText('Creatures need attention',{exact:true})).toBeVisible();await expect(s.page.locator('.cv')).toContainText(code);await expect(s.page.locator('.cv-option')).toHaveCount(0);
 await expect(s.page.getByRole('button',{name:'Retry Creatures preparation',exact:true})).toHaveCount(0);await expect(s.page.getByRole('button',{name:'Open Creature in Tables',exact:true})).toBeVisible();
 await captures(s.page,s.app,state,s.page.getByText('Creatures need attention',{exact:true}));
}finally{await s.close();}}});
test('a Creature table read with a fetched definition says which definition read it',async()=>{test.setTimeout(90000);const s=await opened({fetched:true});try{
 const{page}=s;const notice=page.getByRole('region',{name:/^Creature · Definition from WoWDBDefs \(fetched .*, a50ca6f\)$/});await expect(notice).toBeVisible();
 await expect(notice).toContainText('The bundled Creature definition doesn’t list build');await expect(page.locator('.cv-option')).toHaveCount(50);
 await captures(page,s.app,'fetched-definition',page.locator('.cv .feature-definition'));
}finally{await s.close();}});
