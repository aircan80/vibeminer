import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { existsSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import Ajv from 'ajv';

const root = fileURLToPath(new URL('../../', import.meta.url));
const schema = JSON.parse(await readFile(path.join(root, 'contracts/discovery.schema.json'), 'utf8'));
const ajv = new Ajv({ allErrors: true, strict: false });
ajv.addSchema(schema);
const validator = name => ajv.getSchema(`${schema.$id}#/definitions/${name}`);
const validate = (name, value) => {
  const check = validator(name);
  assert.ok(check(value), `${name}: ${JSON.stringify(check.errors)}`);
};

test('authored discovery fixtures conform to the canonical wire schema', async () => {
  for (const name of ['discovery-page', 'empty-page']) {
    validate('DiscoveryPage', JSON.parse(await readFile(path.join(root, `contracts/examples/${name}.json`), 'utf8')));
  }
  const page = JSON.parse(await readFile(path.join(root, 'contracts/examples/discovery-page.json'), 'utf8'));
  page.builds[0].buildId = 70448;
  assert.equal(validator('DiscoveryPage')(page), false, 'exact build IDs cannot silently become JavaScript numbers');
});

test('a cross-product page keeps exact builds, every product status and per-product refresh problems', async () => {
  const page = JSON.parse(await readFile(path.join(root, 'contracts/examples/discovery-catalog-page.json'), 'utf8'));
  validate('DiscoveryCatalogPage', page);
  assert.deepEqual([...new Set(page.builds.map(build => build.productId))], ['wow', 'wowt'], 'one page mixes products');
  validate('DiscoveryCatalogPage', { ...page, productId: 'wowt', builds: page.builds.filter(build => build.productId === 'wowt'), refreshProblems: [] });
  const invalid = change => { const value = structuredClone(page); change(value); assert.equal(validator('DiscoveryCatalogPage')(value), false); };
  invalid(value => { value.builds[0].buildId = 70448; });
  invalid(value => { value.filter = 'current'; });
  invalid(value => { value.productId = '../wow'; });
  invalid(value => { delete value.refreshProblems; });
  invalid(value => { delete value.products; });
  invalid(value => { value.products[0].connection = 'checked'; });
  invalid(value => { value.products[0].extra = true; });
  // Every finding says whether it is a problem or a regional note (D076).
  assert.deepEqual(page.products[0].errors.map(error => [error.region, error.severity]), [['sg', 'note'], ['xx', 'note']]);
  assert.equal(page.products[0].connection, 'online', 'a check with only regional notes is complete');
  invalid(value => { delete value.products[0].errors[0].severity; });
  invalid(value => { value.products[0].errors[0].severity = 'info'; });
  invalid(value => { value.refreshProblems[0].region = 'us'; });
  invalid(value => { value.builds = Array(101).fill(value.builds[0]); });
  const request = (method, params) => ({ protocolVersion: 1, id: 'all', method, params });
  validate('EngineRequest', request('discovery.readAll', {}));
  validate('EngineRequest', request('discovery.readAll', { productId: 'wow_classic', filter: 'advertised', limit: 100, cursor: 'opaque' }));
  validate('EngineRequest', request('discovery.refreshAll', {}));
  for (const [method, params] of [['discovery.readAll', { limit: 101 }], ['discovery.readAll', { limit: '50' }], ['discovery.readAll', { region: 'us' }],
    ['discovery.readAll', { filter: 'current' }], ['discovery.refreshAll', { productId: 'wow' }]])
    assert.equal(validator('EngineRequest')(request(method, params)), false, `${method} ${JSON.stringify(params)}`);
  const progress = { protocolVersion: 1, type: 'progress', requestId: 'all', sequence: 2, stage: 'Checking official metadata', region: null, completed: 1, total: 13, unit: 'products' };
  validate('EngineProgress', progress);
});

test('a build carries its CDN configuration history outside its identity fields', async () => {
  const page = JSON.parse(await readFile(path.join(root, 'contracts/examples/discovery-page.json'), 'utf8'));
  const build = page.builds[0], [current] = build.cdnConfigHistory;
  assert.equal(current.cdnConfig, build.cdnConfig, 'the newest history entry is the build projection CDN configuration');
  assert.equal('CDNConfig' in build.identityFields, false, 'CDNConfig is not a build identity field');
  validate('Build', { ...build, cdnConfigHistory: [current, { ...current, cdnConfig: 'c'.repeat(32), advertisedRegions: [] }] });
  const { cdnConfigHistory, ...withoutHistory } = build;
  assert.equal(validator('Build')(withoutHistory), false, 'every build projection states its CDN configuration history');
  assert.equal(validator('Build')({ ...build, cdnConfigHistory: [{ ...current, cdnConfig: 'B'.repeat(32) }] }), false, 'history keys are normalized lowercase hashes');
  assert.equal(validator('Build')({ ...build, cdnConfigHistory: [{ ...current, extra: true }] }), false, 'history entries are closed');
  // The projection carries 1 to 16 entries (DiscoveryCatalog.MaxProjectedCdnConfigs); older ones are only counted.
  const entries = count => Array.from({ length: count }, (_, i) => ({ ...current, cdnConfig: i.toString(16).padStart(32, '0') }));
  validate('Build', { ...build, cdnConfigHistory: entries(16), cdnConfigHistoryCount: 19 });
  assert.equal(validator('Build')({ ...build, cdnConfigHistory: [] }), false, 'a build has at least the CDN configuration it was advertised with');
  assert.equal(validator('Build')({ ...build, cdnConfigHistory: entries(17), cdnConfigHistoryCount: 19 }), false, 'the history projection is capped to the newest 16');
});

test('Home configuration admission cannot claim a prepared inventory or invent a scope', async () => {
  const snapshot = JSON.parse(await readFile(path.join(root, 'contracts/examples/workspace-snapshot.json'), 'utf8'));
  validate('WorkspaceSnapshot', snapshot);
  for (const [field, value] of [['readiness', 'fullyDownloaded'], ['metadataState', 'resolverVerified'], ['inventoryState', 'ready'], ['researchLocale', 'enUS']]) {
    assert.equal(validator('WorkspaceSnapshot')({ ...snapshot, [field]: value }), false, `${field} must remain honest in the configuration-only slice`);
  }
  assert.equal(validator('WorkspaceSnapshot')({ ...snapshot, configurations: [snapshot.configurations[0], snapshot.configurations[0]] }), false, 'both distinct configuration roles must be verified');
});

test('inventory-ready Home requires measured sealed metadata and bounded exact inventory pages', async () => {
  const snapshot = JSON.parse(await readFile(path.join(root, 'contracts/examples/inventory-workspace.json'), 'utf8'));
  const page = JSON.parse(await readFile(path.join(root, 'contracts/examples/inventory-page.json'), 'utf8'));
  validate('WorkspaceSnapshot', snapshot); validate('InventoryPage', page);
  for (const field of ['inventory', 'metadata']) assert.equal(validator('WorkspaceSnapshot')({ ...snapshot, [field]: null }), false);
  assert.equal(validator('WorkspaceSnapshot')({ ...snapshot, metadataState: 'configurationsVerified' }), false);
  assert.equal(validator('WorkspaceSnapshot')({ ...snapshot, metadata: snapshot.metadata.filter(m => m.role !== 'root') }), false);
  assert.equal(validator('InventoryPage')({ ...page, entries: Array(101).fill(page.entries[0]) }), false);
  assert.equal(validator('InventoryPage')({ ...page, entries: [{ ...page.entries[0], encodedBytes: 100 }] }), false);
});

test('Files pages and the file inspector stay bounded and keep unknown sizes and names unknown', async () => {
  const page = JSON.parse(await readFile(path.join(root, 'contracts/examples/inventory-files-page.json'), 'utf8'));
  const detail = JSON.parse(await readFile(path.join(root, 'contracts/examples/inventory-file-detail.json'), 'utf8'));
  validate('InventoryFilesPage', page); validate('InventoryFileDetail', detail);
  const scope = { productId: 'wow', buildIdentity: page.buildIdentity, sourceRegion: 'us', generation: '1', limit: 50 };
  validate('InventoryFilesParams', { ...scope, after: 'install:0', fileDataId: '0', localeFlags: '2', contentFlags: 'none', family: 'root', storedOnly: true });
  validate('InventoryFilesParams', { ...scope, contentKey: 'abcd', contentFlags: '134217728' });
  validate('InventoryFileParams', { ...scope, file: 'install:7', after: '1', contentKey: '11111111111111111111111111111111' });
  // The schema bounds digits; the 0..4294967295 range is checked by the desktop validators and the engine.
  for (const invalid of [{ fileDataId: '12345678901' }, { fileDataId: 7 }, { contentKey: 'abc' }, { contentKey: 'ABCD' }, { localeFlags: '0' }, { contentFlags: 'any' },
    { family: 'download' }, { storedOnly: 'yes' }, { after: 'root:-1' }, { after: '12' }, { platform: 'Windows' }, { limit: 101 }]) {
    assert.equal(validator('InventoryFilesParams')({ ...scope, ...invalid }), false, JSON.stringify(invalid));
  }
  assert.equal(validator('InventoryFileParams')({ ...scope, file: 'install:0' }), false, 'install:0 is a cursor, never a file');
  assert.equal(validator('InventoryFilesPage')({ ...page, entries: Array(101).fill(page.entries[0]) }), false);
  assert.equal(validator('InventoryFilesPage')({ ...page, scanLimited: undefined }), false);
  assert.equal(validator('InventoryFilesPage')({ ...page, entries: [{ ...page.entries[0], membershipCount: 4097 }] }), false, 'a file has at most 4,096 memberships');
  assert.equal(validator('InventoryFileDetail')({ ...detail, membershipCount: 4097 }), false);
  assert.equal(validator('InventoryFilesPage')({ ...page, entries: [{ ...page.entries[0], encodedBytesMin: 100 }] }), false);
  assert.equal(validator('InventoryFilesPage')({ ...page, entries: [{ ...page.entries[0], platform: 'Windows' }] }), false, 'no platform is inferred');
  assert.equal(validator('InventoryFileDetail')({ ...detail, memberships: Array(101).fill(detail.memberships[0]) }), false);
  assert.equal(validator('InventoryFileDetail')({ ...detail, memberships: [{ ...detail.memberships[1], encodedBytes: '0'.repeat(2) }] }), false);
  // Unresolved memberships keep EKey and sizes null instead of a zero or a guess.
  assert.equal(detail.memberships[1].encodingKey, null); assert.equal(detail.memberships[1].encodedBytes, null);
});

// Reads `export type RequestMethod = ...;` as a union of string literals. Comments are skipped,
// a leading `|` is allowed and either quote style works; anything else throws instead of guessing.
const requestMethodUnion = source => {
  const head = /export\s+type\s+RequestMethod\s*=/.exec(source);
  if (!head) throw new Error('no RequestMethod declaration');
  const members = [];
  let i = head.index + head[0].length, expect = 'first';
  for (;;) {
    if (i >= source.length) throw new Error('RequestMethod has no closing ;');
    const ch = source[i];
    if (/\s/.test(ch)) { i++; continue; }
    if (source.startsWith('//', i)) { const end = source.indexOf('\n', i); i = end < 0 ? source.length : end; continue; }
    if (source.startsWith('/*', i)) {
      const end = source.indexOf('*/', i + 2);
      if (end < 0) throw new Error('unterminated comment in RequestMethod');
      i = end + 2; continue;
    }
    if (ch === '|' && (expect === 'first' || expect === 'bar')) { expect = 'member'; i++; continue; }
    if (ch === ';' && expect === 'bar') return members;
    if ((ch === "'" || ch === '"') && expect !== 'bar') {
      const end = source.indexOf(ch, i + 1), literal = source.slice(i + 1, end);
      if (end < 0 || !/^[a-z][A-Za-z.]*$/.test(literal)) throw new Error(`unexpected RequestMethod literal at ${i}`);
      members.push(literal); i = end + 1; expect = 'bar'; continue;
    }
    throw new Error(`unexpected ${JSON.stringify(source.slice(i, i + 20))} in RequestMethod`);
  }
};

test('the RequestMethod reader handles one-line, leading-bar, commented and double-quoted unions', () => {
  assert.deepEqual(requestMethodUnion("export type RequestMethod = 'hello' | 'shutdown';"), ['hello', 'shutdown']);
  assert.deepEqual(requestMethodUnion("export type RequestMethod =\n  | 'hello'\n  | 'items.page';\n"), ['hello', 'items.page']);
  assert.deepEqual(requestMethodUnion([
    'export type RequestMethod =',
    "  // engine handshake; first frame",
    "  | 'hello' /* a; b */",
    '  | "items.icon.open" // opens; one view',
    "  /* closing; */ | 'shutdown';",
  ].join('\n')), ['hello', 'items.icon.open', 'shutdown']);
  for (const bad of ["export type RequestMethod = 'hello' | string;", "export type RequestMethod = 'hello' 'shutdown';",
    "export type RequestMethod = 'hello' | | 'shutdown';", "export type RequestMethod = 'hello' |;", "export type RequestMethod = 'hello'",
    "export type RequestMethod = 'hello' /* open;", "export type RequestMethod = 'hello\";", 'export type Other = 1;'])
    assert.throws(() => requestMethodUnion(bad), undefined, bad);
});

test('the schema request method enum lists exactly the bridge RequestMethod union', async () => {
  const bridge = await readFile(path.join(root, 'apps/desktop/src/shared/bridge.ts'), 'utf8');
  const bridgeMethods = requestMethodUnion(bridge);
  const schemaMethods = schema.definitions.EngineRequest.properties.method.enum;
  assert.equal(new Set(bridgeMethods).size, bridgeMethods.length, 'RequestMethod has no duplicates');
  assert.equal(new Set(schemaMethods).size, schemaMethods.length, 'the schema method enum has no duplicates');
  assert.deepEqual(bridgeMethods.filter(method => !schemaMethods.includes(method)), [], 'bridge methods missing from the schema enum');
  assert.deepEqual(schemaMethods.filter(method => !bridgeMethods.includes(method)), [], 'schema methods missing from the bridge RequestMethod');
});

test('inventory.files and inventory.file requests validate their bounded params', () => {
  const scope = { productId: 'wow', buildIdentity: 'a'.repeat(64), sourceRegion: 'us', generation: '1', limit: 50 };
  const request = (method, params) => ({ protocolVersion: 1, id: 'files', method, params });
  validate('EngineRequest', request('inventory.files', { ...scope, after: 'root:7', storedOnly: true }));
  validate('EngineRequest', request('inventory.file', { ...scope, file: 'install:7' }));
  for (const [method, params] of [['inventory.files', { ...scope, platform: 'Windows' }], ['inventory.files', { ...scope, limit: 101 }],
    ['inventory.file', scope], ['inventory.file', { ...scope, file: 'install:0' }]])
    assert.equal(validator('EngineRequest')(request(method, params)), false, `${method} ${JSON.stringify(params)}`);
});

test('File catalog status keeps one shape per state and bounded requests (D071)', async () => {
  const states = JSON.parse(await readFile(path.join(root, 'contracts/examples/file-catalog-status.json'), 'utf8'));
  assert.deepEqual(states.map(status => `${status.state}/${status.stage ?? ''}`), ['pending/writing', 'pending/queued', 'pending/paused', 'stopped/', 'ready/', 'failed/', 'unsupported/', 'notOpened/']);
  for (const status of states) validate('FileCatalogStatus', status);
  const [writing, retrying, , stopped, ready, failed, unsupported, notOpened] = states;
  for (const invalid of [
    { ...writing, stage: 'downloading' }, { ...writing, completed: 12 }, { ...writing, catalog: ready.catalog },
    { ...ready, catalog: null }, { ...ready, stage: 'writing' }, { ...ready, problem: retrying.problem },
    { ...ready, catalog: { ...ready.catalog, unresolvedMembershipCount: '-1' } }, { ...ready, catalog: { ...ready.catalog, platform: 'Windows' } },
    { ...failed, problem: null }, { ...unsupported, problem: null }, { ...failed, attempts: 4 }, { ...notOpened, stage: 'queued' },
    { ...stopped, waitingFor: retrying.waitingFor }, { ...retrying, problem: { ...retrying.problem, detail: 'x'.repeat(401) } },
    { ...retrying, problem: { ...retrying.problem, message: 'x'.repeat(401) } }, { ...writing, state: 'preparing' }, { ...writing, waitingFor: { productId: 'wow' } }]) {
    assert.equal(validator('FileCatalogStatus')(invalid), false, JSON.stringify(invalid).slice(0, 120));
  }
  validate('FileCatalogList', { catalogs: states.filter(status => ['pending', 'stopped', 'failed'].includes(status.state)) });
  assert.equal(validator('FileCatalogList')({ catalogs: Array(33).fill(writing) }), false);
  const params = { productId: 'wow', buildIdentity: ready.buildIdentity, sourceRegion: 'us' };
  validate('EngineRequest', { protocolVersion: 1, id: 'catalog', method: 'inventory.catalog', params });
  for (const invalid of [{ ...params, generation: '1' }, { productId: 'wow', buildIdentity: ready.buildIdentity }, { ...params, buildIdentity: 'A'.repeat(64) }, { ...params, sourceRegion: 'US' }])
    assert.equal(validator('EngineRequest')({ protocolVersion: 1, id: 'catalog', method: 'inventory.catalog', params: invalid }), false, JSON.stringify(invalid));
  const set = { productId: 'wow', buildIdentity: ready.buildIdentity, action: 'stop' };
  validate('EngineRequest', { protocolVersion: 1, id: 'set', method: 'inventory.catalog.set', params: set });
  for (const invalid of [{ ...set, action: 'prepare' }, { ...set, sourceRegion: 'us' }, { productId: 'wow', action: 'resume' }])
    assert.equal(validator('EngineRequest')({ protocolVersion: 1, id: 'set', method: 'inventory.catalog.set', params: invalid }), false, JSON.stringify(invalid));
});

test('preparation progress accepts numbered and unknown totals while bounding explanation structure', () => {
  const frame = { protocolVersion: 1, type: 'progress', requestId: 'open', sequence: 1,
    stage: 'Checking build configurations', region: 'us', completed: null, total: null, unit: null };
  validate('EngineProgress', frame);
  validate('EngineProgress', { ...frame, step: null });
  const step = { index: 1, total: 22, description: 'Verify the exact build configurations.' };
  validate('EngineProgress', { ...frame, step });
  validate('EngineProgress', { ...frame, step: { ...step, total: null } });
  for (const invalid of [{ ...step, index: 0 }, { ...step, total: 0 }, { ...step, index: 1.5 },
    { ...step, description: '' }, { ...step, description: 'x'.repeat(401) }, { index: 1, total: 22 }, { ...step, extra: true }]) {
    assert.equal(validator('EngineProgress')({ ...frame, step: invalid }), false);
  }
});

test('download-size Home separates measured DL scope from unavailable research catalog and unknown size', async () => {
  const load = async name => JSON.parse(await readFile(path.join(root, `contracts/examples/${name}.json`), 'utf8'));
  const ready = await load('download-workspace');
  const unavailable = await load('download-unavailable-workspace');
  const page = await load('download-page');
  validate('WorkspaceSnapshot', ready); validate('WorkspaceSnapshot', unavailable); validate('DownloadPage', page);
  for (const patch of [{ inventoryState: 'ready' }, { inventory: {} }, { download: null },
    { metadata: null }, { downloadProblem: unavailable.downloadProblem }, { retentionState: 'unknown' }]) {
    assert.equal(validator('WorkspaceSnapshot')({ ...ready, ...patch }), false);
  }
  for (const patch of [{ downloadProblem: null }, { retentionState: 'measured' }, { download: ready.download }]) {
    assert.equal(validator('WorkspaceSnapshot')({ ...unavailable, ...patch }), false);
  }
  assert.equal(validator('DownloadSummary')({ ...ready.download, scope: 'allPublishedVariants' }), false);
  assert.equal(validator('DownloadSummary')({ ...ready.download, totalEncodedBytes: 450 }), false);
  assert.equal(validator('DownloadPage')({ ...page, entries: Array(101).fill(page.entries[0]) }), false);
  assert.equal(validator('DownloadPage')({ ...page, entries: [{ ...page.entries[0], id: '0' }] }), false);
  validate('EngineRequest', { protocolVersion: 1, id: 'download', method: 'download.read', params: {} });
});

test('Tables provider metadata cannot claim acquired files or readable rows', async () => {
  const load = async name => JSON.parse(await readFile(path.join(root, `contracts/examples/${name}.json`), 'utf8'));
  const page = await load('table-catalog-page'), definition = await load('table-definition');
  validate('TableCatalogPage', page); validate('TableDefinitionSnapshot', definition);
  assert.equal(validator('TableCatalogPage')({ ...page, entries: Array(101).fill(page.entries[0]) }), false);
  for (const invalid of [{ ...page.entries[0], rowsState: 'ready' }, { ...page.entries[0], sourceState: 'verified' },
    { ...page.entries[0], db2FileDataId: 841626 }, { ...page.entries[0], tableId: '../Item' }]) {
    assert.equal(validator('TableCatalogEntry')(invalid), false);
  }
  assert.equal(validator('TableDefinitionSnapshot')({ ...definition, definition: null }), false);
  validate('TableDefinitionSnapshot', { ...definition, entry: { ...definition.entry, definitionState: 'notBundled' },
    definition: null, problem: { code: 'DEFINITION_NOT_BUNDLED', message: 'No definition data is bundled for this provider entry.' } });
  assert.equal(validator('TableDefinitionField')({ ...definition.definition.columns[0], arrayLength: 4097 }), false);
  validate('EngineRequest', { protocolVersion: 1, id: 'tables', method: 'tables.catalog', params: {} });
  validate('EngineRequest', { protocolVersion: 1, id: 'definition', method: 'tables.definition', params: {} });
});

test('Table data pages preserve exact values and bound rows, cells and explicit locale', () => {
  const page = { productId: 'wow', buildIdentity: 'a'.repeat(64), sourceRegion: 'us',
    tableId: 'b977271e', researchLocale: 'enUS', artifactId: 'b'.repeat(64),
    rows: [{ id: '4294967295', isCopy: false, copySourceId: null, values: ['18446744073709551615', 'authored test value'] }],
    totalCount: '1', nextCursor: null };
  validate('TableDataPage', page);
  validate('TableDataReadiness', { snapshot: null });
  for (const patch of [{ researchLocale: 'us' }, { rows: Array(51).fill(page.rows[0]) },
    { rows: [{ ...page.rows[0], id: 4294967295 }] }, { rows: [{ ...page.rows[0], values: [23] }] },
    { rows: [{ ...page.rows[0], values: Array(513).fill('0') }] }, { nextCursor: '01' }, { artifactId: '../rows' }])
    assert.equal(validator('TableDataPage')({ ...page, ...patch }), false);
  for (const method of ['tables.prepare', 'tables.data', 'tables.rows'])
    validate('EngineRequest', { protocolVersion: 1, id: method, method, params: {} });
});

test('A narrowed table column says it keeps the low 8 or 16 of 32 stored bits and nothing else (D079)', () => {
  const column = { name: 'ItemGroupSoundsID', family: 'integer', isId: false, reference: { table: 'ItemGroupSounds', column: 'ID' },
    interpretation: null, narrowing: { storedBits: 32, usedBits: 8 } };
  validate('TableDataColumn', column);
  validate('TableDataColumn', { ...column, narrowing: { storedBits: 32, usedBits: 16 } });
  const { narrowing, ...plain } = column; validate('TableDataColumn', plain);
  for (const value of [null, { storedBits: 16, usedBits: 8 }, { storedBits: 32, usedBits: 32 }, { storedBits: 32 },
    { storedBits: 32, usedBits: 8, signed: true }])
    assert.equal(validator('TableDataColumn')({ ...column, narrowing: value }), false, JSON.stringify(value));
});

test('A table snapshot says when its definition was matched by layout only, never that the build was verified', async () => {
  const readiness = JSON.parse(await readFile(path.join(root, 'contracts/examples/table-data-layout-only.json'), 'utf8'));
  validate('TableDataReadiness', readiness);
  assert.equal(readiness.snapshot.schemaMatch, 'layoutOnly');
  const { schemaMatch, ...exact } = readiness.snapshot;
  validate('TableDataSnapshot', exact);
  for (const value of ['build', 'exactBuild', 'verified', null, ''])
    assert.equal(validator('TableDataSnapshot')({ ...exact, schemaMatch: value }), false, String(value));
});

test('Reference text keeps exact keys, closed states, the actual text source and bound page shape', async () => {
  const page = JSON.parse(await readFile(path.join(root, 'contracts/examples/table-reference-text.json'), 'utf8'));
  validate('TableReferenceTextPage', page);
  validate('EngineRequest', { protocolVersion: 1, id: 'text', method: 'tables.referenceText', params: {} });
  const invalid = change => { const value = structuredClone(page); change(value); assert.equal(validator('TableReferenceTextPage')(value), false, JSON.stringify(value).slice(0, 80)); };
  invalid(value => { value.policyRevision = 'table-reference-text-v2'; });
  invalid(value => { value.values[0].value = 12; });
  invalid(value => { value.values[0].value = '012'; });
  invalid(value => { value.values[0].value = '-0'; });
  invalid(value => { value.values[0].matchCount = 1; });
  invalid(value => { value.values[0].state = 'guessed'; });
  invalid(value => { value.columns[0].state = 'inferred'; });
  invalid(value => { value.values[0].text = 'x'.repeat(4097); });
  invalid(value => { value.columns[0].textSource.scope = 'partial'; });
  invalid(value => { value.columns[0].textSource.tableId = 'ItemClass'; });
  invalid(value => { value.columns[0].textSource.loadPolicy = 'background'; });
  invalid(value => { delete value.columns[0].textSource.measuredEncodedBytes; });
  invalid(value => { value.rowIds = Array(51).fill('1'); });
  invalid(value => { value.researchLocale = 'deDE'; });
  invalid(value => { delete value.values[0].row; });
  invalid(value => { value.columns[0].extra = true; });
  validate('TableReferenceTextValue', { ...page.values[0], value: '-128' });
  validate('TableReferenceTextValue', { ...page.values[0], value: '18446744073709551615' });
});

test('Follow keeps the exact value, the declared reference, closed states and a bounded target page', async () => {
  const page = JSON.parse(await readFile(path.join(root, 'contracts/examples/table-follow.json'), 'utf8'));
  validate('TableFollowPage', page);
  validate('TableFollowPage', { ...page, state: 'keyOutOfRange', value: '-129', page: null });
  validate('TableFollowPage', { ...page, state: 'unindexed', page: null, problem: { code: 'TABLE_FOLLOW_UNINDEXED', message: 'Too many rows.' } });
  validate('TableFollowPage', { ...page, value: '18446744073709551615', targetRange: { minimum: '0', maximum: '18446744073709551615' } });
  validate('EngineRequest', { protocolVersion: 1, id: 'follow', method: 'tables.follow', params: {} });
  const invalid = change => { const value = structuredClone(page); change(value); assert.equal(validator('TableFollowPage')(value), false, JSON.stringify(value).slice(0, 80)); };
  invalid(value => { value.policyRevision = 'table-follow-v2'; });
  invalid(value => { value.value = 7; });
  invalid(value => { value.value = '07'; });
  invalid(value => { value.value = '-0'; });
  invalid(value => { value.state = 'guessed'; });
  invalid(value => { value.scope = 'partial'; });
  invalid(value => { value.targetTableId = 'ItemClass'; });
  invalid(value => { value.columnIndex = 512; });
  invalid(value => { value.page.rows = Array(51).fill(value.page.rows[0]); });
  invalid(value => { delete value.reference; });
  invalid(value => { value.targetRange.minimum = -128; });
  invalid(value => { value.extra = true; });
});

test('Typed text evidence preserves authored invalid bytes and bounds the wire shape', async () => {
  const fixture = JSON.parse(await readFile(path.join(root, 'tests/desktop/fixtures/table-text-issues.json'), 'utf8'));
  const row = fixture.rows[0], issue = row.textIssues[0];
  validate('TableDataRow', row);
  validate('TableDataRow', { ...row, textIssues: null });
  for (const invalid of [{ ...issue, columnIndex: -1 }, { ...issue, columnIndex: 512 },
    { ...issue, rawBytesHex: 'C328' }, { ...issue, rawBytesHex: 'c32' }, { ...issue, rawBytesHex: '' },
    { ...issue, rawBytesHex: 'ff'.repeat(65536) }, { ...issue, code: 'TEXT_REPLACED' }, { ...issue, extra: true }])
    assert.equal(validator('TableTextIssue')(invalid), false);
  validate('TableTextIssue', { ...issue, rawBytesHex: 'ff'.repeat(65535) });
  // Cross-column family, invalid-UTF8 and per-row budget checks belong to runtime admission.
});

test('Retention separates historical receipts, dated presence, progress and durable intent revision', async () => {
  const snapshot = JSON.parse(await readFile(path.join(root, 'contracts/examples/retention-snapshot.json'), 'utf8'));
  validate('RetentionSnapshot', snapshot);
  validate('RetentionList', {jobs:[snapshot]});
  for (const patch of [{revision:9007199254740993}, {revision:'01'}, {objectCount:3},
    {pendingObjects:33}, {mode:'everything'}, {state:'ready'}, {scope:'installable'},
    {queuePosition:0}, {coverage:{...snapshot.coverage,presenceState:'verified'}}])
    assert.equal(validator('RetentionSnapshot')({...snapshot,...patch}),false);
  assert.equal(validator('RetentionList')({jobs:Array(10).fill(snapshot)}),false);
  for(const method of ['retention.read','retention.list','retention.set'])
    validate('EngineRequest',{protocolVersion:1,id:method,method,params:{}});
});

test('Settings keep unreadable values apart from saved ones and take only a known switch', async () => {
  const snapshot = JSON.parse(await readFile(path.join(root, 'contracts/examples/settings-snapshot.json'), 'utf8'));
  validate('SettingsSnapshot', snapshot);
  validate('SettingsSnapshot', { ...snapshot, file: 'absent', lastChangedAt: null, communityBuildHistory: { value: false, defaultValue: false, source: 'default', problem: null } });
  const community = snapshot.communityBuildHistory;
  for (const patch of [{ file: 'missing' }, { formatVersion: 2 }, { lastChangedAt: '2026-10-18 12:02' }, { lastChangedAt: '2026-10-18T12:02:00' },
    { communityBuildHistory: { ...community, source: 'guessed' } }, { communityBuildHistory: { ...community, defaultValue: true } },
    { communityBuildHistory: { ...community, value: 'false' } }, { storage: { dataDirectory: '' } }, { storage: { dataDirectory: '/x', size: '1' } },
    { downloadLimits: { ...snapshot.downloadLimits, bandwidthCapBytesPerSecond: 1024 } }, { downloadLimits: { ...snapshot.downloadLimits, parallelDownloads: 0 } }, { extra: true }])
    assert.equal(validator('SettingsSnapshot')({ ...snapshot, ...patch }), false, JSON.stringify(patch));
  const measurement = { bytes: '51749982208', files: '1204', skipped: '0', startedAt: '2026-10-20T13:12:00.1234567+00:00', measuredAt: '2026-10-20T13:12:04Z' };
  validate('StorageMeasurement', measurement);
  for (const patch of [{ bytes: 51749982208 }, { bytes: '01' }, { skipped: '-1' }, { measuredAt: null }, { path: '/x' }])
    assert.equal(validator('StorageMeasurement')({ ...measurement, ...patch }), false, JSON.stringify(patch));
  const request = (method, params) => ({ protocolVersion: 1, id: method, method, params });
  validate('EngineRequest', request('settings.read', {}));
  validate('EngineRequest', request('settings.storage.measure', {}));
  validate('EngineRequest', request('settings.set', { name: 'communityBuildHistory', value: true }));
  for (const params of [{}, { name: 'communityBuildHistory' }, { name: 'communityBuildHistory', value: 'true' }, { name: 'bandwidthCap', value: true },
    { name: 'communityBuildHistory', value: true, path: '/tmp' }])
    assert.equal(validator('EngineRequest')(request('settings.set', params)), false, JSON.stringify(params));
  assert.equal(validator('EngineRequest')(request('settings.read', { path: '/tmp' })), false);
  assert.equal(validator('EngineRequest')(request('settings.storage.measure', { root: '/' })), false);
});

test('Download summaries keep exact counts, the three build states and a bounded request', async () => {
  const value = JSON.parse(await readFile(path.join(root, 'contracts/examples/download-summaries.json'), 'utf8'));
  validate('DownloadSummaries', value);
  const measured = value.summaries[0];
  for (const patch of [{ state: 'downloaded' }, { download: { ...measured.download, storedBytes: 410 } }, { download: { ...measured.download, countState: 'checked' } },
    { download: { ...measured.download, scope: 'enUS' } }, { download: { ...measured.download, sourceRegions: [] } },
    { download: { ...measured.download, presence: { ...measured.download.presence, state: 'verified' } } }, { download: { ...measured.download, percent: '41%' } },
    { download: { ...measured.download, unresolvableRootContent: undefined } }, { download: { ...measured.download, unresolvableRootContent: 10 } }])
    assert.equal(validator('BuildDownloadSummary')({ ...measured, ...patch }), false);
  assert.equal(validator('DownloadSummaries')({ ...value, summaries: Array(101).fill(value.summaries[4]) }), false);
  // Every read names the revision it was taken at, and a change wait answers one (D077).
  for (const revision of [undefined, '12', 'ABCDEF0123456789-1', '0123456789abcdef-01', '0123456789abcdef-12345678901234567890'])
    assert.equal(validator('DownloadSummaries')({ ...value, revision }), false, String(revision));
  validate('DownloadChanges', { revision: value.revision });
  assert.equal(validator('DownloadChanges')({ revision: value.revision, changed: true }), false);
  validate('EngineRequest', { protocolVersion: 1, id: 'changes', method: 'download.changes', params: { after: value.revision } });
  for (const params of [{}, { after: '12' }, { after: value.revision, builds: [] }])
    assert.equal(validator('EngineRequest')({ protocolVersion: 1, id: 'changes', method: 'download.changes', params }), false, JSON.stringify(params));
  const builds = value.summaries.map(({ productId, buildIdentity }) => ({ productId, buildIdentity }));
  validate('EngineRequest', { protocolVersion: 1, id: 'summaries', method: 'download.summaries', params: { builds } });
  for (const params of [{ builds: [] }, { builds: Array(101).fill(builds[0]) }, { builds: [{ ...builds[0], sourceRegion: 'us' }] }, { builds: [{ ...builds[0], buildIdentity: 'A'.repeat(64) }] }, {}])
    assert.equal(validator('EngineRequest')({ protocolVersion: 1, id: 'summaries', method: 'download.summaries', params }), false);
});

test('Items pages preserve all name states and literal bounded query shapes', async () => {
  const page=JSON.parse(await readFile(path.join(root,'contracts/examples/items-page.json'),'utf8'));
  validate('ItemsPage',page);
  for(const name of [{state:'valid',value:null},{state:'valid',value:''},{state:'empty',value:' '},
    {state:'invalid',value:''},{state:'missingSparse',value:'Item 7'},{state:'unknown',value:null}])
    assert.equal(validator('ItemName')(name),false);
  for(const patch of [{query:'x'.repeat(129)},{entries:Array(51).fill(page.entries[0])},
    {entries:[{...page.entries[0],id:0}]},{nextCursor:'unsafe/path'},
    {context:{...page.context,researchLocale:'us'}}])
    assert.equal(validator('ItemsPage')({...page,...patch}),false);
  validate('ItemsPage',{...page,query:'%_\\ Épée'});
  validate('ItemsPage',{...page,query:'😀'.repeat(128)});
  for(const method of ['items.data','items.prepare','items.page','items.inspect'])
    validate('EngineRequest',{protocolVersion:1,id:method,method,params:{}});
});

test('Partial source proof cannot masquerade as full content verification', () => {
  const receipt = { contentKey: 'a'.repeat(32), encodingKey: 'b'.repeat(32), sha256: 'c'.repeat(64),
    encodedBytes: '100', decodedBytes: '16', sourceUrl: 'https://us.cdn.blizzard.com/tpr/wow/data/bb/bb/' + 'b'.repeat(32),
    retrievedAt: '2026-10-19T00:00:00Z', verifiedAt: '2026-10-19T00:00:00Z', verifierRevision: 'content-blte-v1',
    location: { kind: 'loose', offset: '0', sourceBytes: '100', archiveKey: null } };
  validate('ContentReceipt', receipt);
  const projection = { policy: 'blte-clear-chunks-v1', identitySha256: 'd'.repeat(64),
    readableRanges: [{ offset: '0', byteLength: '8', sha256: 'e'.repeat(64) }],
    unavailableRanges: [{ offset: '8', byteLength: '8', chunkOrdinal: 1, keyName: '0123456789ABCDEF' }] };
  validate('ContentReceipt', { ...receipt, verifierRevision: 'content-blte-projection-v1', projection });
  assert.equal(validator('ContentReceipt')({ ...receipt, projection }), false);
  assert.equal(validator('ContentReceipt')({ ...receipt, verifierRevision: 'content-blte-projection-v1' }), false);
  assert.equal(validator('ContentReceipt')({ ...receipt, verifierRevision: 'content-blte-projection-v1', projection: null }), false);
  assert.equal(validator('ContentProjection')({ ...projection, unavailableRanges: [] }), false);
  validate('ContentUnavailableRange', { ...projection.unavailableRanges[0], chunkOrdinal: 65535 });
  assert.equal(validator('ContentUnavailableRange')({ ...projection.unavailableRanges[0], chunkOrdinal: 65536 }), false);
  assert.equal(validator('ContentUnavailableRange')({ ...projection.unavailableRanges[0], keyName: '0123456789abcdef' }), false);
});

test('Items readiness accepts known v1 and v2 full snapshots while rejecting unknown interpretations', async () => {
  const snapshot=JSON.parse(await readFile(path.join(root,'tests/desktop/fixtures/items-snapshot.json'),'utf8'));
  const dependencies=[snapshot.itemSource,snapshot.sparseSource].map(source=>({tableId:source.tableId,state:'ready',artifactId:source.artifactId}));
  for (const featureRevision of ['items-projection-v1','items-projection-v2']) {
    const current={...snapshot,featureRevision};
    validate('ItemsSnapshot',current); validate('ItemsReadiness',{snapshot:current,dependencies});
  }
  assert.equal(validator('ItemsSnapshot')({...snapshot,featureRevision:'items-projection-v3'}),false);
  assert.equal(validator('ItemsReadiness')({snapshot:{...snapshot,featureRevision:'unknown'},dependencies}),false);
});

test('Icon capabilities carry only bounded verified images and explicit independent outcomes', () => {
  const image={artifactId:'b'.repeat(64),width:64,height:64,byteLength:'4660'};
  const outcome={itemId:'4294967295',fileDataId:'132759',status:'ready',image,problem:null};
  const context={productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',researchLocale:'enUS'};
  validate('ItemIconBatch',{context,artifactId:'c'.repeat(64),outcomes:[outcome]});
  for (const status of ['missingItem','fieldUnavailable','noIcon','missingMembership','ambiguousVariants','encodingUnresolved','protected','unsupported','unavailable','corrupt']) {
    validate('ItemIconOutcome',{...outcome,status,image:null});
    assert.equal(validator('ItemIconOutcome')({...outcome,status}),false);
  }
  assert.equal(validator('ItemIconOutcome')({...outcome,image:null}),false);
  assert.equal(validator('ItemIconOutcome')({...outcome,fileDataId:'0'}),false);
  assert.equal(validator('ItemIconImage')({...image,width:1025}),false);
  assert.equal(validator('ItemIconImage')({...image,path:'/private/object.png'}),false);
  validate('ItemIconLeaseDescriptor',{leaseId:'d'.repeat(32),artifactId:image.artifactId,sha256:'e'.repeat(64),byteLength:image.byteLength,width:64,height:64});
  assert.equal(validator('ItemIconBatch')({context,artifactId:'c'.repeat(64),outcomes:Array(51).fill(outcome)}),false);
});

test('Classification labels keep source evidence, finite states and strict exact request authority', async () => {
  const itemSnapshot=JSON.parse(await readFile(path.join(root,'tests/desktop/fixtures/items-snapshot.json'),'utf8'));
  const snapshot={...itemSnapshot.itemSource,tableId:'b977271e',association:{...itemSnapshot.itemSource.association,fileDataId:'1140189'}};
  const target={tableId:'b977271e',rowId:'4294967295',columnIndex:2,isCopy:true,copySourceId:'7'};
  const key={tableId:'50238ec2',columnIndex:1,value:'2'};
  const value={state:'ready',sourceKeys:[key],text:' Authored <label> ',target,observedCandidates:'1',problem:null};
  const context=itemSnapshot.context;
  const source={kind:'class',state:'ready',snapshot,indexArtifactId:'c'.repeat(64),problem:null};
  const outcome={itemId:'4294967295',admitted:true,class:value,subclass:null};
  const batch={context,artifactId:'b'.repeat(64),policyRevision:'classification-label-index-v1',sources:[source],outcomes:[outcome]};
  validate('ItemClassificationBatch',batch);
  validate('ItemClassificationBatch',{...batch,outcomes:Array(50).fill(outcome)});
  for(const state of ['emptyText','invalidText','unsupported'])
    validate('ItemClassificationValue',{...value,state,text:state==='emptyText'?'':null,problem:state==='unsupported'?{code:'ITEM_CLASSIFICATION_TEXT_LIMIT',message:'Authored limit.',retryable:false}:null});
  for(const state of ['missingItem','fieldUnavailable','keyOutOfRange','notFound','ambiguous','incompleteTarget','unavailable','protected','corrupt'])
    validate('ItemClassificationValue',{...value,state,text:null,target:null});
  for(const patch of [{policyRevision:'classification-label-index-v3'},{sources:[]},{outcomes:[]},{outcomes:Array(51).fill(outcome)},
    {outcomes:[{...outcome,itemId:'4294967296'}]},{outcomes:[{...outcome,itemId:7}]},{outcomes:[{...outcome,class:{...value,state:'unknown'}}]}])
    assert.equal(validator('ItemClassificationBatch')({...batch,...patch}),false);
  for(const patch of [{text:''},{text:null},{text:'a'.repeat(4097)},{target:null},{sourceKeys:Array(3).fill(key)},
    {target:{...target,copySourceId:null}},{target:{...target,columnIndex:512}},{sourceKeys:[{...key,value:2}]},
    {sourceKeys:[{...key,value:'2147483648'}]},{sourceKeys:[{...key,value:'-2147483649'}]},
    {sourceKeys:[{...key,value:'-0'}]},{observedCandidates:'65537'}])
    assert.equal(validator('ItemClassificationValue')({...value,...patch}),false);
  for(const sourceValue of ['-2147483648','2147483647','0']) validate('ItemClassificationSourceKey',{...key,value:sourceValue});
  validate('ItemClassificationValue',{...value,observedCandidates:'65536'});
  const params={...context,artifactId:batch.artifactId,itemIds:['0','7','4294967295','7']};
  const request={protocolVersion:1,id:'labels',method:'items.classifications',params};
  validate('EngineRequest',request); validate('EngineRequest',{...request,params:{...params,includeSubclass:true}});
  for(const policyRevision of ['classification-label-index-v1','classification-label-index-v2'])
    validate('EngineRequest',{...request,params:{...params,includeSubclass:true,policyRevision}});
  for(const patch of [{itemIds:[]},{itemIds:Array(51).fill('7')},{itemIds:['01']},{itemIds:['4294967296']},{itemIds:[7]},
    {includeSubclass:null},{includeSubclass:1},{includeSubclass:'true'},{researchLocale:'us'},{artifactId:'../index'},{url:'https://example.org/source'},
    {policyRevision:null},{policyRevision:2},{policyRevision:''},{policyRevision:'classification-label-index-v3'}])
    assert.equal(validator('EngineRequest')({...request,params:{...params,...patch}}),false);

  const subSnapshot={...snapshot,tableId:'95d8638f',association:{...snapshot.association,fileDataId:'1261604'}};
  const subclassSource={...source,kind:'subclass',snapshot:subSnapshot,indexArtifactId:'d'.repeat(64)};
  const subclass={...value,sourceKeys:[key,{...key,columnIndex:2,value:'0'}],target:{...target,tableId:'95d8638f'},text:'Authored subclass'};
  const v2={...batch,policyRevision:'classification-label-index-v2',sources:[source,subclassSource],outcomes:[{...outcome,subclass}]};
  validate('ItemClassificationBatch',v2);
  validate('ItemClassificationBatch',{...v2,sources:[source],outcomes:[outcome]}); // v2 class-only echoes v2.
  assert.equal(validator('ItemClassificationBatch')({...v2,policyRevision:'classification-label-index-v1'}),false,'v1 cannot silently enable the v2 target');
  assert.equal(validator('ItemClassificationBatch')({...v2,outcomes:[{...outcome,subclass:{...subclass,target}}]}),false,'subclass target must remain its exact table');
  assert.equal(validator('ItemClassificationBatch')({...v2,outcomes:[{...outcome,class:subclass,subclass}]}),false,'class target cannot become subclass');
  const unsupported={state:'unsupported',sourceKeys:subclass.sourceKeys,text:null,target:null,observedCandidates:null,
    problem:{code:'ITEM_SUBCLASS_UNSUPPORTED',message:'Authored legacy unsupported target.',retryable:false}};
  validate('ItemClassificationBatch',{...batch,sources:[source,{kind:'subclass',state:'unsupported',snapshot:null,indexArtifactId:null,problem:unsupported.problem}],outcomes:[{...outcome,subclass:unsupported}]});
  const unavailable={...unsupported,state:'unavailable',problem:{code:'ITEM_CLASSIFICATION_STALE',message:'Authored target replacement.',retryable:true}};
  for(const failed of ['class','subclass']) {
    const sources=v2.sources.map(entry=>entry.kind===failed?{...entry,state:'unavailable',snapshot:null,indexArtifactId:null,problem:unavailable.problem}:entry);
    validate('ItemClassificationBatch',{...v2,sources,outcomes:[{...v2.outcomes[0],[failed]:unavailable}]});
  }
});

test('Creature core schema preserves partial names, four direct slots and exact source-only authority', async () => {
  const snapshot=JSON.parse(await readFile(path.join(root,'contracts/examples/creatures-snapshot.json'),'utf8'));
  validate('CreaturesSnapshot',snapshot);
  validate('CreaturesReadiness',{snapshot,dependencyState:'ready',sourceArtifactId:snapshot.creatureSource.artifactId});
  validate('CreaturesReadiness',{snapshot:null,dependencyState:'notPrepared',sourceArtifactId:null});
  const fields=snapshot.fields, source={tableId:'c9d6b6b3',artifactId:snapshot.creatureSource.artifactId,rowId:'4294967295',columnIndex:0};
  const name={state:'invalid',value:null,issue:{code:'DB2_TEXT_ENCODING_INVALID',source}};
  const displaySlots=['23','0','-1','23'].map((displayId,slot)=>({slot,displayId,probability:['0.25','0','-0.5','1'][slot],
    state:slot===1?'noDeclaredDisplay':slot===2?'invalidDisplayReference':'declared',displayField:fields.displayIds[slot],probabilityField:fields.probabilities[slot]}));
  const summary={id:'4294967295',name,facts:fields.facts.map((field,i)=>({...field,value:['2','7','9','-1'][i]})),displaySlots};
  const page={context:snapshot.context,artifactId:snapshot.artifactId,query:'',exactCreatureId:null,entries:[summary],nextCursor:null};
  validate('CreaturesPage',page); validate('CreaturesPage',{...page,query:'😀'.repeat(128),entries:[]});
  const detail={summary,creatureRow:{id:summary.id,isCopy:true,copySourceId:'7',values:[''],textIssues:[{columnIndex:0,code:'DB2_TEXT_ENCODING_INVALID',rawBytesHex:'c328'}]},source:{...source,columnIndex:null}};
  const inspect={context:snapshot.context,artifactId:snapshot.artifactId,creatureId:summary.id,state:'found',coverage:snapshot.creatureSource.coverage,detail};
  validate('CreatureInspectResult',inspect); validate('CreatureInspectResult',{...inspect,state:'notFoundInAdmittedScope',detail:null});
  for(const patch of [{state:'valid',value:null,issue:null},{state:'valid',value:'',issue:null},{state:'empty',value:' ',issue:null},
    {state:'invalid',value:''},{state:'invalid',issue:null},{issue:{...name.issue,rawBytesHex:'c328'}}])
    assert.equal(validator('CreatureName')({...name,...patch}),false);
  for(const patch of [{displaySlots:displaySlots.slice(1)},{facts:summary.facts.slice(1)},
    {displaySlots:[displaySlots[1],displaySlots[0],displaySlots[2],displaySlots[3]]},{id:'4294967296'}])
    assert.equal(validator('CreatureSummary')({...summary,...patch}),false);
  for(const patch of [{state:'declared',displayId:'0'},{state:'noDeclaredDisplay',displayId:'-1'},
    {state:'invalidDisplayReference',displayId:'1'},{displayId:'2147483648'},{probability:'NaN'},{probability:'Infinity'}])
    assert.equal(validator('CreatureDisplaySlot')({...displaySlots[0],...patch}),false);
  for(const patch of [{featureRevision:'creatures-core-v2'},{queryPolicy:'fuzzy'}, {semanticProfile:'models-ready'},
    {creatureSource:{...snapshot.creatureSource,tableId:'bfdaf9f1'}},{fields:{...fields,displayIds:[]}}, {counts:{...snapshot.counts,rows:3}}])
    assert.equal(validator('CreaturesSnapshot')({...snapshot,...patch}),false);
  assert.equal(validator('CreaturesPage')({...page,entries:Array(51).fill(summary)}),false);
  assert.equal(validator('CreatureInspectResult')({...inspect,state:'notFoundInAdmittedScope'}),false);
  assert.equal(validator('CreaturesReadiness')({snapshot,dependencyState:'notPrepared',sourceArtifactId:null}),false);
  const request=(method,params)=>({protocolVersion:1,id:method,method,params});
  for(const method of ['creatures.data','creatures.prepare']) validate('EngineRequest',request(method,snapshot.context));
  const params={...snapshot.context,artifactId:snapshot.artifactId};
  validate('EngineRequest',request('creatures.page',params));
  validate('EngineRequest',request('creatures.page',{...params,exactCreatureId:'0',limit:50}));
  validate('EngineRequest',request('creatures.inspect',{...params,creatureId:'4294967295'}));
  for(const patch of [{query:'x',exactCreatureId:'7'},{query:null},{limit:51},{limit:'2'},{after:'../cursor'},
    {exactCreatureId:'01'},{artifactId:'../feature'},{displayId:'23'},{url:'https://example.org/source'}])
    assert.equal(validator('EngineRequest')(request('creatures.page',{...params,...patch})),false);
  assert.equal(validator('EngineRequest')(request('creatures.inspect',{...params,creatureId:7})),false);
  assert.equal(validator('EngineRequest')(request('creatures.display.prepare',params)),false);
});

test('Selected Creature metadata keeps distinct role proofs and never claims model appearance', async () => {
  const result=JSON.parse(await readFile(path.join(root,'contracts/examples/creature-display-metadata.json'),'utf8'));
  const clone=value=>structuredClone(value), display=result.sources[0], model=result.sources[1];
  validate('CreatureDisplayMetadataResult',result);
  for(const patch of [{appearance:'ready'},{metadataPolicy:'creature-display-metadata-v2'},{modelFileDataId:'0'},
    {modelId:null},{modelId:'65536'},{sources:[]},{sources:[display,model,model]},{sources:[model,display]},
    {creatureSource:{...result.creatureSource,tableId:'bfdaf9f1'}},{slot:4}])
    assert.equal(validator('CreatureDisplayMetadataResult')({...result,...patch}),false);
  for(const mutate of [v=>v.snapshot.tableId='c9d6b6b3',v=>v.row.source.tableId='7f695fa5',v=>v.row.edgeValue.tableId='7f695fa5',
    v=>v.row.isCopy=false,v=>v.row.copySourceId=null,v=>v.row.source.columnIndex=512,v=>v.row.edgeValue.columnIndex='0',
    v=>v.row.edgeValue.value='65536',v=>v.matchCount='2',v=>v.matchCount=null]) {
    const changed=clone(display); mutate(changed); assert.equal(validator('CreatureDisplayTarget')(changed),false);
  }
  const badModel=clone(model); badModel.row.edgeValue.value='4294967296'; assert.equal(validator('CreatureDisplayTarget')(badModel),false);
  assert.equal(validator('CreatureSourceReference')(display.row.source),false,'C1 source schema must remain Creature-only');
  assert.equal(validator('CreatureSourceValue')(display.row.edgeValue),false,'C2 edge values must not widen C1 values');
  const noFile={...result,modelFileDataId:null};
  const missing=clone(display); missing.matchCount='0'; missing.row=null;
  const missingResult={...noFile,state:'displayMissingInAdmittedScope',sources:[missing],modelId:null,absence:'unknownOutsideReadableScope'};
  validate('CreatureDisplayMetadataResult',missingResult);
  assert.equal(validator('CreatureDisplayMetadataResult')({...missingResult,absence:'absentFromCompleteSource'}),false);
  const missingModel=clone(model); missingModel.matchCount='0'; missingModel.row=null;
  validate('CreatureDisplayMetadataResult',{...noFile,state:'modelMissingInAdmittedScope',sources:[display,missingModel],absence:'absentFromCompleteSource'});
  const ambiguous=clone(display); ambiguous.matchCount='2'; ambiguous.row=null;
  validate('CreatureDisplayMetadataResult',{...noFile,state:'displayAmbiguous',sources:[ambiguous],modelId:null});
  assert.equal(validator('CreatureDisplayMetadataResult')({...noFile,state:'displayAmbiguous',sources:[display],modelId:null}),false);
  const zero=clone(display); zero.row.edgeValue.value='0';
  validate('CreatureDisplayMetadataResult',{...noFile,state:'noDeclaredModel',sources:[zero],modelId:null});
  const zeroFile=clone(model); zeroFile.row.edgeValue.value='0';
  validate('CreatureDisplayMetadataResult',{...noFile,state:'noDeclaredModelFile',sources:[display,zeroFile]});
  validate('CreatureDisplayMetadataResult',{...noFile,state:'notPrepared',sources:[],modelId:null,requiredTableId:'bfdaf9f1'});
  validate('CreatureDisplayMetadataResult',{...noFile,state:'notPrepared',sources:[display],requiredTableId:'7f695fa5'});
  for(const state of ['protected','unsupported','unavailable','corrupt','resourceLimit'])
    validate('CreatureDisplayMetadataResult',{...noFile,state,sources:[display],problem:{code:'AUTHORED_OPTIONAL_FAILURE',message:'Authored failure.',retryable:true,target:'model'}});
  const noSlot={...noFile,state:'noDeclaredDisplay',sources:[],modelId:null,selected:{...result.selected,displayId:'0',state:'noDeclaredDisplay'}};
  validate('CreatureDisplayMetadataResult',noSlot);
  validate('CreatureDisplayMetadataResult',{...noSlot,state:'invalidDisplayReference',selected:{...result.selected,displayId:'-1',state:'invalidDisplayReference'}});
  validate('CreatureDisplayMetadataResult',{...noSlot,state:'creatureNotFoundInAdmittedScope',selected:null,absence:'unknownOutsideReadableScope'});
  const params={...result.context,artifactId:result.artifactId,creatureId:result.creatureId,slot:0};
  for(const method of ['creatures.display.read','creatures.display.prepare']) {
    const request={protocolVersion:1,id:method,method,params}; validate('EngineRequest',request);
    for(const patch of [{slot:-1},{slot:4},{slot:'0'},{slot:0.5},{creatureId:7},{creatureId:'01'},{creatureId:'4294967296'},
      {researchLocale:'us'},{artifactId:'../core'},{displayId:'23'},{modelFileDataId:'123456'},{url:'https://example.org/model'}])
      assert.equal(validator('EngineRequest')({...request,params:{...params,...patch}}),false);
  }
});

test('real C# host agrees with the wire schema and restores a fresh catalog without HTTP', { timeout: 30_000 }, async t => {
  const local = path.join(root, '.local/toolchains/dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
  const dotnet = process.env.VIBEMINER_DOTNET || (existsSync(local) ? local : 'dotnet');
  const assembly = process.env.VIBEMINER_CONTRACT_HOST_ASSEMBLY || path.join(root, 'src/backend/VibeMiner.Host/bin/Debug/net10.0/VibeMiner.Host.dll');
  assert.ok(existsSync(assembly), 'Build the C# host before running contract integration tests.');
  const data = await mkdtemp(path.join(tmpdir(), 'vibeminer-contract-'));
  let diagnostics = '';
  const child = spawn(dotnet, [assembly, '--data-dir', data], {
    cwd: root, stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' },
  });
  const exited = once(child, 'exit');
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', value => { diagnostics = (diagnostics + value).slice(-16_384); });
  const pending = new Map();
  const frames = createInterface({ input: child.stdout });
  frames.on('line', line => {
    try {
      assert.ok(Buffer.byteLength(line) <= 1_048_576);
      const frame = JSON.parse(line);
      if (frame.type === 'progress') { validate('EngineProgress', frame); return; }
      validate(frame.error ? 'EngineFailure' : 'EngineSuccess', frame);
      const waiter = pending.get(frame.id);
      assert.ok(waiter, `Unexpected response ID ${frame.id}`);
      pending.delete(frame.id);
      waiter.resolve(frame);
    } catch (error) {
      for (const waiter of pending.values()) waiter.reject(error);
      pending.clear();
    }
  });
  child.on('error', error => { for (const waiter of pending.values()) waiter.reject(error); pending.clear(); });
  child.on('exit', () => {
    for (const waiter of pending.values()) waiter.reject(new Error(`Engine exited: ${diagnostics}`));
    pending.clear();
  });
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) child.kill();
    await exited;
    frames.close();
    await rm(data, { recursive: true, force: true });
  });
  let sequence = 0;
  const request = (method, params = {}, protocolVersion = 1) => {
    const id = String(++sequence);
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      child.stdin.write(`${JSON.stringify({ protocolVersion, id, method, params })}\n`);
    });
  };
  const hello = await request('hello');
  assert.ok(!hello.error, diagnostics);
  validate('HelloResult', hello.result);
  assert.ok(hello.result.capabilities.includes('retention-v1'));
  assert.ok(hello.result.capabilities.includes('download-summary-v1'));
  const summaries = await request('download.summaries', { builds: [{ productId: 'wow', buildIdentity: 'a'.repeat(64) }] });
  assert.ok(!summaries.error, JSON.stringify(summaries.error));
  validate('DownloadSummaries', summaries.result);
  assert.deepEqual(summaries.result.summaries, [{ productId: 'wow', buildIdentity: 'a'.repeat(64), state: 'notOpened', download: null }]);
  // A revision from another run answers at once with this run's current one.
  const changes = await request('download.changes', { after: '0000000000000000-0' });
  assert.ok(!changes.error, JSON.stringify(changes.error));
  validate('DownloadChanges', changes.result);
  assert.equal(changes.result.revision, summaries.result.revision);
  assert.ok(hello.result.capabilities.includes('items-v1'));
  assert.ok(hello.result.capabilities.includes('item-icons-v1'));
  assert.ok(hello.result.capabilities.includes('item-classifications-v1'));
  assert.ok(hello.result.capabilities.includes('item-subclass-labels-v1'));
  assert.ok(hello.result.capabilities.includes('creatures-v1'));
  assert.ok(hello.result.capabilities.includes('creature-display-metadata-v1'));
  assert.ok(hello.result.capabilities.includes('tables-reference-text-v1'));
  assert.ok(hello.result.capabilities.includes('tables-follow-v1'));
  const textQuery={productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',tableId:'50238ec2',researchLocale:'enUS',artifactId:'b'.repeat(64)};
  const text=await request('tables.referenceText',textQuery);
  assert.ok(text.error,'A fresh catalog cannot fabricate reference text.');
  assert.ok(!['INVALID_REQUEST','UNKNOWN_METHOD','TABLE_REFERENCE_TEXT_UNAVAILABLE'].includes(text.error.code));
  assert.equal((await request('tables.referenceText',{...textQuery,values:['9']})).error?.code,'INVALID_REQUEST');
  const followQuery={...textQuery,columnIndex:1,value:'9',targetTableId:'b977271e',targetArtifactId:'c'.repeat(64)};
  const followed=await request('tables.follow',followQuery);
  assert.ok(followed.error,'A fresh catalog cannot fabricate followed rows.');
  assert.ok(!['INVALID_REQUEST','UNKNOWN_METHOD','TABLE_FOLLOW_UNAVAILABLE'].includes(followed.error.code));
  assert.equal((await request('tables.follow',{...followQuery,value:'09'})).error?.code,'INVALID_REQUEST');
  for(const capability of ['item-effects-links-v1','item-effects-inputs-v1','item-effect-spell-names-v1'])assert.ok(hello.result.capabilities.includes(capability),'The qualified production Effects adapter must expose all three phases together.');
  const creatureContext={productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',researchLocale:'enUS'};
  const effects=await request('items.effects.data',{...creatureContext,itemsArtifactId:'b'.repeat(64),itemId:'7'});
  assert.ok(effects.error,'A fresh catalog cannot fabricate an admitted Items effects projection.');
  assert.ok(!['INVALID_REQUEST','UNKNOWN_METHOD','ITEM_EFFECTS_UNAVAILABLE'].includes(effects.error.code));
  assert.ok(Buffer.byteLength(JSON.stringify(effects))+1<=512*1024);
  const creatureData=await request('creatures.data',creatureContext);
  if(creatureData.error) assert.ok(!['INVALID_REQUEST','UNKNOWN_METHOD'].includes(creatureData.error.code));
  else { validate('CreaturesReadiness',creatureData.result); assert.equal(creatureData.result.snapshot,null); }
  const creaturePage=await request('creatures.page',{...creatureContext,artifactId:'b'.repeat(64)});
  assert.ok(creaturePage.error,'A fresh catalog cannot fabricate an admitted Creature projection.');
  assert.ok(!['INVALID_REQUEST','UNKNOWN_METHOD'].includes(creaturePage.error.code));
  assert.ok(Buffer.byteLength(JSON.stringify(creaturePage))+1<=512*1024);
  const invalidCreature=await request('creatures.inspect',{...creatureContext,artifactId:'b'.repeat(64),creatureId:7});
  assert.equal(invalidCreature.error?.code,'INVALID_REQUEST');
  const displayParams={...creatureContext,artifactId:'b'.repeat(64),creatureId:'7',slot:0};
  const display=await request('creatures.display.read',displayParams);
  assert.ok(display.error,'A fresh catalog cannot fabricate selected-display metadata.');
  assert.ok(!['INVALID_REQUEST','UNKNOWN_METHOD','CREATURE_DISPLAY_UNAVAILABLE'].includes(display.error.code));
  assert.ok(Buffer.byteLength(JSON.stringify(display))+1<=512*1024);
  const invalidDisplay=await request('creatures.display.prepare',{...displayParams,slot:4});
  assert.equal(invalidDisplay.error?.code,'INVALID_REQUEST');
  const labels = await request('items.classifications', { productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',
    researchLocale:'enUS',artifactId:'b'.repeat(64),itemIds:['0','4294967295'],includeSubclass:true });
  assert.ok(labels.error, 'An unprepared exact Items feature cannot fabricate labels.');
  assert.ok(Buffer.byteLength(JSON.stringify(labels)) + 1 <= 512*1024);
  const invalidLabels=await request('items.classifications',{productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',
    researchLocale:'enUS',artifactId:'b'.repeat(64),itemIds:['7'],includeSubclass:'true'});
  assert.equal(invalidLabels.error?.code,'INVALID_REQUEST');
  const v2Labels=await request('items.classifications',{productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',
    researchLocale:'enUS',artifactId:'b'.repeat(64),itemIds:['7'],includeSubclass:true,policyRevision:'classification-label-index-v2'});
  assert.ok(v2Labels.error,'Explicit v2 cannot fabricate an unprepared exact feature.');
  assert.notEqual(v2Labels.error.code,'INVALID_REQUEST','The configured engine must recognize the explicit v2 request.');
  const unknownLabels=await request('items.classifications',{productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',
    researchLocale:'enUS',artifactId:'b'.repeat(64),itemIds:['7'],policyRevision:'classification-label-index-v3'});
  assert.equal(unknownLabels.error?.code,'INVALID_REQUEST');
  assert.ok(hello.result.capabilities.includes('settings-v1'));
  const settings = await request('settings.read');
  assert.ok(!settings.error, JSON.stringify(settings.error));
  validate('SettingsSnapshot', settings.result);
  assert.equal(settings.result.file, 'absent', 'A fresh data folder has no settings file and reading never creates one.');
  assert.equal(settings.result.communityBuildHistory.source, 'default');
  // Real paths: Windows temp folders can be given in 8.3 form, which the engine expands.
  assert.equal(realpathSync.native(settings.result.storage.dataDirectory), realpathSync.native(data));
  assert.equal((await request('settings.set', { name: 'communityBuildHistory', value: 'true' })).error?.code, 'INVALID_REQUEST');
  const saved = await request('settings.set', { name: 'communityBuildHistory', value: true });
  assert.ok(!saved.error, JSON.stringify(saved.error));
  validate('SettingsSnapshot', saved.result);
  assert.deepEqual([saved.result.file, saved.result.communityBuildHistory.value, saved.result.communityBuildHistory.source], ['read', true, 'saved']);
  const measured = await request('settings.storage.measure');
  assert.ok(!measured.error, JSON.stringify(measured.error));
  validate('StorageMeasurement', measured.result);
  assert.ok(BigInt(measured.result.bytes) > 0n, 'The catalog and settings file are in the measured folder.');
  assert.ok(hello.result.capabilities.includes('inventory-files-v1'));
  assert.ok(hello.result.capabilities.includes('file-catalog-v1'));
  const catalogScope = { productId: 'wow', buildIdentity: 'a'.repeat(64), sourceRegion: 'us' };
  const catalog = await request('inventory.catalog', catalogScope);
  assert.ok(!catalog.error, JSON.stringify(catalog.error));
  validate('FileCatalogStatus', catalog.result);
  assert.equal(catalog.result.state, 'notOpened', 'A build that was never opened has no file catalog and reading never queues one.');
  assert.equal((await request('inventory.catalog', { ...catalogScope, generation: '1' })).error?.code, 'INVALID_REQUEST');
  assert.equal((await request('inventory.catalog', { ...catalogScope, sourceRegion: 'US' })).error?.code, 'INVALID_REQUEST');
  const listed = await request('inventory.catalogs');
  assert.ok(!listed.error, JSON.stringify(listed.error)); validate('FileCatalogList', listed.result); assert.deepEqual(listed.result.catalogs, []);
  assert.equal((await request('inventory.catalog.set', { productId: 'wow', buildIdentity: 'a'.repeat(64), action: 'stop' })).error?.code, 'FILE_CATALOG_NOT_FOUND');
  assert.equal((await request('inventory.catalog.set', { productId: 'wow', buildIdentity: 'a'.repeat(64), action: 'prepare' })).error?.code, 'INVALID_REQUEST');
  const fileScope ={ productId: 'wow', buildIdentity: 'a'.repeat(64), sourceRegion: 'us', generation: '1', limit: 50 };
  const files = await request('inventory.files', { ...fileScope, localeFlags: '2', storedOnly: true });
  assert.equal(files.error?.code, 'INVENTORY_NOT_FOUND', 'A fresh catalog has no prepared inventory to list.');
  assert.equal((await request('inventory.file', { ...fileScope, file: 'root:1' })).error?.code, 'INVENTORY_NOT_FOUND');
  assert.equal((await request('inventory.files', { ...fileScope, storedOnly: 'yes' })).error?.code, 'INVALID_REQUEST');
  assert.equal((await request('inventory.files', { ...fileScope, platform: 'Windows' })).error?.code, 'INVALID_REQUEST');
  const jobs = await request('retention.list');
  assert.ok(!jobs.error, JSON.stringify(jobs.error));
  validate('RetentionList', jobs.result);
  assert.deepEqual(jobs.result.jobs, [], 'Local reads must not invent or start a background intent.');
  const products = await request('products.list');
  assert.ok(Array.isArray(products.result?.products));
  assert.ok(products.result.products.some(product => product.id === 'wow'));
  for (const product of products.result.products) validate('Product', product);
  // Every listed product fits every productId bound in the wire schema (#42), and the authored catalog example lists
  // exactly the registry, so desktop tests that check validators against that example cover every listed product.
  const productPatterns = new Set(), collect = node => { if (Array.isArray(node)) node.forEach(collect); else if (node && typeof node === 'object') for (const [key, value] of Object.entries(node)) { if (key === 'productId' && typeof value?.pattern === 'string') productPatterns.add(value.pattern); collect(value); } };
  collect(schema);
  assert.ok(productPatterns.size > 0);
  for (const { id } of products.result.products) for (const pattern of productPatterns) assert.match(id, new RegExp(pattern), `${id} fits ${pattern}`);
  const catalogExample = JSON.parse(await readFile(path.join(root, 'contracts/examples/discovery-catalog-page.json'), 'utf8'));
  assert.deepEqual(catalogExample.products.map(status => status.productId), products.result.products.map(product => product.id));
  const empty = await request('discovery.read', { productId: 'wow' });
  assert.ok(!empty.error, JSON.stringify(empty.error));
  validate('DiscoveryPage', empty.result);
  assert.deepEqual(empty.result.builds, []);
  assert.equal(empty.result.hasSavedHistory, false);
  assert.equal(empty.result.connection, 'notChecked');
  const allProducts = await request('discovery.readAll', {});
  assert.ok(!allProducts.error, JSON.stringify(allProducts.error));
  validate('DiscoveryCatalogPage', allProducts.result);
  assert.deepEqual(allProducts.result.builds, []);
  assert.equal(allProducts.result.productId, null);
  assert.deepEqual(allProducts.result.products.map(status => status.productId), products.result.products.map(product => product.id), 'every registered product reports a status in registry order');
  assert.ok(allProducts.result.products.every(status => status.connection === 'notChecked' && status.lastCheckedAt === null && !status.hasSavedHistory));
  const oneProduct = await request('discovery.readAll', { productId: 'wowt', filter: 'advertised', limit: 10 });
  validate('DiscoveryCatalogPage', oneProduct.result);
  assert.equal(oneProduct.result.productId, 'wowt'); assert.equal(oneProduct.result.filter, 'advertised');
  assert.equal((await request('discovery.readAll', { productId: '../not-a-product' })).error?.code, 'INVALID_PRODUCT');
  assert.equal((await request('discovery.refreshAll', { productId: 'wow' })).error?.code, 'INVALID_REQUEST', 'refreshAll checks every product and takes no product');
  const invalid = await request('discovery.read', { productId: '../not-a-product' });
  assert.ok(invalid.error, 'Invalid product must fail before any HTTP request.');
  const mismatched = await request('hello', {}, 99);
  assert.ok(mismatched.error, 'Incompatible protocol versions must be rejected.');
  const shutdown = await request('shutdown');
  assert.deepEqual(shutdown.result, { stopping: true });
  const [exitCode] = await exited;
  assert.equal(exitCode, 0, diagnostics);
});

test('authored effects phases preserve separate source roles, copy occurrences and raw input evidence',async()=>{
 const load=async n=>JSON.parse(await readFile(path.join(root,`contracts/examples/${n}.json`),'utf8'));
 const readiness=await load('item-effects-readiness'),page=await load('item-effects-page'),details=await load('item-effect-details'),names=await load('item-effect-names');
 for(const[type,value]of[['ItemEffectsReadiness',readiness],['ItemEffectsPage',page],['ItemEffectDetailsResult',details],['ItemEffectNamesResult',names]])validate(type,value);
 const bad=(type,value,change)=>{const v=structuredClone(value);change(v);assert.equal(validator(type)(v),false,`${type} must refuse changed authority`);};
 bad('ItemEffectsReadiness',readiness,v=>v.state='missingItem');bad('ItemEffectsReadiness',readiness,v=>v.source.role='effect');
 bad('ItemEffectsPage',page,v=>v.edges[0].bridge.isCopy=true);bad('ItemEffectsPage',page,v=>v.edges[0].bridge.sourceOrdinal=null);bad('ItemEffectsPage',page,v=>v.edges[0].parentItemId.columnIndex=1);bad('ItemEffectsPage',page,v=>v.observedAssociations=2);bad('ItemEffectsPage',page,v=>v.edges=Array(17).fill(v.edges[0]));
 validate('ItemEffectsPage',{...page,edges:[],observedAssociations:'0'});
 for(const change of[v=>v.source.snapshot.association.fileDataId='1990283',v=>v.source.snapshot.columns[7].reference.table='SpellName',v=>v.outcomes[0].fields[2].evidence='strictNumeric',v=>v.outcomes[0].fields[2].interpretation.candidateBits=8,v=>v.outcomes[0].fields[5].candidateReference.table='Spell',v=>v.outcomes[0].fields[8].evidence='strictNumeric',v=>v.outcomes[0].fields[0].value='256',v=>v.outcomes[0].fields[3].value='-2147483649'])bad('ItemEffectDetailsResult',details,change);
 bad('ItemEffectNamesResult',names,v=>v.sources.reverse());bad('ItemEffectNamesResult',names,v=>v.policyRevision='other');bad('ItemEffectNamesResult',names,v=>v.outcomes[0].text='x'.repeat(4097));bad('ItemEffectNamesResult',names,v=>v.outcomes[0].state='emptyText');bad('ItemEffectNamesResult',names,v=>v.outcomes[0].effect.state='stale');bad('ItemEffectNamesResult',names,v=>v.outcomes[0].target.tableId='4002a5b1');
 // UTF16 surrogate, equality/order/cursor digest and relational source-generation checks
 // are the shared/runtime matcher responsibility, not scalar JSON Schema length.
});
test('all seven effects requests have exact flat authority and bounded decimal edge keys',()=>{
 const common={productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',researchLocale:'enUS',itemsArtifactId:'b'.repeat(64),itemId:'0'};
 const methods=['items.effects.data','items.effects.prepare','items.effects.page','items.effects.details','items.effects.details.prepare','items.effects.names','items.effects.names.prepare'];
 for(const method of methods){const params={...common,...(!['items.effects.data','items.effects.prepare'].includes(method)?{bridgeIndexArtifactId:'c'.repeat(64)}:{}),...(method.includes('details')||method.includes('names')?{edges:[{indexOrdinal:'0'}]}:{}),...(method.includes('names')?{itemEffectArtifactId:'d'.repeat(64)}:{})};const request={protocolVersion:1,id:'effects',method,params};validate('EngineRequest',request);for(const change of[p=>p.itemId=0,p=>p.itemId='01',p=>p.itemsArtifactId=null,p=>p.effectId='0',p=>p.researchLocale='us']){const v=structuredClone(request);change(v.params);assert.equal(validator('EngineRequest')(v),false);}if(method.includes('names'))for(const policyRevision of[null,'other'])assert.equal(validator('EngineRequest')({...request,params:{...params,policyRevision}}),false);if(params.edges){assert.equal(validator('EngineRequest')({...request,params:{...params,edges:[{indexOrdinal:'65536'}]}}),false);assert.equal(validator('EngineRequest')({...request,params:{...params,edges:Array(17).fill({indexOrdinal:'0'})}}),false);}}
});
test('effects progress has explicit finite source roles and safeJS counters without a made-up denominator',()=>{
 const value={protocolVersion:1,type:'progress',requestId:'effects',sequence:9007199254740991,stage:'Reading rows',region:'us',unit:'rows',completed:1,total:null,tableId:'00cb674f'};validate('ItemEffectsEngineProgress',value);validate('EngineProgress',{...value,tableId:null});validate('ItemEffectsEngineProgress',{...value,completed:null,tableId:null});for(const patch of[{sequence:9007199254740992},{completed:9007199254740992},{total:9007199254740992},{tableId:'50238ec2'},{unit:null},{stage:'\nrows'}])assert.equal(validator('ItemEffectsEngineProgress')({...value,...patch}),false);const absent={...value};delete absent.tableId;assert.equal(validator('ItemEffectsEngineProgress')(absent),false);
});

test('late Effect unavailability preserves consulted Name metadata but never dependent name facts',async()=>{
 const template=JSON.parse(await readFile(path.join(root,'contracts/examples/item-effect-names.json'),'utf8')),problem={code:'ITEM_EFFECTS_SOURCE_UNAVAILABLE',message:'The selected source is unavailable.',retryable:true};
 for(const state of['ready','notPrepared','unavailable']){const v=structuredClone(template);v.sources[0]={role:'effect',state:'unavailable',snapshot:null,historicalArtifactId:null,problem};if(state!=='ready')v.sources[1]={role:'name',state,snapshot:null,historicalArtifactId:null,problem:state==='unavailable'?problem:null};for(const o of v.outcomes){o.effect={state:'unavailable',itemEffectId:'0',observedCandidates:null,target:null,spellId:null,absence:null,problem};Object.assign(o,{state:'effectNotReady',observedCandidates:null,target:null,text:null,issue:null,textUnits:null,absence:null,problem:null});}validate('ItemEffectNamesResult',v);v.outcomes[0].text='stale text';assert.equal(validator('ItemEffectNamesResult')(v),false);}
});

test('original invalid Name locator requires a positive declared encoding-issue count',async()=>{
 const v=JSON.parse(await readFile(path.join(root,'contracts/examples/item-effect-names.json'),'utf8')),o=v.outcomes[0];o.state='invalidText';o.text=null;o.issue={code:'DB2_TEXT_ENCODING_INVALID',source:{...o.target,columnIndex:1}};
 assert.equal(validator('ItemEffectNamesResult')(v),false);v.sources[1].snapshot.textIssueCount='1';validate('ItemEffectNamesResult',v);v.sources[1].snapshot.textIssueCount='0';assert.equal(validator('ItemEffectNamesResult')(v),false);delete v.sources[1].snapshot.textIssueCount;assert.equal(validator('ItemEffectNamesResult')(v),false);
});
