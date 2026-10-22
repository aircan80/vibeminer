import { useEffect, useRef, useState } from 'react';
import type { Build, BuildDownloadSummary, DownloadChanges, DownloadSummariesParams } from '../../../../contracts/generated/discovery';
import type { BridgeReply, DiscoveryBridge } from '../shared/bridge';
import { MAX_SUMMARY_BUILDS, summariesMatch, summaryKey, validRevision } from '../shared/download-summary';

/**
 * A figure that is still changing on its own: a running Download all pass, a first count of content on disk, or recorded
 * changes the engine has not applied yet (a pass that just finished can otherwise stop one change short of 100%).
 */
export const summaryMoving = (s: BuildDownloadSummary) => !!s.download &&
  (s.download.running || s.download.countState === 'counting' || s.download.changesPending);

/** A failed read is tried again after this long, keeping the last confirmed figures meanwhile (shown as not current). */
export const SUMMARY_RETRY_MS = 2000;
/** Each further failure in a row doubles the wait, up to this. A stopped engine is not asked every 2 s for the rest of the session. */
export const SUMMARY_RETRY_MAX_MS = 30000;
/** The wait before the next try after `failures` failed reads or waits in a row: 2, 4, 8, 16, then 30 s. */
export const summaryRetryDelay = (failures: number) => Math.min(SUMMARY_RETRY_MAX_MS, SUMMARY_RETRY_MS * 2 ** Math.max(0, Math.min(failures, 16) - 1));
/** Reads start at most this often, whether a moving figure or an engine change notice asked for them. */
export const SUMMARY_INTERVAL_MS = 1000;

/**
 * Stored figures for the loaded discovery window (D065, D077). Reads are local and never start work. They run when the
 * window, visibility or saved download intents change, and repeat at most once a second, never overlapping, while a
 * figure is still moving. Once nothing moves, one engine wait (`download.changes`) stands open with the revision of the
 * last read; when the engine reports a change (an object stored, a ledger admitted) the window is read again, no sooner
 * than a second after the previous read. The wait is cancelled when the figures stop being shown. A failed read keeps the
 * last confirmed figures, sets `error` so they are shown as not current (#50), and is retried after
 * {@link summaryRetryDelay}. So does a failed wait, except that a wait the engine does not support (`UNSUPPORTED`) only
 * stops waiting, and one that cannot succeed later (`retryable: false`, e.g. a protocol error that stopped the engine)
 * stops without a retry. The next good read clears `error`. The failure count resets once the next step after a good read
 * works: a wait that answers, or the next read when a figure is moving, so a wait that keeps failing while reads succeed
 * still backs off. `readAt` is when the figures shown were read.
 */
export function useDownloadSummaries(bridge: DiscoveryBridge, builds: readonly Build[], visible: boolean, enabled: boolean, nudge: string) {
  const [summaries, setSummaries] = useState<ReadonlyMap<string, BuildDownloadSummary>>(() => new Map());
  const [error, setError] = useState('');
  const [readAt, setReadAt] = useState<number | null>(null);
  const previous = useRef<Promise<void>>(Promise.resolve());
  const keys = builds.map(build => summaryKey(build.productId, build.id));
  const signature = keys.join('|');
  useEffect(() => {
    const read = bridge.downloadSummaries, watch = bridge.downloadChanges;
    if (!enabled || !read || !visible || !builds.length) return;
    const wanted = [...new Map(builds.map(build => [summaryKey(build.productId, build.id), { productId: build.productId, buildIdentity: build.id }])).values()];
    let disposed = false, timer: ReturnType<typeof setTimeout> | undefined, waiting: string | undefined, lastRead = 0, failures = 0;
    const after = (milliseconds: number, next: () => void) => { clearTimeout(timer); timer = setTimeout(next, Math.max(0, milliseconds)); };
    const run = () => {
      const prior = previous.current;
      previous.current = (async () => {
        await prior.catch(() => undefined);
        if (disposed) return;
        const started = lastRead = Date.now();
        try {
          const next = new Map<string, BuildDownloadSummary>();
          let revision: string | undefined;
          for (let start = 0; start < wanted.length; start += MAX_SUMMARY_BUILDS) {
            const params = { builds: wanted.slice(start, start + MAX_SUMMARY_BUILDS) } as DownloadSummariesParams;
            const reply = await read(params);
            if (disposed) return;
            if (!reply.ok) throw new Error(`${reply.error.message} (${reply.error.code})`);
            if (!summariesMatch(reply.value, params)) throw new Error('The stored download figures did not match the listed builds.');
            // The first request's revision was taken before any of these reads, so a change during them still wakes us.
            revision ??= reply.value.revision;
            for (const summary of reply.value.summaries) next.set(summaryKey(summary.productId, summary.buildIdentity), summary);
          }
          setSummaries(next); setError(''); setReadAt(Date.now());
          // Only a wait that answers clears the count, so a wait that keeps failing while reads succeed still backs off.
          if ([...next.values()].some(summaryMoving)) { failures = 0; after(SUMMARY_INTERVAL_MS - (Date.now() - started), run); }
          else if (watch && revision) listen(revision);
          else failures = 0;
        } catch (failure) {
          if (disposed) return;
          setError(failure instanceof Error ? failure.message : 'Stored download figures are unavailable.');
          after(summaryRetryDelay(++failures), run);
        }
      })();
    };
    const listen = (revision: string) => {
      const id = `summaries_${crypto.randomUUID()}`, asked = Date.now();
      waiting = id;
      void (async () => {
        let reply: BridgeReply<DownloadChanges> | null, thrown = '';
        try { reply = await watch!({ after: revision }, id); } catch (failure) { reply = null; thrown = failure instanceof Error ? failure.message : ''; }
        if (waiting === id) waiting = undefined;
        if (disposed) return;
        if (reply?.ok && validRevision(reply.value.revision)) {
          failures = 0;
          // A new revision: something stored may have changed. The same one: the engine's wait ran out; wait again.
          if (reply.value.revision !== revision) after(SUMMARY_INTERVAL_MS - (Date.now() - lastRead), run);
          else after(SUMMARY_INTERVAL_MS - (Date.now() - asked), () => listen(revision));
          return;
        }
        // An engine without the wait leaves the figures as last read; they still come from a good read.
        if (reply && !reply.ok && reply.error.code === 'UNSUPPORTED') return;
        // Any other failed wait (a stopped engine, a protocol error) means a change could go unseen: the figures are not
        // current until a read succeeds. Only a failure that can succeed later is tried again.
        setError(reply && !reply.ok ? `${reply.error.message} (${reply.error.code})` : reply ? 'The engine sent an unreadable change notice.' : thrown || 'The engine stopped reporting changes to the stored figures.');
        if (!reply || reply.ok || reply.error.retryable) after(summaryRetryDelay(++failures), run);
      })();
    };
    run();
    return () => {
      disposed = true; clearTimeout(timer);
      if (waiting) void bridge.cancel(waiting).catch(() => undefined);
    };
    // The signature names exactly the loaded builds.
  }, [bridge, signature, visible, enabled, nudge]);
  return { summaries, error, readAt };
}
