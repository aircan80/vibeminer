import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import type { BridgeReply, DiscoveryBridge } from '../shared/bridge';
import type { PreparedBuild, PreparedDataFreed, PreparedDataList } from '../shared/settings';
import { exactBytes, exactCount, readableBytes } from '../shared/inventory';

// Settings › Storage › Prepared data per build (D085, #46). The engine measures every opened build's file list on disk,
// the table rows its admissions use and its stored download figure. Free deletes only the file list, the one part no other
// build uses; it is prepared again the next time the build is opened.
type Problem = { code: string; message: string; retryable: boolean };
type List = { phase: 'reading' } | { phase: 'read'; value: PreparedDataList; refreshing: boolean } | { phase: 'failed'; error: Problem } | { phase: 'unavailable' };
type Action = { key: string; phase: 'confirm' | 'freeing' } | { key: string; phase: 'freed'; value: PreparedDataFreed } | { key: string; phase: 'failed'; error: Problem };
const thrown: Problem = { code: 'ENGINE_UNAVAILABLE', message: 'The local engine is unavailable. Restart VibeMiner to reconnect.', retryable: true };
const failure = <T,>(reply: BridgeReply<T>): Problem => reply.ok ? thrown : { code: reply.error.code, message: reply.error.message, retryable: reply.error.retryable };
const keyOf = (b: { productId: string; buildIdentity: string }) => `${b.productId}|${b.buildIdentity}`;
const stamp = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const nameOf = (b: PreparedBuild) => b.buildVersion ?? b.buildIdentity.slice(0, 12);
/** Whether Free has anything to do: files on disk, or a preparation to stop. */
export const freeable = (b: PreparedBuild) => b.fileCatalog.state === 'pending' || b.fileCatalog.files > 0;

export function fileListFact(b: PreparedBuild): ReactNode {
  const c = b.fileCatalog, size = c.bytes === '0' ? null : <>{readableBytes(c.bytes)} <span className="st-dim st-mono">· {exactBytes(c.bytes)}</span></>;
  switch (c.state) {
    case 'none': return <span className="st-dim">Not prepared</span>;
    case 'ready': return <>{size ?? '0 bytes'} · ready</>;
    case 'pending': return <>{size ?? 'Nothing written yet'} · being prepared</>;
    case 'stopped': return <>{size ?? '0 bytes'} · stopped, rows kept</>;
    case 'failed': return <>{size ?? '0 bytes'} · preparation failed</>;
    case 'unsupported': return <>{size ?? '0 bytes'} · can’t be read</>;
    // Freed with files the engine couldn't delete yet: what is still on disk, as leftovers, not as the build's file list.
    default: return size ? <>Freed · {c.files} {c.files === 1 ? 'file' : 'files'} left to delete, {size} on disk</> : <span className="st-dim">Freed · prepared again on the next Open</span>;
  }
}
export function tablesFact(b: PreparedBuild): ReactNode {
  const t = b.tables;
  if (t.count === 0) return <span className="st-dim">None</span>;
  return <>{readableBytes(t.bytes)} in {exactCount(String(t.count))} {t.count === 1 ? 'file' : 'files'}{t.sharedBytes !== '0' && <>, {readableBytes(t.sharedBytes)} also used by other builds</>} · kept</>;
}
export function downloadsFact(b: PreparedBuild): ReactNode {
  const d = b.downloads;
  if (d.state === 'notMeasured') return <span className="st-dim">Not measured for this build</span>;
  if (d.storedBytes === null) return <span className="st-dim">Not known: the stored figure couldn’t be read</span>;
  return <>{readableBytes(d.storedBytes)}{d.state === 'counting' ? ' so far, still counting' : d.state === 'unavailable' ? ', not current' : ''} · shared with builds that list the same files · kept</>;
}

export function PreparedDataSetting({ bridge, onFreed }: { bridge: DiscoveryBridge; onFreed: () => void }) {
  const [list, setList] = useState<List>(bridge.storageBuilds ? { phase: 'reading' } : { phase: 'unavailable' });
  const [action, setAction] = useState<Action | null>(null);
  const alive = useRef(true), generation = useRef(0), reading = useRef<string | null>(null), refocus = useRef<{ key: string; to: 'free' | 'confirm' | 'heading' } | null>(null);
  const root = useRef<HTMLDivElement>(null);
  async function read() {
    if (!bridge.storageBuilds) return;
    const number = ++generation.current, id = `settings_builds_${Date.now().toString(36)}_${number}`;
    reading.current = id;
    setList(old => old.phase === 'read' ? { ...old, refreshing: true } : { phase: 'reading' });
    let reply: BridgeReply<PreparedDataList>;
    try { reply = await bridge.storageBuilds(id); } catch { reply = { ok: false, error: thrown }; }
    if (reading.current === id) reading.current = null;
    if (!alive.current || number !== generation.current) return;
    if (reply.ok) setList({ phase: 'read', value: reply.value, refreshing: false });
    else setList(reply.error.code === 'UNSUPPORTED' ? { phase: 'unavailable' } : { phase: 'failed', error: failure(reply) });
  }
  useEffect(() => {
    alive.current = true; void read();
    // Leaving Settings stops the measurement; a late reply is dropped by the generation check.
    return () => { alive.current = false; const id = reading.current; reading.current = null; if (id) void bridge.cancel(id).catch(() => { /* The engine retires it on its own. */ }); };
  }, [bridge]);
  // Focus follows the control that replaced the one used, never the document body.
  useLayoutEffect(() => {
    const target = refocus.current; if (!target || !root.current) return;
    const row = root.current.querySelector<HTMLElement>(`[data-build="${CSS.escape(target.key)}"]`);
    const pick = target.to === 'confirm' ? row?.querySelector<HTMLElement>('.st-confirm .st-btn') : target.to === 'free' ? row?.querySelector<HTMLElement>('.st-free') : null;
    const element = pick ?? row?.querySelector<HTMLElement>('h4');
    if (element) { refocus.current = null; element.focus({ preventScroll: false }); }
  });
  async function free(b: PreparedBuild) {
    const key = keyOf(b);
    if (!bridge.storageFree || action?.phase === 'freeing') return;
    // The confirm buttons give way to a status line: the build's heading holds focus meanwhile.
    setAction({ key, phase: 'freeing' }); refocus.current = { key, to: 'heading' };
    let reply: BridgeReply<PreparedDataFreed>;
    try { reply = await bridge.storageFree({ productId: b.productId, buildIdentity: b.buildIdentity }); } catch { reply = { ok: false, error: thrown }; }
    // The open build's Home and Files read their file list again, even when Settings was closed meanwhile.
    onFreed();
    if (!alive.current) return;
    setAction(reply.ok ? { key, phase: 'freed', value: reply.value } : { key, phase: 'failed', error: failure(reply) });
    // Completion moves focus only if it is still in this build's entry (or nowhere), never away from where the user went.
    const active = document.activeElement, entry = root.current?.querySelector(`[data-build="${CSS.escape(key)}"]`);
    if (!active || active === document.body || entry?.contains(active)) refocus.current = { key, to: reply.ok ? 'heading' : 'free' };
    void read();
  }
  const confirm = (b: PreparedBuild) => { setAction({ key: keyOf(b), phase: 'confirm' }); refocus.current = { key: keyOf(b), to: 'confirm' }; };
  const keep = (b: PreparedBuild) => { setAction(null); refocus.current = { key: keyOf(b), to: 'free' }; };
  const escape = (b: PreparedBuild) => (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); keep(b); } };

  let body: ReactNode;
  if (list.phase === 'unavailable') body = <p className="st-dim st-builds-note">Not measured: this VibeMiner engine doesn’t report prepared data per build.</p>;
  else if (list.phase === 'reading') body = <p className="st-builds-note"><span className="st-loading" role="status"><span className="st-pulse" aria-hidden="true"/>Measuring each opened build…</span></p>;
  else if (list.phase === 'failed') body = <div className="st-notice error st-builds-note" role="alert"><div className="st-text"><strong>Couldn’t measure prepared data.</strong>{list.error.message} Nothing was changed. <code>({list.error.code})</code></div>{list.error.retryable && <button className="st-btn" onClick={() => { void read(); }}>Measure again</button>}</div>;
  else if (list.value.builds.length === 0) body = <p className="st-dim st-builds-note">No build has been opened yet, so nothing is prepared.</p>;
  else body = <ul className="st-builds" aria-labelledby="settings-prepared-name" aria-busy={list.refreshing}>{list.value.builds.map(b => {
    const key = keyOf(b), mine = action?.key === key ? action : null, name = nameOf(b), headingId = `settings-build-${b.productId}-${b.buildIdentity.slice(0, 16)}`;
    const busy = action?.phase === 'freeing', stop = b.fileCatalog.state === 'pending';
    const size = b.fileCatalog.files > 0 ? readableBytes(b.fileCatalog.bytes) : null;
    let control: ReactNode;
    if (mine?.phase === 'confirm') control = <div className="st-confirm" role="group" aria-labelledby={`${headingId}-confirm`} onKeyDown={escape(b)}>
      <span id={`${headingId}-confirm`} className="st-confirm-text">{stop ? 'Stop preparing and free' : 'Free'} {size ?? 'the file list'}? Until it’s prepared again, the next time you open this build, Files has no FileDataID search or names for it. Table rows and downloaded files stay.</span>
      <span className="st-ctl-row"><button className="st-btn" aria-describedby={headingId} onClick={() => { void free(b); }}>{stop ? 'Stop and free' : `Free ${size ?? 'file list'}`}</button><button className="st-btn" onClick={() => keep(b)}>Keep</button></span></div>;
    else if (mine?.phase === 'freeing') control = <span className="st-save-line" role="status"><span className="st-busy"><span className="st-pulse" aria-hidden="true"/>{stop ? 'Stopping and freeing…' : 'Freeing…'}</span></span>;
    else control = <>{freeable(b) ? <button className="st-btn st-free" aria-describedby={headingId} aria-disabled={busy} onClick={() => { if (!busy) confirm(b); }}>{stop ? 'Stop and free file list…' : 'Free file list…'}</button>
      : <span className="st-dim st-save-line">Nothing to free</span>}
      {mine?.phase === 'freed' && <span className="st-save-line" role="status"><span className="st-ok">Freed {readableBytes(mine.value.freedBytes)}</span></span>}</>;
    return <li key={key} className="st-build" data-build={key}>
      <div className="st-text">
        <h4 id={headingId} tabIndex={-1}>{name} <span className="st-dim">· {b.productId} · opened {stamp(b.lastOpenedAt)}</span></h4>
        <dl className="st-facts"><dt>File list</dt><dd>{fileListFact(b)}</dd><dt>Table rows</dt><dd>{tablesFact(b)}</dd><dt>Downloaded files</dt><dd>{downloadsFact(b)}</dd></dl>
        {/* One way to try again: the entry's own Free button, shown while anything is left to delete. */}
        {mine?.phase === 'failed' && (mine.error.code === 'STORAGE_FREE_INCOMPLETE'
          ? <div className="st-notice" role="alert"><div className="st-text"><strong>Freed, with files left to delete.</strong>{mine.error.message} <code>({mine.error.code})</code></div></div>
          : <div className="st-notice error" role="alert"><div className="st-text"><strong>Couldn’t free the file list of {name}.</strong>{mine.error.message} <code>({mine.error.code})</code></div></div>)}
      </div>
      <div className="st-ctl">{control}</div>
    </li>;
  })}</ul>;
  const listed = list.phase === 'read' ? list.value : null;
  const total = listed ? listed.builds.reduce((sum, b) => sum + BigInt(b.fileCatalog.bytes), 0n).toString() : null;
  return <div ref={root} className="st-setting" id="settings-row-prepared">
    <div className="st-text">
      <h3 id="settings-prepared-name">Prepared data per build</h3>
      <p>What each opened build keeps besides its download, measured on disk when read. A retail build’s file list takes about 1.2 GB.</p>
      <dl className="st-effects">
        <dt>Free file list</dt><dd>Deletes that build’s file list, the ROOT and install catalog Files reads. It’s prepared again in the background the next time you open the build. A build still being prepared is stopped first.</dd>
        <dt>Kept</dt><dd>Table rows and downloaded game files. Builds that use the same files share them, so freeing one build never removes them.</dd>
      </dl>
      {listed && listed.builds.length > 0 && <dl className="st-facts"><dt>File lists</dt><dd>{readableBytes(total!)} across {exactCount(String(listed.builds.length))} {listed.builds.length === 1 ? 'build' : 'builds'}{list.phase === 'read' && list.refreshing && <span className="st-dim"> · measuring again…</span>}</dd></dl>}
      {listed?.truncated && <p className="st-tiny">Only the 256 most recently opened builds are listed.</p>}
    </div>
    <div className="st-ctl"/>
    {body}
  </div>;
}
