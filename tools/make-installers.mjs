// Wraps the native package from tools/package-desktop.mjs into installers and a portable archive.
// electron-builder only runs in --prepackaged mode here: it never rebuilds or re-signs the app,
// and it works on a copy, so artifacts/desktop stays exactly as smoke:package checked it.
import { build } from 'electron-builder';
import { existsSync } from 'node:fs';
import { cp, mkdir, readFile, rename, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const electron = JSON.parse(await readFile(path.join(root, 'node_modules/electron/package.json'), 'utf8'));
const { version } = manifest;
const os = { linux: 'linux', win32: 'win', darwin: 'mac' }[process.platform];
const target = `${os}-${process.arch}`;
if (!['linux-x64', 'win-x64', 'mac-arm64', 'mac-x64'].includes(target)) throw new Error(`Unconfigured installer target: ${target}`);
const unpacked = path.join(root, 'artifacts/desktop', `VibeMiner-${process.platform}-${process.arch}`);
const prepackaged = os === 'mac' ? path.join(unpacked, 'VibeMiner.app') : unpacked;
if (!existsSync(prepackaged)) throw new Error(`Missing ${prepackaged}; run npm run package:desktop first.`);
const base = `VibeMiner-${version}-${target}`;
// Each electron-builder target gets its own copy of the package, so no target can change another's
// content (the deb target writes resources/apparmor-profile into the directory it packages).
const targets = {
  linux: [['AppImage', `${base}.AppImage`], ['deb', `VibeMiner-${version}-linux-amd64.deb`], ['tar.gz', `${base}.tar.gz`]],
  win: [['nsis', `${base}-setup.exe`], ['zip', `${base}.zip`]],
  mac: [['dmg', `${base}.dmg`], ['zip', `${base}.zip`]],
}[os];
const work = path.join(root, '.local/installer-stage', target);
const out = path.join(root, 'artifacts/release');
await rm(work, { recursive: true, force: true });
await mkdir(out, { recursive: true });
const icons = path.join(root, 'branding/icons');
for (const [builderTarget, name] of targets) {
  const copy = path.join(work, builderTarget, 'package');
  await cp(unpacked, copy, { recursive: true, verbatimSymlinks: true });
  const output = path.join(work, builderTarget, 'out');
  await build({
    projectDir: root,
    prepackaged: os === 'mac' ? path.join(copy, 'VibeMiner.app') : copy,
    publish: 'never',
    [os]: [builderTarget],
    config: {
      appId: 'io.vibeminer.desktop',
      productName: 'VibeMiner',
      executableName: 'vibeminer',
      copyright: 'Copyright (C) 2026 aircan80 and VibeMiner contributors',
      electronVersion: electron.version,
      directories: { output },
      publish: null,
      // Signing hook: unsigned today. Installer signing would come from CSC_LINK / WIN_CSC_LINK here,
      // after the app itself is signed in tools/package-desktop.mjs.
      win: { icon: path.join(icons, 'vibeminer.ico'), artifactName: `${base}.\${ext}` },
      nsis: { artifactName: `${base}-setup.\${ext}`, oneClick: true, perMachine: false, deleteAppDataOnUninstall: false },
      mac: { identity: null, icon: path.join(icons, 'vibeminer.icns'), category: 'public.app-category.developer-tools', artifactName: `${base}.\${ext}` },
      dmg: { writeUpdateInfo: false },
      linux: {
        executableName: 'vibeminer', icon: path.join(icons, 'vibeminer.png'),
        category: 'Development', artifactName: `${base}.\${ext}`,
        synopsis: 'Explore World of Warcraft data from the official Blizzard CDN',
        description: 'VibeMiner explores World of Warcraft build metadata and data from the official Blizzard CDN.',
        maintainer: 'aircan80 <329744803+aircan80@users.noreply.github.com>', vendor: 'VibeMiner contributors',
      },
      deb: { artifactName: `VibeMiner-${version}-linux-amd64.deb`, packageCategory: 'utils' },
    },
  });
  const file = path.join(output, name);
  if (!existsSync(file)) throw new Error(`electron-builder did not produce ${name}.`);
  await rename(file, path.join(out, name));
  await rm(path.join(work, builderTarget), { recursive: true, force: true });
}
const expected = targets.map(([, name]) => name);
process.stdout.write(`Created unsigned ${target} installers in artifacts/release:\n${expected.join('\n')}\n`);
