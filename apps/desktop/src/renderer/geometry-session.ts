import type {CreatureGeometryResult,EngineProgress} from '../../../../contracts/generated/discovery';
import type {BridgeReply,DiscoveryBridge} from '../shared/bridge';
import {creatureGeometryMatches,geometrySelectionKey,sameGeometryManifest,validGeometryProgress,validGeometryRetirement,validGeometryToken,type GeometryBodyGrant,type GeometryBodyRetired,type GeometrySceneIdentity} from '../shared/creature-geometry';
import {fitGeometryCamera,type GeometryCameraAction} from './geometry-camera';
import {createGeometryScene,type GeometrySceneOwner,type GeometrySceneStatus} from './geometry-scene';
import {validateGeometry,type GeometryValidationResult} from './geometry-vmg';
import {geometryAnchorMatches,sameGeometryAnchor,geometryIntentKey,geometryOutcome,ownGeometryIntent,ownGeometryValue,type GeometryFailure,type GeometryIntent,type GeometryLongAdmission,type GeometryOperation,type GeometrySnapshot,type GeometryViewState} from './geometry-state';

type Validated=Extract<GeometryValidationResult,{state:'valid'}>['value'];
function gate<T>(){let resolve!:(value:T)=>void;return{promise:new Promise<T>(r=>{resolve=r;}),resolve};}
type View={intent:GeometryIntent;request:number;generation:number;revoked:boolean;abort:AbortController;left:ReturnType<typeof gate<void>>;icons:ReturnType<typeof gate<void>>;canvas:ReturnType<typeof gate<void>>;work:Promise<void>;retiring:Promise<void>|null;entered:{viewId:string;viewGeneration:number}|null;enterAttempted:boolean;openAttempted:boolean;observedToken:string|null|undefined;grant:GeometryBodyGrant|null;identity:GeometrySceneIdentity|null;validated:Validated|null;ack:GeometryBodyRetired|null;owner:GeometrySceneOwner|null;confirmed:boolean;mounted:boolean;sceneStatus:GeometrySceneStatus|null;inSceneCall:boolean;debt:boolean;responseDebt:Response|null;iconsCommitted:boolean;pendingCanvas:HTMLCanvasElement|null;mountedCanvas:HTMLCanvasElement|null;canvasTicket:number;canvasWork:Promise<void>|null;canvasDirty:boolean};
type Operation={snapshot:GeometryOperation;result:CreatureGeometryResult|null;promise:Promise<void>;cancellation:Promise<void>|null};
type Decision={kind:'stopped'|'failed';error:GeometryFailure|null};
export interface GeometrySessionDependencies{
 readonly fetch?:typeof fetch;readonly validate?:typeof validateGeometry;readonly createScene?:typeof createGeometryScene;readonly requestId?:()=>string;
}
const unavailable:GeometryFailure=Object.freeze({code:'ENGINE_UNAVAILABLE',message:'Geometry could not finish. Retained source metadata remains usable.',retryable:true});
const failure=(code:string,message:string,retryable=false):GeometryFailure=>Object.freeze({code,message,retryable});
function unwrap<T>(reply:BridgeReply<T>):T{if(!reply.ok)throw reply.error;return reply.value;}
function errorValue(error:unknown):GeometryFailure{
 if(error&&typeof error==='object'&&'code'in error&&'message'in error&&typeof error.code==='string'&&typeof error.message==='string')return failure(error.code.slice(0,64),error.message.slice(0,512),'retryable'in error&&error.retryable===true);
 return unavailable;
}

/** App-session presentation owner. Bodies/scene objects never leave this instance. */
export class GeometrySession{
 private state:GeometrySnapshot=Object.freeze({view:null,operation:null,retirement:null,iconsSuspended:false});
 private readonly listeners=new Set<()=>void>();
 private readonly decisions=new Map<string,Decision>();
 private desired:GeometryIntent|null=null;
 private active:View|null=null;
 private operation:Operation|null=null;
 private generation=0;
 private request=0;
 private admission:GeometryLongAdmission;
 private capacity=gate<void>();
 private offProgress:(()=>void)|null=null;
 private offRetirement:(()=>void)|null=null;
 private closing=false;
 private shutdownTask:Promise<void>|null=null;
 private readonly receive:typeof fetch;
 private readonly validate:typeof validateGeometry;
 private readonly createScene:typeof createGeometryScene;
 private readonly requestId:()=>string;
 constructor(private readonly bridge:DiscoveryBridge,admission:GeometryLongAdmission,dependencies:GeometrySessionDependencies={}){
  this.admission=admission;this.receive=dependencies.fetch??((...args)=>fetch(...args));this.validate=dependencies.validate??validateGeometry;this.createScene=dependencies.createScene??createGeometryScene;this.requestId=dependencies.requestId??(()=>`geometry_${crypto.randomUUID()}`);
 }
 readonly snapshot=():GeometrySnapshot=>this.state;
 readonly subscribe=(listener:()=>void):(()=>void)=>{this.listeners.add(listener);this.connect();return()=>{this.listeners.delete(listener);this.detachIfIdle();};};
 private publish(patch:Partial<GeometrySnapshot>){this.state=Object.freeze({...this.state,...patch});for(const listener of this.listeners){try{listener();}catch{/* A view observer cannot release owned resources. */}}}
 private viewState(v:View,patch:Partial<GeometryViewState>){if(this.state.view?.generation===v.generation)this.publish({view:Object.freeze({...this.state.view,...patch})});}
 private connect(){if(!this.offProgress)this.offProgress=this.bridge.onProgress(p=>this.progress(p));if(!this.offRetirement&&this.bridge.onGeometryRetirement)this.offRetirement=this.bridge.onGeometryRetirement(n=>{
   const v=this.active;if(!v||!v.enterAttempted||!validGeometryRetirement(n)||n.viewGeneration!==v.generation||v.entered&&n.viewId!==v.entered.viewId||n.operationToken!==null&&v.grant&&n.operationToken!==v.grant.operationToken)return;
   if(!v.entered)v.entered=Object.freeze({viewId:n.viewId,viewGeneration:n.viewGeneration});
   if(v.observedToken!==undefined&&v.observedToken!==n.operationToken){v.debt=true;}
   v.observedToken=n.operationToken;
   if(v.revoked)return;
   this.viewState(v,{phase:'outcome',canMount:false,error:failure('GEOMETRY_SOURCE_CHANGED','This geometry presentation is no longer current. Return to the selected metadata and reassess its source.',true),message:'Geometry presentation is no longer current'});
   this.remember(v.intent,{kind:'failed',error:this.state.view?.error??null});void this.retire(v);
  });}
 private detachIfIdle(){if(this.listeners.size||this.active||this.operation&&['pending','stopping'].includes(this.operation.snapshot.status))return;this.offProgress?.();this.offProgress=null;this.offRetirement?.();this.offRetirement=null;}
 updateAdmission(admission:GeometryLongAdmission){const changed=admission.revision!==this.admission.revision;this.admission=admission;if(changed){const old=this.capacity;this.capacity=gate<void>();old.resolve();}}
 private progress(progress:EngineProgress){const op=this.operation;if(!op||!['pending','stopping'].includes(op.snapshot.status)||progress.requestId!==op.snapshot.id||progress.sequence<=(op.snapshot.progress?.sequence??0)||!validGeometryProgress(progress))return;op.snapshot=Object.freeze({...op.snapshot,progress:ownGeometryValue(progress)});this.publish({operation:op.snapshot});const v=this.active;if(v&&!v.revoked&&geometryIntentKey(v.intent)===geometryIntentKey(op.snapshot.intent))this.viewState(v,{message:progress.stage});}
 enter(input:GeometryIntent):void{
  if(this.closing)return;let intent:GeometryIntent;try{intent=ownGeometryIntent(input);}catch{ return; }
  if(this.active&&!this.active.revoked&&geometryIntentKey(this.active.intent)===geometryIntentKey(intent)&&this.desired){
   if(sameGeometryAnchor(this.active.intent.anchor,intent.anchor))return;
   const previous=this.active,error=failure('GEOMETRY_SOURCE_CHANGED','The selected display evidence changed. Reassess metadata before entering geometry.',true);this.request++;this.desired=intent;this.failView(previous,error);void this.retire(previous);return;
  }
  this.request++;this.desired=intent;const previous=this.active;
  if(previous){void this.retire(previous);this.publish({view:Object.freeze({intent,generation:++this.generation,phase:'waiting',message:'Waiting for the previous geometry view to close',result:null,error:null,canMount:false,sections:[],sectionOffset:0})});return;}
  this.startDesired();
 }
 private startDesired(){
  const intent=this.desired;if(!intent||this.active||this.closing)return;
  const decision=this.decisions.get(geometryIntentKey(intent));
  if(decision||this.decisions.size>=256){const error=decision?.error??(decision?null:failure('GEOMETRY_CONTROL_RESOURCE_LIMIT','The session limit for remembered stopped or failed geometry requests was reached. Existing decisions are preserved.',false));this.publish({view:Object.freeze({intent,generation:++this.generation,phase:'outcome',message:decision?.kind==='stopped'?'Geometry preparation stopped':'Geometry needs attention',result:null,error,canMount:false,sections:[],sectionOffset:0})});return;}
  const previous=this.operation;
  if(previous&&geometryIntentKey(previous.snapshot.intent)===geometryIntentKey(intent)&&previous.snapshot.status==='succeeded'&&previous.result&&previous.result.state!=='ready'){
   if(!creatureGeometryMatches(previous.result,intent.selection)||!geometryAnchorMatches(intent.anchor,previous.result)){
    const error=failure('GEOMETRY_SOURCE_CHANGED','The retained geometry outcome does not match the current selected display evidence. Reassess this exact source.',true);this.remember(intent,{kind:'failed',error});this.publish({view:Object.freeze({intent,generation:++this.generation,phase:'outcome',message:'Geometry needs attention',result:null,error,canMount:false,sections:[],sectionOffset:0})});return;
   }
   this.publish({view:Object.freeze({intent,generation:++this.generation,phase:'outcome',message:geometryOutcome[previous.result.state],result:previous.result,error:previous.result.problem,canMount:false,sections:[],sectionOffset:0})});return;
  }
  this.connect();const v:View={intent,request:this.request,generation:++this.generation,revoked:false,abort:new AbortController(),left:gate<void>(),icons:gate<void>(),canvas:gate<void>(),work:Promise.resolve(),retiring:null,entered:null,enterAttempted:false,openAttempted:false,observedToken:undefined,grant:null,identity:null,validated:null,ack:null,owner:null,confirmed:false,mounted:false,sceneStatus:null,inSceneCall:false,debt:false,responseDebt:null,iconsCommitted:false,pendingCanvas:null,mountedCanvas:null,canvasTicket:0,canvasWork:null,canvasDirty:false};
  this.active=v;this.publish({iconsSuspended:true,view:Object.freeze({intent,generation:v.generation,phase:'entering',message:'Opening geometry view',result:null,error:null,canMount:false,sections:[],sectionOffset:0})});
  // Install the owned promise before invoking any external port, including synchronous callbacks.
  v.work=Promise.resolve().then(()=>this.run(v)).catch(e=>{if(!v.revoked)this.failView(v,errorValue(e));}).finally(()=>{if(!v.mounted||v.revoked)void this.retire(v);});
 }
 private current(v:View){return this.active===v&&!v.revoked&&!this.closing&&this.desired!==null&&this.request===v.request&&geometryIntentKey(this.desired)===geometryIntentKey(v.intent);}
 private requireCurrent(v:View){if(!this.current(v))throw failure('GEOMETRY_VIEW_LEFT','The geometry view was left.',false);}
 private accept(v:View,result:CreatureGeometryResult):CreatureGeometryResult{
  if(!creatureGeometryMatches(result,v.intent.selection)||!geometryAnchorMatches(v.intent.anchor,result))throw failure('GEOMETRY_SOURCE_CHANGED','The original selected display evidence changed. Return to metadata and reassess this exact source.',true);
  return ownGeometryValue(result);
 }
 private async run(v:View){
  this.requireCurrent(v);v.enterAttempted=true;
  const entered=unwrap(await this.bridge.geometryViewEnter!({selection:v.intent.selection,viewGeneration:v.generation}));
  if(v.entered&&v.entered.viewId!==entered.viewId)v.debt=true;v.entered=entered;
  if(!validGeometryToken(v.entered.viewId)||v.entered.viewGeneration!==v.generation)throw failure('GEOMETRY_RESULT_INVALID','The entered geometry view did not match this presentation.');
  this.requireCurrent(v);this.viewState(v,{phase:'checking',message:'Checking retained geometry'});
  let result=this.accept(v,unwrap(await this.bridge.creatureGeometryRead!(v.intent.selection)));this.requireCurrent(v);
  if(result.state==='notPrepared'){
   let op=this.operation;
   if(op&&geometryIntentKey(op.snapshot.intent)===geometryIntentKey(v.intent)&&op.snapshot.status==='succeeded'&&op.result?.state==='ready')throw failure('GEOMETRY_NOT_PREPARED','Previously prepared geometry is no longer locally ready. Reassess this source and Retry explicitly.',true);
   // At most one already-started operation; a different intent stays one withdrawable waiter.
   while(op&&['pending','stopping'].includes(op.snapshot.status)&&geometryIntentKey(op.snapshot.intent)!==geometryIntentKey(v.intent)){
    this.viewState(v,{phase:'waiting',message:'Waiting for the current geometry preparation'});
    await Promise.race([op.promise,v.left.promise]);this.requireCurrent(v);op=this.operation;
   }
   if(op&&geometryIntentKey(op.snapshot.intent)===geometryIntentKey(v.intent)&&['pending','stopping'].includes(op.snapshot.status)){
    this.viewState(v,{phase:op.snapshot.status==='stopping'?'stopping':'preparing',message:op.snapshot.progress?.stage??'Preparing required geometry sources'});
    await Promise.race([op.promise,v.left.promise]);this.requireCurrent(v);
    if(op.snapshot.status!=='succeeded'||!op.result)throw op.snapshot.error??failure('CANCELED','Geometry preparation stopped.',true);
    result=op.result;
    if(result.state==='ready'){this.viewState(v,{phase:'checking',message:'Checking current retained geometry'});result=this.accept(v,unwrap(await this.bridge.creatureGeometryRead!(v.intent.selection)));this.requireCurrent(v);}
   }else{
    let release:(()=>void)|null=null;
    while(!(release=this.admission.tryReserve())){this.viewState(v,{phase:'waiting',message:'Waiting for a Creature preparation credit'});const wake=this.capacity.promise;await Promise.race([wake,v.left.promise]);this.requireCurrent(v);}
    op=this.startPreparation(v.intent,release);this.viewState(v,{phase:'preparing',message:'Preparing required geometry sources'});
    await Promise.race([op.promise,v.left.promise]);this.requireCurrent(v);
    if(op.snapshot.status!=='succeeded'||!op.result)throw op.snapshot.error??failure('CANCELED','Geometry preparation stopped.',true);result=op.result;
   }
  }
  this.requireCurrent(v);result=this.accept(v,result);this.viewState(v,{result,message:geometryOutcome[result.state],phase:'outcome'});
  if(result.state!=='ready'){
   if(result.state==='notPrepared')throw failure('GEOMETRY_NOT_PREPARED','Geometry remains unprepared after the requested preparation. Retry explicitly after reviewing this source.',true);
   if(result.problem)this.remember(v.intent,{kind:'failed',error:result.problem});return;
  }
  this.viewState(v,{phase:'receiving',message:'Receiving verified geometry'});
  v.openAttempted=true;
  const grant=unwrap(await this.bridge.geometryViewOpen!({viewId:v.entered.viewId,geometryArtifactId:result.manifest!.artifactId}));
  // Preserve actual minted identity before inspecting the rest of a late/invalid reply.
  if(v.observedToken!==undefined&&v.observedToken!==grant.operationToken)v.debt=true;v.grant=grant;v.observedToken=grant.operationToken;
  if(!validGeometryToken(grant.operationToken)||grant.viewId!==v.entered.viewId||grant.viewGeneration!==v.generation||!sameGeometryManifest(grant.manifest,result.manifest)||!/^vibeminer-geometry:\/\/body\/[0-9a-f]{32}$/.test(grant.url))throw failure('GEOMETRY_RESULT_INVALID','Geometry body grant did not match this exact current view.');
  v.identity=Object.freeze({viewId:grant.viewId,viewGeneration:grant.viewGeneration,operationToken:grant.operationToken,geometryArtifactId:grant.manifest.artifactId});
  this.requireCurrent(v);await Promise.race([v.icons.promise,v.left.promise]);this.requireCurrent(v);
  let response:Response|null=await this.receive(grant.url,{credentials:'omit',redirect:'error',cache:'no-store',signal:v.abort.signal});
  let buffer:ArrayBuffer|null=null;
  try{
   this.requireCurrent(v);if(!response.ok)throw failure('GEOMETRY_BODY_UNAVAILABLE','Verified geometry bytes could not be received.',true);
   const body=response;response=null;buffer=await body.arrayBuffer();this.requireCurrent(v);
  }finally{
   if(response?.body){try{await response.body.cancel();}catch{v.debt=true;v.responseDebt=response;}}
   response=null;
  }
  this.viewState(v,{phase:'validating',message:'Checking geometry framing'});
  const validated=await this.validate(buffer,grant.manifest,{signal:v.abort.signal,isCurrent:()=>this.current(v)});buffer=null;this.requireCurrent(v);
  if(validated.state!=='valid')throw failure('GEOMETRY_BODY_INVALID','Geometry framing could not be validated.',false);
  v.validated=validated.value;
  if(fitGeometryCamera(grant.manifest.bounds).state!=='ready')throw failure('GEOMETRY_CAMERA_UNSUPPORTED','This source cannot be framed safely by the geometry camera.',false);
  const ack=unwrap(await this.bridge.geometryViewBodyRetired!({viewId:grant.viewId,operationToken:grant.operationToken}));
  if(!this.sameIdentity(v.identity,ack)||ack.retired!==true)throw failure('GEOMETRY_RETIREMENT_INVALID','Geometry body retirement did not match the actual grant.');
  v.ack=ack;this.requireCurrent(v);
  this.viewState(v,{canMount:true,phase:'validating',message:'Drawing geometry'});
  await Promise.race([v.canvas.promise,v.left.promise]);this.requireCurrent(v);
 }
 private startPreparation(intent:GeometryIntent,release:()=>void):Operation{
  const op:Operation={snapshot:Object.freeze({id:this.requestId(),intent,status:'pending',progress:null,error:null}),result:null,promise:Promise.resolve(),cancellation:null};this.operation=op;this.publish({operation:op.snapshot});
  op.promise=Promise.resolve().then(async()=>{
   let terminal:GeometryOperation=op.snapshot;
   try{const result=unwrap(await this.bridge.creatureGeometryPrepare!(intent.selection,op.snapshot.id));
    if(!creatureGeometryMatches(result,intent.selection)||!geometryAnchorMatches(intent.anchor,result))throw failure('GEOMETRY_SOURCE_CHANGED','Geometry preparation no longer matches the original selected display evidence.',true);
    if(op.snapshot.status==='stopping')throw failure('CANCELED','Geometry preparation stopped. Verified source work is preserved.',true);
    op.result=ownGeometryValue(result);if(result.problem)this.remember(intent,{kind:'failed',error:result.problem});
    terminal=Object.freeze({...op.snapshot,status:'succeeded',error:null});
   }catch(e){const error=op.snapshot.status==='stopping'?failure('CANCELED','Geometry preparation stopped. Verified source work is preserved.',true):errorValue(e);this.remember(intent,{kind:error.code==='CANCELED'?'stopped':'failed',error});terminal=Object.freeze({...op.snapshot,status:error.code==='CANCELED'?'canceled':'failed',error});}
   finally{if(op.cancellation)await op.cancellation;release();op.snapshot=Object.freeze({...op.snapshot,status:terminal.status,error:terminal.error});this.publish({operation:op.snapshot});if(this.state.view&&geometryIntentKey(this.state.view.intent)===geometryIntentKey(intent)&&this.state.view.phase==='stopping')this.publish({view:Object.freeze({...this.state.view,phase:'outcome',message:'Geometry preparation stopped',error:op.snapshot.error})});this.detachIfIdle();}
  });return op;
 }
 private sameIdentity(a:GeometrySceneIdentity,b:GeometrySceneIdentity){return a.viewId===b.viewId&&a.viewGeneration===b.viewGeneration&&a.operationToken===b.operationToken&&a.geometryArtifactId===b.geometryArtifactId;}
 private remember(intent:GeometryIntent,decision:Decision){const key=geometryIntentKey(intent);if(this.decisions.has(key)||this.decisions.size<256)this.decisions.set(key,decision);}
 private failView(v:View,error:GeometryFailure){this.remember(v.intent,{kind:'failed',error});this.viewState(v,{phase:'outcome',message:error.code==='CANCELED'?'Geometry preparation stopped':'Geometry needs attention',error,canMount:false});}
 iconsHiddenCommitted(generation:number){const v=this.active;if(v?.generation===generation&&!v.revoked){v.iconsCommitted=true;v.icons.resolve();}}
 attachCanvas(generation:number,canvas:HTMLCanvasElement):()=>void{
  const v=this.active;if(!v||v.generation!==generation||!this.current(v)||!v.iconsCommitted||!v.validated||!v.ack||!v.identity)return()=>{};
  const ticket=++v.canvasTicket;v.pendingCanvas=canvas;this.scheduleCanvas(v);
  return()=>{if(ticket===v.canvasTicket){v.pendingCanvas=null;this.scheduleCanvas(v);}};
 }
 private scheduleCanvas(v:View){
  v.canvasDirty=true;if(v.canvasWork)return;
  // Coalesce React's synchronous ref rehearsal into its committed attachment. One owned task, no timer/retry queue.
  v.canvasWork=Promise.resolve().then(()=>{
   v.canvasDirty=false;if(!this.current(v))return;const canvas=v.pendingCanvas;
   if(!canvas){void this.retire(v);return;}
   if(v.owner){if(v.mountedCanvas!==canvas){this.failView(v,failure('GEOMETRY_CANVAS_CHANGED','The geometry canvas changed and must retire before another view.'));void this.retire(v);}return;}
   this.mountCanvas(v,canvas);
  }).finally(()=>{v.canvasWork=null;if(v.canvasDirty&&this.current(v))this.scheduleCanvas(v);});
 }
 private mountCanvas(v:View,canvas:HTMLCanvasElement){
  if(!this.current(v)||!v.validated||!v.ack||!v.identity)return;
  v.inSceneCall=true;
  try{
   const created=this.createScene(canvas,{viewId:v.identity.viewId,viewGeneration:v.generation,isCurrent:()=>this.current(v),confirmRetirement:(identity,reply)=>{if(v.confirmed||reply!==v.ack||!v.identity||!this.sameIdentity(v.identity,identity)||!this.current(v))return false;v.confirmed=true;return true;},onStatus:status=>{v.sceneStatus=status;if(!v.inSceneCall)this.sceneStatus(v,status);}});
   v.owner=created.state==='available'?created.owner:created.owner??null;v.mountedCanvas=v.owner?canvas:null;
   if(created.state!=='available'){if(created.code==='cleanupFailed')v.debt=true;throw failure('GEOMETRY_SCENE_'+created.code.toUpperCase(),'Geometry could not be drawn safely.',created.code!=='cleanupFailed');}
   if(!this.current(v)||v.sceneStatus&&['contextLost','stale','renderFailed','cleanupFailed'].includes(v.sceneStatus.code)){if(v.sceneStatus?.code==='cleanupFailed')v.debt=true;throw failure('GEOMETRY_SCENE_RETIRED','The graphics owner retired during creation.',true);}
   const mounted=v.owner!.mount(v.validated,v.identity,v.ack);
   if(mounted.state!=='mounted'){if(mounted.code==='cleanupFailed')v.debt=true;throw failure('GEOMETRY_SCENE_'+mounted.code.toUpperCase(),'Geometry could not be drawn safely.',false);}
   v.mounted=true;
  }catch(e){this.failView(v,errorValue(e));void this.retire(v);}
  finally{v.inSceneCall=false;v.canvas.resolve();if(v.sceneStatus)this.sceneStatus(v,v.sceneStatus);}
 }
 private sceneStatus(v:View,status:GeometrySceneStatus){
  if(status.code==='drawn'){if(this.current(v)&&v.mounted)this.viewState(v,{phase:'ready',message:'Unposed source edges drawn'});return;}
  if(status.code==='cameraFitUnsupported'&&v.mounted){this.viewState(v,{message:'That camera move could not be framed safely; the previous view is preserved'});return;}
  if(status.code==='cleanupFailed')v.debt=true;
  this.failView(v,failure('GEOMETRY_'+status.code.toUpperCase(),status.code==='contextLost'?'The graphics context was lost. Source metadata remains available.':'The geometry presentation needs attention.',status.code!=='cleanupFailed'));void this.retire(v);
 }
 move(action:GeometryCameraAction){const v=this.active;if(v&&this.current(v)&&this.state.view?.phase==='ready')v.owner?.move(action);}
 sectionPage(offset:number){const v=this.active;if(!v||!this.current(v)||!v.validated||this.state.view?.phase!=='ready'||!Number.isSafeInteger(offset)||offset<0||offset>=v.validated.manifest.sectionCount)return;this.viewState(v,{sections:ownGeometryValue(v.validated.sectionPage(offset,16)),sectionOffset:offset});}
 private retire(v:View):Promise<void>{
  if(v.retiring)return v.retiring;
  v.revoked=true;v.abort.abort();v.left.resolve();this.publish({retirement:Object.freeze({intent:v.intent,phase:'releasing',error:null})});this.viewState(v,{canMount:false,sections:[],message:this.state.view?.phase==='ready'?'Closing geometry view':this.state.view?.message??'Closing geometry view'});
  v.retiring=Promise.resolve().then(async()=>{
   await v.work;if(v.canvasWork)await v.canvasWork;
   try{
    if(v.enterAttempted&&!v.entered||v.openAttempted&&!v.grant&&v.observedToken===undefined)v.debt=true;
    if(v.debt)throw failure('GEOMETRY_CLEANUP_FAILED','Geometry cleanup could not be confirmed. Other source browsing remains available.');
    if(v.entered){
     const disposal=v.owner?.disposeView(v.identity)??{state:'disposed' as const,notice:{viewId:v.entered.viewId,operationToken:v.grant?.operationToken??v.observedToken??null,scope:'view' as const}};
     if(disposal.state!=='disposed')throw failure('GEOMETRY_CLEANUP_FAILED','The graphics owner could not confirm actual retirement.');
     v.owner=null;v.mountedCanvas=null;v.pendingCanvas=null;v.validated=null;v.ack=null;
     unwrap(await this.bridge.geometryViewSceneDisposed!(disposal.notice));
     unwrap(await this.bridge.geometryViewRelease!({viewId:v.entered.viewId}));
    }
    if(this.active===v)this.active=null;
    this.publish({retirement:null,iconsSuspended:false});this.detachIfIdle();
    // A textual outcome/Stop remains selected; a different latest intent may now enter.
    if(this.desired&&this.request!==v.request)this.startDesired();
   }catch(e){v.debt=true;const error=errorValue(e);this.publish({retirement:Object.freeze({intent:v.intent,phase:'blocked',error}),iconsSuspended:true});this.viewState(v,{phase:'blocked',error,canMount:false,message:'Geometry cleanup needs attention'});throw error;}
  });
  // This owner retains the original faulted promise; all fire-and-forget callers observe it.
  void v.retiring.catch(()=>{});return v.retiring;
 }
 async leave(_reason:'return'|'navigation'|'source'|'unmount'):Promise<void>{this.desired=null;this.publish({view:null});const v=this.active;if(v)await this.retire(v);this.detachIfIdle();}
 retry():void{if(this.closing||this.active||!this.desired||this.state.retirement)return;const intent=this.desired;this.decisions.delete(geometryIntentKey(intent));if(this.operation&&geometryIntentKey(this.operation.snapshot.intent)===geometryIntentKey(intent)&&!['pending','stopping'].includes(this.operation.snapshot.status)){this.operation=null;this.publish({operation:null});}this.request++;this.startDesired();}
 stopPreparation(requestId?:string):void{
  const op=this.operation,v=this.active;if(requestId!==undefined&&op?.snapshot.id!==requestId)return;
  const intent=requestId!==undefined?op!.snapshot.intent:this.desired;
  if(!intent)return;this.remember(intent,{kind:'stopped',error:null});
  if(op&&geometryIntentKey(op.snapshot.intent)===geometryIntentKey(intent)&&op.snapshot.status==='pending'){
   op.snapshot=Object.freeze({...op.snapshot,status:'stopping'});this.publish({operation:op.snapshot});
   op.cancellation=Promise.resolve().then(()=>this.bridge.cancel(op.snapshot.id)).then(reply=>{if(!reply.ok){op.snapshot=Object.freeze({...op.snapshot,error:reply.error});this.publish({operation:op.snapshot});}},()=>{op.snapshot=Object.freeze({...op.snapshot,error:unavailable});this.publish({operation:op.snapshot});});
  }
  if(v&&geometryIntentKey(v.intent)===geometryIntentKey(intent)){this.viewState(v,{phase:op?.snapshot.status==='stopping'?'stopping':'outcome',message:op?.snapshot.status==='stopping'?'Stopping geometry preparation…':'Geometry preparation stopped',canMount:false});void this.retire(v);}
 }
 dismissActivity(){if(this.state.retirement||this.operation&&['pending','stopping'].includes(this.operation.snapshot.status))return;this.operation=null;this.publish({operation:null});this.detachIfIdle();}
 shutdown(_reason:'documentReplaced'|'bridgeClosing'|'applicationExit'):Promise<void>{
  if(this.shutdownTask)return this.shutdownTask;this.closing=true;this.desired=null;
  const op=this.operation;if(op&&op.snapshot.status==='pending')this.stopPreparation(op.snapshot.id);
  const v=this.active;this.shutdownTask=(async()=>{const results=await Promise.allSettled([v?this.retire(v):Promise.resolve(),op?.promise??Promise.resolve()]);this.decisions.clear();this.offProgress?.();this.offProgress=null;this.offRetirement?.();this.offRetirement=null;const bad=results.find(r=>r.status==='rejected');if(bad?.status==='rejected')throw bad.reason;})();void this.shutdownTask.catch(()=>{});return this.shutdownTask;
 }
}
