import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Build, DiscoveryPage, EngineProgress, FileCatalogStatus, WorkspaceSnapshot, InventoryPage, InventoryFilesPage, InventoryFileDetail, DownloadPage, TableDefinitionSnapshot, TableDataSnapshot } from '../../../contracts/generated/discovery';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import { App } from '../src/renderer/App';
import { failedShort } from '../src/renderer/DownloadObjects';
import pageFixture from '../../../contracts/examples/discovery-page.json';
import workspaceFixture from '../../../contracts/examples/inventory-workspace.json';
import definitionsFixture from '../../../tests/desktop/fixtures/table-definitions.json';
import downloadFixture from '../../../tests/desktop/fixtures/download-workspace.json';
import configurationFixture from '../../../contracts/examples/workspace-snapshot.json';
import inventoryPageFixture from '../../../contracts/examples/inventory-page.json';
import filesFixture from '../../../contracts/examples/inventory-files-page.json';
import detailFixture from '../../../contracts/examples/inventory-file-detail.json';
import { catalog } from './catalog-page';
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const failure = (code = 'CANCELED', retryable = true): BridgeReply<WorkspaceSnapshot> => ({ ok: false, error: { code, message: `Authored ${code} outcome.`, retryable } });
const base = pageFixture.builds[0] as Build;
function routed(build = base, region = 'us'): Build {
  return { ...build, sourceReceipts: [
    { ...base.sourceReceipts[0], region, role: 'versions', sha256: '1'.repeat(64) },
    { ...base.sourceReceipts[0], region, role: 'cdns', sha256: '2'.repeat(64), url: 'https://metadata.invalid/cdns' }
  ] };
}
function snapshot(build = routed(), region = 'us'): WorkspaceSnapshot {
  return { ...workspaceFixture, build, sourceReference: { ...workspaceFixture.sourceReference, region, versionsSha256: build.sourceReceipts.find(r => r.role === 'versions' && r.region === region)!.sha256, cdnsSha256: build.sourceReceipts.find(r => r.role === 'cdns' && r.region === region)!.sha256 }, configurations: [
    { ...workspaceFixture.configurations[0], role: 'buildConfig', key: build.buildConfig },
    { ...workspaceFixture.configurations[1], role: 'cdnConfig', key: build.cdnConfig }
  ] } as WorkspaceSnapshot;
}
const context = { productId: 'wow', buildIdentity: base.id, sourceRegion: 'us', generation: '1', limit: 50 };
// The authored Files page for this exact build, unfiltered (exact total, no cursor) unless a test patches it.
const filesPage = (patch: Partial<InventoryFilesPage> = {}): InventoryFilesPage => ({ ...(filesFixture as InventoryFilesPage), buildIdentity: base.id, totalCount: '2', nextCursor: null, ...patch });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
function setup(builds: Build[] = [routed()], extra: Partial<DiscoveryBridge> = {}) {
  const progress = new Set<(p: EngineProgress) => void>();
  const bridge: DiscoveryBridge = {
    itemsData: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    itemsPrepare: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    itemsPage: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    itemInspect: vi.fn(async()=>({ok:false as const,error:{code:'NOT_TESTED',message:'Items outside this test.',retryable:false}})),
    products: vi.fn(async () => success({ products: [{ id: 'wow', label: 'Retail', description: 'World of Warcraft' }, { id: 'wowt', label: 'PTR', description: 'Public test' }] })),
    read: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Discovery reads every product.', retryable: false } })),
    refresh: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Discovery refreshes every product.', retryable: false } })),
    readAll: vi.fn(async params => success(catalog({ ...pageFixture, builds: !params.productId || params.productId === 'wow' ? builds : [] } as DiscoveryPage, { productId: params.productId ?? null }))),
    refreshAll: vi.fn(async () => success(catalog({ ...pageFixture, builds: [] } as DiscoveryPage))),
    open: vi.fn(async params => success(snapshot(builds.find(build => build.id === params.buildIdentity)!, params.sourceRegion))),
    tableData: vi.fn(async () => success({snapshot:null})),
    tablePrepare: vi.fn(async () => ({ok:false as const,error:{code:'NOT_TESTED',message:'Table load outside this test.',retryable:false}})),
    tableRows: vi.fn(async () => ({ok:false as const,error:{code:'NOT_TESTED',message:'Rows outside this test.',retryable:false}})),
    tablesCatalog: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Definitions outside this test.', retryable: false } })),
    tableDefinition: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Definitions outside this test.', retryable: false } })),
    download: vi.fn(async () => ({ ok: false as const, error: { code: 'NOT_PREPARED', message: 'Authored unavailable download ledger.', retryable: false } })),
    inventory: vi.fn(async params => success({ ...inventoryPageFixture, productId: params.productId, buildIdentity: params.buildIdentity, sourceRegion: params.sourceRegion, generation: params.generation } as InventoryPage)),
    inventoryFiles: vi.fn(async () => success(filesPage())),
    inventoryFile: vi.fn(async params => success({ ...detailFixture, buildIdentity: params.buildIdentity, file: params.file } as InventoryFileDetail)),
    cancel: vi.fn(async () => success({ requested: true })),
    onProgress: vi.fn(listener => { progress.add(listener); return () => { progress.delete(listener); }; }),
    ...extra
  };
  render(<App bridge={bridge}/>);
  return { bridge, emit: (value: EngineProgress) => { for (const listener of progress) listener(value); } };
}
async function select(pattern = /Identity dddddddddd/) { const row = await screen.findByRole('option', { name: pattern }); fireEvent.click(row); return row; }
async function begin() { await select(); const button = screen.getByRole('button', { name: 'Open build' }); button.focus(); fireEvent.click(button); return button; }
beforeEach(() => {
  localStorage.clear();
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  Element.prototype.scrollIntoView = vi.fn();
  vi.spyOn(HTMLElement.prototype,'getBoundingClientRect').mockReturnValue(new DOMRect(0,0,100,30));
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); this.querySelector<HTMLButtonElement>('button')?.focus(); };
  HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); };
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.useRealTimers(); });
describe('verified inventory opening and preserved discovery', () => {
  it('keeps the open build on its CDN configuration and shows a newer advertised one after refresh', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    const opened = routed(), newer = 'e'.repeat(32);
    vi.mocked(bridge.refreshAll).mockResolvedValue(success(catalog({ ...pageFixture, builds: [{ ...opened, cdnConfig: newer, cdnConfigHistory: [{ ...opened.cdnConfigHistory[0], cdnConfig: newer, firstSeenAt: '2026-10-18T13:00:00Z', lastSeenAt: '2026-10-18T13:00:00Z' }, { ...opened.cdnConfigHistory[0], advertisedRegions: [] }] }] } as DiscoveryPage)));
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); fireEvent.click(await screen.findByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/Discovery complete/); await select();
    const note = screen.getByText('US now advertises a different CDN configuration.').closest('[role="note"]')!;
    expect(within(note as HTMLElement).getByText(`${opened.cdnConfig.slice(0, 10)}…${opened.cdnConfig.slice(-4)}`)).toBeVisible();
    expect(within(note as HTMLElement).getByText(/Open again to switch/)).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Source details' }));
    const marked = within(screen.getByRole('dialog')).getAllByRole('heading', { level: 4 }).filter(h => /Used by the open build/.test(h.textContent || ''));
    expect(marked.map(h => h.textContent)).toEqual([`Earlier · ${opened.cdnConfig.slice(0, 10)}…${opened.cdnConfig.slice(-4)} · Used by the open build`]);
    expect(bridge.open).toHaveBeenCalledTimes(1);
  });
  it('shows the newer CDN configuration note in the Home build header after a refresh, without reopening', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    const opened = routed(), newer = 'e'.repeat(32), build = screen.getByRole('button', { name: `Build ${opened.version}` });
    fireEvent.click(build); expect(within(screen.getByRole('dialog')).queryByText(/now advertises/)).not.toBeInTheDocument(); fireEvent.click(build);
    vi.mocked(bridge.refreshAll).mockResolvedValue(success(catalog({ ...pageFixture, builds: [{ ...opened, cdnConfig: newer, cdnConfigHistory: [{ ...opened.cdnConfigHistory[0], cdnConfig: newer, firstSeenAt: '2026-10-18T13:00:00Z', lastSeenAt: '2026-10-18T13:00:00Z' }, { ...opened.cdnConfigHistory[0], advertisedRegions: [] }], cdnConfigHistoryCount: 2 }] } as DiscoveryPage)));
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); fireEvent.click(await screen.findByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/Discovery complete/);
    fireEvent.click(screen.getByRole('button', { name: 'View verified overview' })); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: `Build ${opened.version}` }));
    const dialog = screen.getByRole('dialog');
    // The header keeps the configuration the build was opened with and names the one US advertises now.
    expect(within(dialog).getByText('CDN configuration').nextElementSibling).toHaveTextContent(opened.cdnConfig);
    expect(within(dialog).getByText('US now advertises').nextElementSibling).toHaveTextContent(newer);
    expect(within(dialog).getByText(/US now advertises a different CDN configuration\. This open build keeps the one it was opened with\. Open again to switch/)).toBeVisible();
    expect(bridge.open).toHaveBeenCalledTimes(1);
  });
  it('does not tell a US open about a CDN configuration only another region advertises in the same check', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    const opened = routed(), entry = opened.cdnConfigHistory[0], other = '0'.repeat(32);
    // us keeps the open configuration; eu advertises another in the same check, which wins only by key order.
    vi.mocked(bridge.refreshAll).mockResolvedValue(success(catalog({ ...pageFixture, builds: [{ ...opened, cdnConfig: other, cdnConfigHistory: [{ ...entry, cdnConfig: other, advertisedRegions: ['eu'], regions: ['eu'], sourceReceipts: entry.sourceReceipts.map(r => ({ ...r, region: 'eu' })) }, entry] }] } as DiscoveryPage)));
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); fireEvent.click(await screen.findByRole('button', { name: 'Refresh builds' }));
    await screen.findByText(/Discovery complete/); await select();
    expect(screen.queryByText(/now advertises a different CDN configuration/)).not.toBeInTheDocument();
    expect(screen.getByText('Several CDN configurations in one check.')).toBeVisible();
  });
  it('opens exact verified Home immediately without a synthetic preparation frame or fake readiness', async () => {
    const { bridge } = setup(); await begin();
    const heading = await screen.findByRole('heading', { name: 'Build overview' }); await waitFor(() => expect(heading).toHaveFocus());
    expect(screen.queryByRole('heading', { name: 'Open this build overview' })).not.toBeInTheDocument();
    expect(bridge.open).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us' }, expect.any(String));
    expect(screen.getAllByText('Partial').length).toBeGreaterThan(0); expect(screen.getByText('Catalog memberships')).toBeVisible(); expect(screen.getAllByText(/All published variants/).length).toBeGreaterThan(0);
    expect(screen.getByText('Required encoded content')).toBeVisible(); expect(screen.queryByText('Configuration ready')).not.toBeInTheDocument(); expect(screen.queryByText(/118.4|1,284,630|Windows/)).not.toBeInTheDocument(); expect(screen.getByText('English · enUS')).toBeVisible(); expect(screen.queryByRole('button',{name:/locale/i})).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Download all' })).not.toBeInTheDocument(); expect(screen.getByText(/Download all needs a verified download-manifest scope/)).toBeVisible(); expect(screen.queryByRole('button', { name: /Items/ })).not.toBeInTheDocument();
  });
  it('uses an actual saved regional route even when routeRegions advertises a different current preference', async () => {
    const build = { ...routed(base, 'eu'), routeRegions: ['us'] }; const { bridge } = setup([build]); await select();
    expect(screen.getByRole('combobox', { name: 'Download source' })).toHaveValue('eu');
    fireEvent.click(screen.getByRole('button', { name: 'Open build' })); await screen.findByRole('heading', { name: 'Build overview' });
    expect(bridge.open).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'eu' }, expect.any(String));
  });
  it('requires a saved cdns receipt and never falls back to an advertised or route-only US region', async () => {
    const { bridge } = setup([{ ...base, routeRegions: ['us'], sourceReceipts: base.sourceReceipts.filter(receipt => receipt.role === 'versions') }]); await select();
    expect(screen.getByRole('button', { name: 'Open build' })).toBeDisabled(); expect(screen.getByRole('combobox', { name: 'Download source' })).toBeDisabled();
    expect(screen.getByText(/Open needs a recorded CDN route/)).toBeVisible(); expect(bridge.open).not.toHaveBeenCalled();
  });
  it('keeps discovery mounted, typed search, loaded rows, exact selection and Open focus when choosing another build', async () => {
    const { bridge } = setup(); await select();
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '70448' } }); const open = screen.getByRole('button', { name: 'Open build' }); open.focus(); fireEvent.click(open);
    await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: /Choose build/ }));
    await waitFor(() => expect(open).toHaveFocus()); expect(screen.getByRole('searchbox')).toHaveValue('70448'); expect(screen.getByRole('option', { name: /Identity dddddddddd/ })).toHaveAttribute('aria-selected', 'true'); expect(bridge.readAll).toHaveBeenCalledTimes(1); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });
  it('presents actual preparation stages and Back detaches without canceling or late navigation', async () => {
    const { bridge, emit } = setup(); const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin();
    await screen.findByRole('heading', { name: 'Open this build overview' }); const id = vi.mocked(bridge.open).mock.calls[0][1];
    act(() => emit({ protocolVersion: 1, type: 'progress', requestId: id, sequence: 1, stage: 'Verifying ROOT metadata', region: 'us', completed: 1, total: 2, unit: 'documents' }));
    expect(screen.getByRole('status')).toHaveTextContent('Verifying ROOT metadata'); expect(within(document.querySelector('.preparation-view')!).getByText('1 of 2 documents · 50% of current stage')).toBeVisible(); fireEvent.click(within(document.querySelector('.preparation-view')!).getByRole('button',{name:/Downloads & activity/}));expect(document.querySelector('.preparation-view .phase')).toHaveAttribute('aria-live','off');expect(screen.getByText('Verifying ROOT metadata',{selector:'.download-task-stage'})).toHaveAttribute('aria-live','polite');fireEvent.keyDown(document,{key:'Escape'});expect(document.querySelector('.preparation-view .phase')).toHaveAttribute('aria-live','polite');
    fireEvent.click(screen.getByRole('button', { name: /Back to build list/ })); expect(bridge.cancel).not.toHaveBeenCalled();
    await act(async () => pending.resolve(success(snapshot()))); expect(screen.queryByRole('heading', { name: 'Build overview' })).not.toBeInTheDocument(); expect(screen.getByText('Verified overview available')).toBeVisible();
  });
  it('reattaches the same pending exact build without a duplicate opening request', async () => {
    const { bridge } = setup(); const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin();
    await screen.findByRole('heading', { name: 'Open this build overview' }); fireEvent.click(screen.getByRole('button', { name: /Back to build list/ })); fireEvent.click(screen.getByRole('button', { name: 'Open build' }));
    await screen.findByRole('heading', { name: 'Open this build overview' }); expect(bridge.open).toHaveBeenCalledTimes(1);
    await act(async () => pending.resolve(success(snapshot()))); await screen.findByRole('heading', { name: 'Build overview' });
  });
  it('refuses a different context while one is pending and returns to the original preparation', async () => {
    const other = routed({ ...base, id: 'f'.repeat(64), buildConfig: 'c'.repeat(32) }); const { bridge } = setup([routed(), other]);
    const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin(); await screen.findByRole('heading', { name: 'Open this build overview' });
    fireEvent.click(screen.getByRole('button', { name: /Back to build list/ })); await select(/Identity ffffffffff/); fireEvent.click(screen.getByRole('button', { name: 'Open build' }));
    await screen.findByRole('heading', { name: 'Open this build overview' }); expect(screen.getByText(/No new opening request was started/)).toBeVisible(); expect(bridge.open).toHaveBeenCalledTimes(1);
    expect(screen.getByText(base.id)).toBeVisible();
  });
  it('shows stopping until actual target retirement and preserves the cancel keyboard anchor', async () => {
    const { bridge } = setup(); const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin(); const prepHeading = await screen.findByRole('heading', { name: 'Open this build overview' });
    await waitFor(() => expect(prepHeading).toHaveFocus());
    const cancel = screen.getByRole('button', { name: 'Cancel preparation' }); cancel.focus(); fireEvent.click(cancel);
    await waitFor(() => expect(bridge.cancel).toHaveBeenCalled()); expect(screen.getByRole('button', { name: 'Stopping preparation…' })).toHaveFocus(); expect(screen.getByRole('status')).toHaveTextContent('Stopping preparation…');
    expect(screen.queryByRole('button', { name: 'Retry this build' })).not.toBeInTheDocument();
    await act(async () => pending.resolve(failure())); expect(screen.getByText('Preparation canceled')).toBeVisible(); expect(screen.getByText('Metadata preparation canceled')).toBeVisible(); expect(screen.queryByText('Metadata verification pending')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Retry this build' })).toBeEnabled();
  });
  it('fences detached completion while the Product filter changes and opens its saved result only on View verified overview', async () => {
    const { bridge } = setup(); const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin(); await screen.findByRole('heading', { name: 'Open this build overview' });
    fireEvent.click(screen.getByRole('button', { name: /Back to build list/ })); fireEvent.change(screen.getByRole('combobox', { name: 'Product' }), { target: { value: 'wowt' } });
    await act(async () => pending.resolve(success(snapshot()))); expect(screen.queryByRole('heading', { name: 'Build overview' })).not.toBeInTheDocument(); expect(screen.getByRole('combobox', { name: 'Product' })).toHaveValue('wowt');
    // The Product filter only narrows the list, so the verified result belongs to the current context and opens directly.
    fireEvent.click(screen.getByRole('button', { name: 'View verified overview' })); await screen.findByRole('heading', { name: 'Build overview' });
  });
  it('shows typed preparation failure and retries only the same product, identity and source', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValueOnce(failure('CONFIGURATION_CORRUPT')).mockResolvedValueOnce(success(snapshot())); await begin();
    await screen.findByText('CONFIGURATION_CORRUPT'); expect(screen.getByText('Metadata preparation not completed')).toBeVisible(); expect(screen.queryByText('Metadata verification pending')).not.toBeInTheDocument(); expect(screen.queryByRole('heading', { name: 'Build overview' })).not.toBeInTheDocument(); fireEvent.click(screen.getByRole('button', { name: 'Retry this build' })); await screen.findByRole('heading', { name: 'Build overview' });
    expect(vi.mocked(bridge.open).mock.calls[0][0]).toEqual(vi.mocked(bridge.open).mock.calls[1][0]); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });
  it('exposes actual verified receipts and source metadata with modal focus return', async () => {
    setup(); await begin(); const heading = await screen.findByRole('heading', { name: 'Build overview' }); await waitFor(() => expect(heading).toHaveFocus()); const metadata = screen.getByRole('button', { name: 'View metadata details' }); metadata.focus(); fireEvent.click(metadata);
    const dialog = screen.getByRole('dialog'); expect(within(dialog).getByText('Build configuration · Verified')).toBeVisible(); expect(within(dialog).getByText(workspaceFixture.configurations[0].sourceUrl)).toBeVisible(); expect(within(dialog).getByText('home-inventory-v1')).toBeVisible();
    fireEvent(dialog, new Event('cancel', { cancelable: true })); expect(metadata).toHaveFocus();
  });
  it('refuses a success projection for another exact identity or source before Home', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success(snapshot({ ...routed(), id: 'f'.repeat(64) })));
    await begin(); await screen.findByText('PROTOCOL_ERROR'); expect(screen.queryByRole('heading', { name: 'Build overview' })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Retry this build' })).not.toBeInTheDocument();
  });
  it('reconciles only a successfully acknowledged exact identity into the preserved discovery window without HTTP', async () => {
    const first = { ...routed(), isNew: true };
    const second = { ...routed(), id: 'f'.repeat(64), buildConfig: 'c'.repeat(32), isNew: true };
    const { bridge } = setup([first, second]); await select();
    expect(screen.getAllByText('New to VibeMiner')).toHaveLength(2);
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '70448' } });
    vi.mocked(bridge.open).mockResolvedValue(success(snapshot({ ...first, isNew: false, isLocal: true })));
    fireEvent.click(screen.getByRole('button', { name: 'Open build' })); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ }));
    const acknowledged = await screen.findByRole('option', { name: /Identity dddddddddd/ });
    expect(within(acknowledged).queryByText('New to VibeMiner')).not.toBeInTheDocument(); expect(within(acknowledged).getByText('Prepared', { exact: true })).toBeVisible();
    expect(within(screen.getByRole('option', { name: /Identity ffffffffff/ })).getByText('New to VibeMiner')).toBeVisible();
    expect(acknowledged).toHaveAttribute('aria-selected', 'true'); expect(screen.getByRole('searchbox')).toHaveValue('70448');
    expect(bridge.readAll).toHaveBeenCalledTimes(1); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });

  it('rejects a historical configuration-only admission until the real inventory profile is prepared', async () => {
    const { bridge } = setup(); const full = snapshot();
    vi.mocked(bridge.open).mockResolvedValueOnce(success({ ...full, preparationProfile: 'home-configurations-v1', readiness: 'configurationReady', metadataState: 'configurationsVerified', inventoryState: 'notPrepared', retentionState: 'unknown', inventory: null, metadata: null })).mockResolvedValueOnce(success(full));
    await begin(); await screen.findByText('INVENTORY_REQUIRED'); expect(screen.queryByRole('heading', { name: 'Build overview' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry this build' })); await screen.findByRole('heading', { name: 'Build overview' }); expect(bridge.open).toHaveBeenCalledTimes(2);
  });
  it('shows complete published counts and exact remaining/shared encoded accounting without adding shared twice', async () => {
    setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    expect(within(screen.getByText('Catalog memberships').closest('.dh-metric')!).getByText('6')).toBeVisible();
    expect(within(screen.getByText('ROOT memberships').closest('dl')!).getByText('3')).toBeVisible();
    expect(within(screen.getByText('Required encoded content').closest('.dh-metric')!).getByText('450 bytes')).toBeVisible();
    const ledger = (label: string) => screen.getByText(label, { selector: 'dt' }).parentElement!;
    expect(within(screen.getByText(/^Verified locally available/, { selector: 'dt' }).parentElement!).getByText('100 bytes')).toBeVisible();
    expect(within(ledger('Of those, shared with other builds')).getByText('100 bytes')).toBeVisible();
    expect(within(ledger('Additional encoded content required')).getByText('350 bytes')).toBeVisible();
    expect(document.querySelector('.inventory-retention-chart > i:nth-child(2)')).toHaveStyle({width:'22.22%'}); expect(screen.getByText(/Shared bytes are part of the verified bytes, not added again\. Metadata may overlap/)).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Scope and measurement details' }));
    const dialog = screen.getByRole('dialog'); expect(within(dialog).getByText('600 bytes')).toBeVisible(); expect(within(dialog).getByText('Unique ROOT/IN contents (CKeys)')).toBeVisible(); expect(within(dialog).getByText('Exact encoded objects (EKeys)')).toBeVisible();
  });
  it('reads Files only on entry through the exact sealed local context with bounded page parameters', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); expect(bridge.inventoryFiles).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    expect(bridge.inventoryFiles).toHaveBeenCalledWith(context); expect(bridge.inventory).not.toHaveBeenCalled();
    expect(screen.getByText('Name not known')).toBeVisible(); expect(screen.getByText('authored/install-a.bin')).toBeVisible(); expect(screen.getByText(/2 files on this page · 2 files in this catalog/)).toBeVisible();
    const inspector = await screen.findByRole('complementary', { name: 'FileDataID 12345' }); await within(inspector).findByText(detailFixture.memberships[0].encodingKey!);
    expect(bridge.inventoryFile).toHaveBeenCalledWith({ ...context, limit: 100, file: 'root:12345' });
    expect(screen.getByRole('searchbox', { name: 'Search by FileDataID or content key' })).toBeVisible(); expect(screen.queryByRole('button', { name: 'Download all', hidden: true })).not.toBeInTheDocument(); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });
  it('keeps page controls focused and current rows readable during paged local reads', async () => {
    const { bridge } = setup(); const later = deferred<BridgeReply<InventoryFilesPage>>();
    vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(success(filesPage({ nextCursor: 'root:12345', entries: [filesFixture.entries[0]] as InventoryFilesPage['entries'] }))).mockReturnValueOnce(later.promise);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    const next = screen.getByRole('button', { name: 'Next page' }); next.focus(); fireEvent.click(next);
    expect(next).toHaveFocus(); expect(next).toHaveAttribute('aria-disabled', 'true'); expect(within(screen.getByRole('listbox')).getByText('12345')).toBeVisible();
    await act(async () => later.resolve(success(filesPage({ entries: [filesFixture.entries[1]] as InventoryFilesPage['entries'] }))));
    expect(within(screen.getByRole('listbox')).getByText('authored/install-a.bin')).toBeVisible(); expect(next).toHaveFocus(); expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ ...context, after: 'root:12345' });
    fireEvent.click(screen.getByRole('button', { name: 'Previous page' })); await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenLastCalledWith(context));
  });
  it('preserves good rows and the exact failed cursor when retrying a failed page', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(success(filesPage({ nextCursor: 'root:12345', entries: [filesFixture.entries[0]] as InventoryFilesPage['entries'] }))).mockResolvedValueOnce({ ok: false, error: { code: 'BUSY', message: 'Authored catalog busy.', retryable: true } }).mockResolvedValueOnce(success(filesPage({ entries: [filesFixture.entries[1]] as InventoryFilesPage['entries'] })));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345'); fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await screen.findByText('BUSY'); expect(within(screen.getByRole('listbox')).getByText('12345')).toBeVisible();
    const retry = screen.getByRole('button', { name: 'Retry page' }); retry.focus(); fireEvent.click(retry);
    await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenCalledTimes(3)); expect(vi.mocked(bridge.inventoryFiles).mock.calls[1][0]).toEqual(vi.mocked(bridge.inventoryFiles).mock.calls[2][0]);
    await within(screen.getByRole('listbox')).findByText('authored/install-a.bin');
    // The Retry button is gone; focus is on the Files heading, not the document body.
    expect(screen.queryByRole('button', { name: 'Retry page' })).not.toBeInTheDocument(); expect(screen.getByRole('heading', { name: 'Files' })).toHaveFocus();
  });
  // A retried page move keeps its own history step: Previous after a retried Next goes back to page 1,
  // and Previous after a retried Previous goes one further back instead of reading the same page again.
  const page1 = () => success(filesPage({ nextCursor: 'root:12345', entries: [filesFixture.entries[0]] as InventoryFilesPage['entries'] }));
  const page2 = () => success(filesPage({ nextCursor: 'install:7', entries: [filesFixture.entries[1]] as InventoryFilesPage['entries'] }));
  const page3 = () => success(filesPage({ entries: [] }));
  const busyPage = { ok: false as const, error: { code: 'BUSY', message: 'Authored catalog busy.', retryable: true } };
  it('goes back to the first page after a failed Next is retried', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(page1()).mockResolvedValueOnce(busyPage).mockResolvedValueOnce(page2()).mockResolvedValueOnce(page1());
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('BUSY');
    fireEvent.click(screen.getByRole('button', { name: 'Retry page' })); await within(screen.getByRole('listbox')).findByText('authored/install-a.bin');
    expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ ...context, after: 'root:12345' });
    const previous = screen.getByRole('button', { name: 'Previous page' }); expect(previous).toHaveAttribute('aria-disabled', 'false'); fireEvent.click(previous);
    await within(screen.getByRole('listbox')).findByText('12345'); expect(bridge.inventoryFiles).toHaveBeenCalledTimes(4); expect(bridge.inventoryFiles).toHaveBeenLastCalledWith(context);
    expect(screen.getByRole('button', { name: 'Previous page' })).toHaveAttribute('aria-disabled', 'true');
  });
  it('steps back one more page after a failed Previous is retried', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(page1()).mockResolvedValueOnce(page2()).mockResolvedValueOnce(page3()).mockResolvedValueOnce(busyPage).mockResolvedValueOnce(page2()).mockResolvedValueOnce(page1());
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await within(screen.getByRole('listbox')).findByText('authored/install-a.bin');
    fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('No files');
    fireEvent.click(screen.getByRole('button', { name: 'Previous page' })); await screen.findByText('BUSY');
    fireEvent.click(screen.getByRole('button', { name: 'Retry page' })); await within(await screen.findByRole('listbox')).findByText('authored/install-a.bin');
    expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ ...context, after: 'root:12345' });
    fireEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    await within(screen.getByRole('listbox')).findByText('12345'); expect(bridge.inventoryFiles).toHaveBeenCalledTimes(6); expect(bridge.inventoryFiles).toHaveBeenLastCalledWith(context);
  });
  it('refuses mismatched prepared generations and invented ROOT names instead of showing them', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(success({ ...filesPage(), generation: '2' }));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('INVENTORY_CONTEXT_MISMATCH');
    expect(screen.queryByText('12345')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Reopen this build' })).toBeEnabled(); expect(screen.queryByRole('button', { name: 'Retry page' })).not.toBeInTheDocument();
    vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(success(filesPage({ totalCount: null, entries: [{ ...filesFixture.entries[0], name: 'must-not-be-a-root-filename' }] as InventoryFilesPage['entries'] })));
    fireEvent.change(screen.getByRole('combobox', { name: 'Locale flag' }), { target: { value: '2' } });
    await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenCalledTimes(2)); expect(screen.queryByText('must-not-be-a-root-filename')).not.toBeInTheDocument(); expect(screen.getByText('INVENTORY_CONTEXT_MISMATCH')).toBeVisible();
  });
  it('shows every membership with raw flags, keys and lengths, and keeps unresolved sizes unknown', async () => {
    setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    const list = screen.getByRole('listbox'); list.focus(); fireEvent.click(within(screen.getByRole('listbox')).getAllByRole('option')[0]);
    const inspector = screen.getByRole('complementary', { name: 'FileDataID 12345' }); await within(inspector).findByText('Membership 1');
    expect(within(inspector).getByText('Membership 2')).toBeVisible(); expect(within(inspector).getByText('ffffffffffffffff')).toBeVisible(); expect(within(inspector).getByText('300 bytes')).toBeVisible();
    expect(within(inspector).getByText('ROOT · block 1, entry 0')).toBeVisible(); expect(within(inspector).getByText('0x00000200')).toBeVisible(); expect(within(inspector).getByText(/raw parts 0x08000000 · 0x00000000 · 0/)).toBeVisible();
    expect(within(inspector).getByText('Unresolved · CONTENT_UNRESOLVED')).toBeVisible(); expect(within(inspector).getAllByText('Unknown')).toHaveLength(2);
    expect(within(inspector).getByText(/no platform is inferred/)).toBeVisible(); expect(within(inspector).getByText(/Name not known. ROOT has no names/)).toBeVisible();
    expect(screen.getByText('+1 unresolved')).toBeVisible(); expect(screen.getByText('1 of 2 stored')).toBeVisible(); expect(screen.getByText('enUS, enGB')).toBeVisible();
    expect(list).toHaveFocus(); expect(within(screen.getByRole('listbox')).getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true');
  });
  it('sends each filter as an exact flat field, names the search when nothing matches and clears back to the search box', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    const search = screen.getByRole('searchbox', { name: 'Search by FileDataID or content key' });
    vi.mocked(bridge.inventoryFiles).mockImplementation(async params => success(filesPage({ entries: [], totalCount: Object.keys(params).length > 5 ? null : '2' })));
    fireEvent.change(search, { target: { value: '99999999' } }); fireEvent.keyDown(search, { key: 'Enter' });
    await screen.findByText("No file in this build's prepared catalog has FileDataID 99999999.");
    expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ ...context, fileDataId: '99999999' });
    fireEvent.change(search, { target: { value: 'ABCD12' } }); fireEvent.keyDown(search, { key: 'Enter' });
    await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ ...context, contentKey: 'abcd12' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Locale flag' }), { target: { value: '512' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Content flags' }), { target: { value: '134217728' } });
    fireEvent.click(within(screen.getByRole('group', { name: 'Source family' })).getByRole('button', { name: 'ROOT' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Stored locally' }));
    await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ ...context, contentKey: 'abcd12', localeFlags: '512', contentFlags: '134217728', family: 'root', storedOnly: true }));
    await screen.findByText("No file in this build's prepared catalog has a content key starting abcd12 with these filters (4 active).");
    expect(screen.getByRole('button', { name: /^Filters/ })).toHaveAccessibleName('Filters, 4 active');
    const calls = vi.mocked(bridge.inventoryFiles).mock.calls.length;
    fireEvent.change(search, { target: { value: 'not a key' } }); fireEvent.keyDown(search, { key: 'Enter' });
    expect(screen.getByText('Search not understood')).toBeVisible(); expect(search).toHaveAttribute('aria-invalid', 'true'); expect(bridge.inventoryFiles).toHaveBeenCalledTimes(calls);
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenLastCalledWith(context)); await waitFor(() => expect(search).toHaveFocus()); expect(search).toHaveValue('');
    expect(screen.getByRole('combobox', { name: 'Locale flag' })).toHaveValue('0'); expect(screen.getByRole('checkbox', { name: 'Stored locally' })).not.toBeChecked();
  });
  it('marks which memberships matched the active filters in the inspector', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    vi.mocked(bridge.inventoryFiles).mockResolvedValue(success(filesPage({ totalCount: null })));
    fireEvent.change(screen.getByRole('combobox', { name: 'Locale flag' }), { target: { value: '2' } });
    await waitFor(() => expect(bridge.inventoryFile).toHaveBeenLastCalledWith({ ...context, limit: 100, file: 'root:12345', localeFlags: '2' }));
    const inspector = screen.getByRole('complementary', { name: 'FileDataID 12345' });
    expect(await within(inspector).findByText('Matches the filters')).toBeVisible(); expect(within(inspector).getByText('Does not match')).toBeVisible();
    expect(within(inspector).getByText(/2 in ROOT · 2 different content keys · 1 matches the filters/)).toBeVisible();
    expect(inspector.querySelectorAll('.dh-member.dh-current')).toHaveLength(1);
  });
  it('explains a too-broad content key calmly and a scan-limited empty page without claiming absence', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(success(filesPage({ nextCursor: 'install:7' })));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    await within(screen.getByRole('complementary', { name: 'FileDataID 12345' })).findByText('Membership 1');
    vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce({ ok: false, error: { code: 'INVENTORY_FILTER_BROAD', message: 'More than 16,384 memberships have a content key starting abcd. Type more of the key.', retryable: false } });
    const search = screen.getByRole('searchbox', { name: 'Search by FileDataID or content key' }); fireEvent.change(search, { target: { value: 'abcd' } }); fireEvent.keyDown(search, { key: 'Enter' });
    expect(await screen.findByText('Content key too short for this catalog')).toBeVisible(); expect(screen.queryByRole('button', { name: 'Retry page' })).not.toBeInTheDocument();
    // The old rows stay readable but are labelled as the previous filters' rows; nothing pages them with the new filters.
    const list = screen.getByRole('listbox'); expect(within(list).getByText('12345')).toBeVisible(); expect(within(list).getByText('authored/install-a.bin')).toBeVisible();
    expect(screen.getByText('These rows are from the previous filters')).toBeVisible();
    expect(screen.getByText('2 files from the previous filters · not the current results')).toBeVisible(); expect(screen.queryByText(/matching files on this page/)).not.toBeInTheDocument();
    for (const name of ['Next page', 'Previous page', 'First page']) expect(screen.getByRole('button', { name })).toHaveAttribute('aria-disabled', 'true');
    const calls = vi.mocked(bridge.inventoryFiles).mock.calls.length; fireEvent.click(screen.getByRole('button', { name: 'Next page' })); expect(bridge.inventoryFiles).toHaveBeenCalledTimes(calls);
    // The inspector keeps the filters of the rows it belongs to.
    expect(vi.mocked(bridge.inventoryFile).mock.calls.every(([params]) => !('contentKey' in params))).toBe(true);
    fireEvent.change(search, { target: { value: '' } }); fireEvent.keyDown(search, { key: 'Enter' }); await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenLastCalledWith(context));
    vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(success(filesPage({ entries: [], totalCount: null, scanLimited: true, nextCursor: 'root:4000000' })));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Stored locally' }));
    expect(await screen.findByText('No match yet')).toBeVisible(); expect(screen.getByText(/Searched through FileDataID 4,000,000/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveAttribute('aria-disabled', 'false'); expect(screen.queryByText('No files match')).not.toBeInTheDocument();
  });
  it('keeps the picked file across filter changes and says when the filters exclude it', async () => {
    const { bridge } = setup();
    const install = { ...detailFixture.memberships[0], id: '7', family: 'install', blockOrdinal: null, entryOrdinal: null, installOrdinal: 0, localeFlags: null, contentFlags: null, rawContentFlags: null, nameHash: null };
    vi.mocked(bridge.inventoryFile).mockImplementation(async params => success((params.file.startsWith('install:')
      ? { ...detailFixture, buildIdentity: params.buildIdentity, file: params.file, family: 'install', fileDataId: null, name: 'authored/install-a.bin', membershipCount: 1, matchingCount: params.contentFlags ? 0 : 1, memberships: [{ ...install, matches: !params.contentFlags }] }
      : { ...detailFixture, buildIdentity: params.buildIdentity, file: params.file }) as InventoryFileDetail));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' }));
    const list = await screen.findByRole('listbox'); await within(list).findByText('12345');
    fireEvent.click(within(list).getAllByRole('option')[1]); expect(screen.getByRole('complementary', { name: 'authored/install-a.bin' })).toBeVisible();
    // Still in the new results: it stays selected; the selection does not jump to the first row.
    vi.mocked(bridge.inventoryFiles).mockImplementation(async params => success(filesPage({ entries: [filesFixture.entries[1]] as InventoryFilesPage['entries'], totalCount: params.storedOnly ? null : '1' })));
    fireEvent.click(within(screen.getByRole('group', { name: 'Source family' })).getByRole('button', { name: 'Install' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Stored locally' }));
    await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ ...context, family: 'install', storedOnly: true }));
    await waitFor(() => expect(within(screen.getByRole('listbox')).getByRole('option')).toHaveAttribute('aria-selected', 'true'));
    await within(screen.getByRole('complementary', { name: 'authored/install-a.bin' })).findByText('Membership 7');
    expect(screen.getByRole('complementary', { name: 'authored/install-a.bin' })).not.toHaveTextContent('outside');
    // Excluded by the filters: the selection stays in the inspector and says so; the first row is not picked instead.
    vi.mocked(bridge.inventoryFiles).mockImplementation(async () => success(filesPage({ entries: [filesFixture.entries[0]] as InventoryFilesPage['entries'], totalCount: null })));
    fireEvent.click(within(screen.getByRole('group', { name: 'Source family' })).getByRole('button', { name: 'All' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Content flags' }), { target: { value: '134217728' } });
    await waitFor(() => expect(bridge.inventoryFile).toHaveBeenLastCalledWith({ ...context, limit: 100, file: 'install:7', storedOnly: true, contentFlags: '134217728' }));
    const inspector = screen.getByRole('complementary', { name: 'authored/install-a.bin' });
    expect(await within(inspector).findByText('Selected file is outside these results')).toBeVisible(); expect(inspector).toHaveTextContent('outside these results');
    expect(within(screen.getByRole('listbox')).getByRole('option')).toHaveAttribute('aria-selected', 'false');
  });
  it('toggles the Filters disclosure with its active count', async () => {
    setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    const toggle = screen.getByRole('button', { name: /^Filters/ }); expect(toggle).toHaveAttribute('aria-expanded', 'false'); expect(toggle).toHaveAttribute('aria-controls', 'file-filters');
    fireEvent.click(toggle); expect(toggle).toHaveAttribute('aria-expanded', 'true'); expect(document.getElementById('file-filters')).toHaveClass('dh-open');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Stored locally' })); expect(toggle).toHaveAccessibleName('Filters, 1 active');
  });
  it('returns Files to the Home origin and retains its local page without another read', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); const browse = screen.getByRole('button', { name: 'Browse in Files' }); browse.focus(); fireEvent.click(browse); await within(await screen.findByRole('listbox')).findByText('12345');
    fireEvent.click(screen.getByRole('button', { name: /Return to build overview/ })); await waitFor(() => expect(browse).toHaveFocus()); fireEvent.click(screen.getByRole('button', { name: 'Files' })); expect(within(screen.getByRole('listbox')).getByText('12345')).toBeVisible(); expect(bridge.inventoryFiles).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); await screen.findByRole('button', { name: 'Open build' }); expect(bridge.readAll).toHaveBeenCalledTimes(1); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });
  it('discards a late catalog result after leaving the exact Home context', async () => {
    const { bridge } = setup(); const pending = deferred<BridgeReply<InventoryFilesPage>>(); vi.mocked(bridge.inventoryFiles).mockReturnValue(pending.promise);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); await act(async () => pending.resolve(success(filesPage())));
    expect(screen.queryByRole('heading', { name: 'Files' })).not.toBeInTheDocument(); expect(screen.queryByText('12345')).not.toBeInTheDocument(); expect(screen.getByRole('combobox', { name: 'Product' })).toHaveValue('all');
  });
  it('drops a page that a newer filter superseded', async () => {
    const { bridge } = setup(); const slow = deferred<BridgeReply<InventoryFilesPage>>();
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    vi.mocked(bridge.inventoryFiles).mockReturnValueOnce(slow.promise).mockResolvedValueOnce(success(filesPage({ entries: [filesFixture.entries[1]] as InventoryFilesPage['entries'], totalCount: null })));
    fireEvent.change(screen.getByRole('combobox', { name: 'Locale flag' }), { target: { value: '2' } });
    fireEvent.click(within(screen.getByRole('group', { name: 'Source family' })).getByRole('button', { name: 'Install' }));
    await screen.findByText(/1 matching files on this page/);
    await act(async () => slow.resolve(success(filesPage({ entries: [filesFixture.entries[0]] as InventoryFilesPage['entries'], totalCount: null }))));
    expect(within(screen.getByRole('listbox')).queryByText('12345')).not.toBeInTheDocument(); expect(within(screen.getByRole('listbox')).getByText('authored/install-a.bin')).toBeVisible();
  });
  it('keeps the selected file in the inspector when a page replaces its original row', async () => {
    const { bridge } = setup(); const later = deferred<BridgeReply<InventoryFilesPage>>(); vi.mocked(bridge.inventoryFiles).mockResolvedValueOnce(success(filesPage({ nextCursor: 'root:12345', entries: [filesFixture.entries[0]] as InventoryFilesPage['entries'] }))).mockReturnValueOnce(later.promise);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    fireEvent.click(within(screen.getByRole('listbox')).getByRole('option')); fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await act(async () => later.resolve(success(filesPage({ entries: [filesFixture.entries[1]] as InventoryFilesPage['entries'] }))));
    expect(screen.getByRole('complementary', { name: 'FileDataID 12345' })).toHaveTextContent('outside the current page'); expect(within(screen.getByRole('listbox')).getByRole('option')).toHaveAttribute('aria-selected', 'false');
  });

  it('shows backend stage numbering and one visible explanation, then updates from an unknown total', async () => {
    const { bridge, emit } = setup(); const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin(); await screen.findByRole('heading', { name: 'Open this build overview' });
    const requestId = vi.mocked(bridge.open).mock.calls[0][1];
    act(() => emit({ protocolVersion: 1, type: 'progress', requestId, sequence: 1, stage: 'Checking saved configuration', region: 'us', completed: null, total: null, unit: null, step: { index: 1, total: null, description: 'Check the exact saved source and configuration documents.' } }));
    expect(document.querySelector('.phase')).toHaveTextContent('[1/…] Checking saved configuration');
    expect(screen.getByRole('status')).toHaveTextContent('Stage 1 of a total determined after the configuration check.');
    expect(document.querySelector('.phase-description')).toHaveTextContent('Check the exact saved source and configuration documents. The stage count is determined after the configuration check.');
    act(() => emit({ protocolVersion: 1, type: 'progress', requestId, sequence: 2, stage: 'Reading ROOT metadata · 2 of 6 blocks', region: 'us', completed: 2, total: 6, unit: 'blocks', step: { index: 3, total: 10, description: 'Read published file identities and their variant flags.' } }));
    expect(document.querySelector('.phase')).toHaveTextContent('[3/10] Reading ROOT metadata · 2 of 6 blocks');
    const announcement = screen.getByRole('status'); expect(announcement).toHaveTextContent('Stage 3 of 10. Read published file identities and their variant flags.');
    act(() => emit({ protocolVersion: 1, type: 'progress', requestId, sequence: 3, stage: 'Reading ROOT metadata · 3 of 6 blocks', region: 'us', completed: 3, total: 6, unit: 'blocks', step: { index: 3, total: 10, description: 'Read published file identities and their variant flags.' } }));
    expect(screen.getByRole('status')).toBe(announcement); expect(announcement).not.toHaveTextContent('3 of 6'); expect(document.querySelector('.phase')).toHaveTextContent('3 of 6 blocks');
    expect(document.querySelector('.phase-description')).toHaveTextContent('Read published file identities and their variant flags.');
    expect(screen.queryByText(/The stage count is determined after the configuration check/)).not.toBeInTheDocument();
    expect(screen.queryByText(/30%|33.33%/)).not.toBeInTheDocument();
  });
  it('keeps legacy progress unnumbered and fences stale or unrelated numbered explanations', async () => {
    const { bridge, emit } = setup(); const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin(); await screen.findByRole('heading', { name: 'Open this build overview' }); const requestId = vi.mocked(bridge.open).mock.calls[0][1];
    const baseProgress: EngineProgress = { protocolVersion: 1, type: 'progress', requestId, sequence: 1, stage: 'Checking legacy metadata', region: 'us', completed: null, total: null, unit: null };
    act(() => emit(baseProgress)); expect(screen.getByRole('status')).toHaveTextContent('Checking legacy metadata'); expect(document.querySelector('.phase-count')).not.toBeInTheDocument();
    act(() => emit({ ...baseProgress, sequence: 3, stage: 'Reading ROOT', step: { index: 3, total: 8, description: 'Current exact-context explanation.' } }));
    act(() => { emit({ ...baseProgress, sequence: 2, stage: 'Old stage', step: { index: 2, total: 8, description: 'Obsolete explanation.' } }); emit({ ...baseProgress, requestId: 'other-request', sequence: 99, step: { index: 8, total: 8, description: 'Other build explanation.' } }); });
    expect(document.querySelector('.phase')).toHaveTextContent('[3/8] Reading ROOT'); expect(screen.getByText('Current exact-context explanation.')).toBeVisible(); expect(screen.queryByText(/Obsolete explanation|Other build explanation/)).not.toBeInTheDocument();
    act(() => emit({ ...baseProgress, sequence: 4, stage: 'Finishing legacy work', step: null })); expect(document.querySelector('.phase-count')).not.toBeInTheDocument();
  });
  it('does not present a numbered work stage or stale explanation while stopping or canceled', async () => {
    const { bridge, emit } = setup(); const pending = deferred<BridgeReply<WorkspaceSnapshot>>(); vi.mocked(bridge.open).mockReturnValue(pending.promise); await begin(); const heading = await screen.findByRole('heading', { name: 'Open this build overview' }); await waitFor(() => expect(heading).toHaveFocus()); const requestId = vi.mocked(bridge.open).mock.calls[0][1];
    act(() => emit({ protocolVersion: 1, type: 'progress', requestId, sequence: 1, stage: 'Reading ROOT', region: 'us', completed: null, total: null, unit: null, step: { index: 3, total: 8, description: 'Only while preparing.' } }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel preparation' })); expect(screen.getByRole('status')).toHaveTextContent('Stopping preparation…'); expect(document.querySelector('.phase-count')).not.toBeInTheDocument(); expect(screen.queryByText('Only while preparing.')).not.toBeInTheDocument(); expect(screen.getByText(/Waiting for preparation to stop safely/)).toBeVisible();
    await act(async () => pending.resolve(failure())); expect(screen.getByRole('status')).toHaveTextContent('Preparation canceled'); expect(document.querySelector('.phase-count')).not.toBeInTheDocument();
  });

  function downloadSnapshot(): WorkspaceSnapshot { return { ...snapshot(), ...downloadFixture, build: snapshot().build, sourceReference: snapshot().sourceReference } as WorkspaceSnapshot; }
  function downloadPage(after = '0'): DownloadPage {
    const result = downloadSnapshot(); const id = String(BigInt(after) + 1n);
    return { productId: result.build.productId, buildIdentity: result.build.id, sourceRegion: result.sourceReference.region, ledgerId: result.download!.ledgerId, assessedAt: result.download!.assessedAt, totalCount: '60', nextCursor: id === '60' ? null : id, entries: [{ id, encodingKey: BigInt(id).toString(16).padStart(32, '0'), encodedBytes: '100', verifiedAtAssessment: Number(id) <= 10 }] };
  }
  it('opens measured download Home without a ROOT catalog and discloses exact scope and receipts', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success(downloadSnapshot())); await begin();
    await screen.findByRole('heading', { name: 'Build overview' });
    expect(screen.getByText('Unique encoded objects')).toBeVisible(); expect(screen.getByText('Published download content', { exact: true })).toBeVisible(); expect(screen.getByText('File list')).toBeVisible(); expect(screen.getAllByText('Not prepared').length).toBeGreaterThan(0);
    expect(within(screen.getByText('Catalog memberships').closest('.dh-metric')!).getByText('Not prepared')).toBeVisible(); expect(screen.queryByText('ROOT/IN decoded bytes')).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: /Files.*Later/ })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Download all' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Scope and measurement details' })); const dialog = screen.getByRole('dialog'); expect(within(dialog).getByText('6,000 bytes')).toBeVisible(); expect(within(dialog).getByText(downloadFixture.download.ledgerId)).toBeVisible(); expect(within(dialog).getByText(/Exact full encoding keys/)).toBeVisible(); fireEvent(dialog, new Event('cancel', { cancelable: true }));
    const metadata = screen.getByRole('button', { name: 'View metadata details' }); metadata.focus(); fireEvent.click(metadata); expect(document.querySelectorAll('.configuration-receipt')).toHaveLength(3); expect(within(screen.getByRole('dialog')).getByText('download · Verified')).toBeVisible(); fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true })); expect(metadata).toHaveFocus(); expect(bridge.inventory).not.toHaveBeenCalled();
  });
  it('admits configurations with a typed unknown download size without inventing zero or readiness', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success({ ...downloadSnapshot(), download: null, downloadProblem: { code: 'DOWNLOAD_UNDECLARED', message: 'This build does not declare a download manifest.' }, metadata: null, metadataState: 'configurationsVerified', retentionState: 'unknown' })); await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    expect(screen.getByText('Published download size is unknown')).toBeVisible(); expect(screen.getByText('DOWNLOAD_UNDECLARED')).toBeVisible(); expect(screen.getByText(/This build does not declare a download manifest\./)).toBeVisible(); expect(screen.queryByRole('button', { name: /Download objects.*Unavailable/ })).not.toBeInTheDocument(); expect(screen.queryByText('0 bytes')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Recheck this build' })).toBeEnabled(); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); expect(screen.getByRole('heading', { name: 'Files' })).toHaveFocus(); expect(screen.getByText('File catalog not prepared', { selector: 'strong' })).toBeVisible(); expect(screen.getByText(/no supported download manifest/)).toHaveTextContent(/Search by FileDataID or CKey.*FileDataID \(ROOT\) or install name.*doesn't prepare yet/); expect(screen.queryByText(/coming/)).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: /Prepare/ })).not.toBeInTheDocument(); expect(screen.queryByRole('listbox')).not.toBeInTheDocument(); expect(bridge.download).not.toHaveBeenCalled();
  });
  it('pages exact download objects locally, preserves old rows on errors and fences a newer assessment', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success(downloadSnapshot())); const later = deferred<BridgeReply<DownloadPage>>(); vi.mocked(bridge.download).mockResolvedValueOnce(success(downloadPage())).mockReturnValueOnce(later.promise).mockResolvedValueOnce(success({ ...downloadPage('1'), assessedAt: '2026-10-19T12:00:01Z' }));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); const browse = screen.getByRole('button', { name: 'Browse in Files' }); browse.focus(); fireEvent.click(browse); await screen.findByText('Object 1'); expect(screen.getByRole('heading', { name: 'Files' })).toHaveFocus(); expect(screen.getAllByText('00000000000000000000000000000001').length).toBeGreaterThan(0); expect(screen.getByRole('listbox')).not.toHaveTextContent(/FileDataID/); expect(document.querySelector('.dh-files .dh-inspector')).not.toHaveTextContent(/FileDataID/);
    const next = screen.getByRole('button', { name: 'Next page' }); next.focus(); fireEvent.click(next); expect(next).toHaveFocus(); expect(next).toHaveAttribute('aria-disabled', 'true'); expect(screen.getByText('Object 1')).toBeVisible();
    await act(async () => later.resolve({ ok: false, error: { code: 'LOCAL_READ_FAILED', message: 'Authored local failure.', retryable: true } })); expect(screen.getByText('Object 1')).toBeVisible(); fireEvent.click(screen.getByRole('button', { name: 'Retry page' })); await screen.findByText('DOWNLOAD_CONTEXT_MISMATCH'); expect(screen.getByText('Object 1')).toBeVisible(); expect(screen.queryByText('Object 2')).not.toBeInTheDocument(); expect(bridge.download).toHaveBeenLastCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us', ledgerId: downloadFixture.download.ledgerId, limit: 50, after: '1' });
    fireEvent.click(screen.getByRole('button', { name: /Return to build overview/ })); await waitFor(() => expect(browse).toHaveFocus()); fireEvent.click(within(screen.getByRole('navigation', { name: 'Build areas' })).getByRole('button', { name: 'Files' })); expect(bridge.download).toHaveBeenCalledTimes(3); expect(screen.getByText('Object 1')).toBeVisible();
  });
  it('discards a late download-object page after Choose build and preserves Discovery without refresh', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success(downloadSnapshot())); const pending = deferred<BridgeReply<DownloadPage>>(); vi.mocked(bridge.download).mockReturnValue(pending.promise); const open = await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await waitFor(() => expect(bridge.download).toHaveBeenCalledTimes(1)); fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); await act(async () => pending.resolve(success(downloadPage()))); await waitFor(() => expect(open).toHaveFocus()); expect(screen.queryByText('Object 1')).not.toBeInTheDocument(); expect(bridge.readAll).toHaveBeenCalledTimes(1); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });

  it('returns focus after a successful download-page retry removes its retry button', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success(downloadSnapshot())); vi.mocked(bridge.download).mockResolvedValueOnce({ ok: false, error: { code: 'LOCAL_READ_FAILED', message: 'Authored local failure.', retryable: true } }).mockResolvedValueOnce(success(downloadPage()));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); const retry = await screen.findByRole('button', { name: 'Retry page' }); retry.focus(); fireEvent.click(retry); await screen.findByText('Object 1'); expect(retry).not.toBeInTheDocument(); expect(screen.getByRole('heading', { name: 'Files' })).toHaveFocus(); expect(bridge.download).toHaveBeenCalledTimes(2);
  });

  it('preserves Home object pages and Tables field/search across horizontal areas without reopening or refreshing', async () => {
    const { bridge } = setup(); const definitions = definitionsFixture.definitions as unknown as Record<string, TableDefinitionSnapshot>; const entries = Object.values(definitions).map(value => value.entry), provider = definitions['50238ec2'].provider;
    vi.mocked(bridge.open).mockResolvedValue(success(downloadSnapshot())); vi.mocked(bridge.download).mockImplementation(async p => success(downloadPage(p.after)));
    vi.mocked(bridge.tablesCatalog).mockImplementation(async p => { const rows = entries.filter(entry => !p.query || entry.name!.toLowerCase().includes(p.query.toLowerCase()) || definitions[entry.tableId].definition!.columns.some(field => field.name.toLowerCase().includes(p.query!.toLowerCase()))); return success({ productId: p.productId, buildIdentity: p.buildIdentity, sourceRegion: p.sourceRegion, provider, query: p.query ?? '', entries: rows, totalCount: String(rows.length), nextCursor: null }); });
    vi.mocked(bridge.tableDefinition).mockImplementation(async p => success(definitions[p.tableId]));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1'); fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Object 2');
    const areas = screen.getByRole('navigation', { name: 'Build areas' }); fireEvent.click(within(areas).getByRole('button', { name: 'Tables' })); const catalogue = await screen.findByRole('region', { name: 'Provider definitions on this page' }); fireEvent.click(await within(catalogue).findByRole('button', { name: /^ItemDefinition bundled/ })); await screen.findByRole('button',{name:'Structure'}); fireEvent.click(screen.getByRole('button',{name:'Structure'})); const field = await screen.findByRole('button', { name: 'Inspect field ClassID' }); field.focus(); fireEvent.click(field); const search = screen.getByRole('searchbox', { name: 'Search definitions' }); fireEvent.change(search, { target: { value: 'ClassName' } }); await screen.findByText('1 provider entries on this page · 1 matching');
    fireEvent.click(within(areas).getByRole('button', { name: 'Home' })); expect(screen.getByRole('heading', { name: 'Build overview' })).toHaveFocus(); expect(screen.getByRole('group', { name: 'Home utilities' })).toBeVisible();
    fireEvent.click(within(areas).getByRole('button', { name: 'Files' })); expect(screen.getByText('Object 2')).toBeVisible(); expect(bridge.download).toHaveBeenCalledTimes(2); fireEvent.click(within(areas).getByRole('button', { name: 'Tables' })); expect(search).toHaveValue('ClassName'); expect(field).toHaveAttribute('aria-pressed', 'true'); expect(screen.getByRole('heading', { name: 'Tables' })).toHaveFocus(); expect(screen.queryByRole('group', { name: 'Home utilities' })).not.toBeInTheDocument(); expect(document.querySelector('.sidebar')).not.toBeInTheDocument(); expect(bridge.open).toHaveBeenCalledTimes(1); expect(bridge.readAll).toHaveBeenCalledTimes(1); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });

});

it('keeps exact background table controls in unified activity across Discovery and preparation with guarded terminal focus',async()=>{
 const {bridge}=setup(),definition=definitionsFixture.definitions.b977271e as TableDefinitionSnapshot,load=deferred<BridgeReply<TableDataSnapshot>>(),opening=deferred<BridgeReply<WorkspaceSnapshot>>();
 vi.mocked(bridge.tablesCatalog).mockImplementation(async p=>success({...p,provider:definition.provider,query:p.query??'',entries:[definition.entry],totalCount:'1',nextCursor:null}));vi.mocked(bridge.tableDefinition).mockResolvedValue(success(definition));vi.mocked(bridge.tablePrepare).mockReturnValue(load.promise);
 await begin();await screen.findByRole('heading',{name:'Build overview'});fireEvent.click(within(screen.getByRole('navigation',{name:'Build areas'})).getByRole('button',{name:'Tables'}));fireEvent.click(await screen.findByRole('button',{name:/^ItemClassDefinition bundled/}));await screen.findByRole('button',{name:'View task'});
 fireEvent.click(screen.getByRole('button',{name:/Choose build/}));fireEvent.click(screen.getByRole('button',{name:/Downloads & activity/}));const row=screen.getByRole('heading',{name:'ItemClass table'}).closest('article')!;expect(row).toHaveTextContent(definition.productId);expect(within(row).queryByRole('button',{name:'View research'})).not.toBeInTheDocument();expect(bridge.cancel).not.toHaveBeenCalled();
 fireEvent.click(screen.getByRole('button',{name:'Close downloads and activity'}));vi.mocked(bridge.open).mockReturnValueOnce(opening.promise);fireEvent.click(screen.getByRole('button',{name:'Open build'}));await screen.findByRole('heading',{name:'Open this build overview'});fireEvent.click(screen.getByRole('button',{name:/Downloads & activity/}));
 const stop=screen.getByRole('button',{name:'Stop ItemClass table'});stop.focus();fireEvent.click(stop);expect(screen.getByRole('button',{name:'Stopping…'})).toHaveFocus();await act(async()=>load.resolve({ok:false,error:{code:'CANCELED',message:'Stopped safely.',retryable:true}}));expect(screen.getByRole('heading',{name:'ItemClass table'})).toHaveFocus();
 const dismiss=within(screen.getByRole('heading',{name:'ItemClass table'}).closest('article')!).getByRole('button',{name:'Dismiss outcome'});dismiss.focus();fireEvent.click(dismiss);expect(screen.queryByRole('heading',{name:'ItemClass table'})).not.toBeInTheDocument();expect(screen.getByRole('heading',{name:'Downloads & activity'})).toHaveFocus();expect(bridge.tablePrepare).toHaveBeenCalledTimes(1);
});

// D071: Files before and after the build's file catalog is prepared in the background, and its Downloads & activity task.
describe('file catalog prepared after Open', () => {
  const opened = (): WorkspaceSnapshot => ({ ...snapshot(), ...downloadFixture, build: snapshot().build, sourceReference: snapshot().sourceReference } as WorkspaceSnapshot);
  const objects = (): DownloadPage => { const result = opened(); return { productId: result.build.productId, buildIdentity: result.build.id, sourceRegion: 'us', ledgerId: result.download!.ledgerId, assessedAt: result.download!.assessedAt, totalCount: '60', nextCursor: '1', entries: [{ id: '1', encodingKey: '1'.padStart(32, '0'), encodedBytes: '100', verifiedAtAssessment: false }] }; };
  const status = (patch: Partial<FileCatalogStatus>): FileCatalogStatus => ({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us', buildVersion: base.version, state: 'pending', stage: 'queued', completed: null, total: null, attempts: 0, catalog: null, problem: null, waitingFor: null, ...patch });
  const ready = status({ state: 'ready', stage: null, catalog: { generation: '7', preparedAt: '2026-10-21T12:00:00.0000000+00:00', rootFileCount: '1', installFileCount: '1', rootMembershipCount: '2', installMembershipCount: '1', uniqueContentCount: '2', unresolvedMembershipCount: '1', storedMembershipCount: '0' } });
  function start(replies: FileCatalogStatus[], listed: FileCatalogStatus[] = []) {
    let call = 0; const fileCatalog = vi.fn(async () => success(replies[Math.min(call++, replies.length - 1)]));
    const fileCatalogs = vi.fn(async () => success({ catalogs: listed }));
    const fileCatalogSet = vi.fn(async (params: { productId: string; buildIdentity: string; action: 'stop' | 'resume' }) => success(status({ state: params.action === 'stop' ? 'stopped' : 'pending', stage: params.action === 'stop' ? null : 'queued' })));
    const { bridge } = setup([routed()], { fileCatalog, fileCatalogs, fileCatalogSet, capabilities: vi.fn(async () => success(['file-catalog-v1'])) } as Partial<DiscoveryBridge>);
    vi.mocked(bridge.open).mockResolvedValue(success(opened())); vi.mocked(bridge.download).mockResolvedValue(success(objects()));
    return { bridge, fileCatalog, fileCatalogs, fileCatalogSet };
  }
  it('says the file list is being prepared, at which stage, what it waits for and what is missing meanwhile', async () => {
    const { fileCatalog } = start([status({ waitingFor: { productId: 'wow', buildIdentity: 'e'.repeat(64), buildVersion: '12.1.7.70901' } })]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    expect(fileCatalog).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us' });
    // The metric is replaced when the first status arrives, so it is looked up again on every check.
    await waitFor(() => expect(screen.getByText('Catalog memberships').closest('.dh-metric')).toHaveTextContent('Preparing…'));
    expect(screen.getByText('Catalog memberships').closest('.dh-metric')).toHaveTextContent('Waiting for the file list of build 12.1.7.70901 to finish first.');
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const note = screen.getByRole('note', { name: 'Preparing file list…' });
    // The mockup's tinted state box, with the stage on its own dim line.
    expect(note).toHaveClass('dh-state-box'); expect(note).not.toHaveClass('dh-plain');
    expect(within(note).getByText('Waiting for the file list of build 12.1.7.70901 to finish first.')).toHaveClass('dh-dim');
    expect(note).toHaveTextContent(/FileDataID \(ROOT\) or install name appear when the list is sealed/);
    expect(within(note).getByRole('button', { name: `Stop preparing file list for ${base.version}` })).toBeVisible();
  });
  it('writes progress as its own line while writing and stops by name from Files', async () => {
    const { fileCatalogSet } = start([status({ stage: 'writing', completed: '1024', total: '3000' })]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const note = screen.getByRole('note', { name: 'Preparing file list…' });
    expect(within(note).getByText('Writing memberships: 1,024 of 3,000.')).toBeVisible();
    fireEvent.click(within(note).getByRole('button', { name: `Stop preparing file list for ${base.version}` }));
    await waitFor(() => expect(fileCatalogSet).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: base.id, action: 'stop' }));
  });
  it('offers a list that becomes ready in the footer without moving the rows, focus or scroll, then reads that exact generation', async () => {
    const { bridge, fileCatalog } = start([status({ stage: 'reading' }), ready]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const list = screen.getByRole('listbox'); list.focus();
    const scroll = list.closest('.dh-list-scroll') as HTMLElement; scroll.scrollTop = 40;
    const note = screen.getByRole('note', { name: 'Preparing file list…' });
    // At 100% Show file list is in the footer; the note's copy is the 200% form, hidden by CSS.
    const show = await within(document.querySelector<HTMLElement>('.dh-files .dh-pane-foot')!).findByRole('button', { name: 'Show file list' }, { timeout: 3000 });
    expect(within(screen.getByRole('note', { name: 'File list ready' })).getByRole('button', { name: 'Show file list' })).toHaveClass('dh-compact-only');
    expect(show.closest('.dh-pane-foot')).not.toBeNull(); expect(show.closest('.dh-catalog-ready')!.querySelector('[role=status]')).toHaveTextContent('File list ready');
    // The note keeps its box (and height), so nothing above the rows changes size; focus and scroll stay.
    expect(screen.getByRole('note', { name: 'File list ready' })).toBe(note);
    expect(fileCatalog).toHaveBeenCalledTimes(2); expect(list).toHaveFocus(); expect(scroll.scrollTop).toBe(40); expect(bridge.inventoryFiles).not.toHaveBeenCalled();
    vi.mocked(bridge.inventoryFiles).mockResolvedValue(success(filesPage({ generation: '7' })));
    fireEvent.click(show);
    await within(await screen.findByRole('listbox')).findByText('12345');
    expect(bridge.inventoryFiles).toHaveBeenCalledWith({ ...context, generation: '7' });
    await new Promise(done => setTimeout(done, 1200)); expect(fileCatalog).toHaveBeenCalledTimes(2);
  });
  it('opens straight into one row per FileDataID once the catalog is sealed', async () => {
    const { bridge } = start([ready]); vi.mocked(bridge.inventoryFiles).mockResolvedValue(success(filesPage({ generation: '7' })));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    await screen.findByText('Ready to browse'); expect(screen.getByText('Unresolved memberships')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await within(await screen.findByRole('listbox')).findByText('12345');
    expect(bridge.inventoryFiles).toHaveBeenCalledWith({ ...context, generation: '7' }); expect(bridge.download).not.toHaveBeenCalled();
    expect(screen.getByText(/File catalog generation/)).toHaveTextContent('File catalog generation 7 · 3 memberships');
  });
  it('states a failure once, keeps its cause and offers Retry and Reopen', async () => {
    const { fileCatalogSet } = start([status({ state: 'failed', stage: null, attempts: 3, problem: { code: 'STORAGE_UNAVAILABLE', message: 'The file list could not be prepared.', retryable: true, detail: 'IOException: Input/output error' } })]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const note = await screen.findByRole('note', { name: 'File list not prepared' });
    const long = note.querySelector('.dh-note-fit')!;
    expect(long.querySelector('.dh-note-long')).toHaveTextContent('The file list could not be prepared. It failed three times.'); expect(long.querySelector('.dh-note-code')).toHaveTextContent('(STORAGE_UNAVAILABLE)');
    // At 100% (without the 200% short form, which says Retry or reopen) the only retry is the button.
    const wide = note.cloneNode(true) as HTMLElement; wide.querySelectorAll('.dh-note-inline').forEach(n => n.remove());
    expect(wide.textContent!.match(/retr/gi) ?? []).toHaveLength(1); expect(note).toHaveTextContent('Cause: IOException: Input/output error');
    expect(within(note).getByRole('button', { name: 'Reopen this build' })).toBeVisible();
    fireEvent.click(within(note).getByRole('button', { name: `Retry preparing file list for ${base.version}` }));
    await waitFor(() => expect(fileCatalogSet).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: base.id, action: 'resume' }));
  });
  it('says retrying cannot help an unreadable file list and offers no Retry', async () => {
    start([status({ state: 'unsupported', stage: null, problem: { code: 'ROOT_UNSUPPORTED', message: "This build's file list could not be read. Trying again reads the same bytes, so it won't help.", retryable: false, detail: null } })]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const note = await screen.findByRole('note', { name: 'File list not prepared' });
    expect(note).toHaveTextContent("won't help"); expect(within(note).queryByRole('button')).not.toBeInTheDocument();
  });
  it('gives every failure and unsupported code a 200% form with a short message and its next step', async () => {
    // The codes the engine settles a preparation with (FileCatalogWorker): unreadable ones are unsupported, a full disk is
    // final, anything else is failed after three attempts.
    const unreadable = ['METADATA_MISSING', 'METADATA_UNSUPPORTED', 'METADATA_INVALID', 'METADATA_LIMIT', 'METADATA_UNRESOLVED', 'METADATA_SIZE_CONFLICT', 'CONTENT_UNRESOLVED', 'CONTENT_ROOT_UNSUPPORTED', 'RESOLVER_LIMIT',
      'ROOT_UNSUPPORTED', 'ROOT_INVALID', 'ROOT_LIMIT', 'ROOT_SCOPE_INVALID', 'IN_UNSUPPORTED', 'IN_INVALID', 'IN_LIMIT', 'EN_UNSUPPORTED', 'EN_INVALID', 'EN_LIMIT', 'FILE_CATALOG_LIMIT', 'INVENTORY_LIMIT', 'INVENTORY_SIZE_CONFLICT'];
    const problem = (code: string) => ({ code, message: 'Authored.', retryable: false, detail: null });
    for (const code of unreadable) expect(failedShort(status({ state: 'unsupported', stage: null, attempts: 1, problem: problem(code) }), true)).toBe('Can’t be read. Retrying won’t help.');
    expect(failedShort(status({ state: 'failed', stage: null, attempts: 1, problem: problem('STORAGE_FULL') }), true)).toBe('Disk full. Free some space, then retry.');
    for (const code of ['STORAGE_UNAVAILABLE', 'FILE_CATALOG_UNAVAILABLE', 'FILE_CATALOG_CHANGED', 'CONTENT_SOURCE_MISSING', 'ENGINE_UNAVAILABLE']) {
      expect(failedShort(status({ state: 'failed', stage: null, attempts: 3, problem: problem(code) }), true)).toBe('Failed three times. Retry or reopen the build.');
      expect(failedShort(status({ state: 'failed', stage: null, attempts: 3, problem: problem(code) }), false)).toBe('Failed three times. Retry it.');
    }
    // In the note the short form sits beside the long one and the code, which CSS drops first when the note is narrow.
    start([status({ state: 'failed', stage: null, attempts: 1, problem: { code: 'STORAGE_FULL', message: 'There is not enough free disk space to save this build\'s file list.', retryable: false, detail: null } })]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const note = await screen.findByRole('note', { name: 'File list not prepared' });
    const line = note.querySelector('.dh-note-fit')!;
    expect(line.querySelector('.dh-note-long')).toHaveTextContent('There is not enough free disk space to save this build\'s file list. Free some disk space, then retry.');
    expect(line.querySelector('.dh-note-inline')).toHaveTextContent('Disk full. Free some space, then retry.');
    expect(line.querySelector('.dh-note-code')).toHaveTextContent('(STORAGE_FULL)');
  });
  it('folds a failed attempt into the Preparing stage line for 200% and keeps its code in the long form', async () => {
    start([status({ stage: 'queued', attempts: 1, problem: { code: 'CONTENT_SOURCE_MISSING', message: 'The file list could not be prepared.', retryable: true, detail: null } })]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const note = await screen.findByRole('note', { name: 'Preparing file list…' });
    expect(within(note).getByText('Attempt 1 failed, retrying ·', { exact: false })).toHaveClass('dh-note-inline');
    expect(within(note).getByText(/; it is tried again shortly\./).closest('p')).toHaveClass('dh-note-long');
  });
  it('after a Retry status read fails again, a later automatic read never moves focus', async () => {
    const { fileCatalog } = start([]);
    const outage = { ok: false, error: { code: 'ENGINE_UNAVAILABLE', message: 'Authored outage.', retryable: true } } as never;
    fileCatalog.mockResolvedValue(outage);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    const panel = screen.getByRole('heading', { name: 'File catalog' }).closest('section')!;
    const retry = await within(panel).findByRole('button', { name: 'Retry status' }, { timeout: 3000 });
    const before = fileCatalog.mock.calls.length;
    retry.focus(); fireEvent.click(retry);
    await waitFor(() => expect(fileCatalog.mock.calls.length).toBe(before + 1));
    await act(async () => { await new Promise(done => setTimeout(done, 50)); });
    expect(within(panel).getByRole('button', { name: 'Retry status' })).toHaveFocus();
    // The user leaves the panel; the next automatic read succeeds and replaces Retry status. Focus stays where it is.
    (document.activeElement as HTMLElement).blur();
    fileCatalog.mockResolvedValue(success(status({ stage: 'writing', completed: '1', total: '2' })) as never);
    await waitFor(() => expect(within(panel).queryByRole('button', { name: 'Retry status' })).not.toBeInTheDocument(), { timeout: 4000 });
    expect(screen.getByRole('heading', { name: 'File catalog' })).not.toHaveFocus();
    expect(document.activeElement).toBe(document.body);
  });
  it('after Stop fails, a later automatic read never moves focus into the note', async () => {
    let state: FileCatalogStatus = status({ stage: 'reading' });
    const { fileCatalog, fileCatalogSet } = start([]);
    fileCatalog.mockImplementation(async () => success(state));
    fileCatalogSet.mockImplementation(async () => ({ ok: false, error: { code: 'ENGINE_UNAVAILABLE', message: 'Authored outage.', retryable: true } }) as never);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const stop = await screen.findByRole('button', { name: `Stop preparing file list for ${base.version}` }); stop.focus(); fireEvent.click(stop);
    await waitFor(() => expect(fileCatalogSet).toHaveBeenCalled());
    const reads = fileCatalog.mock.calls.length;
    await waitFor(() => expect(fileCatalog.mock.calls.length).toBeGreaterThan(reads));
    await act(async () => { await new Promise(done => setTimeout(done, 50)); });
    (document.activeElement as HTMLElement).blur();
    // Stopped from elsewhere: the next poll replaces Stop with Resume, and focus stays on the document body.
    state = status({ state: 'stopped', stage: null });
    await screen.findByRole('button', { name: `Resume preparing file list for ${base.version}` }, { timeout: 3000 });
    expect(document.activeElement).toBe(document.body);
  });
  it('shows a failed status read with Retry instead of claiming the list is never prepared', async () => {
    const { bridge, fileCatalog } = start([ready]);
    fileCatalog.mockReset(); fileCatalog.mockResolvedValue({ ok: false, error: { code: 'ENGINE_UNAVAILABLE', message: 'Authored outage.', retryable: true } } as never);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const box = await screen.findByRole('status', { name: 'File list status unavailable' });
    expect(box).toHaveTextContent('Authored outage. (ENGINE_UNAVAILABLE)'); expect(screen.queryByText(/doesn.t prepare/)).not.toBeInTheDocument();
    fileCatalog.mockResolvedValue(success(ready) as never); vi.mocked(bridge.inventoryFiles).mockResolvedValue(success(filesPage({ generation: '7' })));
    fireEvent.click(within(box).getByRole('button', { name: 'Retry status' }));
    await within(document.querySelector<HTMLElement>('.dh-files .dh-pane-foot')!).findByRole('button', { name: 'Show file list' });
  });
  it('names a failed task with a short stage and states its message and code once', async () => {
    const failed = status({ state: 'failed', stage: null, attempts: 3, problem: { code: 'STORAGE_UNAVAILABLE', message: 'The file list could not be prepared.', retryable: true, detail: 'IOException: Input/output error' } });
    const { fileCatalogs } = start([failed], [failed]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    await waitFor(() => expect(fileCatalogs).toHaveBeenCalled());
    fireEvent.click(await screen.findByRole('button', { name: /File list · ROOT and install · needs attention/ }));
    const task = (await screen.findByRole('heading', { name: 'File list · ROOT and install' })).closest('article')!;
    expect(task).toHaveTextContent('File list needs attention'); expect(task.textContent!.match(/could not be prepared/g)).toHaveLength(1);
    expect(within(task).getByRole('button', { name: `Retry preparing file list for ${base.version}` })).toBeVisible();
  });
  it('moves focus to the button that replaces Stop, Resume or Retry status instead of dropping it', async () => {
    let state: FileCatalogStatus = status({ stage: 'reading' });
    const { fileCatalog, fileCatalogSet } = start([]);
    fileCatalog.mockImplementation(async () => success(state));
    fileCatalogSet.mockImplementation(async params => { state = status({ state: params.action === 'stop' ? 'stopped' : 'pending', stage: params.action === 'stop' ? null : 'queued' }); return success(state); });
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const stop = await screen.findByRole('button', { name: `Stop preparing file list for ${base.version}` }); stop.focus(); fireEvent.click(stop);
    await waitFor(() => expect(screen.getByRole('button', { name: `Resume preparing file list for ${base.version}` })).toHaveFocus());
    fireEvent.click(screen.getByRole('button', { name: `Resume preparing file list for ${base.version}` }));
    await waitFor(() => expect(screen.getByRole('button', { name: `Stop preparing file list for ${base.version}` })).toHaveFocus());
    expect(document.activeElement).not.toBe(document.body);
  });
  it('keeps focus in the note when Stop goes away because the list turned ready', async () => {
    let state: FileCatalogStatus = status({ stage: 'reading' });
    const { fileCatalog } = start([]);
    fileCatalog.mockImplementation(async () => success(state));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    (await screen.findByRole('button', { name: `Stop preparing file list for ${base.version}` })).focus();
    state = ready;
    const note = await screen.findByRole('note', { name: 'File list ready' }, { timeout: 3000 });
    // jsdom has no layout, so the note's own Show file list (the 200% form) counts as shown and takes focus.
    await waitFor(() => expect(within(note).getByRole('button', { name: 'Show file list' })).toHaveFocus());
    expect(document.activeElement).not.toBe(document.body);
  });
  it('marks an earlier status as not confirmed when later reads fail, with Retry status', async () => {
    const { fileCatalog } = start([status({ stage: 'writing', completed: '10', total: '20' })]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('Object 1');
    const note = await screen.findByRole('note', { name: 'Preparing file list…' });
    fileCatalog.mockResolvedValue({ ok: false, error: { code: 'ENGINE_UNAVAILABLE', message: 'Authored outage.', retryable: true } } as never);
    await within(note).findByText(/Status not confirmed: Authored outage\. \(ENGINE_UNAVAILABLE\)/, undefined, { timeout: 3000 });
    expect(within(note).getByText('Writing memberships: 10 of 20.')).toBeVisible();
    fileCatalog.mockResolvedValue(success(status({ stage: 'writing', completed: '15', total: '20' })) as never);
    // The 200% form repeats Retry status beside the note's actions; CSS shows one of the two.
    fireEvent.click(within(note).getAllByRole('button', { name: 'Retry status' })[0]);
    await within(note).findByText('Writing memberships: 15 of 20.');
    expect(within(note).queryByText(/Status not confirmed/)).not.toBeInTheDocument();
  });
  it('says Checking… in the build readiness until the first status read answers', async () => {
    const { fileCatalog } = start([]);
    let answer!: (value: unknown) => void;
    fileCatalog.mockImplementation(() => new Promise(done => { answer = done; }) as never);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    const row = await screen.findByText('File list');
    await waitFor(() => expect(row.nextElementSibling).toHaveTextContent('Checking…'));
    await act(async () => answer(success(status({ stage: 'reading' }))));
    await waitFor(() => expect(screen.getByText('File list').nextElementSibling).toHaveTextContent('Preparing…'));
  });
  it('on Home, Retry status hands focus to the File catalog heading once the read succeeds', async () => {
    const { fileCatalog } = start([]);
    fileCatalog.mockResolvedValue({ ok: false, error: { code: 'ENGINE_UNAVAILABLE', message: 'Authored outage.', retryable: true } } as never);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    const panel = screen.getByRole('heading', { name: 'File catalog' }).closest('section')!;
    let retry = await within(panel).findByRole('button', { name: 'Retry status' }, { timeout: 3000 });
    fileCatalog.mockResolvedValue(success(status({ stage: 'writing', completed: '1', total: '2' })) as never);
    retry.focus(); fireEvent.click(retry);
    await waitFor(() => expect(within(panel).queryByRole('button', { name: 'Retry status' })).not.toBeInTheDocument());
    expect(screen.getByRole('heading', { name: 'File catalog' })).toHaveFocus();
    // The same after a later read fails and the last status is marked not confirmed.
    fileCatalog.mockResolvedValue({ ok: false, error: { code: 'ENGINE_UNAVAILABLE', message: 'Authored outage.', retryable: true } } as never);
    retry = await within(panel).findByRole('button', { name: 'Retry status' }, { timeout: 3000 });
    fileCatalog.mockResolvedValue(success(status({ stage: 'writing', completed: '2', total: '2' })) as never);
    retry.focus(); fireEvent.click(retry);
    await waitFor(() => expect(within(panel).queryByRole('button', { name: 'Retry status' })).not.toBeInTheDocument());
    expect(screen.getByRole('heading', { name: 'File catalog' })).toHaveFocus(); expect(document.activeElement).not.toBe(document.body);
  });
  it('lists one Downloads & activity task per build with its stage, a named Stop and Resume', async () => {
    const writing = status({ stage: 'writing', completed: '10', total: '20' });
    const { fileCatalogSet, fileCatalogs } = start([writing], [writing]);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    await waitFor(() => expect(fileCatalogs).toHaveBeenCalled());
    fireEvent.click(await screen.findByRole('button', { name: /Downloads & activity/ }));
    const task = (await screen.findByRole('heading', { name: 'File list · ROOT and install' })).closest('article')!;
    // The stage names the step; the counts are the measurement, shown once.
    expect(task).toHaveTextContent('Writing memberships'); expect(task.textContent!.match(/10 of 20/g)).toHaveLength(1); expect(task).toHaveTextContent(base.version);
    fireEvent.click(within(task).getByRole('button', { name: `Stop preparing file list for ${base.version}` }));
    await waitFor(() => expect(fileCatalogSet).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: base.id, action: 'stop' }));
  });
});
