import {creatureDisplay} from '../../../tests/desktop/fixtures/creature-display.mjs';
import {creatureSummary} from '../../../tests/desktop/fixtures/creatures.mjs';
import {creaturesSnapshot,creaturesPage,creatureInspect} from '../../../tests/desktop/fixtures/creatures.mjs';
import retentionFixture from '../../../contracts/examples/retention-snapshot.json';
import partialTableFixture from '../../../tests/desktop/fixtures/table-data-partial.json';
import tableDataFixture from '../../../tests/desktop/fixtures/table-data.json';
// @vitest-environment node
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { EngineClient, type EngineProcess } from '../src/main/engine-client';
import {itemsSnapshot as classificationItems} from '../../../tests/desktop/fixtures/items.mjs';
import {classificationBatch} from '../../../tests/desktop/fixtures/classifications.mjs';
import fixture from '../../../contracts/examples/discovery-page.json';
import workspace from '../../../contracts/examples/workspace-snapshot.json';
import inventoryWorkspace from '../../../contracts/examples/inventory-workspace.json';
import tableCatalogue from '../../../contracts/examples/table-catalog-page.json';
import tableDefinition from '../../../contracts/examples/table-definition.json';
import downloadWorkspace from '../../../tests/desktop/fixtures/download-workspace.json';
import inventoryPage from '../../../contracts/examples/inventory-page.json';
class Child extends EventEmitter implements EngineProcess {
  stdout = new PassThrough(); stderr = new PassThrough();
  requests: { id: string; method: string }[] = [];
  stdin = new Writable({ write: (chunk, _encoding, done) => { this.requests.push(JSON.parse(chunk.toString())); done(); } });
  kill = vi.fn(() => true);
  reply(id: string, result: unknown) { this.stdout.write(JSON.stringify({ protocolVersion: 1, id, result }) + '\n'); }
}
const children: Child[] = [];
function setup() { const child = new Child(); children.push(child); return { child, client: new EngineClient(child) }; }
afterEach(() => { for (const child of children.splice(0)) child.emit('exit', 0); vi.useRealTimers(); });
async function labelsSetup(subclass=true){const value=setup(),hello=value.client.hello();value.child.reply(value.child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['items-v1','item-classifications-v1',...(subclass?['item-subclass-labels-v1']:[])]});await hello;return value;}
it('gates explicit composite policy by capability while preserving old-shaped default-v1 labels',async()=>{const{child,client}=await labelsSetup(false),s=classificationItems(),p={...s.context,artifactId:s.artifactId,itemIds:['0']};await expect(client.request('items.classifications',{...p,policyRevision:'classification-label-index-v2'})).rejects.toMatchObject({code:'UNSUPPORTED'});expect(child.requests).toHaveLength(1);const pending=client.request('items.classifications',p,'legacy-labels');child.reply('legacy-labels',classificationBatch(s,p));await expect(pending).resolves.toMatchObject({policyRevision:'classification-label-index-v1'});});
it('copies ordered classification IDs before waiting and rejects mismatched exact source replies',async()=>{const{child,client}=await labelsSetup(),s=classificationItems(),p={...s.context,artifactId:s.artifactId,itemIds:['0','0'],includeSubclass:true,policyRevision:'classification-label-index-v2' as const},request=client.request('items.classifications',p,'labels');p.itemIds[0]='1';child.reply('labels',classificationBatch(s,{...p,itemIds:['0','0']}));await expect(request).resolves.toMatchObject({outcomes:[{itemId:'0'},{itemId:'0'}]});const bad=client.request('items.classifications',p,'bad-labels').catch(e=>e.code),result=classificationBatch(s,p);result.sources[0].snapshot!.association.fileDataId='999';child.reply('bad-labels',result);expect(await bad).toBe('PROTOCOL_ERROR');});
it('rejects a schema-shaped classification envelope above512KiB while other transport methods retain their own bound',async()=>{const{child,client}=await labelsSetup(),s=classificationItems(),p={...s.context,artifactId:s.artifactId,itemIds:Array(50).fill('0'),includeSubclass:true,policyRevision:'classification-label-index-v2' as const},reply=classificationBatch(s,p);for(const o of reply.outcomes){o.class.text='\\'.repeat(4096);o.subclass!.text='\\'.repeat(4096);}const frame=JSON.stringify({protocolVersion:1,id:'large-labels',result:reply})+'\n';expect(Buffer.byteLength(frame)).toBeGreaterThan(512*1024);expect(Buffer.byteLength(frame)).toBeLessThan(1024*1024);const work=client.request('items.classifications',p,'large-labels').catch(e=>e.code);child.stdout.write(frame);expect(await work).toBe('PROTOCOL_ERROR');});
it('reserves four lease closes plus cancellation and shutdown at ordinary saturation, including close during shutdown',async()=>{const{child,client}=setup(),hello=client.hello();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['items-v1','item-icons-v1']});await hello;const normal=Array.from({length:26},(_,i)=>client.request('products.list',{},`normal${i}`).catch(e=>e.code));await expect(client.request('products.list',{})).rejects.toMatchObject({code:'BUSY'});const closes=Array.from({length:4},(_,i)=>client.request('items.icon.close',{leaseId:String(i).repeat(32)},`close${i}`));const cancel=client.request('request.cancel',{requestId:'normal0'},'cancel');const shutdown=client.shutdown();expect(child.requests).toHaveLength(33);child.reply('close0',{closed:true});await closes[0];const late=client.request('items.icon.close',{leaseId:'f'.repeat(32)},'late');child.reply('late',{closed:true});await expect(late).resolves.toEqual({closed:true});for(let i=1;i<4;i++)child.reply(`close${i}`,{closed:true});child.reply('cancel',{requested:true});await Promise.all([...closes,cancel]);child.emit('exit',0);await shutdown;await Promise.all(normal);});
it('gates Creature-only authority and rejects changed source mappings before exposing a ready core',async()=>{const{child,client}=setup(),s=creaturesSnapshot();await expect(client.request('creatures.prepare',s.context,'no-cap')).rejects.toMatchObject({code:'UNSUPPORTED'});expect(child.requests).toHaveLength(0);const hello=client.hello();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['creatures-v1']});await hello;const work=client.request('creatures.prepare',s.context,'creature-core');child.reply('creature-core',s);await expect(work).resolves.toMatchObject({featureRevision:'creatures-core-v1'});const bad=client.request('creatures.prepare',s.context,'wrong-core').catch(e=>e.code),wrong=structuredClone(s);wrong.fields.name.columnIndex=1;child.reply('wrong-core',wrong);expect(await bad).toBe('PROTOCOL_ERROR');});
it('pins Creature pages and inspect replies to the exact ordered query and original raw ID',async()=>{const{child,client}=setup(),s=creaturesSnapshot(),hello=client.hello();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['creatures-v1']});await hello;const p={...s.context,artifactId:s.artifactId,exactCreatureId:'3',limit:50},page=client.request('creatures.page',p,'creature-page');child.reply('creature-page',creaturesPage(s,p));await expect(page).resolves.toMatchObject({exactCreatureId:'3'});const q={...s.context,artifactId:s.artifactId,creatureId:'3'},work=client.request('creatures.inspect',q,'creature-inspect').catch(e=>e.code),value=creatureInspect(s,q);value.creatureId='4';child.reply('creature-inspect',value);expect(await work).toBe('PROTOCOL_ERROR');});
it('bounds whole Creature failure frames including escaped request ID at512KiB',async()=>{const{child,client}=setup(),hello=client.hello(),s=creaturesSnapshot();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['creatures-v1']});await hello;const id='\\'.repeat(262000),work=client.request('creatures.data',s.context,id).catch(e=>e.code);const frame=JSON.stringify({protocolVersion:1,id,error:{code:'CREATURES_RESULT_LIMIT',message:'x'.repeat(1024),retryable:true}})+'\n';expect(Buffer.byteLength(frame)).toBeGreaterThan(512*1024);child.stdout.write(frame);expect(await work).toBe('PROTOCOL_ERROR');});
describe('bounded local engine transport', () => {
  it('reassembles split UTF-8 frames and correlates concurrent responses', async () => {
    const { child, client } = setup();
    const a = client.request('discovery.read', { productId: 'wow' }, 'a');
    const b = client.request('products.list', {}, 'b');
    const bytes = Buffer.from(JSON.stringify({ protocolVersion: 1, id: 'b', result: { products: [{ id: 'wow', label: '世界', description: 'Reviewed label' }] } }) + '\n');
    const offset = bytes.indexOf(Buffer.from('世')) + 1;
    child.stdout.write(bytes.subarray(0, offset)); child.stdout.write(bytes.subarray(offset));
    child.reply('a', fixture);
    await expect(b).resolves.toMatchObject({ products: [{ label: '世界' }] });
    await expect(a).resolves.toEqual(fixture);
  });
  it('rejects incompatible versions and all active consumers without replay', async () => {
    const { child, client } = setup();
    const a = expect(client.request('hello', {})).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    const b = expect(client.request('products.list', {})).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.stdout.write('{"protocolVersion":2,"id":"x","result":{}}\n');
    await Promise.all([a, b]); expect(child.requests).toHaveLength(2); expect(child.kill).toHaveBeenCalled();
  });
  it.each(['not json\n', '{"protocolVersion":1}\n', Buffer.from([0xff, 0x0a]), Buffer.alloc(1024 * 1024 + 1, 65)])('retires consumers on malformed, invalid UTF-8 or oversized input %#', async data => {
    const { child, client } = setup();
    const result = expect(client.request('hello', {})).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.stdout.write(data); await result;
  });
  it('rejects valid envelopes with invalid domain projections', async () => {
    const { child, client } = setup();
    const result = expect(client.request('discovery.read', {}, 'read')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.reply('read', { ...fixture, builds: [{ ...fixture.builds[0], isLocal: 'yes' }] }); await result;
  });
  it('keeps cancellation acknowledgement separate from target retirement', async () => {
    const { child, client } = setup(); let retired = false;
    const refresh = client.request('discovery.refresh', { productId: 'wow' }, 'refresh').catch(error => { retired = true; return error.code; });
    const cancel = client.request('request.cancel', { requestId: 'refresh' }, 'cancel');
    child.reply('cancel', { requested: true }); await cancel;
    expect(retired).toBe(false);
    child.stdout.write(JSON.stringify({ protocolVersion: 1, id: 'refresh', error: { code: 'CANCELED', message: 'Discovery canceled.', retryable: true } }) + '\n');
    await expect(refresh).resolves.toBe('CANCELED');
  });
  it('bounds pending requests while reserving cancellation admission', async () => {
    const { child, client } = setup();
    const active = Array.from({ length: 26 }, (_, i) => client.request('discovery.read', {}, `q${i}`).catch(error => error.code));
    await expect(client.request('products.list', {})).rejects.toMatchObject({ code: 'BUSY' });
    const cancel = client.request('request.cancel', { requestId: 'q0' }, 'cancel'); child.reply('cancel', { requested: true }); await cancel;
    child.emit('exit', 1); expect(await Promise.all(active)).toEqual(Array(26).fill('ENGINE_EXITED'));
  });
  it('coalesces progress and ignores decreasing sequence and post-terminal events', async () => {
    vi.useFakeTimers(); const { child, client } = setup(); const listener = vi.fn(); client.on('progress', listener);
    const target = client.request('discovery.refresh', {}, 'refresh');
    const progress = (sequence: number) => child.stdout.write(JSON.stringify({ protocolVersion: 1, type: 'progress', requestId: 'refresh', sequence, stage: `Stage ${sequence}`, region: null, completed: null, total: null, unit: null }) + '\n');
    progress(2); progress(1); await vi.advanceTimersByTimeAsync(100);
    expect(listener).toHaveBeenCalledTimes(1); expect(listener.mock.calls[0][0].stage).toBe('Stage 2');
    progress(3); child.reply('refresh', fixture); await target; await vi.advanceTimersByTimeAsync(100); progress(4); await vi.advanceTimersByTimeAsync(100);
    expect(listener).toHaveBeenCalledTimes(1);
  });
  it('awaits actual exit after shutdown acknowledgement and kills a stuck child', async () => {
    vi.useFakeTimers(); const { child, client } = setup(); let complete = false;
    const stopping = client.shutdown().then(() => { complete = true; });
    child.reply(child.requests[0].id, { stopping: true });
    await vi.advanceTimersByTimeAsync(4999); expect(complete).toBe(false); expect(child.kill).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1); await stopping; expect(child.kill).toHaveBeenCalledWith('SIGKILL');
  });
  it('finishes normal shutdown only when the real process exits', async () => {
    const { child, client } = setup(); let complete = false;
    const stopping = client.shutdown().then(() => { complete = true; }); child.reply(child.requests[0].id, { stopping: true });
    await Promise.resolve(); expect(complete).toBe(false); child.emit('exit', 0); await stopping; expect(complete).toBe(true); expect(child.kill).not.toHaveBeenCalled();
  });
  it('keeps the forced-exit fallback when a previously failed child ignores SIGTERM', async () => {
    vi.useFakeTimers(); const { child, client } = setup();
    const failed = expect(client.request('hello', {})).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.stdout.write('invalid frame\n'); await failed; expect(child.kill).toHaveBeenCalledTimes(1);
    let complete = false; const stopping = client.shutdown().then(() => { complete = true; });
    await vi.advanceTimersByTimeAsync(4999); expect(complete).toBe(false); expect(child.kill).not.toHaveBeenCalledWith('SIGKILL');
    await vi.advanceTimersByTimeAsync(1); await stopping; expect(child.kill).toHaveBeenCalledWith('SIGKILL'); expect(complete).toBe(true);
  });

  it('validates a configuration-ready snapshot against its exact requested identity and source receipts', async () => {
    const { child, client } = setup();
    const opening = client.request('workspace.open', { productId: workspace.build.productId, buildIdentity: workspace.build.id, sourceRegion: workspace.sourceReference.region }, 'opening');
    child.reply('opening', workspace); await expect(opening).resolves.toEqual(workspace);
  });
  it.each(['product', 'identity', 'region', 'sourceReceipt', 'configurationRole', 'configurationKey', 'readiness'])('rejects a success carrying mismatched workspace %s', async variant => {
    const { child, client } = setup(); const value = structuredClone(workspace);
    if (variant === 'product') value.build.productId = 'wowt';
    if (variant === 'identity') value.build.id = 'f'.repeat(64);
    if (variant === 'region') value.sourceReference.region = 'eu';
    if (variant === 'sourceReceipt') value.sourceReference.cdnsSha256 = 'f'.repeat(64);
    if (variant === 'configurationRole') value.configurations[1].role = 'buildConfig';
    if (variant === 'configurationKey') value.configurations[1].key = 'f'.repeat(32);
    if (variant === 'readiness') value.readiness = 'fullyDownloaded';
    const opening = expect(client.request('workspace.open', { productId: workspace.build.productId, buildIdentity: workspace.build.id, sourceRegion: workspace.sourceReference.region }, 'opening')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.reply('opening', value); await opening;
  });
  it.each([
    { productId: 'wow', buildIdentity: 'd'.repeat(64) },
    { productId: 'wow', buildIdentity: 'd'.repeat(64), sourceRegion: '' },
    { productId: 'wow', buildIdentity: 'd'.repeat(64), sourceRegion: 'us', url: 'https://arbitrary.invalid' },
    { productId: 'wow', buildIdentity: 'version-label', sourceRegion: 'us' }
  ])('refuses malformed or broad workspace request before sending %#', async params => {
    const { child, client } = setup(); await expect(client.request('workspace.open', params)).rejects.toMatchObject({ code: 'INVALID_REQUEST' }); expect(child.requests).toHaveLength(0);
  });

  it('accepts full inventory admission and exact generation-paged local query', async () => {
    const { child, client } = setup();
    const opening = client.request('workspace.open', { productId: inventoryWorkspace.build.productId, buildIdentity: inventoryWorkspace.build.id, sourceRegion: inventoryWorkspace.sourceReference.region }, 'inventory-open');
    child.reply('inventory-open', inventoryWorkspace); await expect(opening).resolves.toEqual(inventoryWorkspace);
    const page = client.request('inventory.read', { productId: inventoryPage.productId, buildIdentity: inventoryPage.buildIdentity, sourceRegion: inventoryPage.sourceRegion, generation: inventoryPage.generation, limit: 50 }, 'page');
    child.reply('page', inventoryPage); await expect(page).resolves.toEqual(inventoryPage);
  });
  it.each(['total', 'shared', 'rootCount', 'metadataRole', 'profileState'])('rejects an inconsistent inventory workspace %s', async variant => {
    const { child, client } = setup(); const value = structuredClone(inventoryWorkspace);
    if (variant === 'total') value.inventory.remainingEncodedBytes = '351';
    if (variant === 'shared') value.inventory.sharedVerifiedEncodedBytes = '101';
    if (variant === 'rootCount') value.inventory.rootMembershipCount = '7';
    if (variant === 'metadataRole') value.metadata[2].role = 'root';
    if (variant === 'profileState') value.inventoryState = 'notPrepared';
    const opening = expect(client.request('workspace.open', { productId: value.build.productId, buildIdentity: value.build.id, sourceRegion: value.sourceReference.region }, 'inventory-open')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.reply('inventory-open', value); await opening;
  });
  it('rejects a one-byte accounting discrepancy beyond JavaScript safe integer precision', async () => {
    const { child, client } = setup(); const value = structuredClone(inventoryWorkspace);
    Object.assign(value.inventory, { totalEncodedBytes: '9007199254740992', verifiedEncodedBytes: '9007199254740992', remainingEncodedBytes: '1' });
    const opening = expect(client.request('workspace.open', { productId: value.build.productId, buildIdentity: value.build.id, sourceRegion: value.sourceReference.region }, 'inventory-open')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.reply('inventory-open', value); await opening;
  });
  it.each(['product', 'identity', 'source', 'generation', 'limit', 'backwardCursor', 'duplicateRows', 'nextCursor', 'emptyNext', 'count', 'numericBytes'])('rejects invalid inventory page %s before publication', async variant => {
    const { child, client } = setup(); const value = structuredClone(inventoryPage);
    if (variant === 'product') value.productId = 'wowt';
    if (variant === 'identity') value.buildIdentity = 'f'.repeat(64);
    if (variant === 'source') value.sourceRegion = 'eu';
    if (variant === 'generation') value.generation = '2';
    if (variant === 'limit') value.entries.push({ ...value.entries[0], id: '2' });
    if (variant === 'backwardCursor') value.entries[0].id = '0';
    if (variant === 'duplicateRows') value.entries.push(value.entries[0]);
    if (variant === 'nextCursor') value.nextCursor = '99';
    if (variant === 'emptyNext') value.entries = [];
    if (variant === 'count') value.totalCount = '0';
    if (variant === 'numericBytes') (value.entries[0] as any).encodedBytes = 100;
    const page = expect(client.request('inventory.read', { productId: inventoryPage.productId, buildIdentity: inventoryPage.buildIdentity, sourceRegion: inventoryPage.sourceRegion, generation: inventoryPage.generation, limit: variant === 'duplicateRows' ? 50 : 1 }, 'page')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.reply('page', value); await page;
  });
  it.each([
    { generation: '0', limit: 50 }, { generation: '1', limit: 101 }, { generation: '1', limit: 0 },
    { generation: '1', limit: 50, after: '-1' }, { generation: '1', limit: 50, search: 'some-file' },
    { generation: 1, limit: 50 }, { generation: '1', limit: 50, after: '01' }
  ])('rejects an unbounded, nondecimal or broadened inventory request %#', async params => {
    const { child, client } = setup(); await expect(client.request('inventory.read', { productId: inventoryPage.productId, buildIdentity: inventoryPage.buildIdentity, sourceRegion: inventoryPage.sourceRegion, ...params })).rejects.toMatchObject({ code: 'INVALID_REQUEST' }); expect(child.requests).toHaveLength(0);
  });

  it.each([
    { index: 3, total: 2, description: 'Impossible relationship' },
    { index: 2, total: null, description: 'Only bootstrap may have an unknown plan' },
    { index: 1, total: 3, description: '   ' },
    { index: 9007199254740992, total: null, description: 'Unsafe integer' },
    { index: 1, total: 9007199254740992, description: 'Unsafe total' },
    { index: 0, total: 3, description: 'Zero index' }
  ])('refuses invalid preparation stage semantics %#', async step => {
    const { child, client } = setup(); const result = expect(client.request('workspace.open', { productId: inventoryWorkspace.build.productId, buildIdentity: inventoryWorkspace.build.id, sourceRegion: inventoryWorkspace.sourceReference.region }, 'step-open')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.stdout.write(JSON.stringify({ protocolVersion: 1, type: 'progress', requestId: 'step-open', sequence: 1, stage: 'Checking metadata', region: null, completed: null, total: null, unit: null, step }) + '\n'); await result;
  });
  it('preserves valid unknown and known stage counts through coalesced progress', async () => {
    vi.useFakeTimers(); const { child, client } = setup(); const listener = vi.fn(); client.on('progress', listener);
    const result = client.request('workspace.open', { productId: inventoryWorkspace.build.productId, buildIdentity: inventoryWorkspace.build.id, sourceRegion: inventoryWorkspace.sourceReference.region }, 'step-open');
    const report = (sequence: number, index: number, total: number | null) => child.stdout.write(JSON.stringify({ protocolVersion: 1, type: 'progress', requestId: 'step-open', sequence, stage: 'Checking metadata', region: null, completed: null, total: null, unit: null, step: { index, total, description: `Description ${index}` } }) + '\n');
    report(1, 1, null); await vi.advanceTimersByTimeAsync(100); expect(listener.mock.calls[0][0].step).toEqual({ index: 1, total: null, description: 'Description 1' });
    report(3, 3, 10); report(2, 2, 10); await vi.advanceTimersByTimeAsync(100); expect(listener.mock.calls[1][0].step).toEqual({ index: 3, total: 10, description: 'Description 3' });
    child.reply('step-open', inventoryWorkspace); await result;
  });

  it('accepts independent manifest sizing and exact bounded download-object pages', async () => {
    const { child, client } = setup(); const value = structuredClone(downloadWorkspace);
    const opening = client.request('workspace.open', { productId: value.build.productId, buildIdentity: value.build.id, sourceRegion: value.sourceReference.region }, 'download-open');
    child.reply('download-open', value); await expect(opening).resolves.toEqual(value);
    const params = { productId: value.build.productId, buildIdentity: value.build.id, sourceRegion: value.sourceReference.region, ledgerId: value.download.ledgerId, limit: 50 };
    const read = client.request('download.read', params, 'download-page');
    const page = { ...params, assessedAt: value.download.assessedAt, totalCount: '60', entries: [{ id: '1', encodingKey: '1'.repeat(32), encodedBytes: '100', verifiedAtAssessment: true }], nextCursor: '1' }; delete (page as any).limit;
    child.reply('download-page', page); await expect(read).resolves.toEqual(page);
  });
  it.each(['total', 'bothBranches', 'metadataRole', 'source'])('rejects inconsistent download admission %s', async variant => {
    const { child, client } = setup(); const value = structuredClone(downloadWorkspace);
    if (variant === 'total') value.download.remainingEncodedBytes = '5001';
    if (variant === 'bothBranches') (value as any).downloadProblem = { code: 'UNAVAILABLE', message: 'Unavailable' };
    if (variant === 'metadataRole') value.metadata[0].role = 'root';
    if (variant === 'source') value.sourceReference.region = 'eu';
    const result = expect(client.request('workspace.open', { productId: downloadWorkspace.build.productId, buildIdentity: downloadWorkspace.build.id, sourceRegion: 'us' }, 'download-open')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' });
    child.reply('download-open', value); await result;
  });
  it.each(['ledger', 'region', 'gap', 'duplicateEKey', 'cursor', 'numericBytes', 'limit', 'earlyEnd'])('rejects malformed or mismatched download page %s', async variant => {
    const { child, client } = setup(); const summary = downloadWorkspace.download;
    const params = { productId: 'wow', buildIdentity: downloadWorkspace.build.id, sourceRegion: 'us', ledgerId: summary.ledgerId, limit: 1 };
    const value = { productId: 'wow', buildIdentity: params.buildIdentity, sourceRegion: 'us', ledgerId: summary.ledgerId, assessedAt: summary.assessedAt, totalCount: '60', entries: [{ id: '1', encodingKey: '1'.repeat(32), encodedBytes: '100', verifiedAtAssessment: true }], nextCursor: '1' as string | null };
    if (variant === 'ledger') value.ledgerId = 'b'.repeat(64);
    if (variant === 'region') value.sourceRegion = 'eu';
    if (variant === 'gap') value.entries[0].id = '2';
    if (variant === 'duplicateEKey') { params.limit = 2; value.entries.push({ ...value.entries[0], id: '2' }); value.nextCursor = '2'; }
    if (variant === 'cursor') value.nextCursor = '2';
    if (variant === 'numericBytes') (value.entries[0] as any).encodedBytes = 100;
    if (variant === 'limit') value.entries.push({ ...value.entries[0], id: '2', encodingKey: '2'.repeat(32) });
    if (variant === 'earlyEnd') value.nextCursor = null;
    const result = expect(client.request('download.read', params, 'download-page')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' }); child.reply('download-page', value); await result;
  });
  it('refuses an unbounded download query before it crosses the process boundary', async () => {
    const { child, client } = setup(); await expect(client.request('download.read', { productId: 'wow', buildIdentity: downloadWorkspace.build.id, sourceRegion: 'us', ledgerId: downloadWorkspace.download.ledgerId, limit: 101 })).rejects.toMatchObject({ code: 'INVALID_REQUEST' }); expect(child.requests).toHaveLength(0);
  });

  it('correlates concurrent provider catalogue and exact definition replies', async () => {
    const { child, client } = setup();
    const context = { productId: tableCatalogue.productId, buildIdentity: tableCatalogue.buildIdentity, sourceRegion: tableCatalogue.sourceRegion };
    const catalogue = client.request('tables.catalog', { ...context, query: 'Item', limit: 50 }, 'catalogue');
    const definition = client.request('tables.definition', { ...context, tableId: tableDefinition.entry.tableId }, 'definition');
    child.reply('definition', tableDefinition); child.reply('catalogue', tableCatalogue);
    await expect(catalogue).resolves.toEqual(tableCatalogue); await expect(definition).resolves.toEqual(tableDefinition);
  });
  it.each(['table', 'query', 'tooMany', 'ready', 'cursor'])('refuses invalid table catalogue output %s', async variant => {
    const { child, client } = setup(); const value = { ...structuredClone(tableCatalogue), nextCursor: null as string | null };
    if (variant === 'table') value.buildIdentity = 'f'.repeat(64);
    if (variant === 'query') value.query = 'Material';
    if (variant === 'tooMany') value.entries = Array.from({ length: 101 }, (_, index) => ({ ...value.entries[0], tableId: index.toString(16).padStart(8, '0') }));
    if (variant === 'ready') value.entries[0].rowsState = 'ready';
    if (variant === 'cursor') value.nextCursor = '2';
    const result = expect(client.request('tables.catalog', { productId: 'wow', buildIdentity: tableCatalogue.buildIdentity, sourceRegion: 'us', query: 'Item', limit: 50 }, 'catalogue')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' }); child.reply('catalogue', value); await result;
  });
  it.each(['identity', 'duplicateField', 'inconsistentReference', 'oversizeFields'])('refuses contradictory definition output %s', async variant => {
    const { child, client } = setup(); const value = structuredClone(tableDefinition);
    if (variant === 'identity') value.entry.tableId = 'deadbeef';
    if (variant === 'duplicateField') value.definition.columns.push(value.definition.columns[0]);
    if (variant === 'inconsistentReference') (value.definition.candidates[0].fields[0] as any).reference = { table: 'MadeUp', column: 'ID' };
    if (variant === 'oversizeFields') value.definition.candidates[0].fields = Array.from({ length: 257 }, (_, index) => ({ ...value.definition.columns[0], name: `Field${index}` }));
    const result = expect(client.request('tables.definition', { productId: 'wow', buildIdentity: tableDefinition.buildIdentity, sourceRegion: 'us', tableId: tableDefinition.entry.tableId }, 'definition')).rejects.toMatchObject({ code: 'PROTOCOL_ERROR' }); child.reply('definition', value); await result;
  });
  it.each([{ method: 'tables.catalog', extra: { limit: 101 } }, { method: 'tables.catalog', extra: { query: 'x'.repeat(81) } }, { method: 'tables.definition', extra: { tableId: 'Item' } }])('rejects broad table metadata request %# before sending', async ({ method, extra }) => {
    const { child, client } = setup(); await expect(client.request(method as 'tables.catalog' | 'tables.definition', { productId: 'wow', buildIdentity: tableCatalogue.buildIdentity, sourceRegion: 'us', ...extra })).rejects.toMatchObject({ code: 'INVALID_REQUEST' }); expect(child.requests).toHaveLength(0);
  });

});

describe('table data transport admission',()=>{
 const params={productId:'wow',buildIdentity:tableDataFixture.buildIdentity,sourceRegion:'us',tableId:'b977271e',researchLocale:'enUS' as const};
 it('validates local readiness and exact preparation correlation without changing wire strings',async()=>{const{child,client}=setup();const local=client.request('tables.data',params,'local');child.reply('local',{snapshot:null});await expect(local).resolves.toEqual({snapshot:null});const prepare=client.request('tables.prepare',params,'prepare');child.reply('prepare',tableDataFixture);await expect(prepare).resolves.toEqual(tableDataFixture);});
 it('rejects unsupported locales and oversized row limits before writing requests',async()=>{const{child,client}=setup();await expect(client.request('tables.prepare',{...params,researchLocale:'frFR'})).rejects.toMatchObject({code:'INVALID_REQUEST'});await expect(client.request('tables.rows',{...params,artifactId:tableDataFixture.artifactId,limit:51})).rejects.toMatchObject({code:'INVALID_REQUEST'});expect(child.requests).toHaveLength(0);});
 it('rejects a schema-valid cross-artifact row page',async()=>{const{child,client}=setup();const query={...params,artifactId:tableDataFixture.artifactId,limit:50};const pending=expect(client.request('tables.rows',query,'rows')).rejects.toMatchObject({code:'PROTOCOL_ERROR'});child.reply('rows',{...params,artifactId:'0'.repeat(64),rows:[],totalCount:'0',nextCursor:null});await pending;});
 it('retains preparation until its terminal result after cancellation acknowledgement',async()=>{const{child,client}=setup();let complete=false;const target=client.request('tables.prepare',params,'prepare').catch(e=>{complete=true;return e.code;});const cancel=client.request('request.cancel',{requestId:'prepare'},'cancel');child.reply('cancel',{requested:true});await cancel;expect(complete).toBe(false);child.stdout.write(JSON.stringify({protocolVersion:1,id:'prepare',error:{code:'CANCELED',message:'Stopped after cleanup.',retryable:true}})+'\n');await expect(target).resolves.toBe('CANCELED');});
});

describe('partial projection transport proof',()=>{
 const params={productId:partialTableFixture.productId,buildIdentity:partialTableFixture.buildIdentity,sourceRegion:partialTableFixture.sourceRegion,tableId:partialTableFixture.tableId,researchLocale:'enUS' as const};
 it('admits partial source proof without weakening the old full receipt path',async()=>{const {child,client}=setup();const pending=client.request('tables.prepare',params,'partial');child.reply('partial',partialTableFixture);await expect(pending).resolves.toEqual(partialTableFixture);});
 it('rejects a schema-valid proof gap before publishing a partial snapshot',async()=>{const {child,client}=setup();const value=structuredClone(partialTableFixture);value.source.projection.unavailableRanges[0].offset='1025';const pending=expect(client.request('tables.prepare',params,'bad-proof')).rejects.toMatchObject({code:'PROTOCOL_ERROR'});child.reply('bad-proof',value);await pending;});
});

it('refuses a schema-valid raw physical column that claims a record reference',async()=>{const {child,client}=setup(),value=structuredClone(partialTableFixture);const column=value.columns.find(c=>c.interpretation)!;column.reference={table:'ItemClass',column:'ID'};const params={productId:value.productId,buildIdentity:value.buildIdentity,sourceRegion:value.sourceRegion,tableId:value.tableId,researchLocale:'enUS'};const pending=expect(client.request('tables.prepare',params,'raw-reference')).rejects.toMatchObject({code:'PROTOCOL_ERROR'});child.reply('raw-reference',value);await pending;});


describe('typed unavailable text transport',()=>{
 const params={productId:tableDataFixture.productId,buildIdentity:tableDataFixture.buildIdentity,sourceRegion:tableDataFixture.sourceRegion,tableId:tableDataFixture.tableId,researchLocale:'enUS',artifactId:tableDataFixture.artifactId,limit:50};
 const page=()=>({...params,rows:[{id:'7',isCopy:false,copySourceId:null,values:['7','','0','1','0'],textIssues:[{columnIndex:1,code:'DB2_TEXT_ENCODING_INVALID',rawBytesHex:'c328'}]}],totalCount:'1',nextCursor:null});
 it('preserves exact malformed raw bytes and keeps other cell strings unchanged',async()=>{const{child,client}=setup(),value=page();delete (value as any).limit;const pending=client.request('tables.rows',params,'text');child.reply('text',value);await expect(pending).resolves.toEqual(value);});
 it.each(['validUTF8','NUL','uppercase','nonemptyValue','duplicate','badIndex'])('rejects forged or misbound text evidence before renderer delivery: %s',async variant=>{const{child,client}=setup(),value=page();delete (value as any).limit;const issue=value.rows[0].textIssues[0];if(variant==='validUTF8')issue.rawBytesHex='61';if(variant==='NUL')issue.rawBytesHex='ff00';if(variant==='uppercase')issue.rawBytesHex='FF';if(variant==='nonemptyValue')value.rows[0].values[1]='replacement';if(variant==='duplicate')value.rows[0].textIssues.push({...issue});if(variant==='badIndex')issue.columnIndex=5;const pending=expect(client.request('tables.rows',params,'bad-text')).rejects.toMatchObject({code:'PROTOCOL_ERROR'});child.reply('bad-text',value);await pending;});
});

async function retentionSetup() {const value=setup();const hello=value.client.request('hello',{},'cap');value.child.reply('cap',{protocolVersion:1,engineVersion:'test',capabilities:['retention-v1']});await hello;return value;}
it('gates retention on the real hello capability, without sending unsupported commands',async()=>{const {child,client}=setup();await expect(client.request('retention.read',retentionFixture.target)).rejects.toMatchObject({code:'UNSUPPORTED'});expect(child.requests).toHaveLength(0);});
it('validates immediate committed retention acknowledgements with exact large revisions',async()=>{const {child,client}=await retentionSetup();const request={...retentionFixture.target,action:'pause',expectedRevision:retentionFixture.revision};const result=client.request('retention.set',request,'policy');child.reply('policy',{...retentionFixture,revision:'9007199254740994',paused:true,state:'stopping'});await expect(result).resolves.toMatchObject({revision:'9007199254740994',state:'stopping'});expect(client.capabilities).toEqual(['retention-v1']);});
it.each(['target','revision','counts','duplicate'])('rejects a malformed retention %s response',async kind=>{const {child,client}=await retentionSetup();const s=structuredClone(retentionFixture);let result:Promise<unknown>;if(kind==='duplicate'){result=expect(client.request('retention.list',{},'r')).rejects.toMatchObject({code:'PROTOCOL_ERROR'});child.reply('r',{jobs:[s,s]});}else{if(kind==='target')s.target.sourceRegion='eu';if(kind==='revision')s.revision='9007199254740995';if(kind==='counts')s.coverage.observedObjects='0';result=expect(client.request('retention.set',{...retentionFixture.target,action:'downloadAll',expectedRevision:retentionFixture.revision},'r')).rejects.toMatchObject({code:'PROTOCOL_ERROR'});child.reply('r',s);}await result;});
async function itemsSetup(){const value=setup();const hello=value.client.request('hello',{},'items-cap');value.child.reply('items-cap',{protocolVersion:1,engineVersion:'test',capabilities:['items-v1']});await hello;return value;}
it('validates both admitted Items sources and keeps prepare success separate from local readiness',async()=>{const {child,client}=await itemsSetup();const value=(await import('../../../tests/desktop/fixtures/items-snapshot.json')).default;const ready=client.request('items.data',value.context,'items-local');child.reply('items-local',{snapshot:null,dependencies:[{tableId:'50238ec2',state:'notPrepared',artifactId:null},{tableId:'919be54e',state:'notPrepared',artifactId:null}]});await expect(ready).resolves.toMatchObject({snapshot:null});const prepared=client.request('items.prepare',value.context,'items-prepare');child.reply('items-prepare',value);await expect(prepared).resolves.toEqual(value);});
it('rejects an Items page from another applied query rather than silently reusing its cursor',async()=>{const {child,client}=await itemsSetup();const value=(await import('../../../contracts/examples/items-page.json')).default;const page=expect(client.request('items.page',{...value.context,artifactId:value.artifactId,query:'literal %_'},'items-page')).rejects.toMatchObject({code:'PROTOCOL_ERROR'});child.reply('items-page',value);await page;});
it('refuses malformed Items queries before writing, preserving literal Unicode and exact ID rules',async()=>{const {child,client}=await itemsSetup();const value=(await import('../../../contracts/examples/items-page.json')).default;const before=child.requests.length;for(const query of [{query:'\ud800'},{query:'a',exactItemId:'7'},{exactItemId:'4294967296'},{after:'../path'}])await expect(client.request('items.page',{...value.context,artifactId:value.artifactId,...query})).rejects.toMatchObject({code:'INVALID_REQUEST'});expect(child.requests).toHaveLength(before);});
it('reports actual process exit separately from failure/kill submission and shutdown deadlines',async()=>{vi.useFakeTimers();const{child,client}=setup();const work=client.request('products.list',{}).catch(x=>x.code);child.stdout.write('invalid\n');await work;expect(child.kill).toHaveBeenCalled();expect(client.hasExited).toBe(false);const waiting=client.waitForExit(1000);await vi.advanceTimersByTimeAsync(1000);expect(await waiting).toBe(false);child.emit('exit',0);expect(client.hasExited).toBe(true);expect(await client.waitForExit(1)).toBe(true);});
it('requires icon capability and correlates ordered optional outcomes and exact native lease descriptors',async()=>{const{child,client}=setup(),context={productId:'wow',buildIdentity:'d'.repeat(64),sourceRegion:'us',researchLocale:'enUS'},hello=client.hello();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['items-v1','item-icons-v1']});await hello;const p={...context,artifactId:'a'.repeat(64),itemIds:['7','7']},request=client.request('items.icons',p),id=child.requests.at(-1)!.id;child.reply(id,{context,artifactId:p.artifactId,outcomes:p.itemIds.map(itemId=>({itemId,fileDataId:'17',status:'ready',image:{artifactId:'b'.repeat(64),width:64,height:32,byteLength:'100'},problem:null}))});await expect(request).resolves.toMatchObject({outcomes:expect.any(Array)});const open=client.request('items.icon.open',{...context,artifactId:p.artifactId,itemId:'7',imageArtifactId:'b'.repeat(64)});child.reply(child.requests.at(-1)!.id,{leaseId:'c'.repeat(32),artifactId:'b'.repeat(64),sha256:'e'.repeat(64),width:64,height:32,byteLength:'100'});await expect(open).resolves.toHaveProperty('leaseId','c'.repeat(32));const close=client.request('items.icon.close',{leaseId:'c'.repeat(32)});child.reply(child.requests.at(-1)!.id,{closed:true});await expect(close).resolves.toEqual({closed:true});});
it('rejects a schema-valid icon outcome for another Item ID and never accepts it as the pending request',async()=>{const{child,client}=setup(),context={productId:'wow',buildIdentity:'d'.repeat(64),sourceRegion:'us',researchLocale:'enUS'},hello=client.hello();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['items-v1','item-icons-v1']});await hello;const pending=client.request('items.icons',{...context,artifactId:'a'.repeat(64),itemIds:['7']}).catch(x=>x.code);child.reply(child.requests.at(-1)!.id,{context,artifactId:'a'.repeat(64),outcomes:[{itemId:'8',fileDataId:'0',status:'noIcon',image:null,problem:null}]});expect(await pending).toBe('PROTOCOL_ERROR');});

it('gates exact selected-slot metadata independently and copies its ordinal before receiving a result',async()=>{const{child,client}=setup(),core=creaturesSnapshot(),hello=client.hello();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['creatures-v1','creature-display-metadata-v1']});await hello;const p={...core.context,artifactId:core.artifactId,creatureId:'0',slot:0};await expect(client.request('creatures.display.read',{...p,displayId:'100'})).rejects.toMatchObject({code:'INVALID_REQUEST'});const request=client.request('creatures.display.prepare',p,'metadata');p.slot=1;child.reply('metadata',creatureDisplay(core,creatureSummary(core,'0')!,0,{...downloadWorkspace.sourceReference,region:core.context.sourceRegion}));await expect(request).resolves.toMatchObject({state:'metadataResolved',slot:0});const wrong=client.request('creatures.display.read',p,'bad-slot').catch(e=>e.code);child.reply('bad-slot',creatureDisplay(core,creatureSummary(core,'0')!,0,{...downloadWorkspace.sourceReference,region:core.context.sourceRegion}));expect(await wrong).toBe('PROTOCOL_ERROR');});
it('requires real metadata capability and rejects full C2 failure envelopes over512KiB',async()=>{const{child,client}=setup(),core=creaturesSnapshot(),p={...core.context,artifactId:core.artifactId,creatureId:'0',slot:0},hello=client.hello();child.reply(child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['creatures-v1']});await hello;await expect(client.request('creatures.display.read',p)).rejects.toMatchObject({code:'UNSUPPORTED'});const other=setup(),ready=other.client.hello();other.child.reply(other.child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:['creatures-v1','creature-display-metadata-v1']});await ready;const id='\\'.repeat(262000),result=other.client.request('creatures.display.read',p,id).catch(e=>e.code),frame=JSON.stringify({protocolVersion:1,id,error:{code:'CREATURES_RESULT_LIMIT',message:'x'.repeat(1024),retryable:true}})+'\n';expect(Buffer.byteLength(frame)).toBeGreaterThan(512*1024);other.child.stdout.write(frame);expect(await result).toBe('PROTOCOL_ERROR');});

const effectCaps=['items-v1','item-effects-links-v1','item-effects-inputs-v1','item-effect-spell-names-v1'];
async function effectsSetup(caps=effectCaps){const value=setup(),hello=value.client.hello();value.child.reply(value.child.requests[0].id,{protocolVersion:1,engineVersion:'authored',capabilities:caps});await hello;return value;}
it('requires every effects capability and validates flat authority before writing IPC',async()=>{const fixture=(await import('../../../contracts/examples/item-effects-readiness.json')).default,p={...fixture.selection.context,itemsArtifactId:fixture.selection.itemsArtifactId,itemId:fixture.selection.itemId};for(const caps of [[],effectCaps.slice(0,2),effectCaps.filter(c=>c!=='item-effects-inputs-v1')]){const{client,child}=await effectsSetup(caps);await expect(client.request('items.effects.data',p)).rejects.toMatchObject({code:'UNSUPPORTED'});await expect(client.request('items.effects.data',{...p,itemId:'01'})).rejects.toMatchObject({code:'INVALID_REQUEST'});expect(child.requests).toHaveLength(1);}});
it.each(['items.effects.data','items.effects.prepare','items.effects.page','items.effects.details','items.effects.details.prepare','items.effects.names','items.effects.names.prepare'] as const)('correlates useful real-schema effects replies for %s',async method=>{const ready=(await import('../../../contracts/examples/item-effects-readiness.json')).default,page=(await import('../../../contracts/examples/item-effects-page.json')).default,details=(await import('../../../contracts/examples/item-effect-details.json')).default,names=(await import('../../../contracts/examples/item-effect-names.json')).default;const result=method.includes('names')?names:method.includes('details')?details:method.endsWith('page')?page:ready;const params={...ready.selection.context,itemsArtifactId:ready.selection.itemsArtifactId,itemId:ready.selection.itemId,...(method!=='items.effects.data'&&method!=='items.effects.prepare'?{bridgeIndexArtifactId:page.bridgeIndexArtifactId}:{}),...(method.includes('details')||method.includes('names')?{edges:details.outcomes.map(o=>({...o.key}))}:{}),...(method.includes('names')?{itemEffectArtifactId:names.itemEffectArtifactId}:{})};const{client,child}=await effectsSetup(),work=client.request(method,params,'effects');child.reply('effects',result);await expect(work).resolves.toMatchObject({selection:ready.selection});});
it('owns edge snapshots through async replies and refuses response order substitution',async()=>{const ready=(await import('../../../contracts/examples/item-effects-readiness.json')).default,value=structuredClone((await import('../../../contracts/examples/item-effect-details.json')).default),p={...ready.selection.context,itemsArtifactId:ready.selection.itemsArtifactId,itemId:ready.selection.itemId,bridgeIndexArtifactId:value.bridgeIndexArtifactId,edges:value.outcomes.map(o=>({...o.key}))};const{client,child}=await effectsSetup(),work=client.request('items.effects.details',p,'effects');p.edges[0].indexOrdinal='3';child.reply('effects',value);expect((await work as {outcomes:{key:{indexOrdinal:string}}[]}).outcomes[0].key.indexOrdinal).toBe('0');const bad=client.request('items.effects.details',{...p,edges:[{indexOrdinal:'0'},{indexOrdinal:'1'}]},'bad').catch(e=>e.code);value.outcomes.reverse();child.reply('bad',value);expect(await bad).toBe('PROTOCOL_ERROR');});
it.each(['counter','table','missingTable','localProgress','region'] as const)('refuses dishonest effects progress %s before renderer delivery',async kind=>{const ready=(await import('../../../contracts/examples/item-effects-readiness.json')).default,p={...ready.selection.context,itemsArtifactId:ready.selection.itemsArtifactId,itemId:ready.selection.itemId};const{client,child}=await effectsSetup(),method=kind==='localProgress'?'items.effects.data':'items.effects.prepare',work=client.request(method,p,'effects').catch(e=>e.code),listener=vi.fn();client.on('progress',listener);const value:Record<string,unknown>={protocolVersion:1,type:'progress',requestId:'effects',sequence:1,stage:'Rows',region:null,unit:'rows',completed:1,total:2,tableId:'00cb674f'};if(kind==='counter')value.total=9007199254740992;if(kind==='table')value.tableId='50238ec2';if(kind==='missingTable')delete value.tableId;if(kind==='region')value.region='eu';child.stdout.write(JSON.stringify(value)+'\n');expect(await work).toBe('PROTOCOL_ERROR');expect(listener).not.toHaveBeenCalled();});
it('bounds complete effects error envelopes with escaping and preserves independent cancel acknowledgement',async()=>{const ready=(await import('../../../contracts/examples/item-effects-readiness.json')).default,p={...ready.selection.context,itemsArtifactId:ready.selection.itemsArtifactId,itemId:ready.selection.itemId};const{client,child}=await effectsSetup(),id='\\'.repeat(262000),work=client.request('items.effects.data',p,id).catch(e=>e.code);child.stdout.write(JSON.stringify({protocolVersion:1,id,error:{code:'AUTHORED',message:'x'.repeat(1024),retryable:false}})+'\n');expect(await work).toBe('PROTOCOL_ERROR');const next=await effectsSetup(),pending=next.client.request('items.effects.prepare',p,'pending').catch(e=>e.code),cancel=next.client.request('request.cancel',{requestId:'pending'},'cancel-effects');next.child.reply('cancel-effects',{requested:true});await expect(cancel).resolves.toEqual({requested:true});expect(next.client.hasFailed).toBe(false);next.child.stdout.write(JSON.stringify({protocolVersion:1,id:'pending',error:{code:'CANCELED',message:'Canceled.',retryable:true}})+'\n');expect(await pending).toBe('CANCELED');});
