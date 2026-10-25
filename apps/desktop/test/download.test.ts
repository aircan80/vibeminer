import { describe, expect, it } from 'vitest';
import type { DownloadPage, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import fixture from '../../../tests/desktop/fixtures/download-workspace.json';
import { downloadAccountingValid, downloadCheckMatches, downloadPageMatches, isDownloadWorkspace, validDownloadCheck, validDownloadRead } from '../src/shared/download';
const workspace = fixture as unknown as WorkspaceSnapshot;
const summary = workspace.download!;
const params = { productId: workspace.build.productId, buildIdentity: workspace.build.id, sourceRegion: workspace.sourceReference.region, ledgerId: summary.ledgerId, limit: 50 };
const page: DownloadPage = { ...params, assessedAt: summary.assessedAt, entries: [{ id: '1', encodingKey: '1'.repeat(32), encodedBytes: '100', verifiedAtAssessment: true, checkedAtOpen: true, check: null }], totalCount: '60', nextCursor: '1', manifestLedgerId: summary.ledgerId, lastCheck: null };
describe('published download capability boundaries', () => {
  it('admits download measurement independently of a research catalog and permits a typed unavailable capability', () => {
    expect(isDownloadWorkspace(workspace)).toBe(true);
    expect(isDownloadWorkspace({ ...workspace, metadata: null, metadataState: 'configurationsVerified', retentionState: 'unknown', download: null, downloadProblem: { code: 'DOWNLOAD_UNDECLARED', message: 'No download manifest is declared.' } })).toBe(true);
  });
  it.each(['sum', 'unsafeNumber', 'scope', 'negative', 'zeroObjects', 'date'])('rejects inconsistent %s measurement', change => {
    const value = structuredClone(summary);
    if (change === 'sum') value.remainingEncodedBytes = '5001';
    if (change === 'unsafeNumber') Object.assign(value, { totalEncodedBytes: '9007199254740992', verifiedEncodedBytes: '9007199254740992', remainingEncodedBytes: '1' });
    if (change === 'scope') (value as any).scope = 'allFiles';
    if (change === 'negative') value.verifiedEncodedBytes = '-1';
    if (change === 'zeroObjects') value.objectCount = '0';
    if (change === 'date') value.assessedAt = 'yesterday';
    expect(downloadAccountingValid(value)).toBe(false);
  });
  it.each(['missingReason', 'bothBranches', 'metadataRoles', 'duplicateRoles', 'inventoryReady'])('refuses contradictory capability %s', change => {
    const value = structuredClone(workspace);
    if (change === 'missingReason') { value.download = null; value.downloadProblem = null; }
    if (change === 'bothBranches') value.downloadProblem = { code: 'UNAVAILABLE', message: 'Unavailable' };
    if (change === 'metadataRoles') value.metadata![0].role = 'root';
    if (change === 'duplicateRoles') value.metadata = [value.metadata![0], value.metadata![0]];
    if (change === 'inventoryReady') value.inventoryState = 'ready';
    expect(isDownloadWorkspace(value)).toBe(false);
  });
  it('pins pages to the exact source, ledger, assessment and unique full EKeys', () => {
    expect(downloadPageMatches(page, params, summary)).toBe(true);
    expect(downloadPageMatches({ ...page, assessedAt: '2026-10-19T12:00:01Z' }, params, summary)).toBe(false);
    expect(downloadPageMatches({ ...page, ledgerId: 'b'.repeat(64) }, params, summary)).toBe(false);
    expect(downloadPageMatches({ ...page, entries: [...page.entries, { ...page.entries[0], id: '2' }] }, params, summary)).toBe(false);
  });
  it.each([{ limit: 101 }, { after: '-1' }, { after: '01' }, { ledgerId: 'short' }, { search: 'table.db2' }, { sourceRegion: 'US' }, { from: 'A'.repeat(32) }, { from: '1'.repeat(31) }, { from: '1'.repeat(32), after: '1' }])('refuses broad or malformed local query %#', change => {
    expect(validDownloadRead({ ...params, ...change })).toBe(false);
  });
  // D093: a measured ledger's page lists files not checked at Open, and only those carry a Check outcome.
  const measuredParams = { ...params, ledgerId: 'e'.repeat(64), from: '2'.repeat(32) };
  const rows = [{ id: '7', encodingKey: '2'.repeat(32), encodedBytes: '100', verifiedAtAssessment: true, checkedAtOpen: true, check: null }, { id: '8', encodingKey: '3'.repeat(32), encodedBytes: '200', verifiedAtAssessment: false, checkedAtOpen: false, check: 'missing' as const }];
  const measured: DownloadPage = { ...page, ledgerId: measuredParams.ledgerId, manifestLedgerId: summary.ledgerId, lastCheck: { checkedAt: '2026-10-19T08:30:00.0000000+00:00', checkedObjects: '1', verifiedObjects: '0', notStoredObjects: '0', missingObjects: '1', corruptObjects: '0' }, entries: rows, totalCount: '61', nextCursor: '8' };
  it('accepts a measured page read from a key, starting at any ordinal', () => {
    expect(validDownloadRead(measuredParams)).toBe(true);
    expect(downloadPageMatches(measured, measuredParams, { assessedAt: summary.assessedAt, objectCount: '61' })).toBe(true);
    expect(downloadPageMatches({ ...measured, lastCheck: null, entries: [rows[0], { ...rows[1], check: null }] }, measuredParams)).toBe(true);
    expect(downloadPageMatches({ ...measured, entries: [], nextCursor: null }, measuredParams)).toBe(true);
  });
  it.each([
    ['an outcome before any Check', { lastCheck: null }],
    ['Check counts that do not add up', { lastCheck: { ...measured.lastCheck!, missingObjects: '2' } }],
    ['a Check without its time', { lastCheck: { ...measured.lastCheck!, checkedAt: 'later' } }],
    ['a measured ledger that is its own manifest', { manifestLedgerId: 'e'.repeat(64) }],
    ['rows out of key order', { entries: [rows[1], { ...rows[0], id: '9' }] }],
    ['a first row before the key it was read from', { entries: [{ ...rows[0], encodingKey: '1'.repeat(32) }, rows[1]] }],
    ['a file not checked at Open that was verified at assessment', { entries: [rows[0], { ...rows[1], verifiedAtAssessment: true }] }],
    ['an outcome on a file Open assessed', { entries: [{ ...rows[0], check: 'verified' as const }, rows[1]] }],
    ['a Check that skipped a file', { entries: [rows[0], { ...rows[1], check: null }] }],
    ['an unknown outcome', { entries: [rows[0], { ...rows[1], check: 'stale' as never }] }],
  ])('refuses a measured page with %s', (_name, change) => {
    expect(downloadPageMatches({ ...measured, ...change } as DownloadPage, measuredParams)).toBe(false);
  });
  it('refuses files not checked at Open on the manifest\'s own pages', () => {
    expect(downloadPageMatches({ ...page, entries: [{ ...page.entries[0], verifiedAtAssessment: false, checkedAtOpen: false }] }, params)).toBe(false);
    expect(downloadPageMatches({ ...page, lastCheck: { checkedAt: '2026-10-19T08:30:00.0000000+00:00', checkedObjects: '1', verifiedObjects: '0', notStoredObjects: '0', missingObjects: '1', corruptObjects: '0' } }, params)).toBe(false);
  });
  it('accepts only a Check whose outcomes add up, for another ledger than its manifest', () => {
    const check = { productId: params.productId, buildIdentity: params.buildIdentity, sourceRegion: params.sourceRegion, ledgerId: 'e'.repeat(64) };
    const result = { ...check, manifestLedgerId: summary.ledgerId, checkedAt: '2026-10-19T08:30:00.0000000+00:00', checkedObjects: '3', verifiedObjects: '1', notStoredObjects: '1', missingObjects: '0', corruptObjects: '1' };
    expect(validDownloadCheck(check)).toBe(true);
    expect(validDownloadCheck({ ...check, limit: 1 })).toBe(false);
    expect(downloadCheckMatches(result, check)).toBe(true);
    expect(downloadCheckMatches({ ...result, corruptObjects: '0' }, check)).toBe(false);
    expect(downloadCheckMatches({ ...result, manifestLedgerId: check.ledgerId }, check)).toBe(false);
    expect(downloadCheckMatches({ ...result, ledgerId: 'f'.repeat(64) }, check)).toBe(false);
  });
});
