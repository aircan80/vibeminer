import {cleanup,render,screen} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,describe,it,expect} from 'vitest';
import type {TableDataColumn,TableDataSnapshot,TableDefinitionSnapshot} from '../../../contracts/generated/discovery';
import definitions from '../../../tests/desktop/fixtures/table-definitions.json';
import fixture from '../../../tests/desktop/fixtures/table-data.json';
import {tableDataMatches} from '../src/shared/table-data';
import {RecordInspector} from '../src/renderer/TableDataView';

// D079 (#53): a verified pallet/common column wider than its definition keeps the low bits and says so.
const definition=definitions.definitions.b977271e as TableDefinitionSnapshot,base=fixture as TableDataSnapshot;
const params={productId:base.productId,buildIdentity:base.buildIdentity,sourceRegion:base.sourceRegion,tableId:base.tableId,researchLocale:'enUS' as const};
const classId=base.columns.findIndex(c=>c.name==='ClassID');
function narrowed(patch:Partial<TableDataColumn>={},index=classId):TableDataSnapshot{const s=structuredClone(base);s.columns[index]={...s.columns[index],narrowing:{storedBits:32,usedBits:8},...patch};return s;}
afterEach(cleanup);

describe('narrowed table columns',()=>{
 it('accept a narrowing that matches the declared width of a verified integer field',()=>{
  expect(tableDataMatches(narrowed(),params,definition)).toBe(true);
  expect(tableDataMatches(narrowed(),params)).toBe(true);
 });
 it('refuse a narrowing that disagrees with the definition or sits on an ID, a string or a raw column',()=>{
  expect(tableDataMatches(narrowed({narrowing:{storedBits:32,usedBits:16}}),params,definition)).toBe(false);
  expect(tableDataMatches(narrowed({},0),params)).toBe(false);
  expect(tableDataMatches(narrowed({},base.columns.findIndex(c=>c.family==='string')),params)).toBe(false);
  expect(tableDataMatches(narrowed({reference:null,interpretation:{policy:'raw-physical-integer-v1',physicalBits:32,physicalSigned:false,candidateBits:8,candidateUnsigned:false,warning:'Authored raw evidence.'}}),params)).toBe(false);
  expect(tableDataMatches(narrowed({narrowing:{storedBits:16 as 32,usedBits:8}}),params)).toBe(false);
 });
 it('show the record inspector that the stored value was wider than defined',()=>{
  const snapshot=narrowed(),row={id:'7',isCopy:false,copySourceId:null,values:snapshot.columns.map(c=>c.family==='string'?'Authored':'7')};
  render(<RecordInspector record={{snapshot,row,definition}}/>);
  expect(screen.getAllByText('Stored wider than defined (32-bit); low 8 bits used')).toHaveLength(1);
 });
});
