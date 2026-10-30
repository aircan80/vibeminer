import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const artifacts = process.argv[2] ?? path.join(root, '.local', 'backend-fixture-build');
if (process.argv.length > 3 || !path.isAbsolute(artifacts))
  throw new Error('Usage: node tests/desktop/backend-fixture/build.mjs [absolute isolated artifacts directory]');
const locks = path.join(artifacts, 'locks');
await mkdir(locks, { recursive: true });
const child = spawn(process.execPath, [path.join(root, 'tools', 'dotnet.mjs'), 'build',
  path.join(root, 'tests', 'desktop', 'backend-fixture', 'BackendFixture.csproj'),
  // No MSBuild nodes or compiler server outlive the build: they would find their next build through sockets in
  // TMPDIR, which `tools/e2e-lock.mjs` makes private to each e2e run and removes afterwards.
  '--artifacts-path', artifacts, `-p:VibeMinerPackageLockDirectory=${locks}`, '--disable-build-servers', '--nologo'], {
  cwd: root, stdio: 'inherit',
});
child.on('error', error => { process.stderr.write(`${error.message}\n`); });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
process.exitCode = await new Promise(resolve => {
  child.once('error', () => resolve(1));
  child.once('close', code => resolve(code ?? 1));
});
