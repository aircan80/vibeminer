import {GeometryBinaryProtocol} from './geometry-binary-protocol';
import {GeometryViewController,type GeometryDocumentOwner} from './geometry-view-controller';
import {authorizedGeometryFetch} from './geometry-provenance';
import {validGeometryParams,validGeometryRead,validGeometryViewEnter,validGeometryViewOpen,validGeometryBodyRetired,validGeometrySceneDisposal,validGeometryViewRelease} from '../shared/creature-geometry';
import type {CreatureGeometryResult,CreatureGeometryLeaseDescriptor} from '../../../../contracts/generated/discovery';
import {randomUUID} from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {validItemEffectsSelection,validItemEffectsPage,validItemEffectDetails,validItemEffectNames} from '../shared/item-effects';
import {validCreatureDisplay} from '../shared/creature-display';
import {validCreatureContext,validCreaturePage,validCreatureInspect} from '../shared/creatures';
import {validClassifications} from '../shared/classifications';
import {IconsController,retireIconsAndEngine} from './icons-controller';
import {authorizedIconImage} from './icon-provenance';
import {validIconBatch,validIconView} from '../shared/item-icons';
import { validItemsContext, validItemsPage, validItemInspect } from '../shared/items';
import { validRetentionTarget, validRetentionSet } from '../shared/retention';
import { validSettingsSet, validEmpty } from '../shared/settings';
import { validTableData, validTableRows } from '../shared/table-data';
import { app, BrowserWindow, clipboard, ipcMain, nativeImage, protocol, session, shell } from 'electron';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { EngineClient, EngineFailure } from './engine-client';
import { validProduct, validRead, validReadAll, validRequestId, validOpen } from '../shared/bridge';
import { validTablesCatalog, validTableDefinition } from '../shared/tables';
import { validDownloadRead } from '../shared/download';
import { validSummariesParams, validChangesParams } from '../shared/download-summary';
import { validInventoryRead, validInventoryFiles, validInventoryFile } from '../shared/inventory';
import type { BridgeReply } from '../shared/bridge';
protocol.registerSchemesAsPrivileged([{scheme:'vibeminer-image',privileges:{standard:true,secure:true}},{scheme:'vibeminer-geometry',privileges:{standard:true,secure:true,supportFetchAPI:true,corsEnabled:true}}]);
let engine: EngineClient;
let icons:IconsController;
let geometry:GeometryViewController;
let geometryBinary:GeometryBinaryProtocol;
let geometryDocument:GeometryDocumentOwner|null=null;
let geometryDocumentSequence=0;
let geometryDocumentBlocked=false;
const appDocumentURL=()=>pathToFileURL(path.join(__dirname,'../renderer/index.html')).href;
const geometryCleanupFailure=()=>{console.error('Geometry presentation cleanup could not be confirmed; its reservation remains unavailable.');};
function geometryOwner():GeometryDocumentOwner{
 if(!window||window.isDestroyed()||window.webContents.isDestroyed()||window.webContents.session!==session.defaultSession||window.webContents.getURL()!==appDocumentURL())throw new EngineFailure('INVALID_REQUEST','The geometry document is unavailable.');
 if(geometryDocument)return geometryDocument;const contents=window.webContents,frame=contents.mainFrame,document=++geometryDocumentSequence;
 if(frame.frames.length)geometryDocumentBlocked=true;
 return geometryDocument={document,webContentsId:contents.id,frame,session:contents.session,isCurrent:()=>geometryDocument?.document===document&&!!window&&!window.isDestroyed()&&window.webContents===contents&&!contents.isDestroyed()&&!frame.isDestroyed()&&contents.mainFrame===frame&&contents.getURL()===appDocumentURL()&&!geometryDocumentBlocked&&frame.frames.length===0};
}
function geometryDocumentGone(){const old=geometryDocument;geometryDocument=null;geometryDocumentBlocked=false;if(old)void geometry.documentGone(old).catch(geometryCleanupFailure);}
const iconCleanupFailure=()=>{console.error('Verified image cleanup could not be confirmed; image delivery remains unavailable.');};
const engineDataDirectory=()=>path.resolve(process.env.VIBEMINER_DATA_DIR||path.join(app.getPath('userData'),'engine-v1'));
let window: BrowserWindow | null = null;
let quitting = false;
let ready: Promise<void>;
function createEngine(): EngineClient {
  const root = path.resolve(__dirname, '../../../..');
  const localDotnet = path.join(root, '.local/toolchains/dotnet/dotnet');
  const dotnet = process.env.VIBEMINER_DOTNET || (existsSync(localDotnet) ? localDotnet : 'dotnet');
  const enginePath = process.env.VIBEMINER_ENGINE_PATH || (app.isPackaged ? path.join(process.resourcesPath, 'engine', process.platform === 'win32' ? 'VibeMiner.Host.exe' : 'VibeMiner.Host') : path.join(root, 'src/backend/VibeMiner.Host/bin/Debug/net10.0/VibeMiner.Host.dll'));
  const dataDirectory = engineDataDirectory();
  const args = ['--data-dir', path.resolve(dataDirectory)];
  const child = enginePath.endsWith('.dll') ? spawn(dotnet, [enginePath, ...args], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true }) : spawn(enginePath, args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  return new EngineClient(child, { dataDirectory: engineDataDirectory() });
}
function registerBridge(): void {
  ipcMain.handle('discovery:invoke', async (event, method: string, params: unknown, requestId?: unknown): Promise<BridgeReply<unknown>> => {
    try {
      if(typeof method!=='string')throw new EngineFailure('INVALID_REQUEST','The operation was refused.');
      if (!window || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame) throw new EngineFailure('INVALID_REQUEST', 'The operation was refused.');
      if(method.startsWith('desktop.geometry.')||method==='creatures.geometry.read'||method==='creatures.geometry.prepare'){
        const valid=method==='desktop.geometry.enter'?validGeometryViewEnter(params):method==='desktop.geometry.open'?validGeometryViewOpen(params):method==='desktop.geometry.bodyRetired'?validGeometryBodyRetired(params):method==='desktop.geometry.sceneDisposed'?validGeometrySceneDisposal(params):method==='desktop.geometry.release'?validGeometryViewRelease(params):method==='creatures.geometry.read'?validGeometryRead(params):method==='creatures.geometry.prepare'&&validGeometryParams(params)&&validRequestId(requestId);
        if(!valid)throw new EngineFailure('INVALID_REQUEST','Choose one exact geometry view operation.');
        const owner=geometryOwner();await ready;if(!method.endsWith('.sceneDisposed')&&!method.endsWith('.release')&&!owner.isCurrent())throw new EngineFailure('CREATURE_GEOMETRY_VIEW_RETIRED','This geometry document was left.');
        if(!['creatures-v1','creature-display-metadata-v1','creature-source-geometry-v1'].every(c=>engine.capabilities.includes(c)))throw new EngineFailure('UNSUPPORTED','Verified source geometry is unavailable.');
        let value:unknown;
        if(method==='desktop.geometry.enter')value=await geometry.enter(owner,params as never);
        else if(method==='desktop.geometry.open')value=await geometry.open(owner,params as never);
        else if(method==='desktop.geometry.bodyRetired')value=await geometry.bodyRetired(owner,params as never);
        else if(method==='desktop.geometry.sceneDisposed')value=await geometry.sceneDisposed(owner,params as never);
        else if(method==='desktop.geometry.release')value=await geometry.release(owner,params as never);
        else if(method==='creatures.geometry.read'&&validGeometryRead(params))value=await geometry.read(owner,params);
        else if(method==='creatures.geometry.prepare'&&validGeometryParams(params)&&validRequestId(requestId))value=await geometry.prepare(owner,params,requestId);
        else throw new EngineFailure('INVALID_REQUEST','Choose one exact geometry view operation.');
        return{ok:true,value};
      }
      if(method==='desktop.icons'){const p=params as{params?:unknown;viewId?:unknown};if(!p||typeof p!=='object'||Object.keys(p).length!==2||!validIconBatch(p.params,12)||!validIconView(p.viewId)||!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose at most twelve exact visible icons.');const document=icons.documentToken;await ready;if(document!==icons.documentToken||!window||event.senderFrame!==window.webContents.mainFrame)throw new EngineFailure('ICON_VIEW_RETIRED','The image document was left.');return{ok:true,value:await icons.batch(p.params,requestId,p.viewId)};}
      if(method==='desktop.iconsRelease'){const p=params as{viewId?:unknown};if(!p||typeof p!=='object'||Object.keys(p).length!==1||!validIconView(p.viewId))throw new EngineFailure('INVALID_REQUEST','Invalid icon view.');return{ok:true,value:await icons.release(p.viewId)};}
      // Main owns the data folder path: the renderer can only ask for these two fixed actions on it.
      if (method === 'desktop.storage.copyPath' || method === 'desktop.storage.showFolder') {
        if (!validEmpty(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid storage folder action.');
        const folder = engineDataDirectory();
        if (method === 'desktop.storage.copyPath') { clipboard.writeText(folder); return { ok: true, value: { copied: true } }; }
        if (await shell.openPath(folder)) throw new EngineFailure('STORAGE_FOLDER_UNAVAILABLE', 'The storage folder could not be opened in the file manager.', true);
        return { ok: true, value: { shown: true } };
      }
      if (method === 'settings.read') { if (!validEmpty(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid settings query.'); }
      else if (method === 'settings.set') { if (!validSettingsSet(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one known setting value.'); }
      else if (method === 'settings.storage.measure') { if (!validEmpty(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Invalid storage measurement request.'); }
      else if (method === 'desktop.capabilities' || method === 'retention.list' || method === 'products.list') { if (!params || typeof params !== 'object' || Object.keys(params).length) throw new EngineFailure('INVALID_REQUEST', 'Invalid product query.'); }
      else if(method==='items.effects.data'||method==='items.effects.prepare'){if(!validItemEffectsSelection(params)||method==='items.effects.prepare'&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose one exact admitted Item.');}
      else if(method==='items.effects.page'){if(!validItemEffectsPage(params))throw new EngineFailure('INVALID_REQUEST','Choose one bounded exact effects page.');}
      else if(method==='items.effects.details'||method==='items.effects.details.prepare'){if(!validItemEffectDetails(params)||method.endsWith('.prepare')&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose bounded ordered effects edge keys.');}
      else if(method==='items.effects.names'||method==='items.effects.names.prepare'){if(!validItemEffectNames(params)||method.endsWith('.prepare')&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose bounded effects name keys and the current input artifact.');}
      else if(method==='creatures.display.read'||method==='creatures.display.prepare'){if(!validCreatureDisplay(params)||method==='creatures.display.prepare'&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose one exact Creature source slot.');}
      else if(method==='creatures.data'||method==='creatures.prepare'){if(!validCreatureContext(params)||method==='creatures.prepare'&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose one exact Creature context and research locale enUS.');}
      else if(method==='creatures.page'){if(!validCreaturePage(params))throw new EngineFailure('INVALID_REQUEST','Invalid bounded Creature query.');}
      else if(method==='creatures.inspect'){if(!validCreatureInspect(params))throw new EngineFailure('INVALID_REQUEST','Choose one exact admitted Creature ID.');}
      else if(method==='items.classifications'){if(!validClassifications(params)||!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose at most fifty exact Item IDs for source labels.');}
      else if (method === 'items.data' || method === 'items.prepare') { if (!validItemsContext(params) || method === 'items.prepare' && !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact Items context and research locale enUS.'); }
      else if (method === 'items.page') { if (!validItemsPage(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded Items query.'); }
      else if (method === 'items.inspect') { if (!validItemInspect(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact admitted Item ID.'); }
      else if (method === 'retention.read') { if (!validRetentionTarget(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid retention target.'); }
      else if (method === 'retention.set') { if (!validRetentionSet(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid retention command.'); }
      else if (method === 'discovery.read') { if (!validRead(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery query.'); }
      else if (method === 'discovery.readAll') { if (!validReadAll(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery query.'); }
      else if (method === 'discovery.refreshAll') { if (!params || typeof params !== 'object' || Object.keys(params).length || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery request.'); }
      else if (method === 'discovery.refresh') { if (!validProduct((params as { productId?: unknown })?.productId) || !validRequestId(requestId) || Object.keys(params as object).length !== 1) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery request.'); }
      else if (method === 'tables.prepare' || method === 'tables.data') { if (!validTableData(params) || method === 'tables.prepare' && !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact table and research locale enUS.'); }
      else if (method === 'tables.rows' || method === 'tables.referenceText') { if (!validTableRows(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded table record query.'); }
      else if (method === 'tables.catalog') { if (!validTablesCatalog(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded definition catalogue query.'); }
      else if (method === 'tables.definition') { if (!validTableDefinition(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact provider table identity.'); }
      else if (method === 'download.read') { if (!validDownloadRead(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded download-object query.'); }
      else if (method === 'download.summaries') { if (!validSummariesParams(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one to 100 distinct exact builds.'); }
      else if (method === 'download.changes') { if (!validChangesParams(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Name the summary revision of the last read.'); }
      else if (method === 'inventory.read') { if (!validInventoryRead(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded inventory query.'); }
      else if (method === 'inventory.files') { if (!validInventoryFiles(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded Files query.'); }
      else if (method === 'inventory.file') { if (!validInventoryFile(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded file inspector query.'); }
      else if (method === 'workspace.open') { if (!validOpen(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose an exact saved build and source region.'); }
      else if (method === 'request.cancel') { if (!validRequestId((params as { requestId?: unknown })?.requestId) || Object.keys(params as object).length !== 1) throw new EngineFailure('INVALID_REQUEST', 'Invalid cancellation request.'); }
      else throw new EngineFailure('INVALID_REQUEST', 'Unsupported desktop operation.');
      await ready;
      if (method === 'desktop.capabilities') return {ok:true, value:[...engine.capabilities]};
      const value = await engine.request(method, params as object, typeof requestId === 'string' ? requestId : undefined);
      return { ok: true, value };
    } catch (error) {
      const failure = error instanceof EngineFailure ? error : new EngineFailure('ENGINE_UNAVAILABLE', 'The local engine is unavailable. Restart VibeMiner to reconnect.', true);
      return { ok: false, error: { code: failure.code, message: failure.message, retryable: failure.retryable } };
    }
  });
}
async function openWindow(): Promise<void> {
  const icon = nativeImage.createFromPath(path.join(__dirname, '../assets/vibeminer.png'));
  if (process.platform === 'darwin') app.dock?.setIcon(icon);
  window = new BrowserWindow({ width: 1280, height: 800, minWidth: 600, minHeight: 480, title: 'VibeMiner', icon, backgroundColor: '#f5f3ed', webPreferences: { preload: path.join(__dirname, '../preload/index.cjs'), session:session.defaultSession, contextIsolation: true, nodeIntegration: false, sandbox: true, additionalArguments: process.env.VIBEMINER_AUTHORED_FIXTURES === '1' ? ['--vibeminer-authored-fixtures'] : [] } });
  // Hide the bar but keep the default menu so edit, zoom, close and quit accelerators still work.
  window.setMenuBarVisibility(false);
  // The default zoomIn accelerator is Ctrl+Plus (Ctrl+Shift+= on most layouts); also accept plain Ctrl+=. Chromium clamps the level at its 500% maximum.
  window.webContents.on('before-input-event',(event,input)=>{if(input.type==='keyDown'&&(input.control||input.meta)&&!input.alt&&!input.shift&&input.key==='='){event.preventDefault();const contents=window!.webContents;contents.setZoomLevel(contents.getZoomLevel()+0.5);}});
  window.webContents.on('did-start-navigation',(_event,_url,inPlace,mainFrame)=>{if(mainFrame&&!inPlace){geometry.invalidate('documentReplaced');void icons.revokeDocument().catch(iconCleanupFailure);}});
  window.webContents.on('frame-created',(_event,details)=>{if(!details.frame||details.frame.parent){geometryDocumentBlocked=true;geometry.invalidate('documentReplaced');}});
  window.webContents.on('did-navigate',()=>geometryDocumentGone());
  window.webContents.on('render-process-gone',()=>{geometryDocumentGone();void icons.revokeDocument().catch(iconCleanupFailure);});
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.on('closed', () => {geometryDocumentGone();window = null;});
  await window.loadFile(path.join(__dirname, '../renderer/index.html'));
}
app.whenReady().then(async () => {
  engine = createEngine();
  icons=new IconsController(engine,engineDataDirectory());
  geometryBinary=new GeometryBinaryProtocol({dataDirectory:engineDataDirectory(),hasEngineExited:()=>engine.hasExited,openGeometry:async(params,signal)=>{
    const id=`geometry_open_${randomUUID()}`,cancel=()=>{void engine.request('request.cancel',{requestId:id}).catch(()=>{/* Original Open remains observed. */});};
    signal.addEventListener('abort',cancel,{once:true});try{if(signal.aborted)throw new EngineFailure('CANCELED','The geometry view was left.');return await engine.request<CreatureGeometryLeaseDescriptor>('creatures.geometry.open',params,id);}finally{signal.removeEventListener('abort',cancel);}
  },closeGeometry:async leaseId=>{try{await engine.request('creatures.geometry.close',{leaseId});}catch(error){if(!engine.hasExited)throw error;}}});
  geometry=new GeometryViewController({icons,binary:geometryBinary,read:p=>engine.request<CreatureGeometryResult>('creatures.geometry.read',p),prepare:(p,id)=>engine.request<CreatureGeometryResult>('creatures.geometry.prepare',p,id),notify:(owner,notice)=>{if(geometryDocument===owner&&window&&!window.isDestroyed()&&!window.webContents.isDestroyed()&&window.webContents.id===owner.webContentsId&&window.webContents.mainFrame===owner.frame&&window.webContents.getURL()===appDocumentURL())window.webContents.send('discovery:geometryRetirement',notice);}});
  // Reuse the sole app-window session to preserve its existing local preferences.
  const appSession=session.defaultSession;
  appSession.webRequest.onBeforeRequest({urls:['vibeminer-image://*/*','vibeminer-geometry://*/*']},(details,done)=>{const owner=window&&!window.isDestroyed()?window.webContents:null;done({cancel:details.url.startsWith('vibeminer-geometry:')?!authorizedGeometryFetch(details,owner,appSession,geometryDocument?.isCurrent()??false):!authorizedIconImage(details,owner)});});
  appSession.protocol.handle('vibeminer-image',request=>icons.handle(request));
  appSession.protocol.handle('vibeminer-geometry',request=>geometryBinary.handle(request));
  engine.on('unavailable',()=>{geometry.invalidate('engineUnavailable');void icons.revokeDocument().catch(iconCleanupFailure);});
  engine.on('terminated',()=>geometry.engineExited());
  ready = engine.hello();
  // Observe launch failure before any renderer request arrives.
  void ready.catch(() => {});
  engine.on('progress', progress => { if (window && !window.isDestroyed()) window.webContents.send('discovery:progress', progress); });
  registerBridge();
  await openWindow();
});
app.on('window-all-closed', () => app.quit());
app.on('before-quit', event => {
  if (quitting || !engine) return;
  event.preventDefault(); quitting = true;
  geometry.invalidate('shutdown');if(window&&!window.isDestroyed())window.destroy();
  const geometryRetirement=geometry.dispose();void geometryRetirement.catch(geometryCleanupFailure);
  void retireIconsAndEngine(icons,engine).then(async retired=>{await geometryRetirement;if(!retired)iconCleanupFailure();}).catch(iconCleanupFailure).finally(()=>app.quit());
});
