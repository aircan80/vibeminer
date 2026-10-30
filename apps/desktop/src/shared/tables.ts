import type { TableCatalogPage, TableDefinitionProvider, TableDefinitionSnapshot, TableDefinitionField, TableDefinitionFetched, TableDefinitionCheck } from '../../../../contracts/generated/discovery';
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
export const fetchedDefinitionUrl = (revision: string, table: string) => `https://raw.githubusercontent.com/wowdev/WoWDBDefs/${revision}/definitions/${table}.dbd`;
/** "Definition from WoWDBDefs (fetched Oct 23, 2026, a50ca6f)": never mixed up with a bundled or verified one. */
export function fetchedLabel(fetched: TableDefinitionFetched): string {
  return `Definition from WoWDBDefs (fetched ${new Date(fetched.fetchedAt).toLocaleDateString(undefined, { dateStyle: 'medium' })}, ${fetched.revision.slice(0, 7)})`;
}
/** The same source in one short line for enlarged text: "WoWDBDefs · Oct 23, 2026 · a50ca6f". */
export function fetchedCompactLabel(fetched: TableDefinitionFetched): string {
  return `WoWDBDefs · ${new Date(fetched.fetchedAt).toLocaleDateString(undefined, { dateStyle: 'medium' })} · ${fetched.revision.slice(0, 7)}`;
}
const checkFailures = new Set(['DEFINITION_FETCH_OFFLINE', 'DEFINITION_FETCH_FAILED', 'DEFINITION_FETCH_TOO_LARGE', 'DEFINITION_FETCH_INVALID', 'DEFINITION_FETCH_NOT_FOUND']);
const instant = (text: unknown) => typeof text === 'string' && !Number.isNaN(Date.parse(text));
// #81: the last WoWDBDefs check comes only with fetching on, for a named table with a bundled definition, and says what it found.
function validCheck(value: TableDefinitionSnapshot, check: TableDefinitionCheck | undefined): boolean {
  if (check === undefined) return true;
  if (!check || typeof check !== 'object' || value.definitionFetch !== 'on' || !value.definition || !value.entry.name || !instant(check.checkedAt) || !instant(check.nextCheckAfter)) return false;
  if (check.fetched === undefined && check.failure === undefined) return false;
  if (check.failure !== undefined && !checkFailures.has(check.failure)) return false;
  return check.fetched === undefined || !!check.fetched && /^[0-9a-f]{40}$/.test(check.fetched.revision) && instant(check.fetched.fetchedAt);
}
const shortTime = (at: number, now: number) => new Date(at).toDateString() === new Date(now).toDateString()
  ? new Date(at).toLocaleTimeString(undefined, { timeStyle: 'short' })
  : new Date(at).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
/** The inspector's Definition match for an unlisted build with no layout on its version line while fetching is on (D081,
 * #81): before WoWDBDefs was asked, a fetch is still to come; after, what the last check found and when Data asks again. */
export function uncoveredDefinitionMatch(check: TableDefinitionCheck | undefined, now = Date.now()): string {
  if (!check) return 'Build not listed · no bundled layout on its version line; opening Data fetches a newer definition from WoWDBDefs';
  const checked = shortTime(Date.parse(check.checkedAt), now), next = Date.parse(check.nextCheckAfter);
  const neither = check.fetched ? `no layout on its version line in the bundled definition or WoWDBDefs ${check.fetched.revision.slice(0, 7)}` : 'no bundled layout on its version line';
  // Data offers Retry table data at once after an offline or failed attempt, otherwise once the wait has passed; reopening
  // Data after the wait asks too.
  const retryNow = next <= now || check.failure === 'DEFINITION_FETCH_OFFLINE' || check.failure === 'DEFINITION_FETCH_FAILED';
  const again = `${retryNow ? '' : `from ${shortTime(next, now)}, `}Retry table data asks WoWDBDefs again`;
  if (check.failure) return `Build not listed · ${neither}; the last WoWDBDefs check failed (${check.failure}, ${checked}); ${again}`;
  return `Build not listed · ${neither} (checked ${checked}), so the table can’t open; ${again}`;
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
  if (value.definitionFetch !== undefined && value.definitionFetch !== 'on' && value.definitionFetch !== 'off') return false;
  if (!validCheck(value, value.definitionCheck)) return false;
  if (!value.definition) return !!value.problem?.code.trim() && !!value.problem.message.trim();
  if (value.entry.definitionState !== 'bundled' || !fieldsValid(value.definition.columns)) return false;
  // D081: a fetched definition comes only from the fixed WoWDBDefs address for its commit and table, and only while fetching is on.
  const fetched = value.definition.fetched;
  if (fetched !== undefined && (!fetched || value.definitionFetch !== 'on' || !value.entry.name || !/^[0-9a-f]{40}$/.test(fetched.revision) || Number.isNaN(Date.parse(fetched.fetchedAt)) ||
    value.definition.sourceUrl !== fetchedDefinitionUrl(fetched.revision, value.entry.name))) return false;
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
