import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Build, DiscoveryPage, EngineProgress, WorkspaceSnapshot, InventoryPage, DownloadPage, TableDefinitionSnapshot, TableDataSnapshot } from '../../../contracts/generated/discovery';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import { App } from '../src/renderer/App';
import pageFixture from '../../../contracts/examples/discovery-page.json';
import workspaceFixture from '../../../contracts/examples/inventory-workspace.json';
import definitionsFixture from '../../../tests/desktop/fixtures/table-definitions.json';
import downloadFixture from '../../../tests/desktop/fixtures/download-workspace.json';
import configurationFixture from '../../../contracts/examples/workspace-snapshot.json';
import inventoryPageFixture from '../../../contracts/examples/inventory-page.json';
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
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
function setup(builds: Build[] = [routed()]) {
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
    cancel: vi.fn(async () => success({ requested: true })),
    onProgress: vi.fn(listener => { progress.add(listener); return () => { progress.delete(listener); }; })
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
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); expect(bridge.inventory).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('FileDataID 12345');
    expect(bridge.inventory).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us', generation: '1', limit: 50 });
    expect(screen.getByText(/Name not known/)).toBeVisible(); expect(within(screen.getByRole('complementary', { name: 'Membership 1' })).getByText(inventoryPageFixture.entries[0].encodingKey)).toBeVisible(); expect(screen.getByText(/1 memberships on this page · 6 total/)).toBeVisible();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Download all', hidden: true })).not.toBeInTheDocument(); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });
  it('keeps page controls focused and current rows readable during paged local reads', async () => {
    const { bridge } = setup(); const later = deferred<BridgeReply<InventoryPage>>();
    vi.mocked(bridge.inventory).mockResolvedValueOnce(success(inventoryPageFixture as InventoryPage)).mockReturnValueOnce(later.promise);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('FileDataID 12345');
    const next = screen.getByRole('button', { name: 'Next page' }); next.focus(); fireEvent.click(next);
    expect(next).toHaveFocus(); expect(next).toHaveAttribute('aria-disabled', 'true'); expect(screen.getByText('FileDataID 12345')).toBeVisible();
    await act(async () => later.resolve(success({ ...inventoryPageFixture, entries: [{ ...inventoryPageFixture.entries[0], id: '2', family: 'install', name: 'authored/install.bin', fileDataId: null, retained: false }], nextCursor: '2' } as InventoryPage)));
    expect(screen.getByText('authored/install.bin')).toBeVisible(); expect(next).toHaveFocus(); expect(bridge.inventory).toHaveBeenLastCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us', generation: '1', limit: 50, after: '1' });
    fireEvent.click(screen.getByRole('button', { name: 'Previous page' })); await screen.findByText('FileDataID 12345'); expect(bridge.inventory).toHaveBeenLastCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us', generation: '1', limit: 50 });
  });
  it('preserves good rows and the exact failed cursor when retrying a failed page', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventory).mockResolvedValueOnce(success(inventoryPageFixture as InventoryPage)).mockResolvedValueOnce({ ok: false, error: { code: 'BUSY', message: 'Authored catalog busy.', retryable: true } }).mockResolvedValueOnce(success({ ...inventoryPageFixture, entries: [{ ...inventoryPageFixture.entries[0], id: '2' }], nextCursor: '2' } as InventoryPage));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('FileDataID 12345'); fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await screen.findByText('BUSY'); expect(screen.getByText('FileDataID 12345')).toBeVisible(); fireEvent.click(screen.getByRole('button', { name: 'Retry page' }));
    await waitFor(() => expect(bridge.inventory).toHaveBeenCalledTimes(3)); expect(vi.mocked(bridge.inventory).mock.calls[1][0]).toEqual(vi.mocked(bridge.inventory).mock.calls[2][0]);
  });
  it('refuses mismatched prepared generations instead of substituting a newer catalog', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventory).mockResolvedValue(success({ ...inventoryPageFixture, generation: '2' } as InventoryPage));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('INVENTORY_CONTEXT_MISMATCH');
    expect(screen.queryByText('FileDataID 12345')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Reopen this build' })).toBeEnabled(); expect(screen.queryByRole('button', { name: 'Retry page' })).not.toBeInTheDocument();
  });
  it('shows exact membership keys, decoded length and raw flags without inventing ROOT filenames', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventory).mockResolvedValue(success({ ...inventoryPageFixture, entries: [{ ...inventoryPageFixture.entries[0], name: 'must-not-be-a-root-filename', nameHash: '18446744073709551615' }] } as InventoryPage));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('FileDataID 12345'); expect(screen.queryByText('must-not-be-a-root-filename')).not.toBeInTheDocument();
    const list = screen.getByRole('listbox'); list.focus(); fireEvent.click(screen.getByRole('option')); const inspector = screen.getByRole('complementary', { name: 'Membership 1' });
    expect(within(inspector).getByText('18446744073709551615')).toBeVisible(); expect(within(inspector).getByText('300 bytes')).toBeVisible(); expect(within(inspector).getByText(inventoryPageFixture.entries[0].encodingKey)).toBeVisible(); expect(within(inspector).getByText(inventoryPageFixture.entries[0].contentKey)).toBeVisible(); expect(within(inspector).getByText('Raw locale flags')).toBeVisible();
    expect(list).toHaveFocus(); expect(screen.getByRole('option')).toHaveAttribute('aria-selected', 'true');
  });
  it('returns Files to the Home origin and retains its local page without another read', async () => {
    const { bridge } = setup(); await begin(); await screen.findByRole('heading', { name: 'Build overview' }); const browse = screen.getByRole('button', { name: 'Browse in Files' }); browse.focus(); fireEvent.click(browse); await screen.findByText('FileDataID 12345');
    fireEvent.click(screen.getByRole('button', { name: /Return to build overview/ })); await waitFor(() => expect(browse).toHaveFocus()); fireEvent.click(screen.getByRole('button', { name: 'Files' })); expect(screen.getByText('FileDataID 12345')).toBeVisible(); expect(bridge.inventory).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); await screen.findByRole('button', { name: 'Open build' }); expect(bridge.readAll).toHaveBeenCalledTimes(1); expect(bridge.refreshAll).not.toHaveBeenCalled();
  });
  it('discards a late catalog result after leaving the exact Home context', async () => {
    const { bridge } = setup(); const pending = deferred<BridgeReply<InventoryPage>>(); vi.mocked(bridge.inventory).mockReturnValue(pending.promise);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await waitFor(() => expect(bridge.inventory).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: /Choose build/ })); await act(async () => pending.resolve(success(inventoryPageFixture as InventoryPage)));
    expect(screen.queryByRole('heading', { name: 'Files' })).not.toBeInTheDocument(); expect(screen.queryByText('FileDataID 12345')).not.toBeInTheDocument(); expect(screen.getByRole('combobox', { name: 'Product' })).toHaveValue('all');
  });

  it('keeps the selected membership in the inspector when a page replaces its original row', async () => {
    const { bridge } = setup(); const later = deferred<BridgeReply<InventoryPage>>(); vi.mocked(bridge.inventory).mockResolvedValueOnce(success(inventoryPageFixture as InventoryPage)).mockReturnValueOnce(later.promise);
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('FileDataID 12345');
    fireEvent.click(screen.getByRole('option')); fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await act(async () => later.resolve(success({ ...inventoryPageFixture, entries: [{ ...inventoryPageFixture.entries[0], id: '2' }], nextCursor: '2' } as InventoryPage)));
    expect(screen.getByRole('complementary', { name: 'Membership 1' })).toHaveTextContent('outside the current page'); expect(screen.getByRole('option')).toHaveAttribute('aria-selected', 'false');
  });

  it('advances bottom pagination through the same bounded exact-generation query without duplicate reads', async () => {
    const { bridge } = setup(); vi.mocked(bridge.inventory).mockResolvedValueOnce(success(inventoryPageFixture as InventoryPage)).mockResolvedValueOnce(success({ ...inventoryPageFixture, entries: [{ ...inventoryPageFixture.entries[0], id: '2' }], nextCursor: '2' } as InventoryPage));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); await screen.findByText('FileDataID 12345');
    const bottom = screen.getByRole('button', { name: 'Next page' }); bottom.focus(); fireEvent.click(bottom);
    await screen.findByText(/rows 2–2/); expect(bridge.inventory).toHaveBeenCalledTimes(2); expect(bridge.inventory).toHaveBeenLastCalledWith({ productId: 'wow', buildIdentity: base.id, sourceRegion: 'us', generation: '1', limit: 50, after: '1' }); expect(bottom).toHaveFocus();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeVisible();
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
    expect(screen.getByText('Unique encoded objects')).toBeVisible(); expect(screen.getByText('Published download content', { exact: true })).toBeVisible(); expect(screen.getByText('Research file catalog')).toBeVisible(); expect(screen.getAllByText('Not prepared').length).toBeGreaterThan(0);
    expect(within(screen.getByText('Catalog memberships').closest('.dh-metric')!).getByText('Not prepared')).toBeVisible(); expect(screen.queryByText('ROOT/IN decoded bytes')).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: /Files.*Later/ })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Download all' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Scope and measurement details' })); const dialog = screen.getByRole('dialog'); expect(within(dialog).getByText('6,000 bytes')).toBeVisible(); expect(within(dialog).getByText(downloadFixture.download.ledgerId)).toBeVisible(); expect(within(dialog).getByText(/Exact full encoding keys/)).toBeVisible(); fireEvent(dialog, new Event('cancel', { cancelable: true }));
    const metadata = screen.getByRole('button', { name: 'View metadata details' }); metadata.focus(); fireEvent.click(metadata); expect(document.querySelectorAll('.configuration-receipt')).toHaveLength(3); expect(within(screen.getByRole('dialog')).getByText('download · Verified')).toBeVisible(); fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true })); expect(metadata).toHaveFocus(); expect(bridge.inventory).not.toHaveBeenCalled();
  });
  it('admits configurations with a typed unknown download size without inventing zero or readiness', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success({ ...downloadSnapshot(), download: null, downloadProblem: { code: 'DOWNLOAD_UNDECLARED', message: 'This build does not declare a download manifest.' }, metadata: null, metadataState: 'configurationsVerified', retentionState: 'unknown' })); await begin(); await screen.findByRole('heading', { name: 'Build overview' });
    expect(screen.getByText('Published download size is unknown')).toBeVisible(); expect(screen.getByText('DOWNLOAD_UNDECLARED')).toBeVisible(); expect(screen.getByText(/This build does not declare a download manifest\./)).toBeVisible(); expect(screen.queryByRole('button', { name: /Download objects.*Unavailable/ })).not.toBeInTheDocument(); expect(screen.queryByText('0 bytes')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Recheck this build' })).toBeEnabled(); fireEvent.click(screen.getByRole('button', { name: 'Browse in Files' })); expect(screen.getByRole('heading', { name: 'Files' })).toHaveFocus(); expect(screen.getByText('File catalog not prepared', { selector: 'strong' })).toBeVisible(); expect(screen.queryByRole('listbox')).not.toBeInTheDocument(); expect(bridge.download).not.toHaveBeenCalled();
  });
  it('pages exact download objects locally, preserves old rows on errors and fences a newer assessment', async () => {
    const { bridge } = setup(); vi.mocked(bridge.open).mockResolvedValue(success(downloadSnapshot())); const later = deferred<BridgeReply<DownloadPage>>(); vi.mocked(bridge.download).mockResolvedValueOnce(success(downloadPage())).mockReturnValueOnce(later.promise).mockResolvedValueOnce(success({ ...downloadPage('1'), assessedAt: '2026-10-19T12:00:01Z' }));
    await begin(); await screen.findByRole('heading', { name: 'Build overview' }); const browse = screen.getByRole('button', { name: 'Browse in Files' }); browse.focus(); fireEvent.click(browse); await screen.findByText('Object 1'); expect(screen.getByRole('heading', { name: 'Files' })).toHaveFocus(); expect(screen.getAllByText('00000000000000000000000000000001').length).toBeGreaterThan(0); expect(document.querySelector('.dh-files')).not.toHaveTextContent(/FileDataID/);
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
