// VMG1 is our app framing. Source authorization and full SHA verification belong to engine/main.
type Region = Readonly<{ offset: number; byteLength: number }>;
type Bounds = Readonly<{ min: readonly number[]; max: readonly number[] }>;
// Narrow local structural input until the transport-owned generated manifest is available.
interface ReadonlyManifest {
  readonly artifactId: string; readonly bindingSha256: string; readonly sha256: string; readonly byteLength: number;
  readonly formatVersion: number; readonly headerBytes: number; readonly scope: string; readonly coordinatePolicy: string;
  readonly evaluations: Readonly<{ material: string; pose: string; visibility: string; appearance: string }>;
  readonly vertexCount: number; readonly vertexLookupCount: number; readonly sourceTriangleIndexCount: number;
  readonly sectionIndexCount: number; readonly lineIndexCount: number; readonly sectionCount: number;
  readonly positions: Region; readonly lineIndices: Region; readonly sections: Region; readonly bounds: Bounds;
  readonly modelFileDataId: number; readonly skinFileDataId: number; readonly mainSkinOrdinal: number;
  readonly modelReaderRevision: string; readonly skinReaderRevision: string; readonly interpretationPolicy: string;
  readonly artifactRevision: string; readonly edgePolicy: string;
}
export interface GeometrySection {
  readonly ordinal: number; readonly id: number; readonly level: number; readonly vertexStart: number;
  readonly vertexCount: number; readonly triangleStart: number; readonly triangleCount: number;
  readonly lineStart: number; readonly lineCount: number;
}
export interface GeometryValidationWork {
  readonly signal: AbortSignal; readonly isCurrent: () => boolean; readonly yieldTurn?: () => Promise<void>;
}
export type GeometryValidationResult =
  | { readonly state: 'valid'; readonly value: ValidatedGeometry }
  | { readonly state: 'refused'; readonly code: 'invalidArtifact' | 'unsupportedEndian' | 'cancelled' };
interface ValidatedGeometry {
  readonly manifest: ReadonlyManifest; readonly positions: Float32Array; readonly lineIndices: Uint32Array;
  sectionPage(offset: number, limit: number): readonly GeometrySection[];
}

class InvalidArtifact extends Error {}
class Cancelled extends Error {}
function requireValid(value: unknown): asserts value { if (!value) throw new InvalidArtifact(); }
function record(value: unknown, keys: readonly string[]): Record<string, unknown> {
  requireValid(typeof value === 'object' && value !== null);
  const prototype = Object.getPrototypeOf(value);
  requireValid(prototype === Object.prototype || prototype === null);
  const own = Reflect.ownKeys(value); requireValid(own.length === keys.length && own.every(key => typeof key === 'string' && keys.includes(key)));
  const result: Record<string, unknown> = Object.create(null);
  for (const key of keys) {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    requireValid(descriptor && 'value' in descriptor); result[key] = descriptor.value;
  }
  return result;
}
function integer(value: unknown, min: number, max: number): number {
  requireValid(typeof value === 'number' && Number.isSafeInteger(value) && !Object.is(value, -0) && value >= min && value <= max);
  return value;
}
function boundVector(value: unknown): readonly number[] {
  requireValid(Array.isArray(value) && Object.getPrototypeOf(value) === Array.prototype && value.length === 3);
  requireValid(Reflect.ownKeys(value).length === 4);
  const result: number[] = [];
  for (let index = 0; index < 3; index++) {
    const descriptor = Object.getOwnPropertyDescriptor(value, String(index));
    requireValid(descriptor && 'value' in descriptor);
    const component = descriptor.value;
    requireValid(typeof component === 'number' && Number.isFinite(component) && Math.fround(component) === component && !Object.is(component, -0));
    result.push(component);
  }
  return Object.freeze(result);
}
function region(value: unknown): Region {
  const item = record(value, ['offset', 'byteLength']);
  return Object.freeze({ offset: integer(item.offset, 128, 2097152), byteLength: integer(item.byteLength, 1, 2097152) });
}
const manifestKeys = ['artifactId', 'bindingSha256', 'sha256', 'byteLength', 'formatVersion', 'headerBytes', 'scope',
  'coordinatePolicy', 'evaluations', 'vertexCount', 'vertexLookupCount', 'sourceTriangleIndexCount', 'sectionIndexCount',
  'lineIndexCount', 'sectionCount', 'positions', 'lineIndices', 'sections', 'bounds', 'modelFileDataId', 'skinFileDataId',
  'mainSkinOrdinal', 'modelReaderRevision', 'skinReaderRevision', 'interpretationPolicy', 'artifactRevision', 'edgePolicy'] as const;
const policies = {
  scope: 'unfiltered-declared-sections', coordinatePolicy: 'native-m2-position-normal-uv-winding-v1',
  modelReaderRevision: 'm2-static-native-v1', skinReaderRevision: 'm2-skin-geometry-v1',
  interpretationPolicy: 'skin-unfiltered-source-geometry-v1', artifactRevision: 'model-geometry-vmg1-v1',
  edgePolicy: 'all-sections-triangle-edges-v1',
} as const;
function snapshotManifest(value: unknown): ReadonlyManifest {
  const item = record(value, manifestKeys);
  for (const key of ['artifactId', 'bindingSha256', 'sha256']) requireValid(typeof item[key] === 'string' && /^[a-f0-9]{64}$/.test(item[key]));
  for (const [key, expected] of Object.entries(policies)) requireValid(item[key] === expected);
  const evaluations = record(item.evaluations, ['material', 'pose', 'visibility', 'appearance']);
  for (const value of Object.values(evaluations)) requireValid(value === 'notEvaluated');
  const bounds = record(item.bounds, ['min', 'max']); const min = boundVector(bounds.min), max = boundVector(bounds.max);
  for (let axis = 0; axis < 3; axis++) requireValid(min[axis] <= max[axis]);
  const result = {
    ...item, formatVersion: integer(item.formatVersion, 1, 1), headerBytes: integer(item.headerBytes, 128, 128),
    byteLength: integer(item.byteLength, 128, 2097152), vertexCount: integer(item.vertexCount, 1, 65536),
    vertexLookupCount: integer(item.vertexLookupCount, 1, 65536), sectionCount: integer(item.sectionCount, 1, 256),
    sourceTriangleIndexCount: integer(item.sourceTriangleIndexCount, 3, 131070),
    sectionIndexCount: integer(item.sectionIndexCount, 3, 131070), lineIndexCount: integer(item.lineIndexCount, 6, 262140),
    modelFileDataId: integer(item.modelFileDataId, 1, 0xffffffff), skinFileDataId: integer(item.skinFileDataId, 1, 0xffffffff),
    mainSkinOrdinal: integer(item.mainSkinOrdinal, 0, 0), positions: region(item.positions), lineIndices: region(item.lineIndices),
    sections: region(item.sections), bounds: Object.freeze({ min, max }), evaluations: Object.freeze({ ...evaluations }),
  };
  // Every field is independently checked above; this assertion never admits a caller object.
  return Object.freeze(result) as unknown as ReadonlyManifest;
}
const byteLengthGetter = Object.getOwnPropertyDescriptor(ArrayBuffer.prototype, 'byteLength')!.get!;
const resizableGetter = Object.getOwnPropertyDescriptor(ArrayBuffer.prototype, 'resizable')?.get;
function bodyLength(buffer: ArrayBuffer): number {
  try {
    const length = byteLengthGetter.call(buffer) as number;
    requireValid(!resizableGetter?.call(buffer)); return length;
  } catch (error) { if (error instanceof InvalidArtifact) throw error; throw new InvalidArtifact(); }
}
// Only four scratch bytes; no input conversion. Big-endian hosts cannot mount native direct views.
function littleEndian(): boolean {
  const probe = new Uint32Array([0x01020304]); return new Uint8Array(probe.buffer)[0] === 4;
}
function section(data: DataView, offset: number): GeometrySection {
  return Object.freeze({ ordinal: data.getUint32(offset, true), id: data.getUint16(offset + 4, true), level: data.getUint16(offset + 6, true),
    vertexStart: data.getUint32(offset + 8, true), vertexCount: data.getUint32(offset + 12, true),
    triangleStart: data.getUint32(offset + 16, true), triangleCount: data.getUint32(offset + 20, true),
    lineStart: data.getUint32(offset + 24, true), lineCount: data.getUint32(offset + 28, true) });
}

export async function validateGeometry(buffer: ArrayBuffer, manifest: unknown, work: GeometryValidationWork): Promise<GeometryValidationResult> {
  const current = () => { if (work.signal.aborted || !work.isCurrent()) throw new Cancelled(); };
  try {
    current(); const length = bodyLength(buffer); requireValid(length >= 128 && length <= 2097152);
    if (!littleEndian()) return { state: 'refused', code: 'unsupportedEndian' };
    const owned = snapshotManifest(manifest); requireValid(length === owned.byteLength);
    const data = new DataView(buffer); const u32 = (offset: number) => data.getUint32(offset, true);
    requireValid(u32(0) === 0x31474d56 && data.getUint16(4, true) === 1 && data.getUint16(6, true) === 128);
    requireValid(u32(8) === length && u32(12) === 1 && u32(116) === 1 && u32(124) === 0);
    for (let index = 0; index < 32; index++) requireValid(data.getUint8(16 + index) === parseInt(owned.bindingSha256.substring(index * 2, index * 2 + 2), 16));
    const v = owned.vertexCount, k = owned.vertexLookupCount, g = owned.sourceTriangleIndexCount;
    const q = owned.sectionIndexCount, l = owned.lineIndexCount, s = owned.sectionCount;
    requireValid(g % 3 === 0 && q % 3 === 0 && l % 6 === 0 && l === 2 * q);
    requireValid(u32(48) === v && u32(52) === l && u32(56) === s && u32(60) === k && u32(112) === q && u32(120) === g);
    for (const [header, region, offset, bytes] of [
      [64, owned.positions, 128, 12 * v], [72, owned.lineIndices, 128 + 12 * v, 4 * l],
      [80, owned.sections, 128 + 12 * v + 4 * l, 32 * s],
    ] as const) requireValid(region.offset === offset && region.byteLength === bytes && offset % 4 === 0 && u32(header) === offset && u32(header + 4) === bytes);
    requireValid(128 + 12 * v + 4 * l + 32 * s === length);
    for (let axis = 0; axis < 3; axis++) for (const [offset, bounds] of [[88, owned.bounds.min], [100, owned.bounds.max]] as const) {
      const value = data.getFloat32(offset + 4 * axis, true); requireValid(!Object.is(value, -0) && value === bounds[axis]);
    }
    let debit = 128, items = 0;
    const turn = async () => {
      await (work.yieldTurn ? work.yieldTurn() : new Promise<void>(resolve => setTimeout(resolve, 0)));
      current(); requireValid(bodyLength(buffer) === length); debit = 0; items = 0;
    };
    for (let index = 0; index < v; index++) {
      if (debit + 12 > 65536 || items >= 4096) await turn();
      current(); const offset = 128 + index * 12;
      for (let axis = 0; axis < 3; axis++) requireValid(Number.isFinite(data.getFloat32(offset + axis * 4, true)));
      debit += 12; items++;
    }
    const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
    for (let ordinal = 0; ordinal < l; ordinal += 6) {
      // Six index reads and three referenced XYZ reads: all repeated lookups are charged.
      if (debit + 60 > 65536 || items >= 4096) await turn();
      current(); const offset = owned.lineIndices.offset + ordinal * 4;
      const a = u32(offset), b = u32(offset + 4), c = u32(offset + 12);
      requireValid(a < v && b < v && c < v && u32(offset + 8) === b && u32(offset + 16) === c && u32(offset + 20) === a);
      for (const vertex of [a, b, c]) for (let axis = 0; axis < 3; axis++) {
        const value = data.getFloat32(128 + vertex * 12 + axis * 4, true);
        min[axis] = Math.min(min[axis], value); max[axis] = Math.max(max[axis], value);
      }
      debit += 60; items++;
    }
    for (let axis = 0; axis < 3; axis++) requireValid(min[axis] === owned.bounds.min[axis] && max[axis] === owned.bounds.max[axis]);
    let cumulative = 0;
    for (let index = 0; index < s; index++) {
      if (debit + 32 > 65536 || items >= 4096) await turn(); current();
      const item = section(data, owned.sections.offset + index * 32);
      requireValid(item.ordinal === index && item.vertexStart <= 65535 && item.vertexCount <= 65535 && item.triangleCount <= 65535);
      requireValid(item.vertexStart + item.vertexCount <= k && item.triangleStart % 3 === 0 && item.triangleCount % 3 === 0);
      requireValid(item.triangleStart + item.triangleCount <= g && Math.floor(item.triangleStart / 65536) === item.level);
      requireValid(item.triangleCount === 0 || item.vertexCount > 0);
      requireValid(item.lineStart === cumulative && item.lineCount === 2 * item.triangleCount);
      cumulative += item.lineCount; requireValid(cumulative <= l); debit += 32; items++;
    }
    requireValid(cumulative === l); current(); requireValid(bodyLength(buffer) === length);
    const value: ValidatedGeometry = Object.freeze({ manifest: owned,
      positions: new Float32Array(buffer, owned.positions.offset, v * 3), lineIndices: new Uint32Array(buffer, owned.lineIndices.offset, l),
      sectionPage(offset: number, limit: number) {
        if (!Number.isInteger(offset) || offset < 0 || offset > s || !Number.isInteger(limit) || limit < 1 || limit > 16) throw new RangeError('Invalid geometry section page');
        const result: GeometrySection[] = [];
        for (let index = offset; index < Math.min(s, offset + limit); index++) result.push(section(data, owned.sections.offset + index * 32));
        return Object.freeze(result);
      },
    });
    return { state: 'valid', value };
  } catch (error) {
    if (error instanceof Cancelled) return { state: 'refused', code: 'cancelled' };
    if (error instanceof InvalidArtifact) return { state: 'refused', code: 'invalidArtifact' };
    throw error;
  }
}
