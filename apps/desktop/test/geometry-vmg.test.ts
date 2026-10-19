// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { createHash } from 'node:crypto';
import { validateGeometry } from '../src/renderer/geometry-vmg';
import { geometryCSharpGolden, geometryFixture } from './geometry-fixtures';

function work(extra: Partial<Parameters<typeof validateGeometry>[2]> = {}) {
  return { signal: new AbortController().signal, isCurrent: () => true, yieldTurn: async () => {}, ...extra };
}

describe('VMG1 renderer admission', () => {
  it('matches the independently fixed C# byte/digest golden and native section evidence', async () => {
    const fixture = geometryCSharpGolden();
    expect(fixture.buffer.byteLength).toBe(320);
    expect(createHash('sha256').update(new Uint8Array(fixture.buffer)).digest('hex')).toBe(fixture.manifest.sha256);
    const length = Buffer.alloc(8); length.writeBigUInt64LE(320n);
    expect(createHash('sha256').update(Buffer.concat([Buffer.from('vibeminer-model-geometry-artifact-v1\0', 'ascii'),
      Buffer.from(fixture.manifest.bindingSha256, 'hex'), Buffer.from(fixture.manifest.sha256, 'hex'), length])).digest('hex')).toBe(fixture.manifest.artifactId);
    const result = await validateGeometry(fixture.buffer, fixture.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    expect(Object.is(result.value.positions[0], -0)).toBe(true);
    expect([...result.value.lineIndices]).toEqual([2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2]);
    expect(result.value.manifest.bounds).toEqual({ min: [-6, -4, 0], max: [3, 7, 8] });
    expect(result.value.sectionPage(0, 16).map(item => [item.id, item.lineStart, item.lineCount])).toEqual([[7, 0, 6], [9, 6, 6], [0, 12, 0]]);
  });
  it('accepts independently authored header/pairs and exposes the same backing bytes', async () => {
    const fixture = geometryFixture(); const original = [...new Uint8Array(fixture.buffer)];
    const result = await validateGeometry(fixture.buffer, fixture.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    expect(result.value.positions.buffer).toBe(fixture.buffer);
    expect(result.value.positions.byteOffset).toBe(128);
    expect(result.value.lineIndices.buffer).toBe(fixture.buffer);
    expect([...result.value.lineIndices]).toEqual([0, 1, 1, 2, 2, 0]);
    expect([...result.value.positions]).toEqual([1, 2, 3, 4, 2, 3, 1, 6, 8]);
    expect([...new Uint8Array(fixture.buffer)]).toEqual(original);
    expect(result.value.sectionPage(0, 16)).toEqual([{ ordinal: 0, id: 5, level: 0, vertexStart: 0,
      vertexCount: 3, triangleStart: 0, triangleCount: 3, lineStart: 0, lineCount: 6 }]);
    expect(() => result.value.sectionPage(0, 17)).toThrow();
  });

  it('preserves duplicate overlapping empty degenerate sections and original negative zero', async () => {
    const fixture = geometryFixture({ positions: [-0, 0, 0, 4, 0, 0, 0, 3, 0, 1e30, 1e30, 1e30],
      triangles: [[0, 0, 1], [1, 2, 0]], sections: [{ start: 0, count: 3 }, { start: 0, count: 6 }, { start: 6, count: 0 }] });
    const result = await validateGeometry(fixture.buffer, fixture.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    expect(Object.is(result.value.positions[0], -0)).toBe(true);
    expect(result.value.manifest.bounds.max).toEqual([4, 3, 0]);
    expect(result.value.sectionPage(2, 1)[0].lineCount).toBe(0);
  });

  it('rejects a nonempty triangle section with no lookup vertices but preserves an actually empty section', async () => {
    const f = geometryFixture(); f.data.setUint32(f.manifest.sections.offset + 12, 0, true);
    expect((await validateGeometry(f.buffer, f.manifest, work())).state).toBe('refused');
    const empty = geometryFixture({ sections: [{ start: 0, count: 3 }, { start: 3, count: 0, vertexCount: 0 }] });
    const result = await validateGeometry(empty.buffer, empty.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    expect(result.value.sectionPage(1, 1)[0]).toMatchObject({ vertexCount: 0, triangleCount: 0, lineCount: 0 });
  });

  it.each([0, 4, 6, 8, 12, 16, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 100, 112, 116, 120, 124])(
    'rejects same-size header corruption at %i', async offset => {
      const fixture = geometryFixture(); fixture.data.setUint8(offset, fixture.data.getUint8(offset) ^ 1);
      expect(await validateGeometry(fixture.buffer, fixture.manifest, work())).toMatchObject({ state: 'refused', code: 'invalidArtifact' });
    });

  it.each(['artifactId', 'bindingSha256', 'sha256', 'byteLength', 'formatVersion', 'headerBytes', 'scope', 'coordinatePolicy',
    'vertexCount', 'vertexLookupCount', 'sourceTriangleIndexCount', 'sectionIndexCount', 'lineIndexCount', 'sectionCount',
    'modelFileDataId', 'skinFileDataId', 'mainSkinOrdinal', 'modelReaderRevision', 'skinReaderRevision',
    'interpretationPolicy', 'artifactRevision', 'edgePolicy'])('rejects malformed manifest %s', async key => {
      const fixture = geometryFixture(); const manifest = { ...fixture.manifest, [key]: 'invalid' };
      expect((await validateGeometry(fixture.buffer, manifest, work())).state).toBe('refused');
    });

  it.each([0, 4, 6, 8, 12, 16, 20, 24, 28])('rejects directory corruption at %i', async relative => {
    const fixture = geometryFixture(); const offset = fixture.manifest.sections.offset + relative;
    // ID itself is arbitrary source evidence; Level is constrained by effective source start.
    if (relative === 4) fixture.data.setUint32(offset + 4, 65536, true);
    else fixture.data.setUint32(offset, 0xffffffff, true);
    expect((await validateGeometry(fixture.buffer, fixture.manifest, work())).state).toBe('refused');
  });

  it('refuses malformed positions, indices, pair topology and inaccurate tight bounds', async () => {
    for (const mutate of [
      (f: ReturnType<typeof geometryFixture>) => f.data.setFloat32(128, NaN, true),
      (f: ReturnType<typeof geometryFixture>) => f.data.setUint32(f.manifest.lineIndices.offset, 3, true),
      (f: ReturnType<typeof geometryFixture>) => f.data.setUint32(f.manifest.lineIndices.offset + 8, 2, true),
      (f: ReturnType<typeof geometryFixture>) => { f.data.setFloat32(88, 0, true); f.manifest.bounds.min[0] = 0; },
      (f: ReturnType<typeof geometryFixture>) => { f.data.setFloat32(88, -0, true); f.manifest.bounds.min[0] = -0; },
    ]) {
      const fixture = geometryFixture(); mutate(fixture);
      expect((await validateGeometry(fixture.buffer, fixture.manifest, work())).state).toBe('refused');
    }
  });

  it('refuses trailing, truncated, wrong-object, zero-triangle and oversized bodies', async () => {
    const f = geometryFixture();
    for (const value of [new ArrayBuffer(0), f.buffer.slice(0, -1), new ArrayBuffer(f.buffer.byteLength + 1),
      new ArrayBuffer(2097153), new Uint8Array(f.buffer), new SharedArrayBuffer(128)]) {
      expect((await validateGeometry(value as ArrayBuffer, f.manifest, work())).state).toBe('refused');
    }
    const empty = geometryFixture({ sections: [{ start: 0, count: 0 }] });
    expect((await validateGeometry(empty.buffer, empty.manifest, work())).state).toBe('refused');
  });

  it('rejects hostile keys/accessors without invoking getter and snapshots across an await', async () => {
    const f = geometryFixture(); let called = false;
    const hostile = { ...f.manifest }; Object.defineProperty(hostile, 'bounds', { get() { called = true; throw Error('getter'); }, enumerable: true });
    expect((await validateGeometry(f.buffer, hostile, work())).state).toBe('refused'); expect(called).toBe(false);
    expect((await validateGeometry(f.buffer, { ...f.manifest, extra: true }, work())).state).toBe('refused');
    expect((await validateGeometry(f.buffer, { ...f.manifest, positions: { ...f.manifest.positions, extra: true } }, work())).state).toBe('refused');
    expect((await validateGeometry(f.buffer, { ...f.manifest, evaluations: { ...f.manifest.evaluations, material: 'ready' } }, work())).state).toBe('refused');
    expect((await validateGeometry(f.buffer, { ...f.manifest, bounds: { ...f.manifest.bounds, min: [1, 2] } }, work())).state).toBe('refused');
    expect((await validateGeometry(f.buffer, Object.assign(Object.create({ inherited: true }), f.manifest), work())).state).toBe('refused');
    const large = geometryFixture({ triangles: Array.from({ length: 3000 }, () => [0, 1, 2] as const) });
    const original = large.manifest.modelFileDataId;
    const snapshot = await validateGeometry(large.buffer, large.manifest, work({ yieldTurn: async () => {
      large.manifest.modelFileDataId = 7; large.manifest.bounds.max[0] = 999; large.manifest.positions.offset = 999;
    } }));
    expect(snapshot.state).toBe('valid'); if (snapshot.state !== 'valid') throw Error('refused');
    expect(snapshot.value.manifest.modelFileDataId).toBe(original);
    expect(snapshot.value.manifest.bounds.max[0]).toBe(4); expect(snapshot.value.positions.byteOffset).toBe(128);
  });

  it('refuses detached/resizable inputs and a non-little-endian host without constructing position views', async () => {
    const f = geometryFixture();
    const detached = f.buffer.slice(0); structuredClone(detached, { transfer: [detached] });
    const resizable = Reflect.construct(ArrayBuffer, [f.buffer.byteLength, { maxByteLength: f.buffer.byteLength * 2 }]) as ArrayBuffer;
    for (const buffer of [detached, resizable]) expect((await validateGeometry(buffer, f.manifest, work())).state).toBe('refused');
    const positions = vi.spyOn(globalThis, 'Float32Array').mockImplementation(() => { throw Error('position view before admission'); });
    try {
      f.data.setUint32(124, 1, true);
      expect((await validateGeometry(f.buffer, f.manifest, work())).state).toBe('refused');
      expect(positions).not.toHaveBeenCalled();
    } finally { positions.mockRestore(); }
    const Original = Uint32Array;
    const endian = vi.spyOn(globalThis, 'Uint32Array').mockImplementation(function (...args: ConstructorParameters<typeof Uint32Array>) {
      const probe = new Original(...args);
      if (probe.length === 1 && probe[0] === 0x01020304) probe[0] = 0x04030201;
      return probe;
    });
    try { expect(await validateGeometry(geometryFixture().buffer, geometryFixture().manifest, work())).toMatchObject({ code: 'unsupportedEndian' }); }
    finally { endian.mockRestore(); }
  });

  it('admits native maximum aggregate counts and high-word starts while preserving section provenance', async () => {
    const triangles = Array.from({ length: 43690 }, () => [0, 1, 2] as const);
    const positions = new Array<number>(65536 * 3).fill(0); positions[3] = 1; positions[7] = 1;
    const sections = [{ start: 0, count: 65535, vertexCount: 65535 }, { start: 65535, count: 65535, vertexCount: 65535 },
      ...Array.from({ length: 254 }, () => ({ start: 131070, count: 0, vertexCount: 0 }))];
    const f = geometryFixture({ positions, triangles, sections, lookupCount: 65536 });
    expect(f.buffer.byteLength).toBe(1843312);
    const result = await validateGeometry(f.buffer, f.manifest, work());
    expect(result.state).toBe('valid'); if (result.state !== 'valid') throw Error('refused');
    expect(result.value.sectionPage(2, 1)[0]).toMatchObject({ level: 1, triangleStart: 131070, triangleCount: 0, lineStart: 262140 });
    expect(result.value.sectionPage(256, 16)).toEqual([]);
    for (const offset of [-1, 0.5, 257, NaN]) expect(() => result.value.sectionPage(offset, 1)).toThrow(RangeError);
  });

  it('keeps unreferenced nonfinite positions invalid and accepts finite subnormal source bits independently of camera', async () => {
    const f = geometryFixture({ positions: [0, 0, 0, 1, 0, 0, 0, 1, 0, Infinity, 0, 0] });
    expect((await validateGeometry(f.buffer, f.manifest, work())).state).toBe('refused');
    f.data.setFloat32(128 + 9 * 4, 2 ** -149, true);
    expect((await validateGeometry(f.buffer, f.manifest, work())).state).toBe('valid');
  });

  it('default work turns allow a queued task before completion and observes pre-cancel', async () => {
    const f = geometryFixture({ triangles: Array.from({ length: 3000 }, () => [0, 1, 2] as const) });
    let task = false; const queued = new Promise<void>(resolve => setTimeout(() => { task = true; resolve(); }, 0));
    const result = await validateGeometry(f.buffer, f.manifest, { signal: new AbortController().signal, isCurrent: () => true });
    expect(result.state).toBe('valid'); expect(task).toBe(true); await queued;
    const cancelled = new AbortController(); cancelled.abort();
    expect(await validateGeometry(f.buffer, f.manifest, work({ signal: cancelled.signal }))).toMatchObject({ code: 'cancelled' });
  });

  it('charges repeated position reads, yields real turns and fences cancellation/currentness after a turn', async () => {
    const triangles = Array.from({ length: 21845 }, () => [0, 1, 2] as const);
    const f = geometryFixture({ triangles }); let turns = 0;
    const result = await validateGeometry(f.buffer, f.manifest, work({ yieldTurn: async () => { turns++; } }));
    expect(result.state).toBe('valid'); expect(turns).toBeGreaterThanOrEqual(20);
    const controller = new AbortController();
    expect(await validateGeometry(f.buffer, f.manifest, work({ signal: controller.signal,
      yieldTurn: async () => { controller.abort(); } }))).toMatchObject({ code: 'cancelled' });
    let current = true;
    expect(await validateGeometry(f.buffer, f.manifest, work({ isCurrent: () => current,
      yieldTurn: async () => { current = false; } }))).toMatchObject({ code: 'cancelled' });
    await expect(validateGeometry(f.buffer, f.manifest, work({ yieldTurn: async () => { throw Error('scheduler'); } }))).rejects.toThrow('scheduler');
  });
});
