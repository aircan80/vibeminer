import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { CommunityListfileStatus } from '../src/shared/settings';
import { listfileMeasurement, listfileStage, useListfileTask } from '../src/renderer/useCommunityListfile';
import statuses from '../../../contracts/examples/community-listfile-status.json';

// D105 (#21): the community listfile download is one Downloads & activity task.
const [ready, downloading, importing, offline, off, notStarted] = statuses as CommunityListfileStatus[];
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const bridge = (read: () => Promise<BridgeReply<CommunityListfileStatus>>, extra: Partial<DiscoveryBridge> = {}) => ({ listfileRead: vi.fn(read), ...extra }) as unknown as DiscoveryBridge;
afterEach(cleanup);

describe('community listfile activity', () => {
  it('names each stage, and calls importing Processing with bytes beside the percentage', () => {
    expect(listfileStage(downloading)).toBe('Downloading release 202601010000');
    expect(listfileMeasurement(downloading)).toEqual({ label: '50.00 MiB of 146.66 MiB', percent: 34 });
    expect(listfileStage(importing)).toBe('Processing release 202601080000');
    expect(listfileMeasurement(importing)!.label).toBe('0% · 1.00 MiB of 146.66 MiB');
    expect(listfileStage(ready)).toBeNull(); expect(listfileMeasurement(ready)).toBeNull();
  });
  it('is a running task while the download works, whose Stop turns the switch off', async () => {
    const settingsSet = vi.fn(async () => success({} as never));
    const heard = vi.fn(); window.addEventListener('vibeminer-settings-changed', heard);
    const b = bridge(async () => success(downloading), { settingsSet }); const { result } = renderHook(() => useListfileTask(b, true));
    await waitFor(() => expect(result.current.task).not.toBeNull());
    const task = result.current.task!;
    expect(task).toMatchObject({ key: 'listfile:wow-listfile', status: 'running', stage: 'Downloading release 202601010000', measurement: { label: '50.00 MiB of 146.66 MiB', percent: 34 } });
    expect(task.actions).toEqual([{ id: 'stop', label: 'Turn off community file names', disabled: false }]);
    await act(async () => { await result.current.act('stop'); });
    expect(settingsSet).toHaveBeenCalledWith({ name: 'communityListfile', value: false });
    expect(heard).toHaveBeenCalled();
    window.removeEventListener('vibeminer-settings-changed', heard);
  });
  it('keeps a failure until it is dismissed, with Try again now', async () => {
    const listfileCheck = vi.fn(async () => success(downloading));
    const b = bridge(async () => success(offline), { listfileCheck }); const { result } = renderHook(() => useListfileTask(b, true));
    await waitFor(() => expect(result.current.task?.status).toBe('failed'));
    expect(result.current.task!.stage).toBe('Couldn’t get the community file names. Files says Name not known.');
    expect(result.current.task!.problem).toMatchObject({ code: 'LISTFILE_FETCH_OFFLINE' });
    expect(result.current.task!.actions.map(a => a.label)).toEqual(['Try again now', 'Dismiss outcome']);
    await act(async () => { await result.current.act('retry'); });
    expect(listfileCheck).toHaveBeenCalledTimes(1);
    const b2 = bridge(async () => success({ ...ready, problem: offline.problem })); const second = renderHook(() => useListfileTask(b2, true));
    await waitFor(() => expect(second.result.current.task?.stage).toBe('Couldn’t get a newer release. Release 202601010000 is still in use; VibeMiner tries again after 15 minutes.'));
    await act(async () => { await second.result.current.act('dismiss'); });
    expect(second.result.current.task).toBeNull();
  });
  it('shows nothing when idle, off, never started or without the capability', async () => {
    for (const status of [ready, off, notStarted]) {
      const b = bridge(async () => success(status)); const { result } = renderHook(() => useListfileTask(b, true));
      await waitFor(() => expect(result.current.status).not.toBeNull());
      expect(result.current.task).toBeNull();
    }
    const read = vi.fn(async () => success(downloading));
    const b = { listfileRead: read } as unknown as DiscoveryBridge; const { result } = renderHook(() => useListfileTask(b, false));
    expect(result.current.task).toBeNull(); expect(read).not.toHaveBeenCalled();
  });
});
