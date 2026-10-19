import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { compile } from 'json-schema-to-typescript';

const root = new URL('../', import.meta.url);
const schema = JSON.parse(await readFile(new URL('contracts/discovery.schema.json', root), 'utf8'));
const output = await compile(schema, 'DiscoveryContract', {
  bannerComment: '/* Generated from contracts/discovery.schema.json. Run npm run contracts:generate. */',
  additionalProperties: false,
  style: { singleQuote: true, semi: true, tabWidth: 2 },
});
const destination = new URL('contracts/generated/discovery.ts', root);
if (process.argv.includes('--check')) {
  const actual = await readFile(destination, 'utf8');
  if (actual !== output) throw new Error('Contract types are stale. Run npm run contracts:generate.');
  process.stdout.write('Generated contract types match the canonical schema.\n');
} else {
  await mkdir(fileURLToPath(new URL('contracts/generated/', root)), { recursive: true });
  await writeFile(destination, output);
  process.stdout.write('Generated contracts/generated/discovery.ts.\n');
}
