import { useEffect, useRef, useState } from 'react';
import type { RetentionSnapshot, RetentionTarget } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { canRetrySavedPolicy, retentionKey, retentionMatches, retentionListMatches, retentionSetMatches, retentionMoving, retentionNeedsPoll, type RetentionAction } from '../shared/retention';
export function useRetention(bridge: DiscoveryBridge, cacheLimit=64) {
 const capacityMessage='The local status cache is full. Check uncertain download statuses before opening another policy.';
 const [features,setFeatures]=useState<string[]>([]);
 const [capability,setCapability]=useState<'checking'|'available'|'unavailable'>('checking');
 const [snapshots,setSnapshots]=useState<Record<string,RetentionSnapshot>>({});
 const [jobKeys,setJobKeys]=useState<string[]>([]);
 const [busy,setBusy]=useState<string|null>(null);
 const [messages,setMessages]=useState<Record<string,string>>({});
 const [recovering,setRecovering]=useState<string[]>([]);
 const [error,setError]=useState('');
 const state=useRef({visibleTarget:null as string|null, uncertain:new Set<string>(), enabled:false, disposed:false, snapshots:{} as Record<string,RetentionSnapshot>, jobs:[] as string[], busy:null as string|null, generation:0, inFlight:false, lastStart:-Infinity, listWanted:false, targets:new Map<string,RetentionTarget>(), timer:undefined as ReturnType<typeof setTimeout>|undefined, failed:false});
 const pumpRef=useRef<()=>void>(()=>{});
 function room(key:string){
  const st=state.current;if(st.snapshots[key]||Object.keys(st.snapshots).length<cacheLimit)return true;
  const evict=Object.keys(st.snapshots).find(k=>k!==st.visibleTarget&&k!==st.busy&&!st.jobs.includes(k)&&!st.uncertain.has(k)&&!retentionMoving(st.snapshots[k]));
  if(!evict){setError(capacityMessage);return false;}
  const next={...st.snapshots};delete next[evict];st.snapshots=next;setSnapshots(next);setMessages(old=>{const copy={...old};delete copy[evict];return copy;});st.targets.delete(evict);return true;
 }
 function accept(s:RetentionSnapshot) {
  const st=state.current,key=retentionKey(s.target),old=st.snapshots[key];
  if (old && BigInt(old.revision)>BigInt(s.revision)) return false;
  if(!room(key))return false;
  st.snapshots={...st.snapshots,[key]:s};setSnapshots(st.snapshots);if(s.state!=='stopping')setMessages(old=>old[key]?.startsWith('Retry of the saved policy requested.')?{...old,[key]:''}:old);return true;
 }
 function schedule() {
  const st=state.current;
  if (st.disposed||!st.enabled||st.timer||st.inFlight||st.busy||document.visibilityState==='hidden') return;
  const moving=Object.values(st.snapshots).some(retentionNeedsPoll);
  if (!st.listWanted&&!st.targets.size&&(!moving||st.failed)) return;
  st.timer=setTimeout(()=>{st.timer=undefined;void pumpRef.current();},Math.max(0,1000-(Date.now()-st.lastStart)));
 }
 async function pump() {
  const st=state.current;
  if(st.disposed||!st.enabled||st.inFlight||st.busy||document.visibilityState==='hidden') return;
  const target=st.targets.values().next().value as RetentionTarget|undefined;
  if(target) st.targets.delete(retentionKey(target));
  else st.listWanted=false;
  st.inFlight=true;st.lastStart=Date.now();const generation=st.generation;
  try {
   if(target){
    const reply=await bridge.retentionRead!(target);
    if(st.disposed||generation!==st.generation)return;
    if(!reply.ok)throw new Error(`${reply.error.message} (${reply.error.code})`);
    if(st.snapshots[retentionKey(target)]&&BigInt(reply.value.revision)<BigInt(st.snapshots[retentionKey(target)].revision))throw new Error('The download status predates the last committed intent. Check status again.');
    if(!retentionMatches(reply.value,target))throw new Error('The download status did not match its exact target.');
    if(!accept(reply.value))throw new Error(capacityMessage);
    const key=retentionKey(target);const wasUncertain=st.uncertain.delete(key);setRecovering([...st.uncertain]);
    setMessages(old=>({...old,[key]:wasUncertain?'Current download status is shown. Choose an action again if still wanted.':''}));
   }else{
    const reply=await bridge.retentionList!();
    if(st.disposed||generation!==st.generation)return;
    if(!reply.ok)throw new Error(`${reply.error.message} (${reply.error.code})`);
    if(!retentionListMatches(reply.value))throw new Error('The saved download intents were invalid.');
    const keys=reply.value.jobs.map(s=>retentionKey(s.target));
    for(const old of Object.values(st.snapshots)) if(retentionMoving(old)&&!keys.includes(retentionKey(old.target)))st.targets.set(retentionKey(old.target),old.target);
    const priorJobs=st.jobs;st.jobs=[...new Set([...priorJobs,...keys])];
    try{for(const job of reply.value.jobs)if(!accept(job))throw new Error(capacityMessage);}
    catch(e){st.jobs=priorJobs;throw e;}
    st.jobs=keys;setJobKeys(keys);
   }
   st.failed=false;if(!st.visibleTarget||st.snapshots[st.visibleTarget])setError('');
  }catch(e){if(!st.disposed&&generation===st.generation){st.failed=true;setError(e instanceof Error?e.message:'Download status is unavailable. Last confirmed status is preserved.');}}
  finally{st.inFlight=false;schedule();}
 }
 pumpRef.current=()=>{void pump();};
 function ensure(target:RetentionTarget){const st=state.current,key=retentionKey(target),prior=st.visibleTarget;st.visibleTarget=key;if(prior&&prior!==key&&!st.uncertain.has(prior))st.targets.delete(prior);if(room(key)&&!st.snapshots[key])st.targets.set(key,target);schedule();}
 function retrySavedPolicy(target:RetentionTarget,expected?:{jobId:string|null;revision:string}){const before=state.current.snapshots[retentionKey(target)];if(before&&canRetrySavedPolicy(before))return command(target,before.mode==='onDemand'?'onDemand':before.paused?'pause':'resume',true,expected);}
 function release(key:string){const st=state.current;if(st.visibleTarget===key)st.visibleTarget=null;if(!st.uncertain.has(key))st.targets.delete(key);}
 function refresh(target?:RetentionTarget){const st=state.current;st.failed=false;if(target)st.targets.set(retentionKey(target),target);else{st.listWanted=true;for(const key of st.uncertain){const snapshot=st.snapshots[key];if(snapshot)st.targets.set(key,snapshot.target);}}schedule();}
 async function command(target:RetentionTarget,action:RetentionAction,retrySaved=false,expected?:{jobId:string|null;revision:string}){
  const st=state.current,key=retentionKey(target),before=st.snapshots[key];
  if(!st.enabled||st.busy||!before||expected!==undefined&&(before.jobId!==expected.jobId||before.revision!==expected.revision)||st.uncertain.has(key)||before.state==='stopping'&&!retrySaved)return;
  if(retrySaved&&(!canRetrySavedPolicy(before)||action!==(before.mode==='onDemand'?'onDemand':before.paused?'pause':'resume')))return;
  // Policy buttons are idempotent selections; recovery is an explicit Resume.
  if(!retrySaved&&(action==='downloadAll'&&before.mode==='downloadAll'||action==='onDemand'&&before.mode==='onDemand'))return;
  st.busy=key;setBusy(key);++st.generation;setMessages(old=>({...old,[key]:'Saving download policy…'}));
  const request={...target,action,expectedRevision:before.revision};
  try{
   const reply=await bridge.retentionSet!(request);
   if(st.disposed)return;
   if(!reply.ok){
    st.targets.set(key,target);st.uncertain.add(key);setRecovering([...st.uncertain]);st.failed=false;
    setMessages(old=>({...old,[key]:reply.error.code==='RETENTION_STALE'?'Download policy changed. Checking current status; choose the action again if still wanted.':`${reply.error.message} (${reply.error.code}). The command may have committed; checking download status before another action.`}));
    return;
   }
   if(!retentionSetMatches(reply.value,request)||retrySaved&&reply.value.revision!==before.revision)throw new Error('The committed policy did not match the exact request.');
   if(!accept(reply.value))throw new Error(capacityMessage);setMessages(old=>({...old,[key]:retrySaved&&reply.value.state==='stopping'?'Retry of the saved policy requested. Cleanup is not yet confirmed.':''}));
   if(reply.value.jobId&&reply.value.mode==='downloadAll'&&!st.jobs.includes(key)){st.jobs=[...st.jobs,key];setJobKeys(st.jobs);}
   st.listWanted=true;st.failed=false;
  }catch{st.targets.set(key,target);st.uncertain.add(key);setRecovering([...st.uncertain]);st.failed=false;setMessages(old=>({...old,[key]:'Acknowledgement was lost. Checking download status before another action; the command may have committed.'}));}
  finally{st.busy=null;setBusy(null);schedule();}
 }
 useEffect(()=>{
  const st=state.current;st.disposed=false;
  void (async()=>{
   try{const reply=await bridge.capabilities?.();if(st.disposed)return;
    setFeatures(reply?.ok?reply.value:[]);
    st.enabled=!!(reply?.ok&&reply.value.includes('retention-v1')&&bridge.retentionRead&&bridge.retentionList&&bridge.retentionSet);
    setCapability(st.enabled?'available':'unavailable');if(st.enabled){st.listWanted=true;schedule();}
   }catch{if(!st.disposed)setCapability('unavailable');}
  })();
  const visible=()=>{if(document.visibilityState!=='hidden')schedule();};document.addEventListener('visibilitychange',visible);
  return()=>{st.disposed=true;++st.generation;clearTimeout(st.timer);st.timer=undefined;document.removeEventListener('visibilitychange',visible);};
 },[bridge]);
 return {capability,features,snapshots,jobs:jobKeys.map(key=>snapshots[key]).filter(Boolean),busy,messages,recovering,error,ensure,release,refresh,command,retrySavedPolicy};
}
export type RetentionController=ReturnType<typeof useRetention>;
