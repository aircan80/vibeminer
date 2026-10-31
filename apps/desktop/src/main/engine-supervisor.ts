import {EventEmitter} from 'node:events';
import {EngineFailure,type EngineClient} from './engine-client';
import type {EngineStatus} from '../shared/engine-status';
/** One engine process with everything main keeps for it. `retire` ends it for good and says whether the process exited. */
export interface EngineSession{readonly engine:Pick<EngineClient,'hasFailed'|'stopReason'|'on'|'off'>;readonly ready:Promise<void>;retire():Promise<boolean>;}
/** A new process refuses these catalogs the same way, so restarting can't help (CATALOG_RESET_EXIT, CATALOG_NEWER_EXIT). */
const refused=['CATALOG_RESET_REQUIRED','CATALOG_VERSION'];
export const ENGINE_RESTARTED='ENGINE_RESTARTED';
/**
 * Owns the engine process lifecycle (D119). An invalid reply still stops the engine (the client refuses the frame and kills
 * the process); the supervisor then reports a restartable stop, and only `restart()`, which the user triggers, starts a new
 * process. There is no automatic restart. Work started on an older process never answers as if from the current one.
 */
export class EngineSupervisor<S extends EngineSession> extends EventEmitter{
  private session:S;
  private value:EngineStatus={state:'running',generation:1,revision:1,problem:null,restartable:false};
  private restarting:Promise<EngineStatus>|null=null;
  private closing=false;
  constructor(private readonly start:()=>S){super();this.session=this.watch(start());}
  get current():S{return this.session;}
  get status():EngineStatus{return this.value;}
  private set(next:Omit<EngineStatus,'revision'>):EngineStatus{this.value=Object.freeze({...next,revision:this.value.revision+1,problem:next.problem&&Object.freeze({...next.problem})});this.emit('status',this.value);return this.value;}
  private watch(session:S):S{
    session.engine.on('unavailable',(error:EngineFailure)=>{if(this.session===session&&!this.restarting)this.stopped(error,false);});
    return session;
  }
  private stopped(error:EngineFailure,afterRestart:boolean):EngineStatus{
    return this.set({state:'stopped',generation:this.value.generation,problem:{code:error.code,message:error.message},restartable:!refused.includes(error.code)&&(afterRestart||error.code==='PROTOCOL_ERROR')});
  }
  /** Starts one new engine process when the current one stopped in a way a restart can fix. Concurrent calls share it. */
  restart():Promise<EngineStatus>{
    if(this.restarting)return this.restarting;
    if(this.closing||this.value.state!=='stopped'||!this.value.restartable)return Promise.resolve(this.value);
    this.set({...this.value,state:'restarting',restartable:false});
    const work=(async()=>{try{
      // The old process must be gone before a new one opens the same data folder.
      let exited=false;try{exited=await this.session.retire();}catch{exited=false;}
      if(!exited)return this.set({state:'stopped',generation:this.value.generation,problem:{code:'ENGINE_RESTART_FAILED',message:'The stopped engine did not exit, so no new engine was started. Try again, or close VibeMiner and start it again.'},restartable:true});
      // Quitting while the old process retired: the app is closing, so no new engine opens the data folder.
      if(this.closing)return this.value;
      let next:S;
      try{next=this.watch(this.start());}catch{return this.set({state:'stopped',generation:this.value.generation,problem:{code:'ENGINE_UNAVAILABLE',message:'The local engine could not be started. Check the desktop setup and try again.'},restartable:true});}
      this.session=next;
      const generation=this.value.generation+1;
      this.set({...this.value,generation});
      let failure:EngineFailure|null=null;
      try{await next.ready;}catch(error){failure=next.engine.stopReason??(error instanceof EngineFailure?error:new EngineFailure('ENGINE_UNAVAILABLE','The local engine could not be started. Check the desktop setup and try again.',true));}
      failure??=next.engine.stopReason;
      return failure?this.stopped(failure,true):this.set({state:'running',generation,problem:null,restartable:false});
    }finally{this.restarting=null;}})();
    this.restarting=work;
    return work;
  }
  /**
   * Quitting: no restart starts a new process from here on. Resolves, once a restart in progress has settled, to the session
   * that is then current, which the caller retires.
   */
  async close():Promise<S>{this.closing=true;if(this.restarting)await this.restarting.catch(()=>{});return this.session;}
  /**
   * Runs one bridge call on the current process. A reply or failure that arrives after a restart replaced that process is
   * stale: it is dropped as ENGINE_RESTARTED rather than handed to a renderer that now talks to a new engine.
   */
  async call<T>(work:(session:S)=>Promise<T>):Promise<T>{
    const session=this.session;
    const stale=()=>new EngineFailure(ENGINE_RESTARTED,'This reply came from an engine that has been restarted since, so it was dropped.',true);
    let value:T;
    try{await session.ready;value=await work(session);}
    catch(error){if(session!==this.session)throw stale();throw error;}
    if(session!==this.session)throw stale();
    return value;
  }
}
