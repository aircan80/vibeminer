import { useEffect, useRef, useState } from 'react';
import type { TableDataPage, TableDataSnapshot, TableDefinitionSnapshot, TableReferenceTextColumn, TableReferenceTextPage, TableReferenceTextValue } from '../../../../contracts/generated/discovery';
import type { DiscoveryBridge } from '../shared/bridge';
import type { TableRowsParams } from '../shared/table-data';
import { referenceTextMatches } from '../shared/reference-text';
import { tableDefinitionMatches } from '../shared/tables';
import { tableIntentKey, type TableLatch, type TableLoading } from './useTableLoading';
import { exactCount } from '../shared/inventory';

export type Failure = { code: string; message: string; retryable: boolean };
/** What one reference cell shows. `cell` is the text after "value · "; null means the number only. */
export type ReferenceTextView = { cell: string | null; pending: boolean; sr: string | null; text: string | null; detail: string; source: string | null; styled: boolean; load?: TextLoad | null };
/** A large text table that loads only when asked (owner decision on #17). */
export type TextLoad = { tableId: string; table: string; label: string; size: string; busy: boolean; loading: boolean;
 /** Why the button is unavailable right now (busy states only), shown as text next to it. */
 reason: string | null;
 /** The notice: a plain-words heading, the typed code shown separately as <code>, and one detail sentence. */
 title: string; code: string | null; detail: string;
 /** Set after a Stop or a failed load: what happened, and whether Retry can help. */
 outcome: { kind: 'stopped' | 'failed'; retryable: boolean } | null;
 /** The text table's definition couldn't be read; the button asks for it again. */
 definitionFailed: boolean;
 /** Whether a button is drawn at all. A failure that Retry can't change has none. */
 actionable: boolean };
export const approximateSize = (bytes: string) => `≈${(Number(bytes) / 1e6).toFixed(1)} MB`;
const PROTECTED = new Set(['BLTE_ENCRYPTED', 'BLTE_KEY_UNAVAILABLE']);
const UNSUPPORTED = new Set(['SCHEMA_MISMATCH', 'SCHEMA_BUILD_NOT_LISTED', 'SCHEMA_MISSING', 'SCHEMA_AMBIGUOUS', 'SCHEMA_LAYOUT_UNVERIFIED', 'DB2_SCHEMA_MISSING', 'DB2_SCHEMA_AMBIGUOUS', 'DB2_SCHEMA_MISMATCH', 'DB2_UNSUPPORTED', 'BLTE_UNSUPPORTED', 'TABLE_UNSUPPORTED']);

/** Pure wording for one reference cell. Every state has its own words; nothing falls back to another text. */
export function describeReferenceText(column: TableReferenceTextColumn, value: TableReferenceTextValue | undefined, ctx: { loading: boolean; latch: TableLatch | null; definitionFailure?: Failure | null }): ReferenceTextView | null {
 const ref = `${column.reference.table}.${column.reference.column}`, s = column.textSource, code = column.problem?.code;
 const plain = (detail: string): ReferenceTextView => ({ cell: null, pending: false, sr: null, text: null, detail, source: null, styled: false });
 const cell = (text: string, detail: string, extra: Partial<ReferenceTextView> = {}): ReferenceTextView => ({ cell: text, pending: false, sr: null, text: null, detail, source: null, styled: true, ...extra });
 if (column.state === 'noTextRule') return plain(`${column.reference.table} has no reviewed text column; number only.`);
 if (!s) return plain(`${column.reference.table} definition not bundled; number only.`);
 const field = `${s.table}.${s.textColumn}`, from = s.table === column.reference.table ? '' : ` Declared target ${ref}; text comes from ${field} by the same ID.`;
 switch (column.state) {
  case 'noDefinition': return plain(`${s.table} definition not bundled; number only.`);
  case 'unsupported': return cell('unsupported layout', `${field} can't be read from the admitted ${s.table} layout${code ? ` (${code})` : ''}.${from}`);
  case 'unindexed': return cell('unindexed', `${s.table}.${s.matchColumn} isn't the record ID and has no value index, so no scan was run.`);
  case 'unreadable': return cell('not readable', `${s.table} text could not be read${code ? ` (${code})` : ''}. Raw values are unaffected.`);
  case 'notPrepared': {
   const latch = ctx.latch;
   if (ctx.definitionFailure) return cell('text unavailable', `The ${s.table} definition could not be read (${ctx.definitionFailure.code}), so its text can't load yet. Use Retry ${s.table} definition.${from}`);
   if (latch?.kind === 'stopped') return cell('loading stopped', `${s.table} loading was stopped, so its text isn't loaded. Use Retry ${s.table} text.${from}`);
   if (latch?.kind === 'failed') {
    const failure = latch.error?.code ?? 'TABLE_DATA_UNAVAILABLE';
    if (PROTECTED.has(failure)) return cell('not readable (protected)', `${s.table} is protected and could not be read (${failure}).${from}`);
    if (UNSUPPORTED.has(failure)) return cell('unsupported layout', `The ${s.table} layout isn't supported for this build (${failure}).${from}`);
    return cell('text unavailable', `${s.table} could not be loaded (${failure}).${latch.error?.retryable === false ? '' : ` Use Retry ${s.table} text.`}${from}`);
   }
   if (s.loadPolicy === 'onRequest' && !ctx.loading) return cell('not loaded', `${field} comes from ${s.table} (${approximateSize(s.measuredEncodedBytes)} encoded, measured on 12.1.5.70448), which loads only when you choose Load text.${from}`);
   return cell('…', `Text not prepared yet · ${field} fills in when ${s.table} is ready.${ctx.loading ? ` ${s.table} is loading now.` : ''}${from}`, { pending: true, sr: ctx.loading ? `${s.table} text loading` : 'text not prepared yet' });
  }
 }
 if (!value) return null;
 const row = value.row, at = row ? `row ID ${row.id}${row.isCopy ? ` (copy of ${row.copySourceId})` : ''}` : '', source = row ? `${field} · ${at} · enUS` : null;
 switch (value.state) {
  case 'resolved': return cell(value.text!, `${value.text}${from}`, { text: value.text, source });
  case 'empty': return cell('empty text', `${field} is empty on ${at}; no fallback text is shown.${from}`, { source });
  case 'unreadable': return cell('text not readable', `${field} on ${at} is not valid UTF-8; no text was inferred.${from}`, { source });
  case 'textTooLong': return cell('text too long', `${field} on ${at} is longer than 4,096 characters, so it isn't shown here.${from}`, { source });
  case 'multiple': return cell(`${exactCount(value.matchCount)} matches`, `${exactCount(value.matchCount)} ${s.table} rows have ${s.matchColumn} = ${value.value}; none is picked.`);
  case 'keyOutOfRange': return cell('out of range', `${value.value} can't be a ${s.table}.${s.matchColumn} value, so nothing was matched.`);
  case 'notFound': return s.scope === 'unencryptedSections'
   ? cell(`not in readable ${s.table} rows`, `No readable ${s.table} row has ${s.matchColumn} = ${value.value}. Protected sections are not loaded, so this isn't an absence.`)
   : cell(`not found in ${s.table}`, `No admitted ${s.table} row has ${s.matchColumn} = ${value.value}.`);
 }
 return null;
}

/**
 * Batched reference text for the page on screen. One request per page; a reply applies only to the exact page it was asked for.
 * An unprepared text table is prepared through the normal table flow, then the page asks again. Completion never moves focus.
 */
export function useReferenceText({ bridge, flow, active, snapshot, page, query }: { bridge: DiscoveryBridge; flow: TableLoading; active: boolean; snapshot: TableDataSnapshot | null; page: TableDataPage | null; query: TableRowsParams | null }) {
 const [result, setResult] = useState<{ key: string; reply: TableReferenceTextPage | null; error: Failure | null; seq: number; retrying?: boolean } | null>(null);
 const sequence = useRef(0);
 const [definitions, setDefinitions] = useState<Record<string, TableDefinitionSnapshot>>({});
 // A failed definition read is kept, shown and retried; it never silently disables Load text.
 const [definitionFailures, setDefinitionFailures] = useState<Record<string, Failure>>({}), [definitionRetry, setDefinitionRetry] = useState(0);
 const [revision, setRevision] = useState(0);
 const generation = useRef(0), alive = useRef(true), asked = useRef(new Set<string>()), started = useRef(new Set<string>());
 useEffect(() => { alive.current = true; return () => { alive.current = false; generation.current++; }; }, []);
 const usable = !!snapshot && !!page && !!query && page.artifactId === snapshot.artifactId && snapshot.columns.some(c => c.reference) && !!bridge.tableReferenceText;
 const key = usable ? [snapshot!.productId, snapshot!.buildIdentity, snapshot!.sourceRegion, snapshot!.tableId, snapshot!.artifactId, query!.after ?? '0', query!.recordId ?? '', query!.limit ?? 50, page!.rows.map(r => r.id).join(',')].join('|') : null;
 useEffect(() => {
  const token = ++generation.current;
  if (!key || !usable) { setResult(null); return; }
  setResult(old => old?.key === key ? (old.error ? { ...old, retrying: true } : old) : null);
  const asking = query!, shown = page!, admitted = snapshot!;
  void bridge.tableReferenceText!(asking).then(reply => {
   if (!alive.current || token !== generation.current) return;
   if (!reply.ok) { setResult({ key, reply: null, error: reply.error, seq: ++sequence.current }); return; }
   if (!referenceTextMatches(reply.value, asking, shown, admitted)) { setResult({ key, reply: null, error: { code: 'TABLE_REFERENCE_TEXT_MISMATCH', message: 'Reference text does not match this exact page. Raw values are unaffected.', retryable: false }, seq: ++sequence.current }); return; }
   setResult({ key, reply: reply.value, error: null, seq: ++sequence.current });
  }).catch(() => { if (alive.current && token === generation.current) setResult({ key, reply: null, error: { code: 'ENGINE_UNAVAILABLE', message: 'Reference text could not be read.', retryable: true }, seq: ++sequence.current }); });
 }, [key, revision]);
 const reply = result && result.key === key ? result.reply : null;
 const pending = (reply?.columns ?? []).filter(c => c.state === 'notPrepared');
 const pendingTables = [...new Set(pending.map(c => c.textSource!.tableId))];
 const automaticTables = [...new Set(pending.filter(c => c.textSource!.loadPolicy === 'automatic').map(c => c.textSource!.tableId))];
 // Read each pending text table's definition once, so the ordinary table flow can prepare it.
 useEffect(() => {
  if (!active || !snapshot) return;
  for (const tableId of pendingTables) {
   const ask = [snapshot.productId, snapshot.buildIdentity, snapshot.sourceRegion, tableId].join('|');
   if (definitions[tableId] || asked.current.has(ask)) continue;
   asked.current.add(ask);
   const params = { productId: snapshot.productId, buildIdentity: snapshot.buildIdentity, sourceRegion: snapshot.sourceRegion, tableId };
   const fail = (failure: Failure) => { if (!alive.current) return; asked.current.delete(ask); setDefinitionFailures(old => ({ ...old, [tableId]: failure })); };
   void bridge.tableDefinition(params).then(r => {
    if (!alive.current) return;
    if (!r.ok) { fail(r.error); return; }
    if (!r.value.definition || !tableDefinitionMatches(r.value, params)) { fail({ code: r.value.problem?.code ?? 'TABLE_DEFINITION_MISMATCH', message: r.value.problem?.message ?? 'The text table definition does not match this exact build.', retryable: !r.value.problem }); return; }
    setDefinitionFailures(old => { const { [tableId]: _, ...rest } = old; return rest; });
    setDefinitions(old => ({ ...old, [tableId]: r.value }));
   }).catch(() => fail({ code: 'ENGINE_UNAVAILABLE', message: 'The text table definition could not be read.', retryable: true }));
  }
 }, [active, reply, snapshot?.artifactId, definitionRetry]);
 const definitionFailure = (tableId: string) => definitions[tableId] ? null : definitionFailures[tableId] ?? null;
 const retryDefinition = (tableId: string) => { setDefinitionFailures(old => { const { [tableId]: _, ...rest } = old; return rest; }); setDefinitionRetry(x => x + 1); };
 const definitionFor = (tableId: string) => { const d = definitions[tableId]; return d && snapshot && d.productId === snapshot.productId && d.buildIdentity === snapshot.buildIdentity && d.sourceRegion === snapshot.sourceRegion ? d : undefined; };
 // Start one pending text table at a time, only while this view is on screen and nothing else is loading.
 useEffect(() => {
  if (!active || flow.pending) return;
  for (const tableId of automaticTables) {
   const d = definitionFor(tableId); if (!d || flow.blocked(d)) continue;
   const intent = tableIntentKey(d); if (started.current.has(intent)) continue;
   if (flow.load(d)) { started.current.add(intent); return; }
  }
 }, [active, reply, definitions, flow.pending, flow.latchRevision]);
 // A text table becoming ready refills this page in place.
 const op = flow.operation;
 useEffect(() => { if (op?.status === 'succeeded' && op.result && pendingTables.includes(op.result.tableId) && definitionFor(op.result.tableId) && tableIntentKey(op.definition) === tableIntentKey(definitionFor(op.result.tableId)!)) setRevision(x => x + 1); }, [op?.id, op?.status]);
 const columns = new Map((reply?.columns ?? []).map(c => [c.columnIndex, c]));
 const values = new Map((reply?.values ?? []).map(v => [`${v.columnIndex}:${v.value}`, v]));
 function view(columnIndex: number, value: string): ReferenceTextView | null {
  const column = columns.get(columnIndex); if (!column) return null;
  const s = column.textSource, d = s ? definitionFor(s.tableId) : undefined;
  const loading = !!d && !!op && ['pending', 'stopping'].includes(op.status) && tableIntentKey(op.definition) === tableIntentKey(d);
  const latch = d ? flow.blocked(d) : null, defFailure = s ? definitionFailure(s.tableId) : null, view = describeReferenceText(column, values.get(`${columnIndex}:${value}`), { loading, latch, definitionFailure: defFailure });
  return view && column.state === 'notPrepared' && s && (s.loadPolicy === 'onRequest' || latch || defFailure) ? { ...view, load: textLoad(s.tableId) } : view;
 }
 // A text table the user has to act on: a large one not yet requested, or any one whose load was stopped or failed.
 function textLoad(tableId: string): TextLoad | null {
  const column = pending.find(c => c.textSource!.tableId === tableId), s = column?.textSource; if (!s) return null;
  const d = definitionFor(tableId), latch = d ? flow.blocked(d) : null, defFailure = definitionFailure(tableId);
  if (!latch && !defFailure && s.loadPolicy !== 'onRequest') return null;
  const loading = !!d && !!op && ['pending', 'stopping'].includes(op.status) && tableIntentKey(op.definition) === tableIntentKey(d);
  const size = approximateSize(s.measuredEncodedBytes), other = flow.pending && !loading;
  if (defFailure) return { tableId, table: s.table, size, label: `Retry ${s.table} definition`, loading: false, busy: false, reason: null,
   title: `${s.table} text not loaded`, code: defFailure.code, detail: `The ${s.table} definition could not be read, so its text can't load yet.`, outcome: null, definitionFailed: true, actionable: true };
  const reason = loading ? null : !d ? `Reading the ${s.table} definition…` : other ? 'Another table is loading. This becomes available when it finishes.' : null;
  let outcome: TextLoad['outcome'] = null, title = `${s.table} text not loaded`, code: string | null = null,
   detail = `References show numbers until you load ${s.table} (${size} encoded, measured on 12.1.5.70448). It loads like any table and stays local.`;
  if (latch?.kind === 'stopped') { outcome = { kind: 'stopped', retryable: true }; title = `${s.table} text not loaded · loading stopped`; detail = `You stopped loading ${s.table}. References show numbers until it loads.`; }
  else if (latch) {
   code = latch.error?.code ?? 'TABLE_DATA_UNAVAILABLE';
   const retryable = latch.error?.retryable !== false && !PROTECTED.has(code) && !UNSUPPORTED.has(code);
   outcome = { kind: 'failed', retryable };
   title = PROTECTED.has(code) ? `${s.table} text not readable (protected)` : UNSUPPORTED.has(code) ? `${s.table} text: unsupported layout` : `${s.table} text could not be loaded`;
   detail = `Previously verified content is preserved. References show numbers.${retryable ? '' : ' Retry wouldn\u2019t change this.'}`;
  }
  const label = loading ? `Loading ${s.table} text…` : outcome ? `Retry ${s.table} text` : `Load text (${size})`;
  return { tableId, table: s.table, size, label, loading, busy: loading || !d || other, reason, title, code, detail, outcome, definitionFailed: false, actionable: !outcome || outcome.retryable };
 }
 const onRequest = [...new Set(pending.map(c => c.textSource!.tableId))].map(textLoad).filter((x): x is TextLoad => !!x);
 /** Explicit Load text or Retry: the ordinary table preparation at interactive priority. */
 function requestLoad(tableId: string) {
  if (definitionFailure(tableId)) { retryDefinition(tableId); return true; }
  const d = definitionFor(tableId); if (!d || flow.pending) return false;
  if (flow.blocked(d)) { const latch = flow.blocked(d)!; if (latch.kind === 'failed' && latch.error && !latch.error.retryable) return false; flow.retry(d); }
  started.current.add(tableIntentKey(d)); return !!flow.load(d);
 }
 const error = result && result.key === key && result.error && result.error.code !== 'UNSUPPORTED' ? result.error : null;
 const retrying = !!error && !!result?.retrying;
 /** Ask again for the same page after a retryable batch failure. */
 const retry = () => { if (!error?.retryable || retrying) return; for (const tableId of Object.keys(definitionFailures)) retryDefinition(tableId); setRevision(x => x + 1); };
 // Changes whenever any view could change, including a new reply for the same page.
 const stamp = [key, result?.key === key ? `${result.seq}:${result.retrying ? 'retry' : ''}` : '-', Object.keys(definitions).join(','), Object.entries(definitionFailures).map(([t, f]) => `${t}:${f.code}`).join(','), op?.id, op?.status, flow.pending, flow.latchRevision].join('|');
 return { view, error, retrying, retry, ready: !!reply, stamp, onRequest, requestLoad };
}
