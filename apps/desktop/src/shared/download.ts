import type { DownloadPage, DownloadSummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { OpenParams } from './bridge';

export type DownloadReadParams = OpenParams & { ledgerId: string; limit: number; after?: string };
const unsigned = /^(0|[1-9][0-9]{0,18})$/;
const positive = /^[1-9][0-9]{0,18}$/;
const hash = /^[0-9a-f]{64}$/;
const timestamp = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value));
export function validDownloadRead(value: unknown): value is DownloadReadParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as DownloadReadParams;
  return typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId) && typeof p.buildIdentity === 'string' && hash.test(p.buildIdentity) && typeof p.sourceRegion === 'string' && /^[a-z]{2,8}$/.test(p.sourceRegion) && typeof p.ledgerId === 'string' && hash.test(p.ledgerId) && Number.isInteger(p.limit) && p.limit >= 1 && p.limit <= 100 && (p.after === undefined || typeof p.after === 'string' && unsigned.test(p.after)) && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion', 'ledgerId', 'limit', 'after'].includes(key));
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
export function downloadPageMatches(value: DownloadPage, params: DownloadReadParams, assessment?: Pick<DownloadSummary, 'assessedAt' | 'objectCount'>): boolean {
  if (value.productId !== params.productId || value.buildIdentity !== params.buildIdentity || value.sourceRegion !== params.sourceRegion || value.ledgerId !== params.ledgerId || !timestamp(value.assessedAt) || !unsigned.test(value.totalCount) || value.entries.length > params.limit || BigInt(value.totalCount) < BigInt(value.entries.length) || assessment && (value.assessedAt !== assessment.assessedAt || value.totalCount !== assessment.objectCount)) return false;
  let cursor = BigInt(params.after || '0');
  const keys = new Set<string>();
  for (const entry of value.entries) {
    if (!positive.test(entry.id) || BigInt(entry.id) !== cursor + 1n || BigInt(entry.id) > BigInt(value.totalCount) || !/^[0-9a-f]{32}$/.test(entry.encodingKey) || keys.has(entry.encodingKey) || !unsigned.test(entry.encodedBytes) || typeof entry.verifiedAtAssessment !== 'boolean') return false;
    cursor = BigInt(entry.id); keys.add(entry.encodingKey);
  }
  if (value.entries.length === 0) return cursor >= BigInt(value.totalCount) && value.nextCursor === null;
  return cursor < BigInt(value.totalCount) ? value.nextCursor === value.entries.at(-1)!.id : value.nextCursor === null;
}
