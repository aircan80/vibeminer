#!/usr/bin/env node
import {effectsScenario,effectsNotPrepared,effectsRows,effectsDefinition} from './item-effects.mjs';
import {creatureDisplay,displayDefinition} from './creature-display.mjs';
import {creatureGeometry,geometryArtifact} from './creature-geometry.mjs';
import {creaturesSnapshot,creaturesPage,creatureInspect,creatureSummary,creatureRow,creatureDefinition} from './creatures.mjs';
import {classificationBatch,classificationSource,classificationRows,classificationDefinition} from './classifications.mjs';
import {png,pngSha,iconOutcome} from './icons.mjs';
import {randomBytes} from 'node:crypto';
import {itemsSnapshot,itemRows,itemsPage,itemDetail,itemsDefinition} from './items.mjs';
// Authored Electron boundary fixture. No network, game parser, or production fallback.
import { createInterface } from 'node:readline';
import { readFileSync, existsSync, writeFileSync, appendFileSync, watch, unlinkSync, mkdirSync, renameSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
// Like VerifiedObjectStore: never rewrite a published object in place. Main may be verifying it for another open.
function publishObject(file,bytes){if(existsSync(file)&&readFileSync(file).equals(bytes))return;const staged=`${file}.${randomBytes(8).toString('hex')}.part`;writeFileSync(staged,bytes);renameSync(staged,file);}
const root = fileURLToPath(new URL('../../../', import.meta.url));
const directory = process.env.VIBEMINER_FIXTURE_CONTROL_DIR;
if (!directory) throw new Error('This test fixture requires an explicit control directory.');
const seed = JSON.parse(readFileSync(path.join(root, 'contracts/examples/discovery-page.json'), 'utf8'));
seed.builds[0].isNew = true;
const second = structuredClone(seed.builds[0]);
second.id = 'c'.repeat(64); second.buildConfig = 'f'.repeat(32); second.identityFields.BuildConfig = second.buildConfig;
seed.builds.push(second);
// Community definitions are pinned metadata, not game records. The four snapshots
// were exported offline through VibeMiner's original C# DBD reader.
const tableDefinitions = JSON.parse(readFileSync(path.join(root, 'tests/desktop/fixtures/table-definitions.json'), 'utf8')).definitions;
const tablesProvider = Object.values(tableDefinitions)[0].provider;
const tableManifest = JSON.parse(readFileSync(path.join(root, `src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs/${tablesProvider.revision}/manifest.json`), 'utf8'));
const tableEntries = tableManifest.map(entry => ({ tableId: entry.tableHash.toLowerCase(), name: entry.tableName ?? null, db2FileDataId: entry.db2FileDataID === undefined ? null : String(entry.db2FileDataID), definitionState: tableDefinitions[entry.tableHash.toLowerCase()] ? 'bundled' : 'notBundled', sourceState: 'notChecked', rowsState: 'notPrepared' })).sort((a, b) => Number(b.definitionState === 'bundled') - Number(a.definitionState === 'bundled') || (a.name ?? '').localeCompare(b.name ?? '', 'en', { sensitivity: 'base' }) || a.tableId.localeCompare(b.tableId));
const offlineEmpty = process.env.VIBEMINER_FIXTURE_OFFLINE_EMPTY === '1';
const saved = new Map(offlineEmpty ? [] : [['wow', { ...seed, connection: 'notChecked' }]]);
// VIBEMINER_FIXTURE_LONG_PRODUCTS=1 (#52): two more products with long real-style ids, one saved build each, so list cells meet ids as long as wow_classic_era_ptr.
const longProducts = process.env.VIBEMINER_FIXTURE_LONG_PRODUCTS === '1' ? [{ id: 'wow_classic_era_ptr', label: 'Classic Era PTR', description: 'Authored long product id' }, { id: 'wow_classic_beta', label: 'Classic Beta', description: 'Authored long product id' }] : [];
for (const [index, product] of longProducts.entries()) {
  const build = { ...structuredClone(seed.builds[0]), productId: product.id, id: String(index + 1).repeat(64) };
  saved.set(product.id, { ...structuredClone(seed), productId: product.id, connection: 'notChecked', builds: [build] });
}
// VIBEMINER_FIXTURE_PERSIST=1 keeps saved observations in the control directory, so a relaunch sees them like a real catalog.
const persisted = process.env.VIBEMINER_FIXTURE_PERSIST === '1' ? path.join(directory, 'persisted-catalog.json') : null;
if (persisted && existsSync(persisted)) { saved.clear(); for (const [id, page] of JSON.parse(readFileSync(persisted, 'utf8'))) saved.set(id, page); }
const persist = () => { if (persisted) writeFileSync(persisted, JSON.stringify([...saved])); };
const active = new Map();
const opened = new Map();
const tableData = new Map();
const labelsEnabled=!!process.env.VIBEMINER_FIXTURE_CLASSIFICATIONS, subclassEnabled=process.env.VIBEMINER_FIXTURE_CLASSIFICATIONS==='subclass', labelAdmissions=new Map();
const labelScenario=()=>existsSync(path.join(directory,'labels-scenario.json'))?JSON.parse(readFileSync(path.join(directory,'labels-scenario.json'),'utf8')).scenario:'ready';
const iconsEnabled=process.env.VIBEMINER_FIXTURE_ICONS==='1', iconLeases=new Map();
const dataDirectory=process.argv[process.argv.indexOf('--data-dir')+1];
const displayEnabled=process.env.VIBEMINER_FIXTURE_CREATURE_DISPLAY==='1',displayReady=new Set(),displayTargets=new Map();
// Diagnostic geometry over the authored display metadata. geometry-state.json picks the outcome; cached-geometry skips Prepare.
const geometryEnabled=displayEnabled&&process.env.VIBEMINER_FIXTURE_CREATURE_GEOMETRY==='1',geometryReady=new Set(),geometryLeases=new Set();
const creaturesEnabled=process.env.VIBEMINER_FIXTURE_CREATURES==='1',creatureAdmissions=new Map();
const effectsEnabled=!!process.env.VIBEMINER_FIXTURE_EFFECTS,effectsAdmissions=new Set(),effectsTargets=new Map();
const effectsMode=()=>existsSync(path.join(directory,'effects-scenario.json'))?JSON.parse(readFileSync(path.join(directory,'effects-scenario.json'),'utf8')).scenario:'ready';
const itemsEnabled=process.env.VIBEMINER_FIXTURE_ITEMS==='1', itemsAdmissions=new Map();
const referenceTextEnabled=process.env.VIBEMINER_FIXTURE_REFERENCE_TEXT==='1';
const followEnabled=process.env.VIBEMINER_FIXTURE_FOLLOW==='1';
// The authored generic rows every prepared fixture table returns (tables.rows default branch).
function genericRows(snapshot){const all=Array.from({length:60},(_,index)=>({id:String(index),isCopy:false,copySourceId:null,values:snapshot.columns.map((column,i)=>column.interpretation?'3011920367':column.isId?String(index):column.family==='string'?`Authored clear value ${index}`:column.family==='float'?'1.25':String(index+i+1))}));if(snapshot.textIssueCount==='1'){const columnIndex=snapshot.columns.findIndex(column=>column.family==='string'&&!column.isId);all[7].values[columnIndex]='';all[7].textIssues=[{columnIndex,code:'DB2_TEXT_ENCODING_INVALID',rawBytesHex:existsSync(path.join(directory,'table-long-text-issue'))?'ff'.repeat(65535):'c328'}];}return all;}
const retentionEnabled=process.env.VIBEMINER_FIXTURE_RETENTION==='1';
// D071: the file catalog prepared after Open. Pending until the control file file-catalog-ready exists (file-catalog-failed fails it).
const fileCatalogEnabled=process.env.VIBEMINER_FIXTURE_FILE_CATALOG==='1', FILE_CATALOG_GENERATION='41';
const catalogStops=new Set(), catalogRetried=new Set();
const fileCatalogReady=key=>fileCatalogEnabled&&!!opened.get(key)&&!opened.get(key).inventory&&existsSync(path.join(directory,'file-catalog-ready'));
const retentionJobs=new Map();
const retentionSeed=JSON.parse(readFileSync(path.join(root,'contracts/examples/retention-snapshot.json'),'utf8'));
function retentionSnapshot(target){
 const key=JSON.stringify(target);let value=retentionJobs.get(key);
 if(!value){value=structuredClone(retentionSeed);Object.assign(value,{target,jobId:null,revision:'9007199254740993',mode:'onDemand',state:'onDemand',pendingObjects:0,progress:null,objectCount:'60',totalEncodedBytes:'6000'});Object.assign(value.coverage,{receiptObjects:'2',receiptBytes:'200',presentObjects:'1',presentBytes:'100'});retentionJobs.set(key,value);}
 if(existsSync(path.join(directory,'retention-state.json')))Object.assign(value,JSON.parse(readFileSync(path.join(directory,'retention-state.json'),'utf8')));
 if(value.state==='stopping'&&existsSync(path.join(directory,'finish-retention-stop')))value.state=value.mode==='onDemand'?'onDemand':value.paused?'paused':'queued';
 return structuredClone(value);
}
const shutdown = new AbortController();
const send = value => process.stdout.write(JSON.stringify({ protocolVersion: 1, ...value }) + '\n');
const reply = (id, result) => send({ id, result });
const fail = (id, code, message) => send({ id, error: { code, message, retryable: true } });
function pageFor(productId, filter = 'all', limit = 50) {
  const page = structuredClone(saved.get(productId) || { ...seed, productId, builds: [], lastCheckedAt: null, checkedRegions: [], errors: [], hasSavedHistory: false, connection: 'notChecked' });
  page.builds = page.builds.filter(b => filter === 'all' || filter === 'local' && b.isLocal || filter === 'advertised' && b.isAdvertised).slice(0, limit);
  return page;
}
const fixtureProducts = [{ id: 'wow', label: 'Retail', description: 'Authored discovery fixture' }, { id: 'wowt', label: 'Public Test Realm', description: 'Authored second product' }, ...longProducts];
/** The authored outcome of checking one product: offline without history, wowt with a regional problem. */
function checkedPage(productId) {
  // Per-product failure controls, shaped like the real engine: HTTP 500 on both documents saves partial with no
  // checked regions; unreachable saves offline. Both keep earlier builds.
  if (existsSync(path.join(directory, `http500-${productId}`))) {
    const page = pageFor(productId, 'all', 100);
    const problem = { region: null, code: 'HTTP_STATUS', message: 'Official metadata returned an unsuccessful HTTP status.', severity: 'problem' };
    return { ...page, connection: 'partial', lastCheckedAt: new Date().toISOString(), checkedRegions: [], errors: [problem, problem] };
  }
  if (existsSync(path.join(directory, `offline-${productId}`))) {
    const page = pageFor(productId, 'all', 100);
    return { ...page, connection: 'offline', lastCheckedAt: new Date().toISOString(), checkedRegions: [], errors: [{ region: null, code: 'NETWORK_UNAVAILABLE', message: 'Authored unavailable metadata source', severity: 'problem' }] };
  }
  if (offlineEmpty) {
    const page = pageFor(productId);
    page.connection = 'offline'; page.lastCheckedAt = new Date().toISOString();
    page.errors = [{ region: null, code: 'NETWORK_UNAVAILABLE', message: 'Authored unavailable metadata source', severity: 'problem' }];
    return page;
  }
  const page = structuredClone(seed);
  page.productId = productId; page.connection = 'online'; page.lastCheckedAt = new Date().toISOString();
  for (const build of page.builds) { build.productId = productId; if (productId !== 'wow') build.id = (productId === 'wowt' ? 'e' : '0').repeat(64); }
  // Shaped like real retail metadata (#47): versions advertises sg and xx, cdns has no row for them. The engine keeps
  // the advertisement, saves no route and reports each region as a note; the check is still online.
  if (existsSync(path.join(directory, 'no-route-sg-xx'))) {
    if (productId === 'wowt') page.builds = page.builds.slice(0, 1);
    for (const build of page.builds) build.advertisedRegions = ['sg', 'us', 'xx'];
    page.checkedRegions = ['sg', 'us', 'xx'];
    page.errors = ['sg', 'xx'].map(region => ({ region, code: 'ROUTE_MISSING', message: 'Advertised without a CDN route, so not usable as a download source.', severity: 'note' }));
    return page;
  }
  if (productId === 'wowt') {
    page.builds = page.builds.slice(0, 1);
    page.connection = 'partial'; page.errors = [{ region: 'xx', code: 'ROUTE_UNAVAILABLE', message: 'Authored missing regional route', severity: 'problem' }];
  }
  return page;
}
/** Cross-product page over the authored products in registry order; the authored pages fit one page. */
function catalogFor(productId = null, filter = 'all', limit = 50) {
  const products = fixtureProducts.map(product => { const page = pageFor(product.id); return { productId: product.id, lastCheckedAt: page.lastCheckedAt, checkedRegions: page.checkedRegions, errors: page.errors, hasSavedHistory: page.hasSavedHistory, connection: page.connection }; });
  const builds = fixtureProducts.filter(product => !productId || product.id === productId).flatMap(product => pageFor(product.id, filter, 100).builds).slice(0, limit);
  return { productId, filter, builds, nextCursor: null, products, refreshProblems: [] };
}
function waitForRelease(name, signal) {
  return new Promise((resolve, reject) => {
    const target = path.join(directory, name);
    let settled = false;
    const finish = error => {
      if (settled) return; settled = true; watcher.close(); signal.removeEventListener('abort', abort);
      error ? reject(error) : resolve();
    };
    const abort = () => finish(new Error('canceled'));
    const watcher = watch(directory, () => { if (existsSync(target)) finish(); });
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) abort(); else if (existsSync(target)) finish();
  });
}
// Summary revision (D077): this run's epoch plus a count that moves whenever what a summary is read from changes here:
// an open, a retention job or the scripted stored-summary.json and retention-state.json. A summaries-error file makes
// summary reads fail (#50); it also moves the revision, so a standing download.changes wait answers and the next read fails.
const summaryEpoch = randomBytes(8).toString('hex');
let summaryCount = 0, summarySeen = null;
function summaryRevision() {
  const text = name => existsSync(path.join(directory, name)) ? readFileSync(path.join(directory, name), 'utf8') : null;
  const seen = JSON.stringify([text('stored-summary.json'), text('retention-state.json'), [...opened.keys()], [...retentionJobs.values()], existsSync(path.join(directory, 'summaries-error'))]);
  if (seen !== summarySeen) { if (summarySeen !== null) summaryCount++; summarySeen = seen; }
  return `${summaryEpoch}-${summaryCount}`;
}
function settingsSnapshot() {
  const file = path.join(directory, 'settings.json'), stored = existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : null;
  return { formatVersion: 1, file: stored ? 'read' : 'absent', problem: null, lastChangedAt: stored?.changedAt ?? null,
    communityBuildHistory: { value: stored?.value ?? false, defaultValue: false, source: stored ? 'saved' : 'default', problem: null },
    storage: { dataDirectory: path.join(directory, 'catalog') }, downloadLimits: { parallelDownloads: 4, backgroundDownloads: 2, bandwidthCapBytesPerSecond: null, adjustable: false } };
}
async function dispatch(request) {
  const { id, method, params = {} } = request;
  appendFileSync(path.join(directory, 'requests.ndjson'), JSON.stringify({ id, method, params }) + '\n');
  if (method === 'hello') return reply(id, { protocolVersion: 1, engineVersion: 'authored-fixture', capabilities: ['discovery.read', 'discovery.refresh', 'discovery-all-products-v1', 'settings-v1', 'download-summary-v1', 'inventory-files-v1',...(fileCatalogEnabled?['file-catalog-v1']:[]),...(retentionEnabled?['retention-v1']:[]),...(itemsEnabled?['items-v1']:[]),...(effectsEnabled?process.env.VIBEMINER_FIXTURE_EFFECTS==='split'?['item-effects-links-v1']:['item-effects-links-v1','item-effects-inputs-v1','item-effect-spell-names-v1']:[]),...(creaturesEnabled?['creatures-v1']:[]),...(displayEnabled?['creature-display-metadata-v1']:[]),...(geometryEnabled?['creature-source-geometry-v1']:[]),...(iconsEnabled?['item-icons-v1']:[]),...(labelsEnabled?['item-classifications-v1']:[]),...(subclassEnabled?['item-subclass-labels-v1']:[]),...(referenceTextEnabled?['tables-reference-text-v1']:[]),...(followEnabled?['tables-follow-v1']:[])] });
  if(geometryEnabled&&method==='creatures.geometry.close'){geometryLeases.delete(params.leaseId);return reply(id,{closed:true});}
  if(creaturesEnabled&&method.startsWith('creatures.')){
    const context={productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,researchLocale:params.researchLocale},key=`${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
    if(!opened.has(key))return fail(id,'WORKSPACE_NOT_OPEN','Open the exact authored workspace first.');
    function admit(){const value=creaturesSnapshot(context,existsSync(path.join(directory,'creatures-partial')));creatureAdmissions.set(key,value);tableData.set(`${key}|c9d6b6b3|enUS`,value.creatureSource);return value;}
    if(method==='creatures.data'){const value=creatureAdmissions.get(key)??(existsSync(path.join(directory,'cached-creatures'))?admit():null);return reply(id,{snapshot:value,dependencyState:value?'ready':'notPrepared',sourceArtifactId:value?.creatureSource.artifactId??null});}
    if(method==='creatures.prepare'){const controller=new AbortController();active.set(id,controller);writeFileSync(path.join(directory,'waiting-creatures'),id);send({type:'progress',requestId:id,sequence:1,stage:'Preparing Creature source',region:params.sourceRegion,completed:25,total:100,unit:'bytes'});try{await waitForRelease('release-creatures',controller.signal);if(existsSync(path.join(directory,'creatures-load-error.json')))return send({id,error:JSON.parse(readFileSync(path.join(directory,'creatures-load-error.json'),'utf8'))});if(existsSync(path.join(directory,'creatures-offline')))return fail(id,'NETWORK_UNAVAILABLE','The authored Creature source is unavailable offline. Retained evidence is preserved.');return reply(id,admit());}catch{if(existsSync(path.join(directory,'hold-creatures-cancel'))&&!shutdown.signal.aborted){try{await waitForRelease('finish-creatures-cancel',shutdown.signal);}catch{}}return fail(id,'CANCELED','Authored Creature consumer stopped after cleanup.');}finally{active.delete(id);}}
    const value=creatureAdmissions.get(key);if(!value||value.artifactId!==params.artifactId)return fail(id,'CREATURES_STALE','This exact authored Creature artifact is no longer current.');
    if(displayEnabled&&method.startsWith('creatures.display.')){const summary=creatureSummary(value,params.creatureId),requestKey=`${key}|${params.artifactId}|${params.creatureId}|${params.slot}`;if(!summary)return fail(id,'CREATURES_STALE','The authored selected Creature is unavailable.');const scenario=()=>existsSync(path.join(directory,'creature-display-state.json'))?JSON.parse(readFileSync(path.join(directory,'creature-display-state.json'),'utf8')).state:'metadataResolved';function metadata(state){const result=creatureDisplay(value,summary,params.slot,opened.get(key).sourceReference,state);for(const target of result.sources){tableData.set(`${key}|${target.snapshot.tableId}|enUS`,target.snapshot);displayTargets.set(`${key}|${target.snapshot.tableId}`,target);}return result;}
      if(method==='creatures.display.read'){if(existsSync(path.join(directory,'hold-display-read')))await waitForRelease('release-display-read',shutdown.signal);return reply(id,metadata(displayReady.has(requestKey)||existsSync(path.join(directory,'cached-display'))?scenario():'notPrepared'));}
      if(method==='creatures.display.prepare'){const controller=new AbortController();active.set(id,controller);writeFileSync(path.join(directory,`waiting-display-${params.creatureId}-${params.slot}`),id);send({type:'progress',requestId:id,sequence:1,stage:'Preparing CreatureDisplayInfo metadata',region:params.sourceRegion,completed:25,total:100,unit:'bytes'});try{await waitForRelease('release-display',controller.signal);if(existsSync(path.join(directory,'display-stale')))return fail(id,'CREATURE_DISPLAY_STALE','The authored target source changed. Core Creature records remain valid.');displayReady.add(requestKey);return reply(id,metadata(existsSync(path.join(directory,'display-offline'))?'unavailable':scenario()));}catch{if(existsSync(path.join(directory,'hold-display-cancel'))&&!shutdown.signal.aborted){try{await waitForRelease('finish-display-cancel',shutdown.signal);}catch{}}return fail(id,'CANCELED','Authored selected-slot consumer stopped after cleanup.');}finally{active.delete(id);}}
    }
    if(geometryEnabled&&method.startsWith('creatures.geometry.')){const summary=creatureSummary(value,params.creatureId),requestKey=`${key}|${params.artifactId}|${params.creatureId}|${params.slot}`;if(!summary)return fail(id,'CREATURES_STALE','The authored selected Creature is unavailable.');
      const display=creatureDisplay(value,summary,params.slot,opened.get(key).sourceReference,'metadataResolved'),artifact=geometryArtifact(),scenario=()=>existsSync(path.join(directory,'geometry-state.json'))?JSON.parse(readFileSync(path.join(directory,'geometry-state.json'),'utf8')).state:'ready';
      const result=state=>creatureGeometry(display,state,artifact.manifest);
      if(method==='creatures.geometry.read')return reply(id,result(geometryReady.has(requestKey)||existsSync(path.join(directory,'cached-geometry'))?scenario():'notPrepared'));
      if(method==='creatures.geometry.prepare'){const controller=new AbortController();active.set(id,controller);writeFileSync(path.join(directory,`waiting-geometry-${params.creatureId}-${params.slot}`),id);send({type:'progress',requestId:id,sequence:1,stage:'Acquiring model source',region:params.sourceRegion,completed:12,total:48,unit:'bytes',geometryStage:'modelAcquisition',geometryDependency:'model'});try{if(existsSync(path.join(directory,'hold-geometry-prepare')))await waitForRelease('release-geometry-prepare',controller.signal);const state=scenario();if(['ready','noDeclaredTriangles'].includes(state))geometryReady.add(requestKey);return reply(id,result(state));}catch{return fail(id,'CANCELED','Authored geometry consumer stopped after cleanup.');}finally{active.delete(id);}}
      if(method==='creatures.geometry.open'){if(existsSync(path.join(directory,'hold-geometry-open'))){writeFileSync(path.join(directory,'waiting-geometry-open'),id);await waitForRelease('release-geometry-open',shutdown.signal);}const ready=result(scenario());if(!geometryReady.has(requestKey)&&!existsSync(path.join(directory,'cached-geometry'))||ready.state!=='ready'||ready.manifest.artifactId!==params.geometryArtifactId)return fail(id,'CREATURE_GEOMETRY_NOT_PREPARED','The exact authored geometry is not prepared.');const shard=path.join(dataDirectory,'objects-v1',artifact.manifest.sha256.slice(0,2));mkdirSync(shard,{recursive:true});publishObject(path.join(shard,artifact.manifest.sha256.slice(2)),artifact.bytes);const leaseId=randomBytes(16).toString('hex');geometryLeases.add(leaseId);return reply(id,{leaseId,manifest:ready.manifest});}
    }
    if(method==='creatures.page'){if(existsSync(path.join(directory,'hold-creatures-page')))await waitForRelease('release-creatures-page',shutdown.signal);if(existsSync(path.join(directory,'creatures-page-error')))return fail(id,'LOCAL_CREATURES_READ_FAILED','Authored local client-record page failed.');return reply(id,creaturesPage(value,params));}
    if(method==='creatures.inspect'){if(existsSync(path.join(directory,`hold-creature-inspect-${params.creatureId}`)))await waitForRelease(`release-creature-inspect-${params.creatureId}`,shutdown.signal);return reply(id,creatureInspect(value,params));}
  }
  if(itemsEnabled&&method.startsWith('items.')){
    if(iconsEnabled&&method==='items.icon.close'){iconLeases.delete(params.leaseId);return reply(id,{closed:true});}
    const context={productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,researchLocale:params.researchLocale},key=`${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
    if(!opened.has(key))return fail(id,'WORKSPACE_NOT_OPEN','Open this exact authored workspace first.');
    function admit(){const value=itemsSnapshot(context);itemsAdmissions.set(key,value);for(const source of[value.itemSource,value.sparseSource])tableData.set(`${key}|${source.tableId}|enUS`,source);return value;}
    if(method==='items.data'){const value=itemsAdmissions.get(key)??(existsSync(path.join(directory,'cached-items'))?admit():null);return reply(id,{snapshot:value,dependencies:['50238ec2','919be54e'].map(tableId=>({tableId,state:value?'ready':'notPrepared',artifactId:value?(tableId==='50238ec2'?value.itemSource:value.sparseSource).artifactId:null}))});}
    if(method==='items.prepare'){const controller=new AbortController();active.set(id,controller);writeFileSync(path.join(directory,'waiting-items'),id);send({type:'progress',requestId:id,sequence:1,stage:'Preparing ItemSparse prerequisite',region:params.sourceRegion,completed:25,total:100,unit:'bytes'});try{await waitForRelease('release-items',controller.signal);if(existsSync(path.join(directory,'items-offline')))return fail(id,'NETWORK_UNAVAILABLE','Authored Items prerequisite is unavailable offline. Existing definitions and retained content remain usable.');return reply(id,admit());}catch{if(existsSync(path.join(directory,'hold-items-cancel'))&&!shutdown.signal.aborted){try{await waitForRelease('finish-items-cancel',shutdown.signal);}catch{}}return fail(id,'CANCELED','Authored Items consumer stopped after cleanup.');}finally{active.delete(id);}}
    if(effectsEnabled&&method.startsWith('items.effects.')){
      const value=itemsAdmissions.get(key);if(!value||value.artifactId!==params.itemsArtifactId)return fail(id,'ITEMS_ARTIFACT_STALE','The exact authored Items parent is unavailable.');
      const p={...context,itemsArtifactId:params.itemsArtifactId,itemId:params.itemId},f=effectsScenario(p,effectsMode(),params);for(const source of[f.page.bridgeSource,f.details.source.snapshot,f.names.sources[1].snapshot]){tableData.set(`${key}|${source.tableId}|enUS`,source);effectsTargets.set(`${key}|${source.tableId}`,f);}
      const phase=method.includes('.names')?'names':method.includes('.details')?'inputs':'links',prepare=method.endsWith('.prepare'),ready=effectsAdmissions.has(`${key}|${phase}`)||existsSync(path.join(directory,`cached-effects-${phase}`));
      if(method==='items.effects.page'){if(params.after&&existsSync(path.join(directory,'hold-effects-next-page'))){writeFileSync(path.join(directory,'waiting-effects-next-page'),'waiting');await waitForRelease('release-effects-next-page',shutdown.signal);}if(existsSync(path.join(directory,'effects-page-error')))return fail(id,'LOCAL_EFFECTS_PAGE_FAILED','Authored exact occurrence page failed.');return reply(id,f.page);}
      const result=phase==='links'?f.readiness:phase==='inputs'?{...f.details,outcomes:params.edges.map(k=>f.details.outcomes.find(o=>o.key.indexOrdinal===k.indexOrdinal))}:{...f.names,outcomes:params.edges.map(k=>f.names.outcomes.find(o=>o.key.indexOrdinal===k.indexOrdinal))};
      if(!prepare){if(existsSync(path.join(directory,`hold-effects-${phase}-read`)))await waitForRelease(`release-effects-${phase}-read`,shutdown.signal);const missing=effectsNotPrepared(f,phase);if(phase!=='links')missing.outcomes=params.edges.map(k=>missing.outcomes.find(o=>o.key.indexOrdinal===k.indexOrdinal));return reply(id,ready?result:missing);}
      const controller=new AbortController();active.set(id,controller);writeFileSync(path.join(directory,`waiting-effects-${phase}`),id);send({type:'progress',requestId:id,sequence:1,stage:`Preparing authored ${phase} source`,tableId:phase==='links'?'00cb674f':phase==='inputs'?'4002a5b1':'46c66698',region:params.sourceRegion,completed:25,total:100,unit:'bytes'});
      try{await waitForRelease(`release-effects-${phase}`,controller.signal);if(existsSync(path.join(directory,`effects-${phase}-error.json`)))return send({id,error:JSON.parse(readFileSync(path.join(directory,`effects-${phase}-error.json`),'utf8'))});if(existsSync(path.join(directory,`effects-${phase}-offline`)))return fail(id,'NETWORK_UNAVAILABLE',`Authored ${phase} source is unavailable offline.`);effectsAdmissions.add(`${key}|${phase}`);return reply(id,result);}catch{if(existsSync(path.join(directory,'hold-effects-cancel'))&&!shutdown.signal.aborted){try{await waitForRelease('finish-effects-cancel',shutdown.signal);}catch{}}return fail(id,'CANCELED','Authored effects consumer stopped after cleanup.');}finally{active.delete(id);}
    }
    const value=itemsAdmissions.get(key);if(!value||value.artifactId!==params.artifactId)return fail(id,'ITEMS_ARTIFACT_STALE','This exact authored Items artifact is no longer current.');
    if(labelsEnabled&&method==='items.classifications'){const controller=new AbortController();active.set(id,controller);writeFileSync(path.join(directory,'waiting-labels'),id);send({type:'progress',requestId:id,sequence:1,stage:'Checking optional classification targets',region:params.sourceRegion,completed:25,total:100,unit:'records'});try{if(!existsSync(path.join(directory,'labels-auto')))await waitForRelease('release-labels',controller.signal);if(existsSync(path.join(directory,'labels-offline')))return fail(id,'NETWORK_UNAVAILABLE','Authored optional classification source is unavailable offline. Original Item values remain usable.');const scenario=labelScenario(),batch=classificationBatch(value,params,scenario);labelAdmissions.set(key,{scenario});for(const source of batch.sources)if(source.snapshot)tableData.set(`${key}|${source.snapshot.tableId}|enUS`,source.snapshot);return reply(id,batch);}catch{if(existsSync(path.join(directory,'hold-labels-cancel'))&&!shutdown.signal.aborted){try{await waitForRelease('finish-labels-cancel',shutdown.signal);}catch{}}return fail(id,'CANCELED','Authored source-label consumer stopped after cleanup.');}finally{active.delete(id);}}
    if(iconsEnabled&&method==='items.icons'){const controller=new AbortController();active.set(id,controller);writeFileSync(path.join(directory,'waiting-icons'),id);send({type:'progress',requestId:id,sequence:1,stage:'Preparing optional icon sources',region:params.sourceRegion,completed:25,total:100,unit:'bytes'});try{if(!existsSync(path.join(directory,'icons-auto')))await waitForRelease('release-icons',controller.signal);if(existsSync(path.join(directory,'icons-error.json')))return send({id,error:JSON.parse(readFileSync(path.join(directory,'icons-error.json'),'utf8'))});if(existsSync(path.join(directory,'icons-offline')))return fail(id,'NETWORK_UNAVAILABLE','Authored optional icon source is unavailable offline. Item text remains usable.');return reply(id,{context,artifactId:value.artifactId,outcomes:params.itemIds.map(iconOutcome)});}catch{if(existsSync(path.join(directory,'hold-icons-cancel'))&&!shutdown.signal.aborted){try{await waitForRelease('finish-icons-cancel',shutdown.signal);}catch{}}return fail(id,'CANCELED','Authored optional icon consumer stopped after cleanup.');}finally{active.delete(id);}}
    if(iconsEnabled&&method==='items.icon.open'){const outcome=iconOutcome(params.itemId);if(outcome.status!=='ready'||outcome.image.artifactId!==params.imageArtifactId)return fail(id,'ICON_ARTIFACT_STALE','The exact authored image is not ready.');const controller=new AbortController();active.set(id,controller);try{if(existsSync(path.join(directory,'hold-icon-open')))await waitForRelease('release-icon-open',shutdown.signal);const root=path.join(dataDirectory,'objects-v1',pngSha.slice(0,2));mkdirSync(root,{recursive:true});publishObject(path.join(root,pngSha.slice(2)),existsSync(path.join(directory,'corrupt-icon-cas'))?Buffer.alloc(png.length):png);const leaseId=randomBytes(16).toString('hex');iconLeases.set(leaseId,params.itemId);return reply(id,{leaseId,...outcome.image,sha256:pngSha});}finally{active.delete(id);}}
    if(iconsEnabled&&method==='items.icon.close'){iconLeases.delete(params.leaseId);return reply(id,{closed:true});}
    if(method==='items.page'){if(existsSync(path.join(directory,'hold-items-page')))await waitForRelease('release-items-page',shutdown.signal);if(existsSync(path.join(directory,'items-page-error')))return fail(id,'LOCAL_ITEMS_READ_FAILED','Authored local Items page could not be read.');return reply(id,itemsPage(value,params,iconsEnabled));}
    if(method==='items.inspect'){if(existsSync(path.join(directory,'hold-item-inspect')))await waitForRelease('release-item-inspect',shutdown.signal);if(existsSync(path.join(directory,'items-detail-error')))return fail(id,'LOCAL_ITEM_READ_FAILED','Authored item detail could not be read.');return reply(id,{context,artifactId:value.artifactId,itemId:params.itemId,detail:itemDetail(value,params.itemId,iconsEnabled)});}
  }
  if(method.startsWith('retention.')&&retentionEnabled){
    if(existsSync(path.join(directory,'retention-read-error'))&&method!=='retention.set')return fail(id,'LOCAL_STATUS_UNAVAILABLE','Authored local status read is unavailable.');
    if(method==='retention.list')return reply(id,{jobs:[...retentionJobs.values()].map(s=>retentionSnapshot(s.target)).filter(s=>s.jobId&&(s.mode==='downloadAll'||s.state==='stopping'))});
    const {action,expectedRevision,...target}=params;
    const s=retentionSnapshot(target),key=JSON.stringify(target);
    if(method==='retention.read')return reply(id,s);
    if(method==='retention.set'){
      if(existsSync(path.join(directory,'retention-stale')))return fail(id,'RETENTION_STALE','Authored policy was changed by another consumer.');
      if(expectedRevision!==s.revision)return fail(id,'RETENTION_STALE','Read the committed revision.');
      if(s.state==='stopping'&&s.problem?.code==='RETENTION_STATE_RETRY'&&action===(s.mode==='onDemand'?'onDemand':s.paused?'pause':'resume')){s.problem=null;retentionJobs.set(key,s);if(existsSync(path.join(directory,'retention-state.json')))unlinkSync(path.join(directory,'retention-state.json'));return reply(id,s);}
      if(existsSync(path.join(directory,'retention-state.json')))unlinkSync(path.join(directory,'retention-state.json'));
      if(existsSync(path.join(directory,'finish-retention-stop')))unlinkSync(path.join(directory,'finish-retention-stop'));
      Object.assign(s,{revision:String(BigInt(s.revision)+1n),jobId:s.jobId??'authored-retention-job',mode:action==='onDemand'?'onDemand':'downloadAll',paused:action==='pause',state:action==='pause'||action==='onDemand'?'stopping':'queued',pendingObjects:0});retentionJobs.set(key,s);return reply(id,s);
    }
  }
  // Authored settings: kept in the control directory; a settings-write-fail file makes the next write fail.
  if (method === 'settings.read') return reply(id, settingsSnapshot());
  if (method === 'settings.set') {
    if (existsSync(path.join(directory, 'settings-write-fail'))) return fail(id, 'SETTINGS_WRITE_FAILED', 'The engine couldn’t write the settings file. Nothing else changed.', true);
    writeFileSync(path.join(directory, 'settings.json'), JSON.stringify({ value: params.value, changedAt: new Date().toISOString() }));
    return reply(id, settingsSnapshot());
  }
  if (method === 'settings.storage.measure') { const now = new Date().toISOString(); return reply(id, { bytes: '51749982208', files: '1204', skipped: '0', startedAt: now, measuredAt: now }); }
  if (method === 'products.list') return reply(id, { products: fixtureProducts });
  if (method === 'discovery.readAll') return reply(id, catalogFor(params.productId ?? null, params.filter ?? 'all', params.limit ?? 50));
  if (method === 'discovery.read') return reply(id, pageFor(params.productId, params.filter, params.limit));
  if (method === 'request.cancel') {
    const controller = active.get(params.requestId); controller?.abort();
    return reply(id, { requested: !!controller });
  }
  if (method === 'shutdown') {
    shutdown.abort();
    for (const controller of active.values()) controller.abort();
    reply(id, { stopping: true }); process.stdin.destroy(); return;
  }
  if (method === 'tables.follow') {
    // Authored follow over the generic rows: the target rows whose declared column equals the value (contracts/table-follow-v1.md).
    const contextKey=`${params.productId}|${params.buildIdentity}|${params.sourceRegion}`, source=tableData.get(`${contextKey}|${params.tableId}|${params.researchLocale}`), target=tableData.get(`${contextKey}|${params.targetTableId}|${params.researchLocale}`);
    if (!followEnabled) return fail(id,'TABLE_FOLLOW_UNAVAILABLE','Following references is unavailable.');
    if (!opened.has(contextKey)) return fail(id,'WORKSPACE_NOT_OPEN','Open the exact authored workspace first.');
    if (!source||source.artifactId!==params.artifactId) return fail(id,'TABLE_ARTIFACT_STALE','This exact admitted artifact is unavailable.');
    const column=source.columns[params.columnIndex]; if (!column?.reference) return fail(id,'TABLE_FOLLOW_NOT_DECLARED','This column declares no reference.');
    if (tableEntries.find(entry=>entry.tableId===params.targetTableId)?.name!==column.reference.table) return fail(id,'TABLE_FOLLOW_TARGET_MISMATCH','Not the declared table.');
    if (!target) return fail(id,'TABLE_NOT_PREPARED','Load the target table first.');
    if (target.artifactId!==params.targetArtifactId) return fail(id,'TABLE_ARTIFACT_STALE','The target rows changed.');
    const controller=new AbortController();active.set(id,controller);
    try{if(existsSync(path.join(directory,'hold-follow')))await waitForRelease('release-follow',controller.signal);}catch{return fail(id,'CANCELED','Authored follow stopped.');}finally{active.delete(id);}
    const index=target.columns.findIndex(c=>c.name===column.reference.column),isId=target.columns[index]?.isId===true,range=isId?{minimum:'0',maximum:'4294967295'}:column.reference.column==='ClassID'?{minimum:'-128',maximum:'127'}:null;
    const base={productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,researchLocale:'enUS',policyRevision:'table-follow-v1',tableId:params.tableId,artifactId:params.artifactId,columnIndex:params.columnIndex,column:column.name,value:params.value,reference:column.reference,targetTableId:params.targetTableId,targetArtifactId:params.targetArtifactId,targetColumnIndex:index<0?null:index,targetIsId:isId,targetRange:index<0?null:range,scope:target.coverage.scope,after:params.after??'0',problem:null};
    if (index<0) return reply(id,{...base,state:'unsupported',page:null,problem:{code:'TABLE_FOLLOW_LAYOUT',message:'No exact integer column to match.'}});
    if (range&&(BigInt(params.value)<BigInt(range.minimum)||BigInt(params.value)>BigInt(range.maximum))) return reply(id,{...base,state:'keyOutOfRange',page:null});
    const all=genericRows(target).filter(row=>isId?row.id===params.value:row.values[index]===params.value),after=Number(params.after??0),rows=all.slice(after,after+(params.limit??50)),end=after+rows.length;
    return reply(id,{...base,state:'ready',page:{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,tableId:params.targetTableId,researchLocale:'enUS',artifactId:params.targetArtifactId,rows,totalCount:String(all.length),nextCursor:end<all.length?String(end):null}});
  }
  if (method === 'tables.referenceText') {
    // Authored Item.ClassID -> ItemClass.ClassName_lang over the generic rows; every other reference is number only.
    const contextKey=`${params.productId}|${params.buildIdentity}|${params.sourceRegion}`, snapshot=tableData.get(`${contextKey}|${params.tableId}|${params.researchLocale}`);
    if (!opened.has(contextKey)) return fail(id,'WORKSPACE_NOT_OPEN','Open the exact authored workspace first.');
    if (!snapshot||snapshot.artifactId!==params.artifactId) return fail(id,'TABLE_ARTIFACT_STALE','This exact admitted artifact is unavailable.');
    if (existsSync(path.join(directory,'reference-text-error'))) return fail(id,'LOCAL_READ_FAILED','Authored reference text read failed.');
    const all=genericRows(snapshot),filtered=params.recordId===undefined?all:all.filter(row=>row.id===params.recordId),after=Number(params.after??0),rows=filtered.slice(after,after+(params.limit??50));
    const target=tableData.get(`${contextKey}|b977271e|enUS`),targetRows=target?genericRows(target):[],classColumn=target?.columns.findIndex(c=>c.name==='ClassID'),textColumn=target?.columns.findIndex(c=>c.name==='ClassName_lang');
    const columns=[],values=[];
    snapshot.columns.forEach((column,columnIndex)=>{
      if(!column.reference)return;
      if(column.reference.table!=='ItemClass'||column.reference.column!=='ClassID'){const bundled=tableEntries.some(entry=>entry.name===column.reference.table&&entry.definitionState==='bundled');columns.push({columnIndex,name:column.name,reference:column.reference,state:bundled?'noTextRule':'noDefinition',textSource:null,problem:null});return;}
      columns.push({columnIndex,name:column.name,reference:column.reference,state:target?'ready':'notPrepared',textSource:{tableId:'b977271e',table:'ItemClass',textColumn:'ClassName_lang',matchColumn:'ClassID',artifactId:target?.artifactId??null,scope:target?.coverage.scope??null,...(existsSync(path.join(directory,'reftext-large'))?{measuredEncodedBytes:'7988612',loadPolicy:'onRequest'}:{measuredEncodedBytes:'453',loadPolicy:'automatic'})},problem:null});
      if(!target)return;
      for(const value of new Set(rows.map(row=>row.values[columnIndex]))){
        const number=Number(value);
        if(number<-128||number>127){values.push({columnIndex,value,state:'keyOutOfRange',text:null,matchCount:'0',row:null});continue;}
        const matches=targetRows.filter(row=>row.values[classColumn]===value);
        if(matches.length===0)values.push({columnIndex,value,state:'notFound',text:null,matchCount:'0',row:null});
        else if(matches.length>1)values.push({columnIndex,value,state:'multiple',text:null,matchCount:String(matches.length),row:null});
        else{const match=matches[0],text=match.values[textColumn];values.push({columnIndex,value,state:text===''?'empty':'resolved',text,matchCount:'1',row:{id:match.id,isCopy:false,copySourceId:null}});}
      }
    });
    return reply(id,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,tableId:params.tableId,researchLocale:params.researchLocale,artifactId:params.artifactId,after:params.after??'0',recordId:params.recordId??null,policyRevision:'table-reference-text-v1',rowIds:rows.map(row=>row.id),columns,values});
  }
  if (['tables.prepare','tables.data','tables.rows'].includes(method)) {
    const contextKey=`${params.productId}|${params.buildIdentity}|${params.sourceRegion}`, key=`${contextKey}|${params.tableId}|${params.researchLocale}`;
    if (!opened.has(contextKey)) return fail(id,'WORKSPACE_NOT_OPEN','Open the exact authored workspace first.');
    if (method === 'tables.data') return reply(id,{snapshot:tableData.get(key)??null});
    if (method === 'tables.prepare') {
      const controller=new AbortController();active.set(id,controller);
      send({type:'progress',requestId:id,sequence:1,stage:'Locating selected table content',region:params.sourceRegion,completed:existsSync(path.join(directory,'known-table-progress'))?25:null,total:existsSync(path.join(directory,'known-table-progress'))?100:null,unit:existsSync(path.join(directory,'known-table-progress'))?'bytes':null});
      writeFileSync(path.join(directory,'waiting-table'),id);
      try {
        if (existsSync(path.join(directory,`hold-table-${params.tableId}`))) await waitForRelease(`release-table-${params.tableId}`,controller.signal);
        await waitForRelease('release-table',controller.signal);
        if (existsSync(path.join(directory,'table-offline'))) return fail(id,'NETWORK_UNAVAILABLE','Authored table dependency is unavailable offline; retained data and definitions remain usable.');
        if (existsSync(path.join(directory,'table-load-error'))) return fail(id,'BLTE_ENCRYPTED','Authored required metadata is encrypted; no readable table admission was published.');
        if (!['b977271e','50238ec2'].includes(params.tableId)) return fail(id,'TABLE_UNSUPPORTED','This authored fixture does not admit this table.');
        const value=JSON.parse(readFileSync(path.join(root,`tests/desktop/fixtures/${params.tableId==='50238ec2'?'table-data-partial':'table-data'}.json`),'utf8'));
        Object.assign(value,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion});
        if (existsSync(path.join(directory,'table-partial'))) {value.coverage={scope:'unencryptedSections',declaredRecords:'70',declaredCopyRecords:'2',physicalRows:'60',copyRows:'0',excludedPhysicalRecords:'10',excludedCopyRecords:'2',excludedDeclaredRecords:'12',excludedKeyedSections:'1'};}
        if(existsSync(path.join(directory,'table-text-issue')))value.textIssueCount='1';
        if(existsSync(path.join(directory,'table-layout-only')))value.schemaMatch='layoutOnly';
        // D079 (#53): the authored ClassID came from a 32-bit pallet and keeps its low 8 bits.
        if(existsSync(path.join(directory,'table-narrowing'))){const c=value.columns.find(c=>c.name==='ClassID');c.narrowing={storedBits:32,usedBits:8};}
        tableData.set(key,value);return reply(id,value);
      } catch { if(existsSync(path.join(directory,'hold-table-cancel'))&&!shutdown.signal.aborted){try{await waitForRelease('finish-table-cancel',shutdown.signal);}catch{}}return fail(id,'CANCELED','Authored table load stopped after cleanup.'); }
      finally {active.delete(id);}
    }
    const snapshot=tableData.get(key);
    if(!snapshot||snapshot.artifactId!==params.artifactId)return fail(id,'TABLE_DATA_STALE','This exact admitted artifact is unavailable.');
    if(existsSync(path.join(directory,'hold-table-rows')))await waitForRelease('release-table-rows',shutdown.signal);
    if(existsSync(path.join(directory,'table-rows-error')))return fail(id,'LOCAL_READ_FAILED','Authored local table row read failed.');
    if(effectsEnabled&&effectsTargets.has(`${contextKey}|${params.tableId}`)){const all=effectsRows(effectsTargets.get(`${contextKey}|${params.tableId}`),params.tableId),filtered=params.recordId===undefined?all:all.filter(r=>r.id===params.recordId),after=Number(params.after??0),rows=filtered.slice(after,after+(params.limit??50)),end=after+rows.length;return reply(id,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,researchLocale:params.researchLocale,tableId:params.tableId,artifactId:params.artifactId,rows,totalCount:String(filtered.length),nextCursor:end<filtered.length?String(end):null});}
    const target=displayTargets.get(`${contextKey}|${params.tableId}`);if(displayEnabled&&target?.row){const evidence=target.row,rows=params.recordId===undefined||params.recordId===evidence.source.rowId?[{id:evidence.source.rowId,isCopy:evidence.isCopy,copySourceId:evidence.copySourceId,values:target.snapshot.columns.map((_,index)=>index===evidence.edgeValue.columnIndex?evidence.edgeValue.value:'0')}]:[];return reply(id,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,researchLocale:params.researchLocale,tableId:params.tableId,artifactId:params.artifactId,rows,totalCount:String(rows.length),nextCursor:null});}
    const creatureAdmission=creatureAdmissions.get(contextKey);
    if(creaturesEnabled&&creatureAdmission&&params.tableId==='c9d6b6b3'){const all=Array.from({length:60},(_,i)=>creatureRow(creatureAdmission,String(i))),filtered=params.recordId===undefined?all:all.filter(r=>r.id===params.recordId),after=Number(params.after??0),rows=filtered.slice(after,after+(params.limit??50)),end=after+rows.length;return reply(id,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,researchLocale:params.researchLocale,tableId:params.tableId,artifactId:params.artifactId,rows,totalCount:String(filtered.length),nextCursor:end<filtered.length?String(end):null});}
    const itemAdmission=itemsAdmissions.get(contextKey);
    if(labelsEnabled&&itemAdmission&&labelAdmissions.has(contextKey)&&['b977271e','95d8638f'].includes(params.tableId)){const kind=params.tableId==='b977271e'?'class':'subclass',scenario=labelAdmissions.get(contextKey).scenario,all=Array.from({length:60},(_,i)=>classificationRows(itemAdmission,kind,String(i),scenario)),filtered=params.recordId===undefined?all:all.filter(r=>r.id===params.recordId),after=Number(params.after??0),rows=filtered.slice(after,after+(params.limit??50)),end=after+rows.length;return reply(id,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,tableId:params.tableId,researchLocale:params.researchLocale,artifactId:params.artifactId,rows,totalCount:String(filtered.length),nextCursor:end<filtered.length?String(end):null});}
    if(itemsEnabled&&itemAdmission&&['50238ec2','919be54e'].includes(params.tableId)){
      const all=Array.from({length:params.tableId==='50238ec2'?60:58},(_,i)=>itemRows(itemAdmission,String(i),iconsEnabled)[params.tableId==='50238ec2'?'itemRow':'sparseRow']);
      if(params.tableId==='919be54e')for(const id of['100','101']){const row=structuredClone(itemRows(itemAdmission,'0',iconsEnabled).sparseRow);row.id=id;row.values[0]=id;all.push(row);}
      const filtered=params.recordId===undefined?all:all.filter(row=>row.id===params.recordId),after=Number(params.after??0),rows=filtered.slice(after,after+(params.limit??50)),end=after+rows.length;
      return reply(id,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,tableId:params.tableId,researchLocale:params.researchLocale,artifactId:params.artifactId,rows,totalCount:String(filtered.length),nextCursor:end<filtered.length?String(end):null});
    }
    const all=genericRows(snapshot);
    const filtered=params.recordId===undefined?all:all.filter(row=>row.id===params.recordId),after=Number(params.after??0),rows=filtered.slice(after,after+(params.limit??50)),end=after+rows.length;
    return reply(id,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,tableId:params.tableId,researchLocale:params.researchLocale,artifactId:params.artifactId,rows,totalCount:String(filtered.length),nextCursor:end<filtered.length?String(end):null});
  }
  if (method === 'tables.catalog' || method === 'tables.definition') {
    const key = `${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
    const openedSnapshot = opened.get(key);
    if (!openedSnapshot) return fail(id, 'WORKSPACE_NOT_OPEN', 'Open this exact authored workspace first.');
    if (method === 'tables.catalog') {
      if (existsSync(path.join(directory, 'hold-table-catalog'))) await waitForRelease('release-table-catalog', shutdown.signal);
      if (existsSync(path.join(directory, 'table-catalog-error'))) return fail(id, 'PROVIDER_UNAVAILABLE', 'Authored local provider read failed.');
      const query = params.query ?? '', lower = query.toLowerCase();
      const filtered = tableEntries.filter(entry => !query || [entry.name, entry.tableId, entry.db2FileDataId, ...(tableDefinitions[entry.tableId]?.definition.columns.map(field => field.name) ?? [])].some(value => value?.toLowerCase().includes(lower)));
      const after = Number(params.after ?? 0), entries = filtered.slice(after, after + (params.limit ?? 50)), end = after + entries.length;
      return reply(id, { productId: params.productId, buildIdentity: params.buildIdentity, sourceRegion: params.sourceRegion, provider: tablesProvider, query, entries, totalCount: String(filtered.length), nextCursor: end < filtered.length ? String(end) : null });
    }
    if (existsSync(path.join(directory, `hold-definition-${params.tableId}`))) await waitForRelease(`release-definition-${params.tableId}`, shutdown.signal);
    if (existsSync(path.join(directory, 'table-definition-error'))) return fail(id, 'DEFINITION_READ_FAILED', 'Authored local definition read failed.');
    const entry = tableEntries.find(entry => entry.tableId === params.tableId);
    if (!entry) return fail(id, 'TABLE_NOT_IN_PROVIDER', 'This exact provider table is not listed.');
    if(effectsEnabled&&effectsTargets.has(`${key}|${params.tableId}`)){const f=effectsTargets.get(`${key}|${params.tableId}`),source=params.tableId==='00cb674f'?f.page.bridgeSource:params.tableId==='4002a5b1'?f.details.source.snapshot:f.names.sources[1].snapshot;return reply(id,await effectsDefinition(source,openedSnapshot.build.version));}
    if(displayEnabled&&displayTargets.has(`${key}|${params.tableId}`))return reply(id,await displayDefinition(displayTargets.get(`${key}|${params.tableId}`),openedSnapshot.build.version));
    if(creaturesEnabled&&creatureAdmissions.has(key)&&params.tableId==='c9d6b6b3')return reply(id,creatureDefinition(creatureAdmissions.get(key),openedSnapshot.build.version));
    const itemAdmission=itemsAdmissions.get(key);
    if(labelsEnabled&&itemAdmission&&labelAdmissions.has(key)&&['b977271e','95d8638f'].includes(params.tableId))return reply(id,classificationDefinition(itemAdmission,params.tableId==='b977271e'?'class':'subclass',openedSnapshot.build.version,labelAdmissions.get(key).scenario));
    if(itemsEnabled&&itemAdmission&&['50238ec2','919be54e'].includes(params.tableId))return reply(id,itemsDefinition(itemAdmission,params.tableId,openedSnapshot.build.version));
    const value = tableDefinitions[params.tableId] ? structuredClone(tableDefinitions[params.tableId]) : { provider: tablesProvider, entry, definition: null, problem: { code: 'DEFINITION_NOT_BUNDLED', message: 'This provider entry is listed, but its definition is not bundled.' } };
    if (value.definition && existsSync(path.join(directory, 'definition-no-candidate'))) {
      value.definition.candidates = []; value.problem = { code: 'SCHEMA_BUILD_NOT_LISTED', message: 'The exact authored build has no matching candidate; no nearby layout was substituted.' };
    }
    // D080: the authored build is not listed, so its candidates can only match a loaded file by layout.
    if (value.definition && existsSync(path.join(directory, 'table-layout-only'))) value.problem = { code: 'SCHEMA_BUILD_NOT_LISTED', message: 'The authored definition does not list this build. Data can open only if the file’s layout hash matches one of its candidate layouts.' };
    // A second authored layout on the same version line, so the candidate selector appears.
    if (value.definition?.candidates.length && existsSync(path.join(directory, 'table-layout-two'))) value.definition.candidates.push({ ...structuredClone(value.definition.candidates[0]), layoutHash: '0A0B0C0D' });
    Object.assign(value, { productId: params.productId, buildIdentity: params.buildIdentity, sourceRegion: params.sourceRegion, buildVersion: openedSnapshot.build.version });
    return reply(id, value);
  }
  if (method === 'download.summaries') {
    if (existsSync(path.join(directory, 'summaries-error'))) return fail(id, 'LOCAL_READ_FAILED', 'Authored stored download figures could not be read.');
    // Authored running figures (D065): one per build across its opened regions. A fresh open is at 0%; an authored
    // Download all job's present objects stand in for stored ones. stored-summary.json overrides the measured fields.
    const scripted = existsSync(path.join(directory, 'stored-summary.json')) ? JSON.parse(readFileSync(path.join(directory, 'stored-summary.json'), 'utf8')) : {};
    const revision = summaryRevision();
    return reply(id, { revision, summaries: params.builds.map(({ productId, buildIdentity }) => {
      const opens = [...opened.values()].filter(snapshot => snapshot.build.productId === productId && snapshot.build.id === buildIdentity);
      // A persisted relaunch keeps a prepared build without this session's opens: it was measured, nothing stored yet.
      if (!opens.length && saved.get(productId)?.builds.some(build => build.id === buildIdentity && build.isLocal)) {
        const workspace = JSON.parse(readFileSync(path.join(root, 'tests/desktop/fixtures/download-workspace.json'), 'utf8'));
        opens.push({ ...workspace, build: { productId, id: buildIdentity }, sourceReference: { ...workspace.sourceReference, region: 'us' } });
      }
      if (!opens.length) return { productId, buildIdentity, state: 'notOpened', download: null };
      const measured = opens.find(snapshot => snapshot.download);
      if (!measured) return { productId, buildIdentity, state: 'notMeasured', download: null };
      const d = measured.download, regions = [...new Set(opens.filter(snapshot => snapshot.download?.ledgerId === d.ledgerId).map(snapshot => snapshot.sourceReference.region))].sort();
      const jobs = retentionEnabled ? [...retentionJobs.values()].map(job => retentionSnapshot(job.target)).filter(job => job.target.productId === productId && job.target.buildIdentity === buildIdentity && job.jobId) : [];
      const best = jobs.reduce((top, job) => !top || BigInt(job.coverage.presentBytes) > BigInt(top.coverage.presentBytes) ? job : top, null);
      const check = jobs.find(job => job.coverage.presenceState === 'assessing') ?? jobs.find(job => job.coverage.presenceState === 'assessed') ?? jobs.find(job => job.coverage.presenceState === 'interrupted');
      return { productId, buildIdentity, state: 'measured', download: { ledgerId: d.ledgerId, scope: d.scope, objectCount: d.objectCount, totalEncodedBytes: d.totalEncodedBytes,
        storedObjects: best?.coverage.presentObjects ?? '0', storedBytes: best?.coverage.presentBytes ?? '0', countState: 'current', countProblem: null, updatedAt: d.assessedAt, unresolvableRootContent: '0', scopeState: 'manifestOnly', scopeProblem: null, changesPending: false,
        presence: check ? { state: check.coverage.presenceState, assessedAt: check.coverage.presenceAssessedAt, observedObjects: check.coverage.observedObjects, missingObjects: check.coverage.missingObjects, corruptObjects: check.coverage.corruptObjects }
          : { state: 'notAssessed', assessedAt: null, observedObjects: '0', missingObjects: '0', corruptObjects: '0' },
        downloadAll: jobs.some(job => job.mode === 'downloadAll' && !job.paused), running: jobs.some(job => ['preparing', 'running'].includes(job.state)), sourceRegions: regions, ...scripted } };
    }) });
  }
  if (method === 'download.changes') {
    // Answers once the revision differs from `after`, or after 25 s with the same one; cancellable like the engine's.
    if (!params || Object.keys(params).length !== 1 || !/^[0-9a-f]{16}-(0|[1-9][0-9]{0,18})$/.test(params.after ?? '')) return fail(id, 'INVALID_REQUEST', 'Name the summary revision of the last read.');
    const controller = new AbortController(); active.set(id, controller);
    try {
      const revision = await new Promise((resolve, reject) => {
        let timer, watcher;
        const finish = value => { clearTimeout(timer); watcher?.close(); controller.signal.removeEventListener('abort', abort); value instanceof Error ? reject(value) : resolve(value); };
        const check = () => { const current = summaryRevision(); if (current !== params.after) finish(current); };
        const abort = () => finish(new Error('canceled'));
        controller.signal.addEventListener('abort', abort, { once: true });
        timer = setTimeout(() => finish(summaryRevision()), 25000);
        watcher = watch(directory, check);
        check();
      });
      return reply(id, { revision });
    } catch { return fail(id, 'CANCELED', 'Operation canceled.'); }
    finally { active.delete(id); }
  }
  if (method === 'download.read') {
    const key = `${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
    const snapshot = opened.get(key);
    if (!snapshot?.download || snapshot.download.ledgerId !== params.ledgerId) return fail(id, 'DOWNLOAD_NOT_FOUND', 'This exact authored download ledger is unavailable.');
    if (existsSync(path.join(directory, 'hold-download-page'))) await waitForRelease('release-download-page', shutdown.signal);
    if (existsSync(path.join(directory, 'download-page-error'))) return fail(id, 'LOCAL_READ_FAILED', 'Authored saved download ledger read failed.');
    const count = Number(snapshot.download.objectCount);
    const all = Array.from({ length: count }, (_, index) => ({ id: String(index + 1), encodingKey: (index + 1).toString(16).padStart(32, '0'), encodedBytes: '100', verifiedAtAssessment: index < 10 }));
    const after = Number(params.after || 0), entries = all.slice(after, after + params.limit);
    return reply(id, { productId: params.productId, buildIdentity: params.buildIdentity, sourceRegion: params.sourceRegion, ledgerId: params.ledgerId,
      assessedAt: snapshot.download.assessedAt, entries, totalCount: String(count), nextCursor: after + entries.length < all.length ? entries.at(-1).id : null });
  }
  if (method === 'inventory.read') {
    const key = `${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
    const snapshot = opened.get(key);
    if (!snapshot?.inventory || params.generation !== snapshot.inventory.generation) return fail(id, 'INVENTORY_NOT_FOUND', 'This exact authored inventory is unavailable.');
    const seedPage = JSON.parse(readFileSync(path.join(root, 'contracts/examples/inventory-page.json'), 'utf8'));
    const all = Array.from({ length: 60 }, (_, index) => ({ ...seedPage.entries[0], id: String(index + 1),
      family: index < 3 ? 'root' : 'install', fileDataId: index < 3 ? String(12345 + index) : null,
      name: index < 3 ? null : `Authored member ${index + 1}` }));
    const after = Number(params.after || 0), entries = all.slice(after, after + params.limit);
    return reply(id, { productId: params.productId, buildIdentity: params.buildIdentity, sourceRegion: params.sourceRegion,
      generation: params.generation, entries, totalCount: '60', nextCursor: after + entries.length < all.length ? entries.at(-1).id : null });
  }
  // D071 authored file catalog: pending (writing) until file-catalog-ready exists; file-catalog-failed fails it; a user Stop
  // leaves it stopped until Resume. Every status carries the build's version and names nothing it waits for.
  const catalogStatus = (productId, buildIdentity, sourceRegion) => {
    const key = `${productId}|${buildIdentity}|${sourceRegion}`, snapshot = opened.get(key);
    const base = { productId, buildIdentity, sourceRegion, buildVersion: snapshot?.build?.version ?? null, stage: null, completed: null, total: null, attempts: 0, catalog: null, problem: null, waitingFor: null };
    if (!snapshot) return { ...base, state: 'notOpened' };
    if (catalogStops.has(`${productId}|${buildIdentity}`)) return { ...base, state: 'stopped' };
    // file-catalog-failed holds the failure: empty or "failed" for three failed attempts, "full" for a full disk, "unsupported" for an unreadable ROOT.
    const failed = path.join(directory, 'file-catalog-failed');
    if (existsSync(failed) && !catalogRetried.has(`${productId}|${buildIdentity}`)) {
      const kind = readFileSync(failed, 'utf8').trim();
      if (kind === 'full') return { ...base, state: 'failed', attempts: 1, problem: { code: 'STORAGE_FULL', message: 'There is not enough free disk space to save this build\'s file list.', retryable: false, detail: 'Less than 268435456 bytes free while writing.' } };
      if (kind === 'unsupported') return { ...base, state: 'unsupported', attempts: 1, problem: { code: 'ROOT_UNSUPPORTED', message: 'This build\'s file list could not be read. Trying again reads the same bytes, so it won\'t help.', retryable: false, detail: null } };
      return { ...base, state: 'failed', attempts: 3, problem: { code: 'STORAGE_UNAVAILABLE', message: 'The file list could not be prepared.', retryable: true, detail: 'IOException: Input/output error' } };
    }
    // file-catalog-retrying: the first attempt failed and the next one waits its turn.
    if (existsSync(path.join(directory, 'file-catalog-retrying')) && !fileCatalogReady(key)) return { ...base, state: 'pending', stage: 'queued', attempts: 1, problem: { code: 'CONTENT_SOURCE_MISSING', message: 'The file list could not be prepared.', retryable: true, detail: null } };
    const roots = new Set(fileMembers.filter(m => m.file.startsWith('root:')).map(m => m.file)), installs = new Set(fileMembers.filter(m => m.file.startsWith('install:')).map(m => m.file));
    const rootMembers = fileMembers.filter(m => m.file.startsWith('root:')).length;
    if (fileCatalogReady(key)) return { ...base, state: 'ready', catalog: { generation: FILE_CATALOG_GENERATION, preparedAt: '2026-10-21T12:00:00.0000000+00:00', rootFileCount: String(roots.size), installFileCount: String(installs.size),
      rootMembershipCount: String(rootMembers), installMembershipCount: String(fileMembers.length - rootMembers), uniqueContentCount: String(new Set(fileMembers.map(m => m.contentKey)).size), unresolvedMembershipCount: String(fileMembers.filter(m => m.encodingKey === null).length), storedMembershipCount: String(fileMembers.filter(m => m.stored).length) } };
    return { ...base, state: 'pending', stage: 'writing', completed: '1048576', total: '3262082' };
  };
  if (method === 'inventory.catalog') {
    if (existsSync(path.join(directory, 'file-catalog-status-error'))) return fail(id, 'ENGINE_UNAVAILABLE', 'Authored status read failure.', true);
    return reply(id, catalogStatus(params.productId, params.buildIdentity, params.sourceRegion));
  }
  if (method === 'inventory.catalogs') {
    const catalogs = [...opened.entries()].filter(([, snapshot]) => !snapshot.inventory).map(([key]) => { const [p, b, r] = key.split('|'); return catalogStatus(p, b, r); })
      .filter(status => ['pending', 'stopped', 'failed'].includes(status.state));
    return reply(id, { catalogs: [...new Map(catalogs.map(status => [`${status.productId}|${status.buildIdentity}`, status])).values()].slice(0, 32) });
  }
  if (method === 'inventory.catalog.set') {
    const build = `${params.productId}|${params.buildIdentity}`, key = [...opened.keys()].find(k => k.startsWith(build + '|'));
    if (!key) return fail(id, 'FILE_CATALOG_NOT_FOUND', 'This build has no file list preparation. Open the build first.');
    if (params.action === 'stop') catalogStops.add(build); else { catalogStops.delete(build); catalogRetried.add(build); }
    return reply(id, catalogStatus(params.productId, params.buildIdentity, key.split('|')[2]));
  }
  if (method === 'inventory.files' || method === 'inventory.file') {
    const key = `${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
    const snapshot = opened.get(key);
    const generation = snapshot?.inventory?.generation ?? (fileCatalogReady(key) ? FILE_CATALOG_GENERATION : null);
    if (!generation || params.generation !== generation) return fail(id, 'INVENTORY_NOT_FOUND', 'This exact authored inventory is unavailable.');
    if (existsSync(path.join(directory, 'files-error'))) return fail(id, 'LOCAL_READ_FAILED', 'Authored local Files read failed.', true);
    if (params.contentKey && params.contentKey.length < 6 && fileMembers.filter(m => m.contentKey.startsWith(params.contentKey)).length > 8) return fail(id, 'INVENTORY_FILTER_BROAD', `More than 8 authored memberships have a content key starting ${params.contentKey}. Type more of the key.`);
    if (method === 'inventory.files') return reply(id, filesPage(params));
    const members = fileMembers.filter(m => m.file === params.file);
    if (!members.length) return fail(id, 'INVENTORY_FILE_NOT_FOUND', 'This prepared inventory has no such file.');
    const after = BigInt(params.after || '0'), page = members.filter(m => BigInt(m.id) > after).slice(0, params.limit);
    const more = members.filter(m => BigInt(m.id) > after).length > page.length;
    return reply(id, { productId: params.productId, buildIdentity: params.buildIdentity, sourceRegion: params.sourceRegion, generation: params.generation, file: params.file,
      family: members[0].family, fileDataId: members[0].family === 'root' ? params.file.slice(5) : null, name: members[0].name, membershipCount: members.length,
      matchingCount: members.filter(m => fileMatches(m, params)).length, memberships: page.map(m => ({ ...wireMember(m), matches: fileMatches(m, params) })), nextCursor: more ? page.at(-1).id : null });
  }
  if (method === 'workspace.open') {
    const build = pageFor(params.productId).builds.find(build => build.id === params.buildIdentity);
    if (!build) return fail(id, 'BUILD_NOT_FOUND', 'This exact authored build is not saved.');
    const versions = build.sourceReceipts.find(receipt => receipt.region === params.sourceRegion && receipt.role === 'versions');
    const cdns = build.sourceReceipts.find(receipt => receipt.region === params.sourceRegion && receipt.role === 'cdns');
    if (!versions || !cdns) return fail(id, 'SOURCE_NOT_FOUND', 'This exact authored source is not saved.');
    const key = `${params.productId}|${params.buildIdentity}|${params.sourceRegion}`;
    send({ type: 'progress', requestId: id, stage: 'Checking saved configuration', sequence: 1, region: params.sourceRegion, completed: null, total: null, unit: null,
      step: { index: 1, total: null, description: 'Check the exact saved source and configuration documents.' } });
    if (opened.has(key)) return reply(id, structuredClone(opened.get(key)));
    const controller = new AbortController(); active.set(id, controller);
    let advanced = false;
    const advance = () => {
      if (advanced || controller.signal.aborted || !existsSync(path.join(directory, 'advance-open-step'))) return;
      advanced = true;
      send({ type: 'progress', requestId: id, stage: 'Building download ledger · 2 of 6 objects', sequence: 2, region: params.sourceRegion, completed: 2, total: 6, unit: 'objects',
        step: { index: 3, total: 5, description: 'Deduplicate full encoding keys and their published encoded sizes.' } });
    };
    const stepWatcher = watch(directory, advance);
    advance();
    writeFileSync(path.join(directory, 'waiting-open'), id);
    try {
      await waitForRelease('release-open', controller.signal);
      if (existsSync(path.join(directory, 'open-error'))) return fail(id, 'CONFIGURATION_INTEGRITY', 'Authored configuration verification failed.');
      // An inventory-workspace control file opens the legacy sealed research catalog, whose Files list is per FileDataID.
      const inventoryMode = existsSync(path.join(directory, 'inventory-workspace'));
      const snapshot = JSON.parse(readFileSync(path.join(root, inventoryMode ? 'contracts/examples/inventory-workspace.json' : 'tests/desktop/fixtures/download-workspace.json'), 'utf8'));
      if (inventoryMode) snapshot.inventory.membershipCount = String(fileMembers.length + 6);
      snapshot.build = build;
      snapshot.build.isNew = false; snapshot.build.isLocal = true;
      const acknowledged = saved.get(params.productId)?.builds.find(row => row.id === build.id);
      if (acknowledged) { acknowledged.isNew = false; acknowledged.isLocal = true; persist(); }
      snapshot.sourceReference = { generation: '1', region: params.sourceRegion, versionsSha256: versions.sha256, versionsRowOrdinal: versions.rowOrdinal, cdnsSha256: cdns.sha256, cdnsRowOrdinal: cdns.rowOrdinal };
      for (const receipt of snapshot.configurations) {
        receipt.key = receipt.role === 'buildConfig' ? build.buildConfig : build.cdnConfig;
        receipt.sourceUrl = `https://fixture.invalid/config/${receipt.key}`;
      }
      if (existsSync(path.join(directory, 'download-unavailable'))) {
        snapshot.download = null; snapshot.downloadProblem = { code: 'DOWNLOAD_UNDECLARED', message: 'This authored build does not declare a download manifest.' };
        snapshot.metadata = null; snapshot.metadataState = 'configurationsVerified'; snapshot.retentionState = 'unknown'; snapshot.build.isLocal = false;
      }
      opened.set(key, snapshot); reply(id, snapshot);
    } catch {
      if (existsSync(path.join(directory, 'hold-open-cancel')) && !shutdown.signal.aborted) {
        try { await waitForRelease('retire-open', shutdown.signal); } catch { /* Shutdown retires the authored wait too. */ }
      }
      fail(id, 'CANCELED', 'Configuration preparation canceled.');
    } finally { stepWatcher.close(); active.delete(id); }
    return;
  }
  if (method === 'discovery.refreshAll') {
    // Both authored products are checked at once, like the bounded engine; each waits for its own release file.
    const controller = new AbortController(); active.set(id, controller);
    let sequence = 1, completed = 0;
    const progress = () => send({ type: 'progress', requestId: id, stage: 'Checking official metadata', sequence: sequence++, region: null, completed, total: fixtureProducts.length, unit: 'products' });
    progress();
    try {
      await Promise.all(fixtureProducts.map(async product => {
        writeFileSync(path.join(directory, `waiting-${product.id}`), id);
        await waitForRelease(`release-${product.id}`, controller.signal);
        saved.set(product.id, checkedPage(product.id)); persist();
        completed++; progress();
      }));
      reply(id, catalogFor());
    } catch { fail(id, 'CANCELED', 'Discovery canceled. Products checked before stopping keep their new results.'); }
    finally { active.delete(id); }
    return;
  }
  if (method !== 'discovery.refresh') return fail(id, 'INVALID_REQUEST', 'Unsupported authored fixture operation.');
  const controller = new AbortController(); active.set(id, controller);
  send({ type: 'progress', requestId: id, stage: 'Reading authored metadata', sequence: 1, region: null, completed: null, total: null, unit: null });
  writeFileSync(path.join(directory, `waiting-${params.productId}`), id);
  try {
    await waitForRelease(`release-${params.productId}`, controller.signal);
    saved.set(params.productId, checkedPage(params.productId)); persist(); reply(id, pageFor(params.productId));
  } catch { fail(id, 'CANCELED', 'Discovery canceled.'); }
  finally { active.delete(id); }
}
const lines = createInterface({ input: process.stdin });
lines.on('line', line => { void dispatch(JSON.parse(line)).catch(() => { process.exitCode = 1; process.stdin.destroy(); }); });

// Authored Files catalog: 40 ROOT FileDataIDs (some with an enGB variant, some encrypted, some unresolved) and two install names.
const authoredKey = seed => { let h = '', x = (seed * 2654435761) >>> 0; for (let i = 0; i < 32; i++) { x = (x * 1103515245 + 12345) >>> 0; h += '0123456789abcdef'[x >>> 28]; } return h; };
const fileMembers = (() => {
  const out = []; let id = 1207000;
  for (let i = 0; i < 40; i++) {
    const fdid = 800001 + i, locales = i % 9 === 3 ? [0x2, 0x200] : [0x2];
    locales.forEach((locale, k) => {
      const first = i % 17 === 6 ? 0x08000000 : 0, unresolved = i === 30 && k === 0;
      out.push({ id: String(++id), file: `root:${fdid}`, family: 'root', name: null, blockOrdinal: 1 + (i >> 4) + k, entryOrdinal: i, installOrdinal: null,
        localeFlags: String(locale), contentFlags: String(first | 0x10000000), rawContentFlags: { first: String(first), second: '268435456', third: 0 },
        nameHash: k ? null : String(5000n + BigInt(i)), contentKey: authoredKey(900 + i * 3 + locale), encodingKey: unresolved ? null : authoredKey(1900 + i * 3 + locale),
        decodedBytes: unresolved ? null : String(4096 + i * 97), encodedBytes: unresolved ? null : String(4160 + i * 101 + k * 7), stored: k ? i % 5 !== 0 : i % 5 === 0 });
    });
  }
  for (const [i, name] of ['authored/install/launcher.bin', 'authored/install/launcher.bin', 'authored/install/readme.txt'].entries()) {
    const first = name === 'authored/install/launcher.bin' ? 1207100 : 1207100 + i;
    out.push({ id: String(1207100 + i), file: `install:${first}`, family: 'install', name, blockOrdinal: null, entryOrdinal: null, installOrdinal: i, localeFlags: null, contentFlags: null,
      rawContentFlags: null, nameHash: null, contentKey: authoredKey(3000 + i), encodingKey: authoredKey(3100 + i), decodedBytes: String(900 + i), encodedBytes: String(960 + i), stored: i === 2 });
  }
  return out;
})();
const wireMember = ({ file, name, ...member }) => member;
function fileMatches(m, p) {
  if (p.family && m.family !== p.family || p.storedOnly && !m.stored || p.contentKey && !m.contentKey.startsWith(p.contentKey)) return false;
  if (m.family === 'install') return !p.fileDataId && !p.localeFlags && !p.contentFlags;
  if (p.fileDataId && m.file !== `root:${p.fileDataId}`) return false;
  const locale = BigInt(m.localeFlags), flags = BigInt(m.contentFlags);
  return (!p.localeFlags || (locale & BigInt(p.localeFlags)) !== 0n) && (!p.contentFlags || (p.contentFlags === 'none' ? flags === 0n : (flags & BigInt(p.contentFlags)) === BigInt(p.contentFlags)));
}
function filesPage(p) {
  const files = [...new Set(fileMembers.map(m => m.file))].map(key => fileMembers.filter(m => m.file === key));
  const position = key => key.startsWith('install:') ? [1, BigInt(key.slice(8))] : [0, BigInt(key.slice(5))];
  const afterKey = p.after ? position(p.after) : null, later = key => { const k = position(key); return !afterKey || (k[0] !== afterKey[0] ? k[0] > afterKey[0] : k[1] > afterKey[1]); };
  const matching = files.filter(group => later(group[0].file) && group.some(m => fileMatches(m, p)));
  const page = matching.slice(0, p.limit), narrowed = p.fileDataId || p.contentKey || p.localeFlags || p.contentFlags || p.storedOnly;
  const total = files.filter(group => !p.family || group[0].family === p.family).length;
  return { productId: p.productId, buildIdentity: p.buildIdentity, sourceRegion: p.sourceRegion, generation: p.generation, scanLimited: false,
    totalCount: narrowed ? null : String(total), nextCursor: matching.length > page.length ? page.at(-1)[0].file : null,
    entries: page.map(group => { const sizes = group.filter(m => m.encodedBytes !== null).map(m => BigInt(m.encodedBytes)); const locale = group.reduce((a, m) => a | BigInt(m.localeFlags ?? 0), 0n);
      return { key: group[0].file, family: group[0].family, fileDataId: group[0].family === 'root' ? group[0].file.slice(5) : null, name: group[0].name, membershipCount: group.length,
        matchingCount: group.filter(m => fileMatches(m, p)).length, contentKeyCount: new Set(group.map(m => m.contentKey)).size,
        encodedBytesMin: sizes.length ? String(sizes.reduce((a, b) => a < b ? a : b)) : null, encodedBytesMax: sizes.length ? String(sizes.reduce((a, b) => a > b ? a : b)) : null,
        unresolvedCount: group.length - sizes.length, localeFlags: group[0].family === 'root' ? String(locale) : null, storedCount: group.filter(m => m.stored).length }; }) };
}
