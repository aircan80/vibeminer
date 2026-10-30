import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { chmod, copyFile, mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import os from 'node:os';
import path from 'node:path';

// Runs copies of tools/e2e-lock.mjs in a temporary repository and its worktree, with a stub command
// that the test holds open until it writes a release file.
const script = fileURLToPath(new URL('../../tools/e2e-lock.mjs', import.meta.url));
const DEADLINE_MS = 30_000;

function env(base) {
  const inherited = Object.entries(process.env).filter(([name]) => !name.startsWith('GIT_'));
  // The ceiling keeps git from finding a repository above the temporary layout, such as this one.
  return { ...Object.fromEntries(inherited), GIT_CEILING_DIRECTORIES: base };
}

function git(base, cwd, ...args) {
  execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'commit.gpgsign=false', ...args], { cwd, env: env(base), stdio: 'ignore' });
}

async function withLayout(run) {
  const base = await realpath(await mkdtemp(path.join(os.tmpdir(), 'vibeminer-e2e-lock-')));
  try {
    const main = path.join(base, 'main');
    await mkdir(path.join(main, 'tools'), { recursive: true });
    await copyFile(script, path.join(main, 'tools/e2e-lock.mjs'));
    git(base, main, 'init', '-q');
    git(base, main, 'add', '.');
    git(base, main, 'commit', '-q', '-m', 'layout');
    const worktree = path.join(main, '.claude/worktrees/agent');
    git(base, main, 'worktree', 'add', '-q', '--detach', worktree);
    const stub = path.join(base, 'stub.mjs');
    // Writes its pid to <name>.started, then exits once <name>.release exists or the layout is gone,
    // so a stub orphaned by a killed wrapper can't outlive the test and hold its pipes open.
    await writeFile(stub, [
      "import { existsSync, writeFileSync } from 'node:fs';",
      "import path from 'node:path';",
      'const [marker] = process.argv.slice(2);',
      'writeFileSync(`${marker}.started`, String(process.pid));',
      "const wait = () => existsSync(`${marker}.release`) || !existsSync(path.dirname(marker)) ? process.exit(0) : setTimeout(wait, 20);",
      'wait();',
    ].join('\n'));
    const lockFile = path.join(main, '.git', 'vibeminer-e2e.lock');
    await run({ base, main, worktree, stub, lockFile });
  } finally {
    await rm(base, { recursive: true, force: true });
  }
}

function start(base, checkout, stub, name) {
  const marker = path.join(base, name);
  const child = spawn(process.execPath, [path.join(checkout, 'tools/e2e-lock.mjs'), process.execPath, stub, marker], { cwd: checkout, env: env(base) });
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

test('a second locked run in another worktree waits for the first, says who holds the lock, then starts', () => withLayout(async ({ base, main, worktree, stub, lockFile }) => {
  const first = start(base, main, stub, 'first');
  await until('the first run to start', () => started(first));
  const holder = JSON.parse(await readFile(lockFile, 'utf8'));
  assert.equal(holder.pid, first.child.pid);
  assert.equal(holder.worktree, main);
  assert.ok(!Number.isNaN(Date.parse(holder.startedAt)));

  const second = start(base, worktree, stub, 'second');
  await until('the second run to report the holder', () => second.stderr().includes('waiting for'));
  assert.match(second.stderr(), new RegExp(`waiting for pid ${first.child.pid}, worktree ${main.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}, started ${holder.startedAt}`));
  assert.equal(started(second), false, 'the second run must not start while the first holds the lock');

  await release(first);
  assert.deepEqual(await first.exited, { code: 0, signal: null });
  await until('the second run to start', () => started(second));
  assert.equal(JSON.parse(await readFile(lockFile, 'utf8')).pid, second.child.pid);
  assert.equal(second.stderr().match(/waiting for/g).length, 1, 'the holder is reported once');

  await release(second);
  assert.deepEqual(await second.exited, { code: 0, signal: null });
  assert.equal(existsSync(lockFile), false, 'the lock is released on exit');
}));

test('a stale lock left by a dead pid is taken over', () => withLayout(async ({ base, worktree, stub, lockFile }) => {
  const dead = spawn(process.execPath, ['-e', '']);
  const deadPid = dead.pid;
  await new Promise(resolve => dead.on('exit', resolve));
  await writeFile(lockFile, JSON.stringify({ pid: deadPid, worktree: '/gone', startedAt: '2026-01-01T00:00:00.000Z', token: 'stale' }));

  const run = start(base, worktree, stub, 'run');
  await until('the run to start', () => started(run));
  assert.match(run.stderr(), new RegExp(`took over a stale lock from pid ${deadPid}, worktree /gone`));
  assert.equal(JSON.parse(await readFile(lockFile, 'utf8')).pid, run.child.pid);
  await release(run);
  assert.deepEqual(await run.exited, { code: 0, signal: null });
  assert.equal(existsSync(lockFile), false);
}));

test('the lock is released when the run is interrupted, and the exit code says so', { skip: process.platform === 'win32' ? 'POSIX signals' : false }, () => withLayout(async ({ base, main, stub, lockFile }) => {
  const run = start(base, main, stub, 'run');
  await until('the run to start', () => started(run));
  assert.equal(existsSync(lockFile), true);
  run.child.kill('SIGINT');
  // The stub ignores nothing, so SIGINT forwarded to it ends it.
  assert.deepEqual(await run.exited, { code: 130, signal: null });
  assert.equal(existsSync(lockFile), false);
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
test('a lock that cannot be created is reported and the run goes ahead', { skip: noChmod }, () => withLayout(async ({ base, main, stub, lockFile }) => {
  const gitDir = path.dirname(lockFile);
  await chmod(gitDir, 0o555);
  try {
    const run = start(base, main, stub, 'run');
    await until('the run to start', () => started(run));
    assert.match(run.stderr(), /could not take .*vibeminer-e2e\.lock .*running without the shared e2e lock/);
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
