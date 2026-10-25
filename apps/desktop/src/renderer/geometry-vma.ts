// VMA1 is our app framing for a display's textured appearance (contracts/geometry-vma1-v1.md).
// Source authority and full SHA verification belong to engine/main; this scans every region before any upload.
import type { GeometryValidationWork } from './geometry-vmg';
import { validAppearanceManifest, type ReadonlyAppearanceManifest } from '../shared/creature-geometry';

export interface AppearanceDraw {
  readonly ordinal: number; readonly batch: number; readonly section: number; readonly indexStart: number; readonly indexCount: number;
  readonly slot: number; readonly textureOrdinal: number; readonly alphaKey: boolean; readonly twoSided: boolean;
  readonly depthTest: boolean; readonly depthWrite: boolean;
}
export interface AppearanceTexture {
  readonly slot: number; readonly fileDataId: number; readonly width: number; readonly height: number; readonly mipLevel: number;
  readonly wrapU: boolean; readonly wrapV: boolean; readonly texels: Uint8Array;
}
export interface ValidatedAppearance {
  readonly kind: 'appearance'; readonly manifest: ReadonlyAppearanceManifest; readonly positions: Float32Array; readonly uvs: Float32Array;
  readonly indices: Uint32Array; readonly draws: readonly AppearanceDraw[]; readonly textures: readonly AppearanceTexture[];
}
export type AppearanceValidationResult =
  | { readonly state: 'valid'; readonly value: ValidatedAppearance }
  | { readonly state: 'refused'; readonly code: 'invalidArtifact' | 'unsupportedEndian' | 'cancelled' };
export const appearanceMaximumBytes = 10 * 1024 * 1024;

class InvalidArtifact extends Error {}
class Cancelled extends Error {}
function requireValid(value: unknown): asserts value { if (!value) throw new InvalidArtifact(); }
const byteLengthGetter = Object.getOwnPropertyDescriptor(ArrayBuffer.prototype, 'byteLength')!.get!;
const resizableGetter = Object.getOwnPropertyDescriptor(ArrayBuffer.prototype, 'resizable')?.get;
function bodyLength(buffer: ArrayBuffer): number {
  try { const length = byteLengthGetter.call(buffer) as number; requireValid(!resizableGetter?.call(buffer)); return length; }
  catch (error) { if (error instanceof InvalidArtifact) throw error; throw new InvalidArtifact(); }
}
function littleEndian(): boolean { const probe = new Uint32Array([0x01020304]); return new Uint8Array(probe.buffer)[0] === 4; }
function own(manifest: unknown): ReadonlyAppearanceManifest {
  // structuredClone drops getters/prototypes; validAppearanceManifest then checks exact keys and every equation.
  let copy: unknown; try { copy = structuredClone(manifest); } catch { throw new InvalidArtifact(); }
  requireValid(validAppearanceManifest(copy)); return Object.freeze(copy) as ReadonlyAppearanceManifest;
}

export async function validateAppearance(buffer: ArrayBuffer, manifest: unknown, work: GeometryValidationWork): Promise<AppearanceValidationResult> {
  const current = () => { if (work.signal.aborted || !work.isCurrent()) throw new Cancelled(); };
  try {
    current(); const length = bodyLength(buffer); requireValid(length >= 160 && length <= appearanceMaximumBytes);
    if (!littleEndian()) return { state: 'refused', code: 'unsupportedEndian' };
    const m = own(manifest); requireValid(length === m.byteLength);
    const data = new DataView(buffer); const u32 = (offset: number) => data.getUint32(offset, true);
    requireValid(u32(0) === 0x31414d56 && data.getUint16(4, true) === 1 && data.getUint16(6, true) === 160 && u32(8) === length && u32(12) === 1);
    for (let index = 0; index < 32; index++) requireValid(data.getUint8(16 + index) === parseInt(m.bindingSha256.substring(index * 2, index * 2 + 2), 16));
    const v = m.vertexCount, n = m.indexCount, d = m.drawCount, t = m.textureCount;
    requireValid(u32(48) === v && u32(52) === n && u32(56) === d && u32(60) === t && u32(136) === 128 && u32(140) === m.displayId);
    for (const at of [144, 148, 152, 156]) requireValid(u32(at) === 0);
    for (const [header, region] of [[64, m.positions], [72, m.uvs], [80, m.indices], [88, m.draws], [96, m.textures], [104, m.texels]] as const)
      requireValid(u32(header) === region.offset && u32(header + 4) === region.byteLength && region.offset % 4 === 0);
    for (let axis = 0; axis < 3; axis++) for (const [offset, bounds] of [[112, m.bounds.min], [124, m.bounds.max]] as const) {
      const value = data.getFloat32(offset + 4 * axis, true); requireValid(!Object.is(value, -0) && value === bounds[axis]);
    }
    let debit = 160, items = 0;
    const charge = async (bytes: number) => {
      if (debit + bytes > 65536 || items >= 4096) {
        await (work.yieldTurn ? work.yieldTurn() : new Promise<void>(resolve => setTimeout(resolve, 0)));
        current(); requireValid(bodyLength(buffer) === length); debit = 0; items = 0;
      }
      debit += bytes; items++;
    };
    for (let at = m.positions.offset; at < m.uvs.offset + m.uvs.byteLength; at += 4) {
      if (((at - m.positions.offset) & 1023) === 0) await charge(1024);
      requireValid(Number.isFinite(data.getFloat32(at, true)));
    }
    const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
    for (let ordinal = 0; ordinal < n; ordinal++) {
      await charge(16); const vertex = u32(m.indices.offset + ordinal * 4); requireValid(vertex < v);
      for (let axis = 0; axis < 3; axis++) {
        const value = data.getFloat32(m.positions.offset + vertex * 12 + axis * 4, true);
        min[axis] = Math.min(min[axis], value); max[axis] = Math.max(max[axis], value);
      }
    }
    for (let axis = 0; axis < 3; axis++) requireValid((min[axis] || 0) === m.bounds.min[axis] && (max[axis] || 0) === m.bounds.max[axis]);
    const draws: AppearanceDraw[] = []; let cumulative = 0, previous = -1;
    for (let index = 0; index < d; index++) {
      await charge(32); const at = m.draws.offset + index * 32, flags = u32(at + 28);
      const draw = Object.freeze({ ordinal: u32(at), batch: u32(at + 4), section: u32(at + 8), indexStart: u32(at + 12), indexCount: u32(at + 16),
        slot: u32(at + 20), textureOrdinal: u32(at + 24), alphaKey: (flags & 1) !== 0, twoSided: (flags & 2) !== 0, depthTest: (flags & 4) === 0, depthWrite: (flags & 8) === 0 });
      requireValid(draw.ordinal === index && draw.batch > previous && draw.batch < 512 && draw.section < 256 && draw.indexStart === cumulative &&
        draw.indexCount > 0 && draw.indexCount % 3 === 0 && draw.slot < t && draw.textureOrdinal < 32 && (flags & ~15) === 0);
      previous = draw.batch; cumulative += draw.indexCount; requireValid(cumulative <= n); draws.push(draw);
    }
    requireValid(cumulative === n);
    const textures: AppearanceTexture[] = []; let texel = m.texels.offset;
    for (let index = 0; index < t; index++) {
      await charge(32); const at = m.textures.offset + index * 32, width = u32(at + 8), height = u32(at + 12), bytes = u32(at + 20), wrap = data.getUint8(at + 26);
      requireValid(u32(at) === index && u32(at + 4) > 0 && width >= 1 && width <= 1024 && height >= 1 && height <= 1024 && u32(at + 16) === texel &&
        bytes === width * height * 4 && data.getUint8(at + 24) <= 15 && data.getUint8(at + 25) >= 1 && data.getUint8(at + 25) <= 5 && (wrap & ~3) === 0 &&
        data.getUint8(at + 27) === 0 && data.getUint16(at + 28, true) >= width && data.getUint16(at + 30, true) >= height);
      textures.push(Object.freeze({ slot: index, fileDataId: u32(at + 4), width, height, mipLevel: data.getUint8(at + 24), wrapU: (wrap & 1) !== 0, wrapV: (wrap & 2) !== 0,
        texels: new Uint8Array(buffer, texel, bytes) }));
      texel += bytes;
    }
    requireValid(texel === m.texels.offset + m.texels.byteLength && texel === length); current(); requireValid(bodyLength(buffer) === length);
    return { state: 'valid', value: Object.freeze({ kind: 'appearance' as const, manifest: m,
      positions: new Float32Array(buffer, m.positions.offset, v * 3), uvs: new Float32Array(buffer, m.uvs.offset, v * 2),
      indices: new Uint32Array(buffer, m.indices.offset, n), draws: Object.freeze(draws), textures: Object.freeze(textures) }) };
  } catch (error) {
    if (error instanceof Cancelled) return { state: 'refused', code: 'cancelled' };
    if (error instanceof InvalidArtifact) return { state: 'refused', code: 'invalidArtifact' };
    throw error;
  }
}
