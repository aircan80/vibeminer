// @vitest-environment node
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { EngineClient, type EngineProcess } from '../src/main/engine-client';
import { buildCatalogMatches, buildCatalogRefreshMatches, validCatalogParams } from '../src/shared/build-catalog';
import { validRequestId } from '../src/shared/bridge';
import listExample from '../../../contracts/examples/build-catalog.json';
import refreshExample from '../../../contracts/examples/build-catalog-refresh.json';
import type { BuildCatalog, BuildCatalogRefresh } from '../../../contracts/generated/discovery';
class Child extends EventEmitter implements EngineProcess {
  stdout = new PassThrough(); stderr = new PassThrough();
  requests: { id: string; method: string; params: unknown }[] = [];
  stdin = new Writable({ write: (chunk, _encoding, done) => { this.requests.push(JSON.parse(chunk.toString())); done(); } });
  kill = vi.fn(() => true);
  reply(id: string, result: unknown) { this.stdout.write(JSON.stringify({ protocolVersion: 1, id, result }) + '\n'); }
  progress(requestId: string, sequence: number, completed: number) { this.stdout.write(JSON.stringify({ protocolVersion: 1, type: 'progress', requestId, sequence, stage: 'Checking the version service', region: null, completed, total: 14, unit: 'products' }) + '\n'); }
}
const children: Child[] = [];
afterEach(() => { for (const child of children.splice(0)) child.emit('exit', 0); vi.useRealTimers(); });
async function setup(capabilities = ['build-catalog-v1']) {
  const child = new Child(); children.push(child);
  const client = new EngineClient(child), hello = client.hello();
  child.reply(child.requests[0].id, { protocolVersion: 1, engineVersion: 'authored', capabilities });
  await hello;
  return { child, client };
}
const list = () => structuredClone(listExample) as BuildCatalog;
const refreshed = () => structuredClone(refreshExample) as BuildCatalogRefresh;
describe('build catalog parameters (main and engine client)', () => {
  it('accepts exactly {} and a valid request id for refresh', () => {
    expect(validCatalogParams({})).toBe(true);
    for (const bad of [null, undefined, [], 'x', { productId: 'wow' }, { limit: 1 }]) expect(validCatalogParams(bad)).toBe(false);
    expect(validRequestId('refresh-1')).toBe(true);
    expect(validRequestId('../x')).toBe(false);
  });
  it('refuses parameters and engines without build-catalog-v1 before writing', async () => {
    const { child, client } = await setup([]);
    await expect(client.request('catalog.list', {})).rejects.toMatchObject({ code: 'UNSUPPORTED' });
    const capable = await setup();
    await expect(capable.client.request('catalog.list', { productId: 'wow' })).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
    await expect(capable.client.request('catalog.refresh', { all: true }, 'r')).rejects.toMatchObject({ code: 'INVALID_REQUEST' });
    expect(child.requests).toHaveLength(1);
    expect(capable.child.requests).toHaveLength(1);
  });
});
describe('build catalog replies', () => {
  it('accepts the examples and refuses replies the schema or the list rules refuse', async () => {
    expect(buildCatalogMatches(list())).toBe(true);
    expect(buildCatalogRefreshMatches(refreshed())).toBe(true);
    const { child, client } = await setup();
    const ok = client.request('catalog.list', {}, 'list');
    child.reply('list', list());
    await expect(ok).resolves.toMatchObject({ access: 'writer', products: expect.any(Array) });
    for (const change of [
      (v: BuildCatalog) => { v.products[1] = v.products[0]; },
      (v: BuildCatalog) => { v.products[0].builds[1] = v.products[0].builds[0]; },
      (v: BuildCatalog) => { v.products[0].buildCount = 1; },
      (v: BuildCatalog) => { v.products[0].builds[0].regions = ['us', 'us']; },
      (v: BuildCatalog) => { v.products[0].builds[0].firstSeenAt = '2027-01-01T00:00:00Z'; },
      (v: BuildCatalog) => { (v as unknown as { extra: boolean }).extra = true; },
    ]) {
      const other = await setup(), value = list(); change(value);
      const work = other.client.request('catalog.list', {}, 'bad').catch(e => e.code);
      other.child.reply('bad', value);
      expect(await work).toBe('PROTOCOL_ERROR');
    }
    for (const change of [
      (v: BuildCatalogRefresh) => { v.catalog.access = 'readOnly'; },
      (v: BuildCatalogRefresh) => { v.failures[0].productId = 'not_reviewed'; },
      (v: BuildCatalogRefresh) => { v.failures[0].document = 'summary'; },
    ]) {
      const other = await setup(), value = refreshed(); change(value);
      const work = other.client.request('catalog.refresh', {}, 'bad').catch(e => e.code);
      other.child.reply('bad', value);
      expect(await work).toBe('PROTOCOL_ERROR');
    }
  });
  it('forwards refresh progress and allows a refresh longer than the ordinary reply limit', async () => {
    const { child, client } = await setup();
    vi.useFakeTimers();
    const seen: unknown[] = [];
    client.on('progress', p => seen.push(p));
    const work = client.request('catalog.refresh', {}, 'refresh');
    child.progress('refresh', 1, 0);
    await vi.advanceTimersByTimeAsync(200);
    expect(seen).toMatchObject([{ requestId: 'refresh', completed: 0, total: 14, unit: 'products' }]);
    await vi.advanceTimersByTimeAsync(5 * 60 * 1000);
    expect(client.hasFailed).toBe(false);
    child.reply('refresh', refreshed());
    await expect(work).resolves.toMatchObject({ checked: 13 });
  });
});
