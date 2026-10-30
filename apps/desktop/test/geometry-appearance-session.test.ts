import {afterEach,describe,expect,it,vi} from 'vitest';
import readyJson from '../../../contracts/examples/creature-geometry-ready.json';
import texturedJson from '../../../contracts/examples/creature-geometry-textured.json';
import type {CreatureGeometryAppearance,CreatureGeometryManifest,CreatureGeometryResult} from '../../../contracts/generated/discovery';
import type {BridgeReply,DiscoveryBridge} from '../src/shared/bridge';
import type {GeometrySceneOptions,GeometrySceneOwner} from '../src/renderer/geometry-scene';
import {GeometrySession} from '../src/renderer/geometry-session';
import {captureGeometryAnchor,type GeometryIntent} from '../src/renderer/geometry-state';
import {validateGeometry} from '../src/renderer/geometry-vmg';
import {validateAppearance} from '../src/renderer/geometry-vma';
import {appearanceFixture,geometryCSharpGolden} from './geometry-fixtures';

const ok=<T,>(value:T):BridgeReply<T>=>({ok:true,value});
const sessions:GeometrySession[]=[];
afterEach(async()=>{await Promise.all(sessions.splice(0).map(s=>s.shutdown('applicationExit')));});

function setup(appearanceState:'ready'|'notPrepared'){
 const lines=geometryCSharpGolden(),surfaces=appearanceFixture({positions:[0,0,0,1,0,0,0,0,1,1,1,1],uvs:[0,1,1,1,0,0,1,0]});
 const base=structuredClone(readyJson) as CreatureGeometryResult;
 base.manifest=lines.manifest as CreatureGeometryManifest;base.metadata.modelFileDataId='800';base.metadata.targets[1]!.row!.edgeValue.value='800';base.sources[0]!.fileDataId='800';
 const textured=structuredClone((texturedJson as unknown as CreatureGeometryResult).appearance!) as CreatureGeometryAppearance;
 textured.manifest={...surfaces.manifest,geometryBindingSha256:lines.manifest.bindingSha256,displayId:Number(base.metadata.selected!.displayId)} as unknown as NonNullable<CreatureGeometryAppearance['manifest']>;
 textured.display!.displayId=base.metadata.selected!.displayId;
 const notPrepared:CreatureGeometryAppearance={policy:'creature-textured-appearance-v1',state:'notPrepared',display:textured.display,textures:[],effects:null,manifest:null,problem:null};
 const refused:CreatureGeometryAppearance={...notPrepared,state:'multiTexture',effects:textured.effects,problem:{code:'CREATURE_APPEARANCE_MULTI_TEXTURE',message:'The batch combines more than one texture stage.',retryable:false,batchOrdinal:0,textureOrdinal:null}};
 const read={...base,appearance:appearanceState==='ready'?textured:notPrepared} as CreatureGeometryResult;
 const intent:GeometryIntent={selection:{...base.context,artifactId:base.artifactId,creatureId:base.creatureId,slot:base.slot},anchor:captureGeometryAnchor(base.opening,base.metadata),label:{name:'Authored Creature',buildVersion:'Authored build'},origin:{key:'exact',invoker:null,windowScroll:[0,0],innerScroll:[]}};
 let options:GeometrySceneOptions|null=null;
 const fetch=vi.fn(async(url:string|URL|Request)=>({ok:true,body:null,arrayBuffer:async()=>String(url).endsWith('d'.repeat(32))?surfaces.buffer:lines.buffer}) as unknown as Response);
 const bridge={onProgress:vi.fn(()=>vi.fn()),onGeometryRetirement:vi.fn(()=>vi.fn()),cancel:vi.fn(async()=>ok({requested:true})),
  creatureGeometryRead:vi.fn(async()=>ok(read)),creatureGeometryPrepare:vi.fn(async()=>ok({...base,appearance:refused} as CreatureGeometryResult)),
  geometryViewEnter:vi.fn(async(p:{viewGeneration:number})=>ok({viewId:'a'.repeat(32),viewGeneration:p.viewGeneration})),
  geometryViewOpen:vi.fn(async(p:{viewId:string;geometryArtifactId:string})=>{const surface=p.geometryArtifactId===textured.manifest!.artifactId;
   return ok({viewId:p.viewId,viewGeneration:session.snapshot().view!.generation,operationToken:'b'.repeat(32),url:'vibeminer-geometry://body/'+(surface?'d':'c').repeat(32),manifest:surface?textured.manifest!:base.manifest!,requestLifetimeMs:30000});}),
  geometryViewBodyRetired:vi.fn(async(p:{viewId:string;operationToken:string})=>ok({viewId:p.viewId,viewGeneration:session.snapshot().view!.generation,operationToken:p.operationToken,geometryArtifactId:vi.mocked(bridge.geometryViewOpen!).mock.calls.at(-1)![0].geometryArtifactId,retired:true as const})),
  geometryViewSceneDisposed:vi.fn(async()=>ok({accepted:true as const})),geometryViewRelease:vi.fn(async()=>ok({released:true as const})),
 } as unknown as DiscoveryBridge;
 const owner:GeometrySceneOwner={mount:vi.fn<GeometrySceneOwner['mount']>((_,identity,ack)=>{options!.confirmRetirement(identity,ack);options!.onStatus({code:'drawn'});return{state:'mounted'};}),
  move:vi.fn(()=>({state:'moved' as const})),disposeScene:vi.fn(identity=>({state:'disposed' as const,notice:{viewId:'a'.repeat(32),operationToken:identity?.operationToken??null,scope:'scene' as const}})),
  disposeView:vi.fn(identity=>({state:'disposed' as const,notice:{viewId:'a'.repeat(32),operationToken:identity?.operationToken??null,scope:'view' as const}}))};
 const validate=vi.fn(validateGeometry),validateTextured=vi.fn(validateAppearance);
 const session=new GeometrySession(bridge,{tryReserve:()=>()=>{},revision:0},{fetch,validate,validateAppearance:validateTextured,createScene:(_c,o)=>{options=o;return{state:'available',owner};},requestId:()=>'geometry_test'});
 sessions.push(session);
 return{session,bridge,owner,intent,textured,validate,validateTextured,start:()=>{session.enter(intent);session.iconsHiddenCommitted(session.snapshot().view!.generation);}};
}
function until(session:GeometrySession,predicate:()=>boolean):Promise<void>{if(predicate())return Promise.resolve();return new Promise(resolve=>{const off=session.subscribe(()=>{if(predicate()){off();resolve();}});});}
async function drawn(h:ReturnType<typeof setup>){await until(h.session,()=>!!h.session.snapshot().view?.canMount);h.session.attachCanvas(h.session.snapshot().view!.generation,document.createElement('canvas'));await until(h.session,()=>h.session.snapshot().view?.phase==='ready');}

describe('textured appearance presentation',()=>{
 it('opens and draws the ready VMA1 appearance instead of the line geometry, without a Prepare',async()=>{
  const h=setup('ready');h.start();await drawn(h);
  expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();
  expect(vi.mocked(h.bridge.geometryViewOpen!).mock.calls[0][0].geometryArtifactId).toBe(h.textured.manifest!.artifactId);
  expect(h.validateTextured).toHaveBeenCalledTimes(1);expect(h.validate).not.toHaveBeenCalled();
  expect((vi.mocked(h.owner.mount).mock.calls[0][0] as {kind?:string}).kind).toBe('appearance');
  expect(h.session.snapshot().view?.message).toBe('Unposed textured surfaces drawn');
 });
 it('prepares once when the ready geometry has no retained appearance and keeps the lines for a refusal',async()=>{
  const h=setup('notPrepared');h.start();await drawn(h);
  expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);
  expect(vi.mocked(h.bridge.geometryViewOpen!).mock.calls[0][0].geometryArtifactId).not.toBe(h.textured.manifest!.artifactId);
  expect(h.validate).toHaveBeenCalledTimes(1);expect(h.session.snapshot().view?.result?.appearance?.state).toBe('multiTexture');
  expect(h.session.snapshot().view?.message).toBe('Unposed source edges drawn');
 });
 // #62: the Creatures preview looks at kept geometry only; it never prepares textures that weren't kept.
 it('a local-only entry draws the kept lines without preparing the missing textures',async()=>{
  const h=setup('notPrepared');h.session.enter(h.intent,{localOnly:true});h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);await drawn(h);
  expect(h.bridge.creatureGeometryPrepare).not.toHaveBeenCalled();
  expect(h.session.snapshot().view?.result?.appearance?.state).toBe('notPrepared');expect(h.session.snapshot().view?.message).toBe('Unposed source edges drawn');
 });
 // #99: a remembered Stop of the texture Prepare must not hide the kept lines when the Creatures preview looks again.
 it('a local-only look after a stopped texture Prepare reads and draws the kept lines, while the view itself still replays the Stop',async()=>{
  const h=setup('notPrepared');let finish!:(reply:BridgeReply<CreatureGeometryResult>)=>void;
  vi.mocked(h.bridge.creatureGeometryPrepare!).mockReturnValueOnce(new Promise(r=>{finish=r;}));
  h.start();await until(h.session,()=>h.session.snapshot().operation?.status==='pending');
  h.session.stopPreparation(h.session.snapshot().operation!.id);finish({ok:false,error:{code:'CANCELED',message:'Stopped.',retryable:true}});
  await until(h.session,()=>h.session.snapshot().view?.message==='Geometry preparation stopped'&&!h.session.snapshot().retirement);
  await h.session.leave('return');const reads=vi.mocked(h.bridge.creatureGeometryRead!).mock.calls.length;
  h.session.enter(h.intent,{localOnly:true});expect(h.session.snapshot().view?.message).not.toBe('Geometry is not kept for this display');
  h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);await drawn(h);
  expect(h.bridge.creatureGeometryRead).toHaveBeenCalledTimes(reads+1);expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);
  expect(h.session.snapshot().view?.message).toBe('Unposed source edges drawn');expect(h.session.snapshot().view?.error).toBeNull();
  // The geometry view's own entry still replays the remembered Stop instead of preparing again.
  await h.session.leave('navigation');h.session.enter(h.intent);
  expect(h.session.snapshot().view?.message).toBe('Geometry preparation stopped');expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);
 });
 it('still draws the retained lines when only the texture Prepare fails',async()=>{
  const h=setup('notPrepared');vi.mocked(h.bridge.creatureGeometryPrepare!).mockResolvedValue({ok:false,error:{code:'NETWORK_UNAVAILABLE',message:'Offline.',retryable:true}});
  h.start();await drawn(h);
  expect(h.validate).toHaveBeenCalledTimes(1);expect(h.session.snapshot().view?.result?.appearance?.state).toBe('notPrepared');
 });
 it('Retry textures retires the drawn lines and prepares the textures again',async()=>{
  const h=setup('notPrepared');vi.mocked(h.bridge.creatureGeometryPrepare!).mockResolvedValueOnce({ok:false,error:{code:'NETWORK_UNAVAILABLE',message:'Offline.',retryable:true}});
  h.start();await drawn(h);expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1);
  const generation=h.session.snapshot().view!.generation;h.session.retryAppearance();
  await until(h.session,()=>(h.session.snapshot().view?.generation??0)>generation);h.session.iconsHiddenCommitted(h.session.snapshot().view!.generation);
  await until(h.session,()=>!!h.session.snapshot().view?.canMount);h.session.attachCanvas(h.session.snapshot().view!.generation,document.createElement('canvas'));
  await until(h.session,()=>h.session.snapshot().view?.phase==='ready');
  expect(h.bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(2);expect(h.session.snapshot().view?.result?.appearance?.state).toBe('multiTexture');
 });
});
