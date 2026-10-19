import {randomBytes,createHash} from 'node:crypto';
import {constants} from 'node:fs';
import {lstat,realpath,open,type FileHandle} from 'node:fs/promises';
import {isAbsolute,join,resolve} from 'node:path';
import {performance} from 'node:perf_hooks';
import type {CreatureGeometryLeaseDescriptor} from '../../../../contracts/generated/discovery';
import {geometryLeaseMatches,sameGeometryManifest,snapshotGeometryManifest,validGeometryParams,validGeometryToken,type GeometryParams,type GeometryOpenParams,type GeometrySceneIdentity,type GeometryBodyGrant,type ReadonlyGeometryManifest} from '../shared/creature-geometry';
import {authorizedGeometryRequest} from './geometry-provenance';
import {EngineFailure} from './engine-client';
const WINDOW=65536,LIFETIME=30000;
interface Options{dataDirectory:string;openGeometry(request:GeometryOpenParams,signal:AbortSignal):Promise<CreatureGeometryLeaseDescriptor>;closeGeometry(leaseId:string):Promise<void>;hasEngineExited():boolean;now?():number;}
interface RecordState{
 identity:GeometrySceneIdentity;selection:GeometryParams;manifest:ReadonlyGeometryManifest;capability:string;deadline:number;current():boolean;
 abort:AbortController;timer:ReturnType<typeof setTimeout>;used:boolean;worker:Promise<void>|null;workerDone:boolean;
 file:FileHandle|null;leaseId:string|null;uncertainOpen:boolean;cleanupFailed:boolean;fileCloseFailed:boolean;
 bytes:Buffer|undefined;body:ReadableStreamDefaultController<Uint8Array>|null;bodyDone:boolean;delivered:boolean;done:Promise<void>;resolve():void;reject(error:Error):void;settled:boolean;
}
class Refusal extends Error{constructor(readonly status:number){super('Geometry body unavailable.');}}
const cleanup=()=>new EngineFailure('CREATURE_GEOMETRY_CLEANUP_FAILED','Geometry delivery did not retire safely. Its presentation remains reserved.',true);
/** One verified app-format snapshot/body, zero queue; no game parser, acquisition or caller paths. */
export class GeometryBinaryProtocol{
 private records=new Map<string,RecordState>();private active:RecordState|null=null;private closed=false;private failed=false;
 private root:string;private now:()=>number;
 constructor(private readonly options:Options,private readonly openFile:typeof open=open){if(!isAbsolute(options.dataDirectory))throw new TypeError('Geometry storage requires an absolute app-owned directory.');this.root=join(resolve(options.dataDirectory),'objects-v1');this.now=options.now??(()=>performance.now());}
 get stats(){return{records:this.records.size,active:this.active!==null,snapshotBytes:[...this.records.values()].reduce((n,r)=>n+(r.bytes?.length??0),0),fileDebt:[...this.records.values()].filter(r=>r.file!==null).length,engineDebt:[...this.records.values()].filter(r=>r.leaseId!==null||r.uncertainOpen).length,failed:this.failed};}
 issue(identity:GeometrySceneIdentity,selection:GeometryParams,manifest:ReadonlyGeometryManifest,current:()=>boolean):GeometryBodyGrant{
  if(this.closed||this.failed)throw cleanup();if(this.records.size>=2||this.active)throw new EngineFailure('CREATURE_GEOMETRY_BUSY','A geometry body is still opening or retiring.',true);
  if(!validGeometryParams(selection)||!validGeometryToken(identity.viewId)||!validGeometryToken(identity.operationToken)||!Number.isSafeInteger(identity.viewGeneration)||identity.viewGeneration<1||identity.geometryArtifactId!==manifest.artifactId||this.records.has(identity.operationToken)||!current())throw new EngineFailure('INVALID_REQUEST','Choose the current geometry view and exact ready artifact.');
  const owned=snapshotGeometryManifest(manifest),capability=randomBytes(16).toString('hex'),deadline=this.now()+LIFETIME;let accept!:()=>void,refuse!:(e:Error)=>void;
  const done=new Promise<void>((yes,no)=>{accept=yes;refuse=no;});void done.catch(()=>{});
  const r:RecordState={identity:Object.freeze({...identity}),selection:{...selection},manifest:owned,capability,deadline,current,abort:new AbortController(),timer:null as unknown as ReturnType<typeof setTimeout>,used:false,worker:null,workerDone:false,file:null,leaseId:null,uncertainOpen:false,cleanupFailed:false,fileCloseFailed:false,bytes:undefined,body:null,bodyDone:false,delivered:false,done,resolve:accept,reject:refuse,settled:false};
  r.timer=setTimeout(()=>r.abort.abort(),LIFETIME);r.timer.unref?.();r.abort.signal.addEventListener('abort',()=>{if(!r.worker){r.workerDone=true;this.finishBody(r,false);}else if(r.body)this.finishBody(r,false);},{once:true});this.records.set(identity.operationToken,r);
  return Object.freeze({viewId:identity.viewId,viewGeneration:identity.viewGeneration,operationToken:identity.operationToken,url:`vibeminer-geometry://body/${capability}`,manifest:owned,requestLifetimeMs:LIFETIME});
 }
 async handle(request:Request):Promise<Response>{
  let r:RecordState;try{
   if(this.closed||this.failed)throw new Refusal(503);if(request.method!=='GET')throw new Refusal(405);if(request.headers.has('range'))throw new Refusal(416);if(['authorization','proxy-authorization','cookie','cookie2'].some(h=>request.headers.has(h))||!authorizedGeometryRequest(request))throw new Refusal(403);
   const match=/^vibeminer-geometry:\/\/body\/([0-9a-f]{32})$/.exec(request.url);if(!match)throw new Refusal(404);
   const found=[...this.records.values()].find(v=>v.capability===match[1]);if(!found||found.used)throw new Refusal(404);r=found;this.check(r);if(this.active)throw new Refusal(429);if(request.signal.aborted)throw new Refusal(499);
   // Consumption and the one active-body credit happen synchronously, before engine open.
   r.used=true;this.active=r;
  }catch(error){return failure(error);}
  const abort=()=>r.abort.abort();request.signal.addEventListener('abort',abort,{once:true});
  const response=Promise.resolve().then(()=>this.serve(r));
  const finished=()=>{r.workerDone=true;request.signal.removeEventListener('abort',abort);this.complete(r);};
  r.worker=response.then(finished,finished);
  return response;
 }
 private check(r:RecordState){if(this.closed||r.abort.signal.aborted||!r.current()||this.now()>=r.deadline)throw new Refusal(410);}
 private async serve(r:RecordState):Promise<Response>{
  try{
   this.check(r);const parameters={...r.selection,geometryArtifactId:r.manifest.artifactId};let lease:CreatureGeometryLeaseDescriptor;
   try{lease=await this.options.openGeometry(parameters,r.abort.signal);}catch(error){if((!(error instanceof EngineFailure)||error.code.endsWith('CLEANUP_FAILED')||['ENGINE_EXITED','ENGINE_UNAVAILABLE','ENGINE_TIMEOUT','PROTOCOL_ERROR'].includes(error.code))&&!this.options.hasEngineExited()){r.uncertainOpen=true;r.cleanupFailed=true;this.failed=true;}throw error;}
   if(lease&&validGeometryToken(lease.leaseId))r.leaseId=lease.leaseId;
   try{
    if(!r.leaseId&&!this.options.hasEngineExited()){r.uncertainOpen=true;r.cleanupFailed=true;this.failed=true;throw new Refusal(502);}
    this.check(r);if(!geometryLeaseMatches(lease,parameters)||!sameGeometryManifest(lease.manifest,r.manifest))throw new Refusal(502);
    r.bytes=await this.copy(r);this.check(r);
   }finally{await this.closeOwned(r);}
   this.check(r);const length=r.manifest.byteLength;let offset=0;
   const body=new ReadableStream<Uint8Array>({start:c=>{r.body=c;},pull:c=>{try{this.check(r);if(!r.bytes||r.bodyDone)throw new Refusal(499);const end=Math.min(offset+WINDOW,length);c.enqueue(Uint8Array.from(r.bytes.subarray(offset,end)));offset=end;if(offset===length){c.close();r.body=null;this.finishBody(r,true);}}catch{this.finishBody(r,false);}},cancel:()=>{r.abort.abort();this.finishBody(r,false);}},{highWaterMark:0});
   return new Response(body,{status:200,headers:{'Content-Type':'application/octet-stream','Content-Length':String(length),'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Access-Control-Allow-Origin':'null','Vary':'Origin'}});
  }catch(error){this.finishBody(r,false);return failure(r.cleanupFailed?cleanup():error);}
 }
 private async copy(r:RecordState):Promise<Buffer>{
  const shard=join(this.root,r.manifest.sha256.slice(0,2)),path=join(shard,r.manifest.sha256.slice(2));
  const rootInfo=await lstat(this.root);this.check(r);if(!rootInfo.isDirectory()||rootInfo.isSymbolicLink()||await realpath(this.root)!==this.root)throw new Refusal(502);
  const shardInfo=await lstat(shard);this.check(r);if(!shardInfo.isDirectory()||shardInfo.isSymbolicLink()||await realpath(shard)!==shard)throw new Refusal(502);
  const leaf=await lstat(path);this.check(r);if(!leaf.isFile()||leaf.isSymbolicLink())throw new Refusal(502);
  r.file=await this.openFile(path,constants.O_RDONLY|(constants.O_NOFOLLOW??0)|(constants.O_NONBLOCK??0));this.check(r);const first=await r.file.stat();this.check(r);if(!first.isFile()||first.size!==r.manifest.byteLength)throw new Refusal(502);
  const bytes=r.bytes=Buffer.allocUnsafe(r.manifest.byteLength),hash=createHash('sha256');let position=0,debit=0;
  while(position<bytes.length){this.check(r);const read=await r.file.read(bytes,position,Math.min(WINDOW-debit,bytes.length-position),position);this.check(r);if(!Number.isInteger(read.bytesRead)||read.bytesRead<=0||read.bytesRead>Math.min(WINDOW-debit,bytes.length-position))throw new Refusal(502);hash.update(bytes.subarray(position,position+read.bytesRead));position+=read.bytesRead;debit+=read.bytesRead;if(debit===WINDOW){debit=0;await new Promise<void>(yes=>setImmediate(yes));this.check(r);}}
  const tail=await r.file.read(Buffer.alloc(1),0,1,bytes.length);this.check(r);const last=await r.file.stat();this.check(r);if(tail.bytesRead!==0||!last.isFile()||last.size!==bytes.length||hash.digest('hex')!==r.manifest.sha256||!headerMatches(bytes,r.manifest))throw new Refusal(502);return bytes;
 }
 private async closeOwned(r:RecordState){
  const outcomes=await Promise.allSettled([r.file?(async()=>{const file=r.file!;try{await file.close();r.file=null;}catch{r.fileCloseFailed=true;throw cleanup();}})():Promise.resolve(),r.leaseId?(async()=>{try{await this.options.closeGeometry(r.leaseId!);r.leaseId=null;}catch{if(this.options.hasEngineExited())r.leaseId=null;else throw cleanup();}})():Promise.resolve()]);
  if(outcomes.some(v=>v.status==='rejected')){r.cleanupFailed=true;this.failed=true;throw cleanup();}
 }
 private finishBody(r:RecordState,delivered:boolean){if(r.bodyDone)return;r.bodyDone=true;r.delivered=delivered;r.bytes=undefined;clearTimeout(r.timer);if(r.body){try{r.body.error(new Error('Geometry body retired.'));}catch{/* Terminal delivery already closed. */}r.body=null;}this.complete(r);}
 private complete(r:RecordState){if(!r.workerDone||!r.bodyDone)return;if(r.file===null&&r.leaseId===null&&!r.uncertainOpen){if(this.active===r)this.active=null;if(!r.settled){r.settled=true;r.resolve();}}else if(r.cleanupFailed&&!r.settled){r.settled=true;r.reject(cleanup());}}
 async bodyRetired(token:string):Promise<void>{const r=this.require(token);await r.done;if(!r.delivered||!r.current())throw new EngineFailure('CREATURE_GEOMETRY_DELIVERY_FAILED','This geometry body did not complete successfully.');}
 async retire(token:string):Promise<void>{const r=this.require(token);r.abort.abort();if(r.worker)await r.worker;if(r.file===null&&r.leaseId===null&&!r.uncertainOpen)return;await r.done;}
 isRetired(token:string):boolean{const r=this.records.get(token);return !!r&&r.workerDone&&r.bodyDone&&r.file===null&&r.leaseId===null&&!r.uncertainOpen;}
 forget(token:string):void{if(!this.isRetired(token))throw cleanup();this.records.delete(token);}
 engineExited():void{for(const r of this.records.values()){r.leaseId=null;r.uncertainOpen=false;this.complete(r);}}
 async dispose():Promise<void>{this.closed=true;for(const r of this.records.values())r.abort.abort();const results=await Promise.allSettled([...this.records.values()].map(r=>this.retire(r.identity.operationToken)));if(results.some(v=>v.status==='rejected'))throw cleanup();}
 private require(token:string):RecordState{const r=this.records.get(token);if(!r)throw new EngineFailure('CREATURE_GEOMETRY_VIEW_RETIRED','This geometry operation is no longer owned.');return r;}
}
function failure(error:unknown):Response{return new Response('Geometry body unavailable.',{status:error instanceof Refusal?error.status:error instanceof EngineFailure&&error.code.endsWith('CLEANUP_FAILED')?502:500,headers:{'Content-Type':'text/plain; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Access-Control-Allow-Origin':'null','Vary':'Origin'}});}
function headerMatches(b:Buffer,m:ReadonlyGeometryManifest):boolean{
 if(b.length<128||b.toString('ascii',0,4)!=='VMG1'||b.readUInt16LE(4)!==1||b.readUInt16LE(6)!==128||b.readUInt32LE(8)!==m.byteLength||b.readUInt32LE(12)!==1||b.subarray(16,48).toString('hex')!==m.bindingSha256)return false;
 const expected=[[48,m.vertexCount],[52,m.lineIndexCount],[56,m.sectionCount],[60,m.vertexLookupCount],[64,m.positions.offset],[68,m.positions.byteLength],[72,m.lineIndices.offset],[76,m.lineIndices.byteLength],[80,m.sections.offset],[84,m.sections.byteLength],[112,m.sectionIndexCount],[116,1],[120,m.sourceTriangleIndexCount],[124,0]];
 if(expected.some(([offset,value])=>b.readUInt32LE(offset)!==value))return false;for(let i=0;i<3;i++){const min=b.readFloatLE(88+i*4),max=b.readFloatLE(100+i*4);if(!Number.isFinite(min)||!Number.isFinite(max)||Object.is(min,-0)||Object.is(max,-0)||min!==m.bounds.min[i]||max!==m.bounds.max[i])return false;}return true;
}
