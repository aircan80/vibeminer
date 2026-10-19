// Render the selected immutable mockup with the same Electron engine used by the app.
import { _electron as electron } from '@playwright/test';
import { mkdtemp, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const directory = await mkdtemp(path.join(tmpdir(), 'vibeminer-design-'));
const output = path.join(root, 'artifacts/design-reference');
await mkdir(output, { recursive: true });
const view = process.argv[2] || 'discovery';
const references = {
  discovery: { directory: 'build-discovery', query: 'concept=guided&state=ready&product=wow', selector: '.layout.wide', prefix: 'guided' },
  preparation: { directory: 'build-preparation', query: 'option=dedicated&state=cached', selector: '.center-stage', prefix: 'preparation' },
  home: { directory: 'build-mining-home', query: 'concept=archive&state=ready', selector: '.shell', prefix: 'home' },
  desktop: { directory: 'desktop-discovery', query: 'state=idle&chrome=0', selector: '.statusbar', prefix: 'desktop-discovery' },
};
const selected = references[view];
if (!selected) throw new Error('Choose discovery, desktop, preparation or home.');
const reference = pathToFileURL(path.join(root, `docs/design/${selected.directory}/index.html`));
reference.search = selected.query;
const application = await electron.launch({
  args: [path.join(root, 'apps/desktop'), '--ozone-platform=headless', `--user-data-dir=${path.join(directory, 'profile')}`],
  env: { ...process.env, VIBEMINER_DATA_DIR: path.join(directory, 'catalog') },
});
try {
  const page = await application.firstWindow();
  await application.evaluate(async ({ BrowserWindow }, url) => { await BrowserWindow.getAllWindows()[0].loadURL(url); }, reference.href);
  await page.locator(selected.selector).waitFor();
  // Exclude the design review's concept/state controls; keep its synthetic-data notice.
  await page.evaluate(() => { document.querySelectorAll('.conceptbar,.demo,.designbar,.preview,.rationale,.review,.states,.mock').forEach(element => element.remove()); });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
    await page.screenshot({ path: path.join(output, `${selected.prefix}-${width}x${height}.png`), fullPage: true });
  }
  process.stdout.write(`Captured selected ${view} reference using Electron in ${output}\n`);
} finally {
  await application.close();
  await rm(directory, { recursive: true, force: true });
}
