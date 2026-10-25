import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { afterEach, expect, it, vi } from 'vitest';
import type { ItemFactsResult, ItemLabelLists, ItemsSnapshot } from '../../../contracts/generated/discovery';
import { itemsSnapshot, itemFacts, itemLabelLists } from '../../../tests/desktop/fixtures/items.mjs';
import factsExample from '../../../contracts/examples/item-facts.json';
import labelsExample from '../../../contracts/examples/item-labels.json';
import { itemFactKeys, itemFactsMatches, itemLabelListsMatches, validItemLabelsParams } from '../src/shared/items';
import { EngineClient, type EngineProcess } from '../src/main/engine-client';

// item-facts-v1 (#61): the desktop accepts only the engine's exact reviewed projection for the requested item.
const snapshot = () => itemsSnapshot() as ItemsSnapshot;
const params = (s: ItemsSnapshot, itemId = '3') => ({ ...s.context, artifactId: s.artifactId, itemId });
const facts = (s: ItemsSnapshot, itemId = '3') => structuredClone(itemFacts(s, { itemId })) as ItemFactsResult;

it('accepts the authored projection, the example and items outside the admitted scope', () => {
  const s = snapshot();
  expect(itemFactsMatches(facts(s), params(s), s)).toBe(true);
  expect(itemFactsMatches(facts(s, '58'), params(s, '58'), s)).toBe(true);
  expect(facts(s, '58').sparse).toBe('missing');
  expect(itemFactsMatches(facts(s, '77'), params(s, '77'), s)).toBe(true);
  expect(itemFactsMatches(facts(s, '9'), params(s, '9'), s)).toBe(true);
  const example = factsExample as ItemFactsResult;
  expect(itemFactsMatches(example, { ...example.context, artifactId: example.artifactId, itemId: example.itemId })).toBe(true);
  expect(example.facts!.map(f => f.key)).toEqual([...itemFactKeys]);
});

it.each([
  ['another item', (r: ItemFactsResult) => { r.itemId = '4'; }],
  ['another artifact', (r: ItemFactsResult) => { r.artifactId = 'f'.repeat(64); }],
  ['an unknown policy', (r: ItemFactsResult) => { (r as { policyRevision: string }).policyRevision = 'item-facts-v2'; }],
  ['reordered keys', (r: ItemFactsResult) => { r.facts!.reverse(); }],
  ['a dropped key', (r: ItemFactsResult) => { r.facts!.pop(); }],
  ['a renamed column', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'quality')!.values[0]!.column = 'ItemLevel'; }],
  ['a shifted column index', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'quality')!.values[0]!.columnIndex = 4; }],
  ['a cell of another record', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'quality')!.values[0]!.recordId = '4'; }],
  ['a table name that disagrees with its ID', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'quality')!.values[0]!.table = 'Item'; }],
  ['the same cell twice', (r: ItemFactsResult) => { const q = r.facts!.find(f => f.key === 'quality')!; r.facts!.find(f => f.key === 'itemLevel')!.values = [...q.values]; }],
  ['a missing state with cells', (r: ItemFactsResult) => { const q = r.facts!.find(f => f.key === 'quality')!; q.state = 'missing'; }],
  ['an unsupported state with cells', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'quality')!.state = 'unsupported'; }],
  ['an agreement from one table', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'quality')!.agreement = 'agree'; }],
  ['a Sparse state that contradicts coverage', (r: ItemFactsResult) => { r.sparse = 'unknown'; }],
  ['an unknown agreement', (r: ItemFactsResult) => { (r.facts!.find(f => f.key === 'inventoryType')! as { agreement: string }).agreement = 'merged'; }],
  ['agree over different values', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'inventoryType')!.agreement = 'agree'; }],
  ['separate over equal counts', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'inventoryType')!.agreement = 'separate'; }],
  ['single over both tables', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'inventoryType')!.agreement = 'single'; }],
  ['ItemSparse cells before Item cells', (r: ItemFactsResult) => { r.facts!.find(f => f.key === 'inventoryType')!.values.reverse(); }],
])('rejects %s', (_name, change) => {
  const s = snapshot(), r = facts(s);
  change(r);
  expect(itemFactsMatches(r, params(s), s)).toBe(false);
});

it('rejects ItemSparse cells when the item has no ItemSparse row, and Partial misreported as Missing', () => {
  const s = snapshot(), missing = facts(s, '58');
  missing.facts!.find(f => f.key === 'description')!.values = [{ tableId: '919be54e', table: 'ItemSparse', column: 'Description_lang', columnIndex: 2, recordId: '58', value: 'x', label: null }];
  missing.facts!.find(f => f.key === 'description')!.state = 'value';
  expect(itemFactsMatches(missing, params(s, '58'), s)).toBe(false);
  const partial = snapshot(); partial.sparseSource.coverage.scope = 'unencryptedSections';
  expect(itemFactsMatches(facts(s, '58'), params(partial, '58'), partial)).toBe(false);
});

it('accepts a Sparse-only row outside the Item scope, and copies of different shapes kept separate', () => {
  const s = snapshot(), outside = facts(s, '77');
  expect(outside.facts).toBeNull();
  // The engine looks the ItemSparse row up for an ID outside the Item scope: a Sparse-only row is present without facts.
  expect(itemFactsMatches({ ...outside, sparse: 'present' }, params(s, '77'), s)).toBe(true);
  // Classic shape: Item.DamageType[0–1] beside one ItemSparse.DamageType stays separate, cells Item first.
  const classic = snapshot(), column = (name: string) => ({ ...structuredClone(classic.itemSource.columns[1]!), name, reference: null });
  classic.itemSource.columns.push(column('DamageType[0]'), column('DamageType[1]'));
  classic.sparseSource.columns.push({ ...column('DamageType') });
  const r = facts(classic), damage = r.facts!.find(f => f.key === 'damageType')!;
  expect(damage.agreement).toBe('separate');
  expect(damage.values.map(v => v.table)).toEqual(['Item', 'Item', 'ItemSparse']);
  expect(itemFactsMatches(r, params(classic), classic)).toBe(true);
  damage.agreement = 'disagree';
  expect(itemFactsMatches(r, params(classic), classic)).toBe(false);
});

it('rejects a Sparse state the renderer can contradict from coverage even when every fact agrees with it', () => {
  // Item 58 has no ItemSparse row in a complete source: an engine saying "unknown" everywhere is still wrong.
  const s = snapshot(), r = facts(s, '58');
  r.sparse = 'unknown'; for (const fact of r.facts!) if (fact.state === 'missing') fact.state = 'unknown';
  expect(itemFactsMatches(r, params(s, '58'))).toBe(true);
  expect(itemFactsMatches(r, params(s, '58'), s)).toBe(false);
});

it('accepts ascending reviewed label lists and nothing looser', () => {
  expect(itemLabelListsMatches(labelsExample as ItemLabelLists)).toBe(true);
  expect(itemLabelListsMatches(itemLabelLists as ItemLabelLists)).toBe(true);
  for (const change of [
    (l: ItemLabelLists) => { (l as { labelRevision: string }).labelRevision = 'item-labels-v2'; },
    (l: ItemLabelLists) => { l.quality.reverse(); },
    (l: ItemLabelLists) => { l.bonding[0].value = '00'; },
    (l: ItemLabelLists) => { l.trigger[0].label = ''; },
  ]) { const l = structuredClone(itemLabelLists) as ItemLabelLists; change(l); expect(itemLabelListsMatches(l)).toBe(false); }
  expect(validItemLabelsParams({})).toBe(true);
  expect(validItemLabelsParams({ locale: 'deDE' })).toBe(false);
});

class Child extends EventEmitter implements EngineProcess {
  stdout = new PassThrough(); stderr = new PassThrough();
  requests: { id: string; method: string }[] = [];
  stdin = new Writable({ write: (chunk, _encoding, done) => { this.requests.push(JSON.parse(chunk.toString())); done(); } });
  kill = vi.fn(() => true);
  reply(id: string, result: unknown) { this.stdout.write(JSON.stringify({ protocolVersion: 1, id, result }) + '\n'); }
}
const children: Child[] = [];
afterEach(() => { for (const child of children.splice(0)) child.emit('exit', 0); });
async function client(capabilities: string[]) {
  const child = new Child(); children.push(child); const value = new EngineClient(child), hello = value.hello();
  child.reply(child.requests[0].id, { protocolVersion: 1, engineVersion: 'authored', capabilities }); await hello;
  return { child, value };
}

it('refuses item facts and labels from an engine without item-facts-v1 before sending anything', async () => {
  const s = snapshot(), { child, value } = await client(['items-v1']);
  await expect(value.request('items.facts', params(s), 'facts')).rejects.toMatchObject({ code: 'UNSUPPORTED' });
  await expect(value.request('items.labels', {}, 'labels')).rejects.toMatchObject({ code: 'UNSUPPORTED' });
  expect(child.requests.map(r => r.method)).toEqual(['hello']);
});

it('resolves the exact requested facts and labels and rejects a reply for another item', async () => {
  const s = snapshot(), { child, value } = await client(['items-v1', 'item-facts-v1']);
  const good = value.request('items.facts', params(s), 'facts'); child.reply('facts', facts(s));
  await expect(good).resolves.toMatchObject({ itemId: '3', sparse: 'present' });
  const labels = value.request('items.labels', {}, 'labels'); child.reply('labels', itemLabelLists);
  await expect(labels).resolves.toMatchObject({ labelRevision: 'item-labels-v1' });
  await expect(value.request('items.labels', { locale: 'deDE' }, 'bad')).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
  // A schema-valid reply for another item is an invalid engine message, never the pending result.
  const wrong = value.request('items.facts', params(s), 'wrong'); child.reply('wrong', facts(s, '4'));
  await expect(wrong).rejects.toBeTruthy();
});

it('rejects in the main process a reply whose Sparse state contradicts its own missing or unknown facts', async () => {
  const s = snapshot();
  for (const [claimed, actual] of [['unknown', 'missing'], ['missing', 'unknown']] as const) {
    const { child, value } = await client(['items-v1', 'item-facts-v1']);
    const r = facts(s, '58'); r.sparse = claimed;
    for (const fact of r.facts!) if (fact.state === 'missing' || fact.state === 'unknown') fact.state = actual;
    const reply = value.request('items.facts', params(s, '58'), 'contradiction'); child.reply('contradiction', r);
    await expect(reply).rejects.toBeTruthy();
  }
  const { child, value } = await client(['items-v1', 'item-facts-v1']);
  const consistent = value.request('items.facts', params(s, '58'), 'consistent'); child.reply('consistent', facts(s, '58'));
  await expect(consistent).resolves.toMatchObject({ sparse: 'missing' });
});
