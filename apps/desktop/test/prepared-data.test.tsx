import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SettingsView, type SettingsOrigin } from '../src/renderer/Settings';
import { EngineClient, type EngineProcess } from '../src/main/engine-client';
import { preparedDataFreedMatches, preparedDataListMatches, validPreparedDataFree, type PreparedDataFreed, type PreparedDataList } from '../src/shared/settings';
import { fileCatalogMatches, fileCatalogSetMatches } from '../src/shared/inventory';
import { CatalogNotPrepared, catalogFoot } from '../src/renderer/DownloadObjects';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { FileCatalogStatus, SettingsSnapshot, StorageMeasurement } from '../../../contracts/generated/discovery';
import example from '../../../contracts/examples/prepared-data.json';
import freedExample from '../../../contracts/examples/prepared-data-freed.json';
import states from '../../../contracts/examples/file-catalog-status.json';

// D085 (#46): Settings › Storage lists what each opened build keeps and frees one build's file list.
const listed = example as PreparedDataList, freed = freedExample as PreparedDataFreed;
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const refused = (code: string, message: string, retryable = true) => ({ ok: false as const, error: { code, message, retryable } });
const fresh: SettingsSnapshot = { formatVersion: 1, file: 'absent', problem: null, lastChangedAt: null, communityBuildHistory: { value: true, defaultValue: true, source: 'default', problem: null }, fetchTableDefinitions: { value: true, defaultValue: true, source: 'default', problem: null }, communityListfile: { value: true, defaultValue: true, source: 'default', problem: null }, storage: { dataDirectory: '/authored/profile/engine-v1' }, downloadLimits: { parallelDownloads: 4, backgroundDownloads: 2, bandwidthCapBytesPerSecond: null, adjustable: false } };
const measured: StorageMeasurement = { bytes: '51749982208', files: '1204', skipped: '0', startedAt: '2026-10-20T13:12:00.0000000+00:00', measuredAt: '2026-10-20T13:12:04.0000000+00:00' };
const origin: SettingsOrigin = { backLabel: 'Back to Discovery', returnsTo: 'Discovery', kept: 'Discovery kept as you left it' };
const afterFree: PreparedDataList = { ...listed, builds: listed.builds.map((b, i) => i === 0 ? { ...b, fileCatalog: { state: 'removed', bytes: '0', files: 0 } } : b) };
function bridgeMock(extra: Partial<DiscoveryBridge> = {}): DiscoveryBridge {
  return {
    cancel: vi.fn(async () => success({ requested: true })), onProgress: vi.fn(() => () => {}),
    settingsRead: vi.fn(async () => success(fresh)), storageMeasure: vi.fn(async () => success(measured)),
    storageBuilds: vi.fn<NonNullable<DiscoveryBridge['storageBuilds']>>().mockResolvedValueOnce(success(listed)).mockResolvedValue(success(afterFree)),
    storageFree: vi.fn(async () => success(freed)),
    ...extra,
  } as unknown as DiscoveryBridge;
}
function row(version: string): HTMLElement {
  const found = [...document.querySelectorAll<HTMLElement>('.st-build')].find(li => li.querySelector('h4')!.textContent!.startsWith(version));
  if (!found) throw new Error(`No prepared data row for ${version}`);
  return found;
}
beforeEach(() => { vi.spyOn(window, 'scrollTo').mockImplementation(() => {}); Element.prototype.scrollIntoView = vi.fn(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('prepared data payloads', () => {
  it('admit one exact build to free and only replies that agree with themselves', () => {
    const params = { productId: 'wow', buildIdentity: 'd'.repeat(64) };
    expect(validPreparedDataFree(params)).toBe(true);
    for (const bad of [null, [], {}, { productId: 'wow' }, { ...params, sourceRegion: 'us' }, { ...params, buildIdentity: 'D'.repeat(64) }, { productId: 'WOW', buildIdentity: 'd'.repeat(64) }])
      expect(validPreparedDataFree(bad)).toBe(false);
    expect(preparedDataListMatches(listed)).toBe(true);
    const [ready] = listed.builds;
    for (const broken of [
      { ...ready, fileCatalog: { ...ready.fileCatalog, files: 0 } },
      { ...ready, fileCatalog: { state: 'none' as const, bytes: '4096', files: 1 } },
      { ...ready, tables: { ...ready.tables, sharedBytes: '999999999999' } },
      { ...ready, downloads: { state: 'notMeasured' as const, storedObjects: '1', storedBytes: '1' } },
      { ...ready, downloads: { state: 'current' as const, storedObjects: null, storedBytes: null } },
      { ...ready, lastOpenedAt: 'yesterday' },
    ]) expect(preparedDataListMatches({ ...listed, builds: [broken] }), JSON.stringify(broken).slice(0, 80)).toBe(false);
    expect(preparedDataListMatches({ ...listed, builds: [ready, ready] })).toBe(false);
    expect(preparedDataFreedMatches(freed, params)).toBe(true);
    expect(preparedDataFreedMatches(freed, { ...params, buildIdentity: 'e'.repeat(64) })).toBe(false);
    expect(preparedDataFreedMatches({ ...freed, fileCatalog: { state: 'ready', bytes: '0', files: 0 } }, params)).toBe(false);
    expect(preparedDataFreedMatches({ ...freed, freedFiles: 0 }, params)).toBe(false);
  });
  it('treat a removed file catalog as its own state that Resume prepares again', () => {
    const removed = (states as FileCatalogStatus[]).find(s => s.state === 'removed')!;
    expect(fileCatalogMatches(removed, { productId: removed.productId, buildIdentity: removed.buildIdentity, sourceRegion: removed.sourceRegion })).toBe(true);
    expect(fileCatalogSetMatches({ ...removed, state: 'pending', stage: 'queued' }, { productId: removed.productId, buildIdentity: removed.buildIdentity, action: 'resume' })).toBe(true);
    expect(catalogFoot(removed)).toBe('File list freed');
  });
  it('Files says a freed file list was freed and prepares it again only when asked', async () => {
    const removed = (states as FileCatalogStatus[]).find(s => s.state === 'removed')!;
    const onAction = vi.fn(async () => {});
    render(<CatalogNotPrepared withObjects catalog={{ status: removed, error: '', busy: false, taskKey: null, onAction, onRetryRead: async () => {} }}/>);
    const note = screen.getByRole('note', { name: 'File list freed' });
    expect(note).toHaveTextContent('It was freed in Settings to save disk space.');
    fireEvent.click(within(note).getByRole('button', { name: /^Prepare file list again/ }));
    await waitFor(() => expect(onAction).toHaveBeenCalledWith('resume'));
  });
});

class Child extends EventEmitter implements EngineProcess {
  stdout = new PassThrough(); stderr = new PassThrough(); requests: { id: string; method: string; params: unknown }[] = [];
  stdin = new Writable({ write: (chunk, _encoding, done) => { this.requests.push(JSON.parse(chunk.toString())); done(); } });
  kill = vi.fn(() => true);
  reply(id: string, result: unknown) { this.stdout.write(JSON.stringify({ protocolVersion: 1, id, result }) + '\n'); }
}
describe('engine client prepared data methods', () => {
  const children: Child[] = [];
  afterEach(() => { for (const child of children.splice(0)) child.emit('exit', 0); });
  async function connected(capabilities: string[]) { const child = new Child(); children.push(child); const client = new EngineClient(child); const hello = client.hello(); child.reply(child.requests[0].id, { protocolVersion: 1, engineVersion: 'authored', capabilities }); await hello; return { child, client }; }
  it('needs prepared-data-v1, refuses invalid frees before sending and checks the reply names the build asked for', async () => {
    const old = await connected(['settings-v1']);
    await expect(old.client.request('settings.storage.builds', {})).rejects.toMatchObject({ code: 'UNSUPPORTED' });
    const { child, client } = await connected(['settings-v1', 'prepared-data-v1']);
    await expect(client.request('settings.storage.free', { productId: 'wow' })).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
    await expect(client.request('settings.storage.builds', { limit: 1 })).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
    expect(child.requests).toHaveLength(1);
    const list = client.request('settings.storage.builds', {}, 'list'); child.reply('list', listed); await expect(list).resolves.toEqual(listed);
    const ok = client.request('settings.storage.free', { productId: 'wow', buildIdentity: 'd'.repeat(64) }, 'free'); child.reply('free', freed); await expect(ok).resolves.toEqual(freed);
    const wrong = client.request('settings.storage.free', { productId: 'wow', buildIdentity: 'e'.repeat(64) }, 'wrong'); child.reply('wrong', freed);
    await expect(wrong).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
  });
});

describe('Prepared data in Settings', () => {
  it('shows each opened build’s measured file list, table rows and downloads, and what Free keeps', async () => {
    render(<SettingsView bridge={bridgeMock()} origin={origin} onBack={() => {}}/>);
    const ready = await waitFor(() => row('1.2.3.12345'));
    expect(ready).toHaveTextContent('1.14 GiB');
    expect(ready).toHaveTextContent('1,234,567,168 bytes · ready');
    expect(ready).toHaveTextContent('70.00 MiB in 4 files, 50.00 MiB also used by other builds · kept');
    expect(ready).toHaveTextContent('48.19 GiB · shared with builds that list the same files · kept');
    expect(within(ready).getByRole('button', { name: 'Free file list…' })).toBeVisible();
    expect(within(row('1.2.3.12300')).getByRole('button', { name: 'Stop and free file list…' })).toBeVisible();
    expect(row('1.2.0.12000')).toHaveTextContent('Freed · prepared again on the next Open');
    expect(within(row('1.2.0.12000')).queryByRole('button')).toBeNull();
    expect(row('cccccccccccc')).toHaveTextContent('Not prepared');
    expect(row('cccccccccccc')).toHaveTextContent('Not measured for this build');
    expect(document.getElementById('settings-row-prepared')).toHaveTextContent('1.52 GiB across 4 builds');
  });
  it('asks before freeing, Escape keeps the list without leaving Settings, and Free reports what it freed', async () => {
    const onBack = vi.fn(), changed = vi.fn(), bridge = bridgeMock();
    render(<SettingsView bridge={bridge} origin={origin} onBack={onBack} onPreparedDataChanged={changed}/>);
    const free = await waitFor(() => within(row('1.2.3.12345')).getByRole('button', { name: 'Free file list…' }));
    fireEvent.click(free);
    const confirm = within(row('1.2.3.12345')).getByRole('group', { name: /Free 1\.14 GiB\? Until it’s prepared again, the next time you open this build, Files has no FileDataID search or names for it\. Table rows and downloaded files stay\./ });
    expect(within(confirm).getByRole('button', { name: 'Free 1.14 GiB' })).toHaveFocus();
    fireEvent.keyDown(within(confirm).getByRole('button', { name: 'Keep' }), { key: 'Escape' });
    expect(onBack).not.toHaveBeenCalled();
    expect(within(row('1.2.3.12345')).getByRole('button', { name: 'Free file list…' })).toHaveFocus();
    expect(bridge.storageFree).not.toHaveBeenCalled();
    fireEvent.click(within(row('1.2.3.12345')).getByRole('button', { name: 'Free file list…' }));
    await act(async () => { fireEvent.click(within(row('1.2.3.12345')).getByRole('button', { name: 'Free 1.14 GiB' })); });
    expect(bridge.storageFree).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: 'd'.repeat(64) });
    await waitFor(() => expect(row('1.2.3.12345')).toHaveTextContent('Freed 1.14 GiB'));
    expect(changed).toHaveBeenCalledTimes(1);
    // The list and the folder size are measured again.
    await waitFor(() => expect(row('1.2.3.12345')).toHaveTextContent('Freed · prepared again on the next Open'));
    expect(bridge.storageBuilds).toHaveBeenCalledTimes(2);
    expect(bridge.storageMeasure).toHaveBeenCalledTimes(2);
    expect(row('1.2.3.12345').querySelector('h4')).toHaveFocus();
  });
  it('says a free that left files is freed with how many are left, and offers one way to try again', async () => {
    // The files left behind are still listed, so Free stays available.
    const left: PreparedDataList = { ...listed, builds: listed.builds.map((b, i) => i === 0 ? { ...b, fileCatalog: { state: 'removed', bytes: '4096', files: 1 } } : b) };
    const builds = vi.fn<NonNullable<DiscoveryBridge['storageBuilds']>>().mockResolvedValueOnce(success(listed)).mockResolvedValue(success(left));
    const bridge = bridgeMock({ storageBuilds: builds, storageFree: vi.fn(async () => refused('STORAGE_FREE_INCOMPLETE', 'The file list is freed, but 1 file of it couldn’t be deleted yet. VibeMiner deletes it later.')) });
    render(<SettingsView bridge={bridge} origin={origin} onBack={() => {}}/>);
    fireEvent.click(await waitFor(() => within(row('1.2.3.12345')).getByRole('button', { name: 'Free file list…' })));
    await act(async () => { fireEvent.click(within(row('1.2.3.12345')).getByRole('button', { name: 'Free 1.14 GiB' })); });
    const alert = await waitFor(() => within(row('1.2.3.12345')).getByRole('alert'));
    expect(alert).toHaveTextContent('Freed, with files left to delete.The file list is freed, but 1 file of it couldn’t be deleted yet.');
    expect(alert).toHaveTextContent('(STORAGE_FREE_INCOMPLETE)');
    expect(alert).not.toHaveTextContent('Couldn’t free');
    await waitFor(() => expect(row('1.2.3.12345')).toHaveTextContent('Freed · 1 file left to delete, 4.00 KiB · 4,096 bytes on disk'));
    // One control to try again: the entry's own Free button.
    expect(within(row('1.2.3.12345')).getAllByRole('button')).toHaveLength(1);
    expect(within(row('1.2.3.12345')).getByRole('button', { name: 'Free file list…' })).toHaveFocus();
  });
  it('says what failed when nothing could be freed, with the entry’s Free button the only retry', async () => {
    const bridge = bridgeMock({ storageBuilds: vi.fn(async () => success(listed)), storageFree: vi.fn(async () => refused('CATALOG_BUSY', 'The discovery catalog is busy. Retry this operation.')) });
    render(<SettingsView bridge={bridge} origin={origin} onBack={() => {}}/>);
    fireEvent.click(await waitFor(() => within(row('1.2.3.12345')).getByRole('button', { name: 'Free file list…' })));
    await act(async () => { fireEvent.click(within(row('1.2.3.12345')).getByRole('button', { name: 'Free 1.14 GiB' })); });
    const alert = await waitFor(() => within(row('1.2.3.12345')).getByRole('alert'));
    expect(alert).toHaveTextContent('Couldn’t free the file list of 1.2.3.12345.');
    expect(alert).toHaveTextContent('(CATALOG_BUSY)');
    expect(within(row('1.2.3.12345')).getAllByRole('button')).toHaveLength(1);
  });
  it('says when there is nothing opened, when the engine can’t measure and when measuring failed', async () => {
    render(<SettingsView bridge={bridgeMock({ storageBuilds: vi.fn(async () => success({ builds: [], truncated: false, measuredAt: measured.measuredAt })) })} origin={origin} onBack={() => {}}/>);
    expect(await screen.findByText('No build has been opened yet, so nothing is prepared.')).toBeVisible();
    cleanup();
    render(<SettingsView bridge={bridgeMock({ storageBuilds: vi.fn(async () => refused('UNSUPPORTED', 'No.', false)) })} origin={origin} onBack={() => {}}/>);
    expect(await screen.findByText(/this VibeMiner engine doesn’t report prepared data per build/)).toBeVisible();
    cleanup();
    const builds = vi.fn<NonNullable<DiscoveryBridge['storageBuilds']>>().mockResolvedValueOnce(refused('CATALOG_BUSY', 'The discovery catalog is busy.')).mockResolvedValue(success(listed));
    render(<SettingsView bridge={bridgeMock({ storageBuilds: builds })} origin={origin} onBack={() => {}}/>);
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Couldn’t measure prepared data.');
    fireEvent.click(within(alert).getByRole('button', { name: 'Measure again' }));
    await waitFor(() => expect(row('1.2.3.12345')).toBeTruthy());
  });
  it('cancels the measurement when Settings closes', async () => {
    let asked = '';
    const bridge = bridgeMock({ storageBuilds: vi.fn((id: string) => { asked = id; return new Promise<BridgeReply<PreparedDataList>>(() => {}); }) });
    const { unmount } = render(<SettingsView bridge={bridge} origin={origin} onBack={() => {}}/>);
    await waitFor(() => expect(asked).not.toBe(''));
    unmount();
    expect(bridge.cancel).toHaveBeenCalledWith(asked);
  });
});
