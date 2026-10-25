import { useCallback, useEffect, useRef, useState } from 'react';
import type { DownloadCheckParams, DownloadCheckResult, EngineProgress } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';

/** What a Check covers: the measured ledger of one exact build and region, and how many files Open never assessed (D093). */
export type DownloadCheckIntent = Readonly<DownloadCheckParams & { buildVersion: string; buildConfig: string; files: string }>;
export type DownloadCheckOperation = Readonly<{
  id: string;
  intent: DownloadCheckIntent;
  status: 'pending' | 'stopping' | 'succeeded' | 'failed' | 'canceled';
  progress: EngineProgress | null;
  result: DownloadCheckResult | null;
  error: { code: string; message: string; retryable: boolean } | null;
}>;
export type DownloadCheckController = ReturnType<typeof useDownloadCheck>;

/**
 * The one Check of files not checked at Open (D093), owned above the build's views like table and Items loading, so it
 * shows in Downloads & activity, keeps running when Files is left (navigation is not cancellation) and is stopped from its
 * note or its task. The engine runs one Check at a time; a second start while one is pending is refused here.
 */
export function useDownloadCheck(bridge: DiscoveryBridge) {
  const [operation, setOperation] = useState<DownloadCheckOperation | null>(null);
  const running = useRef<string | null>(null);
  useEffect(() => typeof bridge.onProgress !== 'function' ? undefined : bridge.onProgress(progress => {
    if (progress.requestId !== running.current) return;
    setOperation(op => op && op.id === progress.requestId && (op.status === 'pending' || op.status === 'stopping') ? { ...op, progress } : op);
  }), [bridge]);
  const start = useCallback(async (intent: DownloadCheckIntent) => {
    if (!bridge.downloadCheck || running.current) return;
    const id = `download_check_${crypto.randomUUID()}`;
    running.current = id;
    setOperation({ id, intent, status: 'pending', progress: null, result: null, error: null });
    let reply: Awaited<ReturnType<NonNullable<DiscoveryBridge['downloadCheck']>>> | null = null;
    try { reply = await bridge.downloadCheck({ productId: intent.productId, buildIdentity: intent.buildIdentity, sourceRegion: intent.sourceRegion, ledgerId: intent.ledgerId }, id); }
    catch { reply = null; }
    finally { if (running.current === id) running.current = null; }
    setOperation(op => op?.id !== id ? op
      : reply?.ok ? { ...op, status: 'succeeded', result: reply.value, error: null }
      : reply && !reply.ok && reply.error.code === 'CANCELED' ? { ...op, status: 'canceled', error: null }
      : { ...op, status: 'failed', error: reply && !reply.ok ? reply.error : { code: 'ENGINE_UNAVAILABLE', message: 'The check could not be completed.', retryable: true } });
  }, [bridge]);
  const stop = useCallback((id: string) => {
    if (running.current !== id) return;
    setOperation(op => op?.id === id && op.status === 'pending' ? { ...op, status: 'stopping' } : op);
    void bridge.cancel(id).catch(() => undefined);
  }, [bridge]);
  const dismiss = useCallback((id: string) => setOperation(op => op?.id === id && op.status !== 'pending' && op.status !== 'stopping' ? null : op), []);
  return { available: !!bridge.downloadCheck, operation, start, stop, dismiss };
}
