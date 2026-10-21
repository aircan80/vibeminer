import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../src/renderer/App';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { DiscoveryCatalogPage, DiscoveryPage } from '../../../contracts/generated/discovery';
import fixture from '../../../contracts/examples/discovery-page.json';
import { catalog, statusOf, testProducts } from './catalog-page';
const original = fixture as DiscoveryPage;
const second = { ...original.builds[0], id: 'f'.repeat(64), buildConfig: 'c'.repeat(32) };
const page: DiscoveryPage = { ...original, builds: [original.builds[0], second] };
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const unavailable = async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Outside this discovery test.', retryable: false } });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
function bridgeMock(): DiscoveryBridge {
  return {
    itemsData: vi.fn(unavailable), itemsPrepare: vi.fn(unavailable), itemsPage: vi.fn(unavailable), itemInspect: vi.fn(unavailable),
    products: vi.fn(async () => success({ products: testProducts })),
    // The single-product methods stay in the bridge; discovery uses the cross-product ones.
    read: vi.fn(unavailable), refresh: vi.fn(unavailable),
    readAll: vi.fn(async params => success(catalog(page, { productId: params.productId ?? null, filter: params.filter ?? 'all', builds: params.filter === 'local' || params.productId === 'wowt' ? [] : page.builds }))),
    refreshAll: vi.fn(async () => success(catalog(page))),
    tableData: vi.fn(async () => success({ snapshot: null })), tablePrepare: vi.fn(unavailable), tableRows: vi.fn(unavailable), tablesCatalog: vi.fn(unavailable), tableDefinition: vi.fn(unavailable),
    download: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_PREPARED', message: 'Authored unavailable download ledger.', retryable: false } })),
    inventory: vi.fn(unavailable), inventoryFiles: vi.fn(unavailable), inventoryFile: vi.fn(unavailable), open: vi.fn(unavailable),
    cancel: vi.fn(async () => success({ requested: true })), onProgress: vi.fn(() => () => {})
  };
}
beforeEach(() => {
  localStorage.clear();
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); this.querySelector<HTMLButtonElement>('button')?.focus(); };
  HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); };
});
afterEach(cleanup);
async function ready(bridge = bridgeMock()) { render(<App bridge={bridge}/>); await screen.findByRole('option', { name: /Config aaaaaaaaaa/ }); return bridge; }
describe('exact build discovery workflow', () => {
  it('startup reads every product locally without refresh and keeps opening unavailable', async () => {
    const bridge = await ready(); expect(bridge.readAll).toHaveBeenCalledWith({ filter: 'all', limit: 50 }); expect(bridge.refreshAll).not.toHaveBeenCalled();
    expect(bridge.read).not.toHaveBeenCalled(); expect(bridge.refresh).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Open build' })).toBeDisabled(); expect(screen.getByText(/Select a build with a recorded download source/)).toBeVisible();
    expect(screen.queryByText('Locally prepared')).not.toBeInTheDocument();
  });
  it('distinguishes same versions by configuration and preserves exact selection and search focus', async () => {
    const bridge = await ready();
    fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ }));
    const input = screen.getByRole('searchbox'); input.focus(); fireEvent.change(input, { target: { value: '70448' } });
    expect(input).toHaveFocus(); expect(screen.getByText('c'.repeat(32))).toBeVisible();
    vi.mocked(bridge.refreshAll).mockResolvedValue(success(catalog({ ...page, builds: [...page.builds].reverse() })));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); await screen.findByText(/Discovery complete/);
    expect(screen.getByText('c'.repeat(32))).toBeVisible(); expect(screen.getByRole('option', { name: /Config cccccccccc/ })).toHaveAttribute('aria-selected', 'true');
  });
  it('distinguishes exact identities when version and build configuration match', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValue(success(catalog({ ...page, builds: [page.builds[0], { ...second, buildConfig: page.builds[0].buildConfig, cdnConfig: 'c'.repeat(32) }] })));
    render(<App bridge={bridge}/>);
    const row = await screen.findByRole('option', { name: /Identity ffffffffff/ });
    fireEvent.click(row); expect(row).toHaveAttribute('aria-selected', 'true'); fireEvent.click(screen.getByRole('button', { name: 'Source details' })); expect(within(screen.getByRole('dialog')).getByText('c'.repeat(32))).toBeVisible();
  });
  it('uses real Local semantics and explains hidden selection without substituting another build', async () => {
    await ready(); fireEvent.click(screen.getByRole('option', { name: /Config aaaaaaaaaa/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Prepared' }));
    await screen.findByText('No prepared builds'); expect(screen.getByText(/Saved discovery observations are available in All/)).toBeVisible();
    // The hidden selection stays selected and inspectable instead of another build being substituted.
    expect(screen.getByRole('heading', { name: page.builds[0].version })).toBeVisible(); expect(screen.getByRole('button', { name: 'Clear filters' })).toBeVisible();
  });
  it('shows stopping until target retirement, preserving saved rows and truthful terminal outcome', async () => {
    const bridge = await ready(); const target = deferred<BridgeReply<DiscoveryCatalogPage>>(); vi.mocked(bridge.refreshAll).mockReturnValue(target.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); fireEvent.click(screen.getByRole('button', { name: 'Cancel refresh' }));
    await waitFor(() => expect(bridge.cancel).toHaveBeenCalled()); expect(screen.getByRole('button', { name: 'Stopping discovery…' })).toHaveAttribute('aria-disabled', 'true'); expect(screen.getByRole('option', { name: /Config aaaaaaaaaa/ })).toBeVisible();
    const reads = vi.mocked(bridge.readAll).mock.calls.length;
    await act(async () => target.resolve({ ok: false, error: { code: 'CANCELED', message: 'Canceled.', retryable: true } }));
    expect(screen.getByText(/Discovery canceled. Products checked before stopping keep their new results/)).toBeVisible(); expect(screen.getByRole('button', { name: 'Refresh builds' })).toBeEnabled();
    // Products that finished before the stop committed, so the saved view is read again locally.
    await waitFor(() => expect(bridge.readAll).toHaveBeenCalledTimes(reads + 1)); expect(bridge.readAll).toHaveBeenLastCalledWith({ filter: 'all', limit: 50 });
  });
  it('shows a late refresh through the Product filter chosen while it ran', async () => {
    const bridge = await ready(); const old = deferred<BridgeReply<DiscoveryCatalogPage>>();
    vi.mocked(bridge.refreshAll).mockReturnValue(old.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); fireEvent.change(screen.getByRole('combobox', { name: 'Product' }), { target: { value: 'wowt' } });
    await waitFor(() => expect(bridge.readAll).toHaveBeenCalledWith({ productId: 'wowt', filter: 'all', limit: 50 }));
    await act(async () => old.resolve(success(catalog(page))));
    expect(bridge.readAll).toHaveBeenLastCalledWith({ productId: 'wowt', filter: 'all', limit: 50 });
    expect(screen.queryByRole('option', { name: /Config aaaaaaaaaa/ })).not.toBeInTheDocument(); expect(screen.getByRole('combobox', { name: 'Product' })).toHaveValue('wowt');
    expect(screen.getByRole('heading', { name: 'No saved builds for wowt' })).toBeVisible();
  });
  it('shows one build with its CDN configuration history newest first and the original receipts', async () => {
    const current = page.builds[0], older = current.cdnConfigHistory[0];
    const olderReceipt = { ...older.sourceReceipts[0], sha256: '9'.repeat(64), retrievedAt: '2026-10-17T19:54:00Z', rowOrdinal: 3 };
    const republished = { ...current, cdnConfig: 'e'.repeat(32), cdnConfigHistory: [{ ...older, cdnConfig: 'e'.repeat(32), firstSeenAt: '2026-10-18T13:00:00Z', lastSeenAt: '2026-10-18T13:00:00Z' }, { ...older, advertisedRegions: [], sourceReceipts: [olderReceipt] }], cdnConfigHistoryCount: 2 };
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValue(success(catalog({ ...page, builds: [republished, second] })));
    render(<App bridge={bridge}/>); fireEvent.click(await screen.findByRole('option', { name: /Config aaaaaaaaaa/ }));
    expect(screen.getByText('CDN configuration republished.')).toBeVisible(); expect(screen.getByText(/1 earlier CDN configuration is kept in Source details/)).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Source details' }));
    const dialog = screen.getByRole('dialog'); expect(within(dialog).getByRole('heading', { name: 'CDN configuration history' })).toBeVisible();
    const entries = within(dialog).getAllByRole('heading', { level: 4 }).map(heading => heading.textContent).filter(text => /^(Newest|Earlier) ·/.test(text || ''));
    expect(entries).toEqual([`Newest · ${'e'.repeat(10)}…eeee`, `Earlier · ${current.cdnConfig.slice(0, 10)}…${current.cdnConfig.slice(-4)}`]);
    // Each receipt uses the labelled fields of Original receipts, not one packed monospace line.
    const receipt = within(dialog).getByText('9'.repeat(64)).closest('section') as HTMLElement;
    expect(within(receipt).getByText('Requested URL')).toBeVisible(); expect(within(receipt).getByText('Retrieved')).toBeVisible();
    expect(within(receipt).getByText('SHA-256')).toBeVisible(); expect(within(receipt).getByText(/bytes · Row 3/)).toBeVisible();
    expect(within(receipt).getByText('Retrieved').nextElementSibling).not.toHaveClass('mono');
    expect(within(dialog).queryByText(/not shown here/)).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ })); expect(screen.queryByText('CDN configuration republished.')).not.toBeInTheDocument();
  });
  it('states CDN configurations omitted by the projection cap', async () => {
    const current = page.builds[0];
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValue(success(catalog({ ...page, builds: [{ ...current, cdnConfigHistoryCount: 18 }, second] })));
    render(<App bridge={bridge}/>); fireEvent.click(await screen.findByRole('option', { name: /Config aaaaaaaaaa/ }));
    expect(screen.getByText(/17 earlier CDN configurations are kept in Source details/)).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Source details' }));
    expect(within(screen.getByRole('dialog')).getByText(/17 older CDN configurations are not shown here\. 18 in total/)).toBeVisible();
  });
  it('does not order CDN configurations advertised in the same check', async () => {
    const current = page.builds[0], entry = current.cdnConfigHistory[0], eu = entry.sourceReceipts.map(r => ({ ...r, region: 'eu' }));
    // us advertises one CDN configuration and eu another in the same check; only key order separates them.
    const tied = { ...current, cdnConfig: '0'.repeat(32), cdnConfigHistory: [{ ...entry, cdnConfig: '0'.repeat(32), advertisedRegions: ['eu'], regions: ['eu'], sourceReceipts: eu }, entry] };
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValue(success(catalog({ ...page, builds: [tied, second] })));
    render(<App bridge={bridge}/>); fireEvent.click(await screen.findByRole('option', { name: /Config aaaaaaaaaa/ }));
    expect(screen.getByText('Several CDN configurations in one check.')).toBeVisible(); expect(screen.queryByText('CDN configuration republished.')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Source details' }));
    const dialog = screen.getByRole('dialog');
    const labels = within(dialog).getAllByRole('heading', { level: 4 }).map(h => h.textContent || '').filter(text => / · /.test(text) && /^(Newest|Also advertised|Earlier)/.test(text)).map(text => text.split(' · ')[0]);
    expect(labels).toEqual(['Also advertised', 'Also advertised']); expect(within(dialog).getByText(/their order is not significant/)).toBeVisible();
  });
  it('reports a refused saved catalog once, without saved-history claims or a delete action', async () => {
    const refusal = { ok: false as const, error: { code: 'CATALOG_RESET_REQUIRED', message: 'The saved catalog was made by an older VibeMiner and cannot be opened by this version. A fresh catalog is needed. The catalog folder is /authored/engine-v1. Close VibeMiner, move or rename that folder (for example to engine-v1.old), then start VibeMiner again. The old catalog was left unchanged.', retryable: false } };
    const bridge = bridgeMock(); vi.mocked(bridge.products).mockResolvedValue(refusal); vi.mocked(bridge.readAll).mockResolvedValue(refusal);
    render(<App bridge={bridge}/>);
    const alert = await screen.findByRole('alert'); await waitFor(() => expect(bridge.readAll).toHaveBeenCalled());
    expect(screen.getAllByRole('alert')).toHaveLength(1);
    expect(within(alert).getByText('Saved catalog not opened')).toBeVisible(); expect(alert).toHaveTextContent(/made by an older VibeMiner.*A fresh catalog is needed.*\/authored\/engine-v1.*move or rename that folder.*engine-v1\.old/);
    expect(within(alert).getByText('(CATALOG_RESET_REQUIRED)').tagName).toBe('CODE');
    // The whole notice grows to fit and can be selected; the folder is its own selectable element.
    expect(alert).toHaveClass('refusal'); expect(alert.firstElementChild).toHaveClass('dv-selectable');
    const folder = alert.querySelector('[data-catalog-path]'); expect(folder).toHaveTextContent(/^\/authored\/engine-v1$/); expect(folder).toHaveClass('dv-selectable');
    // Nothing contradicts the notice: no retry or refresh, no saved-history or empty-history wording, no filters or inspector.
    expect(screen.queryByRole('button', { name: /Retry discovery|Refresh builds/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/Saved observations remain available|No saved observations|Previous observations are still available|No saved builds/)).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('The saved catalog was not opened.');
    expect(screen.getByRole('searchbox', { name: 'Filter version or build number' })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: 'Product' })).toBeDisabled();
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    fireEvent.keyDown(window, { key: 'F5' }); expect(bridge.refreshAll).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: /delete|erase|reset|remove/i })).not.toBeInTheDocument();
  });
  it('keeps source details exact and returns keyboard focus when the modal closes', async () => {
    await ready(); fireEvent.click(screen.getByRole('option', { name: /Config aaaaaaaaaa/ }));
    const source = screen.getByRole('button', { name: 'Source details' }); source.focus(); fireEvent.click(source);
    const dialog = screen.getByRole('dialog'); expect(within(dialog).getAllByText('https://metadata.invalid/v2/products/wow/versions')).toHaveLength(2); // Original receipts and the CDN configuration history entry
    expect(within(dialog).getByText(/Checked regions for/)).toHaveTextContent(`Checked regions for wow: ${original.checkedRegions.map(r => r.toUpperCase()).join(', ')}`);
    expect(within(dialog).getByText(/The Download source control selects one eligible saved regional source/)).toBeVisible(); expect(within(dialog).queryByText(/No acquisition route is selected/)).not.toBeInTheDocument(); expect(within(dialog).getByRole('button', { name: 'Close' })).toHaveFocus();
    fireEvent(dialog, new Event('cancel', { cancelable: true })); expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); expect(source).toHaveFocus();
  });
  it('keeps saved advertisement evidence during offline refresh and presents failures separately', async () => {
    const bridge = await ready(); vi.mocked(bridge.refreshAll).mockResolvedValue(success(catalog(page, {}, [{ ...statusOf(page), connection: 'offline', errors: [{ region: 'us', code: 'OFFLINE', message: 'Official metadata unavailable.' }] }])));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); await screen.findByText('Refresh unavailable');
    expect(within(screen.getByRole('listbox')).getAllByText('Current', { exact: true })).toHaveLength(2); for (const retry of screen.getAllByRole('button', { name: 'Retry discovery' })) expect(retry).toBeEnabled(); expect(screen.getAllByRole('button', { name: 'Retry discovery' })).toHaveLength(2); expect(screen.queryByText(/No builds available/)).not.toBeInTheDocument();
    expect(screen.getByText(/wow US: Official metadata unavailable\./)).toBeVisible();
    expect(screen.getByRole('status')).toHaveTextContent(/^Could not refresh\. Earlier saved observations are kept for wow\.$/);
  });
  it('loads the bounded next page through its exact cursor without replacing selected identity', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValueOnce(success(catalog({ ...original, nextCursor: 'snapshot-cursor' }))).mockResolvedValueOnce(success(catalog({ ...original, builds: [second] }))); await ready(bridge);
    fireEvent.click(screen.getByRole('option', { name: /Config aaaaaaaaaa/ })); fireEvent.click(screen.getByRole('button', { name: 'Load more builds' }));
    await screen.findByRole('option', { name: /Config cccccccccc/ }); expect(bridge.readAll).toHaveBeenLastCalledWith({ filter: 'all', limit: 50, cursor: 'snapshot-cursor' }); expect(screen.getByText('a'.repeat(32))).toBeVisible();
  });
  it('preserves a loaded later-page exact selection across refresh with bounded rereads', async () => {
    const bridge = bridgeMock();
    vi.mocked(bridge.readAll).mockImplementation(async params => success(catalog(params.cursor ? { ...original, builds: [second] } : { ...original, nextCursor: 'old-page-2' })));
    await ready(bridge); fireEvent.click(screen.getByRole('button', { name: 'Load more builds' }));
    const laterRow = await screen.findByRole('option', { name: /Config cccccccccc/ }); fireEvent.click(laterRow);
    vi.mocked(bridge.refreshAll).mockResolvedValue(success(catalog({ ...original, nextCursor: 'refreshed-page-2' })));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); await screen.findByText(/Discovery complete/);
    expect(bridge.readAll).toHaveBeenLastCalledWith({ filter: 'all', limit: 50, cursor: 'refreshed-page-2' });
    expect(screen.getByRole('option', { name: /Config cccccccccc/ })).toHaveAttribute('aria-selected', 'true'); expect(screen.getByText('c'.repeat(32))).toBeVisible();
  });
  it('explains when a previously selected exact identity is not returned after refresh', async () => {
    const bridge = await ready(); fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ }));
    vi.mocked(bridge.refreshAll).mockResolvedValue(success(catalog(original))); fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/no longer returned by this view after refresh/);
    expect(screen.getByRole('option', { name: /Config aaaaaaaaaa/ })).toHaveAttribute('aria-selected', 'false');
  });
  it('presents offline empty history as a failed check with retry', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValue(success(catalog({ ...original, builds: [], hasSavedHistory: false, connection: 'offline', errors: [{ region: null, code: 'OFFLINE', message: 'Metadata unavailable.' }] })));
    render(<App bridge={bridge}/>); await screen.findByRole('heading', { name: 'Could not check any product' });
    expect(screen.getByText(/no saved observations are available/)).toBeVisible(); expect(screen.getAllByRole('button', { name: 'Retry discovery' })).toHaveLength(2); expect(screen.getByText(/No saved observations are available yet/)).toBeVisible(); expect(screen.queryByText('No saved builds yet')).not.toBeInTheDocument();
  });
  it('keeps keyboard focus on the stable cancellation action through stopping and retirement', async () => {
    const bridge = await ready(); const target = deferred<BridgeReply<DiscoveryCatalogPage>>(); vi.mocked(bridge.refreshAll).mockReturnValue(target.promise);
    const action = screen.getByRole('button', { name: 'Refresh builds' }); action.focus(); fireEvent.click(action); fireEvent.click(screen.getByRole('button', { name: 'Cancel refresh' }));
    expect(screen.getByRole('button', { name: 'Stopping discovery…' })).toHaveFocus();
    await act(async () => target.resolve({ ok: false, error: { code: 'CANCELED', message: 'Canceled.', retryable: true } }));
    expect(screen.getByRole('button', { name: 'Refresh builds' })).toHaveFocus();
    await waitFor(() => expect(bridge.readAll).toHaveBeenCalledTimes(2)); expect(screen.getByRole('button', { name: 'Refresh builds' })).toHaveFocus();
  });
  it('labels the display cap honestly without suggesting client search fetches older records', async () => {
    const bridge = bridgeMock(); let offset = 0;
    vi.mocked(bridge.readAll).mockImplementation(async () => {
      const builds = Array.from({ length: 50 }, (_, i) => ({ ...original.builds[0], id: (offset + i).toString(16).padStart(64, '0') })); offset += 50;
      return success(catalog({ ...original, builds, nextCursor: `page-${offset}` }));
    });
    render(<App bridge={bridge}/>); await screen.findByRole('button', { name: 'Load more builds' });
    for (let i = 0; i < 9; i++) {
      const previousCalls = vi.mocked(bridge.readAll).mock.calls.length;
      fireEvent.click(screen.getByRole('button', { name: 'Load more builds' }));
      await waitFor(() => expect(bridge.readAll).toHaveBeenCalledTimes(previousCalls + 1));
      await waitFor(() => expect(document.querySelectorAll('.build-row')).toHaveLength((i + 2) * 50));
    }
    expect(screen.getByRole('button', { name: '500-build display limit reached' })).toBeDisabled(); expect(screen.getByText(/searching beyond this display window is not available/)).toBeVisible();
    expect(screen.queryByText(/narrow the filter/)).not.toBeInTheDocument();
  }, 15000);
  it('describes missing route evidence neutrally and explains full-scope retry', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValue(success(catalog({ ...page, connection: 'partial', errors: [{ region: 'sg', code: 'ROUTE_MISSING', message: 'Advertisement has no matching route.' }] })));
    await ready(bridge); expect(screen.getByText('Refresh incomplete')).toBeVisible(); expect(screen.getByText('Refresh incomplete').closest('.dv-notice')).not.toHaveClass('error'); // regional route problems only expect(screen.getByText(/Retry discovery checks every product again/)).toBeVisible(); expect(within(screen.getByRole('listbox')).getAllByText('Current', { exact: true })).toHaveLength(2);
    expect(screen.getByText(/wow SG: Advertisement has no matching route\./)).toBeVisible();
  });

  it('bounds refresh selection recovery at 500 identities and explains an outside-window selection', async () => {
    const bridge = await ready(); fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ }));
    let offset = 0;
    const nextPage = () => {
      const builds = Array.from({ length: 50 }, (_, i) => ({ ...original.builds[0], id: (offset + i).toString(16).padStart(64, '0') })); offset += 50;
      return catalog({ ...original, builds, nextCursor: `new-${offset}` });
    };
    vi.mocked(bridge.refreshAll).mockResolvedValue(success(nextPage())); vi.mocked(bridge.readAll).mockImplementation(async () => success(nextPage()));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/selected exact build is outside the refreshed 500-build display window/);
    expect(bridge.readAll).toHaveBeenCalledTimes(10); expect(document.querySelectorAll('.build-row')).toHaveLength(500);
    expect(screen.getByRole('button', { name: '500-build display limit reached' })).toBeDisabled();
  });

  it('restores a selection saved by the per-product version on a later saved page without remote refresh', async () => {
    localStorage.setItem('vibeminer.selection.wow', second.id);
    const bridge = bridgeMock(); vi.mocked(bridge.readAll).mockResolvedValueOnce(success(catalog({ ...original, nextCursor: 'saved-page-2' }))).mockResolvedValueOnce(success(catalog({ ...original, builds: [second] })));
    render(<App bridge={bridge}/>);
    const restored = await screen.findByRole('option', { name: /Config cccccccccc/ }); expect(restored).toHaveAttribute('aria-selected', 'true');
    expect(bridge.readAll).toHaveBeenNthCalledWith(2, { filter: 'all', limit: 50, cursor: 'saved-page-2' }); expect(bridge.refreshAll).not.toHaveBeenCalled(); expect(screen.getByText('c'.repeat(32))).toBeVisible();
  });
  it('saves a new selection once for every product and restores it', async () => {
    await ready(); fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ }));
    expect(localStorage.getItem('vibeminer.discovery.selection')).toBe(second.id);
    cleanup(); await ready();
    expect(screen.getByRole('option', { name: /Config cccccccccc/ })).toHaveAttribute('aria-selected', 'true');
  });
});
