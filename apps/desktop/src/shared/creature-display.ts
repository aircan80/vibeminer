import type {CreaturesContext,CreaturesSnapshot,CreatureSummary,CreatureDisplayMetadataResult,CreatureDisplayTarget,OpeningSourceReference,CreatureDisplayFactsResult,CreatureDisplayFactCell,CreatureLabelLists} from '../../../../contracts/generated/discovery';
import {validCreatureContext,creatureKey,validCreatureProbability} from './creatures';
import {validRecordId,tableDataMatches,isPartialSource,reviewedDefinition,reviewedRevision} from './table-data';
export type CreatureDisplayParams=CreaturesContext&{artifactId:string;creatureId:string;slot:number};
export type DisplayAuthority={core:CreaturesSnapshot;summary:CreatureSummary;opening:OpeningSourceReference};
const hash=/^[0-9a-f]{64}$/,decimal=/^(0|[1-9][0-9]{0,18})$/,utf8=new TextEncoder();
const same=(a:unknown,b:unknown):boolean=>a===b||!!a&&!!b&&typeof a==='object'&&typeof b==='object'&&Array.isArray(a)===Array.isArray(b)&&Object.keys(a).length===Object.keys(b).length&&Object.keys(a).every(k=>Object.hasOwn(b,k)&&same((a as Record<string,unknown>)[k],(b as Record<string,unknown>)[k]));
export const displayKey=(p:CreatureDisplayParams)=>`${creatureKey(p)}|${p.artifactId}|${p.creatureId}|${p.slot}`;
export function validCreatureDisplay(v:unknown):v is CreatureDisplayParams{if(!v||typeof v!=='object')return false;const{artifactId,creatureId,slot,...context}=v as CreatureDisplayParams;return validCreatureContext(context)&&typeof artifactId==='string'&&hash.test(artifactId)&&typeof creatureId==='string'&&validRecordId(creatureId)&&Number.isInteger(slot)&&slot>=0&&slot<4;}
// Retail layout first, then the Classic layout the engine also accepts (D089).
const profiles=[{role:'display',table:'bfdaf9f1',file:'1108759',layouts:['7275F5F6','F75F884C'],definition:'64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425',field:'ModelID',max:65535n},{role:'model',table:'7f695fa5',file:'1365368',layouts:['0F5449F8','3FFEE408'],definition:'ace9dfbb1acdec0eae7899273a4e983d851d2de8d6471fd8eb95ac8e70584e2a',field:'FileDataID',max:4294967295n}]as const;
function targetMatches(t:CreatureDisplayTarget,index:number,p:CreatureDisplayParams,id:string,unsupported:boolean){const profile=profiles[index],s=t.snapshot;if(t.role!==profile.role||!tableDataMatches(s,{...p,tableId:profile.table})||s.association.fileDataId!==profile.file||!reviewedDefinition(s,profile.definition)||s.decoderRevision!==(isPartialSource(s.source)?'wdc5-values-v7+table-admission-projection-v3':'wdc5-values-v7+table-admission-v3')&&!(s.fetchedDefinition&&s.tableId==='bfdaf9f1'&&s.layoutHash==='7275F5F6'&&s.decoderRevision===reviewedRevision(s)))return false;
 if(t.matchCount===null)return unsupported&&t.row===null;
 if(typeof t.matchCount!=='string'||!decimal.test(t.matchCount)||BigInt(t.matchCount)>BigInt(s.rowCount)||!(profile.layouts as readonly string[]).includes(s.layoutHash))return false;
 if(t.matchCount!=='1')return t.row===null;
 const r=t.row;if(!r||r.source.tableId!==profile.table||r.source.artifactId!==s.artifactId||r.source.rowId!==id||r.source.columnIndex!==r.edgeValue.columnIndex||r.edgeValue.tableId!==profile.table||!Number.isInteger(r.edgeValue.columnIndex)||r.edgeValue.columnIndex<0||r.edgeValue.columnIndex>=s.columns.length||typeof r.edgeValue.value!=='string'||!validRecordId(r.edgeValue.value)||BigInt(r.edgeValue.value)>profile.max)return false;
 const col=s.columns[r.edgeValue.columnIndex];return col.name===profile.field&&col.family==='integer'&&!col.isId&&!col.interpretation&&s.columns.filter(c=>c.name===profile.field).length===1&&r.isCopy===(r.copySourceId!==null)&&(r.copySourceId===null||typeof r.copySourceId==='string'&&validRecordId(r.copySourceId)&&r.copySourceId!==r.source.rowId)&&BigInt(s.coverage[r.isCopy?'copyRows':'physicalRows'])>0n;
}
export function creatureDisplayMatches(v:CreatureDisplayMetadataResult,p:CreatureDisplayParams,authority?:DisplayAuthority):boolean{try{
 if(!validCreatureDisplay(p)||creatureKey(v.context)!==creatureKey(p)||!validCreatureContext(v.context)||v.artifactId!==p.artifactId||v.creatureId!==p.creatureId||v.slot!==p.slot||v.metadataPolicy!=='creature-display-metadata-v1'||v.appearance!=='notEvaluated'||utf8.encode(JSON.stringify(v)).byteLength>512*1024)return false;
 const opening=v.opening;if(Object.keys(opening).length!==6||typeof opening.generation!=='string'||!decimal.test(opening.generation)||typeof opening.versionsSha256!=='string'||!hash.test(opening.versionsSha256)||typeof opening.cdnsSha256!=='string'||!hash.test(opening.cdnsSha256)||![opening.versionsRowOrdinal,opening.cdnsRowOrdinal].every(n=>Number.isSafeInteger(n)&&n>=0))return false;
 if(v.problem&&(typeof v.problem.code!=='string'||v.problem.code.length<1||v.problem.code.length>128||/[\x00-\x1f\x7f]/.test(v.problem.code)||typeof v.problem.message!=='string'||v.problem.message.length>400||typeof v.problem.retryable!=='boolean'||!['display','model',null].includes(v.problem.target)))return false;
 if(v.opening.region!==p.sourceRegion||v.creatureSource.tableId!=='c9d6b6b3'||typeof v.creatureSource.artifactId!=='string'||!hash.test(v.creatureSource.artifactId)||v.creatureSource.rowId!==p.creatureId||v.creatureSource.columnIndex!==null)return false;
 const selected=v.selected;if(selected){const n=selected.displayId;if(selected.slot!==p.slot||typeof n!=='string'||!/^(0|-?[1-9][0-9]{0,9})$/.test(n)||BigInt(n)<-2147483648n||BigInt(n)>2147483647n||selected.state!==(BigInt(n)>0n?'declared':n==='0'?'noDeclaredDisplay':'invalidDisplayReference')||!validCreatureProbability(selected.probability)||[selected.displayField,selected.probabilityField].some(f=>f.tableId!=='c9d6b6b3'||!Number.isInteger(f.columnIndex)||f.columnIndex<0||f.columnIndex>=512)||selected.displayField.columnIndex===selected.probabilityField.columnIndex)return false;}
 if(authority&&(!same(v.opening,authority.opening)||v.creatureSource.artifactId!==authority.core.creatureSource.artifactId||!same(v.creatureCoverage,authority.core.creatureSource.coverage)||authority.core.artifactId!==p.artifactId||creatureKey(authority.core.context)!==creatureKey(p)||authority.summary.id!==p.creatureId||!same(selected,authority.summary.displaySlots[p.slot])))return false;
 if(!Array.isArray(v.sources)||v.sources.length>2)return false;let id=selected?.displayId??'';
 for(const[tIndex,t]of v.sources.entries()){if(!selected||selected.state!=='declared'||tIndex===1&&(!v.sources[0]!.row||BigInt(v.sources[0]!.row.edgeValue.value)===0n)||!targetMatches(t,tIndex,p,id,v.state==='unsupported'&&v.problem?.target===profiles[tIndex].role))return false;id=t.row?.edgeValue.value??'';}
 const d=v.sources[0],m=v.sources[1],edge=d?.row?.edgeValue.value??null,file=m?.row?.edgeValue.value??null,positiveEdge=edge!==null&&BigInt(edge)>0n;
 if(v.modelId!==(positiveEdge?edge:null)||v.modelFileDataId!==(v.state==='metadataResolved'?file:null))return false;
 const scopeAbsence=(coverage:{scope:string})=>coverage.scope==='complete'?'absentFromCompleteSource':'unknownOutsideReadableScope';
 const missing=v.state==='creatureNotFoundInAdmittedScope'?v.creatureCoverage:v.state==='displayMissingInAdmittedScope'?d?.snapshot.coverage:v.state==='modelMissingInAdmittedScope'?m?.snapshot.coverage:null;
 if(v.absence!==(missing?scopeAbsence(missing):null))return false;
 const failed=['protected','unsupported','unavailable','corrupt','resourceLimit'].includes(v.state);
 if(failed){if(!v.problem||!['display','model'].includes(v.problem.target??'')||v.requiredTableId!==null||!selected||selected.state!=='declared'||v.problem.target==='display'&&v.sources.length>1||v.problem.target==='model'&&!positiveEdge)return false;return v.problem.target==='display'?v.sources.length===0||v.state==='unsupported'&&v.sources.length===1&&d?.matchCount===null:v.sources.length===1||v.state==='unsupported'&&v.sources.length===2&&m?.matchCount===null;}
 if(v.problem!==null)return false;
 if(v.state!=='notPrepared'&&v.requiredTableId!==null)return false;
 switch(v.state){case'creatureNotFoundInAdmittedScope':return selected===null&&v.sources.length===0;case'noDeclaredDisplay':return selected?.state==='noDeclaredDisplay'&&v.sources.length===0;case'invalidDisplayReference':return selected?.state==='invalidDisplayReference'&&v.sources.length===0;case'notPrepared':return selected?.state==='declared'&&(v.sources.length===0?v.requiredTableId===profiles[0].table:v.sources.length===1&&positiveEdge&&v.requiredTableId===profiles[1].table);case'displayMissingInAdmittedScope':return d?.matchCount==='0'&&v.sources.length===1;case'displayAmbiguous':return !!d&&BigInt(d.matchCount??'0')>1n&&v.sources.length===1;case'noDeclaredModel':return edge==='0'&&v.sources.length===1;case'modelMissingInAdmittedScope':return positiveEdge&&m?.matchCount==='0';case'modelAmbiguous':return positiveEdge&&!!m&&BigInt(m.matchCount??'0')>1n;case'noDeclaredModelFile':return positiveEdge&&file==='0';case'metadataResolved':return positiveEdge&&file!==null&&BigInt(file)>0n;default:return false;}
 }catch{return false;}}
// creature-display-facts-v1 (#62): the reviewed facts of the rows a local display chain matched exactly once, and the
// Classification label list. The engine picks the cells; this only checks that each cell is the exact column it names.
const FACT_KEYS={display:['model','scale','textureVariations'],model:['file','scale']} as const;
const FACT_FIELDS:Record<string,string>={'display:model':'ModelID','display:scale':'CreatureModelScale','display:textureVariations':'TextureVariationFileDataID','model:file':'FileDataID','model:scale':'ModelScale'};
const baseName=(column:string)=>{const i=column.indexOf('[');return i<0?column:column.slice(0,i);};
export function creatureDisplayFactsMatches(v:CreatureDisplayFactsResult,p:CreatureDisplayParams,authority?:DisplayAuthority):boolean{try{
 if(v.factsRevision!=='creature-display-facts-v1'||!creatureDisplayMatches(v.metadata,p,authority)||utf8.encode(JSON.stringify(v)).byteLength>512*1024)return false;
 const rowed=v.metadata.sources.filter(t=>t.row!==null);
 if(v.records.length>rowed.length)return false;
 let at=0;
 for(const r of v.records){
  while(at<rowed.length&&rowed[at].role!==r.role)at++;
  const t=rowed[at++];if(!t||!t.row)return false;
  const columns=t.snapshot.columns,expectedTable=r.role==='display'?['bfdaf9f1','CreatureDisplayInfo']:['7f695fa5','CreatureModelData'];
  if(r.tableId!==expectedTable[0]||r.table!==expectedTable[1]||r.tableId!==t.snapshot.tableId||r.recordId!==t.row.source.rowId)return false;
  const keys=FACT_KEYS[r.role];if(r.facts.length!==keys.length||r.facts.some((f,i)=>f.key!==keys[i]))return false;
  const seen=new Set<number>();
  const cellOk=(c:CreatureDisplayFactCell)=>Number.isInteger(c.columnIndex)&&c.columnIndex>=0&&c.columnIndex<columns.length&&columns[c.columnIndex].name===c.column&&!columns[c.columnIndex].isId&&typeof c.value==='string'&&!seen.has(c.columnIndex)&&(seen.add(c.columnIndex),true);
  for(const f of r.facts){
   if(f.state==='unsupported'?f.cells.length!==0:f.state!=='value'||f.cells.length===0)return false;
   if(!f.cells.every(c=>cellOk(c)&&baseName(c.column)===FACT_FIELDS[`${r.role}:${f.key}`]))return false;
   if(f.state==='unsupported'&&columns.some(c=>!c.isId&&baseName(c.name)===FACT_FIELDS[`${r.role}:${f.key}`]))return false;
  }
  if(!r.raw.every(cellOk)||r.raw.some((c,i)=>i>0&&c.columnIndex<=r.raw[i-1].columnIndex))return false;
  // The edge the chain followed is the same cell the model/file fact shows.
  const edge=r.facts[0].cells;if(edge.length!==1||edge[0].columnIndex!==t.row.edgeValue.columnIndex||edge[0].value!==t.row.edgeValue.value)return false;
 }
 return true;
 }catch{return false;}}
export function creatureLabelListsMatches(v:CreatureLabelLists):boolean{
 return !!v&&v.labelRevision==='creature-labels-v1'&&Array.isArray(v.classification)&&v.classification.length<=64&&v.classification.every((l,i,a)=>/^(0|[1-9][0-9]{0,2})$/.test(l.value)&&typeof l.label==='string'&&l.label.length>0&&l.label.length<=64&&(i===0||Number(a[i-1].value)<Number(l.value)));
}
