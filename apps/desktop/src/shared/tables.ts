import type { TableCatalogPage, TableDefinitionProvider, TableDefinitionSnapshot, TableDefinitionField } from '../../../../contracts/generated/discovery';
import type { OpenParams } from './bridge';
export type TablesCatalogParams = OpenParams & { query?: string; limit?: number; after?: string };
export type TableDefinitionParams = OpenParams & { tableId: string };
const decimal = /^(0|[1-9][0-9]{0,18})$/;
const ordinal = /^(0|[1-9][0-9]{0,9})$/;
const hash = /^[0-9a-f]{8}$/;
function context(value: OpenParams): boolean { return typeof value.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(value.productId) && typeof value.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(value.buildIdentity) && typeof value.sourceRegion === 'string' && /^[a-z]{2,8}$/.test(value.sourceRegion); }
export function validTablesCatalog(value: unknown): value is TablesCatalogParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as TablesCatalogParams;
  return context(p) && (p.query === undefined || typeof p.query === 'string' && p.query.length <= 80 && !/[\u0000-\u001f\u007f-\u009f]/.test(p.query)) && (p.limit === undefined || Number.isInteger(p.limit) && p.limit >= 1 && p.limit <= 100) && (p.after === undefined || typeof p.after === 'string' && ordinal.test(p.after) && BigInt(p.after) <= 2147483647n) && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion', 'query', 'limit', 'after'].includes(key));
}
export function validTableDefinition(value: unknown): value is TableDefinitionParams {
  if (!value || typeof value !== 'object') return false;
  const p = value as TableDefinitionParams;
  return context(p) && typeof p.tableId === 'string' && hash.test(p.tableId) && Object.keys(p).length === 4 && Object.keys(p).every(key => ['productId', 'buildIdentity', 'sourceRegion', 'tableId'].includes(key));
}
export function sameProvider(a: TableDefinitionProvider, b: TableDefinitionProvider): boolean { return a.name === b.name && a.revision === b.revision && a.manifestSha256 === b.manifestSha256 && a.license === b.license && a.sourceUrl === b.sourceUrl; }
export function tableCatalogMatches(value: TableCatalogPage, p: TablesCatalogParams, provider?: TableDefinitionProvider): boolean {
  if (value.productId !== p.productId || value.buildIdentity !== p.buildIdentity || value.sourceRegion !== p.sourceRegion || value.query !== (p.query ?? '') || !decimal.test(value.totalCount) || value.entries.length > (p.limit ?? 50) || provider && !sameProvider(value.provider, provider)) return false;
  const ids = new Set<string>();
  for (const entry of value.entries) { if (!hash.test(entry.tableId) || ids.has(entry.tableId) || entry.sourceState !== 'notChecked' || entry.rowsState !== 'notPrepared') return false; ids.add(entry.tableId); }
  const end = BigInt(p.after ?? '0') + BigInt(value.entries.length), total = BigInt(value.totalCount);
  if (value.entries.length === 0) return BigInt(p.after ?? '0') >= total && value.nextCursor === null;
  return end <= total && (end < total ? value.nextCursor === end.toString() : value.nextCursor === null);
}
function fieldsValid(fields: TableDefinitionField[]): boolean {
  const names = new Set<string>();
  return fields.every(field => {
    if (names.has(field.name) || !field.name || field.family !== 'int' && (field.integerBits !== null || field.unsigned !== null)) return false;
    names.add(field.name); return true;
  });
}
export function tableDefinitionMatches(value: TableDefinitionSnapshot, p: TableDefinitionParams, buildVersion?: string, provider?: TableDefinitionProvider): boolean {
  if (value.productId !== p.productId || value.buildIdentity !== p.buildIdentity || value.sourceRegion !== p.sourceRegion || value.entry.tableId !== p.tableId || value.entry.sourceState !== 'notChecked' || value.entry.rowsState !== 'notPrepared' || buildVersion !== undefined && value.buildVersion !== buildVersion || provider && !sameProvider(value.provider, provider)) return false;
  if (!value.definition) return !!value.problem?.code.trim() && !!value.problem.message.trim();
  if (value.entry.definitionState !== 'bundled' || !fieldsValid(value.definition.columns)) return false;
  const columns = new Map(value.definition.columns.map(field => [field.name, field])), layouts = new Set<string>();
  for (const candidate of value.definition.candidates) {
    if (layouts.has(candidate.layoutHash) || !fieldsValid(candidate.fields)) return false;
    layouts.add(candidate.layoutHash);
    for (const field of candidate.fields) { const column = columns.get(field.name); if (!column || column.family !== field.family || column.verifiedName !== field.verifiedName || (column.reference?.table !== field.reference?.table || column.reference?.column !== field.reference?.column)) return false; }
  }
  return true;
}
export function fieldType(field: TableDefinitionField): string {
  const scalar = field.family === 'int' && field.integerBits !== null ? `${field.unsigned ? 'uint' : 'int'}${field.integerBits}` : field.family;
  return `${scalar}${field.arrayLength > 1 ? `[${field.arrayLength}]` : ''}`;
}
