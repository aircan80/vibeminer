import {randomUUID} from 'node:crypto';
import {performance} from 'node:perf_hooks';
import type {ItemIconBatch,ItemIconLeaseDescriptor} from '../../../../contracts/generated/discovery';
import {IconImageProtocol,type IconImageRequest} from './icon-image-protocol';
import {EngineFailure,type EngineClient} from './engine-client';
import {validIconBatch,validIconView,iconBatchMatches,type IconBatchParams,type IconBatch} from '../shared/item-icons';
import {validRequestId} from '../shared/bridge';
import {itemsKey} from '../shared/items';
type Engine=Pick<EngineClient,'request'|'capabilities'|'hasFailed'|'hasExited'|'waitForExit'|'shutdown'>;
type ImagePort=Pick<IconImageProtocol,'issue'|'handle'|'revokeDocument'|'dispose'>;
export class IconsController{
 private document=0;private generation=0;private view:string|null=null;private authority:string|null=null;private active:string|null=null;private closed=false;private failed=false;private uncertainOpen=false;
 private presentationOwner:string|null=null;private presentationDrain:Promise<void>|null=null;
 private retiring:Promise<void>=Promise.resolve();private revoking:Promise<void>|null=null;
 private cache=new Map<string,{image:NonNullable<ItemIconBatch['outcomes'][number]['image']>;url:string;expires:number}>();
 get documentToken():number{return this.document;}
 readonly images:ImagePort;
 constructor(private engine:Engine,dataDirectory:string,private now:()=>number=()=>performance.now(),images?:ImagePort,private lifetime=90000){
  this.images=images??new IconImageProtocol({dataDirectory,openIcon:(p,signal)=>this.open(p,signal),closeIcon:id=>this.close(id)});
 }
 private async open(p:IconImageRequest,signal:AbortSignal):Promise<ItemIconLeaseDescriptor>{
  if(signal.aborted)throw new EngineFailure('CANCELED','The image request ended.');
  const id=`icon_open_${randomUUID()}`,cancel=()=>{void this.engine.request('request.cancel',{requestId:id}).catch(()=>{/* Original open remains observed; its terminal determines lease ownership. */});};
  signal.addEventListener('abort',cancel,{once:true});
  try{return await this.engine.request<ItemIconLeaseDescriptor>('items.icon.open',{...p.context,artifactId:p.featureArtifact,itemId:p.itemId,imageArtifactId:p.artifactId},id);}
  catch(error){if(!this.engine.hasExited&&(this.engine.hasFailed||!(error instanceof EngineFailure)||['ENGINE_EXITED','ENGINE_UNAVAILABLE','ENGINE_TIMEOUT','PROTOCOL_ERROR'].includes(error.code))){this.uncertainOpen=true;this.failed=true;}throw error;}
  finally{signal.removeEventListener('abort',cancel);}
 }
 private async close(leaseId:string):Promise<void>{try{await this.engine.request('items.icon.close',{leaseId});}catch(error){if(this.engine.hasExited||await this.engine.waitForExit(1500))return;throw error;}}
 private revoke():Promise<void>{if(this.revoking)return this.revoking;const result=this.retiring.then(()=>this.images.revokeDocument());this.revoking=result;this.retiring=result.catch(()=>{this.failed=true;});void result.then(()=>{if(this.revoking===result)this.revoking=null;},()=>{if(this.revoking===result)this.revoking=null;});return result;}
 /** Synchronous admission fence precedes every awaited image drain. Started source consumers survive. */
 blockPresentation(ownerToken:string):Promise<void>{
  if(!/^[0-9a-f]{32}$/.test(ownerToken))return Promise.reject(new EngineFailure('INVALID_REQUEST','Invalid presentation owner.'));
  if(this.presentationOwner&&this.presentationOwner!==ownerToken)return Promise.reject(new EngineFailure('ICON_PRESENTATION_BLOCKED','Another presentation still owns the image gate.',true));
  if(this.presentationOwner===ownerToken&&this.presentationDrain)return this.presentationDrain;
  this.presentationOwner=ownerToken;
  this.presentationDrain=this.revokeDocument();
  return this.presentationDrain;
 }
 unblockPresentation(ownerToken:string):void{if(this.presentationOwner===ownerToken){this.presentationOwner=null;this.presentationDrain=null;}}
 async batch(params:IconBatchParams,requestId:string,viewId:string):Promise<IconBatch>{
  const started=this.now();
  if(this.presentationOwner)throw new EngineFailure('ICON_PRESENTATION_BLOCKED','Geometry presentation owns the image gate.',true);
  if(!validIconBatch(params,12)||!validIconView(viewId)||!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose at most twelve exact visible Item IDs.');
  if(!this.engine.capabilities.includes('item-icons-v1'))throw new EngineFailure('UNSUPPORTED','Optional Item icons are not supported by this engine.');
  if(this.closed||this.failed)throw new EngineFailure('ICON_DELIVERY_UNAVAILABLE','Verified image delivery is unavailable. Restart VibeMiner to reconnect.');
  if(this.active)throw new EngineFailure('ICON_BATCH_BUSY','The previous icon consumer is still finishing.',true);
  const authority=`${itemsKey(params)}:${params.artifactId}`,document=this.document;this.active=requestId;
  try{
   let generation=this.generation;if(this.view!==viewId){this.view=viewId;this.authority=authority;generation=++this.generation;this.cache.clear();await this.revoke();}
   if(this.presentationOwner||this.closed||this.failed||document!==this.document||generation!==this.generation||this.view!==viewId)throw new EngineFailure('ICON_VIEW_RETIRED','The image view was left before its request started.');
   if(this.authority!==authority)throw new EngineFailure('ICON_VIEW_CONTEXT_MISMATCH','This icon view belongs to another exact Items artifact.');
   const result=await this.engine.request<ItemIconBatch>('items.icons',params,requestId);
   if(!iconBatchMatches(result,params))throw new EngineFailure('ICON_CONTEXT_MISMATCH','The icon outcome differs from the exact requested Items source.');
   if(this.presentationOwner||this.closed||document!==this.document||generation!==this.generation||this.view!==viewId)throw new EngineFailure('ICON_VIEW_RETIRED','Icon preparation finished after its image view was left.');
   const outcomes=result.outcomes.map(outcome=>{
    if(outcome.status!=='ready'||!outcome.image)return{...outcome,image:null};
    const cached=this.cache.get(outcome.itemId),image=outcome.image;
    if(cached&&(cached.image.artifactId!==image.artifactId||cached.image.byteLength!==image.byteLength||cached.image.width!==image.width||cached.image.height!==image.height))throw new EngineFailure('ICON_VIEW_CHANGED','The admitted image changed within this exact view. Reopen the Items page to reassess it.');
    if(!cached&&this.cache.size>=51)throw new EngineFailure('ICON_VIEW_CAPACITY','This image view reached its bounded target limit. Reopen the Items page.');
    // A URL near its end is reissued, so a renderer refresh at its expiry always gets a usable lifetime.
    let url=cached&&cached.expires-this.now()>=this.lifetime/3?cached.url:null;
    if(!url){try{url=this.images.issue({context:result.context,featureArtifact:params.artifactId,itemId:outcome.itemId,artifactId:image.artifactId},image);}catch{this.failed=true;throw new EngineFailure('ICON_DELIVERY_UNAVAILABLE','Verified image delivery is unavailable. Restart VibeMiner to reconnect.');}this.cache.set(outcome.itemId,{image:{...image},url,expires:this.now()+this.lifetime});}
    return{...outcome,image:{width:image.width,height:image.height,byteLength:image.byteLength,url,requestLifetimeMs:Math.min(1500000,Math.floor(this.cache.get(outcome.itemId)!.expires-started))}};
   });
   return{context:result.context,artifactId:result.artifactId,outcomes};
  }finally{if(this.active===requestId)this.active=null;}
 }
 async release(viewId:string):Promise<{released:boolean}>{if(!validIconView(viewId))throw new EngineFailure('INVALID_REQUEST','Invalid icon view.');if(this.view!==viewId)return{released:false};this.view=null;this.authority=null;this.generation++;this.cache.clear();await this.revoke();return{released:true};}
 async revokeDocument():Promise<void>{this.document++;this.generation++;this.view=null;this.authority=null;this.cache.clear();await this.revoke();}
 async handle(request:Request):Promise<Response>{if(this.presentationOwner||this.failed||this.closed)return new Response('Icon image unavailable.',{status:503,headers:{'Content-Type':'text/plain; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}});const response=await this.images.handle(request);if(response.status>=500){this.failed=true;this.cache.clear();}return response;}
 async dispose():Promise<void>{this.closed=true;this.document++;this.generation++;this.view=null;this.authority=null;this.cache.clear();await this.retiring;await this.images.dispose();if(this.uncertainOpen&&!this.engine.hasExited)throw new EngineFailure('ICON_CLEANUP_UNCONFIRMED','Image ownership remains uncertain until the engine exits.');}
}
// Never wait indefinitely for a short image open before giving the engine its bounded shutdown.
export async function retireIconsAndEngine(icons:Pick<IconsController,'dispose'>,engine:Engine):Promise<boolean>{
 let retired=false,failed=false;const cleanup=icons.dispose().then(()=>{retired=true;},()=>{failed=true;});
 await bounded(cleanup,1500);await engine.shutdown();await engine.waitForExit(1500);await bounded(cleanup,1500);
 if(failed&&engine.hasExited){try{await icons.dispose();retired=true;failed=false;}catch{/* Finite uncertain cleanup remains fail closed. */}}
 return retired&&!failed&&engine.hasExited;
}
async function bounded(work:Promise<void>,milliseconds:number):Promise<void>{let timer:ReturnType<typeof setTimeout>;await Promise.race([work,new Promise<void>(resolve=>{timer=setTimeout(resolve,milliseconds);})]);clearTimeout(timer!);}
