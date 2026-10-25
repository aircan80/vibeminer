import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import type { DownloadObjectEntry, DownloadPage, DownloadSummary, FileCatalogStatus, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { downloadPageMatches, type DownloadReadParams } from '../shared/download';
import { exactBytes, exactCount, readableBytes } from '../shared/inventory';
import { CatalogPageControls, FileTargetNotice, FilesFrame, ReturnHome, useListbox } from './FilesCatalog';
import type { FileTarget } from './table-follow-view';
import { useCoveredSide } from './HomeShell';
import { ActivityLink } from './DownloadActivity';

// Files for a download-sized build: the research file catalog isn't prepared, so the list shows the download manifest's unique encoded objects.
export function DownloadObjects({ snapshot, summary, bridge, active, onReopen, onReturn, fileTarget, onReturnToTable, catalog = null }: { snapshot: WorkspaceSnapshot; summary: DownloadSummary; bridge: DiscoveryBridge; active: boolean; onReopen: () => void; onReturn?: () => void; fileTarget?: FileTarget | null; onReturnToTable?: () => void; catalog?: CatalogView | null }) {
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
  const shortCount = !busy && page && page.entries.length ? `Objects ${first}–${first! + BigInt(page.entries.length) - 1n} of ${exactCount(page.totalCount)}` : count;
  const selected = list.selected;
  // A followed FileDataID's notice sits in the list's scroll area above the note, so the two line up beside its scrollbar.
  const target = fileTarget && <FileTargetNotice target={fileTarget} ready={canShow(catalog)}/>;
  return <FilesFrame fileTarget={fileTarget} onReturnToTable={onReturnToTable} active={active} pane={list.pane} onPane={list.setPane} headingRef={heading} sub="Download manifest · unique encoded objects · all published variants" covered={covered}
    notices={<>{error && <div className="dh-notice dh-error" role="alert"><div className="dh-text"><strong>{error.code}</strong>{error.message} This view keeps the exact build, source, ledger and assessment. Reopen this build to obtain a new assessment.<div className="dh-row">{error.retryable && <button ref={retry} className="dh-btn dh-small" aria-disabled={busy} onClick={() => { if (!busy) void read(attempt.current.after, attempt.current.movement); }}>Retry page</button>}<button className="dh-btn dh-small" onClick={onReopen}>Reopen this build</button></div></div></div>}
</>}
    list={page && page.entries.length > 0 ? <div className="dh-list-scroll" aria-busy={busy}>{target}<CatalogNotPrepared withObjects catalog={catalog} onReopen={onReopen}/><div className="dh-ocols dh-fthead" aria-hidden="true"><span>Object</span><span>Encoding key (EKey)</span><span>Encoded size</span><span>Local at assessment</span></div>
      <div {...list.props} aria-label={`Download objects, one row per encoding key, assessed ${summary.assessedAt}`}>{page.entries.map((entry, index) => <div key={entry.id} {...list.option(index)} className={`dh-ocols${list.option(index).className}`}><span className="dh-fid">Object {entry.id}</span><span className="dh-mono">{entry.encodingKey}</span><span>{readableBytes(entry.encodedBytes)}</span><span>{entry.verifiedAtAssessment ? <span className="dh-ok">Verified locally</span> : <span className="dh-dim">Not verified locally</span>}</span></div>)}</div></div>
      : <div className="dh-pane-scroll">{target}<CatalogNotPrepared withObjects catalog={catalog} onReopen={onReopen}/><div className="dh-local-wait"><div className="dh-state-box dh-plain" role="status"><strong>{busy ? 'Reading the saved download ledger…' : page ? page.totalCount === '0' ? 'No encoded objects' : 'No more encoded objects' : 'Download objects not read yet'}</strong><p>{page ? page.totalCount === '0' ? 'The published manifest declares zero encoded objects.' : 'No more encoded objects follow this cursor.' : 'Reads at most 50 unique encoded objects per page from the saved assessment.'}</p></div></div></div>}
    foot={<>{onReturn && <ReturnHome onReturn={onReturn}/>}<span role="status" aria-live="polite"><span className="dh-note-long">{count}</span><span className="dh-note-inline">{shortCount}</span></span>{catalog && <CatalogReady catalog={catalog}/>}<span className="dh-spacer"/><CatalogPageControls busy={busy} canFirst={cursor !== undefined} canPrevious={history.length > 0} canNext={!!page?.nextCursor} onFirst={() => void read(undefined, 'first')} onPrevious={() => void read(history.at(-1), 'previous')} onNext={() => { if (page?.nextCursor) void read(page.nextCursor, 'next'); }}/></>}
    inspector={selected ? <><div className="dh-insp-head"><div className="dh-label">Object inspector</div><h2 id="files-inspector-heading" className="dh-selectable">Encoded object {selected.entry.id}</h2><p>{selected.onPage ? 'Download manifest · encoded object' : 'Outside the current page · same saved assessment'}</p></div>
      <div className="dh-insp-body"><dl className="dh-facts"><dt>Encoding key</dt><dd className="dh-mono dh-selectable">{selected.entry.encodingKey}</dd><dt>Encoded length</dt><dd>{exactBytes(selected.entry.encodedBytes)}</dd><dt>Local object</dt><dd>{selected.entry.verifiedAtAssessment ? <><span className="dh-ok">Verified locally</span> at assessment</> : 'Not verified locally at assessment'}</dd><dt>Assessed</dt><dd>{new Date(summary.assessedAt).toLocaleString()}</dd><dt>Download ledger</dt><dd className="dh-mono dh-selectable">{summary.ledgerId}</dd><dt>Scope</dt><dd>All published variants</dd><dt>Locale / platform</dt><dd>Not selected</dd></dl>
        <p className="dh-tiny">Encoded objects are not filenames, ROOT memberships or installed files. Not verified locally is not evidence that an object is unavailable upstream. No global search, sorting, payload download or upstream availability check is performed.</p></div></>
      : <><div className="dh-insp-head"><div className="dh-label">Object inspector</div><h2 id="files-inspector-heading">No object selected</h2></div><div className="dh-insp-body"><p className="dh-reason" style={{ fontSize: 'var(--fs-m)' }}>Choose a row. Each page reads at most 50 unique encoded objects from the saved assessment.</p></div></>}/>;
}

// Files with no readable list: no download ledger and no prepared catalog. A calm capability limit, with no producer to offer.
export function FilesUnavailable({ snapshot, active, onReturn, onReopen, fileTarget, onReturnToTable, catalog = null }: { snapshot: WorkspaceSnapshot; active: boolean; onReturn?: () => void; onReopen?: () => void; fileTarget?: FileTarget | null; onReturnToTable?: () => void; catalog?: CatalogView | null }) {
  const heading = useRef<HTMLHeadingElement>(null), covered = useCoveredSide();
  const [pane, setPane] = useState<'list' | 'inspector'>('list');
  useEffect(() => { if (active) heading.current?.focus({ preventScroll: true }); }, [active]);
  return <FilesFrame fileTarget={fileTarget} onReturnToTable={onReturnToTable} active={active} pane={pane} onPane={setPane} headingRef={heading} sub="All published variants" covered={covered} selectedLabel="Selected file"
    list={<div className="dh-pane-scroll">{fileTarget && <FileTargetNotice target={fileTarget} ready={canShow(catalog)}/>}<CatalogNotPrepared problem={snapshot.downloadProblem?.code} catalog={catalog} onReopen={onReopen}/></div>}
    foot={<>{onReturn && <ReturnHome onReturn={onReturn}/>}<span role="status">{catalogFoot(catalog?.status ?? null)}</span>{catalog && <CatalogReady catalog={catalog}/>}</>}
    inspector={<><div className="dh-insp-head"><div className="dh-label">File inspector</div><h2 id="files-inspector-heading">No file selected</h2></div><div className="dh-insp-body"><p className="dh-reason" style={{ fontSize: 'var(--fs-m)' }}>The catalog has no memberships to inspect yet.</p><p className="dh-tiny">ROOT maps FileDataIDs to content keys per locale and content variant. It has no file names.</p></div></>}/>;
}

/** What Files knows about the build's file catalog (D071) and what the user can do about it. */
export type CatalogView = Readonly<{
  status: FileCatalogStatus | null;
  /** A failed status read, shown with Retry; the last confirmed status is kept. */
  error: string;
  busy: boolean;
  taskKey: string | null;
  onShow?: () => void;
  /** Resolves once the action and the status read after it have finished. */
  onAction: (action: 'stop' | 'resume') => Promise<void>;
  /** Resolves once the status read it asked for has finished, whether it succeeded or failed. */
  onRetryRead: () => Promise<void>;
}>;

/**
 * The file catalog became ready while Files shows the download objects: offered in the footer, beside the page count, so
 * rows, focus, selection and the list's scroll position stay where they are. It is announced politely. Nothing is shown
 * once the list itself is shown.
 */
export function CatalogReady({ catalog }: { catalog: CatalogView }) {
  if (!canShow(catalog)) return null;
  // At 200% the note above carries Show file list; this stays only as the polite announcement.
  return <span className="dh-catalog-ready"><span role="status" aria-live="polite"><strong>File list ready</strong><span className="dh-note-long"> · {exactCount(catalog.status!.catalog!.rootFileCount)} FileDataIDs</span></span>
    <button className="dh-btn dh-small dh-wide-only" onClick={catalog.onShow}>Show file list</button></span>;
}

/**
 * The 200% form of a failed or unsupported preparation: what happened and the next step, in a few words. The code follows
 * it while there is room (D071).
 */
export function failedShort(catalog: FileCatalogStatus, reopen: boolean): string {
  if (catalog.state === 'unsupported') return 'Can’t be read. Retrying won’t help.';
  if (catalog.problem?.code === 'STORAGE_FULL') return 'Disk full. Free some space, then retry.';
  const next = reopen ? 'Retry or reopen the build.' : 'Retry it.';
  return `${catalog.attempts >= 3 ? 'Failed three times.' : 'Couldn’t be prepared.'} ${next}`;
}

/** Where focus goes when a note's control is replaced: its first shown button, else the footer's Show file list, else the note. */
function replacement(box: HTMLElement): HTMLElement {
  // A button hidden for this text size (the 100% or 200% form) is skipped.
  const shown = (button: HTMLElement | null | undefined) => button && (typeof button.checkVisibility !== 'function' || button.checkVisibility()) ? button : null;
  return [...box.querySelectorAll<HTMLElement>('button')].find(button => shown(button))
    ?? shown(box.closest('.dh-pane')?.querySelector<HTMLElement>('.dh-pane-foot .dh-catalog-ready button')) ?? box;
}

/** The build's file list is ready and Files can show it with the footer's Show file list. */
export const canShow = (catalog: CatalogView | null): boolean => catalog?.status?.state === 'ready' && !!catalog.status.catalog && !!catalog.onShow;

/** The Files footer while the list shown is not the file catalog. */
export function catalogFoot(catalog: FileCatalogStatus | null): string {
  if (catalog?.state === 'pending') return 'Preparing file list…';
  if (catalog?.state === 'stopped') return 'File list preparation stopped';
  if (catalog?.state === 'removed') return 'File list freed';
  if (catalog?.state === 'ready') return '';
  return catalog?.state === 'failed' || catalog?.state === 'unsupported' ? 'File list not prepared' : 'Catalog not prepared';
}

const waitedFor = (catalog: FileCatalogStatus) => catalog.waitingFor ? `build ${catalog.waitingFor.buildVersion}` : null;
/** What a preparing file catalog is doing now, in one sentence that names what it waits for (D071). */
export function catalogStage(catalog: FileCatalogStatus): string {
  switch (catalog.stage) {
    case 'paused': return 'Paused with this build\'s download. Resume the build download to continue.';
    case 'acquiring': return 'Downloading the build\'s file list and encoding metadata.';
    case 'waiting': return `Waiting for the download-size measurement${waitedFor(catalog) ? ` of ${waitedFor(catalog)}` : ''} to finish first.`;
    case 'reading': return 'Reading the file list and matching it with encoding metadata.';
    case 'writing': return `Writing memberships: ${exactCount(catalog.completed!)} of ${exactCount(catalog.total!)}.`;
    case 'sealing': return 'Sealing the file list.';
    default: return waitedFor(catalog) ? `Waiting for the file list of ${waitedFor(catalog)} to finish first.` : 'Waiting to start.';
  }
}

/**
 * The stage of a preparation's Downloads & activity task: short, and never repeating what the task shows elsewhere. Written
 * counts are the task's measurement; a failure's message and code are its problem.
 */
export function catalogTaskStage(catalog: FileCatalogStatus): string {
  if (catalog.state === 'stopped') return 'Stopped · rows written so far are kept';
  if (catalog.state === 'failed') return 'File list needs attention';
  return catalog.stage === 'writing' ? 'Writing memberships' : catalogStage(catalog);
}

// Files before the file catalog is ready (#35, D071): the ROOT and install catalog is prepared in the background after Open.
// Say what is happening, what it waits for and what is missing meanwhile, so the download-object list (or no list) never
// reads as "this build has no files". It sits at the top of the list's own scroll area; at 200% text a short form keeps
// rows in view. Once the catalog is ready the box keeps its height, so the rows below never move (the hand-off is in the
// footer). With no catalog status at all (an engine without file-catalog-v1) it keeps the earlier "not prepared" note.
export function CatalogNotPrepared({ withObjects = false, problem, catalog = null, onReopen }: { withObjects?: boolean; problem?: string; catalog?: CatalogView | null; onReopen?: () => void }) {
  const box = useRef<HTMLDivElement>(null), height = useRef(0), moveFocus = useRef(false), seen = useRef(0);
  const [finished, setFinished] = useState(0);
  const status = catalog?.status ?? null;
  useLayoutEffect(() => { if (status?.state !== 'ready' && box.current) height.current = box.current.getBoundingClientRect().height; });
  // An action here (Stop, Resume, Retry, Retry status) replaces its own button once the new state arrives. Focus then goes
  // to the button that took its place, or to the note itself, never to the document body. A user who moved focus elsewhere
  // meanwhile keeps it there. Once the action's status read has finished the request is over: an action that failed
  // leaves focus on its button, and a later automatic read never moves it.
  useLayoutEffect(() => {
    const done = finished !== seen.current; seen.current = finished;
    if (!moveFocus.current || !box.current) return;
    const active = document.activeElement;
    if (active && active !== document.body && box.current.contains(active)) { if (done) moveFocus.current = false; return; }
    moveFocus.current = false;
    if (active && active !== document.body) return;
    replacement(box.current).focus({ preventScroll: true });
  });
  // Focus was in the note and its control went away with a new state, e.g. Stop when the list turns ready: focus goes to
  // the control that took its place (Show file list, here or in the footer), or to the note, never to the document body.
  const inside = useRef(false);
  useLayoutEffect(() => {
    const active = document.activeElement;
    if (inside.current && box.current && (!active || active === document.body)) replacement(box.current).focus({ preventScroll: true });
  });
  const onFocus = () => { inside.current = true; };
  const onBlur = (event: React.FocusEvent<HTMLDivElement>) => {
    const next = event.relatedTarget as Node | null, left = event.target as Node;
    if (next) { inside.current = !!box.current?.contains(next); return; }
    // No new target: either the control was removed (focus is then restored above) or focus left the window or page.
    queueMicrotask(() => { if (left.isConnected) inside.current = false; });
  };
  const act = (run: () => Promise<void> | void) => (event: React.MouseEvent<HTMLButtonElement>) => {
    const mine = document.activeElement === event.currentTarget;
    if (mine) moveFocus.current = true;
    void Promise.resolve(run()).then(() => { if (mine) setFinished(n => n + 1); });
  };
  const missing = 'Search by FileDataID or CKey, the filters and one row per FileDataID (ROOT) or install name';
  const objects = withObjects ? 'Meanwhile you can page through the download manifest\'s encoded objects below. They have no FileDataIDs or names.' : 'There is no list to show meanwhile: this build has no supported download manifest.';
  const code = problem && <> <code>({problem})</code></>;
  const name = status?.buildVersion ? ` for ${status.buildVersion}` : '';
  const frame = (title: string, tone: '' | ' dh-plain' | ' dh-warn', body: ReactNode, role: 'note' | 'status' = 'note') =>
    <div className="dh-local-wait dh-catalog-missing"><div ref={box} tabIndex={-1} onFocus={onFocus} onBlur={onBlur} className={`dh-state-box dh-inline-head${tone}`} role={role} aria-labelledby="files-catalog-missing"
      style={status?.state === 'ready' && height.current ? { minHeight: height.current } : undefined}><strong id="files-catalog-missing">{title}</strong>{body}{stale}</div></div>;
  const actions = (children: ReactNode) => <div className="dh-row">{children}</div>;
  // A status read that failed after an earlier one succeeded: the last status stays, marked as not updated, with Retry.
  const unconfirmed = !!(catalog && status && catalog.error);
  const stale = unconfirmed ? <div className="dh-catalog-stale" role="status"><p className="dh-warn-t">Status not confirmed: {catalog!.error}</p>
    {actions(<button className="dh-btn dh-small" onClick={act(catalog!.onRetryRead)}>Retry status</button>)}</div> : null;
  // The 200% form of the same: a marker on the stage line and Retry status beside the note's own actions.
  const staleMark = unconfirmed ? <span className="dh-note-inline dh-warn-t">Not confirmed · </span> : null;
  const staleRetry = unconfirmed ? <button className="dh-btn dh-small dh-compact-only" onClick={act(catalog!.onRetryRead)}>Retry status</button> : null;
  if (catalog && !status && catalog.error)
    return frame('File list status unavailable', ' dh-warn', <><p className="dh-note-long">{catalog.error} The engine could not say whether this build's file list is prepared.</p>
      <p className="dh-note-short">{catalog.error}</p>
      {actions(<button className="dh-btn dh-small" onClick={act(catalog.onRetryRead)}>Retry status</button>)}</>, 'status');
  if (catalog && !status)
    return frame('Checking the file list…', ' dh-plain', <p className="dh-dim">Reading whether this build's ROOT and install catalog is prepared.</p>, 'status');
  if (status?.state === 'pending') {
    const retrying = status.attempts > 0 && status.problem;
    return frame('Preparing file list…', '', <>
      <p>{staleMark}{retrying && <span className="dh-note-inline dh-warn-t">Attempt {status.attempts} failed, retrying · </span>}<span className="dh-dim">{catalogStage(status)}</span></p>
      {retrying && <p className="dh-dim dh-note-long">Attempt {status.attempts} failed <code>({status.problem!.code})</code>; it is tried again shortly.</p>}
      <p className="dh-dim dh-note-long">{missing} appear when the list is sealed. {objects} Progress is in Downloads &amp; activity.</p>
      {actions(<><button className="dh-btn dh-small" aria-disabled={catalog!.busy} onClick={act(() => catalog!.busy ? undefined : catalog!.onAction('stop'))}>Stop preparing file list{name}</button>
        {staleRetry}{catalog!.taskKey && <span className="dh-note-long"><ActivityLink taskKey={catalog!.taskKey}/></span>}</>)}</>);
  }
  // At 200% Show file list sits here beside the title, so the footer keeps one row; at 100% it is in the footer.
  if (status?.state === 'ready')
    return frame('File list ready', '', <p>{staleMark}<span className="dh-dim dh-note-long">Show it from the footer below, one row per file.</span>
      {catalog?.onShow && <button className="dh-btn dh-small dh-compact-only" onClick={catalog.onShow}>Show file list</button>}{staleRetry}</p>);
  if (status?.state === 'stopped')
    return frame('File list preparation stopped', ' dh-plain', <><p className="dh-note-long">The rows written so far are kept, so it continues where it stopped. {missing} need the file list. {objects}</p>
      <p className="dh-note-short">{staleMark}Rows written so far are kept.</p>
      {actions(<><button className="dh-btn dh-small" aria-disabled={catalog!.busy} onClick={act(() => catalog!.busy ? undefined : catalog!.onAction('resume'))}>Resume preparing file list{name}</button>{staleRetry}</>)}</>);
  // Freed from Settings (D085): prepared again only when asked, here or by the next Open.
  if (status?.state === 'removed')
    return frame('File list freed', ' dh-plain', <><p className="dh-note-long">It was freed in Settings to save disk space. Preparing it again runs in the background; opening the build again does it too. {missing} need the file list. {objects}</p>
      <p className="dh-note-short">{staleMark}Freed in Settings to save disk space.</p>
      {actions(<><button className="dh-btn dh-small" aria-disabled={catalog!.busy} onClick={act(() => catalog!.busy ? undefined : catalog!.onAction('resume'))}>Prepare file list again{name}</button>{staleRetry}</>)}</>);
  if (status?.state === 'failed' || status?.state === 'unsupported') {
    // Unsupported: the same bytes would be read again, so neither Retry nor Reopen is offered.
    const p = status.problem!, full = p.code === 'STORAGE_FULL', retry = status.state === 'failed', reopen = retry && onReopen;
    const why = status.state === 'unsupported' ? '' : full ? ' Free some disk space, then retry.' : status.attempts >= 3 ? ' It failed three times.' : '';
    return frame('File list not prepared', ' dh-warn', <><p className="dh-note-fit">{staleMark}<span className="dh-note-long">{p.message}{why} </span><span className="dh-note-inline">{failedShort(status, !!reopen)} </span><code className="dh-note-code">({p.code})</code></p>
      {p.detail && <p className="dh-dim dh-note-long">Cause: {p.detail}</p>}
      <p className="dh-dim dh-note-long">{missing} need the file list. {objects}</p>
      {(retry || unconfirmed) && actions(<>{retry && <button className="dh-btn dh-small" aria-disabled={catalog!.busy} onClick={act(() => catalog!.busy ? undefined : catalog!.onAction('resume'))}>Retry preparing file list{name}</button>}
        {reopen && <button className="dh-btn dh-small" onClick={reopen}>Reopen this build</button>}{staleRetry}</>)}</>);
  }
  // An engine without file-catalog-v1: the earlier note, unchanged.
  const notYet = `${missing} need this build's ROOT and install catalog, which VibeMiner doesn't prepare yet.`;
  return frame('File catalog not prepared', ' dh-plain', withObjects ? <><p className="dh-note-long">You can page through the download manifest's encoded objects below and see which are stored locally. They have no FileDataIDs or names. {notYet}</p>
      <p className="dh-note-short">Encoded objects and local status only. No FileDataID or CKey search yet.</p></>
      : <p>There is no list to show: this build has no supported download manifest.{code} {notYet} Tables, Items and Creatures prepare their own data.</p>);
}
