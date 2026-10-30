import { createContext, useContext, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import type { BridgeReply, DiscoveryBridge } from '../shared/bridge';
import { SETTING_SWITCHES, type CommunityListfileStatus, type SettingSwitch, type SettingsSnapshot, type StorageMeasurement } from '../shared/settings';
import { exactBytes, exactCount, readableBytes } from '../shared/inventory';
import { ActivityTrigger, useDownloadActivity } from './DownloadActivity';
import { summaryLine } from './download-activity-state';
import { PreparedDataSetting } from './PreparedData';
import { listfileMeasurement, listfileStage } from './useCommunityListfile';
import brand from '../../../../branding/vibeminer.svg';
import './settings.css';

// Settings (docs/design/desktop-settings): one plain toolbar, a grouped list, an About column and a status bar.
// It replaces the window content; the screen it was opened from stays mounted and hidden, so its state comes back.
export type SettingsOrigin = Readonly<{ backLabel: string; returnsTo: string; kept: ReactNode }>;
type SettingsEntries = { open: (origin: SettingsOrigin, trigger: HTMLElement | null) => void; register: (element: HTMLElement, origin: () => SettingsOrigin) => () => void; openHere?: (trigger: HTMLElement) => void };
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

/** Opens Settings from inside a screen, for a next step that lives there. Hidden where Settings isn't available. */
export function OpenSettingsButton({ className, children }: { className: string; children: ReactNode }) {
  const entries = useContext(SettingsContext);
  if (!entries?.openHere) return null;
  return <button className={className} onClick={event => entries.openHere!(event.currentTarget)}>{children}</button>;
}

const stamp = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const MEDIUM_REM = 78.4, COMPACT_REM = 65.333333;
type Problem = { code: string; message: string; retryable: boolean };
type Reading = { phase: 'reading' } | { phase: 'readFailed'; error: Problem } | { phase: 'ready' };
type Change = { phase: 'idle'; savedAt: string | null } | { phase: 'saving'; target: boolean } | { phase: 'failed'; target: boolean; error: Problem };
type Measure = { phase: 'measuring'; asked: boolean } | { phase: 'measured'; value: StorageMeasurement; asked: boolean } | { phase: 'failed'; error: Problem } | { phase: 'unavailable' };
const failure = <T,>(reply: BridgeReply<T>): Problem | null => reply.ok ? null : { code: reply.error.code, message: reply.error.message, retryable: reply.error.retryable };
const thrown = { code: 'ENGINE_UNAVAILABLE', message: 'The local engine is unavailable. Restart VibeMiner to reconnect.', retryable: true };
const unchanged = (): Record<SettingSwitch, Change> => ({ communityBuildHistory: { phase: 'idle', savedAt: null }, fetchTableDefinitions: { phase: 'idle', savedAt: null }, communityListfile: { phase: 'idle', savedAt: null } });
// Each switch's id stem, visible name and what its default means when a saved value can't be used.
const SWITCH: Record<SettingSwitch, { key: string; title: string; byDefault: string }> = {
  communityBuildHistory: { key: 'community', title: 'Community build history', byDefault: 'Discovery shows no community builds and nothing is fetched from wago.tools' },
  fetchTableDefinitions: { key: 'fetch', title: 'Fetch newer table definitions', byDefault: 'no table definitions are fetched from WoWDBDefs' },
  communityListfile: { key: 'listfile', title: 'Community file names', byDefault: 'Files shows no community names and nothing is downloaded from wow-listfile' },
};

export function SettingsView({ bridge, origin, fixtureLabel, onBack, onPreparedDataChanged }: { bridge: DiscoveryBridge; origin: SettingsOrigin; fixtureLabel?: string | null; onBack: () => void; onPreparedDataChanged?: () => void }) {
  const ui = useDownloadActivity();
  const [snapshot, setSnapshot] = useState<SettingsSnapshot | null>(null);
  const [reading, setReading] = useState<Reading>({ phase: 'reading' });
  const [changes, setChanges] = useState<Record<SettingSwitch, Change>>(unchanged);
  const [measure, setMeasure] = useState<Measure>(bridge.storageMeasure ? { phase: 'measuring', asked: false } : { phase: 'unavailable' });
  const [storageNote, setStorageNote] = useState('');
  const [size, setSize] = useState({ compact: false, medium: false });
  const refocus = useRef<SettingSwitch | null>(null), alive = useRef(true), readGeneration = useRef(0), measuring = useRef<string | null>(null), measureGeneration = useRef(0);
  // Saves can overlap across switches; only the newest committed snapshot is shown.
  const saveGeneration = useRef(0), shownSave = useRef(0);
  const toggles = useRef<Record<SettingSwitch, HTMLButtonElement | null>>({ communityBuildHistory: null, fetchTableDefinitions: null, communityListfile: null });
  const host = useRef<HTMLDivElement>(null), heading = useRef<HTMLHeadingElement>(null), copy = useRef<HTMLButtonElement>(null), toolbar = useRef<HTMLElement>(null), statusBar = useRef<HTMLElement>(null);
  async function read() {
    const generation = ++readGeneration.current;
    setReading({ phase: 'reading' });
    if (!bridge.settingsRead) { setReading({ phase: 'readFailed', error: { code: 'UNSUPPORTED', message: 'This VibeMiner engine does not save settings.', retryable: false } }); return; }
    let reply: BridgeReply<SettingsSnapshot>;
    try { reply = await bridge.settingsRead(); } catch { reply = { ok: false, error: thrown }; }
    if (!alive.current || generation !== readGeneration.current) return;
    if (reply.ok) { shownSave.current = saveGeneration.current; setSnapshot(reply.value); setChanges(unchanged()); setReading({ phase: 'ready' }); }
    else setReading({ phase: 'readFailed', error: failure(reply)! });
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
  // The switch turned off from Downloads & activity: read the settings again so the row shows it.
  useEffect(() => { const changed = (event: Event) => { if ((event as CustomEvent<{ from?: string }>).detail?.from === 'activity') void read(); }; window.addEventListener('vibeminer-settings-changed', changed); return () => window.removeEventListener('vibeminer-settings-changed', changed); }, [bridge]);
  // Read again keeps focus on the page: the heading while reading, then the switch whose row asked once it exists.
  function readAgain(name: SettingSwitch) { heading.current?.focus({ preventScroll: true }); refocus.current = name; void read(); }
  useLayoutEffect(() => { const name = refocus.current; if (!name || reading.phase === 'reading') return; refocus.current = null; (toggles.current[name] ?? heading.current)?.focus({ preventScroll: true }); }, [reading.phase]);
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
  const newer = snapshot?.file === 'newer', notRead = snapshot?.file === 'unreadable' && snapshot.problem?.code === 'SETTINGS_NOT_READ', locked = newer || notRead;
  async function change(name: SettingSwitch, target: boolean) {
    if (changes[name].phase === 'saving' || !bridge.settingsSet || locked) return;
    setChanges(old => ({ ...old, [name]: { phase: 'saving', target } }));
    const generation = ++saveGeneration.current;
    let reply: BridgeReply<SettingsSnapshot>;
    try { reply = await bridge.settingsSet({ name, value: target }); } catch { reply = { ok: false, error: thrown }; }
    // Screens kept behind Settings re-check what depends on a switch (D081: open tables and their definitions), also
    // when Settings was left before the save replied.
    if (reply.ok) window.dispatchEvent(new CustomEvent('vibeminer-settings-changed', { detail: { name } }));
    if (!alive.current) return;
    if (reply.ok) {
      const committed = reply.value;
      if (generation > shownSave.current) { shownSave.current = generation; setSnapshot(committed); }
      setChanges(old => ({ ...old, [name]: { phase: 'idle', savedAt: committed.lastChangedAt } }));
    }
    else setChanges(old => ({ ...old, [name]: { phase: 'failed', target, error: failure(reply)! } }));
  }
  async function storageAction(kind: 'copy' | 'show') {
    const action = kind === 'copy' ? bridge.storageCopyPath : bridge.storageShowFolder;
    if (!action) return;
    let reply: BridgeReply<unknown>;
    try { reply = await action(); } catch { reply = { ok: false, error: thrown }; }
    if (!alive.current) return;
    setStorageNote(reply.ok ? kind === 'copy' ? 'Path copied' : 'Opened in the file manager' : `${kind === 'copy' ? 'Couldn’t copy the path.' : 'Couldn’t open the folder.'} ${reply.error.message} (${reply.error.code})`);
  }

  const known = reading.phase === 'ready' && !!snapshot;
  const onOff = (value: boolean) => value ? 'On' : 'Off';
  // An unreadable value is never used: it reads as Off, whatever the switch's default (D081, D106).
  const inUse = (c: { value: boolean; defaultValue: boolean }) => c.value === c.defaultValue ? `Using the default, ${onOff(c.value)}.` : locked ? `Using ${onOff(c.value)}.` : `Using ${onOff(c.value)} until you switch it.`; // A locked switch can't be switched here.
  // One switch row's control column, error and note. File-wide problems (a newer or unread file, a read failure) are
  // explained once, at the first row; every switch still says why it can't change.
  function switchParts(name: SettingSwitch, first: boolean): { control: ReactNode; extra: ReactNode; note: ReactNode } {
    const { key, title, byDefault } = SWITCH[name], c = snapshot?.[name], pending = changes[name];
    let control: ReactNode, saveLine: ReactNode = '', extra: ReactNode = null, note: ReactNode = null;
    if (!known || !c) {
      control = reading.phase === 'reading'
        ? <><span className="st-loading" role="status"><span className="st-pulse" aria-hidden="true"/>Reading saved value…</span><span className="st-dflt">No switch until the saved value is known.</span></>
        : <span className="st-dflt">No switch until the saved value is known.</span>;
      if (reading.phase === 'readFailed' && first) extra = <div className="st-notice error" role="alert"><div className="st-text"><strong>Couldn’t read settings.</strong>{reading.error.message} Nothing was changed. <code>({reading.error.code})</code></div>{reading.error.retryable && <button className="st-btn" onClick={() => readAgain(name)}>Read again</button>}</div>;
    } else {
      const p = c.problem, busy = pending.phase === 'saving', committed = c.value;
      // A file from a newer VibeMiner, or one whose bytes were never read, is never replaced, so the switch can't change it.
      const lockReason = newer ? 'Can’t be changed here: saved by a newer VibeMiner.' : notRead ? p?.retryable ? 'Can’t be changed until the settings file is read.' : 'Can’t be changed: this version can’t read settings-v1.json.' : null;
      const fileWide = snapshot!.file !== 'read', noted = !!p;
      control = <><button ref={element => { toggles.current[name] = element; }} className={`st-switch${busy ? ' st-saving' : ''}`} role="switch" id={`settings-${key}-switch`} aria-checked={committed} aria-disabled={busy || locked || !bridge.settingsSet} aria-labelledby={`settings-${key}-name`} aria-describedby={`settings-${key}-desc settings-${key}-save${noted ? ` settings-${key}-note` : ''}`} onClick={() => { if (!busy && !locked) void change(name, !committed); }}><span className="st-track" aria-hidden="true"/><span>{busy ? pending.target ? 'Turning on…' : 'Turning off…' : onOff(committed)}</span></button><span className="st-dflt">Default: {onOff(c.defaultValue)}{c.source === 'unreadable' && (c.value === c.defaultValue ? ' · in use now' : ` · ${onOff(c.value)} in use now`)}</span></>;
      saveLine = busy ? <span className="st-busy"><span className="st-pulse" aria-hidden="true"/>Saving…</span> : lockReason ?? (pending.phase === 'idle' && pending.savedAt ? <><span className="st-ok">Saved</span> · {stamp(pending.savedAt)}</> : '');
      if (pending.phase === 'failed') {
        // Retry only what can work: a newer or unread file needs a fresh read, not the same change again.
        const reread = pending.error.code === 'SETTINGS_VERSION' || pending.error.code === 'SETTINGS_NOT_READ';
        extra = <div className="st-notice error" role="alert"><div className="st-text"><strong>Couldn’t save {title}. It’s still {onOff(committed)}.</strong>{pending.error.message} <code>({pending.error.code})</code></div>{reread ? <button className="st-btn" onClick={() => readAgain(name)}>Read again</button> : pending.error.retryable && <button className="st-btn" onClick={() => { toggles.current[name]?.focus({ preventScroll: true }); void change(name, pending.target); }}>Retry turning {pending.target ? 'on' : 'off'}</button>}</div>;
      }
      if (c.source === 'unreadable' && p && fileWide && !first) note = <div className="st-state warn" role="note" id={`settings-${key}-note`}>
        <strong>{newer ? `Saved by a newer VibeMiner. ${inUse(c)}` : notRead ? `Settings file not read. ${inUse(c)}` : `Settings file couldn’t be read. ${inUse(c)}`}</strong>
        <p>{c.value === c.defaultValue ? `${title} uses its default.` : `${title} reads as ${onOff(c.value)}, so ${byDefault}.`} {newer ? 'Change it in the newer VibeMiner.' : notRead ? 'The file wasn’t changed; see the first setting for why.' : 'Switching saves a new value.'} <code>({p.code})</code></p>
      </div>;
      else if (c.source === 'unreadable' && p && noted) note = <div className="st-state warn" role="note" id={`settings-${key}-note`}>
        <strong>{newer ? `Saved by a newer VibeMiner. ${inUse(c)}` : notRead ? `Settings file not read. ${inUse(c)}` : `Saved value couldn’t be read. ${inUse(c)}`}</strong>
        <p>{newer ? `${p.message} This version uses the default and doesn’t change the file. Change this setting in the newer VibeMiner.` : notRead ? p.retryable ? `${p.message} The file wasn’t changed, and the switches can’t be used until the file is read.` : `${p.message} This version of VibeMiner can’t read settings-v1.json in the storage folder, so it wasn’t changed. Fix or remove that file, then open Settings again.` : fileWide ? `${p.message} Every setting uses a safe value, so ${SWITCH.communityBuildHistory.byDefault}, ${SWITCH.fetchTableDefinitions.byDefault}, and ${SWITCH.communityListfile.byDefault}. Switching saves a new value.` : `The settings file has a value for this setting that VibeMiner doesn’t understand, so it is ignored and ${byDefault}. Switching saves a new value.`} <code>({p.code})</code></p>
        {notRead && p.retryable && <button className="st-btn st-note-action" onClick={() => readAgain(name)}>Read again</button>}
      </div>;
    }
    return { control: <div className="st-ctl">{control}<span className="st-save-line" id={`settings-${key}-save`} role="status">{saveLine}</span></div>, extra, note };
  }
  const community = switchParts('communityBuildHistory', true), fetch = switchParts('fetchTableDefinitions', false), listfile = switchParts('communityListfile', false);
  const limits = snapshot?.downloadLimits;
  const sizeFact = measure.phase === 'measuring' ? <span className="st-loading"><span className="st-pulse" aria-hidden="true"/>Measuring…</span>
    : measure.phase === 'measured' ? <>{readableBytes(measure.value.bytes)} <span className="st-dim st-mono">· {exactBytes(measure.value.bytes)}</span>{measure.value.skipped !== '0' && <span className="st-skipped">{exactCount(measure.value.skipped)} {measure.value.skipped === '1' ? 'item' : 'items'} couldn’t be read and {measure.value.skipped === '1' ? 'isn’t' : 'aren’t'} counted.</span>}</>
    : measure.phase === 'failed' ? <span className="st-measure-failed">Not measured. {measure.error.message} <code>({measure.error.code})</code> <button className="st-btn small" onClick={() => { copy.current?.focus({ preventScroll: true }); void measureNow(true); }}>Measure again</button></span>
    : <span className="st-dim">Not measured: this VibeMiner engine can’t measure the folder.</span>;
  const measuredFact = measure.phase === 'measured' ? `${stamp(measure.value.measuredAt)} · ${measure.asked ? 'when you asked' : 'when Settings opened'}` : null;
  const isReading = reading.phase === 'reading';
  const defaulted = known ? SETTING_SWITCHES.filter(name => snapshot![name].source === 'unreadable') : [];
  const aboutBox = isReading
    ? <div className="st-state plain" role="status"><strong>Reading settings…</strong><p>Values show once the engine has read them. Defaults are not shown as if they were saved.</p></div>
    : reading.phase === 'readFailed' ? <div className="st-state warn"><strong>Settings not read</strong><p>The values aren’t known, so no switch is shown.</p></div>
    : newer ? <div className="st-state warn"><strong>Settings saved by a newer VibeMiner</strong><p>This version uses safe values, Off, and doesn’t change the file.</p></div>
    : notRead ? <div className="st-state warn"><strong>Settings file not read</strong><p>Every setting reads as Off until the file is read. The file wasn’t changed.</p></div>
    : defaulted.length ? <div className="st-state warn"><strong>{defaulted.every(name => snapshot![name].value === snapshot![name].defaultValue) ? defaulted.length === 1 ? '1 setting is using its default' : `${defaulted.length} settings are using their defaults` : defaulted.length === 1 ? '1 setting is Off until you switch it' : `${defaulted.length} settings are Off until you switch them`}</strong><p>{snapshot!.file === 'read' ? `The saved ${defaulted.length === 1 ? 'value' : 'values'} for ${defaulted.map(name => SWITCH[name].title).join(' and ')} couldn’t be read. The other settings were read normally.` : 'The settings file isn’t one VibeMiner understands, so every setting reads as Off and nothing is fetched.'}</p></div> : null;
  const lastChange = isReading ? <span className="st-dim">Not read yet</span> : reading.phase === 'readFailed' || notRead ? <span className="st-dim">Not known</span> : snapshot?.lastChangedAt ? stamp(snapshot.lastChangedAt) : <span className="st-dim">{snapshot?.file === 'absent' ? 'Never changed' : 'Not recorded'}</span>;
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
                <p id="settings-community-desc">Blizzard’s version service only lists current builds. This adds older builds from the community-kept wago.tools build list, so you can see builds VibeMiner never saw itself and check whether Blizzard’s CDN still has their files.</p>
                <dl className="st-effects">
                  <dt>When on</dt><dd><strong>Refresh builds</strong> also reads the build list from wago.tools, at most once a day. Only the list comes from there: product, version, build number and config hashes. <strong>Check availability</strong> asks Blizzard’s CDN for each build’s configs and archive indexes and checks the configs against their hashes, so a wrong entry can only show as Gone. These builds get a <span className="st-pill community">Community</span> badge.</dd>
                  <dt>When off</dt><dd>Discovery lists only builds seen on Blizzard’s version service, and VibeMiner doesn’t contact wago.tools. Community builds are hidden. The list already read is kept for when you turn it back on.</dd>
                  <dt>Takes effect</dt><dd>At once: Discovery shows or hides community builds as soon as you switch. wago.tools is asked at the next Refresh builds that is due, never when you switch.</dd>
                  <dt>This version</dt><dd>Community builds can be checked but not opened yet: Open needs a build seen on Blizzard’s version service.</dd>
                  <dt>Source</dt><dd>wago.tools, <span className="st-mono">wago.tools/api/builds</span>, read at runtime and never shipped with VibeMiner.</dd>
                </dl>
              </div>
              {community.control}{community.extra}{community.note}
            </div>
          </section>
          <section aria-labelledby="settings-group-tables"><h2 className="st-label st-group-h" id="settings-group-tables">Tables</h2>
            <div className="st-setting" id="settings-row-fetch">
              <div className="st-text">
                <h3 id="settings-fetch-name">Fetch newer table definitions from WoWDBDefs</h3>
                <p id="settings-fetch-desc">A new game build can change a table’s layout before VibeMiner is updated. This fetches the newest community definition for just that table, so it can still open.</p>
                <dl className="st-effects">
                  <dt>When on</dt><dd>When no bundled definition lists a build, opening a table’s data in Tables, or an area such as Items or Creatures that reads it, fetches that table’s newest definition, a small text file, from the WoWDBDefs project on GitHub. A definition that lists the exact build is used first, bundled before fetched; otherwise the bundled match by layout. It is kept in the storage folder, checked again at most once a day (sooner when it doesn’t list the build yet), and works offline after that. Definitions only describe columns; all game data still comes from Blizzard’s CDN. Tables and areas read this way say <strong>Definition from WoWDBDefs</strong>.</dd>
                  <dt>When off</dt><dd>Only the definitions bundled with VibeMiner, matched by build or by layout. VibeMiner doesn’t contact GitHub, and a table they don’t cover, or an area that needs it, can’t open.</dd>
                  <dt>Takes effect</dt><dd>Open tables and areas check again at once and load again if they need to; the others the next time they open.</dd>
                  <dt>Source</dt><dd>WoWDBDefs contributors, <span className="st-mono">github.com/wowdev/WoWDBDefs</span>, CC BY-SA 4.0.</dd>
                </dl>
              </div>
              {fetch.control}{fetch.extra}{fetch.note}
            </div>
          </section>
          <section aria-labelledby="settings-group-files"><h2 className="st-label st-group-h" id="settings-group-files">Files</h2>
            <div className="st-setting" id="settings-row-listfile">
              <div className="st-text">
                <h3 id="settings-listfile-name">Community file names from wow-listfile</h3>
                <p id="settings-listfile-desc">ROOT gives files no names, so Files says <strong>Name not known</strong>. This adds names from wow-listfile, a community-kept list of FileDataIDs and their paths.</p>
                <dl className="st-effects">
                  <dt>When on</dt><dd>The first time Files lists a build’s files, VibeMiner downloads the newest wow-listfile release from GitHub, about 150 MB, checks it against the SHA-256 GitHub publishes and keeps a copy in the storage folder. Files then shows names with a <span className="st-pill community">Community</span> badge. It checks for a newer release once a week. Names are labels only: the FileDataID stays the file’s identity, and all data still comes from Blizzard’s CDN.</dd>
                  <dt>When off</dt><dd>Files shows <strong>Name not known</strong> for ROOT files, and VibeMiner doesn’t contact GitHub for names. A download in progress stops. What it had is kept, and so is a downloaded copy, so turning it back on doesn’t start over.</dd>
                  <dt>Takes effect</dt><dd>At once. The download runs in the background and resumes where it stopped after VibeMiner closes.</dd>
                  <dt>Source</dt><dd>wow-listfile contributors, <span className="st-mono">github.com/wowdev/wow-listfile</span>, downloaded from its releases. It has no licence, so VibeMiner never ships it.</dd>
                </dl>
              </div>
              {listfile.control}{listfile.extra}{listfile.note}
              {/* After the switch in layout and tab order, at every size (#21 UI review). */}
              <ListfileState bridge={bridge} enabled={known && !!snapshot?.communityListfile.value} known={known}/>
            </div>
          </section>
          <section aria-labelledby="settings-group-storage"><h2 className="st-label st-group-h" id="settings-group-storage">Storage</h2>
            <div className="st-setting" id="settings-row-storage">
              <div className="st-text">
                <h3 id="settings-storage-name">Storage location</h3>
                <p>Where VibeMiner keeps everything it downloads, its build database and these settings.</p>
                <dl className="st-facts">
                  <dt>Folder</dt><dd>{snapshot ? <span className="st-path st-selectable" id="settings-storage-path">{snapshot.storage.dataDirectory}</span> : <span className="st-dim">{isReading ? 'Reading…' : 'Not known'}</span>}</dd>
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
                <dl className="st-facts"><dt>Parallel downloads</dt><dd>{limits ? `Up to ${limits.parallelDownloads} at once, at most ${limits.backgroundDownloads} of them for background downloads` : <span className="st-dim">{isReading ? 'Reading…' : 'Not known'}</span>}</dd><dt>Bandwidth cap</dt><dd>{limits ? limits.bandwidthCapBytesPerSecond === null ? 'None' : `${readableBytes(limits.bandwidthCapBytesPerSecond)}/s` : <span className="st-dim">{isReading ? 'Reading…' : 'Not known'}</span>}</dd></dl>
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
            <p className="st-tiny">Changing a setting never deletes builds or downloaded files. The only work in Downloads &amp; activity a setting stops is the community file names download, when you turn them off; what it has is kept.</p>
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

// The wow-listfile download (D105): the copy in use, what the engine is doing now, and the last failure. It polls once
// a second while work runs, and reads again when the switch changes.
function ListfileState({ bridge, enabled, known }: { bridge: DiscoveryBridge; enabled: boolean; known: boolean }) {
  const [status, setStatus] = useState<CommunityListfileStatus | null>(null);
  const [error, setError] = useState<Problem | null>(null);
  const alive = useRef(true), generation = useRef(0);
  async function load(check = false) {
    const call = check ? bridge.listfileCheck : bridge.listfileRead;
    if (!call) return;
    const mine = ++generation.current;
    let reply: BridgeReply<CommunityListfileStatus>;
    try { reply = await call(); } catch { reply = { ok: false, error: thrown }; }
    if (!alive.current || mine !== generation.current) return;
    if (reply.ok) { setStatus(reply.value); setError(null); if (check) window.dispatchEvent(new CustomEvent('vibeminer-listfile-asked')); } else setError(failure(reply));
  }
  useEffect(() => { alive.current = true; void load(); return () => { alive.current = false; }; }, [bridge, enabled]);
  // Downloads & activity also shows this download, so a Try again or a stop there shows here too.
  useEffect(() => { const changed = () => { void load(); }; window.addEventListener('vibeminer-listfile-changed', changed); return () => window.removeEventListener('vibeminer-listfile-changed', changed); }, [bridge]);
  const working = !!status && (status.activity !== 'idle' || status.enabled && status.started && !status.release && !status.problem);
  useEffect(() => {
    if (!working) return;
    const timer = window.setInterval(() => { void load(); }, 1000);
    return () => window.clearInterval(timer);
  }, [working]);
  if (!bridge.listfileRead) return <p className="st-tiny">This VibeMiner engine can’t download community file names.</p>;
  if (!known || !status) return error ? <p className="st-tiny">Download state not read. {error.message} <code>({error.code})</code></p> : null;
  const r = status.release, p = status.problem, stage = listfileStage(status), measured = listfileMeasurement(status);
  const now = !status.enabled ? 'Off. Nothing is downloaded.'
    : stage ? `${stage}${measured ? ` · ${measured.label}` : '…'}`
    : r && p ? `Release ${r.tag} is still in use. The last check for a newer one failed; VibeMiner tries again after 15 minutes.`
    : r ? `Up to date as of ${stamp(status.checkedAt ?? r.fetchedAt)}. Checked again in a week.`
    : p ? 'Not downloaded. The last attempt failed; VibeMiner tries again after 15 minutes, or use Try again now.' : status.started ? 'Starting…' : 'Not downloaded yet. It downloads the first time Files lists a build’s files.';
  const busy = status.activity !== 'idle';
  return <div className="st-listfile" id="settings-listfile-state" aria-label="Community file names download" role="group">
    <dl className="st-facts">
      <dt>Copy in use</dt><dd>{r && status.enabled ? <>Release <span className="st-mono">{r.tag}</span> · published {stamp(r.publishedAt)} · fetched {stamp(r.fetchedAt)} · {exactCount(r.nameCount)} names</> : r ? <>Release <span className="st-mono">{r.tag}</span>, kept, not used while off</> : <span className="st-dim">None yet</span>}</dd>
      <dt>Now</dt><dd role="status">{busy && <span className="st-pulse" aria-hidden="true"/>}{now}</dd>
    </dl>
    {status.enabled && p && <div className="st-notice error" role="alert"><div className="st-text"><strong>{r ? `Couldn’t get a newer release. Release ${r.tag} is still in use.` : 'Couldn’t get the community file names. Files says Name not known.'}</strong>{p.message} <code>({p.code})</code></div></div>}
    {status.enabled && bridge.listfileCheck && <div className="st-ctl-row"><button className="st-btn small" aria-disabled={busy} onClick={() => { if (!busy) void load(true); }}>{p ? 'Try again now' : r ? 'Check for a newer release' : 'Download now'}</button></div>}
  </div>;
}
