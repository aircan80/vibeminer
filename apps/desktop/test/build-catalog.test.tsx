import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BuildCatalog } from '../src/renderer/BuildCatalog';
import { App } from '../src/renderer/App';
import { SWITCHED_TEXT, catalogEmpty, catalogRows, lastChecked, OFFLINE_TEXT, othersLine, productLine, progressText, refreshNotice, regionText, splitProducts, summaryNote } from '../src/renderer/build-catalog-view';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { BuildCatalog as Catalog, BuildCatalogRefresh, EngineProgress } from '../../../contracts/generated/discovery';
import listExample from '../../../contracts/examples/build-catalog.json';
import refreshExample from '../../../contracts/examples/build-catalog-refresh.json';
import { catalog as discoveryCatalog } from './catalog-page';
import discoveryPage from '../../../contracts/examples/discovery-page.json';
import type { DiscoveryPage } from '../../../contracts/generated/discovery';
afterEach(cleanup);
const list = () => structuredClone(listExample) as Catalog;
const refreshed = () => structuredClone(refreshExample) as BuildCatalogRefresh;
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const failure = (code: string, message: string) => ({ ok: false as const, error: { code, message, retryable: true } });
const same = (t: string) => t;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
describe('build catalog view model', () => {
  it('lists builds in product order and words regions and product lines', () => {
    const rows = catalogRows(list());
    expect(rows.map(r => `${r.product.productId} ${r.build.versionsName}`)).toEqual(['wow 11.9.0.90002', 'wow 11.9.0.90001', 'wowt 11.9.5.90010', 'wowxptr 11.9.5.90011', 'wow_classic_era 1.9.9.90020', 'wow_example_new 1.99.0.90030']);
    expect(rows[3].product.label).toBe('PTR 2');
    expect(regionText(['us', 'eu', 'kr'])).toBe('US, EU, KR');
    expect(regionText([])).toBe('No region listed');
    const [wow, , , beta] = list().products;
    expect(productLine(wow)).toBe('2 builds');
    expect(productLine(beta)).toBe('No history yet');
    expect(productLine({ ...beta, lastObservedAt: '2026-10-31T09:00:00Z' })).toBe('No builds listed');
    expect(productLine({ ...wow, buildCount: 140 })).toBe('140 builds, newest 2 shown');
    // D476: a product with no versions document never gets history; it lists no builds.
    expect(productLine(beta, ['wow_beta'])).toBe('No builds listed');
    expect(productLine(beta, ['wowdev4'])).toBe('No history yet');
    expect(catalogEmpty(list())).toBe(false);
    expect(catalogEmpty({ ...list(), products: list().products.map(p => ({ ...p, builds: [], buildCount: 0 })) })).toBe(true);
    expect(lastChecked(list())).toBe('2026-10-31T09:00:00Z');
    expect(lastChecked({ ...list(), products: [] })).toBeNull();
    // D476: the summary first (0 of 1), then it counts as the first of products + 1.
    expect(progressText({ completed: 0, total: 1 })).toBe('Checking the version service (the product summary)');
    expect(progressText({ completed: 1, total: 16 })).toBe('Checking the version service (0 of 15 products)');
    expect(progressText({ completed: 4, total: 16 })).toBe('Checking the version service (3 of 15 products)');
    expect(progressText({ completed: 16, total: 16 })).toBe('Checked 15 of 15 products and the summary');
    expect(progressText(null)).toBe('Checking the version service…');
  });
  it('splits the reviewed products from the other WoW products and counts the others (D476)', () => {
    const { reviewed, others } = splitProducts(list());
    expect(reviewed).toHaveLength(14);
    expect(reviewed.every(p => p.reviewed)).toBe(true);
    expect(others.map(p => [p.productId, p.label])).toEqual([['wow_example_new', 'wow_example_new']]);
    expect(othersLine(others)).toBe('1 product, 1 build');
    expect(othersLine([...others, { ...others[0], productId: 'wowdev', buildCount: 0 }])).toBe('2 products, 1 build');
    expect(othersLine([{ ...others[0], buildCount: 3 }])).toBe('1 product, 3 builds');
    expect(splitProducts(null)).toEqual({ reviewed: [], others: [] });
  });
  it('keeps a summary note only for a missing or unreadable summary', () => {
    const at = '2026-10-31T09:00:00Z';
    expect(summaryNote({ observedAt: at, readable: true, newProducts: ['a'], withoutVersions: [] }, same)).toBeNull();
    expect(summaryNote({ observedAt: at, readable: false, newProducts: [], withoutVersions: [] }, same)).toBe(`The summary of ${at} can't be read, so other WoW products aren't listed.`);
    expect(summaryNote(null, same)).toBe('No product summary recorded yet. Refresh fetches it, and with it the other WoW products.');
  });
  it('tells offline from partial refreshes', () => {
    expect(refreshNotice({ ...refreshed(), failures: [] })).toBeNull();
    expect(refreshNotice(refreshed())).toEqual({ kind: 'partial', text: "1 product couldn't be checked. Their stored builds are kept.", items: ['Classic Era (wow_classic_era): CDN_OFFLINE'] });
    const value = refreshed(), all = value.catalog.products.map(p => ({ productId: p.productId, document: 'versions' as const, state: 'unavailable' as const, code: 'CDN_OFFLINE', message: null }));
    expect(refreshNotice({ ...value, failures: [...all, { productId: null, document: 'summary', state: 'unavailable', code: null, message: null }] })).toEqual({ kind: 'offline', text: OFFLINE_TEXT });
    // One product refused rather than unreachable: not offline.
    expect(refreshNotice({ ...value, failures: all.map((f, i) => i ? f : { ...f, state: 'refused' as const, code: 'HTTP_403' }) })?.kind).toBe('partial');
    expect(refreshNotice({ ...value, failures: [{ productId: null, document: 'summary', state: 'storeFailed', code: null, message: null }] })).toMatchObject({ kind: 'partial', items: ['Product summary: storeFailed'] });
  });
});
function bridgeMock(patch: Partial<DiscoveryBridge> = {}) {
  const listeners: ((p: EngineProgress) => void)[] = [];
  const bridge = {
    catalogList: vi.fn(async () => success(list())),
    catalogRefresh: vi.fn(async () => success({ ...refreshed(), failures: [] })),
    cancel: vi.fn(async () => success({ requested: true })),
    onProgress: vi.fn((listener: (p: EngineProgress) => void) => { listeners.push(listener); return () => {}; }),
    ...patch,
  } as unknown as DiscoveryBridge;
  return { bridge, progress: (p: Partial<EngineProgress>) => act(() => { for (const l of listeners) l({ requestId: '', sequence: 1, stage: 'Checking the version service', region: null, completed: 0, total: 14, unit: 'products', ...p } as EngineProgress); }) };
}
const view = (bridge: DiscoveryBridge, onOldList = () => {}) => render(<BuildCatalog bridge={bridge} active onOldList={onOldList}/>);
describe('build catalog screen', () => {
  it('groups builds by product with Current marked, products without history, and Open disabled with its reason', async () => {
    const { bridge } = bridgeMock();
    view(bridge);
    const listbox = await screen.findByRole('listbox', { name: 'Builds' });
    expect(within(listbox).getAllByRole('group').map(g => g.getAttribute('aria-labelledby'))).toHaveLength(14);
    expect(within(listbox).getByRole('group', { name: /PTR 2 wowxptr/ })).toBeInTheDocument();
    expect(document.getElementById('catalog-group-wow_beta')).toHaveTextContent('Beta wow_beta · No history yet');
    expect(within(listbox).getAllByRole('option')).toHaveLength(5);
    expect(within(listbox).getAllByRole('option')[0]).toHaveTextContent(/11\.9\.0\.90002Current.*US, EU, KR, TW/);
    // The summary note is gone (D476); its new products are the collapsed list below.
    expect(screen.queryByText(/New in the .* summary/)).toBeNull();
    expect(document.querySelector('.dv-catalog-summary')).toBeNull();
    // The reviewed product with only a cdn row in the summary lists no builds rather than no history.
    expect(document.getElementById('catalog-group-wow_classic_era_beta')).toHaveTextContent('Classic Era Beta wow_classic_era_beta · No builds listed');
    expect(screen.queryByRole('listbox', { name: 'Other WoW products, not reviewed' })).toBeNull();
    fireEvent.click(within(listbox).getAllByRole('option')[1]);
    const inspector = screen.getByRole('complementary');
    expect(within(inspector).getByRole('heading', { name: '11.9.0.90001' })).toBeInTheDocument();
    expect(within(inspector).getByText('22222222222222222222222222222222')).toBeInTheDocument();
    expect(within(inspector).getByText('US, EU, KR, TW, CN')).toBeInTheDocument();
    const open = within(inspector).getByRole('button', { name: 'Open build' });
    expect(open).toBeDisabled();
    expect(open).toHaveAccessibleDescription('Opening on the new storage comes next. The Old build list still opens builds.');
  });
  it('lists the other WoW products collapsed after the reviewed ones, with their own keys and the one selection (D476)', async () => {
    const { bridge } = bridgeMock();
    view(bridge);
    const main = await screen.findByRole('listbox', { name: 'Builds' });
    const toggle = screen.getByRole('button', { name: /Other WoW products \(not reviewed\)/ });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveTextContent('Other WoW products (not reviewed) · 1 product, 1 build');
    expect(screen.getByLabelText('5 builds shown')).toBeInTheDocument();
    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const others = screen.getByRole('listbox', { name: 'Other WoW products, not reviewed' });
    expect(toggle).toHaveAttribute('aria-controls', others.id);
    expect(within(others).getByRole('group', { name: /wow_example_new/ })).toHaveTextContent('wow_example_new · 1 build');
    expect(screen.getByLabelText('6 builds shown')).toBeInTheDocument();
    others.focus();
    fireEvent.keyDown(others, { key: 'Home' });
    const option = within(others).getByRole('option');
    expect(option).toHaveAttribute('aria-selected', 'true');
    expect(within(main).getAllByRole('option').every(o => o.getAttribute('aria-selected') === 'false')).toBe(true);
    const inspector = screen.getByRole('complementary');
    expect(within(inspector).getByRole('heading', { name: '1.99.0.90030' })).toBeInTheDocument();
    // The id once, said to be unreviewed, rather than "wow_example_new · wow_example_new" (UI review).
    expect(inspector.querySelector('.inspector-product')).toHaveTextContent(/^World of Warcraft · wow_example_new · not reviewed$/);
    expect(within(inspector).getByText('Product').nextElementSibling).toHaveTextContent(/^wow_example_new · Not reviewed$/);
    // Collapsing keeps the selection; the inspector still shows it and the toggle says where it is (usability review).
    fireEvent.click(toggle);
    expect(screen.queryByRole('listbox', { name: 'Other WoW products, not reviewed' })).toBeNull();
    expect(within(inspector).getByRole('heading', { name: '1.99.0.90030' })).toBeInTheDocument();
    expect(toggle).toHaveTextContent('selected build 1.99.0.90030 is in this list');
  });
  it('moves the selection with arrow keys and keeps it, focus included, through a refresh', async () => {
    const pending = deferred<BridgeReply<BuildCatalogRefresh>>();
    const { bridge, progress } = bridgeMock({ catalogRefresh: vi.fn(() => pending.promise) });
    view(bridge);
    const listbox = await screen.findByRole('listbox', { name: 'Builds' });
    listbox.focus();
    fireEvent.keyDown(listbox, { key: 'ArrowDown' });
    fireEvent.keyDown(listbox, { key: 'ArrowDown' });
    expect(within(listbox).getAllByRole('option')[1]).toHaveAttribute('aria-selected', 'true');
    fireEvent.keyDown(listbox, { key: 'End' });
    expect(within(listbox).getAllByRole('option')[4]).toHaveAttribute('aria-selected', 'true');
    fireEvent.keyDown(listbox, { key: 'Home' });
    expect(listbox).toHaveAttribute('aria-activedescendant', within(listbox).getAllByRole('option')[0].id);
    fireEvent.keyDown(window, { key: 'F5' });
    await waitFor(() => expect(bridge.catalogRefresh).toHaveBeenCalledTimes(1));
    const id = vi.mocked(bridge.catalogRefresh!).mock.calls[0][0];
    progress({ requestId: id, completed: 0, total: 1 });
    expect(screen.getByText('Checking the version service (the product summary)')).toBeInTheDocument();
    progress({ requestId: id, completed: 4, total: 16 });
    expect(screen.getByText('Checking the version service (3 of 15 products)')).toBeInTheDocument();
    progress({ requestId: 'someone-else', completed: 9, total: 16 });
    expect(screen.getByText('Checking the version service (3 of 15 products)')).toBeInTheDocument();
    listbox.focus();
    await act(async () => pending.resolve(success({ ...refreshed(), failures: [] })));
    await screen.findByText('Refresh complete. Checked 15 products and the summary.');
    expect(listbox).toHaveFocus();
    expect(within(listbox).getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true');
  });
  it('stops a refresh and reads the list again; offline and busy refreshes keep the stored list', async () => {
    const pending = deferred<BridgeReply<BuildCatalogRefresh>>();
    const { bridge } = bridgeMock({ catalogRefresh: vi.fn(() => pending.promise) });
    view(bridge);
    await screen.findByRole('listbox', { name: 'Builds' });
    fireEvent.click(screen.getByRole('button', { name: /Refresh builds/ }));
    const stop = await screen.findByRole('button', { name: /Stop refresh/ });
    fireEvent.click(stop);
    expect(bridge.cancel).toHaveBeenCalledWith(vi.mocked(bridge.catalogRefresh!).mock.calls[0][0]);
    await act(async () => pending.resolve(failure('CANCELED', 'Refresh stopped. Products checked before stopping keep their new results.')));
    await screen.findByText('Refresh stopped. Products checked before stopping keep their new results.');
    await waitFor(() => expect(bridge.catalogList).toHaveBeenCalledTimes(2));
    expect(screen.getByRole('button', { name: /Refresh builds/ })).toBe(stop);
    const value = refreshed();
    vi.mocked(bridge.catalogRefresh!).mockResolvedValueOnce(success({ ...value, failures: value.catalog.products.map(p => ({ productId: p.productId, document: 'versions', state: 'unavailable', code: 'CDN_OFFLINE', message: null })) }));
    fireEvent.click(stop);
    expect(await screen.findByText(OFFLINE_TEXT)).toBeInTheDocument();
    expect(screen.getAllByRole('option')).toHaveLength(5);
    vi.mocked(bridge.catalogRefresh!).mockResolvedValueOnce(failure('OBJECT_STORE_BUSY', 'busy'));
    fireEvent.click(stop);
    expect(await screen.findByText(/Another VibeMiner process, such as vm browse, is writing the build store\. Close it, then refresh\./)).toBeInTheDocument();
    expect(screen.getAllByRole('option')).toHaveLength(5);
  });
  it('drops a list reply that a refresh overtook', async () => {
    const first = deferred<BridgeReply<Catalog>>();
    const stale = list(); stale.products[0].builds = [stale.products[0].builds[1]]; stale.products[0].buildCount = 1;
    const { bridge } = bridgeMock({ catalogList: vi.fn(() => first.promise) });
    view(bridge);
    fireEvent.click(screen.getByRole('button', { name: /Refresh builds/ }));
    await screen.findByText('Refresh complete. Checked 15 products and the summary.');
    await act(async () => first.resolve(success(stale)));
    expect(screen.getAllByRole('option')).toHaveLength(5);
  });
  it('shows the read-only notice up front, the empty store and a failed read', async () => {
    const readOnly = bridgeMock({ catalogList: vi.fn(async () => success({ ...list(), access: 'readOnly' as const })) });
    view(readOnly.bridge);
    expect(await screen.findByText(/is writing the build store/)).toBeInTheDocument();
    cleanup();
    const empty = bridgeMock({ catalogList: vi.fn(async () => success({ ...list(), summary: null, products: list().products.map(p => ({ ...p, builds: [], buildCount: 0, lastObservedAt: null })) })) });
    view(empty.bridge);
    expect(await screen.findByRole('heading', { name: 'No builds stored yet' })).toBeInTheDocument();
    expect(screen.getByText('Refresh asks the version service.')).toBeInTheDocument();
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    cleanup();
    const broken = bridgeMock({ catalogList: vi.fn(async () => failure('BUILD_STORE_UNAVAILABLE', "The build store can't be opened (OBJECT_STORE_NEWER).")) });
    view(broken.bridge);
    expect(await screen.findByRole('alert')).toHaveTextContent("The build store can't be opened (OBJECT_STORE_NEWER). (BUILD_STORE_UNAVAILABLE)");
    expect(screen.getByRole('heading', { name: 'Build list not read' })).toBeInTheDocument();
  });
});
describe('Discovery slot', () => {
  const page = discoveryPage as DiscoveryPage;
  function appBridge(capabilities: string[]) {
    const unavailable = async () => failure('NOT_TESTED', 'Outside this test.');
    return {
      ...bridgeMock().bridge,
      capabilities: vi.fn(async () => success(capabilities)),
      products: vi.fn(async () => success({ products: [{ id: 'wow', label: 'Retail', description: 'x' }] })),
      readAll: vi.fn(async () => success(discoveryCatalog(page))), refreshAll: vi.fn(unavailable), read: vi.fn(unavailable), refresh: vi.fn(unavailable),
      itemsData: vi.fn(unavailable), itemsPrepare: vi.fn(unavailable), itemsPage: vi.fn(unavailable), itemInspect: vi.fn(unavailable),
      tableData: vi.fn(async () => success({ snapshot: null })), tablePrepare: vi.fn(unavailable), tableRows: vi.fn(unavailable), tablesCatalog: vi.fn(unavailable), tableDefinition: vi.fn(unavailable),
      download: vi.fn(unavailable), inventory: vi.fn(unavailable), inventoryFiles: vi.fn(unavailable), inventoryFile: vi.fn(unavailable), open: vi.fn(unavailable),
    } as unknown as DiscoveryBridge;
  }
  it('shows the new list with build-catalog-v1 and switches to the old list and back for the session', async () => {
    const bridge = appBridge(['build-catalog-v1']);
    render(<App bridge={bridge}/>);
    await screen.findByRole('group', { name: /PTR 2/ });
    expect(bridge.readAll).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Old build list' }));
    const back = await screen.findByRole('button', { name: 'New build list' });
    await waitFor(() => expect(bridge.readAll).toHaveBeenCalled());
    expect(back).toHaveFocus();
    fireEvent.click(back);
    expect(await screen.findByRole('button', { name: 'Old build list' })).toHaveFocus();
  });
  it('keeps the last refresh outcome across a switch, and says when the switch stopped a refresh', async () => {
    const bridge = appBridge(['build-catalog-v1']);
    render(<App bridge={bridge}/>);
    await screen.findByRole('group', { name: /PTR 2/ });
    fireEvent.click(screen.getAllByRole('option')[1]);
    fireEvent.click(screen.getByRole('button', { name: /Refresh builds/ }));
    await screen.findByText('Refresh complete. Checked 15 products and the summary.');
    fireEvent.click(screen.getByRole('button', { name: 'Old build list' }));
    fireEvent.click(await screen.findByRole('button', { name: 'New build list' }));
    expect(await screen.findByText('Refresh complete. Checked 15 products and the summary.')).toBeInTheDocument();
    expect(screen.getAllByRole('option')[1]).toHaveAttribute('aria-selected', 'true');
    vi.mocked(bridge.catalogRefresh!).mockImplementationOnce(() => new Promise(() => {}));
    fireEvent.click(screen.getByRole('button', { name: /Refresh builds/ }));
    await screen.findByRole('button', { name: /Stop refresh/ });
    fireEvent.click(screen.getByRole('button', { name: 'Old build list' }));
    expect(bridge.cancel).toHaveBeenCalledWith(vi.mocked(bridge.catalogRefresh!).mock.calls[1][0]);
    fireEvent.click(await screen.findByRole('button', { name: 'New build list' }));
    expect(await screen.findByText(SWITCHED_TEXT)).toBeInTheDocument();
  });
  it('keeps the old list without the capability, with no way to the new one', async () => {
    const bridge = appBridge([]);
    render(<App bridge={bridge}/>);
    await screen.findByRole('listbox', { name: 'Builds' });
    expect(bridge.catalogList).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'New build list' })).not.toBeInTheDocument();
  });
});
