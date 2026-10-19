import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
export default defineConfig({ plugins: [react()], base: './', build: { outDir: 'dist/renderer', emptyOutDir: false }, test: { environment: 'jsdom', include: ['test/**/*.test.{ts,tsx}'] } });
