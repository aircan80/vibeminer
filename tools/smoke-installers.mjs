// Runs the package smoke check against what users actually get from artifacts/release:
// the portable archive and the installer (AppImage + .deb payload, mounted .dmg, or a silent per-user NSIS install).
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const { version } = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const os = { linux: 'linux', win32: 'win', darwin: 'mac' }[process.platform];
const base = `VibeMiner-${version}-${os}-${process.arch}`;
const release = path.join(root, 'artifacts/release');
const run = (command, args, options = {}) => new Promise((resolve, reject) => {
  const child = spawn(command, args, { cwd: root, stdio: 'inherit', ...options });
  child.on('error', reject);
  child.on('exit', code => code === 0 ? resolve() : reject(new Error(`${path.basename(command)} exited ${code}`)));
});
const artifact = name => {
  const file = path.join(release, name);
  if (!existsSync(file)) throw new Error(`Missing ${file}; run npm run package:installers first.`);
  return file;
};
const smoke = executable => run(process.execPath, [path.join(root, 'tools/smoke-package.mjs'), executable]);
const work = await mkdtemp(path.join(tmpdir(), 'vibeminer-installers-'));
const cleanups = [];
try {
  if (os === 'linux') {
    const archive = path.join(work, 'archive');
    await mkdir(archive);
    await run('tar', ['-xzf', artifact(`${base}.tar.gz`), '-C', archive]);
    await smoke(path.join(archive, `${base}/vibeminer`));
    // AppImage without FUSE: the runtime extracts itself to TMPDIR and runs from there.
    await run(process.execPath, [path.join(root, 'tools/smoke-package.mjs'), artifact(`${base}.AppImage`)], { env: { ...process.env, APPIMAGE_EXTRACT_AND_RUN: '1' } });
    const deb = path.join(work, 'deb');
    await mkdir(deb);
    await run('ar', ['x', artifact(`VibeMiner-${version}-linux-amd64.deb`)], { cwd: deb });
    const data = (await readdir(deb)).find(name => name.startsWith('data.tar'));
    await run('tar', ['-xf', data], { cwd: deb });
    await smoke(path.join(deb, 'opt/VibeMiner/vibeminer'));
  } else if (os === 'mac') {
    const archive = path.join(work, 'archive');
    await run('ditto', ['-x', '-k', artifact(`${base}.zip`), archive]);
    await smoke(path.join(archive, 'VibeMiner.app/Contents/MacOS/vibeminer'));
    const mount = path.join(work, 'dmg');
    await run('hdiutil', ['attach', '-nobrowse', '-readonly', '-noautoopen', '-mountpoint', mount, artifact(`${base}.dmg`)]);
    cleanups.push(() => run('hdiutil', ['detach', mount, '-force']));
    await smoke(path.join(mount, 'VibeMiner.app/Contents/MacOS/vibeminer'));
  } else {
    const archive = path.join(work, 'archive');
    await mkdir(archive);
    await run(path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'tar.exe'), ['-xf', artifact(`${base}.zip`), '-C', archive]);
    await smoke(path.join(archive, 'vibeminer.exe'));
    // NSIS wants /D= last and unquoted, so pass the arguments verbatim. _?= keeps the uninstaller in place and synchronous.
    const installed = path.join(work, 'installed');
    await run(artifact(`${base}-setup.exe`), ['/S', `/D=${installed}`], { windowsVerbatimArguments: true });
    cleanups.push(async () => {
      const uninstaller = path.join(installed, 'Uninstall VibeMiner.exe');
      if (existsSync(uninstaller)) await run(uninstaller, ['/S', `_?=${installed}`], { windowsVerbatimArguments: true });
    });
    await smoke(path.join(installed, 'vibeminer.exe'));
  }
  process.stdout.write(`Installer smoke checks passed for ${base}.\n`);
} finally {
  for (const cleanup of cleanups.reverse()) await cleanup().catch(error => process.stderr.write(`cleanup: ${error.message}\n`));
  await rm(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 1000 });
}
