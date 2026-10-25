import type {ItemsContext,ItemsSnapshot,ItemsReadiness,ItemsPage,ItemInspectResult,ItemSummary,TableDataSnapshot,TableDataRow,ItemFactsResult,ItemLabelLists} from '../../../../contracts/generated/discovery';
import {validOpen} from './bridge';
import {tableDataMatches,validRecordId,validTableTextIssues} from './table-data';
export type ItemsPageParams=ItemsContext&{artifactId:string;query?:string;exactItemId?:string;limit?:number;after?:string};
export type ItemInspectParams=ItemsContext&{artifactId:string;itemId:string};
const utf8=new TextEncoder(),hash=/^[0-9a-f]{64}$/,count=/^(0|[1-9][0-9]{0,18})$/;
export const itemsKey=(c:ItemsContext)=>[c.productId,c.buildIdentity,c.sourceRegion,c.researchLocale].join(':');
export const sameItemsContext=(a:ItemsContext,b:ItemsContext)=>itemsKey(a)===itemsKey(b);
export function validItemsContext(value:unknown):value is ItemsContext{if(!value||typeof value!=='object')return false;const {researchLocale,...context}=value as ItemsContext;return researchLocale==='enUS'&&validOpen(context);}
export function wellFormedText(value:string):boolean{for(let i=0;i<value.length;i++){const c=value.charCodeAt(i);if(c>=0xd800&&c<=0xdbff){const low=value.charCodeAt(++i);if(!(low>=0xdc00&&low<=0xdfff))return false;}else if(c>=0xdc00&&c<=0xdfff)return false;}return true;}
export function validItemsQuery(value:unknown):value is string{return typeof value==='string'&&!value.includes('\0')&&wellFormedText(value)&&Array.from(value).length<=128&&utf8.encode(value).byteLength<=512;}
export function validItemsPage(value:unknown):value is ItemsPageParams{if(!value||typeof value!=='object')return false;const {artifactId,query,exactItemId,limit,after,...context}=value as ItemsPageParams;return validItemsContext(context)&&typeof artifactId==='string'&&hash.test(artifactId)&&(query===undefined||validItemsQuery(query))&&(exactItemId===undefined||typeof exactItemId==='string'&&validRecordId(exactItemId))&&(!query||exactItemId===undefined)&&(limit===undefined||Number.isInteger(limit)&&limit>=1&&limit<=50)&&(after===undefined||typeof after==='string'&&/^[A-Za-z0-9_-]{1,2048}$/.test(after));}
export function validItemInspect(value:unknown):value is ItemInspectParams{if(!value||typeof value!=='object')return false;const {artifactId,itemId,...context}=value as ItemInspectParams;return validItemsContext(context)&&typeof artifactId==='string'&&hash.test(artifactId)&&typeof itemId==='string'&&validRecordId(itemId);}
export function itemsSnapshotMatches(s:ItemsSnapshot,c:ItemsContext):boolean{
 if(!sameItemsContext(s.context,c)||!tableDataMatches(s.itemSource,{...c,tableId:'50238ec2'})||!tableDataMatches(s.sparseSource,{...c,tableId:'919be54e'}))return false;
 const n=s.counts;if(!Object.values(n).every(v=>count.test(v)))return false;const b=(v:string)=>BigInt(v);
 if(n.itemRows!==s.itemSource.rowCount||n.sparseRows!==s.sparseSource.rowCount||b(n.matchedRows)+b(n.itemWithoutSparseRows)!==b(n.itemRows)||b(n.matchedRows)+b(n.sparseOnlyRows)!==b(n.sparseRows)||b(n.validNames)+b(n.emptyNames)+b(n.invalidNames)!==b(n.matchedRows))return false;
 if(new Set(s.propertyGroups.map(g=>g.key)).size!==s.propertyGroups.length)return false;
 const seen=new Set<string>();
 for(const group of s.propertyGroups)for(const field of group.fields){const source=field.tableId==='50238ec2'?s.itemSource:field.tableId==='919be54e'?s.sparseSource:null,key=`${field.tableId}:${field.columnIndex}`;if(!source||!Number.isInteger(field.columnIndex)||field.columnIndex<0||!source.columns[field.columnIndex]||source.columns[field.columnIndex].interpretation||seen.has(key))return false;seen.add(key);}
 return true;
}
export function itemsReadinessMatches(r:ItemsReadiness,c:ItemsContext):boolean{
 const ids=r.dependencies.map(d=>d.tableId);if(ids.length!==2||new Set(ids).size!==2||!ids.includes('50238ec2')||!ids.includes('919be54e')||r.dependencies.some(d=>(d.state==='ready')!==(d.artifactId!==null)))return false;
 return r.snapshot===null||itemsSnapshotMatches(r.snapshot,c)&&r.dependencies.every(d=>d.state==='ready'&&d.artifactId===(d.tableId==='50238ec2'?r.snapshot!.itemSource.artifactId:r.snapshot!.sparseSource.artifactId));
}
const summaryNames={"50238ec2":['ClassID','SubclassID','InventoryType'],"919be54e":['OverallQualityID','ItemLevel','InventoryType']} as const;
export function itemSummaryMatches(s:ItemSummary,snapshot?:ItemsSnapshot):boolean{
 if(!validRecordId(s.id)||s.iconFileDataId!==null&&!validRecordId(s.iconFileDataId))return false;
 if(s.name.state==='valid'?typeof s.name.value!=='string'||s.name.value.length===0||s.name.value.includes('\0')||!wellFormedText(s.name.value)||utf8.encode(s.name.value).byteLength>262144:s.name.state==='empty'?s.name.value!=='':!['invalid','missingSparse'].includes(s.name.state)||s.name.value!==null)return false;
 const seen=new Set<string>();for(const v of s.baseValues){const key=`${v.tableId}:${v.columnIndex}`;if(typeof v.value!=='string'||!/^-(?:[1-9][0-9]{0,19})$|^(?:0|[1-9][0-9]{0,19})$/.test(v.value)||!['50238ec2','919be54e'].includes(v.tableId)||!Number.isInteger(v.columnIndex)||v.columnIndex<0||v.columnIndex>=512||seen.has(key))return false;seen.add(key);if(snapshot){const source=v.tableId==='50238ec2'?snapshot.itemSource:snapshot.sparseSource,column=source.columns[v.columnIndex];if(!column||column.family!=='integer'||column.interpretation||!(summaryNames[v.tableId] as readonly string[]).includes(column.name)||s.name.state==='missingSparse'&&v.tableId==='919be54e')return false;}}
 return s.baseValues.length<=8;
}
export function itemsPageMatches(page:ItemsPage,p:ItemsPageParams,snapshot?:ItemsSnapshot):boolean{
 if(!sameItemsContext(page.context,p)||page.artifactId!==p.artifactId||page.query!==(p.query??'')||page.exactItemId!==(p.exactItemId??null)||page.entries.length>(p.limit??50)||utf8.encode(JSON.stringify(page)).byteLength>512*1024||snapshot&&snapshot.artifactId!==page.artifactId)return false;
 let previous=-1n;for(const item of page.entries){if(!itemSummaryMatches(item,snapshot)||BigInt(item.id)<=previous||p.exactItemId!==undefined&&item.id!==p.exactItemId)return false;previous=BigInt(item.id);}
 return page.nextCursor===null||page.entries.length>0&&/^[A-Za-z0-9_-]{1,2048}$/.test(page.nextCursor)&&page.nextCursor!==p.after;
}
function rowMatches(row:TableDataRow,source?:TableDataSnapshot):boolean{return validRecordId(row.id)&&row.isCopy===(row.copySourceId!==null)&&(row.copySourceId===null||validRecordId(row.copySourceId))&&(!source||row.values.length===source.columns.length)&&utf8.encode(JSON.stringify(row)).byteLength<=256*1024&&(!source||BigInt(row.textIssues?.length??0)<=BigInt(source.textIssueCount??'0'))&&validTableTextIssues(row,source?.columns);}
export function itemInspectMatches(r:ItemInspectResult,p:ItemInspectParams,snapshot?:ItemsSnapshot):boolean{
 if(!sameItemsContext(r.context,p)||r.artifactId!==p.artifactId||r.itemId!==p.itemId||utf8.encode(JSON.stringify(r)).byteLength>512*1024||snapshot&&snapshot.artifactId!==r.artifactId)return false;
 const d=r.detail;if(!d)return true;
 if(d.summary.id!==p.itemId||d.itemRow.id!==p.itemId||d.sparseRow&&d.sparseRow.id!==p.itemId||!itemSummaryMatches(d.summary,snapshot)||!rowMatches(d.itemRow,snapshot?.itemSource)||d.sparseRow&&!rowMatches(d.sparseRow,snapshot?.sparseSource)||(d.summary.name.state==='missingSparse')!==(d.sparseRow===null))return false;
 if(!snapshot)return true;
 for(const v of d.summary.baseValues){const row=v.tableId==='50238ec2'?d.itemRow:d.sparseRow;if(!row||row.values[v.columnIndex]!==v.value)return false;}
 if(d.sparseRow){const index=snapshot.sparseSource.columns.findIndex(c=>c.name==='Display_lang'&&c.family==='string'&&!c.interpretation);if(index<0)return false;const issue=d.sparseRow.textIssues?.some(i=>i.columnIndex===index),value=d.sparseRow.values[index];if(issue?d.summary.name.state!=='invalid':d.summary.name.value!==value||d.summary.name.state!==(value.length?'valid':'empty'))return false;}
 const icon=snapshot.itemSource.columns.findIndex(c=>c.name==='IconFileDataID'&&c.family==='integer'&&!c.interpretation);const value=icon>=0?d.itemRow.values[icon]:null;
 return d.summary.iconFileDataId===(value!==null&&validRecordId(value)?value:null);
}
// item-facts-v1 (#61): the engine's reviewed projection of one item's Item/ItemSparse rows, and its label lists.
export const itemFactKeys=['description','display1','display2','display3','icon','quality','itemLevel','itemSquishEra','contentTuning','playerLevelCurve','itemLevelOffsetCurve','itemLevelOffset','class','subclass','inventoryType','requiredLevel','requiredSkill','requiredSkillRank','requiredAbility','minFaction','minReputation','allowableClass','allowableRaces','bonding','stackable','maxCount','containerSlots','buyPrice','sellPrice','vendorStackCount','durationInInventory','statType','statAmount','statPercentEditor','statSocketShare','socketType','socketBonus','gemProperties','damageType','itemDelay','dmgVariance','itemRange','flags','zoneBound','instanceBound','requiredHoliday','priceVariance','priceRandomValue'] as const;
export type ItemFactKey=typeof itemFactKeys[number];
const labelValue=/^(0|[1-9][0-9]{0,2})$/;
export function validItemLabelsParams(value:unknown):boolean{return value===undefined||!!value&&typeof value==='object'&&!Array.isArray(value)&&Object.keys(value).length===0;}
export function itemLabelListsMatches(r:ItemLabelLists):boolean{
 if(!r||r.labelRevision!=='item-labels-v1')return false;
 return [r.quality,r.inventoryType,r.bonding,r.trigger].every(list=>Array.isArray(list)&&list.length<=64&&list.every((l,i)=>labelValue.test(l.value)&&typeof l.label==='string'&&l.label.length>0&&l.label.length<=64&&(i===0||BigInt(l.value)>BigInt(list[i-1].value))));
}
export function itemFactsMatches(r:ItemFactsResult,p:ItemInspectParams,snapshot?:ItemsSnapshot):boolean{
 if(!sameItemsContext(r.context,p)||r.artifactId!==p.artifactId||r.itemId!==p.itemId||r.policyRevision!=='item-facts-v1'||r.labelRevision!=='item-labels-v1'||!['present','missing','unknown'].includes(r.sparse)||utf8.encode(JSON.stringify(r)).byteLength>512*1024||snapshot&&snapshot.artifactId!==r.artifactId)return false;
 if(snapshot&&r.sparse!=='present'&&r.sparse!==(snapshot.sparseSource.coverage.scope==='complete'?'missing':'unknown'))return false;
 // Outside the Item scope the engine looks the ItemSparse row up: a Sparse-only row is present without facts.
 if(r.facts===null)return true;
 if(r.facts.length!==itemFactKeys.length||r.facts.some((f,i)=>f.key!==itemFactKeys[i]))return false;
 const seen=new Set<string>();
 for(const fact of r.facts){
  if(fact.values.length>16)return false;
  if((fact.state==='missing'||fact.state==='unknown'||fact.state==='unsupported')!==(fact.values.length===0))return false;
  if((fact.state==='missing'||fact.state==='unknown')&&fact.state!==r.sparse)return false;
  // Item cells come first, then ItemSparse cells. single: one table; agree/disagree: both, same element count, compared in
  // order; separate: both, different element counts (Classic Item.DamageType[0–4] beside ItemSparse.DamageType).
  const item=fact.values.filter(v=>v.tableId==='50238ec2'),sparse=fact.values.filter(v=>v.tableId==='919be54e');
  if(fact.values.some((v,i)=>i<item.length?v.tableId!=='50238ec2':v.tableId!=='919be54e'))return false;
  if(!['single','agree','disagree','separate'].includes(fact.agreement))return false;
  if(fact.agreement==='single'){if(item.length&&sparse.length)return false;}
  else{
   if(!item.length||!sparse.length)return false;
   if(fact.agreement==='separate'){if(item.length===sparse.length)return false;}
   else if(item.length!==sparse.length||(fact.agreement==='agree')!==item.every((v,i)=>v.value===sparse[i]!.value))return false;
  }
  for(const v of fact.values){
   const key=`${v.tableId}:${v.columnIndex}`;
   if(seen.has(key)||!validRecordId(v.recordId)||v.recordId!==p.itemId||!['50238ec2','919be54e'].includes(v.tableId)||(v.tableId==='50238ec2')!==(v.table==='Item')||!Number.isInteger(v.columnIndex)||v.columnIndex<0||v.columnIndex>511||typeof v.value!=='string'||!wellFormedText(v.value)||v.label!==null&&(typeof v.label!=='string'||!v.label.length||v.label.length>64))return false;
   seen.add(key);
   if(v.tableId==='919be54e'&&r.sparse!=='present')return false;
   if(snapshot){const column=(v.tableId==='50238ec2'?snapshot.itemSource:snapshot.sparseSource).columns[v.columnIndex];if(!column||column.name!==v.column)return false;}
  }
  if(fact.state==='invalidText'&&!fact.values.some(v=>v.value===''))return false;
 }
 return true;
}
