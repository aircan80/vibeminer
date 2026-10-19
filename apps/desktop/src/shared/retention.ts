import type { RetentionSnapshot, RetentionTarget, RetentionList } from '../../../../contracts/generated/discovery';
export type RetentionAction = 'downloadAll' | 'pause' | 'resume' | 'onDemand';
export type RetentionSet = RetentionTarget & { action: RetentionAction; expectedRevision?: string };
const decimal = (x: unknown): x is string => typeof x === 'string' && /^(0|[1-9][0-9]{0,18})$/.test(x) && BigInt(x) <= 9223372036854775807n;
export const retentionKey = (t: RetentionTarget) => `${t.productId}:${t.buildIdentity}:${t.sourceRegion}:${t.ledgerId}`;
export const canRetrySavedPolicy=(s:RetentionSnapshot)=>s.state==='stopping'&&s.problem?.code==='RETENTION_STATE_RETRY'&&s.problem.retryable;
export const retentionMoving = (s: RetentionSnapshot) => ['queued','preparing','running','stopping'].includes(s.state);
export const retentionNeedsPoll=(s:RetentionSnapshot)=>retentionMoving(s)&&!canRetrySavedPolicy(s);
export function validRetentionTarget(x: unknown): x is RetentionTarget {
 if (!x || typeof x !== 'object') return false;
 const t = x as RetentionTarget;
 return Object.keys(t).length === 4 && Object.values(t).every(v=>typeof v==='string') && /^[a-z][a-z0-9_-]{0,63}$/.test(t.productId) && /^[0-9a-f]{64}$/.test(t.buildIdentity) && /^[a-z]{2,8}$/.test(t.sourceRegion) && /^[0-9a-f]{64}$/.test(t.ledgerId);
}
export function validRetentionSet(x: unknown): x is RetentionSet {
 if (!x || typeof x !== 'object') return false;
 const {action, expectedRevision, ...target} = x as RetentionSet;
 return validRetentionTarget(target) && ['downloadAll','pause','resume','onDemand'].includes(action) && (expectedRevision === undefined || decimal(expectedRevision));
}
export function retentionMatches(s: RetentionSnapshot, target?: RetentionTarget): boolean {
 if (!validRetentionTarget(s.target) || target && retentionKey(s.target) !== retentionKey(target)) return false;
 const c=s.coverage, p=s.progress;
 const numbers=[s.revision,s.objectCount,s.totalEncodedBytes,s.wireBytesThisSession,s.unresolvedObjects,s.failedObjects,c.receiptObjects,c.receiptBytes,c.observedObjects,c.presentObjects,c.presentBytes,c.missingObjects,c.corruptObjects];
 if (!numbers.every(decimal) || p && [p.completed,p.total].some(x=>x!==null&&!decimal(x))) return false;
 const count=BigInt(s.objectCount), bytes=BigInt(s.totalEncodedBytes), n=(v:string)=>BigInt(v);
 if ([c.receiptObjects,c.observedObjects,c.presentObjects,c.missingObjects,c.corruptObjects,s.unresolvedObjects,s.failedObjects].some(v=>n(v)>count) || n(c.receiptBytes)>bytes || n(c.presentBytes)>bytes || n(c.presentObjects)>n(c.receiptObjects) || n(c.presentBytes)>n(c.receiptBytes)) return false;
 if (n(c.observedObjects)!==n(c.presentObjects)+n(c.missingObjects)+n(c.corruptObjects)) return false;
 if(n(c.observedObjects)+n(s.unresolvedObjects)+n(s.failedObjects)>count)return false;
 if (c.presenceState==='assessed' ? !c.presenceAssessedAt : c.presenceAssessedAt!==null) return false;
 if (n(c.receiptObjects)>0n && !c.receiptedAt || n(c.receiptObjects)===0n && n(c.receiptBytes)!==0n) return false;
 if (p?.completed!==null && p?.completed!==undefined && p.total!==null && n(p.completed)>n(p.total)) return false;
 if(s.paused&&!['paused','stopping'].includes(s.state))return false;
 if (s.mode==='onDemand' && !['onDemand','stopping'].includes(s.state) || s.state==='onDemand' && (s.mode!=='onDemand'||s.paused) || s.state==='paused' && (s.mode!=='downloadAll'||!s.paused) || s.state==='queued' && (s.mode!=='downloadAll'||s.paused)) return false;
 if (s.state==='completed' && (s.mode!=='downloadAll'||s.paused||s.pendingObjects!==0||c.presenceState!=='assessed'||n(c.presentObjects)!==count||n(c.presentBytes)!==bytes||n(c.receiptObjects)!==count||n(c.receiptBytes)!==bytes||n(c.observedObjects)!==count||[c.missingObjects,c.corruptObjects,s.unresolvedObjects,s.failedObjects].some(v=>n(v)!==0n))) return false;
 return true;
}
export function retentionSetMatches(s: RetentionSnapshot, request: RetentionSet): boolean {
 if (!retentionMatches(s, request) || s.mode !== (request.action==='onDemand'?'onDemand':'downloadAll') || request.action!=='downloadAll' && s.paused !== (request.action==='pause')) return false;
 return request.expectedRevision===undefined || BigInt(s.revision)>=BigInt(request.expectedRevision) && BigInt(s.revision)<=BigInt(request.expectedRevision)+1n;
}
export function retentionListMatches(list: RetentionList): boolean {
 return list.jobs.length<=9 && list.jobs.every(s=>retentionMatches(s)&&s.jobId!==null) && new Set(list.jobs.map(s=>retentionKey(s.target))).size===list.jobs.length && new Set(list.jobs.map(s=>s.jobId)).size===list.jobs.length;
}
