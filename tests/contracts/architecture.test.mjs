import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
test('production project dependencies preserve pure engine boundaries and the original parser policy', async () => {
  const allowed = {
    Domain: [], Application: ['Domain'], 'Protocols.Tact': ['Domain'],
    'Formats.Wow': ['Protocols.Tact'], Infrastructure: ['Application', 'Protocols.Tact', 'Formats.Wow'],
    // Composition and Builds: the desktop's build list from layer 3 (#205).
    Host: ['Application', 'Infrastructure', 'Composition', 'Builds'],
    // Layer 1 (D121) stands alone: no catalog, builds, protocol parsers or features.
    Objects: [],
    // Layer 1 CDN and version-service clients (D121): keys, bytes and HTTP only.
    Cdn: [],
    // Layer 2 object access (D135): builds only on layer 1.
    // Layer 1 generated store (D210): a second pack store over layer 1's code, private to layers 4-6.
    Generated: ['Objects'],
    ObjectAccess: ['Objects', 'Cdn'],
    // Layer 3 builds (D138): builds only on layer 2, with its own table reader and identity.
    Builds: ['ObjectAccess'],
    // Layer 4 files (D160): a file system per build and locale over layers 3 and 2, never layer 1's object store.
    // Generated is the layer 1 generated store (D210) for the persisted listfile names index (#186).
    Files: ['Builds', 'ObjectAccess', 'Generated'],
    // Layer 5 formats: reads files through layer 4 (#177). ObjectAccess is for the table definitions only: fetched
    // definitions are layer 2 observations and history (D180). Generated is the layer 1 generated store for decoded tables (D210, #179).
    Formats: ['Files', 'ObjectAccess', 'Generated'],
    // Layer 6 game concepts (#204): joins layer 5 tables and reads images and models through layer 5 only; layer 4 types come through it.
    Concepts: ['Formats'],
    // Layer 7 developer CLI (#170, D150): its own composition root over the layer projects, never the legacy ones.
    // Generated: the browse session opens the profile's generated store once, for layer 4's listfile names index (#186) and tables.
    // Composition is the layer 7 composition shared with the desktop Host (#205). Concepts: items, item effects and creatures
    // for :items, :creatures and concepts states (#204).
    Cli: ['Objects', 'Cdn', 'Generated', 'ObjectAccess', 'Builds', 'Files', 'Formats', 'Composition', 'Concepts'],
    // Layer 7 composition shared by vm and the desktop Host (#205): data-folder paths, the writer's layer 2 over layers 1-3.
    Composition: ['Objects', 'Cdn', 'ObjectAccess', 'Builds'],
  };
  for (const [project, dependencies] of Object.entries(allowed)) {
    const source = await readFile(path.join(root, `src/backend/VibeMiner.${project}/VibeMiner.${project}.csproj`), 'utf8');
    const references = [...source.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map(match => path.basename(match[1].replaceAll('\\', '/'), '.csproj').replace('VibeMiner.', '')).sort();
    assert.deepEqual(references, [...dependencies].sort(), `${project} references only its declared architecture dependencies`);
    const packages = [...source.matchAll(/<PackageReference\s+Include="([^"]+)"/g)].map(match => match[1]);
    // Spectre.Console (D151) and Terminal.Gui (D165) are terminal output for the developer CLI only; ZstdSharp.Port (D211) is the generated store's zstd.
    const expected = project === 'Infrastructure' || project === 'Objects' || project === 'Builds' ? ['Microsoft.Data.Sqlite'] : project === 'Cli' ? ['Spectre.Console', 'Terminal.Gui'] : project === 'Generated' ? ['ZstdSharp.Port'] : [];
    assert.deepEqual(packages, expected, `${project} does not acquire a game parsing/download dependency`);
  }
});

test('backend projects reference only their own code layer or lower ones', async () => {
  // Layers from docs/ARCHITECTURE.md "Code layers". Legacy shared projects sit at 0 (the floor),
  // legacy Application/Infrastructure at 3 (they still hold parts of layers 1-6, but
  // reference no layer project, so 3 keeps them above the new layer 1-2 projects), Host at 7 (composition).
  const production = {
    Domain: 0, 'Protocols.Tact': 0, 'Formats.Wow': 0,
    Objects: 1, Cdn: 1, Generated: 1, ObjectAccess: 2, Builds: 3, Files: 4, Formats: 5, Concepts: 6,
    Application: 3, Infrastructure: 3, Host: 7, Cli: 7, Composition: 7,
  };
  const layerProjects = new Set(['Objects', 'Cdn', 'Generated', 'ObjectAccess', 'Builds', 'Files', 'Formats', 'Concepts', 'Cli', 'Composition']);
  const legacy = new Set(Object.keys(production).filter(name => !layerProjects.has(name)));
  const fakes = { 'Cdn.Testing': 1, 'Generated.Testing': 1, 'Objects.Testing': 1, 'ObjectAccess.Testing': 2, 'Builds.Testing': 3, 'Files.Testing': 4, 'Formats.Testing': 5, 'Concepts.Testing': 6, 'Objects.CrashWriter': 1 };
  const layerTests = { 'Primitives.Tests': 1, 'ObjectAccess.Tests': 2, 'Builds.Tests': 3, 'Files.Tests': 4, 'Formats.Tests': 5, 'Concepts.Tests': 6, 'Cli.Tests': 7 };
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
  // The other two exemptions are pinned the same way. Backend.Tests covers the legacy code and the Host; the layer 2 and 3 fakes are
  // for the Host's build catalog (#205).
  assert.deepEqual((await referencesOf('tests/backend/VibeMiner.Backend.Tests/VibeMiner.Backend.Tests.csproj')).sort(), ['Builds.Testing', 'Host', 'Infrastructure', 'ObjectAccess.Testing'],
    'Backend.Tests references only the Host, Infrastructure and the layer 2 and 3 fakes');
  // Storage.IntegrationTests drives real layers 1-5 (D137, #195) against the fakes, never a legacy project or the CLI.
  assert.deepEqual((await referencesOf('tests/backend/VibeMiner.Storage.IntegrationTests/VibeMiner.Storage.IntegrationTests.csproj')).sort(),
    ['Cdn.Testing', 'Files.Testing', 'Formats', 'Generated.Testing', 'ObjectAccess', 'ObjectAccess.Testing', 'Objects.CrashWriter', 'Objects.Testing'],
    'Storage.IntegrationTests references only layers 1-5 and their fakes');
});
