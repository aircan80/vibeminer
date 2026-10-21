import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
const directory = path.join(root, '.github/workflows');

async function workflows() {
  const names = (await readdir(directory)).filter(name => /\.ya?ml$/.test(name)).sort();
  return Promise.all(names.map(async name => ({ name, source: await readFile(path.join(directory, name), 'utf8') })));
}

test('every workflow action is pinned to a full commit SHA, and an action uses one SHA everywhere', async () => {
  const pins = new Map();
  for (const { name, source } of await workflows()) {
    for (const match of source.matchAll(/^\s*(?:-\s+)?uses:\s*(\S+)(.*)$/gm)) {
      const [, reference, rest] = match;
      if (reference.startsWith('./')) continue;
      const parsed = /^([\w.-]+\/[\w./-]+)@([0-9a-f]{40})$/.exec(reference);
      assert.ok(parsed, `${name}: ${reference} is pinned to a full commit SHA`);
      assert.match(rest, /^\s+#\s+v\d/, `${name}: ${reference} names its version in a comment`);
      const [, action, sha] = parsed;
      const earlier = pins.get(action);
      if (earlier) assert.equal(sha, earlier.sha, `${name} pins ${action} like ${earlier.name}`);
      else pins.set(action, { sha, name });
    }
  }
  assert.ok(pins.size > 0, 'workflows were found and read');
});

test('CI runs candidate refs on every platform with read-only permissions and no secrets', async () => {
  const source = await readFile(path.join(directory, 'ci.yml'), 'utf8');
  assert.match(source, /^on:\n  push:\n    branches:\n      - 'ci\/\*\*'\n  workflow_dispatch:\n\n(?=\S)/m, 'CI triggers only on ci/** pushes and dispatch');
  assert.match(source, /^permissions:\n  contents: read\n\n/m);
  assert.equal([...source.matchAll(/^\s*permissions:/gm)].length, 1, 'no job widens permissions');
  assert.doesNotMatch(source, /secrets\.|pull_request_target/);
  assert.match(source, /fail-fast: false/);
  assert.match(source, /os: \[ubuntu-latest, windows-latest, macos-latest\]/);
  for (const command of ['npm ci', 'node tools/dotnet.mjs restore VibeMiner.slnx --locked-mode', 'npm run build', 'npm run test:e2e']) {
    assert.ok(source.includes(command), `CI runs ${command}`);
  }
  // Every part of `npm test` is its own step that still runs when an earlier suite failed.
  const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
  const parts = manifest.scripts.test.split(' && ').map(part => part.trim());
  assert.deepEqual(parts.map(part => /^npm run [\w:-]+$/.test(part)), parts.map(() => true), 'npm test only chains npm scripts');
  for (const part of parts) {
    const step = new RegExp(`\\n {8}if: \\$\\{\\{ !cancelled\\(\\) && steps\\.build\\.outcome == 'success' \\}\\}\\n(?: {8}.*\\n)*? {8}run: ${part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')} `);
    assert.match(source, step, `CI runs ${part} as its own step after a successful build`);
  }
});
