import { describe, expect, it } from 'vitest';
import type { TableCatalogPage, TableDefinitionSnapshot } from '../../../contracts/generated/discovery';
import catalogue from '../../../contracts/examples/table-catalog-page.json';
import definition from '../../../contracts/examples/table-definition.json';
import { fieldType, tableCatalogMatches, tableDefinitionMatches, validTablesCatalog, validTableDefinition } from '../src/shared/tables';
const page = catalogue as TableCatalogPage, snapshot = definition as unknown as TableDefinitionSnapshot;
const context = { productId: page.productId, buildIdentity: page.buildIdentity, sourceRegion: page.sourceRegion };
describe('narrow provider metadata and definition contracts', () => {
  it.each([{ query: 'x'.repeat(81) }, { query: 'a\nb' }, { limit: 101 }, { limit: 0 }, { after: '01' }, { after: '2147483648' }, { after: '-1' }, { sql: 'select *' }])('refuses broad or malformed catalogue query %#', extra => expect(validTablesCatalog({ ...context, ...extra })).toBe(false));
  it('accepts bounded metadata queries and exact lowercase provider identities', () => {
    expect(validTablesCatalog({ ...context, query: '', limit: 50, after: '0' })).toBe(true); expect(validTableDefinition({ ...context, tableId: 'b977271e' })).toBe(true); expect(validTableDefinition({ ...context, tableId: 'B977271E' })).toBe(false); expect(validTableDefinition({ ...context, tableId: 'b977271e', locale: 'enUS' })).toBe(false);
  });
  it.each(['product', 'source', 'query', 'cursor', 'duplicate', 'ready', 'provider'])('rejects mismatched or misleading catalogue %s', variant => {
    const value = structuredClone(page);
    if (variant === 'product') value.productId = 'wowt'; if (variant === 'source') value.sourceRegion = 'eu'; if (variant === 'query') value.query = 'Material'; if (variant === 'cursor') value.nextCursor = '2'; if (variant === 'duplicate') value.entries[1] = value.entries[0]; if (variant === 'ready') (value.entries[0] as any).rowsState = 'ready'; if (variant === 'provider') value.provider.revision = 'f'.repeat(40);
    expect(tableCatalogMatches(value, { ...context, query: 'Item', limit: 50 }, page.provider)).toBe(false);
  });
  it('pins exact build/provider and preserves declared non-ID target metadata', () => {
    expect(tableDefinitionMatches(snapshot, { ...context, tableId: snapshot.entry.tableId }, snapshot.buildVersion, page.provider)).toBe(true); expect(tableDefinitionMatches({ ...snapshot, buildVersion: '12.1.5.70449' }, { ...context, tableId: snapshot.entry.tableId }, snapshot.buildVersion, page.provider)).toBe(false);
    const klass = snapshot.definition!.candidates[0]!.fields.find(field => field.name === 'ClassID')!; expect(fieldType(klass)).toBe('int8'); expect(klass.isId).toBe(false);
  });
  it.each(['fieldFamily', 'reference', 'duplicateLayout', 'duplicateField', 'numericMetadata'])('rejects contradictory definition projection %s', variant => {
    const value = structuredClone(snapshot);
    if (variant === 'fieldFamily') value.definition!.candidates[0]!.fields[0].family = 'float';
    if (variant === 'reference') value.definition!.candidates[0]!.fields[0].reference = { table: 'MadeUp', column: 'ID' };
    if (variant === 'duplicateLayout') value.definition!.candidates = [value.definition!.candidates[0]!, value.definition!.candidates[0]!];
    if (variant === 'duplicateField') value.definition!.columns.push(value.definition!.columns[0]);
    if (variant === 'numericMetadata') value.definition!.columns[1].integerBits = 32;
    expect(tableDefinitionMatches(value, { ...context, tableId: value.entry.tableId })).toBe(false);
  });
});
