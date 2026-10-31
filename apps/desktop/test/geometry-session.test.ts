import {afterEach,describe,expect,it,vi} from 'vitest';
import {createElement,StrictMode,useSyncExternalStore} from 'react';
import {act,render,cleanup} from '@testing-library/react';
import {CreatureGeometryView} from '../src/renderer/CreatureGeometryView';
import {geometryActions} from '../src/renderer/useCreatureGeometry';
import {GeometryViewController} from '../src/main/geometry-view-controller';
import type {GeometryBinaryProtocol} from '../src/main/geometry-binary-protocol';
import readyJson from '../../../contracts/examples/creature-geometry-ready.json';
import type {CreatureGeometryResult,CreatureGeometryManifest} from '../../../contracts/generated/discovery';
import type {DiscoveryBridge,BridgeReply} from '../src/shared/bridge';
import type {GeometryBodyGrant,GeometryBodyRetired,GeometryViewRetirement} from '../src/shared/creature-geometry';
import type {GeometrySceneOptions,GeometrySceneOwner} from '../src/renderer/geometry-scene';
import {GeometrySession} from '../src/renderer/geometry-session';
import {captureGeometryAnchor,type GeometryIntent} from '../src/renderer/geometry-state';
import {validateGeometry} from '../src/renderer/geometry-vmg';
import {geometryCSharpGolden} from './geometry-fixtures';
const ok=<T,>(value:T):BridgeReply<T>=>({ok:true,value});
const defer=<T,>()=>{let resolve!:(v:T)=>void,reject!:(reason:unknown)=>void;return{promise:new Promise<T>((r,j)=>{resolve=r;reject=j;}),resolve,reject};};
const cleanups:(()=>Promise<void>)[]=[];
afterEach(async()=>{cleanup();await Promise.all(cleanups.splice(0).map(f=>f()));});
function setup(){
 const fixture=geometryCSharpGolden(),result=structuredClone(readyJson) as CreatureGeometryResult;
 result.manifest=fixture.manifest as CreatureGeometryManifest;result.metadata.modelFileDataId='800';result.metadata.targets[1]!.row!.edgeValue.value='800';result.sources[0]!.fileDataId='800';
 const missing={...structuredClone(result),state:'notPrepared',manifest:null,sources:[]} as CreatureGeometryResult;
 const intent:GeometryIntent={selection:{...result.context,artifactId:result.artifactId,creatureId:result.creatureId,slot:result.slot},anchor:captureGeometryAnchor(result.opening,result.metadata),label:{name:'Authored Creature',buildVersion:'Authored build'},origin:{key:'exact',invoker:null,windowScroll:[0,0],innerScroll:[]}};
 let retirementListener:(n:GeometryViewRetirement)=>void=()=>{},progressListener:Parameters<DiscoveryBridge['onProgress']>[0]=()=>{},options:GeometrySceneOptions|null=null;
 const body={ok:true,arrayBuffer:vi.fn(async()=>fixture.buffer),body:null} as unknown as Response;
 const validate=vi.fn(validateGeometry),fetch=vi.fn(async()=>body),releaseCredit=vi.fn(),reserve=vi.fn(()=>releaseCredit);
 const bridge={onProgress:vi.fn(f=>{progressListener=f;return vi.fn();}),onGeometryRetirement:vi.fn(f=>{retirementListener=f;return vi.fn();}),creatureGeometryRead:vi.fn(async()=>ok(result)),creatureGeometryPrepare:vi.fn(async()=>ok(result)),geometryViewEnter:vi.fn(async p=>ok({viewId:'a'.repeat(32),viewGeneration:p.viewGeneration})),geometryViewOpen:vi.fn(async p=>ok({viewId:p.viewId,viewGeneration:session.snapshot().view!.generation,operationToken:'b'.repeat(32),url:'vibeminer-geometry://body/'+ 'c'.repeat(32),manifest:result.manifest!,requestLifetimeMs:30000})),geometryViewBodyRetired:vi.fn(async p=>ok({...p,viewGeneration:session.snapshot().view!.generation,geometryArtifactId:result.manifest!.artifactId,retired:true as const})),geometryViewSceneDisposed:vi.fn(async()=>ok({accepted:true as const})),geometryViewRelease:vi.fn(async()=>ok({released:true as const})),cancel:vi.fn(async()=>ok({requested:true}))} as unknown as DiscoveryBridge;
 const owner:GeometrySceneOwner={mount:vi.fn<GeometrySceneOwner['mount']>((_,identity,ack)=>{expect(options!.confirmRetirement(identity,ack)).toBe(true);options!.onStatus({code:'drawn'});return{state:'mounted'};}),move:vi.fn<GeometrySceneOwner['move']>(()=>({state:'moved'})),disposeScene:vi.fn<GeometrySceneOwner['disposeScene']>(identity=>({state:'disposed',notice:{viewId:'a'.repeat(32),operationToken:identity?.operationToken??null,scope:'scene'}})),disposeView:vi.fn<GeometrySceneOwner['disposeView']>(identity=>({state:'disposed',notice:{viewId:'a'.repeat(32),operationToken:identity?.operationToken??null,scope:'view'}}))};
 const scene=vi.fn((_:HTMLCanvasElement,o:GeometrySceneOptions)=>{options=o;return{state:'available' as const,owner};});
 const session=new GeometrySession(bridge,{tryReserve:reserve,revision:0},{fetch,validate,createScene:scene,requestId:()=> 'geometry_test'});
 const holds:(()=>void)[]=[];let debt=false;
 const hold=<T,>(fallback:T)=>{const d=defer<T>();holds.push(()=>d.resolve(fallback));return d;};
 cleanups.push(async()=>{for(const release of holds)release();try{await session.shutdown('applicationExit');}catch(e){if(!debt)throw e;}});
 const start=()=>{session.enter(intent);session.iconsHiddenCommitted(session.snapshot().view!.generation);};
 return{fixture,result,missing,intent,body,fetch,validate,bridge,session,scene,owner,releaseCredit,reserve,hold,start,expectDebt:()=>{debt=true;},retirement:(n:GeometryViewRetirement)=>retirementListener(n),progress:(p:Parameters<typeof progressListener>[0])=>progressListener(p),options:()=>options!};
}
function until(session:GeometrySession,predicate:()=>boolean):Promise<void>{if(predicate())return Promise.resolve();return new Promise(resolve=>{const off=session.subscribe(()=>{if(predicate()){off();resolve();}});});}
async function mount(h:ReturnType<typeof setup>){await until(h.session,()=>!!h.session.snapshot().view?.canMount);h.session.attachCanvas(h.session.snapshot().view!.generation,document.createElement('canvas'));await until(h.session,()=>h.session.snapshot().view?.phase==='ready');}
function observedCall(fn:(...args:any[])=>any,count=1):Promise<void>{const entered=defer<void>(),mock=vi.mocked(fn),implementation=mock.getMockImplementation()!;let calls=0;mock.mockImplementation((...args:any[])=>{const result=implementation(...args);if(++calls===count)entered.resolve();return result;});return entered.promise;}
function bodyForbidden(value:unknown,seen=new Set<unknown>()):boolean{if(!value||typeof value!=='object'||seen.has(value))return false;seen.add(value);if(value instanceof ArrayBuffer||ArrayBuffer.isView(value)||value instanceof Response)return true;return Object.values(value).some(v=>bodyForbidden(v,seen));}

describe('owned geometry presentation',()=>{
 it('requires entered confirmation, icon-hidden commit and actual main body retirement before the sole scene',async()=>{
  const h=setup(),entered=h.hold(ok({viewId:'a'.repeat(32),viewGeneration:1})),ack=h.hold(ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),geometryArtifactId:h.result.manifest!.artifactId,retired:true as const}));
  vi.mocked(h.bridge.geometryViewEnter!).mockReturnValue(entered.promise);vi.mocked(h.bridge.geometryViewBodyRetired!).mockReturnValue(ack.promise);
  h.session.enter(h.intent);expect(h.session.snapshot().iconsSuspended).toBe(true);await Promise.resolve();expect(h.bridge.creatureGeometryRead).not.toHaveBeenCalled();
  entered.resolve(ok({viewId:'a'.repeat(32),viewGeneration:1}));await until(h.session,()=>h.session.snapshot().view?.phase==='receiving');await Promise.resolve();expect(h.fetch).not.toHaveBeenCalled();
  h.session.iconsHiddenCommitted(1);await until(h.session,()=>h.session.snapshot().view?.phase==='validating');expect(h.scene).not.toHaveBeenCalled();expect(h.session.snapshot().view?.canMount).toBe(false);
  ack.resolve(ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),geometryArtifactId:h.result.manifest!.artifactId,retired:true}));await mount(h);
  expect(h.scene).toHaveBeenCalledTimes(1);expect(bodyForbidden(h.session)).toBe(true);expect(bodyForbidden({snapshot:h.session.snapshot(),session:geometryActions(()=>h.session)})).toBe(false);expect(vi.mocked(h.owner.mount).mock.calls[0][0].positions.buffer).toBe(h.fixture.buffer);expect((vi.mocked(h.owner.mount).mock.calls[0][0] as {lineIndices:Uint32Array}).lineIndices.buffer).toBe(h.fixture.buffer);expect(bodyForbidden(h.session.snapshot())).toBe(false);expect(h.fetch).toHaveBeenCalledWith(expect.any(String),expect.objectContaining({credentials:'omit',redirect:'error',cache:'no-store'}));
  expect(h.options().confirmRetirement({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),geometryArtifactId:h.result.manifest!.artifactId},vi.mocked(h.owner.mount).mock.calls[0][2])).toBe(false);
 });
 it('observes late Enter after Return and performs no read, preparation or fetch',async()=>{
  const h=setup(),entered=h.hold(ok({viewId:'a'.repeat(32),viewGeneration:1}));vi.mocked(h.bridge.geometryViewEnter!).mockReturnValue(entered.promise);h.start();await Promise.resolve();
  let retired=false;const leaving=h.session.leave('return').then(()=>{retired=true;});await Promise.resolve();expect(retired).toBe(false);expect(h.session.snapshot().view).toBeNull();
  entered.resolve(ok({viewId:'a'.repeat(32),viewGeneration:1}));await leaving;expect(h.bridge.creatureGeometryRead).not.toHaveBeenCalled();expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();expect(h.fetch).not.toHaveBeenCalled();expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledWith({viewId:'a'.repeat(32),operationToken:null,scope:'view'});
 });
 it('observes a late minted grant, ACKs its actual token and waits Release after local disposal',async()=>{
  const h=setup(),grant=h.hold<BridgeReply<GeometryBodyGrant>>(ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),url:'vibeminer-geometry://body/'+ 'c'.repeat(32),manifest:h.result.manifest!,requestLifetimeMs:30000})),released=h.hold(ok({released:true as const}));
  vi.mocked(h.bridge.geometryViewOpen!).mockReturnValue(grant.promise);vi.mocked(h.bridge.geometryViewRelease!).mockReturnValue(released.promise);const releaseEntered=observedCall(h.bridge.geometryViewRelease!);h.start();await until(h.session,()=>h.session.snapshot().view?.phase==='receiving');
  const leaving=h.session.leave('return');grant.resolve(ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),url:'vibeminer-geometry://body/'+ 'c'.repeat(32),manifest:h.result.manifest!,requestLifetimeMs:30000}));
  await releaseEntered;expect(h.fetch).not.toHaveBeenCalled();expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledWith({viewId:'a'.repeat(32),operationToken:'b'.repeat(32),scope:'view'});expect(h.session.snapshot().iconsSuspended).toBe(true);
  released.resolve(ok({released:true}));await leaving;expect(h.session.snapshot().iconsSuspended).toBe(false);
 });
 it('waits the actual arrayBuffer terminal before ACK and clears external direct views before release',async()=>{
  const h=setup(),buffer=h.hold(h.fixture.buffer);vi.mocked(h.body.arrayBuffer).mockReturnValue(buffer.promise);const bufferEntered=observedCall(h.body.arrayBuffer);h.start();await bufferEntered;const leaving=h.session.leave('navigation');await Promise.resolve();expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();
  buffer.resolve(h.fixture.buffer);await leaving;expect(h.scene).not.toHaveBeenCalled();expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledTimes(1);expect(bodyForbidden(h.session.snapshot())).toBe(false);
 });
 it('Return preserves a started Prepare and its shared credit; reentry joins and obtains fresh Ready Read',async()=>{
  const h=setup(),prepared=h.hold(ok(h.result));vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce(ok(h.missing));vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValue(prepared.promise);
  h.start();await until(h.session,()=>h.session.snapshot().operation?.status==='pending');await h.session.leave('return');expect(h.bridge.cancel).not.toHaveBeenCalled();expect(h.releaseCredit).not.toHaveBeenCalled();
  const secondRead=defer<void>();vi.mocked(h.bridge.creatureGeometryRead!).mockImplementationOnce(async()=>{secondRead.resolve();return ok(h.missing);});h.start();await secondRead.promise;prepared.resolve(ok(h.result));await mount(h);expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);expect(h.bridge.creatureGeometryRead).toHaveBeenCalledTimes(3);expect(h.releaseCredit).toHaveBeenCalledTimes(1);
 });
 it('keeps Stop before Enter/Read despite late successful preparation and only frees credit at actual terminal',async()=>{
  const h=setup(),prepared=h.hold(ok(h.result)),canceled=h.hold(ok({requested:true}));vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce(ok(h.missing));vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValue(prepared.promise);vi.mocked(h.bridge.cancel).mockReturnValue(canceled.promise);
  h.start();await until(h.session,()=>h.session.snapshot().operation?.status==='pending');h.session.stopPreparation();await h.session.leave('return');canceled.resolve(ok({requested:true}));await Promise.resolve();expect(h.releaseCredit).not.toHaveBeenCalled();prepared.resolve(ok(h.result));await until(h.session,()=>h.session.snapshot().operation?.status==='canceled');h.start();expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);expect(h.bridge.creatureGeometryRead).toHaveBeenCalledTimes(1);expect(h.fetch).not.toHaveBeenCalled();expect(h.session.snapshot().view?.message).toBe('Geometry preparation stopped');
 });
 it('uses a capacity event rather than polling and withdraws an unstarted wait without Prepare',async()=>{
  const h=setup();h.reserve.mockReturnValue(null as never);vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValue(ok(h.missing));h.start();await until(h.session,()=>h.session.snapshot().view?.phase==='waiting');expect(h.reserve).toHaveBeenCalledTimes(1);
  h.session.stopPreparation();await h.session.leave('return');h.session.updateAdmission({tryReserve:()=>h.releaseCredit,revision:1});expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();h.start();expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);
 });
 // #131: a machine with no WebGL 2 (no GPU and no software fallback) names that, offers no Retry, and still retires the view.
 it('names a refused WebGL context as no WebGL on this computer, without Retry, and releases the view',async()=>{
  const h=setup();h.scene.mockImplementation(()=>({state:'refused',code:'webglUnavailable'}) as never);h.start();await until(h.session,()=>!!h.session.snapshot().view?.canMount);
  h.session.attachCanvas(1,document.createElement('canvas'));await until(h.session,()=>h.session.snapshot().view?.phase==='outcome'&&!h.session.snapshot().iconsSuspended);
  const v=h.session.snapshot().view!;expect(v.message).toBe('3D drawing unavailable');expect(v.canMount).toBe(false);
  expect(v.error).toEqual({code:'GEOMETRY_WEBGL_UNAVAILABLE',message:"VibeMiner gets no WebGL 2 graphics on this computer, so models can't be drawn. The creature, display and source details still work.",retryable:false});
  expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledTimes(1);
  // The same selection entered again (not a fresh Show model) replays that outcome by name without reading or drawing again.
  h.session.enter(h.intent);expect(h.session.snapshot().view).toMatchObject({phase:'outcome',message:'3D drawing unavailable',error:{code:'GEOMETRY_WEBGL_UNAVAILABLE'}});expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);
 });
 // Any other creation refusal (a lost or mismatched context, a renderer that throws) may pass: it stays retryable and never claims no WebGL.
 it('keeps another WebGL creation refusal retryable and does not call it no WebGL',async()=>{
  const h=setup();h.scene.mockImplementation(()=>({state:'refused',code:'webglUnsupported'}) as never);h.start();await until(h.session,()=>!!h.session.snapshot().view?.canMount);
  h.session.attachCanvas(1,document.createElement('canvas'));await until(h.session,()=>h.session.snapshot().view?.phase==='outcome'&&!h.session.snapshot().iconsSuspended);
  expect(h.session.snapshot().view).toMatchObject({message:'Geometry needs attention',error:{code:'GEOMETRY_SCENE_WEBGLUNSUPPORTED',retryable:true}});
 });
 it('retains cleanupFailed returned owner/debt and blocks another selection without a false ACK',async()=>{
  const h=setup();h.expectDebt();h.scene.mockImplementation((_,options)=>{options.onStatus({code:'cleanupFailed'});return{state:'refused',code:'cleanupFailed',owner:h.owner} as never;});h.start();await until(h.session,()=>!!h.session.snapshot().view?.canMount);h.session.attachCanvas(1,document.createElement('canvas'));await until(h.session,()=>h.session.snapshot().retirement?.phase==='blocked');expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();expect(h.session.snapshot().iconsSuspended).toBe(true);h.session.enter({...h.intent,selection:{...h.intent.selection,slot:1}});await Promise.resolve();expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);
 });
 it.each([['engineUnavailable','ENGINE_EXITED','The local engine stopped. Restart VibeMiner to reconnect.'],['shutdown','ENGINE_STOPPING','VibeMiner is closing. Geometry was not prepared.'],['sourceChanged','GEOMETRY_SOURCE_CHANGED','This geometry presentation is no longer current. Return to the selected metadata and reassess its source.']] as const)('a %s notice during Prepare reports %s, not another cause (#59)',async(reason,code,message)=>{
  const h=setup(),prepared=h.hold<BridgeReply<CreatureGeometryResult>>(ok(h.result));vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValue(ok(h.missing));vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValue(prepared.promise);
  h.start();await until(h.session,()=>h.session.snapshot().view?.phase==='preparing');
  h.retirement({viewId:'a'.repeat(32),viewGeneration:1,operationToken:null,reason});
  expect(h.session.snapshot().view?.error).toEqual({code,message,retryable:reason==='sourceChanged'});
  prepared.resolve({ok:false,error:{code:'ENGINE_EXITED',message:'The local engine stopped. Restart VibeMiner to reconnect.',retryable:true}});
  await until(h.session,()=>h.session.snapshot().operation?.status==='failed'&&!h.session.snapshot().retirement);
  expect(h.session.snapshot().view?.error?.code).toBe(code);expect(h.session.snapshot().operation?.error?.code).toBe('ENGINE_EXITED');
 });
 it('coalesces matching repeated retirement notices and ignores unrelated generations',async()=>{
  const h=setup();h.start();await mount(h);h.retirement({viewId:'a'.repeat(32),viewGeneration:2,operationToken:'b'.repeat(32),reason:'sourceChanged'});expect(h.owner.disposeView).not.toHaveBeenCalled();
  const n={viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),reason:'sourceChanged' as const};h.retirement(n);h.retirement(n);await until(h.session,()=>!h.session.snapshot().retirement);expect(h.owner.disposeView).toHaveBeenCalledTimes(1);expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledTimes(1);expect(h.bridge.geometryViewRelease).toHaveBeenCalledTimes(1);
 });
});

describe('geometry currentness and uncertain ownership regressions',()=>{
 it('rejects a changed same-key anchor instead of silently coalescing the existing drawable selection',async()=>{
  const h=setup();h.start();await mount(h);const changed=structuredClone(h.result);changed.opening.generation='2';h.session.enter({...h.intent,anchor:captureGeometryAnchor(changed.opening,changed.metadata)});await until(h.session,()=>!h.session.snapshot().retirement);expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);expect(h.session.snapshot().view?.error?.code).toBe('GEOMETRY_SOURCE_CHANGED');expect(h.owner.disposeView).toHaveBeenCalledTimes(1);
 });
 it('does not present an old Q0 against a new same-key opening anchor',async()=>{
  const h=setup(),q0={...h.result,state:'noDeclaredTriangles',manifest:null} as CreatureGeometryResult;vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce(ok(h.missing));vi.mocked(h.bridge.creatureGeometryPrepare!).mockResolvedValueOnce(ok(q0));h.start();await until(h.session,()=>h.session.snapshot().view?.result?.state==='noDeclaredTriangles');await h.session.leave('return');const changed=structuredClone(h.result);changed.opening.generation='2';h.session.enter({...h.intent,anchor:captureGeometryAnchor(changed.opening,changed.metadata)});expect(h.session.snapshot().view?.result).toBeNull();expect(h.session.snapshot().view?.error?.code).toBe('GEOMETRY_SOURCE_CHANGED');expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);
 });
 it('explicit Retry discards its matching failed terminal and performs fresh Enter/Read/Prepare',async()=>{
  const h=setup(),protectedResult={...h.result,state:'protected',manifest:null,sources:[],problem:{code:'BLTE_ENCRYPTED',message:'Authored protected SKIN',retryable:true,dependency:'skin',target:null,headline:'encrypted'}} as CreatureGeometryResult;
  vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValue(ok(h.missing));vi.mocked(h.bridge.creatureGeometryPrepare!).mockResolvedValueOnce(ok(protectedResult));h.start();await until(h.session,()=>h.session.snapshot().view?.result?.state==='protected');await h.session.leave('return');h.start();expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);h.session.retry();h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);await mount(h);expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(2);expect(h.bridge.creatureGeometryRead).toHaveBeenCalledTimes(2);expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(2);
 });
 it('retains a main-observed minted token when the accepted Open reply is later lost',async()=>{
  const h=setup(),grant=h.hold<BridgeReply<GeometryBodyGrant>>(ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),url:'vibeminer-geometry://body/'+ 'c'.repeat(32),manifest:h.result.manifest!,requestLifetimeMs:30000}));vi.mocked(h.bridge.geometryViewOpen!).mockReturnValue(grant.promise);const openEntered=observedCall(h.bridge.geometryViewOpen!);h.start();await openEntered;h.retirement({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),reason:'engineUnavailable'});grant.reject(new Error('Authored lost Open reply'));await until(h.session,()=>!h.session.snapshot().retirement);expect(h.fetch).not.toHaveBeenCalled();expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledWith({viewId:'a'.repeat(32),operationToken:'b'.repeat(32),scope:'view'});expect(h.session.snapshot().iconsSuspended).toBe(false);
 });
 it('keeps an uncertain accepted Enter failure blocked instead of fabricating Released',async()=>{
  const h=setup();h.expectDebt();const entered=h.hold<BridgeReply<{viewId:string;viewGeneration:number}>>(ok({viewId:'a'.repeat(32),viewGeneration:1}));vi.mocked(h.bridge.geometryViewEnter!).mockReturnValue(entered.promise);const called=observedCall(h.bridge.geometryViewEnter!);h.start();await called;entered.resolve({ok:false,error:{code:'ENGINE_UNAVAILABLE',message:'Authored lost accepted Enter',retryable:true}});await until(h.session,()=>h.session.snapshot().retirement?.phase==='blocked');expect(h.session.snapshot().iconsSuspended).toBe(true);expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();expect(h.bridge.geometryViewRelease).not.toHaveBeenCalled();h.session.enter({...h.intent,selection:{...h.intent.selection,slot:1}});await Promise.resolve();expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);
 });
 it('does not let a stale Activity Stop ID cancel or latch the newer owned request',async()=>{
  const h=setup(),prepared=h.hold(ok(h.result));vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce(ok(h.missing));vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValue(prepared.promise);h.start();await until(h.session,()=>h.session.snapshot().operation?.status==='pending');h.session.stopPreparation('old_request');expect(h.bridge.cancel).not.toHaveBeenCalled();expect(h.session.snapshot().operation?.status).toBe('pending');prepared.resolve(ok(h.result));await mount(h);expect(h.session.snapshot().view?.error).toBeNull();
 });
 it('retains the original consumer/credit through held cancellation notification before admitting another Prepare or completing shutdown',async()=>{
  const h=setup(),prepared=h.hold(ok(h.result)),cancel=h.hold(ok({requested:true}));vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce(ok(h.missing));vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValue(prepared.promise);vi.mocked(h.bridge.cancel).mockReturnValue(cancel.promise);h.start();await until(h.session,()=>h.session.snapshot().operation?.status==='pending');h.session.stopPreparation();await h.session.leave('return');prepared.resolve(ok(h.result));await prepared.promise;
  const nextResult=structuredClone(h.missing);nextResult.slot=1;nextResult.metadata.selected!.slot=1;const nextAnchor=structuredClone(h.intent.anchor);nextAnchor.metadata.selected!.slot=1;vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce(ok(nextResult));h.session.enter({...h.intent,selection:{...h.intent.selection,slot:1},anchor:nextAnchor});await until(h.session,()=>h.session.snapshot().view?.phase==='waiting');expect(h.session.snapshot().operation?.intent.selection.slot).toBe(0);expect(h.session.snapshot().operation?.status).toBe('stopping');expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);expect(h.releaseCredit).not.toHaveBeenCalled();let ended=false;const closing=h.session.shutdown('applicationExit').then(()=>{ended=true;});await Promise.resolve();expect(ended).toBe(false);cancel.resolve(ok({requested:true}));await closing;expect(h.releaseCredit).toHaveBeenCalledTimes(1);expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);
 });
 it('waits an actually held validator after Return before ACK and never creates a scene',async()=>{
  const h=setup(),validated=await validateGeometry(h.fixture.buffer,h.result.manifest!,{signal:new AbortController().signal,isCurrent:()=>true}),held=h.hold(validated);h.validate.mockReturnValue(held.promise);const entered=observedCall(h.validate);h.start();await entered;const leaving=h.session.leave('return');await Promise.resolve();expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();held.resolve(validated);await leaving;expect(h.scene).not.toHaveBeenCalled();expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledTimes(1);
 });
 it('waits the actual held BodyRetired reply after Return before ACK without mounting stale bytes',async()=>{
  const h=setup(),ack=h.hold<BridgeReply<GeometryBodyRetired>>(ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),geometryArtifactId:h.result.manifest!.artifactId,retired:true}));vi.mocked(h.bridge.geometryViewBodyRetired!).mockReturnValue(ack.promise);const entered=observedCall(h.bridge.geometryViewBodyRetired!);h.start();await entered;const leaving=h.session.leave('return');await Promise.resolve();expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();ack.resolve(ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),geometryArtifactId:h.result.manifest!.artifactId,retired:true}));await leaving;expect(h.scene).not.toHaveBeenCalled();
 });
});

it('composes actual session/view under StrictMode without a false ref-rehearsal retirement, then retires actual unmount',async()=>{
 const h=setup();function Live(){const snapshot=useSyncExternalStore(h.session.subscribe,h.session.snapshot,h.session.snapshot);return createElement(CreatureGeometryView,{flow:{snapshot,session:geometryActions(()=>h.session)},onReturn:()=>{void h.session.leave('return');}});}
 const rendered=render(createElement(StrictMode,null,createElement(Live)));
 await act(async()=>{h.start();await until(h.session,()=>h.session.snapshot().view?.phase==='ready');});
 expect(h.scene).toHaveBeenCalledTimes(1);expect(h.owner.mount).toHaveBeenCalledTimes(1);expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();expect(h.session.snapshot().iconsSuspended).toBe(true);
 rendered.unmount();await until(h.session,()=>!h.session.snapshot().iconsSuspended);expect(h.owner.disposeView).toHaveBeenCalledTimes(1);expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledTimes(1);
});
it('does not mount after synchronous creation-time context loss, retaining returned owner through actual disposal',async()=>{
 const h=setup();h.scene.mockImplementation((_,options)=>{options.onStatus({code:'contextLost'});return{state:'available',owner:h.owner};});h.start();await until(h.session,()=>!!h.session.snapshot().view?.canMount);h.session.attachCanvas(1,document.createElement('canvas'));await until(h.session,()=>h.session.snapshot().view?.error?.code==='GEOMETRY_CONTEXTLOST');await h.session.leave('return');expect(h.owner.mount).not.toHaveBeenCalled();expect(h.owner.disposeView).toHaveBeenCalledTimes(1);expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledTimes(1);
});
it('bounds remembered stopped decisions to 256 without eviction, network work or an artifact-count limit',async()=>{
 const h=setup();for(let i=0;i<256;i++){h.session.enter({...h.intent,selection:{...h.intent.selection,artifactId:i.toString(16).padStart(64,'0')}});h.session.stopPreparation();await h.session.leave('return');}
 h.session.enter({...h.intent,selection:{...h.intent.selection,artifactId:'f'.repeat(64)}});expect(h.session.snapshot().view?.error?.code).toBe('GEOMETRY_CONTROL_RESOURCE_LIMIT');expect(h.bridge.geometryViewEnter).not.toHaveBeenCalled();
 h.session.enter({...h.intent,selection:{...h.intent.selection,artifactId:'0'.repeat(64)}});expect(h.session.snapshot().view?.message).toBe('Geometry preparation stopped');expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();
});
it('uses real main controller certificates for fresh same-view Prepare and does not mistake its own normal retirement notice for source replacement',async()=>{
 const h=setup(),documentOwner={document:1,webContentsId:2,frame:{},session:{},isCurrent:()=>true};let prepared=false;
 const read=vi.fn(async()=>prepared?h.result:h.missing),prepare=vi.fn(async()=>{prepared=true;return h.result;});
 // Only the binary transport is authored here: actual controller certification/notice/ACK/release are exercised.
 const binary={issue:vi.fn((identity,_,manifest)=>({...identity,url:'vibeminer-geometry://body/'+'c'.repeat(32),manifest,requestLifetimeMs:30000})),bodyRetired:vi.fn(async()=>{}),retire:vi.fn(async()=>{}),isRetired:()=>true,forget:vi.fn(),dispose:vi.fn(async()=>{})} as unknown as GeometryBinaryProtocol;
 const controller=new GeometryViewController({icons:{blockPresentation:async()=>{},unblockPresentation:vi.fn()},binary,read,prepare,notify:(_,notice)=>h.retirement(notice)});
 vi.mocked(h.bridge.geometryViewEnter!).mockImplementation(async p=>ok(await controller.enter(documentOwner,p)));
 vi.mocked(h.bridge.creatureGeometryRead!).mockImplementation(async p=>ok(await controller.read(documentOwner,p)));
 vi.mocked(h.bridge.creatureGeometryPrepare!).mockImplementation(async(p,id)=>ok(await controller.prepare(documentOwner,p,id)));
 vi.mocked(h.bridge.geometryViewOpen!).mockImplementation(async p=>ok(await controller.open(documentOwner,p)));
 vi.mocked(h.bridge.geometryViewBodyRetired!).mockImplementation(async p=>ok(await controller.bodyRetired(documentOwner,p)));
 vi.mocked(h.bridge.geometryViewSceneDisposed!).mockImplementation(async p=>ok(await controller.sceneDisposed(documentOwner,p)));
 vi.mocked(h.bridge.geometryViewRelease!).mockImplementation(async p=>ok(await controller.release(documentOwner,p)));
 vi.mocked(h.owner.disposeView).mockImplementation(identity=>({state:'disposed',notice:{viewId:identity!.viewId,operationToken:identity!.operationToken,scope:'view'}}));
 h.start();await mount(h);expect(read).toHaveBeenCalledTimes(1);expect(prepare).toHaveBeenCalledTimes(1);await h.session.leave('return');expect(controller.stats.reserved).toBe(false);h.start();await mount(h);expect(read).toHaveBeenCalledTimes(2);expect(prepare).toHaveBeenCalledTimes(1);expect(h.session.snapshot().view?.error).toBeNull();await h.session.leave('return');await controller.dispose();
});
it('observes and cancels a late Response body before sending view disposal rather than treating fetch settlement as retirement',async()=>{
 const h=setup(),cancel=h.hold<void>(undefined),response={ok:true,arrayBuffer:vi.fn(),body:{cancel:vi.fn(()=>cancel.promise)}} as unknown as Response,received=h.hold(response);h.fetch.mockReturnValue(received.promise);const fetching=observedCall(h.fetch);
 // Bind the authored body's actual cancellation observation; no timeout/order inference from fetch completion.
 const cancelEntered=defer<void>();vi.mocked(response.body!.cancel).mockImplementation(()=>{cancelEntered.resolve();return cancel.promise;});
 h.start();await fetching;const leaving=h.session.leave('return');received.resolve(response);await cancelEntered.promise;expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();expect(response.arrayBuffer).not.toHaveBeenCalled();cancel.resolve();await leaving;expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledTimes(1);
});
it('retains an uncertain late-Response cleanup owner and refuses all new presentation admission',async()=>{
 const h=setup();h.expectDebt();const response={ok:true,arrayBuffer:vi.fn(),body:{cancel:vi.fn(async()=>{throw new Error('Authored response cancellation failed');})}} as unknown as Response,received=h.hold(response);h.fetch.mockReturnValue(received.promise);const fetching=observedCall(h.fetch);h.start();await fetching;const leaving=h.session.leave('return');received.resolve(response);await expect(leaving).rejects.toMatchObject({code:'GEOMETRY_CLEANUP_FAILED'});expect((h.session as unknown as {active:{responseDebt:Response}}).active.responseDebt).toBe(response);expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();expect(h.session.snapshot().iconsSuspended).toBe(true);
});
it('keeps only the latest B→C unstarted selection while an actual A local Read is held',async()=>{
 const h=setup(),held=h.hold(ok(h.result));
 // Observe the initial authored Read directly; this implementation preserves its held original task.
 const readEntered=defer<void>();vi.mocked(h.bridge.creatureGeometryRead!).mockReset().mockImplementationOnce(()=>{readEntered.resolve();return held.promise;}).mockImplementation(async p=>{const result=structuredClone(h.result);result.slot=p.slot;result.metadata.selected!.slot=p.slot;return ok(result);});
 const intentFor=(slot:number)=>{const anchor=structuredClone(h.intent.anchor);anchor.metadata.selected!.slot=slot;return{...h.intent,selection:{...h.intent.selection,slot},anchor};};
 h.start();await readEntered.promise;h.session.enter(intentFor(1));h.session.enter(intentFor(2));expect(h.bridge.creatureGeometryRead).toHaveBeenCalledTimes(1);held.resolve(ok(h.result));await until(h.session,()=>h.session.snapshot().view?.intent.selection.slot===2&&h.session.snapshot().view?.phase==='receiving');h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);await until(h.session,()=>h.session.snapshot().view?.canMount===true);h.session.attachCanvas(h.session.snapshot().view!.generation,document.createElement('canvas'));await until(h.session,()=>h.session.snapshot().view?.phase==='ready');expect(vi.mocked(h.bridge.creatureGeometryRead!).mock.calls.map(([p])=>p.slot)).toEqual([0,2]);expect(vi.mocked(h.bridge.geometryViewEnter!).mock.calls.map(([p])=>p.selection.slot)).toEqual([0,2]);expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();
});
it('does not start B Prepare when C replaces that waiter behind the surviving A consumer',async()=>{
 const h=setup(),aDone=h.hold(ok({...h.result,state:'noDeclaredTriangles',manifest:null} as CreatureGeometryResult));
 const resultFor=(slot:number,state:'notPrepared'|'noDeclaredTriangles')=>{const result=structuredClone(h.result);result.slot=slot;result.metadata.selected!.slot=slot;result.state=state;result.manifest=null;if(state==='notPrepared')result.sources=[];return result;};
 vi.mocked(h.bridge.creatureGeometryRead!).mockImplementation(async p=>ok(resultFor(p.slot,'notPrepared')));vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValueOnce(aDone.promise).mockImplementation(async p=>ok(resultFor(p.slot,'noDeclaredTriangles')));
 const intentFor=(slot:number)=>{const anchor=structuredClone(h.intent.anchor);anchor.metadata.selected!.slot=slot;return{...h.intent,selection:{...h.intent.selection,slot},anchor};};
 h.start();await until(h.session,()=>h.session.snapshot().operation?.status==='pending');h.session.enter(intentFor(1));await until(h.session,()=>h.session.snapshot().view?.intent.selection.slot===1&&h.session.snapshot().view?.phase==='waiting'&&vi.mocked(h.bridge.geometryViewEnter!).mock.calls.length===2);h.session.enter(intentFor(2));await until(h.session,()=>h.session.snapshot().view?.intent.selection.slot===2&&h.session.snapshot().view?.phase==='waiting'&&vi.mocked(h.bridge.geometryViewEnter!).mock.calls.length===3);aDone.resolve(ok({...h.result,state:'noDeclaredTriangles',manifest:null} as CreatureGeometryResult));await until(h.session,()=>h.session.snapshot().view?.result?.state==='noDeclaredTriangles');expect(vi.mocked(h.bridge.creatureGeometryPrepare!).mock.calls.map(([p])=>p.slot)).toEqual([0,2]);expect(h.bridge.geometryViewOpen).not.toHaveBeenCalled();
});
it.each(['fetch','validator','bodyAck'] as const)('observes late rejected %s before renderer ACK and then awaits actual main release',async stage=>{
 const h=setup(),rejected=h.hold<any>(stage==='fetch'?h.body:stage==='validator'?{state:'refused',code:'cancelled'}:ok({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'b'.repeat(32),geometryArtifactId:h.result.manifest!.artifactId,retired:true}));
 let entered:Promise<void>;
 if(stage==='fetch'){h.fetch.mockReturnValue(rejected.promise);entered=observedCall(h.fetch);}
 else if(stage==='validator'){h.validate.mockReturnValue(rejected.promise);entered=observedCall(h.validate);}
 else{vi.mocked(h.bridge.geometryViewBodyRetired!).mockReturnValue(rejected.promise);entered=observedCall(h.bridge.geometryViewBodyRetired!);}
 h.start();await entered;const leaving=h.session.leave('return');expect(h.bridge.geometryViewSceneDisposed).not.toHaveBeenCalled();rejected.reject(new Error('Authored late task failure'));await leaving;expect(h.scene).not.toHaveBeenCalled();expect(h.bridge.geometryViewSceneDisposed).toHaveBeenCalledWith({viewId:'a'.repeat(32),operationToken:'b'.repeat(32),scope:'view'});expect(h.bridge.geometryViewRelease).toHaveBeenCalledTimes(1);expect(h.session.snapshot().iconsSuspended).toBe(false);
});
it.each(['geometryViewSceneDisposed','geometryViewRelease'] as const)('keeps the presentation reservation blocked on actual %s failure',async port=>{
 const h=setup();h.expectDebt();vi.mocked(h.bridge[port]!).mockResolvedValue({ok:false,error:{code:'GEOMETRY_CLEANUP_FAILED',message:'Authored main retirement uncertainty',retryable:false}});h.start();await mount(h);await expect(h.session.leave('return')).rejects.toMatchObject({code:'GEOMETRY_CLEANUP_FAILED'});expect(h.session.snapshot().retirement?.phase).toBe('blocked');expect(h.session.snapshot().iconsSuspended).toBe(true);expect(h.owner.disposeView).toHaveBeenCalledTimes(1);h.start();await Promise.resolve();expect(h.bridge.geometryViewEnter).toHaveBeenCalledTimes(1);
});
// #62: the Creatures preview may look at kept geometry without preparing it; a deliberate entry on the same selection then prepares.
it('a local-only entry never prepares, stops at not kept, and a later deliberate entry on the same selection prepares',async()=>{
 const h=setup();vi.mocked(h.bridge.creatureGeometryRead!).mockImplementation(async()=>ok(h.missing));
 h.session.enter(h.intent,{localOnly:true});h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);
 await until(h.session,()=>h.session.snapshot().view?.phase==='outcome'&&!h.session.snapshot().retirement);
 const v=h.session.snapshot().view!;expect(v.result?.state).toBe('notPrepared');expect(v.error).toBeNull();expect(v.canMount).toBe(false);
 expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();expect(h.reserve).not.toHaveBeenCalled();
 // Still not kept: the deliberate entry prepares it, once.
 h.session.enter(h.intent);const first=h.session.snapshot().view!.generation;
 await until(h.session,()=>h.session.snapshot().view!.generation!==first);h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);
 await until(h.session,()=>!!h.session.snapshot().view?.canMount);
 expect(h.session.snapshot().view?.result?.state).toBe('ready');expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);expect(h.reserve).toHaveBeenCalledTimes(1);
});
it('a local-only entry of kept geometry draws it without asking for anything else',async()=>{
 const h=setup();h.session.enter(h.intent,{localOnly:true});h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);
 await until(h.session,()=>!!h.session.snapshot().view?.canMount);expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();
});

// #62 QA: a busy engine is never an outcome. A local-only look that can't read is just "not looked", and nothing is remembered,
// so Show model right after can ask again instead of replaying the failure.
it('a busy read during a local-only look leaves no outcome or decision, and a deliberate entry then draws',async()=>{
 const h=setup();vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce({ok:false,error:{code:'CREATURES_QUERY_BUSY',message:'Local Creature display reads are at their bounded capacity or retiring.',retryable:true}});
 h.session.enter(h.intent,{localOnly:true});h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);
 await until(h.session,()=>h.session.snapshot().view?.phase==='outcome'&&!h.session.snapshot().retirement);
 expect(h.session.snapshot().view!.error).toBeNull();expect(h.session.snapshot().view!.result).toBeNull();
 h.session.enter(h.intent);const waiting=h.session.snapshot().view!.generation;await until(h.session,()=>h.session.snapshot().view!.generation!==waiting);
 h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);await until(h.session,()=>!!h.session.snapshot().view?.canMount);
 expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();
});
it('a busy read on a deliberate entry is a retryable outcome that Retry asks again, not a remembered failure',async()=>{
 const h=setup();vi.mocked(h.bridge.creatureGeometryRead!).mockResolvedValueOnce({ok:false,error:{code:'CREATURES_QUERY_BUSY',message:'Busy.',retryable:true}});
 h.start();await until(h.session,()=>h.session.snapshot().view?.phase==='outcome'&&!h.session.snapshot().retirement);
 expect(h.session.snapshot().view!.error?.code).toBe('CREATURES_QUERY_BUSY');
 // Entering again (not only Retry) asks again: no decision replays the busy reply.
 h.session.leave('navigation');await until(h.session,()=>!h.session.snapshot().retirement);h.start();
 await until(h.session,()=>!!h.session.snapshot().view?.canMount);expect(h.bridge.creatureGeometryRead).toHaveBeenCalledTimes(2);
});

it('a local-only look never replays a remembered Stop: it reads what is kept and leaves the decision for Show model',async()=>{
 const h=setup();vi.mocked(h.bridge.creatureGeometryRead!).mockImplementation(async()=>ok(h.missing));const held=defer<BridgeReply<CreatureGeometryResult>>();vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValueOnce(held.promise);
 h.start();await until(h.session,()=>h.session.snapshot().operation?.status==='pending');h.session.stopPreparation(h.session.snapshot().operation!.id);held.resolve({ok:false,error:{code:'CANCELED',message:'Stopped.',retryable:true}});
 await until(h.session,()=>h.session.snapshot().view?.phase==='outcome'&&!h.session.snapshot().retirement);expect(h.session.snapshot().view!.message).toBe('Geometry preparation stopped');
 await h.session.leave('navigation');h.session.enter(h.intent,{localOnly:true});expect(h.session.snapshot().view!.message).not.toBe('Geometry preparation stopped');
 h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);await until(h.session,()=>h.session.snapshot().view?.phase==='outcome'&&!h.session.snapshot().retirement);
 // #99: it really reads; nothing is kept here, so it says so without a remembered error and without preparing.
 const v=h.session.snapshot().view!;expect(v.error).toBeNull();expect(v.result?.state).toBe('notPrepared');expect(v.message).toBe('Geometry is not kept for this display');expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);
 // Show model is a fresh, deliberate request: it prepares again instead of replaying the Stop.
 await until(h.session,()=>!h.session.snapshot().retirement);vi.mocked(h.bridge.creatureGeometryPrepare!).mockResolvedValueOnce(ok(h.result));vi.mocked(h.bridge.creatureGeometryRead!).mockImplementation(async()=>ok(h.result));
 h.session.enter(h.intent,{fresh:true});await until(h.session,()=>!['outcome','waiting'].includes(h.session.snapshot().view?.phase??'outcome'));expect(h.session.snapshot().view!.message).not.toBe('Geometry preparation stopped');
 h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);await until(h.session,()=>!!h.session.snapshot().view?.canMount);
});
