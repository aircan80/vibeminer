import { useEffect, useState } from 'react';
import type { DiscoveryBridge } from '../shared/bridge';
import { listfileStatusMatches, type CommunityListfileStatus } from '../shared/settings';
import { readableBytes } from '../shared/inventory';
import type { ActivityTask } from './download-activity-state';

/** Read once a second while the listfile download works, every five seconds otherwise (Files may start it). */
export const LISTFILE_POLL_MS = 1000, LISTFILE_IDLE_MS = 5000;
const percentOf = (done: string, all: string) => BigInt(all) > 0n ? Number(BigInt(done) * 100n / BigInt(all)) : null;
/** Stage words shared by Settings and Downloads & activity. Importing is "Processing", with bytes beside the percentage. */
export function listfileStage(s: CommunityListfileStatus): string | null {
  if (s.activity === 'verifying') return 'Checking the kept copy';
  if (s.activity === 'checking') return 'Asking GitHub for the newest release';
  if (s.activity === 'downloading') return `Downloading release ${s.pendingTag}`;
  if (s.activity === 'importing') return `Processing release ${s.pendingTag}`;
  return null;
}
export function listfileMeasurement(s: CommunityListfileStatus): { label: string; percent: number | null } | null {
  if (s.completed === null || s.total === null) return null;
  const percent = percentOf(s.completed, s.total), bytes = `${readableBytes(s.completed)} of ${readableBytes(s.total)}`;
  return { label: s.activity === 'importing' && percent !== null ? `${percent}% · ${bytes}` : bytes, percent };
}

/**
 * The community listfile download as one Downloads & activity task (D105). It shows while the engine works on it, and
 * after a failed attempt until a later one succeeds or the reader dismisses that failure. Stop turns the switch off,
 * which is what stops the download (its bytes are kept); Try again asks the engine for a new attempt at once.
 */
export function useListfileTask(bridge: DiscoveryBridge, enabled: boolean) {
  const [status, setStatus] = useState<CommunityListfileStatus | null>(null);
  const [again, setAgain] = useState(0);
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    const read = bridge.listfileRead;
    if (!enabled || !read) { setStatus(null); return; }
    let disposed = false, timer: ReturnType<typeof setTimeout> | undefined;
    const run = async () => {
      let working = false;
      try {
        const reply = await read();
        if (disposed) return;
        if (reply.ok && listfileStatusMatches(reply.value)) { setStatus(reply.value); working = reply.value.activity !== 'idle'; }
      } catch { /* The next read tries again. */ }
      if (!disposed) timer = setTimeout(() => void run(), working ? LISTFILE_POLL_MS : LISTFILE_IDLE_MS);
    };
    void run();
    const changed = (event: Event) => { if ((event as CustomEvent<{ name?: string }>).detail?.name === 'communityListfile') setAgain(n => n + 1); };
    const asked = () => setAgain(n => n + 1);
    window.addEventListener('vibeminer-settings-changed', changed); window.addEventListener('vibeminer-listfile-asked', asked);
    return () => { disposed = true; clearTimeout(timer); window.removeEventListener('vibeminer-settings-changed', changed); window.removeEventListener('vibeminer-listfile-asked', asked); };
  }, [bridge, enabled, again]);
  const problemKey = status?.problem ? `${status.problem.code}|${status.problem.message}|${status.release?.tag ?? ''}` : null;
  let task: ActivityTask | null = null;
  if (status && status.enabled && (status.activity !== 'idle' || problemKey && problemKey !== dismissed)) {
    const failed = status.activity === 'idle', release = status.release;
    task = {
      key: 'listfile:wow-listfile', kind: 'research', title: 'Community file names · wow-listfile', identity: { productId: '', buildIdentity: '', sourceRegion: '', detail: 'From github.com/wowdev/wow-listfile releases. Names are labels in Files; they never change what is downloaded or verified.' },
      status: failed ? 'failed' : 'running',
      stage: failed ? release ? `Couldn’t get a newer release. Release ${release.tag} is still in use; VibeMiner tries again after 15 minutes.` : 'Couldn’t get the community file names. Files says Name not known.' : listfileStage(status)!,
      measurement: failed ? null : listfileMeasurement(status),
      problem: status.problem ? { code: status.problem.code, message: status.problem.message, retryable: status.problem.retryable } : null,
      actions: failed ? [...(bridge.listfileCheck ? [{ id: 'retry', label: 'Try again now', disabled: busy }] : []), { id: 'dismiss', label: 'Dismiss outcome' }]
        : bridge.settingsSet ? [{ id: 'stop', label: 'Turn off community file names', disabled: busy }] : [],
      details: ['Turning community file names off stops the download and keeps what it has, so turning them on again continues it.'],
    };
  }
  async function act(action: string) {
    if (busy) return;
    if (action === 'dismiss') { setDismissed(problemKey); return; }
    setBusy(true);
    try {
      if (action === 'retry' && bridge.listfileCheck) { const reply = await bridge.listfileCheck(); if (reply.ok && listfileStatusMatches(reply.value)) { setStatus(reply.value); window.dispatchEvent(new CustomEvent('vibeminer-listfile-changed')); } }
      if (action === 'stop' && bridge.settingsSet) {
        const reply = await bridge.settingsSet({ name: 'communityListfile', value: false });
        if (reply.ok) window.dispatchEvent(new CustomEvent('vibeminer-settings-changed', { detail: { name: 'communityListfile', from: 'activity' } }));
      }
    } catch { /* The task stays as it was; the next read shows the engine's state. */ }
    finally { setBusy(false); setAgain(n => n + 1); }
  }
  return { status, task, act };
}
