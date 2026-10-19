import type {CreatureGeometryResult} from '../../../../contracts/generated/discovery';
import type {GeometryParams,GeometryReadParams,GeometryViewEnterParams,GeometryViewEntered,GeometryViewOpenParams,GeometryBodyGrant,GeometryBodyRetiredParams,GeometryBodyRetired,GeometrySceneDisposal,GeometryViewReleaseParams,GeometryViewRetirement} from './creature-geometry';
import type {ItemEffectsReadiness,ItemEffectsPage,ItemEffectDetailsResult,ItemEffectNamesResult} from '../../../../contracts/generated/discovery';
import type {ItemEffectsSelectionParams,ItemEffectsPageParams,ItemEffectDetailsParams,ItemEffectNamesParams} from './item-effects';
import type {CreatureDisplayMetadataResult} from '../../../../contracts/generated/discovery';
import type {CreatureDisplayParams} from './creature-display';
import type {CreaturesContext,CreaturesSnapshot,CreaturesReadiness,CreaturesPage,CreatureInspectResult} from '../../../../contracts/generated/discovery';
import type {CreaturePageParams,CreatureInspectParams} from './creatures';
import type {ClassificationParams} from './classifications';
import type {ItemClassificationBatch} from '../../../../contracts/generated/discovery';
import type {IconBatchParams,IconBatch} from './item-icons';
import type { ItemsContext, ItemsSnapshot, ItemsReadiness, ItemsPage, ItemInspectResult } from '../../../../contracts/generated/discovery';
import type { ItemsPageParams, ItemInspectParams } from './items';
import type { RetentionSet } from './retention';
import type { RetentionTarget, RetentionSnapshot, RetentionList } from '../../../../contracts/generated/discovery';
import type { TableDataParams, TableRowsParams } from './table-data';
import type { Product, DiscoveryPage, EngineProgress, WorkspaceSnapshot, InventoryPage, DownloadPage, TableCatalogPage, TableDefinitionSnapshot, TableDataSnapshot, TableDataReadiness, TableDataPage } from '../../../../contracts/generated/discovery';
import type { TablesCatalogParams, TableDefinitionParams } from './tables';
import { isDownloadWorkspace, type DownloadReadParams } from './download';
import { isInventoryWorkspace, type InventoryReadParams } from './inventory';
export type DiscoveryFilter = 'all' | 'local' | 'advertised';
export type ReadParams = { productId: string; filter?: DiscoveryFilter; limit?: number; cursor?: string };
export type OpenParams = { productId: string; buildIdentity: string; sourceRegion: string };
export type RequestMethod = 'creatures.geometry.read' | 'creatures.geometry.prepare' | 'creatures.geometry.open' | 'creatures.geometry.close' | 'hello' | 'products.list' | 'discovery.read' | 'discovery.refresh' | 'workspace.open' | 'inventory.read' | 'download.read' | 'tables.catalog' | 'tables.definition' | 'tables.prepare' | 'tables.data' | 'tables.rows' | 'request.cancel' | 'shutdown' | 'retention.read' | 'retention.list' | 'retention.set' | 'items.data' | 'items.prepare' | 'items.page' | 'items.inspect' | 'items.icons' | 'items.icon.open' | 'items.icon.close' | 'items.classifications' | 'creatures.data' | 'creatures.prepare' | 'creatures.page' | 'creatures.inspect' | 'creatures.display.read' | 'creatures.display.prepare' | 'items.effects.data' | 'items.effects.prepare' | 'items.effects.page' | 'items.effects.details' | 'items.effects.details.prepare' | 'items.effects.names' | 'items.effects.names.prepare';
export type BridgeReply<T> = { ok: true; value: T } | { ok: false; error: { code: string; message: string; retryable: boolean } };
export interface DiscoveryBridge {
  onGeometryRetirement?(listener:(notice:GeometryViewRetirement)=>void):()=>void;
  creatureGeometryRead?(params:GeometryReadParams):Promise<BridgeReply<CreatureGeometryResult>>;
  creatureGeometryPrepare?(params:GeometryParams,requestId:string):Promise<BridgeReply<CreatureGeometryResult>>;
  geometryViewEnter?(params:GeometryViewEnterParams):Promise<BridgeReply<GeometryViewEntered>>;
  geometryViewOpen?(params:GeometryViewOpenParams):Promise<BridgeReply<GeometryBodyGrant>>;
  geometryViewBodyRetired?(params:GeometryBodyRetiredParams):Promise<BridgeReply<GeometryBodyRetired>>;
  geometryViewSceneDisposed?(params:GeometrySceneDisposal):Promise<BridgeReply<{accepted:true}>>;
  geometryViewRelease?(params:GeometryViewReleaseParams):Promise<BridgeReply<{released:true}>>;
  itemEffectsRead?(params:ItemEffectsSelectionParams):Promise<BridgeReply<ItemEffectsReadiness>>;
  itemEffectsPrepareLinks?(params:ItemEffectsSelectionParams,requestId:string):Promise<BridgeReply<ItemEffectsReadiness>>;
  itemEffectsPage?(params:ItemEffectsPageParams):Promise<BridgeReply<ItemEffectsPage>>;
  itemEffectDetails?(params:ItemEffectDetailsParams):Promise<BridgeReply<ItemEffectDetailsResult>>;
  itemEffectPrepareDetails?(params:ItemEffectDetailsParams,requestId:string):Promise<BridgeReply<ItemEffectDetailsResult>>;
  itemEffectNames?(params:ItemEffectNamesParams):Promise<BridgeReply<ItemEffectNamesResult>>;
  itemEffectPrepareNames?(params:ItemEffectNamesParams,requestId:string):Promise<BridgeReply<ItemEffectNamesResult>>;
  creatureDisplayRead?(params:CreatureDisplayParams):Promise<BridgeReply<CreatureDisplayMetadataResult>>;
  creatureDisplayPrepare?(params:CreatureDisplayParams,requestId:string):Promise<BridgeReply<CreatureDisplayMetadataResult>>;
  creaturesData?(params:CreaturesContext):Promise<BridgeReply<CreaturesReadiness>>;
  creaturesPrepare?(params:CreaturesContext,requestId:string):Promise<BridgeReply<CreaturesSnapshot>>;
  creaturesPage?(params:CreaturePageParams):Promise<BridgeReply<CreaturesPage>>;
  creatureInspect?(params:CreatureInspectParams):Promise<BridgeReply<CreatureInspectResult>>;
  classifications?(params:ClassificationParams,requestId:string):Promise<BridgeReply<ItemClassificationBatch>>;
  iconsBatch?(params:IconBatchParams,requestId:string,viewId:string):Promise<BridgeReply<IconBatch>>;
  iconsRelease?(viewId:string):Promise<BridgeReply<{released:boolean}>>;
  capabilities?(): Promise<BridgeReply<string[]>>;
  retentionRead?(params: RetentionTarget): Promise<BridgeReply<RetentionSnapshot>>;
  retentionList?(): Promise<BridgeReply<RetentionList>>;
  retentionSet?(params: RetentionSet): Promise<BridgeReply<RetentionSnapshot>>;
  itemsData(params: ItemsContext): Promise<BridgeReply<ItemsReadiness>>;
  itemsPrepare(params: ItemsContext, requestId: string): Promise<BridgeReply<ItemsSnapshot>>;
  itemsPage(params: ItemsPageParams): Promise<BridgeReply<ItemsPage>>;
  itemInspect(params: ItemInspectParams): Promise<BridgeReply<ItemInspectResult>>;
  readonly fixtureLabel?: string | null;
  products(): Promise<BridgeReply<{ products: Product[] }>>;
  read(params: ReadParams): Promise<BridgeReply<DiscoveryPage>>;
  refresh(productId: string, requestId: string): Promise<BridgeReply<DiscoveryPage>>;
  open(params: OpenParams, requestId: string): Promise<BridgeReply<WorkspaceSnapshot>>;
  tablePrepare(params: TableDataParams, requestId: string): Promise<BridgeReply<TableDataSnapshot>>;
  tableData(params: TableDataParams): Promise<BridgeReply<TableDataReadiness>>;
  tableRows(params: TableRowsParams): Promise<BridgeReply<TableDataPage>>;
  tablesCatalog(params: TablesCatalogParams): Promise<BridgeReply<TableCatalogPage>>;
  tableDefinition(params: TableDefinitionParams): Promise<BridgeReply<TableDefinitionSnapshot>>;
  download(params: DownloadReadParams): Promise<BridgeReply<DownloadPage>>;
  inventory(params: InventoryReadParams): Promise<BridgeReply<InventoryPage>>;
  cancel(requestId: string): Promise<BridgeReply<{ requested: boolean }>>;
  onProgress(listener: (progress: EngineProgress) => void): () => void;
}
declare global { interface Window { discovery: DiscoveryBridge } }
export const validProduct = (value: unknown): value is string => typeof value === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(value);
export const validRequestId = (value: unknown): value is string => typeof value === 'string' && /^[a-zA-Z0-9_-]{1,96}$/.test(value);
export function validRead(value: unknown): value is ReadParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as ReadParams;
  return validProduct(p.productId) && (p.filter === undefined || ['all', 'local', 'advertised'].includes(p.filter)) && (p.limit === undefined || Number.isInteger(p.limit) && p.limit >= 1 && p.limit <= 100) && (p.cursor === undefined || typeof p.cursor === 'string' && p.cursor.length <= 4096) && Object.keys(p).every(key => ['productId', 'filter', 'limit', 'cursor'].includes(key));
}

export function validOpen(value: unknown): value is OpenParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as OpenParams;
  return validProduct(p.productId) && typeof p.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(p.buildIdentity) && typeof p.sourceRegion === 'string' && /^[a-z]{2,8}$/.test(p.sourceRegion) && Object.keys(p).length === 3 && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion'].includes(key));
}
export function workspaceMatches(value: WorkspaceSnapshot, params: OpenParams): boolean {
  const profileValid = value.preparationProfile === 'home-download-v1' ? isDownloadWorkspace(value) : value.preparationProfile === 'home-inventory-v1' ? isInventoryWorkspace(value) : value.preparationProfile === 'home-configurations-v1' && value.readiness === 'configurationReady' && value.metadataState === 'configurationsVerified' && value.inventoryState === 'notPrepared' && value.retentionState === 'unknown' && !value.inventory && !value.metadata;
  return profileValid && value.build.productId === params.productId && value.build.id === params.buildIdentity && value.sourceReference.region === params.sourceRegion && value.configurations.length === 2 && value.configurations.some(c => c.role === 'buildConfig' && c.key === value.build.buildConfig) && value.configurations.some(c => c.role === 'cdnConfig' && c.key === value.build.cdnConfig) && (['versions', 'cdns'] as const).every(role => value.build.sourceReceipts.some(receipt => receipt.role === role && receipt.region === params.sourceRegion && receipt.sha256 === (role === 'versions' ? value.sourceReference.versionsSha256 : value.sourceReference.cdnsSha256) && receipt.rowOrdinal === (role === 'versions' ? value.sourceReference.versionsRowOrdinal : value.sourceReference.cdnsRowOrdinal)));
}
