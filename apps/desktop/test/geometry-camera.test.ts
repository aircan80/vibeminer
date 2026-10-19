// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { fitGeometryCamera, moveGeometryCamera } from '../src/renderer/geometry-camera';

describe('geometry-camera-fit-v1', () => {
  it('uses the proper native axis rotation and finite checked matrices for asymmetric bounds', () => {
    const result = fitGeometryCamera({ min: [1, 2, 3], max: [4, 6, 8] });
    expect(result.state).toBe('ready'); if (result.state !== 'ready') throw Error('refused');
    expect(result.value.target).toEqual([2.5, 5.5, -4]);
    expect(result.value.projection.every(Number.isFinite)).toBe(true);
    expect(result.value.view.every(Number.isFinite)).toBe(true);
    expect(result.value.near).toBeGreaterThan(0); expect(result.value.far).toBeGreaterThan(result.value.near);
    expect(moveGeometryCamera(result.value, { kind: 'reset' })).toEqual(result);
    // Independent scalar homogeneous evaluation, including the proper x,z,-y transform.
    const multiply = (m: readonly number[], p: readonly number[]) => [0, 1, 2, 3].map(row =>
      p.reduce((sum, value, col) => Math.fround(sum + Math.fround(value * m[col * 4 + row])), 0));
    for (const x of [1, 4]) for (const y of [2, 6]) for (const z of [3, 8]) {
      const eye = multiply(result.value.view, [x, z, -y, 1]);
      const clip = multiply(result.value.projection, eye);
      expect(clip[3]).toBeGreaterThan(0);
      expect(Math.abs(clip[0] / clip[3])).toBeLessThan(0.999);
      expect(Math.abs(clip[1] / clip[3])).toBeLessThan(0.999);
      expect(Math.abs(clip[2] / clip[3])).toBeLessThan(1);
    }
    const a = result.value.view;
    const determinant = a[0] * (a[5] * a[10] - a[9] * a[6]) - a[4] * (a[1] * a[10] - a[9] * a[2]) + a[8] * (a[1] * a[6] - a[5] * a[2]);
    expect(determinant).toBeCloseTo(1, 6);
  });
  it.each([
    { min: [0, 0, 0], max: [0, 0, 0] },
    { min: [1e-44, 0, 0], max: [1e-43, 0, 0] },
    { min: [3e38, 0, 0], max: [3.1e38, 1e38, 1e38] },
    { min: [1e8, 0, 0], max: [1e8 + 1, 1, 1] },
    { min: [NaN, 0, 0], max: [1, 1, 1] },
    { min: [2, 0, 0], max: [1, 1, 1] },
  ])('refuses unsafe camera without changing bounds %j', bounds => {
    const before = JSON.stringify(bounds); expect(fitGeometryCamera(bounds)).toMatchObject({ state: 'unsupported', code: 'cameraFitUnsupported' });
    expect(JSON.stringify(bounds)).toBe(before);
  });
  it.each([
    { min: [-2, 0, 0], max: [2, 0, 0] },
    { min: [-2, -3, 0], max: [2, 3, 0] },
    { min: [-1e-20, -1e-20, -1e-20], max: [1e-20, 1e-20, 1e-20] },
    { min: [-1e20, -1e20, -1e20], max: [1e20, 1e20, 1e20] },
  ])('supports conditioned planar/line and normal-scale bounds %j', bounds => {
    expect(fitGeometryCamera(bounds).state).toBe('ready');
  });
  it('bounds orbit and zoom and resets exactly without changing the original camera', () => {
    const initial = fitGeometryCamera({ min: [-2, -3, -4], max: [2, 3, 4] });
    if (initial.state !== 'ready') throw Error('refused'); let camera = initial.value;
    for (let index = 0; index < 40; index++) {
      const next = moveGeometryCamera(camera, { kind: 'rotate', yaw: Math.PI / 3, elevation: Math.PI / 3 });
      expect(next.state).toBe('ready'); if (next.state !== 'ready') throw Error('refused'); camera = next.value;
      expect(camera.elevation).toBeLessThanOrEqual(80 * Math.PI / 180);
      expect(camera.yaw).toBeGreaterThanOrEqual(-Math.PI); expect(camera.yaw).toBeLessThan(Math.PI);
    }
    for (const ticks of [-8, -8, -8, 8, 8, 8, 8, 8]) {
      const next = moveGeometryCamera(camera, { kind: 'zoom', ticks });
      expect(next.state).toBe('ready'); if (next.state !== 'ready') throw Error('refused'); camera = next.value;
      expect(camera.distance).toBeGreaterThanOrEqual(camera.minimumDistance);
      expect(camera.distance).toBeLessThanOrEqual(camera.maximumDistance);
    }
    expect(moveGeometryCamera(camera, { kind: 'reset' })).toEqual(initial);
    for (const action of [{ kind: 'zoom', ticks: Infinity }, { kind: 'zoom', ticks: 1.5 },
      { kind: 'rotate', yaw: NaN, elevation: 0 }, { kind: 'rotate', yaw: 7, elevation: 0 }]) {
      expect(moveGeometryCamera(camera, action as never).state).toBe('unsupported');
    }
    expect(initial.value.yaw).toBe(Math.PI / 4);
  });
  it('snapshots source bounds and rejects fabricated camera state', () => {
    const bounds = { min: [-1, -2, -3], max: [1, 2, 3] };
    const result = fitGeometryCamera(bounds); if (result.state !== 'ready') throw Error('refused');
    bounds.min[0] = -Infinity; bounds.max[0] = Infinity;
    expect(moveGeometryCamera(result.value, { kind: 'reset' })).toEqual(result);
    expect(moveGeometryCamera({ ...result.value }, { kind: 'reset' }).state).toBe('unsupported');
    expect(Object.isFrozen(result.value)).toBe(true); expect(Object.isFrozen(result.value.projection)).toBe(true);
  });
});
