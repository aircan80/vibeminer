import {DownloadActivity,ActivityTrigger} from './DownloadActivity';
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
import { RetentionActivity } from './Retention';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { Build, DiscoveryPage, Product, EngineProgress } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge, DiscoveryFilter } from '../shared/bridge';
import { TableLoadBanner, useTableLoading } from './useTableLoading';
import { useWorkspaceFlow } from './useWorkspaceFlow';
import { PreparationView, HomeView } from './WorkspaceViews';
import brand from '../../../../branding/vibeminer.svg';
const stamp = (value: string | null) => value ? new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : 'Not checked yet';
const regions = (values: string[]) => values.length ? values.map(x => x.toUpperCase()).join(', ') : 'None recorded';
const short = (value: string) => `${value.slice(0, 10)}…${value.slice(-4)}`;
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
  const activityContext=workspace?`${workspace.build.productId}:${workspace.build.id}:${workspace.sourceReference.region}:${workspace.sourceReference.generation}`:'discovery';
  const [problem, setProblem] = useState('');
  useEffect(() => { let disposed = false; void bridge.products().then(reply => {
    if (disposed) return;
    if (reply.ok) { setProducts(reply.value.products); if (!reply.value.products.some(p => p.id === productId)) setProductId('wow'); }
    else setProblem(`${reply.error.message} (${reply.error.code})`);
  }).catch(() => { if (!disposed) setProblem('The local engine is unavailable. Restart VibeMiner to reconnect.'); }); return () => { disposed = true; }; }, [bridge]);
  const product = products.find(p => p.id === productId);
  function choose(id: string) { setChosen(true); setProductId(id); try { localStorage.setItem('vibeminer.product', id); } catch { /* Session selection still works. */ } }
  return <DownloadActivity tasks={tasks} contextKey={activityContext} onInfoAction={(info,action)=>{if(action==='refreshStatus'&&info.key===activityContext&&info.target&&workspace?.download&&info.target.ledgerId===workspace.download.ledgerId&&matches(info.target))retention.refresh(info.target);}} onAction={(key,action)=>taskActions.get(key)?.get(action)?.()}>
    <div className="discovery-screen" hidden={flow.view !== 'discovery'} inert={flow.view !== 'discovery'}><a className="skip" href="#discovery">Skip to discovery</a>
    <header className="app-header">
      <a className="brand" href="#discovery"><img src={brand} alt=""/><span>Vibe<span>Miner</span></span></a>
      <nav className="location" aria-label="Current location"><strong>Discover builds</strong></nav>
      <span className="header-note">Standalone research · official CDN sources</span><ActivityTrigger/>
    </header>
    <main id="discovery" className="workspace">
      <DiscoveryWorkspace key={productId} productId={productId} product={product} autoRefresh={chosen} bridge={bridge} acknowledgedBuild={flow.operation?.result?.build} onOpen={(build, region) => { void flow.open(build, region, product?.label || productId); }} introduction={<>
        {flow.operation && <div className="opening-indicator"><p><strong>{flow.operation.status === 'pending' || flow.operation.status === 'stopping' ? 'Opening build' : flow.operation.status === 'succeeded' ? 'Verified overview available' : flow.operation.status === 'canceled' ? 'Opening canceled' : 'Opening needs attention'}</strong> · {flow.operation.build.productId} · {flow.operation.build.version}<br/>{flow.operation.progress?.stage || (flow.operation.status === 'stopping' ? 'Stopping preparation…' : 'Exact configuration context is preserved.')}</p><button onClick={()=>flow.showActive()}>{flow.operation.status === 'succeeded' ? 'View verified overview' : 'View preparation'}</button></div>}{bridge.fixtureLabel && <div className="fixture-label"><strong>{bridge.fixtureLabel}</strong> · Synthetic identities and receipts.</div>}
        <div className="intro"><div className="eyebrow">A simple starting point</div><h1 tabIndex={-1}>Which builds do you want to explore?</h1><p>Choose the exact Warcraft product to refresh its published build list.</p></div>
        <section className="product-picker" aria-label="Research product">
          <div className="product-label-row"><label htmlFor="product">Product</label><span>Select the exact CDN product you want to explore.</span></div>
          <select id="product" value={productId} onChange={e => choose(e.target.value)}>{products.length ? products.map(p => <option value={p.id} key={p.id}>{p.id} · World of Warcraft · {p.label}</option>) : <option value={productId}>{productId}</option>}</select>
          <p>Exact ID: <code>{productId}</code> · {product?.description || 'Exact source identity.'} Exact source identities remain separate.</p>
        </section>
        {problem && <div role="alert" className="notice error">{problem}</div>}
      </>}/>
    </main>
    <footer>Discovery saves source observations. Opening verifies build metadata and assesses published download sizing before Home. Background build downloads follow their saved policy.</footer></div>
    {flow.view === 'preparation' && flow.operation && <PreparationView tableActivity={null} operation={flow.operation} notice={flow.notice} fixtureLabel={bridge.fixtureLabel} onBack={flow.detach} onCancel={() => { void flow.cancel(); }} onRetry={() => { const op = flow.operation!; void flow.open(op.build, op.sourceRegion, op.label); }} onEnter={flow.enterVerified}/>}
    {flow.view === 'home' && flow.operation?.result && <HomeView activityNavigation={activityNavigation} effectsLoading={effectsLoading} effectsAvailable={['item-effects-links-v1','item-effects-inputs-v1','item-effect-spell-names-v1'].every(cap=>retention.features.includes(cap))&&!!bridge.itemEffectsRead&&!!bridge.itemEffectsPrepareLinks&&!!bridge.itemEffectsPage&&!!bridge.itemEffectDetails&&!!bridge.itemEffectPrepareDetails&&!!bridge.itemEffectNames&&!!bridge.itemEffectPrepareNames} creatureDisplays={creatureDisplays} creatureDisplayAvailable={retention.features.includes('creature-display-metadata-v1')&&!!bridge.creatureDisplayRead&&!!bridge.creatureDisplayPrepare} creaturesLoading={creaturesLoading} creaturesAvailable={retention.features.includes('creatures-v1')&&!!bridge.creaturesData&&!!bridge.creaturesPrepare&&!!bridge.creaturesPage&&!!bridge.creatureInspect} classificationsLoading={classificationsLoading} classificationsAvailable={retention.features.includes('item-classifications-v1')} subclassAvailable={retention.features.includes('item-subclass-labels-v1')} iconsLoading={iconsLoading} iconsAvailable={retention.features.includes('item-icons-v1')} itemsLoading={itemsLoading} itemsAvailable={retention.features.includes('items-v1')} retention={retention} tableLoading={tableLoading} operation={{ ...flow.operation, result: flow.operation.result }} fixtureLabel={bridge.fixtureLabel} onChoose={flow.detach} bridge={bridge} onReopen={() => { const op = flow.operation!; void flow.open(op.build, op.sourceRegion, op.label); }}/>}
  </DownloadActivity>;
}

function DiscoveryWorkspace({ productId, product, autoRefresh, bridge, introduction, onOpen, acknowledgedBuild }: { productId: string; product?: Product; autoRefresh: boolean; bridge: DiscoveryBridge; introduction: ReactNode; acknowledgedBuild?: Build; onOpen: (build: Build, sourceRegion: string) => void }) {
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
  const [sourceOpen, setSourceOpen] = useState(false);
  const [selectionNotice, setSelectionNotice] = useState('');
  const currentPage = useRef(page); currentPage.current = page;
  const currentSelection = useRef(selectedId); currentSelection.current = selectedId;
  const active = useRef<string | null>(null);
  const disposed = useRef(false);
  const generation = useRef(0);
  const resultHeading = useRef<HTMLHeadingElement>(null);
  const focusedRow = useRef<string | null>(null);
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
      } else setError(`${reply.error.message} (${reply.error.code})`);
    } catch { if (!disposed.current && version === generation.current) setError('The local engine connection failed. Restart VibeMiner to reconnect.'); }
    finally { if (!disposed.current && version === generation.current) setReading(false); }
  }
  async function refresh() {
    if (active.current) return;
    const id = crypto.randomUUID(); active.current = id;
    ++generation.current; setReading(false); setBusy(true); setStopping(false); setError(''); setActivity('Checking official metadata'); setMessage(`Checking official metadata for ${productId}…`);
    try {
      const reply = await bridge.refresh(productId, id);
      if (disposed.current || active.current !== id) return;
      ++generation.current;
      if (reply.ok) {
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
  useEffect(() => {
    if (focusedRow.current && !filtered.some(build => build.id === focusedRow.current)) {
      focusedRow.current = null;
      resultHeading.current?.focus();
      setMessage('The focused build is no longer visible. Focus returned to the build list heading.');
    }
  }, [filtered]);
  const selected = filtered.find(b => b.id === selectedId);
  const availableRegions = Array.from(new Set([...(page?.checkedRegions || []), ...(page?.builds.flatMap(b => b.advertisedRegions) || [])])).sort();
  function select(build: Build) { setSelectionNotice(''); setSelectedId(build.id); try { localStorage.setItem(`vibeminer.selection.${productId}`, build.id); } catch { /* Selection stays in this session. */ } }
  function clearFilters() { setQuery(''); setRegion('all'); if (filter !== 'all') changeFilter('all'); }
  const hidden = selectedId && !selected;
  const failed = page?.connection === 'offline' || (page?.errors.length || 0) > 0;
  return <div className="discovery-grid" onFocusCapture={event => { focusedRow.current = (event.target as HTMLElement).closest<HTMLElement>('.build-row')?.dataset.buildId || null; }}><section className="results" aria-labelledby="builds-heading">{introduction}<div className="section-heading"><div><h2 id="builds-heading" ref={resultHeading} tabIndex={-1}>Builds <span className="count">{filtered.length}{page?.nextCursor ? '+' : ''}</span></h2><p>Currently advertised and saved discovery observations in one list.</p></div><button className="refresh" onClick={() => { if (busy) void cancel(); else void refresh(); }} aria-disabled={stopping}>{!busy && <span aria-hidden="true">↻ </span>}{busy ? stopping ? 'Stopping discovery…' : 'Cancel discovery' : error || failed ? 'Retry discovery' : 'Refresh builds'}</button></div>{error && <div role="alert" className="notice error">{error}<p>Saved observations remain available. Retry checks the complete product scope; engine failures require restarting VibeMiner.</p></div>}{failed && <div className="notice warning"><strong>{page?.connection === 'offline' ? 'Refresh unavailable' : 'Some discovery evidence is incomplete'}</strong><p>Showing saved evidence. A failed check does not remove earlier advertisements. Retry discovery checks the complete product scope again.</p><ul>{page?.errors.map((p, i) => <li key={i}>{p.region?.toUpperCase() || 'Source'}: {p.message} <code>({p.code})</code></li>)}</ul></div>}<div className="filter-panel"><label className="search-label" htmlFor="search"><span className="visually-hidden">Version or build number</span><input id="search" type="search" placeholder="Filter version or build number" value={query} onChange={e => { setQuery(e.target.value); }}/></label><label className="region-label" htmlFor="region"><span className="visually-hidden">Advertisement region</span><select id="region" value={region} onChange={e => { setRegion(e.target.value); }}><option value="all">All advertised regions</option>{availableRegions.map(r => <option value={r} key={r}>{r.toUpperCase()}</option>)}</select></label><div className="visibility" role="group" aria-label="Build visibility">{(['all', 'local', 'advertised'] as const).map(f => <button key={f} aria-pressed={filter === f} onClick={() => changeFilter(f)}>{f === 'all' ? 'All' : f === 'local' ? 'Local' : 'Currently advertised'}</button>)}</div></div>{selected && <a href="#inspector" className="view-selection">View selected build · {selected.version}</a>}<div className="build-list" aria-busy={reading}>{filtered.map(build => <button key={build.id} data-build-id={build.id} className={`build-row ${selectedId === build.id ? 'selected' : ''}`} aria-pressed={selectedId === build.id} onClick={() => select(build)}><span className="row-identity"><strong>{build.version || 'Version not supplied'}</strong><span>Build {build.buildId || 'not supplied'} <span className="separator">·</span> <span className="mono">Config {short(build.buildConfig)}</span></span><span className="mono">Identity {short(build.id)}</span></span><span className="row-regions">{build.advertisedRegions.length ? build.advertisedRegions.map(r => <span className="region-chip" key={r}>{r.toUpperCase()}</span>) : <span className="region-empty">Saved observation</span>}</span><span className="row-evidence"><span className="badge">{build.isAdvertised ? 'Advertised at last check' : 'Saved history'}</span>{build.isNew && <span className="new">New to VibeMiner</span>}{build.isLocal && <span className="badge">Locally prepared</span>}</span></button>)}</div>{!filtered.length && <div className="empty"><div className="empty-mark" aria-hidden="true">◇</div><h3>{reading ? 'Reading saved observations…' : busy ? 'Checking official metadata…' : filter === 'local' ? 'No locally prepared builds' : page?.connection === 'offline' && !page.hasSavedHistory ? `Could not check ${productId}` : query || region !== 'all' ? 'No matching loaded builds' : page?.hasSavedHistory ? 'No builds match this view' : 'Your research starts with a build'}</h3><p>{filter === 'local' ? 'Saved discovery observations are available in All. Saved discovery observations are separate from a locally prepared build.' : page?.connection === 'offline' && !page.hasSavedHistory ? 'Official metadata could not be reached, and no saved observations are available. Select Retry discovery to check this product again.' : query || region !== 'all' ? 'Try another version, build number, or advertisement region. Search covers loaded pages only; it cannot search beyond the 500-build display window.' : page?.connection === 'online' ? `No builds advertised in the checked scope at ${stamp(page.lastCheckedAt)}.` : `No saved observations for ${productId}. Choose a product or select Refresh builds to check official sources.`}</p>{(query || region !== 'all' || filter !== 'all') && <button onClick={clearFilters}>Clear filters</button>}</div>}{page?.nextCursor && <button className="load-more" disabled={reading || busy || page.builds.length >= 500} onClick={() => void read(filter, page.nextCursor!)}>{reading ? 'Loading…' : page.builds.length >= 500 ? '500-build display limit reached' : 'Load more builds'}</button>}<p className="list-note">Regions combine only when the complete build identity matches. Matching version numbers can have different configurations.{page?.nextCursor && (page.builds.length >= 500 ? ' This view displays the first 500 builds. Search and region controls only refine loaded builds; searching beyond this display window is not available.' : ' Search filters the pages loaded so far.')}</p><div className="check-context"><div className="scope"><span>Checked regions: <strong>{regions(page?.checkedRegions || [])}</strong></span><span>Last check: <strong>{stamp(page?.lastCheckedAt || null)}</strong></span></div><div className={`activity ${busy ? 'working' : ''}`}>{busy && <span className="activity-dot" aria-hidden="true"/>}<span role="status" aria-live="polite">{stopping ? 'Stopping discovery…' : message}</span>{busy && !stopping && <span className="progress-stage" aria-live="off">{activity}</span>}</div></div></section><aside className="inspector" id="inspector" aria-labelledby="inspector-heading"><div className="eyebrow">{selected ? 'Selected build' : 'Build inspector'}</div>{selected ? <>
      <h2 id="inspector-heading">{selected.version}</h2>
      <p className="inspector-product">{product?.label || 'Warcraft product'} · <code>{productId}</code> · <span>{selected.advertisedRegions.map(r => <span className="region-chip" key={r}>{r.toUpperCase()}</span>)}</span></p>
      <div className="status-card"><strong>Discovery observation saved</strong><span>Local preparation: {selected.isLocal ? 'Local evidence recorded' : 'No prepared content'}</span><span>Remote availability: Not checked</span></div>
      <dl className="identity compact-identity">
        <dt>Game / edition</dt><dd>World of Warcraft · {product?.label || productId}</dd>
        <dt>Publication</dt><dd>{selected.isAdvertised ? 'Advertised at last successful check' : 'Saved historical observation'}</dd>
        <dt>Observed here</dt><dd>{stamp(selected.lastSeenAt)}</dd>
        <Field label="Build configuration" value={selected.buildConfig}/>
      </dl>
      {selected.regions.length > 1 && <div className="identity-hint"><strong>Same build data across these regions.</strong><br/>The exact build references match; original regional receipts remain separate.</div>}
      <p className="route-note">Recorded route regions: <strong>{regions(selected.routeRegions)}</strong><br/>Routing evidence only; this does not change the selected build data.</p>
      <BuildOpenAction key={selected.id} build={selected} onOpen={onOpen}/>
      <button className="source-button" onClick={() => setSourceOpen(true)}>Source details</button>
      <p className="inspector-note">Saving discovery metadata is separate from downloading game content. A published pointer does not prove all files remain available.</p>
      {sourceOpen && <SourceDialog build={selected} page={page!} onClose={() => setSourceOpen(false)}/>}
    </> : <><h2 id="inspector-heading" className="visually-hidden">Build inspector</h2><div className="inspector-empty"><div aria-hidden="true">⌕</div><h3>{hidden ? 'Selected build is hidden' : 'Select a build'}</h3><p>{hidden ? selectionNotice || 'Selected build is hidden by these filters or no longer appears in this page. Clear filters or load more to inspect it again.' : 'Review its exact identity and available next steps here.'}</p></div><button className="primary open" disabled>Open Build</button><p className="open-reason">Select a build with a recorded download source to open its prepared build overview.</p><p className="inspector-note">Browsing discovery never starts a game content download.</p></>}</aside></div>;
}
function BuildOpenAction({ build, onOpen }: { build: Build; onOpen: (build: Build, sourceRegion: string) => void }) {
  const eligible = Array.from(new Set(build.sourceReceipts.filter(receipt => receipt.role === 'cdns' && receipt.rowOrdinal !== null && build.sourceReceipts.some(version => version.role === 'versions' && version.region === receipt.region && version.rowOrdinal !== null)).map(receipt => receipt.region))).sort();
  const [source, setSource] = useState(() => eligible.includes('us') ? 'us' : eligible[0] || '');
  const available = eligible.includes(source);
  return <><div className="open-source-control"><label htmlFor="opening-source">Download source</label><select id="opening-source" value={available ? source : ''} onChange={event => setSource(event.target.value)} disabled={!eligible.length}>{!available && <option value="">No recorded source route</option>}{eligible.map(region => <option key={region} value={region}>{region.toUpperCase()}</option>)}</select><p>Uses saved regional source evidence. This choice does not change the build identity.</p></div><button className="primary open" disabled={!available} onClick={() => { if (available) onOpen(build, source); }}>Open Build</button><p className="open-reason">{available ? 'Assesses published download sizing before Home. Research catalogs and whole-build payloads are separate.' : 'No saved source route can resolve this exact build. Refresh discovery or inspect Source details.'}</p></>;
}
function Field({ label, value }: { label: string; value: string }) { return <><dt>{label}</dt><dd className="mono">{value}</dd></>; }
function SourceDialog({ build, page, onClose }: { build: Build; page: DiscoveryPage; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const previous = useRef<HTMLElement | null>(null);
  useEffect(() => { previous.current = document.activeElement as HTMLElement; dialog.current?.showModal(); return () => { dialog.current?.close(); previous.current?.focus(); }; }, []);
  return <dialog ref={dialog} aria-labelledby="source-title" onCancel={e => { e.preventDefault(); onClose(); }}><div className="dialog-heading"><div><div className="eyebrow">Discovery evidence</div><h2 id="source-title">Source details</h2></div><button autoFocus onClick={onClose}>Close</button></div><div className="dialog-body"><p><code>{build.productId}</code> · {build.version} · Build {build.buildId}</p><p>Checked regions: {regions(page.checkedRegions)} · Last attempt: {stamp(page.lastCheckedAt)}</p><h3>Exact build references</h3><dl className="identity"><Field label="Build number" value={build.buildId}/><Field label="Build configuration" value={build.buildConfig}/><Field label="CDN configuration" value={build.cdnConfig}/><Field label="Product configuration" value={build.productConfig === null ? 'Not supplied' : build.productConfig}/><Field label="Exact identity" value={build.id}/><dt>First observed here</dt><dd>{stamp(build.firstSeenAt)}</dd><dt>Last observed</dt><dd>{stamp(build.lastSeenAt)}</dd></dl><details><summary>Original identity fields</summary><dl className="identity">{Object.entries(build.identityFields).map(([name, value]) => <Field key={name} label={name} value={value === '' ? '(empty value)' : value}/>)}</dl></details><h3>Regional route evidence</h3><p>{regions(build.routeRegions)}. Route evidence is separate from advertisement-region grouping. The Download source control selects one eligible saved regional source for Open; that choice does not change the exact build identity or its grouped advertisements.</p><h3>Original receipts</h3>{build.sourceReceipts.length ? build.sourceReceipts.map((receipt, i) => <section className="receipt" key={i}><h4>{receipt.region.toUpperCase()} · {receipt.role}</h4><dl className="identity"><Field label="Requested URL" value={receipt.url}/><dt>Retrieved</dt><dd>{stamp(receipt.retrievedAt)}</dd><Field label="SHA-256" value={receipt.sha256}/><dt>Document length / row</dt><dd>{receipt.byteLength} bytes · {receipt.rowOrdinal === null ? 'No row ordinal' : `Row ${receipt.rowOrdinal}`}</dd></dl></section>) : <p>No receipts recorded.</p>}{page.errors.length > 0 && <><h3>Current attempt problems</h3><ul>{page.errors.map((p, i) => <li key={i}>{p.region || 'Source'}: {p.message} ({p.code})</li>)}</ul></>}</div></dialog>;
}
