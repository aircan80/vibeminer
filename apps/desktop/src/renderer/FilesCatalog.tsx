import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode, type RefObject } from 'react';
import type { InventoryEntry, InventoryPage, InventorySummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { exactBytes, exactCount, inventoryPageMatches, readableBytes, type InventoryReadParams } from '../shared/inventory';
import { useCoveredSide } from './HomeShell';
type Prepared = WorkspaceSnapshot & { inventory: InventorySummary };
const families = { root: 'ROOT', install: 'Install (IN)', download: 'Download (DL)', size: 'Size (DS)' } as const;
// Files (docs/design/desktop-home): a list pane and an inspector; at enlarged text one pane at a time.
// Grouping by FileDataID and filtered queries are #20; today's inventory.read returns plain membership pages.
export function FilesCatalog({ snapshot, bridge, active, onReopen, onReturn }: { snapshot: Prepared; bridge: DiscoveryBridge; active: boolean; onReopen: () => void; onReturn?: () => void }) {
  const [page, setPage] = useState<InventoryPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ code: string; message: string; retryable: boolean } | null>(null);
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const [history, setHistory] = useState<(string | undefined)[]>([]);
  const heading = useRef<HTMLHeadingElement>(null);
  const retryButton = useRef<HTMLButtonElement>(null);
  const alive = useRef(true), request = useRef(0), loading = useRef(false);
  const lastAttempt = useRef<{ after: string | undefined; movement: 'initial' | 'next' | 'previous' | 'first' | 'retry' }>({ after: undefined, movement: 'initial' });
  const latestCursor = useRef(cursor); latestCursor.current = cursor;
  const context: InventoryReadParams = { productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region, generation: snapshot.inventory.generation, limit: 50 };
  const list = useListbox<InventoryEntry>(page?.entries ?? [], entry => entry.id), covered = useCoveredSide();
  useEffect(() => { alive.current = true; return () => { alive.current = false; request.current++; }; }, []);
  useEffect(() => { if (active) { heading.current?.focus({ preventScroll: true }); if (!page && !loading.current) void read(undefined, 'initial'); } }, [active]);
  async function read(after: string | undefined, movement: 'initial' | 'next' | 'previous' | 'first' | 'retry') {
    if (loading.current) return;
    if (movement !== 'retry') lastAttempt.current = { after, movement };
    const generation = ++request.current;
    loading.current = true; setBusy(true);
    const params = { ...context, ...(after === undefined ? {} : { after }) };
    try {
      const reply = await bridge.inventory(params);
      if (!alive.current || generation !== request.current) return;
      if (!reply.ok) { setError(reply.error); return; }
      if (!inventoryPageMatches(reply.value, params) || reply.value.totalCount !== snapshot.inventory.membershipCount) { setError({ code: 'INVENTORY_CONTEXT_MISMATCH', message: 'The returned catalog page does not match this exact prepared generation. Previous rows are preserved.', retryable: false }); return; }
      const retryFocused = document.activeElement === retryButton.current;
      setPage(reply.value); setCursor(after); setError(null);
      if (retryFocused) requestAnimationFrame(() => { if (alive.current && document.activeElement === document.body && !heading.current?.closest('[hidden]')) heading.current?.focus(); });
      if (movement === 'next') setHistory(previous => [...previous.slice(-127), latestCursor.current]);
      if (movement === 'previous') setHistory(previous => previous.slice(0, -1));
      if (movement === 'first') setHistory([]);
    } catch { if (alive.current && generation === request.current) setError({ code: 'ENGINE_UNAVAILABLE', message: 'The local catalog could not be read. Previous rows are preserved.', retryable: true }); }
    finally { if (alive.current && generation === request.current) { loading.current = false; setBusy(false); } }
  }
  const first = page?.entries.length ? BigInt(cursor ?? '0') + 1n : null;
  const count = busy ? 'Reading the prepared local catalog…' : page ? `${page.entries.length} memberships on this page · ${exactCount(page.totalCount)} total${page.entries.length ? ` · rows ${first}–${first! + BigInt(page.entries.length) - 1n}` : ''} · generation ${snapshot.inventory.generation}` : 'No catalog page has been read yet.';
  const selected = list.selected;
  return <FilesFrame active={active} pane={list.pane} onPane={list.setPane} headingRef={heading} sub={`Prepared catalog · generation ${snapshot.inventory.generation} · all published variants`} covered={covered} selectedLabel="Selected membership"
    notices={error && <div className="dh-notice dh-error" role="alert"><div className="dh-text"><strong>{error.code}</strong>{error.message} This view will not substitute another build or prepared generation. Reopen this exact build to revalidate its metadata.<div className="dh-row">{error.retryable && <button ref={retryButton} className="dh-btn dh-small" onClick={() => { if (!busy) void read(lastAttempt.current.after, lastAttempt.current.movement); }} aria-disabled={busy}>Retry page</button>}<button className="dh-btn dh-small" onClick={onReopen}>Reopen this build</button></div></div></div>}
    list={page && page.entries.length > 0 ? <div className="dh-list-scroll" aria-busy={busy}><div className="dh-mcols dh-fthead" aria-hidden="true"><span>Membership</span><span>File</span><span>Family</span><span>Encoded size</span><span>Locale flags</span><span>Local</span></div>
      <div {...list.props} aria-label={`Catalog memberships for ${snapshot.build.productId} build ${snapshot.build.version}, all published variants, generation ${snapshot.inventory.generation}`}>{page.entries.map((entry, index) => <div key={entry.id} {...list.option(index)} className={`dh-mcols${list.option(index).className}`}><span className="dh-fid">{entry.id}</span><span className="dh-wrap">{entryTitle(entry)}{entry.family === 'root' && <span className="dh-dim"> · Name not known</span>}</span><span>{families[entry.family]}</span><span>{readableBytes(entry.encodedBytes)}</span><span className="dh-mono">{entry.localeFlags ?? <span className="dh-dim">Not supplied</span>}</span><span>{entry.retained ? <span className="dh-ok">Stored</span> : <span className="dh-dim">Not stored</span>}</span></div>)}</div></div>
      : <div className="dh-pane-scroll"><div className="dh-local-wait"><div className="dh-state-box dh-plain" role="status"><strong>{busy ? 'Reading the prepared local catalog…' : page ? page.totalCount === '0' ? 'No memberships' : 'No more memberships' : 'Catalog page not read yet'}</strong><p>{page ? page.totalCount === '0' ? 'This prepared inventory has zero published memberships.' : 'No further memberships are returned after this source-row cursor.' : 'Each page reads at most 50 memberships from this sealed local generation.'}</p></div></div></div>}
    foot={<>{onReturn && <ReturnHome onReturn={onReturn}/>}<span role="status" aria-live="polite">{count}</span><span className="dh-spacer"/><CatalogPageControls busy={busy} canFirst={cursor !== undefined} canPrevious={history.length > 0} canNext={!!page?.nextCursor} onFirst={() => { void read(undefined, 'first'); }} onPrevious={() => { void read(history.at(-1), 'previous'); }} onNext={() => { if (page?.nextCursor) void read(page.nextCursor, 'next'); }}/></>}
    inspector={selected ? <MembershipInspector entry={selected.entry} onPage={selected.onPage} snapshot={snapshot}/> : <><div className="dh-insp-head"><div className="dh-label">Membership inspector</div><h2 id="files-inspector-heading">No membership selected</h2></div><div className="dh-insp-body"><p className="dh-reason">Choose a row.</p><p className="dh-tiny">ROOT maps FileDataIDs to content keys per locale and content variant. It has no file names.</p></div></>}/>;
}
const entryTitle = (entry: InventoryEntry) => entry.family === 'install' && entry.name !== null ? entry.name : entry.family === 'root' && entry.fileDataId !== null ? `FileDataID ${entry.fileDataId}` : `Encoded object · membership ${entry.id}`;
function MembershipInspector({ entry, onPage, snapshot }: { entry: InventoryEntry; onPage: boolean; snapshot: Prepared }) {
  const fields: [string, string, boolean?][] = [['Source family', families[entry.family]], ['Install name', entry.family === 'install' && entry.name !== null ? entry.name : 'Not supplied'], ['ROOT FileDataID', entry.fileDataId ?? 'Not supplied'], ['Content key (CKey)', entry.contentKey ?? 'Not declared · encoded-only membership', true], ['Encoding key (EKey)', entry.encodingKey, true], ['Logical decoded length', entry.decodedBytes === null ? 'Not declared' : exactBytes(entry.decodedBytes)], ['Exact encoded length', exactBytes(entry.encodedBytes)], ['Local encoded object', entry.retained ? 'Verified retained' : 'Not retained'], ['Raw locale flags', entry.localeFlags ?? 'Not supplied', true], ['Raw content flags', entry.contentFlags ?? 'Not supplied', true], ['Raw name hash', entry.nameHash ?? 'Not supplied', true], ['Prepared generation', snapshot.inventory.generation], ['Scope', 'All published variants']];
  return <><div className="dh-insp-head"><div className="dh-label">Membership inspector</div><h2 id="files-inspector-heading" className="dh-selectable">Membership {entry.id}</h2><p>{families[entry.family]} · {entryTitle(entry)}{onPage ? '' : ' · outside the current page'}</p></div>
    <div className="dh-insp-body"><dl className="dh-facts">{fields.map(([label, value, mono]) => <div key={label} style={{ display: 'contents' }}><dt>{label}</dt><dd className={mono ? 'dh-mono dh-selectable' : 'dh-selectable'}>{value}</dd></div>)}</dl>
      <p className="dh-tiny">An original membership is separate from a unique file, content identity or exact download object. A name hash is not a filename. Decoded length is a logical metadata fact, not proof of retained decoded bytes or working decoder support. Not retained does not establish whether an upstream payload is still available.</p></div></>;
}
export function ReturnHome({ onReturn }: { onReturn: () => void }) { return <button className="dh-btn" onClick={onReturn}>← Return to build overview</button>; }
export function FilesFrame({ active, pane, onPane, headingRef, sub, notices, list, foot, inspector, covered, selectedLabel = 'Selected object' }: { active: boolean; pane: 'list' | 'inspector'; onPane: (pane: 'list' | 'inspector') => void; headingRef: RefObject<HTMLHeadingElement | null>; sub: string; notices?: ReactNode; list: ReactNode; foot: ReactNode; inspector: ReactNode; covered: boolean; selectedLabel?: string }) {
  useEffect(() => { if (!active) onPane('list'); }, [active]);
  return <section className="dh-files" data-pane={pane} aria-label="Files" hidden={!active}>
    <div className="dh-pane-switch"><div className="dh-segmented" role="group" aria-label="Show pane"><button aria-pressed={pane === 'list'} onClick={() => onPane('list')}>Files list</button><button aria-pressed={pane === 'inspector'} onClick={() => onPane('inspector')}>{selectedLabel}</button></div></div>
    <section className="dh-pane dh-list" aria-labelledby="files-heading"><div className="dh-pane-head dh-files-filters"><h1 ref={headingRef} id="files-heading" tabIndex={-1}>Files</h1><span className="dh-sub">{sub}</span></div>{notices}{list}<div className="dh-pane-foot">{foot}</div></section>
    <aside className="dh-inspector" aria-labelledby="files-inspector-heading" inert={covered}>{inspector}</aside>
  </section>;
}
// A listbox like Discovery's build list: one Tab stop, arrow keys move the selection. A selection on another page stays in the inspector.
export function useListbox<T>(entries: readonly T[], key: (entry: T) => string) {
  const id = useId(), [chosen, setSelected] = useState<T | null>(null), [active, setActive] = useState(0), [pane, setPane] = useState<'list' | 'inspector'>('list');
  // Until the user picks a row, the first row of the first page is selected (derived, so it is never a frame late).
  const selected = chosen ?? entries[0] ?? null;
  const index = selected === null ? -1 : entries.findIndex(entry => key(entry) === key(selected));
  useEffect(() => { if (chosen === null && entries.length) setSelected(entries[0]); setActive(index >= 0 ? index : 0); }, [entries]);
  const optionId = (i: number) => `${id}-option-${i}`;
  function select(i: number) { const entry = entries[Math.max(0, Math.min(entries.length - 1, i))]; if (!entry) return; const next = entries.indexOf(entry); setSelected(entry); setActive(next); requestAnimationFrame(() => document.getElementById(optionId(next))?.scrollIntoView({ block: 'nearest' })); }
  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) { const target = ({ ArrowDown: active + 1, ArrowUp: active - 1, Home: 0, End: entries.length - 1, PageDown: active + 10, PageUp: active - 10 } as Record<string, number>)[event.key]; if (target === undefined || !entries.length) return; event.preventDefault(); select(target); }
  return { pane, setPane, selected: selected === null ? null : { entry: selected, onPage: index >= 0 },
    props: { role: 'listbox', tabIndex: 0, 'aria-activedescendant': entries.length ? optionId(active) : undefined, onKeyDown },
    option: (i: number) => ({ id: optionId(i), role: 'option', 'aria-selected': i === index, className: i === active ? ' dh-active' : '', onClick: () => select(i) }) };
}
export function CatalogPageControls({ busy, canFirst, canPrevious, canNext, onFirst, onPrevious, onNext }: { busy: boolean; canFirst: boolean; canPrevious: boolean; canNext: boolean; onFirst: () => void; onPrevious: () => void; onNext: () => void }) {
  return <span className="dh-pages" role="group" aria-label="Catalog pages"><button className="dh-btn" aria-disabled={busy || !canFirst} onClick={() => { if (!busy && canFirst) onFirst(); }}>First page</button><button className="dh-btn" aria-disabled={busy || !canPrevious} onClick={() => { if (!busy && canPrevious) onPrevious(); }}>Previous page</button><button className="dh-btn" aria-disabled={busy || !canNext} onClick={() => { if (!busy && canNext) onNext(); }}>Next page</button></span>;
}
