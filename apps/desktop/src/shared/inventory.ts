import type { CommunityNamesSource, FileCatalogList, FileCatalogSetParams, FileCatalogStatus, InventoryFileDetail, InventoryFileParams, InventoryFilesPage, InventoryFilesParams, InventoryPage, InventorySummary, WorkspaceSnapshot } from '../../../../contracts/generated/discovery';
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

// Files (contracts/inventory-v1.md#files): one row per ROOT FileDataID or exact install name.
export type { InventoryFilesParams, InventoryFileParams };
export type FileFilters = Pick<InventoryFilesParams, 'fileDataId' | 'contentKey' | 'localeFlags' | 'contentFlags' | 'family' | 'storedOnly'>;
const fileKeyPattern = /^(root:(0|[1-9][0-9]{0,9})|install:[1-9][0-9]{0,18})$/;
const filesCursorPattern = /^(root:(0|[1-9][0-9]{0,9})|install:(0|[1-9][0-9]{0,18}))$/;
const u32 = (value: string) => BigInt(value) <= 4294967295n;
const filterKeys = ['fileDataId', 'contentKey', 'localeFlags', 'contentFlags', 'family', 'storedOnly'];
function contextValid(p: Record<string, unknown>) {
  return typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId) && typeof p.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(p.buildIdentity) && typeof p.sourceRegion === 'string' && /^[a-z]{2,8}$/.test(p.sourceRegion) && typeof p.generation === 'string' && positive.test(p.generation) && Number.isInteger(p.limit) && (p.limit as number) >= 1 && (p.limit as number) <= 100;
}
export function validFileFilters(p: Record<string, unknown>): boolean {
  const decimal32 = (v: unknown, zero: boolean) => typeof v === 'string' && (zero ? /^(0|[1-9][0-9]{0,9})$/ : /^[1-9][0-9]{0,9}$/).test(v) && u32(v);
  return (p.fileDataId === undefined || decimal32(p.fileDataId, true)) && (p.contentKey === undefined || typeof p.contentKey === 'string' && /^[0-9a-f]{4,32}$/.test(p.contentKey))
    && !(p.fileDataId !== undefined && p.contentKey !== undefined) && (p.localeFlags === undefined || decimal32(p.localeFlags, false))
    && (p.contentFlags === undefined || p.contentFlags === 'none' || decimal32(p.contentFlags, false)) && (p.family === undefined || p.family === 'root' || p.family === 'install')
    && (p.storedOnly === undefined || typeof p.storedOnly === 'boolean');
}
export function validInventoryFiles(value: unknown): value is InventoryFilesParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as Record<string, unknown>;
  return contextValid(p) && validFileFilters(p) && (p.after === undefined || typeof p.after === 'string' && filesCursorPattern.test(p.after) && (!p.after.startsWith('root:') || u32(p.after.slice(5))))
    && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion', 'generation', 'limit', 'after', ...filterKeys].includes(key));
}
export function validInventoryFile(value: unknown): value is InventoryFileParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as Record<string, unknown>;
  return contextValid(p) && validFileFilters(p) && typeof p.file === 'string' && fileKeyPattern.test(p.file) && (!p.file.startsWith('root:') || u32(p.file.slice(5))) && (p.after === undefined || typeof p.after === 'string' && positive.test(p.after))
    && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion', 'generation', 'limit', 'file', 'after', ...filterKeys].includes(key));
}
/** Orders Files positions: every ROOT row before every install row, then by value. */
export function filePosition(key: string): [number, bigint] { const install = key.startsWith('install:'); return [install ? 1 : 0, BigInt(key.slice(install ? 8 : 5))]; }
const after = (a: [number, bigint], b: [number, bigint]) => a[0] !== b[0] ? a[0] > b[0] : a[1] > b[1];
const narrows = (p: FileFilters) => p.fileDataId !== undefined || p.contentKey !== undefined || p.localeFlags !== undefined || p.contentFlags !== undefined || !!p.storedOnly;
/** A ready source names its release; off and not ready name nothing. */
const communitySourceMatches = (c: CommunityNamesSource) => c.state === 'ready'
  ? c.tag !== null && c.publishedAt !== null && c.fetchedAt !== null && !Number.isNaN(Date.parse(c.publishedAt)) && !Number.isNaN(Date.parse(c.fetchedAt))
  : c.tag === null && c.publishedAt === null && c.fetchedAt === null;
export function inventoryFilesPageMatches(page: InventoryFilesPage, params: InventoryFilesParams): boolean {
  if (page.productId !== params.productId || page.buildIdentity !== params.buildIdentity || page.sourceRegion !== params.sourceRegion || page.generation !== params.generation || page.entries.length > params.limit) return false;
  if (narrows(params) ? page.totalCount !== null : page.totalCount === null) return false;
  if (!communitySourceMatches(page.communityNames)) return false;
  let previous: [number, bigint] | null = params.after === undefined ? null : filePosition(params.after);
  for (const file of page.entries) {
    const position = filePosition(file.key), root = position[0] === 0;
    if (previous && !after(position, previous)) return false;
    if (params.family && file.family !== params.family || file.family !== (root ? 'root' : 'install')) return false;
    // ROOT has no names; install rows carry their actual name and no ROOT locale flags.
    if (root ? file.fileDataId !== String(position[1]) || file.name !== null || file.localeFlags === null : file.fileDataId !== null || file.name === null || file.localeFlags !== null) return false;
    // A community name labels a ROOT row only, and only from a ready source (D105).
    if (file.communityName !== null && (!root || page.communityNames.state !== 'ready')) return false;
    if (file.matchingCount < 1 || file.matchingCount > file.membershipCount || file.storedCount > file.membershipCount || file.unresolvedCount > file.membershipCount || file.contentKeyCount > file.membershipCount) return false;
    if ((file.unresolvedCount === file.membershipCount) !== (file.encodedBytesMin === null) || (file.encodedBytesMin === null) !== (file.encodedBytesMax === null) || file.encodedBytesMin !== null && BigInt(file.encodedBytesMin) > BigInt(file.encodedBytesMax!)) return false;
    previous = position;
  }
  if (page.scanLimited && page.nextCursor === null) return false;
  if (page.nextCursor !== null && previous && after(previous, filePosition(page.nextCursor))) return false;
  if (page.nextCursor !== null && params.after !== undefined && !after(filePosition(page.nextCursor), filePosition(params.after))) return false;
  return true;
}
export function inventoryFileDetailMatches(detail: InventoryFileDetail, params: InventoryFileParams): boolean {
  if (detail.productId !== params.productId || detail.buildIdentity !== params.buildIdentity || detail.sourceRegion !== params.sourceRegion || detail.generation !== params.generation || detail.file !== params.file || detail.memberships.length > params.limit) return false;
  const [section, value] = filePosition(params.file), root = section === 0;
  if (detail.family !== (root ? 'root' : 'install') || (root ? detail.fileDataId !== String(value) || detail.name !== null : detail.fileDataId !== null || detail.name === null) || detail.matchingCount > detail.membershipCount) return false;
  if (!communitySourceMatches(detail.communityNames) || detail.communityName !== null && (!root || detail.communityNames.state !== 'ready')) return false;
  let cursor = BigInt(params.after ?? '0');
  for (const m of detail.memberships) {
    if (BigInt(m.id) <= cursor || m.family !== detail.family || (m.encodingKey === null) !== (m.encodedBytes === null)) return false;
    if (root) {
      if (m.blockOrdinal === null || m.entryOrdinal === null || m.installOrdinal !== null || m.localeFlags === null || m.contentFlags === null || !m.rawContentFlags) return false;
      const raw = m.rawContentFlags;
      if ((BigInt(raw.first) | BigInt(raw.second) | (BigInt(raw.third) << 17n)) !== BigInt(m.contentFlags)) return false;
    } else if (m.installOrdinal === null || m.blockOrdinal !== null || m.localeFlags !== null || m.contentFlags !== null || m.rawContentFlags !== null || m.nameHash !== null) return false;
    cursor = BigInt(m.id);
  }
  return detail.nextCursor === null || detail.memberships.length > 0 && detail.nextCursor === detail.memberships.at(-1)!.id;
}
/** Known locale bit names (docs/formats/root.md); every other bit stays a raw mask. */
export const localeBits: readonly [number, string][] = [[0x2, 'enUS'], [0x200, 'enGB'], [0x80, 'esES'], [0x1000, 'esMX']];
export const localeNames = (mask: string) => { const value = BigInt(mask); return localeBits.filter(([bit]) => (value & BigInt(bit)) !== 0n).map(([, name]) => name); };
export const hex = (value: string | number) => '0x' + BigInt(value).toString(16).toUpperCase().padStart(8, '0');

// File catalog prepared after Open (D071, contracts/inventory-v1.md#file-catalog-after-open).
export type FileCatalogParams = OpenParams;
export type { FileCatalogStatus, FileCatalogList, FileCatalogSetParams };
export function validFileCatalog(value: unknown): value is FileCatalogParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as Record<string, unknown>;
  return typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId) && typeof p.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(p.buildIdentity)
    && typeof p.sourceRegion === 'string' && /^[a-z]{2,8}$/.test(p.sourceRegion) && Object.keys(p).length === 3;
}
export function validFileCatalogSet(value: unknown): value is FileCatalogSetParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as Record<string, unknown>;
  return typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId) && typeof p.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(p.buildIdentity)
    && (p.action === 'stop' || p.action === 'resume') && Object.keys(p).length === 3;
}
/** One status agrees with itself: a stage only while pending, a written count only while writing, a named wait only while queued or waiting. */
function fileCatalogConsistent(status: FileCatalogStatus): boolean {
  if (status.waitingFor !== null && !(status.state === 'pending' && (status.stage === 'queued' || status.stage === 'waiting'))) return false;
  if (status.state === 'pending' && status.stage === 'writing') return status.completed !== null && status.total !== null && BigInt(status.completed) <= BigInt(status.total);
  if (status.completed !== null || status.total !== null) return false;
  if (status.state !== 'ready') return true;
  const c = status.catalog!;
  const memberships = BigInt(c.rootMembershipCount) + BigInt(c.installMembershipCount);
  return BigInt(c.rootFileCount) <= BigInt(c.rootMembershipCount) && BigInt(c.installFileCount) <= BigInt(c.installMembershipCount)
    && BigInt(c.unresolvedMembershipCount) <= memberships && BigInt(c.storedMembershipCount) <= memberships && BigInt(c.uniqueContentCount) <= memberships;
}
/** The reply is for exactly this build and region, and its counts agree with each other. */
export function fileCatalogMatches(status: FileCatalogStatus, params: FileCatalogParams): boolean {
  return status.productId === params.productId && status.buildIdentity === params.buildIdentity && status.sourceRegion === params.sourceRegion && fileCatalogConsistent(status);
}
/** Activity's list: only pending, stopped or failed preparations, one per build, each consistent. */
export function fileCatalogListMatches(list: FileCatalogList): boolean {
  const builds = new Set(list.catalogs.map(status => `${status.productId}|${status.buildIdentity}`));
  return builds.size === list.catalogs.length && list.catalogs.every(status => ['pending', 'stopped', 'failed'].includes(status.state) && fileCatalogConsistent(status));
}
/** A Stop or Resume reply is for the build it named and in a state that action can leave. */
export function fileCatalogSetMatches(status: FileCatalogStatus, params: FileCatalogSetParams): boolean {
  return status.productId === params.productId && status.buildIdentity === params.buildIdentity && fileCatalogConsistent(status)
    && (params.action === 'stop' ? status.state === 'stopped' : status.state === 'pending');
}
