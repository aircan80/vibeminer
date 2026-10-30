import {useState} from 'react';
import {act,cleanup,fireEvent,render,screen,waitFor,within} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,beforeEach,it,expect,vi} from 'vitest';
import type {DiscoveryBridge,BridgeReply} from '../src/shared/bridge';
import type {CreaturesSnapshot,TableReferenceTextPage} from '../../../contracts/generated/discovery';
import {creaturesSnapshot,creaturesPage,creatureInspect,creatureRow} from '../../../tests/desktop/fixtures/creatures.mjs';
import workspace from '../../../tests/desktop/fixtures/download-workspace.json';
import labels from '../../../contracts/examples/creature-labels.json';
import definitionErrorsJson from '../../../contracts/examples/definition-errors.json';
import type {EngineError} from '../../../contracts/generated/discovery';
import {CreaturesBrowser} from '../src/renderer/CreaturesBrowser';
import {useCreaturesLoading,CreaturesLoadBanner} from '../src/renderer/useCreaturesLoading';
import {SettingsProvider} from '../src/renderer/Settings';
import type {TableLoading} from '../src/renderer/useTableLoading';
// Creatures view (docs/design/creatures-view, #62): the list, the selected creature's record and its source links.
const definitionErrors=definitionErrorsJson as Record<keyof typeof definitionErrorsJson,EngineError>;
const ok=<T,>(value:T):BridgeReply<T>=>({ok:true,value}),deferred=<T,>()=>{let resolve!:(value:T)=>void;return{promise:new Promise<T>(r=>resolve=r),resolve:(value:T)=>resolve(value)};};
const context={productId:workspace.build.productId,buildIdentity:workspace.build.id,sourceRegion:workspace.sourceReference.region,researchLocale:'enUS' as const},s=creaturesSnapshot(context);
// Reference text for the one selected Creature row: CreatureType 1 and CreatureFamily 2 have authored text, every other value has none.
function referenceText(p:{recordId?:string;artifactId:string}):TableReferenceTextPage{
 const row=creatureRow(s,p.recordId!)!,columns:TableReferenceTextPage['columns']=[],values:TableReferenceTextPage['values']=[];
 s.creatureSource.columns.forEach((c,columnIndex)=>{if(!c.reference)return;const type=c.reference.table==='CreatureType',tableId=type?'1f80ad3f':'369f469f';
  columns.push({columnIndex,name:c.name,reference:c.reference,state:'ready',textSource:{tableId,table:c.reference.table,textColumn:'Name_lang',matchColumn:'ID',artifactId:'a'.repeat(64),scope:'complete',measuredEncodedBytes:'4096',loadPolicy:'automatic'},problem:null});
  const value=row.values[columnIndex],text=type?{'1':'Authored type A'}[value]:{'2':'Authored family B'}[value];
  values.push(text?{columnIndex,value,state:'resolved',text,matchCount:'1',row:{id:value,isCopy:false,copySourceId:null}}:{columnIndex,value,state:'notFound',text:null,matchCount:'0',row:null});});
 return{productId:context.productId,buildIdentity:context.buildIdentity,sourceRegion:context.sourceRegion,tableId:'c9d6b6b3',researchLocale:'enUS',artifactId:p.artifactId,after:'0',recordId:p.recordId??null,policyRevision:'table-reference-text-v1',rowIds:[p.recordId!],columns,values} as TableReferenceTextPage;
}
const tableFlow={pending:false,operation:null,blocked:()=>null,load:()=>null,retry:()=>{},latchRevision:0} as unknown as TableLoading;
function setup({ready=true,onSource=vi.fn<(...a:unknown[])=>void>()}:{ready?:boolean;onSource?:(...a:unknown[])=>void}={}){
 const held=deferred<BridgeReply<CreaturesSnapshot>>();
 const bridge={onProgress:()=>()=>{},creaturesData:vi.fn(async()=>ok({snapshot:ready?s:null,dependencyState:ready?'ready':'notPrepared',sourceArtifactId:ready?s.creatureSource.artifactId:null})),creaturesPrepare:vi.fn(()=>held.promise),creaturesPage:vi.fn(async p=>ok(creaturesPage(s,p))),creatureInspect:vi.fn(async p=>ok(creatureInspect(s,p))),cancel:vi.fn(async()=>ok({requested:true})),creatureLabels:vi.fn(async()=>ok(labels)),tableReferenceText:vi.fn(async p=>ok(referenceText(p))),tableDefinition:vi.fn(),tablePrepare:vi.fn(),itemsPrepare:vi.fn()} as unknown as DiscoveryBridge;
 function Harness(){const flow=useCreaturesLoading(bridge),[area,setArea]=useState(true),[mounted,setMounted]=useState(true);return <><button onClick={()=>setArea(x=>!x)}>Switch area</button><button onClick={()=>setMounted(x=>!x)}>Choose another build</button>{(!area||!mounted)&&<CreaturesLoadBanner flow={flow} session/>}{mounted&&<CreaturesBrowser workspace={workspace as never} bridge={bridge} active={area} flow={flow} factsAvailable tableLoading={tableFlow} onSource={onSource}/>}</>;}
 render(<Harness/>);return{bridge,held,onSource};
}
const option=(name:RegExp|string)=>screen.findByRole('option',{name});
beforeEach(()=>{vi.spyOn(window,'scrollTo').mockImplementation(()=>{});Element.prototype.scrollIntoView=vi.fn();});afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('lists admitted creatures and shows the selected record with labels, Type and Family text and source links, without preparing',async()=>{
 const{bridge,onSource}=setup();
 const row=await option(/^Authored creature 0/);expect(row).toHaveTextContent('0 · Type 1');expect(row).toHaveTextContent('2');
 expect(screen.getByRole('listbox',{name:'Creatures on this page'})).toBeVisible();expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
 fireEvent.click(row);
 const heading=await screen.findByRole('heading',{level:2,name:'Authored creature 0'});
 const head=heading.closest('.iv-dhead') as HTMLElement;expect(head).toHaveTextContent('Creature 0');expect(head).toHaveTextContent('Authored title 0');expect(head).toHaveTextContent('Normal');
 const creature=screen.getByRole('region',{name:'Creature'});
 await waitFor(()=>expect(creature).toHaveTextContent('1 · Authored type A'));expect(creature).toHaveTextContent('2 · Authored family B');
 expect(creature).toHaveTextContent('0 · Normal');expect(creature).toHaveTextContent('-1 · raw');
 expect(within(creature).getAllByText('Empty string').length).toBeGreaterThan(0);
 // One Tab stop for the section; the link opens the exact Creature cell in Tables.
 const links=within(creature).getAllByRole('button').filter(b=>b.classList.contains('iv-src'));expect(links.filter(l=>l.tabIndex===0)).toHaveLength(1);
 fireEvent.click(within(creature).getByRole('button',{name:/^Creature\.CreatureType/}));
 expect(onSource).toHaveBeenCalledWith(s.creatureSource,'0',s.fields.facts[1].columnIndex);
 fireEvent.click(screen.getByRole('button',{name:'Creature #0'}));expect(onSource).toHaveBeenLastCalledWith(s.creatureSource,'0',undefined);
 expect(bridge.tableReferenceText).toHaveBeenCalledWith(expect.objectContaining({tableId:'c9d6b6b3',recordId:'0',limit:1}));
 expect(bridge.tablePrepare).not.toHaveBeenCalled();
});

it('says when a CreatureType row is missing and keeps the number',async()=>{
 setup();fireEvent.click(await option(/^Authored creature 7/));
 const creature=await screen.findByRole('region',{name:'Creature'});
 await waitFor(()=>expect(creature).toHaveTextContent('99 · not found in CreatureType'));
 expect(screen.getByRole('heading',{level:2,name:'Authored creature 7'}).closest('.iv-dhead')).toHaveTextContent('Type 99');
});

it('names empty and invalid source names in words, in the list and the detail',async()=>{
 setup();
 const empty=await option(/^Empty source name/);expect(empty).toHaveTextContent('Name_lang is an empty string');
 const invalid=await option(/^Name unavailable · invalid source text/);expect(invalid).toHaveTextContent('aren’t valid UTF-8');
 fireEvent.click(invalid);
 const creature=await screen.findByRole('region',{name:'Creature'});
 await waitFor(()=>expect(creature).toHaveTextContent('Invalid text'));
});

it('searches by exact ID, refuses a non-canonical one without losing results, and says when nothing matches',async()=>{
 const{bridge}=setup();await option(/^Authored creature 0/);
 fireEvent.click(screen.getByRole('button',{name:'Creature ID'}));
 const input=screen.getByRole('searchbox',{name:'Exact Creature ID'});fireEvent.change(input,{target:{value:'01'}});fireEvent.click(screen.getByRole('button',{name:'Search'}));
 expect(input).toHaveAttribute('aria-invalid','true');expect(screen.getByRole('alert')).toHaveTextContent('Enter a whole Creature ID');expect(bridge.creaturesPage).toHaveBeenCalledTimes(1);
 expect(screen.getByRole('option',{name:/^Authored creature 0/})).toBeVisible();
 fireEvent.change(input,{target:{value:'6'}});fireEvent.click(screen.getByRole('button',{name:'Search'}));
 await waitFor(()=>expect(screen.getAllByRole('option')).toHaveLength(1));expect(screen.getByText(/· 1 on this page/)).toHaveTextContent('Exact Creature ID 6');
 fireEvent.click(screen.getByRole('button',{name:'Name'}));const name=screen.getByRole('searchbox',{name:'Name contains'});
 fireEvent.change(name,{target:{value:'zz'}});fireEvent.click(screen.getByRole('button',{name:'Search'}));
 expect(await screen.findByText(/No admitted creature name contains “zz”/)).toBeVisible();
 fireEvent.click(screen.getAllByRole('button',{name:'Clear search'}).at(-1)!);await option(/^Authored creature 0/);
});

it('moves the selection with the arrow keys and keeps the one listbox stop',async()=>{
 const{bridge}=setup();await option(/^Authored creature 0/);const box=screen.getByRole('listbox');
 box.focus();fireEvent.keyDown(box,{key:'ArrowDown'});
 await screen.findByRole('heading',{level:2,name:'Authored creature 0'});
 fireEvent.keyDown(box,{key:'ArrowDown'});await screen.findByRole('heading',{level:2,name:'Empty source name'});
 expect(box).toHaveFocus();expect(box).toHaveAttribute('aria-activedescendant','cv-opt-1');
 expect(bridge.creatureInspect).toHaveBeenCalledTimes(2);
});

it('prepares the Creature table when it is not ready, offers Stop, and lists once the admission arrives',async()=>{
 const{bridge,held}=setup({ready:false});
 expect(await screen.findByText('Preparing Creatures…')).toBeVisible();
 await waitFor(()=>expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1));
 expect(screen.getByRole('searchbox',{name:'Name contains'})).toBeDisabled();
 expect(screen.getByRole('button',{name:'Stop preparing Creatures'})).toBeVisible();
 await act(async()=>{held.resolve(ok(s));});
 await option(/^Authored creature 0/);expect(screen.getByRole('searchbox',{name:'Name contains'})).toBeEnabled();
});

// Restored from the earlier Creatures view (#62 review): the loading, cache and fencing rules didn't change with the layout.
const next=()=>fireEvent.click(screen.getByRole('button',{name:'Next page of Creatures'}));
it('preserves query, page and selection through areas and the App-owned context cache remount',async()=>{
 setup();fireEvent.click(await option(/^Authored creature 0/));await screen.findByRole('heading',{level:2,name:'Authored creature 0'});
 next();await option(/^Authored creature 50/);expect(screen.getByText(/Selected creature 0 is outside these results/)).toBeVisible();
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));fireEvent.click(screen.getByRole('button',{name:'Switch area'}));expect(screen.getByRole('option',{name:/^Authored creature 50/})).toBeVisible();
 fireEvent.click(screen.getByRole('button',{name:'Choose another build'}));fireEvent.click(screen.getByRole('button',{name:'Choose another build'}));
 await option(/^Authored creature 50/);expect(screen.getByRole('heading',{level:2,name:'Authored creature 0'})).toBeVisible();
});
it('preserves a started core consumer across navigation and rejects a late success after explicit Stop',async()=>{
 const{bridge,held}=setup({ready:false});await waitFor(()=>expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1));
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));expect(bridge.cancel).not.toHaveBeenCalled();
 const stop=screen.getByRole('button',{name:'Stop loading Creatures'});stop.focus();fireEvent.click(stop);expect(screen.getByRole('button',{name:'Stopping Creatures…'})).toHaveFocus();
 await act(async()=>held.resolve(ok(s)));await waitFor(()=>expect(screen.getByRole('region',{name:'Background Creatures load'}).querySelector('[tabindex]')).toHaveFocus());
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));expect(await screen.findByText('Creatures loading stopped')).toBeVisible();
 expect(screen.queryByRole('option')).not.toBeInTheDocument();expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1);expect(screen.getByRole('button',{name:'Retry Creatures preparation'})).toBeVisible();
});
it('keeps old rows after a page failure and retries that query without stealing deliberately moved focus',async()=>{
 const{bridge}=setup();await option(/^Authored creature 0/);vi.mocked(bridge.creaturesPage!).mockResolvedValueOnce({ok:false,error:{code:'OFFLINE_LOCAL_TEST',message:'Authored read failure.',retryable:true}});
 next();await screen.findByText('OFFLINE_LOCAL_TEST');expect(screen.getByRole('option',{name:/^Authored creature 0/})).toBeVisible();
 const hold=deferred<BridgeReply<ReturnType<typeof creaturesPage>>>();vi.mocked(bridge.creaturesPage!).mockReturnValueOnce(hold.promise);
 const retry=screen.getByRole('button',{name:'Retry Creature page'});retry.focus();fireEvent.click(retry);const mode=screen.getByRole('button',{name:'Creature ID'});mode.focus();
 await act(async()=>hold.resolve(ok(creaturesPage(s,{...context,artifactId:s.artifactId,limit:50,after:'c50'}))));await option(/^Authored creature 50/);
 expect(mode).toHaveFocus();expect(vi.mocked(bridge.creaturesPage!).mock.calls.at(-1)![0]).toMatchObject({after:'c50'});
});
it('keeps the page Retry focused through pending work and another failure',async()=>{
 const{bridge}=setup();await option(/^Authored creature 0/);const problem={code:'LOCAL_READ_FAILED',message:'Authored repeated local read failure.',retryable:true};
 vi.mocked(bridge.creaturesPage!).mockResolvedValueOnce({ok:false,error:problem});next();
 const retry=await screen.findByRole('button',{name:'Retry Creature page'}),held=deferred<BridgeReply<any>>();vi.mocked(bridge.creaturesPage!).mockReturnValueOnce(held.promise);
 retry.focus();fireEvent.click(retry);expect(retry).toHaveFocus();expect(retry).toHaveAttribute('aria-disabled','true');fireEvent.click(retry);expect(bridge.creaturesPage).toHaveBeenCalledTimes(3);
 await act(async()=>held.resolve({ok:false,error:problem}));expect(screen.getByRole('button',{name:'Retry Creature page'})).toBe(retry);expect(retry).toHaveFocus();expect(retry).toHaveAttribute('aria-disabled','false');
 expect(screen.getByRole('option',{name:/^Authored creature 0/})).toBeVisible();
});
it('fences an older null readiness reply after a cached-fast producer has already admitted rows',async()=>{
 const read=deferred<BridgeReply<any>>(),prepare=deferred<BridgeReply<CreaturesSnapshot>>();
 const bridge={onProgress:()=>()=>{},creaturesData:vi.fn(()=>read.promise),creaturesPrepare:vi.fn(()=>prepare.promise),creaturesPage:vi.fn(async p=>ok(creaturesPage(s,p))),creatureInspect:vi.fn(async p=>ok(creatureInspect(s,p))),cancel:vi.fn()} as unknown as DiscoveryBridge;
 function Harness(){const flow=useCreaturesLoading(bridge);return <><button onClick={()=>flow.load({context,buildVersion:workspace.build.version})}>Existing session producer</button><CreaturesBrowser workspace={workspace as never} bridge={bridge} active flow={flow}/></>;}
 render(<Harness/>);await waitFor(()=>expect(bridge.creaturesData).toHaveBeenCalledTimes(1));
 fireEvent.click(screen.getByRole('button',{name:'Existing session producer'}));await act(async()=>prepare.resolve(ok(s)));await option(/^Authored creature 0/);
 await act(async()=>read.resolve(ok({snapshot:null,dependencyState:'notPrepared',sourceArtifactId:null})));
 expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1);expect(screen.getByRole('option',{name:/^Authored creature 0/})).toBeVisible();
});
it('keeps at most eight context views and evicts old search state without accumulating browsers',async()=>{
 const bridge={onProgress:()=>()=>{},creaturesData:vi.fn(async c=>{const value=creaturesSnapshot(c);return ok({snapshot:value,dependencyState:'ready',sourceArtifactId:value.creatureSource.artifactId});}),creaturesPage:vi.fn(async p=>ok(creaturesPage(creaturesSnapshot(p),p))),creatureInspect:vi.fn(async p=>ok(creatureInspect(creaturesSnapshot(p),p))),creaturesPrepare:vi.fn(),cancel:vi.fn()} as unknown as DiscoveryBridge;
 function Harness(){const flow=useCreaturesLoading(bridge),[index,setIndex]=useState(0),build={...workspace,build:{...workspace.build,id:index.toString(16).repeat(64)}};return <><button onClick={()=>setIndex(n=>n+1)}>Next context</button><button onClick={()=>setIndex(0)}>First context</button><CreaturesBrowser key={index} workspace={build as never} bridge={bridge} active flow={flow}/></>;}
 render(<Harness/>);await option(/^Authored creature 0/);
 fireEvent.click(screen.getByRole('button',{name:'Creature ID'}));fireEvent.change(screen.getByRole('searchbox',{name:'Exact Creature ID'}),{target:{value:'3'}});fireEvent.click(screen.getByRole('button',{name:'Search'}));
 await waitFor(()=>expect(screen.getAllByRole('option')).toHaveLength(1));
 // A context still in the cache keeps its search.
 fireEvent.click(screen.getByRole('button',{name:'Next context'}));await waitFor(()=>expect(document.querySelectorAll('.cv-option')).toHaveLength(50));
 fireEvent.click(screen.getByRole('button',{name:'First context'}));await waitFor(()=>expect(screen.getAllByRole('option')).toHaveLength(1));expect(screen.getByRole('searchbox',{name:'Exact Creature ID'})).toHaveValue('3');
 for(let i=1;i<=8;i++){fireEvent.click(screen.getByRole('button',{name:'Next context'}));await waitFor(()=>expect(document.querySelectorAll('.cv-option')).toHaveLength(50));expect(document.querySelectorAll('.creatures-browser')).toHaveLength(1);}
 fireEvent.click(screen.getByRole('button',{name:'First context'}));await waitFor(()=>expect(document.querySelectorAll('.cv-option')).toHaveLength(50));
 expect(screen.getByRole('button',{name:'Name'})).toHaveAttribute('aria-pressed','true');expect(screen.queryByRole('searchbox',{name:'Exact Creature ID'})).not.toBeInTheDocument();expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
});
it('does not let an older inspected record replace a newer deliberate selection',async()=>{
 const{bridge}=setup();await option(/^Authored creature 0/);const held=deferred<BridgeReply<any>>();vi.mocked(bridge.creatureInspect!).mockReturnValueOnce(held.promise);
 fireEvent.click(screen.getByRole('option',{name:/^Authored creature 0/}));fireEvent.click(screen.getByRole('option',{name:/^Name unavailable/}));
 const creature=await screen.findByRole('region',{name:'Creature'});await waitFor(()=>expect(creature).toHaveTextContent('Invalid text'));
 await act(async()=>held.resolve(ok(creatureInspect(s,{creatureId:'0'}))));
 expect(screen.getByRole('heading',{level:2,name:'Name unavailable · invalid source text'})).toBeVisible();expect(screen.queryByRole('heading',{level:2,name:'Authored creature 0'})).not.toBeInTheDocument();
});
it('prepares once when a null local read is followed by an immediate successful admission',async()=>{
 const{bridge,held}=setup({ready:false});vi.mocked(bridge.creaturesPrepare!).mockResolvedValueOnce(ok(s)).mockReturnValue(held.promise);
 await option(/^Authored creature 0/);expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1);
});
it('keeps prior cached rows useful and offers a deliberate Retry after a current refresh stops',async()=>{
 const{bridge,held}=setup();await option(/^Authored creature 0/);fireEvent.click(screen.getByRole('button',{name:'Choose another build'}));
 vi.mocked(bridge.creaturesData!).mockResolvedValueOnce(ok({snapshot:null,dependencyState:'notPrepared',sourceArtifactId:null}));
 fireEvent.click(screen.getByRole('button',{name:'Choose another build'}));await waitFor(()=>expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1));
 expect(screen.getByRole('option',{name:/^Authored creature 0/})).toBeVisible();expect(screen.getByRole('searchbox',{name:'Name contains'})).toBeDisabled();
 fireEvent.click(screen.getByRole('button',{name:'Stop preparing Creatures'}));await act(async()=>held.resolve({ok:false,error:{code:'CANCELED',message:'Target cleanup completed.',retryable:true}}));
 const retry=await screen.findByRole('button',{name:'Retry Creatures preparation'});expect(screen.getByRole('option',{name:/^Authored creature 0/})).toBeVisible();expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1);
 fireEvent.click(retry);await waitFor(()=>expect(screen.queryByText('Creatures loading stopped')).not.toBeInTheDocument());expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1);
});
it('keeps cached rows and a withdrawable waiting intent while another build finishes',async()=>{
 const other={...context,buildIdentity:'b'.repeat(64)},otherSnapshot=creaturesSnapshot(other),held=deferred<BridgeReply<CreaturesSnapshot>>();let initial=true;
 const bridge={onProgress:()=>()=>{},creaturesData:vi.fn(async c=>{const ready=c.buildIdentity===context.buildIdentity&&initial;initial=false;return ok({snapshot:ready?s:null,dependencyState:ready?'ready':'notPrepared',sourceArtifactId:ready?s.creatureSource.artifactId:null});}),creaturesPrepare:vi.fn(()=>held.promise),creaturesPage:vi.fn(async p=>ok(creaturesPage(p.buildIdentity===context.buildIdentity?s:otherSnapshot,p))),creatureInspect:vi.fn(),cancel:vi.fn()} as unknown as DiscoveryBridge;
 function Harness(){const flow=useCreaturesLoading(bridge),[second,setSecond]=useState(false),build=second?{...workspace,build:{...workspace.build,id:other.buildIdentity}}:workspace;return <><button onClick={()=>setSecond(x=>!x)}>Change Creature context</button><CreaturesBrowser key={build.build.id} workspace={build as never} bridge={bridge} active flow={flow}/></>;}
 render(<Harness/>);await option(/^Authored creature 0/);fireEvent.click(screen.getByRole('button',{name:'Change Creature context'}));await waitFor(()=>expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1));
 fireEvent.click(screen.getByRole('button',{name:'Change Creature context'}));const stop=await screen.findByRole('button',{name:'Stop waiting for Creatures'});expect(screen.getByRole('option',{name:/^Authored creature 0/})).toBeVisible();
 stop.focus();fireEvent.click(stop);expect(screen.getByRole('heading',{level:1,name:'Creatures'})).toHaveFocus();expect(screen.getByRole('button',{name:'Retry Creatures preparation'})).toBeVisible();
 await act(async()=>held.resolve(ok(otherSnapshot)));expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1);expect(bridge.cancel).not.toHaveBeenCalled();expect(screen.getByRole('button',{name:'Retry Creatures preparation'})).toBeVisible();
});
it('names whitespace-only names in words and keeps the exact text in the Name fact',async()=>{
 const{bridge}=setup();const whitespace='\n\n\n\n   ';
 vi.mocked(bridge.creaturesPage!).mockImplementation(async p=>{const page=creaturesPage(s,{...p});for(const row of page.entries)if(row.id==='4')row.name.value=whitespace;return ok(page);});
 vi.mocked(bridge.creatureInspect!).mockImplementation(async p=>{const value=creatureInspect(s,p);if(p.creatureId==='4'){value.detail!.summary.name.value=whitespace;value.detail!.creatureRow.values[s.fields.name.columnIndex]=whitespace;}return ok(value);});
 fireEvent.click(await option(/^Whitespace-only source name/));await screen.findByRole('heading',{level:2,name:'Whitespace-only source name'});
 const creature=screen.getByRole('region',{name:'Creature'});await waitFor(()=>expect(creature).toHaveTextContent('Only spaces or line breaks'));expect(creature.querySelector(".cv-exact")!.textContent).toBe(JSON.stringify(whitespace));
 fireEvent.click(screen.getByRole('option',{name:/^Authored long client name/}));const long=creatureInspect(s,{creatureId:'5'}).detail!.summary.name.value!;
 await waitFor(()=>expect(screen.getByRole('region',{name:'Creature'})).toHaveTextContent(long.trim()));
});

it('says when a fetched definition read the Creature table and checks again when fetching is switched, even from another area',async()=>{
 const commit='2'.repeat(40),fetched=structuredClone(s);
 Object.assign(fetched.creatureSource,{definitionSha256:'e'.repeat(64),providerRevision:commit,fetchedDefinition:{revision:commit,fetchedAt:'2026-10-25T00:00:00Z'},fetchedBecause:'layoutMatch',schemaMatch:'layoutOnly',bundledFallback:false});
 const{bridge}=setup();const data=vi.mocked(bridge.creaturesData!);data.mockImplementation(async()=>ok({snapshot:fetched,dependencyState:'ready',sourceArtifactId:fetched.creatureSource.artifactId}));
 fireEvent.click(screen.getByRole('button',{name:'Choose another build'}));fireEvent.click(screen.getByRole('button',{name:'Choose another build'}));
 const notice=await screen.findByRole('region',{name:/Creature · Definition from WoWDBDefs/});
 expect(notice).toHaveTextContent(`only the WoWDBDefs block matches its layout ${fetched.creatureSource.layoutHash}.`);
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));const calls=data.mock.calls.length;
 data.mockImplementation(async()=>({ok:false,error:definitionErrors.creaturesFetchingOff}));
 act(()=>{window.dispatchEvent(new CustomEvent('vibeminer-settings-changed',{detail:{name:'fetchTableDefinitions'}}));});
 expect(data.mock.calls.length).toBe(calls);
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));
 expect(await screen.findByText('Creatures need attention')).toBeVisible();
 data.mockImplementation(async()=>ok({snapshot:fetched,dependencyState:'ready',sourceArtifactId:fetched.creatureSource.artifactId}));
 act(()=>{window.dispatchEvent(new CustomEvent('vibeminer-settings-changed',{detail:{name:'fetchTableDefinitions'}}));});
 await waitFor(()=>expect(screen.queryByText('Creatures need attention')).not.toBeInTheDocument());
 expect(await option(/^Authored creature 0/)).toBeVisible();expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
});

it('offers Open Settings when Creatures stop because fetching is off',async()=>{
 const openHere=vi.fn(),error=definitionErrors.creaturesFetchingOff;
 const bridge={onProgress:()=>()=>{},creaturesData:vi.fn(async()=>({ok:false,error})),creaturesPrepare:vi.fn(),cancel:vi.fn()} as unknown as DiscoveryBridge;
 function Harness(){const flow=useCreaturesLoading(bridge);return <SettingsProvider value={{open:()=>{},register:()=>()=>{},openHere}}><CreaturesBrowser workspace={workspace as never} bridge={bridge} active flow={flow}/></SettingsProvider>;}
 render(<Harness/>);
 await screen.findByText('Creatures need attention');
 expect(screen.queryByRole('button',{name:'Retry Creatures preparation'})).not.toBeInTheDocument();
 // When no definition can read Creature, Tables can't open it either, so the way out isn't offered.
 expect(screen.queryByRole('button',{name:'Open Creature in Tables'})).not.toBeInTheDocument();
 fireEvent.click(screen.getByRole('button',{name:'Open Settings'}));expect(openHere).toHaveBeenCalledTimes(1);expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
});
