// Runs a command while holding one of the e2e slots that every worktree of this repository shares.
// Usage: node tools/e2e-lock.mjs <command> [args...]
//
// Each slot is a lock file under `git rev-parse --git-common-dir`: slot 1 is `vibeminer-e2e.lock` (the
// single lock older checkouts take, so they still wait for slot 1), slot n is `vibeminer-e2e-<n>.lock`.
// A slot is taken atomically with a hard link, so it appears complete or not at all. It names its
// holder's pid, worktree and start time. A slot whose pid is gone (or, on Linux, now belongs to a newer
// process) is stale and is taken over. VIBEMINER_E2E_SLOTS sets how many runs go at once; the default
// follows the CPU count. Two runs from one worktree never overlap, because they share its build output
// and Playwright's output folders. The command gets a private temporary directory that is removed when
// it exits. Outside a git checkout the command runs without a lock.
import { execFileSync, spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { linkSync, mkdtempSync, readdirSync, readFileSync, renameSync, rmSync, unlinkSync, writeFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import os, { constants } from 'node:os';
import path from 'node:path';

const POLL_MS = 500;
// A run averages under one CPU (most of its time is waiting on the UI) but bursts to several while Electron's
// main, GPU and renderer processes and an engine start. CPUS_PER_RUN per slot leaves most of the machine to
// the builds and backend suites other worktrees run meanwhile. The cap keeps memory and disk in bounds.
const CPUS_PER_RUN = 8;
const MAX_DEFAULT_SLOTS = 4;
const root = fileURLToPath(new URL('../', import.meta.url));
const worktree = root.replace(/[\\/]$/, '');
const say = message => process.stderr.write(`e2e lock: ${message}\n`);

const slotName = n => n === 1 ? 'vibeminer-e2e.lock' : `vibeminer-e2e-${n}.lock`;
const SLOT_FILE = /^vibeminer-e2e(?:-([1-9]\d*))?\.lock$/;

function slotCount() {
  const text = process.env.VIBEMINER_E2E_SLOTS;
  if (text === undefined || text === '') return Math.max(1, Math.min(MAX_DEFAULT_SLOTS, Math.floor(os.availableParallelism() / CPUS_PER_RUN)));
  const value = Number(text);
  if (!/^\d+$/.test(text) || !Number.isSafeInteger(value) || value < 1 || value > 64) {
    say(`VIBEMINER_E2E_SLOTS must be a whole number from 1 to 64, not "${text}"`);
    process.exit(2);
  }
  return value;
}

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

function lockDirectory() {
  const env = { ...process.env };
  for (const name of ['GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE']) delete env[name];
  try {
    const common = execFileSync('git', ['rev-parse', '--git-common-dir'], { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    return common ? path.resolve(root, common) : null;
  } catch {
    return null;
  }
}

function read(file) {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') return undefined;
    // Never written by this script: a slot is linked into place only after it is complete.
    return null;
  }
}

const describe = holder => holder
  ? `pid ${holder.pid}, worktree ${holder.worktree}, started ${holder.startedAt}`
  : 'an unreadable lock file';

// Every live holder of any slot, whatever slot count the run that took it used.
function holders(directory) {
  const found = [];
  for (const name of readdirSync(directory)) {
    const match = SLOT_FILE.exec(name);
    if (!match) continue;
    const holder = read(path.join(directory, name));
    if (holder && alive(holder)) found.push({ ...holder, slot: match[1] ? Number(match[1]) : 1 });
  }
  return found.sort((a, b) => a.slot - b.slot);
}

// Takes `file` and returns null, or returns the live holder that keeps it. A stale holder is moved aside
// first, and put back if another run replaced it in between.
function claim(file, draft, me) {
  for (;;) {
    try {
      linkSync(draft, file);
      return null;
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
    }
    const holder = read(file);
    if (holder === undefined) continue;
    if (alive(holder)) return holder;
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
  }
}

function release(file, me) {
  if (read(file)?.token !== me.token) return;
  try { unlinkSync(file); } catch { /* already gone */ }
}

async function acquire(directory, slots) {
  const me = {
    pid: process.pid,
    processStart: processStart(process.pid),
    worktree,
    startedAt: new Date().toISOString(),
    token: randomUUID(),
  };
  const draft = path.join(directory, `vibeminer-e2e.lock.${me.token}`);
  const discard = () => { try { unlinkSync(draft); } catch { /* already gone */ } };
  process.on('exit', discard);
  writeFileSync(draft, `${JSON.stringify(me, null, 2)}\n`);
  let reported, backOff = 0;
  try {
    for (;;) {
      const sameWorktree = other => other.worktree === me.worktree && other.token !== me.token;
      let waitingFor = holders(directory).filter(sameWorktree);
      let reason = 'another run in this worktree';
      if (!waitingFor.length) {
        waitingFor = [];
        reason = `all ${slots} e2e slot${slots === 1 ? ' is' : 's are'} in use`;
        for (let n = 1; n <= slots; n++) {
          const file = path.join(directory, slotName(n));
          const holder = claim(file, draft, me);
          if (holder) { waitingFor.push(holder); continue; }
          // Two runs from one worktree that start together can each take a slot. Each checks only after its own slot is
          // linked, so the later of two such checks always sees the other; any run that sees a twin gives way and
          // tries again after a random pause, so two that both gave way don't keep meeting.
          const twin = holders(directory).find(sameWorktree);
          if (!twin) return { file, slot: n, me };
          release(file, me);
          waitingFor = [twin];
          reason = 'another run in this worktree';
          backOff = Math.floor(Math.random() * POLL_MS);
          break;
        }
      }
      const key = waitingFor.map(holder => holder?.token ?? 'unreadable').join(' ');
      if (key !== reported) {
        say(`waiting for ${waitingFor.map(describe).join('; ')} (${reason}, ${directory})`);
        reported = key;
      }
      await delay(POLL_MS + backOff);
      backOff = 0;
    }
  } finally {
    discard();
    process.off('exit', discard);
  }
}

const [command, ...args] = process.argv.slice(2);
if (!command) {
  say('usage: node tools/e2e-lock.mjs <command> [args...]');
  process.exit(2);
}
const slots = slotCount();

// Signals that arrive while waiting end the wait; once the command runs they go to it, and the slot
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

const directory = lockDirectory();
const env = { ...process.env };
if (directory) {
  let held;
  try {
    held = await acquire(directory, slots);
  } catch (error) {
    // For example a read-only git directory or a file system without hard links.
    say(`could not take an e2e slot in ${directory} (${error.message}), so running without the shared e2e lock`);
  }
  if (held) {
    process.on('exit', () => release(held.file, held.me));
    env.VIBEMINER_E2E_SLOT = String(held.slot);
    if (slots > 1) say(`running in slot ${held.slot} of ${slots}`);
  }
} else {
  say('not inside a git checkout, so running without the shared e2e lock');
}

// A private temporary directory per run: Electron, the engines and the fixture build put their scratch
// files, sockets and leftovers there instead of in a directory other runs use, and it goes when the run ends.
let scratch;
try {
  scratch = mkdtempSync(path.join(os.tmpdir(), 'vibeminer-e2e-'));
} catch (error) {
  say(`could not make a private temporary directory under ${os.tmpdir()} (${error.message}), so the run uses that one`);
}
if (scratch) {
  for (const name of ['TMPDIR', 'TMP', 'TEMP']) env[name] = scratch;
  process.on('exit', () => {
    try { rmSync(scratch, { recursive: true, force: true, maxRetries: 3 }); } catch { /* left for the system to clear */ }
  });
}

// `npm` is a .cmd shim on Windows, which spawn refuses without a shell; run npm's own script instead.
const [executable, prefix] = command === 'npm' && process.env.npm_execpath
  ? [process.execPath, [process.env.npm_execpath]]
  : [command, []];
child = spawn(executable, [...prefix, ...args], { stdio: 'inherit', env });
child.on('error', error => {
  say(`could not start ${command}: ${error.message}`);
  process.exit(1);
});
child.on('exit', (code, signal) => {
  const ended = signal ?? interrupted;
  process.exit(code ?? (ended ? 128 + (constants.signals[ended] ?? 1) : 1));
});
