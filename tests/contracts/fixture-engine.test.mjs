import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import os from 'node:os';
import path from 'node:path';

// The authored engine the Electron tests launch (tests/desktop/fixtures/engine.mjs). Those tests write its JSON
// control files with a plain writeFile, so it can read one empty or half written (#118).
const engine = fileURLToPath(new URL('../desktop/fixtures/engine.mjs', import.meta.url));
const DEADLINE_MS = 30_000;

async function withEngine(run) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'vibeminer-fixture-engine-'));
  const child = spawn(process.execPath, [engine, '--data-dir', path.join(directory, 'catalog')], { env: { ...process.env, VIBEMINER_FIXTURE_CONTROL_DIR: directory }, stdio: ['pipe', 'pipe', 'pipe'] });
  const exited = new Promise(resolve => child.on('exit', code => resolve(code)));
  const pending = new Map();
  createInterface({ input: child.stdout }).on('line', line => { const message = JSON.parse(line); pending.get(message.id)?.(message); pending.delete(message.id); });
  let next = 0;
  // Resolves with the reply, or with the exit code if the engine stops first.
  const request = (method, params = {}) => {
    const id = `t${++next}`;
    const reply = new Promise(resolve => pending.set(id, resolve));
    child.stdin.write(JSON.stringify({ protocolVersion: 1, id, method, params }) + '\n');
    let timer;
    const deadline = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(`${method} got no reply`)), DEADLINE_MS); });
    return Promise.race([reply, exited.then(code => ({ exited: code })), deadline]).finally(() => clearTimeout(timer));
  };
  try { await run({ directory, request }); }
  finally { child.kill(); await exited; await rm(directory, { recursive: true, force: true }); }
}

const build = { productId: 'wow', buildIdentity: 'd'.repeat(64) };

test('a stored-summary.json still being written reads as not written yet instead of stopping the engine', async () => {
  await withEngine(async ({ directory, request }) => {
    const { revision } = (await request('download.summaries', { builds: [build] })).result;
    for (const partial of ['', '{"storedObjects":"3']) {
      await writeFile(path.join(directory, 'stored-summary.json'), partial);
      const reply = await request('download.summaries', { builds: [build] });
      assert.equal(reply.exited, undefined, `the engine stopped on ${JSON.stringify(partial)}`);
      // Nothing has landed yet, so a standing download.changes wait has nothing to answer.
      assert.equal(reply.result?.revision, revision, JSON.stringify(reply));
    }
    await writeFile(path.join(directory, 'stored-summary.json'), JSON.stringify({ storedObjects: '30', storedBytes: '3000' }));
    const landed = (await request('download.summaries', { builds: [build] })).result.revision;
    assert.notEqual(landed, revision);
    // Overwritten, it reads as the last whole figures until the new ones are whole.
    await writeFile(path.join(directory, 'stored-summary.json'), '{"storedObjects":"6');
    assert.equal((await request('download.summaries', { builds: [build] })).result.revision, landed);
    await writeFile(path.join(directory, 'stored-summary.json'), JSON.stringify({ storedObjects: '60', storedBytes: '6000' }));
    assert.notEqual((await request('download.summaries', { builds: [build] })).result.revision, landed);
  });
});

test('a settings.json still being written reads as absent', async () => {
  await withEngine(async ({ directory, request }) => {
    await writeFile(path.join(directory, 'settings.json'), '{"values":{"fetchTableDef');
    const reply = await request('settings.read');
    assert.equal(reply.exited, undefined, 'the engine stopped');
    assert.equal(reply.result.file, 'absent');
    await writeFile(path.join(directory, 'settings.json'), JSON.stringify({ values: { fetchTableDefinitions: false } }));
    const read = (await request('settings.read')).result;
    assert.equal(read.file, 'read');
    assert.equal(read.fetchTableDefinitions.value, false);
  });
});
