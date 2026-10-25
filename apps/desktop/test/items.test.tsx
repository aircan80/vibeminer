import {useState} from 'react';
import {act,cleanup,fireEvent,render,screen,waitFor,within} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,it,expect,vi} from 'vitest';
import type {DiscoveryBridge,BridgeReply} from '../src/shared/bridge';
import type {ItemsSnapshot,ItemsPage,ItemInspectResult,ItemFactsResult} from '../../../contracts/generated/discovery';
import {ItemsBrowser} from '../src/renderer/ItemsBrowser';
import {ShellContext} from '../src/renderer/HomeShell';
import {SettingsProvider} from '../src/renderer/Settings';
import {useItemsLoading,ItemsLoadBanner} from '../src/renderer/useItemsLoading';
import {itemsSnapshot,itemsPage,itemDetail,itemFacts,itemLabelLists} from '../../../tests/desktop/fixtures/items.mjs';
import workspace from '../../../tests/desktop/fixtures/download-workspace.json';
import definitionErrorsJson from '../../../contracts/examples/definition-errors.json';
import type {EngineError} from '../../../contracts/generated/discovery';
const definitionErrors=definitionErrorsJson as Record<keyof typeof definitionErrorsJson,EngineError>;
// Items view (docs/design/items-view): list, search, engine-projected facts with source links, roving sections and the one-pane layout.
const snapshot=itemsSnapshot(),ok=<T,>(value:T):BridgeReply<T>=>({ok:true,value});
const deferred=<T,>()=>{let resolve!:(value:T)=>void;return{promise:new Promise<T>(r=>resolve=r),resolve:(value:T)=>resolve(value)};};
afterEach(()=>{cleanup();vi.restoreAllMocks();});
const row=(id:string)=>document.getElementById(`iv-opt-${id}`);
const findRow=async(id:string)=>{await waitFor(()=>expect(row(id)).toBeInTheDocument());return row(id)!;};
const listbox=()=>screen.getByRole('listbox',{name:'Items on this page'});
const applied=()=>document.querySelector('.iv-applied')!;
function setup(ready=true,configure?:(bridge:DiscoveryBridge)=>void,options:{compact?:boolean;onSource?:(...args:unknown[])=>void;source?:ItemsSnapshot;openSettings?:(trigger:HTMLElement)=>void;onOpenTable?:(tableId:string,name:string)=>void}={}){const onSource=options.onSource??vi.fn();
 vi.spyOn(window,'scrollTo').mockImplementation(()=>{});Element.prototype.scrollIntoView=vi.fn();
 const s=options.source??snapshot,pending=deferred<BridgeReply<ItemsSnapshot>>();let admitted=ready;
 const bridge={onProgress:vi.fn(()=>()=>{}),cancel:vi.fn(async()=>ok({requested:true})),
  itemsData:vi.fn(async()=>ok({snapshot:admitted?s:null,dependencies:[{tableId:'50238ec2',state:admitted?'ready':'notPrepared',artifactId:admitted?s.itemSource.artifactId:null},{tableId:'919be54e',state:admitted?'ready':'notPrepared',artifactId:admitted?s.sparseSource.artifactId:null}]})),
  itemsPrepare:vi.fn(()=>pending.promise.then(reply=>{if(reply.ok)admitted=true;return reply;})),
  itemsPage:vi.fn(async p=>ok(itemsPage(s,p))),
  itemInspect:vi.fn(async p=>ok({context:s.context,artifactId:s.artifactId,itemId:p.itemId,detail:itemDetail(s,p.itemId)})),
  itemFacts:vi.fn(async p=>ok(itemFacts(s,p))),itemLabels:vi.fn(async()=>ok(itemLabelLists))}as unknown as DiscoveryBridge;
 configure?.(bridge);
 function Harness(){const flow=useItemsLoading(bridge),[active,setActive]=useState(true),[mounted,setMounted]=useState(true);return <SettingsProvider value={options.openSettings?{open:()=>{},register:()=>()=>{},openHere:options.openSettings}:null}><ShellContext.Provider value={{compact:!!options.compact,statusSlot:null}}><button onClick={()=>flow.load({context:s.context,buildVersion:workspace.build.version})}>Attach exact Items demand</button><button onClick={()=>setActive(x=>!x)}>Switch area</button><button onClick={()=>setMounted(x=>!x)}>Choose build</button><ItemsLoadBanner flow={flow} onView={()=>setActive(true)}/>{mounted&&<ItemsBrowser workspace={workspace as never} bridge={bridge} active={active} flow={flow} factsAvailable onSource={onSource} onOpenTable={options.onOpenTable}/>}</ShellContext.Provider></SettingsProvider>;}
 render(<Harness/>);return{bridge,pending};}
async function ready(){await findRow('0');await waitFor(()=>expect(screen.getByRole('heading',{name:'Items'})).toHaveFocus());}
function search(mode:'Name'|'Item ID',value:string){fireEvent.click(screen.getByRole('button',{name:mode}));const box=screen.getByRole('searchbox',{name:mode==='Name'?'Name contains':'Exact Item ID'});fireEvent.change(box,{target:{value}});fireEvent.click(screen.getByRole('button',{name:'Search'}));return box;}
async function choose(id:string){const option=await findRow(id);fireEvent.click(option);await waitFor(()=>expect(option).toHaveAttribute('aria-selected','true'));return option;}

it('opens cached summaries locally without preparing and shows the engine facts with labels, states and aligned stat inputs',async()=>{
 const{bridge}=setup();await ready();expect(bridge.itemsPrepare).not.toHaveBeenCalled();
 await choose('9');await screen.findByRole('heading',{name:'Authored sword 9',level:2});
 expect(bridge.itemFacts).toHaveBeenCalledWith(expect.objectContaining({itemId:'9',artifactId:snapshot.artifactId}));
 // Selection follows the list: the list keeps focus in the two-pane layout.
 expect(listbox()).toHaveFocus();expect(listbox()).toHaveAttribute('aria-activedescendant','iv-opt-9');
 const item=screen.getByRole('region',{name:'Item'});
 await waitFor(()=>expect(within(item).getByText('Rare',{exact:false})).toBeVisible());
 expect(within(item).getByText('Item and ItemSparse disagree.')).toBeVisible();
 expect(within(item).getAllByText(/Inventory type · (Item|ItemSparse)/)).toHaveLength(2);
 const requirements=screen.getByRole('region',{name:'Requirements'});
 expect(within(requirements).getAllByText('Unsupported').length).toBeGreaterThan(0);
 expect(within(requirements).getByText('[0, 1]',{exact:false})).toBeVisible();
 const inventory=screen.getByRole('region',{name:'Inventory and price'});
 expect(within(inventory).getByText('no reviewed label',{exact:false})).toBeVisible();
 expect(screen.getByText('Text unavailable')).toBeVisible();
 const slots=screen.getByRole('region',{name:'Stat allocation, 10 slots'});
 expect(within(slots).getAllByRole('row')).toHaveLength(11);
 expect(within(slots).getByRole('columnheader',{name:/Percent editor/})).toBeVisible();
 expect(screen.getByRole('heading',{name:'Stat allocation, not final stats'})).toBeVisible();
 expect(screen.getByText('Final +N depends on item level, slot and bonuses.',{exact:false})).toBeVisible();
 expect(row('9')).toHaveAttribute('aria-selected','true');
});

it('shows a whole-section Missing for an Item row without ItemSparse in complete scope, and Unknown when ItemSparse is Partial',async()=>{
 setup();await ready();fireEvent.click(screen.getByRole('button',{name:'Next page of Items'}));await choose('58');
 await screen.findByRole('heading',{name:'Name unavailable',level:2});
 expect(screen.getByText('Item row only: no ItemSparse row, so no name, quality or level')).toBeVisible();
 expect(within(screen.getByRole('region',{name:'Requirements'})).getByText('Missing')).toBeVisible();
 expect(screen.getByRole('button',{name:/^Item #58/})).toBeVisible();expect(screen.queryByRole('button',{name:/^ItemSparse #58/})).not.toBeInTheDocument();
 cleanup();
 const partial=structuredClone(snapshot);partial.sparseSource.coverage.scope='unencryptedSections';
 setup(true,undefined,{source:partial});await findRow('0');fireEvent.click(screen.getByRole('button',{name:'Next page of Items'}));await choose('58');
 await screen.findByRole('heading',{name:'Name unknown',level:2});
 expect(within(screen.getByRole('region',{name:'Requirements'})).getByText('Unknown')).toBeVisible();
 expect(screen.getByText('ItemSparse readable rows · Partial')).toBeVisible();
});

it('opens each value at its exact source record and column, and makes every section one Tab stop with arrows between its links',async()=>{
 const onSource=vi.fn();setup(true,undefined,{onSource});await ready();await choose('3');await screen.findByRole('heading',{name:'Authored sword 3',level:2});
 const item=screen.getByRole('region',{name:'Item'});await waitFor(()=>expect(within(item).getByRole('button',{name:/^ItemSparse\.OverallQualityID/})).toBeVisible());
 const stops=within(item).getAllByRole('button').filter(b=>b.classList.contains('iv-src')&&b.tabIndex===0);expect(stops).toHaveLength(1);
 const first=stops[0];first.focus();fireEvent.keyDown(first,{key:'ArrowDown'});
 const second=document.activeElement as HTMLElement;expect(second).not.toBe(first);expect(second).toHaveAttribute('tabindex','0');expect(first).toHaveAttribute('tabindex','-1');
 fireEvent.keyDown(second,{key:'Home'});expect(first).toHaveFocus();
 // Links in the closed "Item level inputs" disclosure are skipped: End and Down stop at the section's last visible link.
 const closed=within(item).getByText('Item level inputs · raw').closest('details')!;expect(closed.open).toBe(false);
 const inside=[...closed.querySelectorAll('.iv-src')];expect(inside.length).toBeGreaterThan(0);
 fireEvent.keyDown(document.activeElement!,{key:'End'});const last=document.activeElement as HTMLElement;
 expect(last).toHaveClass('iv-src');expect(closed.contains(last)).toBe(false);
 fireEvent.keyDown(last,{key:'ArrowDown'});expect(last).toHaveFocus();
 fireEvent.keyDown(last,{key:'ArrowDown'});expect(last).toHaveFocus();expect(inside.every(l=>l.getAttribute('tabindex')==='-1')).toBe(true);
 // Opened, its links join the section in reading order.
 closed.open=true;fireEvent.keyDown(last,{key:'ArrowDown'});expect(closed.contains(document.activeElement)).toBe(true);
 fireEvent.keyDown(document.activeElement!,{key:'Home'});closed.open=false;
 fireEvent.click(within(item).getByRole('button',{name:/^ItemSparse\.OverallQualityID/}));
 expect(onSource).toHaveBeenLastCalledWith(snapshot.sparseSource,'3',3);
 fireEvent.click(screen.getByRole('button',{name:/^ItemSparse #3/}));expect(onSource).toHaveBeenLastCalledWith(snapshot.sparseSource,'3',undefined);
 fireEvent.click(screen.getByRole('button',{name:/^Item #3/}));expect(onSource).toHaveBeenLastCalledWith(snapshot.itemSource,'3',undefined);
 expect(screen.getByText("Tab moves between sections; arrow keys move between a section's links.")).toBeVisible();
});

it('says so when the engine has no item facts, and keeps the record buttons',async()=>{
 setup(true,b=>{vi.mocked(b.itemFacts!).mockResolvedValue({ok:false,error:{code:'UNSUPPORTED',message:'This engine does not support item facts.',retryable:false}});});await ready();await choose('2');
 await screen.findByText('Item facts need attention');expect(screen.getByText('UNSUPPORTED')).toBeVisible();
 expect(screen.queryByRole('button',{name:'Retry item details'})).not.toBeInTheDocument();
});

it('fetches the facts again on Retry item details after a retryable facts failure',async()=>{
 const{bridge}=setup(true,b=>{vi.mocked(b.itemFacts!).mockResolvedValueOnce({ok:false,error:{code:'ENGINE_UNAVAILABLE',message:'The item facts could not be read.',retryable:true}});});await ready();await choose('2');
 const retry=await screen.findByRole('button',{name:'Retry item details'});expect(bridge.itemFacts).toHaveBeenCalledTimes(1);
 fireEvent.click(retry);await waitFor(()=>expect(bridge.itemFacts).toHaveBeenCalledTimes(2));
 await waitFor(()=>expect(within(screen.getByRole('region',{name:'Item'})).getByText('Rare',{exact:false})).toBeVisible());
 expect(screen.queryByText('Item facts need attention')).not.toBeInTheDocument();
});

it('moves a section\'s Tab stop to the link that was clicked, and only that one',async()=>{
 setup();await ready();await choose('3');await screen.findByRole('heading',{name:'Authored sword 3',level:2});
 const item=screen.getByRole('region',{name:'Item'});await waitFor(()=>expect(within(item).getByRole('button',{name:/^Item\.SubclassID/})).toBeVisible());
 fireEvent.click(within(item).getByRole('button',{name:/^ItemSparse\.ItemLevel/}));
 fireEvent.click(within(item).getByRole('button',{name:/^Item\.SubclassID/}));
 const stops=[...item.querySelectorAll('.iv-src')].filter(l=>l.getAttribute('tabindex')==='0');
 expect(stops).toHaveLength(1);expect(stops[0]).toHaveAccessibleName(/^Item\.SubclassID/);
 expect(item.querySelectorAll('.iv-src[data-cur]')).toHaveLength(1);
});

it('focuses the Items search on Ctrl+F, showing the list in the one-pane layout',async()=>{
 setup(true,undefined,{compact:true});await ready();fireEvent.click(await findRow('4'));
 await waitFor(()=>expect(document.querySelector('.iv-panes')).toHaveAttribute('data-pane','detail'));
 fireEvent.keyDown(window,{key:'f',ctrlKey:true});
 await waitFor(()=>expect(screen.getByRole('searchbox',{name:'Name contains'})).toHaveFocus());
 expect(document.querySelector('.iv-panes')).toHaveAttribute('data-pane','list');
});

it('shows the selected item in its own pane in the one-pane layout, focusing its heading, and returns to the row',async()=>{
 setup(true,undefined,{compact:true});await ready();
 const option=await findRow('4');listbox().focus();fireEvent.click(option);
 await waitFor(()=>expect(screen.getByRole('heading',{name:'Authored sword 4',level:2})).toHaveFocus());
 expect(document.querySelector('.iv-panes')).toHaveAttribute('data-pane','detail');
 expect(screen.getByRole('button',{name:'Selected item'})).toHaveAttribute('aria-pressed','true');
 // Effects come first in one column.
 const regions=[...document.querySelectorAll('.iv-grid>*')].map(s=>(s.matches('section')?s:s.querySelector('section'))?.getAttribute('aria-label')??s.querySelector('h3')?.textContent);
 expect(regions[0]).toBe('Effects');
 fireEvent.click(screen.getByRole('button',{name:'Items list'}));await waitFor(()=>expect(listbox()).toHaveFocus());
 expect(document.querySelector('.iv-panes')).toHaveAttribute('data-pane','list');
 fireEvent.keyDown(listbox(),{key:'ArrowDown'});await waitFor(()=>expect(row('5')).toHaveAttribute('aria-selected','true'));
 expect(document.querySelector('.iv-panes')).toHaveAttribute('data-pane','list');
 fireEvent.keyDown(listbox(),{key:'Enter'});await waitFor(()=>expect(screen.getByRole('heading',{name:'Authored sword 5',level:2})).toHaveFocus());
});

// #93: the pane switch moved focus a frame later, so a focus move made right after the click (a keypress under load) was pulled back.
it('moves focus in the same commit as a one-pane switch, and a focus move right after it is never pulled back',async()=>{
 setup(true,undefined,{compact:true});await ready();
 fireEvent.click(await findRow('4'));await waitFor(()=>expect(screen.getByRole('heading',{name:'Authored sword 4',level:2})).toHaveFocus());
 const frame=()=>act(()=>new Promise<void>(done=>requestAnimationFrame(()=>requestAnimationFrame(()=>done()))));
 fireEvent.click(screen.getByRole('button',{name:'Items list'}));expect(listbox()).toHaveFocus();
 const search=screen.getByRole('searchbox',{name:'Name contains'});search.focus();await frame();expect(search).toHaveFocus();
 const detail=screen.getByRole('button',{name:'Selected item'});fireEvent.click(detail);expect(detail).toHaveFocus();
 const record=screen.getByRole('button',{name:/^Item #4/});record.focus();await frame();expect(record).toHaveFocus();
 fireEvent.keyDown(window,{key:'f',ctrlKey:true});expect(search).toHaveFocus();
 expect(document.querySelector('.iv-panes')).toHaveAttribute('data-pane','list');
 listbox().focus();await frame();expect(listbox()).toHaveFocus();
});

it('automatically prepares only on deliberate Items activation and does not cancel on area/build departure',async()=>{
 const{bridge,pending}=setup(false);await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1));
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));fireEvent.click(screen.getByRole('button',{name:'Choose build'}));expect(bridge.cancel).not.toHaveBeenCalled();
 await act(async()=>pending.resolve(ok(snapshot)));expect(row('0')).not.toBeInTheDocument();
 fireEvent.click(screen.getByRole('button',{name:'Choose build'}));fireEvent.click(screen.getByRole('button',{name:'Switch area'}));
 await waitFor(()=>expect(bridge.itemsData).toHaveBeenCalledTimes(2));expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1);
});

it('shows preparation in place with each prerequisite, a named Stop and disabled search with its reason',async()=>{
 const{bridge,pending}=setup(false);await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1));
 await screen.findByText('Preparing Items…');expect(screen.getAllByText('needed by Items, at interactive priority',{exact:false})).toHaveLength(2);
 expect(screen.getByRole('searchbox',{name:'Name contains'})).toBeDisabled();expect(screen.getByRole('searchbox',{name:'Name contains'})).toHaveAccessibleDescription(/Search and the list work once Item and ItemSparse are both ready/);
 const stop=screen.getByRole('button',{name:'Stop preparing Items'});fireEvent.click(stop);await waitFor(()=>expect(bridge.cancel).toHaveBeenCalledTimes(1));
 await act(async()=>pending.resolve({ok:false,error:{code:'CANCELED',message:'Stopped.',retryable:true}}));await waitFor(()=>expect(within(document.querySelector('.iv')!).getByText('Items loading stopped')).toBeVisible());
});

it('says Stopped. after Stop, and Retry Items preparation hands focus to the Preparing status instead of the page',async()=>{
 const{bridge,pending}=setup(false);await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1));
 fireEvent.click(screen.getByRole('button',{name:'Stop preparing Items'}));
 await act(async()=>pending.resolve({ok:false,error:{code:'CANCELED',message:'Operation canceled.',retryable:true}}));
 const box=within(document.querySelector('.iv')!);await waitFor(()=>expect(box.getByText('Items loading stopped')).toBeVisible());
 expect(box.getByText('Stopped.')).toBeVisible();expect(box.queryByText('CANCELED')).not.toBeInTheDocument();
 vi.mocked(bridge.itemsPrepare).mockReturnValue(new Promise(()=>{}));
 const retry=box.getByRole('button',{name:'Retry Items preparation'});retry.focus();fireEvent.click(retry);
 await waitFor(()=>expect(document.activeElement).toHaveClass('dh-state-box'));
 expect(document.activeElement).toHaveAttribute('role','status');expect(document.activeElement).toHaveTextContent(/Checking retained Items…|Preparing Items…/);
 await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(2));expect(document.activeElement).toHaveAttribute('role','status');
});

it('moves focus from the Preparing status to the search when the list becomes ready',async()=>{
 const{bridge,pending}=setup(false);await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1));
 fireEvent.click(screen.getByRole('button',{name:'Stop preparing Items'}));
 await act(async()=>pending.resolve({ok:false,error:{code:'CANCELED',message:'Operation canceled.',retryable:true}}));
 const box=within(document.querySelector('.iv')!);await waitFor(()=>expect(box.getByText('Items loading stopped')).toBeVisible());
 const second=deferred<BridgeReply<ItemsSnapshot>>();vi.mocked(bridge.itemsPrepare).mockReturnValue(second.promise);
 const retry=box.getByRole('button',{name:'Retry Items preparation'});retry.focus();fireEvent.click(retry);
 await waitFor(()=>expect(document.activeElement).toHaveAttribute('role','status'));
 await act(async()=>second.resolve(ok(snapshot)));
 await findRow('0');await waitFor(()=>expect(screen.getByRole('searchbox',{name:'Name contains'})).toHaveFocus());
});

it('keeps literal name and exact ID modes separate and rejects malformed IDs before a query',async()=>{
 const{bridge}=setup();await ready();const before=vi.mocked(bridge.itemsPage).mock.calls.length;
 const id=search('Item ID','01');expect(bridge.itemsPage).toHaveBeenCalledTimes(before);
 expect(screen.getByRole('alert')).toHaveTextContent('whole Item ID');expect(id).toHaveAttribute('aria-invalid','true');expect(id).toHaveAccessibleDescription(/whole Item ID/);
 fireEvent.change(id,{target:{value:'58'}});fireEvent.click(screen.getByRole('button',{name:'Search'}));
 await waitFor(()=>expect(applied()).toHaveTextContent('Exact Item ID 58 · 1 on this page'));
 expect(row('58')).toHaveTextContent('Name unavailable · no ItemSparse row');
 expect(bridge.itemsPage).toHaveBeenLastCalledWith(expect.objectContaining({exactItemId:'58',query:''}));
 search('Name','sword 9');await waitFor(()=>expect(applied()).toHaveTextContent('Name contains “sword 9”'));
 expect(bridge.itemsPage).toHaveBeenLastCalledWith(expect.not.objectContaining({exactItemId:expect.any(String)}));expect(row('9')).toBeVisible();
 fireEvent.click(screen.getByRole('button',{name:'Clear search'}));await waitFor(()=>expect(applied()).toHaveTextContent('All admitted Items · ascending Item ID'));
});

it('retains a selected off-page item and query/page state across area navigation',async()=>{
 setup();await ready();await choose('7');await screen.findByRole('heading',{name:'Empty source name',level:2});
 fireEvent.click(screen.getByRole('button',{name:'Next page of Items'}));await findRow('50');
 expect(screen.getByText(/Selected item 7 is outside these results/)).toBeVisible();
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));fireEvent.click(screen.getByRole('button',{name:'Switch area'}));
 expect(row('50')).toBeVisible();expect(screen.getByRole('heading',{name:'Empty source name',level:2})).toBeVisible();
});

it('discards a held old-query page and removes its stale Retry when a new search is applied',async()=>{
 const{bridge}=setup();await ready();const held=deferred<BridgeReply<ItemsPage>>();vi.mocked(bridge.itemsPage).mockReturnValueOnce(held.promise);
 search('Name','sword 1');const box=screen.getByRole('searchbox',{name:'Name contains'});fireEvent.change(box,{target:{value:'sword 9'}});fireEvent.click(screen.getByRole('button',{name:'Search'}));
 await waitFor(()=>expect(applied()).toHaveTextContent('Name contains “sword 9”'));
 await act(async()=>held.resolve(ok(itemsPage(snapshot,{...snapshot.context,artifactId:snapshot.artifactId,query:'sword 1'}))));
 expect(row('9')).toBeVisible();expect(row('1')).not.toBeInTheDocument();expect(screen.queryByRole('button',{name:'Retry Items page'})).not.toBeInTheDocument();
});

it('does not let a held older detail or facts reply replace a newer deliberate selection',async()=>{
 const{bridge}=setup();await ready();const held=deferred<BridgeReply<ItemInspectResult>>(),heldFacts=deferred<BridgeReply<ItemFactsResult>>();
 vi.mocked(bridge.itemInspect).mockReturnValueOnce(held.promise);vi.mocked(bridge.itemFacts!).mockReturnValueOnce(heldFacts.promise);
 fireEvent.click(await findRow('1'));fireEvent.click(await findRow('2'));await screen.findByRole('heading',{name:'Authored sword 2',level:2});
 await act(async()=>{held.resolve(ok({context:snapshot.context,artifactId:snapshot.artifactId,itemId:'1',detail:itemDetail(snapshot,'1')}));heldFacts.resolve(ok(itemFacts(snapshot,{itemId:'1'})));});
 expect(screen.getByRole('heading',{name:'Authored sword 2',level:2})).toBeVisible();expect(screen.queryByRole('heading',{name:'Authored sword 1',level:2})).not.toBeInTheDocument();
 expect(screen.getByRole('button',{name:/^Item #2/})).toBeVisible();
 // The facts shown are item 2's: every source link points at record 2, none at the late item 1 reply.
 const links=[...document.querySelectorAll('.iv-detail .iv-src')].map(l=>l.textContent??'');
 expect(links.length).toBeGreaterThan(5);expect(links.every(l=>!/record 1 in Tables/.test(l))).toBe(true);expect(links.filter(l=>/ItemSparse record 2 in Tables/.test(l)).length).toBeGreaterThan(3);
});

it('keeps Stop latched across view eviction and offers Retry only after actual terminal',async()=>{
 const{bridge,pending}=setup(false);await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1));
 const stop=screen.getByRole('button',{name:'Stop loading Items'});stop.focus();fireEvent.click(stop);expect(screen.getByRole('button',{name:'Stopping Items…'})).toHaveFocus();
 expect(screen.queryByRole('button',{name:'Retry Items preparation'})).not.toBeInTheDocument();
 await act(async()=>pending.resolve({ok:false,error:{code:'CANCELED',message:'The target stopped after cleanup.',retryable:true}}));
 fireEvent.click(screen.getByRole('button',{name:'Choose build'}));fireEvent.click(screen.getByRole('button',{name:'Choose build'}));
 await waitFor(()=>expect(within(document.querySelector('.iv')!).getByText('Items loading stopped')).toBeVisible());expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1);expect(screen.getByRole('button',{name:'Retry Items preparation'})).toBeVisible();
});

it('clears incompatible pages/selection atomically before a held replacement page can mix column bindings',async()=>{
 const{bridge}=setup();await ready();await choose('0');await screen.findByRole('heading',{name:'Authored sword 0',level:2});
 vi.mocked(bridge.itemsPage).mockResolvedValueOnce({ok:false,error:{code:'ITEMS_ARTIFACT_STALE',message:'Current admission changed.',retryable:true}});
 fireEvent.click(screen.getByRole('button',{name:'Next page of Items'}));await screen.findByRole('button',{name:'Recheck Items admission'});
 const replacement=structuredClone(snapshot);replacement.artifactId='f'.repeat(64);[replacement.sparseSource.columns[3],replacement.sparseSource.columns[4]]=[replacement.sparseSource.columns[4],replacement.sparseSource.columns[3]];
 vi.mocked(bridge.itemsData).mockResolvedValueOnce(ok({snapshot:replacement,dependencies:[{tableId:'50238ec2',state:'ready',artifactId:replacement.itemSource.artifactId},{tableId:'919be54e',state:'ready',artifactId:replacement.sparseSource.artifactId}]}));
 const held=deferred<BridgeReply<ItemsPage>>();vi.mocked(bridge.itemsPage).mockReturnValueOnce(held.promise);
 fireEvent.click(screen.getByRole('button',{name:'Recheck Items admission'}));
 await waitFor(()=>expect(bridge.itemsPage).toHaveBeenLastCalledWith(expect.objectContaining({artifactId:replacement.artifactId})));
 expect(row('0')).not.toBeInTheDocument();expect(screen.queryByRole('heading',{name:'Authored sword 0',level:2})).not.toBeInTheDocument();
 await act(async()=>held.resolve({ok:false,error:{code:'LOCAL_READ_FAILED',message:'Replacement page failed.',retryable:true}}));
 const before=vi.mocked(bridge.itemsPage).mock.calls.length;fireEvent.click(screen.getByRole('button',{name:'Next page of Items'}));expect(bridge.itemsPage).toHaveBeenCalledTimes(before);
 expect(screen.getByText(/previous page and selection were cleared/)).toBeVisible();
});

it('accepts cached-fast preparation once without spawning another producer',async()=>{const{bridge}=setup(false,b=>{vi.mocked(b.itemsPrepare).mockResolvedValue(ok(snapshot));});await ready();expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1);expect(bridge.itemsPage).toHaveBeenCalledTimes(1);});

it('ignores an older held null readiness after the attached producer has published its ready page',async()=>{
 const held=deferred<Awaited<ReturnType<DiscoveryBridge['itemsData']>>>();const{bridge,pending}=setup(false,b=>vi.mocked(b.itemsData).mockReturnValueOnce(held.promise));
 fireEvent.click(screen.getByRole('button',{name:'Attach exact Items demand'}));await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1));
 await act(async()=>pending.resolve(ok(snapshot)));await ready();
 await act(async()=>held.resolve(ok({snapshot:null,dependencies:[{tableId:'50238ec2',state:'notPrepared',artifactId:null},{tableId:'919be54e',state:'notPrepared',artifactId:null}]})));
 expect(row('0')).toBeVisible();expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1);expect(bridge.itemsPage).toHaveBeenCalledTimes(1);
});

for(const withdraw of[false,true])it(`preserves a started old-build producer while ${withdraw?'withdrawing':'draining into'} the latest visible new-build intent`,async()=>{
 vi.spyOn(window,'scrollTo').mockImplementation(()=>{});const a=deferred<BridgeReply<ItemsSnapshot>>(),b=deferred<BridgeReply<ItemsSnapshot>>(),second=structuredClone(workspace);second.build.id='b'.repeat(64);const s2=itemsSnapshot({...snapshot.context,buildIdentity:second.build.id});
 const bridge={onProgress:()=>()=>{},cancel:vi.fn(async()=>ok({requested:true})),itemsData:vi.fn(async()=>ok({snapshot:null,dependencies:[{tableId:'50238ec2',state:'notPrepared',artifactId:null},{tableId:'919be54e',state:'notPrepared',artifactId:null}]})),itemsPrepare:vi.fn(p=>p.buildIdentity===snapshot.context.buildIdentity?a.promise:b.promise),itemsPage:vi.fn(async p=>ok(itemsPage(p.buildIdentity===s2.context.buildIdentity?s2:snapshot,p)))}as unknown as DiscoveryBridge;
 function Harness(){const flow=useItemsLoading(bridge),[view,setView]=useState<'a'|'discovery'|'b'>('a');return <><button onClick={()=>setView('discovery')}>Choose another build</button><button onClick={()=>setView('b')}>Enter second build</button><ItemsLoadBanner flow={flow}/>{view==='a'&&<ItemsBrowser workspace={workspace as never} bridge={bridge} active flow={flow}/>} {view==='b'&&<ItemsBrowser key="second" workspace={second as never} bridge={bridge} active flow={flow}/>}</>;}
 render(<Harness/>);await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1));fireEvent.click(screen.getByRole('button',{name:'Choose another build'}));fireEvent.click(screen.getByRole('button',{name:'Enter second build'}));
 await screen.findByRole('button',{name:'Stop waiting for Items'});expect(bridge.cancel).not.toHaveBeenCalled();if(withdraw)fireEvent.click(screen.getByRole('button',{name:'Stop waiting for Items'}));await act(async()=>a.resolve(ok(snapshot)));
 if(withdraw){await waitFor(()=>expect(within(document.querySelector('.iv')!).getByText('Items loading stopped')).toBeVisible());expect(bridge.itemsPrepare).toHaveBeenCalledTimes(1);expect(bridge.itemsPage).not.toHaveBeenCalled();}
 else{await waitFor(()=>expect(bridge.itemsPrepare).toHaveBeenCalledTimes(2));expect(bridge.itemsPrepare).toHaveBeenLastCalledWith(s2.context,expect.any(String));expect(bridge.itemsPage).not.toHaveBeenCalled();await act(async()=>b.resolve(ok(s2)));await findRow('0');expect(bridge.itemsPage).toHaveBeenLastCalledWith(expect.objectContaining({buildIdentity:s2.context.buildIdentity}));}
});

it('names rows by their visible identity and quality, and announces only concise applied results',async()=>{
 setup();await ready();
 await waitFor(()=>expect(row('9')).toHaveTextContent('Authored sword 9'));expect(row('8')).toHaveTextContent('Name unavailable · Invalid UTF-8');
 expect(row('9')).toHaveAccessibleName(/^Authored sword 9/);
 expect(applied()).toHaveAttribute('role','status');expect(applied()).toHaveTextContent('All admitted Items · ascending Item ID');
});

// D101 (#84): Items says which of its tables a fetched WoWDBDefs definition read, and a change of Fetch newer table
// definitions checks Items again as it checks Tables.
it('says which Items tables a fetched definition read and checks again when fetching is switched',async()=>{
 const commit='2'.repeat(40),fetched=structuredClone(snapshot);
 for(const source of[fetched.itemSource,fetched.sparseSource])Object.assign(source,{definitionSha256:'e'.repeat(64),providerRevision:commit,fetchedDefinition:{revision:commit,fetchedAt:'2026-10-25T00:00:00Z'},fetchedBecause:'exactBuild',bundledFallback:true});
 const{bridge}=setup(true,undefined,{source:fetched});await ready();
 const notice=screen.getByRole('region',{name:/^Item and ItemSparse · Definition from WoWDBDefs \(fetched .*, 2222222\)$/});
 expect(notice).toHaveTextContent(`The bundled Item and ItemSparse definitions don’t list build ${workspace.build.version}; the newest WoWDBDefs definitions do.`);
 expect(notice).toHaveTextContent('Turn off Fetch newer table definitions in Settings to read them with the bundled definitions only.');
 const data=vi.mocked(bridge.itemsData);
 data.mockImplementation(async()=>({ok:false,error:definitionErrors.itemsFetchingOff}));
 act(()=>{window.dispatchEvent(new CustomEvent('vibeminer-settings-changed',{detail:{name:'fetchTableDefinitions'}}));});
 await screen.findByText(/so Items can’t open on this build/);expect(row('0')).not.toBeInTheDocument();
 data.mockImplementation(async()=>ok({snapshot:fetched,dependencies:[{tableId:'50238ec2',state:'ready',artifactId:fetched.itemSource.artifactId},{tableId:'919be54e',state:'ready',artifactId:fetched.sparseSource.artifactId}]}));
 act(()=>{window.dispatchEvent(new CustomEvent('vibeminer-settings-changed',{detail:{name:'fetchTableDefinitions'}}));});
 await findRow('0');expect(bridge.itemsPrepare).not.toHaveBeenCalled();
});

it('shows no definition notice when the bundled definitions list the build',async()=>{setup();await ready();expect(document.querySelector('.feature-definition')).toBeNull();});

// Review fix (#84): with fetching off on a build only a fetched definition opens, the next step is the Settings switch,
// and Tables can't open Item either, so the Tables way out is not offered.
// The engine's own failures (contracts/examples/definition-errors.json, checked against the engine).
const fetchOff=definitionErrors.itemsFetchingOff;
it('offers Open Settings and no dead-end Tables link when Items stop because fetching is off',async()=>{
 const openSettings=vi.fn(),onOpenTable=vi.fn();
 setup(false,bridge=>{vi.mocked(bridge.itemsData).mockResolvedValue({ok:false,error:fetchOff});},{openSettings,onOpenTable});
 await screen.findByText(/so Items can’t open on this build/);
 expect(screen.queryByText(/can be read in Tables now/)).not.toBeInTheDocument();
 expect(screen.queryByRole('button',{name:'Open Item in Tables'})).not.toBeInTheDocument();
 expect(screen.queryByRole('button',{name:'Retry Items preparation'})).not.toBeInTheDocument();
 fireEvent.click(screen.getByRole('button',{name:'Open Settings'}));expect(openSettings).toHaveBeenCalledTimes(1);
});
// Regression (#84 review): with fetching on, ItemSparse can fail with words that never name it; Tables can still open Item.
it('keeps the Tables way out when only ItemSparse has no definition, and no Settings step for other failures',async()=>{
 const onOpenTable=vi.fn(),sparse=definitionErrors.itemSparseNotListed;expect(sparse.message).not.toContain('ItemSparse');
 setup(false,bridge=>{vi.mocked(bridge.itemsData).mockResolvedValue({ok:false,error:sparse});},{openSettings:vi.fn(),onOpenTable});
 await screen.findByText(/so Items can’t open on this build/);expect(screen.queryByRole('button',{name:'Open Settings'})).not.toBeInTheDocument();
 expect(screen.getAllByText(/can be read in Tables now/).length).toBeGreaterThan(0);
 fireEvent.click(screen.getByRole('button',{name:'Open Item in Tables'}));expect(onOpenTable).toHaveBeenCalledWith('50238ec2','Item');
 cleanup();
 // GitHub unreachable for Item: retry, no Settings step, and no Tables way out since Tables can't open Item either.
 setup(false,bridge=>{vi.mocked(bridge.itemsData).mockResolvedValue({ok:false,error:definitionErrors.itemFetchOffline});},{openSettings:vi.fn(),onOpenTable});
 await screen.findByText(/couldn’t reach WoWDBDefs/);expect(screen.queryByRole('button',{name:'Open Settings'})).not.toBeInTheDocument();
 expect(screen.queryByRole('button',{name:'Open Item in Tables'})).not.toBeInTheDocument();expect(screen.getByRole('button',{name:'Retry Items preparation'})).toBeVisible();
});
it('shows the one-line compact notice and names no selected item while Items prepares again',async()=>{
 const commit='2'.repeat(40),fetched=structuredClone(snapshot);
 for(const source of[fetched.itemSource,fetched.sparseSource])Object.assign(source,{definitionSha256:'e'.repeat(64),providerRevision:commit,fetchedDefinition:{revision:commit,fetchedAt:'2026-10-25T00:00:00Z'},fetchedBecause:'exactBuild',bundledFallback:true});
 const{bridge}=setup(true,undefined,{source:fetched,compact:true});await findRow('0');
 const notice=screen.getByRole('region',{name:/^WoWDBDefs · .* · 2222222$/});
 expect(notice).toHaveTextContent(`Only WoWDBDefs lists build ${workspace.build.version}.`);
 expect(notice.querySelector('.feature-definition-short')).toHaveAttribute('title',expect.stringContaining('CC BY-SA 4.0.'));
 await choose('3');expect(document.querySelector('.iv-switch-name')).toHaveTextContent('Authored sword 3');
 vi.mocked(bridge.itemsData).mockResolvedValue({ok:false,error:fetchOff});
 act(()=>{window.dispatchEvent(new CustomEvent('vibeminer-settings-changed',{detail:{name:'fetchTableDefinitions'}}));});
 await screen.findByText(/so Items can’t open on this build/);expect(document.querySelector('.iv-switch-name')).toBeNull();
});
// #84 QA: Item and ItemSparse fetched 24 s apart from one WoWDBDefs commit are one source, so 200% text still gets the
// one compact line (with the earliest fetch), and the full heading names the commit once. Different commits stay apart.
it('treats tables fetched at different times from one commit as one source',async()=>{
 const commit='835e08f'+'0'.repeat(33),fetched=structuredClone(snapshot),at=(source:typeof fetched.itemSource,fetchedAt:string,revision=commit)=>Object.assign(source,{definitionSha256:'e'.repeat(64),providerRevision:revision,fetchedDefinition:{revision,fetchedAt},fetchedBecause:'exactBuild',bundledFallback:true});
 at(fetched.itemSource,'2026-10-28T00:46:01Z');at(fetched.sparseSource,'2026-10-28T00:45:37Z');
 setup(true,undefined,{source:fetched,compact:true});await findRow('0');
 const date=new Date('2026-10-28T00:45:37Z').toLocaleDateString(undefined,{dateStyle:'medium'});
 expect(screen.getByRole('region',{name:`WoWDBDefs · ${date} · 835e08f`})).toHaveTextContent(`Only WoWDBDefs lists build ${workspace.build.version}.`);
 cleanup();
 setup(true,undefined,{source:fetched});await findRow('0');
 expect(screen.getByRole('region',{name:/^Item and ItemSparse · Definition from WoWDBDefs \(fetched .*, 835e08f\)$/})).toBeInTheDocument();
 cleanup();
 at(fetched.itemSource,'2026-10-28T00:46:01Z','3'.repeat(40));
 setup(true,undefined,{source:fetched});await findRow('0');
 expect(screen.getByRole('region',{name:/^Item · Definition from WoWDBDefs \(fetched .*, 3333333\); ItemSparse · Definition from WoWDBDefs \(fetched .*, 835e08f\)$/})).toBeInTheDocument();
});
