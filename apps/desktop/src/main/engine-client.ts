import {validGeometryParams,validGeometryRead,validGeometryOpen,validGeometryToken,creatureGeometryMatches,geometryLeaseMatches,validGeometryProgress,type GeometryParams,type GeometryReadParams,type GeometryOpenParams} from '../shared/creature-geometry';
import type {CreatureGeometryResult,CreatureGeometryLeaseDescriptor} from '../../../../contracts/generated/discovery';
import {validItemEffectsSelection,validItemEffectsPage,validItemEffectDetails,validItemEffectNames,itemEffectsReadinessMatches,itemEffectsPageMatches,itemEffectDetailsMatch,itemEffectNamesMatch,type ItemEffectsSelectionParams,type ItemEffectsPageParams,type ItemEffectDetailsParams,type ItemEffectNamesParams} from '../shared/item-effects';
import type {ItemEffectsReadiness,ItemEffectsPage,ItemEffectDetailsResult,ItemEffectNamesResult} from '../../../../contracts/generated/discovery';
import {validCreatureDisplay,creatureDisplayMatches,type CreatureDisplayParams} from '../shared/creature-display';
import type {CreatureDisplayMetadataResult} from '../../../../contracts/generated/discovery';
import {validCreatureContext,validCreaturePage,validCreatureInspect,creaturesSnapshotMatches,creaturesReadinessMatches,creaturesPageMatches,creatureInspectMatches,type CreaturePageParams,type CreatureInspectParams} from '../shared/creatures';
import type {CreaturesContext,CreaturesSnapshot,CreaturesReadiness,CreaturesPage,CreatureInspectResult} from '../../../../contracts/generated/discovery';
import {validClassifications,classificationsMatch,type ClassificationParams} from '../shared/classifications';
import {validIconBatch,validIconOpen,validIconClose,iconBatchMatches,iconLeaseMatches,type IconBatchParams,type IconOpenParams} from '../shared/item-icons';
import type {ItemIconBatch,ItemIconLeaseDescriptor} from '../../../../contracts/generated/discovery';
import { validItemsContext, validItemsPage, validItemInspect, itemsSnapshotMatches, itemsReadinessMatches, itemsPageMatches, itemInspectMatches, type ItemsPageParams, type ItemInspectParams } from '../shared/items';
import type { ItemsContext, ItemsSnapshot, ItemsReadiness, ItemsPage, ItemInspectResult } from '../../../../contracts/generated/discovery';
import { validRetentionTarget, validRetentionSet, retentionMatches, retentionSetMatches, retentionListMatches, type RetentionSet } from '../shared/retention';
import { validSettingsSet, validEmpty, settingsSnapshotMatches, settingsSetMatches, storageMeasurementMatches, validPreparedDataFree, preparedDataListMatches, preparedDataFreedMatches, type SettingsSnapshot, type SettingsSetParams, type StorageMeasurement, type PreparedDataList, type PreparedDataFreed, type PreparedDataFreeParams } from '../shared/settings';
import type { RetentionTarget, RetentionSnapshot, RetentionList, HelloResult } from '../../../../contracts/generated/discovery';
import { validTableData, validTableRows, tableDataMatches, tableRowsMatch, type TableDataParams, type TableRowsParams } from '../shared/table-data';
import type { TableDataSnapshot, TableDataReadiness, TableDataPage, TableReferenceTextPage } from '../../../../contracts/generated/discovery';
import { referenceTextMatches } from '../shared/reference-text';
import { validTableFollow, followMatches, type TableFollowParams } from '../shared/table-follow';
import type { TableFollowPage } from '../../../../contracts/generated/discovery';
import { EventEmitter } from 'node:events';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type { Readable, Writable } from 'node:stream';
import Ajv from 'ajv';
import schema from '../../../../contracts/discovery.schema.json';
import type { DiscoveryCatalogPage, EngineProgress, WorkspaceSnapshot, InventoryPage, InventoryFilesPage, InventoryFileDetail, DownloadPage, TableCatalogPage, TableDefinitionSnapshot } from '../../../../contracts/generated/discovery';
import { validOpen, validReadAll, catalogPageMatches, workspaceMatches, type OpenParams, type ReadAllParams, type RequestMethod } from '../shared/bridge';
import { validInventoryRead, inventoryPageMatches, validInventoryFiles, validInventoryFile, inventoryFilesPageMatches, inventoryFileDetailMatches, validFileCatalog, fileCatalogMatches, validFileCatalogSet, fileCatalogListMatches, fileCatalogSetMatches, type InventoryReadParams, type InventoryFilesParams, type InventoryFileParams, type FileCatalogStatus, type FileCatalogList, type FileCatalogSetParams } from '../shared/inventory';
import { validTablesCatalog, validTableDefinition, tableCatalogMatches, tableDefinitionMatches, type TablesCatalogParams, type TableDefinitionParams } from '../shared/tables';
import { validDownloadRead, downloadPageMatches, type DownloadReadParams } from '../shared/download';
import { validSummariesParams, validChangesParams, summariesMatch } from '../shared/download-summary';
import type { DownloadSummaries, DownloadSummariesParams } from '../../../../contracts/generated/discovery';
import { validProgressStep,validItemEffectsProgress } from '../shared/progress';
const MAX_FRAME = 1024 * 1024;
const ajv = new Ajv({ strict: false });
ajv.addSchema(schema);
const check = (name: string, value: unknown) => ajv.getSchema(`${schema.$id}#/definitions/${name}`)!(value);
export class EngineFailure extends Error {
  constructor(public code: string, message: string, public retryable = false) { super(message); }
}
/** Host exit codes for a refused catalog (VibeMiner.Host Program.cs). The catalog file is left unchanged. */
export const CATALOG_RESET_EXIT = 3, CATALOG_NEWER_EXIT = 4;
/**
 * Classify an engine exit. A refused catalog is a typed, non-retryable state that names the catalog folder and the
 * user's own step; the app never offers to change or remove it.
 */
export function engineExitFailure(code: number | null | undefined, dataDirectory?: string): EngineFailure {
  const folder = dataDirectory ? `The catalog folder is ${dataDirectory}.` : 'The catalog folder is the engine data folder.';
  const step = `Close VibeMiner, move or rename that folder (for example to ${dataDirectory ? path.basename(dataDirectory) : 'engine-v1'}.old), then start VibeMiner again.`;
  if (code === CATALOG_RESET_EXIT) return new EngineFailure('CATALOG_RESET_REQUIRED', `The saved catalog was made by an older VibeMiner and cannot be opened by this version. A fresh catalog is needed. ${folder} ${step} The old catalog was left unchanged.`);
  if (code === CATALOG_NEWER_EXIT) return new EngineFailure('CATALOG_VERSION', `The saved catalog was made by a newer VibeMiner. Use that version, or start this one with a fresh catalog. ${folder} ${step} The saved catalog was left unchanged.`);
  return new EngineFailure('ENGINE_EXITED', 'The local engine stopped. Restart VibeMiner to reconnect.', true);
}
/** How long a closed pipe waits for the process exit that explains it. */
const EXIT_GRACE_MS = 2000;
export interface EngineProcess extends EventEmitter { stdin: Writable; stdout: Readable; stderr: Readable; kill(signal?: NodeJS.Signals): boolean }
type IconExpected=IconBatchParams|IconOpenParams|{leaseId:string};
type EffectsExpected=ItemEffectsSelectionParams|ItemEffectsPageParams|ItemEffectDetailsParams|ItemEffectNamesParams;
const effectsMethod=(m:string)=>m.startsWith('items.effects.');
const effectsPrepare=(m:string)=>m==='items.effects.prepare'||m==='items.effects.details.prepare'||m==='items.effects.names.prepare';
type GeometryExpected=GeometryReadParams|GeometryOpenParams|{leaseId:string};
const geometryMethod=(m:string)=>m.startsWith('creatures.geometry.');
type Pending = {expectedFree?:PreparedDataFreeParams;expectedCatalogSet?:FileCatalogSetParams;expectedSummaries?:DownloadSummariesParams;expectedReadAll?:ReadAllParams;expectedGeometry?:GeometryExpected;expectedEffects?:EffectsExpected;expectedCreatures?:CreaturesContext|CreaturePageParams|CreatureInspectParams|CreatureDisplayParams;expectedClassifications?:ClassificationParams;expectedIcon?:IconExpected; expectedItems?: ItemsContext | ItemsPageParams | ItemInspectParams; expectedRetention?: RetentionTarget | RetentionSet; expectedTableData?: TableDataParams; expectedTableRows?: TableRowsParams; expectedCatalog?: TablesCatalogParams; expectedDefinition?: TableDefinitionParams; expectedDownload?: DownloadReadParams; expectedInventory?: InventoryReadParams; expectedFiles?: InventoryFilesParams | InventoryFileParams; expectedOpen?: OpenParams; expectedSettings?: SettingsSetParams; expectedFollow?: TableFollowParams; method: RequestMethod; resolve: (value: unknown) => void; reject: (error: EngineFailure) => void; timer: ReturnType<typeof setTimeout> };
export class EngineClient extends EventEmitter {
  private engineCapabilities: readonly string[] = [];
  get hasExited():boolean{return this.exited;}
  get hasFailed():boolean{return this.failure!==null;}
  async waitForExit(milliseconds:number):Promise<boolean>{if(this.exited)return true;let timer:ReturnType<typeof setTimeout>;await Promise.race([this.exitPromise,new Promise<void>(resolve=>{timer=setTimeout(resolve,milliseconds);})]);clearTimeout(timer!);return this.exited;}
  get capabilities(): readonly string[] { return this.engineCapabilities; }
  private pending = new Map<string, Pending>();
  private remainder = Buffer.alloc(MAX_FRAME);
  private frameLength = 0;
  private failure: EngineFailure | null = null;
  private closing = false;
  private exited = false;
  private lostTimer?: ReturnType<typeof setTimeout>;
  private exitResolve!: () => void;
  private exitPromise = new Promise<void>(resolve => { this.exitResolve = resolve; });
  private sequences = new Map<string, number>();
  private latestProgress = new Map<string, EngineProgress>();
  private progressTimer?: ReturnType<typeof setTimeout>;
  constructor(private child: EngineProcess, private readonly options: { dataDirectory?: string } = {}) {
    super();
    child.stdout.on('data', (data: Buffer) => this.receive(data));
    child.stdout.on('end', () => this.lost(new EngineFailure('ENGINE_EXITED', 'The local engine stopped. Restart VibeMiner to reconnect.', true)));
    child.stderr.on('data', () => { /* Drain bounded OS pipe; do not expose diagnostics/source data to renderer. */ });
    child.on('error', () => this.fail(new EngineFailure('ENGINE_UNAVAILABLE', 'The local engine could not be started. Check the desktop setup and restart VibeMiner.', true)));
    child.on('exit', (code: number | null) => { this.exited = true; clearTimeout(this.lostTimer); this.exitResolve(); this.emit('terminated'); this.fail(engineExitFailure(code, this.options.dataDirectory)); });
    child.stdin.on('error', () => this.lost(new EngineFailure('ENGINE_UNAVAILABLE', 'The local engine connection closed.', true)));
  }
  async hello(): Promise<void> { await this.request('hello', {}); }
  request<T = unknown>(method: RequestMethod, params: object, id: string = randomUUID()): Promise<T> {
    if (this.failure) return Promise.reject(this.failure);
    const cleanup = method === 'request.cancel' || method === 'shutdown' || method === 'items.icon.close' || method==='creatures.geometry.close';
    if (this.closing && !cleanup) return Promise.reject(new EngineFailure('STOPPING', 'The local engine is stopping.'));
    const max = cleanup ? 32 : 26;
    if (this.pending.size >= max) return Promise.reject(new EngineFailure('BUSY', 'Too many local engine operations. Try again shortly.', true));
    if (this.pending.has(id)) return Promise.reject(new EngineFailure('DUPLICATE_ID', 'This operation is already active.'));
    if ((method === 'tables.prepare' || method === 'tables.data') && !validTableData(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid exact table context or research locale.'));
    if ((method === 'tables.rows' || method === 'tables.referenceText') && !validTableRows(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid bounded table record query.'));
    if (method === 'tables.referenceText' && !this.capabilities.includes('tables-reference-text-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not support reference text.'));
    if (method === 'tables.follow' && !validTableFollow(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid bounded follow query.'));
    if (method === 'tables.follow' && !this.capabilities.includes('tables-follow-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine cannot follow references.'));
    if (method === 'tables.catalog' && !validTablesCatalog(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid bounded definition catalogue query.'));
    if (method === 'tables.definition' && !validTableDefinition(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Choose one exact provider table identity.'));
    if (method === 'download.read' && !validDownloadRead(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid bounded download-object query.'));
    if (method === 'inventory.read' && !validInventoryRead(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid bounded inventory query.'));
    if (method === 'inventory.files' && !validInventoryFiles(params) || method === 'inventory.file' && !validInventoryFile(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid bounded Files query.'));
    if ((method === 'inventory.files' || method === 'inventory.file') && !this.capabilities.includes('inventory-files-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not support Files queries.'));
    if (method === 'inventory.catalog' && !validFileCatalog(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Choose one exact opened build and source region.'));
    if (method === 'inventory.catalogs' && Object.keys(params).length !== 0 || method === 'inventory.catalog.set' && !validFileCatalogSet(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Choose one exact build and stop or resume.'));
    if (method.startsWith('inventory.catalog') && !this.capabilities.includes('file-catalog-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not prepare file catalogs.'));
    if (method === 'workspace.open' && !validOpen(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Choose an exact saved build and source region.'));
    if (method === 'discovery.readAll' && !validReadAll(params) || method === 'discovery.refreshAll' && Object.keys(params).length !== 0) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid cross-product discovery request.'));
    if ((method === 'items.data' || method === 'items.prepare') && !validItemsContext(params) || method === 'items.page' && !validItemsPage(params) || method === 'items.inspect' && !validItemInspect(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid exact Items context or bounded query.'));
    if(effectsMethod(method)){
      const valid=method==='items.effects.data'||method==='items.effects.prepare'?validItemEffectsSelection(params):method==='items.effects.page'?validItemEffectsPage(params):method==='items.effects.details'||method==='items.effects.details.prepare'?validItemEffectDetails(params):validItemEffectNames(params);
      if(!valid)return Promise.reject(new EngineFailure('INVALID_REQUEST','Invalid exact effects selection or bounded ordered edge keys.'));
      if(!['item-effects-links-v1','item-effects-inputs-v1','item-effect-spell-names-v1'].every(c=>this.capabilities.includes(c)))return Promise.reject(new EngineFailure('UNSUPPORTED','This engine does not support all current effects phases.'));
    }
    if(geometryMethod(method)){
      const valid=method==='creatures.geometry.read'?validGeometryRead(params):method==='creatures.geometry.prepare'?validGeometryParams(params):method==='creatures.geometry.open'?validGeometryOpen(params):Object.keys(params).length===1&&validGeometryToken((params as {leaseId?:unknown}).leaseId);
      if(!valid)return Promise.reject(new EngineFailure('INVALID_REQUEST','Choose one exact geometry selection or owned lease.'));
      if(!['creatures-v1','creature-display-metadata-v1','creature-source-geometry-v1'].every(c=>this.capabilities.includes(c)))return Promise.reject(new EngineFailure('UNSUPPORTED','Verified source geometry is unavailable.'));
    }
    if(method.startsWith('creatures.display.')&&(!this.capabilities.includes('creature-display-metadata-v1')||!validCreatureDisplay(params)))return Promise.reject(new EngineFailure(!validCreatureDisplay(params)?'INVALID_REQUEST':'UNSUPPORTED','The exact optional display metadata request is unavailable.'));
    if(method.startsWith('creatures.')&&!this.capabilities.includes('creatures-v1'))return Promise.reject(new EngineFailure('UNSUPPORTED','This engine does not support client Creature records.'));
    if((method==='creatures.data'||method==='creatures.prepare')&&!validCreatureContext(params)||method==='creatures.page'&&!validCreaturePage(params)||method==='creatures.inspect'&&!validCreatureInspect(params))return Promise.reject(new EngineFailure('INVALID_REQUEST','Invalid exact Creature context or bounded query.'));
    if(method==='items.classifications'&&!validClassifications(params))return Promise.reject(new EngineFailure('INVALID_REQUEST','Invalid bounded exact source-label request.'));
    if(method==='items.classifications'&&(!this.capabilities.includes('item-classifications-v1')||(params as ClassificationParams).policyRevision==='classification-label-index-v2'&&!this.capabilities.includes('item-subclass-labels-v1')))return Promise.reject(new EngineFailure('UNSUPPORTED','This engine does not support the requested source-label policy.'));
    if (method==='items.icons'&&!validIconBatch(params)||method==='items.icon.open'&&!validIconOpen(params)||method==='items.icon.close'&&!validIconClose(params))return Promise.reject(new EngineFailure('INVALID_REQUEST','Invalid exact icon request.'));
    if ((method==='items.icons'||method.startsWith('items.icon.'))&&!this.capabilities.includes('item-icons-v1'))return Promise.reject(new EngineFailure('UNSUPPORTED','This engine does not support optional Item icons.'));
    if (method.startsWith('items.') && !this.capabilities.includes('items-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not support Items.'));
    if (method === 'download.summaries' && !validSummariesParams(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Choose one to 100 distinct exact builds.'));
    if (method === 'download.changes' && !validChangesParams(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Name the summary revision of the last read.'));
    if ((method === 'download.summaries' || method === 'download.changes') && !this.capabilities.includes('download-summary-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not report stored download figures.'));
    if (method.startsWith('retention.') && !this.capabilities.includes('retention-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not support build retention.'));
    if (method === 'retention.read' && !validRetentionTarget(params) || method === 'retention.set' && !validRetentionSet(params) || method === 'retention.list' && Object.keys(params).length !== 0) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Invalid exact retention target or command.'));
    if (method.startsWith('settings.') && !this.capabilities.includes('settings-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not support settings.'));
    if ((method === 'settings.storage.builds' || method === 'settings.storage.free') && !this.capabilities.includes('prepared-data-v1')) return Promise.reject(new EngineFailure('UNSUPPORTED', 'This engine does not report prepared data per build.'));
    if (method === 'settings.storage.free' ? !validPreparedDataFree(params) : method === 'settings.set' ? !validSettingsSet(params) : method.startsWith('settings.') && !validEmpty(params)) return Promise.reject(new EngineFailure('INVALID_REQUEST', 'Choose one known setting value or one exact opened build.'));
    const data = Buffer.from(JSON.stringify({ protocolVersion: 1, id, method, params }) + '\n');
    if (data.length - 1 > MAX_FRAME) return Promise.reject(new EngineFailure('FRAME_TOO_LARGE', 'The engine request exceeds the allowed size.'));
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => this.fail(new EngineFailure('ENGINE_TIMEOUT', 'The local engine did not respond. Restart VibeMiner to reconnect.', true)), method === 'hello' || method==='items.icon.open' ? 15000 : (method === 'workspace.open' || method === 'tables.prepare' || method === 'items.prepare' || method==='items.icons' || method==='items.classifications'||method==='creatures.prepare'||method==='creatures.display.prepare'||method==='creatures.geometry.prepare'||effectsPrepare(method)) ? 21 * 60 * 1000 : method === 'discovery.refreshAll' || method === 'settings.storage.measure' ? 10 * 60 * 1000 : 180000);
      this.pending.set(id, { method,...(geometryMethod(method)?{expectedGeometry:{...params} as GeometryExpected}:{}),...(effectsMethod(method)?{expectedEffects:{...params,...('edges' in params?{edges:(params as ItemEffectDetailsParams).edges.map(e=>({...e}))}:{})} as EffectsExpected}:{}),...(method.startsWith('creatures.')&&!geometryMethod(method)?{expectedCreatures:{...params} as CreaturesContext|CreaturePageParams|CreatureInspectParams|CreatureDisplayParams}:{}),...(method==='items.classifications'?{expectedClassifications:{...params,itemIds:[...(params as ClassificationParams).itemIds]} as ClassificationParams}:{}), ...((method==='items.icons'||method.startsWith('items.icon.'))?{expectedIcon:{...params} as IconExpected}:{}), ...(method.startsWith('items.')&&!effectsMethod(method) ? {expectedItems:{...params} as ItemsContext | ItemsPageParams | ItemInspectParams} : {}), ...((method === 'retention.read' || method === 'retention.set') ? {expectedRetention: {...params} as RetentionTarget | RetentionSet} : {}), ...((method === 'tables.prepare' || method === 'tables.data') ? {expectedTableData:{...params} as TableDataParams} : {}), ...((method === 'tables.rows' || method === 'tables.referenceText') ? {expectedTableRows:{...params} as TableRowsParams} : {}), ...(method === 'tables.catalog' ? { expectedCatalog: { ...params } as TablesCatalogParams } : {}), ...(method === 'tables.definition' ? { expectedDefinition: { ...params } as TableDefinitionParams } : {}), ...(method === 'download.read' ? { expectedDownload: { ...params } as DownloadReadParams } : {}), ...(method === 'download.summaries' ? { expectedSummaries: { builds: (params as DownloadSummariesParams).builds.map(b => ({ ...b })) } as DownloadSummariesParams } : {}), ...(method === 'inventory.read' ? { expectedInventory: { ...params } as InventoryReadParams } : {}), ...(method === 'inventory.files' || method === 'inventory.file' ? { expectedFiles: { ...params } as InventoryFilesParams | InventoryFileParams } : {}), ...(method === 'workspace.open' || method === 'inventory.catalog' ? { expectedOpen: { ...params } as OpenParams } : {}), ...(method === 'inventory.catalog.set' ? { expectedCatalogSet: { ...params } as FileCatalogSetParams } : {}), ...(method === 'settings.storage.free' ? { expectedFree: { ...params } as PreparedDataFreeParams } : {}), ...(method === 'discovery.readAll' ? { expectedReadAll: { ...params } as ReadAllParams } : method === 'discovery.refreshAll' ? { expectedReadAll: {} } : {}), ...(method === 'settings.set' ? { expectedSettings: { ...params } as SettingsSetParams } : {}), ...(method === 'tables.follow' ? { expectedFollow: { ...params } as TableFollowParams } : {}), resolve: value => resolve(value as T), reject, timer });
      this.child.stdin.write(data, error => { if (error) this.lost(new EngineFailure('ENGINE_UNAVAILABLE', 'The local engine connection closed.', true)); });
    });
  }
  private receive(chunk: Buffer): void {
    if (this.failure) return;
    let offset = 0;
    while (offset < chunk.length) {
      const newline = chunk.indexOf(10, offset);
      const end = newline < 0 ? chunk.length : newline;
      const segment = chunk.subarray(offset, end);
      if (this.frameLength + segment.length > MAX_FRAME) { this.fail(new EngineFailure('PROTOCOL_ERROR', 'The local engine sent an oversized message.')); return; }
      segment.copy(this.remainder, this.frameLength);
      this.frameLength += segment.length;
      if (newline < 0) return;
      const frame = this.remainder.subarray(0, this.frameLength);
      this.frameLength = 0;
      try { this.frame(JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(frame)),frame.byteLength+1); }
      catch { this.fail(new EngineFailure('PROTOCOL_ERROR', 'The local engine sent an invalid message.')); return; }
      offset = end + 1;
    }
  }
  private frame(value: unknown,byteLength:number): void {
    if (!value || typeof value !== 'object') throw new Error('Invalid envelope');
    const message = value as Record<string, unknown>;
    if (message.protocolVersion !== 1) throw new Error('Incompatible protocol');
    if (message.type === 'progress') {
      if (!check('EngineProgress', value)) throw new Error('Invalid progress');
      const progress = value as EngineProgress;
      if (!validProgressStep(progress.step)) throw new Error('Invalid progress step');
      const operation=this.pending.get(progress.requestId);if(!operation)return;
      if(geometryMethod(operation.method)){if(operation.method!=='creatures.geometry.prepare'||byteLength>65536||!validGeometryProgress(progress)||progress.region!==(operation.expectedGeometry as GeometryParams)?.sourceRegion)throw new Error('Invalid geometry progress');}
      else if(Object.hasOwn(progress,'geometryStage')||Object.hasOwn(progress,'geometryDependency'))throw new Error('Unexpected geometry progress');
      if(effectsMethod(operation.method)&&(!effectsPrepare(operation.method)||byteLength>512*1024||!check('ItemEffectsEngineProgress',value)||!validItemEffectsProgress(progress)||progress.region!==operation.expectedEffects?.sourceRegion))throw new Error('Invalid effects progress');
      if (progress.sequence <= (this.sequences.get(progress.requestId) || 0)) return;
      this.sequences.set(progress.requestId, progress.sequence);
      this.latestProgress.set(progress.requestId, progress);
      if (!this.progressTimer) this.progressTimer = setTimeout(() => {
        this.progressTimer = undefined;
        for (const [id, item] of this.latestProgress) if (this.pending.has(id)) this.emit('progress', item);
        this.latestProgress.clear();
      }, 100);
      return;
    }
    const failed = 'error' in message;
    if (!check(failed ? 'EngineFailure' : 'EngineSuccess', value)) throw new Error('Invalid reply');
    if (typeof message.id !== 'string') throw new Error('Uncorrelated reply');
    const operation = this.pending.get(message.id);
    if (!operation) return; // Late/duplicate replies never acquire a new consumer.
    if(geometryMethod(operation.method)&&byteLength>65536)throw new Error('Oversized geometry control frame');
    if((operation.method==='items.classifications'||operation.method.startsWith('creatures.')||effectsMethod(operation.method))&&byteLength>512*1024)throw new Error('Oversized source-label frame');
    if (!failed && !this.validResult(operation.method, message.result, operation.expectedOpen, operation.expectedInventory, operation.expectedDownload, operation.expectedCatalog, operation.expectedDefinition, operation.expectedTableData, operation.expectedTableRows, operation.expectedRetention, operation.expectedItems,operation.expectedIcon,operation.expectedClassifications,operation.expectedCreatures,operation.expectedEffects,operation.expectedGeometry,operation.expectedReadAll,operation.expectedSettings,operation.expectedSummaries,operation.expectedFiles,operation.expectedFollow,operation.expectedCatalogSet,operation.expectedFree)) throw new Error('Invalid result');
    if (!failed && operation.method === 'hello') this.engineCapabilities = Object.freeze([...(message.result as HelloResult).capabilities]);
    this.pending.delete(message.id);
    this.latestProgress.delete(message.id);
    this.sequences.delete(message.id);
    clearTimeout(operation.timer);
    if (failed) {
      const error = message.error as { code: string; message: string; retryable: boolean };
      operation.reject(new EngineFailure(error.code, error.message, error.retryable));
    } else operation.resolve(message.result);
  }
  private validResult(method: RequestMethod, result: unknown, expectedOpen?: OpenParams, expectedInventory?: InventoryReadParams, expectedDownload?: DownloadReadParams, expectedCatalog?: TablesCatalogParams, expectedDefinition?: TableDefinitionParams, expectedTableData?: TableDataParams, expectedTableRows?: TableRowsParams, expectedRetention?: RetentionTarget | RetentionSet, expectedItems?: ItemsContext | ItemsPageParams | ItemInspectParams,expectedIcon?:IconExpected,expectedClassifications?:ClassificationParams,expectedCreatures?:CreaturesContext|CreaturePageParams|CreatureInspectParams|CreatureDisplayParams,expectedEffects?:EffectsExpected,expectedGeometry?:GeometryExpected,expectedReadAll?:ReadAllParams,expectedSettings?:SettingsSetParams,expectedSummaries?:DownloadSummariesParams,expectedFiles?:InventoryFilesParams|InventoryFileParams,expectedFollow?:TableFollowParams,expectedCatalogSet?:FileCatalogSetParams,expectedFree?:PreparedDataFreeParams): boolean {
    if(geometryMethod(method)){if(!expectedGeometry)return false;if(method==='creatures.geometry.close')return !!check('CreatureGeometryCloseResult',result);if(method==='creatures.geometry.open')return !!check('CreatureGeometryLeaseDescriptor',result)&&geometryLeaseMatches(result as CreatureGeometryLeaseDescriptor,expectedGeometry as GeometryOpenParams);return !!check('CreatureGeometryResult',result)&&creatureGeometryMatches(result as CreatureGeometryResult,expectedGeometry as GeometryReadParams);}
    if(effectsMethod(method)){if(!expectedEffects)return false;if(method==='items.effects.data'||method==='items.effects.prepare')return !!check('ItemEffectsReadiness',result)&&itemEffectsReadinessMatches(result as ItemEffectsReadiness,expectedEffects);if(method==='items.effects.page')return !!check('ItemEffectsPage',result)&&itemEffectsPageMatches(result as ItemEffectsPage,expectedEffects as ItemEffectsPageParams);if(method==='items.effects.details'||method==='items.effects.details.prepare')return !!check('ItemEffectDetailsResult',result)&&itemEffectDetailsMatch(result as ItemEffectDetailsResult,expectedEffects as ItemEffectDetailsParams);return !!check('ItemEffectNamesResult',result)&&itemEffectNamesMatch(result as ItemEffectNamesResult,expectedEffects as ItemEffectNamesParams);}
    if(method==='creatures.display.read'||method==='creatures.display.prepare')return !!check('CreatureDisplayMetadataResult',result)&&!!expectedCreatures&&creatureDisplayMatches(result as CreatureDisplayMetadataResult,expectedCreatures as CreatureDisplayParams);
    if(method==='creatures.prepare')return !!check('CreaturesSnapshot',result)&&!!expectedCreatures&&creaturesSnapshotMatches(result as CreaturesSnapshot,expectedCreatures);
    if(method==='creatures.data')return !!check('CreaturesReadiness',result)&&!!expectedCreatures&&creaturesReadinessMatches(result as CreaturesReadiness,expectedCreatures);
    if(method==='creatures.page')return !!check('CreaturesPage',result)&&!!expectedCreatures&&creaturesPageMatches(result as CreaturesPage,expectedCreatures as CreaturePageParams);
    if(method==='creatures.inspect')return !!check('CreatureInspectResult',result)&&!!expectedCreatures&&creatureInspectMatches(result as CreatureInspectResult,expectedCreatures as CreatureInspectParams);
    if(method==='items.classifications')return !!check('ItemClassificationBatch',result)&&!!expectedClassifications&&classificationsMatch(result,expectedClassifications);
    if(method==='items.icons')return !!check('ItemIconBatch',result)&&!!expectedIcon&&iconBatchMatches(result as ItemIconBatch,expectedIcon as IconBatchParams);
    if(method==='items.icon.open')return !!check('ItemIconLeaseDescriptor',result)&&!!expectedIcon&&iconLeaseMatches(result as ItemIconLeaseDescriptor,expectedIcon as IconOpenParams);
    if(method==='items.icon.close')return !!result&&typeof result==='object'&&Object.keys(result).length===1&&(result as{closed?:unknown}).closed===true;
    if (method === 'items.prepare') return !!check('ItemsSnapshot',result) && !!expectedItems && itemsSnapshotMatches(result as ItemsSnapshot,expectedItems);
    if (method === 'items.data') return !!check('ItemsReadiness',result) && !!expectedItems && itemsReadinessMatches(result as ItemsReadiness,expectedItems);
    if (method === 'items.page') return !!check('ItemsPage',result) && !!expectedItems && itemsPageMatches(result as ItemsPage,expectedItems as ItemsPageParams);
    if (method === 'items.inspect') return !!check('ItemInspectResult',result) && !!expectedItems && itemInspectMatches(result as ItemInspectResult,expectedItems as ItemInspectParams);
    if (method === 'retention.read') return !!check('RetentionSnapshot', result) && !!expectedRetention && retentionMatches(result as RetentionSnapshot, expectedRetention);
    if (method === 'retention.set') return !!check('RetentionSnapshot', result) && !!expectedRetention && retentionSetMatches(result as RetentionSnapshot, expectedRetention as RetentionSet);
    if (method === 'retention.list') return !!check('RetentionList', result) && retentionListMatches(result as RetentionList);
    if (method === 'settings.read') return !!check('SettingsSnapshot', result) && settingsSnapshotMatches(result as SettingsSnapshot);
    if (method === 'settings.set') return !!check('SettingsSnapshot', result) && !!expectedSettings && settingsSetMatches(result as SettingsSnapshot, expectedSettings);
    if (method === 'settings.storage.measure') return !!check('StorageMeasurement', result) && storageMeasurementMatches(result as StorageMeasurement);
    if (method === 'settings.storage.builds') return !!check('PreparedDataList', result) && preparedDataListMatches(result as PreparedDataList);
    if (method === 'settings.storage.free') return !!check('PreparedDataFreed', result) && !!expectedFree && preparedDataFreedMatches(result as PreparedDataFreed, expectedFree);
    if (method === 'tables.prepare') return !!check('TableDataSnapshot', result) && !!expectedTableData && tableDataMatches(result as TableDataSnapshot, expectedTableData);
    if (method === 'tables.data') return !!check('TableDataReadiness', result) && !!expectedTableData && ((result as TableDataReadiness).snapshot === null || tableDataMatches((result as TableDataReadiness).snapshot!, expectedTableData));
    if (method === 'tables.rows') return !!check('TableDataPage', result) && !!expectedTableRows && tableRowsMatch(result as TableDataPage, expectedTableRows);
    if (method === 'tables.follow') return !!check('TableFollowPage', result) && !!expectedFollow && followMatches(result as TableFollowPage, expectedFollow);
    if (method === 'tables.referenceText') return !!check('TableReferenceTextPage', result) && !!expectedTableRows && referenceTextMatches(result as TableReferenceTextPage, expectedTableRows);
    if (method === 'tables.catalog') return !!check('TableCatalogPage', result) && !!expectedCatalog && tableCatalogMatches(result as TableCatalogPage, expectedCatalog);
    if (method === 'tables.definition') return !!check('TableDefinitionSnapshot', result) && !!expectedDefinition && tableDefinitionMatches(result as TableDefinitionSnapshot, expectedDefinition);
    if (method === 'download.read') return !!check('DownloadPage', result) && !!expectedDownload && downloadPageMatches(result as DownloadPage, expectedDownload);
    if (method === 'download.summaries') return !!check('DownloadSummaries', result) && !!expectedSummaries && summariesMatch(result as DownloadSummaries, expectedSummaries);
    if (method === 'download.changes') return !!check('DownloadChanges', result);
    if (method === 'inventory.read') return !!check('InventoryPage', result) && !!expectedInventory && inventoryPageMatches(result as InventoryPage, expectedInventory);
    if (method === 'inventory.files') return !!check('InventoryFilesPage', result) && !!expectedFiles && inventoryFilesPageMatches(result as InventoryFilesPage, expectedFiles as InventoryFilesParams);
    if (method === 'inventory.file') return !!check('InventoryFileDetail', result) && !!expectedFiles && inventoryFileDetailMatches(result as InventoryFileDetail, expectedFiles as InventoryFileParams);
    if (method === 'workspace.open') return !!check('WorkspaceSnapshot', result) && !!expectedOpen && workspaceMatches(result as WorkspaceSnapshot, expectedOpen);
    if (method === 'inventory.catalog') return !!check('FileCatalogStatus', result) && !!expectedOpen && fileCatalogMatches(result as FileCatalogStatus, expectedOpen);
    if (method === 'inventory.catalogs') return !!check('FileCatalogList', result) && fileCatalogListMatches(result as FileCatalogList);
    if (method === 'inventory.catalog.set') return !!check('FileCatalogStatus', result) && !!expectedCatalogSet && fileCatalogSetMatches(result as FileCatalogStatus, expectedCatalogSet);
    if (method === 'hello') return !!check('HelloResult', result);
    if (method === 'discovery.read' || method === 'discovery.refresh') return !!check('DiscoveryPage', result) && (result as { builds: unknown[] }).builds.length <= 100;
    if (method === 'discovery.readAll' || method === 'discovery.refreshAll') return !!check('DiscoveryCatalogPage', result) && !!expectedReadAll && catalogPageMatches(result as DiscoveryCatalogPage, expectedReadAll);
    if (!result || typeof result !== 'object') return false;
    const record = result as Record<string, unknown>;
    if (method === 'products.list') return Array.isArray(record.products) && record.products.length <= 100 && record.products.every(p => check('Product', p)) && Object.keys(record).length === 1;
    if (method === 'request.cancel') return typeof record.requested === 'boolean' && Object.keys(record).length === 1;
    return record.stopping === true && Object.keys(record).length === 1;
  }
  /** A closed pipe usually precedes the exit code that explains it, so wait briefly for that exit before failing generically. */
  private lost(error: EngineFailure): void {
    if (this.failure || this.lostTimer) return;
    if (this.exited) { this.fail(error); return; }
    this.lostTimer = setTimeout(() => this.fail(error), EXIT_GRACE_MS);
  }
  private fail(error: EngineFailure): void {
    if (this.failure) return;
    clearTimeout(this.lostTimer);
    this.failure = error;
    for (const operation of this.pending.values()) { clearTimeout(operation.timer); operation.reject(error); }
    this.pending.clear(); this.latestProgress.clear(); this.sequences.clear(); this.remainder = Buffer.alloc(0);
    clearTimeout(this.progressTimer);
    if (!this.exited) this.child.kill();
    this.emit('unavailable', error);
  }
  async shutdown(): Promise<void> {
    if (this.closing) return;
    this.closing = true;
    if (this.exited) return;
    let killTimer: ReturnType<typeof setTimeout>;
    const deadline = new Promise<void>(resolve => { killTimer = setTimeout(() => {
      if (!this.exited) this.child.kill('SIGKILL');
      this.fail(new EngineFailure('ENGINE_EXITED', 'The local engine was stopped during shutdown.'));
      resolve();
    }, 5000); });
    const orderly = this.failure ? this.exitPromise : this.request('shutdown', {}).then(() => { this.child.stdin.end(); return this.exitPromise; }).catch(() => this.exitPromise);
    try { await Promise.race([orderly, this.exitPromise, deadline]); }
    finally { clearTimeout(killTimer!); }
  }
}
