// Runs a command while holding the e2e lock that every worktree of this repository shares.
// Usage: node tools/e2e-lock.mjs <command> [args...]
//
// The lock is a file under `git rev-parse --git-common-dir`. It is created atomically with a hard link,
// so it appears complete or not at all. It names its holder's pid, worktree and start time. A lock whose
// pid is gone (or, on Linux, now belongs to a newer process) is stale and is taken over. Outside a git
// checkout the command runs without a lock.
import { execFileSync, spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { linkSync, readFileSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { constants } from 'node:os';
import path from 'node:path';

const LOCK_NAME = 'vibeminer-e2e.lock';
const POLL_MS = 500;
const root = fileURLToPath(new URL('../', import.meta.url));
const say = message => process.stderr.write(`e2e lock: ${message}\n`);

// The kernel start time of a process in clock ticks, which tells a reused pid apart. Linux only.
function processStart(pid) {
  try {
    const stat = readFileSync(`/proc/${pid}/stat`, 'utf8');
    return stat.slice(stat.lastIndexOf(')') + 2).split(' ')[19] ?? null;
  } catch {
    return null;
  }
}

function alive(holder) {
  if (!Number.isSafeInteger(holder?.pid) || holder.pid <= 0) return false;
  try {
    process.kill(holder.pid, 0);
  } catch (error) {
    if (error.code !== 'EPERM') return false;
  }
  if (holder.processStart != null) {
    const start = processStart(holder.pid);
    if (start != null && start !== holder.processStart) return false;
  }
  return true;
}

function lockPath() {
  const env = { ...process.env };
  for (const name of ['GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE']) delete env[name];
  try {
    const common = execFileSync('git', ['rev-parse', '--git-common-dir'], { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    return common ? path.join(path.resolve(root, common), LOCK_NAME) : null;
  } catch {
    return null;
  }
}

function read(file) {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') return undefined;
    // Never written by this script: the lock is linked into place only after it is complete.
    return null;
  }
}

const describe = holder => holder
  ? `pid ${holder.pid}, worktree ${holder.worktree}, started ${holder.startedAt}`
  : 'an unreadable lock file';

async function acquire(file) {
  const me = {
    pid: process.pid,
    processStart: processStart(process.pid),
    worktree: root.replace(/[\\/]$/, ''),
    startedAt: new Date().toISOString(),
    token: randomUUID(),
  };
  const draft = `${file}.${me.token}`;
  const discard = () => { try { unlinkSync(draft); } catch { /* already gone */ } };
  process.on('exit', discard);
  writeFileSync(draft, `${JSON.stringify(me, null, 2)}\n`);
  let reported;
  try {
    for (;;) {
      try {
        linkSync(draft, file);
        return me;
      } catch (error) {
        if (error.code !== 'EEXIST') throw error;
      }
      const holder = read(file);
      if (holder === undefined) continue;
      if (!alive(holder)) {
        // Move the stale file aside and check that it is still the one judged stale. If another
        // run replaced it in between, put that live lock back.
        const aside = `${file}.stale.${me.token}`;
        try {
          renameSync(file, aside);
        } catch (error) {
          if (error.code === 'ENOENT') continue;
          throw error;
        }
        const moved = read(aside);
        if (moved && moved.token !== holder?.token && alive(moved)) {
          try { linkSync(aside, file); } catch { /* a third run holds it now */ }
        } else {
          say(`took over a stale lock from ${describe(holder)}`);
        }
        try { unlinkSync(aside); } catch { /* already gone */ }
        continue;
      }
      const key = holder ? holder.token : 'unreadable';
      if (key !== reported) {
        say(`waiting for ${describe(holder)} (${file})`);
        reported = key;
      }
      await delay(POLL_MS);
    }
  } finally {
    discard();
    process.off('exit', discard);
  }
}

function release(file, me) {
  if (read(file)?.token !== me.token) return;
  try { unlinkSync(file); } catch { /* already gone */ }
}

const [command, ...args] = process.argv.slice(2);
if (!command) {
  say('usage: node tools/e2e-lock.mjs <command> [args...]');
  process.exit(2);
}

// Signals that arrive while waiting end the wait; once the command runs they go to it, and the lock
// is released after it exits.
let child;
let interrupted;
for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
  process.on(signal, () => {
    interrupted ??= signal;
    if (child) child.kill(signal);
    else process.exit(128 + (constants.signals[signal] ?? 1));
  });
}

const file = lockPath();
if (file) {
  let me;
  try {
    me = await acquire(file);
  } catch (error) {
    // For example a read-only git directory or a file system without hard links.
    say(`could not take ${file} (${error.message}), so running without the shared e2e lock`);
  }
  if (me) process.on('exit', () => release(file, me));
} else {
  say('not inside a git checkout, so running without the shared e2e lock');
}

// `npm` is a .cmd shim on Windows, which spawn refuses without a shell; run npm's own script instead.
const [executable, prefix] = command === 'npm' && process.env.npm_execpath
  ? [process.execPath, [process.env.npm_execpath]]
  : [command, []];
child = spawn(executable, [...prefix, ...args], { stdio: 'inherit' });
child.on('error', error => {
  say(`could not start ${command}: ${error.message}`);
  process.exit(1);
});
child.on('exit', (code, signal) => {
  const ended = signal ?? interrupted;
  process.exit(code ?? (ended ? 128 + (constants.signals[ended] ?? 1) : 1));
});
