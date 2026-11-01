// Runs the tests that a branch's changes need, by code layer (docs/TESTING.md, "Layer-scoped unit tests"; D137).
//
//   node tools/test-layers.mjs [base] [--list]     base defaults to main; --list prints the plan only
//
// Changes are `git diff --no-renames <merge-base of base and HEAD>` (committed and uncommitted, both sides of a
// rename) plus untracked files.
// - A change inside one layer runs only that layer's test project. Callers use its fake and can't see it.
// - A change to a layer's interface or fake runs that layer's project and the test projects of every layer
//   above it that has one. The contract suites live in each layer's own project, so that covers them.
//   The interface rule is deliberately simple: a changed file under a layer's src project whose diff adds,
//   removes or changes a line containing "public " counts. It over-triggers; that is the safe side.
//   A change under a layer's *.Testing fake project always counts.
// - Callers skip slow tests (--filter "Speed!=Slow"); a layer 1 change runs Primitives.Tests in full.
// - The cross-layer storage integration project runs whenever layer 1 or 2 changed.
// - The Formats comparison project runs whenever layer 5 or the legacy Formats.Wow reader changed.
// - Shared foundations (toolchain, packages, project files, CI, this script) run everything.
// - Legacy projects map to VibeMiner.Backend.Tests. Desktop and contract paths run their npm suites;
//   docs and Markdown run nothing; anything unknown runs everything.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));

// One row per code layer. Add a row when a layer gets its projects.
export const layers = [
  {
    layer: 1, name: 'Primitives',
    src: ['src/backend/VibeMiner.Objects/', 'src/backend/VibeMiner.Cdn/', 'src/backend/VibeMiner.Generated/'],
    fakes: ['tests/backend/VibeMiner.Cdn.Testing/', 'tests/backend/VibeMiner.Generated.Testing/'],
    paths: ['tests/backend/VibeMiner.Primitives.Tests/', 'tests/backend/VibeMiner.Objects.CrashWriter/'],
    testProject: 'tests/backend/VibeMiner.Primitives.Tests',
  },
  {
    layer: 2, name: 'Object access',
    src: ['src/backend/VibeMiner.ObjectAccess/'],
    fakes: ['tests/backend/VibeMiner.ObjectAccess.Testing/'],
    paths: ['tests/backend/VibeMiner.ObjectAccess.Tests/'],
    testProject: 'tests/backend/VibeMiner.ObjectAccess.Tests',
  },
  {
    layer: 3, name: 'Builds',
    src: ['src/backend/VibeMiner.Builds/'],
    fakes: ['tests/backend/VibeMiner.Builds.Testing/'],
    paths: ['tests/backend/VibeMiner.Builds.Tests/'],
    testProject: 'tests/backend/VibeMiner.Builds.Tests',
  },
  {
    layer: 4, name: 'Files',
    src: ['src/backend/VibeMiner.Files/'],
    fakes: ['tests/backend/VibeMiner.Files.Testing/'],
    paths: ['tests/backend/VibeMiner.Files.Tests/'],
    testProject: 'tests/backend/VibeMiner.Files.Tests',
  },
  {
    layer: 5, name: 'Formats',
    src: ['src/backend/VibeMiner.Formats/'],
    fakes: ['tests/backend/VibeMiner.Formats.Testing/'],
    paths: ['tests/backend/VibeMiner.Formats.Tests/'],
    testProject: 'tests/backend/VibeMiner.Formats.Tests',
  },
  {
    layer: 7, name: 'Developer CLI',
    src: ['src/backend/VibeMiner.Cli/'],
    fakes: [],
    paths: ['tests/backend/VibeMiner.Cli.Tests/'],
    testProject: 'tests/backend/VibeMiner.Cli.Tests',
  },
];

// Cross-layer integration tests over the real layers 1 and 2. Runs only once the project exists.
const integration = { project: 'tests/backend/VibeMiner.Storage.IntegrationTests', layers: [1, 2] };

// Temporary cross-check of the layer 5 reader against the legacy Formats.Wow reader (#177), until slice 9 deletes
// both. Runs only once the project exists, whenever layer 5, the legacy reader or the project itself changes.
const comparison = { project: 'tests/backend/VibeMiner.Formats.Comparison', layers: [5], legacy: ['src/backend/VibeMiner.Formats.Wow/'] };

const legacy = {
  paths: ['src/backend/VibeMiner.Domain/', 'src/backend/VibeMiner.Protocols.Tact/', 'src/backend/VibeMiner.Formats.Wow/',
    'src/backend/VibeMiner.Application/', 'src/backend/VibeMiner.Infrastructure/', 'src/backend/VibeMiner.Host/',
    'tests/backend/VibeMiner.Backend.Tests/', 'tests/backend/unit/', 'tests/backend/integration/', 'tests/backend/architecture/'],
  testProject: 'tests/backend/VibeMiner.Backend.Tests',
};

const foundations = [
  /(^|\/)Directory\.(Build|Packages)\.[^/]+$/, /(^|\/)global\.json$/, /(^|\/)NuGet\.config$/i, /\.slnx$/,
  /(^|\/)packages\.lock\.json$/, /\.csproj$/, /^tools\/(dotnet\.mjs|dev-env\.sh|test-layers\.mjs)$/,
  /^package(-lock)?\.json$/, /^\.github\/workflows\//,
  // Compiled into Backend.Tests and linked into Primitives.Tests: a shared test helper.
  /^tests\/backend\/VibeMiner\.Backend\.Tests\/RepositoryPaths\.cs$/,
];

const under = (file, prefixes) => prefixes.some(prefix => prefix.endsWith('/') ? file.startsWith(prefix) : file === prefix);
const npm = script => ({ command: 'npm', args: ['run', script] });
const dotnet = (project, filter) => ({ command: 'node', args: ['tools/dotnet.mjs', 'test', project, ...(filter ? ['--filter', filter] : [])] });

// changes: [{ path, publicApi }] → { reasons, runs: [{ command, args }], notes }
// exists(project) says whether a test project is there yet; tests pass their own.
export function planFor(changes, { exists = project => existsSync(path.join(root, project)) } = {}) {
  const reasons = [];
  const notes = new Set();
  const own = new Set();      // layers whose own project runs
  const callers = new Set();  // layers whose interface or fake changed, so the layers above run too
  let everything = false;
  let integrationTests = false;
  let comparisonTests = false;
  let comparisonLegacy = false;
  let legacyTests = false;
  let desktop = false;
  let contracts = false;
  const full = why => { everything = true; reasons.push(why); };

  for (const { path: file, publicApi } of changes) {
    if (under(file, comparison.legacy)) comparisonLegacy = true;
    const layer = layers.find(row => [row.src, row.fakes, row.paths].some(prefixes => under(file, prefixes)));
    if (foundations.some(pattern => pattern.test(file))) full(`${file}: shared foundation`);
    else if (layer) {
      own.add(layer.layer);
      if (under(file, layer.fakes)) { callers.add(layer.layer); reasons.push(`${file}: layer ${layer.layer} fake`); }
      else if (publicApi && under(file, layer.src)) { callers.add(layer.layer); reasons.push(`${file}: layer ${layer.layer} interface`); }
      else reasons.push(`${file}: inside layer ${layer.layer} (${layer.name})`);
    } else if (file.startsWith(`${integration.project}/`)) { integrationTests = true; reasons.push(`${file}: storage integration tests`); }
    else if (file.startsWith(`${comparison.project}/`)) { comparisonTests = true; reasons.push(`${file}: formats comparison tests`); }
    else if (under(file, legacy.paths)) { legacyTests = true; reasons.push(`${file}: legacy backend`); }
    else if (file.startsWith('apps/desktop/')) {
      desktop = true; notes.add('e2e: run the e2e specs that cover this area'); reasons.push(`${file}: desktop`);
    } else if (file.startsWith('tests/desktop/')) {
      notes.add('e2e: run the e2e specs that cover this area'); reasons.push(`${file}: desktop e2e`);
    } else if (file.startsWith('contracts/') || file.startsWith('tests/contracts/') || file.startsWith('tools/')) {
      contracts = true; reasons.push(`${file}: contracts and tools`);
    } else if (file.startsWith('docs/') || file.endsWith('.md')) reasons.push(`${file}: docs, nothing to run`);
    else full(`${file}: unknown path, running everything to be safe`);
  }

  if (everything) {
    return { reasons, notes: [...notes],
      runs: [npm('contracts:check'), npm('test:backend'), npm('test:desktop'), npm('test:contracts')] };
  }
  const lowestCaller = Math.min(...callers);
  const runs = [];
  for (const row of layers) {
    if (!row.testProject || !(own.has(row.layer) || row.layer > lowestCaller)) continue;
    runs.push(dotnet(row.testProject, own.has(row.layer) ? null : 'Speed!=Slow'));
  }
  if ((integrationTests || integration.layers.some(layer => own.has(layer))) && exists(integration.project)) {
    runs.push(dotnet(integration.project, integrationTests ? null : 'Speed!=Slow'));
  }
  if ((comparisonTests || comparisonLegacy || comparison.layers.some(layer => own.has(layer))) && exists(comparison.project)) {
    runs.push(dotnet(comparison.project, comparisonTests ? null : 'Speed!=Slow'));
  }
  if (legacyTests) runs.push(dotnet(legacy.testProject, 'Speed!=Slow'));
  if (desktop) runs.push(npm('test:desktop'));
  if (contracts) runs.push(npm('test:contracts'), npm('contracts:check'));
  return { reasons, runs, notes: [...notes] };
}

const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' });
const lines = text => text.split('\n').filter(Boolean);

function changesSince(base) {
  const mergeBase = git('merge-base', base, 'HEAD').trim();
  const tracked = lines(git('diff', '--name-only', '--no-renames', mergeBase));
  const untracked = lines(git('ls-files', '--others', '--exclude-standard'));
  const src = layers.flatMap(row => row.src);
  const publicLine = /^[+-](?![+-]).*\bpublic /m;
  return [...new Set([...tracked, ...untracked])].sort().map(file => {
    if (!under(file, src)) return { path: file, publicApi: false };
    const diff = untracked.includes(file)
      ? readFileSync(path.join(root, file), 'utf8').replace(/^/gm, '+')
      : git('diff', '-U0', '--no-renames', mergeBase, '--', file);
    return { path: file, publicApi: publicLine.test(diff) };
  });
}

function main(argv) {
  const list = argv.includes('--list');
  const base = argv.find(arg => !arg.startsWith('--')) ?? 'main';
  const plan = planFor(changesSince(base));
  console.log(`Changes since ${base}:`);
  for (const reason of plan.reasons) console.log(`  ${reason}`);
  if (plan.reasons.length === 0) console.log('  none');
  console.log('Plan:');
  for (const run of plan.runs) console.log(`  ${run.command} ${run.args.join(' ')}`);
  if (plan.runs.length === 0) console.log('  nothing to run');
  for (const note of plan.notes) console.log(`Note: ${note}`);
  if (list) return 0;
  const failed = [];
  for (const run of plan.runs) {
    console.log(`\n> ${run.command} ${run.args.join(' ')}`);
    const result = spawnSync(run.command, run.args, { cwd: root, stdio: 'inherit', shell: process.platform === 'win32' });
    if (result.status !== 0) failed.push(`${run.command} ${run.args.join(' ')}`);
  }
  if (failed.length) console.error(`\nFailed:\n  ${failed.join('\n  ')}`);
  return failed.length ? 1 : 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = main(process.argv.slice(2));
