// @vitest-environment node
import {it,expect} from 'vitest';
import type {TableDataSnapshot,TableDefinitionSnapshot} from '../../../contracts/generated/discovery';
import template from '../../../tests/desktop/fixtures/table-data.json';
import partialTemplate from '../../../tests/desktop/fixtures/table-data-partial.json';
import definitions from '../../../tests/desktop/fixtures/table-definitions.json';
import {tableDataMatches} from '../src/shared/table-data';
function authored(kind:'subclass'|'creature',ordinal=kind==='subclass'?6:8){
 const s=structuredClone(template) as TableDataSnapshot,d=structuredClone(definitions.definitions.b977271e) as TableDefinitionSnapshot;
 const creature=kind==='creature',name=creature?({8:'NPCSoundID',12:'ObjectEffectPackageID',13:'AnimReplacementSetID'} as Record<number,string>)[ordinal]:'PrerequisiteProficiency';
 Object.assign(s,{tableId:creature?'bfdaf9f1':'95d8638f',layoutHash:creature?'7275F5F6':'1E67DB87',definitionSha256:creature?'64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425':'e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c',decoderRevision:'wdc5-values-v7+table-admission-v3'});
 s.association.fileDataId=creature?'1108759':'1261604';d.entry.tableId=s.tableId;d.entry.db2FileDataId=s.association.fileDataId;d.definition!.sha256=s.definitionSha256;const candidate=d.definition!.candidates[0]!;candidate.layoutHash=s.layoutHash;
 candidate.fields=Array.from({length:ordinal+1},(_,index)=>({...candidate.fields[0],name:index===ordinal?name:index===0?'ID':`AuthoredField${index}`,family:'int',arrayLength:1,integerBits:index===ordinal?(creature?16:8):32,unsigned:index===ordinal&&creature,isId:index===0,verifiedName:true,reference:null,nonInline:false,relationStorage:false}));
 s.columns=candidate.fields.map(field=>({name:field.name,family:'integer',isId:field.isId,reference:null}));s.columns[ordinal].interpretation={policy:'raw-physical-integer-v1',physicalBits:32,physicalSigned:false,candidateBits:creature?16:8,candidateUnsigned:creature,warning:'Authored exact-profile raw evidence; no semantic interpretation is inferred.'};
 return{s,d,p:{productId:s.productId,buildIdentity:s.buildIdentity,sourceRegion:s.sourceRegion,researchLocale:'enUS' as const,tableId:s.tableId},ordinal};
}
it.each([6,8,12,13])('accepts only the reviewed pinned verified-column raw profile at selected field ordinal%i',ordinal=>{const{s,d,p}=authored(ordinal===6?'subclass':'creature',ordinal);expect(tableDataMatches(s,p,d)).toBe(true);});
it.each(['definition','layout','table','provider','ordinal','name','candidateBits','candidateSign','physicalBits','physicalSign','revision','id','reference','array','relation','nonInline'])('keeps an unreviewed verified-column %s strict',change=>{const{s,d,p,ordinal}=authored('subclass'),c=d.definition!.candidates[0]!,field=c.fields[ordinal],column=s.columns[ordinal];switch(change){case'definition':s.definitionSha256=d.definition!.sha256='0'.repeat(64);break;case'layout':s.layoutHash=c.layoutHash='00000000';break;case'table':s.tableId=d.entry.tableId=p.tableId='deadbeef';break;case'provider':s.providerRevision=d.provider.revision='0'.repeat(40);break;case'ordinal':c.fields.unshift({...field,name:'AnotherAuthoredField'});s.columns.unshift({name:'AnotherAuthoredField',family:'integer',isId:false,reference:null});break;case'name':column.name=field.name='DifferentField';break;case'candidateBits':column.interpretation!.candidateBits=field.integerBits=16;break;case'candidateSign':column.interpretation!.candidateUnsigned=field.unsigned=true;break;case'physicalBits':column.interpretation!.physicalBits=64;break;case'physicalSign':column.interpretation!.physicalSigned=true;break;case'revision':s.decoderRevision='wdc5-values-v8+table-admission-v4';break;case'id':column.isId=field.isId=true;break;case'reference':column.reference={table:'SkillLine',column:'ID'};break;case'relation':field.relationStorage=true;break;case'nonInline':field.nonInline=true;break;case'array':field.arrayLength=2;s.columns.splice(ordinal,1,{...column,name:field.name+'[0]'},{...column,name:field.name+'[1]'});break;}expect(tableDataMatches(s,p,d)).toBe(false);});
it('admits the historical exact Subclassv2 profile but keeps Creature rules atv3',()=>{const sub=authored('subclass');sub.s.decoderRevision='wdc5-values-v7+table-admission-v2';expect(tableDataMatches(sub.s,sub.p,sub.d)).toBe(true);const creature=authored('creature');creature.s.decoderRevision='wdc5-values-v7+table-admission-v2';expect(tableDataMatches(creature.s,creature.p,creature.d)).toBe(false);});


function authoredEffect(projected=false){
 const {s,d,p}=authored('subclass'),candidate=d.definition!.candidates[0]!;
 Object.assign(s,{tableId:'4002a5b1',layoutHash:'4CA77678',definitionSha256:'7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c',decoderRevision:projected?'wdc5-values-v7+table-admission-projection-v5':'wdc5-values-v7+table-admission-v5'});
 if(projected){s.source=structuredClone(partialTemplate.source) as TableDataSnapshot['source'];s.association=structuredClone(partialTemplate.association) as TableDataSnapshot['association'];s.coverage=structuredClone(partialTemplate.coverage) as TableDataSnapshot['coverage'];s.rowCount=partialTemplate.rowCount;}
 s.association.fileDataId='969941';p.tableId=s.tableId;d.entry.tableId=s.tableId;d.entry.db2FileDataId='969941';d.definition!.sha256=s.definitionSha256;candidate.layoutHash=s.layoutHash;
 const fields={3:'Charges',6:'SpellCategoryID',8:'ChrSpecializationID'} as Record<number,string>;
 candidate.fields=Array.from({length:9},(_,ordinal)=>({...candidate.fields[1],name:fields[ordinal]??(ordinal===0?'ID':ordinal===7?'SpellID':`AuthoredField${ordinal}`),arrayLength:1,isId:ordinal===0,nonInline:ordinal===0,relationStorage:false,verifiedName:true,integerBits:fields[ordinal]?16:32,unsigned:ordinal===6||ordinal===8,reference:ordinal===7?{table:'Spell',column:'ID'}:null}));
 s.columns=candidate.fields.map(field=>({name:field.name,family:'integer',isId:field.isId,reference:field.reference}));
 for(const ordinal of[3,6,8])s.columns[ordinal].interpretation={policy:'raw-physical-integer-v1',physicalBits:32,physicalSigned:false,candidateBits:16,candidateUnsigned:ordinal!==3,warning:'Authored exact ItemEffect raw evidence; candidate metadata is not a semantic charge or relationship interpretation.'};
 return{s,d,p};
}
it.each([false,true])('admits the exact three ItemEffect columns only in the corresponding source-class v5 envelope (projected=%s)',projected=>{const{s,d,p}=authoredEffect(projected);expect(tableDataMatches(s,p,d)).toBe(true);expect(s.columns[3].interpretation!.physicalSigned).toBe(false);expect(s.columns[3].interpretation!.candidateUnsigned).toBe(false);expect(s.columns[7].reference).toEqual({table:'Spell',column:'ID'});for(const ordinal of[3,6,8])expect(s.columns[ordinal].reference).toBeNull();});
it.each([3,6,8])('does not relax another ItemEffect ordinal%i tuple component',ordinal=>{
 const changes=['definition','layout','table','provider','fdid','ordinal','name','candidateBits','candidateSign','physicalBits','physicalSign','revision','id','reference','array','relation','nonInline'] as const;
 for(const change of changes){const{s,d,p}=authoredEffect(),candidate=d.definition!.candidates[0]!,field=candidate.fields[ordinal],column=s.columns[ordinal];
  switch(change){case'definition':s.definitionSha256=d.definition!.sha256='0'.repeat(64);break;case'layout':s.layoutHash=candidate.layoutHash='00000000';break;case'table':s.tableId=d.entry.tableId=p.tableId='deadbeef';break;case'provider':s.providerRevision=d.provider.revision='0'.repeat(40);break;case'fdid':s.association.fileDataId=d.entry.db2FileDataId='1';break;case'ordinal':candidate.fields.unshift({...field,name:'DifferentAuthoredField',integerBits:32,unsigned:false});s.columns.unshift({name:'DifferentAuthoredField',family:'integer',isId:false,reference:null});break;case'name':column.name=field.name='DifferentField';break;case'candidateBits':column.interpretation!.candidateBits=field.integerBits=8;break;case'candidateSign':column.interpretation!.candidateUnsigned=field.unsigned=!field.unsigned;break;case'physicalBits':column.interpretation!.physicalBits=64;break;case'physicalSign':column.interpretation!.physicalSigned=true;break;case'revision':s.decoderRevision='wdc5-values-v7+table-admission-v3';break;case'id':column.isId=field.isId=true;break;case'reference':column.reference={table:'Spell',column:'ID'};break;case'array':field.arrayLength=2;s.columns.splice(ordinal,1,{...column,name:field.name+'[0]'},{...column,name:field.name+'[1]'});break;case'relation':field.relationStorage=true;break;case'nonInline':field.nonInline=true;break;}
  expect(tableDataMatches(s,p,d),`${ordinal}:${change}`).toBe(false);
 }
});
it.each([false,true])('rejects the wrong source-class revision and v5 attached to another scope, with no definition argument too (projected=%s)',projected=>{
 const revisions=['wdc5-values-v7+table-admission-v5','wdc5-values-v7+table-admission-projection-v5'],before=['wdc5-values-v7+table-admission-v4','wdc5-values-v7+table-admission-projection-v4'];
 const wrong=authoredEffect(projected);wrong.s.decoderRevision=revisions[projected?0:1];expect(tableDataMatches(wrong.s,wrong.p,wrong.d)).toBe(false);expect(tableDataMatches(wrong.s,wrong.p)).toBe(false);
 // D108: ItemEffect rows read with v4, the raw rules before it, are not current.
 const old=authoredEffect(projected);old.s.decoderRevision=before[projected?1:0];expect(tableDataMatches(old.s,old.p,old.d)).toBe(false);
 for(const kind of['subclass','creature']as const)for(const revision of[revisions,before]){const{s,d,p}=authored(kind);s.decoderRevision=revision[projected?1:0];expect(tableDataMatches(s,p,d)).toBe(false);expect(tableDataMatches(s,p)).toBe(false);}
 for(const kind of['subclass','creature']as const){const v5=fetched(authored(kind));v5.s.decoderRevision=revisions[0];expect(tableDataMatches(v5.s,v5.p,v5.d)).toBe(false);}
 for(const name of['table','definition','provider','fdid']as const){const{s,d,p}=authoredEffect(projected);switch(name){case'table':s.tableId=d.entry.tableId=p.tableId='deadbeef';break;case'definition':s.definitionSha256=d.definition!.sha256='0'.repeat(64);break;case'provider':s.providerRevision=d.provider.revision='0'.repeat(40);break;case'fdid':s.association.fileDataId=d.entry.db2FileDataId='1';break;}expect(tableDataMatches(s,p),name).toBe(false);}
});
it('recognizes the scoped v5 envelope on another selected layout without opting its verified columns into raw evidence',()=>{const{s,d,p}=authoredEffect();s.layoutHash=d.definition!.candidates[0]!.layoutHash='00000000';for(const ordinal of[3,6,8])s.columns[ordinal].interpretation=null;expect(tableDataMatches(s,p,d)).toBe(true);s.columns[3].interpretation={policy:'raw-physical-integer-v1',physicalBits:32,physicalSigned:false,candidateBits:16,candidateUnsigned:false,warning:'Authored wrong-layout raw annotation.'};expect(tableDataMatches(s,p,d)).toBe(false);});
it('does not confuse a full source receipt with complete native table coverage',()=>{const{s,d,p}=authoredEffect();s.coverage=structuredClone(partialTemplate.coverage) as TableDataSnapshot['coverage'];s.rowCount=partialTemplate.rowCount;expect(tableDataMatches(s,p,d)).toBe(true);});
// D108 (#95): a reviewed ItemEffect column whose upper bits are one constant pattern narrows: no raw interpretation, the
// narrowing marker and its declared reference. Each column decides for itself.
it('admits narrowed reviewed ItemEffect columns with their declared references alongside a raw one',()=>{
 const{s,d,p}=authoredEffect(),candidate=d.definition!.candidates[0]!;
 candidate.fields[6]!.reference={table:'SpellCategory',column:'ID'};candidate.fields[8]!.reference={table:'ChrSpecialization',column:'ID'};
 for(const ordinal of[3,6])Object.assign(s.columns[ordinal],{interpretation:null,narrowing:{storedBits:32,usedBits:16},reference:candidate.fields[ordinal]!.reference});
 s.columns[8].reference=null;
 expect(tableDataMatches(s,p,d)).toBe(true);
 const rawWithReference=structuredClone(s);rawWithReference.columns[8].reference={table:'ChrSpecialization',column:'ID'};expect(tableDataMatches(rawWithReference,p,d)).toBe(false);
 const both=structuredClone(s);both.columns[3].interpretation=s.columns[8].interpretation;expect(tableDataMatches(both,p,d)).toBe(false);
});
// D101: a definition fetched from WoWDBDefs whose block for the reviewed layout is the reviewed one reads the same raw
// columns (the engine checks the block); here it must name its own commit and never the pinned definition or revision.
function fetched<T extends {s:TableDataSnapshot;d:TableDefinitionSnapshot}>(value:T):T{
 const commit='2'.repeat(40),info={revision:commit,fetchedAt:'2026-10-25T00:00:00Z'};
 Object.assign(value.s,{definitionSha256:'e'.repeat(64),providerRevision:commit,fetchedDefinition:info,fetchedBecause:'exactBuild',bundledFallback:false});
 value.d.definition!.sha256='e'.repeat(64);value.d.definition!.fetched=info;
 // Read with the reviewed rules, a fetched definition's rows carry v4 so they never share a key with rows it decoded without them.
 if(value.s.tableId!=='4002a5b1')value.s.decoderRevision='wdc5-values-v7+table-admission-v4';return value;
}
it.each(['subclass','creature'] as const)('needs v4 for raw columns read with a fetched %s definition and refuses v4 on its bundled one',kind=>{
 const v3=fetched(authored(kind));v3.s.decoderRevision='wdc5-values-v7+table-admission-v3';expect(tableDataMatches(v3.s,v3.p,v3.d)).toBe(false);
 const bundled=authored(kind);bundled.s.decoderRevision='wdc5-values-v7+table-admission-v4';expect(tableDataMatches(bundled.s,bundled.p,bundled.d)).toBe(false);expect(tableDataMatches(bundled.s,bundled.p)).toBe(false);
 const other=fetched(authored(kind));other.s.layoutHash=other.d.definition!.candidates[0]!.layoutHash='00000000';for(const c of other.s.columns)c.interpretation=null;expect(tableDataMatches(other.s,other.p,other.d)).toBe(false);
});
it.each(['subclass','creature','effect'] as const)('admits the reviewed raw columns read with a fetched %s definition only on the reviewed layout',kind=>{
 const make=()=>fetched(kind==='effect'?authoredEffect():authored(kind));
 const ok=make();expect(tableDataMatches(ok.s,ok.p,ok.d)).toBe(true);
 if(kind==='effect')expect(tableDataMatches(ok.s,ok.p)).toBe(true);
 const pinned=make();pinned.s.definitionSha256=pinned.d.definition!.sha256=kind==='effect'?'7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c':kind==='creature'?'64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425':'e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c';
 expect(tableDataMatches(pinned.s,pinned.p,pinned.d)).toBe(false);
 const layout=make();layout.s.layoutHash=layout.d.definition!.candidates[0]!.layoutHash='00000000';expect(tableDataMatches(layout.s,layout.p,layout.d)).toBe(false);
 if(kind==='effect'){const scope=make();scope.s.layoutHash=scope.d.definition!.candidates[0]!.layoutHash='00000000';for(const ordinal of[3,6,8])scope.s.columns[ordinal].interpretation=null;expect(tableDataMatches(scope.s,scope.p,scope.d)).toBe(false);}
 const unfetched=make();delete unfetched.s.fetchedDefinition;delete unfetched.s.fetchedBecause;delete unfetched.s.bundledFallback;delete unfetched.d.definition!.fetched;unfetched.s.providerRevision=unfetched.d.provider.revision;expect(tableDataMatches(unfetched.s,unfetched.p,unfetched.d)).toBe(false);
});
it('preserves all existing Subclass v2/v3 and Creature v3 full/projection spellings',()=>{for(const kind of['subclass','creature']as const)for(const version of kind==='subclass'?[2,3]:[3])for(const projected of[false,true]){const{s,d,p}=authored(kind);if(projected){s.source=structuredClone(partialTemplate.source) as TableDataSnapshot['source'];s.coverage=structuredClone(partialTemplate.coverage) as TableDataSnapshot['coverage'];s.rowCount=partialTemplate.rowCount;s.association=structuredClone(partialTemplate.association) as TableDataSnapshot['association'];s.association.fileDataId=kind==='subclass'?'1261604':'1108759';}s.decoderRevision=`wdc5-values-v7+table-admission-${projected?'projection-':''}v${version}`;expect(tableDataMatches(s,p,d),`${kind}:${version}:${projected}`).toBe(true);}});
