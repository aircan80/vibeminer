import {GeometryBinaryProtocol} from './geometry-binary-protocol';
import { validAvailabilityCheck } from '../shared/availability';
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
import { validItemsContext, validItemsPage, validItemInspect, validItemLabelsParams } from '../shared/items';
import { validRetentionTarget, validRetentionSet } from '../shared/retention';
import { validSettingsSet, validEmpty, validPreparedDataFree } from '../shared/settings';
import { validTableData, validTableRows } from '../shared/table-data';
import { validTableFollow } from '../shared/table-follow';
import { app, BrowserWindow, clipboard, dialog, ipcMain, nativeImage, protocol, session, shell } from 'electron';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { excludeFromBackups, prepareDataFolder, readDataFolderChoice, resolveDataFolder, type DataFolder } from './data-folder';
import path from 'node:path';
import { EngineClient, EngineFailure } from './engine-client';
import { EngineSupervisor } from './engine-supervisor';
import type { EngineStatus } from '../shared/engine-status';
import { validProduct, validRead, validReadAll, validRequestId, validOpen } from '../shared/bridge';
import { validTablesCatalog, validTableDefinition } from '../shared/tables';
import { validDownloadRead, validDownloadCheck } from '../shared/download';
import { validSummariesParams, validChangesParams } from '../shared/download-summary';
import { validInventoryRead, validInventoryFiles, validInventoryFile, validFileCatalog, validFileCatalogSet } from '../shared/inventory';
import type { BridgeReply } from '../shared/bridge';
// D114 (#131): with no usable GPU (none, blocklisted or crashed) Chromium gives WebGL no context unless SwiftShader is allowed,
// and the geometry view could never draw. The "unsafe" in the switch is about web pages reaching SwiftShader's JIT; this window
// only runs the app's own bundle (navigation and new windows are denied, script-src 'self'), so it doesn't apply. A real GPU
// is still used when there is one. Authored-fixture e2e runs may turn the fallback off to check the no-WebGL state.
if(!(process.env.VIBEMINER_AUTHORED_FIXTURES==='1'&&process.env.VIBEMINER_FIXTURE_NO_SOFTWARE_WEBGL==='1'))app.commandLine.appendSwitch('enable-unsafe-swiftshader');
protocol.registerSchemesAsPrivileged([{scheme:'vibeminer-image',privileges:{standard:true,secure:true}},{scheme:'vibeminer-geometry',privileges:{standard:true,secure:true,supportFetchAPI:true,corsEnabled:true}}]);
/** One engine process and the image and geometry delivery bound to it. A restart (D119) replaces all of it. */
interface Session{engine:EngineClient;ready:Promise<void>;icons:IconsController;geometryBinary:GeometryBinaryProtocol;geometry:GeometryViewController;retire():Promise<boolean>;}
let supervisor:EngineSupervisor<Session>;
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
function geometryDocumentGone(){const old=geometryDocument;geometryDocument=null;geometryDocumentBlocked=false;if(old)void supervisor.current.geometry.documentGone(old).catch(geometryCleanupFailure);}
const iconCleanupFailure=()=>{console.error('Verified image cleanup could not be confirmed; image delivery remains unavailable.');};
// Authored-fixture e2e runs may shorten the icon URL lifetime to watch its refresh; the app always uses 90 s.
const iconUrlLifetime=()=>process.env.VIBEMINER_AUTHORED_FIXTURES==='1'&&/^[1-9][0-9]{3,4}$/.test(process.env.VIBEMINER_FIXTURE_ICON_URL_LIFETIME_MS??'')?Number(process.env.VIBEMINER_FIXTURE_ICON_URL_LIFETIME_MS):90000;
// The data folder (D128) is resolved once at start: VIBEMINER_DATA_DIR, then the user's choice, then the per-OS default.
let dataFolder:DataFolder|null=null;
const engineDataDirectory=()=>{if(!dataFolder)throw new Error('The data folder is resolved when the app is ready.');return dataFolder.path;};
/** Resolves and creates the data folder; false, after telling the user, when the saved choice can't be used. */
function openDataFolder():boolean{
  const resolved=resolveDataFolder({platform:process.platform,env:process.env,home:homedir()},readDataFolderChoice(app.getPath('userData'),process.platform));
  const problem='problem' in resolved?resolved:prepareDataFolder(resolved);
  if(problem){dialog.showErrorBox('VibeMiner can\'t open its data folder',`${problem.problem}\n\n${problem.remedy}`);return false;}
  if('problem' in resolved)return false;
  dataFolder=resolved;
  // Test and QA folders (the override) are left alone; the user's own folder is kept out of Time Machine on macOS.
  if(resolved.source!=='override')void excludeFromBackups(resolved.path,process.platform).then(result=>{if(result==='failed')console.error('The data folder could not be excluded from Time Machine backups.');});
  return true;
}
let window: BrowserWindow | null = null;
let quitting = false;
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
async function bounded(work:Promise<unknown>,milliseconds:number):Promise<void>{let timer:ReturnType<typeof setTimeout>;await Promise.race([work.then(()=>{},()=>{}),new Promise<void>(resolve=>{timer=setTimeout(resolve,milliseconds);})]);clearTimeout(timer!);}
function startSession():Session{
  const engine=createEngine();
  const icons=new IconsController(engine,engineDataDirectory(),undefined,undefined,iconUrlLifetime());
  const geometryBinary=new GeometryBinaryProtocol({dataDirectory:engineDataDirectory(),hasEngineExited:()=>engine.hasExited,openGeometry:async(params,signal)=>{
    const id=`geometry_open_${randomUUID()}`,cancel=()=>{void engine.request('request.cancel',{requestId:id}).catch(()=>{/* Original Open remains observed. */});};
    signal.addEventListener('abort',cancel,{once:true});try{if(signal.aborted)throw new EngineFailure('CANCELED','The geometry view was left.');return await engine.request<CreatureGeometryLeaseDescriptor>('creatures.geometry.open',params,id);}finally{signal.removeEventListener('abort',cancel);}
  },closeGeometry:async leaseId=>{try{await engine.request('creatures.geometry.close',{leaseId});}catch(error){if(!engine.hasExited)throw error;}}});
  const geometry=new GeometryViewController({icons,binary:geometryBinary,read:p=>engine.request<CreatureGeometryResult>('creatures.geometry.read',p),prepare:(p,id)=>engine.request<CreatureGeometryResult>('creatures.geometry.prepare',p,id),notify:(owner,notice)=>{if(geometryDocument===owner&&window&&!window.isDestroyed()&&!window.webContents.isDestroyed()&&window.webContents.id===owner.webContentsId&&window.webContents.mainFrame===owner.frame&&window.webContents.getURL()===appDocumentURL())window.webContents.send('discovery:geometryRetirement',notice);}});
  engine.on('unavailable',()=>{geometry.invalidate('engineUnavailable');void icons.revokeDocument().catch(iconCleanupFailure);});
  engine.on('terminated',()=>geometry.engineExited());
  // Only the current process reaches the renderer: an older one's progress is dropped with its replies.
  engine.on('progress', progress => { if (supervisor?.current?.engine === engine && window && !window.isDestroyed()) window.webContents.send('discovery:progress', progress); });
  const ready = engine.hello();
  // Observe launch failure before any renderer request arrives.
  void ready.catch(() => {});
  const session:Session={engine,ready,icons,geometryBinary,geometry,retire:async()=>{
    // The renderer's views of this process retire as if its document were replaced, so geometry can release them.
    geometry.invalidate('engineUnavailable');
    if(supervisor.current===session)geometryDocumentGone();
    const geometryRetired=geometry.dispose();
    await retireIconsAndEngine(icons,engine).catch(iconCleanupFailure);
    await bounded(geometryRetired,1500);
    return engine.hasExited;
  }};
  return session;
}
function registerBridge(): void {
  ipcMain.handle('discovery:invoke', async (event, method: string, params: unknown, requestId?: unknown): Promise<BridgeReply<unknown>> => {
    try {
      if(typeof method!=='string')throw new EngineFailure('INVALID_REQUEST','The operation was refused.');
      if (!window || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame) throw new EngineFailure('INVALID_REQUEST', 'The operation was refused.');
      if(method.startsWith('desktop.geometry.')||method==='creatures.geometry.read'||method==='creatures.geometry.prepare'){
        const valid=method==='desktop.geometry.enter'?validGeometryViewEnter(params):method==='desktop.geometry.open'?validGeometryViewOpen(params):method==='desktop.geometry.bodyRetired'?validGeometryBodyRetired(params):method==='desktop.geometry.sceneDisposed'?validGeometrySceneDisposal(params):method==='desktop.geometry.release'?validGeometryViewRelease(params):method==='creatures.geometry.read'?validGeometryRead(params):method==='creatures.geometry.prepare'&&validGeometryParams(params)&&validRequestId(requestId);
        if(!valid)throw new EngineFailure('INVALID_REQUEST','Choose one exact geometry view operation.');
        const owner=geometryOwner();const{engine,geometry}=supervisor.current;await supervisor.current.ready;if(!method.endsWith('.sceneDisposed')&&!method.endsWith('.release')&&!owner.isCurrent())throw new EngineFailure('CREATURE_GEOMETRY_VIEW_RETIRED','This geometry document was left.');
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
        if(supervisor.current.geometry!==geometry)throw new EngineFailure('ENGINE_RESTARTED','This reply came from an engine that has been restarted since, so it was dropped.',true);
        return{ok:true,value};
      }
      if(method==='desktop.engine.status'||method==='desktop.engine.restart'){
        if(!validEmpty(params))throw new EngineFailure('INVALID_REQUEST','The engine status takes no parameters.');
        const value:EngineStatus=method==='desktop.engine.restart'?await supervisor.restart():supervisor.status;
        return{ok:true,value};
      }
      if(method==='desktop.icons'){const p=params as{params?:unknown;viewId?:unknown};if(!p||typeof p!=='object'||Object.keys(p).length!==2||!validIconBatch(p.params,12)||!validIconView(p.viewId)||!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose at most twelve exact visible icons.');const batch=p.params,view=p.viewId,icons=supervisor.current.icons,document=icons.documentToken;await supervisor.current.ready;if(icons!==supervisor.current.icons||document!==icons.documentToken||!window||event.senderFrame!==window.webContents.mainFrame)throw new EngineFailure('ICON_VIEW_RETIRED','The image document was left.');return{ok:true,value:await supervisor.call(async s=>{if(s.icons!==icons)throw new EngineFailure('ICON_VIEW_RETIRED','The image document was left.');return icons.batch(batch,requestId,view);})};}
      if(method==='desktop.iconsRelease'){const p=params as{viewId?:unknown};if(!p||typeof p!=='object'||Object.keys(p).length!==1||!validIconView(p.viewId))throw new EngineFailure('INVALID_REQUEST','Invalid icon view.');return{ok:true,value:await supervisor.current.icons.release(p.viewId)};}
      // Main owns the data folder path: the renderer can only ask for these two fixed actions on it.
      if (method === 'desktop.storage.copyPath' || method === 'desktop.storage.showFolder') {
        if (!validEmpty(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid storage folder action.');
        const folder = engineDataDirectory();
        if (method === 'desktop.storage.copyPath') { clipboard.writeText(folder); return { ok: true, value: { copied: true } }; }
        if (await shell.openPath(folder)) throw new EngineFailure('STORAGE_FOLDER_UNAVAILABLE', 'The storage folder could not be opened in the file manager.', true);
        return { ok: true, value: { shown: true } };
      }
      if (method === 'settings.read' || method === 'settings.listfile.read' || method === 'settings.listfile.check') { if (!validEmpty(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid settings query.'); }
      else if (method === 'settings.set') { if (!validSettingsSet(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one known setting value.'); }
      else if (method === 'settings.storage.measure' || method === 'settings.storage.builds') { if (!validEmpty(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Invalid storage measurement request.'); }
      else if (method === 'settings.storage.free') { if (!validPreparedDataFree(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact opened build.'); }
      else if (method === 'desktop.capabilities' || method === 'retention.list' || method === 'products.list') { if (!params || typeof params !== 'object' || Object.keys(params).length) throw new EngineFailure('INVALID_REQUEST', 'Invalid product query.'); }
      else if(method==='items.effects.data'||method==='items.effects.prepare'){if(!validItemEffectsSelection(params)||method==='items.effects.prepare'&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose one exact admitted Item.');}
      else if(method==='items.effects.page'){if(!validItemEffectsPage(params))throw new EngineFailure('INVALID_REQUEST','Choose one bounded exact effects page.');}
      else if(method==='items.effects.details'||method==='items.effects.details.prepare'){if(!validItemEffectDetails(params)||method.endsWith('.prepare')&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose bounded ordered effects edge keys.');}
      else if(method==='items.effects.names'||method==='items.effects.names.prepare'){if(!validItemEffectNames(params)||method.endsWith('.prepare')&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose bounded effects name keys and the current input artifact.');}
      else if(method==='creatures.display.facts'){if(!validCreatureDisplay(params))throw new EngineFailure('INVALID_REQUEST','Choose one exact Creature source slot.');}
      else if(method==='creatures.labels'){if(!params||typeof params!=='object'||Object.keys(params).length)throw new EngineFailure('INVALID_REQUEST','Creature labels take no parameters.');}
      else if(method==='creatures.display.read'||method==='creatures.display.prepare'){if(!validCreatureDisplay(params)||method==='creatures.display.prepare'&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose one exact Creature source slot.');}
      else if(method==='creatures.data'||method==='creatures.prepare'){if(!validCreatureContext(params)||method==='creatures.prepare'&&!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose one exact Creature context and research locale enUS.');}
      else if(method==='creatures.page'){if(!validCreaturePage(params))throw new EngineFailure('INVALID_REQUEST','Invalid bounded Creature query.');}
      else if(method==='creatures.inspect'){if(!validCreatureInspect(params))throw new EngineFailure('INVALID_REQUEST','Choose one exact admitted Creature ID.');}
      else if(method==='items.classifications'){if(!validClassifications(params)||!validRequestId(requestId))throw new EngineFailure('INVALID_REQUEST','Choose at most fifty exact Item IDs for source labels.');}
      else if (method === 'items.data' || method === 'items.prepare') { if (!validItemsContext(params) || method === 'items.prepare' && !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact Items context and research locale enUS.'); }
      else if (method === 'items.page') { if (!validItemsPage(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded Items query.'); }
      else if (method === 'items.inspect' || method === 'items.facts') { if (!validItemInspect(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact admitted Item ID.'); }
      else if (method === 'items.labels') { if (!validItemLabelsParams(params)) throw new EngineFailure('INVALID_REQUEST', 'Item labels take no parameters.'); }
      else if (method === 'retention.read') { if (!validRetentionTarget(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid retention target.'); }
      else if (method === 'retention.set') { if (!validRetentionSet(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid retention command.'); }
      else if (method === 'discovery.read') { if (!validRead(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery query.'); }
      else if (method === 'discovery.readAll') { if (!validReadAll(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery query.'); }
      else if (method === 'discovery.refreshAll') { if (!params || typeof params !== 'object' || Object.keys(params).length || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery request.'); }
      else if (method === 'discovery.refresh') { if (!validProduct((params as { productId?: unknown })?.productId) || !validRequestId(requestId) || Object.keys(params as object).length !== 1) throw new EngineFailure('INVALID_REQUEST', 'Invalid discovery request.'); }
      else if (method === 'tables.prepare' || method === 'tables.data') { if (!validTableData(params) || method === 'tables.prepare' && !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact table and research locale enUS.'); }
      else if (method === 'tables.rows' || method === 'tables.referenceText') { if (!validTableRows(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded table record query.'); }
      else if (method === 'tables.follow') { if (!validTableFollow(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose one declared reference, an exact value and a bounded page.'); }
      else if (method === 'tables.catalog') { if (!validTablesCatalog(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded definition catalogue query.'); }
      else if (method === 'tables.definition') { if (!validTableDefinition(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact provider table identity.'); }
      else if (method === 'download.read') { if (!validDownloadRead(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded download-object query.'); }
      else if (method === 'builds.availability.check') { if (!validAvailabilityCheck(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact saved build to check.'); }
      else if (method === 'download.check') { if (!validDownloadCheck(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact measured download ledger.'); }
      else if (method === 'download.summaries') { if (!validSummariesParams(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one to 100 distinct exact builds.'); }
      else if (method === 'download.changes') { if (!validChangesParams(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Name the summary revision of the last read.'); }
      else if (method === 'inventory.read') { if (!validInventoryRead(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded inventory query.'); }
      else if (method === 'inventory.files') { if (!validInventoryFiles(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded Files query.'); }
      else if (method === 'inventory.file') { if (!validInventoryFile(params)) throw new EngineFailure('INVALID_REQUEST', 'Invalid bounded file inspector query.'); }
      else if (method === 'inventory.catalog') { if (!validFileCatalog(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact opened build and source region.'); }
      else if (method === 'inventory.catalogs') { if (!params || typeof params !== 'object' || Object.keys(params).length) throw new EngineFailure('INVALID_REQUEST', 'The file list preparations take no parameters.'); }
      else if (method === 'inventory.catalog.set') { if (!validFileCatalogSet(params)) throw new EngineFailure('INVALID_REQUEST', 'Choose one exact build and stop or resume.'); }
      else if (method === 'workspace.open') { if (!validOpen(params) || !validRequestId(requestId)) throw new EngineFailure('INVALID_REQUEST', 'Choose an exact saved build and source region.'); }
      else if (method === 'request.cancel') { if (!validRequestId((params as { requestId?: unknown })?.requestId) || Object.keys(params as object).length !== 1) throw new EngineFailure('INVALID_REQUEST', 'Invalid cancellation request.'); }
      else throw new EngineFailure('INVALID_REQUEST', 'Unsupported desktop operation.');
      const value = await supervisor.call(({engine}) => method === 'desktop.capabilities' ? Promise.resolve([...engine.capabilities]) : engine.request(method, params as object, typeof requestId === 'string' ? requestId : undefined));
      return { ok: true, value };
    } catch (error) {
      const failure = error instanceof EngineFailure ? error : new EngineFailure('ENGINE_UNAVAILABLE', 'The local engine is unavailable. Restart VibeMiner to reconnect.', true);
      return { ok: false, error: { code: failure.code, message: failure.message, retryable: failure.retryable, ...(failure.definition ? { definition: { tableId: failure.definition.tableId, definitionFetch: failure.definition.definitionFetch } } : {}) } };
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
  window.webContents.on('did-start-navigation',(_event,_url,inPlace,mainFrame)=>{if(mainFrame&&!inPlace){supervisor.current.geometry.invalidate('documentReplaced');void supervisor.current.icons.revokeDocument().catch(iconCleanupFailure);}});
  window.webContents.on('frame-created',(_event,details)=>{if(!details.frame||details.frame.parent){geometryDocumentBlocked=true;supervisor.current.geometry.invalidate('documentReplaced');}});
  window.webContents.on('did-navigate',()=>geometryDocumentGone());
  window.webContents.on('render-process-gone',()=>{geometryDocumentGone();void supervisor.current.icons.revokeDocument().catch(iconCleanupFailure);});
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.on('closed', () => {geometryDocumentGone();window = null;});
  await window.loadFile(path.join(__dirname, '../renderer/index.html'));
}
app.whenReady().then(async () => {
  let opened=false;
  try{opened=openDataFolder();}
  catch(error){dialog.showErrorBox('VibeMiner can\'t open its data folder',`${error instanceof Error?error.message:String(error)}\n\nStart VibeMiner again; if it keeps happening, set VIBEMINER_DATA_DIR to a folder you own.`);}
  if(!opened){app.exit(1);return;}
  supervisor=new EngineSupervisor(startSession);
  supervisor.on('status',(status:EngineStatus)=>{if(window&&!window.isDestroyed())window.webContents.send('discovery:engineStatus',status);});
  // Reuse the sole app-window session to preserve its existing local preferences.
  const appSession=session.defaultSession;
  appSession.webRequest.onBeforeRequest({urls:['vibeminer-image://*/*','vibeminer-geometry://*/*']},(details,done)=>{const owner=window&&!window.isDestroyed()?window.webContents:null;done({cancel:details.url.startsWith('vibeminer-geometry:')?!authorizedGeometryFetch(details,owner,appSession,geometryDocument?.isCurrent()??false):!authorizedIconImage(details,owner)});});
  appSession.protocol.handle('vibeminer-image',request=>supervisor.current.icons.handle(request));
  appSession.protocol.handle('vibeminer-geometry',request=>supervisor.current.geometryBinary.handle(request));
  registerBridge();
  await openWindow();
});
app.on('window-all-closed', () => app.quit());
app.on('before-quit', event => {
  if (quitting || !supervisor) return;
  event.preventDefault(); quitting = true;
  supervisor.current.geometry.invalidate('shutdown');if(window&&!window.isDestroyed())window.destroy();
  // A restart in progress settles first (it starts no new process once closing), then the current session is retired.
  void supervisor.close().then(({geometry,icons,engine})=>{
    geometry.invalidate('shutdown');
    const geometryRetirement=geometry.dispose();void geometryRetirement.catch(geometryCleanupFailure);
    return retireIconsAndEngine(icons,engine).then(async retired=>{await geometryRetirement;if(!retired)iconCleanupFailure();});
  }).catch(iconCleanupFailure).finally(()=>app.quit());
});
