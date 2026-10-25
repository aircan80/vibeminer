import type { TableDataSnapshot, TableDataPage, TableDefinitionSnapshot, TableDefinitionFetched, ContentReceipt, TableDataColumn, TableDataRow } from '../../../../contracts/generated/discovery';
import { validTableDefinition, type TableDefinitionParams } from './tables';
export type TableDataParams = TableDefinitionParams & { researchLocale: 'enUS' };
export type TableRowsParams = TableDataParams & { artifactId: string; after?: string; recordId?: string; limit?: number };
const utf8 = new TextEncoder(), strictUtf8 = new TextDecoder('utf-8',{fatal:true});
const decimal = /^(0|[1-9][0-9]{0,18})$/;
export const validRecordId = (value: string): boolean => /^(0|[1-9][0-9]{0,9})$/.test(value) && BigInt(value) <= 4294967295n;
export function validTableData(value: unknown): value is TableDataParams {
 if (!value || typeof value !== 'object') return false;
 const {researchLocale, ...context} = value as TableDataParams;
 return researchLocale === 'enUS' && validTableDefinition(context);
}
export function validTableRows(value: unknown): value is TableRowsParams {
 if (!value || typeof value !== 'object') return false;
 const {artifactId, after, recordId, limit, ...context} = value as TableRowsParams;
 return validTableData(context) && typeof artifactId === 'string' && /^[0-9a-f]{64}$/.test(artifactId) && (after === undefined || typeof after === 'string' && decimal.test(after) && BigInt(after) <= 9223372036854775807n) && (recordId === undefined || typeof recordId === 'string' && validRecordId(recordId)) && (limit === undefined || Number.isInteger(limit) && limit >= 1 && limit <= 50);
}
export const isPartialSource = (source: ContentReceipt): boolean => source.verifierRevision === 'content-blte-projection-v1';
export function validTableSourceProof(source: ContentReceipt): boolean {
 const projection = source.projection;
 if (source.verifierRevision === 'content-blte-v1') return projection === undefined || projection === null;
 if (!isPartialSource(source) || !projection || projection.policy !== 'blte-clear-chunks-v1' || !/^[0-9a-f]{64}$/.test(projection.identitySha256) || !decimal.test(source.decodedBytes)) return false;
 if (!projection.readableRanges.length || !projection.unavailableRanges.length || projection.readableRanges.length + projection.unavailableRanges.length > 128) return false;
 const size=BigInt(source.decodedBytes), positive=(value:string)=>decimal.test(value)&&BigInt(value)>0n;
 for (const ranges of [projection.readableRanges,projection.unavailableRanges]) {
  let end=0n;
  for (const range of ranges) { if (!decimal.test(range.offset) || !positive(range.byteLength) || BigInt(range.offset)<end || BigInt(range.offset)+BigInt(range.byteLength)>size) return false; end=BigInt(range.offset)+BigInt(range.byteLength); }
 }
 let previousOrdinal=-1;
 if (!projection.unavailableRanges.every(range=>{const valid=range.chunkOrdinal>previousOrdinal;previousOrdinal=range.chunkOrdinal;return valid;})) return false;
 if (!projection.readableRanges.every(range=>/^[0-9a-f]{64}$/.test(range.sha256)) || !projection.unavailableRanges.every(range=>Number.isSafeInteger(range.chunkOrdinal)&&range.chunkOrdinal>=0&&range.chunkOrdinal<65536&&/^[0-9A-F]{16}$/.test(range.keyName))) return false;
 const merged=[...projection.readableRanges.map(range=>({...range,chunkOrdinal:null})),...projection.unavailableRanges].sort((a,b)=>BigInt(a.offset)<BigInt(b.offset)?-1:BigInt(a.offset)>BigInt(b.offset)?1:0);
 let end=0n;
 for (const [index,range] of merged.entries()) {if(BigInt(range.offset)!==end || range.chunkOrdinal!==null && range.chunkOrdinal<index)return false;end+=BigInt(range.byteLength);}
 return end===size;
}
function validRawInterpretation(column:TableDataColumn):boolean {
 const i=column.interpretation;
 return !i || i.policy==='raw-physical-integer-v1' && column.family==='integer' && !column.isId && column.reference===null && Number.isInteger(i.physicalBits) && i.physicalBits>=1 && i.physicalBits<=64 && Number.isInteger(i.candidateBits) && i.candidateBits>=1 && i.candidateBits<=64 && i.physicalBits>i.candidateBits && typeof i.physicalSigned==='boolean' && typeof i.candidateUnsigned==='boolean' && typeof i.warning==='string' && i.warning.length<=400 && !!i.warning.trim();
}
// D079: a 32-bit pallet/common column narrower in its definition keeps the low bits; raw columns never narrow.
function validNarrowing(column:TableDataColumn):boolean {
 const n=column.narrowing;
 return n===undefined || column.family==='integer' && !column.isId && !column.interpretation && n.storedBits===32 && (n.usedBits===8||n.usedBits===16);
}
const itemEffectRevision='wdc5-values-v7+table-admission-v4',itemEffectProjectionRevision='wdc5-values-v7+table-admission-projection-v4';
const bundledProvider='b9a63096cdd99b26967f17f9515377ae62cadfd7';
// The reviewed bundled definition, or (D101) a fetched one the engine reads with the same reviewed rules because its block
// for the reviewed layout is identical. The engine checks that; here a fetched one only has to name its own commit.
export const reviewedDefinition=(s:TableDataSnapshot,sha:string)=>s.fetchedDefinition
 ?s.providerRevision!==bundledProvider&&s.definitionSha256!==sha:s.providerRevision===bundledProvider&&s.definitionSha256===sha;
function itemEffectScope(s:TableDataSnapshot):boolean {
 return s.tableId==='4002a5b1'&&reviewedDefinition(s,'7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c')
  &&s.association.fileDataId==='969941'&&(!s.fetchedDefinition||s.layoutHash==='4CA77678');
}
// D101: a fetched definition read with the reviewed ItemSubClass or CreatureDisplayInfo raw rules carries v4, so its rows
// never share a key with rows that definition decoded without them.
const fetchedReviewedScope=(s:TableDataSnapshot)=>!!s.fetchedDefinition&&(s.tableId==='95d8638f'&&s.layoutHash==='1E67DB87'&&reviewedDefinition(s,'e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c')
 ||s.tableId==='bfdaf9f1'&&s.layoutHash==='7275F5F6'&&reviewedDefinition(s,'64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425'));
export const reviewedRevision=(s:TableDataSnapshot)=>isPartialSource(s.source)?itemEffectProjectionRevision:itemEffectRevision;
function validScopedAdmissionRevision(s:TableDataSnapshot):boolean {
 if(s.decoderRevision!==itemEffectRevision&&s.decoderRevision!==itemEffectProjectionRevision)return true;
 // The version envelope precedes actual-layout selection; it grants no raw field by itself.
 return (itemEffectScope(s)||fetchedReviewedScope(s))&&s.decoderRevision===reviewedRevision(s);
}
// These reviewed backend admissions preserve raw evidence for an exact pinned
// definition/layout. No value decoding or general verified-field widening occurs here.
function verifiedRawAdmission(s:TableDataSnapshot,fieldOrdinal:number,column:TableDataColumn):boolean {
 const i=column.interpretation;
 if(!i||i.physicalBits!==32||i.physicalSigned)return false;
 const revision=s.decoderRevision,subclass=s.tableId==='95d8638f'&&reviewedDefinition(s,'e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c')&&s.layoutHash==='1E67DB87'&&fieldOrdinal===6&&column.name==='PrerequisiteProficiency'&&i.candidateBits===8&&!i.candidateUnsigned;
 const creature=s.tableId==='bfdaf9f1'&&reviewedDefinition(s,'64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425')&&s.layoutHash==='7275F5F6'&&((fieldOrdinal===8&&column.name==='NPCSoundID')||(fieldOrdinal===12&&column.name==='ObjectEffectPackageID')||(fieldOrdinal===13&&column.name==='AnimReplacementSetID'))&&i.candidateBits===16&&i.candidateUnsigned;
 const effect=itemEffectScope(s)&&s.layoutHash==='4CA77678'&&i.candidateBits===16
  &&((fieldOrdinal===3&&column.name==='Charges'&&!i.candidateUnsigned)||((fieldOrdinal===6&&column.name==='SpellCategoryID')||(fieldOrdinal===8&&column.name==='ChrSpecializationID'))&&i.candidateUnsigned);
 // Fetched ItemSubClass and CreatureDisplayInfo rows read with the reviewed rules carry v4 (D101); bundled ones keep their v2/v3 spellings.
 if((subclass||creature)&&s.fetchedDefinition)return revision===reviewedRevision(s);
 return effect&&revision===reviewedRevision(s)||subclass&&['wdc5-values-v7+table-admission-v2','wdc5-values-v7+table-admission-projection-v2','wdc5-values-v7+table-admission-v3','wdc5-values-v7+table-admission-projection-v3'].includes(revision)||creature&&['wdc5-values-v7+table-admission-v3','wdc5-values-v7+table-admission-projection-v3'].includes(revision);
}
export const sameFetch = (a?: TableDefinitionFetched, b?: TableDefinitionFetched) => a === b || !!a && !!b && a.revision === b.revision && Date.parse(a.fetchedAt) === Date.parse(b.fetchedAt);
/** True when the rows were read with another definition than the one shown: the engine fetched a newer one (D081) or
 * the shown one is no longer used. The view reads the definition again instead of calling the rows a mismatch. */
export const definitionChanged = (value: TableDataSnapshot, definition: TableDefinitionSnapshot) =>
 value.definitionSha256 !== definition.definition?.sha256 || !sameFetch(value.fetchedDefinition, definition.definition?.fetched);
export function tableDataMatches(value: TableDataSnapshot, p: TableDataParams, definition?: TableDefinitionSnapshot): boolean {
 if (value.productId !== p.productId || value.buildIdentity !== p.buildIdentity || value.sourceRegion !== p.sourceRegion || value.tableId !== p.tableId || value.researchLocale !== p.researchLocale || value.association.researchLocale !== p.researchLocale) return false;
 if (!validScopedAdmissionRevision(value) || !validTableSourceProof(value.source)) return false;
 // D080: present only when the file's layout chose the block because no block lists the build.
 if (value.schemaMatch !== undefined && value.schemaMatch !== 'layoutOnly') return false;
 // D081: rows read with a definition fetched from WoWDBDefs name its commit as their provider revision.
 const fetched = value.fetchedDefinition;
 if (fetched !== undefined && (!fetched || !/^[0-9a-f]{40}$/.test(fetched.revision) || fetched.revision !== value.providerRevision || Number.isNaN(Date.parse(fetched.fetchedAt)))) return false;
 // The rule that chose it comes with it, and only a layout match is matched by layout.
 const because = value.fetchedBecause;
 if ((because !== undefined) !== (fetched !== undefined) || because !== undefined && (!['exactBuild','layoutNotBundled','layoutMatch'].includes(because) ||
  because === 'layoutMatch' && value.schemaMatch !== 'layoutOnly' || because === 'exactBuild' && value.schemaMatch === 'layoutOnly')) return false;
 // Whether bundled alone would still open the file comes with it, and never when bundled lacks the layout.
 if ((value.bundledFallback !== undefined) !== (fetched !== undefined) || value.bundledFallback !== undefined && (typeof value.bundledFallback !== 'boolean' || value.bundledFallback && because !== 'exactBuild')) return false;
 const textIssueCount=value.textIssueCount??'0';
 if (value.textIssueCount===null || !decimal.test(textIssueCount) || !decimal.test(value.rowCount) || BigInt(textIssueCount)>BigInt(value.rowCount)*BigInt(value.columns.filter(column=>column.family==='string'&&!column.isId).length)) return false;
 const c = value.coverage;
 if (isPartialSource(value.source) && (c.scope !== 'unencryptedSections' || !decimal.test(c.excludedKeyedSections) || BigInt(c.excludedKeyedSections) === 0n)) return false;
 if (![value.rowCount,c.declaredRecords,c.physicalRows,c.copyRows,c.declaredCopyRecords,c.excludedPhysicalRecords,c.excludedCopyRecords,c.excludedKeyedSections].every(x => decimal.test(x))) return false;
 if (BigInt(value.rowCount) !== BigInt(c.physicalRows)+BigInt(c.copyRows) || BigInt(c.declaredRecords) !== BigInt(c.physicalRows)+BigInt(c.excludedPhysicalRecords) || BigInt(c.declaredCopyRecords) !== BigInt(c.copyRows)+BigInt(c.excludedCopyRecords) || c.excludedDeclaredRecords === null || !decimal.test(c.excludedDeclaredRecords) || BigInt(c.excludedDeclaredRecords) !== BigInt(c.excludedPhysicalRecords)+BigInt(c.excludedCopyRecords)) return false;
 if (c.scope === 'complete' && [c.excludedPhysicalRecords,c.excludedCopyRecords,c.excludedKeyedSections].some(x => x !== '0')) return false;
 if (value.association.encodingSource.role !== 'encoding' || value.association.rootSource.role !== 'root' || !value.association.memberships.length || value.association.memberships.some(m => m.contentKey !== value.source.contentKey)) return false;
 if (!value.columns.every(validRawInterpretation) || !value.columns.every(validNarrowing) || new Set(value.columns.map(x => x.name)).size !== value.columns.length) return false;
 if (definition) {
  const candidate = definition.definition?.candidates.find(x => x.layoutHash === value.layoutHash);
  // A layout-only snapshot needs a definition that doesn't list the build, and an exact one a definition that does.
  if ((value.schemaMatch === 'layoutOnly') !== (definition.problem?.code === 'SCHEMA_BUILD_NOT_LISTED')) return false;
  // Fetched rows need the same fetched definition, and bundled rows the bundled one.
  const used = definition.definition?.fetched;
  if (!sameFetch(fetched, used)) return false;
  if (!candidate || value.definitionSha256 !== definition.definition?.sha256 || value.providerRevision !== (used?.revision ?? definition.provider.revision) || value.association.fileDataId !== definition.entry.db2FileDataId || candidate.fields.reduce((sum,f)=>sum+f.arrayLength,0) !== value.columns.length) return false;
  if (!candidate.fields.flatMap((f,fieldOrdinal) => Array.from({length:f.arrayLength},(_,i)=>({...f,fieldOrdinal,name:f.arrayLength>1?`${f.name}[${i}]`:f.name}))).every((f,i) => {const col=value.columns[i]; return f.name===col.name && (f.family==='int'?'integer':f.family==='float'?'float':'string')===col.family && f.isId===col.isId && (col.interpretation ? f.family==='int' && (!f.verifiedName || f.arrayLength===1&&!f.relationStorage&&!f.nonInline&&verifiedRawAdmission(value,f.fieldOrdinal,col)) && !f.isId && col.interpretation.candidateBits===(f.integerBits??32) && col.interpretation.candidateUnsigned===(f.unsigned??false) && col.reference===null : f.reference?.table===col.reference?.table && f.reference?.column===col.reference?.column) && (!col.narrowing || f.family==='int' && f.verifiedName && !f.isId && !f.relationStorage && (f.integerBits??32)===col.narrowing.usedBits);})) return false;
 }
 return true;
}
// Check the aggregate raw-byte budget before allocating any evidence buffers.
export function validTableTextIssues(row:TableDataRow,columns?:TableDataColumn[]):boolean {
 const issues=row.textIssues??[];
 if(!Array.isArray(issues)||issues.length>row.values.length)return false;
 let previous=-1,bytes=0;
 for(const issue of issues){
  if(!issue||issue.code!=='DB2_TEXT_ENCODING_INVALID'||!Number.isSafeInteger(issue.columnIndex)||issue.columnIndex<=previous||issue.columnIndex>=row.values.length||row.values[issue.columnIndex]!==''||typeof issue.rawBytesHex!=='string'||issue.rawBytesHex.length<2||issue.rawBytesHex.length>131070||issue.rawBytesHex.length%2!==0||!/^[0-9a-f]+$/.test(issue.rawBytesHex))return false;
  if(columns&&(columns[issue.columnIndex]?.family!=='string'||columns[issue.columnIndex].isId))return false;
  previous=issue.columnIndex;bytes+=issue.rawBytesHex.length/2;if(bytes>65535)return false;
 }
 for(const issue of issues){const raw=new Uint8Array(issue.rawBytesHex.length/2);for(let i=0;i<raw.length;i++){raw[i]=Number.parseInt(issue.rawBytesHex.slice(i*2,i*2+2),16);if(raw[i]===0)return false;}try{strictUtf8.decode(raw);return false;}catch{/* Exact source bytes are malformed UTF-8, as declared. */}}
 return true;
}
export function tableRowsMatch(value: TableDataPage, p: TableRowsParams, snapshot?: TableDataSnapshot): boolean {
 if (value.productId!==p.productId || value.buildIdentity!==p.buildIdentity || value.sourceRegion!==p.sourceRegion || value.tableId!==p.tableId || value.researchLocale!==p.researchLocale || value.artifactId!==p.artifactId || !decimal.test(value.totalCount) || value.rows.length>(p.limit??50)) return false;
 if (utf8.encode(JSON.stringify(value)).byteLength>512*1024) return false;
 if (snapshot && (value.artifactId!==snapshot.artifactId || BigInt(value.totalCount)>BigInt(snapshot.rowCount) || p.recordId===undefined && value.totalCount!==snapshot.rowCount)) return false;
 let textIssues=0;
 for (const row of value.rows) {
  if (!validRecordId(row.id) || row.isCopy !== (row.copySourceId!==null) || row.copySourceId!==null && !validRecordId(row.copySourceId) || p.recordId!==undefined && row.id!==p.recordId || snapshot && row.values.length!==snapshot.columns.length || utf8.encode(JSON.stringify(row)).byteLength>256*1024 || !validTableTextIssues(row,snapshot?.columns)) return false;
  textIssues+=row.textIssues?.length??0;
 }
 if(snapshot&&BigInt(textIssues)>BigInt(snapshot.textIssueCount??'0'))return false;
 const start=BigInt(p.after??'0'), end=start+BigInt(value.rows.length), total=BigInt(value.totalCount);
 return !value.rows.length ? start>=total && value.nextCursor===null : end<=total && (end<total ? value.nextCursor===end.toString() : value.nextCursor===null);
}
