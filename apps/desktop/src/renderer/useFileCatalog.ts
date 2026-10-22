import { useEffect, useRef, useState } from 'react';
import type { FileCatalogList, FileCatalogSetParams, FileCatalogStatus, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { fileCatalogListMatches, fileCatalogMatches, fileCatalogSetMatches } from '../shared/inventory';

/** A preparing catalog is read again after this long, never overlapping. */
export const CATALOG_POLL_MS = 1000;
/** A failed read is tried again after this long, keeping the last confirmed status. */
export const CATALOG_RETRY_MS = 2000;
/** Activity's list is read this often while nothing is being prepared, to notice a preparation another Open queued. */
export const CATALOG_IDLE_MS = 5000;

/**
 * Where the opened build's file catalog stands (D071). One bounded local read per second while it is pending, none once it
 * is ready, stopped, failed or unsupported until {@link retry} or an action asks again. A reply for another build or
 * region is never shown: the status is cleared when the build changes and every reply is checked against the build it was
 * asked for. A failed read keeps the last confirmed status and says so in `error`. {@link retry} resolves once a read
 * started after it has finished, whether it succeeded or failed.
 */
export function useFileCatalog(bridge: DiscoveryBridge, snapshot: WorkspaceSnapshot, enabled: boolean, nudge = '') {
  const [state, setState] = useState<{ key: string; status: FileCatalogStatus | null; error: string }>({ key: '', status: null, error: '' });
  const [again, setAgain] = useState(0);
  // Each read gets a number; a retry waits for the first read numbered after the last one started before it.
  const started = useRef(0), waiting = useRef<{ after: number; done: () => void }[]>([]);
  const params = { productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region };
  const key = `${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
  useEffect(() => {
    const read = bridge.fileCatalog;
    if (!enabled || !read) return;
    let disposed = false, timer: ReturnType<typeof setTimeout> | undefined;
    const run = async () => {
      const began = Date.now(), number = ++started.current;
      const settle = () => { const ready = waiting.current.filter(w => w.after < number); waiting.current = waiting.current.filter(w => w.after >= number); ready.forEach(w => w.done()); };
      try {
        const reply = await read(params);
        if (disposed) return;
        if (!reply.ok) throw new Error(`${reply.error.message} (${reply.error.code})`);
        if (!fileCatalogMatches(reply.value, params)) throw new Error('The file catalog status did not match this build.');
        setState({ key, status: reply.value, error: '' }); settle();
        if (reply.value.state === 'pending') timer = setTimeout(() => void run(), Math.max(0, CATALOG_POLL_MS - (Date.now() - began)));
      } catch (failure) {
        if (disposed) return;
        const message = failure instanceof Error ? failure.message : 'The file catalog status is unavailable.';
        setState(previous => ({ key, status: previous.key === key ? previous.status : null, error: message })); settle();
        timer = setTimeout(() => void run(), CATALOG_RETRY_MS);
      }
    };
    void run();
    return () => { disposed = true; clearTimeout(timer); };
    // The key names exactly the build and region asked for; `again` and `nudge` ask once more.
  }, [bridge, key, enabled, again, nudge]);
  const current = state.key === key ? { status: state.status, error: state.error } : { status: null, error: '' };
  const retry = () => new Promise<void>(done => { waiting.current.push({ after: started.current, done }); setAgain(n => n + 1); });
  return { ...current, retry };
}

/**
 * The preparations Downloads & activity lists, one task per build (D071): read every second while one is pending, every
 * five seconds otherwise, never overlapping. Stop and Resume go through {@link act}, which reads the list again at once.
 */
export function useFileCatalogTasks(bridge: DiscoveryBridge, enabled: boolean, nudge: string) {
  const [catalogs, setCatalogs] = useState<readonly FileCatalogStatus[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [again, setAgain] = useState(0);
  useEffect(() => {
    const read = bridge.fileCatalogs;
    if (!enabled || !read) { setCatalogs([]); return; }
    let disposed = false, timer: ReturnType<typeof setTimeout> | undefined;
    const run = async () => {
      let pending = false;
      try {
        const reply = await read();
        if (disposed) return;
        if (!reply.ok) throw new Error(`${reply.error.message} (${reply.error.code})`);
        if (!fileCatalogListMatches(reply.value as FileCatalogList)) throw new Error('The file list preparations did not agree with themselves.');
        setCatalogs(reply.value.catalogs); setError('');
        pending = reply.value.catalogs.some(status => status.state === 'pending');
      } catch (failure) {
        if (disposed) return;
        setError(failure instanceof Error ? failure.message : 'The file list preparations are unavailable.');
      }
      timer = setTimeout(() => void run(), pending ? CATALOG_POLL_MS : CATALOG_IDLE_MS);
    };
    void run();
    return () => { disposed = true; clearTimeout(timer); };
  }, [bridge, enabled, nudge, again]);
  async function act(params: FileCatalogSetParams): Promise<FileCatalogStatus | string> {
    const set = bridge.fileCatalogSet;
    if (!set) return 'This engine cannot stop or resume file list preparation.';
    setBusy(`${params.productId}|${params.buildIdentity}`);
    try {
      const reply = await set(params);
      if (!reply.ok) return `${reply.error.message} (${reply.error.code})`;
      if (!fileCatalogSetMatches(reply.value, params)) return 'The engine replied for a different build or state.';
      return reply.value;
    } finally { setBusy(null); setAgain(n => n + 1); }
  }
  return { catalogs, error, busy, act };
}
