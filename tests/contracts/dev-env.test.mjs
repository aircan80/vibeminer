import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { chmod, copyFile, mkdir, mkdtemp, realpath, rm, symlink, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import os from 'node:os';
import path from 'node:path';

// Sources tools/dev-env.sh in bash, and zsh when installed, against temporary checkouts and worktrees with stub toolchains.
const script = fileURLToPath(new URL('../../tools/dev-env.sh', import.meta.url));
const skip = process.platform === 'win32' ? 'tools/dev-env.sh is a bash and zsh helper for Linux and macOS' : false;
const which = tool => execFileSync('bash', ['-c', `command -v ${tool} || true`], { encoding: 'utf8' }).trim();
const zsh = skip ? '' : which('zsh');
const systemPath = skip ? [] : [...new Set([path.dirname(which('git')), '/usr/bin', '/bin'])];
const nodeBin = '.local/toolchains/node-v24.22.0-linux-x64/bin';
const dotnetRoot = '.local/toolchains/dotnet';

function env(base, extra = {}) {
  const inherited = Object.entries(process.env).filter(([name]) => !/^(GIT_|TMPDIR$|electron_config_cache$|DOTNET_ROOT$)/.test(name));
  // The ceiling keeps git from finding a repository above the temporary layout, such as this one.
  return { ...Object.fromEntries(inherited), PATH: systemPath.join(':'), GIT_CEILING_DIRECTORIES: base, ...extra };
}

function source(checkout, { base, cwd = base, times = 1, extra = {}, bash = 'bash', shell = bash, prelude = [] }) {
  const command = [
    ...prelude,
    ...Array(times).fill(`source ${JSON.stringify(path.join(checkout, 'tools/dev-env.sh'))}`),
    'printf "%s\\n" "$PATH" "${DOTNET_ROOT:-}" "$TMPDIR" "$electron_config_cache"',
  ].join('; ');
  const startup = shell === zsh ? ['-f'] : ['--noprofile', '--norc'];
  const [pathValue, dotnet, tmpdir, electronCache] = execFileSync(shell, [...startup, '-c', command], { cwd, env: env(base, extra), encoding: 'utf8' }).split('\n');
  return { path: pathValue.split(':'), dotnet, tmpdir, electronCache };
}

function git(base, cwd, ...args) {
  execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'commit.gpgsign=false', ...args], { cwd, env: env(base), stdio: 'ignore' });
}

async function checkout(directory, { toolchains }) {
  await mkdir(path.join(directory, 'tools'), { recursive: true });
  await copyFile(script, path.join(directory, 'tools/dev-env.sh'));
  if (!toolchains) return;
  await mkdir(path.join(directory, nodeBin), { recursive: true });
  await mkdir(path.join(directory, dotnetRoot), { recursive: true });
  await writeFile(path.join(directory, dotnetRoot, 'dotnet'), '#!/bin/sh\n');
  await chmod(path.join(directory, dotnetRoot, 'dotnet'), 0o755);
}

async function withLayout(run) {
  const base = await realpath(await mkdtemp(path.join(os.tmpdir(), 'vibeminer-dev-env-')));
  try {
    const main = path.join(base, 'main');
    await checkout(main, { toolchains: true });
    await writeFile(path.join(main, '.gitignore'), '.local/\n');
    git(base, main, 'init', '-q');
    git(base, main, 'add', '.');
    git(base, main, 'commit', '-q', '-m', 'layout');
    const worktree = path.join(main, '.claude/worktrees/agent');
    git(base, main, 'worktree', 'add', '-q', '--detach', worktree);
    await run({ base, main, worktree });
  } finally {
    await rm(base, { recursive: true, force: true });
  }
}

test('a worktree without toolchains uses the main checkout\'s, keeps TMPDIR its own and shares the Electron cache', { skip }, () => withLayout(async ({ base, main, worktree }) => {
  const result = source(worktree, { base });
  assert.equal(result.dotnet, path.join(main, dotnetRoot));
  assert.deepEqual(result.path, [path.join(main, dotnetRoot), path.join(main, nodeBin), ...systemPath]);
  assert.equal(result.tmpdir, path.join(worktree, '.local/tmp'));
  assert.equal(result.electronCache, path.join(main, '.local/electron-cache'));

  const twice = source(worktree, { base, cwd: path.join(worktree, 'tools'), times: 2 });
  assert.deepEqual(twice.path, result.path, 'sourcing twice from another cwd gives the same PATH');

  const preset = source(worktree, { base, extra: { TMPDIR: '/preset/tmp', electron_config_cache: '/preset/cache' } });
  assert.equal(preset.tmpdir, '/preset/tmp');
  assert.equal(preset.electronCache, '/preset/cache');
}));

test('the main checkout, and a worktree with its own toolchains, use their own', { skip }, () => withLayout(async ({ base, main, worktree }) => {
  const own = source(main, { base, cwd: worktree });
  assert.equal(own.dotnet, path.join(main, dotnetRoot));
  assert.equal(own.tmpdir, path.join(main, '.local/tmp'));
  assert.equal(own.electronCache, path.join(main, '.local/electron-cache'));

  await checkout(worktree, { toolchains: true });
  const local = source(worktree, { base });
  assert.equal(local.dotnet, path.join(worktree, dotnetRoot));
  assert.deepEqual(local.path, [path.join(worktree, dotnetRoot), path.join(worktree, nodeBin), ...systemPath]);
  assert.equal(local.electronCache, path.join(worktree, '.local/electron-cache'));
}));

test('outside git, or with no git on PATH, only the checkout itself is used', { skip }, () => withLayout(async ({ base, worktree }) => {
  const plain = path.join(base, 'plain');
  await checkout(plain, { toolchains: false });
  const result = source(plain, { base });
  assert.equal(result.dotnet, '');
  assert.deepEqual(result.path, systemPath);
  assert.equal(result.tmpdir, path.join(plain, '.local/tmp'));
  assert.equal(result.electronCache, path.join(plain, '.local/electron-cache'));

  const bin = path.join(base, 'bin');
  await mkdir(bin);
  for (const tool of ['dirname', 'mkdir']) await symlink(which(tool), path.join(bin, tool));
  const noGit = source(worktree, { base, bash: which('bash'), extra: { PATH: bin } });
  assert.equal(noGit.dotnet, '');
  assert.deepEqual(noGit.path, [bin]);
  assert.equal(noGit.tmpdir, path.join(worktree, '.local/tmp'));
  assert.equal(noGit.electronCache, path.join(worktree, '.local/electron-cache'));
}));

test('zsh finds the same roots as bash, even with a chpwd hook that prints', { skip: skip || (!zsh && 'zsh is not installed') }, () => withLayout(async ({ base, main, worktree }) => {
  // The hook would end up in the captured root if the script let zsh run it.
  const prelude = ['chpwd() { print chpwd-noise; }'];
  for (const [checkout, cwd] of [[worktree, base], [main, worktree]]) {
    const expected = source(checkout, { base, cwd });
    assert.deepEqual(source(checkout, { base, cwd, shell: zsh, prelude }), expected);
    assert.deepEqual(source(checkout, { base, cwd: path.join(checkout, 'tools'), shell: zsh, prelude, times: 2 }), expected, 'sourcing twice from another cwd gives the same result');
  }
}));
