import type { InventoryPage, InventorySummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
import type { OpenParams } from './bridge';
export type InventoryReadParams = OpenParams & { generation: string; limit: number; after?: string };
const unsigned = /^(0|[1-9][0-9]{0,18})$/;
const positive = /^[1-9][0-9]{0,18}$/;
export function validInventoryRead(value: unknown): value is InventoryReadParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as InventoryReadParams;
  return typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId) && typeof p.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(p.buildIdentity) && typeof p.sourceRegion === 'string' && /^[a-z]{2,8}$/.test(p.sourceRegion) && typeof p.generation === 'string' && positive.test(p.generation) && Number.isInteger(p.limit) && p.limit >= 1 && p.limit <= 100 && (p.after === undefined || typeof p.after === 'string' && unsigned.test(p.after)) && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion', 'generation', 'limit', 'after'].includes(key));
}
export function inventoryAccountingValid(inventory: InventorySummary): boolean {
  const counters = ['membershipCount', 'rootMembershipCount', 'uniqueContentCount', 'downloadObjectCount', 'logicalDecodedBytes', 'totalEncodedBytes', 'verifiedEncodedBytes', 'remainingEncodedBytes', 'sharedVerifiedEncodedBytes', 'metadataEncodedBytes'] as const;
  if (counters.some(key => typeof inventory[key] !== 'string' || !unsigned.test(inventory[key])) || !positive.test(inventory.generation) || inventory.scope !== 'allPublishedVariants') return false;
  const total = BigInt(inventory.totalEncodedBytes), verified = BigInt(inventory.verifiedEncodedBytes);
  return verified + BigInt(inventory.remainingEncodedBytes) === total && BigInt(inventory.sharedVerifiedEncodedBytes) <= verified && BigInt(inventory.rootMembershipCount) <= BigInt(inventory.membershipCount) && BigInt(inventory.uniqueContentCount) <= BigInt(inventory.membershipCount) && BigInt(inventory.downloadObjectCount) <= BigInt(inventory.membershipCount);
}
export function isInventoryWorkspace(value: WorkspaceSnapshot): value is WorkspaceSnapshot & { inventory: InventorySummary; metadata: NonNullable<WorkspaceSnapshot['metadata']> } {
  if (value.preparationProfile !== 'home-inventory-v1' || value.readiness !== 'partial' || value.metadataState !== 'verified' || value.inventoryState !== 'ready' || value.retentionState !== 'measured' || !value.inventory || !inventoryAccountingValid(value.inventory) || !value.metadata || value.metadata.length < 3 || value.metadata.length > 5) return false;
  const roles = new Set(value.metadata.map(receipt => receipt.role));
  return roles.size === value.metadata.length && ['encoding', 'root', 'install'].every(role => roles.has(role as 'encoding' | 'root' | 'install'));
}
export function inventoryPageMatches(value: InventoryPage, params: InventoryReadParams): boolean {
  if (value.productId !== params.productId || value.buildIdentity !== params.buildIdentity || value.sourceRegion !== params.sourceRegion || value.generation !== params.generation || value.entries.length > params.limit || !unsigned.test(value.totalCount) || BigInt(value.totalCount) < BigInt(value.entries.length)) return false;
  let cursor = BigInt(params.after || '0');
  for (const entry of value.entries) {
    if (!positive.test(entry.id) || BigInt(entry.id) <= cursor) return false;
    cursor = BigInt(entry.id);
  }
  return value.nextCursor === null || value.entries.length > 0 && positive.test(value.nextCursor) && value.nextCursor === value.entries.at(-1)!.id;
}
export const exactCount = (value: string) => BigInt(value).toLocaleString();
export const exactBytes = (value: string) => `${exactCount(value)} bytes`;
export function readableBytes(value: string): string {
  const bytes = BigInt(value), units = ['bytes', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB', 'EiB'];
  let size = 1n, unit = 0;
  while (unit < units.length - 1 && bytes >= size * 1024n) { size *= 1024n; unit++; }
  if (!unit) return exactBytes(value);
  return `${(bytes / size).toLocaleString()}.${((bytes % size) * 100n / size).toString().padStart(2, '0')} ${units[unit]}`;
}
export function encodedPercentage(part: string, total: string): string {
  if (BigInt(total) === 0n) return '0 bytes required';
  const basisPoints = BigInt(part) * 10000n / BigInt(total);
  return `${basisPoints / 100n}.${(basisPoints % 100n).toString().padStart(2, '0')}%`;
}
