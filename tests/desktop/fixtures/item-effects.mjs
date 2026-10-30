import {createHash} from 'node:crypto';
import readiness from '../../../contracts/examples/item-effects-readiness.json' with {type:'json'};
import page from '../../../contracts/examples/item-effects-page.json' with {type:'json'};
import details from '../../../contracts/examples/item-effect-details.json' with {type:'json'};
import names from '../../../contracts/examples/item-effect-names.json' with {type:'json'};
import parentReadiness from '../../../contracts/examples/item-effects-parent-readiness.json' with {type:'json'};
import parentPage from '../../../contracts/examples/item-effects-parent-page.json' with {type:'json'};
import parentDetails from '../../../contracts/examples/item-effect-parent-details.json' with {type:'json'};
import parentNames from '../../../contracts/examples/item-effect-parent-names.json' with {type:'json'};

/** Authored protocol fixtures only; original profile evidence comes from canonical examples. */
// parent: the Classic ItemEffect.ParentItemID link (#86), where one ItemEffect source is the link and the effect.
export function effectsFixtures(params={...readiness.selection.context,itemsArtifactId:readiness.selection.itemsArtifactId,itemId:'0'},parent=false){
 const rewrite=value=>Array.isArray(value)?value.map(rewrite):value&&typeof value==='object'?Object.fromEntries(Object.entries(value).map(([key,v])=>[key,['productId','buildIdentity','sourceRegion','researchLocale','itemsArtifactId','itemId'].includes(key)&&typeof v==='string'&&key in params?params[key]:rewrite(v)])):value;
 const result=parent?{readiness:rewrite(parentReadiness),page:rewrite(parentPage),details:rewrite(parentDetails),names:rewrite(parentNames)}:{readiness:rewrite(readiness),page:rewrite(page),details:rewrite(details),names:rewrite(names)};
 if(parent)result.readiness.source.snapshot=result.readiness.snapshot.bridgeSource;
 for(const edge of result.page.edges)edge.parentItemId.value=params.itemId;
 return result;
}

/** Cursor binding is protocol fixture construction only; the renderer treats returned cursors as opaque. */
export function effectsCursor(p,nextKey){
 const binding=createHash('sha256').update(JSON.stringify({selection:{context:{productId:p.productId,buildIdentity:p.buildIdentity,sourceRegion:p.sourceRegion,researchLocale:p.researchLocale},itemsArtifactId:p.itemsArtifactId,itemId:p.itemId},bridgeIndexArtifactId:p.bridgeIndexArtifactId,revision:'item-effect-bridge-index-v1',orderPolicy:'signed-item-unsigned-bridge-source-ordinal-v1',limit:p.limit??16})).digest('hex');
 return Buffer.from(JSON.stringify({binding,after:String(nextKey)})).toString('base64url');
}

/** Explicit authored UI scenarios; no game acquisition or source parsing. */
export function effectsScenario(params,scenario='ready',pageParams={}){
 const f=effectsFixtures(params,scenario==='parent'||scenario==='parent-unsupported'),page=f.page,details=f.details,names=f.names;
 if(scenario==='eight'){for(const source of[f.readiness.snapshot.bridgeSource,f.readiness.source.snapshot,page.bridgeSource]){source.rowCount='12';Object.assign(source.coverage,{physicalRows:'11',declaredRecords:'13'});}f.readiness.snapshot.emittedRows='12';f.readiness.observedAssociations='10';page.observedAssociations='10';const first=page.edges[0],all=Array.from({length:10},(_,i)=>({...structuredClone(first),key:{indexOrdinal:String(i)},bridge:{...structuredClone(first.bridge),rowId:String(100+i),sourceOrdinal:String(i)}}));f.allEdges=all;details.outcomes=all.map(e=>({...structuredClone(details.outcomes[0]),key:{...e.key}}));names.outcomes=all.map(e=>({...structuredClone(names.outcomes[0]),key:{...e.key}}));const after=pageParams.after?Number(JSON.parse(Buffer.from(pageParams.after,'base64url').toString()).after):0;page.edges=all.slice(after,after+(pageParams.limit??8));page.nextCursor=after+page.edges.length<all.length?effectsCursor({...params,bridgeIndexArtifactId:page.bridgeIndexArtifactId,limit:pageParams.limit??8},after+page.edges.length):null;}
 if(scenario==='zero'){f.readiness.observedAssociations='0';page.observedAssociations='0';page.edges=[];details.outcomes=[];names.outcomes=[];}
 if(scenario==='duplicate-row-id')page.edges[1].bridge.rowId=page.edges[0].bridge.rowId;
 if(scenario==='negative'){page.edges[0].itemEffectId.value='-1';details.outcomes[0]={...details.outcomes[0],itemEffectId:'-1',state:'effectKeyOutOfRange',observedCandidates:null,target:null,fields:null,absence:null,problem:null};names.outcomes[0]={...names.outcomes[0],effect:{...names.outcomes[0].effect,itemEffectId:'-1',state:'effectKeyOutOfRange',observedCandidates:null,target:null,spellId:null,absence:null,problem:null},state:'effectNotReady',observedCandidates:null,target:null,text:null,issue:null,textUnits:null,absence:null,problem:null};}
 if(['missing','ambiguous'].includes(scenario)){const state=scenario==='missing'?'notFoundInAdmittedScope':'ambiguous',count=scenario==='missing'?'0':'2',absence=scenario==='missing'?'unknownOutsideReadableScope':null;details.outcomes=details.outcomes.map(o=>({...o,state,observedCandidates:count,target:null,fields:null,absence}));names.outcomes=names.outcomes.map(o=>({...o,effect:{...o.effect,state,observedCandidates:count,target:null,spellId:null,absence},state:'effectNotReady',observedCandidates:null,target:null,text:null,issue:null,textUnits:null,absence:null}));}
 // #85: the build ships no ItemXItemEffect, so link preparation settles as notInBuild.
 // #86: an undecided Classic build whose ItemEffect layout no definition knows: the link is ParentItemID, unsupported.
 if(scenario==='parent-unsupported'){const problem={code:'ITEM_EFFECTS_SOURCE_UNSUPPORTED',message:'The selected source profile is unsupported.',retryable:false};Object.assign(f.readiness,{state:'unsupported',snapshot:null,observedAssociations:null,link:'parentItemId',source:{role:'bridge',state:'unsupported',snapshot:null,historicalArtifactId:null,problem},problem});}
 if(scenario==='not-in-build'){const problem={code:'ITEM_EFFECTS_SOURCE_NOT_IN_BUILD',message:'This build does not include the selected source.',retryable:false};Object.assign(f.readiness,{state:'notInBuild',snapshot:null,observedAssociations:null,source:{role:'bridge',state:'notInBuild',snapshot:null,historicalArtifactId:null,problem},problem});}
 if(scenario==='narrowed')narrowEffectInputs(f);
 if(scenario==='long-name')for(const o of names.outcomes)o.text='Authored exact source spell name '.repeat(120);
 if(scenario==='whitespace')for(const o of names.outcomes)o.text='\n\n\n   \t';
 if(scenario==='empty-name')for(const o of names.outcomes){o.state='emptyText';o.text='';}
 if(scenario==='invalid-name'){names.sources[1].snapshot.textIssueCount='1';for(const o of names.outcomes){o.state='invalidText';o.text=null;o.issue={code:'DB2_TEXT_ENCODING_INVALID',source:{...o.target,columnIndex:1}};}}
 if(scenario==='large-name')for(const o of names.outcomes){o.state='textTooLarge';o.text=null;o.textUnits='5000';}
 return f;
}
/** D108 (#95): authored Retail inputs whose columns narrowed (one constant upper-bit pattern), read at their declared
 * width with their declared references. Charges 0 has the shape of a stored 0xFFFF0000; Charges stays raw on the second
 * occurrence's column only when `mixed` is set, so a raw column keeps its own note. */
export function narrowEffectInputs(f,{mixed=false}={}){
 const refs={3:null,6:{table:'SpellCategory',column:'ID'},8:{table:'ChrSpecialization',column:'ID'}},values={3:'0',6:'7',8:'3'};
 const narrowed=i=>!(mixed&&i===3);
 for(const snapshot of[f.details.source.snapshot,f.names.sources.find(s=>s.role==='effect')?.snapshot].filter(Boolean))
  for(const i of[3,6,8])if(narrowed(i))Object.assign(snapshot.columns[i],{interpretation:null,narrowing:{storedBits:32,usedBits:16},reference:refs[i]});
 for(const outcome of f.details.outcomes)for(const field of outcome.fields??[])if(field.columnIndex in values&&narrowed(field.columnIndex))
  Object.assign(field,{value:values[field.columnIndex],evidence:'strictNumeric',interpretation:null});
 return f;
}
export function effectsNotPrepared(f,phase){const result=structuredClone(phase==='links'?f.readiness:phase==='inputs'?f.details:f.names),source={role:phase==='links'?'bridge':phase==='inputs'?'effect':'name',state:'notPrepared',snapshot:null,historicalArtifactId:null,problem:null};if(phase==='links')return{...result,state:'notPrepared',snapshot:null,source,observedAssociations:null,problem:null};if(phase==='inputs'){result.source=source;result.outcomes=result.outcomes.map(o=>o.state==='effectKeyOutOfRange'?o:{...o,state:'notPrepared',observedCandidates:null,target:null,fields:null,absence:null,problem:null});}else{result.sources[1]=source;result.outcomes=result.outcomes.map(o=>o.state==='effectNotReady'?o:{...o,state:'notPrepared',observedCandidates:null,target:null,text:null,issue:null,textUnits:null,absence:null,problem:null});}return result;}
export function effectsRows(f,tableId){const source=tableId==='00cb674f'?f.page.bridgeSource:tableId==='4002a5b1'?f.details.source.snapshot:f.names.sources[1].snapshot,rows=Array.from({length:Number(source.rowCount)},(_,i)=>({id:String(i),isCopy:false,copySourceId:null,values:source.columns.map(c=>c.family==='string'?'Authored source text':c.isId?String(i):'0')}));if(BigInt(source.coverage.copyRows)>0n&&rows.length>1){rows.at(-1).isCopy=true;rows.at(-1).copySourceId='0';}if(tableId==='00cb674f'){for(const row of rows)row.values[2]='-1';}if(tableId==='00cb674f')for(const e of f.allEdges??f.page.edges)rows[Number(e.bridge.sourceOrdinal)]={id:e.bridge.rowId,isCopy:e.bridge.isCopy,copySourceId:e.bridge.copySourceId,values:[e.bridge.rowId,e.itemEffectId.value,e.parentItemId.value]};else if(tableId==='4002a5b1'&&f.page.bridgeSource.tableId==='4002a5b1'){const inputs=f.details.outcomes.find(o=>o.fields)?.fields.map(field=>field.value)??Array(9).fill('0');for(const e of f.allEdges??f.page.edges)rows[Number(e.bridge.sourceOrdinal)]={id:e.bridge.rowId,isCopy:e.bridge.isCopy,copySourceId:e.bridge.copySourceId,values:[e.bridge.rowId,...inputs,e.parentItemId.value]};}
 else if(tableId==='4002a5b1'){const o=f.details.outcomes.find(o=>o.target&&o.fields);if(o)rows[0]={id:o.target.rowId,isCopy:o.target.isCopy,copySourceId:o.target.copySourceId,values:[o.target.rowId,...o.fields.map(f=>f.value)]};}else{const o=f.names.outcomes.find(o=>o.target);if(o){rows[0]={id:o.target.rowId,isCopy:o.target.isCopy,copySourceId:o.target.copySourceId,values:[o.target.rowId,o.state==='textTooLarge'?'x'.repeat(Number(o.textUnits)):o.text??'']};if(o.issue)rows[0].textIssues=[{columnIndex:1,code:'DB2_TEXT_ENCODING_INVALID',rawBytesHex:'c328'}];}}return rows;}
export async function effectsDefinition(source,buildVersion){const{itemsDefinition}=await import('./items.mjs'),d=itemsDefinition({itemSource:source,sparseSource:source},source.tableId,buildVersion);d.entry.name=source.tableId==='00cb674f'?'ItemXItemEffect':source.tableId==='4002a5b1'?'ItemEffect':'SpellName';for(const fields of[d.definition.columns,...d.definition.candidates.map(c=>c.fields)])for(const [i,field]of fields.entries()){field.verifiedName=field.name!=='PlayerConditionID';field.nonInline=field.name==='ID';if(source.tableId==='4002a5b1'){if(i<=2){field.integerBits=i===0?32:8;field.unsigned=true;}if(i===6)field.reference={table:'SpellCategory',column:'ID'};if(i===8)field.reference={table:'ChrSpecialization',column:'ID'};}else if(source.tableId==='46c66698'&&i===0)field.unsigned=true;}return d;}
