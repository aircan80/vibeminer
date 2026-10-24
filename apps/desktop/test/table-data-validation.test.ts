// @vitest-environment node
import { describe,it,expect } from 'vitest';
import type {TableDataSnapshot,TableDefinitionSnapshot,TableDataPage} from '../../../contracts/generated/discovery';
import fixture from '../../../tests/desktop/fixtures/table-data.json';
import definitions from '../../../tests/desktop/fixtures/table-definitions.json';
import {validTableData,validTableRows,validRecordId,tableDataMatches,tableRowsMatch,definitionChanged} from '../src/shared/table-data';
import {fetchedDefinitionUrl,fetchedLabel,tableDefinitionMatches} from '../src/shared/tables';
const snapshot=fixture as TableDataSnapshot, definition=definitions.definitions.b977271e as TableDefinitionSnapshot;
const params={productId:snapshot.productId,buildIdentity:snapshot.buildIdentity,sourceRegion:snapshot.sourceRegion,tableId:snapshot.tableId,researchLocale:'enUS' as const};
const query={...params,artifactId:snapshot.artifactId,limit:50};
const page:TableDataPage={...params,artifactId:snapshot.artifactId,rows:[{id:'4294967295',isCopy:false,copySourceId:null,values:['4294967295','Authored example','0','1','0']}],totalCount:'60',nextCursor:'1'};
describe('exact table admission and bounded local row queries',()=>{
 it('validates fixed locale, exact request shape, bounds and raw uint32 IDs',()=>{expect(validTableData(params)).toBe(true);expect(validTableData({...params,researchLocale:'deDE'})).toBe(false);expect(validTableData({...params,url:'https://example.invalid'})).toBe(false);expect(validTableRows(query)).toBe(true);for(const bad of ['-1','+1','01','1.0','4294967296',''])expect(validRecordId(bad)).toBe(false);expect(validRecordId('0')).toBe(true);expect(validTableRows({...query,limit:51})).toBe(false);expect(validTableRows({...query,after:'9223372036854775808'})).toBe(false);});
 it('binds actual header, definition, exact locale membership and columns',()=>{expect(tableDataMatches(snapshot,params,definition)).toBe(true);for(const patch of [{tableId:'deadbeef'},{layoutHash:'DEADBEEF'},{providerRevision:'0'.repeat(40)},{columns:snapshot.columns.slice(1)},{association:{...snapshot.association,memberships:[{...snapshot.association.memberships[0],contentKey:'0'.repeat(32)}]}}])expect(tableDataMatches({...snapshot,...patch},params,definition)).toBe(false);});
 it('counts physical and copy exclusions independently and keeps partial policy distinct',()=>{const partial={...snapshot,rowCount:'5',coverage:{scope:'unencryptedSections' as const,declaredRecords:'10',declaredCopyRecords:'3',physicalRows:'4',copyRows:'1',excludedPhysicalRecords:'6',excludedCopyRecords:'2',excludedDeclaredRecords:'8',excludedKeyedSections:'1'}};expect(tableDataMatches(partial,params)).toBe(true);expect(tableDataMatches({...partial,coverage:{...partial.coverage,excludedCopyRecords:'1'}},params)).toBe(false);expect(tableDataMatches({...partial,coverage:{...partial.coverage,scope:'complete'}},params)).toBe(false);});
 it('uses ordinal cursors independently of exact raw record IDs and rejects artifact/shape mismatch',()=>{expect(tableRowsMatch(page,query,snapshot)).toBe(true);expect(tableRowsMatch({...page,nextCursor:'4294967295'},query,snapshot)).toBe(false);expect(tableRowsMatch({...page,artifactId:'0'.repeat(64)},query,snapshot)).toBe(false);expect(tableRowsMatch({...page,rows:[{...page.rows[0],values:['wrong width']}]},query,snapshot)).toBe(false);expect(tableRowsMatch(page,{...query,recordId:'0'},snapshot)).toBe(false);expect(tableRowsMatch({...page,rows:[],totalCount:'0',nextCursor:null},{...query,recordId:'0'},snapshot)).toBe(true);});
 it('accepts a layout-only match only against a definition that does not list the build (D080)',()=>{const unlisted={...definition,problem:{code:'SCHEMA_BUILD_NOT_LISTED',message:'The authored build is not listed.'}};const layoutOnly={...snapshot,schemaMatch:'layoutOnly' as const};expect(tableDataMatches(layoutOnly,params,unlisted)).toBe(true);expect(tableDataMatches(layoutOnly,params)).toBe(true);expect(tableDataMatches(layoutOnly,params,definition)).toBe(false);expect(tableDataMatches(snapshot,params,unlisted)).toBe(false);for(const schemaMatch of ['build','exactBuild','',null])expect(tableDataMatches({...snapshot,schemaMatch} as unknown as TableDataSnapshot,params)).toBe(false);});
 it('accepts rows read with a fetched WoWDBDefs definition only against that same fetched definition (D081)',()=>{
  const fetched={revision:'a50ca6ffbcfe17d604327c9078993675f7ae831f',fetchedAt:'2026-10-23T12:00:00+00:00'};
  const fetchedDefinition={...definition,definitionFetch:'on' as const,definition:{...definition.definition!,sha256:'ab'.repeat(32),sourceUrl:fetchedDefinitionUrl(fetched.revision,definition.entry.name!),fetched}};
  const rows={...snapshot,definitionSha256:'ab'.repeat(32),providerRevision:fetched.revision,fetchedDefinition:fetched,fetchedBecause:'exactBuild' as const,bundledFallback:false};
  // The rule that chose it is required with it, and only a layout match may be matched by layout.
  expect(tableDataMatches({...rows,fetchedBecause:undefined},params)).toBe(false);
  expect(tableDataMatches({...rows,fetchedBecause:'layoutMatch'},params)).toBe(false);
  expect(tableDataMatches({...rows,fetchedBecause:'layoutMatch',schemaMatch:'layoutOnly'},params)).toBe(true);
  expect(tableDataMatches({...rows,fetchedBecause:'layoutNotBundled'},params)).toBe(true);
  expect(tableDataMatches({...rows,fetchedBecause:'layoutNotBundled',schemaMatch:'layoutOnly'},params)).toBe(true);
  expect(tableDataMatches({...rows,schemaMatch:'layoutOnly'},params)).toBe(false);
  expect(tableDataMatches({...rows,bundledFallback:true},params)).toBe(true);
  expect(tableDataMatches({...rows,fetchedBecause:'layoutNotBundled',bundledFallback:true},params)).toBe(false);
  expect(tableDataMatches({...rows,bundledFallback:undefined},params)).toBe(false);
  expect(tableDataMatches({...snapshot,fetchedBecause:'exactBuild'},params)).toBe(false);
  expect(tableDefinitionMatches(fetchedDefinition,{productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,tableId:params.tableId})).toBe(true);
  expect(tableDataMatches(rows,params,fetchedDefinition)).toBe(true);
  expect(tableDataMatches(rows,params)).toBe(true);
  // Never against the bundled definition, nor bundled rows against the fetched one: the view reads the definition again.
  expect(tableDataMatches(rows,params,definition)).toBe(false);expect(definitionChanged(rows,definition)).toBe(true);
  expect(tableDataMatches(snapshot,params,fetchedDefinition)).toBe(false);expect(definitionChanged(snapshot,fetchedDefinition)).toBe(true);
  expect(definitionChanged(rows,fetchedDefinition)).toBe(false);
  expect(tableDataMatches({...rows,fetchedDefinition:{...fetched,fetchedAt:'2026-10-24T12:00:00Z'}},params,fetchedDefinition)).toBe(false);
  expect(tableDataMatches({...rows,providerRevision:definition.provider.revision},params)).toBe(false);
  expect(tableDataMatches({...rows,fetchedDefinition:{...fetched,revision:'b9a6309'}},params)).toBe(false);
  // A fetched definition comes only from its fixed WoWDBDefs address, and only while fetching is on.
  const context={productId:params.productId,buildIdentity:params.buildIdentity,sourceRegion:params.sourceRegion,tableId:params.tableId};
  expect(tableDefinitionMatches({...fetchedDefinition,definition:{...fetchedDefinition.definition,sourceUrl:'https://example.invalid/ItemClass.dbd'}},context)).toBe(false);
  expect(tableDefinitionMatches({...fetchedDefinition,definitionFetch:'off'},context)).toBe(false);
  expect(tableDefinitionMatches({...definition,definitionFetch:'maybe'} as unknown as TableDefinitionSnapshot,context)).toBe(false);
  expect(fetchedLabel(fetched)).toMatch(/^Definition from WoWDBDefs \(fetched .*2026, a50ca6f\)$/);
 });
 it('expands declared array fields to actual named scalar columns',()=>{const changed=structuredClone(definition);changed.definition!.candidates[0]!.fields[0]!.arrayLength=2;const array={...snapshot,columns:[{...snapshot.columns[0],name:'ID[0]'},{...snapshot.columns[0],name:'ID[1]'},...snapshot.columns.slice(1)]};expect(tableDataMatches(array,params,changed)).toBe(true);});
});
