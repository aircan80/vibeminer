import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { TableCatalogEntry, TableCatalogPage, TableDefinitionSnapshot, WorkspaceSnapshot } from '../../../contracts/generated/discovery';
import type { BridgeReply, DiscoveryBridge } from '../src/shared/bridge';
import type { TablesCatalogParams } from '../src/shared/tables';
import { CATALOG_LOOKUP_PAGES, TablesExplorer, findCatalogTable } from '../src/renderer/TablesExplorer';
import tableFixture from '../../../tests/desktop/fixtures/table-definitions.json';
import workspaceFixture from '../../../tests/desktop/fixtures/download-workspace.json';

// Relationships say whether a declared target can be followed, and never claim absence from a truncated search page.
const definitions = tableFixture.definitions as unknown as Record<string, TableDefinitionSnapshot>;
const workspace = workspaceFixture as unknown as WorkspaceSnapshot;
const provider = definitions['50238ec2'].provider;
const context = { productId: workspace.build.productId, buildIdentity: workspace.build.id, sourceRegion: workspace.sourceReference.region };
const ok = <T,>(value: T): BridgeReply<T> => ({ ok: true, value });
const entry = (id: number, name: string, state: 'bundled' | 'notBundled' = 'notBundled'): TableCatalogEntry => ({ tableId: id.toString(16).padStart(8, '0'), name, db2FileDataId: null, definitionState: state, sourceState: 'notChecked', rowsState: 'notPrepared' });
// Substring search results: `rows` is the full matching list, served 100 at a time.
function paged(p: TablesCatalogParams, rows: TableCatalogEntry[]): TableCatalogPage {
  const after = Number(p.after ?? 0), slice = rows.slice(after, after + (p.limit ?? 50)), end = after + slice.length;
  return { ...context, provider, query: p.query ?? '', entries: slice, totalCount: String(rows.length), nextCursor: end < rows.length ? String(end) : null };
}
const fillers = (count: number, prefix: string) => Array.from({ length: count }, (_, i) => entry(0x10000000 + i, `${prefix}Filler${i}`));
const results: Record<string, TableCatalogEntry[]> = {
  ItemClass: [...fillers(100, 'ItemClass'), definitions['b977271e'].entry],          // exact match only on the second page
  Material: [entry(0x20000001, 'Material', 'notBundled')],                          // listed, not bundled
  ItemGroupSounds: fillers(100 * CATALOG_LOOKUP_PAGES + 5, 'ItemGroupSounds'),       // still more pages after the bound
};
function bridge() {
  const all = Object.values(definitions).map(value => value.entry);
  return {
    onProgress: vi.fn(() => () => {}), tablePrepare: vi.fn(() => new Promise(() => {})), tableData: vi.fn(async () => ok({ snapshot: null })),
    tablesCatalog: vi.fn(async (p: TablesCatalogParams) => ok(paged(p, !p.query ? all : results[p.query] ?? []))),
    tableDefinition: vi.fn(async (p: { tableId: string }) => ok(definitions[p.tableId])),
  } as unknown as DiscoveryBridge;
}
beforeEach(() => { vi.spyOn(window, 'scrollTo').mockImplementation(() => {}); Element.prototype.scrollIntoView = vi.fn(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('pages within a bound and reports found, absent, truncated, ambiguous and failed lookups', async () => {
  const b = bridge();
  expect(await findCatalogTable(b, context, 'ItemClass', provider)).toMatchObject({ kind: 'found', entry: { name: 'ItemClass', definitionState: 'bundled' } });
  expect(await findCatalogTable(b, context, 'Material', provider)).toMatchObject({ kind: 'found', entry: { definitionState: 'notBundled' } });
  expect(await findCatalogTable(b, context, 'FileData', provider)).toEqual({ kind: 'absent' });
  vi.mocked(b.tablesCatalog).mockClear();
  expect(await findCatalogTable(b, context, 'ItemGroupSounds', provider)).toEqual({ kind: 'truncated' });
  expect(b.tablesCatalog).toHaveBeenCalledTimes(CATALOG_LOOKUP_PAGES);
  expect(vi.mocked(b.tablesCatalog).mock.calls.map(([p]) => p.after)).toEqual([undefined, '100', '200', '300']);
  results.Twice = [entry(0x30000001, 'Twice'), entry(0x30000002, 'Twice')];
  expect(await findCatalogTable(b, context, 'Twice', provider)).toEqual({ kind: 'ambiguous' });
  vi.mocked(b.tablesCatalog).mockResolvedValueOnce({ ok: false, error: { code: 'PROVIDER_UNAVAILABLE', message: 'Authored failure.', retryable: true } });
  expect(await findCatalogTable(b, context, 'ItemClass', provider)).toMatchObject({ kind: 'failed', error: { code: 'PROVIDER_UNAVAILABLE' } });
});

it('shows bundled, not bundled, missing and unknown target states on Relationships cards', async () => {
  const b = bridge();
  render(<TablesExplorer snapshot={workspace} bridge={b} active/>);
  const catalogue = await screen.findByRole('region', { name: 'Provider definitions on this page' });
  fireEvent.click(await within(catalogue).findByRole('button', { name: /^ItemDefinition bundled/ }));
  await screen.findByRole('heading', { level: 2, name: 'Item' });
  fireEvent.click(screen.getByRole('button', { name: 'Relationships' }));
  const card = (field: string) => screen.getByText((_, element) => !!element?.matches('b.dh-mono') && element.textContent!.startsWith(`Item.${field}`)).closest('.dh-relcard') as HTMLElement;
  // Bundled, found past the first 100 substring matches.
  expect(await within(card('ClassID')).findByText(/^Definition bundled/)).toBeInTheDocument();
  expect(within(card('ClassID')).getByRole('button', { name: 'Open ItemClass.ClassID definition' })).toBeEnabled();
  // Listed but not bundled: no Open, with the reason.
  expect(await within(card('Material')).findByText('Can’t follow: no bundled definition')).toBeInTheDocument();
  expect(within(card('Material')).getByText('(DEFINITION_NOT_BUNDLED)')).toBeInTheDocument();
  expect(within(card('Material')).queryByRole('button')).not.toBeInTheDocument();
  // The whole result list was read and it has no such table.
  expect(await within(card('IconFileDataID')).findByText('Can’t follow: the provider lists no FileData table')).toBeInTheDocument();
  expect(within(card('IconFileDataID')).queryByRole('button')).not.toBeInTheDocument();
  // Results remain past the bound: unknown, never absent; Open stays and checks the exact definition.
  expect(await within(card('ItemGroupSoundsID')).findByText('Target state not checked. Opening it checks the exact definition.')).toBeInTheDocument();
  expect(within(card('ItemGroupSoundsID')).getByRole('button', { name: 'Open ItemGroupSounds.ID definition' })).toBeEnabled();
  expect(screen.queryByText('Can’t follow: the provider lists no ItemGroupSounds table')).not.toBeInTheDocument();
  // Following the paged target opens it.
  fireEvent.click(within(card('ClassID')).getByRole('button', { name: 'Open ItemClass.ClassID definition' }));
  expect(await screen.findByRole('heading', { level: 2, name: 'ItemClass' })).toBeInTheDocument();
});
