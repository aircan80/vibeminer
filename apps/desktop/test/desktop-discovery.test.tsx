import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App, buildDownloads, downloadedPercent } from '../src/renderer/App';
import { ActivityTrigger, DownloadActivity } from '../src/renderer/DownloadActivity';
import type { ActivityTask } from '../src/renderer/download-activity-state';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { DiscoveryPage, RetentionSnapshot } from '../../../contracts/generated/discovery';
import fixture from '../../../contracts/examples/discovery-page.json';
import retained from '../../../contracts/examples/retention-snapshot.json';
const original = fixture as DiscoveryPage;
const second = { ...original.builds[0], id: 'f'.repeat(64), buildConfig: 'c'.repeat(32), version: '12.1.0.70011', buildId: '70011', identityFields: {} };
const page: DiscoveryPage = { ...original, builds: [original.builds[0], second] };
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const unavailable = async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Outside this test.', retryable: false } });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
function bridgeMock(read: DiscoveryPage = page): DiscoveryBridge {
  return {
    itemsData: vi.fn(unavailable), itemsPrepare: vi.fn(unavailable), itemsPage: vi.fn(unavailable), itemInspect: vi.fn(unavailable),
    products: vi.fn(async () => success({ products: [{ id: 'wow', label: 'Retail', description: 'World of Warcraft' }] })),
    read: vi.fn(async params => success({ ...read, productId: params.productId })),
    tableData: vi.fn(async () => success({ snapshot: null })), tablePrepare: vi.fn(unavailable), tableRows: vi.fn(unavailable), tablesCatalog: vi.fn(unavailable), tableDefinition: vi.fn(unavailable),
    download: vi.fn(unavailable), inventory: vi.fn(unavailable), open: vi.fn(unavailable),
    refresh: vi.fn(async () => success(read)), cancel: vi.fn(async () => success({ requested: true })), onProgress: vi.fn(() => () => {})
  };
}
beforeEach(() => { localStorage.clear(); });
afterEach(cleanup);
async function ready(bridge = bridgeMock()) { render(<App bridge={bridge}/>); await screen.findByRole('listbox', { name: 'Builds' }); return bridge; }
function snapshot(patch: Partial<RetentionSnapshot['coverage']>, top: Partial<RetentionSnapshot> = {}): RetentionSnapshot {
  const s = structuredClone(retained) as RetentionSnapshot;
  // Internally consistent: observed = present + missing + corrupt, and an assessed check carries its time.
  return { ...s, jobId: 'job-1', mode: 'downloadAll', paused: false, state: 'waiting', progress: null, pendingObjects: 0, objectCount: '10', totalEncodedBytes: '1000', failedObjects: '0', unresolvedObjects: '0', ...top, coverage: { ...s.coverage, receiptObjects: '10', receiptBytes: '1000', receiptedAt: '2026-10-01T00:00:00Z', presenceState: 'assessed', presenceAssessedAt: '2026-10-01T00:00:00Z', observedObjects: '10', presentObjects: '4', presentBytes: '410', missingObjects: '6', corruptObjects: '0', ...patch } };
}

describe('Downloaded percentage', () => {
  it('shows a percentage only after a complete presence check of the whole download manifest', () => {
    expect(downloadedPercent(undefined)).toBeNull();
    expect(downloadedPercent(snapshot({ presenceState: 'notAssessed' }))).toBeNull();
    expect(downloadedPercent(snapshot({ presenceState: 'assessing' }))).toBeNull();
    expect(downloadedPercent(snapshot({ presenceState: 'interrupted' }))).toBeNull();
    expect(downloadedPercent(snapshot({ observedObjects: '9' }))).toBeNull();
    expect(downloadedPercent(snapshot({}, { failedObjects: '1' }))).toBeNull();
    expect(downloadedPercent(snapshot({}, { unresolvedObjects: '1' }))).toBeNull();
    expect(downloadedPercent(snapshot({}, { totalEncodedBytes: '0' }))).toBeNull();
  });
  it('never rounds up to 100% and keeps known zero distinct from tiny shares', () => {
    expect(downloadedPercent(snapshot({ presentBytes: '410' }))).toBe('41%');
    expect(downloadedPercent(snapshot({ presentBytes: '1000', presentObjects: '10' }))).toBe('100%');
    expect(downloadedPercent(snapshot({ presentBytes: '1000', presentObjects: '9' }))).toBe('99%');
    expect(downloadedPercent(snapshot({ presentBytes: '9999' }, { totalEncodedBytes: '10000' }))).toBe('99%');
    expect(downloadedPercent(snapshot({ presentBytes: '4' }))).toBe('0.4%');
    expect(downloadedPercent(snapshot({ presentBytes: '1' }, { totalEncodedBytes: '100000' }))).toBe('<0.1%');
    expect(downloadedPercent(snapshot({ presentBytes: '0' }))).toBe('0%');
    expect(downloadedPercent(snapshot({ presentBytes: '18446744073709551615' }, { totalEncodedBytes: '36893488147419103230' }))).toBe('50%');
  });
});

describe('saved download jobs per build', () => {
  const target = (region: string) => ({ productId: 'wow', buildIdentity: original.builds[0].id, sourceRegion: region, ledgerId: 'e'.repeat(64) });
  it('shows the newest complete check and stays running while any of its jobs runs', () => {
    const older = snapshot({ presenceAssessedAt: '2026-10-01T00:00:00Z', presentBytes: '100' }, { target: target('us') });
    const newer = snapshot({ presenceAssessedAt: '2026-10-02T00:00:00Z', presentBytes: '500' }, { target: target('eu') });
    const running = snapshot({ presenceState: 'notAssessed', presenceAssessedAt: null }, { target: target('kr'), state: 'running' });
    const download = buildDownloads([older, running, newer]).get(`wow:${original.builds[0].id}`)!;
    expect(download.job).toBe(newer);
    expect(download.running).toBe(true);
    expect(buildDownloads([running]).get(`wow:${original.builds[0].id}`)!.job).toBe(running);
  });
  it('renders Not opened, a percentage with a live marker, corrupt objects and Not checked in the list and inspector', async () => {
    const prepared = { ...second, isLocal: true };
    const jobs = [snapshot({ presentBytes: '410', missingObjects: '4', corruptObjects: '2' }, { target: target('us'), state: 'running' })];
    const bridge = { ...bridgeMock({ ...page, builds: [{ ...original.builds[0], isLocal: true }, prepared] }), capabilities: vi.fn(async () => success(['retention-v1'])), retentionList: vi.fn(async () => success({ jobs })), retentionRead: vi.fn(async () => success(jobs[0])), retentionSet: vi.fn(unavailable) } as DiscoveryBridge;
    await ready(bridge);
    const list = screen.getByRole('listbox', { name: 'Builds' });
    await waitFor(() => expect(within(list).getAllByRole('option')[0]).toHaveTextContent('41%'));
    expect(within(list).getAllByRole('option')[0]).toHaveTextContent(/downloading/);
    expect(within(list).getByRole('option', { name: /70011/ })).toHaveTextContent('Not checked');
    fireEvent.click(within(list).getAllByRole('option')[0]);
    expect(screen.getByText(/Download all is on for this build/)).toBeVisible();
    expect(screen.getByText('Downloaded', { selector: 'dt' }).nextElementSibling).toHaveTextContent('41%');
    expect(screen.getByText('Corrupt', { selector: 'dt' }).nextElementSibling).toHaveTextContent('2 objects found corrupt');
    expect(screen.getByText('Activity', { selector: 'dt' }).nextElementSibling).toHaveTextContent('Download all running');
    fireEvent.click(within(list).getByRole('option', { name: /70011/ }));
    expect(screen.getByText(/Game content downloads on demand/)).toBeVisible();
  });
  it('says Not opened for a build with no job that was never prepared', async () => {
    await ready();
    expect(within(screen.getByRole('listbox', { name: 'Builds' })).getAllByRole('option')[0]).toHaveTextContent('Not opened');
  });
});

describe('desktop discovery layout', () => {
  it('lists builds as one listbox tab stop with Home/End/arrow selection', async () => {
    await ready();
    const list = screen.getByRole('listbox', { name: 'Builds' });
    expect(list).toHaveAttribute('tabindex', '0');
    for (const option of within(list).getAllByRole('option')) expect(option).not.toHaveAttribute('tabindex');
    list.focus();
    fireEvent.keyDown(list, { key: 'Enter' });
    expect(within(list).getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true');
    fireEvent.keyDown(list, { key: 'End' });
    expect(within(list).getByRole('option', { name: /70011/ })).toHaveAttribute('aria-selected', 'true');
    expect(list).toHaveAttribute('aria-activedescendant', `build-option-${second.id}`);
    fireEvent.keyDown(list, { key: 'Home' });
    expect(within(list).getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true');
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    expect(within(list).getByRole('option', { name: /70011/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('heading', { name: '12.1.0.70011' })).toBeVisible();
    expect(list).toHaveFocus();
  });
  it('starts arrow selection at the first build when nothing is selected', async () => {
    await ready();
    const list = screen.getByRole('listbox', { name: 'Builds' });
    expect(within(list).queryByRole('option', { selected: true })).toBeNull();
    list.focus();
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    expect(within(list).getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true');
  });
  it('keeps focus on Product after choosing another product', async () => {
    const bridge = bridgeMock();
    vi.mocked(bridge.products).mockResolvedValue(success({ products: [{ id: 'wow', label: 'Retail', description: 'World of Warcraft' }, { id: 'wowt', label: 'PTR', description: 'Public test' }] }));
    await ready(bridge);
    const product = screen.getByRole('combobox', { name: 'Product' });
    product.focus();
    fireEvent.change(product, { target: { value: 'wowt' } });
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Product' })).toHaveValue('wowt'));
    expect(screen.getByRole('combobox', { name: 'Product' })).toHaveFocus();
  });
  it('separates a missing key ring from a present but empty one', async () => {
    await ready();
    const list = screen.getByRole('listbox', { name: 'Builds' });
    fireEvent.click(within(list).getAllByRole('option')[0]);
    expect(original.builds[0].identityFields.KeyRing).toBe('');
    expect(screen.getByText('Key ring').nextElementSibling).toHaveTextContent('(empty value)');
    fireEvent.click(within(list).getByRole('option', { name: /70011/ }));
    expect(screen.getByText('Key ring').nextElementSibling).toHaveTextContent('Not supplied');
  });
  it('separates a missing product config from a present but empty one', async () => {
    const withEmpty = { ...original.builds[0], productConfig: null, identityFields: { ...original.builds[0].identityFields, ProductConfig: '' } };
    const absent = { ...second, productConfig: null, identityFields: {} };
    await ready(bridgeMock({ ...page, builds: [withEmpty, absent] }));
    const list = screen.getByRole('listbox', { name: 'Builds' });
    fireEvent.click(within(list).getAllByRole('option')[0]);
    expect(screen.getByText('Product config').nextElementSibling).toHaveTextContent('(empty value)');
    fireEvent.click(within(list).getByRole('option', { name: /70011/ }));
    expect(screen.getByText('Product config').nextElementSibling).toHaveTextContent('Not supplied');
  });
  it('keeps a filtered-out selection selected and says so instead of picking another build', async () => {
    await ready();
    const list = screen.getByRole('listbox', { name: 'Builds' });
    fireEvent.click(within(list).getByRole('option', { name: /70011/ }));
    fireEvent.change(screen.getByRole('searchbox', { name: /Filter version or build number/ }), { target: { value: '70448' } });
    expect(screen.getByText('Selected build is hidden by these filters')).toBeVisible();
    expect(screen.getByRole('heading', { name: '12.1.0.70011' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(screen.queryByText('Selected build is hidden by these filters')).not.toBeInTheDocument();
  });
  it('keeps a selection hidden by an engine-side filter selected, with the notice and Clear filters', async () => {
    const prepared = { ...original.builds[0], isLocal: true };
    const bridge = bridgeMock({ ...page, builds: [prepared, second] });
    vi.mocked(bridge.read).mockImplementation(async params => success({ ...page, productId: params.productId, builds: params.filter === 'local' ? [prepared] : [prepared, second] }));
    await ready(bridge);
    fireEvent.click(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ }));
    fireEvent.click(screen.getByRole('button', { name: /^Prepared$/ }));
    await screen.findByText('Selected build is hidden by these filters');
    expect(screen.getByRole('heading', { name: '12.1.0.70011' })).toBeVisible();
    const clear = screen.getByRole('button', { name: 'Clear filters' });
    clear.focus();
    fireEvent.click(clear);
    expect(screen.getByRole('button', { name: /^All$/ })).toHaveFocus();
    await waitFor(() => expect(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ })).toHaveAttribute('aria-selected', 'true'));
    expect(screen.getByRole('button', { name: /^All$/ })).toHaveFocus();
    expect(screen.queryByText('Selected build is hidden by these filters')).not.toBeInTheDocument();
  });
  it('drops the pre-refresh copy of a selection hidden by a filter once a refresh completes', async () => {
    const prepared = { ...original.builds[0], isLocal: true };
    const bridge = bridgeMock({ ...page, builds: [prepared, second] });
    vi.mocked(bridge.read).mockImplementation(async params => success({ ...page, productId: params.productId, builds: params.filter === 'local' ? [prepared] : [prepared, second] }));
    vi.mocked(bridge.refresh).mockResolvedValue(success({ ...page, builds: [prepared, second] }));
    await ready(bridge);
    fireEvent.click(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ }));
    fireEvent.click(screen.getByRole('button', { name: /^Prepared$/ }));
    await screen.findByText('Selected build is hidden by these filters');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/Discovery complete/);
    expect(screen.queryByRole('heading', { name: '12.1.0.70011' })).not.toBeInTheDocument();
    expect(screen.getByText('Selected build is hidden')).toBeVisible();
    expect(screen.getByText('Selected build is hidden by these filters')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Clear filters' })).toBeVisible();
  });
  it('focuses the filter with Ctrl+F and lets F5 start but never cancel a refresh', async () => {
    const bridge = await ready();
    const held = deferred<BridgeReply<DiscoveryPage>>();
    vi.mocked(bridge.refresh).mockReturnValueOnce(held.promise);
    fireEvent.keyDown(window, { key: 'f', ctrlKey: true });
    expect(screen.getByRole('searchbox', { name: /Filter version or build number/ })).toHaveFocus();
    fireEvent.keyDown(window, { key: 'F5' });
    await screen.findByRole('button', { name: /Cancel refresh/ });
    fireEvent.keyDown(window, { key: 'F5' });
    expect(bridge.refresh).toHaveBeenCalledTimes(1);
    expect(bridge.cancel).not.toHaveBeenCalled();
    await act(async () => { held.resolve(success(page)); });
    await screen.findByText(/Discovery complete/);
  });
  it('disables filters and hides the inspector when nothing has been saved', async () => {
    const empty: DiscoveryPage = { ...page, builds: [], hasSavedHistory: false, connection: 'notChecked', lastCheckedAt: null, checkedRegions: [] };
    render(<App bridge={bridgeMock(empty)}/>);
    await screen.findByRole('heading', { name: 'Your research starts with a build' });
    expect(screen.getByRole('searchbox', { name: /Filter version or build number/ })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: 'Advertisement region' })).toBeDisabled();
    for (const name of ['All', 'Prepared', 'Advertised']) expect(screen.getByRole('button', { name: new RegExp(`^${name}$`) })).toBeDisabled();
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Refresh builds' })).toHaveLength(1);
  });
  it('has no activity entry while nothing is running or waiting', async () => {
    await ready();
    expect(screen.queryByRole('button', { name: /Downloads & activity|Needs attention/ })).not.toBeInTheDocument();
    expect(document.querySelector('.download-activity-floating')).toBeNull();
  });
});

describe('compact activity entry', () => {
  const task = (status: ActivityTask['status']): ActivityTask => ({ key: `table:${status}`, kind: 'research', title: 'Item table', identity: { productId: 'wow', buildIdentity: 'd'.repeat(64), sourceRegion: 'us' }, status, stage: 'Preparing Item table', measurement: null, problem: status === 'failed' ? { code: 'FAILED', message: 'Authored failure.' } : null, actions: [] });
  it('appears for active work and switches to Needs attention for an outcome to review', () => {
    const { rerender } = render(<DownloadActivity tasks={[]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity>);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    rerender(<DownloadActivity tasks={[task('pending')]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity>);
    expect(document.querySelector('.dv-chip')).toHaveAccessibleName(/Downloads & activity/);
    expect(document.querySelector('.dv-chip')).toHaveAttribute('aria-expanded', 'false');
    rerender(<DownloadActivity tasks={[task('failed')]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity>);
    const entry = document.querySelector<HTMLButtonElement>('.dv-chip')!;
    expect(entry).toHaveAccessibleName(/Needs attention/);
    expect(entry).toHaveClass('attention');
    fireEvent.click(entry);
    expect(entry).toHaveAttribute('aria-expanded', 'true');
    return waitFor(() => expect(screen.getByRole('heading', { name: 'Downloads & activity' })).toHaveFocus());
  });
  it('drops the entry and its pulse once a Download all pass has completed', () => {
    const completed: ActivityTask = { ...task('completed'), key: 'retention:done', kind: 'buildRetention', stage: 'Encoded retention pass complete' };
    const running: ActivityTask = { ...completed, key: 'retention:running', status: 'running', stage: 'Encoded retention pass underway' };
    const { rerender } = render(<DownloadActivity tasks={[running]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity>);
    expect(document.querySelector('.dv-chip .dv-pulse')).not.toBeNull();
    rerender(<DownloadActivity tasks={[completed]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity>);
    expect(document.querySelector('.dv-chip')).toBeNull();
  });
  it('drops the entry for finished or canceled work that has nothing to dismiss, and keeps it while one can be dismissed', () => {
    const canceled = { ...task('canceled'), stage: 'Request stopped' };
    const { rerender } = render(<DownloadActivity tasks={[canceled]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity>);
    expect(document.querySelector('.dv-chip')).toBeNull();
    rerender(<DownloadActivity tasks={[{ ...canceled, actions: [{ id: 'dismiss', label: 'Dismiss outcome' }] }]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity>);
    expect(document.querySelector('.dv-chip')).not.toBeNull();
  });
  it('keeps the docked panel and its entry when the last task goes away while the panel is open', async () => {
    const { rerender } = render(<div className="discovery-screen"><h1 tabIndex={-1}>Builds</h1><DownloadActivity tasks={[task('failed')]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity></div>);
    fireEvent.click(document.querySelector<HTMLButtonElement>('.dv-chip')!);
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Downloads & activity' })).toHaveFocus());
    expect(document.getElementById('download-activity-slot')).toContainElement(document.getElementById('download-activity-panel'));
    rerender(<div className="discovery-screen"><h1 tabIndex={-1}>Builds</h1><DownloadActivity tasks={[]} onAction={() => {}} contextKey="discovery"><ActivityTrigger compact/></DownloadActivity></div>);
    expect(document.getElementById('download-activity-slot')).toContainElement(document.getElementById('download-activity-panel'));
    expect(document.activeElement).not.toBe(document.body);
    fireEvent.click(screen.getByRole('button', { name: 'Close downloads and activity' }));
    await waitFor(() => expect(document.querySelector('.dv-chip')).toBeNull());
    expect(screen.getByRole('heading', { name: 'Builds' })).toHaveFocus();
  });
});
