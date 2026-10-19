import {ActivityLink,useDownloadActivity} from './DownloadActivity';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { EngineProgress, TableDataSnapshot, TableDefinitionSnapshot, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { tableDataMatches } from '../shared/table-data';
export type TableFailure = {code:string;message:string;retryable:boolean};
export type TableLatch = {kind:'stopped'|'failed';error:TableFailure|null};
export type TableOperation = { id: string; definition: TableDefinitionSnapshot; status: 'pending'|'stopping'|'succeeded'|'failed'|'canceled'; progress: EngineProgress|null; result: TableDataSnapshot|null; error: TableFailure|null };
export const tableIntentKey=(d:TableDefinitionSnapshot)=>[d.productId,d.buildIdentity,d.sourceRegion,d.entry.tableId,'enUS',d.provider.revision,d.definition?.sha256??''].join(':');
export const tableContextMatches=(d:TableDefinitionSnapshot,s:WorkspaceSnapshot)=>d.productId===s.build.productId&&d.buildIdentity===s.build.id&&d.sourceRegion===s.sourceReference.region;
// Fail closed at the session bound. Never evict a user's stopped/failed intent.
export function rememberTableLatch(map:Map<string,TableLatch>,key:string,value:TableLatch,limit=2048){if(!map.has(key)&&map.size>=limit)return false;map.set(key,value);return true;}
export function measuredStage(progress:EngineProgress|null){
 if(!progress||progress.completed===null)return null;
 const {completed,total,unit}=progress;if(!Number.isSafeInteger(completed)||completed<0)return null;
 const suffix=unit?` ${unit}`:'';
 if(total===null||!Number.isSafeInteger(total)||total<=0||completed>total)return {label:`${completed.toLocaleString()}${total===0?' of 0':''}${suffix}`,percent:null};
 const percent=Number(BigInt(completed)*100n/BigInt(total));
 return {label:`${completed.toLocaleString()} of ${total.toLocaleString()}${suffix} · ${percent}% of current stage`,percent};
}
export function useTableLoading(bridge: DiscoveryBridge,enabled=true) {
 const [operation,setOperation]=useState<TableOperation|null>(null), [viewRequest,setViewRequest]=useState(0),[latchRevision,setLatchRevision]=useState(0);
 const latches=useRef(new Map<string,TableLatch>()),overflow=useRef(false);
 const current=useRef(operation); const alive=useRef(true), cancelButton=useRef<HTMLButtonElement>(null), status=useRef<HTMLDivElement>(null), terminalFocus=useRef(false),dismissOrigin=useRef<HTMLElement|null>(null);
 function publish(op:TableOperation){current.current=op;setOperation(op);}
 function latch(definition:TableDefinitionSnapshot,value:TableLatch){if(!rememberTableLatch(latches.current,tableIntentKey(definition),value))overflow.current=true;setLatchRevision(x=>x+1);}
 function blocked(definition:TableDefinitionSnapshot):TableLatch|null{return latches.current.get(tableIntentKey(definition))??(overflow.current?{kind:'failed',error:{code:'AUTOMATIC_INTENT_LIMIT',message:'The session has reached its limit of remembered stopped or failed table requests. Existing decisions are preserved. Restart the application before creating another automatic request.',retryable:false}}:null);}
 useEffect(()=>{alive.current=true;if(!enabled)return()=>{alive.current=false;};const off=bridge.onProgress(progress=>{const op=current.current;if(!op||!['pending','stopping'].includes(op.status)||op.id!==progress.requestId||progress.sequence<=(op.progress?.sequence??0))return;publish({...op,progress});});return()=>{alive.current=false;off();};},[bridge,enabled]);
 useLayoutEffect(()=>{if(!operation&&dismissOrigin.current){const origin=dismissOrigin.current;dismissOrigin.current=null;if(document.activeElement===document.body||document.activeElement===origin){const heading=[...document.querySelectorAll<HTMLElement>('h1')].find(element=>!element.closest('[hidden],[inert]'));if(heading){heading.tabIndex=-1;heading.focus({preventScroll:true});}}}if(terminalFocus.current&&operation&&!['pending','stopping'].includes(operation.status)){terminalFocus.current=false;status.current?.focus({preventScroll:true});}},[operation]);
 function load(definition:TableDefinitionSnapshot){
  const old=current.current;if((old&&['pending','stopping'].includes(old.status))||blocked(definition))return null;
  const id=`table_${crypto.randomUUID()}`,op:TableOperation={id,definition,status:'pending',progress:null,result:null,error:null};publish(op);
  const params={productId:definition.productId,buildIdentity:definition.buildIdentity,sourceRegion:definition.sourceRegion,tableId:definition.entry.tableId,researchLocale:'enUS' as const};
  void(async()=>{let result:TableDataSnapshot|null=null,error:TableFailure|null=null;
   try{const reply=await bridge.tablePrepare(params,id);if(reply.ok){if(tableDataMatches(reply.value,params,definition))result=reply.value;else error={code:'TABLE_DATA_CONTEXT_MISMATCH',message:'The admitted table does not match this exact source and pinned definition. No records were accepted.',retryable:false};}else error=reply.error;}
   catch{error={code:'ENGINE_UNAVAILABLE',message:'Table preparation could not complete. Previously verified content is preserved.',retryable:true};}
   if(!alive.current||current.current?.id!==id)return;
   terminalFocus.current=!!cancelButton.current&&document.activeElement===cancelButton.current;
   const status=result?'succeeded':error?.code==='CANCELED'?'canceled':'failed';
   if(error)latch(definition,{kind:status==='canceled'?'stopped':'failed',error});
   publish({...current.current,status,result,error});
  })();return id;
 }
 async function cancel(expectedId?:string){const op=current.current;if(!op||expectedId!==undefined&&op.id!==expectedId||op.status!=='pending')return;latch(op.definition,{kind:'stopped',error:null});publish({...op,status:'stopping'});try{const reply=await bridge.cancel(op.id);if(alive.current&&current.current?.id===op.id&&!reply.ok)publish({...current.current,error:reply.error});}catch{if(alive.current&&current.current?.id===op.id)publish({...current.current,error:{code:'ENGINE_UNAVAILABLE',message:'The stop request could not be acknowledged. Waiting for the table operation to finish safely.',retryable:true}});}}
 return {operation,load,cancel,viewRequest,view:(expectedId?:string)=>{if(expectedId!==undefined&&current.current?.id!==expectedId)return false;setViewRequest(x=>x+1);return true;},cancelButton,status,pending:!!operation&&['pending','stopping'].includes(operation.status),blocked,latchRevision,dismiss:(expectedId?:string)=>{const op=current.current;if(!op||expectedId!==undefined&&op.id!==expectedId||['pending','stopping'].includes(op.status))return;dismissOrigin.current=expectedId===undefined?document.activeElement as HTMLElement:null;current.current=null;setOperation(null);},fail:(definition:TableDefinitionSnapshot,error:TableFailure)=>latch(definition,{kind:'failed',error}),stopWaiting:(definition:TableDefinitionSnapshot)=>latch(definition,{kind:'stopped',error:null}),retry:(definition:TableDefinitionSnapshot)=>{latches.current.delete(tableIntentKey(definition));setLatchRevision(x=>x+1);}};
}
export type TableLoading = ReturnType<typeof useTableLoading>;
export function TableLoadBanner({flow,onView,snapshot,session=false}:{flow:TableLoading;onView?:()=>void;snapshot?:WorkspaceSnapshot;session?:boolean}){
 const activityUi=useDownloadActivity();
 const op=flow.operation;if(!op)return null;if(activityUi)return <section className="local-task-status" aria-label="Table data request"><div ref={flow.status} tabIndex={-1}><p>{op.status==='pending'?'Waiting for required data':op.status==='stopping'?'Stopping this request safely':op.status==='succeeded'?'Admitted data available':op.status==='canceled'?'Loading stopped':'Data needs attention'}</p></div><ActivityLink taskKey={`table:${op.id}`}/></section>;const background=session||!!snapshot&&!tableContextMatches(op.definition,snapshot);
 const name=op.definition.entry.name??op.definition.entry.tableId,step=op.status==='pending'?op.progress?.step:null,measurement=op.status==='pending'?measuredStage(op.progress):null;
 const phase=op.status==='pending'?op.progress?.stage??'Opening table data':op.status==='stopping'?'Stopping table load…':op.status==='succeeded'?(op.result?.coverage.scope==='unencryptedSections'?'Readable rows available · Partial':'Table records available'):op.status==='canceled'?'Table loading stopped':'Table load failed';
 return <section className="table-load-strip" aria-label={background?'Background table load':'Table load'}><div ref={flow.status} tabIndex={-1}><strong>{background&&'Background table · '}{name} · Research locale enUS</strong>{background&&<><small>{op.definition.productId} · build {op.definition.buildVersion} · source {op.definition.sourceRegion.toUpperCase()}</small><details><summary>Background load identity</summary><p>Build identity <code>{op.definition.buildIdentity}</code> · table {op.definition.entry.tableId}</p></details></>}<div aria-live="off">{step&&<span aria-hidden="true">[{step.index}/{step.total??'…'}] </span>}{phase}</div><span className="visually-hidden" role="status">{step?`Stage ${step.index}. ${step.description}`:phase}</span>{step&&<small>{step.description}</small>}{measurement&&<small className="stage-measurement" aria-live="off">{measurement.label}</small>}{measurement?.percent!==null&&measurement?.percent!==undefined&&<progress aria-label={`${name} current stage`} max={100} value={measurement.percent}/>} {op.error&&<p>{op.error.code}: {op.error.message}</p>}</div><div>{!background&&onView&&<button onClick={onView}>View table load</button>}{background&&!flow.pending&&<button onClick={()=>flow.dismiss()}>Dismiss table activity</button>}{flow.pending&&<button ref={flow.cancelButton} aria-disabled={op.status==='stopping'} onClick={()=>void flow.cancel()}>{op.status==='stopping'?`Stopping ${name}…`:`Stop loading ${name}`}</button>}</div></section>;
}
