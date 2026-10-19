import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: '.', testMatch: '*.spec.ts', workers: 1, timeout: 30_000,
  expect: { timeout: 8_000 },
  outputDir: '../../artifacts/electron-tests',
  reporter: [['list'], ['html', { outputFolder: '../../artifacts/electron-report', open: 'never' }]],
});
