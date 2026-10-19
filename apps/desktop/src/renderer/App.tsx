import {DownloadActivity,ActivityTrigger,useDownloadActivity} from './DownloadActivity';
import {stageMeasurement,retentionActivity,type ActivityTask,type ActivityIdentity} from './download-activity-state';
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
import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import './discovery.css';
import type { Build, DiscoveryPage, Product, EngineProgress, RetentionSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge, DiscoveryFilter } from '../shared/bridge';
import { historyLabels, newerRegionCdnConfigs, newestGroupSize } from '../shared/cdn-config';
import { TableLoadBanner, useTableLoading } from './useTableLoading';
import { useWorkspaceFlow, type Opening } from './useWorkspaceFlow';
import { PreparationView, HomeView } from './WorkspaceViews';
import brand from '../../../../branding/vibeminer.svg';
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
const savedProduct = () => { try { return localStorage.getItem('vibeminer.product') || 'wow'; } catch { return 'wow'; } };
export function App({ bridge = window.discovery }: { bridge?: DiscoveryBridge }) {
  const [products, setProducts] = useState<Product[]>([]);
  const [productId, setProductId] = useState(savedProduct);
  const [chosen, setChosen] = useState(false);
  const flow = useWorkspaceFlow(bridge, productId);
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
  useEffect(() => { let disposed = false; void bridge.products().then(reply => {
    if (disposed) return;
    if (reply.ok) { setProducts(reply.value.products); if (!reply.value.products.some(p => p.id === productId)) setProductId('wow'); }
    else setProblem(`${reply.error.message} (${reply.error.code})`);
  }).catch(() => { if (!disposed) setProblem('The local engine is unavailable. Restart VibeMiner to reconnect.'); }); return () => { disposed = true; }; }, [bridge]);
  const product = products.find(p => p.id === productId);
  const productFocus = useRef(false);
  useEffect(() => { if (productFocus.current) { productFocus.current = false; document.getElementById('product')?.focus(); } }, [productId]);
  function choose(id: string) { productFocus.current = document.activeElement?.id === 'product'; setChosen(true); setProductId(id); try { localStorage.setItem('vibeminer.product', id); } catch { /* Session selection still works. */ } }
  return <DownloadActivity tasks={tasks} contextKey={activityContext} onInfoAction={(info,action)=>{if(action==='refreshStatus'&&info.key===activityContext&&info.target&&workspace?.download&&info.target.ledgerId===workspace.download.ledgerId&&matches(info.target))retention.refresh(info.target);}} onAction={(key,action)=>taskActions.get(key)?.get(action)?.()}>
    <div className="discovery-screen" hidden={flow.view !== 'discovery'} inert={flow.view !== 'discovery'}><a className="skip" href="#builds">Skip to builds</a>
      <DiscoveryWorkspace key={productId} active={flow.view === 'discovery'} productId={productId} product={product} autoRefresh={chosen} bridge={bridge} acknowledgedBuild={flow.operation?.result?.build} acknowledgedRegion={flow.operation?.result?.sourceReference.region} downloads={downloads} opening={flow.operation} onShowOpening={() => flow.showActive()} onCancelOpening={() => { void flow.cancel(); }} onOpen={(build, region) => { void flow.open(build, region, product?.label || productId); }}
        productControl={<div className="dv-field"><label htmlFor="product">Product</label><select id="product" className="dv-control dv-product" value={productId} onChange={e => choose(e.target.value)}>{products.length ? products.map(p => <option value={p.id} key={p.id}>{p.id} · World of Warcraft · {p.label}</option>) : <option value={productId}>{productId}</option>}</select></div>}
        fixtureLabel={bridge.fixtureLabel ?? undefined} problem={problem}/>
    </div>
    {flow.view === 'preparation' && flow.operation && <PreparationView tableActivity={null} operation={flow.operation} notice={flow.notice} fixtureLabel={bridge.fixtureLabel} onBack={flow.detach} onCancel={() => { void flow.cancel(); }} onRetry={() => { const op = flow.operation!; void flow.open(op.build, op.sourceRegion, op.label); }} onEnter={flow.enterVerified}/>}
    {flow.view === 'home' && flow.operation?.result && <HomeView activityNavigation={activityNavigation} effectsLoading={effectsLoading} effectsAvailable={['item-effects-links-v1','item-effects-inputs-v1','item-effect-spell-names-v1'].every(cap=>retention.features.includes(cap))&&!!bridge.itemEffectsRead&&!!bridge.itemEffectsPrepareLinks&&!!bridge.itemEffectsPage&&!!bridge.itemEffectDetails&&!!bridge.itemEffectPrepareDetails&&!!bridge.itemEffectNames&&!!bridge.itemEffectPrepareNames} creatureDisplays={creatureDisplays} creatureDisplayAvailable={retention.features.includes('creature-display-metadata-v1')&&!!bridge.creatureDisplayRead&&!!bridge.creatureDisplayPrepare} creaturesLoading={creaturesLoading} creaturesAvailable={retention.features.includes('creatures-v1')&&!!bridge.creaturesData&&!!bridge.creaturesPrepare&&!!bridge.creaturesPage&&!!bridge.creatureInspect} classificationsLoading={classificationsLoading} classificationsAvailable={retention.features.includes('item-classifications-v1')} subclassAvailable={retention.features.includes('item-subclass-labels-v1')} iconsLoading={iconsLoading} iconsAvailable={retention.features.includes('item-icons-v1')} itemsLoading={itemsLoading} itemsAvailable={retention.features.includes('items-v1')} retention={retention} tableLoading={tableLoading} operation={{ ...flow.operation, result: flow.operation.result }} fixtureLabel={bridge.fixtureLabel} onChoose={flow.detach} bridge={bridge} onReopen={() => { const op = flow.operation!; void flow.open(op.build, op.sourceRegion, op.label); }}/>}
  </DownloadActivity>;
}

function DiscoveryWorkspace({ active: visibleView, productId, product, autoRefresh, bridge, productControl, fixtureLabel, problem, downloads, opening, onShowOpening, onCancelOpening, onOpen, acknowledgedBuild, acknowledgedRegion }: { active: boolean; productId: string; product?: Product; autoRefresh: boolean; bridge: DiscoveryBridge; productControl: ReactNode; fixtureLabel?: string; problem: string; downloads: Map<string, BuildDownload>; opening: Opening | null; onShowOpening: () => void; onCancelOpening: () => void; acknowledgedBuild?: Build; acknowledgedRegion?: string; onOpen: (build: Build, sourceRegion: string) => void }) {
  const [page, setPage] = useState<DiscoveryPage | null>(null);
  useEffect(() => {
    if (!acknowledgedBuild || acknowledgedBuild.productId !== productId) return;
    setPage(previous => {
      if (!previous?.builds.some(build => build.id === acknowledgedBuild.id && (build.isNew !== acknowledgedBuild.isNew || build.isLocal !== acknowledgedBuild.isLocal))) return previous;
      return { ...previous, builds: previous.builds.map(build => build.id === acknowledgedBuild.id ? { ...build, isNew: acknowledgedBuild.isNew, isLocal: acknowledgedBuild.isLocal } : build) };
    });
  }, [acknowledgedBuild, productId]);
  const [filter, setFilter] = useState<DiscoveryFilter>('all');
  const [query, setQuery] = useState('');
  const [region, setRegion] = useState('all');
  const [selectedId, setSelectedId] = useState<string | null>(() => { try { return localStorage.getItem(`vibeminer.selection.${productId}`); } catch { return null; } });
  const [busy, setBusy] = useState(false);
  const [reading, setReading] = useState(true);
  const [stopping, setStopping] = useState(false);
  const [activity, setActivity] = useState('');
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
  function accept(value: DiscoveryPage, append = false) {
    if (value.productId !== productId) throw new Error('Product mismatch');
    setPage(previous => append && previous ? { ...value, builds: [...previous.builds, ...value.builds.filter(b => !previous.builds.some(old => old.id === b.id))] } : value);
  }
  async function read(nextFilter: DiscoveryFilter, cursor?: string, restoreSelection = false) {
    const version = ++generation.current;
    setReading(true);
    try {
      const reply = await bridge.read({ productId, filter: nextFilter, limit: 50, ...(cursor ? { cursor } : {}) });
      if (disposed.current || version !== generation.current) return;
      if (reply.ok) {
        let windowPage = reply.value;
        const wanted = restoreSelection ? currentSelection.current : null;
        while (wanted && windowPage.nextCursor && windowPage.builds.length < 500 && !windowPage.builds.some(build => build.id === wanted)) {
          const next = await bridge.read({ productId, filter: nextFilter, limit: Math.min(50, 500 - windowPage.builds.length), cursor: windowPage.nextCursor });
          if (disposed.current || version !== generation.current) return;
          if (!next.ok) { setError(`${next.error.message} (${next.error.code})`); return; }
          if (next.value.productId !== productId) throw new Error('Product mismatch');
          const additions = next.value.builds.filter(build => !windowPage.builds.some(old => old.id === build.id));
          if (!additions.length && next.value.nextCursor) throw new Error('Paging did not advance.');
          windowPage = { ...next.value, builds: [...windowPage.builds, ...additions] };
        }
        accept(windowPage, !!cursor); setError('');
        if (wanted && !windowPage.builds.some(build => build.id === wanted)) setSelectionNotice(windowPage.nextCursor ? 'The saved exact selection is outside the 500-build display window. No different build has been selected.' : 'The saved exact selection is not returned by this view. No different build has been selected.');
      } else { if (refusedCodes.includes(reply.error.code)) refusedRead.current = true; setError(`${reply.error.message} (${reply.error.code})`); }
    } catch { if (!disposed.current && version === generation.current) setError('The local engine connection failed. Restart VibeMiner to reconnect.'); }
    finally { if (!disposed.current && version === generation.current) setReading(false); }
  }
  async function refresh() {
    if (active.current || refusedRead.current || refused) return;
    const id = crypto.randomUUID(); active.current = id;
    ++generation.current; setReading(false); setBusy(true); setStopping(false); setError(''); setActivity('Checking official metadata'); setMessage(`Checking official metadata for ${productId}…`);
    try {
      const reply = await bridge.refresh(productId, id);
      if (disposed.current || active.current !== id) return;
      ++generation.current;
      if (reply.ok) {
        // A refreshed view must not keep showing a pre-refresh copy of a selection it no longer returns, even if it exits early.
        lastSelection.current = null;
        const originalWindow = currentPage.current;
        const wantedCount = Math.min(500, originalWindow?.builds.length || 0);
        const wantedSelection = originalWindow?.builds.some(build => build.id === currentSelection.current) ? currentSelection.current : null;
        const contextFilter = latestFilter.current;
        const refreshGeneration = generation.current;
        let windowPage = reply.value;
        if (contextFilter !== 'all') {
          const first = await bridge.read({ productId, filter: contextFilter, limit: 50 });
          if (!first.ok) throw new Error('Refreshed view could not be read.');
          windowPage = first.value;
        }
        while (windowPage.nextCursor && windowPage.builds.length < 500 && (windowPage.builds.length < wantedCount || wantedSelection && !windowPage.builds.some(build => build.id === wantedSelection))) {
          const next = await bridge.read({ productId, filter: contextFilter, limit: Math.min(50, 500 - windowPage.builds.length), cursor: windowPage.nextCursor });
          if (disposed.current) return;
          if (refreshGeneration !== generation.current) { setMessage('Discovery completed. Showing the current filtered view.'); return; }
          if (!next.ok) throw new Error('Refreshed view could not be read.');
          if (next.value.productId !== productId) throw new Error('Product mismatch');
          const additions = next.value.builds.filter(build => !windowPage.builds.some(old => old.id === build.id));
          if (!additions.length && next.value.nextCursor) throw new Error('Paging did not advance.');
          windowPage = { ...next.value, builds: [...windowPage.builds, ...additions] };
        }
        if (disposed.current) return;
        if (refreshGeneration !== generation.current || contextFilter !== latestFilter.current) { setMessage('Discovery completed. Showing the current filtered view.'); return; }
        accept(windowPage);
        setSelectionNotice(wantedSelection && !windowPage.builds.some(build => build.id === wantedSelection) ? windowPage.nextCursor ? 'The selected exact build is outside the refreshed 500-build display window. No different build has been selected.' : 'The selected exact build is no longer returned by this view after refresh. No different build has been selected.' : '');
        const value = reply.value;
        setMessage(value.connection === 'offline' ? 'Could not refresh. Showing saved observations where available.' : value.connection === 'partial' ? 'Check completed with regional problems. Previous successful evidence is retained.' : `Discovery complete. Checked ${regions(value.checkedRegions)}.`);
      } else if (reply.error.code === 'CANCELED') setMessage('Discovery canceled. Previous observations are still available.');
      else { setError(`${reply.error.message} (${reply.error.code})`); setMessage('Discovery could not complete. Previous observations are still available.'); }
    } catch { if (!disposed.current) { setError('The refreshed view could not be completed. Previous observations remain displayed; retry discovery to read a coherent window.'); setMessage('Discovery could not complete the refreshed view. Previous observations are still displayed.'); } }
    finally { if (!disposed.current && active.current === id) { active.current = null; setBusy(false); setStopping(false); setActivity(''); } }
  }
  useEffect(() => {
    disposed.current = false;
    const off = bridge.onProgress(progress => { if (progress.requestId === active.current && !disposed.current) setActivity(progress.stage); });
    void read('all', undefined, true).then(() => { if (autoRefresh && !disposed.current) void refresh(); });
    return () => { disposed.current = true; ++generation.current; off(); if (active.current) void bridge.cancel(active.current).catch(() => {}); };
  }, []);
  async function cancel() {
    if (!active.current || stopping) return;
    setStopping(true); setMessage('Stopping discovery…');
    try { const reply = await bridge.cancel(active.current); if (!disposed.current && !reply.ok) setError(`${reply.error.message} (${reply.error.code}). Waiting for discovery to stop safely.`); }
    catch { if (!disposed.current) setError('Cancellation could not be delivered. Waiting for the local engine.'); }
  }
  function changeFilter(value: DiscoveryFilter) { setFilter(value); setError(''); void read(value); }
  const filtered = useMemo(() => (page?.builds || []).filter(b => (filter === 'all' || filter === 'local' && b.isLocal || filter === 'advertised' && b.isAdvertised) && (region === 'all' || b.advertisedRegions.includes(region)) && `${b.version} ${b.buildId}`.toLowerCase().includes(query.toLowerCase())), [page, query, region, filter]);
  // A selection hidden by filters stays selected and inspectable while its row is still loaded.
  const loadedSelection = (page?.builds || []).find(b => b.id === selectedId);
  if (loadedSelection) lastSelection.current = loadedSelection;
  // Only an engine-side visibility filter (Prepared, Advertised) can hide a loaded selection; a refresh that drops it still reports it as gone.
  const selected = loadedSelection ?? (filter !== 'all' && lastSelection.current?.id === selectedId ? lastSelection.current : undefined);
  const hidden = !!selectedId && !filtered.some(b => b.id === selectedId);
  const activeIndex = Math.max(0, filtered.findIndex(b => b.id === selectedId));
  const availableRegions = Array.from(new Set([...(page?.checkedRegions || []), ...(page?.builds.flatMap(b => b.advertisedRegions) || [])])).sort();
  function select(build: Build) { setSelectionNotice(''); setSelectedId(build.id); try { localStorage.setItem(`vibeminer.selection.${productId}`, build.id); } catch { /* Selection stays in this session. */ } }
  // Clear filters removes its own button, so focus moves to All, the control it just set.
  function clearFilters() { setQuery(''); setRegion('all'); if (filter !== 'all') changeFilter('all'); document.getElementById('visibility-all')?.focus(); }
  const failed = page?.connection === 'offline' || (page?.errors.length || 0) > 0;
  const nothingSaved = !!page && !reading && !page.builds.length && filter === 'all';
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
  const refreshLabel = busy ? stopping ? 'Stopping discovery…' : 'Cancel refresh' : error || failed ? 'Retry discovery' : 'Refresh builds';
  const openingHere = opening && selected && opening.build.id === selected.id && opening.build.productId === productId ? opening : null;
  const openingElsewhere = opening && !openingHere ? opening : null;
  const openingBox = (op: Opening) => <div className="dv-state"><strong>{op.status === 'pending' || op.status === 'stopping' ? 'Opening build' : op.status === 'succeeded' ? 'Verified overview available' : op.status === 'canceled' ? 'Opening canceled' : 'Opening needs attention'}</strong>{op.build.version} · source {op.sourceRegion.toUpperCase()}<br/>{op.progress?.stage || (op.status === 'stopping' ? 'Stopping preparation…' : 'Exact configuration context is preserved.')}<div className="dv-row"><button className="dv-btn" onClick={onShowOpening}>{op.status === 'succeeded' ? 'View verified overview' : 'View preparation'}</button>{(op.status === 'pending' || op.status === 'stopping') && <button className="dv-btn" aria-disabled={op.status === 'stopping'} onClick={() => { if (op.status === 'pending') onCancelOpening(); }}>{op.status === 'stopping' ? 'Stopping opening…' : 'Cancel opening'}</button>}</div></div>;
  const notices = <>
    {problem && (refusedCatalog(problem) ? <RefusedNotice text={problem}/> : <div role="alert" className="dv-notice error"><div><strong>Products unavailable</strong>{problem}</div></div>)}
    {error && !(refusedCatalog(error) && refusedCatalog(problem)) && (refusedCatalog(error) ? <RefusedNotice text={error}/> : <div role="alert" className="dv-notice error"><div><><strong>Discovery problem</strong>{error} Saved observations remain available. Retry checks the complete product scope; engine failures require restarting VibeMiner.</></div></div>)}
    {failed && !refused && <div className={`dv-notice${page?.connection === 'offline' ? ' error' : ''}`}><div><strong>{page?.connection === 'offline' ? 'Refresh unavailable' : 'Some discovery evidence is incomplete'}</strong>{page?.hasSavedHistory ? 'Showing saved evidence. A failed check does not remove earlier advertisements.' : 'No saved observations are available for this product.'} Retry discovery checks the complete product scope again.<ul>{page?.errors.map((p, i) => <li key={i}>{p.region?.toUpperCase() || 'Source'}: {p.message} <code>({p.code})</code></li>)}</ul></div>{!busy && <button className="dv-btn" onClick={() => { refreshButton.current?.focus(); void refresh(); }}>Retry discovery</button>}</div>}
    {!refused && hidden && filtered.length > 0 && (selected || filter !== 'all' || query || region !== 'all') && <div className="dv-notice"><div><strong>Selected build is hidden by these filters</strong>{selected ? `${selected.version} is still selected.` : 'The selected build is still selected.'} Clear filters to see it in the list.</div><button className="dv-btn" onClick={clearFilters}>Clear filters</button></div>}
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
  return <div className={`dv-window${compact ? ' dv-compact' : ''}`} ref={windowRef}>
    <header className="dv-toolbar" ref={toolbarRef}>
      <div className="dv-brand"><img src={brand} alt="VibeMiner"/><span className="dv-word" aria-hidden="true">Vibe<span>Miner</span></span></div>
      <div className="dv-divider" aria-hidden="true"/>
      {productControl}
      {!refused && <button ref={refreshButton} className="dv-btn" aria-keyshortcuts="F5" onClick={() => { if (busy) void cancel(); else void refresh(); }} aria-disabled={stopping}>{!stopping && <span aria-hidden="true">{busy ? '✕' : '↻'}</span>}{refreshLabel}</button>}
      {fixtureLabel && <span className="dv-fixture"><strong>{fixtureLabel}</strong><span> · Synthetic identities and receipts.</span></span>}
      <div className="dv-spacer"/>
      <ActivityTrigger compact/>
    </header>
    <main className={`dv-body${refused || nothingSaved && !selected ? ' no-inspector' : ''}`} id="discovery">
      <section className="dv-pane" aria-labelledby="builds-heading">
        <div className="dv-pane-head">
          <h1 id="builds-heading" tabIndex={-1}>Builds</h1>{filtered.length > 0 && <span className="dv-n" aria-label={`${filtered.length}${page?.nextCursor ? ' or more' : ''} builds shown`}>{filtered.length}{page?.nextCursor ? '+' : ''}</span>}
          <input ref={searchInput} id="search" className="dv-control dv-search" type="search" placeholder="Filter version or build" aria-label="Filter version or build number" aria-keyshortcuts="Control+F" value={query} disabled={nothingSaved || refused} onChange={e => { setQuery(e.target.value); }}/>
          <select id="region" className="dv-control" aria-label="Advertisement region" value={region} disabled={nothingSaved || refused} onChange={e => { setRegion(e.target.value); }}><option value="all">All regions</option>{availableRegions.map(r => <option value={r} key={r}>{r.toUpperCase()}</option>)}</select>
          <div className="dv-segmented" role="group" aria-label="Build visibility">{(['all', 'local', 'advertised'] as const).map(f => <button key={f} id={`visibility-${f}`} aria-pressed={filter === f} disabled={nothingSaved || refused} onClick={() => changeFilter(f)}>{f === 'all' ? 'All' : f === 'local' ? 'Prepared' : 'Advertised'}</button>)}</div>
        </div>
        {notices}
        <div className={`dv-list${notices.props.children.some(Boolean) ? '' : ' flush'}`} aria-busy={reading}>
          {filtered.length > 0 && <>
            <div className="dv-cols dv-thead" aria-hidden="true"><span>Version</span><span>Build</span><span>Advertised in</span><span>Publication</span><span>Downloaded</span><span>In VibeMiner</span></div>
            <div role="listbox" id="builds" aria-label="Builds" tabIndex={0} aria-activedescendant={`build-option-${filtered[activeIndex].id}`} onKeyDown={listKey}>{filtered.map(build => { const download = downloads.get(`${build.productId}:${build.id}`), job = download?.job, percent = downloadedPercent(job), running = !!download?.running; return <div role="option" key={build.id} id={`build-option-${build.id}`} data-build-id={build.id} className={`dv-cols build-row${filtered[activeIndex].id === build.id ? ' active' : ''}`} aria-selected={selectedId === build.id} onClick={() => select(build)}>
              <span className="dv-version">{build.version || 'Version not supplied'}<span className="visually-hidden">, Config {short(build.buildConfig)}, Identity {short(build.id)}</span></span>
              <span className="dv-dim"><span className="visually-hidden">Build </span>{build.buildId || 'not supplied'}</span>
              <span><span className="visually-hidden">Advertised in </span>{build.advertisedRegions.length ? build.advertisedRegions.map(r => <span className="dv-pill" key={r}>{r.toUpperCase()}</span>) : <span className="dv-dim">None at last check</span>}</span>
              <span>{build.isAdvertised ? <span className="dv-pill">Advertised</span> : <span className="dv-dim">Not advertised</span>}</span>
              <span><span className="visually-hidden">Downloaded </span>{!job && !build.isLocal ? <span className="dv-dim">Not opened</span> : percent === null ? <span className="dv-dim">{job?.coverage.presenceState === 'assessing' ? 'Checking…' : 'Not checked'}</span> : <span className={`dv-meter${percent === '100%' ? ' done' : ''}`}><span className="dv-track" aria-hidden="true"><i style={{ width: percent === '<0.1%' ? '0%' : percent }}/></span>{percent}</span>}{running && <><span className="dv-pulse" aria-hidden="true"/><span className="visually-hidden">, downloading</span></>}</span>
              <span>{build.isLocal && <span className="dv-pill">Prepared</span>}{build.isNew && <span className="dv-pill gold">New to VibeMiner</span>}{!build.isLocal && !build.isNew && <span className="dv-dim">—</span>}</span>
            </div>; })}</div>
            {page?.nextCursor && <div className="dv-load-more"><button className="dv-btn" disabled={reading || busy || page.builds.length >= 500} onClick={() => void read(filter, page.nextCursor!)}>{reading ? 'Loading…' : page.builds.length >= 500 ? '500-build display limit reached' : 'Load more builds'}</button><span>{page.builds.length >= 500 ? 'This view displays the first 500 builds. Search and region controls only refine loaded builds; searching beyond this display window is not available.' : 'Filters apply to loaded builds only.'}</span></div>}
          </>}
          {refused && <div className="dv-empty"><div><h2>Saved catalog not opened</h2><p>No builds are shown because the saved catalog was not opened. Follow the steps in the notice above.</p></div></div>}
          {!refused && !filtered.length && <div className="dv-empty"><div><h2>{reading ? 'Reading saved observations…' : busy ? 'Checking official metadata…' : filter === 'local' ? 'No prepared builds' : page?.connection === 'offline' && !page.hasSavedHistory ? `Could not check ${productId}` : query || region !== 'all' ? 'No matching loaded builds' : page?.hasSavedHistory ? 'No builds match this view' : 'Your research starts with a build'}</h2><p>{filter === 'local' ? 'Saved discovery observations are available in All. A saved observation is separate from a prepared build.' : page?.connection === 'offline' && !page.hasSavedHistory ? 'Official metadata could not be reached, and no saved observations are available. Select Retry discovery to check this product again.' : query || region !== 'all' ? 'Try another version, build number, or advertisement region. Filters cover loaded builds only.' : page?.connection === 'online' ? `No builds advertised in the checked scope at ${stamp(page.lastCheckedAt)}.` : `No saved observations for ${productId}. Refresh builds checks Blizzard's official version service.`}</p>{(query || region !== 'all' || filter !== 'all') && <button className="dv-btn" onClick={clearFilters}>Clear filters</button>}</div></div>}
        </div>
      </section>
      {!refused && !(nothingSaved && !selected) && <aside className="dv-inspector" id="inspector" aria-labelledby="inspector-heading" inert={!!activity_?.expanded}>{selected ? <>
        <div className="dv-insp-head"><div className="dv-label">Selected build</div><h2 id="inspector-heading" className="dv-selectable">{selected.version}</h2><p className="inspector-product">World of Warcraft · {product?.label || productId} · <code>{productId}</code></p></div>
        <div className="dv-insp-body">
          {openingHere ? openingBox(openingHere) : <div className="dv-state"><strong>{selected.isLocal ? 'Prepared locally' : 'Not prepared'}</strong>{selected.isLocal ? downloads.get(`${selected.productId}:${selected.id}`)?.job.mode === 'downloadAll' ? 'Local evidence recorded. Download all is on for this build.' : 'Local evidence recorded. Game content downloads on demand.' : 'Opening verifies configs and measures download size.'}</div>}
          <div className="dv-label">Identity</div>
          <dl><dt>Build</dt><dd className="dv-selectable">{selected.buildId || 'Not supplied'}</dd>
            <dt>Build config</dt><dd className="mono dv-selectable">{selected.buildConfig}</dd>
            <dt>CDN config</dt><dd className="mono dv-selectable">{selected.cdnConfig}</dd>
            <dt>Product config</dt><dd className="mono dv-selectable">{missing(selected.identityFields.ProductConfig)}</dd>
            <dt>Key ring</dt><dd className="mono dv-selectable">{missing(selected.identityFields.KeyRing)}</dd>
            <dt>Identity</dt><dd className="mono dv-selectable">{selected.id}</dd></dl>
          <CdnConfigHint build={selected} open={acknowledgedBuild?.productId === productId && acknowledgedBuild.id === selected.id && acknowledgedRegion ? { cdnConfig: acknowledgedBuild.cdnConfig, region: acknowledgedRegion } : undefined}/>
          {(() => { const download = downloads.get(`${selected.productId}:${selected.id}`), job = download?.job, percent = downloadedPercent(job); return job ? <><div className="dv-label">Download</div><dl><dt>Downloaded</dt><dd>{percent === null ? 'Not checked' : `${exactBytes(job.coverage.presentBytes)} of ${exactBytes(job.totalEncodedBytes)} · ${percent}`}</dd><dt>Scope</dt><dd>Download manifest, all variants (encoded)</dd><dt>Checked</dt><dd>{job.coverage.presenceState === 'assessing' ? 'Checking now' : job.coverage.presenceAssessedAt ? stamp(job.coverage.presenceAssessedAt) : 'Never'}</dd><dt>Source</dt><dd>{job.target.sourceRegion.toUpperCase()}</dd>{job.coverage.corruptObjects !== '0' && <><dt>Corrupt</dt><dd>{exactCount(job.coverage.corruptObjects)} objects found corrupt at the check; not counted as downloaded</dd></>}{download!.running && <><dt>Activity</dt><dd>Download all running</dd></>}</dl></> : null; })()}
          <div className="dv-label">Observation</div>
          <dl><dt>Publication</dt><dd>{selected.isAdvertised ? 'Advertised at last check' : 'Not advertised at last check'}</dd>
            <dt>Advertised in</dt><dd>{selected.advertisedRegions.length ? regions(selected.advertisedRegions) : 'None at last check'}</dd>
            <dt>CDN routes</dt><dd>{regions(routeRegions(selected))}{selected.advertisedRegions.filter(r => !routeRegions(selected).includes(r)).map(r => ` · ${r.toUpperCase()} has no CDN route`).join('')}</dd>
            <dt>Last seen</dt><dd>{stamp(selected.lastSeenAt)}</dd>
            <dt>File availability</dt><dd>Not checked</dd></dl>
          {selected.identityFields.KeyRing && <p className="dv-note">A key ring reference does not mean its keys are available.</p>}
        </div>
        <BuildOpenAction key={selected.id} build={selected} onOpen={onOpen} onSource={() => setSourceOpen(true)}/>
        {sourceOpen && <SourceDialog build={selected} page={page!} openCdnConfig={acknowledgedBuild?.productId === productId && acknowledgedBuild.id === selected.id ? acknowledgedBuild.cdnConfig : undefined} onClose={() => setSourceOpen(false)}/>}
      </> : <><h2 id="inspector-heading" className="visually-hidden">Build inspector</h2><div className="dv-insp-empty"><div><strong>{hidden ? 'Selected build is hidden' : 'No build selected'}</strong><p>{hidden ? selectionNotice || 'Selected build is hidden by these filters or no longer appears in this page. Clear filters or load more to inspect it again.' : 'Select a build to see its exact identity.'}</p></div></div><div className="dv-insp-foot blocked"><button className="dv-btn primary" disabled>Open build</button><p className="dv-reason">Select a build with a recorded download source to open it.</p></div></>}</aside>}
    </main>
    <footer className="dv-statusbar" ref={statusRef}>
      {busy && <span className="dv-busy"><span className="dv-pulse" aria-hidden="true"/>{!stopping && <span aria-live="off">{activity}</span>}</span>}
      <span className="dv-secondary">Checked regions <strong>{regions(page?.checkedRegions || [])}</strong></span>
      <span>Last check <strong>{stamp(page?.lastCheckedAt || null)}</strong></span>
      <span role="status" aria-live="polite" className="dv-message">{stopping ? 'Stopping discovery…' : refused ? 'The saved catalog was not opened.' : message}</span>
      <span className="dv-spacer"/>
      {summary && <span className={activity_!.attention ? 'dv-warn' : 'dv-busy'} aria-live="off">{!activity_!.attention && activity_!.active && <span className="dv-pulse" aria-hidden="true"/>}{summary.label}{summary.measurement ? ` · ${summary.measurement}` : ''}</span>}
    </footer>
  </div>;
}
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
function SourceDialog({ build, page, openCdnConfig, onClose }: { build: Build; page: DiscoveryPage; openCdnConfig?: string; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const previous = useRef<HTMLElement | null>(null);
  useEffect(() => { previous.current = document.activeElement as HTMLElement; dialog.current?.showModal(); return () => { dialog.current?.close(); previous.current?.focus(); }; }, []);
  return <dialog ref={dialog} aria-labelledby="source-title" onCancel={e => { e.preventDefault(); onClose(); }}><div className="dialog-heading"><div><div className="eyebrow">Discovery evidence</div><h2 id="source-title">Source details</h2></div><button autoFocus onClick={onClose}>Close</button></div><div className="dialog-body"><p><code>{build.productId}</code> · {build.version} · Build {build.buildId}</p><p>Checked regions: {regions(page.checkedRegions)} · Last attempt: {stamp(page.lastCheckedAt)}</p><h3>Exact build references</h3><dl className="identity"><Field label="Build number" value={build.buildId}/><Field label="Build configuration" value={build.buildConfig}/><Field label="CDN configuration" value={build.cdnConfig}/><Field label="Product configuration" value={build.productConfig === null ? 'Not supplied' : build.productConfig}/><Field label="Exact identity" value={build.id}/><dt>First observed here</dt><dd>{stamp(build.firstSeenAt)}</dd><dt>Last observed</dt><dd>{stamp(build.lastSeenAt)}</dd></dl><CdnConfigHistory build={build} openCdnConfig={openCdnConfig}/><details><summary>Original identity fields</summary><dl className="identity">{Object.entries(build.identityFields).map(([name, value]) => <Field key={name} label={name} value={value === '' ? '(empty value)' : value}/>)}</dl></details><h3>Regional route evidence</h3><p>{regions(build.routeRegions)}. Route evidence is separate from advertisement-region grouping. The Download source control selects one eligible saved regional source for Open; that choice does not change the exact build identity or its grouped advertisements.</p><h3>Original receipts</h3>{build.sourceReceipts.length ? build.sourceReceipts.map((receipt, i) => <section className="receipt" key={i}><h4>{receipt.region.toUpperCase()} · {receipt.role}</h4><dl className="identity"><Field label="Requested URL" value={receipt.url}/><dt>Retrieved</dt><dd>{stamp(receipt.retrievedAt)}</dd><Field label="SHA-256" value={receipt.sha256}/><dt>Document length / row</dt><dd>{receipt.byteLength} bytes · {receipt.rowOrdinal === null ? 'No row ordinal' : `Row ${receipt.rowOrdinal}`}</dd></dl></section>) : <p>No receipts recorded.</p>}{page.errors.length > 0 && <><h3>Current attempt problems</h3><ul>{page.errors.map((p, i) => <li key={i}>{p.region || 'Source'}: {p.message} ({p.code})</li>)}</ul></>}</div></dialog>;
}
