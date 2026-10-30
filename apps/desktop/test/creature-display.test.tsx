import {useState} from 'react';
import {act,cleanup,fireEvent,render,screen,waitFor,within} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {it,expect,vi,afterEach,beforeEach} from 'vitest';
import type {DiscoveryBridge,BridgeReply} from '../src/shared/bridge';
import type {CreatureDisplayMetadataResult} from '../../../contracts/generated/discovery';
import workspace from '../../../tests/desktop/fixtures/download-workspace.json';
import {creaturesSnapshot,creaturesPage,creatureInspect,creatureSummary} from '../../../tests/desktop/fixtures/creatures.mjs';
import {creatureDisplay,creatureDisplayFacts} from '../../../tests/desktop/fixtures/creature-display.mjs';
import {useCreaturesLoading} from '../src/renderer/useCreaturesLoading';
import {useCreatureDisplay} from '../src/renderer/useCreatureDisplay';
import {CreatureDisplayActivity} from '../src/renderer/CreatureDisplayViews';
import {CreaturesBrowser} from '../src/renderer/CreaturesBrowser';
// Displays in the Creatures view (#62): every declared slot reads its chain locally; the first unprepared slot prepares the
// shared tables. The useCreatureDisplay and useCreaturesLoading rules (credits, fencing, Stop, View) are the earlier view's.
const ok=<T,>(value:T):BridgeReply<T>=>({ok:true,value}),deferred=<T,>()=>{let resolve!:(v:T)=>void;return{promise:new Promise<T>(r=>resolve=r),resolve:(v:T)=>resolve(v)};};
const context={productId:workspace.build.productId,buildIdentity:workspace.build.id,sourceRegion:workspace.sourceReference.region,researchLocale:'enUS' as const},core=creaturesSnapshot(context),opening=workspace.sourceReference;
type State=CreatureDisplayMetadataResult['state'];
type Params={creatureId:string;slot:number;buildIdentity:string}&Record<string,unknown>;
function setup({ready=false,state='metadataResolved',facts=true,onFile=vi.fn<(t:unknown)=>void>(),onSource=vi.fn<(...a:unknown[])=>void>(),mutate}:{ready?:boolean;state?:State;facts?:boolean;onFile?:(t:unknown)=>void;onSource?:(...a:unknown[])=>void;mutate?:(v:CreatureDisplayMetadataResult)=>void}={}){
 // Display tables are shared by every slot of one build: once prepared, every chain reads them.
 const tables=new Set<string>(ready?[context.buildIdentity]:[]);
 const chain=(p:Params,forced?:State)=>{const c=creaturesSnapshot({...p} as never),v=creatureDisplay(c,creatureSummary(c,p.creatureId)!,p.slot,opening,forced??(tables.has(p.buildIdentity)?state:'notPrepared'));mutate?.(v);return v;};
 const bridge={onProgress:()=>()=>{},creaturesData:vi.fn(async c=>{const source=creaturesSnapshot({...c});return ok({snapshot:source,dependencyState:'ready',sourceArtifactId:source.creatureSource.artifactId});}),creaturesPrepare:vi.fn(),creaturesPage:vi.fn(async p=>ok(creaturesPage(creaturesSnapshot({...p}),{...p}))),creatureInspect:vi.fn(async p=>ok(creatureInspect(creaturesSnapshot({...p}),p))),
  creatureDisplayRead:vi.fn(async (p:Params)=>ok(chain(p))),creatureDisplayFacts:facts?vi.fn(async (p:Params)=>ok(creatureDisplayFacts(chain(p)))):undefined,
  creatureDisplayPrepare:vi.fn(async (p:Params)=>{tables.add(p.buildIdentity);return ok(chain(p));}),cancel:vi.fn(async()=>ok({requested:true}))} as unknown as DiscoveryBridge;
 function Harness(){const c=useCreaturesLoading(bridge),d=useCreatureDisplay(bridge,c),[active,setActive]=useState(true),[second,setSecond]=useState(false),build=second?{...workspace,build:{...workspace.build,id:'a'.repeat(64)}}:workspace;
  return <><h1 tabIndex={-1}>Build home</h1><button onClick={()=>setSecond(x=>!x)}>Explore another build</button><button onClick={()=>setActive(x=>!x)}>Switch area</button><CreatureDisplayActivity flow={d} snapshot={build as never} activeKey={active?d.visibleKey:null} onView={()=>setActive(true)}/><CreaturesBrowser key={build.build.id} workspace={build as never} bridge={bridge} flow={c} displays={d} displayAvailable factsAvailable={facts} active={active} onFile={onFile} onSource={onSource}/></>;}
 render(<Harness/>);
 // A held Prepare resolves as the engine would: the tables are then prepared for every slot of that build.
 const resolved=(p:{buildIdentity?:string}={},slot=0,id='0')=>{tables.add(p.buildIdentity??context.buildIdentity);return ok(creatureDisplay(core,creatureSummary(core,id)!,slot,opening));};
 return{bridge,onFile,onSource,tables,resolved};
}
async function choose(id='0'){fireEvent.click(await screen.findByRole('option',{name:new RegExp(`^Authored creature ${id}${id} ·`)}));await screen.findByRole('heading',{level:2,name:`Authored creature ${id}`});return screen.getByRole('region',{name:'Displays'});}
const card=(slot:number)=>screen.getByRole('article',{name:new RegExp(`^Slot ${slot} · Display`)});
const displays=()=>screen.getByRole('region',{name:'Displays'});
const status=()=>displays().querySelector<HTMLElement>('.cv-status')!;
beforeEach(()=>{vi.spyOn(window,'scrollTo').mockImplementation(()=>{});Element.prototype.scrollIntoView=vi.fn();});afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('prepares the display tables once for the first slot, then shows every declared slot with its facts and keeps all four slots',async()=>{
 const{bridge}=setup();const hold=deferred<void>(),prepare=vi.mocked(bridge.creatureDisplayPrepare!),original=prepare.getMockImplementation()!;prepare.mockImplementation(async (p,id)=>{await hold.promise;return original(p,id);});
 const region=await choose();
 expect(await within(region).findByText('Loading displays…')).toBeVisible();await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));
 expect(vi.mocked(bridge.creatureDisplayPrepare!).mock.calls[0]![0]).toMatchObject({creatureId:'0',slot:0});
 // One wording per block while it loads.
 expect(within(card(0)).getByText('CreatureDisplayInfo loading')).toBeVisible();expect(within(card(0)).getAllByText('loads after CreatureDisplayInfo')).toHaveLength(2);expect(card(0)).not.toHaveTextContent('CreatureModelData loading');
 expect(within(card(1)).getByText('Same display as slot 0')).toBeVisible();
 await act(async()=>{hold.resolve();});
 await waitFor(()=>expect(within(card(0)).getByText('Model ID').closest('.iv-fact')).toHaveTextContent('19'));
 expect(within(card(0)).getByText('Scale').closest('.iv-fact')).toHaveTextContent('1.25 · CreatureModelScale, as read');
 expect(within(card(0)).getByText('Texture variations').closest('.iv-fact')).toHaveTextContent('[800101, 0, 0, 0] · FileDataIDs');
 expect(within(card(0)).getByText('Model file').closest('.iv-fact')).toHaveTextContent('FileDataID 123456');
 await waitFor(()=>expect(within(card(1)).getByText('Model ID').closest('.iv-fact')).toHaveTextContent('19'));
 expect(card(1)).toHaveTextContent('Probability -0');
 expect(screen.getByText('Slot 2').closest('.cv-slot-line')).toHaveTextContent('DisplayID 0 · No display declared');expect(screen.getByText('Slot 3').closest('.cv-slot-line')).toHaveTextContent('Invalid reference');
 expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1);expect(bridge.creatureDisplayFacts).not.toHaveBeenCalledWith(expect.objectContaining({slot:2}));
});
it('reads retained chains locally without preparing and never moves focus when they arrive',async()=>{
 const{bridge}=setup({ready:true});const option=await screen.findByRole('option',{name:/^Authored creature 00 ·/});const list=screen.getByRole('listbox');list.focus();fireEvent.click(option);
 await waitFor(()=>expect(card(0)).toHaveTextContent('FileDataID 123456'));expect(list).toHaveFocus();
 expect(bridge.creatureDisplayPrepare).not.toHaveBeenCalled();expect(vi.mocked(bridge.creatureDisplayFacts!).mock.calls.map(c=>c[0].slot)).toEqual([0,1]);
});
it('keeps two started display consumers through new selections while a third waits withdrawably',async()=>{
 const{bridge,resolved}=setup(),first=deferred<BridgeReply<CreatureDisplayMetadataResult>>(),second=deferred<BridgeReply<CreatureDisplayMetadataResult>>();
 vi.mocked(bridge.creatureDisplayPrepare!).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
 await choose('0');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));
 await choose('2');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2));
 await choose('6');const waiting=await within(displays()).findByRole('button',{name:'Stop waiting for displays'});waiting.focus();fireEvent.click(waiting);
 expect(status()).toHaveFocus();expect(await within(displays()).findByRole('button',{name:'Retry display tables'})).toBeVisible();
 await act(async()=>first.resolve(resolved()));expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2);
 await act(async()=>second.resolve(resolved({},0,'2')));expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2);expect(bridge.cancel).not.toHaveBeenCalled();
});
it('keeps a started display consumer when the area is left and drops its late success after an explicit Stop',async()=>{
 const{bridge,resolved}=setup(),held=deferred<BridgeReply<CreatureDisplayMetadataResult>>();vi.mocked(bridge.creatureDisplayPrepare!).mockReturnValue(held.promise);
 await choose();await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));expect(bridge.cancel).not.toHaveBeenCalled();
 const stop=screen.getByRole('button',{name:'Stop metadata for Creature 0 slot 0'});stop.focus();fireEvent.click(stop);expect(screen.getByRole('button',{name:'Stopping Creature 0 slot 0…'})).toHaveFocus();
 await act(async()=>held.resolve(resolved()));
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));expect(await within(displays()).findByText('Display loading stopped')).toBeVisible();
 expect(within(displays()).getByRole('button',{name:'Retry display tables'})).toBeVisible();expect(card(0)).not.toHaveTextContent('FileDataID 123456');expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1);
});
it('fences a held read of the previous creature and shows only the current one',async()=>{
 const{bridge}=setup({ready:true}),held=deferred<BridgeReply<any>>();vi.mocked(bridge.creatureDisplayFacts!).mockReturnValueOnce(held.promise);
 await choose('0');await choose('2');await waitFor(()=>expect(card(0)).toHaveTextContent('FileDataID 123456'));
 const heading=screen.getByRole('heading',{level:2,name:'Authored creature 2'});heading.focus();
 await act(async()=>held.resolve(ok(creatureDisplayFacts(creatureDisplay(core,creatureSummary(core,'0')!,0,opening,'displayAmbiguous')))));
 expect(card(0)).not.toHaveTextContent('Ambiguous');expect(heading).toHaveFocus();expect(vi.mocked(bridge.creatureDisplayFacts!).mock.calls.at(-1)![0]).toMatchObject({creatureId:'2'});
});
it('keeps the shared Creature and display preparation credits charged through stopping, and the waiting Creature load goes first',async()=>{
 const{bridge,resolved}=setup(),first=deferred<BridgeReply<CreatureDisplayMetadataResult>>(),second=deferred<BridgeReply<CreatureDisplayMetadataResult>>(),coreReply=deferred<BridgeReply<any>>();
 vi.mocked(bridge.creatureDisplayPrepare!).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
 await choose('0');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));await choose('2');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2));
 vi.mocked(bridge.creaturesData!).mockResolvedValueOnce(ok({snapshot:null,dependencyState:'notPrepared',sourceArtifactId:null}));vi.mocked(bridge.creaturesPrepare!).mockReturnValueOnce(coreReply.promise);
 fireEvent.click(screen.getByRole('button',{name:'Explore another build'}));await screen.findByRole('button',{name:'Stop waiting for Creatures'});expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
 fireEvent.click(screen.getByRole('button',{name:'Stop metadata for Creature 0 slot 0'}));expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
 await act(async()=>first.resolve(resolved()));await waitFor(()=>expect(bridge.creaturesPrepare).toHaveBeenCalledTimes(1));
 await act(async()=>coreReply.resolve(ok(creaturesSnapshot({...context,buildIdentity:'a'.repeat(64)}))));await screen.findByRole('option',{name:/^Authored creature 00 ·/});
 expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2);expect(bridge.cancel).toHaveBeenCalledTimes(1);
 await act(async()=>second.resolve(resolved({},0,'2')));
});
it('treats the same creature and slot in another build as its own waiting intent and focuses its local stopped status',async()=>{
 const{bridge,resolved}=setup(),first=deferred<BridgeReply<CreatureDisplayMetadataResult>>(),second=deferred<BridgeReply<CreatureDisplayMetadataResult>>();
 vi.mocked(bridge.creatureDisplayPrepare!).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
 await choose('0');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));await choose('2');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2));
 fireEvent.click(screen.getByRole('button',{name:'Explore another build'}));await choose('0');
 const stop=await within(displays()).findByRole('button',{name:'Stop waiting for displays'});stop.focus();fireEvent.click(stop);expect(status()).toHaveFocus();
 await act(async()=>first.resolve(resolved()));expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2);expect(bridge.cancel).not.toHaveBeenCalled();
 expect(within(displays()).getByRole('button',{name:'Retry display tables'})).toBeVisible();
 await act(async()=>second.resolve(resolved({},0,'2')));
});
it('withdraws a held local readiness intent without starting a preparation when its old read succeeds',async()=>{
 const{bridge}=setup(),held=deferred<BridgeReply<CreatureDisplayMetadataResult>>();vi.mocked(bridge.creatureDisplayRead!).mockReturnValueOnce(held.promise);
 // Stop waiting shows as soon as the slot is reading, but the read starts in an effect after that render (#107).
 await choose();await waitFor(()=>expect(bridge.creatureDisplayRead).toHaveBeenCalledTimes(1));const stop=within(displays()).getByRole('button',{name:'Stop waiting for displays'});stop.focus();fireEvent.click(stop);
 await act(async()=>held.resolve(ok(creatureDisplay(core,creatureSummary(core,'0')!,0,opening))));
 expect(bridge.creatureDisplayPrepare).not.toHaveBeenCalled();expect(within(displays()).getByRole('button',{name:'Retry display tables'})).toBeVisible();
 fireEvent.click(screen.getByRole('button',{name:'Switch area'}));fireEvent.click(screen.getByRole('button',{name:'Switch area'}));expect(bridge.creatureDisplayRead).toHaveBeenCalledTimes(1);
});
it('prepares exactly once after an immediate completion',async()=>{
 const{bridge}=setup();await choose();await waitFor(()=>expect(card(0)).toHaveTextContent('FileDataID 123456'));expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1);
});
it('clears only a stale chain and keeps the creature, its four slots and a Retry',async()=>{
 const{bridge,tables}=setup({ready:true});await choose('0');await waitFor(()=>expect(card(0)).toHaveTextContent('FileDataID 123456'));
 tables.clear();vi.mocked(bridge.creatureDisplayPrepare!).mockResolvedValueOnce({ok:false,error:{code:'CREATURE_DISPLAY_STALE',message:'An exact target source changed.',retryable:true}});
 await choose('2');await choose('0');
 expect(await within(displays()).findByText('CREATURE_DISPLAY_STALE')).toBeVisible();expect(card(0)).not.toHaveTextContent('FileDataID 123456');
 expect(screen.getByRole('heading',{level:2,name:'Authored creature 0'})).toBeVisible();expect(screen.getAllByRole('article')).toHaveLength(2);expect(document.querySelectorAll('.cv-slot-line')).toHaveLength(2);
 expect(within(displays()).getByRole('button',{name:'Retry display tables'})).toBeVisible();
});
it.each([false,true])('hands Retry focus to the persistent display status while respecting a deliberate move (%s)',async moved=>{
 const{bridge,tables}=setup({state:'unavailable'});await choose();const retry=await within(displays()).findByRole('button',{name:'Retry display tables'});
 const held=deferred<BridgeReply<CreatureDisplayMetadataResult>>();vi.mocked(bridge.creatureDisplayRead!).mockReturnValueOnce(held.promise);
 retry.focus();fireEvent.click(retry);expect(status()).toHaveFocus();const search=screen.getByRole('searchbox',{name:'Name contains'});if(moved)search.focus();
 if(moved){tables.add(context.buildIdentity);}
 await act(async()=>held.resolve(moved?ok(creatureDisplay(core,creatureSummary(core,'0')!,0,opening)):{ok:false,error:{code:'NETWORK_UNAVAILABLE',message:'Authored unavailable local metadata.',retryable:true}}));
 if(moved)expect(search).toHaveFocus();else{expect(status()).toHaveFocus();expect(within(displays()).getByRole('button',{name:'Retry display tables'})).toBeVisible();}
});
it.each(['2','6'])('View in activity returns to the exact creature of a background display request (other selection %s)',async other=>{
 const{bridge,resolved}=setup(),first=deferred<BridgeReply<CreatureDisplayMetadataResult>>(),second=deferred<BridgeReply<CreatureDisplayMetadataResult>>();
 vi.mocked(bridge.creatureDisplayPrepare!).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
 await choose('0');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));await choose(other);await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2));
 vi.mocked(window.scrollTo).mockClear();fireEvent.click(screen.getByRole('button',{name:'View Creature 0 slot 0 metadata'}));
 await screen.findByRole('heading',{level:2,name:'Authored creature 0'});await waitFor(()=>expect(screen.getByRole('heading',{level:3,name:'Displays'})).toHaveFocus());expect(window.scrollTo).not.toHaveBeenCalled();
 await act(async()=>{first.resolve(resolved());second.resolve(resolved({},0,other));});
});
it('a View handled once is never replayed when the browser comes back from another build (usability review on #62)',async()=>{
 const{bridge,resolved}=setup(),first=deferred<BridgeReply<CreatureDisplayMetadataResult>>();vi.mocked(bridge.creatureDisplayPrepare!).mockReturnValueOnce(first.promise);
 await choose('2');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));await choose('6');
 fireEvent.click(screen.getByRole('button',{name:'View Creature 2 slot 0 metadata'}));await screen.findByRole('heading',{level:2,name:'Authored creature 2'});
 await waitFor(()=>expect(screen.getByRole('heading',{level:3,name:'Displays'})).toHaveFocus());
 await choose('6');const list=screen.getByRole('listbox');list.focus();
 fireEvent.click(screen.getByRole('button',{name:'Explore another build'}));await screen.findByRole('option',{name:/^Authored creature 00 ·/});
 fireEvent.click(screen.getByRole('button',{name:'Explore another build'}));
 expect(await screen.findByRole('heading',{level:2,name:'Authored creature 6'})).toBeVisible();
 await act(async()=>{await new Promise(r=>setTimeout(r,50));});
 expect(screen.queryByRole('heading',{level:2,name:'Authored creature 2'})).not.toBeInTheDocument();expect(screen.getByRole('heading',{level:3,name:'Displays'})).not.toHaveFocus();
 await act(async()=>first.resolve(resolved({},0,'2')));
});
it('Stop waiting for Creatures withdraws the waiting Creature load while two display requests prepare',async()=>{
 const{bridge,resolved}=setup(),first=deferred<BridgeReply<CreatureDisplayMetadataResult>>(),second=deferred<BridgeReply<CreatureDisplayMetadataResult>>();
 vi.mocked(bridge.creatureDisplayPrepare!).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
 await choose('0');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(1));await choose('2');await waitFor(()=>expect(bridge.creatureDisplayPrepare).toHaveBeenCalledTimes(2));
 vi.mocked(bridge.creaturesData!).mockResolvedValueOnce(ok({snapshot:null,dependencyState:'notPrepared',sourceArtifactId:null}));
 fireEvent.click(screen.getByRole('button',{name:'Explore another build'}));
 expect(await screen.findByText(/Two display requests are already preparing tables/)).toBeVisible();
 const stop=screen.getByRole('button',{name:'Stop waiting for Creatures'});stop.focus();fireEvent.click(stop);
 expect(screen.getByRole('heading',{level:1,name:'Creatures'})).toHaveFocus();expect(await screen.findByRole('button',{name:'Retry Creatures preparation'})).toBeVisible();
 await act(async()=>first.resolve(resolved()));expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
 await act(async()=>second.resolve(resolved({},0,'2')));expect(bridge.creaturesPrepare).not.toHaveBeenCalled();
});
// Every outcome keeps its own words; none borrows another's, and none offers a model.
it.each([
 ['protected','Protected','AUTHORED_PROTECTED'],['unsupported','Unsupported','AUTHORED_UNSUPPORTED'],['corrupt','Failed integrity checks','AUTHORED_CORRUPT'],['resourceLimit','Too large to read','AUTHORED_RESOURCELIMIT'],
 ['displayAmbiguous','Ambiguous','2 CreatureDisplayInfo rows have ID 100; none is picked'],
 // The fixture's CreatureDisplayInfo is Partial and its CreatureModelData complete: a missing display is Unknown, a missing model is Missing.
 ['displayMissingInAdmittedScope','Unknown','isn\'t in the readable CreatureDisplayInfo rows'],['modelMissingInAdmittedScope','Missing','CreatureModelData, complete in this build, has no row 19.'],
 ['modelAmbiguous','Ambiguous','2 CreatureModelData rows have ID 19; none is picked'],['noDeclaredModel','ModelID is 0','so this display names no model'],['noDeclaredModelFile','no model file declared','0'],
] as const)('keeps the %s state in its own words',async(state,word,text)=>{
 setup({ready:true,state});await choose();
 await waitFor(()=>expect(card(0)).toHaveTextContent(word));expect(card(0)).toHaveTextContent(text);expect(card(0)).not.toHaveTextContent('FileDataID 123456');
 if(state==='displayMissingInAdmittedScope')expect(card(0)).not.toHaveTextContent('Missing');
 if(state==='modelMissingInAdmittedScope')expect(card(0)).not.toHaveTextContent('Unknown');
});
it('opens the model file in Files and each fact in Tables with the exact target cell',async()=>{
 const{onFile,onSource}=setup({ready:true});await choose();
 const files=await within(card(0)).findByRole('button',{name:'Find FileDataID 123456 in Files'});fireEvent.click(files);
 expect(onFile).toHaveBeenCalledWith({fileDataId:'123456',table:'CreatureModelData',recordId:'19'});
 fireEvent.click(within(card(0)).getByRole('button',{name:/^CreatureModelScale/}));expect(onSource).toHaveBeenCalledWith(expect.objectContaining({tableId:'bfdaf9f1'}),'100',1);
 fireEvent.click(within(card(0)).getByRole('button',{name:/^DisplayID\[0\]/}));expect(onSource).toHaveBeenLastCalledWith(expect.objectContaining({tableId:'c9d6b6b3'}),'0',8);
 expect(within(card(0)).getAllByRole('button').filter(b=>b.classList.contains('iv-src')&&b.tabIndex===0)).toHaveLength(1);
});
it('shows the chain without facts on an engine without creature-display-facts-v1, in one column without a preview',async()=>{
 const{bridge}=setup({ready:true,facts:false});await choose();
 await waitFor(()=>expect(within(card(0)).getByText('Model ID').closest('.iv-fact')).toHaveTextContent('19'));
 expect(card(0)).toHaveTextContent('Display facts aren’t available from this engine');expect(within(card(0)).getByText('Model file').closest('.iv-fact')).toHaveTextContent('FileDataID 123456');
 expect(bridge.creatureDisplayRead).toHaveBeenCalled();expect(card(0).querySelector('.cv-disp-body')).toHaveClass('cv-no-preview');
});
