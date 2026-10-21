import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { DownloadObjectEntry, DownloadPage, DownloadSummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { downloadPageMatches, type DownloadReadParams } from '../shared/download';
import { exactBytes, exactCount, readableBytes } from '../shared/inventory';
import { CatalogPageControls, FilesFrame, ReturnHome, useListbox } from './FilesCatalog';
import { useCoveredSide } from './HomeShell';

// Files for a download-sized build: the research file catalog isn't prepared, so the list shows the download manifest's unique encoded objects.
export function DownloadObjects({ snapshot, summary, bridge, active, onReopen, onReturn }: { snapshot: WorkspaceSnapshot; summary: DownloadSummary; bridge: DiscoveryBridge; active: boolean; onReopen: () => void; onReturn?: () => void }) {
  const [page, setPage] = useState<DownloadPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ code: string; message: string; retryable: boolean } | null>(null);
  const [cursor, setCursor] = useState<string | undefined>();
  const [history, setHistory] = useState<(string | undefined)[]>([]);
  const heading = useRef<HTMLHeadingElement>(null), retry = useRef<HTMLButtonElement>(null);
  const focusAfterRead = useRef<HTMLElement | null>(null);
  const alive = useRef(true), request = useRef(0), loading = useRef(false);
  const latestCursor = useRef(cursor); latestCursor.current = cursor;
  type Movement = 'initial' | 'next' | 'previous' | 'first';
  const attempt = useRef<{ after: string | undefined; movement: Movement }>({ after: undefined, movement: 'initial' });
  const context: DownloadReadParams = { productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region, ledgerId: summary.ledgerId, limit: 50 };
  const list = useListbox<DownloadObjectEntry>(page?.entries ?? [], entry => entry.id);
  const covered = useCoveredSide();
  useLayoutEffect(() => {
    const previous = focusAfterRead.current;
    if (!previous || error) return;
    focusAfterRead.current = null;
    if (!heading.current?.closest('[hidden]') && (document.activeElement === document.body || document.activeElement === previous)) heading.current?.focus({ preventScroll: true });
  }, [page, error]);
  useEffect(() => { alive.current = true; return () => { alive.current = false; request.current++; }; }, []);
  useEffect(() => { if (active) { heading.current?.focus({ preventScroll: true }); if (!page && !loading.current) void read(undefined, 'initial'); } }, [active]);
  async function read(after: string | undefined, movement: Movement) {
    if (loading.current) return;
    attempt.current = { after, movement };
    const generation = ++request.current;
    loading.current = true; setBusy(true);
    const params = { ...context, ...(after === undefined ? {} : { after }) };
    try {
      const reply = await bridge.download(params);
      if (!alive.current || generation !== request.current) return;
      if (!reply.ok) { setError(reply.error); return; }
      if (!downloadPageMatches(reply.value, params, summary)) { setError({ code: 'DOWNLOAD_CONTEXT_MISMATCH', message: 'This page belongs to a different ledger or assessment. Previous rows are preserved.', retryable: false }); return; }
      if (document.activeElement === retry.current) focusAfterRead.current = retry.current;
      setPage(reply.value); setCursor(after); setError(null);
      if (movement === 'next') setHistory(previous => [...previous.slice(-127), latestCursor.current]);
      if (movement === 'previous') setHistory(previous => previous.slice(0, -1));
      if (movement === 'first') setHistory([]);
    } catch { if (alive.current && generation === request.current) setError({ code: 'ENGINE_UNAVAILABLE', message: 'The saved download ledger could not be read. Previous rows are preserved.', retryable: true }); }
    finally { if (alive.current && generation === request.current) { loading.current = false; setBusy(false); } }
  }
  const first = page?.entries.length ? BigInt(cursor ?? '0') + 1n : null;
  const count = busy ? 'Reading the saved download ledger…' : page ? `${page.entries.length} encoded objects on this page · ${exactCount(page.totalCount)} total${page.entries.length ? ` · objects ${first}–${first! + BigInt(page.entries.length) - 1n}` : ''}` : 'No download-object page has been read yet.';
  const selected = list.selected;
  return <FilesFrame active={active} pane={list.pane} onPane={list.setPane} headingRef={heading} sub="Download manifest · unique encoded objects · all published variants" covered={covered}
    notices={<>{error && <div className="dh-notice dh-error" role="alert"><div className="dh-text"><strong>{error.code}</strong>{error.message} This view keeps the exact build, source, ledger and assessment. Reopen this build to obtain a new assessment.<div className="dh-row">{error.retryable && <button ref={retry} className="dh-btn dh-small" aria-disabled={busy} onClick={() => { if (!busy) void read(attempt.current.after, attempt.current.movement); }}>Retry page</button>}<button className="dh-btn dh-small" onClick={onReopen}>Reopen this build</button></div></div></div>}
</>}
    list={page && page.entries.length > 0 ? <div className="dh-list-scroll" aria-busy={busy}><CatalogNotPrepared withObjects/><div className="dh-ocols dh-fthead" aria-hidden="true"><span>Object</span><span>Encoding key (EKey)</span><span>Encoded size</span><span>Local at assessment</span></div>
      <div {...list.props} aria-label={`Download objects, one row per encoding key, assessed ${summary.assessedAt}`}>{page.entries.map((entry, index) => <div key={entry.id} {...list.option(index)} className={`dh-ocols${list.option(index).className}`}><span className="dh-fid">Object {entry.id}</span><span className="dh-mono">{entry.encodingKey}</span><span>{readableBytes(entry.encodedBytes)}</span><span>{entry.verifiedAtAssessment ? <span className="dh-ok">Verified locally</span> : <span className="dh-dim">Not verified locally</span>}</span></div>)}</div></div>
      : <div className="dh-pane-scroll"><CatalogNotPrepared withObjects/><div className="dh-local-wait"><div className="dh-state-box dh-plain" role="status"><strong>{busy ? 'Reading the saved download ledger…' : page ? page.totalCount === '0' ? 'No encoded objects' : 'No more encoded objects' : 'Download objects not read yet'}</strong><p>{page ? page.totalCount === '0' ? 'The published manifest declares zero encoded objects.' : 'No more encoded objects follow this cursor.' : 'Reads at most 50 unique encoded objects per page from the saved assessment.'}</p></div></div></div>}
    foot={<>{onReturn && <ReturnHome onReturn={onReturn}/>}<span role="status" aria-live="polite">{count}</span><span className="dh-spacer"/><CatalogPageControls busy={busy} canFirst={cursor !== undefined} canPrevious={history.length > 0} canNext={!!page?.nextCursor} onFirst={() => void read(undefined, 'first')} onPrevious={() => void read(history.at(-1), 'previous')} onNext={() => { if (page?.nextCursor) void read(page.nextCursor, 'next'); }}/></>}
    inspector={selected ? <><div className="dh-insp-head"><div className="dh-label">Object inspector</div><h2 id="files-inspector-heading" className="dh-selectable">Encoded object {selected.entry.id}</h2><p>{selected.onPage ? 'Download manifest · encoded object' : 'Outside the current page · same saved assessment'}</p></div>
      <div className="dh-insp-body"><dl className="dh-facts"><dt>Encoding key</dt><dd className="dh-mono dh-selectable">{selected.entry.encodingKey}</dd><dt>Encoded length</dt><dd>{exactBytes(selected.entry.encodedBytes)}</dd><dt>Local object</dt><dd>{selected.entry.verifiedAtAssessment ? <><span className="dh-ok">Verified locally</span> at assessment</> : 'Not verified locally at assessment'}</dd><dt>Assessed</dt><dd>{new Date(summary.assessedAt).toLocaleString()}</dd><dt>Download ledger</dt><dd className="dh-mono dh-selectable">{summary.ledgerId}</dd><dt>Scope</dt><dd>All published variants</dd><dt>Locale / platform</dt><dd>Not selected</dd></dl>
        <p className="dh-tiny">Encoded objects are not filenames, ROOT memberships or installed files. Not verified locally is not evidence that an object is unavailable upstream. No global search, sorting, payload download or upstream availability check is performed.</p></div></>
      : <><div className="dh-insp-head"><div className="dh-label">Object inspector</div><h2 id="files-inspector-heading">No object selected</h2></div><div className="dh-insp-body"><p className="dh-reason" style={{ fontSize: 'var(--fs-m)' }}>Choose a row. Each page reads at most 50 unique encoded objects from the saved assessment.</p></div></>}/>;
}

// Files with no readable list: no download ledger and no prepared catalog. A calm capability limit, with no producer to offer.
export function FilesUnavailable({ snapshot, active, onReturn }: { snapshot: WorkspaceSnapshot; active: boolean; onReturn?: () => void }) {
  const heading = useRef<HTMLHeadingElement>(null), covered = useCoveredSide();
  const [pane, setPane] = useState<'list' | 'inspector'>('list');
  useEffect(() => { if (active) heading.current?.focus({ preventScroll: true }); }, [active]);
  return <FilesFrame active={active} pane={pane} onPane={setPane} headingRef={heading} sub="All published variants" covered={covered} selectedLabel="Selected file"
    list={<div className="dh-pane-scroll"><CatalogNotPrepared problem={snapshot.downloadProblem?.code}/></div>}
    foot={<>{onReturn && <ReturnHome onReturn={onReturn}/>}<span>Catalog not prepared</span></>}
    inspector={<><div className="dh-insp-head"><div className="dh-label">File inspector</div><h2 id="files-inspector-heading">No file selected</h2></div><div className="dh-insp-body"><p className="dh-reason" style={{ fontSize: 'var(--fs-m)' }}>The catalog has no memberships to inspect yet.</p><p className="dh-tiny">ROOT maps FileDataIDs to content keys per locale and content variant. It has no file names.</p></div></>}/>;
}

// No file catalog yet: production opening prepares no ROOT/install catalog (#35). Say what works today and what is missing,
// so the download-object list (or no list) never reads as "this build has no files". It sits at the top of the list's own
// scroll area, and at 200% text a one-line form keeps rows in view. There is no Prepare button: nothing can prepare it yet.
export function CatalogNotPrepared({ withObjects = false, problem }: { withObjects?: boolean; problem?: string }) {
  const missing = "Search by FileDataID or CKey, the filters and one row per FileDataID (ROOT) or install name need this build's ROOT and install catalog, which VibeMiner doesn't prepare yet.";
  return <div className="dh-local-wait dh-catalog-missing"><div className="dh-state-box dh-plain" role="note" aria-labelledby="files-catalog-missing"><strong id="files-catalog-missing">File catalog not prepared</strong>
    {withObjects ? <><p className="dh-note-long">You can page through the download manifest's encoded objects below and see which are stored locally. They have no FileDataIDs or names. {missing}</p>
      <p className="dh-note-short">Encoded objects and local status only. No FileDataID or CKey search yet.</p></>
      : <p>There is no list to show: this build has no supported download manifest.{problem && <> <code>({problem})</code></>} {missing} Tables, Items and Creatures prepare their own data.</p>}</div></div>;
}
