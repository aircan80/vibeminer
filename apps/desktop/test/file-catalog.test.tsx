import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { FileCatalogStatus, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import workspace from '../../../contracts/examples/download-workspace.json';
import states from '../../../contracts/examples/file-catalog-status.json';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import { fileCatalogMatches, validFileCatalog } from '../src/shared/inventory';
import { useFileCatalog } from '../src/renderer/useFileCatalog';

const w = workspace as unknown as WorkspaceSnapshot;
const other = { ...w, build: { ...w.build, id: 'e'.repeat(64) } } as WorkspaceSnapshot;
const examples = states as FileCatalogStatus[];
const forBuild = (snapshot: WorkspaceSnapshot, status: FileCatalogStatus): FileCatalogStatus =>
  ({ ...status, productId: snapshot.build.productId, buildIdentity: snapshot.build.id, sourceRegion: snapshot.sourceReference.region });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
afterEach(() => { cleanup(); vi.useRealTimers(); });

it('accepts exactly one build and region, and only replies for them with counts that agree', () => {
  const params = { productId: 'wow', buildIdentity: 'd'.repeat(64), sourceRegion: 'us' };
  expect(validFileCatalog(params)).toBe(true);
  for (const invalid of [{ ...params, generation: '1' }, { ...params, sourceRegion: 'US' }, { productId: 'wow', buildIdentity: 'd'.repeat(64) }, null]) expect(validFileCatalog(invalid)).toBe(false);
  for (const status of examples) expect(fileCatalogMatches(status, params)).toBe(true);
  const [writing, , , , ready] = examples;
  expect(fileCatalogMatches({ ...ready, sourceRegion: 'eu' }, params)).toBe(false);
  expect(fileCatalogMatches({ ...writing, completed: '9999999' }, params)).toBe(false);
  expect(fileCatalogMatches({ ...ready, catalog: { ...ready.catalog!, rootFileCount: '9999999' } }, params)).toBe(false);
});

it('drops a reply for the build it left and polls only while the catalog is pending', async () => {
  const first = deferred<BridgeReply<FileCatalogStatus>>();
  const fileCatalog = vi.fn<(params: { buildIdentity: string }) => Promise<BridgeReply<FileCatalogStatus>>>()
    .mockReturnValueOnce(first.promise)
    .mockResolvedValueOnce({ ok: true, value: forBuild(other, examples[0]) })
    .mockResolvedValue({ ok: true, value: forBuild(other, examples[4]) });
  const bridge = { fileCatalog } as unknown as DiscoveryBridge;
  const { result, rerender } = renderHook(({ snapshot }) => useFileCatalog(bridge, snapshot, true), { initialProps: { snapshot: w } });
  rerender({ snapshot: other });
  // The first build's late reply arrives after the switch and is never shown for the second build.
  await act(async () => first.resolve({ ok: true, value: forBuild(w, examples[4]) }));
  await waitFor(() => expect(result.current.status?.state).toBe('pending'));
  expect(result.current.status?.buildIdentity).toBe(other.build.id);
  await waitFor(() => expect(result.current.status?.state).toBe('ready'));
  const calls = fileCatalog.mock.calls.length;
  await new Promise(done => setTimeout(done, 1200));
  expect(fileCatalog.mock.calls.length).toBe(calls);
});

it('keeps the last confirmed status when a read fails and tries again', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  const fileCatalog = vi.fn()
    .mockResolvedValueOnce({ ok: true, value: forBuild(w, examples[0]) })
    .mockResolvedValueOnce({ ok: false, error: { code: 'ENGINE_UNAVAILABLE', message: 'Authored outage.', retryable: true } })
    .mockResolvedValue({ ok: true, value: forBuild(w, examples[4]) });
  const bridge = { fileCatalog } as unknown as DiscoveryBridge;
  const { result } = renderHook(() => useFileCatalog(bridge, w, true));
  await waitFor(() => expect(result.current.status?.stage).toBe('writing'));
  await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
  await waitFor(() => expect(result.current.error).toMatch(/ENGINE_UNAVAILABLE/));
  expect(result.current.status?.stage).toBe('writing');
  await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
  await waitFor(() => expect(result.current.status?.state).toBe('ready'));
  expect(result.current.error).toBe('');
});

it('never reads for a legacy full catalog or without the engine capability', async () => {
  const fileCatalog = vi.fn();
  renderHook(() => useFileCatalog({ fileCatalog } as unknown as DiscoveryBridge, w, false));
  renderHook(() => useFileCatalog({} as DiscoveryBridge, w, true));
  await new Promise(done => setTimeout(done, 50));
  expect(fileCatalog).not.toHaveBeenCalled();
});
