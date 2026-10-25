import { useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode, type RefObject } from 'react';
import type { InventoryFile, InventoryFileDetail, InventoryFileMembership, InventoryFilesPage, InventorySummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { exactBytes, exactCount, filePosition, hex, inventoryFileDetailMatches, inventoryFilesPageMatches, localeBits, localeNames, readableBytes, type FileFilters, type InventoryFilesParams } from '../shared/inventory';
import { useCoveredSide, useShell } from './HomeShell';
import type { FileTarget } from './table-follow-view';
/** One exact sealed generation Files reads: the legacy full catalog of the snapshot, or the build's file catalog prepared after Open (D071). */
export type FilesSource = { productId: string; buildIdentity: string; sourceRegion: string; generation: string; preparedAt: string };
export const inventorySource = (snapshot: WorkspaceSnapshot & { inventory: InventorySummary }): FilesSource =>
  ({ productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region, generation: snapshot.inventory.generation, preparedAt: snapshot.inventory.preparedAt });
type Failure = { code: string; message: string; retryable: boolean };
type Movement = 'initial' | 'next' | 'previous' | 'first' | 'retry';
type Search = { fileDataId?: string; contentKey?: string } | null;
// A page is shown with the exact filters that produced it, so rows from earlier filters are never presented as current.
type Shown = { page: InventoryFilesPage; filters: FileFilters; key: string };
const ENCRYPTED = '134217728';
// Search box: 1 to 10 digits is a FileDataID, 4 to 32 hex digits a content key or its start (docs/design/desktop-home, Files).
export function parseFileSearch(text: string): Search | 'invalid' {
  const value = text.trim();
  if (!value) return null;
  if (/^[0-9]{1,10}$/.test(value)) return BigInt(value) <= 4294967295n ? { fileDataId: String(BigInt(value)) } : 'invalid';
  if (/^[0-9a-fA-F]{4,32}$/.test(value)) return { contentKey: value.toLowerCase() };
  return 'invalid';
}
// Files (docs/design/desktop-home, #20): one row per ROOT FileDataID or install name, filters, and an inspector with every membership.
export function FilesCatalog({ source, bridge, active, onReopen, onReturn, fileTarget, onReturnToTable, viewSwitch }: { viewSwitch?: ReactNode; source: FilesSource; bridge: DiscoveryBridge; active: boolean; onReopen: () => void; onReturn?: () => void; fileTarget?: FileTarget | null; onReturnToTable?: () => void }) {
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState<Search>(null);
  const [invalid, setInvalid] = useState(false);
  const [locale, setLocale] = useState('0'), [flags, setFlags] = useState('any'), [family, setFamily] = useState<'all' | 'root' | 'install'>('all'), [stored, setStored] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [shown, setShown] = useState<Shown | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Failure | null>(null);
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const [history, setHistory] = useState<(string | undefined)[]>([]);
  const heading = useRef<HTMLHeadingElement>(null), searchInput = useRef<HTMLInputElement>(null), filtersButton = useRef<HTMLButtonElement>(null), retryButton = useRef<HTMLButtonElement>(null);
  const alive = useRef(true), request = useRef(0), focusAfterRead = useRef<HTMLElement | null>(null);
  const lastAttempt = useRef<{ after: string | undefined; movement: Movement }>({ after: undefined, movement: 'initial' });
  const latestCursor = useRef(cursor); latestCursor.current = cursor;
  const { compact } = useShell(), covered = useCoveredSide();
  const filters: FileFilters = { ...(search ?? {}), ...(locale !== '0' ? { localeFlags: locale } : {}), ...(flags !== 'any' ? { contentFlags: flags } : {}), ...(family !== 'all' ? { family } : {}), ...(stored ? { storedOnly: true } : {}) };
  const filterKey = JSON.stringify(filters);
  const activeCount = (locale !== '0' ? 1 : 0) + (flags !== 'any' ? 1 : 0) + (family !== 'all' ? 1 : 0) + (stored ? 1 : 0);
  const context = { productId: source.productId, buildIdentity: source.buildIdentity, sourceRegion: source.sourceRegion, generation: source.generation, limit: 50 };
  const page = shown?.page ?? null, stale = !!shown && shown.key !== filterKey;
  const list = useListbox<InventoryFile>(page?.entries ?? [], file => file.key);
  // A successful Retry removes the focused Retry button; focus goes to the Files heading instead of the document body.
  useLayoutEffect(() => {
    const previous = focusAfterRead.current;
    if (!previous || error) return;
    focusAfterRead.current = null;
    if (!heading.current?.closest('[hidden]') && (document.activeElement === document.body || document.activeElement === previous)) heading.current?.focus({ preventScroll: true });
  }, [shown, error]);
  useEffect(() => { alive.current = true; return () => { alive.current = false; request.current++; }; }, []);
  // A FileDataID followed from Tables (#19) becomes the search, no other filter narrows it, and the selection starts over.
  // It applies before the first read, so an unfiltered page never shows for it; the filter change below reads it.
  const target = useRef<number | null>(null), awaitingTarget = !!fileTarget && target.current !== fileTarget.seq && (search?.fileDataId !== fileTarget.fileDataId || activeCount > 0);
  useEffect(() => { if (!fileTarget || target.current === fileTarget.seq) return; target.current = fileTarget.seq; list.reset(); setDraft(fileTarget.fileDataId); setInvalid(false); setSearch({ fileDataId: fileTarget.fileDataId }); setLocale('0'); setFlags('any'); setFamily('all'); setStored(false); }, [fileTarget?.seq]);
  useEffect(() => { if (active) { heading.current?.focus({ preventScroll: true }); if (!page && !busy && !awaitingTarget) void read(undefined, 'initial'); } }, [active]);
  // A filter change starts again from the first page; an earlier reply is dropped by the request generation.
  // A row the user picked stays selected; the inspector says when it falls outside the new results.
  const readFilters = useRef(filterKey);
  useEffect(() => { if (readFilters.current === filterKey) return; readFilters.current = filterKey; list.refresh(); setHistory([]); void read(undefined, 'initial'); }, [filterKey]);
  useEffect(() => {
    if (!active) return;
    const key = (event: globalThis.KeyboardEvent) => { if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'f') { event.preventDefault(); list.setPane('list'); requestAnimationFrame(() => { searchInput.current?.focus(); searchInput.current?.select(); }); } };
    document.addEventListener('keydown', key); return () => document.removeEventListener('keydown', key);
  }, [active]);
  async function read(after: string | undefined, movement: Movement) {
    if (movement !== 'retry') lastAttempt.current = { after, movement };
    const generation = ++request.current;
    setBusy(true);
    const params: InventoryFilesParams = { ...context, ...filters, ...(after === undefined ? {} : { after }) };
    try {
      const reply = await bridge.inventoryFiles(params);
      if (!alive.current || generation !== request.current) return;
      if (!reply.ok) { setError(reply.error); return; }
      if (!inventoryFilesPageMatches(reply.value, params)) { setError({ code: 'INVENTORY_CONTEXT_MISMATCH', message: 'The returned Files page does not match this exact prepared generation and filters. Previous rows are preserved.', retryable: false }); return; }
      if (document.activeElement === retryButton.current) focusAfterRead.current = retryButton.current;
      setShown({ page: reply.value, filters: { ...filters }, key: JSON.stringify(filters) }); setCursor(after); setError(null);
      if (movement === 'next') setHistory(previous => [...previous.slice(-127), latestCursor.current]);
      if (movement === 'previous') setHistory(previous => previous.slice(0, -1));
      if (movement === 'first' || movement === 'initial') setHistory([]);
    } catch { if (alive.current && generation === request.current) setError({ code: 'ENGINE_UNAVAILABLE', message: 'The local catalog could not be read. Previous rows are preserved.', retryable: true }); }
    finally { if (alive.current && generation === request.current) setBusy(false); }
  }
  function applySearch() {
    const parsed = parseFileSearch(draft);
    if (parsed === 'invalid') { setInvalid(true); return; }
    setInvalid(false);
    if (JSON.stringify(parsed) !== JSON.stringify(search)) setSearch(parsed);
  }
  function clearFilters() { setDraft(''); setSearch(null); setInvalid(false); setLocale('0'); setFlags('any'); setFamily('all'); setStored(false); requestAnimationFrame(() => searchInput.current?.focus()); }
  function filtersKey(event: KeyboardEvent<HTMLDivElement>) { if (event.key === 'Escape' && compact && filtersOpen) { event.preventDefault(); event.stopPropagation(); setFiltersOpen(false); filtersButton.current?.focus(); } }
  const narrowed = !!search || activeCount > 0;
  // Everything said about the rows on screen uses the filters that produced them.
  const shownFilters = shown?.filters ?? filters, shownActive = (shownFilters.localeFlags ? 1 : 0) + (shownFilters.contentFlags ? 1 : 0) + (shownFilters.family ? 1 : 0) + (shownFilters.storedOnly ? 1 : 0);
  const shownNarrowed = shownFilters.fileDataId !== undefined || shownFilters.contentKey !== undefined || shownActive > 0;
  const described = shownFilters.fileDataId !== undefined ? `has FileDataID ${shownFilters.fileDataId}` : shownFilters.contentKey !== undefined ? `has a content key starting ${shownFilters.contentKey}` : null;
  const shownFamily = shownFilters.family ?? 'all';
  const position = (key: string) => { const [section, value] = filePosition(key); return section === 0 ? `FileDataID ${value.toLocaleString()}` : value === 0n ? 'every ROOT file' : 'part of the install files'; };
  const count = busy ? 'Reading the prepared local catalog…' : !page ? 'No Files page has been read yet.' : stale ? `${page.entries.length} files from the previous filters · not the current results`
    : page.entries.length
    ? `${page.entries.length} ${shownNarrowed ? 'matching ' : ''}files on this page${page.totalCount !== null ? ` · ${exactCount(page.totalCount)} files in this ${shownFamily === 'all' ? 'catalog' : shownFamily === 'root' ? 'ROOT' : 'install manifest'}` : ''}${page.scanLimited && page.nextCursor ? ` · searched through ${position(page.nextCursor)}, more may match` : ''}`
    : page.scanLimited && page.nextCursor ? `No match yet · searched through ${position(page.nextCursor)}` : '0 files';
  const head = <>
    <h1 ref={heading} id="files-heading" tabIndex={-1}>Files</h1>{viewSwitch && <>{viewSwitch}<span className="dh-head-break" aria-hidden="true"/></>}
    <input ref={searchInput} className="dh-control dh-fsearch" type="search" value={draft} placeholder="FileDataID or CKey" aria-label="Search by FileDataID or content key" aria-keyshortcuts="Control+F" aria-invalid={invalid || undefined} aria-describedby={invalid ? 'files-search-help' : undefined}
      onChange={event => { setDraft(event.target.value); if (invalid) setInvalid(false); }} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); applySearch(); } }} onBlur={applySearch}/>
    <button ref={filtersButton} className="dh-btn dh-fbtn" aria-expanded={filtersOpen} aria-controls="file-filters" onClick={() => setFiltersOpen(open => !open)}>Filters{activeCount > 0 && <><span className="dh-visually-hidden">, {activeCount} active</span><span className="dh-fcount" aria-hidden="true">{activeCount}</span></>}<span className="dh-chev" aria-hidden="true">{filtersOpen ? '▴' : '▾'}</span></button>
    <div className={`dh-fextra${filtersOpen ? ' dh-open' : ''}`} id="file-filters" role="group" aria-label="File filters" onKeyDown={filtersKey}>
      <select className="dh-control" aria-label="Locale flag" value={locale} onChange={event => setLocale(event.target.value)}><option value="0">Any locale</option>{localeBits.map(([bit, name]) => <option key={bit} value={String(bit)}>{name} · {hex(bit)}</option>)}</select>
      <select className="dh-control" aria-label="Content flags" value={flags} onChange={event => setFlags(event.target.value)}><option value="any">Any content flags</option><option value="none">No content flags</option><option value={ENCRYPTED}>Includes {hex(ENCRYPTED)}</option></select>
      <div className="dh-segmented" role="group" aria-label="Source family">{([['all', 'All'], ['root', 'ROOT'], ['install', 'Install']] as const).map(([value, label]) => <button key={value} aria-pressed={family === value} onClick={() => setFamily(value)}>{label}</button>)}</div>
      <label className="dh-check"><input type="checkbox" checked={stored} onChange={event => setStored(event.target.checked)}/> Stored locally</label>
    </div>
  </>;
  const searchHelp = invalid && <div className="dh-notice dh-calm" id="files-search-help" role="status"><div className="dh-text"><strong>Search not understood</strong>Type a FileDataID (up to 10 digits) or at least 4 hex digits of a content key.</div></div>;
  const previous = stale && !busy && <div className="dh-notice dh-calm" role="status"><div className="dh-text"><strong>These rows are from the previous filters</strong>The list for the current filters wasn't read. Fix the search or Retry, or Clear filters to go back.<div className="dh-row"><button className="dh-btn dh-small" onClick={clearFilters}>Clear filters</button></div></div></div>;
  const notices = <>{searchHelp}{previous}{error && <div className={`dh-notice${error.code === 'INVENTORY_FILTER_BROAD' ? ' dh-calm' : ' dh-error'}`} role="alert"><div className="dh-text"><strong>{error.code === 'INVENTORY_FILTER_BROAD' ? 'Content key too short for this catalog' : error.code}</strong>{error.message}{error.code === 'INVENTORY_FILTER_BROAD' ? '' : ' This view will not substitute another build or prepared generation.'}<div className="dh-row">{error.retryable && error.code !== 'INVENTORY_FILTER_BROAD' && <button ref={retryButton} className="dh-btn dh-small" onClick={() => { void read(lastAttempt.current.after, lastAttempt.current.movement); }}>Retry page</button>}{error.code !== 'INVENTORY_FILTER_BROAD' && <button className="dh-btn dh-small" onClick={onReopen}>Reopen this build</button>}</div></div></div>}</>;
  const rows = page && page.entries.length > 0 ? <div className="dh-list-scroll" aria-busy={busy}><div className="dh-fcols dh-fthead" aria-hidden="true"><span>FileDataID</span><span>Name</span><span>Memberships</span><span>Encoded size</span><span>Locales</span><span>Local</span></div>
    <div {...list.props} aria-label="Files, one row per FileDataID">{page.entries.map((file, index) => { const option = list.option(index); return <div key={file.key} {...option} className={`dh-fcols${option.className}`}>
      <span className="dh-fid">{file.fileDataId ?? <span className="dh-dim">Install</span>}</span>
      <span className="dh-fname-cell">{file.name !== null ? <span className="dh-mono">{file.name}</span> : <span className="dh-dim">Name not known</span>}</span>
      <span>{file.membershipCount}{file.contentKeyCount > 1 && <span className="dh-dim"> · {file.contentKeyCount} CKeys</span>}</span>
      <span><SizeSummary file={file}/></span>
      <span>{file.localeFlags === null ? <span className="dh-dim">No ROOT flags</span> : localeNames(file.localeFlags).join(', ') || <span className="dh-dim">No known bit</span>}</span>
      <span><LocalSummary file={file}/></span></div>; })}</div></div>
    : <div className="dh-pane-scroll"><div className="dh-local-wait">{busy && !page ? <div className="dh-state-box dh-plain" role="status"><strong>Reading the prepared local catalog…</strong><p>Each page reads at most 50 files from this sealed local generation.</p></div>
      : page && page.scanLimited && page.nextCursor ? <div className="dh-state-box dh-plain" role="status"><strong>No match yet</strong><p>Searched through {position(page.nextCursor)}. Each request reads a bounded part of the catalog; Next page continues the search.</p></div>
      : page ? <div className="dh-state-box dh-plain" role="status"><strong>{stale ? 'No files matched the previous filters' : shownNarrowed ? 'No files match' : 'No files'}</strong><p>{shownNarrowed ? <>No file in this build's prepared catalog {described ?? 'matches these filters'}{described && shownActive ? ` with these filters (${shownActive} active)` : ''}.</> : 'This prepared catalog has no ROOT or install files.'}</p>{shownNarrowed && !stale && <div className="dh-row"><button className="dh-btn dh-small" onClick={clearFilters}>Clear filters</button></div>}</div>
      : <div className="dh-state-box dh-plain" role="status"><strong>Files not read yet</strong><p>Each page reads at most 50 files from this sealed local generation.</p></div>}</div></div>;
  const selected = list.selected;
  return <FilesFrame fileTarget={fileTarget} onReturnToTable={onReturnToTable} active={active} pane={list.pane} onPane={list.setPane} headingRef={heading} head={head} covered={covered} selectedLabel="Selected file" notices={notices} list={rows}
    foot={<>{onReturn && <ReturnHome onReturn={onReturn}/>}<span role="status" aria-live="polite">{count}</span><span className="dh-spacer"/><CatalogPageControls busy={busy} canFirst={cursor !== undefined && !stale} canPrevious={history.length > 0 && !stale} canNext={!!page?.nextCursor && !stale} onFirst={() => { void read(undefined, 'first'); }} onPrevious={() => { void read(history.at(-1), 'previous'); }} onNext={() => { if (page?.nextCursor) void read(page.nextCursor, 'next'); }}/></>}
    inspector={selected ? <FileInspector key={`${selected.entry.key}|${shown?.key ?? filterKey}`} file={selected.entry} onPage={selected.onPage} filters={shownFilters} narrowed={shownNarrowed} source={source} bridge={bridge}/>
      : <><div className="dh-insp-head"><div className="dh-label">File inspector</div><h2 id="files-inspector-heading">No file selected</h2></div><div className="dh-insp-body"><p className="dh-reason" style={{ fontSize: 'var(--fs-m)' }}>{page && !page.entries.length ? 'Nothing matches these filters.' : 'Choose a row.'}</p><p className="dh-tiny">ROOT maps FileDataIDs to content keys per locale and content variant. It has no file names.</p></div></>}/>;
}
function SizeSummary({ file }: { file: InventoryFile }) {
  if (file.encodedBytesMin === null) return <span className="dh-warn-t">Unresolved</span>;
  const size = file.encodedBytesMin === file.encodedBytesMax ? readableBytes(file.encodedBytesMin) : `${readableBytes(file.encodedBytesMin)}–${readableBytes(file.encodedBytesMax!)}`;
  return <>{size}{file.unresolvedCount > 0 && <span className="dh-warn-t"> +{file.unresolvedCount} unresolved</span>}</>;
}
function LocalSummary({ file }: { file: InventoryFile }) {
  if (file.storedCount === file.membershipCount) return <span className="dh-ok">Stored</span>;
  if (file.storedCount > 0) return <span className="dh-warn-t">{file.storedCount} of {file.membershipCount} stored</span>;
  return <span className="dh-dim">Not stored</span>;
}
// Every membership of the selected file, read in pages of 100; with filters on, the ones that passed them are marked.
function FileInspector({ file, onPage, filters, narrowed, source, bridge }: { file: InventoryFile; onPage: boolean; filters: FileFilters; narrowed: boolean; source: FilesSource; bridge: DiscoveryBridge }) {
  const [detail, setDetail] = useState<InventoryFileDetail | null>(null);
  const [members, setMembers] = useState<InventoryFileMembership[]>([]);
  const [error, setError] = useState<Failure | null>(null);
  const [busy, setBusy] = useState(false);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; void load(undefined); return () => { alive.current = false; }; }, []);
  async function load(after: string | undefined) {
    setBusy(true);
    const params = { productId: source.productId, buildIdentity: source.buildIdentity, sourceRegion: source.sourceRegion, generation: source.generation, limit: 100, file: file.key, ...filters, ...(after === undefined ? {} : { after }) };
    try {
      const reply = await bridge.inventoryFile(params);
      if (!alive.current) return;
      if (!reply.ok) { setError(reply.error); return; }
      if (!inventoryFileDetailMatches(reply.value, params)) { setError({ code: 'INVENTORY_CONTEXT_MISMATCH', message: 'The returned memberships do not match this exact file and prepared generation.', retryable: false }); return; }
      setDetail(reply.value); setMembers(previous => after === undefined ? reply.value.memberships : [...previous, ...reply.value.memberships]); setError(null);
    } catch { if (alive.current) setError({ code: 'ENGINE_UNAVAILABLE', message: 'The local catalog could not be read.', retryable: true }); }
    finally { if (alive.current) setBusy(false); }
  }
  const root = file.family === 'root', title = root ? `FileDataID ${file.fileDataId}` : file.name!;
  const familyName = root ? 'ROOT' : 'Install';
  const verified = new Date(source.preparedAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  const total = detail?.membershipCount ?? file.membershipCount, matching = detail?.matchingCount ?? file.matchingCount;
  // A kept selection the filters now exclude stays in the inspector, labelled, instead of another row being picked.
  const outside = !onPage && narrowed && detail !== null && detail.matchingCount === 0;
  return <><div className="dh-insp-head"><div className="dh-label">File inspector</div><h2 id="files-inspector-heading" className={`dh-selectable${root ? '' : ' dh-mono'}`}>{title}</h2><p>{familyName} · {total} membership{total === 1 ? '' : 's'}{onPage ? '' : outside ? ' · outside these results' : ' · outside the current page'}</p></div>
    <div className="dh-insp-body" aria-busy={busy}>{outside && <div className="dh-state-box dh-plain" role="status" style={{ marginBottom: '.56rem' }}><strong>Selected file is outside these results</strong><p>None of its memberships match the current filters. Choose a row to inspect a file that does.</p></div>}<dl className="dh-facts">
      {root && <><dt>FileDataID</dt><dd className="dh-selectable">{file.fileDataId}</dd></>}
      <dt>Name</dt><dd>{root ? 'Name not known. ROOT has no names; a name hash is not a filename.' : <><span className="dh-mono dh-selectable">{file.name}</span><br/><span className="dh-dim">The install manifest's own name.</span></>}</dd>
      <dt>Memberships</dt><dd>{total} in {familyName}{file.contentKeyCount > 1 ? ` · ${file.contentKeyCount} different content keys` : ''}{narrowed ? ` · ${matching} ${matching === 1 ? 'matches' : 'match'} the filters` : ''}</dd>
    </dl>
      {error && <div className="dh-notice dh-error" role="alert" style={{ margin: '.56rem 0 0' }}><div className="dh-text"><strong>{error.code}</strong>{error.message}{error.retryable && <div className="dh-row"><button className="dh-btn dh-small" onClick={() => { void load(members.length ? members.at(-1)!.id : undefined); }}>Retry memberships</button></div>}</div></div>}
      {!detail && !error && <p className="dh-reason" role="status">Reading memberships…</p>}
      {members.map(member => <Membership key={member.id} member={member} narrowed={narrowed} verified={verified}/>)}
      {detail?.nextCursor && <div className="dh-row" style={{ marginTop: '.56rem' }}><button className="dh-btn dh-small" aria-disabled={busy} onClick={() => { if (!busy && detail.nextCursor) void load(detail.nextCursor); }}>Show more memberships</button><span className="dh-reason">{members.length} of {total} shown</span></div>}
      <p className="dh-tiny">A membership is not a unique file. Content flags are raw facts; no platform is inferred from them. Decoded length is a metadata fact, not proof that a decoder supports the file.</p></div></>;
}
function Membership({ member, narrowed, verified }: { member: InventoryFileMembership; narrowed: boolean; verified: string }) {
  const raw = member.rawContentFlags, mark = narrowed && member.matches;
  return <div className={`dh-member${mark ? ' dh-current' : ''}`}><div className="dh-label" style={{ margin: '0 0 .326667rem' }}>Membership {member.id}{narrowed && <span className={mark ? 'dh-ok' : 'dh-dim'} style={{ textTransform: 'none', letterSpacing: 0, marginLeft: '.373333rem' }}>{mark ? 'Matches the filters' : 'Does not match'}</span>}</div><dl className="dh-facts">
    <dt>Family</dt><dd>{member.family === 'root' ? `ROOT · block ${member.blockOrdinal}, entry ${member.entryOrdinal}` : `Install · entry ${member.installOrdinal}`}</dd>
    <dt>Locale flags</dt><dd>{member.localeFlags === null ? <span className="dh-dim">Not in the install manifest</span> : <><span className="dh-mono">{hex(member.localeFlags)}</span> · {localeNames(member.localeFlags).join(', ') || 'no known locale bit'}</>}</dd>
    <dt>Content flags</dt><dd>{member.contentFlags === null ? <span className="dh-dim">Not in the install manifest</span> : <><span className="dh-mono">{hex(member.contentFlags)}</span>{raw && <span className="dh-dim"> raw parts {hex(raw.first)} · {hex(raw.second)} · {raw.third}</span>}</>}</dd>
    <dt>Name hash</dt><dd className="dh-mono dh-selectable">{member.nameHash === null ? <span className="dh-dim">None</span> : BigInt(member.nameHash).toString(16).padStart(16, '0')}</dd>
    <dt>Content key</dt><dd className="dh-mono dh-selectable">{member.contentKey}</dd>
    <dt>Encoding key</dt><dd className="dh-mono dh-selectable">{member.encodingKey ?? <span className="dh-warn-t">Unresolved · CONTENT_UNRESOLVED</span>}</dd>
    <dt>Decoded length</dt><dd>{member.decodedBytes === null ? 'Unknown' : exactBytes(member.decodedBytes)}</dd>
    <dt>Encoded length</dt><dd>{member.encodedBytes === null ? 'Unknown' : exactBytes(member.encodedBytes)}</dd>
    <dt>Local object</dt><dd>{member.stored ? <><span className="dh-ok">Stored locally</span> · verified {verified}</> : 'Not stored. Fetched on demand when something needs it.'}</dd>
  </dl></div>;
}
export function ReturnHome({ onReturn }: { onReturn: () => void }) { return <button className="dh-btn" onClick={onReturn}>← Return to build overview</button>; }
// Without a prepared catalog there is no FileDataID search, so a FileDataID followed from Tables (#19) can't be looked up.
// The notice says so instead of presenting the list as that file; the foot keeps the way back to the record. Once the
// build's file list becomes ready (D071) it says the FileDataID can be looked up with Show file list, which opens the list
// searching it. At 200% it is one line.
export function FileTargetNotice({ target, ready = false }: { target: FileTarget; ready?: boolean }) {
  const title = ready ? `FileDataID ${target.fileDataId} can be looked up now` : `FileDataID ${target.fileDataId} can’t be looked up yet`;
  const detail = ready ? `Followed from ${target.table} record ${target.recordId}. The file list is ready: Show file list, below, opens it searching that FileDataID.`
    : `Followed from ${target.table} record ${target.recordId}. The file catalog isn’t prepared, so Files has no FileDataID search and nothing is claimed about that file.`;
  return <div className="dh-notice dh-calm file-target" role="status"><div className="dh-text"><strong>{title}</strong><span className="dh-detail dh-note-long">{detail}</span>
    <span className="dh-note-inline"> · {ready ? 'use Show file list below' : 'no file list yet'}</span></div></div>;
}
export function FilesFrame({ active, pane, onPane, headingRef, sub, head, notices, list, foot, inspector, covered, selectedLabel = 'Selected object', fileTarget, onReturnToTable }: { active: boolean; pane: 'list' | 'inspector'; onPane: (pane: 'list' | 'inspector') => void; headingRef: RefObject<HTMLHeadingElement | null>; sub?: string; head?: ReactNode; notices?: ReactNode; list: ReactNode; foot: ReactNode; inspector: ReactNode; covered: boolean; selectedLabel?: string; fileTarget?: FileTarget | null; onReturnToTable?: () => void }) {
  useEffect(() => { if (!active) onPane('list'); }, [active]);
  return <section className="dh-files" data-pane={pane} aria-label="Files" hidden={!active}>
    <div className="dh-pane-switch"><div className="dh-segmented" role="group" aria-label="Show pane"><button aria-pressed={pane === 'list'} onClick={() => onPane('list')}>Files list</button><button aria-pressed={pane === 'inspector'} onClick={() => onPane('inspector')}>{selectedLabel}</button></div></div>
    <section className="dh-pane dh-list" aria-labelledby="files-heading"><div className="dh-pane-head dh-files-filters">{head ?? <><h1 ref={headingRef} id="files-heading" tabIndex={-1}>Files</h1><span className="dh-sub">{sub}</span></>}</div>{notices}{list}<div className="dh-pane-foot">{fileTarget && onReturnToTable && <button className="dh-btn return-table" onClick={onReturnToTable}>← Return to {fileTarget.table} record {fileTarget.recordId}</button>}{foot}</div></section>
    <aside className="dh-inspector" aria-labelledby="files-inspector-heading" inert={covered}>{inspector}</aside>
  </section>;
}
// A listbox like Discovery's build list: one Tab stop, arrow keys move the selection. A selection on another page stays in the inspector.
export function useListbox<T>(entries: readonly T[], key: (entry: T) => string) {
  const id = useId(), [chosen, setSelected] = useState<T | null>(null), [pane, setPane] = useState<'list' | 'inspector'>('list');
  // Until the user picks a row, the first row of the first page is selected. The selection and the active row are derived
  // in render, so a key press that lands before a new page's effects run still moves from that page's rows.
  const selected = chosen ?? entries[0] ?? null;
  const index = selected === null ? -1 : entries.findIndex(entry => key(entry) === key(selected));
  const active = index >= 0 ? index : 0;
  // The page effect runs after paint. A key press can land first, so it reads the latest choice from a ref, not this render's.
  const latest = useRef<T | null>(null), picked = useRef(false);
  useEffect(() => { if (latest.current === null && entries.length) { latest.current = entries[0]; setSelected(entries[0]); } }, [entries]);
  const optionId = (i: number) => `${id}-option-${i}`;
  function select(i: number) { const entry = entries[Math.max(0, Math.min(entries.length - 1, i))]; if (!entry) return; const next = entries.indexOf(entry); latest.current = entry; picked.current = true; setSelected(entry); requestAnimationFrame(() => document.getElementById(optionId(next))?.scrollIntoView({ block: 'nearest' })); }
  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) { const target = ({ ArrowDown: active + 1, ArrowUp: active - 1, Home: 0, End: entries.length - 1, PageDown: active + 10, PageUp: active - 10 } as Record<string, number>)[event.key]; if (target === undefined || !entries.length) return; event.preventDefault(); select(target); }
  // The same key on a newer page is the newer row (fresh ordinal and flags); a pick off the page stays as it was read.
  return { pane, setPane, selected: selected === null ? null : { entry: index >= 0 ? entries[index] : selected, onPage: index >= 0 }, // For a new result set: keep the row the user picked, otherwise follow the new first row.
    refresh: () => { if (picked.current) return; latest.current = null; setSelected(null); },
    /** A newer read of the picked row, e.g. from another ledger: it replaces the pick only while that row is still picked. */
    replace: (entry: T) => { if (picked.current && latest.current !== null && key(latest.current) === key(entry)) { latest.current = entry; setSelected(entry); } },
    /** Whether the user picked the selected row, rather than it following the first row of the page. */
    picked: () => picked.current,
    /** A new destination, such as a FileDataID followed from Tables: forget the earlier pick too. */
    reset: () => { picked.current = false; latest.current = null; setSelected(null); },
    props: { role: 'listbox', tabIndex: 0, 'aria-activedescendant': entries.length ? optionId(active) : undefined, onKeyDown },
    option: (i: number) => ({ id: optionId(i), role: 'option', 'aria-selected': i === index, className: i === active ? ' dh-active' : '', onClick: () => select(i) }) };
}
export function CatalogPageControls({ busy, canFirst, canPrevious, canNext, onFirst, onPrevious, onNext }: { busy: boolean; canFirst: boolean; canPrevious: boolean; canNext: boolean; onFirst: () => void; onPrevious: () => void; onNext: () => void }) {
  return <span className="dh-pages" role="group" aria-label="Catalog pages"><button className="dh-btn" aria-disabled={busy || !canFirst} onClick={() => { if (!busy && canFirst) onFirst(); }}>First page</button><button className="dh-btn" aria-disabled={busy || !canPrevious} onClick={() => { if (!busy && canPrevious) onPrevious(); }}>Previous page</button><button className="dh-btn" aria-disabled={busy || !canNext} onClick={() => { if (!busy && canNext) onNext(); }}>Next page</button></span>;
}
