import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv from 'ajv';

const schema = JSON.parse(await readFile(new URL('../../contracts/discovery.schema.json', import.meta.url)));
const ajv = new Ajv({ allErrors: true, strict: false }); ajv.addSchema(schema);
const check = name => ajv.getSchema(`${schema.$id}#/definitions/${name}`);
const ready = JSON.parse(await readFile(new URL('../../contracts/examples/creature-geometry-ready.json', import.meta.url)));
const lease = JSON.parse(await readFile(new URL('../../contracts/examples/creature-geometry-lease.json', import.meta.url)));
const selection = { ...ready.context, artifactId: ready.artifactId, creatureId: ready.creatureId, slot: ready.slot };

test('geometry control carries compact original selection and bounded owned manifest', () => {
  assert.ok(check('CreatureGeometryResult')(ready), JSON.stringify(check('CreatureGeometryResult').errors));
  assert.ok(check('CreatureGeometryLeaseDescriptor')(lease));
  for (const [operation, params] of [['read', selection], ['read', { ...selection, expectedGeometryArtifactId: null }],
    ['prepare', selection], ['open', { ...selection, geometryArtifactId: lease.manifest.artifactId }], ['close', { leaseId: lease.leaseId }]]) {
    assert.ok(check('EngineRequest')({ protocolVersion: 1, id: 'geometry', method: `creatures.geometry.${operation}`, params }));
  }
  for (const field of ['fileDataId', 'path', 'manifest', 'url']) {
    assert.equal(check('CreatureGeometryOpenParams')({ ...selection, geometryArtifactId: lease.manifest.artifactId, [field]: 'untrusted' }), false);
  }
  for (const patch of [{ byteLength: '320' }, { byteLength: 2097153 }, { bounds: { min: [0, 0], max: [1, 1, 1] } },
    { modelFileDataId: '800' }, { sectionCount: 257 }, { scope: 'visible-sections' }, { mainSkinOrdinal: 1 }, { extra: true }]) {
    assert.equal(check('CreatureGeometryManifest')({ ...lease.manifest, ...patch }), false, JSON.stringify(patch));
  }
});

test('nonready geometry cannot supply bytes and source receipt order remains model then skin', () => {
  const noTriangles = { ...ready, state: 'noDeclaredTriangles', manifest: null };
  assert.ok(check('CreatureGeometryResult')(noTriangles));
  assert.equal(check('CreatureGeometryResult')({ ...noTriangles, manifest: lease.manifest }), false);
  assert.equal(check('CreatureGeometryResult')({ ...ready, manifest: null }), false);
  assert.equal(check('CreatureGeometryResult')({ ...ready, sources: [...ready.sources].reverse() }), false);
  assert.equal(check('CreatureGeometryResult')({ ...ready, sources: [{ ...ready.sources[0], encodedBytes: 4194305 }, ready.sources[1]] }), false);
  assert.equal(check('CreatureGeometryLeaseDescriptor')({ ...lease, leaseId: '../path' }), false);
  assert.equal(check('CreatureGeometryCloseParams')({ leaseId: lease.leaseId, force: true }), false);
});

test('geometry progress stage and dependency have finite codes independent of human label', () => {
  const progress = { protocolVersion: 1, type: 'progress', requestId: 'geometry', sequence: 1, stage: 'Verifying native source',
    region: 'us', completed: 12, total: 24, unit: 'bytes', geometryStage: 'modelVerification', geometryDependency: 'model' };
  assert.ok(check('EngineProgress')(progress));
  for (const patch of [{ geometryStage: 'ready' }, { geometryDependency: 'texture' }]) assert.equal(check('EngineProgress')({ ...progress, ...patch }), false);
});
