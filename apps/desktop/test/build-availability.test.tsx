import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../src/renderer/App';
import { availabilityLines } from '../src/renderer/BuildAvailability';
import { validAvailabilityCheck } from '../src/shared/availability';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { Build, BuildAvailability, CommunityBuildsStatus, DiscoveryCatalogPage, EngineProgress } from '../../../contracts/generated/discovery';
import example from '../../../contracts/examples/discovery-catalog-page.json';
import { testProducts } from './catalog-page';

// #9 / D106: community builds from wago.tools and Check availability, against an authored engine.
const authored = example as unknown as DiscoveryCatalogPage;
const official = authored.builds[0], community = authored.builds.find(build => build.community)!;
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const unavailable = async () => ({ ok: false as const, error: { code: 'NOT_TESTED', message: 'Outside this test.', retryable: false } });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
const pageOf = (builds: Build[], communityBuilds: CommunityBuildsStatus = authored.communityBuilds): DiscoveryCatalogPage =>
  ({ ...authored, products: authored.products.filter(status => testProducts.some(product => product.id === status.productId)), builds: builds.filter(build => testProducts.some(product => product.id === build.productId)), nextCursor: null, refreshProblems: [], communityBuilds });
const result = (patch: Partial<BuildAvailability> = {}): BuildAvailability => ({ ...community.availabilityCheck!, ...patch });

function bridgeMock(page: DiscoveryCatalogPage, extra: Partial<DiscoveryBridge> = {}) {
  const listeners = new Set<(progress: EngineProgress) => void>();
  const bridge: DiscoveryBridge = {
    itemsData: vi.fn(unavailable), itemsPrepare: vi.fn(unavailable), itemsPage: vi.fn(unavailable), itemInspect: vi.fn(unavailable),
    products: vi.fn(async () => success({ products: testProducts })),
    read: vi.fn(unavailable), refresh: vi.fn(unavailable),
    readAll: vi.fn(async params => success({ ...page, productId: params.productId ?? null, filter: params.filter ?? 'all' })),
    refreshAll: vi.fn(async () => success(page)),
    tableData: vi.fn(async () => success({ snapshot: null })), tablePrepare: vi.fn(unavailable), tableRows: vi.fn(unavailable), tablesCatalog: vi.fn(unavailable), tableDefinition: vi.fn(unavailable),
    download: vi.fn(unavailable), inventory: vi.fn(unavailable), inventoryFiles: vi.fn(unavailable), inventoryFile: vi.fn(unavailable), open: vi.fn(unavailable),
    cancel: vi.fn(async () => success({ requested: true })), onProgress: vi.fn(listener => { listeners.add(listener); return () => listeners.delete(listener); }),
    capabilities: vi.fn(async () => success(['retention-v1', 'build-availability-v1'])), retentionList: vi.fn(async () => success({ jobs: [] })), retentionRead: vi.fn(unavailable), retentionSet: vi.fn(unavailable),
    ...extra,
  } as DiscoveryBridge;
  const emit = (progress: Omit<EngineProgress, 'protocolVersion' | 'type' | 'region'>) => { for (const listener of listeners) listener({ protocolVersion: 1, type: 'progress', region: null, ...progress }); };
  return { bridge, emit };
}
beforeEach(() => { localStorage.clear(); });
afterEach(cleanup);
async function ready(bridge: DiscoveryBridge) { render(<App bridge={bridge}/>); await screen.findByRole('listbox', { name: 'Builds' }); }
const row = (build: Build) => document.getElementById(`build-option-${build.id}`)!;
const inspector = () => document.getElementById('inspector')!;

describe('community builds', () => {
  it('get the Community badge, name their source, and say why they can’t be opened', async () => {
    const { bridge } = bridgeMock(pageOf([official, community]));
    await ready(bridge);
    expect(within(row(community)).getByText('Community')).toHaveClass('dv-pill', 'community');
    expect(within(row(official)).queryByText('Community')).not.toBeInTheDocument();
    fireEvent.click(row(community));
    expect(inspector()).toHaveTextContent('Community build');
    expect(inspector()).toHaveTextContent('Community build history from wago.tools (not a Blizzard observation)');
    expect(inspector()).toHaveTextContent('Never seen by VibeMiner on Blizzard’s version service');
    expect(screen.getByRole('button', { name: 'Open build' })).toBeDisabled();
    expect(inspector()).toHaveTextContent('Opening community builds isn’t supported yet');
    fireEvent.click(row(official));
    expect(inspector()).not.toHaveTextContent('wago.tools');
  });

  it('says when the list couldn’t be updated, what is kept and when it is asked again', async () => {
    const problem = { code: 'COMMUNITY_BUILDS_OFFLINE', message: 'VibeMiner couldn’t reach wago.tools to get the community build list.', retryable: true };
    const { bridge } = bridgeMock(pageOf([official, community], { ...authored.communityBuilds, state: 'failed', problem }));
    await ready(bridge);
    const notice = screen.getByText('Community build history not updated').closest('.dv-notice')!;
    expect(notice).toHaveTextContent('couldn’t reach wago.tools');
    expect(notice).toHaveTextContent('are kept');
    expect(notice).toHaveTextContent('Refresh builds asks wago.tools again after');
    expect(notice).toHaveTextContent('(COMMUNITY_BUILDS_OFFLINE)');
  });
});

describe('Check availability', () => {
  it('shows the saved check with its date, and words each outcome', () => {
    expect(availabilityLines(official.availabilityCheck!, official).detail).toBe('Build config verified, CDN config verified; all 1,412 archive indexes present; archives not checked.');
    expect(availabilityLines(community.availabilityCheck!, community).title).toMatch(/^Partly available · checked /);
    expect(availabilityLines(community.availabilityCheck!, community).detail).toBe('Build config verified, CDN config missing.');
    const gone = availabilityLines(result({ state: 'gone', buildConfigState: 'missing', cdnConfigState: 'missing' }), community);
    expect(gone.title).toMatch(/^Gone/); expect(gone.detail).toContain('missing on every host asked (us.cdn.blizzard.com, level3.ssl.blizzard.com)');
    const unknown = availabilityLines(result({ state: 'unknown', scope: 'indexes', cdnConfigState: 'verified', indexes: { total: 10, present: 8, missing: 0, unknown: 2 }, problem: { code: 'AVAILABILITY_UNANSWERED', message: 'Blizzard’s CDN didn’t answer every request.', retryable: true } }), community);
    expect(unknown.detail).toBe('Build config verified, CDN config verified; 2 of 10 archive indexes not answered. Blizzard’s CDN didn’t answer every request. (AVAILABILITY_UNANSWERED)');
    expect(availabilityLines(result({ cdnConfig: '9'.repeat(32) }), community).detail).toContain('Checked with CDN config 9999999999…9999.');
    expect(validAvailabilityCheck({ productId: 'wow', buildIdentity: community.id, archives: true })).toBe(true);
    for (const bad of [null, {}, { productId: 'wow', buildIdentity: community.id }, { productId: 'wow', buildIdentity: community.id, archives: 'yes' }, { productId: 'wow', buildIdentity: 'x', archives: false }, { productId: 'wow', buildIdentity: community.id, archives: false, host: 'a' }])
      expect(validAvailabilityCheck(bad)).toBe(false);
  });

  it('runs one check with progress, keeps focus on the one button, and shows the new result', async () => {
    const reply = deferred<BridgeReply<BuildAvailability>>();
    const checkAvailability = vi.fn(() => reply.promise);
    const { bridge, emit } = bridgeMock(pageOf([official, community]), { checkAvailability });
    await ready(bridge);
    fireEvent.click(row(community));
    const dd = inspector().querySelector('.dv-availability')!;
    expect(dd).toHaveTextContent('Partly available · checked');
    fireEvent.click(within(dd as HTMLElement).getByRole('checkbox', { name: 'Also every archive' }));
    const button = within(dd as HTMLElement).getByRole('button', { name: 'Check again' });
    button.focus(); fireEvent.click(button);
    expect(checkAvailability).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: community.id, archives: true }, expect.any(String));
    const requestId = (checkAvailability.mock.calls[0] as unknown[])[1] as string;
    expect(button).toHaveTextContent('Stop check'); expect(button).toHaveFocus();
    act(() => emit({ requestId, sequence: 2, stage: 'Checking archive indexes', completed: 350, total: 1412, unit: 'files' }));
    expect(within(dd as HTMLElement).getByRole('status')).toHaveTextContent('Checking archive indexes · 350 of 1,412…');
    // Another build can't start a second check meanwhile, and says why.
    fireEvent.click(row(official));
    const other = inspector().querySelector('.dv-availability')! as HTMLElement;
    expect(within(other).getByRole('button', { name: 'Check again' })).toBeDisabled();
    expect(other).toHaveTextContent(`wow ${community.version} is being checked`);
    fireEvent.click(row(community));
    await act(async () => { reply.resolve(success(result({ state: 'available', scope: 'archives', checkedAt: '2026-10-29T10:00:00.0000000+00:00', cdnConfigState: 'verified', indexes: { total: 3, present: 3, missing: 0, unknown: 0 }, archives: { total: 3, present: 3, missing: 0, unknown: 0 } }))); });
    const done = inspector().querySelector('.dv-availability')! as HTMLElement;
    expect(done).toHaveTextContent('Available · checked');
    expect(done).toHaveTextContent('all 3 archive indexes present; all 3 archives present.');
    expect(within(done).getByRole('button', { name: 'Check again' })).toBeEnabled();
  });

  it('stops a check only when asked, and says why a check couldn’t run', async () => {
    const replies = [deferred<BridgeReply<BuildAvailability>>(), deferred<BridgeReply<BuildAvailability>>()];
    const checkAvailability = vi.fn().mockReturnValueOnce(replies[0].promise).mockReturnValueOnce(replies[1].promise);
    const { bridge } = bridgeMock(pageOf([official, community]), { checkAvailability });
    await ready(bridge);
    fireEvent.click(row(official));
    const dd = () => inspector().querySelector('.dv-availability')! as HTMLElement;
    fireEvent.click(within(dd()).getByRole('button', { name: 'Check again' }));
    const requestId = (checkAvailability.mock.calls[0] as unknown[])[1] as string;
    fireEvent.click(within(dd()).getByRole('button', { name: 'Stop check' }));
    expect(bridge.cancel).toHaveBeenCalledWith(requestId);
    expect(within(dd()).getByRole('button', { name: 'Stopping check…' })).toHaveAttribute('aria-disabled', 'true');
    await act(async () => { replies[0].resolve({ ok: false, error: { code: 'CANCELED', message: 'Availability check stopped. Nothing new was saved; an earlier check stays as it was.', retryable: true } }); });
    expect(dd()).toHaveTextContent('Available · checked');
    // Stopped on request is a calm state, not an error (#9 usability review).
    expect(within(dd()).queryByRole('alert')).not.toBeInTheDocument();
    expect(dd()).toHaveTextContent('Check stopped. Nothing new was saved; the result above is from the earlier check.');
    fireEvent.click(within(dd()).getByRole('button', { name: 'Check again' }));
    expect(dd()).not.toHaveTextContent('Check stopped.');
    await act(async () => { replies[1].resolve({ ok: false, error: { code: 'AVAILABILITY_NO_ROUTE', message: 'VibeMiner doesn’t know Blizzard’s CDN hosts yet. Refresh builds once, then check again.', retryable: true } }); });
    expect(within(dd()).getByRole('alert')).toHaveTextContent('(AVAILABILITY_NO_ROUTE)');
  });

  it('offers no check on an engine without build-availability-v1', async () => {
    const { bridge } = bridgeMock(pageOf([official, { ...community, availabilityCheck: null }]), { checkAvailability: vi.fn(), capabilities: vi.fn(async () => success(['retention-v1'])) });
    await ready(bridge);
    fireEvent.click(row(community));
    await waitFor(() => expect(bridge.capabilities).toHaveBeenCalled());
    const dd = inspector().querySelector('.dv-availability')! as HTMLElement;
    expect(dd).toHaveTextContent('Not checked');
    expect(within(dd).queryByRole('button')).not.toBeInTheDocument();
  });
});

describe('the Community build history switch', () => {
  it('reads Discovery again at once when it changes, keeping the selection', async () => {
    const { bridge } = bridgeMock(pageOf([official, community]));
    await ready(bridge);
    fireEvent.click(row(official));
    const before = vi.mocked(bridge.readAll).mock.calls.length;
    vi.mocked(bridge.readAll).mockImplementation(async params => success({ ...pageOf([official], { ...authored.communityBuilds, enabled: false, state: 'off' }), productId: params.productId ?? null, filter: params.filter ?? 'all' }));
    act(() => { window.dispatchEvent(new CustomEvent('vibeminer-settings-changed', { detail: { name: 'fetchTableDefinitions' } })); });
    expect(vi.mocked(bridge.readAll).mock.calls.length).toBe(before);
    act(() => { window.dispatchEvent(new CustomEvent('vibeminer-settings-changed', { detail: { name: 'communityBuildHistory' } })); });
    await waitFor(() => expect(document.getElementById(`build-option-${community.id}`)).toBeNull());
    expect(row(official)).toHaveAttribute('aria-selected', 'true');
  });
});

describe('bridge validation', () => {
  it('accepts community builds only in All while the list is on, and never as Blizzard observations', async () => {
    const { catalogPageMatches } = await import('../src/shared/bridge');
    const page = pageOf([official, community]);
    expect(catalogPageMatches(page, { filter: 'all' })).toBe(true);
    expect(catalogPageMatches({ ...page, filter: 'advertised', builds: [community] }, { filter: 'advertised' })).toBe(false);
    expect(catalogPageMatches({ ...page, communityBuilds: { ...page.communityBuilds, enabled: false, state: 'off' } }, { filter: 'all' })).toBe(false);
    expect(catalogPageMatches({ ...page, builds: [official, { ...community, regions: ['us'] }] }, { filter: 'all' })).toBe(false);
    expect(catalogPageMatches({ ...page, builds: [official, { ...community, sourceReceipts: official.sourceReceipts }] }, { filter: 'all' })).toBe(false);
  });
});
