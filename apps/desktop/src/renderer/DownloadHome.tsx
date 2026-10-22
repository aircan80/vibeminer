import type {ItemEffectsLoading} from './useItemEffects';
import type {CreatureDisplays} from './useCreatureDisplay';
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
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import type { FileTarget } from './table-follow-view';
import type { Build, InventorySummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { encodedPercentage, exactBytes, exactCount, isInventoryWorkspace, readableBytes } from '../shared/inventory';
import type { Opening } from './useWorkspaceFlow';
import { BuildAreas, useHomeFocus, type BuildArea } from './BuildAreas';
import { TablesExplorer } from './TablesExplorer';
import { DownloadObjects, FilesUnavailable } from './DownloadObjects';
import { FilesCatalog } from './FilesCatalog';
import { HomeShell, useCoveredSide, type ShellArea } from './HomeShell';

type Evidence = 'source' | 'metadata' | 'measurement';
type Pane = 'overview' | 'files' | 'tables' | 'items' | 'creatures';
const shellArea: Record<Pane, ShellArea> = { overview: 'home', files: 'files', tables: 'tables', items: 'items', creatures: 'creatures' };
const joined = (values: string[]) => values.length ? values.map(x => x.toUpperCase()).join(', ') : 'no recorded region';
// Home after a build opens (docs/design/desktop-home): one shell for the download-sized and the catalog-prepared workspace.
export function DownloadHome({ activityNavigation, effectsLoading, effectsAvailable=false, creatureDisplays, creatureDisplayAvailable=false, creaturesLoading, creaturesAvailable=false, classificationsLoading, classificationsAvailable=false, subclassAvailable=false, iconsLoading, iconsAvailable=false, itemsLoading, itemsAvailable=false, retention, operation: op, catalogBuild, fixtureLabel, bridge, onChoose, onReopen, evidence, tableLoading }: { activityNavigation?:{area:'tables'|'items'|'creatures';sequence:number}|null;effectsLoading?:ItemEffectsLoading;effectsAvailable?:boolean; creatureDisplays?:CreatureDisplays;creatureDisplayAvailable?:boolean; creaturesLoading?:CreaturesLoading;creaturesAvailable?:boolean; classificationsLoading?:Classifications;classificationsAvailable?:boolean;subclassAvailable?:boolean; iconsLoading?:IconsLoading;iconsAvailable?:boolean; itemsLoading?: ItemsLoading; itemsAvailable?: boolean; retention?: RetentionController; tableLoading: TableLoading; operation: Opening & { result: WorkspaceSnapshot }; catalogBuild?: Build | null; fixtureLabel?: string | null; bridge: DiscoveryBridge; onChoose: () => void; onReopen: () => void; evidence: (mode: Evidence, close: () => void) => ReactNode }) {
  const snapshot = op.result, summary = snapshot.download, inventory = isInventoryWorkspace(snapshot) ? snapshot : null;
  const [pane, setPane] = useState<Pane>('overview');
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
    :pane==='files'?<span className="dh-secondary">{inventory?<>Catalog generation <strong>{inventory.inventory.generation}</strong> · {exactCount(inventory.inventory.membershipCount)} memberships</>:summary?<>Download manifest · <strong>{exactCount(summary.objectCount)}</strong> encoded objects</>:'File catalog not prepared'}</span>:null;
  return <HomeShell snapshot={snapshot} catalogBuild={catalogBuild} label={op.label} retention={retention} onChoose={onChoose} area={shellArea[pane]} areas={areas} fixtureLabel={fixtureLabel} status={status} dialog={dialog && evidence(dialog, () => setDialog(null))}>
    <section className="dh-home" hidden={pane !== 'overview'} aria-labelledby="home-heading">
      <div className="dh-pane-head"><h1 ref={heading} id="home-heading" tabIndex={-1}>Build overview</h1><span className="dh-sub">{snapshot.sourceReference.region.toUpperCase()} download source · advertised in {joined(snapshot.build.advertisedRegions)} · measurements cover every published variant</span><span className="dh-spacer"/><HomeUtilities onMetadata={() => setDialog('metadata')} onSource={() => setDialog('source')}/></div>
      <div className="dh-home-grid" ref={grid}>
        <section className="dh-panel" aria-labelledby="home-sizes"><div className="dh-panel-head"><h2 id="home-sizes">Download sizes</h2><span className="dh-scope">Encoded bytes</span></div><div className="dh-panel-body">{inventory ? <InventorySizes inventory={inventory.inventory}/> : <DownloadSizes snapshot={snapshot} onReopen={onReopen}/>}</div><div className="dh-panel-foot"><button className="dh-btn dh-small" onClick={() => setDialog('measurement')}>Scope and measurement details</button></div></section>
        <section className="dh-panel" aria-labelledby="home-catalog"><div className="dh-panel-head"><h2 id="home-catalog">File catalog</h2><span className="dh-scope">ROOT and install manifests</span></div><div className="dh-panel-body">{inventory ? <CatalogFacts inventory={inventory.inventory}/> : <><div className="dh-metric"><span className="dh-mlabel">Catalog memberships</span><strong className="dh-unknown">Not prepared</strong><span className="dh-exact">No membership count is inferred</span></div><p className="dh-reason">{summary ? 'Files lists the download manifest’s encoded objects for now. Tables, Items and Creatures prepare their own data.' : 'Tables, Items and Creatures prepare their own data.'}</p></>}</div><div className="dh-panel-foot"><button className="dh-btn dh-small" onClick={files}>Browse in Files</button></div></section>
        <section className="dh-panel dh-wide" aria-labelledby="home-storage"><div className="dh-panel-head"><h2 id="home-storage">Local content &amp; shared storage</h2><span className="dh-scope">{inventory ? 'Published inventory scope · exact build' : 'Download manifest scope · exact build'}</span></div><div className="dh-panel-body"><div className="dh-storage-cols"><div>{inventory ? <InventoryStorage inventory={inventory.inventory}/> : <DownloadStorage snapshot={snapshot}/>}</div><div><RetentionModeBox flow={inventory ? undefined : retention} target={retentionTarget}/></div></div></div></section>
        <Readiness snapshot={snapshot} inventory={inventory?.inventory ?? null} gameData={gameData} onMetadata={() => setDialog('metadata')}/>
      </div>
    </section>
    {inventory ? <FilesCatalog key={`${snapshot.build.productId}:${snapshot.build.id}:${snapshot.sourceReference.region}:${inventory.inventory.generation}`} snapshot={inventory} bridge={bridge} active={pane === 'files'} onReopen={onReopen} onReturn={fromHome ? returnHome : undefined} fileTarget={fileTarget} onReturnToTable={returnToTable}/>
      : summary ? <DownloadObjects key={`${snapshot.build.id}:${summary.ledgerId}:${summary.assessedAt}`} snapshot={snapshot} summary={summary} bridge={bridge} active={pane === 'files'} onReopen={onReopen} onReturn={fromHome ? returnHome : undefined} fileTarget={fileTarget} onReturnToTable={returnToTable}/>
      : <FilesUnavailable snapshot={snapshot} active={pane === 'files'} onReturn={fromHome ? returnHome : undefined} fileTarget={fileTarget} onReturnToTable={returnToTable}/>}
    {items&&<div className="dh-area-scroll" hidden={pane!=='items'}><ItemsBrowser effectsLoading={effectsLoading} effectsAvailable={effectsAvailable} effectsSource={itemSource} classificationsLoading={classificationsLoading} classificationsAvailable={classificationsAvailable} subclassAvailable={subclassAvailable} iconsLoading={iconsLoading} iconsAvailable={iconsAvailable} key={`items:${snapshot.build.productId}:${snapshot.build.id}:${snapshot.sourceReference.region}`} workspace={snapshot} bridge={bridge} active={pane==='items'} flow={itemsLoading!} focusRequest={itemsLoading!.viewRequest} onSource={itemSource.open}/></div>}
    {creatures&&<div className="dh-area-scroll" hidden={pane!=='creatures'}><CreaturesBrowser displays={creatureDisplays} displayAvailable={creatureDisplayAvailable} key={`creatures:${snapshot.build.productId}:${snapshot.build.id}:${snapshot.sourceReference.region}`} workspace={snapshot} bridge={bridge} active={pane==='creatures'} flow={creaturesLoading!} focusRequest={creaturesLoading!.viewRequest} onSource={(source,id)=>itemSource.open(source,id,'creatures')}/></div>}
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
function Readiness({ snapshot, inventory, gameData, onMetadata }: { snapshot: WorkspaceSnapshot; inventory: InventorySummary | null; gameData: string; onMetadata: () => void }) {
  const covered = useCoveredSide(), summary = snapshot.download, partial = snapshot.readiness === 'partial';
  const rows: [string, ReactNode, string?][] = inventory ? [['Required metadata', 'Verified', 'dh-ok'], ['File catalog', 'Ready', 'dh-ok'], ['Unique ROOT/IN contents', exactCount(inventory.uniqueContentCount)], ['Exact encoded objects', exactCount(inventory.downloadObjectCount)], ['Payload content locations', 'Not assessed', 'dh-dim'], ['Every file downloadable?', 'Not checked', 'dh-warn-t'], ['Research decoders', 'Selected WDC5 tables'], ['Encryption assessment', 'Not assessed', 'dh-dim']]
    : [['Build and CDN configs', 'Verified', 'dh-ok'], ['Download manifest', summary ? 'Verified' : 'Unavailable', summary ? 'dh-ok' : 'dh-warn-t'], ['Research file catalog', 'Not prepared', 'dh-dim'], ['Decoded / installed size', 'Unknown', 'dh-dim'], ['Payload content locations', 'Not assessed', 'dh-dim'], ['Every object downloadable?', 'Not checked', 'dh-warn-t'], ['Research decoders', 'Selected WDC5 tables'], ['Encryption assessment', 'Not assessed', 'dh-dim']];
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
