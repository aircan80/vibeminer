import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import type { DownloadCheckResult, DownloadCheckSummary, DownloadObjectEntry, DownloadPage, EngineProgress, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import workspace from '../../../contracts/examples/download-workspace.json';
import { DownloadObjects, localState } from '../src/renderer/DownloadObjects';
import { useDownloadCheck, type DownloadCheckController, type DownloadCheckOperation } from '../src/renderer/useDownloadCheck';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { DownloadReadParams } from '../src/shared/download';

// D093: Files lists the measured ledger once ROOT is measured, switches to it in place, and checks the files Open never assessed.
const w = workspace as unknown as WorkspaceSnapshot;
const manifestId = w.download!.ledgerId, measuredId = 'e'.repeat(64);
const key = (n: number) => n.toString(16).padStart(32, '0');
// The manifest holds the even keys 2..240; the measured ledger adds every key that is 5 more than a multiple of 10.
const manifestKeys = Array.from({ length: 120 }, (_, i) => key((i + 1) * 2));
const outsideKeys = Array.from({ length: 24 }, (_, i) => key(i * 10 + 5));
const measuredKeys = [...manifestKeys, ...outsideKeys].sort();
const summary = { ...w.download!, objectCount: String(manifestKeys.length) };
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

function engine(checked: Record<string, DownloadObjectEntry['check']> | null = null) {
  const state = { checked, checkedAt: checked ? '2026-10-19T08:30:00.0000000+00:00' : null as string | null };
  // The counts the engine saved with the latest Check, as a page returns them.
  const last = (): DownloadCheckSummary | null => {
    if (!state.checkedAt || !state.checked) return null;
    const values = Object.values(state.checked), n = (v: string) => String(values.filter(x => x === v).length);
    return { checkedAt: state.checkedAt, checkedObjects: String(outsideKeys.length), verifiedObjects: n('verified'), notStoredObjects: String(outsideKeys.length - values.length + values.filter(v => v === 'notStored').length), missingObjects: n('missing'), corruptObjects: n('corrupt') };
  };
  const reads: DownloadReadParams[] = [];
  const progress = new Set<(value: EngineProgress) => void>();
  const page = (p: DownloadReadParams): DownloadPage => {
    const measured = p.ledgerId === measuredId, keys = measured ? measuredKeys : manifestKeys;
    const start = p.from !== undefined ? keys.findIndex(k => k >= p.from!) : Number(p.after ?? 0);
    const begin = start < 0 ? keys.length : start, rows = keys.slice(begin, begin + p.limit);
    const entries = rows.map((k, i) => {
      const open = !outsideKeys.includes(k);
      return { id: String(begin + i + 1), encodingKey: k, encodedBytes: '100', verifiedAtAssessment: open && k === manifestKeys[0], checkedAtOpen: open, check: open || !state.checked ? null : state.checked[k] ?? 'notStored' };
    });
    return { productId: p.productId, buildIdentity: p.buildIdentity, sourceRegion: p.sourceRegion, ledgerId: p.ledgerId, assessedAt: summary.assessedAt, entries,
      totalCount: String(keys.length), nextCursor: begin + rows.length < keys.length && rows.length ? entries.at(-1)!.id : null,
      manifestLedgerId: manifestId, lastCheck: measured ? last() : null };
  };
  let finish: ((reply: BridgeReply<DownloadCheckResult>) => void) | null = null;
  const bridge = {
    download: vi.fn(async (p: DownloadReadParams) => { reads.push(p); return { ok: true, value: page(p) }; }),
    downloadCheck: vi.fn((_p: unknown, _id: string) => new Promise<BridgeReply<DownloadCheckResult>>(resolve => { finish = resolve; })),
    cancel: vi.fn(async () => ({ ok: true, value: { requested: true } })),
    onProgress: vi.fn((listener: (value: EngineProgress) => void) => { progress.add(listener); return () => progress.delete(listener); }),
  } as unknown as DiscoveryBridge;
  const report = (requestId: string, completed: number, total: number) => act(() => { for (const listener of progress) listener({ protocolVersion: 1, type: 'progress', requestId, stage: 'Checking files not checked at Open', region: 'us', completed, total, unit: 'objects', sequence: completed + 1 }); });
  const complete = async (found: Record<string, DownloadObjectEntry['check']>) => {
    state.checked = found; state.checkedAt = '2026-10-20T09:00:00.0000000+00:00';
    const values = Object.values(found), n = (s: string) => String(values.filter(v => v === s).length);
    await act(async () => finish!({ ok: true, value: { productId: w.build.productId, buildIdentity: w.build.id, sourceRegion: w.sourceReference.region, ledgerId: measuredId, manifestLedgerId: manifestId, checkedAt: state.checkedAt!,
      checkedObjects: String(outsideKeys.length), verifiedObjects: n('verified'), notStoredObjects: String(outsideKeys.length - values.length + values.filter(v => v === 'notStored').length), missingObjects: n('missing'), corruptObjects: n('corrupt') } }));
  };
  const fail = async (code: string, message: string) => act(async () => finish!({ ok: false, error: { code, message, retryable: true } }));
  return { bridge, reads, report, complete, fail };
}
const option = (encodingKey: string) => { const found = screen.getAllByRole('option').find(row => row.querySelector('.dh-mono')!.textContent === encodingKey); if (!found) throw new Error(`No row for ${encodingKey}`); return found; };
const rowNames = () => screen.getAllByRole('option').map(option => option.querySelector('.dh-mono')!.textContent);
// The App owns the Check (D093); here one controller stands in for it.
function Files({ bridge, measured, checkAvailable }: { bridge: DiscoveryBridge; measured: Parameters<typeof DownloadObjects>[0]['measured']; checkAvailable: boolean }) {
  const checker = useDownloadCheck(bridge);
  return <DownloadObjects snapshot={w} summary={summary} measured={measured} bridge={bridge} active onReopen={() => {}} checkAvailable={checkAvailable} checker={checker}/>;
}
const view = (bridge: DiscoveryBridge, measured: Parameters<typeof DownloadObjects>[0]['measured'], checkAvailable = true) => <Files bridge={bridge} measured={measured} checkAvailable={checkAvailable}/>;

describe('the measured download ledger in Files', () => {
  it('waits for the build summary, then reads the measured ledger and marks files not checked at Open apart', async () => {
    const { bridge, reads } = engine();
    const { rerender } = render(view(bridge, 'pending'));
    expect(screen.getByText('Reading the saved download ledger…', { selector: 'strong' })).toBeInTheDocument();
    expect(bridge.download).not.toHaveBeenCalled();
    rerender(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await screen.findByText('Object 1');
    expect(reads[0]).toMatchObject({ ledgerId: measuredId, limit: 50 });
    const outside = screen.getAllByRole('option').filter(option => outsideKeys.includes(option.querySelector('.dh-mono')!.textContent!));
    expect(outside.length).toBeGreaterThan(0);
    for (const option of outside) expect(option).toHaveTextContent('Not checked at Open');
    expect(option(manifestKeys[0])).toHaveTextContent('Verified locally');
    expect(option(manifestKeys[1])).toHaveTextContent('Not verified locally');
    expect(screen.getByRole('group', { name: '24 files not checked at Open' })).toBeInTheDocument();
    expect(screen.getByText('Download manifest and every file ROOT lists · unique encoded objects · all published variants')).toBeInTheDocument();
  });

  it('switches ledgers in place when the measurement lands mid-view, keeping the page and the picked row', async () => {
    Element.prototype.scrollIntoView = vi.fn();
    const { bridge, reads } = engine();
    const { rerender } = render(view(bridge, null));
    await screen.findByText('Object 1');
    fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Object 51');
    const firstKey = manifestKeys[50], pickedKey = manifestKeys[53];
    fireEvent.click(option(pickedKey));
    expect(screen.getByRole('heading', { name: 'Encoded object 54' })).toBeInTheDocument();
    rerender(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await waitFor(() => expect(option(pickedKey)).toHaveAttribute('aria-selected', 'true'));
    expect(reads.at(-1)).toEqual({ productId: w.build.productId, buildIdentity: w.build.id, sourceRegion: w.sourceReference.region, ledgerId: measuredId, limit: 50, from: firstKey });
    // The page starts at the same first row, now with its measured ordinal, and every row is from the measured ledger.
    const index = measuredKeys.indexOf(firstKey);
    expect(rowNames()).toEqual(measuredKeys.slice(index, index + 50));
    expect(screen.getByRole('heading', { name: `Encoded object ${measuredKeys.indexOf(pickedKey) + 1}` })).toBeInTheDocument();
    expect(screen.getByText(`50 encoded objects on this page · ${measuredKeys.length} total · objects ${index + 1}–${index + 50}`)).toBeInTheDocument();
    // Paging goes on from there in the measured ledger.
    fireEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    await waitFor(() => expect(reads.at(-1)).toMatchObject({ ledgerId: measuredId, ...(index - 50 > 0 ? { after: String(index - 50) } : {}) }));
  });

  it('reads a picked row that is off the page again from the measured ledger', async () => {
    Element.prototype.scrollIntoView = vi.fn();
    const { bridge, reads } = engine();
    const { rerender } = render(view(bridge, null));
    await screen.findByText('Object 1');
    const pickedKey = manifestKeys[48];
    fireEvent.click(option(pickedKey));
    fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Object 51');
    expect(screen.getByText('Outside the current page · same saved assessment')).toBeInTheDocument();
    rerender(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await waitFor(() => expect(reads.at(-1)).toMatchObject({ ledgerId: measuredId, limit: 1, from: pickedKey }));
    await waitFor(() => expect(screen.getByRole('heading', { name: `Encoded object ${measuredKeys.indexOf(pickedKey) + 1}` })).toBeInTheDocument());
  });

  it('keeps the manifest rows and offers Retry when the measured ledger cannot be read', async () => {
    const { bridge } = engine();
    const { rerender } = render(view(bridge, null));
    await screen.findByText('Object 1');
    const before = rowNames();
    vi.mocked(bridge.download).mockResolvedValueOnce({ ok: false, error: { code: 'DOWNLOAD_NOT_READY', message: 'Not admitted.', retryable: true } });
    rerender(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await screen.findByRole('alert');
    expect(rowNames()).toEqual(before);
    expect(bridge.download).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole('button', { name: 'Retry page' }));
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    expect(rowNames()[0]).toBe(measuredKeys[0]);
  });

  it('checks the files Open never assessed with progress, then shows each outcome on the rows', async () => {
    const { bridge, report, complete } = engine();
    render(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await screen.findByText('Object 1');
    const button = screen.getByRole('button', { name: 'Check 24 files' });
    button.focus(); fireEvent.click(button);
    expect(bridge.downloadCheck).toHaveBeenCalledWith({ productId: w.build.productId, buildIdentity: w.build.id, sourceRegion: w.sourceReference.region, ledgerId: measuredId }, expect.stringMatching(/^download_check_/));
    const id = vi.mocked(bridge.downloadCheck!).mock.calls[0][1];
    // One button changes between Check and Stop, so focus stays on it.
    expect(button).toHaveAccessibleName('Stop checking'); expect(button).toHaveFocus();
    report(id, 7, 24);
    expect(screen.getByText('Checking 7 of 24 files…')).toBeInTheDocument();
    report('another-request', 20, 24);
    expect(screen.getByText('Checking 7 of 24 files…')).toBeInTheDocument();
    const found = { [outsideKeys[0]]: 'verified' as const, [outsideKeys[1]]: 'missing' as const, [outsideKeys[2]]: 'corrupt' as const };
    await complete(found);
    await waitFor(() => expect(option(outsideKeys[0])).toHaveTextContent('Verified at check'));
    expect(option(outsideKeys[1])).toHaveTextContent('Missing at check');
    expect(option(outsideKeys[2])).toHaveTextContent('Corrupt at check');
    expect(option(outsideKeys[3])).toHaveTextContent('Not stored at check');
    expect(within(screen.getByRole('group', { name: '24 files not checked at Open' })).getByRole('status')).toHaveTextContent(/Checked .*: 1 verified · 21 not stored · 1 missing · 1 corrupt\./);
    expect(button).toHaveAccessibleName('Check 24 files again'); expect(button).toHaveFocus();
    // The engine's own Open assessment is untouched.
    expect(option(manifestKeys[0])).toHaveTextContent('Verified locally');
  });

  it('reads the rows again when a Check finishes while a page read is running, so they never keep the old outcome', async () => {
    const { bridge, reads, complete } = engine();
    render(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await screen.findByText('Object 1');
    fireEvent.click(screen.getByRole('button', { name: 'Check 24 files' }));
    const original = vi.mocked(bridge.download).getMockImplementation()!;
    let release!: () => void;
    // The next page is answered from before the Check finished, and arrives after it.
    vi.mocked(bridge.download).mockImplementationOnce(async p => { const value = await original(p); await new Promise<void>(done => { release = done; }); return value; });
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(release).toBeTypeOf('function'));
    const onPage = measuredKeys.slice(50, 100).find(k => outsideKeys.includes(k))!;
    await complete({ [onPage]: 'verified' });
    await act(async () => release());
    await waitFor(() => expect(option(onPage)).toHaveTextContent('Verified at check'));
    expect(reads.at(-1)).toMatchObject({ ledgerId: measuredId, after: '50' });
  });

  it('shows the saved counts of the last Check on return, and stops a Check on request with nothing new recorded', async () => {
    const { bridge, fail } = engine({ [outsideKeys[0]]: 'missing' });
    render(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await screen.findByText('Object 1');
    expect(within(screen.getByRole('group', { name: '24 files not checked at Open' })).getByRole('status')).toHaveTextContent(/^Last checked .*: 0 verified · 23 not stored · 1 missing · 0 corrupt\.$/);
    fireEvent.click(screen.getByRole('button', { name: 'Check 24 files again' }));
    const id = vi.mocked(bridge.downloadCheck!).mock.calls[0][1];
    fireEvent.click(screen.getByRole('button', { name: 'Stop checking' }));
    expect(bridge.cancel).toHaveBeenCalledWith(id);
    expect(screen.getByText('Stopping the check…')).toBeInTheDocument();
    await fail('CANCELED', 'Check stopped.');
    expect(screen.getByText('Check stopped. Nothing new was recorded; the earlier check stays.')).toBeInTheDocument();
    expect(option(outsideKeys[0])).toHaveTextContent('Missing at check');
  });

  it('shows a failed Check with its code and leaves Check offered; an engine without the capability offers none', async () => {
    const { bridge, fail } = engine();
    const { unmount } = render(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await screen.findByText('Object 1');
    fireEvent.click(screen.getByRole('button', { name: 'Check 24 files' }));
    await fail('ENGINE_FAILURE', 'The engine could not complete this operation.');
    expect(screen.getByText('The check failed: The engine could not complete this operation. (ENGINE_FAILURE)')).toHaveClass('dh-bad-t');
    // One still finishing elsewhere is not a failure.
    fireEvent.click(screen.getByRole('button', { name: 'Check 24 files' }));
    await fail('DOWNLOAD_CHECK_BUSY', 'Another check of stored files is running.');
    expect(screen.getByText('Another check is still finishing; try again shortly.')).not.toHaveClass('dh-bad-t');
    expect(screen.queryByText(/The check failed/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Check 24 files' })).toBeInTheDocument();
    unmount();
    render(view(engine().bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }, false));
    await screen.findByText('Object 1');
    expect(screen.getByRole('group', { name: '24 files not checked at Open' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Check / })).toBeNull();
  });

  it('turns the note mint with Progress is in Downloads & activity while a Check runs', async () => {
    const { bridge, report } = engine();
    render(view(bridge, { ledgerId: measuredId, objectCount: String(measuredKeys.length) }));
    await screen.findByText('Object 1');
    const note = screen.getByRole('group', { name: '24 files not checked at Open' });
    expect(note).toHaveClass('dh-plain');
    fireEvent.click(screen.getByRole('button', { name: 'Check 24 files' }));
    report(vi.mocked(bridge.downloadCheck!).mock.calls[0][1], 3, 24);
    expect(note).not.toHaveClass('dh-plain');
    expect(note).toHaveTextContent('Progress is in Downloads & activity.');
  });

  it('names another build whose Check runs, offers no Check, and never refreshes its rows from that Check', async () => {
    const { bridge, reads } = engine();
    // Same measured ledger on purpose: only the build tells the two apart.
    const intent = { productId: 'wow', buildIdentity: 'b'.repeat(64), sourceRegion: 'us', ledgerId: measuredId, buildVersion: '12.1.5.99999', buildConfig: 'f'.repeat(32), files: '24' };
    const stub = (operation: DownloadCheckOperation) => ({ available: true, operation, start: vi.fn(), stop: vi.fn(), dismiss: vi.fn() }) as unknown as DownloadCheckController;
    const measured = { ledgerId: measuredId, objectCount: String(measuredKeys.length) };
    const running = stub({ id: 'download_check_other', intent, status: 'pending', progress: null, result: null, error: null });
    const { rerender } = render(<DownloadObjects snapshot={w} summary={summary} measured={measured} bridge={bridge} active onReopen={() => {}} checkAvailable checker={running}/>);
    await screen.findByText('Object 1');
    const note = screen.getByRole('group', { name: '24 files not checked at Open' });
    expect(within(note).getByRole('status')).toHaveTextContent('A check of wow 12.1.5.99999 is running; one runs at a time.');
    // A build of the same version is told apart by its region, when that differs, and its build configuration.
    rerender(<DownloadObjects snapshot={w} summary={summary} measured={measured} bridge={bridge} active onReopen={() => {}} checkAvailable checker={stub({ id: 'download_check_other', intent: { ...intent, buildVersion: w.build.version, sourceRegion: 'eu' }, status: 'pending', progress: null, result: null, error: null })}/>);
    expect(within(note).getByRole('status')).toHaveTextContent(`A check of wow ${w.build.version} (EU, build config ffffffff) is running; one runs at a time.`);
    rerender(<DownloadObjects snapshot={w} summary={summary} measured={measured} bridge={bridge} active onReopen={() => {}} checkAvailable checker={running}/>);
    expect(within(note).queryByRole('button', { name: /^(Check|Stop)/ })).toBeNull();
    const before = reads.length;
    const result = { productId: 'wow', buildIdentity: intent.buildIdentity, sourceRegion: 'us', ledgerId: measuredId, manifestLedgerId: manifestId, checkedAt: '2026-10-21T10:00:00.0000000+00:00', checkedObjects: '24', verifiedObjects: '24', notStoredObjects: '0', missingObjects: '0', corruptObjects: '0' };
    rerender(<DownloadObjects snapshot={w} summary={summary} measured={measured} bridge={bridge} active onReopen={() => {}} checkAvailable checker={stub({ id: 'download_check_other', intent, status: 'succeeded', progress: null, result, error: null })}/>);
    await act(async () => { await Promise.resolve(); });
    expect(reads).toHaveLength(before);
    expect(within(note).getByRole('status')).not.toHaveTextContent(/Checked|24 verified/);
    expect(option(outsideKeys[0])).toHaveTextContent('Not checked at Open');
    expect(within(note).getByRole('button', { name: 'Check 24 files' })).toBeInTheDocument();
  });

  it('keeps a Check running when Files is left, and shows its outcome on return', async () => {
    const { bridge, complete } = engine();
    function Owner({ show }: { show: boolean }) {
      const checker = useDownloadCheck(bridge);
      return show ? <DownloadObjects snapshot={w} summary={summary} measured={{ ledgerId: measuredId, objectCount: String(measuredKeys.length) }} bridge={bridge} active onReopen={() => {}} checkAvailable checker={checker}/> : <p>Tables</p>;
    }
    const { rerender } = render(<Owner show/>);
    await screen.findByText('Object 1');
    fireEvent.click(screen.getByRole('button', { name: 'Check 24 files' }));
    rerender(<Owner show={false}/>);
    expect(bridge.cancel).not.toHaveBeenCalled();
    await complete({ [outsideKeys[0]]: 'verified' });
    rerender(<Owner show/>);
    await screen.findByText('Object 1');
    expect(within(screen.getByRole('group', { name: '24 files not checked at Open' })).getByRole('status')).toHaveTextContent(/^Checked .*: 1 verified · 23 not stored · 0 missing · 0 corrupt\.$/);
    expect(option(outsideKeys[0])).toHaveTextContent('Verified at check');
  });

  it('names each local state apart, with the colour on the label only', () => {
    const base = { id: '1', encodingKey: key(1), encodedBytes: '1', verifiedAtAssessment: false, checkedAtOpen: false };
    const labels = [{ ...base, verifiedAtAssessment: true, checkedAtOpen: true, check: null }, { ...base, checkedAtOpen: true, check: null }, { ...base, check: null },
      { ...base, check: 'verified' as const }, { ...base, check: 'notStored' as const }, { ...base, check: 'missing' as const }, { ...base, check: 'corrupt' as const }].map(entry => localState(entry).label);
    expect(labels).toEqual(['Verified locally', 'Not verified locally', 'Not checked at Open', 'Verified at check', 'Not stored at check', 'Missing at check', 'Corrupt at check']);
    expect(localState({ ...base, check: null })).toEqual({ label: 'Not checked at Open', tone: 'dh-warn-t', explanation: ': Open assessed the download manifest only' });
  });
});
