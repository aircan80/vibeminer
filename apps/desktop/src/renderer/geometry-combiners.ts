// The one shader the textured appearance uses (contracts/geometry-vma1-v2.md#renderer). The C# engine admitted each
// combiner and decoded every image; this only samples the RGBA8 slots and applies the documented formula.
import { DoubleSide, FrontSide, ShaderMaterial, type DataTexture } from 'three';
import type { AppearanceDraw } from './geometry-vma';

// Stage coordinates: 0 UV0, 1 UV1 (no v2 combiner uses it), 2 sphere env map from the view-space normal. The env
// mapping is the usual GL sphere map; a zero normal or a degenerate reflection samples the map centre.
const vertexShader = /* glsl */ `
uniform int vmaCoord0;
uniform int vmaCoord1;
varying vec2 vmaSt0;
varying vec2 vmaSt1;
vec2 vmaEnv(vec3 viewPosition, vec3 viewNormal) {
  float size = length(viewNormal);
  if (size == 0.0) return vec2(0.5);
  vec3 r = reflect(normalize(viewPosition), viewNormal / size);
  float m = 2.0 * sqrt(r.x * r.x + r.y * r.y + (r.z + 1.0) * (r.z + 1.0));
  return m > 0.0 ? vec2(r.x / m + 0.5, r.y / m + 0.5) : vec2(0.5);
}
void main() {
  vec4 viewPosition = modelViewMatrix * vec4(position, 1.0);
  vec2 env = vmaCoord0 == 2 || vmaCoord1 == 2 ? vmaEnv(viewPosition.xyz, normalMatrix * normal) : vec2(0.0);
  vmaSt0 = vmaCoord0 == 2 ? env : uv;
  vmaSt1 = vmaCoord1 == 2 ? env : uv;
  gl_Position = projectionMatrix * viewPosition;
}`;

// Combiners run on the stored texel values (no sRGB decode or encode), as the formulas are written. The preview is
// unlit and colour/opacity tracks are not evaluated, so the mesh colour is 1 and an added term is added directly.
// 1 Opaque: T0. 2 Mod: T0, alpha T0.a. 3 Opaque_Mod2xNA_Alpha: mix(T0*T1*2, T0, T0.a).
// 4 Opaque_AddAlpha_Alpha: T0 + T1*T1.a*(1-T0.a). 5 Opaque_Alpha: mix(T0, T1, T1.a).
const fragmentShader = /* glsl */ `
uniform sampler2D vmaMap0;
uniform sampler2D vmaMap1;
uniform int vmaCombiner;
uniform bool vmaAlphaKey;
uniform float vmaAlphaReference;
varying vec2 vmaSt0;
varying vec2 vmaSt1;
void main() {
  vec4 t0 = texture2D(vmaMap0, vmaSt0);
  vec3 color = t0.rgb;
  float alpha = 1.0;
  if (vmaCombiner == 2) alpha = t0.a;
  else if (vmaCombiner >= 3) {
    vec4 t1 = texture2D(vmaMap1, vmaSt1);
    if (vmaCombiner == 3) color = mix(t0.rgb * t1.rgb * 2.0, t0.rgb, t0.a);
    else if (vmaCombiner == 4) color = t0.rgb + t1.rgb * t1.a * (1.0 - t0.a);
    else color = mix(t0.rgb, t1.rgb, t1.a);
  }
  if (vmaAlphaKey && alpha < vmaAlphaReference) discard;
  gl_FragColor = vec4(color, 1.0);
}`;

/** One material per drawn batch. A one-stage draw binds its slot to both samplers; the second is never read. */
export function appearanceMaterial(draw: AppearanceDraw, textures: readonly DataTexture[], alphaReference: number): ShaderMaterial {
  return new ShaderMaterial({
    vertexShader, fragmentShader, transparent: false, toneMapped: false, side: draw.twoSided ? DoubleSide : FrontSide,
    depthTest: draw.depthTest, depthWrite: draw.depthWrite,
    uniforms: {
      vmaMap0: { value: textures[draw.slot] }, vmaMap1: { value: textures[draw.second?.slot ?? draw.slot] },
      vmaCoord0: { value: draw.coordinate }, vmaCoord1: { value: draw.second?.coordinate ?? draw.coordinate },
      vmaCombiner: { value: draw.combiner }, vmaAlphaKey: { value: draw.alphaKey }, vmaAlphaReference: { value: alphaReference / 255 },
    },
  });
}
