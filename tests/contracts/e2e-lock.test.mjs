import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { chmod, copyFile, mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import os from 'node:os';
import path from 'node:path';

// Runs copies of tools/e2e-lock.mjs in a temporary repository and its worktrees, with a stub command
// that the test holds open until it writes a release file.
const script = fileURLToPath(new URL('../../tools/e2e-lock.mjs', import.meta.url));
const DEADLINE_MS = 30_000;

function env(base) {
  const inherited = Object.entries(process.env).filter(([name]) => !name.startsWith('GIT_') && !name.startsWith('VIBEMINER_E2E_'));
  // The ceiling keeps git from finding a repository above the temporary layout, such as this one.
  return { ...Object.fromEntries(inherited), GIT_CEILING_DIRECTORIES: base, TMPDIR: path.join(base, 'tmp') };
}

function git(base, cwd, ...args) {
  execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'commit.gpgsign=false', ...args], { cwd, env: env(base), stdio: 'ignore' });
}

async function withLayout(run) {
  const base = await realpath(await mkdtemp(path.join(os.tmpdir(), 'vibeminer-e2e-lock-')));
  try {
    await mkdir(path.join(base, 'tmp'));
    const main = path.join(base, 'main');
    await mkdir(path.join(main, 'tools'), { recursive: true });
    await copyFile(script, path.join(main, 'tools/e2e-lock.mjs'));
    git(base, main, 'init', '-q');
    git(base, main, 'add', '.');
    git(base, main, 'commit', '-q', '-m', 'layout');
    const worktree = path.join(main, '.claude/worktrees/agent');
    git(base, main, 'worktree', 'add', '-q', '--detach', worktree);
    const other = path.join(main, '.claude/worktrees/other');
    git(base, main, 'worktree', 'add', '-q', '--detach', other);
    const stub = path.join(base, 'stub.mjs');
    // Writes what it was given to <name>.env and its pid to <name>.started, then exits once <name>.release
    // exists or the layout is gone, so a stub orphaned by a killed wrapper can't outlive the test and hold
    // its pipes open.
    await writeFile(stub, [
      "import { existsSync, writeFileSync } from 'node:fs';",
      "import { tmpdir } from 'node:os';",
      "import path from 'node:path';",
      'const [marker] = process.argv.slice(2);',
      'writeFileSync(`${marker}.env`, JSON.stringify({ tmp: tmpdir(), slot: process.env.VIBEMINER_E2E_SLOT ?? null }));',
      "writeFileSync(path.join(tmpdir(), 'scratch'), '');",
      'writeFileSync(`${marker}.started`, String(process.pid));',
      "const wait = () => existsSync(`${marker}.release`) || !existsSync(path.dirname(marker)) ? process.exit(0) : setTimeout(wait, 20);",
      'wait();',
    ].join('\n'));
    const gitDir = path.join(main, '.git');
    const lockFile = path.join(gitDir, 'vibeminer-e2e.lock');
    await run({ base, main, worktree, other, stub, gitDir, lockFile });
  } finally {
    await rm(base, { recursive: true, force: true });
  }
}

// One slot unless a test asks for more, so no outcome follows the CPU count of the machine running it.
function start(base, checkout, stub, name, slots = '1') {
  const marker = path.join(base, name);
  const child = spawn(process.execPath, [path.join(checkout, 'tools/e2e-lock.mjs'), process.execPath, stub, marker], { cwd: checkout, env: { ...env(base), VIBEMINER_E2E_SLOTS: slots } });
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  const exited = new Promise(resolve => child.on('exit', (code, signal) => resolve({ code, signal })));
  return { child, marker, exited, stderr: () => stderr };
}

async function until(what, check) {
  const deadline = Date.now() + DEADLINE_MS;
  for (;;) {
    const value = await check();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${what}`);
    await new Promise(resolve => setTimeout(resolve, 20));
  }
}

const started = run => existsSync(`${run.marker}.started`);
const release = run => writeFile(`${run.marker}.release`, '');
const given = async run => JSON.parse(await readFile(`${run.marker}.env`, 'utf8'));
const escape = text => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const holderOf = async file => JSON.parse(await readFile(file, 'utf8'));
// Long enough for a waiting run to have polled the slots several times.
const settle = () => new Promise(resolve => setTimeout(resolve, 1_500));

test('a second locked run in another worktree waits for the first, says who holds the lock, then starts', () => withLayout(async ({ base, main, worktree, stub, lockFile }) => {
  const first = start(base, main, stub, 'first');
  await until('the first run to start', () => started(first));
  const holder = await holderOf(lockFile);
  assert.equal(holder.pid, first.child.pid);
  assert.equal(holder.worktree, main);
  assert.ok(!Number.isNaN(Date.parse(holder.startedAt)));

  const second = start(base, worktree, stub, 'second');
  await until('the second run to report the holder', () => second.stderr().includes('waiting for'));
  assert.match(second.stderr(), new RegExp(`waiting for pid ${first.child.pid}, worktree ${escape(main)}, started ${escape(holder.startedAt)} \\(all 1 e2e slot is in use, `));
  assert.equal(started(second), false, 'the second run must not start while the first holds the lock');

  await release(first);
  assert.deepEqual(await first.exited, { code: 0, signal: null });
  await until('the second run to start', () => started(second));
  assert.equal((await holderOf(lockFile)).pid, second.child.pid);
  assert.equal(second.stderr().match(/waiting for/g).length, 1, 'the holder is reported once');

  await release(second);
  assert.deepEqual(await second.exited, { code: 0, signal: null });
  assert.equal(existsSync(lockFile), false, 'the lock is released on exit');
}));

test('with two slots, runs from two worktrees go side by side and a third waits for either', () => withLayout(async ({ base, main, worktree, other, stub, gitDir, lockFile }) => {
  const first = start(base, main, stub, 'first', '2');
  await until('the first run to start', () => started(first));
  const second = start(base, worktree, stub, 'second', '2');
  await until('the second run to start alongside the first', () => started(second));
  assert.equal((await given(first)).slot, '1');
  assert.equal((await given(second)).slot, '2');
  assert.match(second.stderr(), /running in slot 2 of 2/);
  const slot2 = path.join(gitDir, 'vibeminer-e2e-2.lock');
  assert.equal((await holderOf(lockFile)).pid, first.child.pid, 'slot 1 is the lock file older checkouts take');
  assert.equal((await holderOf(slot2)).pid, second.child.pid);

  const third = start(base, other, stub, 'third', '2');
  await until('the third run to report both holders', () => third.stderr().includes('waiting for'));
  assert.match(third.stderr(), new RegExp(`waiting for pid ${first.child.pid}, worktree ${escape(main)}, .*; pid ${second.child.pid}, worktree ${escape(worktree)}, .*\\(all 2 e2e slots are in use, `));
  await settle();
  assert.equal(started(third), false, 'a third run must not start while both slots are held');

  await release(second);
  assert.deepEqual(await second.exited, { code: 0, signal: null });
  await until('the third run to take the freed slot', () => started(third));
  assert.equal((await given(third)).slot, '2');
  assert.equal((await holderOf(slot2)).pid, third.child.pid);

  await release(first);
  await release(third);
  assert.deepEqual(await first.exited, { code: 0, signal: null });
  assert.deepEqual(await third.exited, { code: 0, signal: null });
  assert.equal(existsSync(lockFile), false);
  assert.equal(existsSync(slot2), false);
}));

test('a run started with fewer slots does not take a higher one', () => withLayout(async ({ base, main, worktree, stub }) => {
  const first = start(base, main, stub, 'first', '2');
  await until('the first run to start', () => started(first));
  const second = start(base, worktree, stub, 'second', '1');
  await until('the second run to wait', () => second.stderr().includes('all 1 e2e slot is in use'));
  await settle();
  assert.equal(started(second), false);
  await release(first);
  await until('the second run to start', () => started(second));
  await release(second);
  assert.deepEqual(await second.exited, { code: 0, signal: null });
}));

test('two runs from one worktree never overlap, even with a free slot', () => withLayout(async ({ base, worktree, stub }) => {
  const first = start(base, worktree, stub, 'first', '3');
  await until('the first run to start', () => started(first));
  const second = start(base, worktree, stub, 'second', '3');
  await until('the second run to wait', () => second.stderr().includes('waiting for'));
  assert.match(second.stderr(), new RegExp(`waiting for pid ${first.child.pid}, worktree ${escape(worktree)}, .*\\(another run in this worktree, `));
  await settle();
  assert.equal(started(second), false);
  await release(first);
  assert.deepEqual(await first.exited, { code: 0, signal: null });
  await until('the second run to start', () => started(second));
  await release(second);
  assert.deepEqual(await second.exited, { code: 0, signal: null });
}));

test('a run does not take a free lower slot while its worktree holds a higher one', () => withLayout(async ({ base, main, worktree, stub }) => {
  const first = start(base, main, stub, 'first', '2');
  await until('the first run to start', () => started(first));
  const mine = start(base, worktree, stub, 'mine', '2');
  await until('the worktree run to take slot 2', () => started(mine));
  assert.equal((await given(mine)).slot, '2');
  await release(first);
  await first.exited;
  const again = start(base, worktree, stub, 'again', '2');
  await until('the second worktree run to wait', () => again.stderr().includes('another run in this worktree'));
  await settle();
  assert.equal(started(again), false, 'slot 1 is free, but the worktree is busy');
  await release(mine);
  await until('the second worktree run to start', () => started(again));
  await release(again);
  assert.deepEqual(await again.exited, { code: 0, signal: null });
}));

test('two runs from one worktree that start together end up one at a time', () => withLayout(async ({ base, worktree, stub }) => {
  const runs = [0, 1, 2].map(i => start(base, worktree, stub, `run${i}`, '3'));
  const order = [];
  for (let done = 0; done < runs.length; done++) {
    const running = await until('one run to start', () => {
      const now = runs.filter(run => started(run) && !order.includes(run));
      return now.length ? now : null;
    });
    await settle();
    const live = runs.filter(run => started(run) && !order.includes(run));
    assert.equal(live.length, 1, 'only one run from the worktree holds a slot at a time');
    order.push(running[0]);
    await release(running[0]);
    await running[0].exited;
  }
  for (const run of runs) assert.deepEqual(await run.exited, { code: 0, signal: null });
}));

test('each run gets its own temporary directory, removed when it ends', () => withLayout(async ({ base, main, worktree, stub }) => {
  const first = start(base, main, stub, 'first', '2');
  const second = start(base, worktree, stub, 'second', '2');
  await until('both runs to start', () => started(first) && started(second));
  const [a, b] = [(await given(first)).tmp, (await given(second)).tmp];
  assert.notEqual(a, b);
  for (const dir of [a, b]) {
    assert.equal(path.dirname(dir), path.join(base, 'tmp'), 'made under the TMPDIR the run was given');
    assert.equal(existsSync(path.join(dir, 'scratch')), true);
  }
  await release(first);
  await release(second);
  assert.deepEqual(await first.exited, { code: 0, signal: null });
  assert.deepEqual(await second.exited, { code: 0, signal: null });
  assert.equal(existsSync(a), false, 'the first run\'s directory is removed with what the command left in it');
  assert.equal(existsSync(b), false);
}));

test('an invalid slot count is refused before anything runs', () => withLayout(async ({ base, main, stub, lockFile }) => {
  for (const slots of ['0', '-1', 'two', '1.5', '65']) {
    const run = start(base, main, stub, `bad${slots}`, slots);
    assert.deepEqual(await run.exited, { code: 2, signal: null });
    assert.match(run.stderr(), /VIBEMINER_E2E_SLOTS must be a whole number from 1 to 64/);
    assert.equal(started(run), false);
  }
  assert.equal(existsSync(lockFile), false);
}));

test('a stale lock left by a dead pid is taken over', () => withLayout(async ({ base, worktree, stub, gitDir }) => {
  const dead = spawn(process.execPath, ['-e', '']);
  const deadPid = dead.pid;
  await new Promise(resolve => dead.on('exit', resolve));
  // In a higher slot too, which a run with that many slots must also be able to reclaim.
  const slot2 = path.join(gitDir, 'vibeminer-e2e-2.lock');
  await writeFile(path.join(gitDir, 'vibeminer-e2e.lock'), JSON.stringify({ pid: deadPid, worktree: '/gone', startedAt: '2026-01-01T00:00:00.000Z', token: 'stale' }));
  await writeFile(slot2, JSON.stringify({ pid: deadPid, worktree: '/gone2', startedAt: '2026-01-01T00:00:00.000Z', token: 'stale2' }));

  const run = start(base, worktree, stub, 'run');
  await until('the run to start', () => started(run));
  assert.match(run.stderr(), new RegExp(`took over a stale lock from pid ${deadPid}, worktree /gone`));
  assert.equal((await holderOf(path.join(gitDir, 'vibeminer-e2e.lock'))).pid, run.child.pid);
  const next = start(base, path.join(base, 'main'), stub, 'next', '2');
  await until('a run with two slots to start', () => started(next));
  assert.match(next.stderr(), /took over a stale lock from pid \d+, worktree \/gone2/);
  assert.equal((await holderOf(slot2)).pid, next.child.pid);
  await release(run);
  await release(next);
  assert.deepEqual(await run.exited, { code: 0, signal: null });
  assert.deepEqual(await next.exited, { code: 0, signal: null });
  assert.equal(existsSync(path.join(gitDir, 'vibeminer-e2e.lock')), false);
  assert.equal(existsSync(slot2), false);
}));

test('the lock is released when the run is interrupted, and the exit code says so', { skip: process.platform === 'win32' ? 'POSIX signals' : false }, () => withLayout(async ({ base, main, stub, lockFile }) => {
  const run = start(base, main, stub, 'run');
  await until('the run to start', () => started(run));
  assert.equal(existsSync(lockFile), true);
  const scratch = (await given(run)).tmp;
  run.child.kill('SIGINT');
  // The stub ignores nothing, so SIGINT forwarded to it ends it.
  assert.deepEqual(await run.exited, { code: 130, signal: null });
  assert.equal(existsSync(lockFile), false);
  assert.equal(existsSync(scratch), false, 'the private temporary directory goes too');
}));

test('a lock whose holder crashed without cleanup does not block the next run', { skip: process.platform === 'win32' ? 'POSIX signals' : false }, () => withLayout(async ({ base, main, worktree, stub, lockFile }) => {
  const crashed = start(base, main, stub, 'crashed');
  await until('the run to start', () => started(crashed));
  crashed.child.kill('SIGKILL');
  await crashed.exited;
  assert.equal(existsSync(lockFile), true, 'SIGKILL leaves the lock file behind');

  const next = start(base, worktree, stub, 'next');
  await until('the next run to start', () => started(next));
  assert.match(next.stderr(), /took over a stale lock/);
  await release(next);
  assert.deepEqual(await next.exited, { code: 0, signal: null });

  // The killed wrapper's stub is an orphan that still holds the test's stderr pipe; end it.
  const orphan = Number(await readFile(`${crashed.marker}.started`, 'utf8'));
  await release(crashed);
  await until('the orphaned stub to exit', () => { try { process.kill(orphan, 0); return false; } catch { return true; } });
}));

const noChmod = process.platform === 'win32' || process.getuid?.() === 0 ? 'needs POSIX permissions that bind' : false;
test('a lock that cannot be created is reported and the run goes ahead', { skip: noChmod }, () => withLayout(async ({ base, main, stub, gitDir }) => {
  await chmod(gitDir, 0o555);
  try {
    const run = start(base, main, stub, 'run');
    await until('the run to start', () => started(run));
    assert.match(run.stderr(), /could not take an e2e slot in .*running without the shared e2e lock/);
    await release(run);
    assert.deepEqual(await run.exited, { code: 0, signal: null });
  } finally {
    await chmod(gitDir, 0o755);
  }
}));

test('outside a git checkout the command runs without a lock and says so', async () => {
  const base = await realpath(await mkdtemp(path.join(os.tmpdir(), 'vibeminer-e2e-lock-')));
  try {
    // One level below the ceiling: git still searches the ceiling directory's children.
    const plain = path.join(base, 'plain');
    await mkdir(path.join(plain, 'tools'), { recursive: true });
    await mkdir(path.join(base, 'tmp'));
    await copyFile(script, path.join(plain, 'tools/e2e-lock.mjs'));
    const output = execFileSync(process.execPath, [path.join(plain, 'tools/e2e-lock.mjs'), process.execPath, '-e', 'process.exit(3)'], { cwd: plain, env: env(base), encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    assert.fail(`expected exit code 3, got output ${output}`);
  } catch (error) {
    assert.equal(error.status, 3, 'the command\'s exit code is passed through');
    assert.match(String(error.stderr), /not inside a git checkout, so running without the shared e2e lock/);
  } finally {
    await rm(base, { recursive: true, force: true });
  }
});
