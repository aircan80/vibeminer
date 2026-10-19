import partialTableFixture from '../../../tests/desktop/fixtures/table-data-partial.json';
import tableDataFixture from '../../../tests/desktop/fixtures/table-data.json';
import type {TableDataSnapshot} from '../../../contracts/generated/discovery';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TableCatalogEntry, TableCatalogPage, TableDefinitionSnapshot, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { TablesCatalogParams } from '../src/shared/tables';
import { TablesExplorer } from '../src/renderer/TablesExplorer';
import tableFixture from '../../../tests/desktop/fixtures/table-definitions.json';
import workspaceFixture from '../../../tests/desktop/fixtures/download-workspace.json';
const definitions = tableFixture.definitions as unknown as Record<string, TableDefinitionSnapshot>;
const workspace = workspaceFixture as unknown as WorkspaceSnapshot;
const success = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const entries: TableCatalogEntry[] = [...Object.values(definitions).map(value => value.entry), { tableId: 'd2ee2ca7', name: 'Achievement', db2FileDataId: '1260179', definitionState: 'notBundled', sourceState: 'notChecked', rowsState: 'notPrepared' }, { tableId: 'deadbeef', name: null, db2FileDataId: null, definitionState: 'notBundled', sourceState: 'notChecked', rowsState: 'notPrepared' }];
const provider = definitions['50238ec2'].provider;
function catalog(p: TablesCatalogParams): TableCatalogPage {
  const query = p.query ?? '', lower = query.toLowerCase();
  const filtered = entries.filter(entry => !query || [entry.name, entry.tableId, entry.db2FileDataId, ...(definitions[entry.tableId]?.definition?.columns.map(field => field.name) ?? [])].some(value => value?.toLowerCase().includes(lower)));
  const after = Number(p.after ?? 0), rows = filtered.slice(after, after + (p.limit ?? 50)), end = after + rows.length;
  return { ...p, provider, query, entries: rows, totalCount: String(filtered.length), nextCursor: end < filtered.length ? String(end) : null };
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { resolve, promise }; }
function setup(active = true) {
  const bridge = {
    onProgress: vi.fn(() => () => {}), tablePrepare:vi.fn(()=>new Promise(()=>{})), tableData: vi.fn(async () => success({snapshot:null})),
    tablesCatalog: vi.fn(async (p: TablesCatalogParams) => success(catalog(p))),
    tableDefinition: vi.fn(async (p: { tableId: string }) => success(definitions[p.tableId] ?? { ...definitions['50238ec2'], entry: entries.find(entry => entry.tableId === p.tableId)!, definition: null, problem: { code: 'DEFINITION_NOT_BUNDLED', message: 'This definition is not bundled.' } }))
  } as unknown as DiscoveryBridge;
  const mounted = render(<TablesExplorer snapshot={workspace} bridge={bridge} active={active}/>);
  return { bridge, ...mounted };
}
async function select(name = 'Item') {
  const catalogue = await screen.findByRole('region', { name: 'Provider definitions on this page' });
  const button = await within(catalogue).findByRole('button', { name: new RegExp(`^${name}Definition bundled`) }); fireEvent.click(button); await screen.findByRole('heading', { level: 2, name }); fireEvent.click(screen.getByRole('button',{name:'Structure'})); return button;
}
beforeEach(() => { vi.spyOn(window, 'scrollTo').mockImplementation(() => {}); Element.prototype.scrollIntoView = vi.fn(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
describe('provider definition exploration without readable records', () => {
  it('loads attributed provider metadata only on area entry and never invents records or a default table selection', async () => {
    const { bridge, rerender } = setup(false); expect(bridge.tablesCatalog).not.toHaveBeenCalled();
    rerender(<TablesExplorer snapshot={workspace} bridge={bridge} active/>); await screen.findByText('6 provider entries on this page · 6 matching');
    expect(screen.getByRole('heading', { name: 'Tables' })).toHaveFocus(); expect(screen.getByRole('heading', { name: 'Select a table definition' })).toBeVisible(); expect(bridge.tableDefinition).not.toHaveBeenCalled(); expect(screen.getByText('Definitions and table records are separate.')).toBeVisible(); expect(screen.queryByRole('grid')).not.toBeInTheDocument();
    await select(); expect(screen.getByText(provider.revision)).toBeVisible(); expect(screen.getByText('CC-BY-SA-4.0')).toBeVisible(); expect(screen.getByText(/Candidate 996192AA explicitly matches selected build/)).toBeVisible();
  });
  it('inspects width, noninline ID, unverified names and plain arrays without moving field focus or inventing edges', async () => {
    setup(); await select(); const id = screen.getByRole('button', { name: 'Inspect field ID' }); id.focus(); fireEvent.click(id); expect(id).toHaveFocus(); expect(screen.getByText('$noninline$')).toBeVisible(); expect(screen.getByText('$id$ declaration')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Inspect field Field_12_0_0_63534_010' })); expect(screen.getByText('Unverified name (?) · not a nullable marker')).toBeVisible();
    await select('ItemGroupSounds'); const sound = screen.getByRole('button', { name: 'Inspect field Sound' }); sound.focus(); fireEvent.click(sound); expect(sound).toHaveFocus(); expect(screen.queryByRole('button', { name: /Open SoundKit/ })).not.toBeInTheDocument(); expect(screen.getByText('Array length').nextSibling).toHaveTextContent('4');
  });
  it('keeps search focused and preserves an exact definition outside empty filtered results', async () => {
    setup(); await select(); const field = screen.getByRole('button', { name: 'Inspect field ClassID' }); fireEvent.click(field); const search = screen.getByRole('searchbox', { name: 'Search definitions' }); search.focus(); fireEvent.change(search, { target: { value: 'zz-no-definition' } });
    await screen.findByText('0 provider entries on this page · 0 matching'); expect(search).toHaveFocus(); expect(screen.getByText(/Selected definition is outside this catalogue page/)).toBeVisible(); expect(screen.getByRole('heading', { level: 2, name: 'Item' })).toBeVisible(); expect(field).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Clear search' })); await screen.findByText('6 provider entries on this page · 6 matching'); expect(field).toHaveAttribute('aria-pressed', 'true');
  });
  it('follows Item.ClassID to ItemClass.ClassID definition and restores the exact Relationships action after a held read', async () => {
    const { bridge } = setup(); const pending = deferred<BridgeReply<TableDefinitionSnapshot>>(); vi.mocked(bridge.tableDefinition).mockImplementation(p => p.tableId === 'b977271e' ? pending.promise : Promise.resolve(success(definitions[p.tableId])));
    await select(); fireEvent.click(screen.getByRole('button', { name: 'Relationships' })); const relation = within(document.querySelector('.declared-relations') as HTMLElement).getByRole('button', { name: 'Open ItemClass.ClassID definition' }); relation.focus(); fireEvent.click(relation); await waitFor(() => expect(bridge.tableDefinition).toHaveBeenCalledWith({ productId: 'wow', buildIdentity: workspace.build.id, sourceRegion: 'us', tableId: 'b977271e' }));
    expect(screen.getByRole('heading', { level: 2, name: 'Item' })).toBeVisible(); await act(async () => pending.resolve(success(definitions['b977271e']))); expect(screen.getByRole('button', { name: 'Inspect field ClassID' })).toHaveAttribute('aria-pressed', 'true'); expect(screen.getByRole('button', { name: 'Inspect field ID' })).toHaveAttribute('aria-pressed', 'false');
    fireEvent.click(screen.getByRole('button', { name: 'Back through definitions' })); expect(screen.getByRole('button', { name: 'Relationships' })).toHaveAttribute('aria-pressed', 'true'); const restored = within(document.querySelector('.declared-relations') as HTMLElement).getByRole('button', { name: 'Open ItemClass.ClassID definition' }); expect(restored).toHaveFocus();
    fireEvent.click(screen.getByRole('button', { name: 'Forward through definitions' })); expect(screen.getByRole('heading', { level: 2, name: 'ItemClass' })).toBeVisible();
    await select('Material'); expect(screen.getByRole('button', { name: 'Back through definitions' })).toHaveAttribute('aria-disabled', 'true'); expect(document.querySelectorAll('.definition-trail .crumb')).toHaveLength(1);
  });
  it('retains known catalogue rows and selected definition after a page failure and retries the exact cursor', async () => {
    const { bridge, rerender } = setup(false); vi.mocked(bridge.tablesCatalog).mockResolvedValueOnce(success({ ...catalog({ productId: 'wow', buildIdentity: workspace.build.id, sourceRegion: 'us' }), entries: [entries[0]], totalCount: '2', nextCursor: '1' })).mockResolvedValueOnce({ ok: false, error: { code: 'LOCAL_READ_FAILED', message: 'Authored provider read failure.', retryable: true } }).mockResolvedValueOnce(success({ ...catalog({ productId: 'wow', buildIdentity: workspace.build.id, sourceRegion: 'us' }), entries: [entries[1]], totalCount: '2', nextCursor: null }));
    rerender(<TablesExplorer snapshot={workspace} bridge={bridge} active/>); await select(); const next = screen.getByRole('button', { name: 'Next page' }); next.focus(); fireEvent.click(next); await screen.findByText('LOCAL_READ_FAILED'); expect(screen.getByRole('heading', { level: 2, name: 'Item' })).toBeVisible(); expect(next).toHaveFocus();
    const retry = screen.getByRole('button', { name: 'Retry catalogue' }); retry.focus(); fireEvent.click(retry); await screen.findByText('1 provider entries on this page · 2 matching'); await waitFor(() => expect(screen.queryByText('LOCAL_READ_FAILED')).not.toBeInTheDocument()); expect(bridge.tablesCatalog).toHaveBeenLastCalledWith({ productId: 'wow', buildIdentity: workspace.build.id, sourceRegion: 'us', query: '', limit: 50, after: '1' }); expect(screen.getByRole('heading', { level: 2, name: 'Item' })).toBeVisible(); expect(screen.getByRole('searchbox', { name: 'Search definitions' })).toHaveFocus();
  });
  it('keeps unbundled and no-exact-build interpretations separate from missing records', async () => {
    const { bridge } = setup(); await select(); fireEvent.click(screen.getByRole('button', { name: 'Data' })); expect(await screen.findByRole('heading',{name:'Opening table data…'})).toBeVisible(); expect(screen.queryByText(/0 records/)).not.toBeInTheDocument();
    const unbundled = within(screen.getByRole('region', { name: 'Provider definitions on this page' })).getByRole('button', { name: /^AchievementDefinition not bundled/ }); fireEvent.click(unbundled); await screen.findByText('DEFINITION_NOT_BUNDLED'); expect(screen.getByRole('heading', { level: 2, name: 'Achievement' })).toBeVisible();
    vi.mocked(bridge.tableDefinition).mockResolvedValue(success({ ...definitions['50238ec2'], definition: { ...definitions['50238ec2'].definition!, candidates: [] }, problem: { code: 'SCHEMA_BUILD_NOT_LISTED', message: 'The exact build is not listed.' } })); await select(); expect(screen.getByText('SCHEMA_BUILD_NOT_LISTED')).toBeVisible(); expect(screen.getByText(/Universal COLUMNS declarations/)).toBeVisible(); expect(screen.queryByText(/Candidate 996192AA explicitly/)).not.toBeInTheDocument();
  });
  it('drops superseded and area-detached definition replies without selecting or stealing focus', async () => {
    const { bridge, rerender } = setup(); const first = deferred<BridgeReply<TableDefinitionSnapshot>>(); vi.mocked(bridge.tableDefinition).mockImplementation(p => p.tableId === '50238ec2' ? first.promise : Promise.resolve(success(definitions[p.tableId])));
    const list = await screen.findByRole('region', { name: 'Provider definitions on this page' }); fireEvent.click(await within(list).findByRole('button', { name: /^ItemDefinition bundled/ })); await select('Material'); await act(async () => first.resolve(success(definitions['50238ec2']))); expect(screen.getByRole('heading', { level: 2, name: 'Material' })).toBeVisible();
    const second = deferred<BridgeReply<TableDefinitionSnapshot>>(); vi.mocked(bridge.tableDefinition).mockReturnValue(second.promise); fireEvent.click(within(list).getByRole('button', { name: /^ItemDefinition bundled/ })); rerender(<TablesExplorer snapshot={workspace} bridge={bridge} active={false}/>); await act(async () => second.resolve(success(definitions['50238ec2']))); rerender(<TablesExplorer snapshot={workspace} bridge={bridge} active/>); expect(screen.getByRole('heading', { level: 2, name: 'Material' })).toBeVisible(); expect(screen.getByRole('heading', { name: 'Tables' })).toHaveFocus();
  });
  it('rejects a changed provider or build-version result while preserving the prior definition', async () => {
    const { bridge } = setup(); await select(); vi.mocked(bridge.tableDefinition).mockResolvedValue(success({ ...definitions['a4c11069'], buildVersion: '12.1.5.70449' })); const list = screen.getByRole('region', { name: 'Provider definitions on this page' }); fireEvent.click(within(list).getByRole('button', { name: /^MaterialDefinition bundled/ })); await screen.findByText('DEFINITION_CONTEXT_MISMATCH'); expect(screen.getByRole('heading', { level: 2, name: 'Item' })).toBeVisible();
  });
  it('preserves a good definition after a failed local read and retries the requested definition explicitly', async () => {
    const { bridge } = setup(); await select(); vi.mocked(bridge.tableDefinition).mockRejectedValueOnce(new Error('Authored local failure.')); const list = screen.getByRole('region', { name: 'Provider definitions on this page' }); fireEvent.click(within(list).getByRole('button', { name: /^MaterialDefinition bundled/ })); await screen.findByText('ENGINE_UNAVAILABLE'); expect(screen.getByRole('heading', { level: 2, name: 'Item' })).toBeVisible(); await select('Material'); expect(screen.queryByText('ENGINE_UNAVAILABLE')).not.toBeInTheDocument();
  });
  it('requires an explicit layout choice when multiple exact-build candidates exist and keeps data unavailable', async () => {
    const { bridge } = setup(); const value = structuredClone(definitions['b977271e']); value.definition!.candidates = [value.definition!.candidates[0]!, { ...value.definition!.candidates[0]!, layoutHash: '1234ABCD' }]; value.problem = { code: 'SCHEMA_LAYOUT_UNVERIFIED', message: 'Multiple candidate layouts; actual header not checked.' }; vi.mocked(bridge.tableDefinition).mockResolvedValue(success(value));
    await select('ItemClass'); expect(screen.getByText(/Universal COLUMNS declarations/)).toBeVisible(); fireEvent.change(screen.getByRole('combobox', { name: 'Candidate layout' }), { target: { value: '35680EB8' } }); expect(screen.getByText(/Candidate 35680EB8 explicitly matches selected build/)).toBeVisible(); fireEvent.click(screen.getByRole('button', { name: 'Inspect field ClassID' })); expect(screen.getByText('8 bits · signed')).toBeVisible(); expect(screen.getByText('Definitions and table records are separate.')).toBeVisible();
  });
  it('bounds an explicitly authored cyclic definition trail to 64 entries', async () => {
    const { bridge } = setup(); const value = structuredClone(definitions['b977271e']);
    for (const fields of [value.definition!.columns, value.definition!.candidates[0]!.fields]) fields.find(field => field.name === 'ClassID')!.reference = { table: 'ItemClass', column: 'ClassID' };
    vi.mocked(bridge.tableDefinition).mockResolvedValue(success(value)); await select('ItemClass'); fireEvent.click(screen.getByRole('button', { name: 'Inspect field ClassID' }));
    for (let index = 0; index < 65; index++) await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Open ItemClass.ClassID definition' })); });
    expect(document.querySelectorAll('.definition-trail .crumb')).toHaveLength(64); expect(screen.getByText(/most recent 64 entries/)).toBeVisible(); expect(bridge.tableDefinition).toHaveBeenCalledTimes(66); expect(screen.getByRole('button', { name: 'Forward through definitions' })).toHaveAttribute('aria-disabled', 'true');
  }, 15_000);

  it('clears a failed search intent without leaving a retry that can replace the matching visible query', async () => {
    const { bridge } = setup(); await select(); vi.mocked(bridge.tablesCatalog).mockImplementation(async p => p.query === 'Item' ? { ok: false, error: { code: 'LOCAL_READ_FAILED', message: 'Authored failed search.', retryable: true } } : success(catalog(p)));
    const search = screen.getByRole('searchbox', { name: 'Search definitions' }); search.focus(); fireEvent.change(search, { target: { value: 'Item' } }); await screen.findByText('LOCAL_READ_FAILED'); expect(screen.getByText(/Showing previous catalogue results/)).toBeVisible(); fireEvent.change(search, { target: { value: '' } }); expect(search).toHaveFocus(); expect(search).toHaveValue(''); expect(screen.queryByRole('button', { name: 'Retry catalogue' })).not.toBeInTheDocument(); expect(screen.queryByText('LOCAL_READ_FAILED')).not.toBeInTheDocument(); expect(screen.getByText('6 provider entries on this page · 6 matching')).toBeVisible(); expect(bridge.tablesCatalog).toHaveBeenCalledTimes(2);
  });

});

it('gives admitted data precedence over provider-only readiness without changing definition evidence',async()=>{const {bridge}=setup();vi.mocked(bridge.tableData).mockResolvedValue(success({snapshot:tableDataFixture as TableDataSnapshot}));bridge.tableRows=vi.fn(async p=>success({...p,rows:[],totalCount:'0',nextCursor:null}));await select('ItemClass');fireEvent.click(screen.getByRole('button',{name:'Data'}));await screen.findByText('Complete admitted section scope');expect(screen.getByText('Verified / 60 admitted rows · enUS · complete admitted section scope')).toBeVisible();fireEvent.click(screen.getByRole('button',{name:'Structure'}));expect(screen.getByText(/Physical layout verified for the admitted enUS artifact/)).toBeVisible();expect(screen.getByText('Acquisition source / Home locale / platform')).toBeVisible();});

it('keeps Structure and source inspector explicit about readable partial projection',async()=>{const {bridge}=setup();vi.mocked(bridge.tableData).mockResolvedValue(success({snapshot:partialTableFixture as TableDataSnapshot}));bridge.tableRows=vi.fn(async p=>success({productId:p.productId,buildIdentity:p.buildIdentity,sourceRegion:p.sourceRegion,tableId:p.tableId,researchLocale:p.researchLocale,artifactId:p.artifactId,rows:[{id:'0',isCopy:false,copySourceId:null,values:partialTableFixture.columns.map(()=> 'Authored clear cell')}],totalCount:'60',nextCursor:'1'}));await select('Item');fireEvent.click(screen.getByRole('button',{name:'Data'}));await screen.findByText('Readable rows available · Partial');fireEvent.click(screen.getByRole('button',{name:'Structure'}));expect(screen.getByText(/Observed layout verified for the admitted readable projection · Partial/)).toBeVisible();expect(screen.getByText('Not verified · expected key only')).toBeVisible();fireEvent.click(screen.getByRole('button',{name:'Inspect field Field_12_0_0_63534_010'}));expect(screen.getByText('Raw physical uint32',{selector:'.definition-fact b'})).toBeVisible();expect(screen.getByText(/Original DBD hint: uint8\./)).toBeVisible();expect(screen.queryByText('Verified / 60 admitted rows · enUS · complete admitted section scope')).not.toBeInTheDocument();});

it('preserves a fresh exact data View across parent activation, then restores ordinary area focus without replaying the old intent',async()=>{
 const definition=definitions['50238ec2'],rows=deferred<BridgeReply<never>>(),bridge={onProgress:()=>()=>{},tablesCatalog:vi.fn(async(p:TablesCatalogParams)=>success(catalog(p))),tableData:vi.fn(async()=>success({snapshot:partialTableFixture})),tableRows:vi.fn(()=>rows.promise)} as unknown as DiscoveryBridge;
 const operation={id:'original-item-table',definition,status:'succeeded',result:partialTableFixture,progress:null,error:null},flow={operation,viewRequest:0,pending:false,latchRevision:0,blocked:()=>null,status:{current:null},cancelButton:{current:null},load:vi.fn(),fail:vi.fn()};
 const view=(active:boolean,request:number,other=false)=><><button>Independent control</button><TablesExplorer snapshot={workspace} bridge={bridge} active={active} tableLoading={{...flow,viewRequest:request,operation:other?{...operation,definition:{...definition,buildIdentity:'f'.repeat(64)}}:operation} as never}/></>;
 const mounted=render(view(false,0));mounted.rerender(view(false,1));await waitFor(()=>expect(screen.getByRole('heading',{name:'Table data',hidden:true})).toBeInTheDocument());mounted.rerender(view(true,1));expect(screen.getByRole('heading',{name:'Table data'})).toHaveFocus();expect(Element.prototype.scrollIntoView).toHaveBeenCalledWith({block:'start'});
 const independent=screen.getByRole('button',{name:'Independent control'});independent.focus();await act(async()=>rows.resolve({ok:false,error:{code:'AUTHORED_READ_FAILED',message:'Authored held read finished.',retryable:true}}));expect(independent).toHaveFocus();
 mounted.rerender(view(false,1));mounted.rerender(view(true,1));expect(screen.getByRole('heading',{name:'Tables'})).toHaveFocus();mounted.rerender(view(false,2,true));mounted.rerender(view(true,2,true));expect(screen.getByRole('heading',{name:'Tables'})).toHaveFocus();
});
