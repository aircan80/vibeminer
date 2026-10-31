import { mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';

// e2e runs in DejaVu on every machine (#138). The app uses system-ui and ui-monospace, so its layout follows the
// machine's fonts; Ubuntu and the CI runner resolve them to DejaVu, Fedora to Noto, and fit specs that passed locally
// failed on main. Every Electron launch inherits FONTCONFIG_FILE from the runner's environment, so pointing it at a
// fonts.conf that knows only the dejavu-fonts-ttf devDependency gives a local gate the fonts CI renders with.
// VIBEMINER_E2E_SYSTEM_FONTS=1 leaves the machine's fonts in place.
export const systemFonts = process.env.VIBEMINER_E2E_SYSTEM_FONTS === '1';

const xml = (text: string) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const alias = (family: string, to: string) => `  <alias binding="same"><family>${family}</family><prefer><family>${to}</family></prefer></alias>\n`;

export function fontsConf(fonts: string, cache: string) {
  return '<?xml version="1.0"?>\n<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">\n<fontconfig>\n'
    + `  <dir>${xml(fonts)}</dir>\n  <cachedir>${xml(cache)}</cachedir>\n`
    + ['sans-serif', 'sans', 'system-ui'].map(f => alias(f, 'DejaVu Sans')).join('')
    + ['monospace', 'mono', 'ui-monospace'].map(f => alias(f, 'DejaVu Sans Mono')).join('')
    + alias('serif', 'DejaVu Serif')
    // Any other family falls back to DejaVu Sans, as Ubuntu's 49-sansserif.conf does.
    + '  <match target="pattern"><edit name="family" mode="append_last"><string>DejaVu Sans</string></edit></match>\n'
    // Ubuntu 24.04's rendering for DejaVu (fc-match -v there). Fontconfig's own default is full hinting, which rounds
    // each advance to a whole pixel: regular DejaVu Sans renders about 1.5% wider and bold 1% narrower than on CI.
    + '  <match target="font">\n'
    + '    <edit name="antialias" mode="assign"><bool>true</bool></edit>\n'
    + '    <edit name="hinting" mode="assign"><bool>true</bool></edit>\n'
    + '    <edit name="hintstyle" mode="assign"><const>hintslight</const></edit>\n'
    + '    <edit name="autohint" mode="assign"><bool>false</bool></edit>\n'
    + '    <edit name="rgba" mode="assign"><const>rgb</const></edit>\n'
    + '    <edit name="lcdfilter" mode="assign"><const>lcddefault</const></edit>\n'
    + '    <edit name="embeddedbitmap" mode="assign"><bool>true</bool></edit>\n'
    + '  </match>\n</fontconfig>\n';
}

// Writes artifacts/e2e-fonts/fonts.conf and sets FONTCONFIG_FILE to it. Playwright loads the config in the runner and
// again in each worker, so the file is replaced atomically and only when it changes.
export function pinFonts(root: string) {
  if (systemFonts) return undefined;
  let fonts: string;
  try {
    fonts = path.join(path.dirname(createRequire(path.join(root, 'package.json')).resolve('dejavu-fonts-ttf/package.json')), 'ttf');
  } catch {
    throw new Error('e2e pins DejaVu fonts from the dejavu-fonts-ttf devDependency, which is not installed. Run npm ci, or set VIBEMINER_E2E_SYSTEM_FONTS=1 to use the system fonts.');
  }
  const folder = path.join(root, 'artifacts/e2e-fonts'), file = path.join(folder, 'fonts.conf');
  const text = fontsConf(fonts, path.join(folder, 'cache'));
  let current: string | undefined;
  try { current = readFileSync(file, 'utf8'); } catch { current = undefined; }
  if (current !== text) {
    mkdirSync(folder, { recursive: true });
    const next = `${file}.${process.pid}.tmp`;
    writeFileSync(next, text); renameSync(next, file);
  }
  process.env.FONTCONFIG_FILE = file;
  return file;
}
