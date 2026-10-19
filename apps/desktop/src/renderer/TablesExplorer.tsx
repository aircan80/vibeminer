import {useTablesLayout} from './useTablesLayout';
import type {SourceRecordRequest} from './useItemSourceNavigation';
import { isPartialSource } from '../shared/table-data';
import { TableDataView, RecordInspector, rawPhysicalType, candidateIntegerHint, type AdmittedRecord } from './TableDataView';
import { useTableLoading, type TableLoading } from './useTableLoading';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { TableCatalogEntry, TableCatalogPage, TableDefinitionField, TableDefinitionProvider, TableDefinitionSnapshot, TableDataSnapshot, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { fieldType, tableCatalogMatches, tableDefinitionMatches, type TablesCatalogParams } from '../shared/tables';
import { exactCount } from '../shared/inventory';
import './tables.css';

type Tab = 'data' | 'structure' | 'relations';
type View = { snapshot: TableDefinitionSnapshot; field: string | null; layout: string | null; tab: Tab; target: string | null; returnToRelation?: boolean; sourceRecord?:SourceRecordRequest };
type Failure = { code: string; message: string; retryable: boolean };
const title = (entry: TableCatalogEntry) => entry.name ?? `Unnamed table · ${entry.tableId}`;
const named = (field: TableDefinitionField) => `${field.name}${field.verifiedName ? '' : ' ?'}`;
export function TablesExplorer({ snapshot, bridge, active, tableLoading, sourceRecord, onReturnToItems }: { snapshot: WorkspaceSnapshot; bridge: DiscoveryBridge; active: boolean; tableLoading?: TableLoading; sourceRecord?:SourceRecordRequest|null; onReturnToItems?:()=>void }) {
  const container=useRef<HTMLElement>(null);useTablesLayout(container,active);
  const localLoading = useTableLoading(bridge,!tableLoading), flow = tableLoading ?? localLoading;
  const [dataViews,setDataViews] = useState<TableDefinitionSnapshot[]>([]), [record,setRecord] = useState<AdmittedRecord|null>(null), [admissions,setAdmissions] = useState<Record<string,TableDataSnapshot>>({});
  const dataFocus = useRef(0),consumedDataFocus=useRef(0),preferredMode=useRef<Tab|null>(null);
  const context = { productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region };
  const [page, setPage] = useState<TableCatalogPage | null>(null), [draft, setDraft] = useState('');
  const [catalogBusy, setCatalogBusy] = useState(false), [definitionBusy, setDefinitionBusy] = useState(false);
  const [catalogError, setCatalogError] = useState<Failure | null>(null), [definitionError, setDefinitionError] = useState<Failure | null>(null);
  const [cursor, setCursor] = useState<string | undefined>(), [pages, setPages] = useState<(string | undefined)[]>([]);
  const [trail, setTrail] = useState<View[]>([]), [position, setPosition] = useState(0), [notice, setNotice] = useState('');
  const search = useRef<HTMLInputElement>(null), catalogRetry = useRef<HTMLButtonElement>(null), catalogFocus = useRef<HTMLElement | null>(null);
  const heading = useRef<HTMLHeadingElement>(null), tableHeading = useRef<HTMLHeadingElement>(null), inspector = useRef<HTMLDivElement>(null);
  const alive = useRef(true), activeNow = useRef(active), catalogSequence = useRef(0), definitionSequence = useRef(0);
  const provider = useRef<TableDefinitionProvider | null>(null), latestCursor = useRef(cursor); latestCursor.current = cursor; activeNow.current = active;
  type Movement = 'fresh' | 'next' | 'previous' | 'first';
  const lastCatalog = useRef<{ params: TablesCatalogParams; movement: Movement }>({ params: { ...context, query: '', limit: 50 }, movement: 'fresh' });
  const focusIntent = useRef<{ kind: 'heading' | 'trail'; field?: string | null; relation?: boolean } | null>(null);
  useLayoutEffect(() => {
    const intent = focusIntent.current; if (!intent || !activeNow.current) return; focusIntent.current = null;
    if (intent.kind === 'heading') { tableHeading.current?.focus({ preventScroll: true }); return; }
    const relation = intent.relation ? [...document.querySelectorAll<HTMLButtonElement>('.tables-explorer:not([hidden]) [data-relation-field]')].find(element => element.dataset.relationField === intent.field) : undefined;
    const field = [...document.querySelectorAll<HTMLButtonElement>('.tables-explorer:not([hidden]) [data-field-name]')].find(element => element.dataset.fieldName === intent.field);
    (relation ?? field ?? tableHeading.current)?.focus({ preventScroll: true });
  }, [trail, position]);
  const currentTrail = useRef(trail), currentPosition = useRef(position); currentTrail.current = trail; currentPosition.current = position;
  const view = trail[position];
  const admission = view && admissions[view.snapshot.entry.tableId];
  const visibleRecord = record && record.snapshot.tableId === view?.snapshot.entry.tableId && (!admission || record.snapshot.artifactId === admission.artifactId) ? record : null;
  const focusedSourceField=useRef(0);
  useLayoutEffect(()=>{const focus=visibleRecord?.sourceFocus;if(!active||view?.tab!=='data'||!focus||focusedSourceField.current===focus.requestId)return;const field=inspector.current?.querySelector<HTMLElement>(`[data-source-column="${focus.columnIndex}"]`);if(field){focusedSourceField.current=focus.requestId;if(document.activeElement!==document.body&&document.activeElement!==focus.origin)return;field.scrollIntoView({block:'start'});field.focus({preventScroll:true});}},[active,visibleRecord,view?.tab]);
  const definition = view?.snapshot.definition;
  const candidate = definition?.candidates.find(value => value.layoutHash === view?.layout);
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
      else {heading.current?.focus({ preventScroll: true }); window.scrollTo(0, 0);}
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
      const next: View = { snapshot: reply.value, field: target, layout: reply.value.definition?.candidates.length === 1 ? reply.value.definition.candidates[0].layoutHash : null, tab: append ? 'structure' : preferredMode.current==='structure'||preferredMode.current==='relations' ? preferredMode.current : ['Item','ItemClass'].includes(entry.name??'') ? 'data' : 'structure', target };
      const sourceTrail = currentTrail.current.slice(0, currentPosition.current + 1).map((value, index) => index === currentPosition.current && sourceField ? { ...value, field: sourceField, returnToRelation: true } : value);
      const nextTrail = append ? [...sourceTrail, next] : [next];
      if (nextTrail.length > 64) setNotice('The definition trail keeps the most recent 64 entries. Earlier entries were dropped.');
      focusIntent.current = { kind: 'heading' };
      setTrail(nextTrail.slice(-64)); setPosition(Math.min(nextTrail.length, 64) - 1);

    } catch { if (alive.current && generation === definitionSequence.current) setDefinitionError({ code: 'ENGINE_UNAVAILABLE', message: 'This local definition could not be read. The previous selection is preserved.', retryable: true }); }
    finally { if (alive.current && generation === definitionSequence.current) setDefinitionBusy(false); }
  }
  async function follow(field: TableDefinitionField) {
    if (!field.reference) return;
    const generation = ++definitionSequence.current; setDefinitionBusy(true); setDefinitionError(null);
    const params = { ...context, query: field.reference.table, limit: 100 };
    try {
      const reply = await bridge.tablesCatalog(params);
      if (!alive.current || !activeNow.current || generation !== definitionSequence.current) return;
      if (!reply.ok) { setDefinitionError(reply.error); return; }
      if (!tableCatalogMatches(reply.value, params, provider.current ?? undefined)) { setDefinitionError({ code: 'PROVIDER_CONTEXT_MISMATCH', message: 'The target search returned a different provider or build context.', retryable: false }); return; }
      const matches = reply.value.entries.filter(entry => entry.name === field.reference!.table);
      if (matches.length !== 1) { setDefinitionError({ code: matches.length ? 'DEFINITION_AMBIGUOUS' : 'TARGET_DEFINITION_UNAVAILABLE', message: `The exact target definition ${field.reference.table}.${field.reference.column} could not be identified uniquely in this bounded provider search. No record lookup or acquisition was performed.`, retryable: false }); return; }
      await openDefinition(matches[0], true, field.reference.column, field.name);
    } catch { if (alive.current && generation === definitionSequence.current) setDefinitionError({ code: 'ENGINE_UNAVAILABLE', message: 'The target definition could not be read. The source selection is preserved.', retryable: true }); }
    finally { if (alive.current && generation === definitionSequence.current) setDefinitionBusy(false); }
  }
  function update(changes: Partial<View>) { setTrail(previous => previous.map((value, index) => index === position ? { ...value, ...changes } : value)); }
  function navigate(next: number) {
    definitionSequence.current++; setDefinitionBusy(false); setDefinitionError(null); setPosition(next);
    const destination = trail[next];
    focusIntent.current = { kind: 'trail', field: destination?.field, relation: destination?.returnToRelation };
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
    dataFocus.current=flow.viewRequest;if(activeNow.current)consumedDataFocus.current=flow.viewRequest;
    setTrail(previous => { const index=previous.findIndex(x=>x.snapshot.entry.tableId===value.entry.tableId); if(index>=0){setPosition(index);return previous.map((v,i)=>i===index?{...v,tab:'data'}:v);}setPosition(0);return [{snapshot:value,field:null,layout:value.definition?.candidates.length===1?value.definition.candidates[0].layoutHash:null,tab:'data',target:null}]; });
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
      const next:View={snapshot:reply.value,field:null,layout:source.layoutHash,tab:'data',target:null,sourceRecord};
      setTrail(previous=>{const nextTrail=[...previous.slice(0,currentPosition.current+1),next].slice(-64);setPosition(nextTrail.length-1);return nextTrail;});
      setDataViews(previous=>[...previous.filter(value=>value.entry.tableId!==source.tableId).slice(-7),reply.value]);
    }).catch(()=>{if(alive.current&&generation===definitionSequence.current)setDefinitionError({code:'ENGINE_UNAVAILABLE',message:'The exact source definition could not be read. Return to Items; its selection is preserved.',retryable:true});}).finally(()=>{if(alive.current&&generation===definitionSequence.current)setDefinitionBusy(false);});
  },[active,sourceRecord?.id]);
  const hiddenSelection = !!view && !!page && !page.entries.some(entry => entry.tableId === view.snapshot.entry.tableId);
  return <section ref={container} className="tables-explorer" hidden={!active} aria-labelledby="tables-heading">
    <div className="tables-heading"><div><h1 ref={heading} id="tables-heading" tabIndex={-1}>Tables</h1><p>Browse pinned community definitions for {snapshot.build.productId} · {snapshot.build.version}. Source {snapshot.sourceReference.region.toUpperCase()}; table research locale enUS is explicit on Data. Platform remains unselected.</p></div></div>
    {sourceRecord&&onReturnToItems&&<button className="back" onClick={onReturnToItems}>← Return to {sourceRecord.origin==='creatures'?'Creatures':'Items'} selection</button>}
    <div className="table-workbench"><aside className="tables-catalog" aria-label="Definition catalogue"><div className="sectionlabel">Provider catalogue</div><label htmlFor="table-query">Search definitions</label><input ref={search} id="table-query" type="search" maxLength={80} value={draft} placeholder="Name, ID or bundled field" onChange={event => changeQuery(event.target.value)}/><p className="smallnote">Provider names, hashes, FileDataIDs and bundled field names. This is metadata search, not a list of tables present in this build or a search of records.</p>
      {catalogError && <div className="table-error" role="alert"><strong>{catalogError.code}</strong><p>{catalogError.message}</p>{catalogError.retryable && <button ref={catalogRetry} onClick={() => void readCatalog(lastCatalog.current.params.query ?? '', lastCatalog.current.params.after, lastCatalog.current.movement)}>Retry catalogue</button>}</div>}
      <p className="catalog-count" role="status" aria-live="polite">{catalogBusy ? 'Reading local provider metadata…' : page ? `${page.entries.length} provider entries on this page · ${exactCount(page.totalCount)} matching` : 'Provider catalogue not loaded.'}</p>{page && page.query !== draft && <p className="smallnote">Showing previous catalogue results for “{page.query || 'all definitions'}”.</p>}
      {hiddenSelection && <p className="selection-note">Selected definition is outside this catalogue page. {draft && <button onClick={() => changeQuery('')}>Clear search</button>}</p>}
      <div className="tables-catalog-list" role="region" aria-label="Provider definitions on this page" tabIndex={0}>{page?.entries.map(entry => <button className={`tableitem ${view?.snapshot.entry.tableId === entry.tableId ? 'selected' : ''}`} aria-pressed={view?.snapshot.entry.tableId === entry.tableId} key={entry.tableId} onClick={() => void openDefinition(entry)}><span className="glyph" aria-hidden="true">▦</span><span><b>{title(entry)}</b><small>{entry.definitionState === 'bundled' ? 'Definition bundled' : 'Definition not bundled'} · {entry.tableId}</small></span></button>)}</div>
      {page?.entries.length === 0 && <p className="smallnote">No provider definitions match this search. This does not establish table absence from the build.{draft && !hiddenSelection && <button onClick={() => changeQuery('')}>Clear search</button>}</p>}
      <div className="table-pages" role="group" aria-label="Definition catalogue pages"><button aria-disabled={catalogBusy || page?.query !== draft || cursor === undefined} onClick={() => { if (!catalogBusy && page?.query === draft && cursor !== undefined) void readCatalog(page?.query ?? draft, undefined, 'first'); }}>First page</button><button aria-disabled={catalogBusy || page?.query !== draft || !pages.length} onClick={() => { if (!catalogBusy && page?.query === draft && pages.length) void readCatalog(page?.query ?? draft, pages.at(-1), 'previous'); }}>Previous page</button><button aria-disabled={catalogBusy || page?.query !== draft || !page?.nextCursor} onClick={() => { if (!catalogBusy && page?.query === draft && page?.nextCursor) void readCatalog(page.query, page.nextCursor, 'next'); }}>Next page</button></div>
    </aside><div className="tables-center" aria-busy={definitionBusy}><nav className="definition-trail" aria-label="Definition investigation trail"><button aria-label="Back through definitions" aria-disabled={position === 0 || definitionBusy} onClick={() => { if (position > 0 && !definitionBusy) navigate(position - 1); }}>←</button><button aria-label="Forward through definitions" aria-disabled={position >= trail.length - 1 || definitionBusy} onClick={() => { if (position < trail.length - 1 && !definitionBusy) navigate(position + 1); }}>→</button>{trail.map((value, index) => <button key={index} className="crumb" aria-current={index === position ? 'step' : undefined} onClick={() => navigate(index)}>{title(value.snapshot.entry)}{value.target ? `.${value.target}` : ''}</button>)}</nav>
      <div className="table-title"><h2 ref={tableHeading} tabIndex={-1}>{view ? title(view.snapshot.entry) : 'Select a table definition'}</h2><p>{view ? `Provider table ${view.snapshot.entry.tableId} · FileDataID ${view.snapshot.entry.db2FileDataId ?? 'not supplied'}` : 'Definitions describe community interpretations, independently of acquired files and admitted records.'}</p></div>
      <div className="table-record-limit"><strong>Definitions and table records are separate.</strong><p>Definition browsing performs no game-content download. Data prepares missing table content as needed for research locale enUS. Structure and definition browsing stay local.</p></div>
      {definitionBusy && <p className="table-local-progress" role="status">Reading a local definition… Previous selection remains visible.</p>}{definitionError && <div className="table-error" role="alert"><strong>{definitionError.code}</strong><p>{definitionError.message}</p><p>Try the same definition or relationship action again; the previous selection is preserved.</p></div>}{notice && <p className="selection-note">{notice}</p>}
      {view && <><div className="definition-tabs" role="group" aria-label="Table definition view">{(['data', 'structure', 'relations'] as const).map(tab => <button aria-pressed={view.tab === tab} key={tab} onClick={() => { preferredMode.current=tab;update({ tab }); }}>{tab === 'relations' ? 'Relationships' : tab[0].toUpperCase() + tab.slice(1)}</button>)}</div>{view.snapshot.problem && <div className="table-notice"><strong>{view.snapshot.problem.code}</strong><p>{view.snapshot.problem.message}</p></div>}
        {view.tab === 'data' ? null : view.tab === 'structure' ? <><div className="candidate-choice">{definition && definition.candidates.length > 1 && <label>Candidate layout<select value={view.layout ?? ''} onChange={event => update({ layout: event.target.value || null, field: null })}><option value="">Universal column declarations</option>{definition.candidates.map(layout => <option key={layout.layoutHash} value={layout.layoutHash}>{layout.layoutHash}</option>)}</select></label>}<p>{candidate ? `Candidate ${candidate.layoutHash} explicitly matches selected build ${view.snapshot.buildVersion}. ${admission && admission.layoutHash===candidate.layoutHash ? isPartialSource(admission.source) ? 'Observed layout verified for the admitted readable projection · Partial. Protected sections not loaded; full decoded content key not verified.' : `Physical layout verified for the admitted enUS artifact.${admission.coverage.scope === 'unencryptedSections' ? ' Readable rows available · Partial; protected sections not loaded.' : ''}` : 'Physical header/layout not verified.'}` : definition ? 'Universal COLUMNS declarations; no candidate layout is selected. These are not a matched physical structure.' : 'The definition source is not bundled.'}</p></div>{fields.length > 0 && <p className="field-count">{fields.length} declared fields · {candidate ? 'selected build candidate' : 'provider columns'}</p>}<div className="structure-fields">{fields.map(field => <div className="schema-row" key={field.name}><button data-field-name={field.name} aria-label={`Inspect field ${field.name}`} aria-pressed={view.field === field.name} onClick={() => update({ field: field.name })}><b>{named(field)}</b><span>{fieldType(field)}</span></button><small>{[field.isId && 'Declared ID', field.nonInline && 'Noninline', field.relationStorage && 'Relation storage', field.reference && `→ ${field.reference.table}.${field.reference.column}`].filter(Boolean).join(' · ') || 'No declared relationship'}</small>{displayedAdmission?.columns.filter(column=>column.interpretation && (column.name===field.name || column.name.startsWith(`${field.name}[`))).map(column=><small className="raw-structure-note" key={column.name}>{column.name}: {rawPhysicalType(column.interpretation!)} admitted; DBD hint {candidateIntegerHint(column.interpretation!)} remains a pinned community interpretation.</small>)}</div>)}</div></> : <div className="declared-relations"><p>Provider-declared definition targets only. No value lookup, target record, uniqueness or label is implied.</p>{fields.filter(field => field.reference).map(field => <div className="declared-relation" key={field.name}><b>{view.snapshot.entry.name ?? view.snapshot.entry.tableId}.{field.name} → {field.reference!.table}.{field.reference!.column}</b><button data-relation-field={field.name} onClick={() => { update({ field: field.name }); void follow(field); }}>Open {field.reference!.table}.{field.reference!.column} definition</button></div>)}{!fields.some(field => field.reference) && <p>No relationships are declared in the displayed field set. Plain arrays and similar names do not create an edge.</p>}</div>}</>}
      {dataViews.map(value=><TableDataView key={value.entry.tableId} definition={value} bridge={bridge} flow={flow} active={active && view?.tab==='data' && view.snapshot.entry.tableId===value.entry.tableId} focusRequest={dataFocus.current} sourceRecord={view?.sourceRecord?.snapshot.tableId===value.entry.tableId?view.sourceRecord:undefined} onRecord={setRecord} onAdmission={value=>setAdmissions(previous=>({...Object.fromEntries(Object.entries(previous).slice(-7)),[value.tableId]:value}))}/>)}
      {view?.tab==='data' && visibleRecord && <button className="view-selected-field" onClick={()=>{inspector.current?.scrollIntoView({block:'start'});inspector.current?.focus({preventScroll:true});}}>View selected record</button>}
      {selected && !(view?.tab==='data'&&visibleRecord) && <button className="view-selected-field" onClick={() => { inspector.current?.scrollIntoView({ block: 'start' }); inspector.current?.focus({ preventScroll: true }); }}>View selected field</button>}
    </div><aside className="table-inspector" aria-labelledby="table-inspector-heading"><div className="sectionlabel">{view?.tab==='data'&&visibleRecord?'Record inspector':'Definition inspector'}</div><div className="table-inspector-body" ref={inspector} tabIndex={-1}>{view?.tab==='data'&&visibleRecord ? <RecordInspector record={visibleRecord!}/> : <><h3 id="table-inspector-heading" tabIndex={-1}>{selected ? named(selected) : 'Source & interpretation'}</h3>{selected ? <><Fact label="Declared type" value={fieldType(selected)}/>{selectedRawColumns.map(column=><div className="raw-structure-note" key={column.name}><Fact label={`${column.name} · admitted representation`} value={rawPhysicalType(column.interpretation!)}/><p>Original DBD hint: {candidateIntegerHint(column.interpretation!)}. {column.interpretation!.warning}</p><p>No related record or label is inferred from this raw value.</p></div>)}<Fact label="Integer width / sign" value={selected.integerBits === null ? 'Not declared in this field set' : `${selected.integerBits} bits · ${selected.unsigned ? 'unsigned' : 'signed'}`}/><Fact label="Array length" value={String(selected.arrayLength)}/><Fact label="Name confidence" value={selected.verifiedName ? 'Provider name' : 'Unverified name (?) · not a nullable marker'}/><Fact label="Declared key" value={selected.isId ? '$id$ declaration' : 'Not declared as ID'}/><Fact label="Physical annotations" value={[selected.nonInline && '$noninline$', selected.relationStorage && '$relation$'].filter(Boolean).join(' · ') || 'None declared'}/><Fact label="Comment" value={selected.comment ?? 'No provider comment'}/>{selected.reference && <div className="inspector-relation"><Fact label="Declared target" value={`${selected.reference.table}.${selected.reference.column}`}/><button data-relation-field={selected.name} onClick={() => void follow(selected)}>Open {selected.reference.table}.{selected.reference.column} definition</button><p>Definition navigation only; no row lookup or descriptive label.</p></div>}</> : <p>Select a field to inspect its declaration. No game record has been selected.</p>}{view?.target && !selected && <p className="table-notice">Declared target field {view.target} is not in the displayed field set. No substitute field or record was selected.</p>}</>}</div><div className="inspector-source"><Fact label="Exact product / build" value={`${snapshot.build.productId} · ${snapshot.build.version}`}/><Fact label="Build identity" value={snapshot.build.id}/><Fact label="Acquisition source / Home locale / platform" value={`${snapshot.sourceReference.region.toUpperCase()} / Not selected / Not selected`}/><Fact label="Physical source / rows" value={admission ? `${isPartialSource(admission.source) ? 'Authenticated encoded source' : 'Verified'} / ${admission.rowCount} admitted rows · enUS · ${admission.coverage.scope === 'complete' ? 'complete admitted section scope' : 'Partial · protected sections not loaded'}` : "Not checked / Not prepared"}/>{admission && <Fact label="Full decoded content key" value={isPartialSource(admission.source) ? 'Not verified · expected key only' : 'Verified'}/>}{view && <><Fact label="Provider table identity" value={view.snapshot.entry.tableId}/><Fact label="Provider DB2 FileDataID" value={view.snapshot.entry.db2FileDataId ?? 'Not supplied · not an absence claim'}/></>}{view?.snapshot.definition && <><Fact label="Definition SHA-256" value={view.snapshot.definition.sha256}/><Fact label="Definition source" value={view.snapshot.definition.sourceUrl}/></>}{(view?.snapshot.provider ?? page?.provider) && <Provider provider={(view?.snapshot.provider ?? page?.provider)!}/>}</div></aside></div>
    <footer className="tables-footer">Definitions: WoWDBDefs contributors. Provider metadata and candidate declarations do not prove file presence, readable rows or gameplay meaning. Definition browsing does not include game records or descriptive labels.</footer>
  </section>;
}
function Fact({ label, value }: { label: string; value: string }) { return <div className="definition-fact"><label>{label}</label><b>{value}</b></div>; }
function Provider({ provider }: { provider: TableDefinitionProvider }) { return <><Fact label="Definition provider" value={provider.name}/><Fact label="Pinned revision" value={provider.revision}/><Fact label="Provider manifest SHA-256" value={provider.manifestSha256}/><Fact label="Definition license" value={provider.license}/><Fact label="Provider source" value={provider.sourceUrl}/></>; }
