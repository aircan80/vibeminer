// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { validateAppearance } from '../src/renderer/geometry-vma';
import { appearanceFixture } from './geometry-fixtures';

const work = (extra: Partial<Parameters<typeof validateAppearance>[2]> = {}) => ({ signal: new AbortController().signal, isCurrent: () => true, yieldTurn: async () => {}, ...extra });

describe('VMA1 renderer admission', () => {
  it('exposes positions, UVs, indices, draws and texels as views over the one received buffer', async () => {
    const fixture = appearanceFixture(); const result = await validateAppearance(fixture.buffer, fixture.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    const value = result.value;
    expect(value.kind).toBe('appearance'); expect(value.positions.buffer).toBe(fixture.buffer); expect(value.textures[0].texels.buffer).toBe(fixture.buffer);
    expect([...value.uvs]).toEqual([0, 1, 1, 1, 0, 0]); expect([...value.indices]).toEqual([0, 1, 2]);
    expect(value.draws).toEqual([{ ordinal: 0, batch: 0, section: 0, indexStart: 0, indexCount: 3, slot: 0, textureOrdinal: 0, alphaKey: true, twoSided: true, depthTest: true, depthWrite: true }]);
    expect(value.textures[0]).toMatchObject({ fileDataId: 779, width: 2, height: 2, wrapU: true, wrapV: false });
    expect([...value.textures[0].texels]).toEqual([...Array(16).keys()]);
  });
  it('refuses every structural violation before anything can be uploaded', async () => {
    const cases: [string, (f: ReturnType<typeof appearanceFixture>) => void][] = [
      ['magic', f => f.data.setUint8(3, 0x32)],
      ['binding', f => f.data.setUint8(20, 0)],
      ['index range', f => f.data.setUint32(f.offsets.indexOffset, 3, true)],
      ['bounds', f => f.data.setFloat32(124, 2, true)],
      ['uv', f => f.data.setFloat32(f.offsets.uvOffset, Number.NaN, true)],
      ['draw partition', f => f.data.setUint32(f.offsets.drawOffset + 16, 6, true)],
      ['draw flag', f => f.data.setUint32(f.offsets.drawOffset + 28, 16, true)],
      ['slot', f => f.data.setUint32(f.offsets.drawOffset + 20, 1, true)],
      ['texel offset', f => f.data.setUint32(f.offsets.textureOffset + 16, f.offsets.texelOffset + 4, true)],
      ['texture side', f => f.data.setUint32(f.offsets.textureOffset + 8, 1025, true)],
      ['reserved', f => f.data.setUint32(150, 1, true)],
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
