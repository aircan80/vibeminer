import {ActivityLink} from './DownloadActivity';
import {useCallback,useEffect,useLayoutEffect,useRef,useState,type ReactNode} from 'react';
import {createPortal} from 'react-dom';
import type {WorkspaceSnapshot,CreaturesSnapshot,CreaturesPage,CreatureSummary,CreatureDetail,TableDataSnapshot,TableDataPage,CreatureDisplayMetadataResult,CreatureDisplayFactsResult,CreatureDisplayRecordFacts,CreatureDisplayFact,CreatureDisplayFactCell} from '../../../../contracts/generated/discovery';
import type {DiscoveryBridge} from '../shared/bridge';
import {creatureKey,validCreatureQuery,creaturesSnapshotMatches,creaturesReadinessMatches,creaturesPageMatches,creatureInspectMatches,type CreaturePageParams} from '../shared/creatures';
import {displayKey,creatureDisplayMatches,creatureDisplayFactsMatches,creatureLabelListsMatches,type CreatureDisplayParams} from '../shared/creature-display';
import {validRecordId} from '../shared/table-data';
import type {TableRowsParams} from '../shared/table-data';
import {exactCount} from '../shared/inventory';
import {creaturesContext,type CreaturesLoading} from './useCreaturesLoading';
import {emptyCreatureView,type CreatureView} from './creature-view';
import type {CreatureDisplays,DisplayIntent} from './useCreatureDisplay';
import type {GeometryFlow} from './useCreatureGeometry';
import {CreatureGeometryView} from './CreatureGeometryView';
import {geometryBusy} from './geometry-session';
import {geometryAnchorFromDisplay,geometryIntentKey,type GeometryReturnOrigin} from './geometry-state';
import {geometrySelectionKey} from '../shared/creature-geometry';
import {measuredStage,type TableFailure,type TableLoading} from './useTableLoading';
import {useReferenceText} from './useReferenceText';
import {FeatureDefinitionNotice,useDefinitionSettingRecheck,failsTable,settingsStep} from './FeatureDefinitionNotice';
import {OpenSettingsButton} from './Settings';
import {useCoveredSide,useShell} from './HomeShell';
import './items.css';
import './creatures.css';

// Creatures view (docs/design/creatures-view, #62): the admitted Creature records and the selected creature's record, its four
// display slots, each slot's CreatureDisplayInfo and CreatureModelData rows and model file, with a small model preview that opens
// the geometry view (#59). It reuses the Items view's panes, fact and source-link styles (iv-*); the engine picks every cell.
const CREATURE_TABLE='c9d6b6b3';
// A name of only spaces or line breaks would read as nothing, so it is named in words; the exact text stays in the Name fact.
export const creatureName=(s:CreatureSummary)=>s.name.state==='valid'?(s.name.value!.trim()?s.name.value!:'Whitespace-only source name'):s.name.state==='empty'?'Empty source name':'Name unavailable · invalid source text';
const ID_ERROR='Enter a whole Creature ID from 0 to 4294967295, without signs or leading zeros. The last results stay.';
const declaredCount=(s:CreatureSummary)=>s.displaySlots.filter(slot=>slot.state==='declared').length;
const displaysWord=(n:number)=>n===0?'No display declared':n===1?'1 display':`${n} displays`;
/** Opens a table record in Tables with one field selected (same build, source and locale). */
type Open=(source:TableDataSnapshot,recordId:string,columnIndex?:number)=>void;
export type CreatureFileTarget={fileDataId:string;table:string;recordId:string};

/* ---------- Source links and roving groups (the Items view's rules) ---------- */
function SourceLink({source,recordId,columnIndex,label,full,open}:{source:TableDataSnapshot;recordId:string;columnIndex?:number;label:string;full?:string;open?:Open}){
 const table=tableName(source);
 if(!open)return <span className="iv-nosrc">{label}</span>;
 return <button type="button" className="iv-src" aria-describedby="cv-rove-hint" data-iv-key={`cv|${source.tableId}|${recordId}|${columnIndex??''}|${label}`} onClick={e=>{markCurrent(e.currentTarget);open(source,recordId,columnIndex);}}>{label}<span className="iv-visually-hidden">{full&&full!==label?` (${full})`:''}, open {table} record {recordId} in Tables</span></button>;
}
const tableName=(s:TableDataSnapshot)=>s.tableId===CREATURE_TABLE?'Creature':s.tableId==='bfdaf9f1'?'CreatureDisplayInfo':s.tableId==='7f695fa5'?'CreatureModelData':s.tableId==='1f80ad3f'?'CreatureType':s.tableId==='369f469f'?'CreatureFamily':s.tableId;
const reachable=(g:Element)=>[...g.querySelectorAll<HTMLElement>('.iv-src')].filter(l=>l.closest('[data-rove]')===g&&!l.closest('details:not([open]),[hidden]'));
function markCurrent(link:HTMLElement){const group=link.closest('[data-rove]');for(const l of group?.querySelectorAll<HTMLElement>('.iv-src')??[]){if(l!==link){l.tabIndex=-1;delete l.dataset.cur;}}link.dataset.cur='1';link.tabIndex=0;}
function rove(root:HTMLElement|null){
 if(!root)return;
 for(const g of root.querySelectorAll('[data-rove]')){const all=[...g.querySelectorAll<HTMLElement>('.iv-src')].filter(l=>l.closest('[data-rove]')===g);for(const l of all)l.tabIndex=-1;const vis=reachable(g);(vis.find(l=>l.dataset.cur)??vis[0])?.setAttribute('tabindex','0');}
}
function onRoveKey(e:React.KeyboardEvent<HTMLElement>){
 const link=(e.target as HTMLElement).closest<HTMLElement>('.iv-src'),group=link?.closest('[data-rove]');
 if(!link||!group||!['ArrowRight','ArrowDown','ArrowLeft','ArrowUp','Home','End'].includes(e.key))return;
 const vis=reachable(group),i=vis.indexOf(link),j={ArrowRight:i+1,ArrowDown:i+1,ArrowLeft:i-1,ArrowUp:i-1,Home:0,End:vis.length-1}[e.key]!,next=vis[Math.max(0,Math.min(vis.length-1,j))];
 if(!next||next===link)return;e.preventDefault();for(const l of group.querySelectorAll<HTMLElement>('.iv-src')){l.tabIndex=-1;delete l.dataset.cur;}next.tabIndex=0;next.dataset.cur='1';next.focus();
}
function Fact({label,value,sources,note}:{label:string;value:ReactNode;sources?:ReactNode;note?:ReactNode}){
 return <div className="iv-fact"><div className="iv-fhead"><span className="iv-fl">{label}</span>{sources&&<span className="iv-srcs">{sources}</span>}</div><span className="iv-fv iv-selectable">{value}</span>{note&&<span className="iv-fnote">{note}</span>}</div>;
}
const sub=(text:ReactNode)=><span className="iv-fsub">· {text}</span>;
const nv=(word:string,why:string)=><><span className="iv-nv">{word}</span> <span className="iv-fsub">· {why}</span></>;

/* ---------- Browser ---------- */
type SlotRead={key:string;state:'reading'|'ready'|'failed';result:CreatureDisplayFactsResult|null;error:TableFailure|null};
export function CreaturesBrowser({workspace,bridge,active,flow,focusRequest=0,onSource,onOpenTable,onFile,displays,displayAvailable=false,factsAvailable=false,geometry,tableLoading}:{workspace:WorkspaceSnapshot;bridge:DiscoveryBridge;active:boolean;flow:CreaturesLoading;focusRequest?:number;onSource?:(source:TableDataSnapshot,recordId:string,columnIndex?:number)=>void;onOpenTable?:(tableId:string,name:string)=>void;onFile?:(target:CreatureFileTarget)=>void;displays?:CreatureDisplays;displayAvailable?:boolean;factsAvailable?:boolean;geometry?:GeometryFlow;tableLoading?:TableLoading}){
 const context=creaturesContext(workspace),key=creatureKey(context),intent={context,buildVersion:workspace.build.version};
 const {compact,statusSlot}=useShell(),covered=useCoveredSide();
 // Another build's view remounts this browser; the last eight contexts keep their search, page and selection (useCreaturesLoading).
 const [cached]=useState<CreatureView>(()=>flow.cacheGet(context)??emptyCreatureView());
 const [snapshot,setSnapshot]=useState<CreaturesSnapshot|null>(cached.snapshot),[page,setPage]=useState<CreaturesPage|null>(cached.page),[mode,setMode]=useState<'name'|'id'>(cached.mode),[draft,setDraft]=useState(cached.draft),[inputError,setInputError]=useState(''),[error,setError]=useState<TableFailure|null>(null),[checked,setChecked]=useState(false),[checking,setChecking]=useState(false),[needs,setNeeds]=useState(false),[busy,setBusy]=useState(false),[history,setHistory]=useState<(string|undefined)[]>(cached.history),[cursor,setCursor]=useState<string|undefined>(cached.cursor),[selected,setSelected]=useState<CreatureSummary|null>(cached.selected),[detail,setDetail]=useState<CreatureDetail|null>(cached.detail),[detailBusy,setDetailBusy]=useState(false),[detailError,setDetailError]=useState<TableFailure|null>(null),[notice,setNotice]=useState(cached.notice),[pane,setPane]=useState<'list'|'detail'>(cached.pane),[classification,setClassification]=useState<Map<string,string>|null>(null);
 const alive=useRef(true),activated=useRef(false),readinessToken=useRef(0),pageToken=useRef(0),detailToken=useRef(0),observed=useRef<string|null>(null),seen=useRef<string|null>(null),current=useRef(snapshot),currentPage=useRef(page),last=useRef<{params:CreaturePageParams;movement:'first'|'next'|'previous'|'stay'}|null>(null),heading=useRef<HTMLHeadingElement>(null),detailHeading=useRef<HTMLHeadingElement>(null),input=useRef<HTMLInputElement>(null),listbox=useRef<HTMLDivElement>(null),listScroll=useRef<HTMLDivElement>(null),detailRoot=useRef<HTMLElement>(null),wasActive=useRef(false),detailFocus=useRef(false),prepRetryFocus=useRef<HTMLElement|null>(null),prepStatus=useRef<HTMLDivElement|null>(null),listTop=useRef(cached.listTop);
 current.current=snapshot;currentPage.current=page;
 useEffect(()=>{flow.cachePut(context,{snapshot,page,mode,draft,history,cursor,selected,detail,notice,listTop:listTop.current,pane});},[snapshot,page,mode,draft,history,cursor,selected,detail,notice,pane]);
 const op=flow.operation,own=!!op&&creatureKey(op.intent.context)===key,blocked=flow.blocked(context),waiting=active&&checked&&needs&&!checking&&!error&&!blocked&&(flow.pending&&!own||flow.capacityBlocked);
 useEffect(()=>{alive.current=true;return()=>{alive.current=false;readinessToken.current++;pageToken.current++;detailToken.current++;};},[]);
 useEffect(()=>{if(cached.detail===null&&cached.selected)void inspect(cached.selected,true);},[]);
 useEffect(()=>{if(!factsAvailable||!bridge.creatureLabels)return;let live=true;void bridge.creatureLabels().then(reply=>{if(live&&alive.current&&reply.ok&&creatureLabelListsMatches(reply.value))setClassification(new Map(reply.value.classification.map(l=>[l.value,l.label])));}).catch(()=>{/* Labels stay off; numbers still show. */});return()=>{live=false;};},[bridge,factsAvailable]);
 useLayoutEffect(()=>{if(active&&(focusRequest>0||!wasActive.current))heading.current?.focus({preventScroll:true});wasActive.current=active;},[active,focusRequest]);
 useLayoutEffect(()=>{if(active&&detailFocus.current&&(!compact||pane==='detail')){detailFocus.current=false;detailHeading.current?.focus({preventScroll:true});}},[active,pane,selected]);
 useLayoutEffect(()=>{rove(detailRoot.current);});
 useLayoutEffect(()=>{const l=listScroll.current;if(active&&l&&Math.abs(l.scrollTop-listTop.current)>1)l.scrollTop=listTop.current;},[active]);
 // Retry replaces its own button: focus moves to the Preparing status, or to the search once the list is ready.
 useLayoutEffect(()=>{const origin=prepRetryFocus.current;if(!origin||!active||origin.isConnected&&document.activeElement===origin)return;const target=prepStatus.current??(snapshot&&!needs?input.current:null);if(!target)return;prepRetryFocus.current=null;const now=document.activeElement;if(!now||now===document.body||now===origin)target.focus({preventScroll:true});});
 useEffect(()=>{if(!active)return;const onKey=(event:KeyboardEvent)=>{if((event.ctrlKey||event.metaKey)&&!event.altKey&&event.key.toLowerCase()==='f'&&!input.current?.closest('[inert]')){event.preventDefault();showPane('search');}};window.addEventListener('keydown',onKey);return()=>window.removeEventListener('keydown',onKey);},[active]);
 useEffect(()=>{if(active&&!activated.current){activated.current=true;if(blocked){setChecked(true);setNeeds(true);}else void readiness();}},[active]);
 useEffect(()=>{if(!op||!own)return;if(['pending','stopping'].includes(op.status))observed.current=op.id;if(op.result&&observed.current===op.id&&seen.current!==op.id&&!blocked){seen.current=op.id;accept(op.result);}},[flow.operation]);
 useEffect(()=>{flow.setCoreWanted(active&&checked&&needs&&!checking&&!error&&!blocked&&!(own&&flow.pending));return()=>flow.setCoreWanted(false);},[active,checked,needs,checking,error,flow.pending,flow.latchRevision]);
 useEffect(()=>{if(active&&checked&&needs&&!checking&&!error&&!blocked&&!flow.pending&&!(own&&op?.result&&observed.current===op.id)){const id=flow.load(intent);if(id)observed.current=id;}},[active,checked,needs,checking,error,flow.pending,flow.operation?.id,flow.latchRevision,flow.capacityRevision]);
 // After a Settings change, records that can no longer be read the way they were give way to the reason (D101).
 async function readiness(settingChanged=false){const token=++readinessToken.current;setChecking(true);setError(null);try{const reply=await bridge.creaturesData!(context);if(!alive.current||token!==readinessToken.current)return;if(!reply.ok){if(settingChanged)setNeeds(true);setError(reply.error);flow.fail(context,reply.error);return;}if(!creaturesReadinessMatches(reply.value,context))throw new Error('Creature readiness mismatch');setChecked(true);if(reply.value.snapshot)accept(reply.value.snapshot);else setNeeds(true);}catch{if(alive.current&&token===readinessToken.current){const problem={code:'CREATURES_READINESS_UNAVAILABLE',message:'The local Creature records could not be confirmed. Previous results are kept.',retryable:true};setError(problem);flow.fail(context,problem);}}finally{if(alive.current&&token===readinessToken.current)setChecking(false);}}
 function accept(s:CreaturesSnapshot){if(!creaturesSnapshotMatches(s,context)){setError({code:'CREATURES_CONTEXT_MISMATCH',message:'The Creature records differ from their exact source admission. Previous results are kept.',retryable:false});return;}readinessToken.current++;setChecking(false);setChecked(true);setNeeds(false);setError(null);const old=current.current,replaced=!!old&&old.artifactId!==s.artifactId;setSnapshot(s);current.current=s;const previous=currentPage.current;if(replaced){setPage(null);currentPage.current=null;detailToken.current++;setSelected(null);setDetail(null);setDetailError(null);setHistory([]);setCursor(undefined);setNotice('The admitted Creature source changed. The previous page and selection were cleared; choose a creature again.');}if(!replaced&&previous?.artifactId===s.artifactId)return;void read(s,{...context,artifactId:s.artifactId,query:previous?.query??'',...(previous?.exactCreatureId?{exactCreatureId:previous.exactCreatureId}:{}),limit:50},'first');}
 async function read(s:CreaturesSnapshot,params:CreaturePageParams,movement:'first'|'next'|'previous'|'stay',retainError=false){
  const token=++pageToken.current;last.current={params,movement};setBusy(true);if(!retainError)setError(null);
  try{const reply=await bridge.creaturesPage!(params);if(!alive.current||token!==pageToken.current)return;if(!reply.ok){setError(reply.error);return;}if(!creaturesPageMatches(reply.value,params,s)){setError({code:'CREATURES_PAGE_CONTEXT_MISMATCH',message:'The page differs from its exact Creature source, search or order. Previous rows are kept.',retryable:false});return;}setPage(reply.value);setCursor(params.after);if(movement==='first')setHistory([]);if(movement==='next')setHistory(old=>[...old.slice(-127),cursor]);if(movement==='previous')setHistory(old=>old.slice(0,-1));setError(null);}
  catch{if(alive.current&&token===pageToken.current)setError({code:'ENGINE_UNAVAILABLE',message:'The Creature page could not be read. Previous rows stay.',retryable:true});}finally{if(alive.current&&token===pageToken.current)setBusy(false);}
 }
 function apply(clear=false){if(!snapshot||busy||needs)return;const value=clear?'':draft;
  if(mode==='id'&&value&&!validRecordId(value)){setInputError(ID_ERROR);return;}
  if(mode==='name'&&!validCreatureQuery(value)){setInputError('Use valid Unicode text, at most 128 characters / 512 UTF-8 bytes, without NUL. The last results stay.');return;}
  setInputError('');if(clear)setDraft('');void read(snapshot,{...context,artifactId:snapshot.artifactId,query:mode==='name'?value:'',...(mode==='id'&&value?{exactCreatureId:value}:{}),limit:50},'first');}
 function pageMove(movement:'first'|'next'|'previous'){if(!snapshot||!page||busy||needs)return;const after=movement==='next'?page.nextCursor:movement==='previous'?history.at(-1):undefined;if(movement==='next'&&!after||movement==='previous'&&!history.length)return;void read(snapshot,{...context,artifactId:snapshot.artifactId,query:page.query,...(page.exactCreatureId===null?{}:{exactCreatureId:page.exactCreatureId}),limit:50,...(after?{after}:{})},movement);}
 async function inspect(summary:CreatureSummary,force=false){const s=snapshot;if(!s)return;setSelected(summary);if(!force&&selected?.id===summary.id&&detail)return;setDetail(null);setDetailError(null);setDetailBusy(true);const token=++detailToken.current,params={...context,artifactId:s.artifactId,creatureId:summary.id};
  try{const reply=await bridge.creatureInspect!(params);if(!alive.current||token!==detailToken.current)return;if(!reply.ok){setDetailError(reply.error);return;}if(!creatureInspectMatches(reply.value,params,s)){setDetailError({code:'CREATURES_DETAIL_CONTEXT_MISMATCH',message:'The record differs from the exact selected creature or its source.',retryable:false});return;}if(!reply.value.detail){setDetailError({code:'NOT_FOUND_IN_ADMITTED_SCOPE',message:'This Creature ID is not in the admitted source. No absence elsewhere is claimed.',retryable:false});return;}setDetail(reply.value.detail);}
  catch{if(alive.current&&token===detailToken.current)setDetailError({code:'ENGINE_UNAVAILABLE',message:'The creature record could not be read.',retryable:true});}finally{if(alive.current&&token===detailToken.current)setDetailBusy(false);}}
 // Selection follows the list's keyboard focus; a click or Enter in the one-pane layout shows the creature and focuses its heading.
 function choose(summary:CreatureSummary,show:boolean){void inspect(summary);keepRow(summary.id);if(show&&compact){detailFocus.current=true;setPane('detail');}}
 function keepRow(id:string){requestAnimationFrame(()=>{const l=listScroll.current,o=document.getElementById(`cv-opt-${id}`);if(!l||!o)return;const head=l.querySelector<HTMLElement>('.iv-thead')?.offsetHeight??0,top=o.offsetTop-head,bottom=o.offsetTop+o.offsetHeight-l.clientHeight;if(l.scrollTop>top)l.scrollTop=top;else if(l.scrollTop<bottom)l.scrollTop=bottom;});}
 function listKey(e:React.KeyboardEvent){const entries=page?.entries??[];if(!entries.length)return;const i=entries.findIndex(x=>x.id===selected?.id);const t={ArrowDown:i+1,ArrowUp:i<0?0:i-1,Home:0,End:entries.length-1,PageDown:i+10,PageUp:i-10}[e.key];if(t!==undefined){e.preventDefault();choose(entries[Math.max(0,Math.min(entries.length-1,t))],false);}else if(e.key==='Enter'&&i>=0){e.preventDefault();choose(entries[i],true);}}
 const [paneFocus,setPaneFocus]=useState<{target:'list'|'detail'|'search';serial:number}|null>(null);
 function showPane(target:'list'|'detail'|'search'){setPane(target==='detail'?'detail':'list');setPaneFocus(old=>({target,serial:(old?.serial??0)+1}));}
 useLayoutEffect(()=>{if(!paneFocus)return;if(paneFocus.target==='search'){input.current?.focus();input.current?.select();}else if(paneFocus.target==='list'){listbox.current?.focus({preventScroll:true});if(selected)keepRow(selected.id);}else document.querySelector<HTMLElement>('.cv .iv-switch [data-pane="detail"]')?.focus();},[paneFocus]);
 function retryCore(origin?:HTMLElement){if(origin)prepRetryFocus.current=origin;observed.current=null;flow.retry(context);setError(null);setNeeds(false);setChecked(false);void readiness();}
 // D101: switching Fetch newer table definitions checks Creatures again, as Tables does; a load that stopped on it runs again.
 useDefinitionSettingRecheck(active,activated.current,flow.pending,()=>{observed.current=null;flow.retry(context);setError(null);void readiness(true);});
 // Rows already admitted stay readable while a refresh prepares, waits or stops; only search waits for the current admission.
 const prepState=!snapshot,refreshing=!!snapshot&&(needs||!!blocked);
 const problem=blocked?.error??error,creatureUnreadable=!!problem&&failsTable(problem,CREATURE_TABLE);
 const status=active&&statusSlot&&snapshot?createPortal(<span className="dh-secondary">Creatures · <strong>{exactCount(snapshot.counts.rows)}</strong> {snapshot.counts.rows==='1'?'row':'rows'} · {exactCount(snapshot.counts.validNames)} named · {exactCount(snapshot.counts.positiveDisplaySlots)} declared displays{snapshot.creatureSource.coverage.scope==='complete'?'':' · Creature Partial'}</span>,statusSlot):null;

 /* ----- Geometry: one inline preview at a time, and the full view (#59) when opened from it ----- */
 const geometryView=geometry?.snapshot.view,geometryHere=!!geometryView&&creatureKey(geometryView.intent.selection)===key&&geometryView.intent.selection.artifactId===snapshot?.artifactId,[expandedKey,setExpandedKey]=useState<string|null>(null),geometryOwned=useRef(false),expandOrigin=useRef<string|null>(null);
 geometryOwned.current=geometryHere;
 // The full view is the same session entered again on its own canvas: a canvas change retires a view, so Open and Return re-enter.
 const expanded=geometryHere&&expandedKey===geometrySelectionKey(geometryView!.intent.selection),fullGeometry=expanded;
 useEffect(()=>{if(!active&&geometryHere)void geometry!.session.leave('navigation');},[active]);
 useEffect(()=>()=>{if(geometryOwned.current)void geometry?.session.leave('navigation');},[]);
 // A preview belongs to the selected creature: choosing another one closes it (a started Prepare keeps running in Downloads & activity).
 useEffect(()=>{if(geometryHere&&!expanded&&selected&&geometryView!.intent.selection.creatureId!==selected.id)void geometry!.session.leave('navigation');},[selected?.id]);
 // Item icons live in another hidden area while this one is shown; that committed state releases the body fetch (preview or full view).
 useLayoutEffect(()=>{if(active&&geometryHere&&geometry!.snapshot.iconsSuspended)geometry!.session.iconsHiddenCommitted(geometryView!.generation);},[active,geometryHere,geometryView?.generation,geometry?.snapshot.iconsSuspended]);
 // After Return the preview button comes back once the kept geometry draws again; until then focus waits on the Displays heading.
 useLayoutEffect(()=>{const k=expandOrigin.current;if(expanded||!k)return;const open=document.querySelector<HTMLElement>(`.cv:not([hidden]) [data-geometry-open="${k}"]`);if(open){expandOrigin.current=null;open.focus({preventScroll:true});}else if(document.activeElement===document.body)document.querySelector<HTMLElement>('.cv:not([hidden]) #cv-h-displays')?.focus({preventScroll:true});});
 const geometryOrigin=(k:string,invoker:HTMLElement):GeometryReturnOrigin=>({key:k,invoker,windowScroll:[window.scrollX,window.scrollY],innerScroll:[]});
const previewSlot=geometryHere&&!expanded&&selected&&geometryView!.intent.selection.creatureId===selected.id?geometryView!.intent.selection.slot:null;
 const preview={
  slot:previewSlot,view:previewSlot!==null?geometryView!:null,flow:geometry,
  show:(params:CreatureDisplayParams,metadata:CreatureDisplayMetadataResult,name:string,invoker:HTMLElement|null,localOnly=false)=>{if(!geometry)return;let anchor;try{anchor=geometryAnchorFromDisplay(metadata);}catch{return;}const k=geometrySelectionKey(params);geometry.session.enter({selection:params,anchor,label:{name,buildVersion:workspace.build.version},origin:geometryOrigin(k,invoker??document.body)},localOnly?{localOnly}:{fresh:true});},
  open:(k:string)=>{const v=geometryView;if(!geometry||!v)return;expandOrigin.current=null;setExpandedKey(k);void geometry.session.leave('source').then(()=>geometry.session.enter(v.intent));},
 };
 // Return draws the kept geometry in its preview again (local only) and puts focus back on Open geometry view.
 function returnGeometry(){const v=geometryView;if(!geometry||!v)return;expandOrigin.current=geometrySelectionKey(v.intent.selection);setExpandedKey(null);void geometry.session.leave('return').then(()=>geometry.session.enter(v.intent,{localOnly:true}));}

 /* ----- List pane ----- */
 const applied=page?.exactCreatureId!==null&&page?.exactCreatureId!==undefined?<>Exact Creature ID <b className="iv-mono">{page.exactCreatureId}</b></>:page?.query?<>Name contains <b>“{page.query}”</b></>:null;
 const prepStatusRef=useCallback((node:HTMLDivElement|null)=>{prepStatus.current=node;},[]);
 const preparing=<div className="iv-wait">{waiting&&op&&flow.pending&&!own?<div className="dh-state-box" role="status"><strong>Waiting for Creatures in {op.intent.context.productId} · build {op.intent.buildVersion}</strong><p>Another build's Creatures {op.status==='stopping'?'are stopping':'are preparing'}. This build prepares when that finishes, if you stay in Creatures.</p><div className="dh-row"><ActivityLink taskKey={`creatures:${op.id}`} label="View task"/><button className="dh-btn dh-small" onClick={()=>{flow.stopWaiting(context);heading.current?.focus({preventScroll:true});}}>Stop waiting for Creatures</button></div></div>
  :waiting?<div className="dh-state-box" role="status"><strong>Waiting for display requests to finish</strong><p>Two display requests are already preparing tables. This build's Creatures prepare when one of them finishes, if you stay here.</p><div className="dh-row">{displays?.operations.filter(o=>['pending','stopping'].includes(o.status)).slice(0,1).map(o=><ActivityLink key={o.id} taskKey={`display:${o.id}`} label="View task"/>)}<button className="dh-btn dh-small" onClick={()=>{flow.stopWaiting(context);heading.current?.focus({preventScroll:true});}}>Stop waiting for Creatures</button></div></div>
    :blocked&&!(own&&op?.status==='stopping')?<div className={`dh-state-box ${blocked.kind==='stopped'?'dh-plain':'iv-error'}`} role={blocked.kind==='stopped'?'status':'alert'}><strong>{blocked.kind==='stopped'?'Creatures loading stopped':'Creatures need attention'}</strong>{blocked.error&&(blocked.error.code==='CANCELED'?<p>Stopped.</p>:<p><code>{blocked.error.code}</code> {blocked.error.message}</p>)}<p>Verified source content is kept.</p>{!(own&&flow.pending)&&(blocked.error?.retryable!==false||!!blocked.error&&settingsStep(blocked.error))&&<div className="dh-row">{blocked.error?.retryable!==false&&<button className="dh-btn dh-small" onClick={e=>retryCore(e.currentTarget)}>Retry Creatures preparation</button>}{blocked.error&&settingsStep(blocked.error)&&<OpenSettingsButton className="dh-btn dh-small">Open Settings</OpenSettingsButton>}{blocked.kind==='failed'&&own&&op&&<ActivityLink taskKey={`creatures:${op.id}`} label="View task"/>}</div>}</div>
  :<div ref={prepStatusRef} tabIndex={-1} className="dh-state-box" role="status"><strong>{own&&op?.status==='stopping'?'Stopping Creatures preparation…':checking||!checked?'Checking retained Creatures…':'Preparing Creatures…'}</strong>{own&&op?.status==='pending'&&op.progress?.stage&&<p>{op.progress.stage}</p>}
   <dl className="iv-prereq"><dt>Creature</dt><dd>{snapshot&&!needs?'Ready':snapshot?'Checking the current admission · the rows below are the last admitted ones':'Not ready yet · needed by Creatures, at interactive priority'}</dd></dl>
   {own&&flow.pending&&<div className="dh-row"><button ref={flow.cancelButton} className="dh-btn dh-small" aria-disabled={op!.status==='stopping'} onClick={()=>{if(op!.status!=='stopping')void flow.cancel(op!.id);}}>{op!.status==='stopping'?'Stopping…':'Stop preparing Creatures'}</button><ActivityLink taskKey={`creatures:${op!.id}`} label="View task"/></div>}</div>}
  <p className="dh-reason" id="cv-search-reason">Search and the list work once the Creature table is ready. Display tables load when you open a creature.</p></div>;
 const rows=page?.entries??[],hidden=!!selected&&!!page&&!rows.some(r=>r.id===selected.id);
 const listPane=<section className="dh-pane iv-list" aria-labelledby="creatures-heading">
  <div className="dh-pane-head iv-list-head"><h1 id="creatures-heading" ref={heading} tabIndex={-1}>Creatures</h1><span className="dh-spacer"/>
   <div className="dh-segmented" role="group" aria-label="Search by">{(['name','id'] as const).map(m=><button key={m} type="button" aria-pressed={mode===m} disabled={prepState||refreshing} onClick={()=>{setMode(m);setDraft('');setInputError('');requestAnimationFrame(()=>input.current?.focus());}}>{m==='name'?'Name':'Creature ID'}</button>)}</div>
   <form className="iv-search" role="search" aria-label="Search Creatures" onSubmit={e=>{e.preventDefault();apply();}}><input ref={input} className="dh-control" type="search" value={draft} placeholder={mode==='id'?'Exact Creature ID':'Name contains…'} aria-label={mode==='id'?'Exact Creature ID':'Name contains'} aria-keyshortcuts="Control+F" inputMode={mode==='id'?'numeric':undefined} aria-invalid={!!inputError} aria-describedby={inputError?'cv-search-err':prepState||refreshing?'cv-search-reason':undefined} disabled={prepState||refreshing} onChange={e=>{++pageToken.current;setBusy(false);setError(null);setDraft(e.target.value);setInputError('');}}/><button className="dh-btn dh-small" type="submit" disabled={prepState||refreshing} aria-disabled={busy}>Search</button></form>
  </div>
  {inputError&&<p className="iv-search-err" id="cv-search-err" role="alert">{inputError}</p>}
  {prepState?<div className="dh-pane-scroll">{preparing}{error&&!blocked&&<div className="dh-state-box iv-error" role="alert"><strong>Creatures need attention</strong><p><code>{error.code}</code> {error.message}</p><div className="dh-row">{error.retryable!==false&&<button className="dh-btn dh-small" onClick={e=>retryCore(e.currentTarget)}>Retry Creatures preparation</button>}{settingsStep(error)&&<OpenSettingsButton className="dh-btn dh-small">Open Settings</OpenSettingsButton>}</div></div>}</div>:<>
   {refreshing&&<div className="cv-refresh">{preparing}</div>}
   <FeatureDefinitionNotice id="creatures" sources={[snapshot.creatureSource]} build={workspace.build.version}/>
   {snapshot.creatureSource.coverage.scope!=='complete'&&<div className="dh-notice dh-calm"><div className="dh-text"><strong>Creature readable rows · Partial</strong>Encrypted sections aren't loaded. A search can't show that a creature is absent outside the readable rows.</div></div>}
   {notice&&<p className="dh-notice dh-calm" role="status">{notice}</p>}
   <div className={`iv-applied${applied?'':' iv-browse'}`} role="status" aria-live="polite">{applied?<><span>{applied} · {rows.length} on this page</span><span className="dh-spacer"/><button className="dh-linkbtn" onClick={()=>apply(true)}>Clear search</button></>:<span>All admitted Creatures · ascending Creature ID</span>}</div>
   {page&&applied&&(mode==='name'&&page.query!==draft||mode==='id'&&page.exactCreatureId!==draft)?<p className="iv-draft">Showing results for the applied search. Search to apply your changes.</p>:null}
   {busy&&<p className="iv-draft" role="status">Reading Creatures… The previous rows stay visible.</p>}
   {error&&<div className="dh-notice iv-error" role="alert"><div className="dh-text"><strong>{error.code}</strong>{error.message}</div>{error.retryable&&last.current&&<button className="dh-btn dh-small" aria-disabled={busy} onClick={()=>{if(busy||!last.current)return;void read(snapshot,last.current.params,last.current.movement,true);}}>Retry Creature page</button>}<button className="dh-btn dh-small" onClick={e=>retryCore(e.currentTarget)}>Recheck Creature admission</button></div>}
   {hidden&&<div className="dh-notice dh-calm"><div className="dh-text">Selected creature {selected!.id} is outside these results. Its details stay open.</div></div>}
   {page&&rows.length>0&&<div className="iv-list-scroll" ref={listScroll} onScroll={e=>{listTop.current=e.currentTarget.scrollTop;flow.cachePut(context,{snapshot,page,mode,draft,history,cursor,selected,detail,notice,listTop:listTop.current,pane});}}><div className="iv-thead iv-cols cv-cols" aria-hidden="true"><span>Creature</span><span>Displays</span></div>
    <div ref={listbox} role="listbox" id="creatures-listbox" className="iv-listbox" tabIndex={0} aria-label="Creatures on this page" aria-activedescendant={rows.some(r=>r.id===selected?.id)?`cv-opt-${selected!.id}`:undefined} onKeyDown={listKey}>
     {rows.map(row=>{const on=row.id===selected?.id,n=declaredCount(row),type=row.facts[1]?.value,named=row.name.state==='valid';
      return <div key={row.id} role="option" id={`cv-opt-${row.id}`} aria-selected={on} className={`iv-cols cv-cols iv-option cv-option${on?' iv-active':''}`} onClick={()=>{listbox.current?.focus({preventScroll:true});choose(row,true);}}>
       <span className="iv-name"><b className={named&&row.name.value!.trim()?undefined:'iv-nameless'}>{creatureName(row)}</b>{named?<small>{row.id}{type!==undefined&&` · Type ${type}`}<span className="iv-lvl-inline"> · {displaysWord(n).toLowerCase()}</span></small>:<small className="iv-why">{row.id} · {row.name.state==='empty'?'Name_lang is an empty string':'Name_lang bytes aren’t valid UTF-8'}<span className="iv-lvl-inline"> · {displaysWord(n).toLowerCase()}</span></small>}</span>
       <span className="iv-level">{n?n:<span className="iv-nv">None</span>}</span></div>;})}
    </div></div>}
   {page&&rows.length===0&&<div className="dh-pane-scroll"><div className="iv-empty"><div className="dh-state-box dh-plain"><strong>No match</strong><p>{page.exactCreatureId!==null?`No Creature row in this build's ${snapshot.creatureSource.coverage.scope==='complete'?'admitted':'readable'} Creatures has ID ${page.exactCreatureId}.`:`No admitted creature name contains “${page.query}”. Search reads Name_lang only: titles, alternative names and rows without a readable name don't match.`}</p><div className="dh-row"><button className="dh-btn dh-small" onClick={()=>apply(true)}>Clear search</button></div></div></div></div>}
   <div className="dh-pane-foot" role="group" aria-label="Creatures pages"><span className="iv-count">{page?rows.length?`${applied?'Results':'Creatures'} ${rows.length===1?'1':`1–${rows.length}`}${page.nextCursor?' · more after':''}`:'No results':'Reading Creatures…'}</span><span className="dh-spacer"/>
    <button className="dh-btn" aria-label="First page of Creatures" aria-disabled={busy||cursor===undefined} onClick={()=>{if(!busy&&cursor!==undefined)pageMove('first');}}>First</button><button className="dh-btn" aria-label="Previous page of Creatures" aria-disabled={busy||!history.length} onClick={()=>{if(!busy&&history.length)pageMove('previous');}}>Previous</button><button className="dh-btn" aria-label="Next page of Creatures" aria-disabled={busy||!page?.nextCursor} onClick={()=>{if(!busy&&page?.nextCursor)pageMove('next');}}>Next</button></div>
  </>}
 </section>;

 /* ----- Detail pane ----- */
// View from Downloads & activity (a display request of this build): show that creature and its Displays heading.
 const viewFocus=useRef(false),viewTarget=useRef<string|null>(null);
 useLayoutEffect(()=>{const request=displays?.focusRequest;if(!active||!request||!snapshot||creatureKey(request.intent.params)!==key)return;displays!.consumeFocusRequest(request.sequence);if(request.intent.params.artifactId!==snapshot.artifactId){setNotice('The requested display belongs to an older Creature source. Choose the creature again.');return;}viewFocus.current=true;viewTarget.current=request.intent.params.creatureId;if(selected?.id!==request.intent.params.creatureId)void inspect(request.intent.authority.summary);if(compact)setPane('detail');},[active,displays?.focusRequest?.sequence]);
 useLayoutEffect(()=>{if(!viewFocus.current||!active)return;const heading=detailRoot.current?.querySelector<HTMLElement>('#cv-h-displays');if(heading&&selected?.id===viewTarget.current){viewFocus.current=false;viewTarget.current=null;heading.scrollIntoView({block:'start'});heading.focus({preventScroll:true});}});
 const open:Open|undefined=onSource?(source,recordId,columnIndex)=>onSource(source,recordId,columnIndex):undefined;
 const detailPane=<section ref={detailRoot} className="dh-pane iv-detail cv-detail" aria-labelledby="cv-detail-title" inert={covered||undefined} onKeyDown={onRoveKey}>
  {prepState?<div className="iv-placeholder"><div><h2 id="cv-detail-title" ref={detailHeading} tabIndex={-1}>No creature selected</h2><p>Creature details appear when the Creature table is ready.</p>{onOpenTable&&!creatureUnreadable&&<button type="button" className="dh-btn dh-small iv-open-table" onClick={()=>onOpenTable(CREATURE_TABLE,'Creature')}>Open Creature in Tables</button>}</div></div>
  :!selected?<div className="iv-placeholder"><div><h2 id="cv-detail-title" ref={detailHeading} tabIndex={-1}>No creature selected</h2><p>Choose a creature to see its record, displays and model.</p></div></div>
  :<CreatureDetailView key={selected.id} capacityBlocked={flow.capacityBlocked} workspace={workspace} bridge={bridge} active={active} snapshot={snapshot} summary={selected} detail={detail} busy={detailBusy} error={detailError} onRetry={()=>void inspect(selected,true)} classification={classification} open={open} onFile={onFile} compact={compact} headingRef={detailHeading} displays={displayAvailable?displays:undefined} factsAvailable={factsAvailable} tableLoading={tableLoading} preview={geometry?preview:undefined}/>}
 </section>;
 const sel=selected&&!prepState?(selected.name.state==='valid'?selected.name.value:`Creature ${selected.id}`):null;
 return <>
  {active&&fullGeometry&&<div className="cv-geometry"><CreatureGeometryView flow={geometry!} onReturn={returnGeometry} returnLabel="← Return to Creatures selection"/></div>}
  <section className="iv cv creatures-browser" hidden={!active||fullGeometry} aria-label="Creatures">
   <div className="dh-pane-switch iv-switch"><div className="dh-segmented" role="group" aria-label="Show pane"><button type="button" data-pane="list" aria-pressed={pane==='list'} onClick={()=>showPane('list')}>Creatures list</button><button type="button" data-pane="detail" aria-pressed={pane==='detail'} onClick={()=>showPane('detail')}>Selected creature</button></div>{sel&&<span className="dh-reason iv-switch-name">{sel}</span>}</div>
   <div className="iv-panes" data-pane={pane}>{listPane}{detailPane}</div>{status}
  </section></>;
}

/* ---------- Selected creature ---------- */
type Preview={slot:number|null;view:NonNullable<GeometryFlow['snapshot']['view']>|null;flow?:GeometryFlow;show:(params:CreatureDisplayParams,metadata:CreatureDisplayMetadataResult,name:string,invoker:HTMLElement|null,localOnly?:boolean)=>void;open:(key:string)=>void};
function CreatureDetailView({capacityBlocked=false,workspace,bridge,active,snapshot:s,summary,detail,busy,error,onRetry,classification,open,onFile,compact,headingRef,displays,factsAvailable,tableLoading,preview}:{capacityBlocked?:boolean;workspace:WorkspaceSnapshot;bridge:DiscoveryBridge;active:boolean;snapshot:CreaturesSnapshot;summary:CreatureSummary;detail:CreatureDetail|null;busy:boolean;error:TableFailure|null;onRetry:()=>void;classification:Map<string,string>|null;open?:Open;onFile?:(target:CreatureFileTarget)=>void;compact:boolean;headingRef:React.RefObject<HTMLHeadingElement|null>;displays?:CreatureDisplays;factsAvailable:boolean;tableLoading?:TableLoading;preview?:Preview}){
 const source=s.creatureSource,row=detail?.creatureRow??null;
 // Type and Family text: this build's CreatureType and CreatureFamily Name_lang, read through Tables' reference text for this one row.
 const refPage:TableDataPage|null=row?{productId:source.productId,buildIdentity:source.buildIdentity,sourceRegion:source.sourceRegion,tableId:source.tableId,researchLocale:'enUS',artifactId:source.artifactId,rows:[row],totalCount:'1',nextCursor:null}:null;
 const refQuery:TableRowsParams|null=row?{productId:source.productId,buildIdentity:source.buildIdentity,sourceRegion:source.sourceRegion,tableId:source.tableId,researchLocale:'enUS',artifactId:source.artifactId,recordId:row.id,limit:1}:null;
 const noFlow=useNoTableLoading();
 const ref=useReferenceText({bridge,flow:tableLoading??noFlow,active:active&&!!tableLoading,snapshot:tableLoading?source:null,page:refPage,query:refQuery});
 const value=(index:number):ReactNode=>{if(!row)return <span className="iv-ld">…</span>;if(row.textIssues?.some(t=>t.columnIndex===index))return nv('Invalid text','the source bytes aren’t valid UTF-8; the raw bytes are in Tables');const v=row.values[index];return v===''?<span className="iv-fsub">Empty string</span>:!v.trim()?<><span className="iv-fsub">Only spaces or line breaks</span> <span className="cv-exact">{JSON.stringify(v)}</span></>:<span className="cv-text">{v}</span>;};
 const link=(index:number,label?:string)=><SourceLink source={source} recordId={summary.id} columnIndex={index} label={label??`Creature.${source.columns[index]?.name}`} open={open}/>;
 const [classIndex,typeIndex,familyIndex,animIndex]=s.fields.facts.map(f=>f.columnIndex),classValue=summary.facts[0]?.value,typeValue=summary.facts[1]?.value,familyValue=summary.facts[2]?.value;
 const textOf=(index:number,v:string|undefined)=>{if(v===undefined||!row)return null;const view=ref.view(index,v);return view;};
 const typeText=textOf(typeIndex,typeValue),familyText=textOf(familyIndex,familyValue);
 const refSub=(view:ReturnType<typeof textOf>,table:string)=>!view?null:view.cell===null?null:view.pending?<><span className="iv-ld">…</span> <span className="iv-fsub">{table} loading</span>{view.sr&&<span className="iv-visually-hidden">{view.sr}</span>}</>:sub(view.cell);
 // The text's own row isn't opened from here: its snapshot belongs to Tables, which follows Creature.CreatureType to it (D072).
 const refLink=(view:ReturnType<typeof textOf>,table:string,v:string|undefined)=>view?.source&&v!==undefined?<span className="iv-nosrc">from {table} #{v} · Name_lang</span>:null;
 const always=source.columns.map((c,i)=>({c,i})).filter(x=>/^AlwaysItem\[\d+\]$/.test(x.c.name));
 const classLabel=classValue===undefined?null:classification?.get(classValue)??null;
 const title=s.fields.title.columnIndex,titleValue=row?row.values[title]:null,titleShown=!!titleValue&&!row?.textIssues?.some(t=>t.columnIndex===title);
 const n=declaredCount(summary);
 const head=<div className="iv-dhead cv-dhead"><div className="iv-dtitle"><h2 id="cv-detail-title" ref={headingRef} tabIndex={-1} className="iv-selectable">{summary.name.state==='valid'&&summary.name.value!.trim()?summary.name.value:<span className="iv-nv">{creatureName(summary)}</span>}</h2>
  <div className="iv-dline"><span className="iv-mono iv-selectable">Creature {summary.id}</span>{titleShown&&<><span className="iv-sep" aria-hidden="true">|</span><span className="cv-subname iv-selectable">{titleValue}</span></>}{typeValue!==undefined&&<><span className="iv-sep" aria-hidden="true">|</span><span>{typeText?.text??`Type ${typeValue}`}</span></>}{classValue!==undefined&&<><span className="iv-sep" aria-hidden="true">|</span><span>{classLabel??`Classification ${classValue}`}</span></>}<span className="iv-sep" aria-hidden="true">|</span><span>{displaysWord(n)}</span></div></div>
  <div className="iv-drec">{open&&<button type="button" className="dh-btn dh-small" data-iv-key={`cv|rec|${summary.id}`} onClick={()=>open(source,summary.id)}>Creature #{summary.id}</button>}</div></div>;
 const creatureSection=<section className="iv-sec" aria-labelledby="cv-h-creature" data-rove><div className="iv-sec-head"><h3 id="cv-h-creature">Creature</h3><span className="iv-scope">Creature</span></div>
  {busy&&!row&&<p className="iv-sec-note" role="status">Reading the Creature record…</p>}
  {error&&<div className="dh-state-box iv-error iv-sec-state" role="alert"><strong>The record could not be read</strong><p><code>{error.code}</code> {error.message}</p>{error.retryable&&<div className="dh-row"><button className="dh-btn dh-small" onClick={onRetry}>Retry Creature record</button></div>}</div>}
  <Fact label="Name" value={value(s.fields.name.columnIndex)} sources={link(s.fields.name.columnIndex)}/>
  <Fact label="Alternative name" value={value(s.fields.nameAlt.columnIndex)} sources={link(s.fields.nameAlt.columnIndex)}/>
  <Fact label="Title" value={value(title)} sources={link(title)} note={titleShown?'Shown under the name in game.':undefined}/>
  <Fact label="Alternative title" value={value(s.fields.titleAlt.columnIndex)} sources={link(s.fields.titleAlt.columnIndex)}/>
  <Fact label="Classification" value={classValue===undefined?nv('Unknown','not projected'):<>{classValue} {sub(classLabel??'no reviewed label')}</>} sources={link(classIndex)}/>
  <Fact label="Type" value={typeValue===undefined?nv('Unknown','not projected'):<>{typeValue} {refSub(typeText,'CreatureType')}</>} sources={<>{link(typeIndex)}{refLink(typeText,'CreatureType',typeValue)}</>} note={typeText&&!typeText.text&&typeText.cell!=='…'?typeText.detail:undefined}/>
  <Fact label="Family" value={familyValue===undefined?nv('Unknown','not projected'):<>{familyValue} {refSub(familyText,'CreatureFamily')}</>} sources={<>{link(familyIndex)}{refLink(familyText,'CreatureFamily',familyValue)}</>} note={familyText&&!familyText.text&&familyText.cell!=='…'?familyText.detail:undefined}/>
  <Fact label="Start animation state" value={summary.facts[3]?<>{summary.facts[3].value} {sub('raw')}</>:nv('Unknown','not projected')} sources={link(animIndex)}/>
  {always.length>0&&<Fact label="Always items" value={row?<>[{always.map(a=>row.values[a.i]).join(', ')}] {sub('raw Item IDs')}</>:<span className="iv-ld">…</span>} sources={link(always[0].i,`Creature.AlwaysItem[0–${always.length-1}]`)}/>}
  {ref.error&&<p className="iv-sec-note"><code>{ref.error.code}</code> Type and Family text couldn't be read; the numbers stay.</p>}
  <p className="iv-legend">The number is the source value. Text after · is CreatureType or CreatureFamily text from this build, or VibeMiner's reviewed Classification list. This is the client Creature record: no spawns, locations, loot or level.</p></section>;
 const displaySection=<DisplaysSection capacityBlocked={capacityBlocked} workspace={workspace} bridge={bridge} active={active} snapshot={s} summary={summary} displays={displays} factsAvailable={factsAvailable} open={open} onFile={onFile} preview={preview}/>;
 return <><div className="iv-dbody cv-dbody">{head}<p className="iv-rove-tip" id="cv-rove-hint">Tab moves between sections and displays; arrow keys move between their links.</p>
  {/* One tree for both layouts: React moves the keyed columns instead of remounting them, so a drawn preview canvas survives
      the switch to one column (Displays first) at enlarged text. */}
  <div className={compact?'iv-grid':'iv-grid iv-two cv-two'}>{(compact?['displays','creature']:['creature','displays']).map(k=><div key={k} className="iv-col">{k==='creature'?creatureSection:displaySection}</div>)}</div></div></>;
}
// Without the shared table flow (older hosts in tests) the reference text hook stays idle: no snapshot, nothing read or loaded.
function useNoTableLoading():TableLoading{return useRef({pending:false,operation:null,blocked:()=>null,load:()=>null,retry:()=>{},latchRevision:0} as unknown as TableLoading).current;}

/* ---------- Displays ---------- */
const FACT_LABEL:Record<string,string>={model:'Model ID',scale:'Scale',textureVariations:'Texture variations',file:'Model file'};
function factOf(record:CreatureDisplayRecordFacts|undefined,key:string):CreatureDisplayFact|undefined{return record?.facts.find(f=>f.key===key);}
function DisplaysSection({capacityBlocked=false,workspace,bridge,active,snapshot:s,summary,displays,factsAvailable,open,onFile,preview}:{capacityBlocked?:boolean;workspace:WorkspaceSnapshot;bridge:DiscoveryBridge;active:boolean;snapshot:CreaturesSnapshot;summary:CreatureSummary;displays?:CreatureDisplays;factsAvailable:boolean;open?:Open;onFile?:(target:CreatureFileTarget)=>void;preview?:Preview}){
 const context=creaturesContext(workspace),declared=summary.displaySlots.filter(slot=>slot.state==='declared');
 const paramsOf=(slot:number):CreatureDisplayParams=>({...context,artifactId:s.artifactId,creatureId:summary.id,slot});
 const [reads,setReads]=useState<Record<number,SlotRead>>({}),[revision,setRevision]=useState(0),alive=useRef(true),generation=useRef(0);
 useEffect(()=>{alive.current=true;return()=>{alive.current=false;generation.current++;};},[]);
 const canRead=!!displays&&(factsAvailable?!!bridge.creatureDisplayFacts:!!bridge.creatureDisplayRead);
 // Each declared slot reads its chain locally, one at a time (two local read credits are shared with every view).
 // Showing the area again doesn't read again: only a new selection, a finished preparation or Retry does.
 const shown=useRef(false);if(active)shown.current=true;
 useEffect(()=>{if(!shown.current||!canRead||!declared.length)return;const token=++generation.current;void(async()=>{for(const slot of declared){if(!alive.current||token!==generation.current)return;const params=paramsOf(slot.slot),k=displayKey(params);setReads(old=>({...old,[slot.slot]:{key:k,state:'reading',result:old[slot.slot]?.key===k?old[slot.slot].result:null,error:null}}));
   let entry=await readSlot(bridge,params,factsAvailable,{core:s,summary,opening:workspace.sourceReference});
   // Local reads share two engine credits with every view; a busy reply is asked once more after a short pause.
   if(entry.error?.code==='CREATURES_QUERY_BUSY'){await new Promise(r=>setTimeout(r,250));if(!alive.current||token!==generation.current)return;entry=await readSlot(bridge,params,factsAvailable,{core:s,summary,opening:workspace.sourceReference});}
   if(!alive.current||token!==generation.current)return;setReads(old=>({...old,[slot.slot]:{key:k,...entry}}));}})();},[shown.current,canRead,s.artifactId,summary.id,revision]);
 // The first slot whose chain isn't prepared drives the shared display preparation (CreatureDisplayInfo, then CreatureModelData) at
 // interactive priority; once it finishes every slot reads again.
 const driverSlot=declared.find(slot=>reads[slot.slot]?.result?.metadata.state==='notPrepared')?.slot;
 const driver:DisplayIntent|null=displays&&driverSlot!==undefined?{params:paramsOf(driverSlot),authority:{core:s,summary,opening:workspace.sourceReference},buildVersion:workspace.build.version}:null;
 const driverKey=driver?displayKey(driver.params):null;
 useEffect(()=>{displays?.show(active?driver:null);},[active,driverKey]);
 useEffect(()=>()=>displays?.show(null),[]);
 const driverView=displays?.view&&driverKey&&displayKey(displays.view.intent.params)===driverKey?displays.view:null,driverOp=driver?displays?.operationFor(driver):undefined,driverLatch=driver?displays?.blocked(driver):null;
 const phase=driverView?.phase;
 useEffect(()=>{if(phase==='ready')setRevision(x=>x+1);},[phase,driverKey]);
 const pending=!!driverOp&&['pending','stopping'].includes(driverOp.status);
 const measurement=pending?measuredStage(driverOp!.progress):null;
 const statusRef=useRef<HTMLDivElement>(null),stopOrigin=useRef<HTMLElement|null>(null);
 useLayoutEffect(()=>{const o=stopOrigin.current;if(o&&!o.isConnected){stopOrigin.current=null;if(document.activeElement===document.body||document.activeElement===o)statusRef.current?.focus({preventScroll:true});}});
 const anyComplete=declared.map(slot=>reads[slot.slot]?.result?.metadata).find(Boolean);
 const coverage=anyComplete?anyComplete.sources.map(t=>`${t.role==='display'?'CreatureDisplayInfo':'CreatureModelData'} ${t.snapshot.coverage.scope==='complete'?'complete':'readable rows · Partial'}`).join(' · '):null;
 const status=!displays?<div className="dh-state-box dh-plain iv-sec-state"><strong>Display details aren't available from this engine</strong><p>The four slots are listed from Creature. <code>CREATURE_DISPLAY_UNAVAILABLE</code></p></div>
  :driverLatch&&!pending?<div className={`dh-state-box iv-sec-state ${driverLatch.kind==='stopped'?'dh-plain':'iv-error'}`} role={driverLatch.kind==='stopped'?'status':'alert'}><strong>{driverLatch.kind==='stopped'?'Display loading stopped':'Displays need attention'}</strong>{driverLatch.error&&driverLatch.error.code!=='CANCELED'&&<p><code>{driverLatch.error.code}</code> {driverLatch.error.message}</p>}{driverLatch.error?.retryable!==false&&<div className="dh-row"><button className="dh-btn dh-small" onClick={()=>{displays.retry(driver!);statusRef.current?.focus({preventScroll:true});}}>Retry display tables</button></div>}</div>
  :driver&&(pending||phase==='waiting'||phase==='reading')?<div className="dh-state-box iv-sec-state" role="status"><strong><span className="dh-loading-line"><span className="dh-pulse" aria-hidden="true"/>{driverOp?.status==='stopping'?'Stopping display loading…':'Loading displays…'}</span></strong><p>{driverOp?.progress?.stage??(phase==='waiting'&&capacityBlocked?'Two display requests are already preparing tables; these displays load when one of them finishes':driver&&reads[driver.params.slot]?.result?.metadata.requiredTableId==='7f695fa5'?'Waiting to load CreatureModelData table data':'Waiting to load CreatureDisplayInfo table data, then CreatureModelData')}. The slots below are already known from Creature.</p>{measurement&&<p aria-live="off">{measurement.label}</p>}
   <div className="dh-row">{pending?<button className="dh-btn dh-small" aria-disabled={driverOp!.status==='stopping'} onClick={e=>{if(driverOp!.status==='stopping')return;stopOrigin.current=e.currentTarget;void displays.cancel(driverOp!.id);}}>{driverOp!.status==='stopping'?'Stopping…':'Stop loading displays'}</button>:<button className="dh-btn dh-small" onClick={e=>{stopOrigin.current=e.currentTarget;displays.stopWaiting(driver!);}}>Stop waiting for displays</button>}{driverOp&&<ActivityLink taskKey={`display:${driverOp.id}`} label="View task"/>}</div></div>:null;
 const loading=!!driver&&(pending||phase==='waiting'||phase==='reading')&&!driverLatch;
 // A model this build already keeps draws by itself, for the first resolved display only (design: kept model draws itself).
 const firstResolved=declared.find(slot=>reads[slot.slot]?.result?.metadata.state==='metadataResolved')?.slot;
 const cards=declared.map(slot=><DisplayCard key={slot.slot} active={active} summary={summary} slot={slot.slot} snapshot={s} read={reads[slot.slot]} loading={loading} open={open} onFile={onFile} preview={preview} autoPreview={slot.slot===firstResolved} params={paramsOf(slot.slot)} onRetry={()=>setRevision(x=>x+1)} factsAvailable={factsAvailable}/>);
 const lines=summary.displaySlots.filter(slot=>slot.state!=='declared').map(slot=><SlotLine key={slot.slot} summary={summary} slot={slot.slot} snapshot={s} open={open}/>);
 return <section className="iv-sec cv-displays" aria-labelledby="cv-h-displays"><div className="iv-sec-head"><h3 id="cv-h-displays" tabIndex={-1}>Displays</h3><span className="iv-scope">Creature → CreatureDisplayInfo → CreatureModelData</span></div>
  {/* One stable, focusable status: Stop and Retry hand focus to it, and it keeps focus while its words change. */}
  <div ref={statusRef} tabIndex={-1} className="cv-status">{status}</div>
  {preview?.flow?.snapshot.retirement?.error&&<div className="dh-notice iv-error cv-cleanup" role="alert"><div className="dh-text"><strong>The last model view couldn't be closed cleanly</strong><code>{preview.flow.snapshot.retirement.error.code}</code> {preview.flow.snapshot.retirement.error.message}</div></div>}
  {declared.length===0?<div className="dh-state-box dh-plain iv-sec-state"><strong>No display declared</strong><p>All four Creature.DisplayID slots are 0 or invalid. The four slots stay listed below.</p></div>
   :<p className="iv-sec-note">Each declared slot, in slot order. Probabilities are as read: no share is worked out and no display is picked.{coverage&&!loading?` ${coverage}.`:''}</p>}
  {cards}{lines.length>0&&<div className="cv-lines">{lines}</div>}
  {preview&&<p className="iv-legend">A preview is unposed geometry, not the in-game look. Open geometry view for the evidence and controls.</p>}</section>;
}
async function readSlot(bridge:DiscoveryBridge,params:CreatureDisplayParams,facts:boolean,authority:DisplayIntent['authority']):Promise<Omit<SlotRead,'key'>>{
 try{
  if(facts&&bridge.creatureDisplayFacts){const reply=await bridge.creatureDisplayFacts(params);if(!reply.ok)return{state:'failed',result:null,error:reply.error};if(!creatureDisplayFactsMatches(reply.value,params,authority))return{state:'failed',result:null,error:{code:'CREATURE_DISPLAY_CONTEXT_MISMATCH',message:'The display facts differ from this exact slot or its source.',retryable:true}};return{state:'ready',result:reply.value,error:null};}
  const reply=await bridge.creatureDisplayRead!(params);if(!reply.ok)return{state:'failed',result:null,error:reply.error};if(!creatureDisplayMatches(reply.value,params,authority))return{state:'failed',result:null,error:{code:'CREATURE_DISPLAY_CONTEXT_MISMATCH',message:'The display chain differs from this exact slot or its source.',retryable:true}};
  return{state:'ready',result:{metadata:reply.value,factsRevision:'creature-display-facts-v1',records:[]},error:null};
 }catch{return{state:'failed',result:null,error:{code:'ENGINE_UNAVAILABLE',message:'The display chain could not be read.',retryable:true}};}
}
function SlotLinks({summary,slot,snapshot:s,open}:{summary:CreatureSummary;slot:number;snapshot:CreaturesSnapshot;open?:Open}){
 const d=s.fields.displayIds[slot].columnIndex,p=s.fields.probabilities[slot].columnIndex;
 return <span className="iv-srcs"><SourceLink source={s.creatureSource} recordId={summary.id} columnIndex={d} label={`DisplayID[${slot}]`} full={`Creature.DisplayID[${slot}]`} open={open}/><SourceLink source={s.creatureSource} recordId={summary.id} columnIndex={p} label={`DisplayProbability[${slot}]`} full={`Creature.DisplayProbability[${slot}]`} open={open}/></span>;
}
function SlotLine({summary,slot,snapshot,open}:{summary:CreatureSummary;slot:number;snapshot:CreaturesSnapshot;open?:Open}){
 const v=summary.displaySlots[slot];
 return <div className="cv-slot-line" data-rove><b>Slot {slot}</b>{v.state==='noDeclaredDisplay'?<span>DisplayID 0 · <span className="iv-nv">No display declared</span></span>:<span>DisplayID {v.displayId} · <span className="iv-nv">Invalid reference</span> <span className="iv-fsub">· a negative value can't name a CreatureDisplayInfo row; nothing is looked up</span></span>}<span className="iv-fsub">Probability {v.probability}</span><SlotLinks summary={summary} slot={slot} snapshot={snapshot} open={open}/></div>;
}
function cellsText(cells:readonly CreatureDisplayFactCell[]){return cells.length===1?cells[0].value:`[${cells.map(c=>c.value).join(', ')}]`;}
function DisplayCard({summary,slot,snapshot:s,read,loading,open,onFile,preview,autoPreview,params,onRetry,factsAvailable,active}:{active:boolean;summary:CreatureSummary;slot:number;snapshot:CreaturesSnapshot;read?:SlotRead;loading:boolean;open?:Open;onFile?:(target:CreatureFileTarget)=>void;preview?:Preview;autoPreview:boolean;params:CreatureDisplayParams;onRetry:()=>void;factsAvailable:boolean}){
 const v=summary.displaySlots[slot],dup=summary.displaySlots.findIndex((o,j)=>j<slot&&o.state==='declared'&&o.displayId===v.displayId);
 const result=read?.result,m=result?.metadata,display=result?.records.find(r=>r.role==='display'),model=result?.records.find(r=>r.role==='model');
 const dTarget=m?.sources.find(t=>t.role==='display'),mTarget=m?.sources.find(t=>t.role==='model');
 const fieldLink=(target:typeof dTarget,recordId:string|undefined,cell:CreatureDisplayFactCell,label?:string)=>target&&recordId!==undefined?<SourceLink key={cell.columnIndex} source={target.snapshot} recordId={recordId} columnIndex={cell.columnIndex} label={label??cell.column} full={`${tableName(target.snapshot)}.${cell.column}`} open={open}/>:null;
 const factView=(target:typeof dTarget,record:CreatureDisplayRecordFacts|undefined,key:string,render:(cells:readonly CreatureDisplayFactCell[])=>ReactNode,extra?:ReactNode,linkLabel?:string)=>{const f=factOf(record,key);if(!f)return null;return <Fact key={key} label={key==='scale'&&record?.role==='model'?'Model scale':FACT_LABEL[key]} value={f.state==='unsupported'?nv('Unsupported','not in this layout'):render(f.cells)} sources={f.cells.length?<>{fieldLink(target,record?.recordId,f.cells[0],linkLabel??(f.cells.length>1?`${baseName(f.cells[0].column)}[0–${f.cells.length-1}]`:f.cells[0].column))}{extra}</>:extra}/>;};
 const head=<div className="cv-disp-head"><h4 id={`cv-dh-${slot}`}>Slot {slot} · Display {v.displayId}</h4><span className="cv-prob">Probability <b>{v.probability}</b>{v.probability==='0'&&<span className="iv-fsub"> · 0 as read</span>}</span>{dup>=0&&<span className="iv-conflict">Same display as slot {dup}</span>}<SlotLinks summary={summary} slot={slot} snapshot={s} open={open}/></div>;
 let chain:ReactNode;
 if(loading&&(!m||m.state==='notPrepared'))chain=<><div className="cv-block"><div className="cv-block-head"><b>CreatureDisplayInfo</b><span className="iv-nosrc">loading</span></div><Fact label="Model ID" value={<><span className="iv-ld">…</span> <span className="iv-fsub">CreatureDisplayInfo loading</span></>}/></div><div className="cv-block"><div className="cv-block-head"><b>CreatureModelData</b><span className="iv-nosrc">loads after CreatureDisplayInfo</span></div><Fact label="Model file" value={<><span className="iv-ld">…</span> <span className="iv-fsub">loads after CreatureDisplayInfo</span></>}/></div></>;
 else if(!read||read.state==='reading'&&!m)chain=<p className="iv-sec-note" role="status">Reading this display…</p>;
 else if(read.state==='failed'||!m)chain=<div className="dh-state-box iv-error iv-sec-state" role="alert"><strong>This display could not be read</strong>{read.error&&<p><code>{read.error.code}</code> {read.error.message}</p>}{read.error?.retryable!==false&&<div className="dh-row"><button className="dh-btn dh-small" onClick={onRetry}>Retry display</button></div>}</div>;
 else chain=<Chain metadata={m} display={display} model={model} dTarget={dTarget} mTarget={mTarget} factView={factView} fieldLink={fieldLink} open={open} onFile={onFile} factsAvailable={factsAvailable}/>;
 const raw=(display?.raw.length||model?.raw.length)?<details className="iv-more cv-raw" onToggle={e=>rove(e.currentTarget.closest('.cv-detail'))}><summary>Display and model inputs · raw</summary><p className="iv-sec-note">Fields without a reviewed meaning, as read. Only the fields this layout has are listed.</p>
  {display?.raw.map(c=><Fact key={`d${c.columnIndex}`} label={c.column} value={c.value} sources={fieldLink(dTarget,display.recordId,c,`CreatureDisplayInfo.${c.column}`)}/>)}{model?.raw.map(c=><Fact key={`m${c.columnIndex}`} label={c.column} value={c.value} sources={fieldLink(mTarget,model.recordId,c,`CreatureModelData.${c.column}`)}/>)}</details>:null;
 return <article className="cv-display" aria-labelledby={`cv-dh-${slot}`} data-rove>{head}<div className={preview?'cv-disp-body':'cv-disp-body cv-no-preview'}>{preview&&<PreviewBox preview={preview} params={params} metadata={m??null} loading={loading} name={creatureName(summary)} slot={slot} displayId={v.displayId} auto={autoPreview} active={active}/>}<div className="cv-chain">{chain}</div></div>{raw}</article>;
}
const baseName=(column:string)=>{const i=column.indexOf('[');return i<0?column:column.slice(0,i);};
function Chain({metadata:m,display,model,dTarget,mTarget,factView,fieldLink,open,onFile,factsAvailable}:{metadata:CreatureDisplayMetadataResult;display?:CreatureDisplayRecordFacts;model?:CreatureDisplayRecordFacts;dTarget?:CreatureDisplayMetadataResult['sources'][number];mTarget?:CreatureDisplayMetadataResult['sources'][number];factView:(target:CreatureDisplayMetadataResult['sources'][number]|undefined,record:CreatureDisplayRecordFacts|undefined,key:string,render:(cells:readonly CreatureDisplayFactCell[])=>ReactNode,extra?:ReactNode,linkLabel?:string)=>ReactNode;fieldLink:(target:CreatureDisplayMetadataResult['sources'][number]|undefined,recordId:string|undefined,cell:CreatureDisplayFactCell,label?:string)=>ReactNode;open?:Open;onFile?:(target:CreatureFileTarget)=>void;factsAvailable:boolean}){
 const displayId=m.selected?.displayId??'';
 const partial=(t?:CreatureDisplayMetadataResult['sources'][number])=>t?.snapshot.coverage.scope!=='complete';
 const box=(word:string,text:ReactNode,extra?:ReactNode)=><><div className="dh-state-box dh-plain iv-sec-state"><strong>{word}</strong><p>{text}</p></div>{extra}</>;
 const recordLink=(t:CreatureDisplayMetadataResult['sources'][number]|undefined,label:string)=>t?.row&&open?<SourceLink source={t.snapshot} recordId={t.row.source.rowId} label={label} open={open}/>:<span className="iv-nosrc">{label}</span>;
 const problem=m.problem?<p className="iv-sec-note"><code>{m.problem.code}</code> {m.problem.message}</p>:null;
 switch(m.state){
  case 'notPrepared':return box('Not loaded yet',`${m.requiredTableId==='7f695fa5'?'CreatureModelData':'CreatureDisplayInfo'} isn't prepared for this build yet. It loads with the displays.`);
  case 'displayMissingInAdmittedScope':return m.absence==='absentFromCompleteSource'?box('Missing',`CreatureDisplayInfo, complete in this build, has no row ${displayId}.`):box('Unknown',`Display ${displayId} isn't in the readable CreatureDisplayInfo rows. It may be in an encrypted section; nothing is inferred.`);
  case 'displayAmbiguous':return box('Ambiguous',`${exactCount(dTarget?.matchCount??'0')} CreatureDisplayInfo rows have ID ${displayId}; none is picked, so no model is looked up.`,open&&dTarget?<div className="iv-group-src"><span>Rows:</span><SourceLink source={dTarget.snapshot} recordId={displayId} label={`CreatureDisplayInfo #${displayId}`} open={open}/></div>:null);
  case 'protected':case 'unsupported':case 'unavailable':case 'corrupt':case 'resourceLimit':{
   const words={protected:'Protected',unsupported:'Unsupported',unavailable:'Unavailable',corrupt:'Failed integrity checks',resourceLimit:'Too large to read'}[m.state];
   return <>{box(words,m.problem?.target==='model'?'CreatureModelData for this display could not be read.':'CreatureDisplayInfo for this display could not be read.')}{problem}</>;}
 }
 const dBlock=<div className="cv-block"><div className="cv-block-head"><b>CreatureDisplayInfo</b>{recordLink(dTarget,`CreatureDisplayInfo #${dTarget?.row?.source.rowId??displayId}`)}{dTarget&&partial(dTarget)&&<span className="iv-nosrc">readable rows · Partial</span>}</div>
  {display?<>{factView(dTarget,display,'model',cells=>cells[0].value==='0'?<>0 {sub('no model declared')}</>:cellsText(cells))}{factView(dTarget,display,'scale',cells=><>{cellsText(cells)} {sub('CreatureModelScale, as read')}</>)}{factView(dTarget,display,'textureVariations',cells=><>{cellsText(cells)} {sub('FileDataIDs')}</>)}</>
   :<Fact label="Model ID" value={m.modelId??(m.state==='noDeclaredModel'?<>0 {sub('no model declared')}</>:nv('Unknown','not read'))} sources={dTarget?.row?fieldLink(dTarget,dTarget.row.source.rowId,{column:'ModelID',columnIndex:dTarget.row.edgeValue.columnIndex,value:dTarget.row.edgeValue.value},'ModelID'):undefined} note={factsAvailable?undefined:'Display facts aren’t available from this engine; only the chain is shown.'}/>}</div>;
 const modelId=m.modelId;
 let mBlock:ReactNode;
 if(m.state==='noDeclaredModel')mBlock=<div className="cv-block"><div className="cv-block-head"><b>CreatureModelData</b><span className="iv-nosrc">not looked up</span></div><p className="iv-sec-note">ModelID is 0, so this display names no model.</p></div>;
 else if(m.state==='modelMissingInAdmittedScope')mBlock=<div className="cv-block"><div className="cv-block-head"><b>CreatureModelData</b><span className="iv-nosrc">{m.absence==='absentFromCompleteSource'?'no record':'not readable'}</span></div>{m.absence==='absentFromCompleteSource'?box('Missing',`CreatureModelData, complete in this build, has no row ${modelId}.`):box('Unknown',`Model ${modelId} isn't in the readable CreatureModelData rows. It may be in an encrypted section; nothing is inferred.`)}</div>;
 else if(m.state==='modelAmbiguous')mBlock=<div className="cv-block"><div className="cv-block-head"><b>CreatureModelData</b><span className="iv-nosrc">ambiguous</span></div>{box('Ambiguous',`${exactCount(mTarget?.matchCount??'0')} CreatureModelData rows have ID ${modelId}; none is picked, so there's no model file.`)}</div>;
 else{
  const file=m.modelFileDataId??mTarget?.row?.edgeValue.value??null;
  const filesLink=file&&file!=='0'&&onFile&&mTarget?.row?<button type="button" className="iv-src" aria-describedby="cv-rove-hint" data-iv-key={`cv|file|${file}`} onClick={e=>{markCurrent(e.currentTarget);onFile({fileDataId:file,table:'CreatureModelData',recordId:mTarget.row!.source.rowId});}}>Find FileDataID {file} in Files</button>:null;
  mBlock=<div className="cv-block"><div className="cv-block-head"><b>CreatureModelData</b>{recordLink(mTarget,`CreatureModelData #${mTarget?.row?.source.rowId??modelId}`)}{mTarget&&partial(mTarget)&&<span className="iv-nosrc">readable rows · Partial</span>}</div>
   {model?<>{factView(mTarget,model,'file',cells=>cells[0].value==='0'?<>0 {sub('no model file declared')}</>:`FileDataID ${cells[0].value}`,filesLink)}{factView(mTarget,model,'scale',cells=><>{cellsText(cells)} {sub('ModelScale, as read')}</>)}</>
    :<Fact label="Model file" value={file===null?nv('Unknown','not read'):file==='0'?<>0 {sub('no model file declared')}</>:`FileDataID ${file}`} sources={<>{mTarget?.row?fieldLink(mTarget,mTarget.row.source.rowId,{column:'FileDataID',columnIndex:mTarget.row.edgeValue.columnIndex,value:mTarget.row.edgeValue.value},'FileDataID'):null}{filesLink}</>}/>}</div>;
 }
 return <>{dBlock}{mBlock}</>;
}

/* ---------- Model preview ---------- */
function PreviewBox({preview,params,metadata,loading,name,slot,displayId,auto,active}:{preview:Preview;params:CreatureDisplayParams;metadata:CreatureDisplayMetadataResult|null;loading:boolean;name:string;slot:number;displayId:string;auto:boolean;active:boolean}){
 const k=geometrySelectionKey(params),mine=preview.slot===slot?preview.view:null,flow=preview.flow,resolved=metadata?.state==='metadataResolved';
 const globalOp=flow?.snapshot.operation&&['pending','stopping'].includes(flow.snapshot.operation.status)?flow.snapshot.operation:null;
 // A preparation for this exact display keeps its Stop here even after the preview was left (another creature, area or build).
 const opHere=globalOp&&geometrySelectionKey(globalOp.intent.selection)===k?globalOp:null,opOther=globalOp&&!opHere?globalOp:null;
 const op=opHere,pending=!!op;
 const showRef=useRef<HTMLButtonElement>(null),stopRef=useRef<HTMLButtonElement>(null),openRef=useRef<HTMLButtonElement>(null),statusRef=useRef<HTMLDivElement>(null),focusNext=useRef<'stop'|'open'|'show'|null>(null),tried=useRef(false);
 const canvasCleanup=useRef<(()=>void)|null>(null),generation=mine?.generation;
 const canvas=useCallback((node:HTMLCanvasElement|null)=>{canvasCleanup.current?.();canvasCleanup.current=null;if(node&&generation!==undefined&&flow){const detach=flow.session.attachCanvas(generation,node),observer=typeof ResizeObserver==='function'?new ResizeObserver(()=>flow.session.resizeCanvas(generation)):null;observer?.observe(node);canvasCleanup.current=()=>{observer?.disconnect();detach();};}},[flow,generation]);
 // A model this build already keeps draws by itself for the first resolved display, each time the area shows it (first open, Home
 // and back, Return from Tables or Files). It only looks: nothing is prepared or downloaded, and a busy engine just leaves Show model.
 const otherDrawn=!!preview.view&&preview.slot!==slot&&preview.view.phase!=='outcome';
 useEffect(()=>{if(!active)tried.current=false;},[active]);
 useEffect(()=>{if(!flow||!active||!resolved||!auto||tried.current||mine||opHere||otherDrawn)return;tried.current=true;
  // The look reads before it answers, so Show model briefly gives way to its status: focus on it moves along and comes back (#99).
  if(!focusNext.current&&showRef.current&&document.activeElement===showRef.current)focusNext.current='show';
  preview.show(params,metadata!,name,null,true);},[resolved,auto,active,!!mine,!!opHere,otherDrawn]);
 // The canvas mounts as soon as the view can draw (the session draws into it); Open geometry view waits for the drawing.
 const mountable=!!mine&&mine.canMount,ready=mountable&&mine!.phase==='ready';
 const outcome=mine&&!pending&&mine.phase==='outcome'?mine:null,result=outcome?.result,problem=outcome?.error??result?.problem??null;
 // A busy engine (its bounded reads or preparations are full) isn't an outcome: a deliberate Show model waits and asks again.
 const busy=!!outcome&&geometryBusy(outcome.error),[busyTries,setBusyTries]=useState(0);
 // Only while Creatures shows and the session can ask again: a try spent while it closes a view or the area is hidden is no try.
 useEffect(()=>{if(!busy||!flow||!active||flow.snapshot.retirement)return;if(busyTries>=BUSY_TRIES)return;const timer=setTimeout(()=>{setBusyTries(n=>n+1);flow.session.retry();},Math.min(400*2**busyTries,3000));return()=>clearTimeout(timer);},[busy,outcome?.generation,busyTries,active,!!flow?.snapshot.retirement]);
 useEffect(()=>{if(ready)setBusyTries(0);},[ready]);
 // Show model hands focus on as the preview changes: to Stop loading model while it prepares, to its status while it only reads,
 // and to Open geometry view once drawn (straight away for a kept model). A deliberate move elsewhere is left alone.
 useLayoutEffect(()=>{const want=focusNext.current;if(!want)return;const now=document.activeElement,ours=!now||now===document.body||now===stopRef.current||now===showRef.current||now===statusRef.current;
  // After Stop the card may first read or wait before Show model or Retry model comes back: focus waits on its status meanwhile.
  // If that look draws a kept model instead, focus goes on to Open geometry view (#99).
  // Any outcome ends the handoff on its status (only a busy engine that is asked again keeps it), so nothing grabs focus later.
  if(want==='show'){if(!ours){focusNext.current=null;return;}
   if(ready&&openRef.current){focusNext.current=null;openRef.current.focus();return;}
   if(showRef.current){focusNext.current=null;showRef.current.focus();return;}
   if(statusRef.current&&now!==statusRef.current)statusRef.current.focus();
   if(outcome&&!(busy&&busyTries<BUSY_TRIES))focusNext.current=null;return;}
  if(!ours){focusNext.current=null;return;}
  if(ready&&openRef.current){focusNext.current=null;openRef.current.focus();return;}
  if(stopRef.current){if(now!==stopRef.current)stopRef.current.focus();return;}
  if(statusRef.current){if(now!==statusRef.current)statusRef.current.focus();if(outcome&&!(busy&&busyTries<BUSY_TRIES))focusNext.current=null;return;}
  if(showRef.current&&!mine){focusNext.current=null;showRef.current.focus();}});
 if(!flow)return null;
 if(loading)return <div className="cv-preview"><div className="cv-pbox cv-off" role="img" aria-label="Model preview waits for the display tables">Preview after CreatureModelData</div></div>;
 if(!resolved)return <div className="cv-preview"><div className="cv-pbox cv-off" role="img" aria-label="No model preview">No model file</div><p className="cv-pnote">The chain stops before a model file.</p></div>;
 const notKept=!!outcome&&!problem&&(!result||result.state==='notPrepared');
 const stop=(id:string)=>{focusNext.current='show';flow.session.stopPreparation(id);};
 if(mountable)return <div className="cv-preview"><div className="cv-pbox cv-drawn"><canvas ref={canvas} aria-label={`Unposed model preview for display ${displayId}`}/><span className="cv-ptag" aria-hidden="true">{ready?'Unposed':'Drawing…'}</span></div>{ready&&<button ref={openRef} type="button" className="dh-btn dh-small" data-geometry-open={k} onClick={()=>preview.open(k)}>Open geometry view</button>}</div>;
 if(op&&(!mine||mine.phase!=='outcome'))return <div className="cv-preview"><div ref={statusRef} tabIndex={-1} className="cv-pbox cv-pending" role="status"><span><span className="dh-pulse" aria-hidden="true"/> {op.status==='stopping'?'Stopping…':'Loading model'}<br/><span className="cv-pstage">{op.progress?.stage??mine?.message??'Preparing required geometry sources'}</span></span></div><button ref={stopRef} type="button" className="dh-btn dh-small" aria-disabled={op.status==='stopping'} onClick={()=>{if(op.status!=='stopping')stop(op.id);}}>Stop loading model</button></div>;
 if(mine&&!outcome&&opOther&&mine.phase==='waiting')return <div className="cv-preview"><div ref={statusRef} tabIndex={-1} className="cv-pbox cv-pending" role="status"><span>Waiting for another model<br/><span className="cv-pstage">Creature {opOther.intent.selection.creatureId} · slot {opOther.intent.selection.slot} is loading first</span></span></div><button type="button" className="dh-btn dh-small" aria-disabled={opOther.status==='stopping'} onClick={()=>{if(opOther.status!=='stopping')flow.session.stopPreparation(opOther.id);}}>Stop that model</button></div>;
 if(mine&&!outcome)return <div className="cv-preview"><div ref={statusRef} tabIndex={-1} className="cv-pbox cv-pending" role="status"><span><span className="dh-pulse" aria-hidden="true"/> Loading model<br/><span className="cv-pstage">{mine.message}</span></span></div></div>;
 if(busy&&busyTries<BUSY_TRIES)return <div className="cv-preview"><div ref={statusRef} tabIndex={-1} className="cv-pbox cv-pending" role="status"><span><span className="dh-pulse" aria-hidden="true"/> Waiting for the engine<br/><span className="cv-pstage">It is busy with other reads; this model is asked for again by itself</span></span></div></div>;
 if(outcome&&!notKept&&result?.state!=='ready'){
  const code=problem?.code??'',encrypted=result?.state==='protected'||/ENCRYPT|KEY_UNAVAILABLE|PROTECTED/.test(code),unsupported=/UNSUPPORTED/.test(code)||result?.state==='unsupported';
  const word=busy?'Engine still busy':encrypted?'Encrypted model':unsupported?'Unsupported model':outcome.message==='Geometry preparation stopped'?'Loading stopped':'Model not drawn';
  return <div className="cv-preview"><div ref={statusRef} tabIndex={-1} className="cv-pbox cv-off" role="img" aria-label={word}>{word}</div><p className="cv-pnote">{problem&&<><code>{problem.code}</code> {problem.message}</>}</p>{(problem?.retryable||word==='Loading stopped')&&<button ref={showRef} type="button" className="dh-btn dh-small" onClick={()=>{focusNext.current='stop';setBusyTries(0);flow.session.retry();}}>Retry model</button>}</div>;
 }
 return <div className="cv-preview"><div className="cv-pbox cv-idle" aria-hidden="true">No preview yet</div><button ref={showRef} type="button" className="dh-btn dh-small" aria-label={`Show model preview for display ${displayId}`} onClick={e=>{focusNext.current='stop';setBusyTries(0);preview.show(params,metadata!,name,e.currentTarget);}}>Show model</button></div>;
}
/** A deliberate Show model asks a busy engine again this many times (about 20 s) before it says the engine is still busy. */
const BUSY_TRIES=10;
