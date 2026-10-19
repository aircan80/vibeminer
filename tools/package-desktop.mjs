import { packager } from '@electron/packager';
import { spawn } from 'node:child_process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { cp, mkdir, readdir, readFile, writeFile, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const execute = promisify(execFile);
// --release: refuse a dirty checkout, require bundled notices and name the public source in SOURCE.txt.
const release = process.argv.includes('--release');
const sourceUrl = process.env.VIBEMINER_SOURCE_URL;
if (release && !/^https:\/\/\S+$/.test(sourceUrl || '')) throw new Error('Release packaging needs VIBEMINER_SOURCE_URL (https URL of the public repository).');
const revision = (await execute('git', ['rev-parse', 'HEAD'], { cwd: root })).stdout.trim();
const dirty = (await execute('git', ['status', '--porcelain'], { cwd: root })).stdout.trim().length > 0;
if (release && dirty) throw new Error('Release packaging needs a clean checkout; commit or discard local changes.');
const hostRid = `${process.platform === 'win32' ? 'win' : process.platform === 'darwin' ? 'osx' : 'linux'}-${process.arch}`;
if (!['linux-x64', 'win-x64', 'osx-arm64', 'osx-x64'].includes(hostRid)) throw new Error(`Unconfigured native target: ${hostRid}`);
const localDotnet = path.join(root, '.local/toolchains/dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
const dotnet = process.env.VIBEMINER_DOTNET || (existsSync(localDotnet) ? localDotnet : 'dotnet');
const stage = path.join(root, '.local/package-stage', hostRid);
const engine = path.join(stage, 'engine');
const desktop = path.join(stage, 'desktop');
// Licenses, notices and source identity land beside the engine in resources/legal, outside app.asar.
const legal = path.join(stage, 'legal');
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
await run(dotnet, ['publish', 'src/backend/VibeMiner.Host/VibeMiner.Host.csproj', '-c', 'Release', '-r', hostRid, '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-p:ContinuousIntegrationBuild=true', `-p:VibeMinerPackageLockDirectory=${packageLocks}`, '-o', engine]);
// Unsigned builds get consistent ad-hoc signatures on macOS (arm64 refuses unsigned code).
// Developer ID signing and notarization replace adHocSign later; see BUILDING.md "Release".
const adHocSign = async (target, deep = false) => run('codesign', ['--force', ...(deep ? ['--deep'] : []), '--sign', '-', target]);
if (process.platform === 'darwin') {
  for (const name of (await readdir(engine)).filter(name => name === 'VibeMiner.Host' || name.endsWith('.dylib'))) await adHocSign(path.join(engine, name));
}
await cp(path.join(root, 'apps/desktop/dist'), path.join(desktop, 'dist'), { recursive: true });
for (const name of ['LICENSE', 'NOTICE']) await cp(path.join(root, name), path.join(legal, name));
await cp(path.join(root, 'src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs'),
  path.join(legal, 'third-party/WoWDBDefs'), { recursive: true });
await cp(path.join(root, 'docs/third-party/three'),
  path.join(legal, 'third-party/three'), { recursive: true });
await bundleRuntimeNotices();
const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const electron = JSON.parse(await readFile(path.join(root, 'node_modules/electron/package.json'), 'utf8'));
await writeFile(path.join(desktop, 'package.json'), JSON.stringify({ name: 'vibeminer', productName: 'VibeMiner', version: manifest.version, desktopName: 'vibeminer.desktop', main: 'dist/main/index.cjs', license: manifest.license }, null, 2) + '\n');
await writeFile(path.join(legal, 'SOURCE.txt'), release
  ? `VibeMiner ${manifest.version}\nSource revision: ${revision}\nUncommitted changes: false\nTarget: ${hostRid}\nCorresponding source: ${sourceUrl}/tree/${revision}\nThe v${manifest.version} release also attaches VibeMiner-${manifest.version}-source.tar.gz built from this revision.\n`
  : `VibeMiner local development package\nSource revision: ${revision}\nUncommitted changes: ${dirty}\nTarget: ${hostRid}\nThis package has not been published. Provide accessible corresponding source before distribution.\n`);
const outputs = await packager({
  dir: desktop, name: 'VibeMiner', executableName: 'vibeminer',
  appBundleId: 'io.vibeminer.desktop', appVersion: manifest.version,
  ...(process.platform === 'darwin' ? { icon: path.join(root, 'branding/icons/vibeminer.icns') } : process.platform === 'win32' ? { icon: path.join(root, 'branding/icons/vibeminer.ico') } : {}),
  electronVersion: electron.version, platform: process.platform, arch: process.arch,
  out: path.join(root, 'artifacts/desktop'), overwrite: true, asar: true,
  prune: false, extraResource: [engine, legal],
  download: { cacheRoot: process.env.electron_config_cache || path.join(root, '.local/electron-cache') },
});
// Electron's licenses sit beside the .app on macOS, so keep a copy inside every package.
const resources = process.platform === 'darwin' ? path.join(outputs[0], 'VibeMiner.app/Contents/Resources') : path.join(outputs[0], 'resources');
for (const name of ['LICENSE', 'LICENSES.chromium.html']) await cp(path.join(outputs[0], name), path.join(resources, 'legal/electron', name));
if (process.platform === 'darwin') {
  const bundle = path.join(outputs[0], 'VibeMiner.app');
  await adHocSign(bundle, true);
  await run('codesign', ['--verify', '--deep', '--strict', bundle]);
}
process.stdout.write(`Created ${release ? 'release' : 'local development'} ${hostRid} package:\n${outputs.join('\n')}\n`);
process.stdout.write('The package is unsigned (ad-hoc on macOS). Run tools/make-installers.mjs for installers.\n');

async function bundleRuntimeNotices() {
  const notices = path.join(legal, 'third-party');
  const missing = what => {
    if (release) throw new Error(`Release packaging could not find ${what}.`);
    process.stderr.write(`warning: ${what} not found; this local package lacks that notice.\n`);
  };
  // The self-contained engine ships the .NET runtime: keep the SDK's license and third-party notices.
  const sdkVersion = (await execute(dotnet, ['--version'], { cwd: root })).stdout.trim();
  const sdkLine = (await execute(dotnet, ['--list-sdks'], { cwd: root })).stdout.split(/\r?\n/).find(line => line.startsWith(`${sdkVersion} [`));
  const dotnetRoot = sdkLine && path.dirname(sdkLine.slice(sdkLine.indexOf('[') + 1, sdkLine.lastIndexOf(']')));
  for (const name of ['LICENSE.txt', 'ThirdPartyNotices.txt']) {
    const file = dotnetRoot && path.join(dotnetRoot, name);
    if (file && existsSync(file)) await cp(file, path.join(notices, 'dotnet', name));
    else missing(`.NET SDK ${name}`);
  }
  // NuGet packages from the lockfile this publication resolved.
  const lock = JSON.parse(await readFile(path.join(packageLocks, 'VibeMiner.Host.packages.lock.json'), 'utf8'));
  const nuget = new Map();
  for (const group of Object.values(lock.dependencies)) {
    for (const [id, entry] of Object.entries(group)) if (entry.type !== 'Project') nuget.set(`${id} ${entry.resolved}`, { id, version: entry.resolved });
  }
  const cache = process.env.NUGET_PACKAGES || path.join(homedir(), '.nuget', 'packages');
  // NuGet ships license expressions, not texts: copy the committed canonical text for each package.
  const licenseTexts = { 'MIT': 'MIT.txt', 'Apache-2.0': 'Apache-2.0.txt' };
  const nugetLines = [];
  for (const [key, { id, version }] of [...nuget].sort(([a], [b]) => a.localeCompare(b))) {
    const nuspec = path.join(cache, id.toLowerCase(), version.toLowerCase(), `${id.toLowerCase()}.nuspec`);
    const text = existsSync(nuspec) ? await readFile(nuspec, 'utf8') : '';
    if (!text) missing(`NuGet metadata for ${key}`);
    const field = name => text.match(new RegExp(`<${name}\\b[^>]*>([^<]*)</${name}>`))?.[1]?.trim() || 'not stated';
    const license = field('license'), copyright = field('copyright');
    const directory = `nuget/${id}.${version}`;
    if (licenseTexts[license]) {
      const body = await readFile(path.join(root, 'docs/third-party/licenses', licenseTexts[license]), 'utf8');
      const holder = copyright === 'not stated' ? `${id} ${version} (copyright holder not stated in its package metadata)` : copyright;
      const licenseText = license === 'MIT'
        ? body.replace('Copyright (c) <year> <copyright holders>', holder)
        : `${id} ${version}\n${holder}\n\n${body}`;
      await mkdir(path.join(notices, directory), { recursive: true });
      await writeFile(path.join(notices, directory, 'LICENSE.txt'), licenseText);
    } else missing(`a committed license text for ${key} (license: ${license})`);
    nugetLines.push(`${key}\n  License: ${license} (text: ${directory}/LICENSE.txt)\n  Copyright: ${copyright}\n  Project: ${field('projectUrl')}\n`);
  }
  await writeFile(path.join(notices, 'nuget-packages.txt'), `NuGet packages resolved when publishing the bundled VibeMiner engine (build-only packages such as Microsoft.NET.ILLink.Tasks included).\nSQLitePCLRaw.lib.e_sqlite3 contains the SQLite library, which is in the public domain.\n\n${nugetLines.join('\n')}`);
  // npm runtime dependencies bundled into the desktop main, preload and renderer code.
  const resolvePackage = (name, from) => {
    for (let directory = from; ; directory = path.dirname(directory)) {
      const candidate = path.join(directory, 'node_modules', name);
      if (existsSync(path.join(candidate, 'package.json'))) return candidate;
      if (path.resolve(directory) === path.resolve(root) || path.dirname(directory) === directory) throw new Error(`Cannot resolve runtime dependency ${name}.`);
    }
  };
  const desktopManifest = JSON.parse(await readFile(path.join(root, 'apps/desktop/package.json'), 'utf8'));
  const queue = Object.keys(desktopManifest.dependencies).map(name => [name, path.join(root, 'apps/desktop')]);
  const npmLines = [], seen = new Set();
  while (queue.length) {
    const [name, from] = queue.shift();
    const directory = resolvePackage(name, from);
    const metadata = JSON.parse(await readFile(path.join(directory, 'package.json'), 'utf8'));
    const key = `${metadata.name}@${metadata.version}`;
    if (seen.has(key)) continue;
    seen.add(key);
    const files = (await readdir(directory)).filter(file => /^(licen[cs]e|notice|copying)/i.test(file));
    if (!files.length) missing(`a license file for ${key}`);
    if (typeof metadata.license !== 'string' || !metadata.license.trim()) missing(`a license expression for ${key}`);
    for (const file of files) await cp(path.join(directory, file), path.join(notices, 'npm', key.replace('/', '__'), file));
    npmLines.push(`${key}  ${typeof metadata.license === 'string' ? metadata.license : 'see files'}`);
    for (const dependency of Object.keys(metadata.dependencies || {})) queue.push([dependency, directory]);
  }
  await writeFile(path.join(notices, 'npm-packages.txt'), `npm packages bundled into the desktop code. License files are in npm/.\n\n${npmLines.sort().join('\n')}\n`);
}
