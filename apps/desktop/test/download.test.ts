import { describe, expect, it } from 'vitest';
import type { DownloadPage, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import fixture from '../../../tests/desktop/fixtures/download-workspace.json';
import { downloadAccountingValid, downloadPageMatches, isDownloadWorkspace, validDownloadRead } from '../src/shared/download';
const workspace = fixture as unknown as WorkspaceSnapshot;
const summary = workspace.download!;
const params = { productId: workspace.build.productId, buildIdentity: workspace.build.id, sourceRegion: workspace.sourceReference.region, ledgerId: summary.ledgerId, limit: 50 };
const page: DownloadPage = { ...params, assessedAt: summary.assessedAt, entries: [{ id: '1', encodingKey: '1'.repeat(32), encodedBytes: '100', verifiedAtAssessment: true }], totalCount: '60', nextCursor: '1' };
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
  it.each([{ limit: 101 }, { after: '-1' }, { after: '01' }, { ledgerId: 'short' }, { search: 'table.db2' }, { sourceRegion: 'US' }])('refuses broad or malformed local query %#', change => {
    expect(validDownloadRead({ ...params, ...change })).toBe(false);
  });
});
