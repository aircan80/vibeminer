import {DownloadActivity,ActivityTrigger,useDownloadActivity} from './DownloadActivity';
import {stageMeasurement,summaryLine,retentionActivity,type ActivityTask,type ActivityIdentity} from './download-activity-state';
import {retentionKey} from '../shared/retention';
import {useItemEffects} from './useItemEffects';
import {ItemEffectsActivity} from './ItemEffects';
import {useCreatureDisplay} from './useCreatureDisplay';
import {CreatureDisplayActivity} from './CreatureDisplayViews';
import {useCreaturesLoading,CreaturesLoadBanner} from './useCreaturesLoading';
import {useClassifications} from './useClassifications';
import {ClassificationActivity} from './ItemClassifications';
import {useIconsLoading} from './useIconsLoading';
import {IconActivity} from './ItemIcons';
import {useItemsLoading,ItemsLoadBanner} from './useItemsLoading';
import { useRetention } from './useRetention';
import { exactBytes, exactCount } from '../shared/inventory';
import { RetentionActivity } from './Retention';
import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import './discovery.css';
import type { Build, DiscoveryCatalogPage, Product, ProductDiscoveryStatus, ProductRefreshProblem, EngineProgress, RetentionSnapshot } from '../../../../contracts/generated/discovery';
import type { BridgeReply, DiscoveryBridge, DiscoveryFilter, ReadAllParams } from '../shared/bridge';
import { historyLabels, newerRegionCdnConfigs, newestGroupSize } from '../shared/cdn-config';
import { TableLoadBanner, useTableLoading } from './useTableLoading';
import { useWorkspaceFlow, type Opening } from './useWorkspaceFlow';
import { PreparationView, HomeView } from './WorkspaceViews';
import brand from '../../../../branding/vibeminer.svg';
import { SettingsButton, SettingsProvider, SettingsView, type SettingsOrigin } from './Settings';
const stamp = (value: string | null) => value ? new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : 'Not checked yet';
const regions = (values: string[]) => values.length ? values.map(x => x.toUpperCase()).join(', ') : 'None recorded';
const short = (value: string) => `${value.slice(0, 10)}…${value.slice(-4)}`;
/** The engine refused the saved catalog (older or newer VibeMiner). Retrying or restarting does not help, and the app never offers to delete it. */
const refusedCodes = ['CATALOG_RESET_REQUIRED', 'CATALOG_VERSION'];
const refusedCatalog = (text: string) => /\((CATALOG_RESET_REQUIRED|CATALOG_VERSION)\)$/.test(text);
/**
 * The refused-catalog notice: shown in full (never a clipped scroll box) and selectable, with the catalog folder
 * as its own selectable element and the code rendered like other typed codes.
 */
function RefusedNotice({ text }: { text: string }) {
  const match = /^([\s\S]*) \(([A-Z0-9_]+)\)$/.exec(text), message = match ? match[1] : text;
  const folder = /^([\s\S]*The catalog folder is )([\s\S]+?)(\. Close VibeMiner[\s\S]*)$/.exec(message);
  return <div role="alert" className="dv-notice error refusal"><div className="dv-selectable"><strong>Saved catalog not opened</strong>
    {folder ? <>{folder[1]}<span className="dv-path dv-selectable" data-catalog-path="">{folder[2]}</span>{folder[3]}</> : message}{match && <> <code>({match[2]})</code></>}</div></div>;
}
const missing = (value: string | null | undefined) => value === null || value === undefined ? 'Not supplied' : value === '' ? '(empty value)' : value;
// Share of the build's download manifest present at the last complete presence check; null when no honest percentage exists.
export function downloadedPercent(r: RetentionSnapshot | undefined): string | null {
  const c = r?.coverage;
  if (!r || !c || c.presenceState !== 'assessed' || c.observedObjects !== r.objectCount || r.failedObjects !== '0' || r.unresolvedObjects !== '0') return null;
  const scope = BigInt(r.totalEncodedBytes), present = BigInt(c.presentBytes);
  if (scope <= 0n) return null;
  // 100% means every manifest object is stored, not just enough bytes.
  if (present >= scope) return c.presentObjects === r.objectCount ? '100%' : '99%';
  const tenths = Number(present * 1000n / scope);
  return tenths >= 10 ? `${Math.floor(tenths / 10)}%` : tenths > 0 ? `${(tenths / 10).toFixed(1)}%` : present > 0n ? '<0.1%' : '0%';
}
export type BuildDownload = { job: RetentionSnapshot; running: boolean };
const moving = (job: RetentionSnapshot) => ['queued', 'preparing', 'running'].includes(job.state);
// One build can have a saved job per source region. Show its newest complete check, and mark it running if any job runs.
export function buildDownloads(jobs: readonly RetentionSnapshot[]): Map<string, BuildDownload> {
  const downloads = new Map<string, BuildDownload>();
  const rank = (job: RetentionSnapshot) => [downloadedPercent(job) === null ? 0 : 1, job.coverage.presenceAssessedAt ?? ''] as const;
  for (const job of jobs) {
    const key = `${job.target.productId}:${job.target.buildIdentity}`, old = downloads.get(key);
    const [complete, at] = rank(job), better = !old || complete > rank(old.job)[0] || complete === rank(old.job)[0] && at > rank(old.job)[1];
    downloads.set(key, { job: better ? job : old!.job, running: moving(job) || !!old?.running });
  }
  return downloads;
}
const routeRegions = (build: Build) => Array.from(new Set(build.sourceReceipts.filter(receipt => receipt.role === 'cdns' && receipt.rowOrdinal !== null && build.sourceReceipts.some(version => version.role === 'versions' && version.region === receipt.region && version.rowOrdinal !== null)).map(receipt => receipt.region))).sort();
/** The persisted exact selection. Older versions kept one per product under the last chosen product. */
const selectionKey = 'vibeminer.discovery.selection';
function savedSelection(): string | null {
  try {
    const saved = localStorage.getItem(selectionKey);
    if (saved) return saved;
    return localStorage.getItem(`vibeminer.selection.${localStorage.getItem('vibeminer.product') || 'wow'}`);
  } catch { return null; }
}
export function App({ bridge = window.discovery }: { bridge?: DiscoveryBridge }) {
  const [products, setProducts] = useState<Product[]>([]);
  const flow = useWorkspaceFlow(bridge);
  const tableLoading = useTableLoading(bridge);
  const itemsLoading = useItemsLoading(bridge);
  const effectsLoading=useItemEffects(bridge);
  const creaturesLoading=useCreaturesLoading(bridge);
  const creatureDisplays=useCreatureDisplay(bridge,creaturesLoading);
  const iconsLoading=useIconsLoading(bridge);
  const classificationsLoading=useClassifications(bridge);
  const retention = useRetention(bridge);
  const [activityNavigation,setActivityNavigation]=useState<{area:'tables'|'items'|'creatures';sequence:number}|null>(null);
  const navigateActivity=(area:'tables'|'items'|'creatures')=>setActivityNavigation(old=>({area,sequence:(old?.sequence??0)+1}));
  const tasks:ActivityTask[]=[],taskActions=new Map<string,Map<string,()=>void>>();
  const workspace=flow.view==='home'?flow.operation?.result:null;
  const currentWorkspace=()=>!!workspace&&flow.currentWorkspaceMatches({productId:workspace.build.productId,buildIdentity:workspace.build.id,sourceRegion:workspace.sourceReference.region,generation:workspace.sourceReference.generation});
  const matches=(c:{productId:string;buildIdentity:string;sourceRegion:string})=>!!workspace&&c.productId===workspace.build.productId&&c.buildIdentity===workspace.build.id&&c.sourceRegion===workspace.sourceReference.region;
  function addTask(kind:string,id:string,title:string,identity:ActivityIdentity,status:ActivityTask['status'],progress:EngineProgress|null,error:ActivityTask['problem'],handlers:{stop?:()=>void;dismiss?:()=>void;view?:()=>void},detail?:string){
    const key=`${kind}:${id}`,pending=['pending','stopping'].includes(status),actions:ActivityTask['actions']=[...(handlers.stop&&pending?[{id:'stop',label:status==='stopping'?'Stopping…':`Stop ${title}`,disabled:status==='stopping'}]:[]),...(handlers.view?[{id:'view',label:'View research'}]:[]),...(!pending&&handlers.dismiss?[{id:'dismiss',label:'Dismiss outcome'}]:[])];
    const stage=status==='pending'?progress?.stage??`Preparing ${title}`:status==='stopping'?'Waiting for this consumer to stop safely':status==='succeeded'?'Request completed':status==='canceled'?'Request stopped':status==='blocked'?'Cleanup unconfirmed':'Needs attention';
    tasks.push({key,kind:kind==='iconsDelivery'?'presentationCleanup':'research',title,identity:{...identity,...(detail?{detail}:{})},status,stage,measurement:status==='pending'?stageMeasurement(progress):null,problem:error,actions,...(progress?.step?{details:[`${status==='pending'?'Current':'Last reported'} stage ${progress.step.index}${progress.step.total===null?' (total determined after bootstrap)':` of ${progress.step.total}`}: ${progress.step.description}`]}:{})});taskActions.set(key,new Map(Object.entries(handlers).filter((entry):entry is[string,()=>void]=>typeof entry[1]==='function')));
  }
  const opening=flow.operation;if(opening)addTask('workspace',opening.id,'Build preparation',{productId:opening.build.productId,buildIdentity:opening.build.id,buildVersion:opening.build.version,sourceRegion:opening.sourceRegion},opening.status,opening.progress,opening.error,{stop:()=>{void flow.cancel(opening.id);},view:()=>{flow.showActive(opening.id);}});
  const tableOp=tableLoading.operation;if(tableOp){const d=tableOp.definition;addTask('table',tableOp.id,`${d.entry.name??d.entry.tableId} table`,{productId:d.productId,buildIdentity:d.buildIdentity,buildVersion:d.buildVersion,sourceRegion:d.sourceRegion,researchLocale:'enUS'},tableOp.status,tableOp.progress,tableOp.error,{stop:()=>{void tableLoading.cancel(tableOp.id);},dismiss:()=>tableLoading.dismiss(tableOp.id),...(matches(d)?{view:()=>{if(currentWorkspace()&&tableLoading.view(tableOp.id))navigateActivity('tables');}}:{})},`Table ${d.entry.tableId}`);}
  const itemsOp=itemsLoading.operation;if(itemsOp)addTask('items',itemsOp.id,'Items',{...itemsOp.intent.context,buildVersion:itemsOp.intent.buildVersion},itemsOp.status,itemsOp.progress,itemsOp.error,{stop:()=>{void itemsLoading.cancel(itemsOp.id);},dismiss:()=>itemsLoading.dismiss(itemsOp.id),...(matches(itemsOp.intent.context)?{view:()=>{if(currentWorkspace()&&itemsLoading.view(itemsOp.id))navigateActivity('items');}}:{})});
  const creatureOp=creaturesLoading.operation;if(creatureOp)addTask('creatures',creatureOp.id,'Creature records',{...creatureOp.intent.context,buildVersion:creatureOp.intent.buildVersion},creatureOp.status,creatureOp.progress,creatureOp.error,{stop:()=>{void creaturesLoading.cancel(creatureOp.id);},dismiss:()=>creaturesLoading.dismiss(creatureOp.id),...(matches(creatureOp.intent.context)?{view:()=>{if(currentWorkspace()&&creaturesLoading.view(creatureOp.id))navigateActivity('creatures');}}:{})});
  for(const op of creatureDisplays.operations)addTask('display',op.id,`Creature ${op.intent.params.creatureId} slot ${op.intent.params.slot} metadata`,{...op.intent.params,buildVersion:op.intent.buildVersion},op.status,op.progress,op.error,{stop:()=>{void creatureDisplays.cancel(op.id);},dismiss:()=>creatureDisplays.dismiss(op.id,false),...(matches(op.intent.params)?{view:()=>{if(currentWorkspace()&&creatureDisplays.viewOperation(op.id))navigateActivity('creatures');}}:{})});
  const iconsOp=iconsLoading.operation;if(iconsOp)addTask(iconsOp.kind==='delivery'?'iconsDelivery':'icons',iconsOp.id,'Optional Item icons',{...iconsOp.intent.context,buildVersion:iconsOp.intent.buildVersion},iconsOp.kind==='delivery'&&iconsOp.status==='failed'?'blocked':iconsOp.status,iconsOp.progress,iconsOp.error,{...(iconsOp.kind==='prepare'?{stop:()=>{void iconsLoading.cancel(iconsOp.id);},dismiss:()=>iconsLoading.dismiss(iconsOp.id)}:{})},`Items artifact ${iconsOp.intent.artifactId} · requested Item IDs ${iconsOp.intent.itemIds.join(', ')}`);
  const labelsOp=classificationsLoading.operation;if(labelsOp)addTask('classifications',labelsOp.id,'Source classification labels',{...labelsOp.intent.snapshot.context,buildVersion:labelsOp.intent.buildVersion},labelsOp.status,labelsOp.progress,labelsOp.error,{stop:()=>{void classificationsLoading.cancel(labelsOp.id);},dismiss:()=>classificationsLoading.dismiss(labelsOp.id)},`Items artifact ${labelsOp.intent.snapshot.artifactId} · requested Item IDs ${labelsOp.params.itemIds.join(', ')}`);
  const effectsOp=effectsLoading.operation;if(effectsOp)addTask('effects',effectsOp.id,`Item ${effectsOp.intent.params.itemId} effects · ${effectsOp.phase}`,{...effectsOp.intent.params,buildVersion:effectsOp.intent.buildVersion},effectsOp.status,effectsOp.progress,effectsOp.error,{stop:()=>{void effectsLoading.stop(effectsOp.id);},dismiss:()=>effectsLoading.dismiss(effectsOp.id),...(matches(effectsOp.intent.params)?{view:()=>{if(currentWorkspace()&&effectsLoading.viewOperation(effectsOp.id))navigateActivity('items');}}:{})},`Items artifact ${effectsOp.intent.params.itemsArtifactId}`);
  for(const snapshot of retention.jobs){if(snapshot.mode!=='downloadAll'&&snapshot.state!=='stopping'&&!snapshot.problem)continue;const target=snapshot.target,key=retentionKey(target),task=retentionActivity(snapshot,retention.busy!==null||retention.recovering.includes(key));tasks.push(task);const expected={jobId:snapshot.jobId,revision:snapshot.revision};taskActions.set(task.key,new Map([['pause',()=>{void retention.command(target,'pause',false,expected);}],['resume',()=>{void retention.command(target,'resume',false,expected);}],['onDemand',()=>{void retention.command(target,'onDemand',false,expected);}],['savedPolicy',()=>{void retention.retrySavedPolicy(target,expected);}],['refresh',()=>retention.refresh(target)]]));}
  if(retention.error)tasks.push({key:'retention:status',kind:'buildRetention',title:'Build download status',identity:{productId:'',buildIdentity:'',sourceRegion:''},status:'blocked',stage:'Status unconfirmed',measurement:null,problem:{code:'RETENTION_STATUS_UNCONFIRMED',message:retention.error},actions:[{id:'refresh',label:'Refresh download status'}]});
  if(retention.error)taskActions.set('retention:status',new Map([['refresh',()=>retention.refresh()]]));
  const downloads=buildDownloads(retention.jobs);
  const activityContext=workspace?`${workspace.build.productId}:${workspace.build.id}:${workspace.sourceReference.region}:${workspace.sourceReference.generation}`:'discovery';
  const [problem, setProblem] = useState('');
  // Settings replaces the window content. The screen it was opened from stays mounted but hidden and inert,
  // so its search, filters, selection, area and trails come back; pane scroll positions are restored on return.
  const [settings, setSettings] = useState<{ origin: SettingsOrigin; trigger: HTMLElement | null; scroll: [HTMLElement, number, number][] } | null>(null);
  const settingsState = useRef(settings); settingsState.current = settings;
  const settingsEntries = useRef(new Map<HTMLElement, () => SettingsOrigin>()), returning = useRef<NonNullable<typeof settings> | null>(null);
  const settingsApi = useMemo(() => ({
    register: (element: HTMLElement, origin: () => SettingsOrigin) => { settingsEntries.current.set(element, origin); return () => { settingsEntries.current.delete(element); }; },
    open: (origin: SettingsOrigin, trigger: HTMLElement | null) => {
      if (settingsState.current) return;
      const view = document.querySelector<HTMLElement>('[data-app-view]:not([hidden])');
      const scroll = view ? [...view.querySelectorAll<HTMLElement>('*')].filter(el => el.scrollTop || el.scrollLeft).map((el): [HTMLElement, number, number] => [el, el.scrollTop, el.scrollLeft]) : [];
      setSettings({ origin, trigger, scroll });
    },
  }), []);
  const closeSettings = useCallback(() => { returning.current = settingsState.current; setSettings(null); }, []);
  useLayoutEffect(() => {
    const back = returning.current; if (settings || !back) return; returning.current = null;
    for (const [el, top, left] of back.scroll) if (el.isConnected) { el.scrollTop = top; el.scrollLeft = left; }
    const visible = (el: HTMLElement | null) => !!el?.isConnected && !el.closest('[hidden],[inert]');
    const target = visible(back.trigger) ? back.trigger : [...document.querySelectorAll<HTMLElement>('[data-app-view]:not([hidden]) .settings-entry, [data-app-view]:not([hidden]) h1')].find(visible);
    target?.focus({ preventScroll: true });
  }, [settings]);
  useEffect(() => {
    // Ctrl+, opens Settings from any screen, through its visible entry when there is one.
    const keys = (event: globalThis.KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey || event.key !== ',') return;
      event.preventDefault();
      if (settingsState.current || document.querySelector('dialog[open]')) return;
      const entry = [...settingsEntries.current].find(([el]) => el.isConnected && !el.closest('[hidden],[inert]'));
      settingsApi.open(entry?.[1]() ?? { backLabel: 'Back to build preparation', returnsTo: 'build preparation', kept: 'Build preparation kept as you left it' }, entry?.[0] ?? null);
    };
    window.addEventListener('keydown', keys);
    return () => window.removeEventListener('keydown', keys);
  }, [settingsApi]);
  useEffect(() => { let disposed = false; void bridge.products().then(reply => {
    if (disposed) return;
    if (reply.ok) setProducts(reply.value.products);
    else setProblem(`${reply.error.message} (${reply.error.code})`);
  }).catch(() => { if (!disposed) setProblem('The local engine is unavailable. Restart VibeMiner to reconnect.'); }); return () => { disposed = true; }; }, [bridge]);
  return <SettingsProvider value={settingsApi}><DownloadActivity tasks={tasks} contextKey={activityContext} onInfoAction={(info,action)=>{if(action==='refreshStatus'&&info.key===activityContext&&info.target&&workspace?.download&&info.target.ledgerId===workspace.download.ledgerId&&matches(info.target))retention.refresh(info.target);}} onAction={(key,action)=>{if(action==='view'&&settingsState.current){returning.current=null;setSettings(null);}/* View research leaves Settings for the task's screen. */taskActions.get(key)?.get(action)?.();}}>
    <div className="discovery-screen" data-app-view="" hidden={flow.view !== 'discovery' || !!settings} inert={flow.view !== 'discovery' || !!settings}><a className="skip" href="#builds">Skip to builds</a>
      <DiscoveryWorkspace active={flow.view === 'discovery' && !settings} products={products} bridge={bridge} acknowledgedBuild={flow.operation?.result?.build} acknowledgedRegion={flow.operation?.result?.sourceReference.region} downloads={downloads} opening={flow.operation} onShowOpening={() => flow.showActive()} onCancelOpening={() => { void flow.cancel(); }} onOpen={(build, region) => { void flow.open(build, region, products.find(p => p.id === build.productId)?.label || build.productId); }}
        fixtureLabel={bridge.fixtureLabel ?? undefined} problem={problem}/>
    </div>
    <div className="app-view" data-app-view="" hidden={flow.view === 'discovery' || !!settings} inert={flow.view === 'discovery' || !!settings}>
    {flow.view === 'preparation' && flow.operation && <PreparationView tableActivity={null} operation={flow.operation} notice={flow.notice} fixtureLabel={bridge.fixtureLabel} onBack={flow.detach} onCancel={() => { void flow.cancel(); }} onRetry={() => { const op = flow.operation!; void flow.open(op.build, op.sourceRegion, op.label); }} onEnter={flow.enterVerified}/>}
    {flow.view === 'home' && flow.operation?.result && <HomeView activityNavigation={activityNavigation} effectsLoading={effectsLoading} effectsAvailable={['item-effects-links-v1','item-effects-inputs-v1','item-effect-spell-names-v1'].every(cap=>retention.features.includes(cap))&&!!bridge.itemEffectsRead&&!!bridge.itemEffectsPrepareLinks&&!!bridge.itemEffectsPage&&!!bridge.itemEffectDetails&&!!bridge.itemEffectPrepareDetails&&!!bridge.itemEffectNames&&!!bridge.itemEffectPrepareNames} creatureDisplays={creatureDisplays} creatureDisplayAvailable={retention.features.includes('creature-display-metadata-v1')&&!!bridge.creatureDisplayRead&&!!bridge.creatureDisplayPrepare} creaturesLoading={creaturesLoading} creaturesAvailable={retention.features.includes('creatures-v1')&&!!bridge.creaturesData&&!!bridge.creaturesPrepare&&!!bridge.creaturesPage&&!!bridge.creatureInspect} classificationsLoading={classificationsLoading} classificationsAvailable={retention.features.includes('item-classifications-v1')} subclassAvailable={retention.features.includes('item-subclass-labels-v1')} iconsLoading={iconsLoading} iconsAvailable={retention.features.includes('item-icons-v1')} itemsLoading={itemsLoading} itemsAvailable={retention.features.includes('items-v1')} retention={retention} tableLoading={tableLoading} operation={{ ...flow.operation, result: flow.operation.result }} fixtureLabel={bridge.fixtureLabel} onChoose={flow.detach} bridge={bridge} onReopen={() => { const op = flow.operation!; void flow.open(op.build, op.sourceRegion, op.label); }}/>}
    </div>
    {settings && <SettingsView bridge={bridge} origin={settings.origin} fixtureLabel={bridge.fixtureLabel} onBack={closeSettings}/>}
  </DownloadActivity></SettingsProvider>;
}

type ProductFilter = 'all' | string;
type ProblemGroup = { key: string; label: string; text: string; code: string };
/** Per-product problems from the latest saved checks and from this session's refresh, grouped when products share one. */
export function productProblems(statuses: readonly ProductDiscoveryStatus[], refreshProblems: readonly ProductRefreshProblem[]): ProblemGroup[] {
  const groups = new Map<string, { labels: string[]; text: string; code: string }>();
  const add = (key: string, label: string, text: string, code: string) => { const group = groups.get(key); if (group) { if (!group.labels.includes(label)) group.labels.push(label); } else groups.set(key, { labels: [label], text, code }); };
  for (const problem of refreshProblems) add(`refresh|${problem.code}|${problem.message}`, problem.productId, `check did not complete. ${problem.message}`, problem.code);
  for (const status of statuses) for (const error of status.errors) add(`${error.region ?? ''}|${error.code}|${error.message}`, `${status.productId}${error.region ? ` ${error.region.toUpperCase()}` : ''}`, error.message, error.code);
  return [...groups].map(([key, group]) => ({ key, label: group.labels.join(', '), text: group.text, code: group.code }));
}
/**
 * A latest check that received the versions document. `checkedRegions` lists that document's regions, so `partial`
 * with no checked regions means neither document was usable (offline-like failures, HTTP errors, invalid metadata).
 */
export const checkCompleted = (status: ProductDiscoveryStatus) => status.connection === 'online' || status.connection === 'partial' && status.checkedRegions.length > 0;
/** A latest check that was attempted but received no usable evidence. It never reads as a check that found nothing. */
export const checkFailed = (status: ProductDiscoveryStatus) => status.connection === 'offline' || status.connection === 'partial' && status.checkedRegions.length === 0;
/** The typed problem a failed check reports, for naming it in place. */
const failureText = (status: ProductDiscoveryStatus | undefined, refresh?: ProductRefreshProblem) => refresh ? `${refresh.message} (${refresh.code})` : status?.errors[0] ? `${status.errors[0].message} (${status.errors[0].code})` : 'Official metadata could not be read.';
export type CheckSummary = { checked: string[]; failed: string[]; notChecked: string[]; total: number };
/**
 * Which products have a completed check. With a baseline (each product's lastCheckedAt when this session's latest
 * refresh started), only checks that refresh committed count; without one, the saved latest checks do. An offline
 * check or a check that did not complete is never counted as checked.
 */
export function checkSummary(statuses: readonly ProductDiscoveryStatus[], baseline: ReadonlyMap<string, string | null> | null, refreshProblems: readonly ProductRefreshProblem[]): CheckSummary {
  const summary: CheckSummary = { checked: [], failed: [], notChecked: [], total: statuses.length };
  const problems = new Set(refreshProblems.map(problem => problem.productId));
  for (const status of statuses) {
    const ran = baseline ? status.lastCheckedAt !== null && status.lastCheckedAt !== (baseline.get(status.productId) ?? null) : status.connection !== 'notChecked';
    if (problems.has(status.productId) || ran && checkFailed(status)) summary.failed.push(status.productId);
    else if (ran && checkCompleted(status)) summary.checked.push(status.productId);
    else summary.notChecked.push(status.productId);
  }
  return summary;
}
const productNames = (ids: readonly string[]) => ids.length <= 3 ? ids.join(', ') : `${ids.length} products`;
/**
 * What a problem leaves behind, per product with the problem: its own saved observations are kept, or it has none.
 * Never decided from other products' history, and never "every product".
 */
export function retainedEvidence(problemStatuses: readonly ProductDiscoveryStatus[]): string {
  const kept = problemStatuses.filter(s => s.hasSavedHistory).map(s => s.productId), none = problemStatuses.filter(s => !s.hasSavedHistory).map(s => s.productId);
  return [...(kept.length ? [`Earlier saved observations are kept for ${productNames(kept)}.`] : []), ...(none.length ? [`${productNames(none)}: no saved observations.`] : [])].join(' ');
}
/** "Checked 1 of 2 products · wowt not checked"; null when nothing was ever checked and no refresh ran this session. */
export function checkLine(summary: CheckSummary, refreshed: boolean): string | null {
  if (!refreshed && !summary.checked.length && !summary.failed.length) return null;
  const count = summary.checked.length === summary.total ? `${summary.total}` : `${summary.checked.length} of ${summary.total}`;
  return [`Checked ${count} products`, ...(summary.failed.length ? [`${productNames(summary.failed)} could not be checked`] : []), ...(summary.notChecked.length ? [`${productNames(summary.notChecked)} not checked`] : [])].join(' · ');
}
function DiscoveryWorkspace({ active: visibleView, products, bridge, fixtureLabel, problem, downloads, opening, onShowOpening, onCancelOpening, onOpen, acknowledgedBuild, acknowledgedRegion }: { active: boolean; products: Product[]; bridge: DiscoveryBridge; fixtureLabel?: string; problem: string; downloads: Map<string, BuildDownload>; opening: Opening | null; onShowOpening: () => void; onCancelOpening: () => void; acknowledgedBuild?: Build; acknowledgedRegion?: string; onOpen: (build: Build, sourceRegion: string) => void }) {
  const [page, setPage] = useState<DiscoveryCatalogPage | null>(null);
  useEffect(() => {
    if (!acknowledgedBuild) return;
    setPage(previous => {
      const same = (build: Build) => build.id === acknowledgedBuild.id && build.productId === acknowledgedBuild.productId;
      if (!previous?.builds.some(build => same(build) && (build.isNew !== acknowledgedBuild.isNew || build.isLocal !== acknowledgedBuild.isLocal))) return previous;
      return { ...previous, builds: previous.builds.map(build => same(build) ? { ...build, isNew: acknowledgedBuild.isNew, isLocal: acknowledgedBuild.isLocal } : build) };
    });
  }, [acknowledgedBuild]);
  const [filter, setFilter] = useState<DiscoveryFilter>('all');
  const [productFilter, setProductFilter] = useState<ProductFilter>('all');
  const [query, setQuery] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(savedSelection);
  const [busy, setBusy] = useState(false);
  const [reading, setReading] = useState(true);
  const [stopping, setStopping] = useState(false);
  const [progress, setProgress] = useState<{ completed: number; total: number } | null>(null);
  const [refreshProblems, setRefreshProblems] = useState<ProductRefreshProblem[]>([]);
  // Each product's lastCheckedAt when the latest refresh this session started; set once that refresh ends.
  const startBaseline = useRef<Map<string, string | null> | null>(null);
  const [sessionBaseline, setSessionBaseline] = useState<Map<string, string | null> | null>(null);
  const [message, setMessage] = useState('Not refreshed this session.');
  const [error, setError] = useState('');
  // A refused catalog (older or newer VibeMiner) cannot be read or refreshed; nothing on screen may suggest otherwise.
  const refusedRead = useRef(false);
  const refused = refusedCatalog(problem) || refusedCatalog(error);
  const [sourceOpen, setSourceOpen] = useState(false);
  const [selectionNotice, setSelectionNotice] = useState('');
  const currentPage = useRef(page); currentPage.current = page;
  const currentSelection = useRef(selectedId); currentSelection.current = selectedId;
  const active = useRef<string | null>(null);
  const disposed = useRef(false);
  const generation = useRef(0);
  const lastSelection = useRef<Build | null>(null);
  const searchInput = useRef<HTMLInputElement>(null);
  const refreshButton = useRef<HTMLButtonElement>(null);
  const activity_ = useDownloadActivity();
  const latestFilter = useRef(filter); latestFilter.current = filter;
  const latestProduct = useRef(productFilter); latestProduct.current = productFilter;
  const label = (id: string) => products.find(p => p.id === id)?.label;
  const params = (nextFilter: DiscoveryFilter, product: ProductFilter, limit = 50, cursor?: string | null): ReadAllParams => ({ ...(product !== 'all' ? { productId: product } : {}), filter: nextFilter, limit, ...(cursor ? { cursor } : {}) });
  const expected = (value: DiscoveryCatalogPage, product: ProductFilter) => { if (value.productId !== (product === 'all' ? null : product)) throw new Error('Product mismatch'); };
  function accept(value: DiscoveryCatalogPage, product: ProductFilter, append = false) {
    expected(value, product);
    setPage(previous => append && previous ? { ...value, builds: [...previous.builds, ...value.builds.filter(b => !previous.builds.some(old => old.id === b.id))] } : value);
  }
  /** Read pages until the wanted build is loaded, the window holds 500 builds or the engine has no more. */
  async function fill(first: DiscoveryCatalogPage, nextFilter: DiscoveryFilter, product: ProductFilter, done: (value: DiscoveryCatalogPage) => boolean, live: () => boolean): Promise<DiscoveryCatalogPage | null> {
    let windowPage = first;
    while (windowPage.nextCursor && windowPage.builds.length < 500 && !done(windowPage)) {
      const next = await bridge.readAll(params(nextFilter, product, Math.min(50, 500 - windowPage.builds.length), windowPage.nextCursor));
      if (!live()) return null;
      if (!next.ok) throw Object.assign(new Error('read'), { reply: next });
      expected(next.value, product);
      const additions = next.value.builds.filter(build => !windowPage.builds.some(old => old.id === build.id));
      if (!additions.length && next.value.nextCursor) throw new Error('Paging did not advance.');
      windowPage = { ...next.value, builds: [...windowPage.builds, ...additions] };
    }
    return windowPage;
  }
  async function read(nextFilter: DiscoveryFilter, product: ProductFilter, cursor?: string, restoreSelection = false) {
    const version = ++generation.current;
    const live = () => !disposed.current && version === generation.current;
    setReading(true);
    try {
      const reply = await bridge.readAll(params(nextFilter, product, 50, cursor));
      if (!live()) return;
      if (reply.ok) {
        const wanted = restoreSelection ? currentSelection.current : null;
        const windowPage = await fill(reply.value, nextFilter, product, value => !wanted || value.builds.some(build => build.id === wanted), live);
        if (!windowPage) return;
        accept(windowPage, product, !!cursor); setError('');
        if (wanted && !windowPage.builds.some(build => build.id === wanted)) setSelectionNotice(windowPage.nextCursor ? 'The saved exact selection is outside the 500-build display window. No different build has been selected.' : 'The saved exact selection is not returned by this view. No different build has been selected.');
      } else { if (refusedCodes.includes(reply.error.code)) refusedRead.current = true; setError(`${reply.error.message} (${reply.error.code})`); }
    } catch (failure) {
      const reply = (failure as { reply?: BridgeReply<unknown> }).reply;
      if (live()) setError(reply && !reply.ok ? `${reply.error.message} (${reply.error.code})` : 'The local engine connection failed. Restart VibeMiner to reconnect.');
    }
    finally { if (live()) setReading(false); }
  }
  async function refresh() {
    if (active.current || refusedRead.current || refused) return;
    const id = crypto.randomUUID(); active.current = id;
    startBaseline.current = new Map((currentPage.current?.products ?? []).map(status => [status.productId, status.lastCheckedAt]));
    ++generation.current; setReading(false); setBusy(true); setStopping(false); setError(''); setProgress(null); setRefreshProblems([]); setMessage('Checking official metadata for every product…');
    let reread = false;
    try {
      const reply = await bridge.refreshAll(id);
      if (disposed.current || active.current !== id) return;
      ++generation.current;
      if (reply.ok) {
        // A refreshed view must not keep showing a pre-refresh copy of a selection it no longer returns, even if it exits early.
        lastSelection.current = null;
        setRefreshProblems(reply.value.refreshProblems);
        const originalWindow = currentPage.current;
        const wantedCount = Math.min(500, originalWindow?.builds.length || 0);
        const wantedSelection = originalWindow?.builds.some(build => build.id === currentSelection.current) ? currentSelection.current : null;
        const contextFilter = latestFilter.current, contextProduct = latestProduct.current;
        const refreshGeneration = generation.current;
        const live = () => !disposed.current && refreshGeneration === generation.current && contextFilter === latestFilter.current && contextProduct === latestProduct.current;
        let first = reply.value;
        if (contextFilter !== 'all' || contextProduct !== 'all') {
          const filtered = await bridge.readAll(params(contextFilter, contextProduct));
          if (disposed.current) return;
          if (!live()) { setMessage('Discovery completed. Showing the current filtered view.'); return; }
          if (!filtered.ok) throw new Error('Refreshed view could not be read.');
          first = filtered.value;
        }
        const windowPage = await fill(first, contextFilter, contextProduct, value => value.builds.length >= wantedCount && (!wantedSelection || value.builds.some(build => build.id === wantedSelection)), live);
        if (disposed.current) return;
        if (!windowPage || !live()) { setMessage('Discovery completed. Showing the current filtered view.'); return; }
        accept(windowPage, contextProduct);
        setSelectionNotice(wantedSelection && !windowPage.builds.some(build => build.id === wantedSelection) ? windowPage.nextCursor ? 'The selected exact build is outside the refreshed 500-build display window. No different build has been selected.' : 'The selected exact build is no longer returned by this view after refresh. No different build has been selected.' : '');
        const statuses = reply.value.products, total = statuses.length, checkedNow = statuses.filter(s => s.connection !== 'notChecked');
        const failed = new Set([...statuses.filter(s => s.connection === 'offline' || s.errors.length > 0).map(s => s.productId), ...reply.value.refreshProblems.map(p => p.productId)]);
        // What is kept is stated per product with a problem, from that product's own saved history.
        const kept = retainedEvidence(statuses.filter(s => failed.has(s.productId)));
        setMessage(checkedNow.length > 0 && checkedNow.every(checkFailed) ? `Could not refresh. ${kept}` : failed.size ? `Check completed with problems for ${failed.size} of ${total} products. ${kept}` : `Discovery complete. Checked ${total} products.`);
      } else if (reply.error.code === 'CANCELED') { setMessage('Discovery canceled. Products checked before stopping keep their new results.'); reread = true; }
      else { setError(`${reply.error.message} (${reply.error.code})`); setMessage('Discovery could not complete. Previous observations are still available.'); reread = true; }
    } catch { if (!disposed.current) { setError('The refreshed view could not be completed. Previous observations remain displayed; retry discovery to read a coherent window.'); setMessage('Discovery could not complete the refreshed view. Previous observations are still displayed.'); } }
    finally { if (!disposed.current && active.current === id) { active.current = null; setBusy(false); setStopping(false); setProgress(null); setSessionBaseline(startBaseline.current); } }
    // Products that finished before a stop or failure committed their results; show what is saved now.
    if (reread && !disposed.current) void read(latestFilter.current, latestProduct.current, undefined, true);
  }
  useEffect(() => {
    disposed.current = false;
    const off = bridge.onProgress(update => { if (update.requestId === active.current && !disposed.current && update.unit === 'products' && update.completed !== null && update.total !== null) setProgress({ completed: update.completed, total: update.total }); });
    void read('all', 'all', undefined, true);
    return () => { disposed.current = true; ++generation.current; off(); if (active.current) void bridge.cancel(active.current).catch(() => {}); };
  }, []);
  async function cancel() {
    if (!active.current || stopping) return;
    setStopping(true); setMessage('Stopping discovery…');
    try { const reply = await bridge.cancel(active.current); if (!disposed.current && !reply.ok) setError(`${reply.error.message} (${reply.error.code}). Waiting for discovery to stop safely.`); }
    catch { if (!disposed.current) setError('Cancellation could not be delivered. Waiting for the local engine.'); }
  }
  function changeFilter(value: DiscoveryFilter) { setFilter(value); setError(''); void read(value, productFilter); }
  function changeProduct(value: ProductFilter) { setProductFilter(value); setError(''); void read(filter, value); }
  const filtered = useMemo(() => (page?.builds || []).filter(b => (filter === 'all' || filter === 'local' && b.isLocal || filter === 'advertised' && b.isAdvertised) && (productFilter === 'all' || b.productId === productFilter) && `${b.version} ${b.buildId}`.toLowerCase().includes(query.toLowerCase())), [page, query, productFilter, filter]);
  // A selection hidden by filters stays selected and inspectable while its row is still loaded.
  const loadedSelection = (page?.builds || []).find(b => b.id === selectedId);
  if (loadedSelection) lastSelection.current = loadedSelection;
  // Only an engine-side filter (Product, Prepared, Current) can hide a loaded selection; a refresh that drops it still reports it as gone.
  const selected = loadedSelection ?? ((filter !== 'all' || productFilter !== 'all') && lastSelection.current?.id === selectedId ? lastSelection.current : undefined);
  const hidden = !!selectedId && !filtered.some(b => b.id === selectedId);
  const filtersActive = filter !== 'all' || productFilter !== 'all' || !!query;
  const activeIndex = Math.max(0, filtered.findIndex(b => b.id === selectedId));
  const statuses = page?.products || [];
  const status = (productId: string) => statuses.find(s => s.productId === productId);
  function select(build: Build) { setSelectionNotice(''); setSelectedId(build.id); try { localStorage.setItem(selectionKey, build.id); } catch { /* Selection stays in this session. */ } }
  // Clear filters removes its own button, so focus moves to All, the control it just set.
  function clearFilters() { setQuery(''); if (filter !== 'all' || productFilter !== 'all') { setFilter('all'); setProductFilter('all'); setError(''); void read('all', 'all'); } document.getElementById('visibility-all')?.focus(); }
  const problems = productProblems(statuses, refreshProblems);
  // A product whose latest attempt failed (saved as failed, or not committed this session) has no completed check to report.
  const refreshFailed = new Set(refreshProblems.map(p => p.productId));
  const failedNow = (s: ProductDiscoveryStatus) => checkFailed(s) || refreshFailed.has(s.productId);
  const attempted = statuses.filter(s => s.connection !== 'notChecked' || refreshFailed.has(s.productId));
  const completed = attempted.filter(s => !failedNow(s) && checkCompleted(s));
  const failedChecks = attempted.filter(failedNow);
  // Products the notice reports a problem for: failed attempts and checks with regional problems.
  const problemStatuses = statuses.filter(s => failedNow(s) || s.errors.length > 0);
  const allFailed = attempted.length > 0 && completed.length === 0;
  const anySaved = statuses.some(s => s.hasSavedHistory);
  const nothingSaved = !!page && !reading && !page.builds.length && filter === 'all' && productFilter === 'all' && !anySaved;
  function listKey(event: KeyboardEvent<HTMLDivElement>) {
    if ((event.key === 'Enter' || event.key === ' ') && filtered.length) { event.preventDefault(); select(filtered[activeIndex]); return; }
    const from = filtered.some(b => b.id === selectedId) ? activeIndex : -1;
    const target = { ArrowDown: from + 1, ArrowUp: Math.max(0, from - 1), Home: 0, End: filtered.length - 1, PageDown: from + 10, PageUp: Math.max(0, from - 10) }[event.key];
    if (target === undefined || !filtered.length) return;
    event.preventDefault();
    const next = filtered[Math.max(0, Math.min(filtered.length - 1, target))];
    select(next);
    document.getElementById(`build-option-${next.id}`)?.scrollIntoView?.({ block: 'nearest' });
  }
  useEffect(() => {
    if (!visibleView) return;
    const keys = (event: globalThis.KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'f') { event.preventDefault(); searchInput.current?.focus(); searchInput.current?.select(); }
      // F5 starts a check only when none is running; it never cancels one.
      else if (event.key === 'F5') { event.preventDefault(); if (!active.current) void refresh(); /* refresh() itself refuses while the catalog is refused. */ }
    };
    window.addEventListener('keydown', keys);
    return () => window.removeEventListener('keydown', keys);
  }, [visibleView]);
  const refreshLabel = busy ? stopping ? 'Stopping discovery…' : 'Cancel refresh' : error || problems.length ? 'Retry discovery' : 'Refresh builds';
  const openingHere = opening && selected && opening.build.id === selected.id && opening.build.productId === selected.productId ? opening : null;
  const openingElsewhere = opening && !openingHere ? opening : null;
  const openingBox = (op: Opening) => <div className="dv-state"><strong>{op.status === 'pending' || op.status === 'stopping' ? 'Opening build' : op.status === 'succeeded' ? 'Verified overview available' : op.status === 'canceled' ? 'Opening canceled' : 'Opening needs attention'}</strong>{op.build.productId} {op.build.version} · source {op.sourceRegion.toUpperCase()}<br/>{op.progress?.stage || (op.status === 'stopping' ? 'Stopping preparation…' : 'Exact configuration context is preserved.')}<div className="dv-row"><button className="dv-btn" onClick={onShowOpening}>{op.status === 'succeeded' ? 'View verified overview' : 'View preparation'}</button>{(op.status === 'pending' || op.status === 'stopping') && <button className="dv-btn" aria-disabled={op.status === 'stopping'} onClick={() => { if (op.status === 'pending') onCancelOpening(); }}>{op.status === 'stopping' ? 'Stopping opening…' : 'Cancel opening'}</button>}</div></div>;
  const shownProblems = problems.slice(0, 6);
  const notices = <>
    {problem && (refusedCatalog(problem) ? <RefusedNotice text={problem}/> : <div role="alert" className="dv-notice error"><div><strong>Products unavailable</strong>{problem}</div></div>)}
    {error && !(refusedCatalog(error) && refusedCatalog(problem)) && (refusedCatalog(error) ? <RefusedNotice text={error}/> : <div role="alert" className="dv-notice error"><div><><strong>Discovery problem</strong>{error} Saved observations remain available. Retry discovery checks every product again; engine failures require restarting VibeMiner.</></div></div>)}
    {problems.length > 0 && !refused && <div className={`dv-notice${failedChecks.length ? ' error' : ''}`}><div><strong>{allFailed ? 'Refresh unavailable' : 'Refresh incomplete'}</strong>{anySaved ? `${retainedEvidence(problemStatuses)}${problemStatuses.some(s => s.hasSavedHistory) ? ' A failed check does not remove earlier advertisements.' : ''}` : 'No saved observations are available yet.'} Retry discovery checks every product again.<ul>{shownProblems.map(p => <li key={p.key}>{p.label}: {p.text} <code>({p.code})</code></li>)}</ul>{problems.length > shownProblems.length && <span>{problems.length - shownProblems.length} more problems. Source details lists the problems of the selected build's product.</span>}</div>{!busy && <button className="dv-btn" onClick={() => { refreshButton.current?.focus(); void refresh(); }}>Retry discovery</button>}</div>}
    {!refused && hidden && filtered.length > 0 && (selected || filtersActive) && <div className="dv-notice"><div><strong>Selected build is hidden by these filters</strong>{selected ? `${selected.productId} ${selected.version} is still selected.` : 'The refresh did not reload its details.'} Clear filters to see it in the list.</div><button className="dv-btn" onClick={clearFilters}>Clear filters</button></div>}
    {openingElsewhere && <div className="dv-notice info">{openingBox(openingElsewhere)}</div>}
  </>;
  const summary = activity_ && activity_.entry && !activity_.expanded ? activity_.summary : null;
  const windowRef = useRef<HTMLDivElement>(null), toolbarRef = useRef<HTMLElement>(null), statusRef = useRef<HTMLElement>(null);
  // Text-relative breakpoint, evaluated here because Chromium does not always re-run rem container queries when only the root text size changes.
  const [compact, setCompact] = useState(false);
  useLayoutEffect(() => {
    const measure = () => { const host = windowRef.current; if (host) setCompact(host.clientWidth < 65.333333 * parseFloat(getComputedStyle(document.documentElement).fontSize)); };
    measure();
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    if (windowRef.current) resize?.observe(windowRef.current);
    const root = new MutationObserver(measure);
    root.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class'] });
    window.addEventListener('resize', measure);
    return () => { resize?.disconnect(); root.disconnect(); window.removeEventListener('resize', measure); };
  }, []);
  // When a canceled opening retires its Stopping control, focus moves to Open build instead of the page.
  const openingStatus = opening?.status;
  const previousOpeningStatus = useRef(openingStatus);
  useEffect(() => {
    const before = previousOpeningStatus.current; previousOpeningStatus.current = openingStatus;
    if (before === 'stopping' && openingStatus !== 'stopping' && (document.activeElement === document.body || !document.activeElement)) (document.querySelector<HTMLElement>('.dv-open') ?? document.getElementById('builds-heading'))?.focus();
  }, [openingStatus]);
  useLayoutEffect(() => {
    const place = () => { const host = windowRef.current; if (!host || !toolbarRef.current || !statusRef.current) return; host.style.setProperty('--dv-panel-top', `${toolbarRef.current.getBoundingClientRect().bottom}px`); host.style.setProperty('--dv-panel-bottom', `${statusRef.current.offsetHeight}px`); };
    place();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(place);
    if (toolbarRef.current) observer?.observe(toolbarRef.current);
    if (statusRef.current) observer?.observe(statusRef.current);
    return () => observer?.disconnect();
  }, []);
  const productCount = statuses.length || products.length;
  const checksLine = statuses.length ? checkLine(checkSummary(statuses, sessionBaseline, refreshProblems), sessionBaseline !== null) : null;
  // Products are checked at different times: show the newest check and, when they differ, the oldest one too.
  // Only checks that received evidence count; a failed attempt is never shown as the latest check.
  const checkTimes = completed.map(s => s.lastCheckedAt).filter((time): time is string => !!time).sort();
  const latestCheck = checkTimes.at(-1) ?? null, oldestCheck = checkTimes.length > 1 && stamp(checkTimes[0]) !== stamp(latestCheck) ? checkTimes[0] : null;
  const checking = progress ? `Checking ${progress.total} products · ${progress.completed} of ${progress.total} done…` : `Checking ${productCount || 'every'} products…`;
  const filteredStatus = productFilter === 'all' ? undefined : status(productFilter);
  const selectedStatus = selected ? status(selected.productId) : undefined;
  // Empty states never present a failed check as a completed one that found nothing.
  const [emptyTitle, emptyText] = ((): [string, string] => {
    if (reading) return ['Reading saved observations…', ''];
    if (busy) return ['Checking official metadata…', ''];
    if (filter === 'local') return ['No prepared builds', 'Saved discovery observations are available in All. A saved observation is separate from a prepared build.'];
    if (query) return ['No matching loaded builds', 'Try another version or build number. Filters cover loaded builds only.'];
    const failedNames = failedChecks.length ? ` ${productNames(failedChecks.map(s => s.productId))} could not be checked.` : '';
    if (productFilter !== 'all') {
      if (!filteredStatus || filteredStatus.connection === 'notChecked' && !refreshFailed.has(productFilter)) return [`No saved builds for ${productFilter}`, `${productFilter} has not been checked yet. Refresh builds checks every product.`];
      if (failedNow(filteredStatus)) return [`Could not check ${productFilter}`, `The latest check of ${productFilter} failed: ${failureText(filteredStatus, refreshProblems.find(p => p.productId === productFilter))} ${filter === 'advertised' ? 'No build of it is known to be current.' : `No saved builds for ${productFilter}.`} Retry discovery checks every product again.`];
      if (filter === 'advertised') return ['No current builds', `No build of ${productFilter} was advertised at the last check, ${stamp(filteredStatus.lastCheckedAt)}. Saved builds are in All.`];
      return [`No saved builds for ${productFilter}`, `${productFilter} had no advertised builds at ${stamp(filteredStatus.lastCheckedAt)}. Refresh builds checks every product.`];
    }
    if (allFailed && (filter === 'advertised' || !anySaved)) return ['Could not check any product', anySaved ? 'No product could be checked at its latest attempt, so no build is known to be current. Saved builds are in All.' : 'Official metadata could not be read for any product, and no saved observations are available. Select Retry discovery to check every product again.'];
    if (filter === 'advertised') return ['No current builds', `No build was advertised at the last completed checks.${failedNames} Saved builds are in All.`];
    if (anySaved) return ['No builds match this view', 'Saved builds are not returned by this view.'];
    if (completed.length) return ['No builds advertised', `No builds were advertised by the checked products at ${stamp(latestCheck)}.${failedNames}`];
    return ['No saved builds yet', "Refresh builds checks Blizzard's official version service for every World of Warcraft product."];
  })();
  return <div className={`dv-window${compact ? ' dv-compact' : ''}`} ref={windowRef}>
    <header className="dv-toolbar" ref={toolbarRef}>
      <div className="dv-brand"><img src={brand} alt="VibeMiner"/><span className="dv-word" aria-hidden="true">Vibe<span>Miner</span></span></div>
      {fixtureLabel && <span className="dv-fixture"><strong>{fixtureLabel}</strong><span> · Synthetic identities and receipts.</span></span>}
      <div className="dv-spacer"/>
      <ActivityTrigger compact/>
      <SettingsButton className="dv-btn" origin={discoveryOrigin}/>
    </header>
    <main className={`dv-body${refused || nothingSaved && !selected ? ' no-inspector' : ''}`} id="discovery">
      <section className="dv-pane" aria-labelledby="builds-heading">
        <div className="dv-pane-head">
          <h1 id="builds-heading" tabIndex={-1}>Builds</h1>{filtered.length > 0 && <span className="dv-n" aria-label={`${filtered.length}${page?.nextCursor ? ' or more' : ''} builds shown`}>{filtered.length}{page?.nextCursor ? '+' : ''}</span>}
          <input ref={searchInput} id="search" className="dv-control dv-search" type="search" placeholder="Filter version or build" aria-label="Filter version or build number" aria-keyshortcuts="Control+F" value={query} disabled={nothingSaved || refused} onChange={e => { setQuery(e.target.value); }}/>
          <select id="product-filter" className="dv-control dv-product-filter" aria-label="Product" value={productFilter} disabled={nothingSaved || refused} onChange={e => changeProduct(e.target.value)}><option value="all">All products</option>{products.map(p => <option value={p.id} key={p.id}>{p.id} · {p.label}</option>)}</select>
          <div className="dv-segmented" role="group" aria-label="Build visibility">{(['all', 'local', 'advertised'] as const).map(f => <button key={f} id={`visibility-${f}`} aria-pressed={filter === f} disabled={nothingSaved || refused} onClick={() => changeFilter(f)}>{f === 'all' ? 'All' : f === 'local' ? 'Prepared' : 'Current'}</button>)}</div>
          {!refused && <button ref={refreshButton} className="dv-btn dv-refresh" aria-keyshortcuts="F5" onClick={() => { if (busy) void cancel(); else void refresh(); }} aria-disabled={stopping}>{!stopping && <span aria-hidden="true">{busy ? '✕' : '↻'}</span>}{refreshLabel}</button>}
        </div>
        {notices}
        <div className={`dv-list${notices.props.children.some(Boolean) ? '' : ' flush'}`} aria-busy={reading}>
          {filtered.length > 0 && <>
            <div className="dv-cols dv-thead" aria-hidden="true"><span>Product</span><span>Version</span><span>Build</span><span>Downloaded</span><span>In VibeMiner</span></div>
            <div role="listbox" id="builds" aria-label="Builds" tabIndex={0} aria-activedescendant={`build-option-${filtered[activeIndex].id}`} onKeyDown={listKey}>{filtered.map(build => { const download = downloads.get(`${build.productId}:${build.id}`), job = download?.job, percent = downloadedPercent(job), running = !!download?.running; return <div role="option" key={build.id} id={`build-option-${build.id}`} data-build-id={build.id} data-product-id={build.productId} className={`dv-cols build-row${filtered[activeIndex].id === build.id ? ' active' : ''}${build.isAdvertised ? ' current' : ''}`} aria-selected={selectedId === build.id} onClick={() => select(build)}>
              <span className="dv-product">{build.productId}</span>
              <span className="dv-version">{build.version || 'Version not supplied'}{build.isAdvertised && <span className="dv-pill current">Current</span>}<span className="visually-hidden">, Config {short(build.buildConfig)}, Identity {short(build.id)}</span></span>
              <span className="dv-dim"><span className="visually-hidden">Build </span>{build.buildId || 'not supplied'}</span>
              <span><span className="visually-hidden">Downloaded </span>{!job && !build.isLocal ? <span className="dv-dim">Not opened</span> : percent === null ? <span className="dv-dim">{job?.coverage.presenceState === 'assessing' ? 'Checking…' : 'Not checked'}</span> : <span className={`dv-meter${percent === '100%' ? ' done' : ''}`}><span className="dv-track" aria-hidden="true"><i style={{ width: percent === '<0.1%' ? '0%' : percent }}/></span>{percent}</span>}{running && <><span className="dv-pulse" aria-hidden="true"/><span className="visually-hidden">, downloading</span></>}</span>
              <span>{build.isLocal && <span className="dv-pill">Prepared</span>}{build.isNew && <span className="dv-pill gold">New to VibeMiner</span>}{!build.isLocal && !build.isNew && <span className="dv-dim">—</span>}</span>
            </div>; })}</div>
            {page?.nextCursor && <div className="dv-load-more"><button className="dv-btn" disabled={reading || busy || page.builds.length >= 500} onClick={() => void read(filter, productFilter, page.nextCursor!)}>{reading ? 'Loading…' : page.builds.length >= 500 ? '500-build display limit reached' : 'Load more builds'}</button><span>{page.builds.length >= 500 ? 'This view displays the first 500 builds. Search only refines loaded builds; searching beyond this display window is not available.' : 'Filters apply to loaded builds only.'}</span></div>}
          </>}
          {refused && <div className="dv-empty"><div><h2>Saved catalog not opened</h2><p>No builds are shown because the saved catalog was not opened. Follow the steps in the notice above.</p></div></div>}
          {!refused && !filtered.length && <div className="dv-empty"><div><h2>{emptyTitle}</h2>{!reading && !busy && <p>{emptyText}</p>}{filtersActive && !reading && <button className="dv-btn" onClick={clearFilters}>Clear filters</button>}</div></div>}
        </div>
      </section>
      {!refused && !(nothingSaved && !selected) && <aside className="dv-inspector" id="inspector" aria-labelledby="inspector-heading" inert={!!activity_?.expanded}>{selected ? <>
        <div className="dv-insp-head"><div className="dv-label">Selected build</div><h2 id="inspector-heading" className="dv-selectable">{selected.version}</h2><p className="inspector-product">World of Warcraft · {label(selected.productId) || selected.productId} · <code>{selected.productId}</code></p></div>
        <div className="dv-insp-body">
          {openingHere ? openingBox(openingHere) : <div className="dv-state"><strong>{selected.isLocal ? 'Prepared locally' : 'Not prepared'}</strong>{selected.isLocal ? downloads.get(`${selected.productId}:${selected.id}`)?.job.mode === 'downloadAll' ? 'Local evidence recorded. Download all is on for this build.' : 'Local evidence recorded. Game content downloads on demand.' : 'Opening verifies configs and measures download size.'}</div>}
          <div className="dv-label">Identity</div>
          <dl><dt>Product</dt><dd><code>{selected.productId}</code>{label(selected.productId) ? ` · ${label(selected.productId)}` : ''}</dd>
            <dt>Build</dt><dd className="dv-selectable">{selected.buildId || 'Not supplied'}</dd>
            <dt>Build config</dt><dd className="mono dv-selectable">{selected.buildConfig}</dd>
            <dt>CDN config</dt><dd className="mono dv-selectable">{selected.cdnConfig}</dd>
            <dt>Product config</dt><dd className="mono dv-selectable">{missing(selected.identityFields.ProductConfig)}</dd>
            <dt>Key ring</dt><dd className="mono dv-selectable">{missing(selected.identityFields.KeyRing)}</dd>
            <dt>Identity</dt><dd className="mono dv-selectable">{selected.id}</dd></dl>
          <CdnConfigHint build={selected} open={acknowledgedBuild?.productId === selected.productId && acknowledgedBuild.id === selected.id && acknowledgedRegion ? { cdnConfig: acknowledgedBuild.cdnConfig, region: acknowledgedRegion } : undefined}/>
          {(() => { const download = downloads.get(`${selected.productId}:${selected.id}`), job = download?.job, percent = downloadedPercent(job); return job ? <><div className="dv-label">Download</div><dl><dt>Downloaded</dt><dd>{percent === null ? 'Not checked' : `${exactBytes(job.coverage.presentBytes)} of ${exactBytes(job.totalEncodedBytes)} · ${percent}`}</dd><dt>Scope</dt><dd>Download manifest, all variants (encoded)</dd><dt>Checked</dt><dd>{job.coverage.presenceState === 'assessing' ? 'Checking now' : job.coverage.presenceAssessedAt ? stamp(job.coverage.presenceAssessedAt) : 'Never'}</dd><dt>Source</dt><dd>{job.target.sourceRegion.toUpperCase()}</dd>{job.coverage.corruptObjects !== '0' && <><dt>Corrupt</dt><dd>{exactCount(job.coverage.corruptObjects)} objects found corrupt at the check; not counted as downloaded</dd></>}{download!.running && <><dt>Activity</dt><dd>Download all running</dd></>}</dl></> : null; })()}
          <div className="dv-label">Observation</div>
          <dl><dt>Publication</dt><dd>{selected.isAdvertised ? `Current: advertised at the last completed check (${stamp(selected.lastSeenAt)})` : 'Not advertised at the last completed check'}{selectedStatus && failedNow(selectedStatus) && ' · latest check failed'}</dd>
            <dt>Advertised in</dt><dd>{selected.advertisedRegions.length ? regions(selected.advertisedRegions) : 'None at last check'}</dd>
            <dt>CDN routes</dt><dd>{regions(routeRegions(selected))}{selected.advertisedRegions.filter(r => !routeRegions(selected).includes(r)).map(r => ` · ${r.toUpperCase()} has no CDN route`).join('')}</dd>
            <dt>Last seen</dt><dd>{stamp(selected.lastSeenAt)}</dd>
            <dt>File availability</dt><dd>Not checked</dd></dl>
          {selected.identityFields.KeyRing && <p className="dv-note">A key ring reference does not mean its keys are available.</p>}
        </div>
        <BuildOpenAction key={selected.id} build={selected} onOpen={onOpen} onSource={() => setSourceOpen(true)}/>
        {sourceOpen && <SourceDialog build={selected} status={selectedStatus} openCdnConfig={acknowledgedBuild?.productId === selected.productId && acknowledgedBuild.id === selected.id ? acknowledgedBuild.cdnConfig : undefined} onClose={() => setSourceOpen(false)}/>}
      </> : <><h2 id="inspector-heading" className="visually-hidden">Build inspector</h2><div className="dv-insp-empty"><div><strong>{hidden ? 'Selected build is hidden' : 'No build selected'}</strong><p>{hidden ? selectionNotice || (filtersActive ? 'Clear filters to inspect it again.' : 'It is not among the loaded builds.') : 'Select a build to see its exact identity.'}</p></div></div><div className="dv-insp-foot blocked"><button className="dv-btn primary" disabled>Open build</button><p className="dv-reason">Select a build with a recorded download source to open it.</p></div></>}</aside>}
    </main>
    <footer className="dv-statusbar" ref={statusRef}>
      {busy && <span className="dv-busy"><span className="dv-pulse" aria-hidden="true"/>{!stopping && <span aria-live="off">{checking}</span>}</span>}
      {checksLine && <span className="dv-secondary dv-checks">{checksLine}</span>}
      {latestCheck ? <span className="dv-latest">Latest check <strong>{stamp(latestCheck)}</strong>{oldestCheck && <span className="dv-secondary"> · oldest <strong>{stamp(oldestCheck)}</strong></span>}</span> : !checksLine && <span>Not checked yet</span>}
      <span role="status" aria-live="polite" className="dv-message">{stopping ? 'Stopping discovery…' : refused ? 'The saved catalog was not opened.' : message}</span>
      <span className="dv-spacer"/>
      {summary && <span className={activity_!.attention ? 'dv-warn' : 'dv-busy'} aria-live="off">{!activity_!.attention && activity_!.active && <span className="dv-pulse" aria-hidden="true"/>}{summaryLine(summary)}</span>}
    </footer>
  </div>;
}
const discoveryOrigin: SettingsOrigin = { backLabel: 'Back to Discovery', returnsTo: 'Discovery', kept: 'Discovery kept as you left it' };
function BuildOpenAction({ build, onOpen, onSource }: { build: Build; onOpen: (build: Build, sourceRegion: string) => void; onSource: () => void }) {
  const eligible = routeRegions(build);
  const [source, setSource] = useState(() => eligible.includes('us') ? 'us' : eligible[0] || '');
  const available = eligible.includes(source);
  return <div className={`dv-insp-foot${available ? '' : ' blocked'}`}><div className="dv-field"><label htmlFor="opening-source">Download source</label><select id="opening-source" className="dv-control" value={available ? source : ''} onChange={event => setSource(event.target.value)} disabled={!eligible.length}>{!available && <option value="">No recorded source route</option>}{eligible.map(region => <option key={region} value={region}>{region.toUpperCase()}</option>)}</select></div><button className="dv-btn primary dv-open" disabled={!available} aria-describedby="open-reason" onClick={() => { if (available) onOpen(build, source); }}>Open build</button><button className="dv-btn" onClick={onSource}>Source details</button><p className="dv-reason" id="open-reason">{available ? 'Browsing never downloads game content.' : 'Open needs a recorded CDN route. Refresh builds to check again, or inspect Source details.'}</p></div>;
}
function Field({ label, value }: { label: string; value: string }) { return <><dt>{label}</dt><dd className="mono">{value}</dd></>; }
function CdnConfigHint({ build, open }: { build: Build; open?: { cdnConfig: string; region: string } }) {
  // Only the open build's own source region decides whether switching would change anything.
  if (open && newerRegionCdnConfigs(build, open.region, open.cdnConfig).length) return <p className="dv-note" role="note"><strong>{open.region.toUpperCase()} now advertises a different CDN configuration.</strong> Your open build keeps <code>{short(open.cdnConfig)}</code>. Open again to switch; retained content is reused.</p>;
  const tied = newestGroupSize(build), earlier = build.cdnConfigHistoryCount - tied;
  if (tied > 1) return <p className="dv-note" role="note"><strong>Several CDN configurations in one check.</strong> {tied} CDN configurations were advertised in the same check{earlier ? ` and ${earlier} earlier ones are kept` : ''}. See Source details. The build is the same.</p>;
  if (earlier < 1) return null;
  return <p className="dv-note" role="note"><strong>CDN configuration republished.</strong> {earlier === 1 ? '1 earlier CDN configuration is' : `${earlier} earlier CDN configurations are`} kept in Source details. The build is the same.</p>;
}
function CdnConfigHistory({ build, openCdnConfig }: { build: Build; openCdnConfig?: string }) {
  const labels = historyLabels(build), tied = newestGroupSize(build) > 1, omitted = build.cdnConfigHistoryCount - build.cdnConfigHistory.length;
  return <><h3>CDN configuration history</h3><p>{tied ? 'Several CDN configurations were advertised in the same check; their order is not significant. Earlier ones follow.' : 'Newest first.'} A republished CDN configuration only changes the archive list; it is the same build. Earlier ones keep their original receipts.</p>{build.cdnConfigHistory.map((entry, i) => <section key={entry.cdnConfig}><h4>{labels[i]} · <span className="mono">{short(entry.cdnConfig)}</span>{entry.cdnConfig === openCdnConfig && ' · Used by the open build'}</h4><dl className="identity"><Field label="CDN configuration" value={entry.cdnConfig}/><dt>First observed</dt><dd>{stamp(entry.firstSeenAt)}</dd><dt>Last observed</dt><dd>{stamp(entry.lastSeenAt)}</dd><dt>Advertised at last check</dt><dd>{regions(entry.advertisedRegions)}</dd></dl>{entry.sourceReceipts.map(receipt => <section className="receipt" key={receipt.region}><h4>{receipt.region.toUpperCase()} · {receipt.role}</h4><dl className="identity"><Field label="Requested URL" value={receipt.url}/><dt>Retrieved</dt><dd>{stamp(receipt.retrievedAt)}</dd><Field label="SHA-256" value={receipt.sha256}/><dt>Document length / row</dt><dd>{receipt.byteLength} bytes · {receipt.rowOrdinal === null ? 'No row ordinal' : `Row ${receipt.rowOrdinal}`}</dd></dl></section>)}</section>)}{omitted > 0 && <p>{omitted === 1 ? '1 older CDN configuration is' : `${omitted} older CDN configurations are`} not shown here. {build.cdnConfigHistoryCount} in total; the catalog keeps all of them with their receipts.</p>}</>;
}
function SourceDialog({ build, status, openCdnConfig, onClose }: { build: Build; status?: ProductDiscoveryStatus; openCdnConfig?: string; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const previous = useRef<HTMLElement | null>(null);
  useEffect(() => { previous.current = document.activeElement as HTMLElement; dialog.current?.showModal(); return () => { dialog.current?.close(); previous.current?.focus(); }; }, []);
  return <dialog ref={dialog} aria-labelledby="source-title" onCancel={e => { e.preventDefault(); onClose(); }}><div className="dialog-heading"><div><div className="eyebrow">Discovery evidence</div><h2 id="source-title">Source details</h2></div><button autoFocus onClick={onClose}>Close</button></div><div className="dialog-body"><p><code>{build.productId}</code> · {build.version} · Build {build.buildId}</p><p>Checked regions for <code>{build.productId}</code>: {regions(status?.checkedRegions ?? [])} · Last attempt: {stamp(status?.lastCheckedAt ?? null)}</p><h3>Exact build references</h3><dl className="identity"><Field label="Build number" value={build.buildId}/><Field label="Build configuration" value={build.buildConfig}/><Field label="CDN configuration" value={build.cdnConfig}/><Field label="Product configuration" value={build.productConfig === null ? 'Not supplied' : build.productConfig}/><Field label="Exact identity" value={build.id}/><dt>First observed here</dt><dd>{stamp(build.firstSeenAt)}</dd><dt>Last observed</dt><dd>{stamp(build.lastSeenAt)}</dd></dl><CdnConfigHistory build={build} openCdnConfig={openCdnConfig}/><details><summary>Original identity fields</summary><dl className="identity">{Object.entries(build.identityFields).map(([name, value]) => <Field key={name} label={name} value={value === '' ? '(empty value)' : value}/>)}</dl></details><h3>Regional route evidence</h3><p>{regions(build.routeRegions)}. Route evidence is separate from advertisement-region grouping. The Download source control selects one eligible saved regional source for Open; that choice does not change the exact build identity or its grouped advertisements.</p><h3>Original receipts</h3>{build.sourceReceipts.length ? build.sourceReceipts.map((receipt, i) => <section className="receipt" key={i}><h4>{receipt.region.toUpperCase()} · {receipt.role}</h4><dl className="identity"><Field label="Requested URL" value={receipt.url}/><dt>Retrieved</dt><dd>{stamp(receipt.retrievedAt)}</dd><Field label="SHA-256" value={receipt.sha256}/><dt>Document length / row</dt><dd>{receipt.byteLength} bytes · {receipt.rowOrdinal === null ? 'No row ordinal' : `Row ${receipt.rowOrdinal}`}</dd></dl></section>) : <p>No receipts recorded.</p>}{!!status?.errors.length && <><h3>Current attempt problems</h3><ul>{status.errors.map((p, i) => <li key={i}>{p.region || 'Source'}: {p.message} ({p.code})</li>)}</ul></>}</div></dialog>;
}
