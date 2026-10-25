import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv from 'ajv';

const schema = JSON.parse(await readFile(new URL('../../contracts/discovery.schema.json', import.meta.url)));
const ajv = new Ajv({ allErrors: true, strict: false }); ajv.addSchema(schema);
const check = name => ajv.getSchema(`${schema.$id}#/definitions/${name}`);
const ready = JSON.parse(await readFile(new URL('../../contracts/examples/creature-geometry-ready.json', import.meta.url)));
const lease = JSON.parse(await readFile(new URL('../../contracts/examples/creature-geometry-lease.json', import.meta.url)));
const textured = JSON.parse(await readFile(new URL('../../contracts/examples/creature-geometry-textured.json', import.meta.url)));
const appearanceLease = JSON.parse(await readFile(new URL('../../contracts/examples/creature-geometry-appearance-lease.json', import.meta.url)));
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
  const noTriangles = { ...ready, state: 'noDeclaredTriangles', manifest: null, appearance: null };
  assert.equal(check('CreatureGeometryResult')({ ...noTriangles, appearance: textured.appearance }), false);
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
  for (const patch of [{ geometryStage: 'ready' }, { geometryDependency: 'animation' }]) assert.equal(check('EngineProgress')({ ...progress, ...patch }), false);
});

test('a ready display appearance carries its VMA1 manifest and every other state carries none', () => {
  assert.ok(check('CreatureGeometryResult')(textured), JSON.stringify(check('CreatureGeometryResult').errors));
  assert.ok(check('CreatureGeometryLeaseDescriptor')(appearanceLease), JSON.stringify(check('CreatureGeometryLeaseDescriptor').errors));
  const appearance = textured.appearance;
  assert.equal(check('CreatureGeometryAppearance')({ ...appearance, problem: { code: 'X', message: 'x', retryable: false, batchOrdinal: null, textureOrdinal: null } }), false);
  assert.equal(check('CreatureGeometryAppearance')({ ...appearance, state: 'multiTexture' }), false);
  assert.equal(check('CreatureGeometryAppearance')({ ...appearance, state: 'notPrepared', manifest: null, problem: null }), false);
  assert.ok(check('CreatureGeometryAppearance')({ ...appearance, state: 'notPrepared', manifest: null, problem: null, textures: [] }));
  assert.equal(check('CreatureGeometryResult')({ ...textured, state: 'noDeclaredTriangles', manifest: null }), false);
  for (const patch of [{ byteLength: 10485761 }, { headerBytes: 128 }, { alphaReference: 127 }, { textureCount: 33 }, { artifactRevision: 'model-geometry-vmg1-v1' }, { extra: 1 }])
    assert.equal(check('CreatureAppearanceManifest')({ ...appearance.manifest, ...patch }), false, JSON.stringify(patch));
  assert.equal(check('CreatureGeometryTexture')({ ...appearance.textures[0], type: 14 }), false);
  assert.equal(check('CreatureGeometryTextureImage')({ ...appearance.textures[0].image, width: 1025 }), false);
});
