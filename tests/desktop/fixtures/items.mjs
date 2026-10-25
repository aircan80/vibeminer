// Authored projection and rows only. No downloaded game bytes or parser shortcut.
import seed from './items-snapshot.json' with { type:'json' };
export function itemsSnapshot(context=seed.context){const s=structuredClone(seed);s.context={...context};for(const source of[s.itemSource,s.sparseSource])Object.assign(source,context);return s;}
export function itemRows(snapshot,id,withIcons=false){
 const n=Number(id);if(!Number.isInteger(n)||n<0||n>=60)return null;
 const row=(source)=>({id,isCopy:n===10,copySourceId:n===10?'0':null,values:source.columns.map((c,i)=>c.interpretation?'3011920367':c.isId?id:c.name==='IconFileDataID'?(withIcons&&n%9!==3?String(200+n):'0'):c.name==='Display_lang'?`Authored sword ${id}`:c.name==='Description_lang'?`Authored source description ${id}`:c.family==='float'?'1.25':String(i%11))});
 const itemRow=row(snapshot.itemSource),sparseRow=n<58?row(snapshot.sparseSource):null;
 if(sparseRow){const display=snapshot.sparseSource.columns.findIndex(c=>c.name==='Display_lang'),description=snapshot.sparseSource.columns.findIndex(c=>c.name==='Description_lang');if(n===7)sparseRow.values[display]='';if(n===8){sparseRow.values[display]='';sparseRow.textIssues=[{columnIndex:display,code:'DB2_TEXT_ENCODING_INVALID',rawBytesHex:'c328'}];}if(n===9){sparseRow.values[description]='';sparseRow.textIssues=[{columnIndex:description,code:'DB2_TEXT_ENCODING_INVALID',rawBytesHex:'ff'}];}}
 return {itemRow,sparseRow};
}
export function itemSummary(snapshot,id,withIcons=false){const rows=itemRows(snapshot,id,withIcons);if(!rows)return null;const {itemRow,sparseRow}=rows,index=snapshot.sparseSource.columns.findIndex(c=>c.name==='Display_lang'),value=sparseRow?.values[index],invalid=sparseRow?.textIssues?.some(i=>i.columnIndex===index);const name=!sparseRow?{state:'missingSparse',value:null}:invalid?{state:'invalid',value:null}:value.length?{state:'valid',value}:{state:'empty',value:''};const baseValues=[];for(const [source,row,names]of[[snapshot.itemSource,itemRow,['ClassID','SubclassID','InventoryType']],[snapshot.sparseSource,sparseRow,['OverallQualityID','ItemLevel','InventoryType']]])if(row)for(const name of names){const columnIndex=source.columns.findIndex(c=>c.name===name);if(columnIndex>=0)baseValues.push({tableId:source.tableId,columnIndex,value:row.values[columnIndex]});}return{id,name,baseValues,iconFileDataId:withIcons&&Number(id)%9!==3?String(200+Number(id)):'0'};}
export function itemDetail(snapshot,id,withIcons=false){const rows=itemRows(snapshot,id,withIcons);return rows?{summary:itemSummary(snapshot,id,withIcons),...rows}:null;}
export function itemsPage(snapshot,params,withIcons=false){const all=Array.from({length:60},(_,i)=>itemSummary(snapshot,String(i),withIcons)).filter(i=>params.exactItemId===undefined||i.id===params.exactItemId).filter(i=>!params.query||i.name.state==='valid'&&i.name.value.toUpperCase().includes(params.query.toUpperCase()));const after=params.after?Number(params.after.slice(1)):0,entries=all.slice(after,after+(params.limit??50));return{context:snapshot.context,artifactId:snapshot.artifactId,query:params.query??'',exactItemId:params.exactItemId??null,entries,nextCursor:after+entries.length<all.length?`p${after+entries.length}`:null};}
export function itemsDefinition(snapshot,tableId,buildVersion){
 const source=tableId==='50238ec2'?snapshot.itemSource:snapshot.sparseSource,fields=[];
 for(const column of source.columns){const match=/^(.*)\[(\d+)\]$/.exec(column.name),name=match?match[1]:column.name;if(match&&match[2]!=='0')continue;const arrayLength=match?source.columns.filter(c=>c.name.startsWith(`${name}[`)).length:1;fields.push({name,family:column.family==='integer'?'int':column.family,integerBits:column.family==='integer'?column.interpretation?.candidateBits??32:null,unsigned:column.family==='integer'?column.interpretation?.candidateUnsigned??false:null,arrayLength,isId:column.isId,nonInline:false,relationStorage:false,verifiedName:!column.interpretation,comment:null,reference:column.reference});}
 return {productId:source.productId,buildIdentity:source.buildIdentity,sourceRegion:source.sourceRegion,buildVersion,provider:{name:'WoWDBDefs contributors',revision:source.providerRevision,manifestSha256:'f7b752a991d6a0321d07ecb7f06e62cd94694c1594d2fe5ce0cb7b243c71559a',license:'CC-BY-SA-4.0',sourceUrl:`https://github.com/wowdev/WoWDBDefs/blob/${source.providerRevision}/manifest.json`},entry:{tableId,name:tableId==='50238ec2'?'Item':'ItemSparse',db2FileDataId:source.association.fileDataId,definitionState:'bundled',sourceState:'notChecked',rowsState:'notPrepared'},definition:{sha256:source.definitionSha256,sourceUrl:'https://fixture.invalid/definition',columns:fields,candidates:[{layoutHash:source.layoutHash,fields}]},problem:null};
}
// item-facts-v1 for the authored rows: the same key table, state and label rules as the C# ItemFacts projector.
const names=(...list)=>list.map((label,value)=>({value:String(value),label}));
export const itemLabelLists={labelRevision:'item-labels-v1',
 quality:names('Poor','Common','Uncommon','Rare','Epic','Legendary','Artifact','Heirloom','WoW Token'),
 inventoryType:names('Not equippable','Head','Neck','Shoulder','Shirt','Chest','Waist','Legs','Feet','Wrist','Hands','Finger','Trinket','One-hand','Shield','Ranged','Back','Two-hand','Bag','Tabard','Robe','Main hand','Off hand','Held in off-hand','Ammo','Thrown','Ranged right','Quiver','Relic','Profession tool','Profession gear'),
 bonding:names('No binding','Binds when picked up','Binds when equipped','Binds when used','Quest item'),
 trigger:names('On use','On equip','Chance on hit')};
export const itemFactKeys=[['description',['Description_lang']],['display1',['Display1_lang']],['display2',['Display2_lang']],['display3',['Display3_lang']],['icon',['IconFileDataID']],
 ['quality',['OverallQualityID']],['itemLevel',['ItemLevel']],['itemSquishEra',['ItemSquishEraID']],['contentTuning',['ContentTuningID']],['playerLevelCurve',['PlayerLevelToItemLevelCurveID']],['itemLevelOffsetCurve',['ItemLevelOffsetCurveID']],['itemLevelOffset',['ItemLevelOffsetItemLevel']],
 ['class',['ClassID']],['subclass',['SubclassID']],['inventoryType',['InventoryType']],
 ['requiredLevel',['RequiredLevel']],['requiredSkill',['RequiredSkill']],['requiredSkillRank',['RequiredSkillRank']],['requiredAbility',['RequiredAbility']],['minFaction',['MinFactionID']],['minReputation',['MinReputation']],['allowableClass',['AllowableClass']],['allowableRaces',['AllowableRaces','AllowableRace']],
 ['bonding',['Bonding']],['stackable',['Stackable']],['maxCount',['MaxCount']],['containerSlots',['ContainerSlots']],['buyPrice',['BuyPrice']],['sellPrice',['SellPrice']],['vendorStackCount',['VendorStackCount']],['durationInInventory',['DurationInInventory']],
 ['statType',['StatModifier_bonusStat']],['statAmount',['StatModifier_bonusAmount']],['statPercentEditor',['StatPercentEditor']],['statSocketShare',['StatPercentageOfSocket']],['socketType',['SocketType']],['socketBonus',['Socket_match_enchantment_ID','Socket_match_enchantment_id']],['gemProperties',['Gem_properties','GemProperties']],
 ['damageType',['DamageType']],['itemDelay',['ItemDelay']],['dmgVariance',['DmgVariance']],['itemRange',['ItemRange']],['flags',['Flags']],['zoneBound',['ZoneBound']],['instanceBound',['InstanceBound']],['requiredHoliday',['RequiredHoliday']],['priceVariance',['PriceVariance']],['priceRandomValue',['PriceRandomValue']]];
const labelMaps={quality:itemLabelLists.quality,inventoryType:itemLabelLists.inventoryType,bonding:itemLabelLists.bonding};
export function itemFacts(snapshot,params,withIcons=false){
 const rows=itemRows(snapshot,params.itemId,withIcons),complete=snapshot.sparseSource.coverage.scope==='complete';
 const base={context:snapshot.context,artifactId:snapshot.artifactId,itemId:params.itemId,policyRevision:'item-facts-v1',labelRevision:'item-labels-v1'};
 // Outside the authored Item scope there is no ItemSparse row either: the lookup finds none.
 if(!rows)return {...base,sparse:complete?'missing':'unknown',facts:null};
 const {itemRow,sparseRow}=rows,sparse=sparseRow?'present':complete?'missing':'unknown';
 const columns=(source,fields)=>source.columns.flatMap((c,i)=>fields.includes(c.name.split('[')[0])?[i]:[]);
 const cells=(source,row,table,fields,key)=>columns(source,fields).map(i=>{const invalid=!!row.textIssues?.some(t=>t.columnIndex===i),value=invalid?'':row.values[i];return {tableId:source.tableId,table,column:source.columns[i].name,columnIndex:i,recordId:row.id,value,label:invalid?null:labelMaps[key]?.find(l=>l.value===value)?.label??null};});
 const facts=itemFactKeys.map(([key,fields])=>{
  const item=cells(snapshot.itemSource,itemRow,'Item',fields,key),sparseColumns=columns(snapshot.sparseSource,fields),sparseCells=sparseRow?cells(snapshot.sparseSource,sparseRow,'ItemSparse',fields,key):[];
  const invalid=[[itemRow,item],[sparseRow,sparseCells]].some(([row,list])=>row?.textIssues?.some(t=>list.some(c=>c.columnIndex===t.columnIndex)));
  const state=!item.length&&!sparseColumns.length?'unsupported':!item.length&&!sparseRow?sparse:invalid?'invalidText':'value';
  const agreement=!item.length||!sparseCells.length?'single':item.length!==sparseCells.length?'separate':item.every((c,i)=>c.value===sparseCells[i].value)?'agree':'disagree';
  return {key,state,agreement,values:state==='missing'||state==='unknown'?[]:[...item,...sparseCells]};});
 return {...base,sparse,facts};
}
