import { act, cleanup, configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TableCatalogEntry, TableCatalogPage, TableDataSnapshot, TableDefinitionSnapshot, TableFollowPage, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { TablesCatalogParams } from '../src/shared/tables';
import type { TableRowsParams } from '../src/shared/table-data';
import { followMatches, validTableFollow, type TableFollowParams } from '../src/shared/table-follow';
import { describeFollow, followLabel } from '../src/renderer/table-follow-view';
import { TablesExplorer } from '../src/renderer/TablesExplorer';
import { FilesUnavailable } from '../src/renderer/DownloadObjects';
import { FilesCatalog } from '../src/renderer/FilesCatalog';
import inventoryWorkspace from '../../../contracts/examples/inventory-workspace.json';
import example from '../../../contracts/examples/table-follow.json';
import itemFixture from '../../../tests/desktop/fixtures/table-data-partial.json';
import classFixture from '../../../tests/desktop/fixtures/table-data.json';
import tableFixture from '../../../tests/desktop/fixtures/table-definitions.json';
import workspaceFixture from '../../../tests/desktop/fixtures/download-workspace.json';

const item = itemFixture as TableDataSnapshot, itemClass = classFixture as TableDataSnapshot;
const definitions = tableFixture.definitions as unknown as Record<string, TableDefinitionSnapshot>;
const workspace = workspaceFixture as unknown as WorkspaceSnapshot;
const ok = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { resolve, promise }; }
configure({ asyncUtilTimeout: 5000 });

describe('follow reply validation', () => {
 const reply = example as TableFollowPage;
 const query: TableFollowParams = { productId: 'wow', buildIdentity: 'a'.repeat(64), sourceRegion: 'us', researchLocale: 'enUS', tableId: '50238ec2', artifactId: 'b'.repeat(64), columnIndex: 1, value: '7', targetTableId: 'b977271e', targetArtifactId: 'c'.repeat(64), limit: 50 };
 it('accepts the authored follow and refuses one for another value, table, page or state', () => {
  expect(validTableFollow(query)).toBe(true);
  expect(followMatches(reply, query)).toBe(true);
  const changed = (change: (value: TableFollowPage) => void) => { const value = structuredClone(reply); change(value); return followMatches(value, query); };
  expect(changed(v => { v.value = '8'; })).toBe(false);
  expect(changed(v => { v.page!.rows[1].values[2] = '8'; })).toBe(false); // A row that doesn't hold the value is not a match.
  expect(changed(v => { v.page!.artifactId = 'd'.repeat(64); })).toBe(false);
  expect(changed(v => { v.page!.totalCount = '1'; })).toBe(false);
  expect(changed(v => { v.state = 'keyOutOfRange'; })).toBe(false); // An out-of-range reply carries no page.
  expect(changed(v => { v.state = 'keyOutOfRange'; v.page = null; })).toBe(false); // 7 is inside -128..127.
  expect(changed(v => { v.state = 'keyOutOfRange'; v.page = null; v.value = '300'; })).toBe(false); // Not the value asked for.
  expect(followMatches({ ...reply, state: 'keyOutOfRange', page: null, value: '300' }, { ...query, value: '300' })).toBe(true);
  expect(changed(v => { v.after = '50'; })).toBe(false);
  expect(changed(v => { v.policyRevision = 'table-follow-v2' as never; })).toBe(false);
  for (const bad of [{ value: '07' }, { value: '-0' }, { value: 7 }, { columnIndex: 512 }, { targetTableId: 'ItemClass' }, { limit: 51 }, { after: '01' }])
   expect(validTableFollow({ ...query, ...bad })).toBe(false);
 });
 it('checks the declared reference and the scope against what the desktop shows', () => {
  const source = { ...item, artifactId: query.artifactId }, target = { ...itemClass, artifactId: query.targetArtifactId };
  const reply = structuredClone(example as TableFollowPage); for (const row of reply.page!.rows) row.values.push('0', '0'); // ItemClass has five columns.
  expect(followMatches(reply, query, source, target)).toBe(true);
  expect(followMatches(example as TableFollowPage, query, source, target)).toBe(false); // Rows shaped for another layout.
  expect(followMatches(reply, query, { ...source, columns: source.columns.map(c => ({ ...c, reference: null })) }, target)).toBe(false);
  expect(followMatches({ ...reply, scope: 'unencryptedSections' }, query, source, target)).toBe(false);
 });
 it('words several, none, partial, out-of-range and unindexed results apart', () => {
  const base = { reference: { table: 'ItemClass', column: 'ClassID' }, value: '7', targetIsId: false, targetRange: { minimum: '-128', maximum: '127' }, problem: null, scope: 'complete' as const, state: 'ready' as const };
  expect(describeFollow({ ...base, matchCount: '1' }, 'ItemClass')).toBeNull();
  // One readable row of a partly readable table may not be the only one, unless it is the record ID.
  expect(describeFollow({ ...base, matchCount: '1', scope: 'unencryptedSections' }, 'ItemClass')).toEqual({ tone: 'warn', title: '1 readable ItemClass row has ClassID = 7', detail: "Protected sections are not loaded and may hold more rows with ClassID = 7, so this row isn't picked as the match." });
  expect(describeFollow({ ...base, matchCount: '1', scope: 'unencryptedSections', targetIsId: true, reference: { table: 'Material', column: 'ID' } }, 'Material')).toBeNull();
  expect(describeFollow({ ...base, matchCount: '2' }, 'ItemClass')).toEqual({ tone: 'calm', title: '2 admitted rows have ClassID = 7', detail: "Choose one. ClassID isn't the record ID, so no row is picked." });
  expect(describeFollow({ ...base, matchCount: '0' }, 'ItemClass')!.title).toBe('No admitted ItemClass row has ClassID = 7');
  const partial = describeFollow({ ...base, matchCount: '0', scope: 'unencryptedSections' }, 'ItemClass')!;
  expect([partial.tone, partial.title, partial.detail]).toEqual(['warn', 'Not in the readable ItemClass rows', "No readable row has ClassID = 7. Protected sections are not loaded, so this isn't an absence."]);
  expect(describeFollow({ ...base, matchCount: '2', scope: 'unencryptedSections', targetIsId: true, reference: { table: 'Material', column: 'ID' } }, 'Material')!.detail).toBe('Choose one. Copies count as rows of their own, so no row is picked.');
  expect(describeFollow({ ...base, value: '300', state: 'keyOutOfRange', matchCount: null }, 'ItemClass')!.detail).toBe("ItemClass.ClassID holds -128 to 127. The value isn't wrapped or truncated, so nothing was searched.");
  expect(describeFollow({ ...base, state: 'unindexed', matchCount: null, problem: { code: 'TABLE_FOLLOW_UNINDEXED', message: 'Too many rows.' } }, 'ItemClass')!.detail).toBe("Too many rows. No row was picked, and this isn't an absence.");
  expect(followLabel({ kind: 'files' }, { table: 'FileData', column: 'ID' }, '-1')).toBeNull();
  expect(followLabel({ kind: 'none', reason: 'x' }, { table: 'ContentTuning', column: 'ID' }, '1')).toBeNull();
 });
});

// Item rows: ClassID cycles 12 (one ItemClass match), 7 (two), 5 (none) and 300 (out of range); every icon is FileDataID 800001.
const classOf = (i: number) => ['12', '7', '5', '300'][i % 4];
const itemRow = (i: number) => ({ id: String(i), isCopy: false, copySourceId: null, values: item.columns.map(c => c.isId ? String(i) : c.name === 'ClassID' ? classOf(i) : c.name === 'IconFileDataID' ? '800001' : c.family === 'string' ? 'Authored' : '1') });
const classRow = (id: number, classId: string) => ({ id: String(id), isCopy: false, copySourceId: null, values: itemClass.columns.map(c => c.isId ? String(id) : c.name === 'ClassID' ? classId : c.family === 'string' ? `Authored class ${id}` : '0') });
const matches: Record<string, ReturnType<typeof classRow>[]> = { '12': [classRow(312, '12')], '7': [classRow(307, '7'), classRow(308, '7')], '5': [] };
const entries: TableCatalogEntry[] = Object.values(definitions).map(value => value.entry);
function catalog(p: TablesCatalogParams): TableCatalogPage {
 const query = (p.query ?? '').toLowerCase(), filtered = entries.filter(e => !query || (e.name ?? '').toLowerCase().includes(query)), after = Number(p.after ?? 0), rows = filtered.slice(after, after + (p.limit ?? 50));
 return { ...p, provider: definitions['50238ec2'].provider, query: p.query ?? '', entries: rows, totalCount: String(filtered.length), nextCursor: after + rows.length < filtered.length ? String(after + rows.length) : null };
}
function followReply(p: TableFollowParams): TableFollowPage {
 const outOfRange = BigInt(p.value) > 127n || BigInt(p.value) < -128n, rows = matches[p.value] ?? [];
 return { productId: p.productId, buildIdentity: p.buildIdentity, sourceRegion: p.sourceRegion, researchLocale: 'enUS', policyRevision: 'table-follow-v1', tableId: p.tableId, artifactId: p.artifactId, columnIndex: p.columnIndex, column: 'ClassID', value: p.value,
  reference: { table: 'ItemClass', column: 'ClassID' }, targetTableId: p.targetTableId, targetArtifactId: p.targetArtifactId, targetColumnIndex: 2, targetIsId: false, targetRange: { minimum: '-128', maximum: '127' }, scope: 'complete',
  state: outOfRange ? 'keyOutOfRange' : 'ready', after: p.after ?? '0', problem: null,
  page: outOfRange ? null : { productId: p.productId, buildIdentity: p.buildIdentity, sourceRegion: p.sourceRegion, tableId: p.targetTableId, researchLocale: 'enUS', artifactId: p.targetArtifactId, rows, totalCount: String(rows.length), nextCursor: null } };
}
const partialCoverage = { scope: 'unencryptedSections' as const, declaredRecords: '70', physicalRows: '60', copyRows: '0', excludedPhysicalRecords: '10', declaredCopyRecords: '0', excludedCopyRecords: '0', excludedDeclaredRecords: '10', excludedKeyedSections: '1' };
// ItemClass.ClassID declared as a reference to ItemClass.ClassID: a Follow that stays in the same Data view, like Creature.MountCreatureID.
const selfReference = { table: 'ItemClass', column: 'ClassID' };
function selfDefinition(): TableDefinitionSnapshot { const d = structuredClone(definitions[itemClass.tableId]); d.definition!.columns = d.definition!.columns.map(f => f.name === 'ClassID' ? { ...f, reference: selfReference } : f); for (const c of d.definition!.candidates) c.fields = c.fields.map(f => f.name === 'ClassID' ? { ...f, reference: selfReference } : f); return d; }
function setup(options: { follow?: (p: TableFollowParams) => Promise<BridgeReply<TableFollowPage>>; capabilities?: string[]; partialClass?: boolean; selfReference?: boolean; materialFails?: boolean; classPrepare?: Promise<BridgeReply<TableDataSnapshot>> } = {}) {
 const classSnapshot: TableDataSnapshot = { ...itemClass, ...(options.partialClass ? { coverage: partialCoverage } : {}), columns: options.selfReference ? itemClass.columns.map(c => c.name === 'ClassID' ? { ...c, reference: selfReference } : c) : itemClass.columns };
 const classScope = classSnapshot.coverage.scope;
 const onOpenFile = vi.fn();
 const bridge = {
  onProgress: vi.fn(() => () => {}), tablePrepare: vi.fn((p: { tableId: string }) => options.classPrepare && p.tableId === itemClass.tableId ? options.classPrepare : options.materialFails && p.tableId === 'a4c11069' ? Promise.resolve({ ok: false, error: { code: 'TABLE_DATA_UNAVAILABLE', message: 'Authored Material failure.', retryable: true } }) : new Promise(() => {})), cancel: vi.fn(async () => ok({ requested: true })),
  capabilities: vi.fn(async () => ok(options.capabilities ?? ['tables-follow-v1'])),
  tableData: vi.fn(async (p: { tableId: string }) => ok({ snapshot: p.tableId === item.tableId ? item : p.tableId === itemClass.tableId && !options.classPrepare ? classSnapshot : null })),
  tableRows: vi.fn(async (p: TableRowsParams) => { const all = p.tableId === item.tableId ? Array.from({ length: 60 }, (_, i) => itemRow(i)) : Array.from({ length: 60 }, (_, i) => classRow(i, String(i))), filtered = p.recordId === undefined ? all : all.filter(r => r.id === p.recordId), after = Number(p.after ?? 0), rows = filtered.slice(after, after + (p.limit ?? 50)); return ok({ ...p, rows, totalCount: String(filtered.length), nextCursor: after + rows.length < filtered.length ? String(after + rows.length) : null }); }),
  tableFollow: vi.fn(options.follow ?? (async (p: TableFollowParams) => ok({ ...followReply(p), scope: classScope }))),
  tablesCatalog: vi.fn(async (p: TablesCatalogParams) => ok(catalog(p))),
  tableDefinition: vi.fn(async (p: { tableId: string }) => ok(options.selfReference && p.tableId === itemClass.tableId ? selfDefinition() : definitions[p.tableId])),
 } as unknown as DiscoveryBridge;
 render(<TablesExplorer snapshot={workspace} bridge={bridge} active onOpenFile={onOpenFile}/>);
 return { bridge, onOpenFile };
}
async function openItem(name = 'Item') {
 const list = await screen.findByRole('region', { name: 'Provider definitions on this page' });
 fireEvent.click(await within(list).findByRole('button', { name: new RegExp(`^${name}Definition bundled`) }));
 await screen.findByRole('button', { name: 'Inspect record 0' });
}
const inspect = (id: string) => screen.getByRole('button', { name: `Inspect record ${id}` });
const crumbs = () => [...document.querySelectorAll('.tables-explorer .crumb')].map(c => `${c.textContent}${c.getAttribute('aria-current') ? '*' : ''}`);
async function followFromInspector(record: string, label: string) {
 fireEvent.click(inspect(record));
 fireEvent.click(await screen.findByRole('button', { name: label }));
}
beforeEach(() => { vi.spyOn(window, 'scrollTo').mockImplementation(() => {}); Element.prototype.scrollIntoView = vi.fn(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('Follow in the Tables workbench', { timeout: 30000 }, () => {
 it('selects the one match and Back and Forward restore the table, page and selection', async () => {
  const { bridge } = setup(); await openItem();
  fireEvent.click(screen.getByRole('button', { name: 'Next page of records' }));
  await screen.findByRole('button', { name: 'Inspect record 52' });
  await followFromInspector('52', 'Show ItemClass rows where ClassID = 12');
  await waitFor(() => expect(inspect('312')).toHaveAttribute('aria-pressed', 'true'));
  expect(bridge.tableFollow).toHaveBeenCalledWith({ productId: item.productId, buildIdentity: item.buildIdentity, sourceRegion: item.sourceRegion, researchLocale: 'enUS', tableId: item.tableId, artifactId: item.artifactId, columnIndex: 1, value: '12', targetTableId: itemClass.tableId, targetArtifactId: itemClass.artifactId, limit: 50 }, expect.stringMatching(/^follow_/));
  expect(crumbs()).toEqual(['Item', 'ItemClass*']);
  expect(screen.getByRole('group', { name: 'Follow filter' })).toHaveTextContent('Followed from ItemClassID = 12');
  expect(await screen.findByRole('heading', { name: 'Record #312' })).toBeVisible();
  // No Record ID filter while a Follow filter applies.
  expect(screen.queryByRole('textbox', { name: 'Raw record ID (exact match)' })).not.toBeInTheDocument();

  const back = screen.getByRole('button', { name: 'Back through definitions' }); back.focus(); fireEvent.click(back);
  await waitFor(() => expect(inspect('52')).toHaveAttribute('aria-pressed', 'true'));
  expect(crumbs()).toEqual(['Item*', 'ItemClass']);
  expect(back).toHaveFocus();
  expect(screen.getByText(/^Rows 51–60 · 60 admitted rows/)).toBeVisible();
  expect(await screen.findByRole('heading', { name: 'Record #52' })).toBeVisible();

  const forward = screen.getByRole('button', { name: 'Forward through definitions' }); forward.focus(); fireEvent.click(forward);
  await waitFor(() => expect(inspect('312')).toHaveAttribute('aria-pressed', 'true'));
  expect(crumbs()).toEqual(['Item', 'ItemClass*']);
  expect(forward).toHaveFocus();
  const crumb = document.querySelectorAll<HTMLElement>('.tables-explorer .crumb')[0]; crumb.focus(); fireEvent.click(crumb);
  await waitFor(() => expect(inspect('52')).toHaveAttribute('aria-pressed', 'true'));
  expect(crumb).toHaveFocus();
  // One row button is a Tab stop: the selected one. Arrow keys move between rows.
  expect([...document.querySelectorAll('.dh-data:not([hidden]) .table-records .dh-rowsel')].filter(b => b.getAttribute('tabindex') === '0').map(b => b.textContent)).toEqual(['52']);
 });
 it('lists several matches without picking one and says when nothing matched', async () => {
  setup(); await openItem();
  await followFromInspector('1', 'Show ItemClass rows where ClassID = 7');
  expect(await screen.findByText('2 admitted rows have ClassID = 7')).toBeVisible();
  expect(inspect('307')).toHaveAttribute('aria-pressed', 'false'); expect(inspect('308')).toHaveAttribute('aria-pressed', 'false');
  expect(screen.getByText(/^Rows 1–2 · 2 matching rows · ClassID = 7/)).toBeVisible();

  fireEvent.click(screen.getByRole('button', { name: 'Back through definitions' }));
  await waitFor(() => expect(inspect('1')).toHaveAttribute('aria-pressed', 'true'));
  await followFromInspector('2', 'Show ItemClass rows where ClassID = 5');
  expect(await screen.findByText('No admitted ItemClass row has ClassID = 5')).toBeVisible();
  expect(screen.getByText('Complete admitted section scope for enUS. Nothing else was searched or fetched.')).toBeVisible();
  expect(screen.queryByText(/^No admitted row/)).not.toBeInTheDocument();
  expect(crumbs()).toEqual(['Item', 'ItemClass*']); // Following from Item again replaced the forward entry.

  fireEvent.click(screen.getByRole('button', { name: 'Back through definitions' }));
  await waitFor(() => expect(inspect('2')).toHaveAttribute('aria-pressed', 'true'));
  await followFromInspector('3', 'Show ItemClass rows where ClassID = 300');
  expect(await screen.findByText('No ItemClass row can have ClassID = 300')).toBeVisible();
 });
 it('follows from a reference cell by mouse and leaves undeclared or unbundled targets plain', async () => {
  const { bridge } = setup(); await openItem();
  const row = inspect('1').closest('tr')!, cells = row.querySelectorAll('td');
  const at = (name: string) => cells[item.columns.findIndex(c => c.name === name)] as HTMLElement;
  await waitFor(() => expect(at('ClassID')).toHaveClass('dh-follow'));
  expect(at('ClassID')).toHaveAttribute('title', expect.stringContaining('Show ItemClass rows where ClassID = 7'));
  expect(at('Material')).toHaveClass('dh-follow');
  expect(at('ContentTuningID')).toHaveClass('dh-refcell'); expect(at('ContentTuningID')).not.toHaveClass('dh-follow');
  expect(at('SubclassID')).not.toHaveClass('dh-refcell');
  fireEvent.click(at('ClassID'));
  await waitFor(() => expect(bridge.tableFollow).toHaveBeenCalledWith(expect.objectContaining({ value: '7', columnIndex: 1 }), expect.any(String)));
  expect(await screen.findByText('2 admitted rows have ClassID = 7')).toBeVisible();
 });
 it('drops a lookup that is still running when the user goes Back, and cancels it', async () => {
  const pending = deferred<BridgeReply<TableFollowPage>>(); let asked: TableFollowParams | null = null;
  const { bridge } = setup({ follow: p => { asked = p; return pending.promise; } }); await openItem();
  await followFromInspector('0', 'Show ItemClass rows where ClassID = 12');
  await waitFor(() => expect(bridge.tableFollow).toHaveBeenCalled());
  const requestId = vi.mocked(bridge.tableFollow!).mock.calls[0][1];
  fireEvent.click(screen.getByRole('button', { name: 'Back through definitions' }));
  await waitFor(() => expect(bridge.cancel).toHaveBeenCalledWith(requestId));
  await act(async () => pending.resolve(ok(followReply(asked!))));
  expect(crumbs()).toEqual(['Item*', 'ItemClass']);
  expect(screen.queryByRole('button', { name: 'Inspect record 312' })).not.toBeInTheDocument();
  expect(inspect('0')).toHaveAttribute('aria-pressed', 'true');
 });
 it('clears the Follow filter in place and focuses the Record ID filter', async () => {
  setup(); await openItem();
  await followFromInspector('1', 'Show ItemClass rows where ClassID = 7');
  const clear = await screen.findByRole('button', { name: 'Clear filter ClassID = 7' }); clear.focus(); fireEvent.click(clear);
  await screen.findByRole('button', { name: 'Inspect record 49' });
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Raw record ID (exact match)' })).toHaveFocus());
  expect(screen.queryByRole('group', { name: 'Follow filter' })).not.toBeInTheDocument();
  expect(crumbs()).toEqual(['Item', 'ItemClass*']);
 });
 it('hands a FileDataID to Files with the record to return to', async () => {
  const { onOpenFile, bridge } = setup(); await openItem();
  await followFromInspector('4', 'Find FileDataID 800001 in Files');
  expect(onOpenFile).toHaveBeenCalledWith({ fileDataId: '800001', table: 'Item', recordId: '4' });
  expect(bridge.tableFollow).not.toHaveBeenCalled();
 });
 it('keeps definition navigation only when the engine cannot follow by value', async () => {
  const { bridge } = setup({ capabilities: [] }); await openItem();
  fireEvent.click(inspect('1'));
  await waitFor(() => expect(bridge.capabilities).toHaveBeenCalled());
  await screen.findByRole('heading', { name: 'Record #1' });
  await waitFor(() => expect(document.querySelector('.dh-inspector')!.textContent).toContain('This engine can’t follow references by value.'));
  expect(screen.queryByRole('button', { name: /^Show ItemClass rows/ })).not.toBeInTheDocument();
 });
});

describe('Files hand-off', () => {
 it('searches the prepared catalog for the followed FileDataID with no other filter, and returns to the exact record', async () => {
  const back = vi.fn(), prepared = inventoryWorkspace as unknown as Parameters<typeof FilesCatalog>[0]['snapshot'];
  const bridge = { inventoryFiles: vi.fn(async () => ({ ok: false, error: { code: 'LOCAL_READ_FAILED', message: 'Authored.', retryable: true } })), inventoryFile: vi.fn() } as unknown as DiscoveryBridge;
  render(<FilesCatalog snapshot={prepared} bridge={bridge} active onReopen={() => {}} fileTarget={{ fileDataId: '800001', table: 'Item', recordId: '900001', seq: 1 }} onReturnToTable={back}/>);
  await waitFor(() => expect(bridge.inventoryFiles).toHaveBeenLastCalledWith({ productId: prepared.build.productId, buildIdentity: prepared.build.id, sourceRegion: prepared.sourceReference.region, generation: prepared.inventory.generation, limit: 50, fileDataId: '800001' }));
  expect(screen.getByRole('searchbox', { name: 'Search by FileDataID or content key' })).toHaveValue('800001');
  expect(screen.queryByText(/can’t be looked up yet/)).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: '← Return to Item record 900001' }));
  expect(back).toHaveBeenCalledTimes(1);
 });
 it('says a FileDataID can’t be looked up when no file catalog is prepared', () => {
  const back = vi.fn();
  render(<FilesUnavailable snapshot={workspace} active fileTarget={{ fileDataId: '800001', table: 'Item', recordId: '900001', seq: 1 }} onReturnToTable={back}/>);
  expect(screen.getByText('FileDataID 800001 can’t be looked up yet')).toBeVisible();
  expect(screen.getByText(/^Followed from Item record 900001\. The file catalog isn’t prepared/)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: '← Return to Item record 900001' }));
  expect(back).toHaveBeenCalledTimes(1);
 });
});

describe('Follow outcomes that need care', { timeout: 30000 }, () => {
 it('a failed Follow in the same Data view drops the old rows and keeps the earlier entry intact for Back', async () => {
  let fail = true;
  const { bridge } = setup({ selfReference: true, follow: async p => fail ? { ok: false, error: { code: 'TABLE_ARTIFACT_STALE', message: 'Authored stale source.', retryable: false } } : ok({ ...followReply(p), page: { ...followReply(p).page!, rows: [classRow(4, '54')], totalCount: '1' } }) });
  await openItem('ItemClass');
  fireEvent.click(screen.getByRole('button', { name: 'Next page of records' }));
  await screen.findByRole('button', { name: 'Inspect record 54' });
  await followFromInspector('54', 'Show ItemClass rows where ClassID = 54');
  expect(await screen.findByText('TABLE_ARTIFACT_STALE')).toBeVisible();
  expect(crumbs()).toEqual(['ItemClass', 'ItemClass*']);
  // None of the earlier entry's rows stay under the new chip, and the note says nothing was searched.
  expect(screen.getByRole('button', { name: 'Clear filter ClassID = 54' })).toBeVisible();
  expect(screen.queryByRole('button', { name: /^Inspect record / })).not.toBeInTheDocument();
  expect(screen.getByText('No search ran')).toBeVisible();
  expect(screen.getByText(/^The lookup didn’t complete, so nothing is claimed about ClassID = 54\./)).toBeVisible();

  fireEvent.click(screen.getByRole('button', { name: 'Back through definitions' }));
  await waitFor(() => expect(inspect('54')).toHaveAttribute('aria-pressed', 'true'));
  expect(screen.getByText(/^Rows 51–60 · 60 admitted rows/)).toBeVisible();
  expect(screen.queryByRole('group', { name: 'Follow filter' })).not.toBeInTheDocument();

  fail = false; vi.mocked(bridge.tableFollow!).mockClear();
  fireEvent.click(screen.getByRole('button', { name: 'Forward through definitions' }));
  await waitFor(() => expect(inspect('4')).toBeInTheDocument());
  // The Follow starts from its own first page, not the earlier entry's cursor.
  expect(vi.mocked(bridge.tableFollow!).mock.calls.map(([p]) => p.after)).toEqual([undefined]);
  expect(screen.getByText(/^Rows 1–1 · 1 matching row · ClassID = 54/)).toBeVisible();
 });
 it('clears the previous rows as soon as a new Follow starts', async () => {
  const later = deferred<BridgeReply<TableFollowPage>>(); let calls = 0;
  setup({ follow: async p => ++calls === 1 ? ok(followReply(p)) : later.promise }); await openItem();
  await followFromInspector('0', 'Show ItemClass rows where ClassID = 12');
  await waitFor(() => expect(inspect('312')).toHaveAttribute('aria-pressed', 'true'));
  fireEvent.click(screen.getByRole('button', { name: 'Back through definitions' }));
  await waitFor(() => expect(inspect('0')).toHaveAttribute('aria-pressed', 'true'));
  await followFromInspector('1', 'Show ItemClass rows where ClassID = 7');
  expect(await screen.findByRole('button', { name: 'Clear filter ClassID = 7' })).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Inspect record 312' })).not.toBeInTheDocument();
  expect(screen.getByText('Looking up ItemClass rows where ClassID = 7…')).toBeVisible();
  expect(screen.getByText(/^Looking up ClassID = 7…/)).toBeVisible();
  expect(screen.queryByText(/Previous rows remain visible/)).not.toBeInTheDocument();
  await act(async () => later.resolve(ok(followReply({ productId: item.productId, buildIdentity: item.buildIdentity, sourceRegion: item.sourceRegion, researchLocale: 'enUS', tableId: item.tableId, artifactId: item.artifactId, columnIndex: 1, value: '7', targetTableId: itemClass.tableId, targetArtifactId: itemClass.artifactId, limit: 50 }))));
  expect(await screen.findByText('2 admitted rows have ClassID = 7')).toBeVisible();
 });
 it('keeps the chip and says no search ran when the target table can’t be loaded', async () => {
  setup({ materialFails: true }); await openItem();
  await followFromInspector('1', 'Show Material rows where ID = 1');
  expect(await screen.findByRole('button', { name: 'Clear filter ID = 1' })).toBeVisible();
  expect(await screen.findByText('No search ran')).toBeVisible();
  expect(screen.getByText(/^Material rows aren’t available, so nothing is claimed about ID = 1\./)).toBeVisible();
 });
 it('does not pick the one readable match of a partly readable table', async () => {
  setup({ partialClass: true }); await openItem();
  await followFromInspector('0', 'Show ItemClass rows where ClassID = 12');
  expect(await screen.findByText('1 readable ItemClass row has ClassID = 12')).toBeVisible();
  expect(inspect('312')).toHaveAttribute('aria-pressed', 'false');
 });
 it('does not run a Follow while its view is hidden; Forward runs it once from the first page', async () => {
  const prepared = deferred<BridgeReply<TableDataSnapshot>>();
  const { bridge } = setup({ classPrepare: prepared.promise }); await openItem();
  await followFromInspector('0', 'Show ItemClass rows where ClassID = 12');
  await waitFor(() => expect(bridge.tablePrepare).toHaveBeenCalledWith(expect.objectContaining({ tableId: itemClass.tableId }), expect.any(String)));
  fireEvent.click(screen.getByRole('button', { name: 'Back through definitions' }));
  await waitFor(() => expect(inspect('0')).toHaveAttribute('aria-pressed', 'true'));
  // ItemClass is admitted while Item is shown.
  await act(async () => prepared.resolve(ok(itemClass)));
  await act(async () => { await new Promise(done => setTimeout(done, 50)); });
  expect(bridge.tableFollow).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Forward through definitions' }));
  await waitFor(() => expect(inspect('312')).toHaveAttribute('aria-pressed', 'true'));
  expect(vi.mocked(bridge.tableFollow!).mock.calls.map(([p]) => p.after)).toEqual([undefined]);
 });
 it('after a failed lookup the foot says so, Retry selects the one match and focuses it, and nothing claims old rows', async () => {
  let fail = true;
  setup({ follow: async p => { if (fail) throw new Error('Authored IPC failure.'); return ok(followReply(p)); } }); await openItem();
  await followFromInspector('0', 'Show ItemClass rows where ClassID = 12');
  expect(await screen.findByText('ENGINE_UNAVAILABLE')).toBeVisible();
  expect(screen.getByText('The followed rows could not be read.')).toBeVisible();
  expect(screen.queryByText(/Previous rows are preserved/)).not.toBeInTheDocument();
  expect(screen.getByText('Lookup failed · ClassID = 12 · nothing searched')).toBeVisible();
  expect(screen.getByText('No search ran')).toBeVisible();
  fail = false;
  const retry = screen.getByRole('button', { name: 'Retry table read' }); retry.focus(); fireEvent.click(retry);
  await waitFor(() => expect(inspect('312')).toHaveAttribute('aria-pressed', 'true'));
  expect(inspect('312')).toHaveFocus();
  expect(screen.queryByText('No search ran')).not.toBeInTheDocument();
  expect(screen.getByText(/^Rows 1–1 · 1 matching row · ClassID = 12/)).toBeVisible();
 });
});
