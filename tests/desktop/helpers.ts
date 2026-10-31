import { _electron as electron, type ElectronApplication, type Page } from '@playwright/test';
import { mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
export const root = fileURLToPath(new URL('../../', import.meta.url));
export async function launch(fixtures = false, offlineEmpty = false, retention = false, items = false, icons = false, classifications: false|'class'|'subclass'=false, creatures=false,creatureDisplay=false,effects:false|true|'split'=false,referenceText=false,follow=false,fileCatalog=false,geometry=false,longProducts=false,extra:{args?:readonly string[];env?:Readonly<Record<string,string>>}={}) {
  const data = await mkdtemp(path.join(tmpdir(), 'vibeminer-electron-'));
  const app = await electron.launch({
    args: [process.env.VIBEMINER_DESKTOP_TEST_APP_PATH??path.join(root, 'apps/desktop'), '--ozone-platform=headless', `--user-data-dir=${path.join(data, 'electron-profile')}`, ...(extra.args??[])],
    env: { ...process.env, VIBEMINER_DATA_DIR: path.join(data, 'catalog'), ...(fixtures ? {
      VIBEMINER_ENGINE_PATH: path.join(root, 'tests/desktop/fixtures/engine.mjs'),
      VIBEMINER_FIXTURE_CONTROL_DIR: data, VIBEMINER_AUTHORED_FIXTURES: '1',
      ...(retention ? { VIBEMINER_FIXTURE_RETENTION: '1' } : {}),
      ...(items||icons||classifications||effects ? { VIBEMINER_FIXTURE_ITEMS: '1' } : {}),
      ...(creatures?{VIBEMINER_FIXTURE_CREATURES:'1'}:{}),
      ...(fileCatalog?{VIBEMINER_FIXTURE_FILE_CATALOG:'1'}:{}),
      ...(longProducts?{VIBEMINER_FIXTURE_LONG_PRODUCTS:'1'}:{}),
      ...(creatureDisplay?{VIBEMINER_FIXTURE_CREATURE_DISPLAY:'1'}:{}),
      ...(geometry?{VIBEMINER_FIXTURE_CREATURE_GEOMETRY:'1'}:{}),
      ...(effects?{VIBEMINER_FIXTURE_EFFECTS:effects==='split'?'split':'1'}:{}),
      ...(icons ? { VIBEMINER_FIXTURE_ICONS: '1' } : {}),
      ...(referenceText ? { VIBEMINER_FIXTURE_REFERENCE_TEXT: '1' } : {}),
      ...(follow ? { VIBEMINER_FIXTURE_FOLLOW: '1' } : {}),
      ...(classifications?{VIBEMINER_FIXTURE_CLASSIFICATIONS:classifications}:{}),
      ...(offlineEmpty ? { VIBEMINER_FIXTURE_OFFLINE_EMPTY: '1' } : {}),
    } : {}), ...(extra.env??{}) },
  });
  const page = await app.firstWindow();
  return { app, page, data, close: async () => { await app.close(); await rm(data, { recursive: true, force: true }); } };
}

export async function size(app: ElectronApplication, width: number, height: number) { await app.evaluate(({ BrowserWindow }, s) => BrowserWindow.getAllWindows()[0].setContentSize(s.width, s.height), { width, height }); }
export async function text(page: Page, scale: number) { await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, 15 * scale); }
// A stand-in for DejaVu Sans (#130), the system font on Ubuntu and the CI runner. The app's layout follows the machine's
// fonts, and DejaVu Sans renders about a tenth wider than Fedora's Noto Sans, and bold a sixth wider. This puts an
// installed sans (DejaVu Sans itself where it is installed) in system-ui's place, each weight scaled to the width
// DejaVu Sans gives a reference line at 15px, so a fit test catches columns sized for one font on any machine. Where
// DejaVu Sans is the system font it changes nothing. Fails if no installed font can be scaled to that width.
const dejaVu = { line: 'Object 2879941 Verified locally 12.1.5.70448 Current Encoded size Local at assessment Memberships', regular: 778, bold: 851 };
export async function widerSystemFont(page: Page) {
  const fit = await page.evaluate(async target => {
    const names = { regular: ['DejaVu Sans', 'DejaVuSans', 'Noto Sans Regular', 'NotoSans-Regular', 'Liberation Sans', 'LiberationSans'],
      bold: ['DejaVu Sans Bold', 'DejaVuSans-Bold', 'Noto Sans Bold', 'NotoSans-Bold', 'Liberation Sans Bold', 'LiberationSans-Bold'] };
    const face = (weights: string, list: string[], adjust: number) => `@font-face{font-family:vm-wider-sans;font-weight:${weights};src:${list.map(name => `local("${name}")`).join(',')};size-adjust:${adjust}%}`;
    const style = document.createElement('style'); style.id = 'wider-system-font'; document.head.append(style);
    // Every rule that names system-ui, from :root and body to each screen, puts the stand-in first.
    const rules = (list: CSSRuleList): CSSStyleRule[] => [...list].flatMap(rule => rule instanceof CSSStyleRule ? [rule, ...rules(rule.cssRules)] : 'cssRules' in rule ? rules((rule as CSSGroupingRule).cssRules) : []);
    for (const sheet of document.styleSheets) for (const rule of rules(sheet.cssRules)) {
      if (/^system-ui\b/.test(rule.style.fontFamily)) rule.style.fontFamily = `vm-wider-sans, ${rule.style.fontFamily}`;
    }
    const width = async (weight: number) => {
      await document.fonts.ready;
      const probe = document.createElement('span'); probe.style.cssText = `position:absolute;font:${weight} 15px vm-wider-sans;white-space:nowrap`; probe.textContent = target.line;
      document.body.append(probe); const w = probe.getBoundingClientRect().width; probe.remove(); return w;
    };
    // Glyph advances snap to whole pixels at 15px, so scale twice to land on the target.
    const adjust = { regular: 100, bold: 100 };
    for (let pass = 0; pass < 3; pass++) {
      style.textContent = face('100 500', names.regular, adjust.regular) + face('600 900', names.bold, adjust.bold);
      const now = { regular: await width(400), bold: await width(600) };
      if (pass === 2) return { regular: now.regular / target.regular, bold: now.bold / target.bold };
      adjust.regular *= target.regular / now.regular; adjust.bold *= target.bold / now.bold;
    }
    throw new Error('unreachable');
  }, dejaVu);
  if (Math.abs(fit.regular - 1) > .015 || Math.abs(fit.bold - 1) > .015) throw new Error(`No installed sans scales to DejaVu Sans's width: ${JSON.stringify(fit)}`);
  return fit;
}
