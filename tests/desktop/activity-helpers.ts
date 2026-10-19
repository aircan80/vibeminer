import {expect,type Page} from '@playwright/test';
export async function activityTask(page:Page,title:string){
 const panel=page.locator('#download-activity-panel');
 if(!await panel.isVisible())await page.locator('.download-activity-trigger:visible').last().click();
 const row=panel.locator('article').filter({has:page.getByRole('heading',{name:title,exact:true})});await expect(row).toBeVisible();return row;
}
export async function closeActivity(page:Page){await page.getByRole('button',{name:'Close downloads and activity',exact:true}).click();}
export async function currentStage(page:Page,title:string){const row=await activityTask(page,title),summary=row.getByText('Current stage and exact task identity',{exact:true});if(!await summary.evaluate(el=>(el.parentElement as HTMLDetailsElement).open))await summary.click();return row;}
