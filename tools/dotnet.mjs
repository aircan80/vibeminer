import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const local = path.join(root, '.local', 'toolchains', 'dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
const executable = process.env.VIBEMINER_DOTNET || (existsSync(local) ? local : 'dotnet');
const child = spawn(executable, process.argv.slice(2), {
  cwd: root,
  stdio: 'inherit',
  env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' },
});
child.on('error', error => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
child.on('exit', code => { process.exitCode = code ?? 1; });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
