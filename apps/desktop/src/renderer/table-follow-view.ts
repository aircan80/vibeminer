import type { TableFollowPage, TableReference } from '../../../../contracts/generated/discovery';
import { exactCount } from '../shared/inventory';
import { validRecordId } from '../shared/table-data';

/** One Follow: the exact source cell and its declared reference. The engine checks the declaration again. */
export type FollowFilter = { sourceTable: string; sourceTableId: string; sourceArtifactId: string; recordId: string; columnIndex: number; column: string; value: string; reference: TableReference };
/** Where a Data view was, so Back and Forward can put it back: page cursor, earlier pages, record ID filter and selected row. */
export type DataPosition = { artifactId: string; cursor?: string; history: (string | undefined)[]; filter?: string; selected: { id: string; isCopy: boolean; copySourceId: string | null } | null };
/** Whether a declared reference can be followed: to a bundled table, to Files (FileData.ID), or not at all. */
export type FollowTarget = { kind: 'table'; tableId: string } | { kind: 'files' } | { kind: 'checking' } | { kind: 'none'; reason: string };
/** A followed value that left Tables for Files, with the record to return to. */
/** `from: 'creatures'` when a Creatures model file opened Files (#62); its return goes back to the Creatures selection. */
export type FileTarget = { fileDataId: string; table: string; recordId: string; seq: number; from?: 'creatures' };

export const isFileDataReference = (reference: TableReference) => reference.table === 'FileData' && reference.column === 'ID';
/** The action's name in the grid title and the record inspector, or null when this value can't be followed. */
export function followLabel(target: FollowTarget | undefined, reference: TableReference, value: string): string | null {
 if (target?.kind === 'files') return validRecordId(value) ? `Find FileDataID ${value} in Files` : null;
 if (target?.kind === 'table') return `Show ${reference.table} rows where ${reference.column} = ${value}`;
 return null;
}
export type FollowNotice = { tone: 'calm' | 'warn'; title: string; detail: string };
/**
 * Wording for a follow result. One match selects its row and needs no notice, unless it is the one readable row of a
 * partly readable table on a non-ID column: that row is listed, not picked. Several are listed and none is picked.
 * A miss says so for the complete table, and says it isn't an absence when only readable rows were searched.
 */
export function describeFollow(result: Pick<TableFollowPage, 'state' | 'scope' | 'targetIsId' | 'targetRange' | 'problem' | 'value' | 'reference'> & { matchCount: string | null }, table: string): FollowNotice | null {
 const { column } = result.reference, at = `${column} = ${result.value}`, partial = result.scope === 'unencryptedSections';
 switch (result.state) {
  case 'keyOutOfRange': return { tone: 'calm', title: `No ${table} row can have ${at}`, detail: `${table}.${column} holds ${result.targetRange ? `${result.targetRange.minimum} to ${result.targetRange.maximum}` : 'a narrower range'}. The value isn't wrapped or truncated, so nothing was searched.` };
  case 'unindexed': return { tone: 'calm', title: `${table} can't be searched by ${column}`, detail: `${result.problem?.message ?? ''} No row was picked, and this isn't an absence.`.trim() };
  case 'unsupported': return { tone: 'calm', title: `${table}.${column} can't be matched in this layout`, detail: `${result.problem?.message ?? ''} No row was picked, and this isn't an absence.`.trim() };
 }
 const count = result.matchCount === null ? 0n : BigInt(result.matchCount);
 // One readable row in a partly readable table doesn't prove it is the only one, unless it is the record ID.
 if (count === 1n) return partial && !result.targetIsId ? { tone: 'warn', title: `1 readable ${table} row has ${at}`, detail: `Protected sections are not loaded and may hold more rows with ${at}, so this row isn't picked as the match.` } : null;
 if (count > 1n) return { tone: 'calm', title: `${exactCount(result.matchCount!)} ${partial ? 'readable' : 'admitted'} rows have ${at}`, detail: `Choose one. ${result.targetIsId ? 'Copies count as rows of their own' : `${column} isn't the record ID`}, so no row is picked.` };
 return partial
  ? { tone: 'warn', title: `Not in the readable ${table} rows`, detail: `No readable row has ${at}. Protected sections are not loaded, so this isn't an absence.` }
  : { tone: 'calm', title: `No admitted ${table} row has ${at}`, detail: 'Complete admitted section scope for enUS. Nothing else was searched or fetched.' };
}
