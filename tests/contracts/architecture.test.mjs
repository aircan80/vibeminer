import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
test('production project dependencies preserve pure engine boundaries and the original parser policy', async () => {
  const allowed = {
    Domain: [], Application: ['Domain'], 'Protocols.Tact': ['Domain'],
    'Formats.Wow': ['Protocols.Tact'], Infrastructure: ['Application', 'Protocols.Tact', 'Formats.Wow'], Host: ['Application', 'Infrastructure'],
  };
  for (const [project, dependencies] of Object.entries(allowed)) {
    const source = await readFile(path.join(root, `src/backend/VibeMiner.${project}/VibeMiner.${project}.csproj`), 'utf8');
    const references = [...source.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map(match => path.basename(match[1].replaceAll('\\', '/'), '.csproj').replace('VibeMiner.', '')).sort();
    assert.deepEqual(references, [...dependencies].sort(), `${project} references only its declared architecture dependencies`);
    const packages = [...source.matchAll(/<PackageReference\s+Include="([^"]+)"/g)].map(match => match[1]);
    assert.deepEqual(packages, project === 'Infrastructure' ? ['Microsoft.Data.Sqlite'] : [], `${project} does not acquire a game parsing/download dependency`);
  }
});
