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
const limit = JSON.parse(await readFile(new URL('../../contracts/examples/creature-geometry-limit.json', import.meta.url)));
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

test('the engine names a problem headline, and a too-large one only for a limit that will not pass on a retry (#116)', () => {
  const result = check('CreatureGeometryResult');
  assert.ok(result(limit), JSON.stringify(result.errors));
  assert.equal(limit.problem.headline, 'buildListTooLarge');
  const { headline, ...withoutHeadline } = limit.problem;
  assert.equal(result({ ...limit, problem: withoutHeadline }), false);
  assert.equal(result({ ...limit, problem: { ...limit.problem, headline: 'tooLarge' } }), false);
  for (const big of ['modelTooLarge', 'buildListTooLarge']) {
    assert.ok(result({ ...limit, problem: { ...limit.problem, headline: big } }));
    assert.equal(result({ ...limit, problem: { ...limit.problem, headline: big, retryable: true } }), false, big);
    assert.equal(result({ ...limit, state: 'corrupt', problem: { ...limit.problem, headline: big } }), false, big);
  }
  for (const small of ['notDrawn', 'encrypted', 'unsupported']) {
    assert.ok(result({ ...limit, state: 'corrupt', problem: { ...limit.problem, headline: small, retryable: true } }), small);
  }
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
  for (const patch of [{ byteLength: 10485761 }, { headerBytes: 128 }, { alphaReference: 127 }, { textureCount: 33 }, { artifactRevision: 'model-geometry-vmg1-v1' }, { extra: 1 },
    { formatVersion: 1 }, { artifactRevision: 'model-appearance-vma1-v1' }, { materialPolicy: 'm2-single-texture-opaque-alphakey-v1' }, { normals: undefined }])
    assert.equal(check('CreatureAppearanceManifest')({ ...appearance.manifest, ...patch }), false, JSON.stringify(patch));
  // VMA1 v2: the normals region is empty when no stage samples an env map; every other region is not.
  assert.ok(check('CreatureAppearanceManifest')({ ...appearance.manifest, normals: { offset: 208, byteLength: 0 } }));
  assert.equal(check('CreatureAppearanceManifest')({ ...appearance.manifest, uvs: { offset: 256, byteLength: 0 } }), false);
  const { blendedBatches, ...withoutBlended } = appearance.effects;
  assert.equal(blendedBatches, 1); assert.equal(check('CreatureGeometryEffects')(withoutBlended), false);
  const refused = { ...appearance, state: 'combinerUnsupported', textures: [], manifest: null,
    problem: { code: 'CREATURE_APPEARANCE_COMBINER_UNSUPPORTED', message: 'The batch combines its textures with Combiners_Mod_Mod with Diffuse_T1_T2 (shader 0x4011, 2 textures), which is not drawn.', retryable: false, batchOrdinal: 0, textureOrdinal: null } };
  assert.ok(check('CreatureGeometryAppearance')(refused), JSON.stringify(check('CreatureGeometryAppearance').errors));
  assert.equal(check('CreatureGeometryTexture')({ ...appearance.textures[0], type: 14 }), false);
  assert.equal(check('CreatureGeometryTextureImage')({ ...appearance.textures[0].image, width: 1025 }), false);
});

test('a texture row names the D095 standard-resolution ROOT variant it used, or null', () => {
  const texture = textured.appearance.textures[0];
  const variant = { policy: 'root-texture-standard-resolution-v1', contentKey: texture.source.contentKey, contentFlags: 0x2080000,
    highResolutionContentKey: 'd'.repeat(32), highResolutionContentFlags: 0x2080001 };
  assert.ok(check('CreatureGeometryTexture')({ ...texture, variant }), JSON.stringify(check('CreatureGeometryTexture').errors));
  assert.ok(check('CreatureGeometryTexture')({ ...texture, variant: null }));
  const { variant: _omitted, ...withoutVariant } = texture;
  assert.equal(check('CreatureGeometryTexture')(withoutVariant), false);
  for (const patch of [{ policy: 'all-root-variants-consistent-asset-v1' }, { contentFlags: -1 }, { highResolutionContentFlags: 2 ** 32 }, { contentKey: 'A'.repeat(32) }, { extra: 1 }])
    assert.equal(check('CreatureGeometryTextureVariant')({ ...variant, ...patch }), false, JSON.stringify(patch));
});
