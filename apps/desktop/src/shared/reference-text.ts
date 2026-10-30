import type { TableDataPage, TableDataSnapshot, TableReferenceTextPage, TableReferenceTextColumn, TableReferenceTextValue } from '../../../../contracts/generated/discovery';
import type { TableRowsParams } from './table-data';
// Reviewed allowlist (docs/WOW-DATA-REFERENCE.md#reference-text-columns). The engine owns the lookup; this only checks
// that a reply names the reviewed text source for each declared target and never invents one.
export const referenceTextRules: Readonly<Record<string,{table:string;tableId:string;textColumn:string;matchColumn:string}>> = Object.freeze({
 'ItemClass.ClassID':{table:'ItemClass',tableId:'b977271e',textColumn:'ClassName_lang',matchColumn:'ClassID'},
 'Creature.ID':{table:'Creature',tableId:'c9d6b6b3',textColumn:'Name_lang',matchColumn:'ID'},
 'CreatureType.ID':{table:'CreatureType',tableId:'1f80ad3f',textColumn:'Name_lang',matchColumn:'ID'},
 'CreatureFamily.ID':{table:'CreatureFamily',tableId:'369f469f',textColumn:'Name_lang',matchColumn:'ID'},
 'Spell.ID':{table:'SpellName',tableId:'46c66698',textColumn:'Name_lang',matchColumn:'ID'},
 'Item.ID':{table:'ItemSparse',tableId:'919be54e',textColumn:'Display_lang',matchColumn:'ID'},
});
/** Owner decision (#17): text tables at most 1 MiB encoded load automatically; larger ones wait for Load text. */
export const AUTOMATIC_TEXT_LOAD_MAX_BYTES=1048576n;
const utf8=new TextEncoder(), decimal=/^(0|[1-9][0-9]{0,18})$/;
const ruleFor=(c:{reference:{table:string;column:string}})=>Object.hasOwn(referenceTextRules,`${c.reference.table}.${c.reference.column}`)?referenceTextRules[`${c.reference.table}.${c.reference.column}`]:undefined;
function validColumn(c:TableReferenceTextColumn,snapshot?:TableDataSnapshot):boolean{
 const declared=snapshot?.columns[c.columnIndex];
 if(snapshot&&(!declared||declared.name!==c.name||declared.reference?.table!==c.reference.table||declared.reference?.column!==c.reference.column))return false;
 const rule=ruleFor(c),s=c.textSource;
 if(c.state==='noTextRule'||c.state==='noDefinition'&&!rule)return !rule&&s===null&&c.problem===null;
 if(!rule||!s||s.table!==rule.table||s.tableId!==rule.tableId||s.textColumn!==rule.textColumn||s.matchColumn!==rule.matchColumn)return false;
 if(!decimal.test(s.measuredEncodedBytes)||s.loadPolicy!==(BigInt(s.measuredEncodedBytes)<=AUTOMATIC_TEXT_LOAD_MAX_BYTES?'automatic':'onRequest'))return false;
 if(c.state==='ready')return !!s.artifactId&&!!s.scope&&c.problem===null;
 if(c.state==='notPrepared')return s.artifactId===null&&s.scope===null&&c.problem===null;
 return s.artifactId===null&&s.scope===null&&c.problem!==null;
}
function validValue(v:TableReferenceTextValue):boolean{
 const one=v.matchCount==='1',row=v.row;
 switch(v.state){
  case 'resolved':return one&&!!row&&typeof v.text==='string'&&v.text.length>0;
  case 'empty':return one&&!!row&&v.text==='';
  case 'unreadable':case 'textTooLong':return one&&!!row&&v.text===null;
  case 'notFound':case 'keyOutOfRange':return v.matchCount==='0'&&!row&&v.text===null;
  case 'multiple':return decimal.test(v.matchCount)&&BigInt(v.matchCount)>1n&&!row&&v.text===null;
  default:return false;
 }
}
// A reply belongs to one exact page. Every ready column has exactly one value per distinct cell on that page; nothing else has values.
export function referenceTextMatches(value:TableReferenceTextPage,p:TableRowsParams,page?:TableDataPage,snapshot?:TableDataSnapshot):boolean{
 if(value.productId!==p.productId||value.buildIdentity!==p.buildIdentity||value.sourceRegion!==p.sourceRegion||value.tableId!==p.tableId||value.researchLocale!==p.researchLocale||value.artifactId!==p.artifactId||value.after!==(p.after??'0')||value.recordId!==(p.recordId??null)||value.policyRevision!=='table-reference-text-v1')return false;
 if(value.rowIds.length>(p.limit??50)||!value.rowIds.every(id=>decimal.test(id))||p.recordId!==undefined&&value.rowIds.some(id=>id!==p.recordId))return false;
 if(utf8.encode(JSON.stringify(value)).byteLength>512*1024)return false;
 if(page&&(page.artifactId!==value.artifactId||page.rows.length!==value.rowIds.length||page.rows.some((row,i)=>row.id!==value.rowIds[i])))return false;
 const indices=value.columns.map(c=>c.columnIndex);
 if(indices.some((x,i)=>i>0&&x<=indices[i-1])||!value.columns.every(c=>validColumn(c,snapshot)))return false;
 if(snapshot&&snapshot.columns.some((c,i)=>!!c.reference!==indices.includes(i)))return false;
 const ready=new Set(value.columns.filter(c=>c.state==='ready').map(c=>c.columnIndex)),seen=new Set<string>();
 for(const v of value.values){const key=`${v.columnIndex}:${v.value}`;if(!ready.has(v.columnIndex)||seen.has(key)||!validValue(v))return false;seen.add(key);}
 if(page)for(const i of ready){const cells=new Set(page.rows.map(row=>row.values[i]));if(cells.size!==value.values.filter(v=>v.columnIndex===i).length||[...cells].some(cell=>!seen.has(`${i}:${cell}`)))return false;}
 return true;
}
