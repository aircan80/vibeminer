import { createContext, useContext, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import type { BridgeReply, DiscoveryBridge } from '../shared/bridge';
import type { SettingsSnapshot, StorageMeasurement } from '../shared/settings';
import { exactBytes, exactCount, readableBytes } from '../shared/inventory';
import { ActivityTrigger, useDownloadActivity } from './DownloadActivity';
import { summaryLine } from './download-activity-state';
import { PreparedDataSetting } from './PreparedData';
import brand from '../../../../branding/vibeminer.svg';
import './settings.css';

// Settings (docs/design/desktop-settings): one plain toolbar, a grouped list, an About column and a status bar.
// It replaces the window content; the screen it was opened from stays mounted and hidden, so its state comes back.
export type SettingsOrigin = Readonly<{ backLabel: string; returnsTo: string; kept: ReactNode }>;
type SettingsEntries = { open: (origin: SettingsOrigin, trigger: HTMLElement | null) => void; register: (element: HTMLElement, origin: () => SettingsOrigin) => () => void };
const SettingsContext = createContext<SettingsEntries | null>(null);
export const SettingsProvider = SettingsContext.Provider;
const gear = <svg viewBox="0 0 16 16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"><path d="M2 4h7M12 4h2M2 8h2M7 8h7M2 12h8M13 12h1"/><circle cx="10.5" cy="4" r="1.5"/><circle cx="5.5" cy="8" r="1.5"/><circle cx="11.5" cy="12" r="1.5"/></svg>;
/** The top-bar entry. It is always the last toolbar item; Ctrl+, opens the same view from the visible entry. */
export function SettingsButton({ className, origin }: { className: string; origin: SettingsOrigin }) {
  const entries = useContext(SettingsContext), element = useRef<HTMLButtonElement>(null), current = useRef(origin);
  current.current = origin;
  useLayoutEffect(() => element.current && entries ? entries.register(element.current, () => current.current) : undefined, [entries]);
  if (!entries) return null;
  return <button ref={element} className={`${className} settings-entry`} aria-keyshortcuts="Control+Comma" onClick={e => entries.open(current.current, e.currentTarget)}>{gear}Settings</button>;
}

const stamp = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const MEDIUM_REM = 78.4, COMPACT_REM = 65.333333;
type Problem = { code: string; message: string; retryable: boolean };
type Community = { phase: 'reading' } | { phase: 'readFailed'; error: Problem } | { phase: 'ready'; savedAt: string | null } | { phase: 'saving'; target: boolean } | { phase: 'failed'; target: boolean; error: Problem };
type Measure = { phase: 'measuring'; asked: boolean } | { phase: 'measured'; value: StorageMeasurement; asked: boolean } | { phase: 'failed'; error: Problem } | { phase: 'unavailable' };
const failure = <T,>(reply: BridgeReply<T>): Problem | null => reply.ok ? null : { code: reply.error.code, message: reply.error.message, retryable: reply.error.retryable };
const thrown = { code: 'ENGINE_UNAVAILABLE', message: 'The local engine is unavailable. Restart VibeMiner to reconnect.', retryable: true };

export function SettingsView({ bridge, origin, fixtureLabel, onBack, onPreparedDataChanged }: { bridge: DiscoveryBridge; origin: SettingsOrigin; fixtureLabel?: string | null; onBack: () => void; onPreparedDataChanged?: () => void }) {
  const ui = useDownloadActivity();
  const [snapshot, setSnapshot] = useState<SettingsSnapshot | null>(null);
  const [community, setCommunity] = useState<Community>({ phase: 'reading' });
  const [measure, setMeasure] = useState<Measure>(bridge.storageMeasure ? { phase: 'measuring', asked: false } : { phase: 'unavailable' });
  const [storageNote, setStorageNote] = useState('');
  const [size, setSize] = useState({ compact: false, medium: false });
  const refocus = useRef(false), alive = useRef(true), readGeneration = useRef(0), measuring = useRef<string | null>(null), measureGeneration = useRef(0);
  const host = useRef<HTMLDivElement>(null), heading = useRef<HTMLHeadingElement>(null), toggle = useRef<HTMLButtonElement>(null), copy = useRef<HTMLButtonElement>(null), toolbar = useRef<HTMLElement>(null), statusBar = useRef<HTMLElement>(null);
  async function read() {
    const generation = ++readGeneration.current;
    setCommunity({ phase: 'reading' });
    if (!bridge.settingsRead) { setCommunity({ phase: 'readFailed', error: { code: 'UNSUPPORTED', message: 'This VibeMiner engine does not save settings.', retryable: false } }); return; }
    let reply: BridgeReply<SettingsSnapshot>;
    try { reply = await bridge.settingsRead(); } catch { reply = { ok: false, error: thrown }; }
    if (!alive.current || generation !== readGeneration.current) return;
    if (reply.ok) { setSnapshot(reply.value); setCommunity({ phase: 'ready', savedAt: null }); }
    else setCommunity({ phase: 'readFailed', error: failure(reply)! });
  }
  async function measureNow(asked: boolean) {
    if (!bridge.storageMeasure) return;
    const generation = ++measureGeneration.current, id = `settings_storage_${Date.now().toString(36)}_${generation}`;
    measuring.current = id; setMeasure({ phase: 'measuring', asked });
    let reply: BridgeReply<StorageMeasurement>;
    try { reply = await bridge.storageMeasure(id); } catch { reply = { ok: false, error: thrown }; }
    if (measuring.current === id) measuring.current = null;
    if (!alive.current || generation !== measureGeneration.current) return;
    setMeasure(reply.ok ? { phase: 'measured', value: reply.value, asked } : { phase: 'failed', error: failure(reply)! });
  }
  useEffect(() => {
    alive.current = true;
    void read(); void measureNow(false);
    // Leaving Settings stops the walk; a late reply is dropped by the generation check.
    return () => { alive.current = false; const id = measuring.current; measuring.current = null; if (id) void bridge.cancel(id).catch(() => { /* The engine retires it on its own. */ }); };
  }, [bridge]);
  // Read again keeps focus on the page: the heading while reading, then the switch once it exists.
  function readAgain() { heading.current?.focus({ preventScroll: true }); refocus.current = true; void read(); }
  useLayoutEffect(() => { if (!refocus.current || community.phase === 'reading') return; refocus.current = false; (toggle.current ?? heading.current)?.focus({ preventScroll: true }); }, [community.phase]);
  // Opening Settings closes an open popover or the activity panel and focuses the heading.
  useLayoutEffect(() => { ui?.close(); heading.current?.focus({ preventScroll: true }); }, []);
  useEffect(() => {
    const keys = (event: KeyboardEvent) => { if (event.key === 'Escape' && !event.defaultPrevented && !ui?.expanded && !document.querySelector('dialog[open]')) { event.preventDefault(); onBack(); } };
    window.addEventListener('keydown', keys);
    return () => window.removeEventListener('keydown', keys);
  }, [onBack, ui?.expanded]);
  useLayoutEffect(() => {
    const measureWindow = () => { const node = host.current; if (!node) return; const root = parseFloat(getComputedStyle(document.documentElement).fontSize) || 15, width = node.clientWidth; if (width <= 0) return; const next = { compact: width < COMPACT_REM * root, medium: width < MEDIUM_REM * root }; setSize(old => old.compact === next.compact && old.medium === next.medium ? old : next); };
    const place = () => { const node = host.current; if (!node || !toolbar.current || !statusBar.current) return; node.style.setProperty('--st-panel-top', `${toolbar.current.getBoundingClientRect().bottom}px`); node.style.setProperty('--st-panel-bottom', `${statusBar.current.offsetHeight}px`); };
    const all = () => { measureWindow(); place(); };
    all();
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(all);
    for (const node of [host.current, toolbar.current, statusBar.current]) if (node) resize?.observe(node);
    const text = new MutationObserver(all); text.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class'] });
    window.addEventListener('resize', all);
    return () => { resize?.disconnect(); text.disconnect(); window.removeEventListener('resize', all); };
  }, []);
  async function change(target: boolean) {
    if (community.phase === 'saving' || !bridge.settingsSet || locked) return;
    setCommunity({ phase: 'saving', target });
    let reply: BridgeReply<SettingsSnapshot>;
    try { reply = await bridge.settingsSet({ name: 'communityBuildHistory', value: target }); } catch { reply = { ok: false, error: thrown }; }
    if (!alive.current) return;
    if (reply.ok) { setSnapshot(reply.value); setCommunity({ phase: 'ready', savedAt: reply.value.lastChangedAt }); }
    else setCommunity({ phase: 'failed', target, error: failure(reply)! });
  }
  async function storageAction(kind: 'copy' | 'show') {
    const action = kind === 'copy' ? bridge.storageCopyPath : bridge.storageShowFolder;
    if (!action) return;
    let reply: BridgeReply<unknown>;
    try { reply = await action(); } catch { reply = { ok: false, error: thrown }; }
    if (!alive.current) return;
    setStorageNote(reply.ok ? kind === 'copy' ? 'Path copied' : 'Opened in the file manager' : `${kind === 'copy' ? 'Couldn’t copy the path.' : 'Couldn’t open the folder.'} ${reply.error.message} (${reply.error.code})`);
  }

  const c = snapshot?.communityBuildHistory, known = community.phase !== 'reading' && community.phase !== 'readFailed' && !!c;
  const newer = snapshot?.file === 'newer', notRead = snapshot?.file === 'unreadable' && snapshot.problem?.code === 'SETTINGS_NOT_READ', locked = newer || notRead;
  const committed = c?.value ?? false, busy = community.phase === 'saving';
  const onOff = (value: boolean) => value ? 'On' : 'Off';
  let control: ReactNode, saveLine: ReactNode = '', extra: ReactNode = null, note: ReactNode = null;
  if (!known) {
    control = community.phase === 'reading'
      ? <><span className="st-loading" role="status"><span className="st-pulse" aria-hidden="true"/>Reading saved value…</span><span className="st-dflt">No switch until the saved value is known.</span></>
      : <span className="st-dflt">No switch until the saved value is known.</span>;
    if (community.phase === 'readFailed') extra = <div className="st-notice error" role="alert"><div className="st-text"><strong>Couldn’t read settings.</strong>{community.error.message} Nothing was changed. <code>({community.error.code})</code></div>{community.error.retryable && <button className="st-btn" onClick={readAgain}>Read again</button>}</div>;
  } else {
    const p = c!.problem;
    // A file from a newer VibeMiner, or one whose bytes were never read, is never replaced, so the switch can't change it.
    const lockReason = newer ? 'Can’t be changed here: saved by a newer VibeMiner.' : notRead ? p?.retryable ? 'Can’t be changed until the settings file is read.' : 'Can’t be changed: this version can’t read settings-v1.json.' : null;
    control = <><button ref={toggle} className={`st-switch${busy ? ' st-saving' : ''}`} role="switch" id="settings-community-switch" aria-checked={committed} aria-disabled={busy || locked || !bridge.settingsSet} aria-labelledby="settings-community-name" aria-describedby={`settings-community-desc settings-community-save${p ? ' settings-community-note' : ''}`} onClick={() => { if (!busy && !locked) void change(!committed); }}><span className="st-track" aria-hidden="true"/><span>{busy ? community.target ? 'Turning on…' : 'Turning off…' : onOff(committed)}</span></button><span className="st-dflt">Default: {onOff(c!.defaultValue)}{c!.source === 'unreadable' && ' · in use now'}</span></>;
    saveLine = busy ? <span className="st-busy"><span className="st-pulse" aria-hidden="true"/>Saving…</span> : lockReason ?? (community.phase === 'ready' && community.savedAt ? <><span className="st-ok">Saved</span> · {stamp(community.savedAt)}</> : '');
    if (community.phase === 'failed') {
      // Retry only what can work: a newer or unread file needs a fresh read, not the same change again.
      const reread = community.error.code === 'SETTINGS_VERSION' || community.error.code === 'SETTINGS_NOT_READ';
      extra = <div className="st-notice error" role="alert"><div className="st-text"><strong>Couldn’t save Community build history. It’s still {onOff(committed)}.</strong>{community.error.message} <code>({community.error.code})</code></div>{reread ? <button className="st-btn" onClick={readAgain}>Read again</button> : community.error.retryable && <button className="st-btn" onClick={() => { toggle.current?.focus({ preventScroll: true }); void change(community.target); }}>Retry turning {community.target ? 'on' : 'off'}</button>}</div>;
    }
    if (c!.source === 'unreadable' && p) note = <div className="st-state warn" role="note" id="settings-community-note">
      <strong>{newer ? `Saved by a newer VibeMiner. Using the default, ${onOff(c!.defaultValue)}.` : notRead ? `Settings file not read. Using the default, ${onOff(c!.defaultValue)}.` : `Saved value couldn’t be read. Using the default, ${onOff(c!.defaultValue)}.`}</strong>
      <p>{newer ? `${p.message} This version uses the default and doesn’t change the file. Change this setting in the newer VibeMiner.` : notRead ? p.retryable ? `${p.message} The file wasn’t changed, and the switch can’t be used until the file is read.` : `${p.message} This version of VibeMiner can’t read settings-v1.json in the storage folder, so it wasn’t changed. Fix or remove that file, then open Settings again.` : snapshot!.file === 'unreadable' ? `${p.message} Community build history uses its default, so nothing is fetched from the community source. Switching saves a new value.` : 'The settings file has a value for this setting that VibeMiner doesn’t understand, so it is ignored and nothing is fetched from the community source. Switching saves a new value.'} <code>({p.code})</code></p>
      {notRead && p.retryable && <button className="st-btn st-note-action" onClick={readAgain}>Read again</button>}
    </div>;
  }
  const limits = snapshot?.downloadLimits;
  const sizeFact = measure.phase === 'measuring' ? <span className="st-loading"><span className="st-pulse" aria-hidden="true"/>Measuring…</span>
    : measure.phase === 'measured' ? <>{readableBytes(measure.value.bytes)} <span className="st-dim st-mono">· {exactBytes(measure.value.bytes)}</span>{measure.value.skipped !== '0' && <span className="st-skipped">{exactCount(measure.value.skipped)} {measure.value.skipped === '1' ? 'item' : 'items'} couldn’t be read and {measure.value.skipped === '1' ? 'isn’t' : 'aren’t'} counted.</span>}</>
    : measure.phase === 'failed' ? <span className="st-measure-failed">Not measured. {measure.error.message} <code>({measure.error.code})</code> <button className="st-btn small" onClick={() => { copy.current?.focus({ preventScroll: true }); void measureNow(true); }}>Measure again</button></span>
    : <span className="st-dim">Not measured: this VibeMiner engine can’t measure the folder.</span>;
  const measuredFact = measure.phase === 'measured' ? `${stamp(measure.value.measuredAt)} · ${measure.asked ? 'when you asked' : 'when Settings opened'}` : null;
  const reading = community.phase === 'reading', aboutBox = reading
    ? <div className="st-state plain" role="status"><strong>Reading settings…</strong><p>Values show once the engine has read them. Defaults are not shown as if they were saved.</p></div>
    : community.phase === 'readFailed' ? <div className="st-state warn"><strong>Settings not read</strong><p>The values aren’t known, so no switch is shown.</p></div>
    : newer ? <div className="st-state warn"><strong>Settings saved by a newer VibeMiner</strong><p>This version uses the defaults and doesn’t change the file.</p></div>
    : notRead ? <div className="st-state warn"><strong>Settings file not read</strong><p>Community build history uses its default. The file wasn’t changed.</p></div>
    : c?.source === 'unreadable' ? <div className="st-state warn"><strong>1 setting is using its default</strong><p>{snapshot!.file === 'read' ? 'Community build history’s saved value couldn’t be read. The other settings were read normally.' : 'The settings file isn’t one VibeMiner understands, so Community build history uses its default.'}</p></div> : null;
  const lastChange = reading ? <span className="st-dim">Not read yet</span> : community.phase === 'readFailed' || notRead ? <span className="st-dim">Not known</span> : snapshot?.lastChangedAt ? stamp(snapshot.lastChangedAt) : <span className="st-dim">{snapshot?.file === 'absent' ? 'Never changed' : 'Not recorded'}</span>;
  const summary = ui && ui.entry && !ui.expanded ? ui.summary : null;
  return <div ref={host} className={`settings-screen${size.compact ? ' st-compact st-medium' : size.medium ? ' st-medium' : ''}`}>
    <a className="st-skip" href="#settings-list">Skip to settings</a>
    <div className="st-window">
      <header className="st-toolbar" ref={toolbar}>
        <div className="st-brand"><img src={brand} alt="VibeMiner"/><span className="st-word" aria-hidden="true">Vibe<span>Miner</span></span></div>
        <div className="st-divider" aria-hidden="true"/>
        <button className="st-btn" onClick={onBack}>← {origin.backLabel}</button>
        <div className="st-spacer"/>
        <ActivityTrigger compact slotId="settings-activity-slot"/>
      </header>
      <main className="st-main" aria-labelledby="settings-title">
        <div className="st-pane-head"><h1 id="settings-title" ref={heading} tabIndex={-1}>Settings</h1><span className="st-sub">For every product and build. A change saves as soon as you make it.</span></div>
        <div className="st-list" id="settings-list" tabIndex={-1}>
          <section aria-labelledby="settings-group-builds"><h2 className="st-label st-group-h" id="settings-group-builds">Builds</h2>
            <div className="st-setting" id="settings-row-community">
              <div className="st-text">
                <h3 id="settings-community-name">Community build history <span className="st-opt">(optional)</span></h3>
                <p id="settings-community-desc">Blizzard’s version service only lists current builds. This adds older builds from one community-kept build list, so you can open builds VibeMiner never saw itself.</p>
                <dl className="st-effects">
                  <dt>When on</dt><dd>At each <strong>Refresh builds</strong>, Discovery also reads the build list from the community source. Only the list comes from there: product, version, build number and config hashes. All data still comes from Blizzard’s CDN and is checked against its hash, so a wrong entry can only fail to open. These builds get a <span className="st-pill community">Community</span> badge.</dd>
                  <dt>When off</dt><dd>Discovery lists only builds seen on Blizzard’s version service, and VibeMiner doesn’t contact the community source. Community builds you haven’t opened are hidden; ones you opened stay, with their badge.</dd>
                  <dt>Takes effect</dt><dd>At the next Refresh builds. Nothing is fetched when you switch.</dd>
                  <dt>This version</dt><dd>No community source is connected yet, so nothing changes until one is added.{!locked && ' Your choice is saved now.'}</dd>
                </dl>
              </div>
              <div className="st-ctl">{control}<span className="st-save-line" id="settings-community-save" role="status">{saveLine}</span></div>
              {extra}{note}
            </div>
          </section>
          <section aria-labelledby="settings-group-storage"><h2 className="st-label st-group-h" id="settings-group-storage">Storage</h2>
            <div className="st-setting" id="settings-row-storage">
              <div className="st-text">
                <h3 id="settings-storage-name">Storage location</h3>
                <p>Where VibeMiner keeps everything it downloads, its build database and these settings.</p>
                <dl className="st-facts">
                  <dt>Folder</dt><dd>{snapshot ? <span className="st-path st-selectable" id="settings-storage-path">{snapshot.storage.dataDirectory}</span> : <span className="st-dim">{reading ? 'Reading…' : 'Not known'}</span>}</dd>
                  <dt>Size on disk</dt><dd>{sizeFact}</dd>
                  <dt>Measured</dt><dd>{measuredFact ?? <span className="st-dim">Not measured yet</span>}</dd>
                </dl>
                <dl className="st-effects"><dt>Changes</dt><dd>The folder can’t be moved yet. The size is the whole folder, every build together, not a build’s download size from Home. A build’s file list can be freed below.</dd></dl>
              </div>
              <div className="st-ctl"><div className="st-ctl-row"><button ref={copy} className="st-btn" aria-describedby="settings-storage-name" disabled={!bridge.storageCopyPath} onClick={() => { void storageAction('copy'); }}>Copy path</button><button className="st-btn" disabled={!bridge.storageShowFolder} onClick={() => { void storageAction('show'); }}>Show in folder</button></div><span className="st-save-line" id="settings-storage-line" role="status">{storageNote}</span></div>
            </div>
            <PreparedDataSetting bridge={bridge} onFreed={() => { onPreparedDataChanged?.(); if (alive.current) void measureNow(true); }}/>
          </section>
          <section aria-labelledby="settings-group-downloads"><h2 className="st-label st-group-h" id="settings-group-downloads">Downloads</h2>
            <div className="st-setting" id="settings-row-limits">
              <div className="st-text">
                <h3 id="settings-limits-name">Download limits</h3>
                <p>How much VibeMiner downloads from Blizzard’s CDN at once. What you’re looking at always goes first; Download all uses what’s left.</p>
                <dl className="st-facts"><dt>Parallel downloads</dt><dd>{limits ? `Up to ${limits.parallelDownloads} at once, at most ${limits.backgroundDownloads} of them for background downloads` : <span className="st-dim">{reading ? 'Reading…' : 'Not known'}</span>}</dd><dt>Bandwidth cap</dt><dd>{limits ? limits.bandwidthCapBytesPerSecond === null ? 'None' : `${readableBytes(limits.bandwidthCapBytesPerSecond)}/s` : <span className="st-dim">{reading ? 'Reading…' : 'Not known'}</span>}</dd></dl>
                <dl className="st-effects"><dt>Changes</dt><dd>Fixed in this version.</dd></dl>
              </div>
              <div className="st-ctl"><span className="st-pill plain">Fixed for now</span></div>
            </div>
          </section>
        </div>
        <aside className="st-side" aria-labelledby="settings-about" inert={!!ui?.expanded && !size.compact}>
          <div className="st-panel-head"><h2 id="settings-about">About settings</h2></div>
          <div className="st-side-body">{aboutBox}
            <dl className="st-facts"><dt>Saved by</dt><dd>The VibeMiner engine</dd><dt>Applies to</dt><dd>Every product and build</dd><dt>Saved in</dt><dd>The storage folder, settings version {snapshot?.formatVersion ?? 1}</dd><dt>Last change</dt><dd>{lastChange}</dd></dl>
            <p className="st-tiny">Changing a setting never deletes builds or downloaded files, and never pauses or cancels work in Downloads &amp; activity.</p>
            <p className="st-tiny">Escape or <strong>← {origin.backLabel}</strong> returns to {origin.returnsTo} as you left it.</p>
          </div>
        </aside>
      </main>
      <footer className="st-statusbar" ref={statusBar}>
        <span><strong>Settings</strong> · saved by the engine</span>
        <span className="st-secondary">{origin.kept}</span>
        {fixtureLabel && <span className="st-fixture"><strong>{fixtureLabel}</strong><span className="st-secondary"> · Synthetic identities and receipts.</span></span>}
        <span className="st-spacer"/>
        {summary && <span className={ui!.attention ? 'st-warn' : 'st-busy'} aria-live="off">{!ui!.attention && ui!.active && <span className="st-pulse" aria-hidden="true"/>}{summaryLine(summary)}</span>}
      </footer>
    </div>
  </div>;
}
