import {test,expect,type Page,type ElectronApplication} from '@playwright/test';
import {mkdir,writeFile} from 'node:fs/promises';
import path from 'node:path';
import {launch,root,size,text} from './helpers';
// #113: at 600x480 and 200% text the floating Downloads & activity entry keeps white on dark teal in every state (the shared
// button:hover rule once turned it pale), and once the shell has scrolled to its end it covers none of the area's content.
const out=path.join(root,'artifacts/activity-floating');
const frame=(page:Page)=>page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
async function opened(geometry='ready'){
 const s=await launch(true,false,true,true,false,false,true,true,false,false,false,false,true);
 try{const{page,app}=s;await size(app,1280,800);
  await page.locator('.build-row').first().click();await page.getByRole('button',{name:'Open build',exact:true}).click();await writeFile(path.join(s.data,'release-open'),'release');
  await expect(page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();
  await page.getByRole('switch').click();
  // retention.set removes retention-state.json, so write it only after the set reply shows (#123).
  await expect(page.locator('.retention-state')).toHaveText('Download all queued');
  await writeFile(path.join(s.data,'retention-state.json'),JSON.stringify({state:'running',progress:{stage:'Retaining encoded content',unit:'bytes',completed:'7',total:null}}));
  await expect(page.locator('.dv-chip:visible')).toHaveCount(1);
  await writeFile(path.join(s.data,'cached-creatures'),'cached');await writeFile(path.join(s.data,'cached-display'),'cached');await writeFile(path.join(s.data,'geometry-state.json'),JSON.stringify({state:geometry,textured:false}));
  return s;
 }catch(error){await s.close();throw error;}
}
async function small(app:ElectronApplication,page:Page){
 await size(app,600,480);await expect.poll(()=>page.evaluate(()=>[innerWidth,innerHeight])).toEqual([600,480]);
 await text(page,2);await page.waitForFunction(()=>!!document.querySelector('.desktop-home.dh-shell-scroll'));
}
// Scrolls the shell to its end, where the floating entry shows once the header has gone.
async function toEnd(page:Page){
 await page.evaluate(()=>{const app=document.querySelector('.dh-app')!;app.scrollTop=app.scrollHeight;});
 await expect(page.locator('.download-activity-floating')).toBeVisible();await frame(page);
 await page.evaluate(()=>{const app=document.querySelector('.dh-app')!;app.scrollTop=app.scrollHeight;});await frame(page);
}
type Look={background:string;color:string;border:string;contrast:number;hover:boolean;focusVisible:boolean;active:boolean};
const look=(page:Page)=>page.locator('.download-activity-floating').evaluate((el):Look=>{
 const rgb=(v:string)=>{const m=v.match(/[\d.]+/g)!.map(Number);return{c:m.slice(0,3),a:m[3]??1};};
 const lum=(c:number[])=>{const[r,g,b]=c.map(v=>{v/=255;return v<=.03928?v/12.92:((v+.055)/1.055)**2.4;});return .2126*r+.7152*g+.0722*b;};
 const cs=getComputedStyle(el),bg=rgb(cs.backgroundColor),fg=rgb(cs.color);
 const[hi,lo]=[lum(bg.c),lum(fg.c)].sort((a,b)=>b-a);
 return{background:cs.backgroundColor,color:cs.color,border:cs.borderTopColor,contrast:bg.a<1?0:(hi+.05)/(lo+.05),hover:el.matches(':hover'),focusVisible:el.matches(':focus-visible'),active:el.matches(':active')};
});
async function shot(page:Page,name:string){
 await mkdir(out,{recursive:true});const box=(await page.locator('.download-activity-floating').boundingBox())!;
 await page.screenshot({path:path.join(out,`${name}-600x480-200.png`)});
 await page.screenshot({path:path.join(out,`${name}-entry-600x480-200.png`),clip:{x:Math.max(0,box.x-8),y:Math.max(0,box.y-8),width:Math.min(600-Math.max(0,box.x-8),box.width+16),height:Math.min(480-Math.max(0,box.y-8),box.height+16)}});
}

test('the floating activity entry keeps white on dark teal at rest, hover, focus and press at 600x480 and 200% text',async()=>{
 test.setTimeout(90_000);const s=await opened();
 try{const{page,app}=s;await small(app,page);await toEnd(page);
  const entry=page.locator('.download-activity-floating');
  const expectReadable=(l:Look,state:string)=>{
   expect(l.color,`${state} text`).toBe('rgb(255, 255, 255)');
   expect(['rgb(23, 110, 102)','rgb(18, 91, 84)'],`${state} background ${l.background}`).toContain(l.background);
   expect(l.contrast,`${state} contrast`).toBeGreaterThanOrEqual(4.5);
  };
  // Rest: the pointer is off the entry.
  await page.mouse.move(5,5);await frame(page);
  const rest=await look(page);expect(rest.hover).toBe(false);expectReadable(rest,'rest');expect(rest.background).toBe('rgb(23, 110, 102)');await shot(page,'rest');
  // Hover: the shared button:hover rule must not win over the entry's own colours.
  await entry.hover();await frame(page);
  const hover=await look(page);expect(hover.hover).toBe(true);expectReadable(hover,'hover');expect(hover.background).toBe('rgb(18, 91, 84)');await shot(page,'hover');
  // Press: held down without releasing, then released off the entry so nothing opens.
  await page.mouse.down();await frame(page);
  const active=await look(page);expect(active.active).toBe(true);expectReadable(active,'active');
  await page.mouse.move(5,5);await page.mouse.up();await expect(page.locator('#download-activity-panel')).toHaveCount(0);
  // Keyboard focus: the gold ring, still white on teal, with and without the pointer over it.
  await page.mouse.move(5,5);await entry.focus();await page.keyboard.press('Shift');await frame(page);
  const focus=await look(page);expect(focus.focusVisible).toBe(true);expectReadable(focus,'focus');
  expect(await entry.evaluate(el=>{const cs=getComputedStyle(el);return[cs.outlineStyle,cs.outlineWidth,cs.outlineColor];})).toEqual(['solid','3px','rgb(221, 163, 58)']);
  await shot(page,'focus');
  await entry.hover();await frame(page);
  const both=await look(page);expect(both.hover&&both.focusVisible).toBe(true);expectReadable(both,'focus and hover');
  // Enter still opens the panel from it.
  await page.keyboard.press('Enter');await expect(page.getByRole('heading',{name:'Downloads & activity',exact:true})).toBeFocused();
  await page.keyboard.press('Escape');await expect(entry).toBeFocused();
 }finally{await s.close();}
});

test('at 600x480 and 200% text the shell scrolls every area clear of the floating activity entry, including a Creatures card note',async()=>{
 test.setTimeout(120_000);const s=await opened('resourceLimit');
 try{const{page,app}=s;const nav=page.getByRole('navigation',{name:'Build areas'});
  // With the shell and every list inside it at their end, no visible piece of the area's content is under the entry: what is
  // still below it can't be scrolled any further. Parts clipped by a list's own scroller don't count; that list scrolls them.
  const covered=()=>page.evaluate(()=>{
   const app=document.querySelector<HTMLElement>('.dh-app')!,area=document.querySelector<HTMLElement>('.desktop-home .dh-area')!;
   for(const el of area.querySelectorAll<HTMLElement>('*'))if(el.scrollHeight>el.clientHeight+1&&/(auto|scroll)/.test(getComputedStyle(el).overflowY))el.scrollTop=el.scrollHeight;
   app.scrollTop=app.scrollHeight;
   const entry=document.querySelector('.download-activity-floating')!.getBoundingClientRect(),hits:string[]=[];
   for(const el of area.querySelectorAll<HTMLElement>('*')){
    const own=[...el.childNodes].some(n=>n.nodeType===3&&n.textContent!.trim());
    if(!own&&!el.matches('button,input,select,canvas,img,[role=img]'))continue;
    if(el.closest('.visually-hidden'))continue;
    for(const rect of el.getClientRects()){
     let top=rect.top,bottom=rect.bottom,left=rect.left,right=rect.right;
     for(let p=el.parentElement;p&&p!==app;p=p.parentElement){if(getComputedStyle(p).overflowY==='visible')continue;const c=p.getBoundingClientRect();top=Math.max(top,c.top);bottom=Math.min(bottom,c.bottom);left=Math.max(left,c.left);right=Math.min(right,c.right);}
     if(right-left<1||bottom-top<1)continue;
     if(bottom>entry.top+1&&top<entry.bottom-1&&right>entry.left+1&&left<entry.right-1)hits.push(`${el.tagName.toLowerCase()}.${el.className} "${(el.textContent??'').trim().slice(0,60)}" ${Math.round(top)}-${Math.round(bottom)}`);
    }
   }
   return hits;
  });
  await nav.getByRole('button',{name:'Creatures',exact:true}).click();await expect(page.locator('.cv-option')).toHaveCount(50);
  await page.getByRole('option',{name:/^Authored creature 0/}).click();
  const card=page.getByRole('article',{name:/^Slot 0 · Display 100/});await expect(card).toContainText('FileDataID 123456');
  // The #98 capture: a model over a size limit leaves its code and note under the preview box.
  await card.getByRole('button',{name:'Show model preview for display 100',exact:true}).click();
  await expect(card.getByRole('img',{name:'Model too large',exact:true})).toBeVisible({timeout:20_000});
  await small(app,page);
  // The compact layout shows one pane at a time; the detail holds the card.
  const pane=page.getByRole('button',{name:'Selected creature',exact:true});if(await pane.isVisible())await pane.click();
  const note=card.locator('.cv-pnote');await expect(note).toContainText('AUTHORED_RESOURCE_LIMIT');
  await toEnd(page);
  // The note sits mid-page, so it passes under the entry while scrolling; revealed with the shell's scroll padding it ends above it.
  await note.evaluate(el=>el.scrollIntoView({block:'end'}));await frame(page);
  const clear=await note.evaluate(el=>({note:el.getBoundingClientRect().bottom,entry:document.querySelector('.download-activity-floating')!.getBoundingClientRect().top}));
  expect(clear.note,`card note ends at ${clear.note}px, entry starts at ${clear.entry}px`).toBeLessThanOrEqual(clear.entry);
  await shot(page,'creatures-note');
  expect(await covered(),'Creatures').toEqual([]);
  await shot(page,'creatures-end');
  for(const area of['Home','Tables','Files','Items']){
   await size(app,1280,800);await text(page,1);
   await nav.getByRole('button',{name:area,exact:true}).click();await expect(nav.getByRole('button',{name:area,exact:true})).toHaveAttribute('aria-current','page');
   await small(app,page);await toEnd(page);
   expect(await covered(),area).toEqual([]);
   await shot(page,`${area.toLowerCase()}-end`);
  }
 }finally{await s.close();}
});
