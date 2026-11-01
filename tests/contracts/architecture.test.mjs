import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
test('production project dependencies preserve pure engine boundaries and the original parser policy', async () => {
  const allowed = {
    Domain: [], Application: ['Domain'], 'Protocols.Tact': ['Domain'],
    'Formats.Wow': ['Protocols.Tact'], Infrastructure: ['Application', 'Protocols.Tact', 'Formats.Wow'], Host: ['Application', 'Infrastructure'],
    // Layer 1 (D121) stands alone: no catalog, builds, protocol parsers or features.
    Objects: [],
    // Layer 1 CDN and version-service clients (D121): keys, bytes and HTTP only.
    Cdn: [],
    // Layer 2 object access (D135): builds only on layer 1.
    ObjectAccess: ['Objects', 'Cdn'],
    // Layer 3 builds (D138): builds only on layer 2, with its own table reader and identity.
    Builds: ['ObjectAccess'],
    // Layer 4 files (D160): a file system per build and locale over layers 3 and 2, never layer 1.
    Files: ['Builds', 'ObjectAccess'],
    // Layer 5 formats: reads through layer 4 only (#177).
    Formats: ['Files'],
    // Layer 7 developer CLI (#170, D150): its own composition root over the layer projects, never the legacy ones.
    Cli: ['Objects', 'Cdn', 'ObjectAccess', 'Builds'],
  };
  for (const [project, dependencies] of Object.entries(allowed)) {
    const source = await readFile(path.join(root, `src/backend/VibeMiner.${project}/VibeMiner.${project}.csproj`), 'utf8');
    const references = [...source.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map(match => path.basename(match[1].replaceAll('\\', '/'), '.csproj').replace('VibeMiner.', '')).sort();
    assert.deepEqual(references, [...dependencies].sort(), `${project} references only its declared architecture dependencies`);
    const packages = [...source.matchAll(/<PackageReference\s+Include="([^"]+)"/g)].map(match => match[1]);
    // Spectre.Console (D151) and Terminal.Gui (D165) are terminal output for the developer CLI only.
    const expected = project === 'Infrastructure' || project === 'Objects' || project === 'Builds' ? ['Microsoft.Data.Sqlite'] : project === 'Cli' ? ['Spectre.Console', 'Terminal.Gui'] : [];
    assert.deepEqual(packages, expected, `${project} does not acquire a game parsing/download dependency`);
  }
});

test('backend projects reference only their own code layer or lower ones', async () => {
  // Layers from docs/ARCHITECTURE.md "Code layers". Legacy shared projects sit at 0 (the floor),
  // legacy Application/Infrastructure at 3 (they still hold parts of layers 1-6, but
  // reference no layer project, so 3 keeps them above the new layer 1-2 projects), Host at 7 (composition).
  const production = {
    Domain: 0, 'Protocols.Tact': 0, 'Formats.Wow': 0,
    Objects: 1, Cdn: 1, ObjectAccess: 2, Builds: 3, Files: 4, Formats: 5,
    Application: 3, Infrastructure: 3, Host: 7, Cli: 7,
  };
  const layerProjects = new Set(['Objects', 'Cdn', 'ObjectAccess', 'Builds', 'Files', 'Formats', 'Cli']);
  const legacy = new Set(Object.keys(production).filter(name => !layerProjects.has(name)));
  const fakes = { 'Cdn.Testing': 1, 'ObjectAccess.Testing': 2, 'Builds.Testing': 3, 'Files.Testing': 4, 'Objects.CrashWriter': 1 };
  const layerTests = { 'Primitives.Tests': 1, 'ObjectAccess.Tests': 2, 'Builds.Tests': 3, 'Files.Tests': 4, 'Formats.Tests': 5, 'Cli.Tests': 7 };
  // Cross-layer integration projects drive real lower layers by design (D137), so they have no single layer.
  // Formats.Comparison cross-checks the layer 5 reader against the legacy Formats.Wow one until slice 9 deletes it (#177).
  const exemptTests = new Set(['Backend.Tests', 'Storage.IntegrationTests', 'Formats.Comparison']);
  const referencesOf = async file => {
    const source = await readFile(path.join(root, file), 'utf8');
    return [...source.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map(match => path.basename(match[1].replaceAll('\\', '/'), '.csproj').replace('VibeMiner.', ''));
  };
  const projectsIn = async dir => (await readdir(path.join(root, dir), { withFileTypes: true }))
    .filter(entry => entry.isDirectory() && entry.name.startsWith('VibeMiner.'))
    .map(entry => entry.name.replace('VibeMiner.', ''));

  for (const project of await projectsIn('src/backend')) {
    assert.ok(project in production, `src/backend/VibeMiner.${project} has a code layer in the architecture test`);
  }
  for (const project of await projectsIn('tests/backend')) {
    assert.ok(project in fakes || project in layerTests || exemptTests.has(project), `tests/backend/VibeMiner.${project} has a code layer in the architecture test`);
  }

  for (const [project, layer] of Object.entries(production)) {
    for (const reference of await referencesOf(`src/backend/VibeMiner.${project}/VibeMiner.${project}.csproj`)) {
      assert.ok(reference in production, `${project} references a known production project (${reference})`);
      assert.ok(production[reference] <= layer, `${project} (layer ${layer}) does not reference higher layer ${reference} (layer ${production[reference]})`);
      if (layerProjects.has(project)) assert.ok(!legacy.has(reference), `layer project ${project} does not reference legacy project ${reference}`);
    }
  }

  const testLayers = { ...production, ...fakes };
  for (const [project, layer] of Object.entries({ ...fakes, ...layerTests })) {
    for (const reference of await referencesOf(`tests/backend/VibeMiner.${project}/VibeMiner.${project}.csproj`)) {
      assert.ok(reference in testLayers, `${project} references a known production project or fake (${reference})`);
      assert.ok(testLayers[reference] <= layer, `${project} (layer ${layer}) does not reference higher layer ${reference} (layer ${testLayers[reference]})`);
      assert.ok(!legacy.has(reference), `layer test project ${project} does not reference legacy project ${reference}`);
    }
  }
  // The comparison exemption stays narrow: exactly the two readers it diffs, nothing else.
  assert.deepEqual((await referencesOf('tests/backend/VibeMiner.Formats.Comparison/VibeMiner.Formats.Comparison.csproj')).sort(), ['Formats', 'Formats.Wow'],
    'Formats.Comparison references only the layer 5 and legacy DB2 readers');
});
