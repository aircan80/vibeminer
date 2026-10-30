import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../src/renderer/App';
import { BuildHeader } from '../src/renderer/BuildHeader';
import { DownloadActivity } from '../src/renderer/DownloadActivity';
import { SettingsProvider, SettingsView, type SettingsOrigin } from '../src/renderer/Settings';
import type { ActivityTask } from '../src/renderer/download-activity-state';
import { EngineClient, type EngineProcess } from '../src/main/engine-client';
import { listfileStatusMatches, settingsSetMatches, settingsSnapshotMatches, storageMeasurementMatches, validSettingsSet, type CommunityListfileStatus } from '../src/shared/settings';
import listfileStatuses from '../../../contracts/examples/community-listfile-status.json';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { DiscoveryPage, SettingsSnapshot, StorageMeasurement, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import fixture from '../../../contracts/examples/discovery-page.json';
import example from '../../../contracts/examples/settings-snapshot.json';
import workspace from '../../../contracts/examples/download-workspace.json';
import { catalog, testProducts } from './catalog-page';

const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const refused = (code: string, message: string, retryable = true) => ({ ok: false as const, error: { code, message, retryable } });
const unavailable = async () => refused('NOT_TESTED', 'Outside this test.', false);
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
const fresh: SettingsSnapshot = { formatVersion: 1, file: 'absent', problem: null, lastChangedAt: null, communityBuildHistory: { value: true, defaultValue: true, source: 'default', problem: null }, fetchTableDefinitions: { value: true, defaultValue: true, source: 'default', problem: null }, communityListfile: { value: true, defaultValue: true, source: 'default', problem: null }, storage: { dataDirectory: '/authored/profile/engine-v1' }, downloadLimits: { parallelDownloads: 4, backgroundDownloads: 2, bandwidthCapBytesPerSecond: null, adjustable: false } };
const saved = (value: boolean, at = '2026-10-20T13:14:00.0000000+00:00'): SettingsSnapshot => ({ ...fresh, file: 'read', lastChangedAt: at, communityBuildHistory: { value, defaultValue: true, source: 'saved', problem: null } });
const communityNote = () => within(document.getElementById('settings-row-community')!).getByRole('note');
const measured: StorageMeasurement = { bytes: '51749982208', files: '1204', skipped: '0', startedAt: '2026-10-20T13:12:00.0000000+00:00', measuredAt: '2026-10-20T13:12:04.0000000+00:00' };
function bridgeMock(settings: Partial<DiscoveryBridge> = {}): DiscoveryBridge {
  const page = fixture as DiscoveryPage;
  return {
    itemsData: vi.fn(unavailable), itemsPrepare: vi.fn(unavailable), itemsPage: vi.fn(unavailable), itemInspect: vi.fn(unavailable),
    products: vi.fn(async () => success({ products: testProducts })), read: vi.fn(unavailable), refresh: vi.fn(unavailable),
    readAll: vi.fn(async () => success(catalog(page))), refreshAll: vi.fn(unavailable),
    tableData: vi.fn(async () => success({ snapshot: null })), tablePrepare: vi.fn(unavailable), tableRows: vi.fn(unavailable), tablesCatalog: vi.fn(unavailable), tableDefinition: vi.fn(unavailable),
    download: vi.fn(unavailable), inventory: vi.fn(unavailable), inventoryFiles: vi.fn(unavailable), inventoryFile: vi.fn(unavailable), open: vi.fn(unavailable),
    cancel: vi.fn(async () => success({ requested: true })), onProgress: vi.fn(() => () => {}),
    settingsRead: vi.fn(async () => success(fresh)), settingsSet: vi.fn(async params => success(saved(params.value))),
    storageMeasure: vi.fn(async () => success(measured)),
    storageCopyPath: vi.fn(async () => success({ copied: true as const })), storageShowFolder: vi.fn(async () => success({ shown: true as const })),
    ...settings,
  };
}
async function openFromDiscovery(bridge = bridgeMock()) {
  render(<App bridge={bridge}/>);
  await screen.findByRole('listbox', { name: 'Builds' });
  const entry = screen.getByRole('button', { name: 'Settings' });
  fireEvent.click(entry);
  await screen.findByRole('heading', { level: 1, name: 'Settings' });
  return { bridge, entry };
}
beforeEach(() => { localStorage.clear(); vi.spyOn(window, 'scrollTo').mockImplementation(() => {}); Element.prototype.scrollIntoView = vi.fn(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('settings bridge payloads', () => {
  it('admit only the one known switch with a boolean value', () => {
    expect(validSettingsSet({ name: 'communityBuildHistory', value: true })).toBe(true);
    expect(validSettingsSet({ name: 'fetchTableDefinitions', value: false })).toBe(true);
    expect(validSettingsSet({ name: 'fetchTableDefinitions', value: 0 })).toBe(false);
    for (const bad of [null, [], {}, { name: 'communityBuildHistory' }, { name: 'communityBuildHistory', value: 'true' }, { name: 'bandwidthCap', value: true }, { name: 'communityBuildHistory', value: false, path: '/tmp' }])
      expect(validSettingsSet(bad)).toBe(false);
  });
  it('refuse snapshots that present a default or unreadable value as saved', () => {
    expect(settingsSnapshotMatches(example as unknown as SettingsSnapshot)).toBe(true);
    expect(settingsSnapshotMatches(fresh)).toBe(true);
    expect(settingsSnapshotMatches({ ...fresh, communityBuildHistory: { ...fresh.communityBuildHistory, source: 'saved', value: true } })).toBe(false);
    expect(settingsSnapshotMatches({ ...fresh, lastChangedAt: '2026-10-20T13:14:00Z' })).toBe(false);
    expect(settingsSnapshotMatches({ ...fresh, file: 'unreadable', problem: null })).toBe(false);
    expect(settingsSnapshotMatches({ ...saved(true), communityBuildHistory: { value: true, defaultValue: true, source: 'unreadable', problem: { code: 'SETTING_UNREADABLE', message: 'x', retryable: false } } })).toBe(false);
    expect(settingsSnapshotMatches({ ...fresh, downloadLimits: { ...fresh.downloadLimits, backgroundDownloads: 5 } })).toBe(false);
    expect(settingsSetMatches(saved(true), { name: 'communityBuildHistory', value: true })).toBe(true);
    expect(settingsSetMatches(saved(false), { name: 'communityBuildHistory', value: true })).toBe(false);
    expect(settingsSetMatches(fresh, { name: 'communityBuildHistory', value: false })).toBe(false);
    const fetchOff: SettingsSnapshot = { ...saved(true), fetchTableDefinitions: { value: false, defaultValue: true, source: 'saved', problem: null } };
    expect(settingsSetMatches(fetchOff, { name: 'fetchTableDefinitions', value: false })).toBe(true);
    expect(settingsSetMatches(saved(true), { name: 'fetchTableDefinitions', value: false })).toBe(false);
    expect(settingsSnapshotMatches({ ...fresh, fetchTableDefinitions: { value: false, defaultValue: true, source: 'default', problem: null } })).toBe(false);
    const unreadable = { code: 'SETTING_UNREADABLE', message: 'x', retryable: false };
    expect(settingsSnapshotMatches({ ...fresh, file: 'read', lastChangedAt: '2026-10-23T12:00:00Z', fetchTableDefinitions: { value: false, defaultValue: true, source: 'unreadable', problem: unreadable } })).toBe(true);
    expect(settingsSnapshotMatches({ ...fresh, file: 'read', lastChangedAt: '2026-10-23T12:00:00Z', fetchTableDefinitions: { value: true, defaultValue: true, source: 'unreadable', problem: unreadable } })).toBe(false);
    expect(settingsSnapshotMatches({ ...fresh, file: 'newer', problem: { code: 'SETTINGS_VERSION', message: 'x', retryable: false }, communityBuildHistory: { ...fresh.communityBuildHistory, value: false, source: 'unreadable', problem: { code: 'SETTINGS_VERSION', message: 'x', retryable: false } } })).toBe(false);
    expect(storageMeasurementMatches(measured)).toBe(true);
    expect(storageMeasurementMatches({ ...measured, measuredAt: '2026-10-20T13:11:00Z' })).toBe(false);
  });
});

class Child extends EventEmitter implements EngineProcess {
  stdout = new PassThrough(); stderr = new PassThrough(); requests: { id: string; method: string; params: unknown }[] = [];
  stdin = new Writable({ write: (chunk, _encoding, done) => { this.requests.push(JSON.parse(chunk.toString())); done(); } });
  kill = vi.fn(() => true);
  reply(id: string, result: unknown) { this.stdout.write(JSON.stringify({ protocolVersion: 1, id, result }) + '\n'); }
}
describe('engine client settings methods', () => {
  const children: Child[] = [];
  afterEach(() => { for (const child of children.splice(0)) child.emit('exit', 0); });
  async function connected(capabilities: string[]) { const child = new Child(); children.push(child); const client = new EngineClient(child); const hello = client.hello(); child.reply(child.requests[0].id, { protocolVersion: 1, engineVersion: 'authored', capabilities }); await hello; return { child, client }; }
  it('needs the settings capability and refuses invalid changes before sending them', async () => {
    const old = await connected([]);
    await expect(old.client.request('settings.read', {})).rejects.toMatchObject({ code: 'UNSUPPORTED' });
    const { child, client } = await connected(['settings-v1']);
    await expect(client.request('settings.set', { name: 'communityBuildHistory', value: 'true' })).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
    await expect(client.request('settings.storage.measure', { root: '/' })).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
    expect(child.requests).toHaveLength(1);
  });
  it('needs the listfile capability and checks the status it is sent (D105)', async () => {
    const statuses = listfileStatuses as CommunityListfileStatus[];
    for (const status of statuses) expect(listfileStatusMatches(status)).toBe(true);
    const without = await connected(['settings-v1']);
    await expect(without.client.request('settings.listfile.read', {})).rejects.toMatchObject({ code: 'UNSUPPORTED' });
    const { child, client } = await connected(['settings-v1', 'community-listfile-v1']);
    await expect(client.request('settings.listfile.check', { tag: 'x' })).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
    const read = client.request('settings.listfile.read', {}, 'read'); child.reply('read', statuses[0]); await expect(read).resolves.toEqual(statuses[0]);
    for (const [id, bad] of [['done', { ...statuses[1], completed: '999999999999' }], ['idleProgress', { ...statuses[0], completed: '1' }], ['source', { ...statuses[0], release: { ...statuses[0].release!, sourceUrl: 'https://github.com/wowdev/wow-listfile/releases/download/other/community-listfile-withcapitals.csv' } }]] as const) {
      const work = expect(client.request('settings.listfile.check', {}, id)).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' }); child.reply(id, bad); await work;
    }
  });
  it('accepts the committed value and treats a different committed value as a protocol failure', async () => {
    const { child, client } = await connected(['settings-v1']);
    const read = client.request('settings.read', {}, 'read'); child.reply('read', fresh); await expect(read).resolves.toEqual(fresh);
    const measure = client.request('settings.storage.measure', {}, 'measure'); child.reply('measure', measured); await expect(measure).resolves.toEqual(measured);
    const set = client.request('settings.set', { name: 'communityBuildHistory', value: true }, 'set'); child.reply('set', saved(true)); await expect(set).resolves.toEqual(saved(true));
    const wrong = client.request('settings.set', { name: 'communityBuildHistory', value: true }, 'wrong'); child.reply('wrong', saved(false));
    await expect(wrong).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
  });
});

describe('Settings view', () => {
  it('opens from the last toolbar item, focuses its heading and keeps Discovery mounted but hidden', async () => {
    render(<App bridge={bridgeMock()}/>);
    await screen.findByRole('listbox', { name: 'Builds' });
    const toolbar = document.querySelector('.dv-toolbar')!;
    expect(toolbar.lastElementChild).toHaveTextContent('Settings');
    expect(screen.getByRole('button', { name: 'Settings' })).toHaveAttribute('aria-keyshortcuts', 'Control+Comma');
    fireEvent.change(screen.getByRole('searchbox', { name: 'Filter version or build number' }), { target: { value: '70448' } });
    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
    const heading = await screen.findByRole('heading', { level: 1, name: 'Settings' });
    expect(heading).toHaveFocus();
    expect(document.querySelector('.discovery-screen')).toHaveAttribute('hidden');
    expect(screen.getByRole('button', { name: '← Back to Discovery' })).toBeVisible();
    expect(screen.getByText('Discovery kept as you left it')).toBeInTheDocument();
    await screen.findByRole('switch', { name: /Community build history/ });
    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('heading', { level: 1, name: 'Settings' })).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Settings' })).toHaveFocus();
    expect(screen.getByRole('searchbox', { name: 'Filter version or build number' })).toHaveValue('70448');
  });
  it('opens with Ctrl+, and returns with Back', async () => {
    render(<App bridge={bridgeMock()}/>);
    await screen.findByRole('listbox', { name: 'Builds' });
    fireEvent.keyDown(window, { key: ',', ctrlKey: true });
    await screen.findByRole('heading', { level: 1, name: 'Settings' });
    fireEvent.click(screen.getByRole('button', { name: '← Back to Discovery' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Settings' })).toHaveFocus());
  });
  it('shows no switch and no saved-looking default until the engine has read the value', async () => {
    const reading = deferred<BridgeReply<SettingsSnapshot>>();
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(() => reading.promise) }));
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    expect(screen.getAllByText('Reading saved value…')).toHaveLength(3);
    expect(screen.getByText('Reading settings…')).toBeVisible();
    expect(screen.getByText('Not read yet')).toBeVisible();
    await act(async () => { reading.resolve(success(fresh)); });
    // Owner decision on #9: the wago.tools build list is on by default (D106).
    const toggle = screen.getByRole('switch', { name: /Community build history/ });
    expect(toggle).toHaveAttribute('aria-checked', 'true');
    expect(toggle).toHaveTextContent('On');
    expect(within(document.getElementById('settings-row-community')!).getByText('Default: On')).toBeVisible();
    expect(screen.getByText('Never changed')).toBeVisible();
    expect(screen.getByText('Up to 4 at once, at most 2 of them for background downloads')).toBeVisible();
    expect(screen.getByText('Fixed for now')).toBeVisible();
    expect(screen.getByText('/authored/profile/engine-v1')).toBeVisible();
  });
  it('keeps the committed position while saving, keeps focus, then shows the saved time', async () => {
    const commit = deferred<BridgeReply<SettingsSnapshot>>(), bridge = bridgeMock({ settingsSet: vi.fn(() => commit.promise) });
    await openFromDiscovery(bridge);
    const toggle = await screen.findByRole('switch', { name: /Community build history/ });
    toggle.focus(); fireEvent.click(toggle);
    expect(bridge.settingsSet).toHaveBeenCalledWith({ name: 'communityBuildHistory', value: false });
    expect(toggle).toHaveAttribute('aria-checked', 'true');
    expect(toggle).toHaveAttribute('aria-disabled', 'true');
    expect(toggle).toHaveTextContent('Turning off…');
    expect(screen.getByText('Saving…')).toBeVisible();
    fireEvent.click(toggle);
    expect(bridge.settingsSet).toHaveBeenCalledTimes(1);
    await act(async () => { commit.resolve(success(saved(false))); });
    expect(toggle).toHaveAttribute('aria-checked', 'false');
    expect(toggle).toHaveTextContent('Off');
    expect(toggle).toHaveFocus();
    expect(within(document.getElementById('settings-community-save')!).getByText('Saved')).toBeVisible();
  });
  it('says a failed save left the value as it was, never replays it, and Retry keeps focus on the switch', async () => {
    const settingsSet = vi.fn().mockResolvedValueOnce(refused('SETTINGS_WRITE_FAILED', 'The engine couldn’t write the settings file. Nothing else changed.')).mockResolvedValueOnce(success(saved(false)));
    await openFromDiscovery(bridgeMock({ settingsSet }));
    fireEvent.click(await screen.findByRole('switch', { name: /Community build history/ }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Couldn’t save Community build history. It’s still On.');
    expect(alert).toHaveTextContent('(SETTINGS_WRITE_FAILED)');
    expect(screen.getByRole('switch', { name: /Community build history/ })).toHaveAttribute('aria-checked', 'true');
    expect(settingsSet).toHaveBeenCalledTimes(1);
    const retry = screen.getByRole('button', { name: 'Retry turning off' });
    retry.focus(); fireEvent.click(retry);
    expect(screen.getByRole('switch', { name: /Community build history/ })).toHaveFocus();
    await waitFor(() => expect(screen.getByRole('switch', { name: /Community build history/ })).toHaveAttribute('aria-checked', 'false'));
    expect(settingsSet).toHaveBeenNthCalledWith(2, { name: 'communityBuildHistory', value: false });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
  it('shows Fetch newer table definitions on by default under Tables and saves it on its own', async () => {
    const settingsSet = vi.fn(async (params: { name: 'communityBuildHistory' | 'fetchTableDefinitions'; value: boolean }) =>
      success({ ...fresh, file: 'read' as const, lastChangedAt: '2026-10-23T12:00:00.0000000+00:00', fetchTableDefinitions: { value: params.value, defaultValue: true as const, source: 'saved' as const, problem: null } }));
    const { bridge } = await openFromDiscovery(bridgeMock({ settingsSet }));
    const toggle = await screen.findByRole('switch', { name: 'Fetch newer table definitions from WoWDBDefs' });
    expect(toggle).toHaveAttribute('aria-checked', 'true');
    expect(toggle).toHaveTextContent('On');
    expect(within(document.getElementById('settings-row-fetch')!).getByText('Default: On')).toBeVisible();
    expect(screen.getByRole('heading', { level: 2, name: 'Tables' })).toBeVisible();
    expect(toggle).toHaveAccessibleDescription(/fetches the newest community definition for just that table/);
    const row = document.getElementById('settings-row-fetch')!;
    expect(row).toHaveTextContent('all game data still comes from Blizzard’s CDN');
    expect(row).toHaveTextContent('VibeMiner doesn’t contact GitHub');
    expect(row).toHaveTextContent('CC BY-SA 4.0');
    toggle.focus(); fireEvent.click(toggle);
    expect(toggle).toHaveTextContent('Turning off…');
    expect(bridge.settingsSet).toHaveBeenCalledWith({ name: 'fetchTableDefinitions', value: false });
    await waitFor(() => expect(toggle).toHaveAttribute('aria-checked', 'false'));
    expect(toggle).toHaveFocus();
    expect(within(document.getElementById('settings-fetch-save')!).getByText('Saved')).toBeVisible();
    expect(document.getElementById('settings-community-save')).toHaveTextContent('');
    expect(screen.getByRole('switch', { name: /Community build history/ })).toHaveAttribute('aria-checked', 'true');
  });
  it('shows Community file names on by default under Files with the copy in use, and saves the switch on its own (D105)', async () => {
    const [ready] = listfileStatuses as CommunityListfileStatus[];
    const settingsSet = vi.fn(async (params: { name: 'communityBuildHistory' | 'fetchTableDefinitions' | 'communityListfile'; value: boolean }) =>
      success({ ...fresh, file: 'read' as const, lastChangedAt: '2026-10-23T12:00:00.0000000+00:00', communityListfile: { value: params.value, defaultValue: true as const, source: 'saved' as const, problem: null } }));
    let on = true;
    const listfileRead = vi.fn(async () => success({ ...ready, enabled: on }));
    settingsSet.mockImplementation(async params => { on = params.value; return success({ ...fresh, file: 'read' as const, lastChangedAt: '2026-10-23T12:00:00.0000000+00:00', communityListfile: { value: params.value, defaultValue: true as const, source: 'saved' as const, problem: null } }); });
    const { bridge } = await openFromDiscovery(bridgeMock({ settingsSet, listfileRead, listfileCheck: vi.fn(async () => success(ready)) }));
    const toggle = await screen.findByRole('switch', { name: 'Community file names from wow-listfile' });
    expect(toggle).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByRole('heading', { level: 2, name: 'Files' })).toBeVisible();
    const row = document.getElementById('settings-row-listfile')!;
    expect(within(row).getByText('Default: On')).toBeVisible();
    expect(row).toHaveTextContent('all data still comes from Blizzard’s CDN');
    expect(row).toHaveTextContent('It has no licence, so VibeMiner never ships it.');
    expect(within(row).getByText('Community')).toHaveClass('st-pill', 'community');
    await waitFor(() => expect(row).toHaveTextContent(/Copy in useRelease 202601010000 · published .* · fetched .* · 2,275,783 names/));
    expect(within(row).getByRole('button', { name: 'Check for a newer release' })).toBeVisible();
    toggle.focus(); fireEvent.click(toggle);
    expect(bridge.settingsSet).toHaveBeenCalledWith({ name: 'communityListfile', value: false });
    await waitFor(() => expect(toggle).toHaveAttribute('aria-checked', 'false'));
    expect(toggle).toHaveFocus();
    // The download state is read again for the new value: off says so and keeps the copy, unused.
    await within(row).findByText('Off. Nothing is downloaded.');
    expect(row).toHaveTextContent('kept, not used while off');
    expect(within(row).queryByRole('button', { name: 'Check for a newer release' })).toBeNull();
  });
  it('calls importing Processing with bytes, says a failed refresh keeps the copy and when it retries, and puts the switch first (D105)', async () => {
    const [ready, , importing] = listfileStatuses as CommunityListfileStatus[];
    const failedRefresh = { ...ready, problem: { code: 'LISTFILE_FETCH_OFFLINE', message: 'VibeMiner couldn’t reach GitHub to get the wow-listfile community file names.', retryable: true } };
    const listfileRead = vi.fn(async () => success(importing));
    await openFromDiscovery(bridgeMock({ listfileRead, listfileCheck: vi.fn(async () => success(importing)) }));
    const row = document.getElementById('settings-row-listfile')!;
    await within(row).findByText('Processing release 202601080000 · 0% · 1.00 MiB of 146.66 MiB');
    // The switch comes before the download state and its button in the document, so in tab order and in the one-column layout.
    const toggle = within(row).getByRole('switch', { name: 'Community file names from wow-listfile' }), button = within(row).getByRole('button', { name: 'Check for a newer release' });
    expect(toggle.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(toggle.compareDocumentPosition(document.getElementById('settings-listfile-state')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    listfileRead.mockResolvedValue(success(failedRefresh));
    await within(row).findByText('Release 202601010000 is still in use. The last check for a newer one failed; VibeMiner tries again after 15 minutes.', {}, { timeout: 3000 });
    expect(row).not.toHaveTextContent('Up to date as of');
    expect(within(row).getByRole('alert')).toHaveTextContent('Couldn’t get a newer release. Release 202601010000 is still in use.');
  });
  it('shows the listfile download as it runs, a failure with its code, and asks for a download only when told to', async () => {
    const [, downloading, , offline, , notStarted] = listfileStatuses as CommunityListfileStatus[];
    const listfileRead = vi.fn(async () => success(notStarted));
    const listfileCheck = vi.fn(async () => success(downloading));
    await openFromDiscovery(bridgeMock({ listfileRead, listfileCheck }));
    const row = document.getElementById('settings-row-listfile')!;
    await within(row).findByText('Not downloaded yet. It downloads the first time Files lists a build’s files.');
    expect(listfileCheck).not.toHaveBeenCalled();
    listfileRead.mockResolvedValue(success(downloading));
    const download = within(row).getByRole('button', { name: 'Download now' }); download.focus(); fireEvent.click(download);
    await within(row).findByText('Downloading release 202601010000 · 50.00 MiB of 146.66 MiB');
    expect(listfileCheck).toHaveBeenCalledTimes(1);
    expect(within(row).getByRole('button', { name: 'Download now' })).toHaveAttribute('aria-disabled', 'true');
    expect(within(row).getByRole('button', { name: 'Download now' })).toHaveFocus();
    listfileRead.mockResolvedValue(success(offline));
    await within(row).findByText('Couldn’t get the community file names. Files says Name not known.', {}, { timeout: 3000 });
    expect(within(row).getByRole('alert')).toHaveTextContent('(LISTFILE_FETCH_OFFLINE)');
    expect(within(row).getByRole('button', { name: 'Try again now' })).toBeVisible();
  });
  it('says a failed fetch-switch save left it on and retries only that switch', async () => {
    const settingsSet = vi.fn().mockResolvedValueOnce(refused('SETTINGS_WRITE_FAILED', 'The engine couldn’t write the settings file. Nothing else changed.'));
    await openFromDiscovery(bridgeMock({ settingsSet }));
    fireEvent.click(await screen.findByRole('switch', { name: /Fetch newer table definitions/ }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Couldn’t save Fetch newer table definitions. It’s still On.');
    expect(within(document.getElementById('settings-row-fetch')!).getByRole('button', { name: 'Retry turning off' })).toBeVisible();
    expect(within(document.getElementById('settings-row-community')!).queryByRole('alert')).not.toBeInTheDocument();
  });
  it('shows an unreadable fetch value as Off in use at its own row, so nothing is fetched', async () => {
    const problem = { code: 'SETTING_UNREADABLE', message: 'The saved value for Fetch newer table definitions isn’t one VibeMiner understands.', retryable: false };
    const bad: SettingsSnapshot = { ...fresh, file: 'read', lastChangedAt: '2026-10-23T12:00:00Z', fetchTableDefinitions: { value: false, defaultValue: true, source: 'unreadable', problem } };
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(async () => success(bad)) }));
    const toggle = await screen.findByRole('switch', { name: /Fetch newer table definitions/ });
    expect(toggle).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByText('Default: On · Off in use now')).toBeVisible();
    const note = within(document.getElementById('settings-row-fetch')!).getByRole('note');
    expect(note).toHaveTextContent('Saved value couldn’t be read. Using Off until you switch it.');
    expect(note).toHaveTextContent('no table definitions are fetched from WoWDBDefs');
    expect(toggle).toHaveAccessibleDescription(/Using Off until you switch it/);
    expect(screen.getByText('1 setting is Off until you switch it')).toBeVisible();
    expect(screen.getByText('The saved value for Fetch newer table definitions couldn’t be read. The other settings were read normally.')).toBeVisible();
  });
  it('says each row of a damaged settings file reads as Off, and the About box never calls fetching Off its default', async () => {
    const problem = { code: 'SETTINGS_UNREADABLE', message: 'The settings file isn’t valid JSON.', retryable: false };
    const damaged: SettingsSnapshot = { ...fresh, file: 'unreadable', problem, communityBuildHistory: { ...fresh.communityBuildHistory, value: false, source: 'unreadable', problem }, fetchTableDefinitions: { ...fresh.fetchTableDefinitions, value: false, source: 'unreadable', problem } };
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(async () => success(damaged)) }));
    const fetch = await screen.findByRole('switch', { name: /Fetch newer table definitions/ });
    expect(fetch).toHaveAttribute('aria-checked', 'false');
    const note = within(document.getElementById('settings-row-fetch')!).getByRole('note');
    expect(note).toHaveTextContent('Settings file couldn’t be read. Using Off until you switch it.');
    expect(note).toHaveTextContent('no table definitions are fetched from WoWDBDefs');
    expect(fetch).toHaveAccessibleDescription(/Using Off until you switch it/);
    expect(screen.getByText('2 settings are Off until you switch them')).toBeVisible();
    expect(screen.queryByText(/using (its|their) default/)).not.toBeInTheDocument();
  });
  it('shows an unreadable saved value as the default in use, with its code', async () => {
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(async () => success(example as unknown as SettingsSnapshot)) }));
    const toggle = await screen.findByRole('switch', { name: /Community build history/ });
    // An unreadable value fails closed: Off, not the default On, so nothing is fetched from wago.tools.
    expect(toggle).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByText('Default: On · Off in use now')).toBeVisible();
    const note = screen.getByRole('note');
    expect(note).toHaveTextContent('Saved value couldn’t be read. Using Off until you switch it.');
    expect(note).toHaveTextContent('(SETTING_UNREADABLE)');
    expect(screen.getByText('1 setting is Off until you switch it')).toBeVisible();
  });
  it('reports a failed read at the row with Read again and shows nothing as if it were known', async () => {
    const settingsRead = vi.fn().mockResolvedValueOnce(refused('ENGINE_UNAVAILABLE', 'The local engine is unavailable.')).mockResolvedValueOnce(success(fresh));
    await openFromDiscovery(bridgeMock({ settingsRead }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Couldn’t read settings.');
    expect(alert).toHaveTextContent('(ENGINE_UNAVAILABLE)');
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    expect(screen.getAllByText('Not known').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Read again' }));
    await screen.findByRole('switch', { name: /Community build history/ });
  });
  it('locks the switch for a newer settings file, says why, and never offers a change that cannot work', async () => {
    const problem = { code: 'SETTINGS_VERSION', message: 'The settings file is version 2, saved by a newer VibeMiner.', retryable: false };
    const newer: SettingsSnapshot = { ...fresh, file: 'newer', problem, communityBuildHistory: { ...fresh.communityBuildHistory, value: false, source: 'unreadable', problem }, fetchTableDefinitions: { ...fresh.fetchTableDefinitions, value: false, source: 'unreadable', problem }, communityListfile: { ...fresh.communityListfile, value: false, source: 'unreadable', problem } };
    const bridge = bridgeMock({ settingsRead: vi.fn(async () => success(newer)) });
    await openFromDiscovery(bridge);
    const toggle = await screen.findByRole('switch', { name: /Community build history/ });
    expect(toggle).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(toggle);
    expect(bridge.settingsSet).not.toHaveBeenCalled();
    for (const row of ['settings-community-save', 'settings-fetch-save']) expect(document.getElementById(row)).toHaveTextContent('Can’t be changed here: saved by a newer VibeMiner.');
    expect(communityNote()).toHaveTextContent('Saved by a newer VibeMiner. Using Off.');
    expect(communityNote()).toHaveTextContent('(SETTINGS_VERSION)');
    expect(within(document.getElementById('settings-row-fetch')!).getByRole('note')).toHaveTextContent('Saved by a newer VibeMiner. Using Off.');
    expect(screen.getByRole('switch', { name: /Fetch newer table definitions/ })).toHaveAccessibleDescription(/Using Off\./);
    expect(toggle).toHaveAccessibleDescription(/saved by a newer VibeMiner/);
    expect(screen.getByText('Settings saved by a newer VibeMiner')).toBeVisible();
    expect(screen.queryByText(/couldn’t be read/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Retry/ })).not.toBeInTheDocument();
  });
  // #100: formatVersion is the engine's own version, so About never calls a newer file "settings version 1".
  it('says in About where settings are saved, and that a newer file comes from a newer VibeMiner rather than calling it version 1', async () => {
    const savedIn = () => within(screen.getByRole('complementary', { name: 'About settings' })).getByText('Saved in').nextElementSibling;
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(async () => success(saved(true))) }));
    await screen.findByRole('switch', { name: /Community build history/ });
    expect(savedIn()).toHaveTextContent(/^The storage folder, settings version 1$/);
    cleanup();
    const problem = { code: 'SETTINGS_VERSION', message: 'The settings file is version 2, saved by a newer VibeMiner.', retryable: false };
    const off = (c: SettingsSnapshot['communityBuildHistory']) => ({ ...c, value: false, source: 'unreadable' as const, problem });
    const newer: SettingsSnapshot = { ...fresh, file: 'newer', problem, communityBuildHistory: off(fresh.communityBuildHistory), fetchTableDefinitions: off(fresh.fetchTableDefinitions), communityListfile: off(fresh.communityListfile) };
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(async () => success(newer)) }));
    await screen.findByRole('switch', { name: /Community build history/ });
    expect(savedIn()).toHaveTextContent(/^The storage folder, saved by a newer VibeMiner\. This version reads settings version 1\.$/);
    expect(savedIn()).not.toHaveTextContent(/, settings version 1/);
  });
  // #97: a newer file fails closed, so a default-on switch is Off; no row may call that the default.
  it.each([
    ['communityBuildHistory', 'community', /Community build history/, 'Community build history is Off because the settings file comes from a newer VibeMiner, so Discovery shows no community builds and nothing is fetched from wago.tools.'],
    ['fetchTableDefinitions', 'fetch', /Fetch newer table definitions/, 'Fetch newer table definitions is Off because the settings file comes from a newer VibeMiner, so no table definitions are fetched from WoWDBDefs.'],
    ['communityListfile', 'listfile', /Community file names/, 'Community file names is Off because the settings file comes from a newer VibeMiner, so Files shows no community names and nothing is downloaded from wow-listfile.'],
  ] as const)('says default-on %s is Off because a newer VibeMiner saved the file, and the switch agrees', async (_name, key, label, why) => {
    const problem = { code: 'SETTINGS_VERSION', message: 'The settings file is version 2, saved by a newer VibeMiner.', retryable: false };
    const off = (c: SettingsSnapshot['communityBuildHistory']) => ({ ...c, value: false, source: 'unreadable' as const, problem });
    const newer: SettingsSnapshot = { ...fresh, file: 'newer', problem, communityBuildHistory: off(fresh.communityBuildHistory), fetchTableDefinitions: off(fresh.fetchTableDefinitions), communityListfile: off(fresh.communityListfile) };
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(async () => success(newer)) }));
    const toggle = await screen.findByRole('switch', { name: label });
    const row = document.getElementById(`settings-row-${key}`)!, note = within(row).getByRole('note');
    expect(toggle).toHaveAttribute('aria-checked', 'false');
    expect(toggle).toHaveTextContent('Off');
    expect(within(row).getByText('Default: On · Off in use now')).toBeVisible();
    expect(note).toHaveTextContent('Saved by a newer VibeMiner. Using Off.');
    expect(note).toHaveTextContent(why);
    expect(note).not.toHaveTextContent(/default/i);
    expect(toggle.getAttribute('aria-describedby')).toContain(`settings-${key}-note`);
  });
  it('locks the switch for a file that was never read and offers Read again only when it can help', async () => {
    const problem = { code: 'SETTINGS_NOT_READ', message: 'The settings file couldn’t be opened. It may be locked or not readable.', retryable: true };
    const locked: SettingsSnapshot = { ...fresh, file: 'unreadable', problem, communityBuildHistory: { ...fresh.communityBuildHistory, value: false, source: 'unreadable', problem }, fetchTableDefinitions: { ...fresh.fetchTableDefinitions, value: false, source: 'unreadable', problem } };
    const settingsRead = vi.fn().mockResolvedValueOnce(success(locked)).mockResolvedValueOnce(success(fresh)), bridge = bridgeMock({ settingsRead });
    await openFromDiscovery(bridge);
    const toggle = await screen.findByRole('switch', { name: /Community build history/ });
    expect(toggle).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(toggle);
    expect(bridge.settingsSet).not.toHaveBeenCalled();
    expect(communityNote()).toHaveTextContent('Settings file not read. Using Off.');
    expect(screen.getByText('Settings file not read')).toBeVisible();
    const again = screen.getByRole('button', { name: 'Read again' });
    again.focus(); fireEvent.click(again);
    await waitFor(() => expect(screen.getByRole('switch', { name: /Community build history/ })).not.toHaveAttribute('aria-disabled', 'true'));
    expect(screen.getByRole('switch', { name: /Community build history/ })).toHaveFocus();
  });
  it('names the file and the next step when this version can never read it, and claims nothing is saved', async () => {
    const problem = { code: 'SETTINGS_NOT_READ', message: 'The settings file is larger than VibeMiner allows, so it wasn’t read.', retryable: false };
    const oversized: SettingsSnapshot = { ...fresh, file: 'unreadable', problem, communityBuildHistory: { ...fresh.communityBuildHistory, value: false, source: 'unreadable', problem }, fetchTableDefinitions: { ...fresh.fetchTableDefinitions, value: false, source: 'unreadable', problem } };
    await openFromDiscovery(bridgeMock({ settingsRead: vi.fn(async () => success(oversized)) }));
    const toggle = await screen.findByRole('switch', { name: /Community build history/ });
    expect(toggle).toHaveAttribute('aria-disabled', 'true');
    for (const row of ['settings-community-save', 'settings-fetch-save']) expect(document.getElementById(row)).toHaveTextContent('Can’t be changed: this version can’t read settings-v1.json.');
    expect(communityNote()).toHaveTextContent('This version of VibeMiner can’t read settings-v1.json in the storage folder, so it wasn’t changed. Fix or remove that file, then open Settings again.');
    expect(screen.queryByRole('button', { name: 'Read again' })).not.toBeInTheDocument();
    expect(screen.queryByText(/Your choice is saved now/)).not.toBeInTheDocument();
    expect(within(document.querySelector('.st-side')!).getByText('Not known')).toBeVisible();
  });
  it('offers Read again instead of Retry when a save fails on a newer file, and nothing for a non-retryable failure', async () => {
    const settingsSet = vi.fn().mockResolvedValueOnce(refused('SETTINGS_VERSION', 'These settings were saved by a newer VibeMiner.', false)).mockResolvedValueOnce(refused('SETTINGS_WRITE_FAILED', 'The settings file would be larger than VibeMiner allows.', false));
    await openFromDiscovery(bridgeMock({ settingsSet }));
    fireEvent.click(await screen.findByRole('switch', { name: /Community build history/ }));
    await screen.findByRole('alert');
    expect(screen.queryByRole('button', { name: /Retry/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Read again' })).toBeVisible();
    fireEvent.click(screen.getByRole('switch', { name: /Community build history/ }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('larger than VibeMiner allows'));
    expect(within(screen.getByRole('alert')).queryByRole('button')).not.toBeInTheDocument();
  });
  it('keeps focus on the page when Read again is used from the keyboard', async () => {
    const reading = deferred<BridgeReply<SettingsSnapshot>>();
    const settingsRead = vi.fn().mockResolvedValueOnce(refused('ENGINE_UNAVAILABLE', 'The local engine is unavailable.')).mockReturnValueOnce(reading.promise).mockResolvedValueOnce(refused('ENGINE_UNAVAILABLE', 'The local engine is unavailable.'));
    await openFromDiscovery(bridgeMock({ settingsRead }));
    const again = await screen.findByRole('button', { name: 'Read again' });
    again.focus(); fireEvent.click(again);
    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toHaveFocus();
    await act(async () => { reading.resolve(success(fresh)); });
    expect(screen.getByRole('switch', { name: /Community build history/ })).toHaveFocus();
    cleanup();
    const failing = vi.fn(async () => refused('ENGINE_UNAVAILABLE', 'The local engine is unavailable.'));
    await openFromDiscovery(bridgeMock({ settingsRead: failing }));
    fireEvent.click(await screen.findByRole('button', { name: 'Read again' }));
    await waitFor(() => expect(failing).toHaveBeenCalledTimes(2));
    await screen.findByRole('button', { name: 'Read again' });
    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toHaveFocus();
  });
  it('measures the folder once per opening, shows readable and exact bytes, and stops the walk when Settings is left', async () => {
    const walk = deferred<BridgeReply<StorageMeasurement>>(), bridge = bridgeMock({ storageMeasure: vi.fn(() => walk.promise) });
    await openFromDiscovery(bridge);
    expect(screen.getByText('Measuring…')).toBeVisible();
    expect(screen.getByText('Not measured yet')).toBeVisible();
    const id = vi.mocked(bridge.storageMeasure!).mock.calls[0][0];
    fireEvent.click(screen.getByRole('button', { name: '← Back to Discovery' }));
    await waitFor(() => expect(bridge.cancel).toHaveBeenCalledWith(id));
    await act(async () => { walk.resolve(success(measured)); });
    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
    await screen.findByText('48.19 GiB');
    expect(screen.getByText(`· ${(51749982208).toLocaleString()} bytes`)).toBeVisible();
    expect(screen.getByText(/when Settings opened$/)).toBeVisible();
    expect(bridge.storageMeasure).toHaveBeenCalledTimes(2);
  });
  it('names skipped folders and a failed measurement with Measure again', async () => {
    const storageMeasure = vi.fn().mockResolvedValueOnce(refused('STORAGE_MEASURE_TIMEOUT', 'Measuring the storage folder took too long.')).mockResolvedValueOnce(success({ ...measured, skipped: '2' }));
    await openFromDiscovery(bridgeMock({ storageMeasure }));
    expect(await screen.findByText(/took too long/)).toHaveTextContent('(STORAGE_MEASURE_TIMEOUT)');
    const again = screen.getByRole('button', { name: 'Measure again' });
    again.focus(); fireEvent.click(again);
    expect(screen.getByRole('button', { name: 'Copy path' })).toHaveFocus();
    await screen.findByText('2 items couldn’t be read and aren’t counted.');
    expect(screen.getByText(/when you asked$/)).toBeVisible();
  });
  it('asks main to copy or show the folder and says what happened', async () => {
    const bridge = bridgeMock({ storageShowFolder: vi.fn(async () => refused('STORAGE_FOLDER_UNAVAILABLE', 'The storage folder could not be opened in the file manager.')) });
    await openFromDiscovery(bridge);
    fireEvent.click(screen.getByRole('button', { name: 'Copy path' }));
    await screen.findByText('Path copied');
    expect(bridge.storageCopyPath).toHaveBeenCalledWith();
    fireEvent.click(screen.getByRole('button', { name: 'Show in folder' }));
    expect(await screen.findByText(/Couldn’t open the folder/)).toHaveTextContent('(STORAGE_FOLDER_UNAVAILABLE)');
  });
  it('says Settings are unavailable on an engine without them', async () => {
    await openFromDiscovery(bridgeMock({ settingsRead: undefined, settingsSet: undefined, storageMeasure: undefined }));
    expect(await screen.findByRole('alert')).toHaveTextContent('(UNSUPPORTED)');
    expect(screen.getByText(/can’t measure the folder/)).toBeVisible();
  });
});

describe('Settings and the activity panel', () => {
  const task: ActivityTask = { key: 'items:one', kind: 'research', title: 'Items', identity: { productId: 'wow', buildIdentity: 'a'.repeat(64), buildVersion: '1.2', sourceRegion: 'us', researchLocale: 'enUS' }, status: 'pending', stage: 'Preparing sources', measurement: null, problem: null, actions: [] };
  it('Escape closes the activity panel first and only then leaves Settings; About is inert under the panel', async () => {
    const onBack = vi.fn(), origin: SettingsOrigin = { backLabel: 'Back to Discovery', returnsTo: 'Discovery', kept: 'Discovery kept as you left it' };
    render(<DownloadActivity tasks={[task]} onAction={() => {}} contextKey="discovery"><SettingsView bridge={bridgeMock()} origin={origin} onBack={onBack}/></DownloadActivity>);
    await screen.findByRole('switch', { name: /Community build history/ });
    fireEvent.click(document.querySelector('.st-toolbar .dv-chip')!);
    expect(document.getElementById('download-activity-panel')).toBeInTheDocument();
    expect(document.querySelector('.st-side')).toHaveAttribute('inert');
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(document.getElementById('download-activity-panel')).not.toBeInTheDocument();
    expect(onBack).not.toHaveBeenCalled();
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onBack).toHaveBeenCalledTimes(1);
  });
});

describe('Home entry', () => {
  it('sits after Choose build and returns to the area it was opened from', () => {
    const open = vi.fn(), w = workspace as unknown as WorkspaceSnapshot;
    render(<SettingsProvider value={{ open, register: () => () => {} }}><DownloadActivity tasks={[]} onAction={() => {}} contextKey="home"><BuildHeader snapshot={w} label="Retail" onChoose={() => {}} area="files"/></DownloadActivity></SettingsProvider>);
    const entry = screen.getByRole('button', { name: 'Settings' });
    expect(entry.previousElementSibling).toHaveTextContent('← Choose build');
    fireEvent.click(entry);
    const origin = open.mock.calls[0][0] as SettingsOrigin;
    expect(origin).toMatchObject({ backLabel: 'Back to Files', returnsTo: 'Files' });
    render(<>{origin.kept}</>);
    expect(screen.getByText(`${w.build.productId} ${w.build.version}`)).toBeInTheDocument();
  });
});
