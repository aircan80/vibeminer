import type {ItemsContext,ItemsSnapshot,ItemSummary,ItemClassificationBatch,ItemClassificationSource,ItemClassificationValue} from '../../../../contracts/generated/discovery';
import {validItemsContext,sameItemsContext,wellFormedText} from './items';
import {tableDataMatches,validRecordId} from './table-data';
export type ClassificationParams=ItemsContext&{artifactId:string;itemIds:string[];includeSubclass?:boolean;policyRevision?:'classification-label-index-v1'|'classification-label-index-v2'};
const hash=/^[0-9a-f]{64}$/,decimal=/^(0|[1-9][0-9]{0,4})$/,integer=/^(0|-?[1-9][0-9]{0,9})$/,utf8=new TextEncoder();
const fields=['productId','buildIdentity','sourceRegion','researchLocale','artifactId','itemIds','includeSubclass','policyRevision'];
const exact=(v:object,keys:string[])=>Object.keys(v).length===keys.length&&Object.keys(v).every(k=>keys.includes(k));
export function validClassifications(value:unknown):value is ClassificationParams{
 if(!value||typeof value!=='object')return false;const{artifactId,itemIds,includeSubclass,policyRevision,...context}=value as ClassificationParams;
 return Object.keys(value).every(k=>fields.includes(k))&&validItemsContext(context)&&typeof artifactId==='string'&&hash.test(artifactId)&&Array.isArray(itemIds)&&itemIds.length>=1&&itemIds.length<=50&&itemIds.every(id=>typeof id==='string'&&validRecordId(id))&&(includeSubclass===undefined||typeof includeSubclass==='boolean')&&(policyRevision===undefined||policyRevision==='classification-label-index-v1'||policyRevision==='classification-label-index-v2');
}
const problem=(p:ItemClassificationValue['problem'])=>p===null||!!p&&exact(p,['code','message','retryable'])&&typeof p.code==='string'&&p.code.length>=1&&p.code.length<=128&&typeof p.message==='string'&&p.message.length>=1&&p.message.length<=1024&&typeof p.retryable==='boolean';
const states=['ready','emptyText','invalidText','missingItem','fieldUnavailable','keyOutOfRange','notFound','ambiguous','incompleteTarget','unavailable','protected','unsupported','corrupt'];
export function classificationsMatch(value:unknown,p:ClassificationParams,items?:ItemsSnapshot,summaries?:readonly ItemSummary[]):value is ItemClassificationBatch{
 try{return match(value as ItemClassificationBatch,p,items,summaries);}catch{return false;}
}
function match(v:ItemClassificationBatch,p:ClassificationParams,items?:ItemsSnapshot,summaries?:readonly ItemSummary[]):boolean{
 if(!v||!exact(v,['context','artifactId','policyRevision','sources','outcomes'])||!validItemsContext(v.context)||!sameItemsContext(v.context,p)||v.artifactId!==p.artifactId||v.policyRevision!==(p.policyRevision??'classification-label-index-v1')||!Array.isArray(v.sources)||v.sources.length!==(p.includeSubclass?2:1)||new Set(v.sources.map(s=>s.kind)).size!==v.sources.length||!v.sources.some(s=>s.kind==='class')||p.includeSubclass&&!v.sources.some(s=>s.kind==='subclass')||!Array.isArray(v.outcomes)||v.outcomes.length!==p.itemIds.length||utf8.encode(JSON.stringify(v)).byteLength>512*1024)return false;
 if(items&&(!sameItemsContext(items.context,p)||items.artifactId!==p.artifactId))return false;
 for(const s of v.sources){if(!exact(s,['kind','state','snapshot','indexArtifactId','problem'])||!['class','subclass'].includes(s.kind)||!['ready','incompleteTarget','unavailable','protected','unsupported','corrupt'].includes(s.state)||!problem(s.problem)||s.indexArtifactId!==null&&(typeof s.indexArtifactId!=='string'||!hash.test(s.indexArtifactId)))return false;
  if(s.snapshot!==null&&(!s.snapshot||typeof s.snapshot!=='object'||!tableDataMatches(s.snapshot,{...p,tableId:s.kind==='class'?'b977271e':'95d8638f'})||s.snapshot.association.fileDataId!==(s.kind==='class'?'1140189':'1261604')))return false;
  if(['ready','incompleteTarget'].includes(s.state)&&(!s.snapshot||!s.indexArtifactId||s.problem!==null||(s.state==='ready')!==(s.snapshot.coverage.scope==='complete')))return false;
  if(v.policyRevision==='classification-label-index-v1'&&s.kind==='subclass'&&(s.state!=='unsupported'||s.snapshot!==null||s.indexArtifactId!==null))return false;
 }
 return v.outcomes.every((o,i)=>{if(!o||!exact(o,['itemId','admitted','class','subclass'])||o.itemId!==p.itemIds[i]||typeof o.admitted!=='boolean'||(o.subclass!==null)!==!!p.includeSubclass)return false;const summary=summaries?.find(s=>s.id===o.itemId);if(summary&&!o.admitted)return false;return checkValue(o.class,v.sources.find(s=>s.kind==='class')!,o.admitted,items,summary)&&(!o.subclass||checkValue(o.subclass,v.sources.find(s=>s.kind==='subclass')!,o.admitted,items,summary,v.policyRevision==='classification-label-index-v1'))&&(v.policyRevision!=='classification-label-index-v1'||!o.subclass||['unsupported','missingItem'].includes(o.subclass.state));});
}
function checkValue(v:ItemClassificationValue,s:ItemClassificationSource,admitted:boolean,items?:ItemsSnapshot,summary?:ItemSummary,legacySubclass=false):boolean{
 if(!v||!exact(v,['state','sourceKeys','text','target','observedCandidates','problem'])||!states.includes(v.state)||!Array.isArray(v.sourceKeys)||v.sourceKeys.length>(s.kind==='class'?1:2)||!problem(v.problem)||v.observedCandidates!==null&&(typeof v.observedCandidates!=='string'||!decimal.test(v.observedCandidates)||BigInt(v.observedCandidates)>65536n)||!admitted&&v.state!=='missingItem'||admitted&&v.state==='missingItem')return false;
 const names=s.kind==='class'?['ClassID']:['ClassID','SubclassID'];let previous=-1;
 for(const k of v.sourceKeys){if(!k||!exact(k,['tableId','columnIndex','value'])||k.tableId!=='50238ec2'||!Number.isInteger(k.columnIndex)||k.columnIndex<0||k.columnIndex>511||typeof k.value!=='string'||!integer.test(k.value)||BigInt(k.value)<-2147483648n||BigInt(k.value)>2147483647n)return false;
  if(items){const c=items.itemSource.columns[k.columnIndex],ordinal=names.indexOf(c?.name);if(!c||c.family!=='integer'||c.interpretation||ordinal<=previous||summary&&!summary.baseValues.some(b=>b.tableId===k.tableId&&b.columnIndex===k.columnIndex&&b.value===k.value))return false;previous=ordinal;}
 }
 if(new Set(v.sourceKeys.map(k=>k.columnIndex)).size!==v.sourceKeys.length)return false;
 if(v.state==='ready'?typeof v.text!=='string'||!v.text.length||v.problem!==null:v.state==='emptyText'?v.text!=='':v.text!==null)return false;
 if(typeof v.text==='string'&&(!wellFormedText(v.text)||v.text.includes('\0')||v.text.length>4096||utf8.encode(v.text).byteLength>16384))return false;
 const unique=['ready','emptyText','invalidText'].includes(v.state)||v.state==='unsupported'&&v.target!==null;
 if(unique){if(s.state!=='ready'||!s.snapshot||v.observedCandidates!=='1'||!v.target||v.sourceKeys.length!==names.length)return false;}
 else if(v.target!==null)return false;
 if(v.state==='unsupported'&&v.target!==null&&v.problem?.code!=='ITEM_CLASSIFICATION_TEXT_LIMIT')return false;
 if(v.state==='invalidText'&&BigInt(s.snapshot?.textIssueCount??'0')===0n)return false;
 if(v.target){const t=v.target,c=s.snapshot!.columns[t.columnIndex];if(!exact(t,['tableId','rowId','columnIndex','isCopy','copySourceId'])||t.tableId!==s.snapshot!.tableId||typeof t.rowId!=='string'||!validRecordId(t.rowId)||!Number.isInteger(t.columnIndex)||t.columnIndex<0||!c||c.family!=='string'||c.isId||c.interpretation||c.name!==(s.kind==='class'?'ClassName_lang':'DisplayName_lang')||typeof t.isCopy!=='boolean'||(t.isCopy?typeof t.copySourceId!=='string'||!validRecordId(t.copySourceId)||t.copySourceId===t.rowId:t.copySourceId!==null)||BigInt(s.snapshot!.coverage[t.isCopy?'copyRows':'physicalRows'])===0n)return false;}
 if(v.observedCandidates!==null&&s.snapshot&&BigInt(v.observedCandidates)>BigInt(s.snapshot.rowCount))return false;
 if(v.state==='missingItem'&&(v.sourceKeys.length||v.observedCandidates!==null||v.problem!==null))return false;
 if(['fieldUnavailable','keyOutOfRange'].includes(v.state)&&(v.observedCandidates!==null||v.problem!==null))return false;
 if(['unavailable','protected','corrupt'].includes(v.state)&&v.state!==s.state)return false;
 if(v.state==='unsupported'&&v.target===null&&s.state!=='unsupported')return false;
 if(v.state==='keyOutOfRange'){if(v.sourceKeys.length!==names.length)return false;const a=BigInt(v.sourceKeys[0]!.value),b=s.kind==='subclass'?BigInt(v.sourceKeys[1]!.value):0n;if(b<0n||b>255n||a>=-128n&&a<=127n&&b<=127n)return false;}
 const failed=['unavailable','protected','corrupt'].includes(v.state)||v.state==='unsupported'&&v.target===null;
 if(failed&&(v.observedCandidates!==null||v.problem?.code!==s.problem?.code||v.problem?.message!==s.problem?.message||v.problem?.retryable!==s.problem?.retryable))return false;
 if(['notFound','ambiguous','incompleteTarget','emptyText','invalidText'].includes(v.state)&&v.problem!==null)return false;
 if(v.state==='notFound'&&(s.state!=='ready'||v.observedCandidates!=='0'))return false;
 if(v.state==='ambiguous'&&(!['ready','incompleteTarget'].includes(s.state)||v.observedCandidates===null||BigInt(v.observedCandidates)<2n))return false;
 if(v.state==='incompleteTarget'&&(s.state!=='incompleteTarget'||v.observedCandidates===null||BigInt(v.observedCandidates)>1n))return false;
 if(!['missingItem','fieldUnavailable','keyOutOfRange','unsupported'].includes(v.state)&&!['ready','incompleteTarget'].includes(s.state)&&v.state!==s.state)return false;
 if(!['missingItem','fieldUnavailable'].includes(v.state)&&!legacySubclass&&v.sourceKeys.length!==names.length)return false;
 if(!legacySubclass&&(unique||failed||['notFound','ambiguous','incompleteTarget'].includes(v.state))){if(v.sourceKeys.some((k,i)=>BigInt(k.value)<(i===1?0n:-128n)||BigInt(k.value)>127n))return false;}
 return true;
}
