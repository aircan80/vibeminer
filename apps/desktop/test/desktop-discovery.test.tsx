import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App, buildDownloads, checkLine, checkSummary, downloadedPercent, productProblems, retainedEvidence } from '../src/renderer/App';
import { ActivityTrigger, DownloadActivity } from '../src/renderer/DownloadActivity';
import { summaryLine, type ActivityTask } from '../src/renderer/download-activity-state';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { DiscoveryCatalogPage, DiscoveryPage, EngineProgress, RetentionSnapshot, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import fixture from '../../../contracts/examples/discovery-page.json';
import retained from '../../../contracts/examples/retention-snapshot.json';
import { catalog, notChecked, statusOf, testProducts } from './catalog-page';
const original = fixture as DiscoveryPage;
// A saved build that was not advertised at the last check.
const second = { ...original.builds[0], id: 'f'.repeat(64), buildConfig: 'c'.repeat(32), version: '12.1.0.70011', buildId: '70011', identityFields: {}, advertisedRegions: [], isAdvertised: false };
const ptr = { ...original.builds[0], id: 'e'.repeat(64), productId: 'wowt', buildConfig: 'b'.repeat(32), version: '12.2.0.70631', buildId: '70631' };
const page: DiscoveryPage = { ...original, builds: [original.builds[0], second] };
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const unavailable = async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Outside this test.', retryable: false } });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
/** Reads answer the Product and visibility filters the way the engine does. */
function bridgeMock(read: DiscoveryPage = page, extra: DiscoveryCatalogPage['builds'] = []): DiscoveryBridge {
  const answer = (productId?: string, filter: 'all' | 'local' | 'advertised' = 'all') => catalog(read, { productId: productId ?? null, filter, builds: [...read.builds, ...extra].filter(b => (!productId || b.productId === productId) && (filter === 'all' || filter === 'local' && b.isLocal || filter === 'advertised' && b.isAdvertised)) });
  return {
    itemsData: vi.fn(unavailable), itemsPrepare: vi.fn(unavailable), itemsPage: vi.fn(unavailable), itemInspect: vi.fn(unavailable),
    products: vi.fn(async () => success({ products: testProducts })),
    read: vi.fn(unavailable), refresh: vi.fn(unavailable),
    readAll: vi.fn(async params => success(answer(params.productId, params.filter))),
    refreshAll: vi.fn(async () => success(answer())),
    tableData: vi.fn(async () => success({ snapshot: null })), tablePrepare: vi.fn(unavailable), tableRows: vi.fn(unavailable), tablesCatalog: vi.fn(unavailable), tableDefinition: vi.fn(unavailable),
    download: vi.fn(unavailable), inventory: vi.fn(unavailable), open: vi.fn(unavailable),
    cancel: vi.fn(async () => success({ requested: true })), onProgress: vi.fn(() => () => {})
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
  it('lays the grid out as Product, Version, Build, Downloaded and In VibeMiner, one row per exact build across products', async () => {
    await ready(bridgeMock(page, [ptr]));
    const head = document.querySelector('.dv-thead')!;
    expect([...head.children].map(cell => cell.textContent)).toEqual(['Product', 'Version', 'Build', 'Downloaded', 'In VibeMiner']);
    expect(head).toHaveAttribute('aria-hidden', 'true');
    const rows = within(screen.getByRole('listbox', { name: 'Builds' })).getAllByRole('option');
    expect(rows.map(row => row.querySelector('.dv-product')!.textContent)).toEqual(['wow', 'wow', 'wowt']);
    expect(rows.map(row => row.children.length)).toEqual([5, 5, 5]);
    expect(rows[2].children[1]).toHaveTextContent('12.2.0.70631'); expect(rows[2].children[2]).toHaveTextContent('70631');
    // Publication and Advertised in moved to the inspector.
    expect(screen.queryByText('None at last check')).not.toBeInTheDocument(); expect(screen.queryByText('Not advertised')).not.toBeInTheDocument();
    fireEvent.click(rows[2]);
    expect(screen.getByRole('heading', { name: '12.2.0.70631' })).toBeVisible();
    expect(screen.getByText('World of Warcraft · PTR ·', { exact: false })).toHaveTextContent('World of Warcraft · PTR · wowt');
    expect(screen.getByText('Product', { selector: 'dt' }).nextElementSibling).toHaveTextContent('wowt · PTR');
    expect(screen.getByText('Advertised in', { selector: 'dt' }).nextElementSibling).toHaveTextContent('US');
    expect(screen.getByText('Publication', { selector: 'dt' }).nextElementSibling).toHaveTextContent(/^Current: advertised at the last completed check \(.+\)$/);
    expect(screen.getByText('CDN routes', { selector: 'dt' })).toBeVisible();
  });
  it('marks Current builds with a warm tint and a Current label, so colour is never the only signal', async () => {
    await ready();
    const list = screen.getByRole('listbox', { name: 'Builds' });
    const current = within(list).getAllByRole('option')[0], saved = within(list).getByRole('option', { name: /70011/ });
    expect(current).toHaveClass('current'); expect(within(current).getByText('Current', { exact: true })).toHaveClass('dv-pill', 'current');
    expect(current).toHaveAccessibleName(/Current/);
    expect(saved).not.toHaveClass('current'); expect(within(saved).queryByText('Current')).not.toBeInTheDocument();
    // Selection keeps its own mint state; the label stays on a selected Current row.
    fireEvent.click(current);
    expect(current).toHaveAttribute('aria-selected', 'true'); expect(within(current).getByText('Current', { exact: true })).toBeVisible();
    fireEvent.click(saved);
    expect(screen.getByText('Publication', { selector: 'dt' }).nextElementSibling).toHaveTextContent(/^Not advertised at the last completed check$/);
    expect(screen.getByText('Advertised in', { selector: 'dt' }).nextElementSibling).toHaveTextContent('None at last check');
  });
  it('filters by one product through the engine and keeps a selection of another product hidden, not replaced', async () => {
    const bridge = bridgeMock(page, [ptr]);
    await ready(bridge);
    const product = screen.getByRole('combobox', { name: 'Product' });
    expect([...product.querySelectorAll('option')].map(option => option.textContent)).toEqual(['All products', 'wow · Retail', 'wowt · PTR']);
    expect(product).toHaveValue('all');
    fireEvent.click(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ }));
    product.focus();
    fireEvent.change(product, { target: { value: 'wowt' } });
    await waitFor(() => expect(bridge.readAll).toHaveBeenLastCalledWith({ productId: 'wowt', filter: 'all', limit: 50 }));
    await waitFor(() => expect(within(screen.getByRole('listbox', { name: 'Builds' })).getAllByRole('option')).toHaveLength(1));
    expect(product).toHaveFocus();
    expect(screen.getByText('Selected build is hidden by these filters')).toBeVisible();
    expect(screen.getByText(/wow 12\.1\.0\.70011 is still selected\. Clear filters to see it in the list\./)).toBeVisible();
    expect(screen.getByRole('heading', { name: '12.1.0.70011' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(screen.getByRole('button', { name: /^All$/ })).toHaveFocus();
    await waitFor(() => expect(bridge.readAll).toHaveBeenLastCalledWith({ filter: 'all', limit: 50 }));
    expect(screen.getByRole('combobox', { name: 'Product' })).toHaveValue('all');
    await waitFor(() => expect(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ })).toHaveAttribute('aria-selected', 'true'));
  });
  it('keeps the toolbar to the brand and puts search, Product, visibility and Refresh builds in one filter row', async () => {
    await ready();
    const toolbar = document.querySelector('.dv-toolbar')!;
    expect(within(toolbar as HTMLElement).queryByRole('combobox')).toBeNull();
    expect(within(toolbar as HTMLElement).queryByRole('button', { name: /Refresh builds/ })).toBeNull();
    expect(within(toolbar as HTMLElement).getByRole('img', { name: 'VibeMiner' })).toBeVisible();
    const head = document.querySelector('.dv-pane-head') as HTMLElement;
    const order = [screen.getByRole('searchbox', { name: /Filter version or build number/ }), screen.getByRole('combobox', { name: 'Product' }),
      screen.getByRole('button', { name: /^All$/ }), screen.getByRole('button', { name: /^Prepared$/ }), screen.getByRole('button', { name: /^Current$/ }), screen.getByRole('button', { name: 'Refresh builds' })];
    for (const control of order) expect(head).toContainElement(control);
    // DOM order is Tab order: every control is a native tab stop in visual order, Refresh last.
    for (let i = 1; i < order.length; i++) expect(order[i - 1].compareDocumentPosition(order[i]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    for (const control of order) expect(control).not.toHaveAttribute('tabindex');
    expect(order[5]).toHaveClass('dv-refresh'); expect(order[5]).not.toHaveClass('primary'); expect(order[5]).toHaveAttribute('aria-keyshortcuts', 'F5');
    expect(screen.getByRole('group', { name: 'Build visibility' })).toBeVisible();
  });
  it('shows progress across products in the status bar and lists per-product failures in the notice', async () => {
    const listeners = new Set<(progress: EngineProgress) => void>();
    const bridge = bridgeMock();
    vi.mocked(bridge.onProgress).mockImplementation(listener => { listeners.add(listener); return () => { listeners.delete(listener); }; });
    const held = deferred<BridgeReply<DiscoveryCatalogPage>>();
    vi.mocked(bridge.refreshAll).mockReturnValueOnce(held.promise);
    await ready(bridge);
    expect(document.querySelector('.dv-statusbar')).toHaveTextContent('Checked 1 of 2 products · wowt not checked');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    const id = vi.mocked(bridge.refreshAll).mock.calls[0][0];
    expect(document.querySelector('.dv-statusbar')).toHaveTextContent('Checking 2 products…');
    act(() => { for (const listener of listeners) listener({ protocolVersion: 1, type: 'progress', requestId: id, sequence: 3, stage: 'Checking official metadata', region: null, completed: 5, total: 13, unit: 'products' }); });
    expect(document.querySelector('.dv-statusbar')).toHaveTextContent('Checking 13 products · 5 of 13 done…');
    expect(document.querySelector('.dv-statusbar')).not.toHaveTextContent('%');
    // Another request's progress is ignored.
    act(() => { for (const listener of listeners) listener({ protocolVersion: 1, type: 'progress', requestId: 'other', sequence: 9, stage: 'Checking official metadata', region: null, completed: 12, total: 13, unit: 'products' }); });
    expect(document.querySelector('.dv-statusbar')).toHaveTextContent('5 of 13 done');
    const offline = { region: null, code: 'NETWORK_UNAVAILABLE', message: 'Official metadata could not be reached.' };
    await act(async () => held.resolve(success(catalog(page, { refreshProblems: [{ productId: 'wow', code: 'CATALOG_BUSY', message: 'The discovery catalog is busy.' }] }, [statusOf(page), { ...notChecked('wowt'), connection: 'offline', lastCheckedAt: '2026-10-20T09:00:00Z', errors: [offline] }]))));
    const notice = screen.getByText('Refresh unavailable').closest('.dv-notice') as HTMLElement;
    expect(notice).toHaveTextContent('wow: check did not complete. The discovery catalog is busy. (CATALOG_BUSY)');
    expect(notice).toHaveTextContent('wowt: Official metadata could not be reached. (NETWORK_UNAVAILABLE)');
    expect(notice).toHaveClass('error'); // a product could not be reached
    expect(within(notice).getByRole('button', { name: 'Retry discovery' })).toBeEnabled();
    expect(screen.getByRole('status')).toHaveTextContent('Check completed with problems for 2 of 2 products. Earlier saved observations are kept for wow. wowt: no saved observations.');
    expect(document.querySelector('.dv-statusbar')).toHaveTextContent('Checked 0 of 2 products · wow, wowt could not be checked'); // a failed or offline check is never counted
  });
  it('names the products a canceled refresh did not check', async () => {
    const bridge = bridgeMock(), held = deferred<BridgeReply<DiscoveryCatalogPage>>();
    vi.mocked(bridge.refreshAll).mockReturnValueOnce(held.promise);
    await ready(bridge);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel refresh' }));
    // wow committed before the stop; wowt did not.
    vi.mocked(bridge.readAll).mockResolvedValue(success(catalog({ ...page, lastCheckedAt: '2026-10-20T09:00:00Z' })));
    await act(async () => held.resolve({ ok: false, error: { code: 'CANCELED', message: 'Canceled.', retryable: true } }));
    await waitFor(() => expect(document.querySelector('.dv-checks')).toHaveTextContent(/^Checked 1 of 2 products · wowt not checked$/));
    expect(document.querySelector('.dv-checks')).toHaveClass('dv-secondary');
  });
  it('counts only the checks a refresh completed: an unchanged or offline product is not counted as checked', async () => {
    const bridge = bridgeMock(); await ready(bridge);
    // Before any refresh this session, the saved latest checks are summarized.
    expect(document.querySelector('.dv-checks')).toHaveTextContent(/^Checked 1 of 2 products · wowt not checked$/);
    vi.mocked(bridge.refreshAll).mockResolvedValueOnce(success(catalog(page, {}, [statusOf({ ...page, lastCheckedAt: '2026-10-20T09:00:00Z' }), { ...notChecked('wowt'), connection: 'offline', lastCheckedAt: '2026-10-20T09:00:01Z', errors: [{ region: null, code: 'NETWORK_UNAVAILABLE', message: 'Unreachable.' }] }])));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    await waitFor(() => expect(document.querySelector('.dv-checks')).toHaveTextContent(/^Checked 1 of 2 products · wowt could not be checked$/));
    // A refresh that commits nothing new for wow does not count wow's older saved check.
    vi.mocked(bridge.refreshAll).mockResolvedValueOnce(success(catalog(page, { refreshProblems: [] }, [statusOf({ ...page, lastCheckedAt: '2026-10-20T09:00:00Z' }), { ...notChecked('wowt'), connection: 'online', lastCheckedAt: '2026-10-20T10:00:00Z' }])));
    fireEvent.click(document.querySelector<HTMLButtonElement>('.dv-refresh')!);
    await waitFor(() => expect(document.querySelector('.dv-checks')).toHaveTextContent(/^Checked 1 of 2 products · wow not checked$/));
  });
  it('labels the newest check as the latest and shows the oldest when products were checked at different times', async () => {
    const bridge = bridgeMock();
    vi.mocked(bridge.readAll).mockResolvedValue(success(catalog(page, {}, [statusOf(page), { ...notChecked('wowt'), connection: 'online', lastCheckedAt: '2026-10-20T09:00:00Z' }])));
    await ready(bridge);
    const latest = document.querySelector('.dv-latest')!;
    const when = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
    expect(latest).toHaveTextContent(`Latest check ${when('2026-10-20T09:00:00Z')} · oldest ${when(page.lastCheckedAt!)}`);
    expect(latest.querySelector('.dv-secondary')).toHaveTextContent(`· oldest ${when(page.lastCheckedAt!)}`);
    expect(document.querySelector('.dv-statusbar')).not.toHaveTextContent('Last check');
    cleanup();
    await ready(bridgeMock());
    expect(document.querySelector('.dv-latest')).toHaveTextContent(`Latest check ${when(page.lastCheckedAt!)}`);
    expect(document.querySelector('.dv-latest')).not.toHaveTextContent('oldest');
  });
  describe('failed checks never read as completed checks that found nothing', () => {
    const http500 = { region: null, code: 'HTTP_STATUS', message: 'Official metadata returned an unsuccessful HTTP status.' };
    const offline = { region: null, code: 'NETWORK_UNAVAILABLE', message: 'Official metadata could not be reached.' };
    // An HTTP 500 on both documents is saved as partial with no checked regions: nothing was received.
    const failedHttp = (productId: string) => ({ ...notChecked(productId), connection: 'partial' as const, lastCheckedAt: '2026-10-20T09:00:00Z', errors: [http500, http500] });
    const answering = (statuses: ReturnType<typeof notChecked>[], read: DiscoveryPage = page) => (params: { productId?: string; filter?: 'all' | 'local' | 'advertised' }) =>
      Promise.resolve(success(catalog(read, { productId: params.productId ?? null, filter: params.filter ?? 'all', builds: read.builds.filter(b => (!params.productId || b.productId === params.productId) && (params.filter !== 'advertised' || b.isAdvertised)) }, statuses)));
    it('names a filtered product whose latest check failed, with its typed problem, under All and Current', async () => {
      const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockImplementation(answering([statusOf(page), failedHttp('wowt')]));
      await ready(bridge);
      fireEvent.change(screen.getByRole('combobox', { name: 'Product' }), { target: { value: 'wowt' } });
      await screen.findByRole('heading', { name: 'Could not check wowt' });
      expect(screen.getByText(/The latest check of wowt failed: Official metadata returned an unsuccessful HTTP status\. \(HTTP_STATUS\)/)).toBeVisible();
      expect(screen.queryByText(/no advertised builds|was advertised at the last check/)).not.toBeInTheDocument();
      fireEvent.click(screen.getByRole('button', { name: /^Current$/ }));
      await waitFor(() => expect(bridge.readAll).toHaveBeenLastCalledWith({ productId: 'wowt', filter: 'advertised', limit: 50 }));
      expect(screen.getByRole('heading', { name: 'Could not check wowt' })).toBeVisible();
      expect(screen.getByText(/No build of it is known to be current\./)).toBeVisible();
      expect(screen.queryByText(/No build of wowt was advertised/)).not.toBeInTheDocument();
    });
    it('says a product had no current build only when its last check completed', async () => {
      const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockImplementation(answering([statusOf(page), { ...notChecked('wowt'), connection: 'online', lastCheckedAt: '2026-10-20T09:00:00Z', checkedRegions: ['us'] }]));
      await ready(bridge);
      fireEvent.change(screen.getByRole('combobox', { name: 'Product' }), { target: { value: 'wowt' } });
      fireEvent.click(screen.getByRole('button', { name: /^Current$/ }));
      await screen.findByRole('heading', { name: 'No current builds' });
      expect(screen.getByText(/^No build of wowt was advertised at the last check, /)).toBeVisible();
    });
    it('reports a fresh catalog where every check failed as unchecked, without retained-evidence claims or a latest check', async () => {
      const empty: DiscoveryPage = { ...page, builds: [], hasSavedHistory: false, connection: 'notChecked', lastCheckedAt: null, checkedRegions: [] };
      const bridge = bridgeMock(empty);
      render(<App bridge={bridge}/>);
      await screen.findByRole('heading', { name: 'No saved builds yet' });
      const after = [{ ...notChecked('wow'), connection: 'offline' as const, lastCheckedAt: '2026-10-20T09:00:00Z', errors: [offline] }, failedHttp('wowt')];
      vi.mocked(bridge.refreshAll).mockResolvedValueOnce(success(catalog(empty, {}, after)));
      vi.mocked(bridge.readAll).mockImplementation(answering(after, empty));
      fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
      await screen.findByRole('heading', { name: 'Could not check any product' });
      expect(screen.queryByText(/No builds advertised|No builds were advertised/)).not.toBeInTheDocument();
      expect(screen.getByRole('status')).toHaveTextContent(/^Could not refresh\. wow, wowt: no saved observations\.$/);
      expect(document.querySelector('.dv-checks')).toHaveTextContent(/^Checked 0 of 2 products · wow, wowt could not be checked$/);
      expect(screen.getByText('Refresh unavailable')).toBeVisible();
      expect(document.querySelector('.dv-statusbar')).not.toHaveTextContent(/Latest check/);
      expect(screen.queryByText(/Previous successful evidence is retained/)).not.toBeInTheDocument();
    });
    it('claims retained evidence only when saved evidence exists', async () => {
      const empty: DiscoveryPage = { ...page, builds: [], hasSavedHistory: false, connection: 'notChecked', lastCheckedAt: null, checkedRegions: [] };
      const bridge = bridgeMock(empty);
      render(<App bridge={bridge}/>);
      await screen.findByRole('heading', { name: 'No saved builds yet' });
      // wow answered with no builds; wowt failed with HTTP 500. Nothing is saved.
      const after = [{ ...notChecked('wow'), connection: 'online' as const, lastCheckedAt: '2026-10-20T09:00:00Z', checkedRegions: ['us'] }, failedHttp('wowt')];
      vi.mocked(bridge.refreshAll).mockResolvedValueOnce(success(catalog(empty, {}, after)));
      fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
      await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/^Check completed with problems for 1 of 2 products\. wowt: no saved observations\.$/));
      expect(screen.getByRole('heading', { name: 'No builds advertised' })).toBeVisible();
      expect(screen.getByText(/No builds were advertised by the checked products at .*\. wowt could not be checked\./)).toBeVisible();
      expect(document.querySelector('.dv-checks')).toHaveTextContent(/^Checked 1 of 2 products · wowt could not be checked$/);
      // Another product's saved history says nothing about the failed product: wowt still has none.
      cleanup();
      const saved = bridgeMock();
      vi.mocked(saved.refreshAll).mockResolvedValueOnce(success(catalog(page, {}, [statusOf({ ...page, lastCheckedAt: '2026-10-20T09:00:00Z' }), failedHttp('wowt')])));
      await ready(saved);
      fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
      await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/^Check completed with problems for 1 of 2 products\. wowt: no saved observations\.$/));
      const notice = screen.getByText('Refresh incomplete').closest('.dv-notice') as HTMLElement;
      expect(notice).toHaveTextContent('wowt: no saved observations.');
      expect(notice).not.toHaveTextContent(/for every product|kept for/);
    });
    it('says earlier evidence is kept only for a failed product that has its own saved history', async () => {
      const bridge = bridgeMock();
      const after = [{ ...statusOf({ ...page, lastCheckedAt: '2026-10-20T09:00:00Z' }), connection: 'offline' as const, checkedRegions: [], errors: [offline] }, { ...notChecked('wowt'), connection: 'online' as const, lastCheckedAt: '2026-10-20T09:00:00Z', checkedRegions: ['us'] }];
      vi.mocked(bridge.refreshAll).mockResolvedValueOnce(success(catalog(page, {}, after)));
      await ready(bridge);
      vi.mocked(bridge.readAll).mockImplementation(answering(after));
      fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
      await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/^Check completed with problems for 1 of 2 products\. Earlier saved observations are kept for wow\.$/));
      const notice = screen.getByText('Refresh incomplete').closest('.dv-notice') as HTMLElement;
      expect(notice).toHaveTextContent('Earlier saved observations are kept for wow. A failed check does not remove earlier advertisements.');
      expect(notice).not.toHaveTextContent(/for every product|no saved observations/);
      // The row keeps its Current label from the last completed check; the inspector says which check that was and that the latest failed.
      const row = within(screen.getByRole('listbox', { name: 'Builds' })).getAllByRole('option')[0];
      expect(within(row).getByText('Current', { exact: true })).toBeVisible();
      fireEvent.click(row);
      const when = new Date(page.builds[0].lastSeenAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
      expect(screen.getByText('Publication', { selector: 'dt' }).nextElementSibling).toHaveTextContent(`Current: advertised at the last completed check (${when}) · latest check failed`);
    });
    it('describes a product with no saved history by name in the notice and never claims every product', () => {
      expect(retainedEvidence([{ ...notChecked('wowt'), connection: 'offline', errors: [offline] }])).toBe('wowt: no saved observations.');
      expect(retainedEvidence([{ ...statusOf(page), connection: 'offline', errors: [offline] }, { ...notChecked('wowt'), connection: 'offline', errors: [offline] }])).toBe('Earlier saved observations are kept for wow. wowt: no saved observations.');
      expect(retainedEvidence([])).toBe('');
    });
    it('shows only completed checks as the latest check, never a newer failed attempt', async () => {
      const bridge = bridgeMock();
      vi.mocked(bridge.readAll).mockImplementation(answering([statusOf(page), { ...notChecked('wowt'), connection: 'offline', lastCheckedAt: '2026-10-20T09:00:00Z', errors: [offline] }]));
      await ready(bridge);
      const when = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
      expect(document.querySelector('.dv-latest')).toHaveTextContent(new RegExp(`^Latest check ${when(page.lastCheckedAt!).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
      expect(document.querySelector('.dv-checks')).toHaveTextContent(/^Checked 1 of 2 products · wowt could not be checked$/);
    });
  });
  it('summarizes checks per product without counting failures', () => {
    const statuses = [statusOf(page), { ...notChecked('wowt'), connection: 'offline' as const, lastCheckedAt: '2026-10-20T09:00:00Z' }, notChecked('wow_beta'), { ...notChecked('wow_classic'), connection: 'partial' as const, lastCheckedAt: '2026-10-20T09:00:00Z', checkedRegions: ['us'] }, { ...notChecked('wow_classic_ptr'), connection: 'partial' as const, lastCheckedAt: '2026-10-20T09:00:00Z', errors: [{ region: null, code: 'HTTP_STATUS', message: 'Official metadata returned an unsuccessful HTTP status.' }] }];
    expect(checkSummary(statuses, null, [])).toEqual({ checked: ['wow', 'wow_classic'], failed: ['wowt', 'wow_classic_ptr'], notChecked: ['wow_beta'], total: 5 }); // partial with no checked regions received nothing
    expect(checkSummary(statuses, new Map([['wow', page.lastCheckedAt], ['wowt', null], ['wow_beta', null], ['wow_classic', null], ['wow_classic_ptr', null]]), [{ productId: 'wow_beta', code: 'CATALOG_BUSY', message: 'Busy.' }]))
      .toEqual({ checked: ['wow_classic'], failed: ['wowt', 'wow_beta', 'wow_classic_ptr'], notChecked: ['wow'], total: 5 });
    expect(checkLine({ checked: ['a'], failed: [], notChecked: ['b', 'c', 'd', 'e'], total: 5 }, true)).toBe('Checked 1 of 5 products · 4 products not checked');
    expect(checkLine({ checked: [], failed: [], notChecked: ['a'], total: 1 }, false)).toBeNull();
    expect(checkLine({ checked: ['a', 'b'], failed: [], notChecked: [], total: 2 }, true)).toBe('Checked 2 products');
  });
  it('groups one problem shared by several products', () => {
    const offline = { region: null, code: 'NETWORK_UNAVAILABLE', message: 'Official metadata could not be reached.' };
    const groups = productProblems([{ ...notChecked('wow_beta'), connection: 'offline', errors: [offline] }, { ...notChecked('wow_classic_era'), connection: 'offline', errors: [offline] },
      { ...notChecked('wowt'), connection: 'partial', errors: [{ region: 'eu', code: 'ROUTE_MISSING', message: 'No route.' }] }], []);
    expect(groups.map(group => `${group.label}: ${group.text} (${group.code})`)).toEqual(['wow_beta, wow_classic_era: Official metadata could not be reached. (NETWORK_UNAVAILABLE)', 'wowt EU: No route. (ROUTE_MISSING)']);
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
    await ready(bridge);
    fireEvent.click(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ }));
    fireEvent.click(screen.getByRole('button', { name: /^Prepared$/ }));
    await screen.findByText('Selected build is hidden by these filters');
    expect(bridge.readAll).toHaveBeenLastCalledWith({ filter: 'local', limit: 50 });
    expect(screen.getByRole('heading', { name: '12.1.0.70011' })).toBeVisible();
    const clear = screen.getByRole('button', { name: 'Clear filters' });
    clear.focus();
    fireEvent.click(clear);
    expect(screen.getByRole('button', { name: /^All$/ })).toHaveFocus();
    await waitFor(() => expect(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ })).toHaveAttribute('aria-selected', 'true'));
    expect(screen.getByRole('button', { name: /^All$/ })).toHaveFocus();
    expect(screen.queryByText('Selected build is hidden by these filters')).not.toBeInTheDocument();
  });
  it('drops the pre-refresh copy of a selection hidden by a filter once a refresh completes, with plain wording', async () => {
    const prepared = { ...original.builds[0], isLocal: true };
    const bridge = bridgeMock({ ...page, builds: [prepared, second] });
    await ready(bridge);
    fireEvent.click(within(screen.getByRole('listbox', { name: 'Builds' })).getByRole('option', { name: /70011/ }));
    fireEvent.click(screen.getByRole('button', { name: /^Prepared$/ }));
    await screen.findByText('Selected build is hidden by these filters');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/Discovery complete/);
    expect(screen.queryByRole('heading', { name: '12.1.0.70011' })).not.toBeInTheDocument();
    expect(screen.getByText('Selected build is hidden')).toBeVisible();
    expect(screen.getByText('Selected build is hidden by these filters')).toBeVisible();
    expect(screen.getByText('The refresh did not reload its details. Clear filters to see it in the list.')).toBeVisible();
    expect(screen.queryByText(/still selected\./)).not.toBeInTheDocument();
    expect(screen.getByText('Clear filters to inspect it again.')).toBeVisible();
    expect(screen.queryByText(/load more/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clear filters' })).toBeVisible();
  });
  it('focuses the filter with Ctrl+F and lets F5 start but never cancel a refresh', async () => {
    const bridge = await ready();
    const held = deferred<BridgeReply<DiscoveryCatalogPage>>();
    vi.mocked(bridge.refreshAll).mockReturnValueOnce(held.promise);
    fireEvent.keyDown(window, { key: 'f', ctrlKey: true });
    expect(screen.getByRole('searchbox', { name: /Filter version or build number/ })).toHaveFocus();
    fireEvent.keyDown(window, { key: 'F5' });
    await screen.findByRole('button', { name: /Cancel refresh/ });
    fireEvent.keyDown(window, { key: 'F5' });
    expect(bridge.refreshAll).toHaveBeenCalledTimes(1);
    expect(bridge.cancel).not.toHaveBeenCalled();
    await act(async () => { held.resolve(success(catalog(page))); });
    await screen.findByText(/Discovery complete/);
  });
  it('disables filters and hides the inspector when nothing has been saved', async () => {
    const empty: DiscoveryPage = { ...page, builds: [], hasSavedHistory: false, connection: 'notChecked', lastCheckedAt: null, checkedRegions: [] };
    render(<App bridge={bridgeMock(empty)}/>);
    await screen.findByRole('heading', { name: 'No saved builds yet' });
    expect(screen.getByText("Refresh builds checks Blizzard's official version service for every World of Warcraft product.")).toBeVisible();
    expect(screen.getByRole('searchbox', { name: /Filter version or build number/ })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: 'Product' })).toBeDisabled();
    for (const name of ['All', 'Prepared', 'Current']) expect(screen.getByRole('button', { name: new RegExp(`^${name}$`) })).toBeDisabled();
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Refresh builds' })).toHaveLength(1);
    expect(document.querySelector('.dv-statusbar')).toHaveTextContent('Not checked yet');
  });
  it('names the product scope when a Product filter has no saved builds', async () => {
    await ready();
    fireEvent.change(screen.getByRole('combobox', { name: 'Product' }), { target: { value: 'wowt' } });
    await screen.findByRole('heading', { name: 'No saved builds for wowt' });
    expect(screen.getByText('wowt has not been checked yet. Refresh builds checks every product.')).toBeVisible();
    expect(screen.getByRole('combobox', { name: 'Product' })).toBeEnabled();
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

describe('status bar activity summary', () => {
  it('shows a count once when the engine stage already carries it', async () => {
    const bridge = bridgeMock(), listeners = new Set<(p: EngineProgress) => void>(), pending = deferred<BridgeReply<WorkspaceSnapshot>>();
    vi.mocked(bridge.onProgress).mockImplementation(listener => { listeners.add(listener); return () => { listeners.delete(listener); }; });
    vi.mocked(bridge.open).mockReturnValue(pending.promise);
    await ready(bridge);
    fireEvent.click(within(screen.getByRole('listbox', { name: 'Builds' })).getAllByRole('option')[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Open build' }));
    await waitFor(() => expect(bridge.open).toHaveBeenCalledTimes(1));
    const requestId = vi.mocked(bridge.open).mock.calls[0][1];
    // Check the build list's status bar while it is visible, not in the frame before preparation hides it.
    fireEvent.click(await screen.findByRole('button', { name: '← Back to build list · preparation continues' }));
    expect(document.querySelector('.discovery-screen')).not.toHaveAttribute('hidden');
    act(() => { for (const listener of listeners) listener({ protocolVersion: 1, type: 'progress', requestId, sequence: 2, stage: 'Building download ledger · 2 of 6 objects', region: 'us', completed: 2, total: 6, unit: 'objects' }); });
    const status = document.querySelector('.dv-statusbar .dv-busy:last-child')!;
    expect(status).toHaveTextContent(/^Build preparation · Building download ledger · 2 of 6 objects · 33% of current stage$/);
    await act(async () => pending.resolve({ ok: false, error: { code: 'CANCELED', message: 'Authored stop.', retryable: true } }));
  });
  it('shows refresh progress across products next to the de-duplicated activity summary', async () => {
    const bridge = bridgeMock(), listeners = new Set<(p: EngineProgress) => void>(), pending = deferred<BridgeReply<WorkspaceSnapshot>>(), refresh = deferred<BridgeReply<DiscoveryCatalogPage>>();
    vi.mocked(bridge.onProgress).mockImplementation(listener => { listeners.add(listener); return () => { listeners.delete(listener); }; });
    vi.mocked(bridge.open).mockReturnValue(pending.promise);
    vi.mocked(bridge.refreshAll).mockReturnValue(refresh.promise);
    await ready(bridge);
    fireEvent.click(within(screen.getByRole('listbox', { name: 'Builds' })).getAllByRole('option')[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Open build' }));
    await waitFor(() => expect(bridge.open).toHaveBeenCalledTimes(1));
    const openId = vi.mocked(bridge.open).mock.calls[0][1];
    // A pending opening switches to preparation one frame later. Wait for that switch, then go back
    // to the build list the way a user does, so Refresh builds never races the frame.
    fireEvent.click(await screen.findByRole('button', { name: '← Back to build list · preparation continues' }));
    expect(document.querySelector('.discovery-screen')).not.toHaveAttribute('hidden');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    const refreshId = vi.mocked(bridge.refreshAll).mock.calls[0][0];
    act(() => { for (const listener of listeners) {
      listener({ protocolVersion: 1, type: 'progress', requestId: openId, sequence: 2, stage: 'Building download ledger · 2 of 6 objects', region: 'us', completed: 2, total: 6, unit: 'objects' });
      listener({ protocolVersion: 1, type: 'progress', requestId: refreshId, sequence: 2, stage: 'Checking official metadata', region: null, completed: 5, total: 13, unit: 'products' });
    } });
    const bar = document.querySelector('.dv-statusbar')!;
    expect(bar.querySelector('.dv-busy:first-child')).toHaveTextContent(/^Checking 13 products · 5 of 13 done…$/);
    expect(bar.querySelector('.dv-busy:last-child')).toHaveTextContent(/^Build preparation · Building download ledger · 2 of 6 objects · 33% of current stage$/);
    await act(async () => { refresh.resolve(success(catalog(page))); pending.resolve({ ok: false, error: { code: 'CANCELED', message: 'Authored stop.', retryable: true } }); });
  });
  it('keeps every fact once for real engine stage text, with or without a count', () => {
    expect(summaryLine({ label: 'Build preparation · Reading download sizes · 65,536 of 131,072 entries · 50%', measurement: '65,536 of 131,072 entries · 50% of current stage' }))
      .toBe('Build preparation · Reading download sizes · 65,536 of 131,072 entries · 50% of current stage');
    expect(summaryLine({ label: 'Build preparation · Reading download sizes · 0 entries', measurement: '0 of 0 entries · no percentage for zero total' }))
      .toBe('Build preparation · Reading download sizes · 0 of 0 entries · no percentage for zero total');
    expect(summaryLine({ label: 'Items · Preparing ItemSparse prerequisite', measurement: '25 of 100 bytes · 25% of current stage' }))
      .toBe('Items · Preparing ItemSparse prerequisite · 25 of 100 bytes · 25% of current stage');
    expect(summaryLine({ label: 'Build preparation · Matching archive locations · 10 of 20 download objects · 50%', measurement: '12 of 20 download objects · 60% of current stage' }))
      .toBe('Build preparation · Matching archive locations · 10 of 20 download objects · 50% · 12 of 20 download objects · 60% of current stage');
    expect(summaryLine({ label: 'Build preparation · Matching archive locations · 10 of 20 download objects · 50%', measurement: '11 of 22 download objects · 50% of current stage' }))
      .toBe('Build preparation · Matching archive locations · 10 of 20 download objects · 50% · 11 of 22 download objects · 50% of current stage');
    expect(summaryLine({ label: 'Build preparation · Preparing compact download ledger', measurement: null })).toBe('Build preparation · Preparing compact download ledger');
    expect(summaryLine({ label: 'Published encoded content · all variants · Encoded retention pass underway', measurement: '3 objects · total unknown' }))
      .toBe('Published encoded content · all variants · Encoded retention pass underway · 3 objects · total unknown');
  });
});
