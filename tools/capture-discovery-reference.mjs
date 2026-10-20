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
  'all-products': { directory: 'desktop-discovery', file: 'all-products.html', query: 'state=idle&chrome=0', selector: '.statusbar', prefix: 'all-products' },
};
const selected = references[view];
if (!selected) throw new Error('Choose discovery, desktop, all-products, preparation or home.');
const reference = pathToFileURL(path.join(root, `docs/design/${selected.directory}/${selected.file ?? 'index.html'}`));
reference.search = selected.query;
const application = await electron.launch({
  args: [path.join(root, 'apps/desktop'), '--ozone-platform=headless', `--user-data-dir=${path.join(directory, 'profile')}`],
  env: { ...process.env, VIBEMINER_DATA_DIR: path.join(directory, 'catalog') },
});
try {
  // The reference gets its own window. Loading it into the app window raced the app's first load and aborted (-3).
  await application.firstWindow();
  const [page, id] = await Promise.all([
    application.waitForEvent('window'),
    application.evaluate(async ({ BrowserWindow }, url) => {
      const window = new BrowserWindow({ width: 1280, height: 800, useContentSize: true, webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true } });
      // Headless ozone leaves a new window's viewport at 1x1 until it is sized explicitly.
      window.setContentSize(1280, 800);
      await window.loadURL(url);
      return window.id;
    }, reference.href),
  ]);
  await page.locator(selected.selector).waitFor();
  // Exclude the design review's concept/state controls; keep its synthetic-data notice.
  await page.evaluate(() => { document.querySelectorAll('.conceptbar,.demo,.designbar,.preview,.rationale,.review,.states,.mock').forEach(element => element.remove()); });
  for (const [width, height] of [[1280, 800], [1440, 1100]]) {
    await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.fromId(size.id).setContentSize(size.width, size.height), { id, width, height });
    await page.waitForFunction(size => innerWidth === size.width && innerHeight === size.height, { width, height });
    await page.screenshot({ path: path.join(output, `${selected.prefix}-${width}x${height}.png`), fullPage: true });
  }
  process.stdout.write(`Captured selected ${view} reference using Electron in ${output}\n`);
} finally {
  await application.close();
  await rm(directory, { recursive: true, force: true });
}
