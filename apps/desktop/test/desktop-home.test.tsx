import {afterEach,it,expect,vi} from 'vitest';
import {render,screen,fireEvent,cleanup,within} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import type {WorkspaceSnapshot,DownloadPage} from '../../../contracts/generated/discovery';
import workspace from '../../../contracts/examples/download-workspace.json';
import {BuildAreas} from '../src/renderer/BuildAreas';
import {HomeShell} from '../src/renderer/HomeShell';
import {DownloadActivity} from '../src/renderer/DownloadActivity';
import {DownloadObjects} from '../src/renderer/DownloadObjects';
import type {DiscoveryBridge} from '../src/shared/bridge';
const w=workspace as unknown as WorkspaceSnapshot;
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('orders Home, the source layer and the joined game views, hiding Items and Creatures with their separator when unavailable',()=>{
 const go=vi.fn(),{rerender}=render(<BuildAreas active="tables" onHome={go} onTables={go} onFiles={go}/>);
 const nav=screen.getByRole('navigation',{name:'Build areas'});
 expect(within(nav).getAllByRole('button').map(b=>b.textContent)).toEqual(['Home','Tables','Files']);
 expect(within(within(nav).getByRole('group',{name:'Source data'})).getAllByRole('button').map(b=>b.textContent)).toEqual(['Tables','Files']);
 expect(within(nav).queryByRole('group',{name:'Game data'})).not.toBeInTheDocument();expect(nav.querySelector('.dh-area-sep')).toBeNull();
 expect(within(nav).getByRole('button',{name:'Tables'})).toHaveAttribute('aria-current','page');
 rerender(<BuildAreas active="items" onHome={go} onTables={go} onFiles={go} onItems={go} onCreatures={go}/>);
 expect(within(nav).getAllByRole('button').map(b=>b.textContent)).toEqual(['Home','Tables','Files','Items','Creatures']);
 expect(within(within(nav).getByRole('group',{name:'Game data'})).getAllByRole('button').map(b=>b.textContent)).toEqual(['Items','Creatures']);
 expect(nav.querySelectorAll('.dh-area-sep')).toHaveLength(1);
 rerender(<BuildAreas active="overview" onHome={go} onTables={go} onFiles={go} onItems={go}/>);
 expect(within(within(nav).getByRole('group',{name:'Game data'})).getAllByRole('button').map(b=>b.textContent)).toEqual(['Items']);
});

it('shows the exact build, source and locale in the status bar and no activity entry while idle',()=>{
 const choose=vi.fn();
 render(<DownloadActivity tasks={[]} onAction={()=>{}} contextKey="ctx"><HomeShell snapshot={w} label="Retail" onChoose={choose} area="home" areas={<nav aria-label="Build areas"/>} fixtureLabel="Authored test data"><p>Area</p></HomeShell></DownloadActivity>);
 const status=document.querySelector('.dh-statusbar')!;
 expect(status).toHaveTextContent(`${w.build.productId} ${w.build.version} · ${w.sourceReference.region.toUpperCase()} · enUS`);
 expect(status).toHaveTextContent('Authored test data');
 expect(document.querySelector('.dv-chip')).toBeNull();expect(document.querySelector('.download-activity-floating')).toBeNull();
 const button=screen.getByRole('button',{name:'← Choose build'});expect(button).toHaveClass('dh-primary');fireEvent.click(button);expect(choose).toHaveBeenCalledTimes(1);
 expect(screen.queryByRole('button',{name:'Source details'})).not.toBeInTheDocument();
 expect(screen.getByRole('button',{name:`Build ${w.build.version}`})).toBeVisible();
});

it('moves the Files selection with arrow keys from one listbox tab stop and keeps it in the inspector across pages',async()=>{
 const page=(after?:string):DownloadPage=>{const start=Number(after??0);return {productId:w.build.productId,buildIdentity:w.build.id,sourceRegion:w.sourceReference.region,ledgerId:w.download!.ledgerId,assessedAt:w.download!.assessedAt,entries:Array.from({length:start?2:3},(_,i)=>({id:String(start+i+1),encodingKey:String(start+i+1).padStart(32,'0'),encodedBytes:'100',verifiedAtAssessment:i===0})),totalCount:'5',nextCursor:start?null:'3'};};
 const bridge={download:vi.fn(async(p:{after?:string})=>({ok:true,value:page(p.after)}))} as unknown as DiscoveryBridge;
 Element.prototype.scrollIntoView=vi.fn();
 render(<DownloadObjects snapshot={w} summary={{...w.download!,objectCount:'5'}} bridge={bridge} active onReopen={()=>{}}/>);
 await screen.findByText('Object 1');
 const list=screen.getByRole('listbox');expect(screen.getAllByRole('option')[0]).toHaveAttribute('aria-selected','true');expect(screen.getByRole('heading',{name:'Encoded object 1'})).toBeInTheDocument();
 list.focus();fireEvent.keyDown(list,{key:'ArrowDown'});expect(screen.getByRole('heading',{name:'Encoded object 2'})).toBeInTheDocument();expect(list).toHaveAttribute('aria-activedescendant',screen.getAllByRole('option')[1].id);
 fireEvent.keyDown(list,{key:'End'});expect(screen.getByRole('heading',{name:'Encoded object 3'})).toBeInTheDocument();expect(list).toHaveFocus();
 fireEvent.click(screen.getByRole('button',{name:'Next page'}));await screen.findByText('Object 4');
 expect(screen.getByRole('heading',{name:'Encoded object 3'})).toBeInTheDocument();expect(screen.getByText('Outside the current page · same saved assessment')).toBeInTheDocument();
 expect(screen.getAllByRole('option').every(o=>o.getAttribute('aria-selected')==='false')).toBe(true);
});
