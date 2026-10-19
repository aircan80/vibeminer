import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { DownloadPage, DownloadSummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { downloadPageMatches, type DownloadReadParams } from '../shared/download';
import { exactBytes, exactCount } from '../shared/inventory';
import { CatalogPageControls } from './FilesCatalog';

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
  useLayoutEffect(() => {
    const previous = focusAfterRead.current;
    if (!previous || error) return;
    focusAfterRead.current = null;
    if (!heading.current?.closest('[hidden]') && (document.activeElement === document.body || document.activeElement === previous)) heading.current?.focus({ preventScroll: true });
  }, [page, error]);
  useEffect(() => { alive.current = true; return () => { alive.current = false; request.current++; }; }, []);
  useEffect(() => { if (active) { heading.current?.focus({ preventScroll: true }); window.scrollTo(0, 0); if (!page && !loading.current) void read(undefined, 'initial'); } }, [active]);
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
  function pages(below: boolean) { return <CatalogPageControls below={below} busy={busy} canFirst={cursor !== undefined} canPrevious={history.length > 0} canNext={!!page?.nextCursor} onFirst={() => void read(undefined, 'first')} onPrevious={() => void read(history.at(-1), 'previous')} onNext={() => { if (page?.nextCursor) void read(page.nextCursor, 'next'); }}/>; }
  const count = page ? `${page.entries.length} encoded objects on this page · ${exactCount(page.totalCount)} total` : 'No download-object page has been read yet.';
  return <section className="download-objects" hidden={!active} aria-labelledby="download-objects-heading">{onReturn && <button className="return-home" onClick={onReturn}>← Return to build overview</button>}
    <div className="heading"><div><h1 ref={heading} id="download-objects-heading" tabIndex={-1}>Download objects</h1><p className="muted">Published download content · all published variants · {snapshot.build.productId} · {snapshot.build.version} · {snapshot.sourceReference.region.toUpperCase()} source</p></div></div>
    <p className="muted">Each row is one unique full encoding key (EKey) declared by the download manifest. These are encoded objects, not filenames, ROOT memberships or installed files.</p>
    <div className="catalog-context"><span>Assessment <b>{new Date(summary.assessedAt).toLocaleString()}</b></span><span>Locale / platform: <b>Not selected</b></span></div>
    {error && <div className="notice" role="alert"><strong>{error.code}</strong><br/>{error.message}<p>This view keeps the exact build, source, ledger and assessment. Reopen this build to obtain a new assessment.</p>{error.retryable && <button ref={retry} aria-disabled={busy} onClick={() => void read(attempt.current.after, attempt.current.movement)}>Retry page</button>}<button onClick={onReopen}>Reopen this build</button></div>}
    <div className="catalog-toolbar"><p role="status" aria-live="polite">{busy ? 'Reading the saved download ledger…' : count}</p>{pages(false)}</div>
    <div className="panel download-table-panel" aria-busy={busy}><table className="catalog download-table"><caption className="visually-hidden">Unique published download objects, all published variants, assessed {summary.assessedAt}</caption><thead><tr><th scope="col">Full encoding key (EKey)</th><th scope="col">Encoded size</th><th scope="col">Verified locally at assessment</th></tr></thead><tbody>{page?.entries.map(entry => <tr key={entry.id}><td><code>{entry.encodingKey}</code><small>Object {entry.id}</small></td><td>{exactBytes(entry.encodedBytes)}</td><td>{entry.verifiedAtAssessment ? 'Verified locally' : 'Not verified locally'}</td></tr>)}</tbody></table>{page?.entries.length === 0 && <p className="muted">{page.totalCount === '0' ? 'The published manifest declares zero encoded objects.' : 'No more encoded objects follow this cursor.'}</p>}</div>
    <div className="catalog-toolbar catalog-toolbar-bottom"><p>{count}</p>{pages(true)}</div><p className="tiny">Reads at most 50 unique encoded objects per page from the saved assessment. No global search, sorting, payload download or upstream availability check is performed. Not verified locally is not evidence that an object is unavailable upstream.</p>
  </section>;
}
