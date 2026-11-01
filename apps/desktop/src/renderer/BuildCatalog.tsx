import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type MutableRefObject } from 'react';
import './discovery.css';
import './build-catalog.css';
import type { BuildCatalog as Catalog } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import brand from '../../../../branding/vibeminer.svg';
import { ActivityTrigger, useDownloadActivity } from './DownloadActivity';
import { summaryLine } from './download-activity-state';
import { SettingsButton, type SettingsOrigin } from './Settings';
import { useTruncationTitles } from './status-titles';
import { BUSY_TEXT, OPEN_REASON, SWITCHED_TEXT, checkedText, catalogEmpty, catalogKey, catalogRows, catalogTime, lastChecked, productLine, progressText, refreshNotice, regionText, summaryText, type RefreshNotice } from './build-catalog-view';
const origin: SettingsOrigin = { backLabel: 'Back to Discovery', returnsTo: 'Discovery', kept: 'Discovery kept as you left it' };
/**
 * What the list keeps for the session while the old list is shown instead (#205): the list last read, the selection and
 * the last refresh outcome. App owns it, so switching back shows them again.
 */
export type CatalogSession = { catalog: Catalog | null; selectedKey: string | null; notice: RefreshNotice | null; storeBusy: boolean; refreshError: string; message: string };
/**
 * Stacked windows shorter than this many rem share one scroller. The old list switches at 25rem; this list also has product
 * headings and the summary line above its rows, so a stacked pane at 1280x800 with 200% text (26.7rem) would show no row.
 */
const SHARED_HEIGHT = 30;
const optionId = (key: string) => `catalog-build-${key.replace(':', '-')}`;
/**
 * Discovery's build list from the layer 3 catalog (#205, build-catalog-v1): every reviewed product with its stored builds,
 * Refresh through the version service, and an inspector. Opening a build on the new storage is not there yet, so Open is
 * disabled with its reason; "Old build list" switches this session to the old Discovery list, which still opens builds.
 */
export function BuildCatalog({ bridge, active, fixtureLabel, onOldList, focusSwitch = false, session }: { bridge: DiscoveryBridge; active: boolean; fixtureLabel?: string; onOldList: () => void; focusSwitch?: boolean; session?: MutableRefObject<CatalogSession | null> }) {
  const kept = session?.current ?? null;
  const [catalog, setCatalog] = useState<Catalog | null>(kept?.catalog ?? null);
  const [reading, setReading] = useState(true);
  const [listError, setListError] = useState('');
  const [selectedKey, setSelectedKey] = useState<string | null>(kept?.selectedKey ?? null);
  const [busy, setBusy] = useState(false);
  const [stopping, setStopping] = useState(false);
  const [progress, setProgress] = useState<{ completed: number; total: number } | null>(null);
  const [notice, setNotice] = useState<RefreshNotice | null>(kept?.notice ?? null);
  const [storeBusy, setStoreBusy] = useState(kept?.storeBusy ?? false);
  const [refreshError, setRefreshError] = useState(kept?.refreshError ?? '');
  const [message, setMessage] = useState(kept?.message ?? 'Not refreshed this session.');
  // Each read or refresh takes a new generation; a reply from an older one is dropped.
  const generation = useRef(0), activeId = useRef<string | null>(null), disposed = useRef(false);
  const switchButton = useRef<HTMLButtonElement>(null), refreshButton = useRef<HTMLButtonElement>(null);
  const activity = useDownloadActivity();
  async function read() {
    const mine = ++generation.current;
    setReading(true);
    try {
      const reply = await bridge.catalogList!();
      if (disposed.current || mine !== generation.current) return;
      if (reply.ok) { setCatalog(reply.value); setListError(''); setStoreBusy(reply.value.access === 'readOnly'); }
      else setListError(`${reply.error.message} (${reply.error.code})`);
    } catch { if (!disposed.current && mine === generation.current) setListError('The local engine is unavailable. Restart VibeMiner to reconnect.'); }
    finally { if (!disposed.current && mine === generation.current) setReading(false); }
  }
  async function refresh() {
    if (activeId.current) return;
    const id = crypto.randomUUID(); activeId.current = id;
    const mine = ++generation.current;
    setReading(false); setBusy(true); setStopping(false); setProgress(null); setRefreshError(''); setMessage('Checking the version service for every product…');
    let reread = false;
    try {
      const reply = await bridge.catalogRefresh!(id);
      if (disposed.current || activeId.current !== id || mine !== generation.current) return;
      if (reply.ok) {
        const result = reply.value, next = refreshNotice(result);
        setCatalog(result.catalog); setListError(''); setStoreBusy(false); setNotice(next);
        setMessage(next?.kind === 'offline' ? 'Refresh could not reach the version service.' : next ? `Refresh finished with problems. Checked ${checkedText(result.checked)}.` : `Refresh complete. Checked ${checkedText(result.checked)}.`);
      } else if (reply.error.code === 'CANCELED') { setMessage(reply.error.message); reread = true; }
      else if (reply.error.code === 'OBJECT_STORE_BUSY') { setStoreBusy(true); setNotice(null); setMessage('Refresh not started: the build store is in use.'); }
      else { setRefreshError(`${reply.error.message} (${reply.error.code})`); setMessage('Refresh could not complete. The stored list is still shown.'); reread = true; }
    } catch { if (!disposed.current && activeId.current === id) { setRefreshError('The local engine is unavailable. Restart VibeMiner to reconnect.'); setMessage('Refresh could not complete. The stored list is still shown.'); } }
    finally { if (!disposed.current && activeId.current === id) { activeId.current = null; setBusy(false); setStopping(false); setProgress(null); } }
    // Products checked before a stop or failure kept their results; show what is stored now.
    if (reread && !disposed.current) void read();
  }
  async function stop() {
    if (!activeId.current || stopping) return;
    setStopping(true); setMessage('Stopping refresh…');
    try { const reply = await bridge.cancel(activeId.current); if (!disposed.current && !reply.ok) setRefreshError(`${reply.error.message} (${reply.error.code}). Waiting for the refresh to stop.`); }
    catch { if (!disposed.current) setRefreshError('The stop could not be delivered. Waiting for the local engine.'); }
  }
  useEffect(() => {
    disposed.current = false;
    const off = bridge.onProgress(update => { if (update.requestId === activeId.current && !disposed.current && update.unit === 'products' && update.completed !== null && update.total !== null) setProgress({ completed: update.completed, total: update.total }); });
    void read();
    return () => {
      disposed.current = true; ++generation.current; off();
      // Leaving the list (Old build list) stops a running refresh; coming back says so, and the list read then shows what it kept.
      if (activeId.current) { void bridge.cancel(activeId.current).catch(() => {}); if (session?.current) session.current = { ...session.current, message: SWITCHED_TEXT }; }
    };
  }, []);
  // Switching lists this session puts focus on the control that switches back.
  useEffect(() => { if (focusSwitch) switchButton.current?.focus(); }, []);
  useEffect(() => {
    if (!active) return;
    // F5 starts a refresh when none runs; it never stops one.
    const keys = (event: globalThis.KeyboardEvent) => { if (event.key === 'F5') { event.preventDefault(); if (!activeId.current) void refresh(); } };
    window.addEventListener('keydown', keys);
    return () => window.removeEventListener('keydown', keys);
  }, [active]);
  // The session copy follows every change, so a switch to the old list keeps the latest state.
  useEffect(() => { if (session && !disposed.current) session.current = { catalog, selectedKey, notice, storeBusy, refreshError, message: busy ? 'Not refreshed this session.' : message }; });
  const rows = catalogRows(catalog);
  const index = rows.findIndex(row => row.key === selectedKey);
  const activeIndex = Math.max(0, index);
  const selected = index >= 0 ? rows[index] : null;
  const empty = !!catalog && catalogEmpty(catalog);
  function select(key: string) { setSelectedKey(key); }
  function listKey(event: KeyboardEvent<HTMLDivElement>) {
    if (!rows.length) return;
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); select(rows[activeIndex].key); return; }
    const target = { ArrowDown: index + 1, ArrowUp: Math.max(0, index - 1), Home: 0, End: rows.length - 1, PageDown: index + 10, PageUp: Math.max(0, index - 10) }[event.key];
    if (target === undefined) return;
    event.preventDefault();
    const next = rows[Math.max(0, Math.min(rows.length - 1, target))];
    select(next.key);
    document.getElementById(optionId(next.key))?.scrollIntoView?.({ block: 'nearest' });
  }
  // Shared area: the inspector's top edge goes to the top of the area and its heading takes focus; Return goes back to the row.
  function viewSelected() {
    const area = windowRef.current?.querySelector<HTMLElement>('.dv-body'), inspector = document.getElementById('inspector'), heading = document.getElementById('inspector-heading');
    if (area && inspector) area.scrollTop += inspector.getBoundingClientRect().top - area.getBoundingClientRect().top - area.clientTop;
    heading?.focus({ preventScroll: true });
  }
  function returnToList() {
    const row = selectedKey ? document.getElementById(optionId(selectedKey)) : null, list = document.getElementById('builds');
    if (list) { (row ?? list).scrollIntoView({ block: 'nearest' }); list.focus({ preventScroll: true }); } else { const heading = document.getElementById('builds-heading'); heading?.scrollIntoView({ block: 'start' }); heading?.focus({ preventScroll: true }); }
  }
  const windowRef = useRef<HTMLDivElement>(null), toolbarRef = useRef<HTMLElement>(null), statusRef = useRef<HTMLElement>(null);
  useTruncationTitles(statusRef);
  // The old list's text-relative breakpoints: stacked below 65.333333rem wide, two-line rows below 28rem. Stacked and shorter than
  // SHARED_HEIGHT, the list and the inspector take their natural height in one scrolling area with jumps between them (#52).
  const [compact, setCompact] = useState(false), [narrow, setNarrow] = useState(false), [shared, setShared] = useState(false);
  useLayoutEffect(() => {
    const measure = () => {
      const host = windowRef.current; if (!host) return;
      const rem = parseFloat(getComputedStyle(document.documentElement).fontSize), stacked = host.clientWidth < 65.333333 * rem, together = stacked && host.clientHeight < SHARED_HEIGHT * rem;
      // The jumps exist only in the shared area. If one of them has focus as it goes, focus moves to what it pointed at.
      if (!together) { const focused = document.activeElement; if (focused?.matches('.dv-jump')) document.getElementById('inspector-heading')?.focus({ preventScroll: true }); else if (focused?.matches('.dv-return')) (document.getElementById('builds') ?? document.getElementById('builds-heading'))?.focus({ preventScroll: true }); }
      setCompact(stacked); setNarrow(host.clientWidth < 28 * rem); setShared(together);
    };
    measure();
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    if (windowRef.current) resize?.observe(windowRef.current);
    const root = new MutationObserver(measure);
    root.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class'] });
    window.addEventListener('resize', measure);
    return () => { resize?.disconnect(); root.disconnect(); window.removeEventListener('resize', measure); };
  }, []);
  useLayoutEffect(() => {
    const place = () => { const host = windowRef.current; if (!host || !toolbarRef.current || !statusRef.current) return; host.style.setProperty('--dv-panel-top', `${toolbarRef.current.getBoundingClientRect().bottom}px`); host.style.setProperty('--dv-panel-bottom', `${statusRef.current.offsetHeight}px`); };
    place();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(place);
    if (toolbarRef.current) observer?.observe(toolbarRef.current);
    if (statusRef.current) observer?.observe(statusRef.current);
    return () => observer?.disconnect();
  }, []);
  const checked = lastChecked(catalog);
  const summary = activity && activity.entry && !activity.expanded ? activity.summary : null;
  const showInspector = !!catalog && !empty;
  const newProducts = !!catalog?.summary?.newProducts.length;
  const notices = <>
    {listError && <div role="alert" className="dv-notice error"><div><strong>Build list unavailable</strong>{listError} {catalog ? 'The list last read is still shown.' : ''}</div>{!reading && !busy && <button className="dv-btn" onClick={() => void read()}>Read again</button>}</div>}
    {storeBusy && <div role="status" className="dv-notice"><div><strong>Build store in use</strong>{BUSY_TEXT} The stored list is shown.</div></div>}
    {refreshError && <div role="alert" className="dv-notice error"><div><strong>Refresh problem</strong>{refreshError}</div></div>}
    {notice?.kind === 'offline' && <div role="status" className="dv-notice error"><div><strong>Refresh unavailable</strong>{notice.text}</div></div>}
    {notice?.kind === 'partial' && <div role="status" className="dv-notice"><div><strong>Refresh incomplete</strong>{notice.text}<ul>{notice.items.map(item => <li key={item}>{item}</li>)}</ul></div></div>}
    {catalog && !empty && <p className={`dv-catalog-summary${newProducts ? ' new' : ''}`}>{summaryText(catalog.summary)}</p>}
  </>;
  const hasNotices = !!(listError || storeBusy || refreshError || notice || catalog && !empty);
  const refreshLabel = busy ? stopping ? 'Stopping refresh…' : 'Stop refresh' : 'Refresh builds';
  return <div className={`dv-window dv-catalog${compact ? ' dv-compact' : ''}${compact && narrow ? ' dv-narrow' : ''}${shared ? ' dv-shared' : ''}`} ref={windowRef}>
    <header className="dv-toolbar" ref={toolbarRef}>
      <div className="dv-brand"><img src={brand} alt="VibeMiner"/><span className="dv-word" aria-hidden="true">Vibe<span>Miner</span></span></div>
      {fixtureLabel && <span className="dv-fixture"><strong>{fixtureLabel}</strong><span> · Synthetic identities and receipts.</span></span>}
      <div className="dv-spacer"/>
      <button ref={switchButton} className="dv-btn" onClick={onOldList}>Old build list</button>
      <ActivityTrigger compact/>
      <SettingsButton className="dv-btn" origin={origin}/>
    </header>
    <main className={`dv-body${showInspector ? '' : ' no-inspector'}`} id="discovery">
      <section className="dv-pane" aria-labelledby="builds-heading">
        <div className="dv-pane-head">
          <h1 id="builds-heading" tabIndex={-1}>Builds</h1>{rows.length > 0 && <span className="dv-n" aria-label={`${rows.length} builds shown`}>{rows.length}</span>}
          <button ref={refreshButton} className="dv-btn dv-refresh" aria-keyshortcuts="F5" aria-disabled={stopping} onClick={() => { if (busy) void stop(); else void refresh(); }}>{!stopping && <span aria-hidden="true">{busy ? '✕' : '↻'}</span>}{refreshLabel}</button>
          {/* Its slot stays reserved in the shared area, so the first selection doesn't move the rows under the pointer. */}
          {shared && showInspector && <button className={`dv-btn dv-jump${selected ? '' : ' dv-reserved'}`} onClick={viewSelected} disabled={!selected} aria-hidden={!selected || undefined}>View selected build</button>}
        </div>
        {notices}
        <div className={`dv-list${hasNotices ? '' : ' flush'}`} aria-busy={reading}>
          {rows.length > 0 && catalog && <>
            <div className="dv-cols dv-thead" aria-hidden="true"><span>Version</span><span>Regions</span><span>Last seen</span></div>
            <div role="listbox" id="builds" aria-label="Builds" tabIndex={0} aria-activedescendant={optionId(rows[activeIndex].key)} onKeyDown={listKey}>
              {catalog.products.map(product => <div role="group" key={product.productId} aria-labelledby={`catalog-group-${product.productId}`} className="dv-group">
                <div className="dv-group-head" id={`catalog-group-${product.productId}`}><strong>{product.label}</strong> <code>{product.productId}</code><span className="dv-dim"> · {productLine(product)}{product.problemCount ? ` · ${product.problemCount === 1 ? '1 catalog problem' : `${product.problemCount} catalog problems`}` : ''}</span></div>
                {product.builds.map(build => { const key = catalogKey(product.productId, build.id); return <div role="option" key={key} id={optionId(key)} data-build-id={build.id} data-product-id={product.productId} className={`dv-cols build-row${rows[activeIndex].key === key ? ' active' : ''}${build.current ? ' current' : ''}`} aria-selected={selectedKey === key} onClick={() => select(key)}>
                  <span className="dv-version">{build.versionsName}{build.current ? <span className="dv-pill current">Current</span> : <span className="visually-hidden">, older</span>}<span className="visually-hidden">, {product.label}</span></span>
                  <span><span className="visually-hidden">Regions </span>{regionText(build.regions)}</span>
                  <span className="dv-dim"><span className="visually-hidden">Last seen </span>{catalogTime(build.lastSeenAt)}</span>
                </div>; })}
              </div>)}
            </div>
          </>}
          {!rows.length && <div className="dv-empty"><div>{
            !catalog ? reading ? <h2>Reading stored builds…</h2> : <><h2>Build list not read</h2><p>The stored build list couldn't be read. See the notice above.</p></>
            : busy ? <h2>Checking the version service…</h2>
            : empty ? <><h2>No builds stored yet</h2><p>Refresh asks the version service.</p></>
            : <><h2>No builds listed</h2><p>The stored history lists no build for any product.</p></>}</div></div>}
        </div>
      </section>
      {showInspector && <aside className="dv-inspector" id="inspector" aria-labelledby="inspector-heading" inert={!!activity?.expanded}>{selected ? <>
        <div className="dv-insp-head"><div className="dv-label">Selected build</div><h2 id="inspector-heading" className="dv-selectable" tabIndex={-1}>{selected.build.versionsName}</h2><p className="inspector-product">World of Warcraft · {selected.product.label} · <code>{selected.product.productId}</code></p>{shared && <button className="dv-btn dv-return" onClick={returnToList}>Return to build list</button>}</div>
        <div className="dv-insp-body">
          <div className="dv-state"><strong>{selected.build.current ? 'Current' : 'Older build'}</strong>{selected.build.current ? 'Listed by the version service at the latest check.' : 'Not listed by the version service at the latest check.'}</div>
          <div className="dv-label">Identity</div>
          <dl><dt>Version</dt><dd className="dv-selectable">{selected.build.versionsName}</dd>
            <dt>Product</dt><dd><code>{selected.product.productId}</code> · {selected.product.label}</dd>
            <dt>Build config</dt><dd className="mono dv-selectable">{selected.build.buildConfig}</dd>
            <dt>Identity</dt><dd className="mono dv-selectable">{selected.build.id}</dd></dl>
          <div className="dv-label">Observation</div>
          <dl><dt>Current</dt><dd>{selected.build.current ? 'Yes' : 'No'}</dd>
            <dt>Regions</dt><dd>{regionText(selected.build.regions)}{selected.build.current && <span className="dv-route-note">Regions whose listing is current.</span>}</dd>
            <dt>First seen</dt><dd>{catalogTime(selected.build.firstSeenAt)}</dd>
            <dt>Last seen</dt><dd>{catalogTime(selected.build.lastSeenAt)}</dd></dl>
          {selected.product.problemCount > 0 && <><div className="dv-label">Catalog problems · {selected.product.productId}</div><ul className="dv-catalog-problems">{selected.product.problems.map((p, i) => <li key={i}>{p.message} <code>({p.code})</code></li>)}</ul>{selected.product.problemCount > selected.product.problems.length && <p className="dv-note">{selected.product.problemCount - selected.product.problems.length} more not shown.</p>}</>}
        </div>
        <div className="dv-insp-foot blocked"><button className="dv-btn primary" disabled aria-describedby="catalog-open-reason">Open build</button><p className="dv-reason" id="catalog-open-reason">{OPEN_REASON}</p></div>
      </> : <><h2 id="inspector-heading" className="visually-hidden">Build inspector</h2><div className="dv-insp-empty"><div><strong>No build selected</strong><p>Select a build to see its identity.</p></div></div><div className="dv-insp-foot blocked"><button className="dv-btn primary" disabled aria-describedby="catalog-open-reason">Open build</button><p className="dv-reason" id="catalog-open-reason">{OPEN_REASON}</p></div></>}</aside>}
    </main>
    <footer className="dv-statusbar" ref={statusRef}>
      {busy && <span className="dv-busy"><span className="dv-pulse" aria-hidden="true"/>{!stopping && <span aria-live="off">{progressText(progress)}</span>}</span>}
      {checked ? <span className="dv-latest">Latest check <strong>{catalogTime(checked)}</strong></span> : <span className="dv-latest">Not checked yet</span>}
      <span role="status" aria-live="polite" className="dv-message">{stopping ? 'Stopping refresh…' : message}</span>
      {summary && <span className={`dv-status-end ${activity!.attention ? 'dv-warn' : 'dv-busy'}`} aria-live="off">{!activity!.attention && activity!.active && <span className="dv-pulse" aria-hidden="true"/>}{summaryLine(summary)}</span>}
    </footer>
  </div>;
}
