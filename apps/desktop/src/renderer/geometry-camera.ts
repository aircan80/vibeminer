// geometry-camera-fit-v1. Viewing convention only; no source repair/rebase/pose inference.
type Vector = readonly [number, number, number];
type Bounds = Readonly<{ min: readonly number[]; max: readonly number[] }>;
export interface GeometryCamera {
  readonly target: Vector; readonly radius: number; readonly yaw: number; readonly elevation: number;
  readonly distance: number; readonly minimumDistance: number; readonly maximumDistance: number;
  readonly near: number; readonly far: number; readonly position: Vector;
  readonly projection: readonly number[]; readonly view: readonly number[];
}
export type GeometryCameraAction = Readonly<{ kind: 'rotate'; yaw: number; elevation: number } | { kind: 'zoom'; ticks: number } | { kind: 'reset' }>;
export type CameraResult = { readonly state: 'ready'; readonly value: GeometryCamera }
  | { readonly state: 'unsupported'; readonly code: 'cameraFitUnsupported' };
const unsupported: CameraResult = Object.freeze({ state: 'unsupported', code: 'cameraFitUnsupported' });
const smallestNormal = 2 ** -126;
const elevationLimit = 80 * Math.PI / 180;
const fieldOfView = Math.PI / 4, aspect = 4 / 3;
const initialYaw = Math.PI / 4, initialElevation = Math.PI / 6;
const provenance = new WeakMap<GeometryCamera, Readonly<{ corners: readonly Vector[]; target: Vector; radius: number; min: number; max: number }>>();
const normal = (value: number) => Number.isFinite(value) && Number.isFinite(Math.fround(value)) && (value === 0 || Math.abs(Math.fround(value)) >= smallestNormal);
const positive = (value: number) => value > 0 && normal(value);
function vector(values: readonly number[]): Vector { return Object.freeze([values[0], values[1], values[2]]) as Vector; }
function matrix(values: readonly number[]): readonly number[] { return Object.freeze(values.map(Math.fround)); }
function roundedProduct(m: readonly number[], v: readonly number[]): number[] {
  const result: number[] = [];
  for (let row = 0; row < 4; row++) {
    let sum = Math.fround(m[row] * v[0]);
    for (let col = 1; col < 4; col++) sum = Math.fround(sum + Math.fround(m[col * 4 + row] * v[col]));
    result.push(sum);
  }
  return result;
}
function derive(proof: NonNullable<ReturnType<typeof provenance.get>>, yaw: number, elevation: number, distance: number): CameraResult {
  const { target, radius, min, max, corners } = proof;
  const z = [Math.cos(elevation) * Math.sin(yaw), Math.sin(elevation), Math.cos(elevation) * Math.cos(yaw)];
  const position = vector(target.map((value, axis) => value + distance * z[axis]));
  // Orthonormal look-at basis with no roll; no near-pole singularity under ±80°.
  const horizontal = Math.hypot(z[0], z[2]);
  if (!positive(horizontal) || !position.every(normal)) return unsupported;
  const x = [z[2] / horizontal, 0, -z[0] / horizontal];
  const y = [z[1] * x[2], z[2] * x[0] - z[0] * x[2], -z[1] * x[0]];
  const dot = (a: readonly number[], b: readonly number[]) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const near = 0.5 * (distance - radius), far = 1.5 * (distance + radius);
  if (!positive(near) || !positive(far) || near >= far) return unsupported;
  const f = 1 / Math.tan(fieldOfView / 2), range = near - far;
  const rawProjection = [f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / range, -1, 0, 0, 2 * far * near / range, 0];
  const rawView = [x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, position), -dot(y, position), -dot(z, position), 1];
  if (![...rawProjection, ...rawView].every(normal)) return unsupported;
  const projection = matrix(rawProjection), view = matrix(rawView);
  for (const corner of corners) {
    const camera = roundedProduct(view, [...corner.map(Math.fround), 1]);
    const clip = roundedProduct(projection, camera);
    if (!camera.every(Number.isFinite) || !clip.every(Number.isFinite) || !positive(clip[3])) return unsupported;
    const ndc = clip.slice(0, 3).map(value => Math.fround(value / clip[3]));
    if (!ndc.every(Number.isFinite) || Math.abs(ndc[0]) > 0.999 || Math.abs(ndc[1]) > 0.999 || ndc[2] <= -1 || ndc[2] >= 1) return unsupported;
  }
  const value = Object.freeze({ target, radius, yaw, elevation, distance, minimumDistance: min, maximumDistance: max, near, far, position, projection, view });
  provenance.set(value, proof); return { state: 'ready', value };
}

export function fitGeometryCamera(bounds: Bounds): CameraResult {
  if (!bounds || !Array.isArray(bounds.min) || !Array.isArray(bounds.max) || bounds.min.length !== 3 || bounds.max.length !== 3) return unsupported;
  for (let axis = 0; axis < 3; axis++) if (typeof bounds.min[axis] !== 'number' || typeof bounds.max[axis] !== 'number'
    || !normal(bounds.min[axis]) || !normal(bounds.max[axis]) || bounds.min[axis] > bounds.max[axis]) return unsupported;
  const min = [bounds.min[0], bounds.min[2], -bounds.max[1]], max = [bounds.max[0], bounds.max[2], -bounds.min[1]];
  const target = vector(min.map((value, axis) => value + (max[axis] - value) / 2));
  const half = min.map((value, axis) => (max[axis] - value) / 2);
  const radius = Math.hypot(...half);
  if (!positive(radius) || !target.every(normal) || Math.max(...target.map(Math.abs)) / radius > 4096) return unsupported;
  const angle = Math.min(fieldOfView / 2, Math.atan(Math.tan(fieldOfView / 2) * aspect));
  const minimum = 1.10 * radius / Math.sin(angle), maximum = minimum * 8;
  if (!positive(minimum) || !positive(maximum)) return unsupported;
  const corners: Vector[] = [];
  for (let bits = 0; bits < 8; bits++) corners.push(vector([0, 1, 2].map(axis => bits & (1 << axis) ? max[axis] : min[axis])));
  return derive(Object.freeze({ corners: Object.freeze(corners), target, radius, min: minimum, max: maximum }), initialYaw, initialElevation, minimum * 1.5);
}

export function moveGeometryCamera(camera: GeometryCamera, action: GeometryCameraAction): CameraResult {
  const proof = provenance.get(camera); if (!proof || !action || typeof action !== 'object') return unsupported;
  let { yaw, elevation, distance } = camera;
  if (action.kind === 'reset') { yaw = initialYaw; elevation = initialElevation; distance = proof.min * 1.5; }
  else if (action.kind === 'rotate') {
    if (!Number.isFinite(action.yaw) || !Number.isFinite(action.elevation) || Math.abs(action.yaw) > Math.PI || Math.abs(action.elevation) > Math.PI) return unsupported;
    yaw = ((yaw + action.yaw + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI;
    elevation = Math.max(-elevationLimit, Math.min(elevationLimit, elevation + action.elevation));
  } else if (action.kind === 'zoom') {
    if (!Number.isInteger(action.ticks) || action.ticks < -8 || action.ticks > 8) return unsupported;
    distance = Math.max(proof.min, Math.min(proof.max, distance * 1.1 ** action.ticks));
  } else return unsupported;
  return derive(proof, yaw, elevation, distance);
}
