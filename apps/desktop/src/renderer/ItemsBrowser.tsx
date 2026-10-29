import {ActivityLink} from './DownloadActivity';
import {paneScrollTop,restorePaneScroll} from './pane-scroll';
import {ItemEffectsPanel,type EffectsSourceNavigation} from './ItemEffects';
import {effectsKey,type EffectsIntent,type ItemEffectsLoading} from './useItemEffects';
import {ClassificationControls,classificationState} from './ItemClassifications';
import type {Classifications} from './useClassifications';
import {ItemIcon,IconControls,iconStatus,useVisibleIconSlots} from './ItemIcons';
import type {IconsLoading} from './useIconsLoading';
import {useCoveredSide,useShell} from './HomeShell';
import {Fragment,useCallback,useEffect,useLayoutEffect,useMemo,useRef,useState,type ReactNode} from 'react';
import {createPortal} from 'react-dom';
import type {WorkspaceSnapshot,ItemsSnapshot,ItemsPage,ItemSummary,ItemDetail,ItemsReadiness,TableDataSnapshot,ItemFactsResult,ItemFact,ItemFactValue,ItemLabelLists} from '../../../../contracts/generated/discovery';
import type {DiscoveryBridge} from '../shared/bridge';
import {itemsKey,itemsSnapshotMatches,itemsReadinessMatches,itemsPageMatches,itemInspectMatches,itemFactsMatches,validItemsQuery,type ItemsPageParams} from '../shared/items';
import {validRecordId} from '../shared/table-data';
import {itemsContext,type ItemsLoading} from './useItemsLoading';
import {exactCount} from '../shared/inventory';
import type {TableFailure} from './useTableLoading';
import './items.css';

// Items view (docs/design/items-view): a list of admitted items and the selected item's details, stats and effects, each value
// linked to its source record in Tables. The engine projects and labels the facts (items.facts); this view only presents them.
const ITEM_TABLE='50238ec2',SPARSE_TABLE='919be54e';
export const itemName=(s:ItemSummary)=>s.name.state==='valid'?s.name.value:s.name.state==='empty'?'Empty source name':s.name.state==='invalid'?'Name unavailable · Invalid UTF-8':'Name unavailable · No admitted Sparse counterpart';
const ID_ERROR='Enter a whole Item ID from 0 to 4294967295, without signs or leading zeros. The last results stay.';
type Labels={quality:Map<string,string>;inventoryType:Map<string,string>;bonding:Map<string,string>;trigger:Map<string,string>};
export const labelMaps=(l:ItemLabelLists):Labels=>({quality:new Map(l.quality.map(x=>[x.value,x.label])),inventoryType:new Map(l.inventoryType.map(x=>[x.value,x.label])),bonding:new Map(l.bonding.map(x=>[x.value,x.label])),trigger:new Map(l.trigger.map(x=>[x.value,x.label]))});
// Quality ink is presentation only, keyed by the source value; the quality is always written next to it.
const qualityClass=(value:string|undefined,labels:Labels|null)=>value!==undefined&&labels?.quality.has(value)?`iv-q${value}`:'iv-qx';
const baseValue=(item:ItemSummary,s:ItemsSnapshot,table:string,name:string)=>{const source=table===ITEM_TABLE?s.itemSource:s.sparseSource,index=source.columns.findIndex(c=>c.name===name);return item.baseValues.find(v=>v.tableId===table&&v.columnIndex===index)?.value;};
const sparseAbsence=(s:ItemsSnapshot)=>s.sparseSource.coverage.scope==='complete'?'missing':'unknown';
const copper=(raw:string)=>{if(!/^\d+$/.test(raw))return raw;const c=BigInt(raw),g=c/10000n,s=c%10000n/100n,k=c%100n;return [g?`${g} g`:'',g||s?`${s} s`:'',`${k} c`].filter(Boolean).join(' ');};

/* ---------- Source links and roving sections ---------- */
type Open=(table:string,recordId:string,columnIndex?:number)=>void;
function SourceLink({value,label,open}:{value:Pick<ItemFactValue,'table'|'tableId'|'recordId'|'columnIndex'|'column'>;label?:string;open?:Open}){
 if(!open)return <span className="iv-nosrc">{label??`${value.table}.${value.column}`}</span>;
 return <button type="button" className="iv-src" aria-describedby="iv-rove-hint" data-iv-key={`src|${value.tableId}|${value.recordId}|${value.columnIndex}|${label??''}`} onClick={e=>{markCurrent(e.currentTarget);open(value.tableId,value.recordId,value.columnIndex);}}>{label??`${value.table}.${value.column}`}<span className="iv-visually-hidden">, open {value.table} record {value.recordId} in Tables</span></button>;
}
// Each [data-rove] group is one Tab stop: its current source link (or its first) takes Tab, arrow keys and Home/End move between its links.
// Links inside a closed disclosure are skipped.
const reachable=(g:Element)=>[...g.querySelectorAll<HTMLElement>('.iv-src')].filter(l=>l.closest('[data-rove]')===g&&!l.closest('details:not([open]),[hidden]'));
// A clicked or focused link becomes its section's one Tab stop; its peers lose the mark.
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

/* ---------- Facts ---------- */
const factOf=(f:ItemFactsResult|null,key:string)=>f?.facts?.find(x=>x.key===key);
const SPARSE_WORDS={missing:['Missing','no ItemSparse row'],unknown:['Unknown','not in readable ItemSparse rows']} as const;
function stateValue(fact:ItemFact|undefined):ReactNode|null{
 if(!fact)return <><span className="iv-nv">Unknown</span> <span className="iv-fsub">· not projected</span></>;
 if(fact.state==='missing'||fact.state==='unknown'){const [word,why]=SPARSE_WORDS[fact.state];return <><span className="iv-nv">{word}</span> <span className="iv-fsub">· {why}</span></>;}
 if(fact.state==='unsupported')return <><span className="iv-nv">Unsupported</span> <span className="iv-fsub">· not in this layout</span></>;
 if(fact.state==='invalidText')return <><span className="iv-nv">Text unavailable</span> <span className="iv-fsub">· Invalid UTF-8</span></>;
 return null;
}
const plain=(v:ItemFactValue)=>v.value===''?<span className="iv-fsub">Empty string</span>:v.value;
const withLabel=(v:ItemFactValue)=><>{plain(v)} <span className="iv-fsub">· {v.label??'no reviewed label'}</span></>;
function Fact({label,fact,open,render=v=>plain(v[0]),note,extraSources}:{label:string;fact:ItemFact|undefined;open?:Open;render?:(values:ItemFactValue[])=>ReactNode;note?:ReactNode;extraSources?:ReactNode}){
 const word=stateValue(fact),values=fact?.values??[];
 return <div className="iv-fact"><div className="iv-fhead"><span className="iv-fl">{label}</span>{(values.length>0||extraSources)&&<span className="iv-srcs">{values.map(v=><SourceLink key={`${v.tableId}:${v.columnIndex}`} value={v} open={open}/>)}{extraSources}</span>}</div><span className="iv-fv iv-selectable">{word??render(values)}</span>{note&&<span className="iv-fnote">{note}</span>}</div>;
}
// Item and ItemSparse can both carry a field. Agreeing copies are one fact with both links; disagreeing copies, or copies stored
// differently (Classic Item.DamageType[0–4] beside ItemSparse.DamageType), are one fact per table. Neither is picked.
const byTable=(values:readonly ItemFactValue[])=>[ITEM_TABLE,SPARSE_TABLE].map(t=>values.filter(v=>v.tableId===t)).filter(g=>g.length>0);
export function SharedFact({label,fact,open,render}:{label:string;fact:ItemFact|undefined;open?:Open;render:(v:ItemFactValue)=>ReactNode}){
 const shown=(group:readonly ItemFactValue[])=>group.length===1?render(group[0]!):`[${group.map(v=>v.value).join(', ')}]`;
 if(!fact||fact.state!=='value'||fact.agreement==='single'||fact.agreement==='agree')
  return <Fact label={label} fact={fact} open={open} render={v=>shown(fact?.agreement==='agree'?byTable(v)[0]!:v)} note={fact?.state==='value'&&fact.values.every(v=>v.tableId===ITEM_TABLE)?'From Item only.':undefined}/>;
 const groups=byTable(fact.values);
 return <>{groups.map((g,i)=><div className="iv-fact" key={g[0]!.tableId}><div className="iv-fhead"><span className="iv-fl">{label} · {g[0]!.table}</span><span className="iv-srcs">{g.map(v=><SourceLink key={v.columnIndex} value={v} open={open}/>)}</span></div><span className="iv-fv iv-selectable">{shown(g)}</span>{i===groups.length-1&&<span className="iv-fnote">{fact.agreement==='disagree'?<><span className="iv-conflict">Item and ItemSparse disagree.</span> Both values are kept; neither is picked.</>:'Item and ItemSparse store this field differently. Both are kept as read.'}</span>}</div>)}</>;
}

/* ---------- Browser ---------- */
export function ItemsBrowser({effectsLoading,effectsAvailable=false,effectsSource,workspace,bridge,active,flow,focusRequest=0,onSource,classificationsLoading,classificationsAvailable=false,subclassAvailable=false,iconsLoading,iconsAvailable=false,factsAvailable=false,onOpenTable}:{onOpenTable?:(tableId:string,name:string)=>void;effectsLoading?:ItemEffectsLoading;effectsAvailable?:boolean;effectsSource?:EffectsSourceNavigation;workspace:WorkspaceSnapshot;bridge:DiscoveryBridge;active:boolean;flow:ItemsLoading;focusRequest?:number;onSource?:(snapshot:TableDataSnapshot,recordId:string,columnIndex?:number)=>void;classificationsLoading?:Classifications;classificationsAvailable?:boolean;subclassAvailable?:boolean;iconsLoading?:IconsLoading;iconsAvailable?:boolean;factsAvailable?:boolean}){
 const context=itemsContext(workspace),key=itemsKey(context),intent={context,buildVersion:workspace.build.version};
 const {compact,statusSlot}=useShell(),covered=useCoveredSide();
 const [snapshot,setSnapshot]=useState<ItemsSnapshot|null>(null),[readinessInfo,setReadinessInfo]=useState<ItemsReadiness|null>(null),[page,setPage]=useState<ItemsPage|null>(null),[mode,setMode]=useState<'name'|'id'>('name'),[draft,setDraft]=useState(''),[inputError,setInputError]=useState(''),[error,setError]=useState<TableFailure|null>(null),[checked,setChecked]=useState(false),[checking,setChecking]=useState(false),[needsPreparation,setNeedsPreparation]=useState(false),[busy,setBusy]=useState(false),[history,setHistory]=useState<(string|undefined)[]>([]),[cursor,setCursor]=useState<string|undefined>(),[selected,setSelected]=useState<ItemSummary|null>(null),[detail,setDetail]=useState<ItemDetail|null>(null),[facts,setFacts]=useState<ItemFactsResult|null>(null),[factsError,setFactsError]=useState<TableFailure|null>(null),[detailBusy,setDetailBusy]=useState(false),[detailError,setDetailError]=useState<TableFailure|null>(null),[notice,setNotice]=useState(''),[pane,setPane]=useState<'list'|'detail'>('list'),[labels,setLabels]=useState<Labels|null>(null);
 const alive=useRef(true),activated=useRef(false),readinessGeneration=useRef(0),pageGeneration=useRef(0),detailGeneration=useRef(0),observed=useRef<string|null>(null),seen=useRef<string|null>(null),current=useRef(snapshot),currentPage=useRef(page),last=useRef<{params:ItemsPageParams;movement:'first'|'next'|'previous'|'stay'}|null>(null),heading=useRef<HTMLHeadingElement>(null),detailHeading=useRef<HTMLHeadingElement>(null),input=useRef<HTMLInputElement>(null),listbox=useRef<HTMLDivElement>(null),listScroll=useRef<HTMLDivElement>(null),detailRoot=useRef<HTMLElement>(null),retryOrigin=useRef<HTMLElement|null>(null),retryFocus=useRef(false),wasActive=useRef(false),detailFocus=useRef(false);
 const browser=useRef<HTMLElement>(null),iconView=useMemo(()=>iconsAvailable&&snapshot&&page?`icon_view_${crypto.randomUUID()}`:'',[iconsAvailable,snapshot?.artifactId,page?.query,page?.exactItemId,cursor]);
 const iconSlots=useVisibleIconSlots(browser,active&&iconsAvailable,iconView,page?.entries.map(i=>i.id).join(',')??'',`${selected?.id}:${pane}:${compact}`),iconIds=[...new Set(iconSlots.map(slot=>slot.split(':')[1]))],iconIdsKey=iconIds.join(',');
 useEffect(()=>{if(!active||!iconsAvailable||!iconView)return;return()=>iconsLoading?.hide(iconView);},[active,iconsAvailable,iconView]);
 useEffect(()=>{if(active&&iconsAvailable&&iconView&&snapshot)iconsLoading?.show({viewId:iconView,context,artifactId:snapshot.artifactId,itemIds:iconIds,buildVersion:workspace.build.version});},[active,iconsAvailable,iconView,iconIdsKey]);
 // Class and Subclass labels are asked for the selected item only; the list shows name, quality and level.
 const classificationView=useMemo(()=>classificationsAvailable&&snapshot&&selected?`classification_view_${crypto.randomUUID()}`:'',[classificationsAvailable,subclassAvailable,snapshot?.artifactId,selected?.id]);
 useEffect(()=>{if(!active||!classificationView)return;return()=>classificationsLoading?.hide(classificationView);},[active,classificationView]);
 useEffect(()=>{if(active&&classificationView&&snapshot&&selected)classificationsLoading?.show({viewId:classificationView,snapshot,summaries:[selected],includeSubclass:subclassAvailable,buildVersion:workspace.build.version});},[active,classificationView]);
 current.current=snapshot;currentPage.current=page;
 const op=flow.operation,own=!!op&&itemsKey(op.intent.context)===key,blocked=flow.blocked(context),waiting=active&&checked&&needsPreparation&&!checking&&!error&&!blocked&&flow.pending&&!own;
 useEffect(()=>{alive.current=true;return()=>{alive.current=false;readinessGeneration.current++;pageGeneration.current++;detailGeneration.current++;};},[]);
 useEffect(()=>{if(!factsAvailable||!bridge.itemLabels)return;let live=true;void bridge.itemLabels().then(reply=>{if(live&&alive.current&&reply.ok)setLabels(labelMaps(reply.value));}).catch(()=>{/* Labels stay off; numbers still show. */});return()=>{live=false;};},[bridge,factsAvailable]);
 useLayoutEffect(()=>{if(active&&(focusRequest>0||!wasActive.current))heading.current?.focus({preventScroll:true});wasActive.current=active;},[active,focusRequest]);
 useLayoutEffect(()=>{if(active&&detailFocus.current&&(!compact||pane==='detail')){detailFocus.current=false;detailHeading.current?.focus({preventScroll:true});}},[active,pane,selected]);
 useLayoutEffect(()=>{if(!active){retryFocus.current=false;return;}if(retryFocus.current&&!error){retryFocus.current=false;if(document.activeElement===document.body||document.activeElement===retryOrigin.current)(input.current??heading.current)?.focus({preventScroll:true});}},[page,error,active]);
 useLayoutEffect(()=>{rove(detailRoot.current);});
 // The area is hidden while Tables or another area shows, which drops the list scroller's offset: put it back on return.
 const listTop=useRef(0);
 // After Retry Items preparation its button is gone: focus moves to the Preparing status, or to the search once the list is ready.
 // Only when focus was left on the retired button or fell to the page; a deliberate move elsewhere stays.
 const iconControlsShown=useRef<string|null>(null);
 const prepStatus=useRef<HTMLDivElement|null>(null),prepRetryFocus=useRef<HTMLElement|null>(null),prepStatusLeft=useRef(false);
 // When the Preparing status that holds focus goes away (the list is ready, or the request stopped or failed), focus moves to the
 // search, or to the Items heading while the search is still disabled, never to the page.
 const prepStatusRef=useCallback((node:HTMLDivElement|null)=>{const old=prepStatus.current;prepStatus.current=node;if(!node&&old&&(document.activeElement===old||document.activeElement===document.body))prepStatusLeft.current=true;},[]);
 useLayoutEffect(()=>{const origin=prepRetryFocus.current;if(!origin||!active||origin.isConnected&&document.activeElement===origin)return;
  const target=prepStatus.current??(snapshot&&!needsPreparation?input.current:null);if(!target)return;prepRetryFocus.current=null;
  const now=document.activeElement;if(!now||now===document.body||now===origin)target.focus({preventScroll:true});});
 useLayoutEffect(()=>{if(!prepStatusLeft.current)return;prepStatusLeft.current=false;if(prepStatus.current||!active)return;
  const now=document.activeElement;if(now&&now!==document.body)return;(input.current&&!input.current.disabled?input.current:heading.current)?.focus({preventScroll:true});});
 // Ctrl+F focuses the Items search, as on Tables and Files; in the one-pane layout it shows the list first.
 useEffect(()=>{if(!active)return;const key=(event:KeyboardEvent)=>{if((event.ctrlKey||event.metaKey)&&!event.altKey&&event.key.toLowerCase()==='f'&&!input.current?.closest('[inert]')){event.preventDefault();showPane('search');}};window.addEventListener('keydown',key);return()=>window.removeEventListener('keydown',key);},[active]);
 useLayoutEffect(()=>{const l=listScroll.current;if(active&&l&&Math.abs(l.scrollTop-listTop.current)>1)l.scrollTop=listTop.current;},[active]);
 useEffect(()=>{if(active&&!activated.current){activated.current=true;void readiness();}},[active]);
 useEffect(()=>{if(!op||itemsKey(op.intent.context)!==key)return;if(['pending','stopping'].includes(op.status))observed.current=op.id;if(op.result&&observed.current===op.id&&seen.current!==op.id){seen.current=op.id;accept(op.result);}},[flow.operation]);
 useEffect(()=>{if(active&&checked&&needsPreparation&&!checking&&!error&&!blocked&&!flow.pending&&!(own&&op?.result&&observed.current===op.id)){const id=flow.load(intent);if(id)observed.current=id;}},[active,checked,needsPreparation,checking,error,flow.pending,flow.operation?.id,flow.latchRevision]);
 async function readiness(){const token=++readinessGeneration.current;setChecking(true);setError(null);try{const reply=await bridge.itemsData(context);if(!alive.current||token!==readinessGeneration.current)return;if(!reply.ok){setError(reply.error);flow.fail(context,reply.error);return;}if(!itemsReadinessMatches(reply.value,context))throw new Error('Exact Items readiness mismatch');setReadinessInfo(reply.value);setChecked(true);if(reply.value.snapshot)accept(reply.value.snapshot);else setNeedsPreparation(true);}catch{if(alive.current&&token===readinessGeneration.current){const problem={code:'ITEMS_READINESS_UNAVAILABLE',message:'The exact local Items readiness could not be confirmed. Previous admitted results are preserved.',retryable:true};setError(problem);flow.fail(context,problem);}}finally{if(alive.current&&token===readinessGeneration.current)setChecking(false);}}
 function accept(s:ItemsSnapshot){if(!itemsSnapshotMatches(s,context)){setError({code:'ITEMS_CONTEXT_MISMATCH',message:'The Items snapshot does not match both exact source admissions. Previous results are preserved.',retryable:false});return;}++readinessGeneration.current;setChecking(false);setNeedsPreparation(false);setChecked(true);setError(null);const previousPage=currentPage.current;const replacement=!!current.current&&current.current.artifactId!==s.artifactId;setSnapshot(s);current.current=s;if(replacement){setPage(null);currentPage.current=null;++detailGeneration.current;setSelected(null);setDetail(null);setFacts(null);setDetailError(null);setFactsError(null);setNotice('The admitted source generation changed. The previous page and selection were cleared because their artifact binding changed. Select an item again to inspect current evidence.');setHistory([]);setCursor(undefined);}const old=previousPage;const params={...context,artifactId:s.artifactId,query:old?.query??'',...(old?.exactItemId?{exactItemId:old.exactItemId}:{}),limit:50,...(!replacement&&cursor?{after:cursor}:{})};void read(s,params,replacement||!old?'first':'stay');}
 async function read(s:ItemsSnapshot,params:ItemsPageParams,movement:'first'|'next'|'previous'|'stay',retainError=false){
  const token=++pageGeneration.current;last.current={params,movement};setBusy(true);if(!retainError)setError(null);
  try{const reply=await bridge.itemsPage(params);if(!alive.current||token!==pageGeneration.current)return;if(!reply.ok){setError(reply.error);return;}if(!itemsPageMatches(reply.value,params,s)){setError({code:'ITEMS_PAGE_CONTEXT_MISMATCH',message:'The page differs from its exact Items artifact, applied search or bounded order. Previous results are preserved.',retryable:false});return;}setPage(reply.value);setCursor(params.after);if(movement==='first')setHistory([]);if(movement==='next')setHistory(old=>[...old.slice(-127),cursor]);if(movement==='previous')setHistory(old=>old.slice(0,-1));setError(null);}
  catch{if(alive.current&&token===pageGeneration.current)setError({code:'ENGINE_UNAVAILABLE',message:'The retained Items page could not be read. Previous results remain available.',retryable:true});}finally{if(alive.current&&token===pageGeneration.current)setBusy(false);}
 }
 function apply(clear=false){if(!snapshot||busy||needsPreparation)return;const value=clear?'':draft;
  if(mode==='id'&&value&&!validRecordId(value)){setInputError(ID_ERROR);return;}
  if(mode==='name'&&!validItemsQuery(value)){setInputError('Use valid Unicode text, at most 128 characters / 512 UTF-8 bytes, without NUL. The last results stay.');return;}
  setInputError('');if(clear)setDraft('');void read(snapshot,{...context,artifactId:snapshot.artifactId,query:mode==='name'?value:'',...(mode==='id'&&value?{exactItemId:value}:{}),limit:50},'first');}
 function pageMove(movement:'first'|'next'|'previous'){if(!snapshot||!page||busy||needsPreparation)return;const after=movement==='next'?page.nextCursor:movement==='previous'?history.at(-1):undefined;if(movement==='next'&&!after||movement==='previous'&&!history.length)return;void read(snapshot,{...context,artifactId:snapshot.artifactId,query:page.query,...(page.exactItemId===null?{}:{exactItemId:page.exactItemId}),limit:50,...(after?{after}:{})},movement);}
 async function inspect(item:ItemSummary,force=false){if(!snapshot)return;setSelected(item);if(!force&&selected?.id===item.id&&detail)return;setDetail(null);setFacts(null);setDetailError(null);setFactsError(null);setDetailBusy(true);const token=++detailGeneration.current,request={...context,artifactId:snapshot.artifactId,itemId:item.id};
  const factsRead=factsAvailable&&bridge.itemFacts?bridge.itemFacts(request).then(reply=>{if(!alive.current||token!==detailGeneration.current)return;if(!reply.ok){setFactsError(reply.error);return;}if(!itemFactsMatches(reply.value,request,snapshot)){setFactsError({code:'ITEM_FACTS_CONTEXT_MISMATCH',message:'The item facts differ from the exact selected item or its source columns.',retryable:false});return;}setFacts(reply.value);}).catch(()=>{if(alive.current&&token===detailGeneration.current)setFactsError({code:'ENGINE_UNAVAILABLE',message:'The item facts could not be read.',retryable:true});}):Promise.resolve();
  try{const reply=await bridge.itemInspect(request);if(!alive.current||token!==detailGeneration.current)return;if(!reply.ok){setDetailError(reply.error);return;}if(!itemInspectMatches(reply.value,request,snapshot)){setDetailError({code:'ITEMS_DETAIL_CONTEXT_MISMATCH',message:'The detail differs from the exact selected item or its original source evidence.',retryable:false});return;}if(!reply.value.detail){setDetailError({code:'ITEM_NOT_IN_ADMITTED_SCOPE',message:'This Item ID is not in the current admitted Item scope. No global absence is inferred.',retryable:false});return;}setDetail(reply.value.detail);}catch{if(alive.current&&token===detailGeneration.current)setDetailError({code:'ENGINE_UNAVAILABLE',message:'The exact retained item detail could not be read.',retryable:true});}finally{await factsRead;if(alive.current&&token===detailGeneration.current)setDetailBusy(false);}}
 // Selection follows the list's keyboard focus; a click or Enter in the one-pane layout shows the item and focuses its heading.
 function choose(item:ItemSummary,show:boolean){void inspect(item);keepRow(item.id);if(show&&compact){detailFocus.current=true;setPane('detail');}}
 function keepRow(id:string){requestAnimationFrame(()=>{const l=listScroll.current,o=document.getElementById(`iv-opt-${id}`);if(!l||!o)return;const head=l.querySelector<HTMLElement>('.iv-thead')?.offsetHeight??0,top=o.offsetTop-head,bottom=o.offsetTop+o.offsetHeight-l.clientHeight;if(l.scrollTop>top)l.scrollTop=top;else if(l.scrollTop<bottom)l.scrollTop=bottom;});}
 function listKey(e:React.KeyboardEvent){const entries=page?.entries??[];if(!entries.length)return;const i=entries.findIndex(x=>x.id===selected?.id);const t={ArrowDown:i+1,ArrowUp:i<0?0:i-1,Home:0,End:entries.length-1,PageDown:i+10,PageUp:i-10}[e.key];if(t!==undefined){e.preventDefault();choose(entries[Math.max(0,Math.min(entries.length-1,t))],false);}else if(e.key==='Enter'&&i>=0){e.preventDefault();choose(entries[i],true);}}
 // A pane switch moves focus in the commit that shows the pane, never a frame later: a deferred move could land after the reader (or
 // a keypress) had already moved focus on, and pull it back to the switch (#93).
 const [paneFocus,setPaneFocus]=useState<{target:'list'|'detail'|'search';serial:number}|null>(null);
 function showPane(target:'list'|'detail'|'search'){setPane(target==='detail'?'detail':'list');setPaneFocus(old=>({target,serial:(old?.serial??0)+1}));}
 useLayoutEffect(()=>{if(!paneFocus)return;if(paneFocus.target==='search'){input.current?.focus();input.current?.select();}else if(paneFocus.target==='list'){listbox.current?.focus({preventScroll:true});if(selected)keepRow(selected.id);}else document.querySelector<HTMLElement>('.iv-switch [data-pane="detail"]')?.focus();},[paneFocus]);
 const effectsHeading=useRef<HTMLHeadingElement>(null),effectsFocus=useRef<string|null>(null);
 const effectsIntent:EffectsIntent|null=effectsAvailable&&effectsLoading&&snapshot&&selected?{params:{...context,itemsArtifactId:snapshot.artifactId,itemId:selected.id},summary:selected,buildVersion:workspace.build.version}:null;
 const effectKey=effectsIntent?effectsKey(effectsIntent.params):null;
 useEffect(()=>{effectsLoading?.show(active?effectsIntent:null);return()=>effectsLoading?.show(null);},[active,effectKey]);
 useLayoutEffect(()=>{const request=effectsLoading?.focusRequest;if(!active||!request||!snapshot||request.intent.params.productId!==context.productId||request.intent.params.buildIdentity!==context.buildIdentity||request.intent.params.sourceRegion!==context.sourceRegion)return;if(request.intent.params.itemsArtifactId!==snapshot.artifactId){setNotice('The requested effects belong to a different Items admission. Select a current item again.');return;}effectsFocus.current=effectsKey(request.intent.params);if(selected?.id!==request.intent.params.itemId)void inspect(request.intent.summary);if(compact)setPane('detail');},[active,snapshot?.artifactId,effectsLoading?.focusRequest?.sequence]);
 useLayoutEffect(()=>{if(active&&effectsFocus.current===effectKey&&effectKey&&effectsHeading.current){effectsFocus.current=null;effectsHeading.current.scrollIntoView({block:'start'});effectsHeading.current.focus({preventScroll:true});}},[active,effectKey,effectsLoading?.focusRequest?.sequence]);
// Retry replaces its own button: focus moves to the Preparing status that takes its place, as Effects' Retry does.
 function retryItems(origin?:HTMLElement){if(origin)prepRetryFocus.current=origin;observed.current=null;flow.retry(context);setError(null);setChecked(false);void readiness();}
 const open:Open|undefined=onSource&&snapshot?(table,recordId,columnIndex)=>onSource(table===ITEM_TABLE?snapshot.itemSource:snapshot.sparseSource,recordId,columnIndex):undefined;
 const prepState=!snapshot||needsPreparation;
 const status=active&&statusSlot&&snapshot?createPortal(<span className="dh-secondary">Items · <strong>{exactCount(snapshot.counts.itemRows)}</strong> Item rows · {exactCount(snapshot.counts.matchedRows)} with ItemSparse{snapshot.sparseSource.coverage.scope==='complete'?'':' · ItemSparse Partial'}</span>,statusSlot):null;

 /* ----- List pane ----- */
 const applied=page?.exactItemId!==null&&page?.exactItemId!==undefined?<>Exact Item ID <b className="iv-mono">{page.exactItemId}</b></>:page?.query?<>Name contains <b>“{page.query}”</b></>:null;
 const preparing=<div className="iv-wait">{waiting?<div className="dh-state-box" role="status"><strong>Waiting for Items in {op!.intent.context.productId} · build {op!.intent.buildVersion}</strong><p>Another build's Items {op!.status==='stopping'?'are stopping':'are preparing'}. This build prepares when that finishes, if you stay in Items.</p><div className="dh-row"><ActivityLink taskKey={`items:${op!.id}`} label="View task"/><button className="dh-btn dh-small" onClick={()=>{flow.stopWaiting(context);heading.current?.focus({preventScroll:true});}}>Stop waiting for Items</button></div></div>
  :blocked&&!(own&&op?.status==='stopping')?<div className={`dh-state-box ${blocked.kind==='stopped'?'dh-plain':'iv-error'}`} role={blocked.kind==='stopped'?'status':'alert'}><strong>{blocked.kind==='stopped'?'Items loading stopped':'Items need attention'}</strong>{blocked.error&&(blocked.error.code==='CANCELED'?<p>Stopped.</p>:<p><code>{blocked.error.code}</code> {blocked.error.message}</p>)}<p>Verified source content is kept.</p>{!(own&&flow.pending)&&blocked.error?.retryable!==false&&<div className="dh-row"><button className="dh-btn dh-small" onClick={e=>retryItems(e.currentTarget)}>Retry Items preparation</button>{blocked.kind==='failed'&&own&&op&&<ActivityLink taskKey={`items:${op.id}`} label="View task"/>}</div>}</div>
  :<div ref={prepStatusRef} tabIndex={-1} className="dh-state-box" role="status"><strong>{own&&op?.status==='stopping'?'Stopping Items preparation…':checking||!checked?'Checking retained Items…':'Preparing Items…'}</strong>{own&&op?.status==='pending'&&op.progress?.stage&&<p>{op.progress.stage}</p>}
   {readinessInfo&&<dl className="iv-prereq">{readinessInfo.dependencies.map(d=><Fragment key={d.tableId}><dt>{d.tableId===ITEM_TABLE?'Item':d.tableId===SPARSE_TABLE?'ItemSparse':d.tableId}</dt><dd>{d.state==='ready'?'Ready':'Not ready yet · needed by Items, at interactive priority'}</dd></Fragment>)}</dl>}
   {own&&flow.pending&&<div className="dh-row"><button ref={flow.cancelButton} className="dh-btn dh-small" aria-disabled={op!.status==='stopping'} onClick={()=>{if(op!.status!=='stopping')void flow.cancel(op!.id);}}>{op!.status==='stopping'?'Stopping…':'Stop preparing Items'}</button><ActivityLink taskKey={`items:${op!.id}`} label="View task"/></div>}</div>}
  <p className="dh-reason" id="iv-search-reason">Search and the list work once Item and ItemSparse are both ready. Item rows can be read in Tables now.</p></div>;
 // Once shown for this icon view the controls stay, so their status keeps focus after Retry and the list doesn't jump.
 const iconsNeed=!!iconsLoading&&!!iconView&&(iconsLoading.waiting(iconView)||iconIds.some(id=>iconsLoading.blocked(iconView,id)||iconsLoading.expired(iconView,id)||iconsLoading.get(iconView,id)?.delivery==='failed'||!!iconsLoading.get(iconView,id)?.outcome.problem?.retryable));
 if(iconsNeed)iconControlsShown.current=iconView;
 const optional=<>{active&&iconsAvailable&&iconsLoading&&(iconsNeed||iconControlsShown.current===iconView&&!!iconView||iconIds.some(id=>iconsLoading.blocked(iconView,id)||iconsLoading.expired(iconView,id)||iconsLoading.get(iconView,id)?.delivery==='failed'||!!iconsLoading.get(iconView,id)?.outcome.problem?.retryable))&&<div className="iv-optional"><IconControls flow={iconsLoading} viewId={iconView} ids={iconIds} contextKey={key}/></div>}
  {active&&classificationsAvailable&&classificationsLoading&&classificationView&&(classificationsLoading.waiting(classificationView)||classificationsLoading.canRetry(classificationView))&&<div className="iv-optional"><ClassificationControls flow={classificationsLoading} viewId={classificationView} contextKey={key} subclass={subclassAvailable}/></div>}</>;
 const rows=page?.entries??[],hidden=!!selected&&!!page&&!rows.some(r=>r.id===selected.id);
 const listPane=<section className="dh-pane iv-list" aria-labelledby="items-heading">
  <div className="dh-pane-head iv-list-head"><h1 id="items-heading" ref={heading} tabIndex={-1}>Items</h1><span className="dh-spacer"/>
   <div className="dh-segmented" role="group" aria-label="Search by">{(['name','id'] as const).map(m=><button key={m} type="button" aria-pressed={mode===m} disabled={prepState} onClick={()=>{setMode(m);setDraft('');setInputError('');requestAnimationFrame(()=>input.current?.focus());}}>{m==='name'?'Name':'Item ID'}</button>)}</div>
   <form className="iv-search" role="search" aria-label="Search Items" onSubmit={e=>{e.preventDefault();apply();}}><input ref={input} className="dh-control" type="search" value={draft} placeholder={mode==='id'?'Exact Item ID':'Name contains…'} aria-label={mode==='id'?'Exact Item ID':'Name contains'} aria-keyshortcuts="Control+F" inputMode={mode==='id'?'numeric':undefined} aria-invalid={!!inputError} aria-describedby={inputError?'iv-search-err':prepState?'iv-search-reason':undefined} disabled={prepState} onChange={e=>{++pageGeneration.current;setBusy(false);setError(null);setDraft(e.target.value);setInputError('');}}/><button className="dh-btn dh-small" type="submit" disabled={prepState} aria-disabled={busy}>Search</button></form>
  </div>
  {inputError&&<p className="iv-search-err" id="iv-search-err" role="alert">{inputError}</p>}
  {prepState?<div className="dh-pane-scroll">{preparing}{error&&!blocked&&<div className="dh-state-box iv-error" role="alert"><strong>Items need attention</strong><p><code>{error.code}</code> {error.message}</p><div className="dh-row"><button className="dh-btn dh-small" onClick={e=>retryItems(e.currentTarget)}>Retry Items preparation</button></div></div>}</div>:<>
   {snapshot.sparseSource.coverage.scope!=='complete'&&<div className="dh-notice dh-calm"><div className="dh-text"><strong>ItemSparse readable rows · Partial</strong>Encrypted sections aren't loaded. An item whose ItemSparse row isn't readable shows <b>Unknown</b>; nothing is inferred about it.</div></div>}
   {notice&&<p className="dh-notice dh-calm" role="status">{notice}</p>}
   <div className={`iv-applied${applied?'':' iv-browse'}`} role="status" aria-live="polite">{applied?<><span>{applied} · {rows.length} on this page</span><span className="dh-spacer"/><button className="dh-linkbtn" onClick={()=>apply(true)}>Clear search</button></>:<span>All admitted Items · ascending Item ID</span>}</div>
   {page&&applied&&page.query!==draft&&mode==='name'||page&&applied&&mode==='id'&&page.exactItemId!==draft?<p className="iv-draft">Showing results for the applied search. Search to apply your changes.</p>:null}
   {busy&&<p className="iv-draft" role="status">Reading Items… The previous rows stay visible.</p>}
   {error&&<div className="dh-notice iv-error" role="alert"><div className="dh-text"><strong>{error.code}</strong>{error.message}</div>{error.retryable&&last.current&&<button className="dh-btn dh-small" aria-disabled={busy} onClick={e=>{if(busy||!last.current)return;retryOrigin.current=e.currentTarget;retryFocus.current=true;void read(snapshot,last.current.params,last.current.movement,true);}}>Retry Items page</button>}<button className="dh-btn dh-small" onClick={e=>retryItems(e.currentTarget)}>Recheck Items admission</button></div>}
   {hidden&&<div className="dh-notice dh-calm"><div className="dh-text">Selected item {selected!.id} is outside these results. Its details stay open.</div></div>}
   {page&&rows.length>0&&<div className="iv-list-scroll" ref={listScroll} onScroll={e=>{listTop.current=e.currentTarget.scrollTop;}}><div className="iv-thead iv-cols" aria-hidden="true"><span/><span>Item</span><span>Level</span></div>
    <div ref={listbox} role="listbox" id="items-listbox" className="iv-listbox" tabIndex={0} aria-label="Items on this page" aria-activedescendant={rows.some(r=>r.id===selected?.id)?`iv-opt-${selected!.id}`:undefined} onKeyDown={listKey}>
     {rows.map(item=>{const on=item.id===selected?.id,named=item.name.state!=='missingSparse',absent=sparseAbsence(snapshot),quality=baseValue(item,snapshot,SPARSE_TABLE,'OverallQualityID'),level=baseValue(item,snapshot,SPARSE_TABLE,'ItemLevel'),qLabel=quality===undefined?null:labels?.quality.get(quality)??`${quality} · no reviewed label`;
      return <div key={item.id} role="option" id={`iv-opt-${item.id}`} aria-selected={on} className={`iv-cols iv-option ${named?qualityClass(quality,labels):''}${on?' iv-active':''}`} onClick={()=>{listbox.current?.focus({preventScroll:true});choose(item,true);}}>
       <span className={`iv-icon-cell${item.iconFileDataId==='0'?' iv-icon-none':''}`}>{iconsAvailable&&iconsLoading?<ItemIcon flow={iconsLoading} viewId={iconView} id={item.id} slot={`row:${item.id}`} admitted={iconSlots.includes(`row:${item.id}`)}/>:<span className="item-icon-box" aria-hidden="true"/>}</span>
       <span className="iv-name">{named?<b className={`iv-qn${item.name.state==='valid'?'':' iv-nameless'}`}>{itemName(item)}</b>:<b className="iv-qn iv-nameless">{absent==='missing'?'Name unavailable · no ItemSparse row':'Name unknown · not in readable ItemSparse rows'}</b>}
        {named?<small>{item.id}{qLabel&&` · ${qLabel}`}{level!==undefined&&<span className="iv-lvl-inline"> · level {level}</span>}</small>:<small className="iv-why">{item.id} · {absent==='missing'?'Item row only':'not in readable ItemSparse rows'}</small>}</span>
       <span className="iv-level">{named&&level!==undefined?level:<span className="iv-nv">{named?'—':absent==='missing'?'Missing':'Unknown'}</span>}</span></div>;})}
    </div></div>}
   {page&&rows.length===0&&<div className="dh-pane-scroll"><div className="iv-empty"><div className="dh-state-box dh-plain"><strong>No match</strong><p>{page.exactItemId!==null?`No Item row in this build's ${snapshot.itemSource.coverage.scope==='complete'?'admitted':'readable'} Items has ID ${page.exactItemId}.`:`No admitted item name contains “${page.query}”. Items without a readable name don't match a name search.`}</p><div className="dh-row"><button className="dh-btn dh-small" onClick={()=>apply(true)}>Clear search</button></div></div></div></div>}
   {optional}
   <div className="dh-pane-foot" role="group" aria-label="Items pages"><span className="iv-count">{page?rows.length?`${applied?'Results':'Items'} ${rows.length===1?'1':`1–${rows.length}`}${page.nextCursor?' · more after':''}`:'No results':'Reading Items…'}</span><span className="dh-spacer"/>
    <button className="dh-btn" aria-label="First page of Items" aria-disabled={busy||cursor===undefined} onClick={()=>{if(!busy&&cursor!==undefined)pageMove('first');}}>First</button><button className="dh-btn" aria-label="Previous page of Items" aria-disabled={busy||!history.length} onClick={()=>{if(!busy&&history.length)pageMove('previous');}}>Previous</button><button className="dh-btn" aria-label="Next page of Items" aria-disabled={busy||!page?.nextCursor} onClick={()=>{if(!busy&&page?.nextCursor)pageMove('next');}}>Next</button></div>
  </>}
 </section>;

 /* ----- Detail pane ----- */
 const detailPane=<section ref={detailRoot} className="dh-pane iv-detail" aria-labelledby="iv-detail-title" inert={covered||undefined} onKeyDown={onRoveKey}>
  {!snapshot||needsPreparation?<div className="iv-placeholder"><div><h2 id="iv-detail-title" ref={detailHeading} tabIndex={-1}>No item selected</h2><p>Item details appear when Items are ready. Raw Item rows can be read in Tables now.</p>{onOpenTable&&<button type="button" className="dh-btn dh-small iv-open-table" onClick={()=>onOpenTable(ITEM_TABLE,'Item')}>Open Item in Tables</button>}</div></div>
  :!selected?<div className="iv-placeholder"><div><h2 id="iv-detail-title" ref={detailHeading} tabIndex={-1}>No item selected</h2><p>Choose an item to see its details, stats and effects.</p></div></div>
  :<ItemDetailView snapshot={snapshot} item={selected} detail={detail} facts={facts} factsAvailable={factsAvailable} factsError={factsError} detailError={detailError} busy={detailBusy} labels={labels} open={open} compact={compact} headingRef={detailHeading}
     onRetry={()=>void inspect(selected,true)}
     icon={iconsAvailable&&iconsLoading?<ItemIcon flow={iconsLoading} viewId={iconView} id={selected.id} slot={`detail:${selected.id}`} admitted={iconSlots.includes(`detail:${selected.id}`)}/>:<span className="item-icon-box" aria-hidden="true"/>}
     iconText={iconsAvailable&&iconsLoading?iconLine(iconsLoading,iconView,selected.id):null}
     classification={classificationsAvailable&&classificationsLoading&&classificationView?classificationsLoading.get(classificationView,selected.id):undefined} classificationPending={!!classificationsLoading&&!!classificationView&&classificationsLoading.pending(classificationView,selected.id)} classificationBlocked={classificationsLoading&&classificationView&&classificationsLoading.blocked(classificationView,selected.id)?classificationsLoading.problem(classificationView,selected.id)?'failed':'stopped':null} classificationsAvailable={classificationsAvailable} subclassAvailable={subclassAvailable} onClassSource={onSource}
     effects={effectsIntent&&effectsLoading?<ItemEffectsPanel flow={effectsLoading} intent={effectsIntent} active={active} onSource={effectsSource} headingRef={effectsHeading} triggerLabels={labels?.trigger}/>:null}/>}
 </section>;
 const sel=selected?(selected.name.state==='valid'?selected.name.value:`Item ${selected.id}`):null;
 return <section ref={browser} className="iv" hidden={!active} aria-label="Items">
  <div className="dh-pane-switch iv-switch"><div className="dh-segmented" role="group" aria-label="Show pane"><button type="button" data-pane="list" aria-pressed={pane==='list'} onClick={()=>showPane('list')}>Items list</button><button type="button" data-pane="detail" aria-pressed={pane==='detail'} onClick={()=>showPane('detail')}>Selected item</button></div>{sel&&<span className="dh-reason iv-switch-name">{sel}</span>}</div>
  <div className="iv-panes" data-pane={pane}>{listPane}{detailPane}</div>{status}
 </section>;
}
// The head's icon line names the outcome and, for a typed problem, its code; a failed image delivery keeps its ready source.
function iconLine(flow:IconsLoading,viewId:string,id:string):string{const value=flow.get(viewId,id),failure=flow.problem(viewId,id);if(!value)return flow.pendingFor(viewId,id)?'Icon loading':flow.blocked(viewId,id)?failure?`Icon load failed · ${failure.code}`:'Icon loading stopped':'Icon not loaded';if(value.delivery==='failed')return 'Icon image delivery failed · the prepared source stays ready';if(value.outcome.status==='ready')return value.delivery==='loading'?'Icon loading':'Icon';return `${iconStatus(value.outcome.status)}${value.outcome.problem?` · ${value.outcome.problem.code}`:''}`;}

/* ---------- Detail ---------- */
// A `|` in the head's subline hides when it would end or start a line, like the area separator (D086).
function useLineSeparators(){
 const ref=useRef<HTMLDivElement>(null);
 useLayoutEffect(()=>{const line=ref.current;if(!line)return;
  const box=(e:Element)=>e.getBoundingClientRect(),same=(a:Element,b:Element)=>box(a).top<box(b).bottom&&box(b).top<box(a).bottom;
  const fix=()=>{const seps=[...line.querySelectorAll<HTMLElement>('.iv-sep')];for(const s of seps)s.classList.remove('iv-dangling');for(const s of seps){const a=s.previousElementSibling,b=s.nextElementSibling;if(!box(s).height)continue;if(!a||!b||!same(s,a)||!same(s,b))s.classList.add('iv-dangling');}};
  fix();const observer=typeof ResizeObserver==='undefined'?null:new ResizeObserver(fix);observer?.observe(line);return()=>observer?.disconnect();});
 return ref;
}
export function ItemDetailView({snapshot,item,detail,facts,factsAvailable,factsError,detailError,busy,labels,open,compact,headingRef,onRetry,icon,iconText,classification,classificationPending,classificationBlocked,classificationsAvailable,subclassAvailable,onClassSource,effects}:{snapshot:ItemsSnapshot;item:ItemSummary;detail:ItemDetail|null;facts:ItemFactsResult|null;factsAvailable:boolean;factsError:TableFailure|null;detailError:TableFailure|null;busy:boolean;labels:Labels|null;open?:Open;compact:boolean;headingRef:React.RefObject<HTMLHeadingElement|null>;onRetry:()=>void;icon:ReactNode;iconText:string|null;classification?:ReturnType<Classifications['get']>;classificationPending:boolean;classificationBlocked:'stopped'|'failed'|null;classificationsAvailable:boolean;subclassAvailable:boolean;onClassSource?:(snapshot:TableDataSnapshot,recordId:string,columnIndex?:number)=>void;effects:ReactNode}){
 const sparse=facts?.sparse??(detail?detail.sparseRow?'present':sparseAbsence(snapshot):null),named=sparse==='present'||sparse===null&&item.name.state!=='missingSparse';
 const f=(k:string)=>factOf(facts,k),first=(k:string)=>f(k)?.state==='value'?f(k)!.values[0]:undefined;
 const quality=first('quality')?.value??baseValue(item,snapshot,SPARSE_TABLE,'OverallQualityID'),level=first('itemLevel')?.value??baseValue(item,snapshot,SPARSE_TABLE,'ItemLevel');
 const invValue=f('inventoryType')?.values.at(-1)?.value??baseValue(item,snapshot,SPARSE_TABLE,'InventoryType'),bond=first('bonding');
 const lbl=(m:keyof Labels,v:string|undefined)=>v===undefined?undefined:labels?.[m].get(v);
 const name=named?item.name.state==='valid'?item.name.value:itemName(item):sparse==='unknown'?'Name unknown':'Name unavailable';
 const sep=<span className="iv-sep" aria-hidden="true">|</span>,lineRef=useLineSeparators();
 // Effects moves between columns when the layout switches (two columns, or one with Effects first). It renders into one persistent
 // host that each layout's slot adopts, so its open evidence, reading-region scroll and focus survive a text-size or window change.
 const effectsHost=useMemo(()=>document.createElement('div'),[]),effectsSlot=useCallback((slot:HTMLDivElement|null)=>{if(slot&&effectsHost.parentElement!==slot)slot.appendChild(effectsHost);},[effectsHost]);
 const desc=f('description'),iconFact=f('icon'),iconValue=iconFact?.state==='value'?iconFact.values[0]:undefined;
 // The record buttons come from the selected item ID and the facts' ItemSparse state, so a late inspect never grows the head.
 const record=(table:string,id:string,label:string)=>open?<button type="button" className="dh-btn dh-small" data-iv-key={`record|${table}|${id}`} onClick={()=>open(table,id)}>{label}<span className="iv-visually-hidden">, open {table===ITEM_TABLE?'Item':'ItemSparse'} record {id} in Tables</span></button>:null;
 const head=<div className={`iv-dhead ${named?qualityClass(quality,labels):''}`}>
  <span className={`iv-icon-cell${(iconValue?.value??item.iconFileDataId)==='0'?' iv-icon-none':''}`}>{icon}</span>
  <div className="iv-dtitle"><h2 id="iv-detail-title" ref={headingRef} tabIndex={-1} className="iv-selectable"><span className={`iv-qn${named?'':' iv-nameless'}`}>{name}</span></h2>
   <div className="iv-dline" ref={lineRef}><span className="iv-mono iv-selectable">Item {item.id}</span>{named?<>{sep}<span className="iv-qn iv-strong">{quality===undefined?'Quality not supplied':lbl('quality',quality)??`${quality} · no reviewed label`}</span>{level!==undefined&&<>{sep}<span>Item level <b>{level}</b></span></>}{invValue!==undefined&&<>{sep}<span>{lbl('inventoryType',invValue)??`Inventory type ${invValue}`}</span></>}{bond&&<>{sep}<span>{lbl('bonding',bond.value)??`Bonding ${bond.value}`}</span></>}</>
    :<>{sep}<span>{sparse==='unknown'?'ItemSparse row not readable: name, quality and level unknown':'Item row only: no ItemSparse row, so no name, quality or level'}</span></>}</div></div>
  <div className="iv-drec">{record(ITEM_TABLE,detail?.itemRow.id??item.id,`Item #${detail?.itemRow.id??item.id}`)}{(detail?!!detail.sparseRow:facts?.sparse==='present')&&record(SPARSE_TABLE,detail?.sparseRow?.id??item.id,`ItemSparse #${detail?.sparseRow?.id??item.id}`)}</div>
  <div className="iv-ddesc" data-rove="" role="group" aria-label="Description and icon sources">
   {desc&&named&&<p className="iv-desc iv-selectable">{desc.state==='value'&&desc.values[0]?.value!==''?<q>{desc.values[0]?.value}</q>:desc.state==='value'?<span className="iv-dim">Empty description · the source text is an empty string</span>:stateValue(desc)}{desc.values[0]&&<> <SourceLink value={desc.values[0]} label="Description_lang" open={open}/></>}</p>}
   {(iconValue||iconText)&&<p className="iv-desc iv-dim">{iconValue?.value==='0'?'Item.IconFileDataID is 0. An icon from the item\'s appearance isn\'t looked up here.':`${iconText??'Icon'}${iconValue?` · FileDataID ${iconValue.value}`:''}`}{iconValue&&<> <SourceLink value={iconValue} label="Item.IconFileDataID" open={open}/></>}</p>}
  </div></div>;
 if(detailError)return <>{head}<div className="iv-dbody"><div className="dh-state-box iv-error iv-pad" role="alert"><strong>Item details need attention</strong><p><code>{detailError.code}</code> {detailError.message}</p>{detailError.retryable&&<div className="dh-row"><button className="dh-btn dh-small" onClick={onRetry}>Retry item details</button></div>}</div></div></>;
 if(busy&&!facts)return <>{head}<div className="iv-dbody"><p className="iv-pad iv-dim" role="status">Reading the item's source values…</p></div></>;
 if(!factsAvailable||factsError||!facts)return <>{head}<div className="iv-dbody"><div className="dh-state-box dh-plain iv-pad" role={factsError?'alert':undefined}><strong>{factsError?'Item facts need attention':'Item facts aren\'t available from this engine'}</strong><p>{factsError?<><code>{factsError.code}</code> {factsError.message}</>:<>It doesn't provide the item facts capability. <code>ITEM_FACTS_UNAVAILABLE</code></>} The Item and ItemSparse records can still be read in Tables.</p>{factsError?.retryable&&<div className="dh-row"><button className="dh-btn dh-small" onClick={onRetry}>Retry item details</button></div>}</div></div></>;
 // others: the box sits under a value Item supplied, so it speaks of the section's other values.
 const absentBox=(others:boolean)=><div className="dh-state-box dh-plain iv-sec-state"><strong>{sparse==='missing'?'Missing':'Unknown'}</strong><p>{sparse==='missing'?`${others?'The other values':'These values'} come from ItemSparse, which has no row ${item.id} in its complete sections.`:`${others?'The other values come from ItemSparse. ':''}Row ${item.id} isn't in the readable ItemSparse sections. It may be in an encrypted section; nothing is inferred.`}</p></div>;
 const whole=sparse!=='present'?absentBox(false):null;
 const section=(id:string,title:string,scope:string,body:ReactNode,extra?:ReactNode)=><section className="iv-sec" aria-labelledby={id} data-rove=""><div className="iv-sec-head"><h3 id={id}>{title}</h3><span className="iv-scope">{scope}</span></div>{body}{extra}</section>;
 // Class and Subclass: the number is the Item key; the label comes from ItemClass / ItemSubClass through the optional label service.
 const kindLabel=(kind:'class'|'subclass')=>{if(!classificationsAvailable)return null;if(kind==='subclass'&&!subclassAvailable)return <span className="iv-fsub">· Subclass labels aren't available from this engine</span>;const value=classification?.outcome[kind];
  if(classification?.staleKinds.includes(kind))return <span className="iv-fsub">· {classificationPending?'Rechecking source label…':'Source changed · recheck labels'}</span>;
  if(!value){if(classificationPending)return <span className="iv-fsub"><span className="iv-ld">…</span> {kind==='class'?'ItemClass':'ItemSubClass'} loading</span>;if(classificationBlocked)return <span className="iv-fsub">· {classificationBlocked==='failed'?'Source label unavailable':'Label loading stopped'}</span>;return null;}
  if(value.state==='ready')return <span className="iv-fsub">· {value.text!.trim()?value.text:'Whitespace-only source label'}</span>;return <span className="iv-fsub">· {classificationState(value.state)}</span>;};
 const kindLink=(kind:'class'|'subclass')=>{const value=classification?.outcome[kind],source=classification?.sources.find(s=>s.kind===kind);if(!value?.target||!source?.snapshot||!onClassSource)return null;const s=source.snapshot,target=value.target;return <button type="button" className="iv-src" aria-describedby="iv-rove-hint" data-iv-key={`label|${kind}|${target.rowId}|${target.columnIndex}`} onClick={e=>{markCurrent(e.currentTarget);onClassSource(s,target.rowId,target.columnIndex);}}>{kind==='class'?'ItemClass':'ItemSubClass'}.{s.columns[target.columnIndex].name}<span className="iv-visually-hidden">, open {kind==='class'?'ItemClass':'ItemSubClass'} record {target.rowId} in Tables</span></button>;};
 const levelInputs=<details className="iv-more"><summary>Item level inputs · raw</summary><p className="iv-sec-note">Inputs a level calculation would need. Nothing is calculated from them here.</p>
  {[['ItemSquishEraID','itemSquishEra'],['ContentTuningID','contentTuning'],['PlayerLevelToItemLevelCurveID','playerLevelCurve'],['ItemLevelOffsetCurveID','itemLevelOffsetCurve'],['ItemLevelOffsetItemLevel','itemLevelOffset']].map(([label,k])=><SharedFact key={k} label={label} fact={f(k)} open={open} render={plain}/>)}</details>;
 const secItem=section('iv-h-item','Item','Classification',<>
  <Fact label="Quality" fact={f('quality')} open={open} render={v=>withLabel(v[0])}/>
  <Fact label="Item level" fact={f('itemLevel')} open={open} render={v=><>{v[0].value} <span className="iv-fsub">· base, before bonus lists and scaling</span></>}/>
  <Fact label="Class" fact={f('class')} open={open} render={v=><>{v[0].value} {kindLabel('class')}</>} extraSources={kindLink('class')}/>
  <Fact label="Subclass" fact={f('subclass')} open={open} render={v=><>{v[0].value} {kindLabel('subclass')}</>} extraSources={kindLink('subclass')}/>
  <SharedFact label="Inventory type" fact={f('inventoryType')} open={open} render={withLabel}/>
  {levelInputs}
  <p className="iv-legend">The number is the source value. Text after · is ItemClass or ItemSubClass text, or VibeMiner's reviewed label list.</p></>);
 const rank=f('requiredSkillRank'),rep=f('minReputation');
 // A mask field is an array (retail AllowableRaces[0–1], Classic 1.15.9 AllowableRace[0–1]) or, in some layouts, one column; every element is shown as read.
 const masks=(v:ItemFactValue[])=>v.length===1?<>{v[0]!.value} <span className="iv-fsub">· raw mask</span></>:<>[{v.map(x=>x.value).join(', ')}] <span className="iv-fsub">· {v.length} raw masks</span></>;
 // Classic stores RequiredLevel in Item as well, so the scope names both tables when Item supplies it, and the Item value
 // still shows when the ItemSparse row is missing or unreadable.
 const reqLevel=f('requiredLevel'),reqFromItem=reqLevel?.state==='value'&&reqLevel.values.some(v=>v.tableId===ITEM_TABLE);
 const secReq=section('iv-h-req','Requirements',reqFromItem?'Item and ItemSparse':'ItemSparse',whole?<>{reqFromItem&&<SharedFact label="Required level" fact={reqLevel} open={open} render={plain}/>}{absentBox(reqFromItem)}</>:<>
  <SharedFact label="Required level" fact={reqLevel} open={open} render={plain}/>
  <Fact label="Required skill" fact={f('requiredSkill')} open={open} render={v=><>{v[0].value} <span className="iv-fsub">· rank {rank?.state==='value'?rank.values[0]?.value:'not supplied'}</span></>} extraSources={rank?.values.map(v=><SourceLink key={v.columnIndex} value={v} open={open}/>)}/>
  <Fact label="Required ability" fact={f('requiredAbility')} open={open}/>
  <Fact label="Faction and reputation" fact={f('minFaction')} open={open} render={v=><>{v[0].value} <span className="iv-fsub">· minimum reputation {rep?.state==='value'?rep.values[0]?.value:'not supplied'}</span></>} extraSources={rep?.values.map(v=><SourceLink key={v.columnIndex} value={v} open={open}/>)}/>
  <Fact label="Classes" fact={f('allowableClass')} open={open} render={masks}/>
  <Fact label="Races" fact={f('allowableRaces')} open={open} render={masks}/></>);
 const price=(v:ItemFactValue[])=><>{copper(v[0].value)} <span className="iv-fsub">· {exactCount(v[0].value)} copper</span></>;
 const secInv=section('iv-h-inv','Inventory and price','ItemSparse',whole??<>
  <Fact label="Binding" fact={f('bonding')} open={open} render={v=>withLabel(v[0])}/>
  <Fact label="Stack size" fact={f('stackable')} open={open}/>
  <Fact label="Max count" fact={f('maxCount')} open={open}/>
  <Fact label="Container slots" fact={f('containerSlots')} open={open}/>
  <Fact label="Buy price" fact={f('buyPrice')} open={open} render={price} note="Source value, not a vendor quote."/>
  <Fact label="Sell price" fact={f('sellPrice')} open={open} render={price}/>
  <Fact label="Vendor stack" fact={f('vendorStackCount')} open={open}/>
  <Fact label="Duration in inventory" fact={f('durationInInventory')} open={open}/></>);
 // Retail stores allocation inputs (percent editor, socket share); Classic layouts store the flat amounts (StatModifier_bonusAmount).
 // Only the columns the layout has are shown, and the note says which kind of number they are.
 const statColumns=([['statType','Stat type','bonusStat','StatModifier_bonusStat'],['statAmount','Amount','bonusAmount','StatModifier_bonusAmount'],['statPercentEditor','Percent editor','PercentEditor','StatPercentEditor'],['statSocketShare','Socket share','PercentageOfSocket','StatPercentageOfSocket']] as const).map(([key,title,short,field])=>({fact:f(key),title,short,field})).filter(c=>c.fact?.state==='value'),slots=Math.max(0,...statColumns.map(c=>c.fact!.values.length));
 const stored=statColumns.some(c=>c.field==='StatModifier_bonusAmount');
 const group=(x:ItemFact|undefined,name:string)=>x?.state==='value'&&x.values.length?<SourceLink value={x.values[0]} label={`${name}[0–${x.values.length-1}]`} open={open}/>:null;
 // With no readable ItemSparse row there is no layout evidence either way, so the heading claims neither kind.
 const statTitle=whole?'Stats':stored?'Stats · stored amounts':'Stat allocation, not final stats';
 const secStats=section('iv-h-stats',statTitle,'ItemSparse',whole??(slots===0?<div className="dh-state-box dh-plain iv-sec-state"><strong>Not decoded for this layout</strong><p>This ItemSparse layout has no stat fields, so they are shown as unsupported, not as zero.</p></div>:<>
  <p className="iv-sec-note">{stored?'These are the stat amounts this layout stores for each slot, as read. Nothing is scaled or added.':'Final +N depends on item level, slot and bonuses. These are the aligned allocation inputs, as read.'}</p>
  <div className="iv-tbl" role="region" aria-label={`${stored?'Stats':'Stat allocation'}, ${slots} slots`}><table className="iv-inputs"><thead><tr><th>Slot</th>{statColumns.map(c=><th key={c.field}>{c.title}<small>{c.short}</small></th>)}</tr></thead>
   <tbody>{Array.from({length:slots},(_,i)=><tr key={i}><th scope="row">{i}</th>{statColumns.map(c=><td key={c.field}>{c.fact!.values[i]?.value??'—'}</td>)}</tr>)}</tbody></table></div>
  <div className="iv-group-src"><span>Sources:</span>{statColumns.map(c=><Fragment key={c.field}>{group(c.fact,c.field)}</Fragment>)}</div>
  <p className="iv-legend">All slots are kept as read, zeros and −1 included.</p></>));
 const sockets=f('socketType');
 const secSockets=sparse==='present'?section('iv-h-sock','Sockets','ItemSparse',<>
  {sockets?.state==='value'?<><div className="iv-tbl" role="region" aria-label="Sockets"><table className="iv-inputs"><thead><tr><th>Socket</th><th>Socket type<small>SocketType</small></th></tr></thead><tbody>{sockets.values.map((v,i)=><tr key={i}><th scope="row">{i}</th><td>{v.value}</td></tr>)}</tbody></table></div><div className="iv-group-src"><span>Source:</span>{group(sockets,'SocketType')}</div></>:<Fact label="Socket types" fact={sockets} open={open}/>}
  <Fact label="Socket bonus enchantment" fact={f('socketBonus')} open={open}/>
  <Fact label="Gem properties" fact={f('gemProperties')} open={open}/></>):null;
 const other=sparse==='present'?<section className="iv-sec iv-full" aria-labelledby="iv-h-other" data-rove=""><details className="iv-more"><summary id="iv-h-other">Other source inputs · raw</summary><p className="iv-sec-note">Supported fields without a reviewed meaning. They are shown as read and never used as filters.</p>
  {[['DamageType','damageType'],['ItemDelay','itemDelay'],['DmgVariance','dmgVariance'],['ItemRange','itemRange'],['Flags','flags'],['ZoneBound','zoneBound'],['InstanceBound','instanceBound'],['RequiredHoliday','requiredHoliday'],['PriceVariance','priceVariance'],['PriceRandomValue','priceRandomValue'],['Display1_lang','display1'],['Display2_lang','display2'],['Display3_lang','display3']].map(([label,k])=><SharedFact key={k} label={label} fact={f(k)} open={open} render={plain}/>)}</details></section>:null;
 const effectsSection=<section className="iv-sec iv-effects" aria-label="Effects" data-rove="">{effects??<><div className="iv-sec-head"><h3>Effects</h3><span className="iv-scope">ItemXItemEffect → ItemEffect → SpellName</span></div><div className="dh-state-box dh-plain iv-sec-state"><strong>Effects aren't available from this engine</strong><p>It doesn't provide the item effects capability. Effect rows can still be read in Tables. <code>ITEM_EFFECTS_UNAVAILABLE</code></p></div></>}</section>;
 const tip=<p className="iv-rove-tip" id="iv-rove-hint">Tab moves between sections; arrow keys move between a section's links.</p>;
 // Wide: two columns in reading order. One pane: one column with Effects first, so its actions are near the top.
 const effectsAt=<div ref={effectsSlot} className="iv-effects-slot"/>;
 return <>{head}<div className="iv-dbody">{tip}{compact?<div className="iv-grid">{effectsAt}{secItem}{secReq}{secInv}{secStats}{secSockets}{other}</div>
  :<div className="iv-grid iv-two"><div className="iv-col">{secItem}{secReq}{secInv}</div><div className="iv-col">{effectsAt}{secStats}{secSockets}</div>{other}</div>}</div>{createPortal(effectsSection,effectsHost)}</>;
}
