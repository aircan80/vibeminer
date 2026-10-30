import { useEffect, useRef, useState } from 'react';
import type { Build, BuildAvailability, AvailabilityCounts } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';

const stamp = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const count = (value: number) => value.toLocaleString('en-US');
const short = (value: string) => `${value.slice(0, 10)}…${value.slice(-4)}`;
type Problem = { code: string; message: string };
type Running = { productId: string; id: string; version: string; requestId: string; archives: boolean; stage: string | null; completed: number | null; total: number | null; stopping: boolean };
export type AvailabilityCheck = {
  available: boolean;
  running: Running | null;
  /** Checks finished this session, newest per build; they win over the page's saved copy. */
  results: ReadonlyMap<string, BuildAvailability>;
  errors: ReadonlyMap<string, Problem>;
  /** Builds whose latest check was stopped on request: a calm state, not an error. */
  stopped: ReadonlySet<string>;
  start: (build: Build, archives: boolean) => void;
  stop: () => void;
};
const key = (productId: string, id: string) => `${productId}:${id}`;

/**
 * Check availability (#9, D106): one check at a time, kept running while the selection moves elsewhere. Only Stop check
 * cancels it; leaving Discovery for good (unmount) does too.
 */
export function useAvailabilityCheck(bridge: DiscoveryBridge, enabled: boolean): AvailabilityCheck {
  const [running, setRunning] = useState<Running | null>(null);
  const [results, setResults] = useState<Map<string, BuildAvailability>>(new Map());
  const [errors, setErrors] = useState<Map<string, Problem>>(new Map());
  const [stopped, setStopped] = useState<Set<string>>(new Set());
  const current = useRef<Running | null>(null); current.current = running;
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    const off = bridge.onProgress(update => {
      const now = current.current;
      if (!now || update.requestId !== now.requestId || update.unit !== 'files') return;
      setRunning(value => value && value.requestId === update.requestId ? { ...value, stage: update.stage, completed: update.completed, total: update.total } : value);
    });
    return () => { alive.current = false; off(); const now = current.current; if (now) void bridge.cancel(now.requestId).catch(() => {}); };
  }, [bridge]);
  function start(build: Build, archives: boolean) {
    if (!enabled || !bridge.checkAvailability || current.current) return;
    const requestId = crypto.randomUUID(), at = key(build.productId, build.id);
    const value: Running = { productId: build.productId, id: build.id, version: build.version, requestId, archives, stage: null, completed: null, total: null, stopping: false };
    current.current = value; setRunning(value);
    setErrors(previous => { const next = new Map(previous); next.delete(at); return next; });
    setStopped(previous => { const next = new Set(previous); next.delete(at); return next; });
    void bridge.checkAvailability({ productId: build.productId, buildIdentity: build.id, archives }, requestId).then(reply => {
      if (!alive.current) return;
      if (reply.ok) setResults(previous => new Map(previous).set(at, reply.value));
      // A check stopped on request is what the user asked for, so it says so calmly, as Items and Creatures do.
      else if (reply.error.code === 'CANCELED') setStopped(previous => new Set(previous).add(at));
      else setErrors(previous => new Map(previous).set(at, { code: reply.error.code, message: reply.error.message }));
    }).catch(() => {
      if (alive.current) setErrors(previous => new Map(previous).set(at, { code: 'ENGINE_UNAVAILABLE', message: 'The local engine is unavailable. Restart VibeMiner to reconnect.' }));
    }).finally(() => { if (alive.current && current.current?.requestId === requestId) { current.current = null; setRunning(null); } });
  }
  function stop() {
    const now = current.current;
    if (!now || now.stopping) return;
    setRunning({ ...now, stopping: true });
    void bridge.cancel(now.requestId).catch(() => {});
  }
  return { available: enabled && !!bridge.checkAvailability, running, results, errors, stopped, start, stop };
}

const STATE: Record<BuildAvailability['state'], string> = { available: 'Available', partlyAvailable: 'Partly available', gone: 'Gone', unknown: 'Unknown' };
const part = (counts: AvailabilityCounts, noun: string) => counts.missing
  ? `${count(counts.missing)} of ${count(counts.total)} ${noun} missing${counts.unknown ? `, ${count(counts.unknown)} not answered` : ''}`
  : counts.unknown ? `${count(counts.unknown)} of ${count(counts.total)} ${noun} not answered` : `all ${count(counts.total)} ${noun} present`;
const configText = (state: BuildAvailability['buildConfigState'], name: string) =>
  state === 'verified' ? `${name} verified` : state === 'missing' ? `${name} missing` : state === 'invalid' ? `${name} didn’t match its hash` : `${name} not answered`;

/** What one saved check found, in words: the outcome, the date, then each stage that ran. */
export function availabilityLines(result: BuildAvailability, build: Build): { title: string; detail: string } {
  const stages = [`${configText(result.buildConfigState, 'Build config')}, ${configText(result.cdnConfigState, 'CDN config')}`];
  if (result.indexes) stages.push(part(result.indexes, 'archive indexes'));
  if (result.archives) stages.push(part(result.archives, 'archives'));
  else if (result.indexes && result.scope === 'indexes' && result.state === 'available') stages.push('archives not checked');
  const gone = result.state === 'gone' ? ` The build config is missing on every host asked (${result.hosts.join(', ')}).` : '';
  const other = result.cdnConfig !== build.cdnConfig ? ` Checked with CDN config ${short(result.cdnConfig)}.` : '';
  const problem = result.problem ? ` ${result.problem.message} (${result.problem.code})` : '';
  return { title: `${STATE[result.state]} · checked ${stamp(result.checkedAt)}`, detail: `${stages.join('; ')}.${gone}${other}${problem}` };
}

/** The inspector's File availability row: the latest check, and Check availability. Never downloads game content. */
export function AvailabilityRow({ build, check }: { build: Build; check: AvailabilityCheck }) {
  const [archives, setArchives] = useState(false);
  const at = key(build.productId, build.id);
  const result = check.results.get(at) ?? build.availabilityCheck ?? null;
  const error = check.errors.get(at);
  const here = check.running && check.running.productId === build.productId && check.running.id === build.id ? check.running : null;
  const elsewhere = check.running && !here ? check.running : null;
  const lines = result ? availabilityLines(result, build) : null;
  const progress = here ? here.stopping ? 'Stopping the check…' : here.stage ? `${here.stage}${here.total !== null ? ` · ${count(here.completed ?? 0)} of ${count(here.total)}` : ''}…` : 'Starting the check…' : null;
  return <>
    <dt>File availability</dt>
    <dd className="dv-availability">
      {lines ? <><strong className={`dv-avail-${result!.state}`}>{lines.title}</strong><span className="dv-avail-detail">{lines.detail}</span></> : <span>Not checked</span>}
      {error && <span className="dv-avail-error" role="alert">Couldn’t check: {error.message} <code>({error.code})</code></span>}
      {!here && check.stopped.has(at) && <span className="dv-avail-stopped" role="status">Check stopped. Nothing new was saved{result ? '; the result above is from the earlier check' : ''}.</span>}
      {check.available && <span className="dv-avail-actions">
        {/* One button that becomes Stop check and back, so focus stays on it when a check starts or ends. */}
        <button className="dv-btn" disabled={!!elsewhere} aria-disabled={here?.stopping || undefined} aria-describedby="availability-note"
          onClick={() => { if (here) check.stop(); else check.start(build, archives); }}>{here ? here.stopping ? 'Stopping check…' : 'Stop check' : result ? 'Check again' : 'Check availability'}</button>
        <label className="dv-avail-option"><input type="checkbox" checked={here ? here.archives : archives} disabled={!!here || !!elsewhere} onChange={event => setArchives(event.target.checked)}/> Also every archive</label>
        <span role="status" className="dv-avail-progress">{progress && <><span className="dv-pulse" aria-hidden="true"/>{progress}</>}</span>
        <span className="dv-note" id="availability-note">{elsewhere ? `${elsewhere.productId} ${elsewhere.version} is being checked. Wait for it, or select it and stop it, to check this build.` : 'Asks Blizzard’s CDN for the build and CDN configs, then each archive index. Only the two small configs are downloaded.'}</span>
      </span>}
    </dd>
  </>;
}
