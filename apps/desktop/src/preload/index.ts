import {validGeometryRetirement} from '../shared/creature-geometry';
import { contextBridge, ipcRenderer } from 'electron';
import type { DiscoveryBridge } from '../shared/bridge';
import type { EngineProgress } from '../../../../contracts/generated/discovery';
let geometryRetirementUnsubscribe:(()=>void)|null=null;
const bridge: DiscoveryBridge = Object.freeze<DiscoveryBridge>({
  creatureGeometryRead:params=>ipcRenderer.invoke('discovery:invoke','creatures.geometry.read',params),
  creatureGeometryPrepare:(params,id)=>ipcRenderer.invoke('discovery:invoke','creatures.geometry.prepare',params,id),
  geometryViewEnter:params=>ipcRenderer.invoke('discovery:invoke','desktop.geometry.enter',params),
  geometryViewOpen:params=>ipcRenderer.invoke('discovery:invoke','desktop.geometry.open',params),
  geometryViewBodyRetired:params=>ipcRenderer.invoke('discovery:invoke','desktop.geometry.bodyRetired',params),
  geometryViewSceneDisposed:params=>ipcRenderer.invoke('discovery:invoke','desktop.geometry.sceneDisposed',params),
  geometryViewRelease:params=>ipcRenderer.invoke('discovery:invoke','desktop.geometry.release',params),
  onGeometryRetirement:listener=>{geometryRetirementUnsubscribe?.();let live=true;const handler=(_event:Electron.IpcRendererEvent,notice:unknown)=>{if(live&&validGeometryRetirement(notice))listener(notice);};ipcRenderer.on('discovery:geometryRetirement',handler);const unsubscribe=()=>{live=false;ipcRenderer.removeListener('discovery:geometryRetirement',handler);if(geometryRetirementUnsubscribe===unsubscribe)geometryRetirementUnsubscribe=null;};geometryRetirementUnsubscribe=unsubscribe;return unsubscribe;},
  itemEffectsRead:params=>ipcRenderer.invoke('discovery:invoke','items.effects.data',params),
  itemEffectsPrepareLinks:(params,id)=>ipcRenderer.invoke('discovery:invoke','items.effects.prepare',params,id),
  itemEffectsPage:params=>ipcRenderer.invoke('discovery:invoke','items.effects.page',params),
  itemEffectDetails:params=>ipcRenderer.invoke('discovery:invoke','items.effects.details',params),
  itemEffectPrepareDetails:(params,id)=>ipcRenderer.invoke('discovery:invoke','items.effects.details.prepare',params,id),
  itemEffectNames:params=>ipcRenderer.invoke('discovery:invoke','items.effects.names',params),
  itemEffectPrepareNames:(params,id)=>ipcRenderer.invoke('discovery:invoke','items.effects.names.prepare',params,id),
  creatureDisplayRead:params=>ipcRenderer.invoke('discovery:invoke','creatures.display.read',params),
  creatureDisplayPrepare:(params,id)=>ipcRenderer.invoke('discovery:invoke','creatures.display.prepare',params,id),
  creaturesData:params=>ipcRenderer.invoke('discovery:invoke','creatures.data',params),
  creaturesPrepare:(params,id)=>ipcRenderer.invoke('discovery:invoke','creatures.prepare',params,id),
  creaturesPage:params=>ipcRenderer.invoke('discovery:invoke','creatures.page',params),
  creatureInspect:params=>ipcRenderer.invoke('discovery:invoke','creatures.inspect',params),
  fixtureLabel: process.argv.includes('--vibeminer-authored-fixtures') ? 'Authored test data' : null,
  classifications:(params,requestId)=>ipcRenderer.invoke('discovery:invoke','items.classifications',params,requestId),
  iconsBatch:(params,requestId,viewId)=>ipcRenderer.invoke('discovery:invoke','desktop.icons',{params,viewId},requestId),
  iconsRelease:viewId=>ipcRenderer.invoke('discovery:invoke','desktop.iconsRelease',{viewId}),
  capabilities: () => ipcRenderer.invoke('discovery:invoke', 'desktop.capabilities', {}),
  retentionRead: params => ipcRenderer.invoke('discovery:invoke', 'retention.read', params),
  retentionList: () => ipcRenderer.invoke('discovery:invoke', 'retention.list', {}),
  retentionSet: params => ipcRenderer.invoke('discovery:invoke', 'retention.set', params),
  itemsData: params => ipcRenderer.invoke('discovery:invoke', 'items.data', params),
  itemsPrepare: (params, requestId) => ipcRenderer.invoke('discovery:invoke', 'items.prepare', params, requestId),
  itemsPage: params => ipcRenderer.invoke('discovery:invoke', 'items.page', params),
  itemInspect: params => ipcRenderer.invoke('discovery:invoke', 'items.inspect', params),
  products: () => ipcRenderer.invoke('discovery:invoke', 'products.list', {}),
  read: params => ipcRenderer.invoke('discovery:invoke', 'discovery.read', params),
  refresh: (productId, requestId) => ipcRenderer.invoke('discovery:invoke', 'discovery.refresh', { productId }, requestId),
  readAll: params => ipcRenderer.invoke('discovery:invoke', 'discovery.readAll', params),
  refreshAll: requestId => ipcRenderer.invoke('discovery:invoke', 'discovery.refreshAll', {}, requestId),
  open: (params, requestId) => ipcRenderer.invoke('discovery:invoke', 'workspace.open', params, requestId),
  tablePrepare: (params, requestId) => ipcRenderer.invoke('discovery:invoke', 'tables.prepare', params, requestId),
  tableData: params => ipcRenderer.invoke('discovery:invoke', 'tables.data', params),
  tableRows: params => ipcRenderer.invoke('discovery:invoke', 'tables.rows', params),
  tablesCatalog: params => ipcRenderer.invoke('discovery:invoke', 'tables.catalog', params),
  tableDefinition: params => ipcRenderer.invoke('discovery:invoke', 'tables.definition', params),
  download: params => ipcRenderer.invoke('discovery:invoke', 'download.read', params),
  inventory: params => ipcRenderer.invoke('discovery:invoke', 'inventory.read', params),
  cancel: requestId => ipcRenderer.invoke('discovery:invoke', 'request.cancel', { requestId }),
  onProgress: (listener: (progress: EngineProgress) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, progress: EngineProgress) => listener(progress);
    ipcRenderer.on('discovery:progress', handler);
    return () => { ipcRenderer.removeListener('discovery:progress', handler); };
  }
});
contextBridge.exposeInMainWorld('discovery', bridge);
