import template from '../../../contracts/examples/creature-display-metadata.json' with{type:'json'};
export function creatureDisplay(core,summary,slot,opening,state='metadataResolved'){
 const v=structuredClone(template);Object.assign(v,{context:core.context,artifactId:core.artifactId,creatureId:summary.id,slot,opening:structuredClone(opening),creatureSource:{tableId:'c9d6b6b3',artifactId:core.creatureSource.artifactId,rowId:summary.id,columnIndex:null},creatureCoverage:structuredClone(core.creatureSource.coverage),selected:structuredClone(summary.displaySlots[slot]),state,absence:null,requiredTableId:null,problem:null});
 // Reviewed and raw columns after the edge column, so facts have cells to name (creature-display-facts-v1). Authored values.
 const col=(name,family='integer')=>({name,family,isId:false,reference:null});
 v.sources[0].snapshot.columns.push(col('CreatureModelScale','float'),...[0,1,2,3].map(i=>col(`TextureVariationFileDataID[${i}]`)),col('SizeClass'));
 v.sources[1].snapshot.columns.push(col('ModelScale','float'),col('CollisionHeight','float'));
 for(const t of v.sources){Object.assign(t.snapshot,core.context);t.snapshot.textIssueCount='0';t.snapshot.decoderRevision=t.snapshot.source.projection?'wdc5-values-v7+table-admission-projection-v3':'wdc5-values-v7+table-admission-v3';}v.sources[0].row.source.rowId=v.selected.displayId;
 if(['notPrepared','protected','unsupported','unavailable','corrupt','resourceLimit','creatureNotFoundInAdmittedScope','noDeclaredDisplay','invalidDisplayReference'].includes(state)){v.sources=[];v.modelId=null;v.modelFileDataId=null;if(state==='notPrepared')v.requiredTableId='bfdaf9f1';if(['protected','unsupported','unavailable','corrupt','resourceLimit'].includes(state))v.problem={code:'AUTHORED_'+state.toUpperCase(),message:'Authored optional metadata outcome.',retryable:true,target:'display'};if(state==='creatureNotFoundInAdmittedScope'){v.selected=null;v.absence=core.creatureSource.coverage.scope==='complete'?'absentFromCompleteSource':'unknownOutsideReadableScope';}}
 if(['displayMissingInAdmittedScope','displayAmbiguous','noDeclaredModel'].includes(state)){v.sources=v.sources.slice(0,1);v.modelId=null;v.modelFileDataId=null;if(state==='noDeclaredModel')v.sources[0].row.edgeValue.value='0';else{v.sources[0].matchCount=state==='displayAmbiguous'?'2':'0';v.sources[0].row=null;}}
 if(['modelMissingInAdmittedScope','modelAmbiguous','noDeclaredModelFile'].includes(state)){v.modelFileDataId=null;if(state==='noDeclaredModelFile')v.sources[1].row.edgeValue.value='0';else{v.sources[1].matchCount=state==='modelAmbiguous'?'2':'0';v.sources[1].row=null;}}
 if(state==='displayMissingInAdmittedScope'||state==='modelMissingInAdmittedScope'){const source=v.sources.at(-1).snapshot;v.absence=source.coverage.scope==='complete'?'absentFromCompleteSource':'unknownOutsideReadableScope';}
 return v;
}
export async function displayDefinition(target,buildVersion){const{itemsDefinition}=await import('./items.mjs');const source=target.snapshot,d=itemsDefinition({itemSource:source,sparseSource:source},source.tableId,buildVersion);d.entry.name=target.role==='display'?'CreatureDisplayInfo':'CreatureModelData';for(const c of d.definition.candidates)for(const f of c.fields)if(f.family==='int'){f.integerBits=target.role==='display'?16:32;f.unsigned=true;}return d;}
// The reviewed facts of each uniquely matched row (creature-display-facts-v1), from the same authored chain.
export function creatureDisplayFacts(metadata){
 const values={display:['1.25','800101','0','0','0','1'],model:['1','2.25']},records=[];
 for(const t of metadata.sources){if(!t.row)continue;const columns=t.snapshot.columns,cell=i=>({column:columns[i].name,columnIndex:i,value:i===0?t.row.edgeValue.value:values[t.role][i-1]});
  if(t.role==='display')records.push({role:'display',tableId:'bfdaf9f1',table:'CreatureDisplayInfo',recordId:t.row.source.rowId,facts:[{key:'model',state:'value',cells:[cell(0)]},{key:'scale',state:'value',cells:[cell(1)]},{key:'textureVariations',state:'value',cells:[2,3,4,5].map(cell)}],raw:[cell(6)]});
  else records.push({role:'model',tableId:'7f695fa5',table:'CreatureModelData',recordId:t.row.source.rowId,facts:[{key:'file',state:'value',cells:[cell(0)]},{key:'scale',state:'value',cells:[cell(1)]}],raw:[cell(2)]});}
 return{metadata,factsRevision:'creature-display-facts-v1',records};
}
