// @vitest-environment node
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, it } from 'vitest';
// #119: a stylesheet nothing imports is dead weight that reads as live (browser-base.css was left behind that way).
const renderer = fileURLToPath(new URL('../src/renderer/', import.meta.url));
it('imports every renderer stylesheet from a renderer module', () => {
  const files = readdirSync(renderer);
  const imported = new Set(files.filter(f => /\.tsx?$/.test(f)).flatMap(f => [...readFileSync(join(renderer, f), 'utf8').matchAll(/^import '\.\/([\w-]+\.css)';$/gm)].map(m => m[1])));
  const sheets = files.filter(f => f.endsWith('.css'));
  expect(sheets.length).toBeGreaterThan(0);
  expect(sheets.filter(f => !imported.has(f))).toEqual([]);
});
