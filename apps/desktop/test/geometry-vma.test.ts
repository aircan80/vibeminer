// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { validateAppearance } from '../src/renderer/geometry-vma';
import { appearanceFixture } from './geometry-fixtures';

const work = (extra: Partial<Parameters<typeof validateAppearance>[2]> = {}) => ({ signal: new AbortController().signal, isCurrent: () => true, yieldTurn: async () => {}, ...extra });

describe('VMA1 v2 renderer admission', () => {
  it('exposes positions, UVs, indices, draws and texels as views over the one received buffer', async () => {
    const fixture = appearanceFixture(); const result = await validateAppearance(fixture.buffer, fixture.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    const value = result.value;
    expect(value.kind).toBe('appearance'); expect(value.positions.buffer).toBe(fixture.buffer); expect(value.textures[0].texels.buffer).toBe(fixture.buffer);
    expect([...value.uvs]).toEqual([0, 1, 1, 1, 0, 0]); expect([...value.indices]).toEqual([0, 1, 2]);
    expect(value.normals).toBeNull();
    expect(value.draws).toEqual([{ ordinal: 0, batch: 0, section: 0, indexStart: 0, indexCount: 3, combiner: 2, slot: 0, textureOrdinal: 0, coordinate: 0, second: null,
      alphaKey: true, twoSided: true, depthTest: true, depthWrite: true }]);
    expect(value.textures[0]).toMatchObject({ fileDataId: 779, width: 2, height: 2, wrapU: true, wrapV: false });
    expect([...value.textures[0].texels]).toEqual([...Array(16).keys()]);
  });
  it('exposes normals and both stages for an env-map combiner', async () => {
    const fixture = appearanceFixture({ env: true }); const result = await validateAppearance(fixture.buffer, fixture.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    expect(result.value.normals?.buffer).toBe(fixture.buffer); expect([...result.value.normals!]).toEqual([0, 0, 1, 0, 0, 1, 0, 0, 1]);
    expect(result.value.draws[0]).toMatchObject({ combiner: 4, slot: 0, coordinate: 0, second: { slot: 1, textureOrdinal: 1, coordinate: 2 }, alphaKey: false });
    expect(result.value.textures.map(t => t.fileDataId)).toEqual([779, 780]);
  });
  it('refuses a draw that disagrees with its combiner or the normals region', async () => {
    const cases: [string, (f: ReturnType<typeof appearanceFixture>) => void][] = [
      ['unknown combiner', f => f.data.setUint32(f.offsets.drawOffset + 20, 6, true)],
      ['second stage coordinate', f => f.data.setUint8(f.offsets.drawOffset + 41, 0)],
      ['second slot', f => f.data.setUint32(f.offsets.drawOffset + 32, 2, true)],
      ['alpha key without texture alpha', f => f.data.setUint32(f.offsets.drawOffset + 44, 1, true)],
      ['one-stage combiner with a second stage', f => f.data.setUint32(f.offsets.drawOffset + 20, 1, true)],
      ['normal', f => f.data.setFloat32(f.offsets.normalOffset, Number.POSITIVE_INFINITY, true)],
    ];
    for (const [name, mutate] of cases) {
      const fixture = appearanceFixture({ env: true }); mutate(fixture);
      expect((await validateAppearance(fixture.buffer, fixture.manifest, work())).state, name).toBe('refused');
    }
    // Env stages need the normals region and a region without env stages must be empty: swap the combiner to Opaque_Alpha (UV0 only).
    const fixture = appearanceFixture({ env: true }); fixture.data.setUint32(fixture.offsets.drawOffset + 20, 5, true); fixture.data.setUint8(fixture.offsets.drawOffset + 41, 0);
    expect((await validateAppearance(fixture.buffer, fixture.manifest, work())).state).toBe('refused');
  });
  it('refuses every structural violation before anything can be uploaded', async () => {
    const cases: [string, (f: ReturnType<typeof appearanceFixture>) => void][] = [
      ['magic', f => f.data.setUint8(3, 0x32)],
      ['binding', f => f.data.setUint8(20, 0)],
      ['index range', f => f.data.setUint32(f.offsets.indexOffset, 3, true)],
      ['bounds', f => f.data.setFloat32(132, 2, true)],
      ['format version', f => f.data.setUint16(4, 1, true)],
      ['uv', f => f.data.setFloat32(f.offsets.uvOffset, Number.NaN, true)],
      ['draw partition', f => f.data.setUint32(f.offsets.drawOffset + 16, 6, true)],
      ['draw flag', f => f.data.setUint32(f.offsets.drawOffset + 44, 16, true)],
      ['slot', f => f.data.setUint32(f.offsets.drawOffset + 24, 1, true)],
      ['one-stage second slot', f => f.data.setUint32(f.offsets.drawOffset + 32, 0, true)],
      ['texel offset', f => f.data.setUint32(f.offsets.textureOffset + 16, f.offsets.texelOffset + 4, true)],
      ['texture side', f => f.data.setUint32(f.offsets.textureOffset + 8, 1025, true)],
      ['reserved', f => f.data.setUint32(156, 1, true)],
    ];
    for (const [name, mutate] of cases) {
      const fixture = appearanceFixture(); mutate(fixture);
      expect((await validateAppearance(fixture.buffer, fixture.manifest, work())).state, name).toBe('refused');
    }
    const fixture = appearanceFixture();
    expect(await validateAppearance(fixture.buffer, { ...fixture.manifest, byteLength: fixture.manifest.byteLength + 4 }, work())).toEqual({ state: 'refused', code: 'invalidArtifact' });
    expect(await validateAppearance(fixture.buffer, { ...fixture.manifest, extra: true }, work())).toEqual({ state: 'refused', code: 'invalidArtifact' });
    expect(await validateAppearance(fixture.buffer, fixture.manifest, work({ isCurrent: () => false }))).toEqual({ state: 'refused', code: 'cancelled' });
  });
});
