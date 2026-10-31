/**
 * Electron main's view of the local engine process (D119). `generation` counts engine processes this session and moves
 * only when a restart starts a new one; `revision` moves on every change, so a reader keeps the newest status it saw.
 * `restartable` is true only for a stop a new process can recover from: an invalid reply (PROTOCOL_ERROR) or a failed
 * restart. A refused catalog never is, since a new process refuses it the same way.
 */
export type EngineStatus=Readonly<{state:'running'|'restarting'|'stopped';generation:number;revision:number;problem:Readonly<{code:string;message:string}>|null;restartable:boolean}>;
const keys=['generation','problem','restartable','revision','state'];
const count=(value:unknown)=>typeof value==='number'&&Number.isSafeInteger(value)&&value>=1;
export function validEngineStatus(value:unknown):value is EngineStatus{
  if(!value||typeof value!=='object'||Array.isArray(value))return false;
  const v=value as Record<string,unknown>;
  if(Object.keys(v).sort().join()!==keys.join()||!['running','restarting','stopped'].includes(v.state as string)||!count(v.generation)||!count(v.revision)||typeof v.restartable!=='boolean')return false;
  if(v.problem===null)return v.state==='running'&&!v.restartable;
  const p=v.problem as Record<string,unknown>;
  return !!p&&typeof p==='object'&&!Array.isArray(p)&&Object.keys(p).sort().join()==='code,message'&&typeof p.code==='string'&&/^[A-Z][A-Z0-9_]{0,63}$/.test(p.code)&&typeof p.message==='string'&&p.message.length>0&&p.message.length<=1024;
}
