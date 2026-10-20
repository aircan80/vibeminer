import type {WorkspaceSnapshot,RetentionSnapshot} from '../../../../contracts/generated/discovery';
import {downloadAccountingValid} from '../shared/download';
import {inventoryAccountingValid,exactBytes} from '../shared/inventory';
import {retentionMatches,retentionKey,canRetrySavedPolicy} from '../shared/retention';
export type ActivityIdentity=Readonly<{productId:string;buildIdentity:string;buildVersion?:string;sourceRegion:string;researchLocale?:string;detail?:string}>;
export type ActivityAction=Readonly<{id:string;label:string;disabled?:boolean}>;
export type ActivityStatus='pending'|'stopping'|'succeeded'|'failed'|'canceled'|'queued'|'preparing'|'running'|'waiting'|'paused'|'completed'|'releasing'|'blocked';
export type ActivityTask=Readonly<{key:string;kind:'research'|'buildRetention'|'presentationCleanup';title:string;identity:ActivityIdentity;status:ActivityStatus;stage:string;measurement:Readonly<{label:string;percent:number|null}>|null;problem:Readonly<{code:string;message:string;retryable?:boolean}>|null;actions:readonly ActivityAction[];counted?:boolean;details?:readonly string[]}>;
export function stageMeasurement(p:{completed:number|string|null;total:number|string|null;unit?:string|null}|null):ActivityTask['measurement']{
 if(!p||p.completed===null)return null;
 const uint=(v:unknown)=>typeof v==='string'&&/^(0|[1-9][0-9]{0,18})$/.test(v)||typeof v==='number'&&Number.isSafeInteger(v)&&v>=0;
 if(!uint(p.completed)||p.total!==null&&!uint(p.total))return null;
 const c=BigInt(p.completed),t=p.total===null?null:BigInt(p.total);if(t!==null&&c>t)return null;
 const unit=p.unit?` ${p.unit}`:'',percent=t!==null&&t>0n?Number(c*100n/t):null;
 return {label:`${c.toLocaleString()}${t===null?'':` of ${t.toLocaleString()}`}${unit}${percent===null?t===0n?' · no percentage for zero total':' · total unknown':` · ${percent}% of current stage`}`,percent};
}
export const taskAttention=(t:ActivityTask)=>t.status==='failed'||t.status==='blocked'||!!t.problem&&!['succeeded','canceled'].includes(t.status);
export const taskRecent=(t:ActivityTask)=>['succeeded','canceled','completed'].includes(t.status)&&t.kind!=='buildRetention';
export function activitySummary(tasks:readonly ActivityTask[]){
 const counted=tasks.filter(t=>t.counted??(t.kind==='buildRetention'||!taskRecent(t))),attention=tasks.find(taskAttention),active=tasks.find(t=>['pending','running','preparing','stopping','releasing'].includes(t.status)),other=counted[0],chosen=attention??active??other;
 return {count:counted.length,measurement:chosen?.measurement?.label??null,label:attention?`${attention.title} · needs attention`:active?`${active.title} · ${active.stage}`:other?`${other.title} · ${other.stage}`:'No active tasks'};
}
// Joins a summary label and its measurement for one line. Engine stage text may already end with the same count, or the
// same count and percent; that tail is dropped so each fact appears once. A differing count is kept.
export function summaryLine(s:Readonly<{label:string;measurement:string|null}>){
 if(!s.measurement)return s.label;
 const plain=(t:string)=>t.trim().replace(/(\d)[,.\u00a0\u202f' ](?=\d{3}(?!\d))/g,'$1').replace(/^0 of 0 /,'0 '),count=plain(s.measurement.split(' · ')[0]),percent=/ · (\d+)% of current stage$/.exec(s.measurement)?.[1];
 const parts=s.label.split(' · '),n=parts.length,tail=(i:number)=>plain(parts[n-i]);
 const drop=percent!==undefined&&n>2&&tail(1)===`${percent}%`&&tail(2)===count?2:n>1&&tail(1)===count?1:0;
 return `${parts.slice(0,n-drop).join(' · ')} · ${s.measurement}`;
}
export const retentionStateLabel=(s:RetentionSnapshot)=>s.state==='stopping'?(s.mode==='onDemand'?'Switching to On demand':s.paused?'Pausing build download':'Updating build download'):({onDemand:'On demand',queued:'Build download queued',preparing:'Preparing encoded retention',running:'Encoded retention pass underway',waiting:'Build download waiting',paused:'Build download paused',completed:'Encoded retention pass complete',failed:'Build download needs attention'}[s.state]);
export function retentionActivity(s:RetentionSnapshot,locked=false):ActivityTask{
 const actions:ActivityAction[]=[];
 if(canRetrySavedPolicy(s))actions.push({id:'savedPolicy',label:'Retry saved download policy',disabled:locked});
 else if(s.mode==='downloadAll'){
  if(s.state==='stopping')actions.push({id:'pause',label:'Stopping…',disabled:true});
  else if(['paused','waiting','failed'].includes(s.state))actions.push({id:'resume',label:'Resume build download',disabled:locked});
  else if(s.state!=='completed')actions.push({id:'pause',label:'Pause build download',disabled:locked});
 }
 if(s.mode==='downloadAll')actions.push({id:'onDemand',label:'Use on demand',disabled:locked||s.state==='stopping'});
 actions.push({id:'refresh',label:'Check download status'});
 return {key:`retention:${retentionKey(s.target)}:${s.jobId??'none'}`,kind:'buildRetention',counted:s.mode==='downloadAll'||!!s.problem,title:'Published encoded content · all variants',identity:{...s.target,detail:`Ledger ${s.target.ledgerId} · intent revision ${s.revision}`},status:s.state==='onDemand'?'completed':s.state,stage:s.progress&&['preparing','running'].includes(s.state)?s.progress.stage:retentionStateLabel(s),measurement:s.progress&&['preparing','running'].includes(s.state)?stageMeasurement(s.progress):null,problem:s.problem,actions,details:[...(s.queuePosition===null?[]:[`Build-job queue position: ${s.queuePosition}. This is not a physical transfer position.`]),`Payload transferred this session: ${exactBytes(s.wireBytesThisSession)}; includes retries, excludes lookup metadata.`,`Previously verified receipt credit: ${exactBytes(s.coverage.receiptBytes)}; historical proof, not current possession.`,`Presence assessment: ${s.coverage.presenceState}; ${s.coverage.observedObjects} objects observed.`,`Feeder frontier: ${s.pendingObjects} objects; not global transfer concurrency.`,'Running means a retention pass is underway, not that an HTTP body is active at every instant.']};
}
export type StorageAssessment=Readonly<{opening:{objectCount:string;total:string;verified:string;remaining:string;at:string;scope:string}|null;presence:{state:string;at:string|null;presentBytes:string;presentObjects:string;missingObjects:string;corruptObjects:string;observedObjects:string;complement:string|null}|null;freshVerification:string}>;
export function storageAssessment(w:WorkspaceSnapshot,s?:RetentionSnapshot):StorageAssessment{
 const d=w.download&&downloadAccountingValid(w.download)?w.download:null,i=w.inventory&&inventoryAccountingValid(w.inventory)?w.inventory:null;
 const matching=!!s&&!!d&&retentionMatches(s,{productId:w.build.productId,buildIdentity:w.build.id,sourceRegion:w.sourceReference.region,ledgerId:d.ledgerId})&&s.totalEncodedBytes===d.totalEncodedBytes&&s.objectCount===d.objectCount;
 const c=matching?s!.coverage:null,complete=!!c&&c.presenceState==='assessed'&&c.observedObjects===s!.objectCount&&s!.failedObjects==='0'&&s!.unresolvedObjects==='0';
 return {opening:d?{objectCount:d.objectCount,total:d.totalEncodedBytes,verified:d.verifiedEncodedBytes,remaining:d.remainingEncodedBytes,at:d.assessedAt,scope:'Download manifest · all published variants'}:i?{objectCount:i.downloadObjectCount,total:i.totalEncodedBytes,verified:i.verifiedEncodedBytes,remaining:i.remainingEncodedBytes,at:i.preparedAt,scope:'Published inventory · all variants'}:null,presence:c?{state:c.presenceState,at:c.presenceAssessedAt,presentBytes:c.presentBytes,presentObjects:c.presentObjects,missingObjects:c.missingObjects,corruptObjects:c.corruptObjects,observedObjects:c.observedObjects,complement:complete?(BigInt(s!.totalEncodedBytes)-BigInt(c.presentBytes)).toString():null}:null,freshVerification:'Not assessed by this view'};
}
