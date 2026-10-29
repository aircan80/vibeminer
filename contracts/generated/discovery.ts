/* Generated from contracts/discovery.schema.json. Run npm run contracts:generate. */

export type CreatureGeometryStage =
  | 'metadata'
  | 'sourceContext'
  | 'modelResolution'
  | 'modelAcquisition'
  | 'modelVerification'
  | 'modelParsing'
  | 'skinResolution'
  | 'skinAcquisition'
  | 'skinVerification'
  | 'skinParsing'
  | 'artifactBuild'
  | 'artifactVerification'
  | 'publication'
  | 'textureResolution'
  | 'textureAcquisition'
  | 'textureVerification'
  | 'textureDecoding'
  | 'appearanceBuild'
  | 'appearanceVerification'
  | 'appearancePublication';
export type CreatureGeometryDependency = 'metadata' | 'model' | 'skin' | 'artifact' | 'texture';
export type EngineRequest = EngineRequest1;
export type WorkspaceSnapshot = WorkspaceSnapshot1;
export type SettingsTimestamp = string;
/**
 * inventory.catalog reply (D071). pending carries its stage (and written memberships while writing); ready carries the sealed catalog Files reads; failed (after three attempts) and unsupported carry the cause, and the next Open retries both. removed: freed from Settings (D085); the next Open or resume prepares it again.
 */
export type FileCatalogStatus = FileCatalogStatus1;
export type TableDefinitionSnapshot = TableDefinitionSnapshot1;
export type ContentReceipt = ContentReceipt1;
/**
 * Opaque engine-run epoch and change count (D077). Compare for equality only.
 */
export type SummaryRevision = string;
export type ItemName = ValidItemName | EmptyItemName | UnavailableItemName;
export type ItemIconOutcome = ItemIconOutcome1;
export type ItemClassificationBatch = ItemClassificationBatch1;
export type ItemClassificationSource = ItemClassificationSource1 & ItemClassificationSource1;
export type ItemClassificationOutcome = {
  class?: {
    target?: {
      tableId?: 'b977271e';
    };
  };
  subclass?: {
    target?: {
      tableId?: '95d8638f';
    };
  };
} & ItemClassificationOutcome1 &
  ItemClassificationOutcome1;
export type ItemClassificationItemId = string;
export type ItemClassificationValue = ItemClassificationValue1 & ItemClassificationValue1;
export type ItemClassificationState =
  | 'ready'
  | 'emptyText'
  | 'invalidText'
  | 'missingItem'
  | 'fieldUnavailable'
  | 'keyOutOfRange'
  | 'notFound'
  | 'ambiguous'
  | 'incompleteTarget'
  | 'unavailable'
  | 'protected'
  | 'unsupported'
  | 'corrupt';
export type ItemClassificationTarget = ItemClassificationTarget1;
export type CreaturesSnapshot = {
  creatureSource?: {
    tableId?: 'c9d6b6b3';
    layoutHash?: '6E14C900';
    association?: {
      fileDataId?: '841631';
    };
  };
} & CreaturesSnapshot1;
export type CreaturesReadiness = CreaturesReadiness1;
export type CreaturesPage = CreaturesPage1;
export type CreatureId = string;
export type CreatureName = CreatureName1;
export type CreatureNameIssue = {
  source?: {
    columnIndex?: number;
  };
} & CreatureNameIssue1;
export type CreatureDisplaySlot = CreatureDisplaySlot1;
export type CreatureInspectResult = CreatureInspectResult1;
export type CreatureDetail = {
  creatureRow?: {
    id?: CreatureId;
    copySourceId?: CreatureId | null;
  };
} & CreatureDetail1;
export type CreatureDisplayMetadataResult = CreatureDisplayMetadataResult1;
export type CreatureDisplayTarget = CreatureDisplayTarget1 & CreatureDisplayTarget1;
export type CreatureDisplayRowEvidence = CreatureDisplayRowEvidence1;
export type ItemEffectsSourceRole = 'bridge' | 'effect' | 'name';
export type ItemEffectsSourceState =
  | 'notChecked'
  | 'notPrepared'
  | 'ready'
  | 'unavailable'
  | 'protected'
  | 'unsupported'
  | 'corrupt'
  | 'resourceLimit'
  | 'stale'
  | 'notInBuild';
export type ItemEffectsReadinessState =
  | 'notPrepared'
  | 'ready'
  | 'missingItem'
  | 'itemKeyOutOfRange'
  | 'unavailable'
  | 'protected'
  | 'unsupported'
  | 'corrupt'
  | 'resourceLimit'
  | 'notInBuild';
export type ItemEffectState =
  | 'notPrepared'
  | 'observedReady'
  | 'effectKeyOutOfRange'
  | 'fieldUnavailable'
  | 'notFoundInAdmittedScope'
  | 'ambiguous'
  | 'unavailable'
  | 'protected'
  | 'unsupported'
  | 'corrupt'
  | 'resourceLimit'
  | 'stale'
  | 'notInBuild';
export type ItemEffectNameState =
  | 'effectNotReady'
  | 'notPrepared'
  | 'ready'
  | 'emptyText'
  | 'invalidText'
  | 'textTooLarge'
  | 'spellKeyOutOfRange'
  | 'notFoundInAdmittedScope'
  | 'ambiguous'
  | 'unavailable'
  | 'protected'
  | 'unsupported'
  | 'corrupt'
  | 'resourceLimit'
  | 'stale'
  | 'notInBuild';
export type ItemEffectsAbsence = 'absentFromCompleteSource' | 'unknownOutsideReadableScope';
export type ItemEffectEvidenceKind = 'strictNumeric' | 'rawPhysicalInteger' | 'unverifiedCandidate';
export type ItemEffectsSource = ItemEffectsSource1;
export type ItemEffectsReadiness = ItemEffectsReadiness1;
export type ItemEffectsSourceValue = ItemEffectsSourceValue1;
export type ItemEffectsSourceReference = ItemEffectsSourceReference1;
export type ItemEffectFieldEvidence = ItemEffectFieldEvidence1;
export type ItemEffectOutcome = ItemEffectOutcome1;
export type ItemEffectSpellEvidence = ItemEffectSpellEvidence1;
export type ItemEffectNameOutcome = ItemEffectNameOutcome1;
export type ItemEffectNamesResult = ItemEffectNamesResult1;
export type ItemEffectsEngineProgress = EngineProgress & {
  requestId?: string;
  stage?: string;
  unit?: string;
  completed?: number | null;
  total?: number | null;
  sequence?: number;
  tableId: ('00cb674f' | '4002a5b1' | '46c66698') | null;
};
export type CreatureGeometryState =
  | 'notPrepared'
  | 'metadataNotResolved'
  | 'ready'
  | 'noDeclaredTriangles'
  | 'missingMembership'
  | 'ambiguousVariants'
  | 'encodingUnresolved'
  | 'protected'
  | 'unsupported'
  | 'unavailable'
  | 'corrupt'
  | 'resourceLimit';
export type CreatureGeometrySourceRole = 'model' | 'skin';
export type CreatureGeometryAppearanceState =
  | 'notPrepared'
  | 'ready'
  | 'characterModel'
  | 'multiTexture'
  | 'blendUnsupported'
  | 'textureWeight'
  | 'textureTransform'
  | 'materialUnsupported'
  | 'textureUnresolved'
  | 'textureMissing'
  | 'textureAmbiguous'
  | 'textureProtected'
  | 'textureUnsupported'
  | 'textureCorrupt'
  | 'textureUnavailable'
  | 'resourceLimit'
  | 'corrupt'
  | 'combinerUnsupported';
export type CreatureGeometryTextureState =
  | 'decoded'
  | 'unresolved'
  | 'notReached'
  | 'missing'
  | 'ambiguous'
  | 'protected'
  | 'unsupported'
  | 'corrupt'
  | 'unavailable'
  | 'resourceLimit';
/**
 * Textured draw of a ready geometry pair; see contracts/creature-geometry-v1.md#appearance.
 */
export type CreatureGeometryAppearance = CreatureGeometryAppearance1;
export type CreatureGeometryResult = CreatureGeometryResult1;

export interface DiscoveryContract {
  product: Product;
  sourceReceipt: SourceReceipt;
  cdnConfigObservation: CdnConfigObservation;
  build: Build;
  discoveryProblem: DiscoveryProblem;
  discoveryPage: DiscoveryPage;
  productDiscoveryStatus: ProductDiscoveryStatus;
  productRefreshProblem: ProductRefreshProblem;
  discoveryCatalogPage: DiscoveryCatalogPage;
  discoveryReadAllParams: DiscoveryReadAllParams;
  discoveryRefreshAllParams: DiscoveryRefreshAllParams;
  engineProgress: EngineProgress;
  engineError: EngineError;
  engineRequest: EngineRequest;
  helloResult: HelloResult;
  engineSuccess: EngineSuccess;
  engineFailure: EngineFailure;
  workspaceSnapshot: WorkspaceSnapshot;
  inventoryPage: InventoryPage;
  inventoryFilesParams?: InventoryFilesParams;
  inventoryFilesPage?: InventoryFilesPage;
  inventoryFileParams?: InventoryFileParams;
  inventoryFileDetail?: InventoryFileDetail;
  fileCatalogParams?: FileCatalogParams;
  fileCatalogStatus?: FileCatalogStatus;
  fileCatalogList?: FileCatalogList;
  fileCatalogSetParams?: FileCatalogSetParams;
  downloadPage: DownloadPage;
  downloadCheckParams?: DownloadCheckParams;
  downloadCheckResult?: DownloadCheckResult;
  tableCatalogPage: TableCatalogPage;
  tableDefinitionSnapshot: TableDefinitionSnapshot;
  tableDataSnapshot: TableDataSnapshot;
  tableDataReadiness: TableDataReadiness;
  tableDataPage: TableDataPage;
  tableReferenceTextPage?: TableReferenceTextPage;
  tableFollowPage?: TableFollowPage;
  retentionTarget: RetentionTarget;
  retentionSnapshot: RetentionSnapshot;
  retentionList: RetentionList;
  downloadSummariesParams?: DownloadSummariesParams;
  downloadSummaries?: DownloadSummaries;
  downloadChangesParams?: DownloadChangesParams;
  downloadChanges?: DownloadChanges;
  itemsSnapshot: ItemsSnapshot;
  itemsReadiness: ItemsReadiness;
  itemsPage: ItemsPage;
  itemInspectResult: ItemInspectResult;
  itemFactsResult?: ItemFactsResult;
  itemLabelLists?: ItemLabelLists;
  itemIconImage?: ItemIconImage;
  itemIconProblem?: ItemIconProblem;
  itemIconOutcome?: ItemIconOutcome;
  itemIconBatch?: ItemIconBatch;
  itemIconLeaseDescriptor?: ItemIconLeaseDescriptor;
  itemClassificationBatch?: ItemClassificationBatch;
  creaturesSnapshot?: CreaturesSnapshot;
  creaturesReadiness?: CreaturesReadiness;
  creaturesPage?: CreaturesPage;
  creatureInspectResult?: CreatureInspectResult;
  creatureDisplayMetadataResult?: CreatureDisplayMetadataResult;
  ItemEffectsSourceRole?: ItemEffectsSourceRole;
  ItemEffectsSourceState?: ItemEffectsSourceState;
  ItemEffectsReadinessState?: ItemEffectsReadinessState;
  ItemEffectState?: ItemEffectState;
  ItemEffectNameState?: ItemEffectNameState;
  ItemEffectsAbsence?: ItemEffectsAbsence;
  ItemEffectEvidenceKind?: ItemEffectEvidenceKind;
  ItemEffectsSelection?: ItemEffectsSelection;
  ItemEffectsPageRequest?: ItemEffectsPageRequest;
  ItemEffectsEdgeKey?: ItemEffectsEdgeKey;
  ItemEffectDetailsRequest?: ItemEffectDetailsRequest;
  ItemEffectNamesRequest?: ItemEffectNamesRequest;
  ItemEffectsProblem?: ItemEffectsProblem;
  ItemEffectsSource?: ItemEffectsSource;
  ItemEffectsFieldReference?: ItemEffectsFieldReference;
  ItemEffectsBridgeFields?: ItemEffectsBridgeFields;
  ItemEffectsIndexSnapshot?: ItemEffectsIndexSnapshot;
  ItemEffectsReadiness?: ItemEffectsReadiness;
  ItemEffectsSourceValue?: ItemEffectsSourceValue;
  ItemEffectsSourceReference?: ItemEffectsSourceReference;
  ItemEffectsEdge?: ItemEffectsEdge;
  ItemEffectsPage?: ItemEffectsPage;
  ItemEffectFieldEvidence?: ItemEffectFieldEvidence;
  ItemEffectOutcome?: ItemEffectOutcome;
  ItemEffectDetailsResult?: ItemEffectDetailsResult;
  ItemEffectSpellEvidence?: ItemEffectSpellEvidence;
  ItemEffectNameIssue?: ItemEffectNameIssue;
  ItemEffectNameOutcome?: ItemEffectNameOutcome;
  ItemEffectNamesResult?: ItemEffectNamesResult;
  ItemEffectsProgress?: ItemEffectsProgress;
  ItemEffectsSelectionParams?: ItemEffectsSelectionParams;
  ItemEffectsPageParams?: ItemEffectsPageParams;
  ItemEffectDetailsParams?: ItemEffectDetailsParams;
  ItemEffectNamesParams?: ItemEffectNamesParams;
  ItemEffectsEngineProgress?: ItemEffectsEngineProgress;
  creatureGeometryState?: CreatureGeometryState;
  creatureGeometrySourceRole?: CreatureGeometrySourceRole;
  creatureGeometryDependency?: CreatureGeometryDependency;
  creatureGeometryStage?: CreatureGeometryStage;
  creatureGeometryAppearanceState?: CreatureGeometryAppearanceState;
  creatureGeometryTextureState?: CreatureGeometryTextureState;
  creatureGeometryAppearanceRegion?: CreatureGeometryAppearanceRegion;
  creatureGeometryAppearanceDisplay?: CreatureGeometryAppearanceDisplay;
  creatureGeometryTextureImage?: CreatureGeometryTextureImage;
  creatureGeometryTextureSource?: CreatureGeometryTextureSource;
  creatureGeometryTextureVariant?: CreatureGeometryTextureVariant;
  creatureGeometryTexture?: CreatureGeometryTexture;
  creatureGeometryEffects?: CreatureGeometryEffects;
  creatureGeometryAppearanceProblem?: CreatureGeometryAppearanceProblem;
  creatureAppearanceManifest?: CreatureAppearanceManifest;
  creatureGeometryAppearance?: CreatureGeometryAppearance;
  creatureGeometryBufferRegion?: CreatureGeometryBufferRegion;
  creatureGeometryEvaluations?: CreatureGeometryEvaluations;
  creatureGeometryBounds?: CreatureGeometryBounds;
  creatureGeometryManifest?: CreatureGeometryManifest;
  creatureGeometryTarget?: CreatureGeometryTarget;
  creatureGeometryMetadata?: CreatureGeometryMetadata;
  creatureGeometrySource?: CreatureGeometrySource;
  creatureGeometryProblem?: CreatureGeometryProblem;
  creatureGeometryResult?: CreatureGeometryResult;
  creatureGeometryLeaseDescriptor?: CreatureGeometryLeaseDescriptor;
  creatureGeometryParams?: CreatureGeometryParams;
  creatureGeometryReadParams?: CreatureGeometryReadParams;
  creatureGeometryOpenParams?: CreatureGeometryOpenParams;
  creatureGeometryCloseParams?: CreatureGeometryCloseParams;
  creatureGeometryCloseResult?: CreatureGeometryCloseResult;
  settingsSnapshot?: SettingsSnapshot;
  settingsSetParams?: SettingsSetParams;
  storageMeasurement?: StorageMeasurement;
  communityListfileStatus?: CommunityListfileStatus;
  preparedDataList?: PreparedDataList;
  preparedDataFreeParams?: PreparedDataFreeParams;
  preparedDataFreed?: PreparedDataFreed;
}
export interface Product {
  id: string;
  label: string;
  description: string;
}
export interface SourceReceipt {
  region: string;
  role: 'versions' | 'cdns';
  url: string;
  retrievedAt: string;
  sha256: string;
  byteLength: number;
  rowOrdinal: number | null;
}
/**
 * One CDN configuration the build was advertised with. CDNConfig is not part of the build identity; each republished one is kept with the newest versions-row receipt per region.
 */
export interface CdnConfigObservation {
  cdnConfig: string;
  firstSeenAt: string;
  lastSeenAt: string;
  regions: string[];
  advertisedRegions: string[];
  sourceReceipts: SourceReceipt[];
}
export interface Build {
  id: string;
  productId: string;
  version: string;
  buildId: string;
  buildConfig: string;
  /**
   * The CDN configuration this projection is bound to: the newest advertised one in discovery pages, or the exact one an open or admission was made with.
   */
  cdnConfig: string;
  productConfig: string | null;
  identityFields: {
    [k: string]: string;
  };
  regions: string[];
  advertisedRegions: string[];
  routeRegions: string[];
  firstSeenAt: string;
  lastSeenAt: string;
  isNew: boolean;
  isAdvertised: boolean;
  isLocal: boolean;
  availability: 'unknown';
  sourceReceipts: SourceReceipt[];
  /**
   * Every CDN configuration this build was advertised with, newest first, capped to the newest 16.
   *
   * @minItems 1
   * @maxItems 16
   */
  cdnConfigHistory: CdnConfigObservation[];
  /**
   * How many CDN configurations the build has in total. When it exceeds cdnConfigHistory's length, the older ones are omitted from this projection but kept in the catalog.
   */
  cdnConfigHistoryCount: number;
}
export interface DiscoveryProblem {
  region: string | null;
  code: string;
  message: string;
  /**
   * problem: the check did not get everything it asked for. note: a regional fact about the official source that leaves the check complete (D076).
   */
  severity: 'problem' | 'note';
}
export interface DiscoveryPage {
  productId: string;
  builds: Build[];
  nextCursor: string | null;
  lastCheckedAt: string | null;
  checkedRegions: string[];
  errors: DiscoveryProblem[];
  hasSavedHistory: boolean;
  connection: 'notChecked' | 'online' | 'offline' | 'partial';
}
/**
 * The saved outcome of one product's latest check, the same facts a single-product DiscoveryPage carries without its builds.
 */
export interface ProductDiscoveryStatus {
  productId: string;
  lastCheckedAt: string | null;
  checkedRegions: string[];
  errors: DiscoveryProblem[];
  hasSavedHistory: boolean;
  connection: 'notChecked' | 'online' | 'offline' | 'partial';
}
/**
 * A product whose part of a cross-product refresh did not complete. Its saved evidence is unchanged.
 */
export interface ProductRefreshProblem {
  productId: string;
  code: string;
  message: string;
}
/**
 * One bounded page of builds across every registered product, ordered by registry product order, then numeric BuildId descending (missing or non-decimal last), then build identity.
 */
export interface DiscoveryCatalogPage {
  /**
   * The Product filter this page was read with; null reads every product.
   */
  productId: string | null;
  filter: 'all' | 'local' | 'advertised';
  /**
   * @maxItems 100
   */
  builds: Build[];
  nextCursor: string | null;
  /**
   * Every registered product's saved status in registry order, whatever the Product filter.
   *
   * @maxItems 100
   */
  products: ProductDiscoveryStatus[];
  /**
   * Products whose refresh attempt did not complete. Always empty for discovery.readAll.
   *
   * @maxItems 100
   */
  refreshProblems: ProductRefreshProblem[];
}
export interface DiscoveryReadAllParams {
  productId?: string;
  filter?: 'all' | 'local' | 'advertised';
  limit?: number;
  cursor?: string;
}
export interface DiscoveryRefreshAllParams {}
export interface EngineProgress {
  protocolVersion: 1;
  type: 'progress';
  requestId: string;
  stage: string;
  region: string | null;
  completed: number | null;
  total: number | null;
  unit: string | null;
  sequence: number;
  step?: {
    index: number;
    total: number | null;
    description: string;
  } | null;
  tableId?: string | null;
  geometryStage?: CreatureGeometryStage | null;
  geometryDependency?: CreatureGeometryDependency | null;
}
export interface EngineError {
  code: string;
  message: string;
  retryable: boolean;
  definition?: TableDefinitionProblem;
}
/**
 * Present on an error when no definition can read a table (D081, D101): the table, and the Settings switch as it read then (the same values as definitionFetch). Views decide their next step from it, never from the message.
 */
export interface TableDefinitionProblem {
  tableId: string;
  definitionFetch: 'on' | 'off';
}
export interface EngineRequest1 {
  protocolVersion: 1;
  id: string;
  method:
    | 'hello'
    | 'products.list'
    | 'discovery.read'
    | 'discovery.refresh'
    | 'discovery.readAll'
    | 'discovery.refreshAll'
    | 'workspace.open'
    | 'request.cancel'
    | 'shutdown'
    | 'inventory.read'
    | 'inventory.catalog'
    | 'inventory.catalogs'
    | 'inventory.catalog.set'
    | 'inventory.files'
    | 'inventory.file'
    | 'download.read'
    | 'download.summaries'
    | 'download.changes'
    | 'download.check'
    | 'tables.catalog'
    | 'tables.definition'
    | 'tables.prepare'
    | 'tables.data'
    | 'tables.rows'
    | 'tables.referenceText'
    | 'tables.follow'
    | 'retention.read'
    | 'retention.list'
    | 'retention.set'
    | 'items.data'
    | 'items.prepare'
    | 'items.page'
    | 'items.inspect'
    | 'items.facts'
    | 'items.labels'
    | 'items.icons'
    | 'items.icon.open'
    | 'items.icon.close'
    | 'items.classifications'
    | 'creatures.data'
    | 'creatures.prepare'
    | 'creatures.page'
    | 'creatures.inspect'
    | 'creatures.display.read'
    | 'creatures.display.prepare'
    | 'items.effects.data'
    | 'items.effects.prepare'
    | 'items.effects.page'
    | 'items.effects.details'
    | 'items.effects.details.prepare'
    | 'items.effects.names'
    | 'items.effects.names.prepare'
    | 'creatures.geometry.read'
    | 'creatures.geometry.prepare'
    | 'creatures.geometry.open'
    | 'creatures.geometry.close'
    | 'settings.read'
    | 'settings.set'
    | 'settings.listfile.read'
    | 'settings.listfile.check'
    | 'settings.storage.measure'
    | 'settings.storage.builds'
    | 'settings.storage.free';
  params: {
    [k: string]: unknown;
  };
}
export interface HelloResult {
  protocolVersion: 1;
  engineVersion: string;
  capabilities: string[];
}
export interface EngineSuccess {
  protocolVersion: 1;
  id: string;
  result: unknown;
}
export interface EngineFailure {
  protocolVersion: 1;
  id: string | null;
  error: EngineError;
}
export interface WorkspaceSnapshot1 {
  build: Build;
  openedAt: string;
  preparationProfile: 'home-configurations-v1' | 'home-inventory-v1' | 'home-download-v1';
  readiness: 'configurationReady' | 'partial';
  metadataState: 'configurationsVerified' | 'verified';
  inventoryState: 'notPrepared' | 'ready';
  retentionState: 'unknown' | 'measured';
  downloadPolicy: 'onDemand';
  researchLocale: null;
  platformScope: null;
  sourceReference: OpeningSourceReference;
  /**
   * @minItems 2
   * @maxItems 2
   */
  configurations: [ConfigurationReceipt, ConfigurationReceipt];
  inventory?: InventorySummary | null;
  metadata?:
    | [MetadataReceipt]
    | [MetadataReceipt, MetadataReceipt]
    | [MetadataReceipt, MetadataReceipt, MetadataReceipt]
    | [MetadataReceipt, MetadataReceipt, MetadataReceipt, MetadataReceipt]
    | [MetadataReceipt, MetadataReceipt, MetadataReceipt, MetadataReceipt, MetadataReceipt]
    | null;
  download?: DownloadSummary | null;
  downloadProblem?: DownloadProblem | null;
}
export interface OpeningSourceReference {
  generation: string;
  region: string;
  versionsSha256: string;
  versionsRowOrdinal: number;
  cdnsSha256: string;
  cdnsRowOrdinal: number;
}
export interface ConfigurationReceipt {
  role: 'buildConfig' | 'cdnConfig';
  key: string;
  sha256: string;
  byteLength: number;
  sourceUrl: string;
  retrievedAt: string;
  verifiedAt: string;
  parsedFieldCount: number;
}
export interface InventorySummary {
  membershipCount: string;
  rootMembershipCount: string;
  uniqueContentCount: string;
  downloadObjectCount: string;
  logicalDecodedBytes: string;
  totalEncodedBytes: string;
  verifiedEncodedBytes: string;
  remainingEncodedBytes: string;
  sharedVerifiedEncodedBytes: string;
  metadataEncodedBytes: string;
  generation: string;
  preparedAt: string;
  scope: 'allPublishedVariants';
}
export interface MetadataReceipt {
  role: 'encoding' | 'root' | 'install' | 'download' | 'size';
  contentKey: string;
  encodingKey: string;
  sha256: string;
  encodedBytes: string;
  decodedBytes: string;
  sourceUrl: string;
  retrievedAt: string;
  verifiedAt: string;
  parserRevision: string;
  location?: ContentLocation | null;
}
export interface ContentLocation {
  kind: 'loose' | 'archive';
  offset: string;
  sourceBytes: string;
  archiveKey: string | null;
}
export interface DownloadSummary {
  ledgerId: string;
  scope: 'downloadManifestAllVariants';
  objectCount: string;
  totalEncodedBytes: string;
  verifiedEncodedBytes: string;
  remainingEncodedBytes: string;
  assessedAt: string;
  metadataEncodedBytes: string;
}
export interface DownloadProblem {
  code: string;
  message: string;
}
export interface InventoryPage {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  generation: string;
  /**
   * @maxItems 100
   */
  entries: InventoryEntry[];
  totalCount: string;
  nextCursor: string | null;
}
export interface InventoryEntry {
  id: string;
  family: 'root' | 'install' | 'download' | 'size';
  name: string | null;
  fileDataId: string | null;
  contentKey: string | null;
  encodingKey: string;
  decodedBytes: string | null;
  encodedBytes: string;
  retained: boolean;
  localeFlags: string | null;
  contentFlags: string | null;
  nameHash: string | null;
}
/**
 * inventory.files: one bounded page of Files rows (D068). Every filter tests single memberships; a file is listed when one membership passes all of them.
 */
export interface InventoryFilesParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  generation: string;
  limit: number;
  after?: string;
  /**
   * Exact ROOT FileDataID.
   */
  fileDataId?: string;
  /**
   * Full lowercase CKey, or a prefix of at least four hex digits.
   */
  contentKey?: string;
  /**
   * Raw locale mask; a membership matches when it shares a bit.
   */
  localeFlags?: string;
  /**
   * none (derived content flags are zero), or a raw mask whose bits must all be set.
   */
  contentFlags?: string;
  family?: 'root' | 'install';
  storedOnly?: boolean;
}
export interface InventoryFilesPage {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  generation: string;
  /**
   * @maxItems 100
   */
  entries: InventoryFile[];
  totalCount: string | null;
  nextCursor: string | null;
  /**
   * The engine stopped at its per-request examination bound before filling the page; nextCursor continues the search.
   */
  scanLimited: boolean;
  communityNames: CommunityNamesSource;
}
/**
 * One ROOT FileDataID or one exact install name with every membership summarized. Sizes are encoded bytes of resolved memberships; unresolved ones are counted, never sized.
 */
export interface InventoryFile {
  key: string;
  family: 'root' | 'install';
  fileDataId: string | null;
  name: string | null;
  /**
   * Every membership of the file; sealing refuses a file with more than 4,096.
   */
  membershipCount: number;
  matchingCount: number;
  contentKeyCount: number;
  encodedBytesMin: string | null;
  encodedBytesMax: string | null;
  unresolvedCount: number;
  localeFlags: string | null;
  storedCount: number;
  /**
   * A community name from wow-listfile for a ROOT FileDataID (D105); null for install files and FileDataIDs it doesn't name. Never the file's identity.
   */
  communityName: string | null;
}
/**
 * Where a Files page's community names came from (D105): off (the switch reads as off, or the engine has no listfile), notReady (on, no verified copy yet and a download on its way), failed (on, no verified copy, the last attempt failed and nothing runs now), unreadable (on, but the names couldn't be read from the storage folder for this page) or ready, with the release tag, GitHub's publish time and when VibeMiner fetched it. Names are labels only.
 */
export interface CommunityNamesSource {
  state: 'off' | 'notReady' | 'failed' | 'unreadable' | 'ready';
  tag: string | null;
  publishedAt: SettingsTimestamp | null;
  fetchedAt: SettingsTimestamp | null;
}
/**
 * inventory.file: every membership of one Files row, in membership order, marked against the same filters.
 */
export interface InventoryFileParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  generation: string;
  limit: number;
  file: string;
  after?: string;
  /**
   * Exact ROOT FileDataID.
   */
  fileDataId?: string;
  /**
   * Full lowercase CKey, or a prefix of at least four hex digits.
   */
  contentKey?: string;
  /**
   * Raw locale mask; a membership matches when it shares a bit.
   */
  localeFlags?: string;
  /**
   * none (derived content flags are zero), or a raw mask whose bits must all be set.
   */
  contentFlags?: string;
  family?: 'root' | 'install';
  storedOnly?: boolean;
}
export interface InventoryFileDetail {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  generation: string;
  file: string;
  family: 'root' | 'install';
  fileDataId: string | null;
  name: string | null;
  membershipCount: number;
  matchingCount: number;
  /**
   * @maxItems 100
   */
  memberships: InventoryFileMembership[];
  nextCursor: string | null;
  /**
   * A community name from wow-listfile for a ROOT FileDataID (D105); null for install files and FileDataIDs it doesn't name. Never the file's identity.
   */
  communityName: string | null;
  communityNames: CommunityNamesSource;
}
export interface InventoryFileMembership {
  id: string;
  family: 'root' | 'install';
  blockOrdinal: number | null;
  entryOrdinal: number | null;
  installOrdinal: number | null;
  localeFlags: string | null;
  contentFlags: string | null;
  rawContentFlags: InventoryRawContentFlags | null;
  nameHash: string | null;
  contentKey: string;
  encodingKey: string | null;
  decodedBytes: string | null;
  encodedBytes: string | null;
  stored: boolean;
  matches: boolean;
}
/**
 * The three raw ROOT block parts; the derived content flags are first | second | (third << 17).
 */
export interface InventoryRawContentFlags {
  first: string;
  second: string;
  third: number;
}
/**
 * inventory.catalog: where one opened build's file catalog stands (D071). One bounded read; it never starts a transfer, count or check.
 */
export interface FileCatalogParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
}
export interface FileCatalogStatus1 {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  buildVersion: string | null;
  state: 'notOpened' | 'pending' | 'stopped' | 'ready' | 'failed' | 'unsupported' | 'removed';
  stage: ('queued' | 'paused' | 'acquiring' | 'waiting' | 'reading' | 'writing' | 'sealing') | null;
  completed: string | null;
  total: string | null;
  attempts: number;
  catalog: FileCatalogSummary | null;
  problem: FileCatalogProblem | null;
  waitingFor: FileCatalogBuild | null;
}
/**
 * A sealed ROOT/install file catalog (profile files-root-install-v1). Every count is exact.
 */
export interface FileCatalogSummary {
  generation: string;
  preparedAt: string;
  rootFileCount: string;
  installFileCount: string;
  rootMembershipCount: string;
  installMembershipCount: string;
  uniqueContentCount: string;
  /**
   * Memberships whose CKey has no sized encoded object in ENCODING: no EKey and no encoded size, never zero.
   */
  unresolvedMembershipCount: string;
  /**
   * Memberships whose EKey was verified locally when the row was written.
   */
  storedMembershipCount: string;
}
export interface FileCatalogProblem {
  code: string;
  message: string;
  retryable: boolean;
  /**
   * The underlying storage or I/O cause, for diagnosis.
   */
  detail: string | null;
}
export interface FileCatalogBuild {
  productId: string;
  buildIdentity: string;
  buildVersion: string;
}
/**
 * inventory.catalogs: the preparations Downloads & activity shows (pending, stopped or failed), most recently opened first.
 */
export interface FileCatalogList {
  /**
   * @maxItems 32
   */
  catalogs: FileCatalogStatus[];
}
/**
 * inventory.catalog.set: stop a pending preparation, or resume a stopped, failed or removed one.
 */
export interface FileCatalogSetParams {
  productId: string;
  buildIdentity: string;
  action: 'stop' | 'resume';
}
export interface DownloadPage {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  ledgerId: string;
  assessedAt: string;
  /**
   * @maxItems 100
   */
  entries: DownloadObjectEntry[];
  totalCount: string;
  nextCursor: string | null;
  /**
   * The admitted download manifest ledger whose Open assessment the page carries; the same as ledgerId for the manifest's own pages (D093).
   */
  manifestLedgerId: string;
  /**
   * The latest completed Check of a measured ledger and its counts; null for a manifest page or before a Check (D093).
   */
  lastCheck: DownloadCheckSummary | null;
}
export interface DownloadObjectEntry {
  id: string;
  encodingKey: string;
  encodedBytes: string;
  verifiedAtAssessment: boolean;
  /**
   * False for a file ROOT lists outside the admitted download manifest: Open's assessment never covered it (D093).
   */
  checkedAtOpen: boolean;
  /**
   * What the latest completed Check found for a file not checked at Open; null before one, and always null for a file Open assessed (D093).
   */
  check: ('verified' | 'notStored' | 'missing' | 'corrupt') | null;
}
/**
 * When a completed Check finished and what it found; the four outcomes add up to checkedObjects (D093).
 */
export interface DownloadCheckSummary {
  checkedAt: string;
  checkedObjects: string;
  verifiedObjects: string;
  notStoredObjects: string;
  missingObjects: string;
  corruptObjects: string;
}
/**
 * Check the files a measured ledger adds to its admitted manifest (D093). Long-running, with progress; cancel it with request.cancel.
 */
export interface DownloadCheckParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  ledgerId: string;
}
/**
 * One completed Check (D093). checkedObjects is every file the measured ledger adds to its manifest; the four outcomes add up to it.
 */
export interface DownloadCheckResult {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  ledgerId: string;
  manifestLedgerId: string;
  checkedAt: string;
  checkedObjects: string;
  verifiedObjects: string;
  notStoredObjects: string;
  missingObjects: string;
  corruptObjects: string;
}
export interface TableCatalogPage {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  provider: TableDefinitionProvider;
  query: string;
  /**
   * @maxItems 100
   */
  entries: TableCatalogEntry[];
  totalCount: string;
  nextCursor: string | null;
}
export interface TableDefinitionProvider {
  name: string;
  revision: string;
  manifestSha256: string;
  license: string;
  sourceUrl: string;
}
export interface TableCatalogEntry {
  tableId: string;
  name: string | null;
  db2FileDataId: string | null;
  definitionState: 'bundled' | 'notBundled';
  sourceState: 'notChecked';
  rowsState: 'notPrepared';
}
export interface TableDefinitionSnapshot1 {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  buildVersion: string;
  provider: TableDefinitionProvider;
  entry: TableCatalogEntry;
  definition: TableDefinitionInfo | null;
  problem: TablesProblem | null;
  /**
   * on: the Tables view may fetch a newer definition from WoWDBDefs when its data is prepared, exact build first (D081). off: the Settings switch is off or unreadable. Absent when the table has no bundled definition.
   */
  definitionFetch?: 'on' | 'off';
}
export interface TableDefinitionInfo {
  sha256: string;
  sourceUrl: string;
  /**
   * @maxItems 512
   */
  columns: TableDefinitionField[];
  /**
   * @maxItems 16
   */
  candidates:
    | []
    | [TableLayoutCandidate]
    | [TableLayoutCandidate, TableLayoutCandidate]
    | [TableLayoutCandidate, TableLayoutCandidate, TableLayoutCandidate]
    | [TableLayoutCandidate, TableLayoutCandidate, TableLayoutCandidate, TableLayoutCandidate]
    | [TableLayoutCandidate, TableLayoutCandidate, TableLayoutCandidate, TableLayoutCandidate, TableLayoutCandidate]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ]
    | [
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate,
        TableLayoutCandidate
      ];
  fetched?: TableDefinitionFetched;
}
export interface TableDefinitionField {
  name: string;
  family: 'int' | 'float' | 'string' | 'locstring';
  integerBits: number | null;
  unsigned: boolean | null;
  arrayLength: number;
  isId: boolean;
  nonInline: boolean;
  relationStorage: boolean;
  verifiedName: boolean;
  comment: string | null;
  reference: TableReference | null;
}
export interface TableReference {
  table: string;
  column: string;
}
export interface TableLayoutCandidate {
  layoutHash: string;
  /**
   * @maxItems 256
   */
  fields: TableDefinitionField[];
}
/**
 * Present when this definition was fetched from WoWDBDefs; sha256 and sourceUrl are then those of the fetched file. Absent: the bundled definition at the provider revision.
 */
export interface TableDefinitionFetched {
  revision: string;
  fetchedAt: SettingsTimestamp;
}
export interface TablesProblem {
  code: string;
  message: string;
}
export interface TableDataSnapshot {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  tableId: string;
  researchLocale: 'enUS';
  artifactId: string;
  preparedAt: string;
  layoutHash: string;
  definitionSha256: string;
  providerRevision: string;
  decoderRevision: string;
  source: ContentReceipt;
  association: SelectedFileAssociation;
  /**
   * @maxItems 512
   */
  columns: TableDataColumn[];
  coverage: TableDataCoverage;
  rowCount: string;
  textIssueCount?: string;
  /**
   * layoutOnly: no definition block lists this build; the file's layout hash chose the block (D080). Omitted: a block lists the exact build and layout.
   */
  schemaMatch?: 'layoutOnly';
  fetchedDefinition?: TableDefinitionFetched1;
  /**
   * With fetchedDefinition, which rule chose it (D081). exactBuild: it lists the build and the bundled definition doesn't. layoutNotBundled: the bundled definition lists the build but has no block for the file's layout, and the fetched one has one. layoutMatch: neither lists the build and only the fetched block matches the file's layout (schemaMatch layoutOnly).
   */
  fetchedBecause?: 'exactBuild' | 'layoutNotBundled' | 'layoutMatch';
  /**
   * With fetchedDefinition: true when the bundled definition alone would still open this file by layout, so turning fetching off keeps the table open.
   */
  bundledFallback?: boolean;
}
export interface ContentReceipt1 {
  contentKey: string;
  encodingKey: string;
  sha256: string;
  encodedBytes: string;
  decodedBytes: string;
  sourceUrl: string;
  retrievedAt: string;
  verifiedAt: string;
  verifierRevision: string;
  location: ContentLocation;
  projection?: ContentProjection | null;
}
export interface ContentProjection {
  policy: 'blte-clear-chunks-v1';
  identitySha256: string;
  /**
   * @maxItems 128
   */
  readableRanges: ContentReadableRange[];
  /**
   * @minItems 1
   * @maxItems 128
   */
  unavailableRanges: [ContentUnavailableRange, ...ContentUnavailableRange[]];
}
export interface ContentReadableRange {
  offset: string;
  byteLength: string;
  sha256: string;
}
export interface ContentUnavailableRange {
  offset: string;
  byteLength: string;
  chunkOrdinal: number;
  keyName: string;
}
export interface SelectedFileAssociation {
  fileDataId: string;
  researchLocale: 'enUS';
  variantPolicy: string;
  encodingSource: MetadataReceipt;
  rootSource: MetadataReceipt;
  /**
   * @maxItems 256
   */
  memberships: SelectedRootMembership[];
}
export interface SelectedRootMembership {
  blockOrdinal: string;
  entryOrdinal: string;
  contentKey: string;
  localeFlags: string;
  contentFlags: string;
  rawFirst: string;
  rawSecond: string;
  rawThird: string;
  nameHash: string | null;
}
export interface TableDataColumn {
  name: string;
  family: 'integer' | 'float' | 'string';
  isId: boolean;
  reference: TableReference | null;
  interpretation?: TableColumnInterpretation | null;
  narrowing?: TableColumnNarrowing;
}
export interface TableColumnInterpretation {
  policy: 'raw-physical-integer-v1';
  physicalBits: number;
  physicalSigned: boolean;
  candidateBits: number;
  candidateUnsigned: boolean;
  warning: string;
}
/**
 * D079: the column's 32-bit pallet or common values are wider than its definition, and each value keeps its low usedBits bits, signed or unsigned per the definition.
 */
export interface TableColumnNarrowing {
  storedBits: 32;
  usedBits: 8 | 16;
}
export interface TableDataCoverage {
  scope: 'complete' | 'unencryptedSections';
  declaredRecords: string;
  physicalRows: string;
  copyRows: string;
  excludedKeyedSections: string;
  excludedDeclaredRecords: string;
  declaredCopyRecords: string;
  excludedPhysicalRecords: string;
  excludedCopyRecords: string;
}
/**
 * Present when the rows were read with a definition fetched from WoWDBDefs (D081); providerRevision is then its commit.
 */
export interface TableDefinitionFetched1 {
  revision: string;
  fetchedAt: SettingsTimestamp;
}
export interface TableDataReadiness {
  snapshot: TableDataSnapshot | null;
}
export interface TableDataPage {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  tableId: string;
  researchLocale: 'enUS';
  artifactId: string;
  /**
   * @maxItems 50
   */
  rows: TableDataRow[];
  totalCount: string;
  nextCursor: string | null;
}
export interface TableDataRow {
  id: string;
  isCopy: boolean;
  copySourceId: string | null;
  /**
   * @maxItems 512
   */
  values: string[];
  textIssues?: TableTextIssue[] | null;
}
export interface TableTextIssue {
  columnIndex: number;
  code: 'DB2_TEXT_ENCODING_INVALID';
  rawBytesHex: string;
}
export interface TableReferenceTextPage {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  tableId: string;
  researchLocale: 'enUS';
  artifactId: string;
  after: string;
  recordId: string | null;
  policyRevision: 'table-reference-text-v1';
  /**
   * @maxItems 50
   */
  rowIds: string[];
  /**
   * @maxItems 512
   */
  columns: TableReferenceTextColumn[];
  /**
   * @maxItems 25600
   */
  values: TableReferenceTextValue[];
}
export interface TableReferenceTextColumn {
  columnIndex: number;
  name: string;
  reference: TableReference;
  state: 'ready' | 'notPrepared' | 'noTextRule' | 'noDefinition' | 'unsupported' | 'unindexed' | 'unreadable';
  textSource: TableReferenceTextSource | null;
  problem: TableReferenceTextProblem | null;
}
export interface TableReferenceTextSource {
  tableId: string;
  table: string;
  textColumn: string;
  matchColumn: string;
  artifactId: string | null;
  scope: ('complete' | 'unencryptedSections') | null;
  measuredEncodedBytes: string;
  loadPolicy: 'automatic' | 'onRequest';
}
export interface TableReferenceTextProblem {
  code: string;
  message: string;
}
export interface TableReferenceTextValue {
  columnIndex: number;
  value: string;
  state: 'resolved' | 'empty' | 'notFound' | 'keyOutOfRange' | 'multiple' | 'unreadable' | 'textTooLong';
  text: string | null;
  matchCount: string;
  row: TableReferenceTextRow | null;
}
export interface TableReferenceTextRow {
  id: string;
  isCopy: boolean;
  copySourceId: string | null;
}
export interface TableFollowPage {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  policyRevision: 'table-follow-v1';
  tableId: string;
  artifactId: string;
  columnIndex: number;
  column: string;
  value: string;
  reference: TableReference;
  targetTableId: string;
  targetArtifactId: string;
  targetColumnIndex: number | null;
  targetIsId: boolean;
  targetRange: TableFollowRange | null;
  scope: 'complete' | 'unencryptedSections';
  state: 'ready' | 'keyOutOfRange' | 'unindexed' | 'unsupported';
  after: string;
  page: TableDataPage | null;
  problem: TableReferenceTextProblem | null;
}
export interface TableFollowRange {
  minimum: string;
  maximum: string;
}
export interface RetentionTarget {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  ledgerId: string;
}
export interface RetentionSnapshot {
  target: RetentionTarget;
  jobId: string | null;
  revision: string;
  mode: 'onDemand' | 'downloadAll';
  paused: boolean;
  state: 'onDemand' | 'queued' | 'preparing' | 'running' | 'waiting' | 'stopping' | 'paused' | 'completed' | 'failed';
  scope: 'downloadManifestAllVariants' | 'downloadManifestAndRootAllVariants';
  objectCount: string;
  totalEncodedBytes: string;
  coverage: RetentionCoverage;
  progress: RetentionProgress | null;
  pendingObjects: number;
  wireBytesThisSession: string;
  unresolvedObjects: string;
  failedObjects: string;
  queuePosition: number | null;
  problem: RetentionProblem | null;
}
export interface RetentionCoverage {
  receiptObjects: string;
  receiptBytes: string;
  receiptedAt: string | null;
  presenceState: 'notAssessed' | 'assessing' | 'assessed' | 'interrupted';
  presenceAssessedAt: string | null;
  observedObjects: string;
  presentObjects: string;
  presentBytes: string;
  missingObjects: string;
  corruptObjects: string;
}
export interface RetentionProgress {
  stage: string;
  unit: string;
  completed: string | null;
  total: string | null;
}
export interface RetentionProblem {
  code: string;
  message: string;
  retryable: boolean;
}
export interface RetentionList {
  /**
   * @maxItems 9
   */
  jobs:
    | []
    | [RetentionSnapshot]
    | [RetentionSnapshot, RetentionSnapshot]
    | [RetentionSnapshot, RetentionSnapshot, RetentionSnapshot]
    | [RetentionSnapshot, RetentionSnapshot, RetentionSnapshot, RetentionSnapshot]
    | [RetentionSnapshot, RetentionSnapshot, RetentionSnapshot, RetentionSnapshot, RetentionSnapshot]
    | [RetentionSnapshot, RetentionSnapshot, RetentionSnapshot, RetentionSnapshot, RetentionSnapshot, RetentionSnapshot]
    | [
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot
      ]
    | [
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot
      ]
    | [
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot,
        RetentionSnapshot
      ];
}
export interface DownloadSummariesParams {
  /**
   * @minItems 1
   * @maxItems 100
   */
  builds: [BuildKey, ...BuildKey[]];
}
export interface BuildKey {
  productId: string;
  buildIdentity: string;
}
export interface DownloadSummaries {
  /**
   * @maxItems 100
   */
  summaries: BuildDownloadSummary[];
  revision: SummaryRevision;
}
export interface BuildDownloadSummary {
  productId: string;
  buildIdentity: string;
  state: 'notOpened' | 'notMeasured' | 'measured';
  download: BuildStoredCoverage | null;
}
export interface BuildStoredCoverage {
  ledgerId: string;
  scope: 'downloadManifestAllVariants' | 'downloadManifestAndRootAllVariants';
  objectCount: string;
  totalEncodedBytes: string;
  storedObjects: string;
  storedBytes: string;
  countState: 'counting' | 'current' | 'unavailable';
  countProblem: RetentionProblem | null;
  updatedAt: string;
  presence: BuildPresenceCheck;
  downloadAll: boolean;
  running: boolean;
  /**
   * @minItems 1
   * @maxItems 64
   */
  sourceRegions: [string, ...string[]];
  unresolvableRootContent: string;
  scopeState: 'measuring' | 'measured' | 'manifestOnly';
  scopeProblem: RetentionProblem | null;
  changesPending: boolean;
  /**
   * The admitted download manifest ledger that ledgerId stands for; the same ledger until the file list is measured (D093).
   */
  manifestLedgerId: string;
}
export interface BuildPresenceCheck {
  state: 'notAssessed' | 'assessing' | 'assessed' | 'interrupted';
  assessedAt: string | null;
  observedObjects: string;
  missingObjects: string;
  corruptObjects: string;
}
export interface DownloadChangesParams {
  after: SummaryRevision;
}
export interface DownloadChanges {
  revision: SummaryRevision;
}
export interface ItemsSnapshot {
  context: ItemsContext;
  artifactId: string;
  preparedAt: string;
  featureRevision: 'items-projection-v1' | 'items-projection-v2';
  queryPolicy: 'ordinal-ignorecase-contains-v1';
  itemSource: TableDataSnapshot;
  sparseSource: TableDataSnapshot;
  counts: ItemsJoinCounts;
  /**
   * @minItems 0
   * @maxItems 7
   */
  propertyGroups:
    | []
    | [ItemPropertyGroup]
    | [ItemPropertyGroup, ItemPropertyGroup]
    | [ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup]
    | [ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup]
    | [ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup]
    | [ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup, ItemPropertyGroup]
    | [
        ItemPropertyGroup,
        ItemPropertyGroup,
        ItemPropertyGroup,
        ItemPropertyGroup,
        ItemPropertyGroup,
        ItemPropertyGroup,
        ItemPropertyGroup
      ];
}
export interface ItemsContext {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
}
export interface ItemsJoinCounts {
  itemRows: string;
  sparseRows: string;
  matchedRows: string;
  itemWithoutSparseRows: string;
  sparseOnlyRows: string;
  validNames: string;
  emptyNames: string;
  invalidNames: string;
}
export interface ItemPropertyGroup {
  key:
    | 'identity'
    | 'classification'
    | 'requirements'
    | 'inventoryEconomy'
    | 'socketsStats'
    | 'restrictions'
    | 'otherInputs';
  /**
   * @minItems 1
   * @maxItems 1024
   */
  fields: [ItemFieldReference, ...ItemFieldReference[]];
}
export interface ItemFieldReference {
  tableId: '50238ec2' | '919be54e';
  columnIndex: number;
}
export interface ItemsReadiness {
  snapshot: ItemsSnapshot | null;
  /**
   * @minItems 0
   * @maxItems 2
   */
  dependencies: [] | [ItemsDependencyReadiness] | [ItemsDependencyReadiness, ItemsDependencyReadiness];
}
export interface ItemsDependencyReadiness {
  tableId: '50238ec2' | '919be54e';
  state: 'ready' | 'notPrepared';
  artifactId: string | null;
}
export interface ItemsPage {
  context: ItemsContext;
  artifactId: string;
  query: string;
  exactItemId: string | null;
  /**
   * @minItems 0
   * @maxItems 50
   */
  entries: ItemSummary[];
  nextCursor: string | null;
}
export interface ItemSummary {
  id: string;
  name: ItemName;
  /**
   * @minItems 0
   * @maxItems 8
   */
  baseValues:
    | []
    | [ItemSourceValue]
    | [ItemSourceValue, ItemSourceValue]
    | [ItemSourceValue, ItemSourceValue, ItemSourceValue]
    | [ItemSourceValue, ItemSourceValue, ItemSourceValue, ItemSourceValue]
    | [ItemSourceValue, ItemSourceValue, ItemSourceValue, ItemSourceValue, ItemSourceValue]
    | [ItemSourceValue, ItemSourceValue, ItemSourceValue, ItemSourceValue, ItemSourceValue, ItemSourceValue]
    | [
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue
      ]
    | [
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue,
        ItemSourceValue
      ];
  iconFileDataId: string | null;
}
export interface ValidItemName {
  state: 'valid';
  value: string;
}
export interface EmptyItemName {
  state: 'empty';
  value: '';
}
export interface UnavailableItemName {
  state: 'invalid' | 'missingSparse';
  value: null;
}
export interface ItemSourceValue {
  tableId: '50238ec2' | '919be54e';
  columnIndex: number;
  value: string;
}
export interface ItemInspectResult {
  context: ItemsContext;
  artifactId: string;
  itemId: string;
  detail: ItemDetail | null;
}
export interface ItemDetail {
  summary: ItemSummary;
  itemRow: TableDataRow;
  sparseRow: TableDataRow | null;
}
export interface ItemFactsResult {
  context: ItemsContext;
  artifactId: string;
  itemId: string;
  policyRevision: 'item-facts-v1';
  labelRevision: 'item-labels-v1';
  sparse: 'present' | 'missing' | 'unknown';
  facts: ItemFact[] | null;
}
export interface ItemFact {
  key:
    | 'description'
    | 'display1'
    | 'display2'
    | 'display3'
    | 'icon'
    | 'quality'
    | 'itemLevel'
    | 'itemSquishEra'
    | 'contentTuning'
    | 'playerLevelCurve'
    | 'itemLevelOffsetCurve'
    | 'itemLevelOffset'
    | 'class'
    | 'subclass'
    | 'inventoryType'
    | 'requiredLevel'
    | 'requiredSkill'
    | 'requiredSkillRank'
    | 'requiredAbility'
    | 'minFaction'
    | 'minReputation'
    | 'allowableClass'
    | 'allowableRaces'
    | 'bonding'
    | 'stackable'
    | 'maxCount'
    | 'containerSlots'
    | 'buyPrice'
    | 'sellPrice'
    | 'vendorStackCount'
    | 'durationInInventory'
    | 'statType'
    | 'statAmount'
    | 'statPercentEditor'
    | 'statSocketShare'
    | 'socketType'
    | 'socketBonus'
    | 'gemProperties'
    | 'damageType'
    | 'itemDelay'
    | 'dmgVariance'
    | 'itemRange'
    | 'flags'
    | 'zoneBound'
    | 'instanceBound'
    | 'requiredHoliday'
    | 'priceVariance'
    | 'priceRandomValue';
  state: 'value' | 'missing' | 'unknown' | 'unsupported' | 'invalidText';
  agreement: 'single' | 'agree' | 'disagree' | 'separate';
  /**
   * @minItems 0
   * @maxItems 16
   */
  values:
    | []
    | [ItemFactValue]
    | [ItemFactValue, ItemFactValue]
    | [ItemFactValue, ItemFactValue, ItemFactValue]
    | [ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue]
    | [ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue]
    | [ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue]
    | [ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue, ItemFactValue]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ]
    | [
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue,
        ItemFactValue
      ];
}
export interface ItemFactValue {
  tableId: '50238ec2' | '919be54e';
  table: 'Item' | 'ItemSparse';
  column: string;
  columnIndex: number;
  recordId: string;
  value: string;
  label: string | null;
}
export interface ItemLabelLists {
  labelRevision: 'item-labels-v1';
  /**
   * @minItems 0
   * @maxItems 64
   */
  quality: ItemLabel[];
  /**
   * @minItems 0
   * @maxItems 64
   */
  inventoryType: ItemLabel[];
  /**
   * @minItems 0
   * @maxItems 64
   */
  bonding: ItemLabel[];
  /**
   * @minItems 0
   * @maxItems 64
   */
  trigger: ItemLabel[];
}
export interface ItemLabel {
  value: string;
  label: string;
}
export interface ItemIconImage {
  artifactId: string;
  width: number;
  height: number;
  byteLength: string;
}
export interface ItemIconProblem {
  code: string;
  message: string;
  retryable: boolean;
}
export interface ItemIconOutcome1 {
  itemId: string;
  fileDataId: string | null;
  status:
    | 'ready'
    | 'missingItem'
    | 'fieldUnavailable'
    | 'noIcon'
    | 'missingMembership'
    | 'ambiguousVariants'
    | 'encodingUnresolved'
    | 'protected'
    | 'unsupported'
    | 'unavailable'
    | 'corrupt';
  image: ItemIconImage | null;
  problem: ItemIconProblem | null;
}
export interface ItemIconBatch {
  context: ItemsContext;
  artifactId: string;
  /**
   * @minItems 1
   * @maxItems 50
   */
  outcomes: [ItemIconOutcome, ...ItemIconOutcome[]];
}
export interface ItemIconLeaseDescriptor {
  leaseId: string;
  artifactId: string;
  sha256: string;
  byteLength: string;
  width: number;
  height: number;
}
export interface ItemClassificationBatch1 {
  context: ItemsContext;
  artifactId: string;
  policyRevision: 'classification-label-index-v1' | 'classification-label-index-v2';
  /**
   * @minItems 1
   * @maxItems 2
   */
  sources: [ItemClassificationSource] | [ItemClassificationSource, ItemClassificationSource];
  /**
   * @minItems 1
   * @maxItems 50
   */
  outcomes: [ItemClassificationOutcome, ...ItemClassificationOutcome[]];
}
export interface ItemClassificationSource1 {
  kind: 'class' | 'subclass';
  state: 'ready' | 'incompleteTarget' | 'unavailable' | 'protected' | 'unsupported' | 'corrupt';
  snapshot: TableDataSnapshot | null;
  indexArtifactId: string | null;
  problem: ItemClassificationProblem | null;
}
export interface ItemClassificationProblem {
  code: string;
  message: string;
  retryable: boolean;
}
export interface ItemClassificationOutcome1 {
  itemId: ItemClassificationItemId;
  admitted: boolean;
  class: ItemClassificationValue;
  subclass: ItemClassificationValue | null;
}
export interface ItemClassificationValue1 {
  state: ItemClassificationState;
  /**
   * @maxItems 2
   */
  sourceKeys: [] | [ItemClassificationSourceKey] | [ItemClassificationSourceKey, ItemClassificationSourceKey];
  text: string | null;
  target: ItemClassificationTarget | null;
  observedCandidates: string | null;
  problem: ItemClassificationProblem | null;
}
export interface ItemClassificationSourceKey {
  tableId: '50238ec2';
  columnIndex: number;
  value: string;
}
export interface ItemClassificationTarget1 {
  tableId: 'b977271e' | '95d8638f';
  rowId: ItemClassificationItemId;
  columnIndex: number;
  isCopy: boolean;
  copySourceId: ItemClassificationItemId | null;
}
export interface CreaturesSnapshot1 {
  context: CreaturesContext;
  artifactId: string;
  preparedAt: string;
  featureRevision: 'creatures-core-v1';
  queryPolicy: 'primary-name-ordinal-ignorecase-contains-v1';
  semanticProfile: 'creatures-direct-display-v1';
  creatureSource: TableDataSnapshot;
  counts: CreatureCounts;
  fields: CreatureFieldMap;
}
export interface CreaturesContext {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
}
export interface CreatureCounts {
  rows: string;
  validNames: string;
  emptyNames: string;
  invalidNames: string;
  positiveDisplaySlots: string;
  zeroDisplaySlots: string;
  invalidDisplaySlots: string;
}
export interface CreatureFieldMap {
  name: CreatureFieldReference;
  nameAlt: CreatureFieldReference;
  title: CreatureFieldReference;
  titleAlt: CreatureFieldReference;
  /**
   * @minItems 4
   * @maxItems 4
   */
  facts: [CreatureFieldReference, CreatureFieldReference, CreatureFieldReference, CreatureFieldReference];
  /**
   * @minItems 4
   * @maxItems 4
   */
  displayIds: [CreatureFieldReference, CreatureFieldReference, CreatureFieldReference, CreatureFieldReference];
  /**
   * @minItems 4
   * @maxItems 4
   */
  probabilities: [CreatureFieldReference, CreatureFieldReference, CreatureFieldReference, CreatureFieldReference];
}
export interface CreatureFieldReference {
  tableId: 'c9d6b6b3';
  columnIndex: number;
}
export interface CreaturesReadiness1 {
  snapshot: CreaturesSnapshot | null;
  dependencyState: 'notPrepared' | 'ready';
  sourceArtifactId: string | null;
}
export interface CreaturesPage1 {
  context: CreaturesContext;
  artifactId: string;
  query: string;
  exactCreatureId: CreatureId | null;
  /**
   * @minItems 0
   * @maxItems 50
   */
  entries: CreatureSummary[];
  nextCursor: string | null;
}
export interface CreatureSummary {
  id: CreatureId;
  name: CreatureName;
  /**
   * @minItems 4
   * @maxItems 4
   */
  facts: [CreatureSourceValue, CreatureSourceValue, CreatureSourceValue, CreatureSourceValue];
  /**
   * @minItems 4
   * @maxItems 4
   */
  displaySlots: [
    CreatureDisplaySlot & {
      slot?: 0;
    },
    CreatureDisplaySlot & {
      slot?: 1;
    },
    CreatureDisplaySlot & {
      slot?: 2;
    },
    CreatureDisplaySlot & {
      slot?: 3;
    }
  ];
}
export interface CreatureName1 {
  state: 'valid' | 'empty' | 'invalid';
  value: string | null;
  issue: CreatureNameIssue | null;
}
export interface CreatureNameIssue1 {
  code: 'DB2_TEXT_ENCODING_INVALID';
  source: CreatureSourceReference;
}
export interface CreatureSourceReference {
  tableId: 'c9d6b6b3';
  artifactId: string;
  rowId: CreatureId;
  columnIndex: number | null;
}
export interface CreatureSourceValue {
  tableId: 'c9d6b6b3';
  columnIndex: number;
  value: string;
}
export interface CreatureDisplaySlot1 {
  slot: number;
  displayId: string;
  probability: string;
  state: 'declared' | 'noDeclaredDisplay' | 'invalidDisplayReference';
  displayField: CreatureFieldReference;
  probabilityField: CreatureFieldReference;
}
export interface CreatureInspectResult1 {
  context: CreaturesContext;
  artifactId: string;
  creatureId: CreatureId;
  state: 'found' | 'notFoundInAdmittedScope';
  coverage: TableDataCoverage;
  detail: CreatureDetail | null;
}
export interface CreatureDetail1 {
  summary: CreatureSummary;
  creatureRow: TableDataRow;
  source: CreatureSourceReference;
}
export interface CreatureDisplayMetadataResult1 {
  context: CreaturesContext;
  artifactId: string;
  creatureId: CreatureId;
  slot: number;
  metadataPolicy: 'creature-display-metadata-v1';
  opening: OpeningSourceReference;
  creatureSource: CreatureSourceReference;
  creatureCoverage: TableDataCoverage;
  selected: CreatureDisplaySlot | null;
  state:
    | 'notPrepared'
    | 'creatureNotFoundInAdmittedScope'
    | 'noDeclaredDisplay'
    | 'invalidDisplayReference'
    | 'displayMissingInAdmittedScope'
    | 'displayAmbiguous'
    | 'noDeclaredModel'
    | 'modelMissingInAdmittedScope'
    | 'modelAmbiguous'
    | 'noDeclaredModelFile'
    | 'metadataResolved'
    | 'protected'
    | 'unsupported'
    | 'unavailable'
    | 'corrupt'
    | 'resourceLimit';
  appearance: 'notEvaluated';
  /**
   * @minItems 0
   * @maxItems 2
   */
  sources:
    | []
    | [
        CreatureDisplayTarget & {
          role?: 'display';
        }
      ]
    | [
        CreatureDisplayTarget & {
          role?: 'display';
        },
        CreatureDisplayTarget & {
          role?: 'model';
        }
      ];
  absence: ('unknownOutsideReadableScope' | 'absentFromCompleteSource') | null;
  modelId: string | null;
  modelFileDataId: CreatureId | null;
  requiredTableId: ('bfdaf9f1' | '7f695fa5') | null;
  problem: CreatureDisplayMetadataProblem | null;
}
export interface CreatureDisplayTarget1 {
  role: 'display' | 'model';
  snapshot: TableDataSnapshot;
  matchCount: string | null;
  row: CreatureDisplayRowEvidence | null;
}
export interface CreatureDisplayRowEvidence1 {
  source: CreatureDisplayTargetReference;
  isCopy: boolean;
  copySourceId: CreatureId | null;
  edgeValue: CreatureDisplayTargetValue;
}
export interface CreatureDisplayTargetReference {
  tableId: 'bfdaf9f1' | '7f695fa5';
  artifactId: string;
  rowId: CreatureId;
  columnIndex: number | null;
}
export interface CreatureDisplayTargetValue {
  tableId: 'bfdaf9f1' | '7f695fa5';
  columnIndex: number;
  value: CreatureId;
}
export interface CreatureDisplayMetadataProblem {
  code: string;
  message: string;
  retryable: boolean;
  target: ('display' | 'model') | null;
}
export interface ItemEffectsSelection {
  context: ItemsContext;
  itemsArtifactId: string;
  itemId: CreatureId;
}
export interface ItemEffectsPageRequest {
  selection: ItemEffectsSelection;
  bridgeIndexArtifactId: string;
  limit: number;
  after: string | null;
}
export interface ItemEffectsEdgeKey {
  indexOrdinal: string;
}
export interface ItemEffectDetailsRequest {
  selection: ItemEffectsSelection;
  bridgeIndexArtifactId: string;
  /**
   * @minItems 1
   * @maxItems 16
   */
  edges:
    | [ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ];
}
export interface ItemEffectNamesRequest {
  selection: ItemEffectsSelection;
  bridgeIndexArtifactId: string;
  itemEffectArtifactId: string;
  /**
   * @minItems 1
   * @maxItems 8
   */
  edges:
    | [ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ];
  policyRevision: 'spell-name-by-emitted-id-v1';
}
export interface ItemEffectsProblem {
  code: string;
  message: string;
  retryable: boolean;
}
export interface ItemEffectsSource1 {
  role: ItemEffectsSourceRole;
  state: ItemEffectsSourceState;
  snapshot: TableDataSnapshot | null;
  historicalArtifactId: string | null;
  problem: ItemEffectsProblem | null;
}
export interface ItemEffectsFieldReference {
  tableId: '00cb674f';
  columnIndex: number;
}
export interface ItemEffectsBridgeFields {
  rowId: ItemEffectsFieldReference & {
    columnIndex?: 0;
  };
  itemEffectId: ItemEffectsFieldReference & {
    columnIndex?: 1;
  };
  itemId: ItemEffectsFieldReference & {
    columnIndex?: 2;
  };
}
export interface ItemEffectsIndexSnapshot {
  context: ItemsContext;
  artifactId: string;
  preparedAt: string;
  revision: 'item-effect-bridge-index-v1';
  orderPolicy: 'signed-item-unsigned-bridge-source-ordinal-v1';
  bridgeSource: TableDataSnapshot &
    ((
      | {
          definitionSha256?: '3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb';
          providerRevision?: 'b9a63096cdd99b26967f17f9515377ae62cadfd7';
        }
      | {
          providerRevision?: string;
        }
    ) & {
      tableId?: '00cb674f';
      layoutHash?: '96F083AD';
      association?: {
        fileDataId?: '3177687';
      };
      decoderRevision?: 'wdc5-values-v7+table-admission-v3' | 'wdc5-values-v7+table-admission-projection-v3';
      /**
       * @minItems 3
       * @maxItems 3
       */
      columns?: [
        {
          name: 'ID';
          family: 'integer';
          isId: true;
          reference: null;
          interpretation: null;
        },
        {
          name: 'ItemEffectID';
          family: 'integer';
          isId: false;
          reference: {table: 'ItemEffect'; column: 'ID'};
          interpretation: null;
        },
        {
          name: 'ItemID';
          family: 'integer';
          isId: false;
          reference: {table: 'Item'; column: 'ID'};
          interpretation: null;
        }
      ];
      rowCount?: string;
    });
  fields: ItemEffectsBridgeFields;
  emittedRows: string;
}
export interface ItemEffectsReadiness1 {
  selection: ItemEffectsSelection;
  admitted: boolean;
  state: ItemEffectsReadinessState;
  snapshot: ItemEffectsIndexSnapshot | null;
  observedAssociations: string | null;
  source:
    | (ItemEffectsSource & {
        role?: 'bridge';
      })
    | null;
  problem: ItemEffectsProblem | null;
}
export interface ItemEffectsSourceValue1 {
  tableId: '00cb674f' | '4002a5b1';
  columnIndex: number;
  value: string;
}
export interface ItemEffectsSourceReference1 {
  tableId: '00cb674f' | '4002a5b1' | '46c66698';
  artifactId: string;
  rowId: CreatureId;
  isCopy: boolean;
  copySourceId: CreatureId | null;
  sourceOrdinal: string | null;
  columnIndex: number | null;
}
export interface ItemEffectsEdge {
  key: ItemEffectsEdgeKey;
  bridge: ItemEffectsSourceReference & {
    tableId?: '00cb674f';
  };
  parentItemId: ItemEffectsSourceValue & {
    tableId?: '00cb674f';
    columnIndex?: 2;
  };
  itemEffectId: ItemEffectsSourceValue & {
    tableId?: '00cb674f';
    columnIndex?: 1;
  };
}
export interface ItemEffectsPage {
  selection: ItemEffectsSelection;
  bridgeIndexArtifactId: string;
  orderPolicy: 'signed-item-unsigned-bridge-source-ordinal-v1';
  bridgeSource: TableDataSnapshot &
    ((
      | {
          definitionSha256?: '3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb';
          providerRevision?: 'b9a63096cdd99b26967f17f9515377ae62cadfd7';
        }
      | {
          providerRevision?: string;
        }
    ) & {
      tableId?: '00cb674f';
      layoutHash?: '96F083AD';
      association?: {
        fileDataId?: '3177687';
      };
      decoderRevision?: 'wdc5-values-v7+table-admission-v3' | 'wdc5-values-v7+table-admission-projection-v3';
      /**
       * @minItems 3
       * @maxItems 3
       */
      columns?: [
        {
          name: 'ID';
          family: 'integer';
          isId: true;
          reference: null;
          interpretation: null;
        },
        {
          name: 'ItemEffectID';
          family: 'integer';
          isId: false;
          reference: {table: 'ItemEffect'; column: 'ID'};
          interpretation: null;
        },
        {
          name: 'ItemID';
          family: 'integer';
          isId: false;
          reference: {table: 'Item'; column: 'ID'};
          interpretation: null;
        }
      ];
      rowCount?: string;
    });
  observedAssociations: string;
  /**
   * @minItems 0
   * @maxItems 16
   */
  edges:
    | []
    | [ItemEffectsEdge]
    | [ItemEffectsEdge, ItemEffectsEdge]
    | [ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge]
    | [ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge]
    | [ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge]
    | [ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge, ItemEffectsEdge]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ]
    | [
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge,
        ItemEffectsEdge
      ];
  nextCursor: string | null;
}
export interface ItemEffectFieldEvidence1 {
  name:
    | 'LegacySlotIndex'
    | 'TriggerType'
    | 'Charges'
    | 'CoolDownMSec'
    | 'CategoryCoolDownMSec'
    | 'SpellCategoryID'
    | 'SpellID'
    | 'ChrSpecializationID'
    | 'PlayerConditionID';
  columnIndex: number;
  value: CreatureId;
  evidence: ItemEffectEvidenceKind;
  interpretation: TableColumnInterpretation | null;
  candidateReference: TableReference | null;
}
export interface ItemEffectOutcome1 {
  key: ItemEffectsEdgeKey;
  itemEffectId: string;
  state: ItemEffectState;
  observedCandidates: string | null;
  target:
    | (ItemEffectsSourceReference & {
        tableId?: '4002a5b1';
        columnIndex?: null;
      })
    | null;
  fields:
    | [
        ItemEffectFieldEvidence & {
          name?: 'LegacySlotIndex';
        },
        ItemEffectFieldEvidence & {
          name?: 'TriggerType';
        },
        ItemEffectFieldEvidence & {
          name?: 'Charges';
        },
        ItemEffectFieldEvidence & {
          name?: 'CoolDownMSec';
        },
        ItemEffectFieldEvidence & {
          name?: 'CategoryCoolDownMSec';
        },
        ItemEffectFieldEvidence & {
          name?: 'SpellCategoryID';
        },
        ItemEffectFieldEvidence & {
          name?: 'SpellID';
        },
        ItemEffectFieldEvidence & {
          name?: 'ChrSpecializationID';
        },
        ItemEffectFieldEvidence & {
          name?: 'PlayerConditionID';
        }
      ]
    | null;
  absence: ItemEffectsAbsence | null;
  problem: ItemEffectsProblem | null;
}
export interface ItemEffectDetailsResult {
  selection: ItemEffectsSelection;
  bridgeIndexArtifactId: string;
  policyRevision: 'item-effect-inputs-v1';
  source: ItemEffectsSource & {
    role?: 'effect';
  };
  /**
   * @minItems 1
   * @maxItems 16
   */
  outcomes:
    | [ItemEffectOutcome]
    | [ItemEffectOutcome, ItemEffectOutcome]
    | [ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome]
    | [ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome]
    | [ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome]
    | [ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome, ItemEffectOutcome]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ]
    | [
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome,
        ItemEffectOutcome
      ];
}
export interface ItemEffectSpellEvidence1 {
  state: ItemEffectState;
  itemEffectId: string;
  observedCandidates: string | null;
  target:
    | (ItemEffectsSourceReference & {
        tableId?: '4002a5b1';
        columnIndex?: null;
      })
    | null;
  spellId:
    | (ItemEffectsSourceValue & {
        tableId?: '4002a5b1';
        columnIndex?: 7;
      })
    | null;
  absence: ItemEffectsAbsence | null;
  problem: ItemEffectsProblem | null;
}
export interface ItemEffectNameIssue {
  code: 'DB2_TEXT_ENCODING_INVALID';
  source: ItemEffectsSourceReference & {
    tableId?: '46c66698';
    columnIndex?: 1;
  };
}
export interface ItemEffectNameOutcome1 {
  key: ItemEffectsEdgeKey;
  effect: ItemEffectSpellEvidence;
  state: ItemEffectNameState;
  observedCandidates: string | null;
  target:
    | (ItemEffectsSourceReference & {
        tableId?: '46c66698';
        columnIndex?: null;
      })
    | null;
  text: string | null;
  issue: ItemEffectNameIssue | null;
  textUnits: string | null;
  absence: ItemEffectsAbsence | null;
  problem: ItemEffectsProblem | null;
}
export interface ItemEffectNamesResult1 {
  selection: ItemEffectsSelection;
  bridgeIndexArtifactId: string;
  itemEffectArtifactId: string;
  policyRevision: 'spell-name-by-emitted-id-v1';
  /**
   * @minItems 2
   * @maxItems 2
   */
  sources: [
    ItemEffectsSource & {
      role?: 'effect';
    },
    ItemEffectsSource & {
      role?: 'name';
    }
  ];
  /**
   * @minItems 1
   * @maxItems 8
   */
  outcomes:
    | [ItemEffectNameOutcome]
    | [ItemEffectNameOutcome, ItemEffectNameOutcome]
    | [ItemEffectNameOutcome, ItemEffectNameOutcome, ItemEffectNameOutcome]
    | [ItemEffectNameOutcome, ItemEffectNameOutcome, ItemEffectNameOutcome, ItemEffectNameOutcome]
    | [
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome
      ]
    | [
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome
      ]
    | [
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome
      ]
    | [
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome,
        ItemEffectNameOutcome
      ];
}
export interface ItemEffectsProgress {
  stage: string;
  unit: string;
  completed: number | null;
  total: number | null;
  tableId: ('00cb674f' | '4002a5b1' | '46c66698') | null;
}
export interface ItemEffectsSelectionParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  itemsArtifactId: string;
  itemId: CreatureId;
}
export interface ItemEffectsPageParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  itemsArtifactId: string;
  itemId: CreatureId;
  bridgeIndexArtifactId: string;
  limit?: number;
  after?: string;
}
export interface ItemEffectDetailsParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  itemsArtifactId: string;
  itemId: CreatureId;
  bridgeIndexArtifactId: string;
  /**
   * @minItems 1
   * @maxItems 16
   */
  edges:
    | [ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ];
}
export interface ItemEffectNamesParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  itemsArtifactId: string;
  itemId: CreatureId;
  bridgeIndexArtifactId: string;
  itemEffectArtifactId: string;
  /**
   * @minItems 1
   * @maxItems 8
   */
  edges:
    | [ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey, ItemEffectsEdgeKey]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ]
    | [
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey,
        ItemEffectsEdgeKey
      ];
  policyRevision?: 'spell-name-by-emitted-id-v1';
}
export interface CreatureGeometryAppearanceRegion {
  offset: number;
  byteLength: number;
}
export interface CreatureGeometryAppearanceDisplay {
  displayId: string;
  extendedDisplayInfoId: string;
  /**
   * @minItems 4
   * @maxItems 4
   */
  textureVariations: [string, string, string, string];
}
export interface CreatureGeometryTextureImage {
  width: number;
  height: number;
  mipLevel: number;
  baseWidth: number;
  baseHeight: number;
  encoding: 'palette' | 'bc1' | 'bc2' | 'bc3' | 'bgra8';
}
export interface CreatureGeometryTextureSource {
  contentKey: string;
  encodingKey: string;
  encodedSha256: string;
  encodedBytes: number;
  decodedBytes: number;
  fullVerifierRevision: 'content-blte-v1';
}
/**
 * D095: the standard-resolution ROOT variant used when a texture's memberships differ only in content bit 0x1 (HighResTexture), and the high-resolution variant left out.
 */
export interface CreatureGeometryTextureVariant {
  policy: 'root-texture-standard-resolution-v1';
  contentKey: string;
  contentFlags: number;
  highResolutionContentKey: string;
  highResolutionContentFlags: number;
}
export interface CreatureGeometryTexture {
  ordinal: number;
  type: 0 | 11 | 12 | 13;
  binding: 'txid' | 'variation';
  variation: number | null;
  fileDataId: string | null;
  state: CreatureGeometryTextureState;
  image: CreatureGeometryTextureImage | null;
  source: CreatureGeometryTextureSource | null;
  variant: CreatureGeometryTextureVariant | null;
}
export interface CreatureGeometryEffects {
  particles: number;
  ribbons: number;
  lights: number;
  shadowBatches: number;
  unbatchedSections: number;
  blendedBatches: number;
  envTransforms: number;
  evaluation: 'notDrawn';
}
export interface CreatureGeometryAppearanceProblem {
  code: string;
  message: string;
  retryable: boolean;
  batchOrdinal: number | null;
  textureOrdinal: number | null;
}
/**
 * VMA1 v2 textured appearance manifest; see contracts/geometry-vma1-v2.md.
 */
export interface CreatureAppearanceManifest {
  artifactId: string;
  bindingSha256: string;
  sha256: string;
  byteLength: number;
  formatVersion: 2;
  headerBytes: 160;
  coordinatePolicy: 'native-m2-position-normal-uv-winding-v1';
  vertexCount: number;
  indexCount: number;
  drawCount: number;
  textureCount: number;
  positions: CreatureGeometryAppearanceRegion;
  normals: CreatureGeometryAppearanceOptionalRegion;
  uvs: CreatureGeometryAppearanceRegion;
  indices: CreatureGeometryAppearanceRegion;
  draws: CreatureGeometryAppearanceRegion;
  textures: CreatureGeometryAppearanceRegion;
  texels: CreatureGeometryAppearanceRegion;
  bounds: CreatureGeometryBounds;
  alphaReference: 128;
  displayId: number;
  geometryBindingSha256: string;
  materialPolicy: 'm2-one-or-two-stage-combiners-v1';
  texturePolicy: 'blp2-largest-mip-within-1024-rgba8-v1';
  readerRevision: 'm2-appearance-batches-v1';
  decoderRevision: 'blp2-rgba8-v2';
  artifactRevision: 'model-appearance-vma1-v2';
}
/**
 * A VMA1 region that is empty (byteLength 0) when no drawn stage needs it.
 */
export interface CreatureGeometryAppearanceOptionalRegion {
  offset: number;
  byteLength: number;
}
export interface CreatureGeometryBounds {
  /**
   * @minItems 3
   * @maxItems 3
   */
  min: [number, number, number];
  /**
   * @minItems 3
   * @maxItems 3
   */
  max: [number, number, number];
}
export interface CreatureGeometryAppearance1 {
  policy: 'creature-textured-appearance-v1';
  state: CreatureGeometryAppearanceState;
  display: CreatureGeometryAppearanceDisplay | null;
  /**
   * @maxItems 32
   */
  textures: CreatureGeometryTexture[];
  effects: CreatureGeometryEffects | null;
  manifest: CreatureAppearanceManifest | null;
  problem: CreatureGeometryAppearanceProblem | null;
}
export interface CreatureGeometryBufferRegion {
  offset: number;
  byteLength: number;
}
export interface CreatureGeometryEvaluations {
  material: 'notEvaluated';
  pose: 'notEvaluated';
  visibility: 'notEvaluated';
  appearance: 'notEvaluated';
}
export interface CreatureGeometryManifest {
  artifactId: string;
  bindingSha256: string;
  sha256: string;
  byteLength: number;
  formatVersion: 1;
  headerBytes: 128;
  scope: 'unfiltered-declared-sections';
  coordinatePolicy: 'native-m2-position-normal-uv-winding-v1';
  evaluations: CreatureGeometryEvaluations;
  vertexCount: number;
  vertexLookupCount: number;
  sourceTriangleIndexCount: number;
  sectionIndexCount: number;
  lineIndexCount: number;
  sectionCount: number;
  positions: CreatureGeometryBufferRegion;
  lineIndices: CreatureGeometryBufferRegion;
  sections: CreatureGeometryBufferRegion;
  bounds: CreatureGeometryBounds;
  modelFileDataId: number;
  skinFileDataId: number;
  mainSkinOrdinal: 0;
  modelReaderRevision: 'm2-static-native-v1';
  skinReaderRevision: 'm2-skin-geometry-v1';
  interpretationPolicy: 'skin-unfiltered-source-geometry-v1';
  artifactRevision: 'model-geometry-vmg1-v1';
  edgePolicy: 'all-sections-triangle-edges-v1';
}
export interface CreatureGeometryTarget {
  role: 'display' | 'model';
  tableId: 'bfdaf9f1' | '7f695fa5';
  artifactId: string;
  coverage: TableDataCoverage;
  matchCount: string | null;
  row: CreatureDisplayRowEvidence | null;
}
export interface CreatureGeometryMetadata {
  state:
    | 'notPrepared'
    | 'creatureNotFoundInAdmittedScope'
    | 'noDeclaredDisplay'
    | 'invalidDisplayReference'
    | 'displayMissingInAdmittedScope'
    | 'displayAmbiguous'
    | 'noDeclaredModel'
    | 'modelMissingInAdmittedScope'
    | 'modelAmbiguous'
    | 'noDeclaredModelFile'
    | 'metadataResolved'
    | 'protected'
    | 'unsupported'
    | 'unavailable'
    | 'corrupt'
    | 'resourceLimit';
  creatureSource: CreatureSourceReference;
  creatureCoverage: TableDataCoverage;
  selected: CreatureDisplaySlot | null;
  /**
   * @minItems 0
   * @maxItems 2
   */
  targets:
    | []
    | [
        CreatureGeometryTarget & {
          role?: 'display';
          tableId?: 'bfdaf9f1';
        }
      ]
    | [
        CreatureGeometryTarget & {
          role?: 'display';
          tableId?: 'bfdaf9f1';
        },
        CreatureGeometryTarget & {
          role?: 'model';
          tableId?: '7f695fa5';
        }
      ];
  absence: ('unknownOutsideReadableScope' | 'absentFromCompleteSource') | null;
  modelId: string | null;
  modelFileDataId: CreatureId | null;
  requiredTableId: ('bfdaf9f1' | '7f695fa5') | null;
  appearance: 'notEvaluated';
}
export interface CreatureGeometrySource {
  role: CreatureGeometrySourceRole;
  fileDataId: CreatureId;
  contentKey: string;
  encodingKey: string;
  encodedSha256: string;
  encodedBytes: number;
  decodedBytes: number;
  fullVerifierRevision: 'content-blte-v1';
  associationSha256: string;
  variantPolicy: 'all-root-variants-consistent-asset-v1';
  encodingLookupRevision: 'encoding-native-file-v1';
  rootLookupRevision: 'root-native-file-v1';
  encodingSelectionPolicy: 'lexicographic-complete-encoding-v1';
}
export interface CreatureGeometryProblem {
  code: string;
  message: string;
  retryable: boolean;
  dependency: CreatureGeometryDependency | null;
  target: ('display' | 'model') | null;
}
export interface CreatureGeometryResult1 {
  context: CreaturesContext;
  artifactId: string;
  creatureId: CreatureId;
  slot: number;
  policy: 'creature-source-geometry-v1';
  opening: OpeningSourceReference;
  state: CreatureGeometryState;
  metadata: CreatureGeometryMetadata;
  /**
   * @minItems 0
   * @maxItems 2
   */
  sources:
    | []
    | [
        CreatureGeometrySource & {
          role?: 'model';
        }
      ]
    | [
        CreatureGeometrySource & {
          role?: 'model';
        },
        CreatureGeometrySource & {
          role?: 'skin';
        }
      ];
  manifest: CreatureGeometryManifest | null;
  appearance?: CreatureGeometryAppearance | null;
  problem: CreatureGeometryProblem | null;
}
export interface CreatureGeometryLeaseDescriptor {
  leaseId: string;
  manifest: CreatureGeometryManifest | CreatureAppearanceManifest;
}
export interface CreatureGeometryParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  artifactId: string;
  creatureId: CreatureId;
  slot: number;
}
export interface CreatureGeometryReadParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  artifactId: string;
  creatureId: CreatureId;
  slot: number;
  expectedGeometryArtifactId?: string | null;
}
export interface CreatureGeometryOpenParams {
  productId: string;
  buildIdentity: string;
  sourceRegion: string;
  researchLocale: 'enUS';
  artifactId: string;
  creatureId: CreatureId;
  slot: number;
  geometryArtifactId: string;
}
export interface CreatureGeometryCloseParams {
  leaseId: string;
}
export interface CreatureGeometryCloseResult {
  closed: true;
}
export interface SettingsSnapshot {
  formatVersion: 1;
  file: 'absent' | 'read' | 'unreadable' | 'newer';
  problem: SettingsProblem | null;
  lastChangedAt: SettingsTimestamp | null;
  communityBuildHistory: {
    value: boolean;
    defaultValue: false;
    source: 'default' | 'saved' | 'unreadable';
    problem: SettingsProblem | null;
  };
  /**
   * Fetch newer table definitions from WoWDBDefs for Tables and the feature views, exact build first (D081, D101). On by default; an unreadable value reads as off.
   */
  fetchTableDefinitions: {
    value: boolean;
    defaultValue: true;
    source: 'default' | 'saved' | 'unreadable';
    problem: SettingsProblem | null;
  };
  /**
   * Community file names from wow-listfile in Files (D105). On by default; an unreadable value, or an unreadable or newer file, reads as off.
   */
  communityListfile: {
    value: boolean;
    defaultValue: true;
    source: 'default' | 'saved' | 'unreadable';
    problem: SettingsProblem | null;
  };
  storage: {
    dataDirectory: string;
  };
  downloadLimits: {
    parallelDownloads: number;
    backgroundDownloads: number;
    bandwidthCapBytesPerSecond: string | null;
    adjustable: boolean;
  };
}
export interface SettingsProblem {
  code: string;
  message: string;
  retryable: boolean;
}
export interface SettingsSetParams {
  name: 'communityBuildHistory' | 'fetchTableDefinitions' | 'communityListfile';
  value: boolean;
}
export interface StorageMeasurement {
  bytes: string;
  files: string;
  skipped: string;
  startedAt: SettingsTimestamp;
  measuredAt: SettingsTimestamp;
}
/**
 * The community listfile download (settings.listfile.read and settings.listfile.check, capability community-listfile-v1, D105).
 */
export interface CommunityListfileStatus {
  /**
   * The switch reads as on.
   */
  enabled: boolean;
  /**
   * Something asked for names in this engine run, or a copy or a partial download was kept from earlier. False means nothing has been fetched yet: the first download starts when Files lists files, the switch is turned on or Settings asks for it.
   */
  started: boolean;
  activity: 'idle' | 'verifying' | 'checking' | 'downloading' | 'importing';
  /**
   * The verified copy names come from while enabled; null when there is none yet.
   */
  release: CommunityListfileRelease | null;
  /**
   * The release being downloaded or imported.
   */
  pendingTag: string | null;
  /**
   * Bytes downloaded, or bytes read while importing.
   */
  completed: string | null;
  total: string | null;
  /**
   * When GitHub last confirmed the release in use.
   */
  checkedAt: SettingsTimestamp | null;
  /**
   * The last attempt's failure. A release in use stays in use.
   */
  problem: SettingsProblem | null;
}
/**
 * One verified wow-listfile release kept in the data folder: GitHub's tag and publish time, when VibeMiner fetched it, the CSV's SHA-256 and length as GitHub published them, the names read from it and its download address.
 */
export interface CommunityListfileRelease {
  tag: string;
  publishedAt: SettingsTimestamp;
  fetchedAt: SettingsTimestamp;
  sha256: string;
  byteLength: string;
  nameCount: string;
  sourceUrl: string;
}
/**
 * settings.storage.builds: every opened build, most recently opened first, at most 256; truncated when more were opened.
 */
export interface PreparedDataList {
  /**
   * @maxItems 256
   */
  builds: PreparedBuild[];
  truncated: boolean;
  measuredAt: SettingsTimestamp;
}
/**
 * What one opened build holds locally (D085).
 */
export interface PreparedBuild {
  productId: string;
  buildIdentity: string;
  buildVersion: string | null;
  lastOpenedAt: string;
  fileCatalog: PreparedFileCatalog;
  tables: PreparedTables;
  downloads: PreparedDownloads;
}
/**
 * A build's file catalog: its state (none when it was never queued) and the bytes and count of its files on disk, measured when read. Only this build uses them.
 */
export interface PreparedFileCatalog {
  state: 'none' | 'pending' | 'stopped' | 'ready' | 'failed' | 'unsupported' | 'removed';
  bytes: string;
  files: number;
}
/**
 * The table row files the build's admissions use, each counted once, and the bytes another build also uses. Kept by settings.storage.free.
 */
export interface PreparedTables {
  count: number;
  bytes: string;
  sharedBytes: string;
}
/**
 * The build's stored download figure (D065). Stored objects are shared with every build that lists them. Kept by settings.storage.free.
 */
export interface PreparedDownloads {
  state: 'notMeasured' | 'counting' | 'current' | 'unavailable';
  storedObjects: string | null;
  storedBytes: string | null;
}
/**
 * settings.storage.free: one opened build whose file catalog is freed (D085).
 */
export interface PreparedDataFreeParams {
  productId: string;
  buildIdentity: string;
}
/**
 * settings.storage.free reply: the bytes and files deleted and the file catalog as measured after.
 */
export interface PreparedDataFreed {
  productId: string;
  buildIdentity: string;
  freedBytes: string;
  freedFiles: number;
  fileCatalog: PreparedFileCatalog;
}
