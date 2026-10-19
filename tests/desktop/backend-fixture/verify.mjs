// Real protocol/SQLite/CAS/acquisition qualification, using only the authored handler.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { watch } from 'node:fs';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import Ajv from 'ajv';

const schema = JSON.parse(await readFile(new URL('../../../contracts/discovery.schema.json', import.meta.url), 'utf8'));
const ajv = new Ajv({ allErrors: true, strict: false });
ajv.addSchema(schema);
const validateEngineProgress = ajv.getSchema(`${schema.$id}#/definitions/EngineProgress`);
assert.ok(validateEngineProgress, 'The canonical EngineProgress definition is required.');
const maximumFrameBytes = 1024 * 1024;
const strictUtf8 = new TextDecoder('utf-8', { fatal: true });

const [dotnet, engine, data] = process.argv.slice(2);
if (!dotnet || !engine || !data || !path.isAbsolute(engine) || !path.isAbsolute(data))
  throw new Error('Usage: node verify.mjs <dotnet> <absolute BackendFixture.dll> <new absolute data directory>');
await mkdir(data); // Refuse to reset/reuse an existing verification destination.
const controls = path.join(data, 'authored-controls');
let nextId = 0;
function start() {
  const child = spawn(dotnet, [engine, '--data-dir', data], { stdio: ['pipe', 'pipe', 'pipe'] });
  const pending = new Map(); let stderr = ''; let remainder = Buffer.alloc(0);
  child.stderr.setEncoding('utf8'); child.stderr.on('data', chunk => { stderr += chunk; });
  const exited = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => {
      if (code === 0 && remainder.length === 0) resolve();
      else reject(new Error(`Fixture exit ${code}/${signal}, trailing stdout bytes ${remainder.length}: ${stderr}`));
      for (const { reject, timeout } of pending.values()) { clearTimeout(timeout); reject(new Error(`Fixture retired: ${stderr}`)); }
    });
  });
  function acceptFrame(bytes) {
    let frame;
    try { frame = JSON.parse(strictUtf8.decode(bytes)); } catch { throw new Error('Fixture stdout is not a strict UTF-8 JSON protocol frame.'); }
    assert.ok(frame && typeof frame === 'object' && !Array.isArray(frame), 'Protocol frames must be objects.');
    assert.equal(frame.protocolVersion, 1);
    if (!Object.hasOwn(frame, 'id')) {
      assert.equal(frame.type, 'progress', 'A no-id frame must be actual engine progress.');
      assert.ok(validateEngineProgress(frame), `EngineProgress: ${JSON.stringify(validateEngineProgress.errors)}`);
      const request = pending.get(frame.requestId);
      assert.ok(request, `Uncorrelated progress ${frame.requestId}`);
      assert.ok(frame.sequence > request.sequence, 'Progress sequence must advance for its active request.');
      request.sequence = frame.sequence;
      return;
    }
    const request = pending.get(frame.id); assert.ok(request, `Unexpected reply ${frame.id}`);
    pending.delete(frame.id); clearTimeout(request.timeout);
    if (frame.error) request.reject(new Error(`${frame.error.code}: ${frame.error.message}`));
    else request.resolve(frame.result);
  }
  child.stdout.on('data', chunk => {
    let offset = 0;
    while (offset < chunk.length) {
      const newline = chunk.indexOf(10, offset);
      const end = newline < 0 ? chunk.length : newline;
      const length = remainder.length + end - offset;
      assert.ok(length <= maximumFrameBytes, 'Fixture stdout exceeds the 1 MiB protocol frame limit.');
      remainder = Buffer.concat([remainder, chunk.subarray(offset, end)], length);
      if (newline < 0) return;
      acceptFrame(remainder); remainder = Buffer.alloc(0);
      offset = end + 1;
    }
  });
  return {
    rpc(method, params = {}) {
      const id = `authored-${++nextId}`;
      return new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error(`Protocol timeout: ${method}; ${stderr}`)), 15000);
        pending.set(id, { resolve, reject, timeout, sequence: 0 });
        child.stdin.write(`${JSON.stringify({ protocolVersion: 1, id, method, params })}\n`);
      });
    },
    async close() { await this.rpc('shutdown'); await exited; assert.equal(stderr, ''); },
    kill() { child.kill('SIGKILL'); },
  };
}
async function json(file) { return JSON.parse(await readFile(file, 'utf8')); }
async function waitGate(attempt, state = 'waiting') {
  const filename = path.join(controls, 'gated-state.json');
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { watcher.close(); reject(new Error(`Gate timeout at attempt ${attempt}/${state}`)); }, 15000);
    const watcher = watch(controls, () => { void check(); });
    async function check() {
      try {
        const value = await json(filename);
        if (value.attempt === attempt && value.state === state) { clearTimeout(timeout); watcher.close(); resolve(value); }
      } catch (error) { if (error.code !== 'ENOENT') { clearTimeout(timeout); watcher.close(); reject(error); } }
    }
    void check();
  });
}
async function waitState(process, target, state) {
  // Retention intentionally uses bounded snapshot polling, not an endless progress RPC.
  for (let count = 0; count < 15; count++) {
    const value = await process.rpc('retention.read', target);
    if (value.state === state) return value;
    await delay(1000);
  }
  throw new Error(`Retention never settled ${state}`);
}
let processUnderTest = start();
try {
  const hello = await processUnderTest.rpc('hello'); assert.ok(hello.capabilities.includes('retention-v1'));
  const history = await processUnderTest.rpc('discovery.read', { productId: 'wow' });
  assert.equal(history.builds.length, 1); assert.match(history.builds[0].version, /AUTHORED-RETENTION-FIXTURE/);
  const opening = { productId: 'wow', buildIdentity: history.builds[0].id, sourceRegion: 'us' };
  const workspace = await processUnderTest.rpc('workspace.open', opening);
  assert.equal(workspace.download.objectCount, '2');
  const target = { ...opening, ledgerId: workspace.download.ledgerId };
  const initial = await processUnderTest.rpc('retention.read', target); assert.equal(initial.mode, 'onDemand');
  const download = await processUnderTest.rpc('retention.set', { ...target, action: 'downloadAll', expectedRevision: initial.revision });
  await waitGate(1);
  await processUnderTest.rpc('retention.set', { ...target, action: 'pause', expectedRevision: download.revision });
  const paused = await waitState(processUnderTest, target, 'paused'); await waitGate(1, 'canceled');
  assert.deepEqual(await readdir(path.join(data, 'staging-v1')), [], 'Pause settles only after private staging cleanup.');
  await processUnderTest.close();

  // A paused intent survives a real process restart and never starts a new payload attempt.
  processUnderTest = start(); await processUnderTest.rpc('hello');
  const restoredPause = await processUnderTest.rpc('retention.read', target);
  assert.equal(restoredPause.mode, 'downloadAll'); assert.equal(restoredPause.paused, true); assert.equal(restoredPause.state, 'paused');
  assert.equal(Number(await readFile(path.join(controls, 'gated-attempts.txt'), 'utf8')), 1);
  const resumed = await processUnderTest.rpc('retention.set', { ...target, action: 'resume', expectedRevision: paused.revision });
  await waitGate(2);
  await processUnderTest.close();
  assert.deepEqual(await readdir(path.join(data, 'staging-v1')), [], 'Normal shutdown retires unfinished private staging.');

  // Normal exit preserves unpaused intent; the real startup driver reconstructs and runs it.
  processUnderTest = start(); await processUnderTest.rpc('hello'); await waitGate(3);
  const restoredRun = await processUnderTest.rpc('retention.read', target);
  assert.equal(restoredRun.mode, 'downloadAll'); assert.equal(restoredRun.paused, false); assert.equal(restoredRun.revision, resumed.revision);
  await writeFile(path.join(controls, 'release'), 'Release authored gated transfer.\n');
  const completed = await waitState(processUnderTest, target, 'completed');
  assert.equal(completed.coverage.receiptObjects, '2'); assert.equal(completed.coverage.presentObjects, '2');
  assert.equal(completed.coverage.receiptBytes, workspace.download.totalEncodedBytes);
  assert.equal(completed.coverage.presenceState, 'assessed');
  await processUnderTest.close();
  assert.deepEqual(await readdir(path.join(data, 'staging-v1')), [], 'Completed objects leave no private staging.');
  const counts = await json(path.join(controls, 'http-counts.json'));
  assert.equal(counts['https://us.version.battle.net/v2/products/wow/versions'], 1);
  assert.equal(counts['https://us.version.battle.net/v2/products/wow/cdns'], 1);
  const scenario = await json(path.join(controls, 'scenario.json'));
  const payloadCounts = Object.entries(counts).filter(([url]) => url.includes('/data/') &&
    scenario.gatedEncodingKey !== url.split('/').at(-1));
  assert.ok(payloadCounts.every(([, count]) => count === 1), 'Completed authored objects/metadata must not redownload on restart.');
  await writeFile(path.join(controls, 'verification.json'), JSON.stringify({ authoredFixture: true, passed: true,
    target, completed, httpCounts: counts, gatedAttempts: 3, processRestarts: 2 }, null, 2));
  console.log('PASS: real C# protocol, pause, paused restart, resume, unpaused restart, encoded completion and cached reuse.');
  console.log(`Authored evidence: ${path.join(controls, 'verification.json')}`);
} catch (error) { processUnderTest.kill(); throw error; }
