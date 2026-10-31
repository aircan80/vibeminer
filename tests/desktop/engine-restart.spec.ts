import {test,expect} from '@playwright/test';
import {mkdir,writeFile,readFile} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import path from 'node:path';
import {launch,root,size,text} from './helpers';
// D119 (#91): one invalid engine reply stops the engine, the app says so in one place, and Restart engine starts a new
// process that serves the build and area that were open.
const calls=async(data:string)=>(await readFile(path.join(data,'requests.ndjson'),'utf8')).trim().split('\n').map(x=>JSON.parse(x) as {id:string;method:string});
test('an invalid reply shows the stopped engine, and Restart engine brings Items back',async()=>{test.setTimeout(90_000);const s=await launch(true,false,false,true);try{const{page,app,data}=s;
 await page.locator('.build-row').first().click();await page.getByRole('button',{name:'Open build',exact:true}).click();await writeFile(path.join(data,'release-open'),'release');
 await expect(page.getByRole('heading',{name:'Build overview',exact:true})).toBeVisible();await writeFile(path.join(data,'cached-items'),'cached');
 await page.getByRole('navigation',{name:'Build areas'}).getByRole('button',{name:'Items',exact:true}).click();
 await expect(page.locator('#iv-opt-0')).toBeVisible();
 // The next Items page gets a reply the schema refuses.
 await writeFile(path.join(data,'invalid-reply-items.page'),'once');
 const next=page.getByRole('button',{name:'Next page of Items',exact:true});await next.focus();await page.keyboard.press('Enter');
 const dialog=page.getByRole('alertdialog',{name:'Engine stopped after an invalid reply'});
 await expect(dialog).toBeVisible();await expect(dialog).toContainText('PROTOCOL_ERROR');await expect(dialog).toContainText('The local engine sent an invalid message.');
 const restart=dialog.getByRole('button',{name:'Restart engine',exact:true});await expect(restart).toBeFocused();
 expect(existsSync(path.join(data,'invalid-reply-items.page'))).toBe(false);
 // Modal: Tab stays on the one action and Escape doesn't dismiss it. Nothing restarts by itself.
 await page.keyboard.press('Tab');await expect(restart).toBeFocused();await page.keyboard.press('Escape');await expect(dialog).toBeVisible();
 await page.waitForTimeout(500);expect((await calls(data)).filter(r=>r.method==='hello')).toHaveLength(1);
 const directory=path.join(root,'artifacts/engine-restart');await mkdir(directory,{recursive:true});
 for(const[width,height,scale]of[[1280,800,1],[1280,800,2],[600,480,2]]as const){await size(app,width,height);await text(page,scale);
  await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
  const box=await dialog.boundingBox();expect(box!.x).toBeGreaterThanOrEqual(0);expect(box!.x+box!.width).toBeLessThanOrEqual(width+1);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  // The one action stays whole in view even when 200% text makes the dialog scroll.
  const action=await restart.boundingBox();expect(action!.y).toBeGreaterThanOrEqual(0);expect(action!.y+action!.height).toBeLessThanOrEqual(height);
  await page.screenshot({path:path.join(directory,`stopped-${width}x${height}-${scale*100}.png`)});}
 await text(page,1);await size(app,1280,800);
 await page.keyboard.press('Enter');
 await expect(dialog).toHaveCount(0,{timeout:30_000});
 // The new engine reopens the same build and Items loads again with focus on its heading.
 await expect(page.getByRole('heading',{name:'Items',exact:true})).toBeFocused();
 await expect(page.locator('#iv-opt-0')).toBeVisible();
 await page.getByRole('button',{name:'Next page of Items',exact:true}).click();
 await expect(page.locator('#iv-opt-50')).toBeVisible();
 const requests=await calls(data);
 expect(requests.filter(r=>r.method==='hello')).toHaveLength(2);expect(requests.filter(r=>r.method==='workspace.open')).toHaveLength(2);
 expect(requests.filter(r=>r.method==='items.page').length).toBeGreaterThanOrEqual(4);
 await page.screenshot({path:path.join(directory,'restarted-items.png')});
}finally{await s.close();}});
