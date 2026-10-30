import { useCoveredSide, useShell } from './HomeShell';
import type {SourceRecordRequest} from './useItemSourceNavigation';
import { isPartialSource, tableDataMatches } from '../shared/table-data';
import { TableDataView, RecordInspector, rawPhysicalType, candidateIntegerHint, type AdmittedRecord } from './TableDataView';
import { useTableLoading, type TableLoading } from './useTableLoading';
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { isFileDataReference, type DataPosition, type FileTarget, type FollowFilter, type FollowTarget } from './table-follow-view';
import { validRecordId } from '../shared/table-data';
import { createPortal } from 'react-dom';
import type { TableCatalogEntry, TableCatalogPage, TableDefinitionField, TableDefinitionProvider, TableDefinitionSnapshot, TableDataSnapshot, TableReference, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { fetchedLabel, fieldType, uncoveredDefinitionMatch, tableCatalogMatches, tableDefinitionMatches, type TablesCatalogParams } from '../shared/tables';
import { exactCount } from '../shared/inventory';

type Tab = 'data' | 'structure' | 'relations';
// One trail entry. A Follow (#19) adds its filter; position is where its Data view was, so Back and Forward put it back.
type View = { id: number; snapshot: TableDefinitionSnapshot; field: string | null; layout: string | null; tab: Tab; target: string | null; sourceRecord?:SourceRecordRequest; follow?: FollowFilter; position?: DataPosition };
type Failure = { code: string; message: string; retryable: boolean };
const title = (entry: TableCatalogEntry) => entry.name ?? `Unnamed table · ${entry.tableId}`;
const named = (field: TableDefinitionField) => `${field.name}${field.verifiedName ? '' : ' ?'}`;
export function TablesExplorer({ snapshot, bridge, active, tableLoading, sourceRecord, onReturnToItems, onOpenFile, tableRequest }: { snapshot: WorkspaceSnapshot; bridge: DiscoveryBridge; active: boolean; tableLoading?: TableLoading; sourceRecord?:SourceRecordRequest|null; onReturnToItems?:()=>void; onOpenFile?:(target:Omit<FileTarget,'seq'>)=>void; tableRequest?:{id:number;tableId:string;name:string}|null }) {
  const container=useRef<HTMLElement>(null),covered=useCoveredSide(),{compact,statusSlot}=useShell();
  // At enlarged text one pane shows at a time (docs/design/desktop-home). Picking or following a table shows Records.
  const [tablePane,setTablePane]=useState<'catalog'|'records'|'inspector'>('catalog'),[filterSlot,setFilterSlot]=useState<HTMLElement|null>(null),[footSlot,setFootSlot]=useState<HTMLElement|null>(null),[inspectorFoot,setInspectorFoot]=useState<HTMLElement|null>(null),[inspectorDetails,setInspectorDetails]=useState<HTMLElement|null>(null),[headingSlot,setHeadingSlot]=useState<HTMLElement|null>(null);
  // Declared reference targets, looked up in the local provider catalogue so Relationships can say whether a target can be followed.
  type Target={state:'bundled'|'notBundled'|'missing'|'unknown';entry?:TableCatalogEntry};
  const [targets,setTargets]=useState<Record<string,Target>>({}),targetSequence=useRef(0);
  const localLoading = useTableLoading(bridge,!tableLoading), flow = tableLoading ?? localLoading;
  const [dataViews,setDataViews] = useState<TableDefinitionSnapshot[]>([]), [record,setRecord] = useState<AdmittedRecord|null>(null), [admissions,setAdmissions] = useState<Record<string,TableDataSnapshot>>({});
  const dataFocus = useRef(0),consumedDataFocus=useRef(0),preferredMode=useRef<Tab|null>(null);
  const context = { productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region };
  const [page, setPage] = useState<TableCatalogPage | null>(null), [draft, setDraft] = useState('');
  const [catalogBusy, setCatalogBusy] = useState(false), [definitionBusy, setDefinitionBusy] = useState(false);
  const [catalogError, setCatalogError] = useState<Failure | null>(null), [definitionError, setDefinitionError] = useState<Failure | null>(null);
  const [cursor, setCursor] = useState<string | undefined>(), [pages, setPages] = useState<(string | undefined)[]>([]);
  const [trail, setTrail] = useState<View[]>([]), [position, setPosition] = useState(0), [notice, setNotice] = useState('');
  const viewIds = useRef(0), fileOrigin = useRef<HTMLElement | null>(null), openFile = useRef(onOpenFile); openFile.current = onOpenFile;
  // Follow by value needs the engine capability; older engines keep definition navigation only.
  const [followCapable, setFollowCapable] = useState(!!bridge.tableFollow && !bridge.capabilities);
  useEffect(() => { let live = true; void bridge.capabilities?.().then(reply => { if (live && reply.ok) setFollowCapable(!!bridge.tableFollow && reply.value.includes('tables-follow-v1')); }).catch(() => { /* Keep Follow off. */ }); return () => { live = false; }; }, [bridge]);
  const search = useRef<HTMLInputElement>(null), catalogRetry = useRef<HTMLButtonElement>(null), catalogFocus = useRef<HTMLElement | null>(null);
  const heading = useRef<HTMLHeadingElement>(null), tableHeading = useRef<HTMLHeadingElement>(null), inspector = useRef<HTMLElement>(null);
  const alive = useRef(true), activeNow = useRef(active), catalogSequence = useRef(0), definitionSequence = useRef(0);
  const provider = useRef<TableDefinitionProvider | null>(null), latestCursor = useRef(cursor); latestCursor.current = cursor; activeNow.current = active;
  const compactNow = useRef(compact), tablePaneNow = useRef(tablePane); compactNow.current = compact; tablePaneNow.current = tablePane;
  useEffect(() => { if (!active) return; const key = (event: KeyboardEvent) => { if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'f' && !search.current?.closest('[inert]')) { event.preventDefault(); setTablePane('catalog'); requestAnimationFrame(() => search.current?.focus()); } }; document.addEventListener('keydown', key); return () => document.removeEventListener('keydown', key); }, [active]);
  type Movement = 'fresh' | 'next' | 'previous' | 'first';
  const lastCatalog = useRef<{ params: TablesCatalogParams; movement: Movement }>({ params: { ...context, query: '', limit: 50 }, movement: 'fresh' });
  const focusIntent = useRef<{ kind: 'heading' } | null>(null);
  useLayoutEffect(() => {
    const intent = focusIntent.current; if (!intent || !activeNow.current) return; focusIntent.current = null;
    tableHeading.current?.focus({ preventScroll: true });
  }, [trail, position]);
  const currentTrail = useRef(trail), currentPosition = useRef(position); currentTrail.current = trail; currentPosition.current = position;
  const view = trail[position];
  const admission = view && admissions[view.snapshot.entry.tableId];
  const visibleRecord = record && record.snapshot.tableId === view?.snapshot.entry.tableId && (!admission || record.snapshot.artifactId === admission.artifactId) ? record : null;
  const focusedSourceField=useRef(0);
  useLayoutEffect(()=>{const focus=visibleRecord?.sourceFocus;if(!active||view?.tab!=='data'||!focus||focusedSourceField.current===focus.requestId)return;const field=inspector.current?.querySelector<HTMLElement>(`[data-source-column="${focus.columnIndex}"]`);if(field){focusedSourceField.current=focus.requestId;if(document.activeElement!==document.body&&document.activeElement!==focus.origin)return;field.scrollIntoView({block:'start'});field.focus({preventScroll:true});}},[active,visibleRecord,view?.tab]);
  const definition = view?.snapshot.definition;
  const candidate = definition?.candidates.find(value => value.layoutHash === view?.layout);
  // D080: the build isn't listed, so any candidate can only match the file by layout.
  const layoutOnly = view?.snapshot.problem?.code === 'SCHEMA_BUILD_NOT_LISTED';
  const fields = candidate?.fields ?? definition?.columns ?? [];
  const selected = fields.find(field => field.name === view?.field);
  const displayedAdmission = admission?.layoutHash === candidate?.layoutHash ? admission : undefined;
  const selectedRawColumns = displayedAdmission?.columns.filter(column=>column.interpretation && (column.name===selected?.name || column.name.startsWith(`${selected?.name}[`))) ?? [];
  useLayoutEffect(() => { const origin = catalogFocus.current; if (!origin || catalogError) return; catalogFocus.current = null; if (activeNow.current && (document.activeElement === document.body || document.activeElement === origin)) search.current?.focus({ preventScroll: true }); }, [page, catalogError]);
  function changeQuery(query: string) { catalogSequence.current++; setCatalogBusy(false); setCatalogError(null); setDraft(query); }
  useEffect(() => { alive.current = true; return () => { alive.current = false; catalogSequence.current++; definitionSequence.current++; }; }, []);
  useEffect(() => {
    if (active) {
      const op=flow.operation,explicit=flow.viewRequest>consumedDataFocus.current&&op&&op.definition.productId===context.productId&&op.definition.buildIdentity===context.buildIdentity&&op.definition.sourceRegion===context.sourceRegion;
      if(explicit)consumedDataFocus.current=flow.viewRequest;
      else if (fileOrigin.current?.isConnected) { fileOrigin.current.focus({ preventScroll: true }); fileOrigin.current = null; }
      else (compactNow.current&&tablePaneNow.current!=='catalog'&&tableHeading.current?tableHeading.current:heading.current)?.focus({ preventScroll: true });
    }
    else { focusIntent.current = null; catalogSequence.current++; definitionSequence.current++; setCatalogBusy(false); setDefinitionBusy(false); }
  }, [active]);
  useEffect(() => {
    if (!active || page?.query === draft) return;
    if (!page && draft === '') { void readCatalog('', undefined, 'fresh'); return; }
    const timer = setTimeout(() => { void readCatalog(draft, undefined, 'fresh'); }, 150);
    return () => clearTimeout(timer);
  }, [active, draft]);
  async function readCatalog(query: string, after: string | undefined, movement: Movement) {
    const generation = ++catalogSequence.current;
    const params: TablesCatalogParams = { ...context, query, limit: 50, ...(after === undefined ? {} : { after }) };
    lastCatalog.current = { params, movement }; setCatalogBusy(true);
    try {
      const reply = await bridge.tablesCatalog(params);
      if (!alive.current || !activeNow.current || generation !== catalogSequence.current) return;
      if (!reply.ok) { setCatalogError(reply.error); return; }
      if (!tableCatalogMatches(reply.value, params, provider.current ?? undefined)) { setCatalogError({ code: 'TABLES_CONTEXT_MISMATCH', message: 'The catalogue does not match this exact context, query or pinned provider. Previous results are preserved.', retryable: false }); return; }
      if (document.activeElement === catalogRetry.current) catalogFocus.current = catalogRetry.current;
      provider.current = reply.value.provider; setPage(reply.value); setCatalogError(null); setCursor(after);
      if (movement === 'fresh' || movement === 'first') setPages([]);
      if (movement === 'next') setPages(previous => [...previous.slice(-127), latestCursor.current]);
      if (movement === 'previous') setPages(previous => previous.slice(0, -1));
    } catch { if (alive.current && generation === catalogSequence.current) setCatalogError({ code: 'ENGINE_UNAVAILABLE', message: 'The local provider catalogue could not be read. Previous results are preserved.', retryable: true }); }
    finally { if (alive.current && generation === catalogSequence.current) setCatalogBusy(false); }
  }
  async function openDefinition(entry: TableCatalogEntry, append = false, target: string | null = null, sourceField: string | null = null) {
    const generation = ++definitionSequence.current; setDefinitionBusy(true); setDefinitionError(null); setNotice('');
    try {
      const params = { ...context, tableId: entry.tableId }, reply = await bridge.tableDefinition(params);
      if (!alive.current || !activeNow.current || generation !== definitionSequence.current) return;
      if (!reply.ok) { setDefinitionError(reply.error); return; }
      if (!tableDefinitionMatches(reply.value, params, snapshot.build.version, provider.current ?? undefined)) { setDefinitionError({ code: 'DEFINITION_CONTEXT_MISMATCH', message: 'This definition belongs to another build, table or provider. The previous selection is preserved.', retryable: false }); return; }
      const next: View = { id: ++viewIds.current, snapshot: reply.value, field: target, layout: reply.value.definition?.candidates.length === 1 ? reply.value.definition.candidates[0].layoutHash : null, tab: append ? 'structure' : preferredMode.current==='structure'||preferredMode.current==='relations' ? preferredMode.current : ['Item','ItemClass'].includes(entry.name??'') ? 'data' : 'structure', target };
      const sourceTrail = currentTrail.current.slice(0, currentPosition.current + 1).map((value, index) => index === currentPosition.current && sourceField ? { ...value, field: sourceField } : value);
      const nextTrail = append ? [...sourceTrail, next] : [next];
      if (nextTrail.length > 64) setNotice('The definition trail keeps the most recent 64 entries. Earlier entries were dropped.');
      focusIntent.current = { kind: 'heading' }; setTablePane('records');
      setTrail(nextTrail.slice(-64)); setPosition(Math.min(nextTrail.length, 64) - 1);

    } catch { if (alive.current && generation === definitionSequence.current) setDefinitionError({ code: 'ENGINE_UNAVAILABLE', message: 'This local definition could not be read. The previous selection is preserved.', retryable: true }); }
    finally { if (alive.current && generation === definitionSequence.current) setDefinitionBusy(false); }
  }
  async function follow(field: TableDefinitionField) {
    if (!field.reference) return;
    const generation = ++definitionSequence.current; setDefinitionBusy(true); setDefinitionError(null);
    try {
      const found = await findCatalogTable(bridge, context, field.reference.table, provider.current ?? undefined);
      if (!alive.current || !activeNow.current || generation !== definitionSequence.current) return;
      if (found.kind === 'failed') { setDefinitionError(found.error); return; }
      if (found.kind === 'mismatch') { setDefinitionError({ code: 'PROVIDER_CONTEXT_MISMATCH', message: 'The target search returned a different provider or build context.', retryable: false }); return; }
      if (found.kind !== 'found') { setDefinitionError({ code: found.kind === 'ambiguous' ? 'DEFINITION_AMBIGUOUS' : 'TARGET_DEFINITION_UNAVAILABLE', message: `The exact target definition ${field.reference.table}.${field.reference.column} could not be identified uniquely in this bounded provider search. No record lookup or acquisition was performed.`, retryable: false }); return; }
      await openDefinition(found.entry, true, field.reference.column, field.name);
    } catch { if (alive.current && generation === definitionSequence.current) setDefinitionError({ code: 'ENGINE_UNAVAILABLE', message: 'The target definition could not be read. The source selection is preserved.', retryable: true }); }
    finally { if (alive.current && generation === definitionSequence.current) setDefinitionBusy(false); }
  }
  // Follow a declared reference by its exact value (#19): the target table's Data, filtered by the engine on the target column.
  async function followValue(f: FollowFilter, origin: HTMLElement | null) {
    const target = followTarget(f.reference);
    if (target?.kind === 'files') { if (!validRecordId(f.value) || !openFile.current) return; fileOrigin.current = origin; openFile.current({ fileDataId: f.value, table: f.sourceTable, recordId: f.recordId }); return; }
    if (target?.kind !== 'table') return;
    const generation = ++definitionSequence.current; setDefinitionBusy(true); setDefinitionError(null); setNotice('');
    try {
      const params = { ...context, tableId: target.tableId }, reply = await bridge.tableDefinition(params);
      if (!alive.current || !activeNow.current || generation !== definitionSequence.current) return;
      if (!reply.ok) { setDefinitionError(reply.error); return; }
      if (!tableDefinitionMatches(reply.value, params, snapshot.build.version, provider.current ?? undefined) || reply.value.entry.name !== f.reference.table) { setDefinitionError({ code: 'DEFINITION_CONTEXT_MISMATCH', message: `The ${f.reference.table} definition does not match this exact build, table or provider. The source record is preserved.`, retryable: false }); return; }
      const next: View = { id: ++viewIds.current, snapshot: reply.value, field: f.reference.column, layout: reply.value.definition?.candidates.length === 1 ? reply.value.definition.candidates[0].layoutHash : null, tab: 'data', target: null, follow: f };
      const at = currentPosition.current, sourceTrail = currentTrail.current.slice(0, at + 1).map((value, index) => index === at ? { ...value, field: f.column.replace(/\[\d+\]$/, '') } : value);
      const nextTrail = [...sourceTrail, next];
      if (nextTrail.length > 64) setNotice('The trail keeps the most recent 64 entries. Earlier entries were dropped.');
      focusIntent.current = { kind: 'heading' }; setTablePane('records');
      setTrail(nextTrail.slice(-64)); setPosition(Math.min(nextTrail.length, 64) - 1);
    } catch { if (alive.current && generation === definitionSequence.current) setDefinitionError({ code: 'ENGINE_UNAVAILABLE', message: 'The target definition could not be read. The source record is preserved.', retryable: true }); }
    finally { if (alive.current && generation === definitionSequence.current) setDefinitionBusy(false); }
  }
  async function definitionFor(rows: TableDataSnapshot): Promise<boolean> {
    const params = { ...context, tableId: rows.tableId };
    try {
      const reply = await bridge.tableDefinition(params);
      if (!alive.current || !reply.ok || !tableDefinitionMatches(reply.value, params, snapshot.build.version, provider.current ?? undefined) ||
        !tableDataMatches(rows, { ...params, researchLocale: 'enUS' }, reply.value)) return false;
      const next = reply.value, layout = next.definition?.candidates.some(candidate => candidate.layoutHash === rows.layoutHash) ? rows.layoutHash : null;
      setTrail(previous => previous.map(v => v.snapshot.entry.tableId === rows.tableId ? { ...v, snapshot: next, layout: v.layout && next.definition?.candidates.some(c => c.layoutHash === v.layout) ? v.layout : layout } : v));
      setDataViews(previous => previous.map(v => v.entry.tableId === rows.tableId ? next : v));
      return true;
    } catch { return false; }
  }
  // D081: a change of the fetch switch in Settings re-reads the open table's definition, so its notice and inspector
  // say what the engine reads with now.
  async function refreshDefinition(tableId: string) {
    const params = { ...context, tableId };
    try {
      const reply = await bridge.tableDefinition(params);
      if (!alive.current || !reply.ok || !tableDefinitionMatches(reply.value, params, snapshot.build.version, provider.current ?? undefined)) return;
      const next = reply.value, only = next.definition?.candidates.length === 1 ? next.definition.candidates[0].layoutHash : null;
      setTrail(previous => previous.map(v => v.snapshot.entry.tableId === tableId ? { ...v, snapshot: next, layout: v.layout && next.definition?.candidates.some(c => c.layoutHash === v.layout) ? v.layout : only } : v));
      setDataViews(previous => previous.map(v => v.entry.tableId === tableId ? next : v));
    } catch { /* The definition shown stays until the table is opened again. */ }
  }
  // Another area (Items while it prepares) can ask for one table by ID: it opens like a catalogue pick, on its Data tab.
  const tableRouted = useRef(0);
  useEffect(() => {
    if (!active || !tableRequest || tableRouted.current === tableRequest.id) return;
    tableRouted.current = tableRequest.id;
    void openDefinition({ tableId: tableRequest.tableId, name: tableRequest.name } as TableCatalogEntry);
  }, [active, tableRequest?.id]);
  const refreshLatest = useRef(refreshDefinition); refreshLatest.current = refreshDefinition;
  useEffect(() => {
    const changed = (event: Event) => {
      if ((event as CustomEvent<{ name?: string }>).detail?.name !== 'fetchTableDefinitions') return;
      const shown = currentTrail.current[currentPosition.current]; if (shown) void refreshLatest.current(shown.snapshot.entry.tableId);
    };
    window.addEventListener('vibeminer-settings-changed', changed);
    return () => window.removeEventListener('vibeminer-settings-changed', changed);
  }, []);
  // #81: a failed load may have asked WoWDBDefs, so the inspector reads the definition again and says what that check found.
  const failedRead = useRef<string | null>(null);
  useEffect(() => {
    const op = flow.operation;
    if (!op || op.status !== 'failed' || failedRead.current === op.id || op.definition.productId !== context.productId || op.definition.buildIdentity !== context.buildIdentity || op.definition.sourceRegion !== context.sourceRegion) return;
    failedRead.current = op.id;
    if (currentTrail.current.some(v => v.snapshot.entry.tableId === op.definition.entry.tableId)) void refreshLatest.current(op.definition.entry.tableId);
  }, [flow.operation?.id, flow.operation?.status]);
  const savePosition = (id: number, value: DataPosition) => setTrail(previous => previous.some(v => v.id === id) ? previous.map(v => v.id === id ? { ...v, position: value } : v) : previous);
  const clearFollow = (id: number) => setTrail(previous => previous.map(v => v.id === id ? { ...v, follow: undefined, position: undefined } : v));
  function update(changes: Partial<View>) { setTrail(previous => previous.map((value, index) => index === position ? { ...value, ...changes } : value)); }
  function navigate(next: number) {
    definitionSequence.current++; setDefinitionBusy(false); setDefinitionError(null); setPosition(next);
    // ←, → and a crumb keep focus on themselves (docs/design/desktop-home); the destination's content isn't focused.
    focusIntent.current = null;
    setTrail(previous => [...previous]);
  }
  useEffect(() => {
    if (!view || view.tab !== 'data') return;
    setDataViews(previous => previous.some(x => x.entry.tableId === view.snapshot.entry.tableId) ? previous : [...previous.slice(-7),view.snapshot]);
  }, [view?.snapshot,view?.tab]);
  useEffect(() => {
    if (!flow.viewRequest || !flow.operation || flow.operation.definition.productId!==context.productId || flow.operation.definition.buildIdentity!==context.buildIdentity || flow.operation.definition.sourceRegion!==context.sourceRegion) return;
    const value=flow.operation.definition;
    definitionSequence.current++; setDefinitionBusy(false); setDefinitionError(null);
    dataFocus.current=flow.viewRequest;if(activeNow.current)consumedDataFocus.current=flow.viewRequest;setTablePane('records');
    setTrail(previous => { const index=previous.findIndex(x=>x.snapshot.entry.tableId===value.entry.tableId); if(index>=0){setPosition(index);return previous.map((v,i)=>i===index?{...v,tab:'data'}:v);}setPosition(0);return [{id:++viewIds.current,snapshot:value,field:null,layout:value.definition?.candidates.length===1?value.definition.candidates[0].layoutHash:null,tab:'data',target:null}]; });
  }, [flow.viewRequest]);
  const routed=useRef(0);
  useEffect(()=>{
    if(!active||!sourceRecord||routed.current===sourceRecord.id)return;
    routed.current=sourceRecord.id;
    const source=sourceRecord.snapshot,params={...context,tableId:source.tableId},generation=++definitionSequence.current;
    setDefinitionBusy(true);setDefinitionError(null);
    void bridge.tableDefinition(params).then(reply=>{
      if(!alive.current||!activeNow.current||generation!==definitionSequence.current)return;
      if(!reply.ok){setDefinitionError(reply.error);return;}
      if(!tableDefinitionMatches(reply.value,params,snapshot.build.version,provider.current??undefined)){setDefinitionError({code:'DEFINITION_CONTEXT_MISMATCH',message:'The exact item source definition could not be matched. Return to Items; its selection is preserved.',retryable:false});return;}
      const next:View={id:++viewIds.current,snapshot:reply.value,field:null,layout:source.layoutHash,tab:'data',target:null,sourceRecord};setTablePane('records');
      setTrail(previous=>{const nextTrail=[...previous.slice(0,currentPosition.current+1),next].slice(-64);setPosition(nextTrail.length-1);return nextTrail;});
      setDataViews(previous=>[...previous.filter(value=>value.entry.tableId!==source.tableId).slice(-7),reply.value]);
    }).catch(()=>{if(alive.current&&generation===definitionSequence.current)setDefinitionError({code:'ENGINE_UNAVAILABLE',message:'The exact source definition could not be read. Return to Items; its selection is preserved.',retryable:true});}).finally(()=>{if(alive.current&&generation===definitionSequence.current)setDefinitionBusy(false);});
  },[active,sourceRecord?.id]);
  const hiddenSelection = !!view && !!page && !page.entries.some(entry => entry.tableId === view.snapshot.entry.tableId);
  const tableName = view ? title(view.snapshot.entry) : '';
  const provided = view?.snapshot.provider ?? page?.provider;
  const recordShown = view?.tab === 'data' && !!visibleRecord;
  const returnLink = sourceRecord && onReturnToItems ? <><button className="dh-btn dh-small dh-return-link" onClick={onReturnToItems}>← Return to {sourceRecord.origin === 'creatures' ? 'Creatures' : 'Items'} selection</button><span className="dh-trail-sep" aria-hidden="true"/></> : null;
  const physical = candidate ? admission && admission.layoutHash === candidate.layoutHash ? isPartialSource(admission.source) ? `Observed layout verified for the admitted readable projection · Partial. Protected sections not loaded; full decoded content key not verified.` : `Physical layout verified for the admitted enUS artifact.${admission.coverage.scope === 'unencryptedSections' ? ' Readable rows available · Partial; protected sections not loaded.' : ''}` : 'Physical header/layout not verified.' : null;
  const source = <><div className="dh-label">Source</div><dl className="dh-facts">
    <dt>Product / build</dt><dd>{snapshot.build.productId} · {snapshot.build.version}</dd>
    <dt>Build identity</dt><dd className="dh-mono dh-selectable">{snapshot.build.id}</dd>
    <dt>Source / locale</dt><dd>{snapshot.sourceReference.region.toUpperCase()} / enUS for table data · platform not selected</dd>
    <dt>Physical source / rows</dt><dd>{admission ? `${isPartialSource(admission.source) ? 'Authenticated encoded source' : 'Verified'} / ${admission.rowCount} admitted rows · enUS · ${admission.coverage.scope === 'complete' ? 'complete admitted section scope' : 'Partial · protected sections not loaded'}` : 'Not checked / Not prepared'}</dd>
    {admission && <><dt>Full decoded content key</dt><dd>{isPartialSource(admission.source) ? 'Not verified · expected key only' : 'Verified'}</dd></>}
    {view && <><dt>Provider table</dt><dd className="dh-mono dh-selectable">{view.snapshot.entry.tableId}</dd><dt>DB2 FileDataID</dt><dd className="dh-selectable">{view.snapshot.entry.db2FileDataId ?? 'Not supplied · not an absence claim'}</dd></>}
    {candidate && <><dt>Candidate layout</dt><dd className="dh-mono">{candidate.layoutHash}</dd></>}
    {view?.snapshot.definition && (layoutOnly || admission?.schemaMatch === 'layoutOnly') && <><dt>Definition match</dt><dd className="definition-match">{admission?.schemaMatch === 'layoutOnly' ? 'Matched by layout, build not listed' : definition?.candidates.length ? 'Build not listed · opens only if the file’s layout matches a candidate' : view.snapshot.definitionFetch === 'on' && !definition?.fetched ? uncoveredDefinitionMatch(view.snapshot.definitionCheck) : 'Build not listed · no layout on its version line, so the table can’t open'}</dd></>}
    {view?.snapshot.definition && <><dt>Definition from</dt><dd className="definition-source">{view.snapshot.definition.fetched ? <>{fetchedLabel(view.snapshot.definition.fetched)} · not bundled or verified · WoWDBDefs contributors, CC BY-SA 4.0</> : 'Bundled with VibeMiner'}</dd></>}
    {view?.snapshot.definition && <><dt>Definition SHA-256</dt><dd className="dh-mono dh-selectable">{view.snapshot.definition.sha256}</dd><dt>Definition source</dt><dd className="dh-mono dh-selectable">{view.snapshot.definition.sourceUrl}</dd></>}
    {provided && <><dt>Definitions</dt><dd>{provided.name}</dd><dt>License</dt><dd>{provided.license}</dd><dt>{definition?.fetched ? 'Bundled revision' : 'Revision'}</dt><dd className="dh-mono dh-selectable">{provided.revision}</dd>{definition?.fetched && <><dt>Fetched revision</dt><dd className="dh-mono dh-selectable">{definition.fetched.revision}</dd></>}<dt>Provider manifest SHA-256</dt><dd className="dh-mono dh-selectable">{provided.manifestSha256}</dd><dt>Provider source</dt><dd className="dh-mono dh-selectable">{provided.sourceUrl}</dd></>}
  </dl><div className="dh-insp-details" ref={setInspectorDetails}/></>;
  const declaredWidth = (field: TableDefinitionField) => field.family === 'int' && field.integerBits !== null ? `${field.integerBits}-bit ${field.unsigned ? 'unsigned' : 'signed'}` : field.family === 'float' ? 'Not declared' : 'String';
  const inspectorPane = recordShown ? 'Selected record' : 'Inspector';
  const referenced = [...new Set(fields.filter(field => field.reference).map(field => field.reference!.table))].slice(0, 32);
  useEffect(() => {
    // On Data only once records are admitted: reference cells exist only then.
    if (!active || !view || (view.tab === 'structure' && !selected?.reference) || (view.tab === 'data' && !admission)) return;
    const names = referenced.filter(name => !(name in targets)); if (!names.length) return;
    const generation = ++targetSequence.current;
    void (async () => { for (const name of names) {
      const found = await findCatalogTable(bridge, context, name, provider.current ?? undefined);
      if (!alive.current || generation !== targetSequence.current) return;
      const value: Target = found.kind === 'found' ? { state: found.entry.definitionState, entry: found.entry } : found.kind === 'absent' ? { state: 'missing' } : { state: 'unknown' };
      setTargets(previous => ({ ...previous, [name]: value }));
    } })();
  }, [active, view?.snapshot, view?.tab, selected?.name, referenced.join('|'), !!admission]);
  const filesAvailable = !!onOpenFile;
  const followTarget = useMemo(() => (r: TableReference): FollowTarget | undefined => {
    if (isFileDataReference(r)) return filesAvailable ? { kind: 'files' } : { kind: 'none', reason: 'FileDataID · Files can’t be opened from here.' };
    const t = targets[r.table];
    if (!t) return { kind: 'checking' };
    if (t.state === 'bundled' && t.entry) return followCapable ? { kind: 'table', tableId: t.entry.tableId } : { kind: 'none', reason: 'This engine can’t follow references by value.' };
    if (t.state === 'notBundled') return { kind: 'none', reason: `${r.table} definition not bundled, can’t follow.` };
    if (t.state === 'missing') return { kind: 'none', reason: `The provider lists no ${r.table} table, so it can’t be followed.` };
    return { kind: 'none', reason: `The ${r.table} definition couldn’t be identified, so it can’t be followed.` };
  }, [targets, followCapable, filesAvailable]);
  function relationAction(field: TableDefinitionField, before?: () => void) {
    const reference = field.reference!, target = targets[reference.table];
    if (target?.state === 'notBundled') return <span className="dh-reason">Can’t follow: no bundled definition</span>;
    if (target?.state === 'missing') return <span className="dh-reason">Can’t follow: the provider lists no {reference.table} table</span>;
    return <button className="dh-btn dh-small" data-relation-field={field.name} onClick={() => { before?.(); void follow(field); }}>Open {reference.table}.{reference.column} definition</button>;
  }
  function relationStatus(field: TableDefinitionField) {
    const reference = field.reference!, target = targets[reference.table];
    if (!target) return 'Checking the target definition…';
    if (target.state === 'notBundled') return <>Provider entry {target.entry!.tableId} · definition not bundled <code>(DEFINITION_NOT_BUNDLED)</code></>;
    if (target.state === 'missing') return 'The provider catalogue lists no table with this name.';
    if (target.state === 'unknown') return 'Target state not checked. Opening it checks the exact definition.';
    const admitted = admissions[target.entry!.tableId];
    return `Definition bundled · ${admitted ? admitted.coverage.scope === 'complete' ? 'records admitted (enUS)' : 'readable rows only · protected sections not loaded' : 'records not checked in this session'} · opens the definition at ${reference.column}; it runs no row query`;
  }
  const blockedView = view ? flow.blocked(view.snapshot) : null;
  const status = active && statusSlot ? createPortal(<>{provided && <span className="dh-secondary">Definitions <strong>{provided.name}</strong> · {definition?.fetched ? `fetched ${definition.fetched.revision.slice(0, 7)}` : provided.revision.slice(0, 7)}</span>}{view && <span className="dh-secondary">{tableName} · {admission ? <><strong>{exactCount(admission.rowCount)}</strong> admitted rows{admission.coverage.scope === 'complete' ? '' : ' · Partial'}</> : blockedView?.kind === 'stopped' ? 'loading stopped' : !view.snapshot.definition ? 'no bundled definition' : 'records not prepared'}</span>}</>, statusSlot) : null;
  return <section ref={container} className="dh-tables tables-explorer" data-pane={tablePane} hidden={!active} aria-label="Tables workbench">
    {status}
    <div className="dh-pane-switch"><div className="dh-segmented" role="group" aria-label="Show pane">{([['catalog', 'Tables list'], ['records', 'Records'], ['inspector', inspectorPane]] as const).map(([key, label]) => <button key={key} aria-pressed={tablePane === key} onClick={() => setTablePane(key)}>{label}</button>)}</div></div>
    <aside className="dh-pane dh-catalog" aria-labelledby="tables-heading">
      <div className="dh-pane-head"><h1 ref={heading} id="tables-heading" tabIndex={-1}>Tables</h1>{page && <span className="dh-n" aria-label={`${exactCount(page.totalCount)} matching provider entries`}>{exactCount(page.totalCount)}</span>}<input ref={search} id="table-query" className="dh-control dh-search" type="search" maxLength={80} value={draft} placeholder="Name, ID or field" aria-label="Search definitions" aria-keyshortcuts="Control+F" onChange={event => changeQuery(event.target.value)}/></div>
      {catalogError && <div className="dh-notice dh-error" role="alert"><div className="dh-text"><strong>{catalogError.code}</strong>{catalogError.message}{catalogError.retryable && <div className="dh-row"><button ref={catalogRetry} className="dh-btn dh-small" onClick={() => void readCatalog(lastCatalog.current.params.query ?? '', lastCatalog.current.params.after, lastCatalog.current.movement)}>Retry catalogue</button></div>}</div></div>}
      {page && page.query !== draft && <div className="dh-notice dh-calm"><div className="dh-text">Showing previous catalogue results for “{page.query || 'all definitions'}”.</div></div>}
      {hiddenSelection && <div className="dh-notice dh-calm selection-note"><div className="dh-text">Selected definition is outside this catalogue page.{draft && <div className="dh-row"><button className="dh-btn dh-small" onClick={() => changeQuery('')}>Clear search</button></div>}</div></div>}
      {page?.entries.length === 0 && <div className="dh-notice dh-calm"><div className="dh-text">No provider definitions match this search. This does not establish table absence from the build.{draft && !hiddenSelection && <div className="dh-row"><button className="dh-btn dh-small" onClick={() => changeQuery('')}>Clear search</button></div>}</div></div>}
      <div className="dh-cat-list tables-catalog-list" role="region" aria-label="Provider definitions on this page" tabIndex={0}>{page?.entries.map(entry => <button className="dh-tableitem tableitem" aria-pressed={view?.snapshot.entry.tableId === entry.tableId} key={entry.tableId} onClick={() => void openDefinition(entry)}><span className="dh-glyph" aria-hidden="true">▦</span><span><b>{camel(title(entry))}</b><small>{entry.definitionState === 'bundled' ? 'Definition bundled' : 'Definition not bundled'} · {entry.tableId}</small></span></button>)}</div>
      <div className="dh-pane-foot" role="group" aria-label="Definition catalogue pages"><span className="dh-count" role="status" aria-live="polite">{catalogBusy ? 'Reading local provider metadata…' : page ? `${page.entries.length} provider entries on this page · ${exactCount(page.totalCount)} matching` : 'Provider catalogue not loaded.'}</span><button className="dh-btn" aria-label="First catalogue page" aria-disabled={catalogBusy || page?.query !== draft || cursor === undefined} onClick={() => { if (!catalogBusy && page?.query === draft && cursor !== undefined) void readCatalog(page?.query ?? draft, undefined, 'first'); }}>First</button><button className="dh-btn" aria-label="Previous catalogue page" aria-disabled={catalogBusy || page?.query !== draft || !pages.length} onClick={() => { if (!catalogBusy && page?.query === draft && pages.length) void readCatalog(page?.query ?? draft, pages.at(-1), 'previous'); }}>Previous</button><button className="dh-btn" aria-label="Next catalogue page" aria-disabled={catalogBusy || page?.query !== draft || !page?.nextCursor} onClick={() => { if (!catalogBusy && page?.query === draft && page?.nextCursor) void readCatalog(page.query, page.nextCursor, 'next'); }}>Next</button></div>
    </aside>
    <section className="dh-pane dh-records" aria-labelledby="table-title" aria-busy={definitionBusy}>
      <div className="dh-pane-head"><div className="dh-trail" role="group" aria-label="Definition investigation trail"><button className="dh-btn dh-nav" aria-label="Back through definitions" aria-disabled={position === 0 || definitionBusy} onClick={() => { if (position > 0 && !definitionBusy) navigate(position - 1); }}>←</button><button className="dh-btn dh-nav" aria-label="Forward through definitions" aria-disabled={position >= trail.length - 1 || definitionBusy} onClick={() => { if (position < trail.length - 1 && !definitionBusy) navigate(position + 1); }}>→</button>{trail.map((value, index) => <span key={index} style={{ display: 'contents' }}>{index > 0 && <span className="dh-crumb-sep" aria-hidden="true">›</span>}<button className="dh-crumb crumb" aria-current={index === position ? 'step' : undefined} onClick={() => navigate(index)}>{title(value.snapshot.entry)}{value.target ? `.${value.target}` : ''}</button></span>)}</div>
        <span className="dh-spacer"/><div className="dh-data-slot" ref={setFilterSlot} hidden={view?.tab !== 'data'}/>
        {view && <div className="dh-segmented" role="group" aria-label="Table definition view">{(['data', 'structure', 'relations'] as const).map(tab => <button aria-pressed={view.tab === tab} key={tab} onClick={() => { preferredMode.current = tab; update({ tab }); }}>{tab === 'relations' ? 'Relationships' : tab[0].toUpperCase() + tab.slice(1)}</button>)}</div>}</div>
      <div className={`dh-titlerow${returnLink ? ' dh-has-return' : ''}`}>{returnLink}<h2 ref={tableHeading} id="table-title" tabIndex={-1}>{view ? tableName : 'Select a table definition'}</h2>{compact && view?.tab === 'data' && <span className="dh-heading-slot" ref={setHeadingSlot}/>}<span className="dh-meta">{view ? `Provider table ${view.snapshot.entry.tableId} · FileDataID ${view.snapshot.entry.db2FileDataId ?? 'not supplied'}` : 'Definitions describe community interpretations, independently of acquired files and admitted records.'}</span></div>
      {definitionBusy && <div className="dh-notice dh-calm" role="status"><div className="dh-text">Reading a local definition… Previous selection remains visible.</div></div>}
      {definitionError && <div className="dh-notice dh-error" role="alert"><div className="dh-text"><strong>{definitionError.code}</strong>{definitionError.message} Try the same definition or relationship action again; the previous selection is preserved.</div></div>}
      {notice && <div className="dh-notice dh-calm selection-note"><div className="dh-text">{notice}</div></div>}
      {view?.snapshot.problem && !(view.snapshot.problem.code === 'SCHEMA_BUILD_NOT_LISTED' && admission?.schemaMatch === 'layoutOnly') && <div className={`dh-notice dh-def-problem${view.tab === 'data' ? ' dh-on-data' : ''}`} title={view.tab === 'data' ? view.snapshot.problem.message : undefined}><div className="dh-text"><strong>{view.snapshot.problem.code}</strong>{view.snapshot.problem.message}</div></div>}
      {!view && <div className="dh-pane-scroll"><div className="dh-local-wait"><div className="dh-state-box dh-plain"><strong>No table selected</strong><p>Choose a definition from the Tables list. Definitions and table records are separate: definition browsing performs no game-content download, and Data prepares missing table content as needed for research locale enUS.</p></div></div></div>}
      {view?.tab === 'structure' && <><div className="dh-subbar dh-structure-bar"><strong className="dh-ink">DBD structure</strong>{definition && definition.candidates.length > 1 && <label>Candidate layout<select className="dh-control" value={view.layout ?? ''} onChange={event => update({ layout: event.target.value || null, field: null })}><option value="">Universal column declarations</option>{definition.candidates.map(layout => <option key={layout.layoutHash} value={layout.layoutHash}>{layout.layoutHash}</option>)}</select></label>}<span>{candidate ? layoutOnly ? `Candidate ${candidate.layoutHash} · layout match only, build not listed · ${fields.length} declared fields` : `Candidate ${candidate.layoutHash} explicitly matches selected build ${view.snapshot.buildVersion} · ${fields.length} declared fields` : definition ? `Universal COLUMNS declarations; no candidate layout is selected. These are not a matched physical structure · ${fields.length} declared fields` : 'The definition source is not bundled.'}</span><span className="dh-spacer"/>{physical && <span>{physical}</span>}</div>
        {fields.length > 0 ? <div className="dh-grid-scroll" role="region" aria-label={`${tableName} declared fields`} tabIndex={0}><table className="dh-records-grid dh-struct"><thead><tr><th scope="col">#</th><th scope="col">Field</th><th scope="col">Type</th><th scope="col">Width · sign</th><th scope="col">Array</th><th scope="col">Key</th><th scope="col">Reference</th><th scope="col">Annotations</th></tr></thead><tbody>{fields.map((field, index) => <tr key={field.name} aria-selected={view.field === field.name}><td className="dh-dim">{index}</td><td><button className="dh-rowsel" data-field-name={field.name} aria-label={`Inspect field ${field.name}`} aria-pressed={view.field === field.name} onClick={() => update({ field: field.name })}>{field.name}{!field.verifiedName && <span className="dh-unv" title="Unverified name">?</span>}</button></td><td className="dh-mono">{fieldType(field)}</td><td>{declaredWidth(field)}</td><td>{field.arrayLength > 1 ? field.arrayLength : <span className="dh-dim">—</span>}</td><td>{field.isId ? <span className="dh-pill">$id$</span> : <span className="dh-dim">—</span>}</td><td>{field.reference ? `→ ${field.reference.table}.${field.reference.column}` : <span className="dh-dim">None declared</span>}</td><td>{[field.nonInline && '$noninline$', field.relationStorage && '$relation$', !field.verifiedName && 'Unverified name ?'].filter(Boolean).join(' · ') || <span className="dh-dim">—</span>}{displayedAdmission?.columns.filter(column => column.interpretation && (column.name === field.name || column.name.startsWith(`${field.name}[`))).map(column => <span className="dh-raw-note raw-structure-note" key={column.name}>{column.name}: {rawPhysicalType(column.interpretation!)} admitted; DBD hint {candidateIntegerHint(column.interpretation!)} remains a pinned community interpretation.</span>)}</td></tr>)}</tbody></table></div> : <div className="dh-pane-scroll"/>}</>}
      {view?.tab === 'relations' && <div className="dh-pane-scroll dh-rel-list declared-relations"><p className="dh-reason" style={{ marginTop: '.56rem' }}>Provider-declared definition targets only. No value lookup, target record, uniqueness or label is implied.</p><div className="dh-label">Outgoing · declared by {tableName}</div>
        {fields.filter(field => field.reference).map(field => <div className={`dh-relcard${view.field === field.name ? ' dh-current' : ''}`} key={field.name}><b className="dh-mono">{tableName}.{field.name}{!field.verifiedName && <span className="dh-unv" title="Unverified name">?</span>} → {field.reference!.table}.{field.reference!.column}</b><span className="dh-act">{relationAction(field, () => update({ field: field.name }))}</span><span className="dh-st">{relationStatus(field)}</span></div>)}
        {!fields.some(field => field.reference) && <p className="dh-reason">No relationships are declared in the displayed field set. Plain arrays and similar names do not create an edge.</p>}
        <div className="dh-label">Incoming</div><p className="dh-reason">Not available. The definition contract returns only {tableName}’s own declarations, so references from other tables to {tableName} aren’t listed.</p>
        <div className="dh-label">No declared reference</div><p className="dh-reason">{fields.filter(field => !field.reference && !field.isId).map(field => field.name).join(', ') || 'None'}. These stay plain values; a matching number never creates a link.</p></div>}
      {dataViews.map(value => <TableDataView key={value.entry.tableId} definition={value} bridge={bridge} flow={flow} active={active && view?.tab === 'data' && view.snapshot.entry.tableId === value.entry.tableId} focusRequest={dataFocus.current} sourceRecord={view?.sourceRecord?.snapshot.tableId === value.entry.tableId ? view.sourceRecord : undefined} onRecord={setRecord} filterSlot={filterSlot} footSlot={footSlot} inspectorFoot={inspectorFoot} inspectorDetails={inspectorDetails} headingSlot={compact && view?.tab === 'data' ? headingSlot : null} onAdmission={value => setAdmissions(previous => ({ ...Object.fromEntries(Object.entries(previous).slice(-7)), [value.tableId]: value }))} entry={view?.snapshot.entry.tableId === value.entry.tableId ? { id: view.id, follow: view.follow, position: view.position } : undefined} onPosition={savePosition} onDefinitionChanged={definitionFor} followTarget={followTarget} onFollow={(f, origin) => void followValue(f, origin)} onClearFollow={clearFollow}/>)}
      <div className="dh-pane-foot"><div className="dh-foot-slot" ref={setFootSlot} hidden={view?.tab !== 'data'}/>{view && view.tab !== 'data' && provided && <><span>Definitions: {provided.name} · {definition?.fetched ? 'fetched revision' : 'revision'} <span className="dh-mono">{(definition?.fetched?.revision ?? provided.revision).slice(0, 7)}</span> · {provided.license}</span><span className="dh-spacer"/><span>Not SQL: no UNIQUE, NOT NULL or CASCADE is implied</span></>}{!view && <span>Definitions: WoWDBDefs contributors. Provider metadata does not prove file presence, readable rows or gameplay meaning.</span>}</div>
    </section>
    <aside ref={inspector} className="dh-inspector" aria-labelledby="table-inspector-heading" inert={covered}>{recordShown ? <RecordInspector record={visibleRecord!}>{source}</RecordInspector> : <><div className="dh-insp-head"><div className="dh-label">{selected ? 'Field inspector' : 'Definition inspector'}</div><h2 id="table-inspector-heading" tabIndex={-1}>{selected ? `${tableName}.${named(selected)}` : 'Source & interpretation'}</h2>{selected && <p>{fieldType(selected)} · {declaredWidth(selected)}</p>}</div>
      <div className="dh-insp-body">{selected ? <><dl className="dh-facts"><dt>Declared type</dt><dd className="dh-mono">{fieldType(selected)}</dd><dt>Integer width / sign</dt><dd>{selected.integerBits === null ? 'Not declared in this field set' : `${selected.integerBits} bits · ${selected.unsigned ? 'unsigned' : 'signed'}`}</dd><dt>Array length</dt><dd>{selected.arrayLength}</dd><dt>Name confidence</dt><dd>{selected.verifiedName ? 'Provider name' : 'Unverified name (?) · not a nullable marker'}</dd><dt>Declared key</dt><dd>{selected.isId ? '$id$ declaration' : 'Not declared as ID'}</dd><dt>Annotations</dt><dd>{[selected.nonInline && '$noninline$', selected.relationStorage && '$relation$'].filter(Boolean).join(' · ') || 'None declared'}</dd><dt>Comment</dt><dd>{selected.comment ?? 'No provider comment'}</dd><dt>Declared target</dt><dd>{selected.reference ? `${selected.reference.table}.${selected.reference.column}` : 'None. No link is inferred from values.'}</dd></dl>
        {selectedRawColumns.map(column => <div className="dh-state-box dh-plain raw-structure-note" style={{ marginTop: '.56rem' }} key={column.name}><strong>{column.name} · admitted representation: {rawPhysicalType(column.interpretation!)}</strong><p>Original DBD hint: {candidateIntegerHint(column.interpretation!)}. {column.interpretation!.warning}</p><p>No related record or label is inferred from this raw value.</p></div>)}
        {selected.reference && <><div className="dh-row" style={{ display: 'flex', marginTop: '.56rem' }}>{relationAction(selected)}</div><p className="dh-tiny">Definition navigation only. It runs no row query and adds no label.</p></>}</> : <p className="dh-reason" style={{ fontSize: 'var(--fs-m)' }}>Select a field to inspect its declaration. No game record has been selected.</p>}
        {view?.target && !selected && <div className="dh-notice" style={{ margin: '.56rem 0 0' }}><div className="dh-text">Declared target field {view.target} is not in the displayed field set. No substitute field or record was selected.</div></div>}
        <div style={{ marginTop: '.933333rem' }}>{source}</div></div></>}<div className="dh-insp-foot" ref={setInspectorFoot}/></aside>
  </section>;
}
// Exact-name lookup in the local provider catalogue. The engine search is a substring match on names, IDs and fields,
// so it reads at most CATALOG_LOOKUP_PAGES pages and reports `truncated` instead of absence when results remain.
export const CATALOG_LOOKUP_PAGES = 4;
type CatalogLookup = { kind: 'found'; entry: TableCatalogEntry } | { kind: 'absent' } | { kind: 'truncated' } | { kind: 'ambiguous' } | { kind: 'mismatch' } | { kind: 'failed'; error: Failure };
export async function findCatalogTable(bridge: DiscoveryBridge, context: { productId: string; buildIdentity: string; sourceRegion: string }, name: string, expected?: TableDefinitionProvider): Promise<CatalogLookup> {
  let after: string | undefined, match: TableCatalogEntry | null = null;
  for (let page = 0; page < CATALOG_LOOKUP_PAGES; page++) {
    const params: TablesCatalogParams = { ...context, query: name, limit: 100, ...(after === undefined ? {} : { after }) };
    let reply: Awaited<ReturnType<DiscoveryBridge['tablesCatalog']>>;
    try { reply = await bridge.tablesCatalog(params); } catch { return { kind: 'failed', error: { code: 'ENGINE_UNAVAILABLE', message: 'The target definition could not be read. The source selection is preserved.', retryable: true } }; }
    if (!reply.ok) return { kind: 'failed', error: reply.error };
    if (!tableCatalogMatches(reply.value, params, expected)) return { kind: 'mismatch' };
    for (const entry of reply.value.entries) if (entry.name === name) { if (match) return { kind: 'ambiguous' }; match = entry; }
    if (reply.value.nextCursor === null) return match ? { kind: 'found', entry: match } : { kind: 'absent' };
    if (match) return { kind: 'found', entry: match };
    after = reply.value.nextCursor;
  }
  return { kind: 'truncated' };
}
const camel = (name: string) => name.split(/(?<=[a-z0-9])(?=[A-Z])/).flatMap((part, index) => index ? [<wbr key={index}/>, part] : [part]);
