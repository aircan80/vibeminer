import type { DownloadCheckController } from './useDownloadCheck';
import type {ItemEffectsLoading} from './useItemEffects';
import type {CreatureDisplays} from './useCreatureDisplay';
import type {GeometryFlow} from './useCreatureGeometry';
import {CreaturesBrowser} from './CreaturesBrowser';
import type {CreaturesLoading} from './useCreaturesLoading';
import type {Classifications} from './useClassifications';
import type {IconsLoading} from './useIconsLoading';
import {useItemSourceNavigation} from './useItemSourceNavigation';
import {ItemsBrowser} from './ItemsBrowser';
import type {ItemsLoading} from './useItemsLoading';
import type { RetentionController } from './useRetention';
import { RetentionModeBox } from './Retention';
import { retentionKey } from '../shared/retention';
import type { TableLoading } from './useTableLoading';
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type MouseEvent, type ReactNode } from 'react';
import type { FileTarget } from './table-follow-view';
import type { Build, FileCatalogStatus, InventorySummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { encodedPercentage, exactBytes, exactCount, isInventoryWorkspace, readableBytes } from '../shared/inventory';
import type { Opening } from './useWorkspaceFlow';
import { BuildAreas, useHomeFocus, type BuildArea } from './BuildAreas';
import { TablesExplorer } from './TablesExplorer';
import { DownloadObjects, FilesUnavailable, catalogStage, type MeasuredLedger } from './DownloadObjects';
import { useDownloadSummaries } from './useDownloadSummaries';
import { summaryKey } from '../shared/download-summary';
import { FilesCatalog, inventorySource, type FilesSource } from './FilesCatalog';
import { useFileCatalog, type useFileCatalogTasks } from './useFileCatalog';
import type { CatalogView } from './DownloadObjects';
export type FileCatalogTasks = ReturnType<typeof useFileCatalogTasks>;
import { HomeShell, useCoveredSide, type ShellArea } from './HomeShell';

type Evidence = 'source' | 'metadata' | 'measurement';
export type FilesView = 'catalog' | 'objects';
const noViews = new Map<string, FilesView>();
type Pane = 'overview' | 'files' | 'tables' | 'items' | 'creatures';
const shellArea: Record<Pane, ShellArea> = { overview: 'home', files: 'files', tables: 'tables', items: 'items', creatures: 'creatures' };
const joined = (values: string[]) => values.length ? values.map(x => x.toUpperCase()).join(', ') : 'no recorded region';
// Home after a build opens (docs/design/desktop-home): one shell for the download-sized and the catalog-prepared workspace.
export function DownloadHome({ filesViews = noViews, downloadCheck, catalogTasks, activityNavigation, creatureGeometry, effectsLoading, effectsAvailable=false, creatureDisplays, creatureDisplayAvailable=false, creaturesLoading, creaturesAvailable=false, classificationsLoading, classificationsAvailable=false, subclassAvailable=false, iconsLoading, iconsAvailable=false, itemsLoading, itemsAvailable=false, retention, operation: op, catalogBuild, fixtureLabel, bridge, onChoose, onReopen, evidence, tableLoading }: { filesViews?: Map<string, FilesView>; downloadCheck?: DownloadCheckController; catalogTasks?: FileCatalogTasks; activityNavigation?:{area:'tables'|'items'|'creatures'|'files';sequence:number}|null;creatureGeometry?:GeometryFlow;effectsLoading?:ItemEffectsLoading;effectsAvailable?:boolean; creatureDisplays?:CreatureDisplays;creatureDisplayAvailable?:boolean; creaturesLoading?:CreaturesLoading;creaturesAvailable?:boolean; classificationsLoading?:Classifications;classificationsAvailable?:boolean;subclassAvailable?:boolean; iconsLoading?:IconsLoading;iconsAvailable?:boolean; itemsLoading?: ItemsLoading; itemsAvailable?: boolean; retention?: RetentionController; tableLoading: TableLoading; operation: Opening & { result: WorkspaceSnapshot }; catalogBuild?: Build | null; fixtureLabel?: string | null; bridge: DiscoveryBridge; onChoose: () => void; onReopen: () => void; evidence: (mode: Evidence, close: () => void) => ReactNode }) {
  const snapshot = op.result, summary = snapshot.download, inventory = isInventoryWorkspace(snapshot) ? snapshot : null;
  const [pane, setPane] = useState<Pane>('overview');
  // The build's file catalog prepared after Open (D071). A legacy full catalog snapshot carries its own generation; any other
  // workspace reads the build's ready file catalog. Files never mixes the two: each view reads one exact generation.
  // Only an engine with file-catalog-v1 prepares one; without it Files keeps the earlier "not prepared" note.
  const catalogRead = useFileCatalog(bridge, snapshot, !inventory && !!catalogTasks, `${catalogTasks?.revision ?? 0}#` + (catalogTasks?.catalogs.map(c => `${c.buildIdentity}:${c.state}:${c.stage}`).join('|') ?? ''));
  const catalog = catalogRead.status;
  const catalogSource: FilesSource | null = catalog?.state === 'ready' && catalog.catalog ? { productId: catalog.productId, buildIdentity: catalog.buildIdentity, sourceRegion: catalog.sourceRegion, generation: catalog.catalog.generation, preparedAt: catalog.catalog.preparedAt } : null;
  const catalogKey = catalogSource ? `${catalogSource.productId}:${catalogSource.buildIdentity}:${catalogSource.sourceRegion}:${catalogSource.generation}` : null;
  // A catalog that becomes ready while Files shows the download list is offered with a button rather than swapped in under
  // the user, so async completion never moves focus; it is shown directly the next time Files opens.
  const [adopted, setAdopted] = useState<string | null>(null);
  useEffect(() => { if (catalogKey && pane !== 'files') setAdopted(catalogKey); }, [catalogKey, pane]);
  // D093: the download objects stay reachable once the file list is ready. Files switches between the two; the choice is
  // kept for this build for the session, and without one the file list is still adopted once, as above.
  const choiceKey = `${snapshot.build.productId}:${snapshot.build.id}`;
  const [choice, setChoice] = useState<FilesView | null>(() => filesViews.get(choiceKey) ?? null);
  const focusSwitch = useRef(false);
  const choose = (next: FilesView, focus: boolean) => { filesViews.set(choiceKey, next); setChoice(next); if (focus) focusSwitch.current = true; };
  const shownCatalog = choice === 'objects' && summary ? null : choice === 'catalog' ? catalogSource : catalogKey && adopted === catalogKey ? catalogSource : null;
  // The switch's own button goes with the view it was in; focus moves to the same switch in the view that replaced it.
  useEffect(() => { if (!focusSwitch.current) return; focusSwitch.current = false; document.querySelector<HTMLElement>('.dh-files:not([hidden]) [aria-label="Files view"] button[aria-pressed="true"]')?.focus({ preventScroll: true }); });
  const catalogTaskKey = `fileCatalog:${snapshot.build.productId}:${snapshot.build.id}`;
  const [catalogNotice, setCatalogNotice] = useState('');
  const catalogView: CatalogView | null = inventory || !bridge.fileCatalog || !catalogTasks ? null : {
    status: catalog, error: catalogRead.error || catalogNotice, busy: catalogTasks?.busy === `${snapshot.build.productId}|${snapshot.build.id}`,
    taskKey: catalogTasks?.catalogs.some(c => c.buildIdentity === snapshot.build.id && c.productId === snapshot.build.productId) ? catalogTaskKey : null,
    ...(catalogKey ? { onShow: () => { setAdopted(catalogKey); choose('catalog', false); } } : {}),
    onAction: async action => { const result = await catalogTasks?.act({ productId: snapshot.build.productId, buildIdentity: snapshot.build.id, action }); setCatalogNotice(typeof result === 'string' ? result : ''); await catalogRead.retry(); },
    onRetryRead: () => { setCatalogNotice(''); return catalogRead.retry(); },
  };
  // The build's stored figure names the measured ledger once every file ROOT lists is measured (D066, D093). Files reads
  // it while it shows the download objects, so a measurement that lands then switches the list in place.
  const features = retention?.features ?? [];
  const scopeWatch = !!summary && !inventory && !!bridge.downloadSummaries && features.includes('download-summary-v1');
  const builds = useRef<Build[]>([]); if (builds.current[0] !== snapshot.build) builds.current = [snapshot.build];
  const stored = useDownloadSummaries(bridge, scopeWatch ? builds.current : [], pane === 'overview' || pane === 'files' && !shownCatalog, scopeWatch, '');
  const figure = stored.summaries.get(summaryKey(snapshot.build.productId, snapshot.build.id))?.download ?? null;
  const measured: MeasuredLedger | null | 'pending' = !scopeWatch || !summary ? null : figure ? (figure.scopeState === 'measured' && figure.manifestLedgerId === summary.ledgerId && figure.ledgerId !== summary.ledgerId ? { ledgerId: figure.ledgerId, objectCount: figure.objectCount } : null) : stored.readAt === null && !stored.error ? 'pending' : null;
  const measuring = !!figure && figure.scopeState === 'measuring' && figure.manifestLedgerId === summary?.ledgerId;
  const [listed, setListed] = useState<MeasuredLedger | null>(null);
  const itemSource=useItemSourceNavigation(pane,setPane);
  useEffect(()=>{if(activityNavigation)setPane(activityNavigation.area);},[activityNavigation?.sequence]);
  const [dialog, setDialog] = useState<Evidence | null>(null);
  const heading = useRef<HTMLHeadingElement>(null), grid = useRef<HTMLDivElement>(null);
  const origin = useRef<{ element: HTMLElement | null; scroll: number }>({ element: null, scroll: 0 });
  useEffect(() => { heading.current?.focus({ preventScroll: true }); }, []);
  const { home, returnHome } = useHomeFocus(pane, heading, origin, () => setPane('overview'), grid);
  const [fromHome, setFromHome] = useState(false);
  // A FileDataID followed from Tables (#19) opens Files with the way back to that record.
  const [fileTarget, setFileTarget] = useState<FileTarget | null>(null), fileSeq = useRef(0);
  const openFile = useCallback((target: Omit<FileTarget, 'seq'>) => { setFileTarget({ ...target, seq: ++fileSeq.current }); setFromHome(false); setPane('files'); }, []);
  const returnToTable = () => { setFileTarget(null); setPane('tables'); };
  // Build overview's line about files not checked at Open opens Download objects with focus on the Check.
  const outsideFiles = summary && measured && measured !== 'pending' ? BigInt(measured.objectCount) - BigInt(summary.objectCount) : 0n;
  const [checkFocus, setCheckFocus] = useState(0);
  // Each request has its own number; the view clears it once served, so leaving and coming back never replays it.
  const checkRequests = useRef(0);
  function openCheck() { choose('objects', false); setCheckFocus(++checkRequests.current); files(); }
  const viewSwitch = summary && !inventory && catalogView ? <div className="dh-segmented dh-files-view" role="group" aria-label="Files view">
    <button aria-pressed={!!shownCatalog} aria-disabled={!catalogSource} onClick={() => { if (catalogSource && !shownCatalog) choose('catalog', true); }}>All files</button>
    <button aria-pressed={!shownCatalog} onClick={() => { if (shownCatalog) choose('objects', true); }}>Download objects</button></div> : null;
  function files() { origin.current = { element: document.activeElement as HTMLElement, scroll: grid.current?.scrollTop ?? 0 }; setFromHome(true); setFileTarget(null); setPane('files'); }
  function go(next: Pane) { setFromHome(false); setFileTarget(null); setPane(next); }
  const retentionTarget=summary?{productId:snapshot.build.productId,buildIdentity:snapshot.build.id,sourceRegion:snapshot.sourceReference.region,ledgerId:summary.ledgerId}:null;
  const targetKey=retentionTarget?retentionKey(retentionTarget):'';
  useEffect(()=>{if(retentionTarget&&retention?.capability==='available')retention.ensure(retentionTarget);return()=>{if(targetKey)retention?.release(targetKey);};},[targetKey,retention?.capability]);
  const items=itemsAvailable&&!!itemsLoading,creatures=creaturesAvailable&&!!creaturesLoading;
  const gameData=[items&&'Items',creatures&&'Creatures'].filter(Boolean).join(', ');
  const areas=<BuildAreas active={pane as BuildArea} onHome={()=>{setFromHome(false);setFileTarget(null);home();}} onTables={()=>go('tables')} onFiles={()=>go('files')} onItems={items?()=>go('items'):undefined} onCreatures={creatures?()=>go('creatures'):undefined}/>;
  const assessed=summary?new Date(summary.assessedAt).toLocaleString():null;
  const status=pane==='overview'?<span className="dh-secondary">{inventory?<>Catalog generation <strong>{inventory.inventory.generation}</strong> · {exactCount(inventory.inventory.membershipCount)} memberships</>:assessed?<>Assessed <strong>{assessed}</strong></>:'Download size unknown'}</span>
    :pane==='files'?<span className="dh-secondary">{inventory?<>Catalog generation <strong>{inventory.inventory.generation}</strong> · {exactCount(inventory.inventory.membershipCount)} memberships</>:shownCatalog&&catalog?.catalog?<>File catalog generation <strong>{shownCatalog.generation}</strong> · {exactCount((BigInt(catalog.catalog.rootMembershipCount)+BigInt(catalog.catalog.installMembershipCount)).toString())} memberships</>:listed?<>Download manifest and every listed file · <strong>{exactCount(listed.objectCount)}</strong> encoded objects</>:summary?<>Download manifest · <strong>{exactCount(summary.objectCount)}</strong> encoded objects</>:'File catalog not prepared'}</span>:null;
  return <HomeShell snapshot={snapshot} catalogBuild={catalogBuild} label={op.label} retention={retention} onChoose={onChoose} area={shellArea[pane]} areas={areas} fixtureLabel={fixtureLabel} status={status} dialog={dialog && evidence(dialog, () => setDialog(null))}>
    <section className="dh-home" hidden={pane !== 'overview'} aria-labelledby="home-heading">
      <div className="dh-pane-head"><h1 ref={heading} id="home-heading" tabIndex={-1}>Build overview</h1><span className="dh-sub">{snapshot.sourceReference.region.toUpperCase()} download source · advertised in {joined(snapshot.build.advertisedRegions)} · measurements cover every published variant</span><span className="dh-spacer"/><HomeUtilities onMetadata={() => setDialog('metadata')} onSource={() => setDialog('source')}/></div>
      <div className="dh-home-grid" ref={grid}>
        <section className="dh-panel" aria-labelledby="home-sizes"><div className="dh-panel-head"><h2 id="home-sizes">Download sizes</h2><span className="dh-scope">Encoded bytes</span></div><div className="dh-panel-body">{inventory ? <InventorySizes inventory={inventory.inventory}/> : <><DownloadSizes snapshot={snapshot} onReopen={onReopen}/>{outsideFiles > 0n && <p className="dh-check-line"><strong className="dh-warn-t">{exactCount(outsideFiles.toString())} {outsideFiles === 1n ? 'file' : 'files'} not checked at Open</strong> · <button className="dh-linkbtn" onClick={openCheck}>Check</button></p>}</>}</div><div className="dh-panel-foot"><button className="dh-btn dh-small" onClick={() => setDialog('measurement')}>Scope and measurement details</button></div></section>
        <section className="dh-panel" aria-labelledby="home-catalog"><div className="dh-panel-head"><h2 id="home-catalog" tabIndex={-1}>File catalog</h2><span className="dh-scope">ROOT and install manifests</span></div><div className="dh-panel-body">{inventory ? <CatalogFacts inventory={inventory.inventory}/> : catalogView && catalog?.state !== 'notOpened' ? <FileCatalogFacts view={catalogView} objects={!!summary}/> : <><div className="dh-metric"><span className="dh-mlabel">Catalog memberships</span><strong className="dh-unknown">Not prepared</strong><span className="dh-exact">No membership count is inferred</span></div><p className="dh-reason">{summary ? 'Files lists the download manifest’s encoded objects for now. Tables, Items and Creatures prepare their own data.' : 'Tables, Items and Creatures prepare their own data.'}</p></>}</div><div className="dh-panel-foot"><button className="dh-btn dh-small" onClick={() => { if (catalogSource) choose('catalog', false); files(); }}>Browse in Files</button></div></section>
        <section className="dh-panel dh-wide" aria-labelledby="home-storage"><div className="dh-panel-head"><h2 id="home-storage">Local content &amp; shared storage</h2><span className="dh-scope">{inventory ? 'Published inventory scope · exact build' : 'Download manifest scope · exact build'}</span></div><div className="dh-panel-body"><div className="dh-storage-cols"><div>{inventory ? <InventoryStorage inventory={inventory.inventory}/> : <DownloadStorage snapshot={snapshot}/>}</div><div><RetentionModeBox flow={inventory ? undefined : retention} target={retentionTarget}/></div></div></div></section>
        <Readiness snapshot={snapshot} inventory={inventory?.inventory ?? null} catalog={catalog} catalogChecking={!!catalogView && !catalog} gameData={gameData} onMetadata={() => setDialog('metadata')}/>
      </div>
    </section>
    {inventory ? <FilesCatalog key={`${snapshot.build.productId}:${snapshot.build.id}:${snapshot.sourceReference.region}:${inventory.inventory.generation}`} source={inventorySource(inventory)} bridge={bridge} active={pane === 'files'} onReopen={onReopen} onReturn={fromHome ? returnHome : undefined} fileTarget={fileTarget} onReturnToTable={returnToTable}/>
      : shownCatalog ? <FilesCatalog key={`catalog:${catalogKey}`} viewSwitch={viewSwitch} source={shownCatalog} bridge={bridge} active={pane === 'files'} onReopen={onReopen} onReturn={fromHome ? returnHome : undefined} fileTarget={fileTarget} onReturnToTable={returnToTable}/>
      : summary ? <DownloadObjects key={`${snapshot.build.id}:${summary.ledgerId}:${summary.assessedAt}`} snapshot={snapshot} summary={summary} measured={measured} measuring={measuring} checkAvailable={features.includes('download-check-v1')} checker={downloadCheck} focusCheck={checkFocus} onCheckFocused={() => setCheckFocus(0)} viewSwitch={viewSwitch} onLedger={setListed} bridge={bridge} active={pane === 'files'} onReopen={onReopen} onReturn={fromHome ? returnHome : undefined} catalog={choice === 'objects' && catalogView ? { ...catalogView, onShow: undefined } : catalogView} fileTarget={fileTarget} onReturnToTable={returnToTable}/>
      : <FilesUnavailable snapshot={snapshot} active={pane === 'files'} onReturn={fromHome ? returnHome : undefined} onReopen={onReopen} catalog={catalogView} fileTarget={fileTarget} onReturnToTable={returnToTable}/>}
    {items&&<div className="dh-area-scroll" hidden={pane!=='items'}><ItemsBrowser effectsLoading={effectsLoading} effectsAvailable={effectsAvailable} effectsSource={itemSource} classificationsLoading={classificationsLoading} classificationsAvailable={classificationsAvailable} subclassAvailable={subclassAvailable} iconsLoading={iconsLoading} iconsAvailable={iconsAvailable} key={`items:${snapshot.build.productId}:${snapshot.build.id}:${snapshot.sourceReference.region}`} workspace={snapshot} bridge={bridge} active={pane==='items'} flow={itemsLoading!} focusRequest={itemsLoading!.viewRequest} onSource={itemSource.open}/></div>}
    {creatures&&<div className="dh-area-scroll" hidden={pane!=='creatures'}><CreaturesBrowser displays={creatureDisplays} displayAvailable={creatureDisplayAvailable} geometry={creatureGeometry} key={`creatures:${snapshot.build.productId}:${snapshot.build.id}:${snapshot.sourceReference.region}`} workspace={snapshot} bridge={bridge} active={pane==='creatures'} flow={creaturesLoading!} focusRequest={creaturesLoading!.viewRequest} onSource={(source,id)=>itemSource.open(source,id,'creatures')}/></div>}
    <TablesExplorer key={`tables:${snapshot.build.productId}:${snapshot.build.id}:${snapshot.sourceReference.region}:${snapshot.sourceReference.generation}`} snapshot={snapshot} bridge={bridge} active={pane === 'tables'} tableLoading={tableLoading} sourceRecord={itemSource.request} onReturnToItems={itemSource.returnToItems} onOpenFile={openFile}/>
  </HomeShell>;
}
// The docked activity panel covers the right end of the pane head too, so these become inert with the side column.
function HomeUtilities({ onMetadata, onSource }: { onMetadata: () => void; onSource: () => void }) { const covered = useCoveredSide(); return <span className="dh-utilities" role="group" aria-label="Home utilities" inert={covered}><button className="dh-btn dh-small" onClick={onMetadata}>Metadata</button><button className="dh-btn dh-small" onClick={onSource}>Source details</button></span>; }
function Metric({ label, value, exact, unknown = false }: { label: string; value: string; exact?: ReactNode; unknown?: boolean }) { return <div className="dh-metric"><span className="dh-mlabel">{label}</span><strong className={unknown ? 'dh-unknown' : undefined}>{value}</strong>{exact && exact !== value && <span className="dh-exact dh-mono dh-selectable">{exact}</span>}</div>; }
function Facts({ rows }: { rows: [string, ReactNode, string?][] }) { return <dl className="dh-facts">{rows.map(([label, value, tone]) => <div key={label} style={{ display: 'contents' }}><dt>{label}</dt><dd className={tone}>{value}</dd></div>)}</dl>; }
function DownloadSizes({ snapshot, onReopen }: { snapshot: WorkspaceSnapshot; onReopen: () => void }) {
  const summary = snapshot.download;
  if (summary) return <><Metric label="Published download content" value={readableBytes(summary.totalEncodedBytes)} exact={exactBytes(summary.totalEncodedBytes)}/><Facts rows={[['Unique encoded objects', exactCount(summary.objectCount)], ['Scope', 'Download manifest · all variants'], ['Decoded / installed size', 'Unknown', 'dh-dim']]}/></>;
  return <><div className="dh-metric"><span className="dh-mlabel">Published download content</span><strong className="dh-unknown">Unknown</strong><span className="dh-exact">Encoded scope unknown</span></div><div className="dh-state-box dh-warn" role="status"><strong>Published download size is unknown</strong><p>{snapshot.downloadProblem?.message} No download total or local coverage is inferred.</p>{snapshot.downloadProblem && <p>(<code>{snapshot.downloadProblem.code}</code>)</p>}<div className="dh-row"><button className="dh-btn dh-small" onClick={onReopen}>Recheck this build</button></div></div></>;
}
function InventorySizes({ inventory }: { inventory: InventorySummary }) {
  return <><Metric label="Required encoded content" value={readableBytes(inventory.totalEncodedBytes)} exact={exactBytes(inventory.totalEncodedBytes)}/><Facts rows={[['Exact encoded objects', exactCount(inventory.downloadObjectCount)], ['Scope', 'All published variants'], ['Decoded / installed size', 'Unknown', 'dh-dim']]}/></>;
}
// The build's file catalog on Home (D071): preparing with its stage, ready with exact counts, stopped, or why it is not
// prepared. Actions live in Files and in Downloads & activity; Home says where.
function FileCatalogFacts({ view, objects }: { view: CatalogView; objects: boolean }) {
  // Retry status disappears once a read succeeds. Focus then goes to the control that took its place in this panel, or
  // to the panel heading, never to the document body; a user who moved focus elsewhere meanwhile keeps it there. Once
  // the read has finished the request is over, so a read that fails again leaves focus on Retry status and a later
  // automatic read never moves it.
  const root = useRef<HTMLDivElement>(null), moveFocus = useRef(false), seen = useRef(0);
  const [finished, setFinished] = useState(0);
  useLayoutEffect(() => {
    const done = finished !== seen.current; seen.current = finished;
    if (!moveFocus.current || !root.current) return;
    const active = document.activeElement;
    if (active && active !== document.body && root.current.contains(active)) { if (done) moveFocus.current = false; return; }
    moveFocus.current = false;
    if (active && active !== document.body) return;
    (root.current.querySelector<HTMLElement>('button') ?? document.getElementById('home-catalog'))?.focus({ preventScroll: true });
  });
  const retry = (event: MouseEvent<HTMLButtonElement>) => {
    const mine = document.activeElement === event.currentTarget;
    if (mine) moveFocus.current = true;
    void Promise.resolve(view.onRetryRead()).then(() => { if (mine) setFinished(n => n + 1); });
  };
  return <div ref={root}><FileCatalogFactsBody view={view} objects={objects} onRetry={retry}/></div>;
}
function FileCatalogFactsBody({ view, objects, onRetry }: { view: CatalogView; objects: boolean; onRetry: (event: MouseEvent<HTMLButtonElement>) => void }) {
  const catalog = view.status, meanwhile = objects ? 'Files lists the download manifest’s encoded objects until then.' : 'Files has no list until then.';
  const metric = (value: string, exact: string) => <div className="dh-metric"><span className="dh-mlabel">Catalog memberships</span><strong className="dh-unknown">{value}</strong><span className="dh-exact">{exact}</span></div>;
  // Before the first read: say it is being checked, never that the list is not prepared.
  if (!catalog && !view.error) return metric('Checking…', 'Reading whether the file list is prepared');
  if (!catalog) return <>{metric('Status unavailable', 'No membership count is inferred')}<p className="dh-reason">{view.error} <button className="dh-btn dh-small" onClick={onRetry}>Retry status</button></p></>;
  const stale = view.error ? <p className="dh-reason dh-warn-t">Status not confirmed: {view.error} <button className="dh-btn dh-small" onClick={onRetry}>Retry status</button></p> : null;
  return <>{facts()}{stale}</>;
  function facts() {
  if (catalog!.state === 'ready' && catalog!.catalog) {
    const c = catalog!.catalog, members = (BigInt(c.rootMembershipCount) + BigInt(c.installMembershipCount)).toString();
    return <><Metric label="Catalog memberships" value={exactCount(members)} exact="All published variants"/><Facts rows={[['ROOT FileDataIDs', exactCount(c.rootFileCount)], ['Install names', exactCount(c.installFileCount)], ['Unresolved memberships', exactCount(c.unresolvedMembershipCount), BigInt(c.unresolvedMembershipCount) > 0n ? 'dh-warn-t' : undefined], ['Catalog', 'Ready to browse', 'dh-ok']]}/></>;
  }
  if (catalog!.state === 'pending') return <>{metric('Preparing…', catalogStage(catalog!))}<p className="dh-reason">The ROOT and install manifests are read in the background after Open. {meanwhile}{catalog!.attempts > 0 && catalog!.problem && <> Attempt {catalog!.attempts} failed <code>({catalog!.problem.code})</code>; it is tried again shortly.</>} Progress and Stop are in Downloads &amp; activity.</p></>;
  if (catalog!.state === 'stopped') return <>{metric('Stopped', 'The rows written so far are kept')}<p className="dh-reason">Resume it from Files or Downloads &amp; activity. Opening the build again resumes it too.</p></>;
  // Freed from Settings (D085): nothing is left, and nothing runs until the user asks or opens the build again.
  if (catalog!.state === 'removed') return <>{metric('Freed', 'Removed in Settings to save disk space')}<p className="dh-reason">Opening the build again prepares it again, or prepare it now from Files. {meanwhile}</p></>;
  const p = catalog!.problem;
  return <>{metric('Not prepared', 'No membership count is inferred')}<p className="dh-reason">{p?.message} <code>({p?.code})</code>{catalog!.state === 'failed' ? ' Retry it from Files or Downloads & activity.' : ''}</p></>;
  }
}
function CatalogFacts({ inventory }: { inventory: InventorySummary }) {
  return <><Metric label="Catalog memberships" value={exactCount(inventory.membershipCount)} exact="All published variants"/><Facts rows={[['ROOT memberships', exactCount(inventory.rootMembershipCount)], ['Unique ROOT/IN contents', exactCount(inventory.uniqueContentCount)], ['Catalog', 'Ready to browse', 'dh-ok']]}/></>;
}
function Ledger({ rows }: { rows: [ReactNode, string, string?][] }) { return <dl className="dh-ledger">{rows.map(([label, value, exact], i) => <div key={i}><dt>{label}</dt><dd className={value === 'Unknown' || value === 'Not measured' ? 'dh-dim' : undefined}>{value}</dd><dd className="dh-exact dh-selectable">{exact ?? ''}</dd></div>)}</dl>; }
function DownloadStorage({ snapshot }: { snapshot: WorkspaceSnapshot }) {
  const summary = snapshot.download;
  if (!summary) return <><Ledger rows={[['Verified locally at assessment', 'Unknown'], ['Additional encoded content required', 'Unknown'], ['Retained metadata (separate)', 'Not measured']]}/><p className="dh-tiny">No bar or percentage is shown while the download scope is unknown.</p></>;
  const measured = BigInt(summary.totalEncodedBytes) > 0n, digits = (value: string) => BigInt(value).toLocaleString('en-US');
  return <>{measured ? <div className="dh-stack download-retention-chart" role="img" aria-label={`${exactBytes(summary.verifiedEncodedBytes)} verified locally at assessment of ${exactBytes(summary.totalEncodedBytes)} published download content`}><i className="dh-own" style={{ width: encodedPercentage(summary.verifiedEncodedBytes, summary.totalEncodedBytes) }}/></div> : <p className="dh-reason">The manifest declares zero encoded bytes. No percentage is inferred from an empty denominator.</p>}
    {measured && <div className="dh-legend"><span><i className="dh-own"/>Verified locally at assessment</span><span><i/>Additional encoded content</span></div>}
    <Ledger rows={[[<>Verified locally at assessment{measured && <span className="dh-dim"> · {encodedPercentage(summary.verifiedEncodedBytes, summary.totalEncodedBytes)}</span>}</>, readableBytes(summary.verifiedEncodedBytes), digits(summary.verifiedEncodedBytes)], ['Additional encoded content required', readableBytes(summary.remainingEncodedBytes), digits(summary.remainingEncodedBytes)], ['Retained metadata (separate)', readableBytes(summary.metadataEncodedBytes), digits(summary.metadataEncodedBytes)], ['Assessed', new Date(summary.assessedAt).toLocaleString()]]}/>
    <p className="dh-tiny">Verified plus additional required equals published download content at this dated assessment, not a live transfer count. Shared bytes are not separately measured. Metadata may overlap content objects, so there is no grand disk total. These are not installed-game sizes.</p></>;
}
function InventoryStorage({ inventory }: { inventory: InventorySummary }) {
  const measured = BigInt(inventory.totalEncodedBytes) > 0n, digits = (value: string) => BigInt(value).toLocaleString('en-US');
  const own = (BigInt(inventory.verifiedEncodedBytes) - BigInt(inventory.sharedVerifiedEncodedBytes)).toString();
  return <>{measured ? <div className="dh-stack inventory-retention-chart" role="img" aria-label={`${exactBytes(inventory.verifiedEncodedBytes)} verified locally, including ${exactBytes(inventory.sharedVerifiedEncodedBytes)} shared, of ${exactBytes(inventory.totalEncodedBytes)} required`}><i className="dh-own" style={{ width: encodedPercentage(own, inventory.totalEncodedBytes) }}/><i className="dh-shared" style={{ width: encodedPercentage(inventory.sharedVerifiedEncodedBytes, inventory.totalEncodedBytes) }}/></div> : <p className="dh-reason">The verified ledger declares zero encoded content bytes. No percentage is inferred from an empty denominator.</p>}
    {measured && <div className="dh-legend"><span><i className="dh-own"/>Verified locally · only this build</span><span><i className="dh-shared"/>Verified locally · shared</span><span><i/>Additional encoded content</span></div>}
    <Ledger rows={[[<>Verified locally available{measured && <span className="dh-dim"> · {encodedPercentage(inventory.verifiedEncodedBytes, inventory.totalEncodedBytes)}</span>}</>, readableBytes(inventory.verifiedEncodedBytes), digits(inventory.verifiedEncodedBytes)], ['Of those, shared with other builds', readableBytes(inventory.sharedVerifiedEncodedBytes), digits(inventory.sharedVerifiedEncodedBytes)], ['Additional encoded content required', readableBytes(inventory.remainingEncodedBytes), digits(inventory.remainingEncodedBytes)], ['Retained metadata (separate)', readableBytes(inventory.metadataEncodedBytes), digits(inventory.metadataEncodedBytes)]]}/>
    <p className="dh-tiny">Shared bytes are part of the verified bytes, not added again. Metadata may overlap content objects, so there is no grand disk total. These are not installed-game sizes.</p></>;
}
function Readiness({ snapshot, inventory, catalog, catalogChecking = false, gameData, onMetadata }: { snapshot: WorkspaceSnapshot; inventory: InventorySummary | null; catalog: FileCatalogStatus | null; catalogChecking?: boolean; gameData: string; onMetadata: () => void }) {
  const covered = useCoveredSide(), summary = snapshot.download, partial = snapshot.readiness === 'partial';
  const rows: [string, ReactNode, string?][] = inventory ? [['Required metadata', 'Verified', 'dh-ok'], ['File catalog', 'Ready', 'dh-ok'], ['Unique ROOT/IN contents', exactCount(inventory.uniqueContentCount)], ['Exact encoded objects', exactCount(inventory.downloadObjectCount)], ['Payload content locations', 'Not assessed', 'dh-dim'], ['Every file downloadable?', 'Not checked', 'dh-warn-t'], ['Research decoders', 'Selected WDC5 tables'], ['Encryption assessment', 'Not assessed', 'dh-dim']]
    : [['Build and CDN configs', 'Verified', 'dh-ok'], ['Download manifest', summary ? 'Verified' : 'Unavailable', summary ? 'dh-ok' : 'dh-warn-t'], ['File list', catalog?.state === 'ready' ? 'Ready' : catalog?.state === 'pending' ? 'Preparing…' : catalog?.state === 'stopped' ? 'Stopped' : catalog?.state === 'removed' ? 'Freed' : catalogChecking ? 'Checking…' : 'Not prepared', catalog?.state === 'ready' ? 'dh-ok' : 'dh-dim'], ['Decoded / installed size', 'Unknown', 'dh-dim'], ['Payload content locations', 'Not assessed', 'dh-dim'], ['Every object downloadable?', 'Not checked', 'dh-warn-t'], ['Research decoders', 'Selected WDC5 tables'], ['Encryption assessment', 'Not assessed', 'dh-dim']];
  rows.push(['Game data areas', gameData || 'Not provided by this engine', gameData ? undefined : 'dh-dim']);
  const explanation = inventory ? 'Metadata and the published file inventory are verified. Content stays on demand.' : summary ? 'Metadata and the download manifest are verified. Content stays on demand.' : 'Configurations identify this exact build. Download sizing is unavailable.';
  return <aside className="dh-panel dh-side" aria-labelledby="home-readiness" inert={covered}><div className="dh-panel-head"><h2 id="home-readiness">Build readiness</h2><span className="dh-scope">All published variants</span></div>
    <div className="dh-panel-body"><div className={`dh-state-box${summary || inventory ? '' : ' dh-warn'}`} style={{ marginBottom: '.746667rem' }}><strong>{partial ? 'Partial' : snapshot.readiness === 'configurationReady' ? 'Configuration verified' : snapshot.readiness}</strong><p>{explanation}</p></div><Facts rows={rows}/><p className="dh-tiny">Partial is separate from catalog readiness. A complete metadata inventory does not prove every payload is still downloadable or decodable.</p></div>
    <div className="dh-panel-foot"><button className="dh-btn" onClick={onMetadata}>View metadata details</button></div></aside>;
}
export function DownloadMeasurement({ snapshot }: { snapshot: WorkspaceSnapshot }) {
  const summary = snapshot.download;
  return <><p><strong>All published variants in the download manifest.</strong> Exact full encoding keys (EKeys) are deduplicated. Encoded object bytes are separate from a ROOT file catalog, decoded size, installed size and network-transfer overhead.</p>{summary ? <dl className="detail-list">{[['Ledger SHA-256 identity', summary.ledgerId], ['Assessed at', summary.assessedAt], ['Unique encoded objects', exactCount(summary.objectCount)], ['Published download content', exactBytes(summary.totalEncodedBytes)], ['Verified locally at assessment', exactBytes(summary.verifiedEncodedBytes)], ['Additional encoded content required', exactBytes(summary.remainingEncodedBytes)], ['Separate retained metadata encoded bytes', exactBytes(summary.metadataEncodedBytes)], ['Decoded / installed size', 'Unknown'], ['Research file catalog', 'Not prepared']].map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl> : <p>{snapshot.downloadProblem?.code}: {snapshot.downloadProblem?.message} No size or local-retention total has been inferred.</p>}<p>Verified local plus additional required equals published download content at the recorded assessment. Metadata may overlap content objects physically. No grand disk-space total or cross-build saving is inferred. Upstream availability remains unassessed.</p></>;
}
