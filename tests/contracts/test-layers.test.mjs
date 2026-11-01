import test from 'node:test';
import assert from 'node:assert/strict';
import { planFor } from '../../tools/test-layers.mjs';

// Checks the changed-paths planner in tools/test-layers.mjs (D137) without running git or any tests.
const commands = (changes, exists = () => false) =>
  planFor(changes.map(path => typeof path === 'string' ? { path, publicApi: false } : path), { exists })
    .runs.map(run => [run.command, ...run.args].join(' '));
const everything = ['npm run contracts:check', 'npm run test:backend', 'npm run test:desktop', 'npm run test:contracts'];
const primitives = 'node tools/dotnet.mjs test tests/backend/VibeMiner.Primitives.Tests';
const objectAccess = 'node tools/dotnet.mjs test tests/backend/VibeMiner.ObjectAccess.Tests';
const builds = 'node tools/dotnet.mjs test tests/backend/VibeMiner.Builds.Tests';
const files = 'node tools/dotnet.mjs test tests/backend/VibeMiner.Files.Tests';
const cli = 'node tools/dotnet.mjs test tests/backend/VibeMiner.Cli.Tests';
const integration = 'node tools/dotnet.mjs test tests/backend/VibeMiner.Storage.IntegrationTests';

test('a change inside layer 1 runs only Primitives, in full', () => {
  assert.deepEqual(commands(['src/backend/VibeMiner.Objects/ObjectStore.cs']), [primitives]);
});

test('a change inside layer 2 runs only layer 2', () => {
  assert.deepEqual(commands(['src/backend/VibeMiner.ObjectAccess/ObjectSource.cs']), [objectAccess]);
});

test('a layer 1 interface or fake change also runs the callers without slow tests', () => {
  const callers = [primitives, `${objectAccess} --filter Speed!=Slow`, `${builds} --filter Speed!=Slow`, `${files} --filter Speed!=Slow`, `${cli} --filter Speed!=Slow`];
  assert.deepEqual(commands([{ path: 'src/backend/VibeMiner.Objects/IObjectReader.cs', publicApi: true }]), callers);
  assert.deepEqual(commands(['tests/backend/VibeMiner.Cdn.Testing/FakeCdn.cs']), callers);
});

test('a layer 2 interface change also runs layers 3 and 4 and the CLI', () => {
  assert.deepEqual(commands([{ path: 'src/backend/VibeMiner.ObjectAccess/IObjectSource.cs', publicApi: true }]), [objectAccess, `${builds} --filter Speed!=Slow`, `${files} --filter Speed!=Slow`, `${cli} --filter Speed!=Slow`]);
});

test('a change inside layer 4 runs only layer 4', () => {
  assert.deepEqual(commands(['src/backend/VibeMiner.Files/FileSystem.cs', 'tests/backend/VibeMiner.Files.Tests/FileSystemTests.cs']), [files]);
});

test('a layer 3 interface or fake change also runs layer 4 and the CLI', () => {
  const callers = [builds, `${files} --filter Speed!=Slow`, `${cli} --filter Speed!=Slow`];
  assert.deepEqual(commands([{ path: 'src/backend/VibeMiner.Builds/IBuilds.cs', publicApi: true }]), callers);
  assert.deepEqual(commands(['tests/backend/VibeMiner.Builds.Testing/InMemoryBuilds.cs']), callers);
});

test('a layer 4 interface or fake change also runs the CLI', () => {
  const callers = [files, `${cli} --filter Speed!=Slow`];
  assert.deepEqual(commands([{ path: 'src/backend/VibeMiner.Files/IFileSystem.cs', publicApi: true }]), callers);
  assert.deepEqual(commands(['tests/backend/VibeMiner.Files.Testing/InMemoryFileSystem.cs']), callers);
});

test('a change inside the layer 7 CLI runs only its own tests', () => {
  assert.deepEqual(commands([{ path: 'src/backend/VibeMiner.Cli/VmCommands.cs', publicApi: true }, 'tests/backend/VibeMiner.Cli.Tests/CommandTests.cs']), [cli]);
});

test('the storage integration project runs on layer 1 or 2 changes once it exists', () => {
  const exists = () => true;
  assert.deepEqual(commands(['src/backend/VibeMiner.ObjectAccess/ObjectSource.cs'], exists),
    [objectAccess, `${integration} --filter Speed!=Slow`]);
  assert.deepEqual(commands(['tests/backend/VibeMiner.Storage.IntegrationTests/StorageTests.cs'], exists), [integration]);
});

test('a project file or the script itself runs everything', () => {
  assert.deepEqual(commands(['tests/backend/VibeMiner.Backend.Tests/RepositoryPaths.cs']), everything);
  assert.deepEqual(commands(['tests/backend/VibeMiner.ObjectAccess.Tests/VibeMiner.ObjectAccess.Tests.csproj']), everything);
  assert.deepEqual(commands(['tools/test-layers.mjs']), everything);
});

test('legacy backend paths run Backend.Tests', () => {
  assert.deepEqual(commands(['src/backend/VibeMiner.Host/Program.cs']), [
    'node tools/dotnet.mjs test tests/backend/VibeMiner.Backend.Tests --filter Speed!=Slow',
  ]);
});

test('desktop runs the desktop suite and notes e2e', () => {
  const plan = planFor([{ path: 'apps/desktop/src/renderer/App.tsx', publicApi: false }]);
  assert.deepEqual(plan.runs.map(run => run.args.join(' ')), ['run test:desktop']);
  assert.match(plan.notes.join('\n'), /e2e specs that cover this area/);
});

test('contracts and tools run the contract suites', () => {
  assert.deepEqual(commands(['contracts/engine.schema.json', 'tools/e2e-lock.mjs']),
    ['npm run test:contracts', 'npm run contracts:check']);
});

test('docs only runs nothing', () => {
  assert.deepEqual(commands(['docs/ARCHITECTURE.md', 'README.md', 'src/backend/README.md']), []);
});

test('an unknown path runs everything and says why', () => {
  const plan = planFor([{ path: 'mystery/thing.bin', publicApi: false }]);
  assert.deepEqual(plan.runs.map(run => [run.command, ...run.args].join(' ')), everything);
  assert.match(plan.reasons.join('\n'), /unknown path/);
});
