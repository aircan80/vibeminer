// Authored VMG1 app-format fixtures. No game data and no production writer/validator.
export function geometryFixture(options: {
  positions?: readonly number[]; triangles?: readonly (readonly [number, number, number])[];
  sections?: readonly { start: number; count: number; vertexStart?: number; vertexCount?: number; id?: number; level?: number }[];
  lookupCount?: number;
} = {}) {
  const positions = options.positions ?? [1, 2, 3, 4, 2, 3, 1, 6, 8];
  const triangles = options.triangles ?? [[0, 1, 2]];
  const sections = options.sections ?? [{ start: 0, count: triangles.length * 3 }];
  const vertexCount = positions.length / 3;
  const lines: number[] = [];
  for (const section of sections) for (let ordinal = section.start; ordinal < section.start + section.count; ordinal += 3) {
    const [a, b, c] = triangles[ordinal / 3]; lines.push(a, b, b, c, c, a);
  }
  const positionBytes = positions.length * 4;
  const lineOffset = 128 + positionBytes;
  const sectionOffset = lineOffset + lines.length * 4;
  const buffer = new ArrayBuffer(sectionOffset + sections.length * 32);
  const data = new DataView(buffer);
  const u32 = (offset: number, value: number) => data.setUint32(offset, value, true);
  [0x56, 0x4d, 0x47, 0x31].forEach((value, index) => data.setUint8(index, value));
  data.setUint16(4, 1, true); data.setUint16(6, 128, true);
  u32(8, buffer.byteLength); u32(12, 1);
  for (let index = 0; index < 32; index++) data.setUint8(16 + index, 0x11);
  u32(48, vertexCount); u32(52, lines.length); u32(56, sections.length);
  u32(60, options.lookupCount ?? vertexCount); u32(64, 128); u32(68, positionBytes);
  u32(72, lineOffset); u32(76, lines.length * 4); u32(80, sectionOffset); u32(84, sections.length * 32);
  const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
  for (const vertex of lines) for (let axis = 0; axis < 3; axis++) {
    min[axis] = Math.min(min[axis], positions[vertex * 3 + axis]);
    max[axis] = Math.max(max[axis], positions[vertex * 3 + axis]);
  }
  for (let axis = 0; axis < 3; axis++) {
    min[axis] = Math.fround(min[axis]) || 0; max[axis] = Math.fround(max[axis]) || 0;
    data.setFloat32(88 + axis * 4, min[axis], true); data.setFloat32(100 + axis * 4, max[axis], true);
  }
  u32(112, lines.length / 2); u32(116, 1); u32(120, triangles.length * 3); u32(124, 0);
  positions.forEach((value, index) => data.setFloat32(128 + index * 4, value, true));
  lines.forEach((value, index) => u32(lineOffset + index * 4, value));
  let firstLine = 0;
  sections.forEach((section, index) => {
    const offset = sectionOffset + index * 32;
    u32(offset, index); data.setUint16(offset + 4, section.id ?? index + 5, true);
    data.setUint16(offset + 6, section.level ?? Math.floor(section.start / 65536), true);
    u32(offset + 8, section.vertexStart ?? 0); u32(offset + 12, section.vertexCount ?? vertexCount);
    u32(offset + 16, section.start); u32(offset + 20, section.count);
    u32(offset + 24, firstLine); u32(offset + 28, section.count * 2); firstLine += section.count * 2;
  });
  const manifest = {
    artifactId: '22'.repeat(32), bindingSha256: '11'.repeat(32), sha256: '33'.repeat(32), byteLength: buffer.byteLength,
    formatVersion: 1, headerBytes: 128, scope: 'unfiltered-declared-sections',
    coordinatePolicy: 'native-m2-position-normal-uv-winding-v1',
    evaluations: { material: 'notEvaluated', pose: 'notEvaluated', visibility: 'notEvaluated', appearance: 'notEvaluated' },
    vertexCount, vertexLookupCount: options.lookupCount ?? vertexCount, sourceTriangleIndexCount: triangles.length * 3,
    sectionIndexCount: lines.length / 2, lineIndexCount: lines.length, sectionCount: sections.length,
    positions: { offset: 128, byteLength: positionBytes }, lineIndices: { offset: lineOffset, byteLength: lines.length * 4 },
    sections: { offset: sectionOffset, byteLength: sections.length * 32 }, bounds: { min, max },
    modelFileDataId: 1472486, skinFileDataId: 1472488, mainSkinOrdinal: 0,
    modelReaderRevision: 'm2-static-native-v1', skinReaderRevision: 'm2-skin-geometry-v1',
    interpretationPolicy: 'skin-unfiltered-source-geometry-v1', artifactRevision: 'model-geometry-vmg1-v1',
    edgePolicy: 'all-sections-triangle-edges-v1',
  };
  return { buffer, data, manifest };
}

// Exact authored cross-language golden from Protocols/ModelGeometryArtifactTests.cs.
// These are synthetic app-format bytes and fixed expected hashes, not downloaded game content.
export function geometryCSharpGolden() {
  const fixture = geometryFixture({ positions: [-0, 2, 0, 3, -4, 5, -6, 7, 8, 100, 100, 100], lookupCount: 3,
    triangles: [[2, 0, 1], [1, 1, 1]], sections: [
      { start: 0, count: 3, vertexCount: 3, id: 7 }, { start: 0, count: 3, vertexCount: 3, id: 9 },
      { start: 3, count: 0, vertexCount: 3, id: 0 },
    ] });
  fixture.manifest.bindingSha256 = '26d091dddb2aae81502176c74454a201d3c55e29150aa0f3d3311d19a08cc956';
  fixture.manifest.sha256 = '7c6551ca0fe903121e78b848a221f72f504048b9a5ba606576f07d8b897f1119';
  fixture.manifest.artifactId = '04d7492b51e1ba4ec7d84829ff5bc79a1bd54cdc9c38dc5eddb6682eb143dc45';
  fixture.manifest.modelFileDataId = 800; fixture.manifest.skinFileDataId = 900;
  for (let index = 0; index < 32; index++) fixture.data.setUint8(16 + index, parseInt(fixture.manifest.bindingSha256.substring(index * 2, index * 2 + 2), 16));
  return fixture;
}
