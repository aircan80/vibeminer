import type { TableDataSnapshot, TableFollowPage } from '../../../../contracts/generated/discovery';
import { validTableData, validTableTextIssues, validRecordId, type TableDataParams } from './table-data';
// Follow one declared reference by exact target column value (contracts/table-follow-v1.md, D072).
export type TableFollowParams = TableDataParams & { artifactId: string; columnIndex: number; value: string; targetTableId: string; targetArtifactId: string; limit?: number; after?: string };
const utf8 = new TextEncoder(), decimal = /^(0|[1-9][0-9]{0,18})$/, hex64 = /^[0-9a-f]{64}$/;
/** Canonical decimal integer: no plus, no leading zeros, no -0, at most 20 digits. */
export const canonicalInteger = (value: unknown): value is string => typeof value === 'string' && /^(0|-?[1-9][0-9]{0,19})$/.test(value);
export function validTableFollow(value: unknown): value is TableFollowParams {
 if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
 const { artifactId, columnIndex, value: key, targetTableId, targetArtifactId, limit, after, ...context } = value as TableFollowParams;
 return validTableData(context) && typeof artifactId === 'string' && hex64.test(artifactId) && Number.isInteger(columnIndex) && columnIndex >= 0 && columnIndex <= 511
  && canonicalInteger(key) && typeof targetTableId === 'string' && /^[0-9a-f]{8}$/.test(targetTableId) && typeof targetArtifactId === 'string' && hex64.test(targetArtifactId)
  && (limit === undefined || Number.isInteger(limit) && limit >= 1 && limit <= 50) && (after === undefined || typeof after === 'string' && decimal.test(after) && BigInt(after) <= 9223372036854775807n);
}
/**
 * A follow reply answers exactly the lookup that was asked. Every row on a ready page really has the value in the
 * target column (or as its record ID), so a stale or forged page can't pass as a match. With the snapshots, the
 * declared reference and the scope must agree with what the desktop already shows.
 */
export function followMatches(value: TableFollowPage, p: TableFollowParams, source?: TableDataSnapshot, target?: TableDataSnapshot): boolean {
 if (value.productId !== p.productId || value.buildIdentity !== p.buildIdentity || value.sourceRegion !== p.sourceRegion || value.researchLocale !== p.researchLocale || value.policyRevision !== 'table-follow-v1') return false;
 if (value.tableId !== p.tableId || value.artifactId !== p.artifactId || value.columnIndex !== p.columnIndex || value.value !== p.value || value.targetTableId !== p.targetTableId || value.targetArtifactId !== p.targetArtifactId || value.after !== (p.after ?? '0')) return false;
 if (utf8.encode(JSON.stringify(value)).byteLength > 512 * 1024) return false;
 if (source) { const c = source.columns[p.columnIndex]; if (source.artifactId !== p.artifactId || !c || c.name !== value.column || c.family !== 'integer' || c.reference?.table !== value.reference.table || c.reference.column !== value.reference.column) return false; }
 if (target && (target.artifactId !== p.targetArtifactId || target.coverage.scope !== value.scope)) return false;
 const index = value.targetColumnIndex;
 if (index !== null && target && (target.columns[index]?.name !== value.reference.column || target.columns[index].isId !== value.targetIsId)) return false;
 if (value.targetRange && !(canonicalInteger(value.targetRange.minimum) && canonicalInteger(value.targetRange.maximum) && BigInt(value.targetRange.minimum) <= BigInt(value.targetRange.maximum))) return false;
 if (value.state !== 'ready') {
  if (value.page !== null) return false;
  if (value.state === 'keyOutOfRange') return index !== null && value.problem === null && !!value.targetRange && (BigInt(value.value) < BigInt(value.targetRange.minimum) || BigInt(value.value) > BigInt(value.targetRange.maximum));
  if (value.state === 'unindexed') return index !== null && !value.targetIsId && value.problem !== null;
  return value.state === 'unsupported' && index === null && value.problem !== null;
 }
 const page = value.page;
 if (!page || index === null || value.problem !== null || page.productId !== p.productId || page.buildIdentity !== p.buildIdentity || page.sourceRegion !== p.sourceRegion || page.researchLocale !== p.researchLocale || page.tableId !== p.targetTableId || page.artifactId !== p.targetArtifactId) return false;
 if (!decimal.test(page.totalCount) || page.rows.length > (p.limit ?? 50) || BigInt(p.after ?? '0') + BigInt(page.rows.length) > BigInt(page.totalCount)) return false;
 if (page.nextCursor !== null && (!decimal.test(page.nextCursor) || BigInt(page.nextCursor) !== BigInt(p.after ?? '0') + BigInt(page.rows.length) || page.rows.length === 0)) return false;
 if (page.nextCursor === null && BigInt(p.after ?? '0') + BigInt(page.rows.length) !== BigInt(page.totalCount)) return false;
 if (target && BigInt(page.totalCount) > BigInt(target.rowCount)) return false;
 if (value.targetRange && (BigInt(value.value) < BigInt(value.targetRange.minimum) || BigInt(value.value) > BigInt(value.targetRange.maximum))) return false;
 return page.rows.every(row => validRecordId(row.id) && row.isCopy === (row.copySourceId !== null) && (row.copySourceId === null || validRecordId(row.copySourceId)) && (!target || row.values.length === target.columns.length)
  && (value.targetIsId ? row.id === value.value : row.values[index] === value.value) && utf8.encode(JSON.stringify(row)).byteLength <= 256 * 1024 && validTableTextIssues(row, target?.columns));
}
