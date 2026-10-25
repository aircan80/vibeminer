import type { DownloadCheckParams, DownloadCheckResult, DownloadCheckSummary, DownloadPage, DownloadSummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { OpenParams } from './bridge';

/** `after` is an ordinal cursor; `from` starts the page at the first member whose encoding key is at least it (D093). */
export type DownloadReadParams = OpenParams & { ledgerId: string; limit: number; after?: string; from?: string };
const unsigned = /^(0|[1-9][0-9]{0,18})$/;
const positive = /^[1-9][0-9]{0,18}$/;
const hash = /^[0-9a-f]{64}$/;
const ekey = /^[0-9a-f]{32}$/;
const timestamp = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value));
const context = (p: OpenParams) => typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId) && typeof p.buildIdentity === 'string' && hash.test(p.buildIdentity) && typeof p.sourceRegion === 'string' && /^[a-z]{2,8}$/.test(p.sourceRegion);
export function validDownloadRead(value: unknown): value is DownloadReadParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as DownloadReadParams;
  return context(p) && typeof p.ledgerId === 'string' && hash.test(p.ledgerId) && Number.isInteger(p.limit) && p.limit >= 1 && p.limit <= 100 && (p.after === undefined || typeof p.after === 'string' && unsigned.test(p.after)) && (p.from === undefined || p.after === undefined && typeof p.from === 'string' && ekey.test(p.from)) && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion', 'ledgerId', 'limit', 'after', 'from'].includes(key));
}
export function downloadAccountingValid(summary: DownloadSummary): boolean {
  const counters = ['objectCount', 'totalEncodedBytes', 'verifiedEncodedBytes', 'remainingEncodedBytes', 'metadataEncodedBytes'] as const;
  return summary.scope === 'downloadManifestAllVariants' && typeof summary.ledgerId === 'string' && hash.test(summary.ledgerId) && timestamp(summary.assessedAt) && counters.every(key => typeof summary[key] === 'string' && unsigned.test(summary[key])) && BigInt(summary.verifiedEncodedBytes) + BigInt(summary.remainingEncodedBytes) === BigInt(summary.totalEncodedBytes) && (summary.objectCount !== '0' || summary.totalEncodedBytes === '0');
}
export function isDownloadWorkspace(value: WorkspaceSnapshot): boolean {
  if (value.preparationProfile !== 'home-download-v1' || value.readiness !== 'partial' || value.inventoryState !== 'notPrepared' || value.inventory) return false;
  if (!value.download) return value.metadataState === 'configurationsVerified' && value.retentionState === 'unknown' && !value.metadata && !!value.downloadProblem?.code?.trim() && !!value.downloadProblem.message?.trim();
  if (!downloadAccountingValid(value.download) || value.downloadProblem || value.metadataState !== 'verified' || value.retentionState !== 'measured' || !value.metadata || value.metadata.length < 1 || value.metadata.length > 2) return false;
  const roles = new Set(value.metadata.map(receipt => receipt.role));
  return roles.size === value.metadata.length && roles.has('download') && [...roles].every(role => role === 'download' || role === 'encoding');
}
/**
 * A page answers exactly the read that was asked. Rows are contiguous ordinals in strictly increasing key order; a page read
 * `from` a key starts at the first member at or after it. Only a measured ledger (one that is not its own manifest) lists
 * files not checked at Open, and only those carry a Check outcome, all of them once a Check has completed (D093).
 */
export function downloadPageMatches(value: DownloadPage, params: DownloadReadParams, assessment?: Pick<DownloadSummary, 'assessedAt' | 'objectCount'>): boolean {
  if (value.productId !== params.productId || value.buildIdentity !== params.buildIdentity || value.sourceRegion !== params.sourceRegion || value.ledgerId !== params.ledgerId || !timestamp(value.assessedAt) || !unsigned.test(value.totalCount) || value.entries.length > params.limit || BigInt(value.totalCount) < BigInt(value.entries.length) || assessment && (value.assessedAt !== assessment.assessedAt || value.totalCount !== assessment.objectCount)) return false;
  if (typeof value.manifestLedgerId !== 'string' || !hash.test(value.manifestLedgerId) || value.lastCheck !== null && !checkSummaryValid(value.lastCheck)) return false;
  const measured = value.manifestLedgerId !== value.ledgerId;
  if (!measured && value.lastCheck !== null) return false;
  let cursor = params.from === undefined ? BigInt(params.after || '0') : value.entries.length ? BigInt(value.entries[0].id) - 1n : 0n;
  let previous = params.from ?? '';
  const keys = new Set<string>();
  for (const [index, entry] of value.entries.entries()) {
    if (!positive.test(entry.id) || BigInt(entry.id) !== cursor + 1n || BigInt(entry.id) > BigInt(value.totalCount) || typeof entry.encodingKey !== 'string' || !ekey.test(entry.encodingKey) || keys.has(entry.encodingKey) || !unsigned.test(entry.encodedBytes) || typeof entry.verifiedAtAssessment !== 'boolean' || typeof entry.checkedAtOpen !== 'boolean') return false;
    if (index === 0 ? entry.encodingKey < previous : entry.encodingKey <= previous) return false;
    if (entry.checkedAtOpen ? entry.check !== null : !measured || entry.verifiedAtAssessment || (value.lastCheck === null) !== (entry.check === null) || entry.check !== null && !['verified', 'notStored', 'missing', 'corrupt'].includes(entry.check)) return false;
    cursor = BigInt(entry.id); keys.add(entry.encodingKey); previous = entry.encodingKey;
  }
  if (value.entries.length === 0) return (params.from !== undefined || cursor >= BigInt(value.totalCount)) && value.nextCursor === null;
  return cursor < BigInt(value.totalCount) ? value.nextCursor === value.entries.at(-1)!.id : value.nextCursor === null;
}
/** A completed Check's time and counts: the four outcomes add up to every file it checked (D093). */
export function checkSummaryValid(value: DownloadCheckSummary): boolean {
  const counts = [value.checkedObjects, value.verifiedObjects, value.notStoredObjects, value.missingObjects, value.corruptObjects];
  if (!timestamp(value.checkedAt) || !counts.every(count => typeof count === 'string' && unsigned.test(count))) return false;
  return BigInt(value.verifiedObjects) + BigInt(value.notStoredObjects) + BigInt(value.missingObjects) + BigInt(value.corruptObjects) === BigInt(value.checkedObjects);
}
export function validDownloadCheck(value: unknown): value is DownloadCheckParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as DownloadCheckParams;
  return context(p) && typeof p.ledgerId === 'string' && hash.test(p.ledgerId) && Object.keys(p).length === 4;
}
/** A completed Check of exactly the requested measured ledger, whose four outcomes add up to every file checked. */
export function downloadCheckMatches(value: DownloadCheckResult, params: DownloadCheckParams): boolean {
  if (value.productId !== params.productId || value.buildIdentity !== params.buildIdentity || value.sourceRegion !== params.sourceRegion || value.ledgerId !== params.ledgerId || !hash.test(value.manifestLedgerId) || value.manifestLedgerId === value.ledgerId) return false;
  return checkSummaryValid(value);
}
