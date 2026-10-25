import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from 'react';
import type { DownloadCheckResult, DownloadCheckSummary, DownloadObjectEntry, DownloadPage, DownloadSummary, FileCatalogStatus, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { downloadPageMatches, type DownloadReadParams } from '../shared/download';
import { exactBytes, exactCount, readableBytes } from '../shared/inventory';
import { CatalogPageControls, FileTargetNotice, FilesFrame, ReturnHome, useListbox } from './FilesCatalog';
import type { FileTarget } from './table-follow-view';
import { useCoveredSide, useShell } from './HomeShell';
import { ActivityLink } from './DownloadActivity';
import type { DownloadCheckController } from './useDownloadCheck';

/** The measured ledger that stands for the opening manifest once ROOT is measured (D066): the build summary names it. */
export type MeasuredLedger = Readonly<{ ledgerId: string; objectCount: string }>;
type Ledger = Readonly<{ ledgerId: string; objectCount: string; measured: boolean }>;
type Movement = 'initial' | 'next' | 'previous' | 'first' | 'switch' | 'refresh';
type PageRequest = Readonly<{ ledger: Ledger; after?: string; from?: string; movement: Movement }>;
/** This view's Check as the App-level controller reports it (D093). `elsewhere` is a Check of another ledger still running. */
type CheckState = { status: 'running' | 'stopping'; taskKey: string; completed: number | null; total: number | null }
  | { status: 'done'; result: DownloadCheckResult } | { status: 'stopped' } | { status: 'busy' } | { status: 'failed'; code: string; message: string }
  | { status: 'elsewhere'; taskKey: string; build: string };
const PAGE = 50;

/**
 * What a row's local column says: Open's assessment for manifest files, "Not checked at Open" or a Check's outcome for the
 * rest (D093). The label carries the colour; the explanation after it, in the inspector, does not.
 */
export function localState(entry: DownloadObjectEntry): { label: string; tone: string; explanation: string } {
  if (entry.checkedAtOpen) return entry.verifiedAtAssessment ? { label: 'Verified locally', tone: 'dh-ok', explanation: ' at assessment' } : { label: 'Not verified locally', tone: 'dh-dim', explanation: ' at assessment' };
  switch (entry.check) {
    case 'verified': return { label: 'Verified at check', tone: 'dh-ok', explanation: ': the stored copy matches its receipt' };
    case 'notStored': return { label: 'Not stored at check', tone: 'dh-dim', explanation: ': there is no stored copy here' };
    case 'missing': return { label: 'Missing at check', tone: 'dh-bad-t', explanation: ': a receipt names it, but the stored file is gone' };
    case 'corrupt': return { label: 'Corrupt at check', tone: 'dh-bad-t', explanation: ': the stored bytes failed verification' };
    default: return { label: 'Not checked at Open', tone: 'dh-warn-t', explanation: ': Open assessed the download manifest only' };
  }
}
/** A completed Check's counts, the same words in the note whether it ran now or was read back with the page. */
const outcomes = (value: Pick<DownloadCheckSummary, 'verifiedObjects' | 'notStoredObjects' | 'missingObjects' | 'corruptObjects'>) =>
  `${exactCount(value.verifiedObjects)} verified · ${exactCount(value.notStoredObjects)} not stored · ${exactCount(value.missingObjects)} missing · ${exactCount(value.corruptObjects)} corrupt.`;

// Files for a download-sized build: the research file catalog isn't prepared, so the list shows the download ledger's unique
// encoded objects. Once the build's file list is measured (D066) that is the ledger with every file ROOT lists; the ones
// outside the manifest are not checked at Open until a Check covers them (D093).
export function DownloadObjects({ snapshot, summary, measured = null, measuring = false, bridge, active, onReopen, onReturn, fileTarget, onReturnToTable, catalog = null, checkAvailable = false, checker, onLedger, focusCheck = 0, onCheckFocused, viewSwitch }: { focusCheck?: number; onCheckFocused?: () => void; viewSwitch?: ReactNode; snapshot: WorkspaceSnapshot; summary: DownloadSummary; measured?: MeasuredLedger | null | 'pending'; measuring?: boolean; bridge: DiscoveryBridge; active: boolean; onReopen: () => void; onReturn?: () => void; fileTarget?: FileTarget | null; onReturnToTable?: () => void; catalog?: CatalogView | null; checkAvailable?: boolean; checker?: DownloadCheckController; onLedger?: (ledger: MeasuredLedger | null) => void }) {
  // `measured` is 'pending' until the build's summary has been read, so the first page is read from the right ledger.
  const wanted: Ledger | null = measured === 'pending' ? null : measured ? { ...measured, measured: true } : { ledgerId: summary.ledgerId, objectCount: summary.objectCount, measured: false };
  const [ledger, setLedger] = useState<Ledger | null>(null);
  const [page, setPage] = useState<DownloadPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ code: string; message: string; retryable: boolean } | null>(null);
  const [cursor, setCursor] = useState<string | undefined>();
  const heading = useRef<HTMLHeadingElement>(null), retry = useRef<HTMLButtonElement>(null);
  const focusAfterRead = useRef<HTMLElement | null>(null);
  const alive = useRef(true), request = useRef(0), loading = useRef(false), failedSwitch = useRef<string | null>(null);
  const attempt = useRef<PageRequest | null>(null);
  const list = useListbox<DownloadObjectEntry>(page?.entries ?? [], entry => entry.encodingKey);
  const covered = useCoveredSide(), { compact } = useShell();
  const shown = ledger ?? wanted;
  const current = shown ?? { ledgerId: summary.ledgerId, objectCount: summary.objectCount, measured: false };
  const outside = current.measured ? BigInt(current.objectCount) - BigInt(summary.objectCount) : 0n;
  // The Check belongs to the App (D093): it keeps running when Files or the build is left, and shows in Downloads & activity.
  const op = checker?.operation ?? null;
  const ours = !!op && op.intent.ledgerId === current.ledgerId && op.intent.productId === snapshot.build.productId && op.intent.buildIdentity === snapshot.build.id && op.intent.sourceRegion === snapshot.sourceReference.region;
  const runningOp = !!op && (op.status === 'pending' || op.status === 'stopping');
  // Another build's Check, named by product and version; a build of the same version adds its region when that differs,
  // and the first characters of its build configuration.
  const otherBuild = (intent: NonNullable<typeof op>['intent']) => `${intent.productId} ${intent.buildVersion}${intent.buildVersion !== snapshot.build.version ? ''
    : ` (${intent.sourceRegion !== snapshot.sourceReference.region ? `${intent.sourceRegion.toUpperCase()}, ` : ''}build config ${intent.buildConfig.slice(0, 8)})`}`;
  const check: CheckState | null = !op ? null : !ours ? runningOp ? { status: 'elsewhere', taskKey: `downloadCheck:${op.id}`, build: otherBuild(op.intent) } : null
    : runningOp ? { status: op.status === 'stopping' ? 'stopping' : 'running', taskKey: `downloadCheck:${op.id}`, completed: op.progress?.completed ?? null, total: op.progress?.total ?? null }
    : op.status === 'succeeded' && op.result ? { status: 'done', result: op.result }
    : op.status === 'canceled' ? { status: 'stopped' }
    : op.error?.code === 'DOWNLOAD_CHECK_BUSY' ? { status: 'busy' }
    : { status: 'failed', code: op.error?.code ?? 'ENGINE_UNAVAILABLE', message: op.error?.message ?? 'The check could not be completed.' };
  useLayoutEffect(() => {
    const previous = focusAfterRead.current;
    if (!previous || error) return;
    focusAfterRead.current = null;
    if (!heading.current?.closest('[hidden]') && (document.activeElement === document.body || document.activeElement === previous)) heading.current?.focus({ preventScroll: true });
  }, [page, error]);
  useEffect(() => { alive.current = true; return () => { alive.current = false; request.current++; }; }, []);
  // Opened from Build overview's Check line: focus goes to the Check (below), not the heading.
  // The request is cleared by its owner once served (onCheckFocused), so a later return to this view focuses the heading.
  const served = useRef(0);
  useEffect(() => { if (active && !focusCheck) heading.current?.focus({ preventScroll: true }); }, [active]);
  useEffect(() => { if (active && wanted && !page && !loading.current) void read({ ledger: wanted, movement: 'initial' }); }, [active, wanted?.ledgerId]);
  useEffect(() => { onLedger?.(ledger?.measured ? { ledgerId: ledger.ledgerId, objectCount: ledger.objectCount } : null); }, [ledger?.ledgerId]);
  // The measurement landed while these rows were shown: read the measured ledger from the first row on screen, so the page
  // and the picked row stay where they were. Rows of the two ledgers are never mixed; the old page stays until the new one
  // has been read and checked.
  useEffect(() => {
    if (!page || !ledger || !wanted?.measured || wanted.ledgerId === ledger.ledgerId || busy || loading.current || failedSwitch.current === wanted.ledgerId) return;
    const first = page.entries[0]?.encodingKey;
    void read({ ledger: wanted, movement: 'switch', ...(first ? { from: first } : {}) });
  }, [page, ledger?.ledgerId, wanted?.ledgerId, busy]);
  // A Check finished: the rows on screen are read again once no other read is running, so they carry its outcome; the page
  // and the pick stay where they are. A page read meanwhile already brings it.
  const refreshed = useRef<string | null>(null);
  useEffect(() => {
    if (check?.status !== 'done' || !page || !ledger || busy || loading.current) return;
    const { ledgerId, checkedAt } = check.result;
    if (ledger.ledgerId !== ledgerId || page.lastCheck?.checkedAt === checkedAt || refreshed.current === checkedAt) return;
    refreshed.current = checkedAt;
    void read({ ledger, movement: 'refresh', ...(cursor === undefined ? {} : { after: cursor }) });
  }, [check?.status === 'done' ? check.result.checkedAt : null, page, busy]);
  const paramsOf = (target: Ledger, limit = PAGE): DownloadReadParams => ({ productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region, ledgerId: target.ledgerId, limit });
  async function read(next: PageRequest) {
    if (loading.current) return;
    attempt.current = next;
    const generation = ++request.current;
    loading.current = true; setBusy(true);
    const params = { ...paramsOf(next.ledger), ...(next.after === undefined ? {} : { after: next.after }), ...(next.from === undefined ? {} : { from: next.from }) };
    const picked = list.picked() ? list.selected?.entry : undefined;
    try {
      const reply = await bridge.download(params);
      if (!alive.current || generation !== request.current) return;
      if (!reply.ok) { if (next.movement === 'switch') failedSwitch.current = next.ledger.ledgerId; setError(reply.error); return; }
      if (!downloadPageMatches(reply.value, params, { assessedAt: summary.assessedAt, objectCount: next.ledger.objectCount })) { if (next.movement === 'switch') failedSwitch.current = next.ledger.ledgerId; setError({ code: 'DOWNLOAD_CONTEXT_MISMATCH', message: 'This page belongs to a different ledger or assessment. Previous rows are preserved.', retryable: false }); return; }
      if (document.activeElement === retry.current) focusAfterRead.current = retry.current;
      const value = reply.value, start = value.entries.length ? (BigInt(value.entries[0].id) - 1n).toString() : next.from !== undefined ? value.totalCount : next.after ?? '0';
      failedSwitch.current = null;
      setPage(value); setLedger(next.ledger); setCursor(start === '0' ? undefined : start); setError(null);
      // A row picked before the switch keeps its pick: read it again from the measured ledger when it is off this page.
      if (next.movement === 'switch' && picked && !value.entries.some(entry => entry.encodingKey === picked.encodingKey)) {
        const one = { ...paramsOf(next.ledger, 1), from: picked.encodingKey };
        const again = await bridge.download(one).catch(() => null);
        if (alive.current && generation === request.current && again?.ok && downloadPageMatches(again.value, one, { assessedAt: summary.assessedAt, objectCount: next.ledger.objectCount }) && again.value.entries[0]?.encodingKey === picked.encodingKey) list.replace(again.value.entries[0]);
      }
    } catch { if (alive.current && generation === request.current) setError({ code: 'ENGINE_UNAVAILABLE', message: 'The saved download ledger could not be read. Previous rows are preserved.', retryable: true }); }
    finally { if (alive.current && generation === request.current) { loading.current = false; setBusy(false); } }
  }
  const runCheck = () => { if (checker && current.measured) void checker.start({ productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region, ledgerId: current.ledgerId, buildVersion: snapshot.build.version, buildConfig: snapshot.build.buildConfig, files: outside.toString() }); };
  const stopCheck = () => { if (op && ours) checker?.stop(op.id); };
  const first = page?.entries.length ? BigInt(page.entries[0].id) : null;
  const count = busy ? 'Reading the saved download ledger…' : page ? `${page.entries.length} encoded objects on this page · ${exactCount(page.totalCount)} total${page.entries.length ? ` · objects ${first}–${first! + BigInt(page.entries.length) - 1n}` : ''}` : 'No download-object page has been read yet.';
  const shortCount = !busy && page && page.entries.length ? `Objects ${first}–${first! + BigInt(page.entries.length) - 1n} of ${exactCount(page.totalCount)}` : count;
  const selected = list.selected;
  const previousCursor = cursor === undefined ? undefined : BigInt(cursor) <= BigInt(PAGE) ? undefined : (BigInt(cursor) - BigInt(PAGE)).toString();
  const measuredPage = !!page && page.ledgerId !== page.manifestLedgerId;
  // A followed FileDataID's notice sits in the list's scroll area above the note, so the two line up beside its scrollbar. Both, and
  // the note about files not checked at Open, come after the column header, so the header ends where the inspector head ends (#82).
  const target = fileTarget && <FileTargetNotice target={fileTarget} ready={canShow(catalog)}/>;
  // At 200% the notes above the rows leave no room for another, so the Check note moves to the Selected object pane, and the
  // pane head, which has room beside the title then, points there (and says how a running Check is going).
  const noteShown = measuredPage && outside > 0n;
  const checkNote = (placement: 'list' | 'inspector') => noteShown && compact === (placement === 'inspector') &&
    <CheckNote placement={placement} noteRef={placement === 'inspector' ? inspectorNote : listNote} outside={outside} state={check} lastCheck={page!.lastCheck} available={checkAvailable && !!checker?.available} onCheck={runCheck} onStop={stopCheck}/>;
  // The pointer's own pane hides as it shows Selected object, so focus goes to the Check note there, never to the body:
  // its Check or Stop button, or the note itself when it offers none.
  const inspectorNote = useRef<HTMLDivElement>(null), listNote = useRef<HTMLDivElement>(null), focusNote = useRef(false);
  // A Check line on Build overview asked for the Check: once its note is shown (at 200% in Selected object), focus it.
  useLayoutEffect(() => {
    if (!focusCheck || focusCheck === served.current || !active) return;
    if (compact && list.pane !== 'inspector') { list.setPane('inspector'); return; }
    const note = compact ? inspectorNote.current : listNote.current;
    if (!note) return;
    served.current = focusCheck; onCheckFocused?.();
    (note.querySelector<HTMLElement>('button') ?? note).focus();
  });
  useLayoutEffect(() => {
    if (!focusNote.current || list.pane !== 'inspector' || !inspectorNote.current) return;
    focusNote.current = false;
    (inspectorNote.current.querySelector<HTMLElement>('button') ?? inspectorNote.current).focus({ preventScroll: false });
  });
  const pointer = noteShown && compact && <span className="dh-check-pointer"><strong className="dh-warn-t">{exactCount(outside.toString())} not checked at Open</strong>
    {check?.status === 'running' && check.completed !== null && check.total !== null ? <> · Checking {exactCount(String(check.completed))} of {exactCount(String(check.total))}…</> : null}
    {' · '}<button className="dh-linkbtn" onClick={() => { focusNote.current = true; list.setPane('inspector'); }}>{check?.status === 'running' || check?.status === 'stopping' ? 'Check in Selected object' : 'Check them in Selected object'}</button></span>;
  const sub = current.measured ? 'Download manifest and every file ROOT lists · unique encoded objects · all published variants' : `Download manifest · unique encoded objects · all published variants${measuring ? ' · files outside it are being measured' : ''}`;
  const selectedState = selected ? localState(selected.entry) : null;
  return <FilesFrame fileTarget={fileTarget} onReturnToTable={onReturnToTable} active={active} pane={list.pane} onPane={list.setPane} headingRef={heading} sub={sub} covered={covered}
    head={<><h1 ref={heading} id="files-heading" tabIndex={-1}>Files</h1>{viewSwitch}<span className="dh-sub">{sub}</span>{pointer}</>}
    notices={<>{error && <div className="dh-notice dh-error" role="alert"><div className="dh-text"><strong>{error.code}</strong>{error.message} This view keeps the exact build, source, ledger and assessment. Reopen this build to obtain a new assessment.<div className="dh-row">{error.retryable && <button ref={retry} className="dh-btn dh-small" aria-disabled={busy} onClick={() => { if (!busy && attempt.current) { failedSwitch.current = null; void read(attempt.current); } }}>Retry page</button>}<button className="dh-btn dh-small" onClick={onReopen}>Reopen this build</button></div></div></div>}
</>}
    list={page && page.entries.length > 0 ? <div className="dh-list-scroll" aria-busy={busy}><div className="dh-ocols dh-fthead" aria-hidden="true"><span>Object</span><span>Encoding key (EKey)</span><span>Encoded size</span><span>{measuredPage ? 'Local status' : 'Local at assessment'}</span></div>{target}<CatalogNotPrepared withObjects catalog={catalog} onReopen={onReopen}/>{checkNote('list')}
      <div {...list.props} aria-label={`Download objects, one row per encoding key, assessed ${summary.assessedAt}`}>{page.entries.map((entry, index) => { const state = localState(entry); return <div key={entry.encodingKey} {...list.option(index)} className={`dh-ocols${list.option(index).className}`}><span className="dh-fid">Object {entry.id}</span><span className="dh-mono dh-wrap dh-okey">{entry.encodingKey}</span><span>{readableBytes(entry.encodedBytes)}</span><span className="dh-wrap dh-ostate"><span className={state.tone}>{state.label}</span></span></div>; })}</div></div>
      : <div className="dh-pane-scroll">{target}<CatalogNotPrepared withObjects catalog={catalog} onReopen={onReopen}/>{checkNote('list')}<div className="dh-local-wait"><div className="dh-state-box dh-plain" role="status"><strong>{busy || !page && !wanted ? 'Reading the saved download ledger…' : page ? page.totalCount === '0' ? 'No encoded objects' : 'No more encoded objects' : 'Download objects not read yet'}</strong><p>{page ? page.totalCount === '0' ? 'The published manifest declares zero encoded objects.' : 'No more encoded objects follow this cursor.' : `Reads at most ${PAGE} unique encoded objects per page from the saved assessment.`}</p></div></div></div>}
    foot={<>{onReturn && <ReturnHome onReturn={onReturn}/>}<span role="status" aria-live="polite"><span className="dh-note-long">{count}</span><span className="dh-note-inline">{shortCount}</span></span>{catalog && <CatalogReady catalog={catalog}/>}<span className="dh-spacer"/><CatalogPageControls busy={busy} canFirst={cursor !== undefined} canPrevious={cursor !== undefined} canNext={!!page?.nextCursor} onFirst={() => { if (shown) void read({ ledger: shown, movement: 'first' }); }} onPrevious={() => { if (shown) void read({ ledger: shown, movement: 'previous', ...(previousCursor === undefined ? {} : { after: previousCursor }) }); }} onNext={() => { if (page?.nextCursor && shown) void read({ ledger: shown, movement: 'next', after: page.nextCursor }); }}/></>}
    inspector={selected && selectedState ? <><div className="dh-insp-head"><div className="dh-label">Object inspector</div><h2 id="files-inspector-heading" className="dh-selectable">Encoded object {selected.entry.id}</h2><p>{!selected.onPage ? 'Outside the current page · same saved assessment' : selected.entry.checkedAtOpen ? 'Download manifest · encoded object' : 'Listed by ROOT outside the download manifest · encoded object'}</p></div>
      <div className="dh-insp-body">{checkNote('inspector')}<dl className="dh-facts"><dt>Encoding key</dt><dd className="dh-mono dh-selectable">{selected.entry.encodingKey}</dd><dt>Encoded length</dt><dd>{exactBytes(selected.entry.encodedBytes)}</dd><dt>Local object</dt><dd><span className={selectedState.tone}>{selectedState.label}</span>{selectedState.explanation}</dd><dt>Assessed</dt><dd>{new Date(summary.assessedAt).toLocaleString()}</dd>{!selected.entry.checkedAtOpen && <><dt>Checked</dt><dd>{page?.lastCheck ? new Date(page.lastCheck.checkedAt).toLocaleString() : 'Not yet'}</dd></>}<dt>Download ledger</dt><dd className="dh-mono dh-selectable">{current.ledgerId}</dd>{current.measured && <><dt>Manifest ledger</dt><dd className="dh-mono dh-selectable">{summary.ledgerId}</dd></>}<dt>Scope</dt><dd>{current.measured ? 'Download manifest and every file ROOT lists · all published variants' : 'All published variants'}</dd><dt>Locale / platform</dt><dd>Not selected</dd></dl>
        <p className="dh-tiny">Encoded objects are not filenames, ROOT memberships or installed files. Not verified locally is not evidence that an object is unavailable upstream.{current.measured ? ' Open assessed the download manifest only; a Check verifies the stored copies of the files ROOT lists outside it.' : ''} No global search, sorting, payload download or upstream availability check is performed.</p></div></>
      : <><div className="dh-insp-head"><div className="dh-label">Object inspector</div><h2 id="files-inspector-heading">No object selected</h2></div><div className="dh-insp-body">{checkNote('inspector')}<p className="dh-reason" style={{ fontSize: 'var(--fs-m)' }}>Choose a row. Each page reads at most {PAGE} unique encoded objects from the saved assessment.</p></div></>}/>;
}

/**
 * The files a measured ledger adds to the manifest: how many were not checked at Open, what the latest Check found (its
 * counts are saved with it), and Check or Stop. One button changes between the two, so focus stays on it through the whole
 * Check. While it runs the note turns mint and points to its task in Downloads & activity, as file list preparation does.
 */
function CheckNote({ placement, noteRef, outside, state, lastCheck, available, onCheck, onStop }: { placement: 'list' | 'inspector'; noteRef?: RefObject<HTMLDivElement | null>; outside: bigint; state: CheckState | null; lastCheck: DownloadCheckSummary | null; available: boolean; onCheck: () => void; onStop: () => void }) {
  const count = exactCount(outside.toString()), files = outside === 1n ? 'file' : 'files';
  const active = state?.status === 'running' || state?.status === 'stopping';
  const done = state?.status === 'done' ? state.result : null;
  const progress = active && state.completed !== null && state.total !== null ? `Checking ${exactCount(String(state.completed))} of ${exactCount(String(state.total))} ${files}…` : active ? 'Starting the check…' : '';
  const earlier = lastCheck ? <><span className="dh-note-long">Last checked {new Date(lastCheck.checkedAt).toLocaleString()}: </span><span className="dh-note-inline">Last checked: </span>{outcomes(lastCheck)}</> : null;
  // At 200% the outcome drops its date, so it and the button keep to the title's line where they fit.
  const outcome = state?.status === 'stopping' ? 'Stopping the check…'
    : done ? <><span className="dh-note-long">Checked {new Date(done.checkedAt).toLocaleString()}: </span><span className="dh-note-inline">Checked: </span>{outcomes(done)}</>
    : state?.status === 'stopped' ? <>Check stopped. Nothing new was recorded{lastCheck ? '; the earlier check stays' : ''}.</>
    : state?.status === 'busy' ? 'Another check is still finishing; try again shortly.'
    : state?.status === 'elsewhere' ? `A check of ${state.build} is running; one runs at a time.`
    : state?.status === 'failed' ? `The check failed: ${state.message} (${state.code})`
    : active ? '' : earlier ?? '';
  const title = useId();
  // When a Check ends, its Stop or View task button goes. If focus was on it, it moves to the button that took its place
  // (Check), or to the note itself, never to the body. Focus the reader moved elsewhere stays there.
  const own = useRef<HTMLDivElement | null>(null), inside = useRef(false);
  useLayoutEffect(() => {
    const focused = document.activeElement;
    if (!inside.current || !own.current || focused && focused !== document.body) return;
    (own.current.querySelector<HTMLElement>('button') ?? own.current).focus({ preventScroll: true });
  });
  const onFocus = () => { inside.current = true; };
  const onBlur = (event: React.FocusEvent<HTMLDivElement>) => {
    const next = event.relatedTarget as Node | null, left = event.target as Node;
    if (next) { inside.current = !!own.current?.contains(next); return; }
    // No new target: the button was removed (restored above), or focus left the window.
    queueMicrotask(() => { if (left.isConnected) inside.current = false; });
  };
  const setBox = (element: HTMLDivElement | null) => { own.current = element; if (noteRef) (noteRef as { current: HTMLDivElement | null }).current = element; };
  const taskKey = state?.status === 'running' || state?.status === 'stopping' || state?.status === 'elsewhere' ? state.taskKey : null;
  const box = <div ref={setBox} onFocus={onFocus} onBlur={onBlur} tabIndex={-1} className={`dh-state-box dh-inline-head${active ? '' : ' dh-plain'}`} role="group" aria-labelledby={title}>
    <strong id={title}>{count} {files} not checked at Open</strong>
    <p className={placement === 'list' ? 'dh-note-long dh-dim' : 'dh-dim'}>ROOT lists {outside === 1n ? 'it' : 'them'} outside the download manifest, and Open assessed only the manifest. Check hashes each stored copy against its receipt, in the background; nothing is downloaded.{active ? ' Progress is in Downloads & activity.' : ''}</p>
    {progress && <p className="dh-check-progress">{progress}</p>}
    {(active || state?.status === 'stopped') && earlier && <p className="dh-dim dh-check-earlier">{earlier}</p>}
    <p role="status" aria-live="polite" className={state?.status === 'failed' ? 'dh-bad-t' : undefined}>{outcome}</p>
    {(available || taskKey) && <div className="dh-row">{available && state?.status !== 'elsewhere' && <button className="dh-btn dh-small" aria-disabled={state?.status === 'stopping'} onClick={() => { if (state?.status === 'running') onStop(); else if (!active) onCheck(); }}>{active ? 'Stop checking' : lastCheck || done ? `Check ${count} ${files} again` : `Check ${count} ${files}`}</button>}
      {taskKey && <ActivityLink taskKey={taskKey}/>}</div>}
  </div>;
  return placement === 'list' ? <div className="dh-local-wait dh-catalog-missing dh-check-note">{box}</div> : <div className="dh-check-note dh-check-inspector">{box}</div>;
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
  // A reader who switched to the download objects on purpose has the Files view switch; no second offer of the list.
  if (status?.state === 'ready' && !catalog?.onShow) return null;
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
