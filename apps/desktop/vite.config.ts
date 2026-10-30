import { availableParallelism } from 'node:os';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
export default defineConfig({ plugins: [react()], base: './', build: { outDir: 'dist/renderer', emptyOutDir: false }, test: {
  environment: 'jsdom', include: ['test/**/*.test.{ts,tsx}'], setupFiles: ['test/setup.ts'],
  // Vitest starts one jsdom worker per CPU minus one, assuming it owns the machine. Each worker pays its own jsdom and
  // module setup, so on 32 CPUs 31 workers spent 166 CPU-seconds and 26 s against 124 CPU-seconds and 21 s for 8: the
  // longest file sets the wall time. Several gates running at once multiplied that waste (#104).
  maxWorkers: Math.max(1, Math.min(8, availableParallelism() - 1)),
  // Deadlock guards, not performance claims (#104, docs/TESTING.md). The tests are CPU-bound (React development
  // renders and role queries), so their wall time follows the CPU share: a 1.3 s test took 13.6 s with a CPU hog
  // running and failed the old 5 s default.
  testTimeout: 60_000, hookTimeout: 60_000,
} });
