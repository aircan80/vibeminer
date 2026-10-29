import {useEffect,useRef,useState} from 'react';
import type {TableDataSnapshot,TableDefinitionProblem} from '../../../../contracts/generated/discovery';
import {fetchedCompactLabel,fetchedLabel} from '../shared/tables';
import {useShell} from './HomeShell';

/**
 * D101: a change of Fetch newer table definitions in Settings reaches an open feature view as it reaches Tables. The view
 * checks again at once while it shows, or when it shows next; never while its own load runs, and never before its first
 * check, which reads the current setting anyway.
 */
export function useDefinitionSettingRecheck(active:boolean,started:boolean,busy:boolean,recheck:()=>void){
 const pending=useRef(false),run=useRef(recheck),[tick,setTick]=useState(0);run.current=recheck;
 useEffect(()=>{const changed=(event:Event)=>{if((event as CustomEvent<{name?:string}>).detail?.name!=='fetchTableDefinitions')return;pending.current=true;setTick(t=>t+1);};
  window.addEventListener('vibeminer-settings-changed',changed);return()=>window.removeEventListener('vibeminer-settings-changed',changed);},[]);
 useEffect(()=>{if(!pending.current||!active||busy)return;pending.current=false;if(started)run.current();},[tick,active,busy,started]);
}

// D101: the engine sends which table no definition could read, and how the Settings switch read, as fields on the
// failure. Views decide from those fields only, never from the message.
type Problem={definition?:TableDefinitionProblem};
/** The next step is the Settings switch: fetching was off, as Tables' settingsStep says with definitionFetch. */
export const settingsStep=(p:Problem)=>p.definition?.definitionFetch==='off';
/** No definition can read that table, so Tables can't open it either. */
export const failsTable=(p:Problem,tableId:string)=>p.definition?.tableId===tableId;

// The tables features read, by name, so a notice can say which one used which definition.
const TABLE_NAMES:Record<string,string>={'50238ec2':'Item','919be54e':'ItemSparse','c9d6b6b3':'Creature','b977271e':'ItemClass','95d8638f':'ItemSubClass',
 '00cb674f':'ItemXItemEffect','4002a5b1':'ItemEffect','46c66698':'SpellName','bfdaf9f1':'CreatureDisplayInfo','7f695fa5':'CreatureModelData'};
export const tableName=(s:TableDataSnapshot)=>TABLE_NAMES[s.tableId]??s.tableId;
const list=(names:string[])=>names.length<2?names.join(''):`${names.slice(0,-1).join(', ')} and ${names.at(-1)}`;

/** Why tables were read with a fetched definition, one sentence per rule, in the words Tables uses (D081). */
export function fetchedWhy(sources:TableDataSnapshot[],build:string):string[]{
 return (['exactBuild','layoutNotBundled','layoutMatch'] as const).map(rule=>sources.filter(s=>(s.fetchedBecause??'exactBuild')===rule)).filter(group=>group.length).map(group=>{
  const names=list(group.map(tableName)),many=group.length>1,rule=group[0].fetchedBecause??'exactBuild',layouts=many?'their layouts':`its layout ${group[0].layoutHash}`;
  return rule==='layoutNotBundled'?`The bundled ${names} definition${many?'s list':' lists'} build ${build} but ${many?'have':'has'} no block for ${layouts}; the newest WoWDBDefs definition${many?'s have':' has'} one.`
   :rule==='layoutMatch'?`Neither the bundled nor the newest WoWDBDefs ${names} definition${many?'s list':' lists'} build ${build}; only the WoWDBDefs block${many?'s match':' matches'} ${layouts}.`
   :`The bundled ${names} definition${many?'s don’t':' doesn’t'} list build ${build}; the newest WoWDBDefs definition${many?'s do':' does'}.`;});
}

/** One line for a feature's own state (effects, labels): which of its tables used a fetched definition, or null. */
export function fetchedNote(sources:(TableDataSnapshot|null|undefined)[]):string|null{
 // One table named once: on Classic the ParentItemID link source and the effect source are the same ItemEffect (#86).
 const names=[...new Set(sources.filter((s):s is TableDataSnapshot=>!!s?.fetchedDefinition).map(tableName))];
 return names.length?`${list(names)} read with ${names.length>1?'definitions':'a definition'} fetched from WoWDBDefs. Names and meanings are community data, not verified.`:null;}

/**
 * D101: a feature view says, as Tables does, when its tables were read with a definition fetched from WoWDBDefs or
 * matched by layout because no definition lists the build. Nothing shows when every table used a bundled definition
 * that lists the build.
 */
export function FeatureDefinitionNotice({id,sources,build}:{id:string;sources:TableDataSnapshot[];build:string}){
 const shell=useShell(),fetched=sources.filter(s=>s.fetchedDefinition),layout=sources.filter(s=>s.schemaMatch==='layoutOnly'&&!s.fetchedDefinition);
 if(!fetched.length&&!layout.length)return null;
 const fallback=fetched.length>0&&fetched.every(s=>s.bundledFallback);
 // One fetched commit names them all, or each table names its own. Tables fetched seconds apart from the same commit
 // (Item and ItemSparse are) are one source; the line gives the earliest fetch.
 const first=fetched.map(s=>s.fetchedDefinition!).sort((a,b)=>Date.parse(a.fetchedAt)-Date.parse(b.fetchedAt))[0],oneCommit=fetched.every(s=>s.fetchedDefinition!.revision===first?.revision);
 const source=!first?null:oneCommit?`${list(fetched.map(tableName))} · ${fetchedLabel(first)}`:fetched.map(s=>`${tableName(s)} · ${fetchedLabel(s.fetchedDefinition!)}`).join('; ');
 const layoutHeading=layout.length?`${list(layout.map(tableName))} · Matched by layout, build not listed`:null;
 const full=[...fetchedWhy(fetched,build),layout.length?`The bundled ${list(layout.map(tableName))} definition${layout.length>1?'s don’t':' doesn’t'} list build ${build}, so the block for the file’s layout was used.`:'',
  fetched.length?'Checked field by field.':'',layout.length?'Names and meanings aren’t confirmed for this build.':'Names and meanings are community data, not verified.',fetched.length?'CC BY-SA 4.0.':'',
  fallback?`Turn off Fetch newer table definitions in Settings to read ${fetched.length>1?'them with the bundled definitions':'it with the bundled definition'} only.`:''].filter(Boolean).join(' ');
 // At 200% text, or wherever the shell is compact, one short line as Tables does: the source and the rule, with the
 // whole explanation kept as the line's title.
 const heading=shell.compact&&first&&oneCommit?[fetchedCompactLabel(first),layoutHeading].filter(Boolean).join(' · '):[source,layoutHeading].filter(Boolean).join(' · ');
 return <div className={`dh-notice dh-keep feature-definition${fetched.length?' fetched-definition':''}${layout.length?' layout-only-match':''}`} role="region" aria-labelledby={`${id}-definition`}>
  <div className="dh-text"><strong id={`${id}-definition`}>{heading}</strong><span className="dh-detail">
   {shell.compact?<span className="feature-definition-short" title={full}>{fetchedShort(fetched,sources.length,build)||'Names and meanings aren’t confirmed for this build.'}</span>:full}
  </span></div></div>;
}

/** The rule in one short phrase per group of tables, as Tables' compact line says it. When one rule covers every table
 * the feature reads, the tables go unnamed, as on Tables; the full text stays in the title. */
function fetchedShort(sources:TableDataSnapshot[],all:number,build:string):string{
 const groups=(['exactBuild','layoutNotBundled','layoutMatch'] as const).map(rule=>sources.filter(s=>(s.fetchedBecause??'exactBuild')===rule)).filter(group=>group.length);
 const named=!(groups.length===1&&groups[0]!.length===all);
 return groups.map(group=>{
  const many=group.length>1,rule=group[0]!.fetchedBecause??'exactBuild',layout=many?'their layouts':`layout ${group[0]!.layoutHash}`;
  // Statements, never instructions: they say what each definition lists.
  const phrase=rule==='layoutNotBundled'?`only WoWDBDefs has a block for ${layout}.`
   :rule==='layoutMatch'?`no definition lists build ${build}; only WoWDBDefs matches ${layout}.`
   :`only WoWDBDefs lists build ${build}.`;
  const sentence=phrase[0]!.toUpperCase()+phrase.slice(1);
  return named?`${list(group.map(tableName))}: ${phrase}`:sentence;}).join(' ');
}
