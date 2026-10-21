import { useEffect, useRef, useState } from 'react';
import type { Build, BuildDownloadSummary, DownloadSummariesParams } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import { MAX_SUMMARY_BUILDS, summariesMatch, summaryKey } from '../shared/download-summary';

/**
 * A figure that is still changing on its own: a running Download all pass, a first count of content on disk, or recorded
 * changes the engine has not applied yet (a pass that just finished can otherwise stop one change short of 100%).
 */
export const summaryMoving = (s: BuildDownloadSummary) => !!s.download &&
  (s.download.running || s.download.countState === 'counting' || s.download.changesPending);

/** A failed read is tried again after this long, keeping the last confirmed figures meanwhile. */
export const SUMMARY_RETRY_MS = 2000;

/**
 * Stored figures for the loaded discovery window (D065). Reads are local and never start work. They run when the window,
 * visibility or saved download intents change, and repeat at most once a second, never overlapping, only while a figure is
 * still moving. A failed read keeps the last confirmed figures and is retried after {@link SUMMARY_RETRY_MS}.
 */
export function useDownloadSummaries(bridge: DiscoveryBridge, builds: readonly Build[], visible: boolean, enabled: boolean, nudge: string) {
  const [summaries, setSummaries] = useState<ReadonlyMap<string, BuildDownloadSummary>>(() => new Map());
  const [error, setError] = useState('');
  const previous = useRef<Promise<void>>(Promise.resolve());
  const keys = builds.map(build => summaryKey(build.productId, build.id));
  const signature = keys.join('|');
  useEffect(() => {
    const read = bridge.downloadSummaries;
    if (!enabled || !read || !visible || !builds.length) return;
    const wanted = [...new Map(builds.map(build => [summaryKey(build.productId, build.id), { productId: build.productId, buildIdentity: build.id }])).values()];
    let disposed = false, timer: ReturnType<typeof setTimeout> | undefined;
    const run = () => {
      const prior = previous.current;
      previous.current = (async () => {
        await prior.catch(() => undefined);
        if (disposed) return;
        const started = Date.now();
        try {
          const next = new Map<string, BuildDownloadSummary>();
          for (let start = 0; start < wanted.length; start += MAX_SUMMARY_BUILDS) {
            const params = { builds: wanted.slice(start, start + MAX_SUMMARY_BUILDS) } as DownloadSummariesParams;
            const reply = await read(params);
            if (disposed) return;
            if (!reply.ok) throw new Error(`${reply.error.message} (${reply.error.code})`);
            if (!summariesMatch(reply.value, params)) throw new Error('The stored download figures did not match the listed builds.');
            for (const summary of reply.value.summaries) next.set(summaryKey(summary.productId, summary.buildIdentity), summary);
          }
          setSummaries(next); setError('');
          if ([...next.values()].some(summaryMoving)) timer = setTimeout(run, Math.max(0, 1000 - (Date.now() - started)));
        } catch (failure) {
          if (disposed) return;
          setError(failure instanceof Error ? failure.message : 'Stored download figures are unavailable.');
          timer = setTimeout(run, SUMMARY_RETRY_MS);
        }
      })();
    };
    run();
    return () => { disposed = true; clearTimeout(timer); };
    // The signature names exactly the loaded builds.
  }, [bridge, signature, visible, enabled, nudge]);
  return { summaries, error };
}
