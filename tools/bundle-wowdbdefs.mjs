// Rebuilds the bundled WoWDBDefs definitions (#144) from a checkout of github.com/wowdev/WoWDBDefs at the pinned
// revision: node tools/bundle-wowdbdefs.mjs <checkout>. The checkout's manifest.json must hash to the pinned digest.
// Writes sources.json (every file's path, SHA-256 and length, in stream order) and definitions.br (the unmodified
// definition files back to back as one Brotli stream). The engine verifies both at load; see docs/LICENSING.md.
import { createHash } from 'node:crypto';
import { readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { brotliCompressSync, constants } from 'node:zlib';

const revision = 'b9a63096cdd99b26967f17f9515377ae62cadfd7';
const manifestSha256 = 'f7b752a991d6a0321d07ecb7f06e62cd94694c1594d2fe5ce0cb7b243c71559a';
const root = fileURLToPath(new URL('../', import.meta.url));
const target = path.join(root, 'src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs', revision);
const checkout = process.argv[2];
if (!checkout) throw new Error('Usage: node tools/bundle-wowdbdefs.mjs <WoWDBDefs checkout at ' + revision + '>');

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const base = `https://raw.githubusercontent.com/wowdev/WoWDBDefs/${revision}/`;
const manifest = await readFile(path.join(checkout, 'manifest.json'));
if (sha(manifest) !== manifestSha256) throw new Error('manifest.json is not the pinned revision.');
const names = JSON.parse(manifest.toString('utf8')).map(entry => entry.tableName);
const files = (await readdir(path.join(checkout, 'definitions'))).filter(name => name.endsWith('.dbd')).sort();
const expected = names.map(name => `${name}.dbd`).sort();
if (files.length !== expected.length || files.some((name, index) => name !== expected[index]))
  throw new Error('The definitions directory does not match the manifest one to one.');

const resources = [];
for (const name of ['manifest.json', 'LICENSE.md']) {
  const bytes = await readFile(path.join(checkout, name));
  if (!(await readFile(path.join(target, name))).equals(bytes)) throw new Error(`${name} differs from the bundled copy.`);
  resources.push({ path: name, sourceUrl: base + name, sha256: sha(bytes), byteLength: bytes.length });
}
const parts = [], entries = [];
for (const name of files) {
  const bytes = await readFile(path.join(checkout, 'definitions', name));
  if (bytes.length < 1 || bytes.length > 1 << 20) throw new Error(`${name} is outside the 1 MiB definition bound.`);
  parts.push(bytes); entries.push({ path: `definitions/${name}`, sha256: sha(bytes), byteLength: bytes.length });
}
const decoded = Buffer.concat(parts);
// A 1 MiB window: each read allocates a decoder ring that big. 16 MiB saves only 16 KB of stream.
const stream = brotliCompressSync(decoded, { params: {
  [constants.BROTLI_PARAM_QUALITY]: 11, [constants.BROTLI_PARAM_LGWIN]: 20, [constants.BROTLI_PARAM_SIZE_HINT]: decoded.length } });
await writeFile(path.join(target, 'definitions.br'), stream);
const sources = {
  provider: 'WoWDBDefs contributors', revision, definitionLicense: 'CC-BY-SA-4.0', sourceBase: base, resources,
  definitions: { path: 'definitions.br', compression: 'brotli', sha256: sha(stream), byteLength: stream.length, decodedLength: decoded.length, files: entries },
};
// One definition per line keeps the file reviewable; it is still plain JSON.
const text = JSON.stringify(sources, null, 2).replace(/\{\n\s+"path": "(definitions\/[^"]+)",\n\s+"sha256": "([0-9a-f]{64})",\n\s+"byteLength": (\d+)\n\s+\}/g,
  (_, file, hash, length) => `{ "path": "${file}", "sha256": "${hash}", "byteLength": ${length} }`) + '\n';
if (JSON.stringify(JSON.parse(text)) !== JSON.stringify(sources)) throw new Error('sources.json formatting changed its content.');
await writeFile(path.join(target, 'sources.json'), text);
console.log(`${entries.length} definitions, ${decoded.length} bytes, ${stream.length} compressed; sources.json SHA-256 ${sha(Buffer.from(text))}`);
