import { defineConfig } from '@playwright/test';
// No e2e run reaches GitHub (D105): every engine these tests start sends its community listfile requests to a loopback
// port where nothing listens, so a real engine that opens Files reports the listfile as offline instead of downloading it.
process.env.VIBEMINER_LISTFILE_ORIGIN = 'http://127.0.0.1:9/';
// Nor wago.tools (D106): a real engine's Refresh builds asks that same dead port for the community build list.
process.env.VIBEMINER_COMMUNITY_BUILDS_ORIGIN = 'http://127.0.0.1:9/';
export default defineConfig({
  // One worker per run: tools/e2e-lock.mjs sizes its slots for runs of one Electron app at a time (docs/TESTING.md).
  // Don't raise it or pass --workers, or N parallel runs oversubscribe the CPUs those slots assume.
  testDir: '.', testMatch: '*.spec.ts', workers: 1, timeout: 30_000,
  expect: { timeout: 8_000 },
  outputDir: '../../artifacts/electron-tests',
  reporter: [['list'], ['html', { outputFolder: '../../artifacts/electron-report', open: 'never' }]],
});
