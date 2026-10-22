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

/** A failed read is tried again after this long, keeping the last confirmed figures meanwhile. */
export const SUMMARY_RETRY_MS = 2000;
/** Reads start at most this often, whether a moving figure or an engine change notice asked for them. */
export const SUMMARY_INTERVAL_MS = 1000;

/**
 * Stored figures for the loaded discovery window (D065, D077). Reads are local and never start work. They run when the
 * window, visibility or saved download intents change, and repeat at most once a second, never overlapping, while a
 * figure is still moving. Once nothing moves, one engine wait (`download.changes`) stands open with the revision of the
 * last read; when the engine reports a change (an object stored, a ledger admitted) the window is read again, no sooner
 * than a second after the previous read. The wait is cancelled when the figures stop being shown. A failed read keeps the
 * last confirmed figures and is retried after {@link SUMMARY_RETRY_MS}.
 */
export function useDownloadSummaries(bridge: DiscoveryBridge, builds: readonly Build[], visible: boolean, enabled: boolean, nudge: string) {
  const [summaries, setSummaries] = useState<ReadonlyMap<string, BuildDownloadSummary>>(() => new Map());
  const [error, setError] = useState('');
  const previous = useRef<Promise<void>>(Promise.resolve());
  const keys = builds.map(build => summaryKey(build.productId, build.id));
  const signature = keys.join('|');
  useEffect(() => {
    const read = bridge.downloadSummaries, watch = bridge.downloadChanges;
    if (!enabled || !read || !visible || !builds.length) return;
    const wanted = [...new Map(builds.map(build => [summaryKey(build.productId, build.id), { productId: build.productId, buildIdentity: build.id }])).values()];
    let disposed = false, timer: ReturnType<typeof setTimeout> | undefined, waiting: string | undefined, lastRead = 0;
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
          setSummaries(next); setError('');
          if ([...next.values()].some(summaryMoving)) after(SUMMARY_INTERVAL_MS - (Date.now() - started), run);
          else if (watch && revision) listen(revision);
        } catch (failure) {
          if (disposed) return;
          setError(failure instanceof Error ? failure.message : 'Stored download figures are unavailable.');
          after(SUMMARY_RETRY_MS, run);
        }
      })();
    };
    const listen = (revision: string) => {
      const id = `summaries_${crypto.randomUUID()}`, asked = Date.now();
      waiting = id;
      void (async () => {
        let reply: BridgeReply<DownloadChanges> | null;
        try { reply = await watch!({ after: revision }, id); } catch { reply = null; }
        if (waiting === id) waiting = undefined;
        if (disposed) return;
        if (reply?.ok && validRevision(reply.value.revision)) {
          // A new revision: something stored may have changed. The same one: the engine's wait ran out; wait again.
          if (reply.value.revision !== revision) after(SUMMARY_INTERVAL_MS - (Date.now() - lastRead), run);
          else after(SUMMARY_INTERVAL_MS - (Date.now() - asked), () => listen(revision));
        } else if (!reply || reply.ok || reply.error.retryable) after(SUMMARY_RETRY_MS, run);
        // A refusal that cannot succeed later (an engine without the wait) leaves the figures as last read.
      })();
    };
    run();
    return () => {
      disposed = true; clearTimeout(timer);
      if (waiting) void bridge.cancel(waiting).catch(() => undefined);
    };
    // The signature names exactly the loaded builds.
  }, [bridge, signature, visible, enabled, nudge]);
  return { summaries, error };
}
