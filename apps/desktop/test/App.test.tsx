import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../src/renderer/App';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { DiscoveryPage } from '../../../contracts/generated/discovery';
import fixture from '../../../contracts/examples/discovery-page.json';
const original = fixture as DiscoveryPage;
const second = { ...original.builds[0], id: 'f'.repeat(64), buildConfig: 'c'.repeat(32) };
const page: DiscoveryPage = { ...original, builds: [original.builds[0], second] };
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
function bridgeMock(): DiscoveryBridge {
  return {
    itemsData: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    itemsPrepare: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    itemsPage: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    itemInspect: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    products: vi.fn(async () => success({ products: [{ id: 'wow', label: 'Retail', description: 'World of Warcraft' }, { id: 'wowt', label: 'PTR', description: 'Public test' }] })),
    read: vi.fn(async params => success({ ...page, productId: params.productId, builds: params.filter === 'local' ? [] : params.productId === 'wow' ? page.builds : [] })),
    tableData: vi.fn(async () => success({snapshot:null})),
    tablePrepare: vi.fn(async () => ({ok:false as const,error:{code:'NOT_TESTED',message:'Table load outside this test.',retryable:false}})),
    tableRows: vi.fn(async () => ({ok:false as const,error:{code:'NOT_TESTED',message:'Rows outside this test.',retryable:false}})),
    tablesCatalog: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Definitions outside this test.', retryable: false } })),
    tableDefinition: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Definitions outside this test.', retryable: false } })),
    download: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_PREPARED', message: 'Authored unavailable download ledger.', retryable: false } })),
    inventory: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Inventory is outside this discovery test.', retryable: false } })),
    open: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Open is outside this discovery test.', retryable: false } })),
    refresh: vi.fn(async () => success(page)), cancel: vi.fn(async () => success({ requested: true })), onProgress: vi.fn(() => () => {})
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
  it('startup reads history locally without refresh and keeps opening unavailable', async () => {
    const bridge = await ready(); expect(bridge.read).toHaveBeenCalledWith({ productId: 'wow', filter: 'all', limit: 50 }); expect(bridge.refresh).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Open build' })).toBeDisabled(); expect(screen.getByText(/Select a build with a recorded download source/)).toBeVisible();
    expect(screen.queryByText('Locally prepared')).not.toBeInTheDocument();
  });
  it('distinguishes same versions by configuration and preserves exact selection and search focus', async () => {
    const bridge = await ready();
    fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ }));
    const input = screen.getByRole('searchbox'); input.focus(); fireEvent.change(input, { target: { value: '70448' } });
    expect(input).toHaveFocus(); expect(screen.getByText('c'.repeat(32))).toBeVisible();
    vi.mocked(bridge.refresh).mockResolvedValue(success({ ...page, builds: [...page.builds].reverse() }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); await screen.findByText(/Discovery complete/);
    expect(screen.getByText('c'.repeat(32))).toBeVisible(); expect(screen.getByRole('option', { name: /Config cccccccccc/ })).toHaveAttribute('aria-selected', 'true');
  });
  it('distinguishes exact identities when version and build configuration match', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.read).mockResolvedValue(success({ ...page, builds: [page.builds[0], { ...second, buildConfig: page.builds[0].buildConfig, cdnConfig: 'c'.repeat(32) }] }));
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
    const bridge = await ready(); const target = deferred<BridgeReply<DiscoveryPage>>(); vi.mocked(bridge.refresh).mockReturnValue(target.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); fireEvent.click(screen.getByRole('button', { name: 'Cancel refresh' }));
    await waitFor(() => expect(bridge.cancel).toHaveBeenCalled()); expect(screen.getByRole('button', { name: 'Stopping discovery…' })).toHaveAttribute('aria-disabled', 'true'); expect(screen.getByRole('option', { name: /Config aaaaaaaaaa/ })).toBeVisible();
    await act(async () => target.resolve({ ok: false, error: { code: 'CANCELED', message: 'Canceled.', retryable: true } }));
    expect(screen.getByText(/Discovery canceled. Previous observations/)).toBeVisible(); expect(screen.getByRole('button', { name: 'Refresh builds' })).toBeEnabled();
  });
  it('fences a late product response after deliberate product choice', async () => {
    const bridge = await ready(); const old = deferred<BridgeReply<DiscoveryPage>>();
    vi.mocked(bridge.refresh).mockImplementation(productId => productId === 'wow' ? old.promise : Promise.resolve(success({ ...page, productId: 'wowt', builds: [] })));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); fireEvent.change(screen.getByRole('combobox', { name: 'Product' }), { target: { value: 'wowt' } });
    await waitFor(() => expect(bridge.refresh).toHaveBeenCalledWith('wowt', expect.any(String)));
    await act(async () => old.resolve(success(page)));
    expect(screen.queryByRole('option', { name: /Config aaaaaaaaaa/ })).not.toBeInTheDocument(); expect(screen.getByRole('combobox', { name: 'Product' })).toHaveValue('wowt');
  });
  it('keeps source details exact and returns keyboard focus when the modal closes', async () => {
    await ready(); fireEvent.click(screen.getByRole('option', { name: /Config aaaaaaaaaa/ }));
    const source = screen.getByRole('button', { name: 'Source details' }); source.focus(); fireEvent.click(source);
    const dialog = screen.getByRole('dialog'); expect(within(dialog).getByText('https://metadata.invalid/v2/products/wow/versions')).toBeVisible(); expect(within(dialog).getByText(/The Download source control selects one eligible saved regional source/)).toBeVisible(); expect(within(dialog).queryByText(/No acquisition route is selected/)).not.toBeInTheDocument(); expect(within(dialog).getByRole('button', { name: 'Close' })).toHaveFocus();
    fireEvent(dialog, new Event('cancel', { cancelable: true })); expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); expect(source).toHaveFocus();
  });
  it('keeps saved advertisement evidence during offline refresh and presents failures separately', async () => {
    const bridge = await ready(); vi.mocked(bridge.refresh).mockResolvedValue(success({ ...page, connection: 'offline', errors: [{ region: 'us', code: 'OFFLINE', message: 'Official metadata unavailable.' }] }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); await screen.findByText('Refresh unavailable');
    expect(within(screen.getByRole('listbox')).getAllByText('Advertised', { exact: true })).toHaveLength(2); for (const retry of screen.getAllByRole('button', { name: 'Retry discovery' })) expect(retry).toBeEnabled(); expect(screen.getAllByRole('button', { name: 'Retry discovery' })).toHaveLength(2); expect(screen.queryByText(/No builds available/)).not.toBeInTheDocument();
  });
  it('loads the bounded next page through its exact cursor without replacing selected identity', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.read).mockResolvedValueOnce(success({ ...original, nextCursor: 'snapshot-cursor' })).mockResolvedValueOnce(success({ ...original, builds: [second] })); await ready(bridge);
    fireEvent.click(screen.getByRole('option', { name: /Config aaaaaaaaaa/ })); fireEvent.click(screen.getByRole('button', { name: 'Load more builds' }));
    await screen.findByRole('option', { name: /Config cccccccccc/ }); expect(bridge.read).toHaveBeenLastCalledWith({ productId: 'wow', filter: 'all', limit: 50, cursor: 'snapshot-cursor' }); expect(screen.getByText('a'.repeat(32))).toBeVisible();
  });
  it('preserves a loaded later-page exact selection across refresh with bounded rereads', async () => {
    const bridge = bridgeMock();
    vi.mocked(bridge.read).mockImplementation(async params => success(params.cursor ? { ...original, builds: [second] } : { ...original, nextCursor: 'old-page-2' }));
    await ready(bridge); fireEvent.click(screen.getByRole('button', { name: 'Load more builds' }));
    const laterRow = await screen.findByRole('option', { name: /Config cccccccccc/ }); fireEvent.click(laterRow);
    vi.mocked(bridge.refresh).mockResolvedValue(success({ ...original, nextCursor: 'refreshed-page-2' }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' })); await screen.findByText(/Discovery complete/);
    expect(bridge.read).toHaveBeenLastCalledWith({ productId: 'wow', filter: 'all', limit: 50, cursor: 'refreshed-page-2' });
    expect(screen.getByRole('option', { name: /Config cccccccccc/ })).toHaveAttribute('aria-selected', 'true'); expect(screen.getByText('c'.repeat(32))).toBeVisible();
  });
  it('explains when a previously selected exact identity is not returned after refresh', async () => {
    const bridge = await ready(); fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ }));
    vi.mocked(bridge.refresh).mockResolvedValue(success(original)); fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/no longer returned by this view after refresh/);
    expect(screen.getByRole('option', { name: /Config aaaaaaaaaa/ })).toHaveAttribute('aria-selected', 'false');
  });
  it('presents offline empty history as a failed check with retry', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.read).mockResolvedValue(success({ ...original, builds: [], hasSavedHistory: false, connection: 'offline', errors: [{ region: null, code: 'OFFLINE', message: 'Metadata unavailable.' }] }));
    render(<App bridge={bridge}/>); await screen.findByRole('heading', { name: 'Could not check wow' });
    expect(screen.getByText(/no saved observations are available/)).toBeVisible(); expect(screen.getAllByRole('button', { name: 'Retry discovery' })).toHaveLength(2); expect(screen.getByText(/No saved observations are available for this product/)).toBeVisible(); expect(screen.queryByText('Your research starts with a build')).not.toBeInTheDocument();
  });
  it('keeps keyboard focus on the stable cancellation action through stopping and retirement', async () => {
    const bridge = await ready(); const target = deferred<BridgeReply<DiscoveryPage>>(); vi.mocked(bridge.refresh).mockReturnValue(target.promise);
    const action = screen.getByRole('button', { name: 'Refresh builds' }); action.focus(); fireEvent.click(action); fireEvent.click(screen.getByRole('button', { name: 'Cancel refresh' }));
    expect(screen.getByRole('button', { name: 'Stopping discovery…' })).toHaveFocus();
    await act(async () => target.resolve({ ok: false, error: { code: 'CANCELED', message: 'Canceled.', retryable: true } }));
    expect(screen.getByRole('button', { name: 'Refresh builds' })).toHaveFocus();
  });
  it('labels the display cap honestly without suggesting client search fetches older records', async () => {
    const bridge = bridgeMock(); let offset = 0;
    vi.mocked(bridge.read).mockImplementation(async () => {
      const builds = Array.from({ length: 50 }, (_, i) => ({ ...original.builds[0], id: (offset + i).toString(16).padStart(64, '0') })); offset += 50;
      return success({ ...original, builds, nextCursor: `page-${offset}` });
    });
    render(<App bridge={bridge}/>); await screen.findByRole('button', { name: 'Load more builds' });
    for (let i = 0; i < 9; i++) {
      const previousCalls = vi.mocked(bridge.read).mock.calls.length;
      fireEvent.click(screen.getByRole('button', { name: 'Load more builds' }));
      await waitFor(() => expect(bridge.read).toHaveBeenCalledTimes(previousCalls + 1));
      await waitFor(() => expect(document.querySelectorAll('.build-row')).toHaveLength((i + 2) * 50));
    }
    expect(screen.getByRole('button', { name: '500-build display limit reached' })).toBeDisabled(); expect(screen.getByText(/searching beyond this display window is not available/)).toBeVisible();
    expect(screen.queryByText(/narrow the filter/)).not.toBeInTheDocument();
  }, 15000);
  it('describes missing route evidence neutrally and explains full-scope retry', async () => {
    const bridge = bridgeMock(); vi.mocked(bridge.read).mockResolvedValue(success({ ...page, connection: 'partial', errors: [{ region: 'sg', code: 'ROUTE_MISSING', message: 'Advertisement has no matching route.' }] }));
    await ready(bridge); expect(screen.getByText('Some discovery evidence is incomplete')).toBeVisible(); expect(screen.getByText(/Retry discovery checks the complete product scope again/)).toBeVisible(); expect(within(screen.getByRole('listbox')).getAllByText('Advertised', { exact: true })).toHaveLength(2);
  });

  it('bounds refresh selection recovery at 500 identities and explains an outside-window selection', async () => {
    const bridge = await ready(); fireEvent.click(screen.getByRole('option', { name: /Config cccccccccc/ }));
    let offset = 0;
    const nextPage = () => {
      const builds = Array.from({ length: 50 }, (_, i) => ({ ...original.builds[0], id: (offset + i).toString(16).padStart(64, '0') })); offset += 50;
      return { ...original, builds, nextCursor: `new-${offset}` };
    };
    vi.mocked(bridge.refresh).mockResolvedValue(success(nextPage())); vi.mocked(bridge.read).mockImplementation(async () => success(nextPage()));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/selected exact build is outside the refreshed 500-build display window/);
    expect(bridge.read).toHaveBeenCalledTimes(10); expect(document.querySelectorAll('.build-row')).toHaveLength(500);
    expect(screen.getByRole('button', { name: '500-build display limit reached' })).toBeDisabled();
  });

  it('restores a persisted exact selection on a later saved page without remote refresh', async () => {
    localStorage.setItem('vibeminer.selection.wow', second.id);
    const bridge = bridgeMock(); vi.mocked(bridge.read).mockResolvedValueOnce(success({ ...original, nextCursor: 'saved-page-2' })).mockResolvedValueOnce(success({ ...original, builds: [second] }));
    render(<App bridge={bridge}/>);
    const restored = await screen.findByRole('option', { name: /Config cccccccccc/ }); expect(restored).toHaveAttribute('aria-selected', 'true');
    expect(bridge.read).toHaveBeenNthCalledWith(2, { productId: 'wow', filter: 'all', limit: 50, cursor: 'saved-page-2' }); expect(bridge.refresh).not.toHaveBeenCalled(); expect(screen.getByText('c'.repeat(32))).toBeVisible();
  });

});
