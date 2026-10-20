import { useEffect, useRef, useState } from 'react';
import type { Build, EngineProgress, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import { validProgressStep } from '../shared/progress';
import { isDownloadWorkspace } from '../shared/download';
import { isInventoryWorkspace } from '../shared/inventory';
import { workspaceMatches, type DiscoveryBridge, type OpenParams } from '../shared/bridge';
export type Opening = {
  id: string; build: Build; sourceRegion: string; label: string;
  status: 'pending' | 'stopping' | 'succeeded' | 'failed' | 'canceled';
  progress: EngineProgress | null; result: WorkspaceSnapshot | null;
  error: { code: string; message: string; retryable: boolean } | null;
};
/** productId, when given, is the product a finished opening may still navigate for; without it any product may. */
export function useWorkspaceFlow(bridge: DiscoveryBridge, productId?: string) {
  const [operation, setOperation] = useState<Opening | null>(null);
  const [view, setView] = useState<'discovery' | 'preparation' | 'home'>('discovery');
  const [notice, setNotice] = useState('');
  const active = useRef<Opening | null>(null);
  const attached = useRef<string | null>(null);
  const currentProduct = useRef(productId); currentProduct.current = productId;
  const productMatches = (id: string) => currentProduct.current === undefined || currentProduct.current === id;
  const alive = useRef(true);
  const origin = useRef<{ element: HTMLElement | null; scroll: number }>({ element: null, scroll: 0 });
  const frame = useRef<number | null>(null);
  function update(next: Opening) { active.current = next; if (alive.current) setOperation(next); }
  useEffect(() => {
    alive.current = true;
    const off = bridge.onProgress(progress => {
      const op = active.current;
      if (!validProgressStep(progress.step)) return;
      if (op?.id === progress.requestId && (op.status === 'pending' || op.status === 'stopping') && (!op.progress || progress.sequence > op.progress.sequence)) update({ ...op, progress });
    });
    return () => { alive.current = false; off(); if (frame.current !== null) cancelAnimationFrame(frame.current); };
  }, [bridge]);
  function rememberOrigin() { const element = document.activeElement as HTMLElement | null; if (element?.closest('.discovery-screen:not([hidden])')) origin.current = { element, scroll: window.scrollY }; }
  function detach() {
    attached.current = null;
    if (frame.current !== null) { cancelAnimationFrame(frame.current); frame.current = null; }
    setView('discovery');
    // The mounted discovery tree becomes visible in the next commit.
    requestAnimationFrame(() => {
      if (!alive.current || attached.current) return;
      const target = origin.current.element;
      if (target?.isConnected && !target.closest('[hidden]')) target.focus({ preventScroll: true });
      else document.querySelector<HTMLElement>('.discovery-screen:not([hidden]) .build-row[aria-pressed="true"], .discovery-screen:not([hidden]) #builds-heading')?.focus({ preventScroll: true });
      window.scrollTo(0, origin.current.scroll);
    });
  }
  function showActive(expectedId?:string) {
    const op = active.current; if (!op||expectedId!==undefined&&op.id!==expectedId) return false;
    rememberOrigin(); attached.current = op.id;
    if (op.status === 'succeeded' && op.result && productMatches(op.build.productId)) setView('home');
    else setView('preparation');
    return true;
  }
  function enterVerified() { const op = active.current; if (op?.result && op.status === 'succeeded') { attached.current = op.id; setView('home'); } }
  async function open(build: Build, sourceRegion: string, label: string) {
    const previous = active.current;
    if (previous && (previous.status === 'pending' || previous.status === 'stopping')) {
      const same = previous.build.id === build.id && previous.build.productId === build.productId && previous.sourceRegion === sourceRegion;
      setNotice(same ? '' : `Another build is opening: ${previous.build.productId} · ${previous.build.version}. No new opening request was started.`);
      showActive(); return;
    }
    rememberOrigin(); setNotice('');
    const id = crypto.randomUUID();
    const op: Opening = { id, build, sourceRegion, label, status: 'pending', progress: null, result: null, error: null };
    update(op); attached.current = id;
    // One browser frame only decides whether a still-pending screen needs painting.
    // It never delays a completed/cached response.
    frame.current = requestAnimationFrame(() => { frame.current = null; if (alive.current && active.current?.id === id && attached.current === id && active.current.status === 'pending') setView('preparation'); });
    const params: OpenParams = { productId: build.productId, buildIdentity: build.id, sourceRegion };
    try {
      const reply = await bridge.open(params, id);
      if (!alive.current || active.current?.id !== id) return;
      if (reply.ok && !workspaceMatches(reply.value, params)) {
        update({ ...active.current, status: 'failed', error: { code: 'PROTOCOL_ERROR', message: 'The returned build or source does not match the requested exact context.', retryable: false } });
      } else if (reply.ok && !isInventoryWorkspace(reply.value) && !isDownloadWorkspace(reply.value)) {
        update({ ...active.current, status: 'failed', progress: null, error: { code: 'INVENTORY_REQUIRED', message: 'A supported preparation result is required before Home. Configuration-only preparation is no longer sufficient.', retryable: true } });
      } else if (reply.ok) update({ ...active.current, status: 'succeeded', result: reply.value, progress: null });
      else update({ ...active.current, status: reply.error.code === 'CANCELED' ? 'canceled' : 'failed', error: reply.error, progress: null });
      if (attached.current === id && productMatches(build.productId)) setView(active.current?.status === 'succeeded' ? 'home' : 'preparation');
    } catch {
      if (!alive.current || active.current?.id !== id) return;
      update({ ...active.current, status: 'failed', progress: null, error: { code: 'ENGINE_UNAVAILABLE', message: 'The local engine connection failed. Restart VibeMiner to reconnect.', retryable: true } });
      if (attached.current === id && productMatches(build.productId)) setView('preparation');
    }
  }
  async function cancel(expectedId?:string) {
    const op = active.current; if (!op || expectedId!==undefined&&op.id!==expectedId || op.status !== 'pending') return;
    update({ ...op, status: 'stopping' });
    try {
      const reply = await bridge.cancel(op.id);
      if (!reply.ok && active.current?.id === op.id && active.current.status === 'stopping') update({ ...active.current, error: reply.error });
    } catch { if (active.current?.id === op.id && active.current.status === 'stopping') update({ ...active.current, error: { code: 'CANCEL_UNAVAILABLE', message: 'Cancellation could not be delivered. Waiting for preparation to stop safely.', retryable: false } }); }
  }
  return { operation, view, notice, open, cancel, detach, showActive, enterVerified,currentWorkspaceMatches:(expected:{productId:string;buildIdentity:string;sourceRegion:string;generation:string})=>{const op=active.current,s=op?.result;return !!op&&attached.current===op.id&&op.status==='succeeded'&&!!s&&s.build.productId===expected.productId&&s.build.id===expected.buildIdentity&&s.sourceReference.region===expected.sourceRegion&&s.sourceReference.generation===expected.generation;} };
}
