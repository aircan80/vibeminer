import { packager } from '@electron/packager';
import { spawn } from 'node:child_process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { cp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const execute = promisify(execFile);
const hostRid = `${process.platform === 'win32' ? 'win' : process.platform === 'darwin' ? 'osx' : 'linux'}-${process.arch}`;
if (!['linux-x64', 'win-x64', 'osx-arm64', 'osx-x64'].includes(hostRid)) throw new Error(`Unconfigured native target: ${hostRid}`);
const localDotnet = path.join(root, '.local/toolchains/dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
const dotnet = process.env.VIBEMINER_DOTNET || (existsSync(localDotnet) ? localDotnet : 'dotnet');
const stage = path.join(root, '.local/package-stage', hostRid);
const engine = path.join(stage, 'engine');
const desktop = path.join(stage, 'desktop');
const packageLocks = path.join(stage, 'locks');
await rm(stage, { recursive: true, force: true });
await mkdir(desktop, { recursive: true });
await mkdir(packageLocks, { recursive: true });
for (const project of ['Domain', 'Application', 'Protocols.Tact', 'Infrastructure', 'Host']) {
  await cp(path.join(root, `src/backend/VibeMiner.${project}/packages.lock.json`), path.join(packageLocks, `VibeMiner.${project}.packages.lock.json`));
}
const run = (command, args) => new Promise((resolve, reject) => {
  const child = spawn(command, args, { cwd: root, stdio: 'inherit', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  child.on('error', reject);
  child.on('exit', code => code === 0 ? resolve() : reject(new Error(`${command} exited ${code}`)));
});
await run(dotnet, ['publish', 'src/backend/VibeMiner.Host/VibeMiner.Host.csproj', '-c', 'Release', '-r', hostRid, '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', `-p:VibeMinerPackageLockDirectory=${packageLocks}`, '-o', engine]);
await cp(path.join(root, 'apps/desktop/dist'), path.join(desktop, 'dist'), { recursive: true });
for (const name of ['LICENSE', 'NOTICE']) await cp(path.join(root, name), path.join(desktop, name));
await cp(path.join(root, 'src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs'),
  path.join(desktop, 'third-party/WoWDBDefs'), { recursive: true });
await cp(path.join(root, 'docs/third-party/three'),
  path.join(desktop, 'third-party/three'), { recursive: true });
const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const electron = JSON.parse(await readFile(path.join(root, 'node_modules/electron/package.json'), 'utf8'));
const revision = (await execute('git', ['rev-parse', 'HEAD'], { cwd: root })).stdout.trim();
const dirty = (await execute('git', ['status', '--porcelain'], { cwd: root })).stdout.trim().length > 0;
await writeFile(path.join(desktop, 'package.json'), JSON.stringify({ name: 'vibeminer', productName: 'VibeMiner', version: manifest.version, main: 'dist/main/index.cjs', license: manifest.license }, null, 2) + '\n');
await writeFile(path.join(desktop, 'SOURCE.txt'), `VibeMiner local development package\nSource revision: ${revision}\nUncommitted changes: ${dirty}\nTarget: ${hostRid}\nThis package has not been published. Provide accessible corresponding source before distribution.\n`);
const outputs = await packager({
  dir: desktop, name: 'VibeMiner', executableName: 'vibeminer',
  appBundleId: 'io.vibeminer.desktop', appVersion: manifest.version,
  ...(process.platform === 'darwin' ? { icon: path.join(root, 'branding/icons/vibeminer.icns') } : process.platform === 'win32' ? { icon: path.join(root, 'branding/icons/vibeminer.ico') } : {}),
  electronVersion: electron.version, platform: process.platform, arch: process.arch,
  out: path.join(root, 'artifacts/desktop'), overwrite: true, asar: true,
  prune: false, extraResource: engine,
  download: { cacheRoot: process.env.electron_config_cache || path.join(root, '.local/electron-cache') },
});
process.stdout.write(`Created local ${hostRid} development package:\n${outputs.join('\n')}\n`);
process.stdout.write('Native signing and complete dependency-notice/source-distribution qualification remain separate release work.\n');
