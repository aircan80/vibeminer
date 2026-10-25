import { BufferAttribute, BufferGeometry, ClampToEdgeWrapping, DataTexture, DoubleSide, FrontSide, LinearFilter, LinearMipmapLinearFilter,
  LineBasicMaterial, LineSegments, Matrix4, Mesh, MeshBasicMaterial, NoToneMapping, PerspectiveCamera, RepeatWrapping, RGBAFormat, Scene,
  SRGBColorSpace, UnsignedByteType, WebGLRenderer, type Camera, type Material, type Object3D, type WebGLRendererParameters } from 'three';
import type { ValidatedAppearance } from './geometry-vma';
import type { GeometryBodyRetired, GeometrySceneDisposal, GeometrySceneIdentity } from '../shared/geometry-lifecycle';
import { fitGeometryCamera, moveGeometryCamera, type GeometryCamera, type GeometryCameraAction } from './geometry-camera';
import type { GeometryValidationResult } from './geometry-vmg';
export type { GeometryBodyRetired, GeometrySceneDisposal, GeometrySceneIdentity } from '../shared/geometry-lifecycle';

type ValidatedLines = Extract<GeometryValidationResult, { state: 'valid' }>['value'];
/** Either the VMG1 line set or the display's VMA1 textured surfaces. */
type ValidatedGeometry = ValidatedLines | ValidatedAppearance;
const isAppearance = (input: ValidatedGeometry): input is ValidatedAppearance => (input as { kind?: unknown }).kind === 'appearance';
type Refusal = 'disposed' | 'busy' | 'stale' | 'invalidIdentity' | 'invalidGeometry' | 'retirementRequired'
  | 'cameraFitUnsupported' | 'webglUnsupported' | 'resourceLimit' | 'renderFailed' | 'cleanupFailed' | 'observerFailed';
export type GeometrySceneStatus = Readonly<{ code: 'drawn' | 'contextLost' | 'stale' | 'cameraFitUnsupported' | 'renderFailed' | 'cleanupFailed' }>;
type Refused = Readonly<{ state: 'refused'; code: Refusal }>;
type Disposed = Readonly<{ state: 'disposed'; notice: GeometrySceneDisposal }> | Refused;
export interface GeometrySceneOptions {
  readonly viewId: string; readonly viewGeneration: number;
  readonly isCurrent: () => boolean;
  // Trusted composition port: the caller already awaited the actual main-owned retirement result.
  readonly confirmRetirement: (identity: GeometrySceneIdentity, result: GeometryBodyRetired) => boolean;
  readonly onStatus: (status: GeometrySceneStatus) => void;
  // Backing-store pixels from geometryBackingSize; the default is the original fixed 588×441.
  readonly size?: GeometryBackingSize;
}
export type GeometryBackingSize = Readonly<{ width: number; height: number }>;
// Canvas accounting: 16 bytes per backing pixel (two colour buffers, depth, one compositor copy) within the 8 MiB canvas share.
export const geometryCanvasBytesPerPixel = 16, geometryCanvasBudgetBytes = 8 * 1024 * 1024;
const maxBackingWidth = 836, minBackingWidth = 64; // 836×627×16 = 8,386,752 bytes.
/** Displayed CSS size × devicePixelRatio, at the camera's fixed 4:3 aspect, clamped to the canvas budget. */
export function geometryBackingSize(cssWidth: number, cssHeight: number, devicePixelRatio: number): GeometryBackingSize {
  const ratio = Number.isFinite(devicePixelRatio) && devicePixelRatio > 0 ? devicePixelRatio : 1;
  const wanted = Number.isFinite(cssWidth) && Number.isFinite(cssHeight) && cssWidth > 0 && cssHeight > 0 ? Math.min(cssWidth, cssHeight * 4 / 3) * ratio : 588;
  const width = Math.max(minBackingWidth, Math.min(maxBackingWidth, Math.floor(wanted / 4) * 4));
  return Object.freeze({ width, height: width * 3 / 4 });
}
function validBackingSize(size: GeometryBackingSize): boolean {
  return Number.isSafeInteger(size.width) && Number.isSafeInteger(size.height) && size.width % 4 === 0 && size.height === size.width * 3 / 4
    && size.width >= minBackingWidth && size.width <= maxBackingWidth && size.width * size.height * geometryCanvasBytesPerPixel <= geometryCanvasBudgetBytes;
}
export interface GeometrySceneOwner {
  mount(geometry: ValidatedGeometry, identity: GeometrySceneIdentity, retired: GeometryBodyRetired): Readonly<{ state: 'mounted' }> | Refused;
  move(action: GeometryCameraAction): Readonly<{ state: 'moved' }> | Refused;
  // Changes only the backing store to follow the displayed size; the camera and scene are unchanged.
  resize?(size: GeometryBackingSize): Readonly<{ state: 'resized' }> | Refused;
  // Caller supplies its observed current grant identity, including a failed/unmounted fetch.
  disposeScene(identity: GeometrySceneIdentity|null): Disposed;
  disposeView(identity: GeometrySceneIdentity|null): Disposed;
}
export type GeometrySceneCreation = Readonly<{ state: 'available'; owner: GeometrySceneOwner }>
  | Readonly<{ state: 'refused'; code: Refusal; owner?: GeometrySceneOwner }>;

// Narrow graphics/scheduling seam for authored tests only. Production always constructs real Three.
export interface GeometryGraphics {
  setPixelRatio(ratio: number): void; setSize(width: number, height: number, updateStyle: boolean): void;
  setClearColor(color: string, alpha: number): void; getContext(): WebGL2RenderingContext|WebGLRenderingContext;
  render(scene: Scene, camera: Camera): void; dispose(): void; forceContextLoss(): void;
  readonly renderLists: { dispose(): void }; outputColorSpace: string; toneMapping: number; sortObjects: boolean;
  readonly shadowMap: { enabled: boolean };
  readonly debug: { checkShaderErrors: boolean; onShaderError: WebGLRenderer['debug']['onShaderError'] };
}
interface Frames { request(callback: FrameRequestCallback): number; cancel(id: number): void }
type Factory = (parameters: WebGLRendererParameters) => GeometryGraphics;
const defaultSize: GeometryBackingSize = Object.freeze({ width: 588, height: 441 });
const rotation = new Matrix4().set(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
const refused = (code: Refusal): Refused => ({ state: 'refused', code });
const lowerId = /^[a-f0-9]{32}$/, artifactId = /^[a-f0-9]{64}$/;
const heldCanvases = new WeakSet<HTMLCanvasElement>();
interface Mounted {
  readonly scene: Scene; readonly geometry: BufferGeometry; readonly materials: readonly Material[]; readonly textures: readonly DataTexture[];
  readonly line: Object3D; readonly camera: PerspectiveCamera;
  readonly bounds: ValidatedGeometry['manifest']['bounds']; readonly identity: GeometrySceneIdentity;
  cameraState: GeometryCamera;
}
function roundedProduct(matrix: readonly number[], point: readonly number[]): number[] {
  const result: number[] = [];
  for (let row = 0; row < 4; row++) {
    let sum = Math.fround(matrix[row] * point[0]);
    for (let column = 1; column < 4; column++) sum = Math.fround(sum + Math.fround(matrix[column * 4 + row] * point[column]));
    result.push(sum);
  }
  return result;
}
function installCamera(camera: PerspectiveCamera, state: GeometryCamera, bounds: Mounted['bounds']): boolean {
  camera.matrixAutoUpdate = false; camera.matrixWorldAutoUpdate = false;
  camera.matrixWorldInverse.fromArray(state.view); camera.matrixWorld.copy(camera.matrixWorldInverse).invert();
  camera.projectionMatrix.fromArray(state.projection); camera.projectionMatrixInverse.copy(camera.projectionMatrix).invert();
  camera.position.fromArray(state.position); camera.near = state.near; camera.far = state.far;
  // This is precisely Three's actual modelView combination, including its native-coordinate dot order.
  const combined = new Matrix4().multiplyMatrices(camera.matrixWorldInverse, rotation).elements.map(Math.fround);
  if (![...camera.matrixWorld.elements, ...camera.projectionMatrixInverse.elements, ...combined].every(Number.isFinite)) return false;
  for (let bits = 0; bits < 8; bits++) {
    const corner = [0, 1, 2].map(axis => Math.fround(bits & (1 << axis) ? bounds.max[axis] : bounds.min[axis]));
    const eye = roundedProduct(combined, [...corner, 1]); const clip = roundedProduct(state.projection, eye);
    if (!eye.every(Number.isFinite) || !clip.every(Number.isFinite) || clip[3] <= 0) return false;
    const [x, y, z] = clip.slice(0, 3).map(value => Math.fround(value / clip[3]));
    if (![x, y, z].every(Number.isFinite) || Math.abs(x) > 0.999 || Math.abs(y) > 0.999 || z <= -1 || z >= 1) return false;
  }
  return true;
}

export function createGeometryScene(canvas: HTMLCanvasElement, options: GeometrySceneOptions): GeometrySceneCreation {
  const window = canvas.ownerDocument.defaultView;
  if (!window) return refused('webglUnsupported');
  return create(canvas, options, parameters => new WebGLRenderer(parameters), {
    request: callback => window.requestAnimationFrame(callback), cancel: id => window.cancelAnimationFrame(id),
  }, () => canvas.getContext('webgl2', { alpha: false, depth: true, stencil: false, antialias: false, preserveDrawingBuffer: false }));
}
export function createGeometrySceneForTests(canvas: HTMLCanvasElement, options: GeometrySceneOptions, factory: Factory, frames: Frames): GeometrySceneCreation {
  return create(canvas, options, factory, frames);
}

function create(canvas: HTMLCanvasElement, options: GeometrySceneOptions, factory: Factory, frames: Frames,
  acquireContext?: () => WebGL2RenderingContext|null): GeometrySceneCreation {
  if (!lowerId.test(options.viewId) || !Number.isSafeInteger(options.viewGeneration) || options.viewGeneration < 1) return refused('invalidIdentity');
  const initial = options.size ?? defaultSize;
  if (!validBackingSize(initial)) return refused('resourceLimit');
  if (heldCanvases.has(canvas)) return refused('busy');
  heldCanvases.add(canvas);
  const viewId = options.viewId, viewGeneration = options.viewGeneration;
  let graphics: GeometryGraphics|null = null, constructingContext: WebGL2RenderingContext|null = null, mounted: Mounted|null = null, debt: Mounted|null = null;
  let pendingFrame: number|null = null, pointer: { id: number; x: number; y: number }|null = null;
  let disposed = false, disposing = false, retiringScene = false, mounting = false, cleanupFailed = false, observerFailed = false, shaderFailed = false;
  let consumedToken: string|null = null, lastStatus: GeometrySceneStatus['code']|null = null, size = initial;
  let controls: (() => void)[] = [], lossListener = false;
  const attempt = (action: () => void): boolean => { try { action(); return true; } catch { cleanupFailed = true; return false; } };
  const isCurrent = () => { try { return options.isCurrent(); } catch { return false; } };
  const notify = (code: GeometrySceneStatus['code']) => {
    if (observerFailed || lastStatus === code) return; lastStatus = code;
    try { options.onStatus({ code }); } catch { observerFailed = true; retireView(); }
  };
  const validIdentity = (identity: GeometrySceneIdentity|null): boolean => identity === null ||
    identity.viewId === viewId && identity.viewGeneration === viewGeneration && lowerId.test(identity.operationToken) && artifactId.test(identity.geometryArtifactId);
  const same = (left: GeometrySceneIdentity, right: GeometrySceneIdentity) => left.viewId === right.viewId && left.viewGeneration === right.viewGeneration
    && left.operationToken === right.operationToken && left.geometryArtifactId === right.geometryArtifactId;
  const snapshot = (identity: GeometrySceneIdentity): GeometrySceneIdentity => Object.freeze({ viewId: identity.viewId, viewGeneration: identity.viewGeneration,
    operationToken: identity.operationToken, geometryArtifactId: identity.geometryArtifactId });
  const notice = (identity: GeometrySceneIdentity|null, scope: GeometrySceneDisposal['scope']): Disposed => {
    if (cleanupFailed || debt) return refused('cleanupFailed');
    if (!validIdentity(identity)) return refused('invalidIdentity');
    return { state: 'disposed', notice: Object.freeze({ viewId, operationToken: identity?.operationToken ?? null, scope }) };
  };
  function endDrag() {
    const previous = pointer; pointer = null;
    if (previous) attempt(() => { if (canvas.hasPointerCapture(previous.id)) canvas.releasePointerCapture(previous.id); });
  }
  function stopFrames() { const previous = pendingFrame; pendingFrame = null; if (previous !== null) attempt(() => frames.cancel(previous)); }
  function stopControls() { endDrag(); const previous = controls; controls = []; for (const remove of previous) attempt(remove); }
  function retireScene() {
    if (retiringScene) return; retiringScene = true;
    stopFrames(); stopControls(); const previous = mounted; mounted = null;
    if (!previous) { retiringScene = false; return; }
    attempt(() => previous.scene.remove(previous.line));
    const geometryClosed = attempt(() => previous.geometry.dispose());
    const materialClosed = previous.materials.map(material => attempt(() => material.dispose())).every(Boolean);
    const texturesClosed = previous.textures.map(texture => attempt(() => texture.dispose())).every(Boolean);
    attempt(() => graphics?.renderLists.dispose());
    if (geometryClosed && materialClosed && texturesClosed && !cleanupFailed) {
      // Even an external diagnostics reference to this object cannot retain the received body through attributes or texels.
      attempt(() => previous.geometry.deleteAttribute('position')); attempt(() => previous.geometry.deleteAttribute('uv')); attempt(() => previous.geometry.setIndex(null));
      for (const texture of previous.textures) attempt(() => { texture.image = { data: null, width: 1, height: 1 } as unknown as typeof texture.image; });
    }
    if (cleanupFailed) debt = previous;
    retiringScene = false;
  }
  function retireView() {
    if (disposed || disposing) return; disposing = true; disposed = true;
    if (lossListener) { lossListener = false; attempt(() => canvas.removeEventListener('webglcontextlost', lost)); }
    retireScene();
    const previous = graphics;
    if (previous) {
      attempt(() => { previous.debug.onShaderError = null; }); attempt(() => previous.renderLists.dispose());
      attempt(() => previous.dispose()); attempt(() => previous.forceContextLoss());
      if (!cleanupFailed) graphics = null;
    }
    if (constructingContext) {
      const context = constructingContext;
      attempt(() => {
        if (!context.isContextLost()) {
          const extension = context.getExtension('WEBGL_lose_context');
          if (!extension) throw new Error('Geometry context retirement unavailable');
          extension.loseContext();
        }
      });
      if (!cleanupFailed) constructingContext = null;
    }
    attempt(() => { canvas.width = 0; canvas.height = 0; }); disposing = false;
    if (!cleanupFailed && !debt) heldCanvases.delete(canvas);
  }
  function lost(event: Event) { event.preventDefault(); retireView(); notify(cleanupFailed ? 'cleanupFailed' : 'contextLost'); }
  function schedule() {
    if (disposed || pendingFrame !== null || !mounted) return;
    try { pendingFrame = frames.request(draw); } catch { retireView(); notify('renderFailed'); }
  }
  function draw() {
    pendingFrame = null; if (disposed || !mounted || !graphics) return;
    const drawing = mounted, renderer = graphics;
    if (!isCurrent()) { retireView(); notify('stale'); return; }
    if (disposed || mounted !== drawing || graphics !== renderer) return;
    try {
      shaderFailed = false; renderer.render(drawing.scene, drawing.camera);
      if (disposed || graphics !== renderer || mounted !== drawing) return;
      const gl = renderer.getContext();
      if (shaderFailed || gl.isContextLost() || gl.getError() !== gl.NO_ERROR) throw new Error('Geometry draw unavailable');
      if (disposed || graphics !== renderer || mounted !== drawing) return;
      notify('drawn');
    } catch { if (!disposed) { retireView(); notify(cleanupFailed ? 'cleanupFailed' : 'renderFailed'); } }
  }
  function move(action: GeometryCameraAction): Readonly<{ state: 'moved' }> | Refused {
    if (disposed) return refused('disposed'); if (cleanupFailed) return refused('cleanupFailed'); if (!mounted) return refused('busy');
    const moving = mounted;
    if (!isCurrent()) { retireView(); notify('stale'); return refused('stale'); }
    if (disposed) return refused('disposed'); if (mounted !== moving) return refused('busy');
    const result = moveGeometryCamera(mounted.cameraState, action);
    if (result.state !== 'ready') { notify('cameraFitUnsupported'); return refused('cameraFitUnsupported'); }
    // Validate a separate small camera before replacing the displayed matrices; a refused action preserves the scene.
    const candidate = new PerspectiveCamera(45, 4 / 3, result.value.near, result.value.far);
    if (!installCamera(candidate, result.value, mounted.bounds)) { notify('cameraFitUnsupported'); return refused('cameraFitUnsupported'); }
    mounted.camera.copy(candidate, false); mounted.cameraState = result.value; schedule();
    return disposed ? refused(cleanupFailed ? 'cleanupFailed' : 'renderFailed') : { state: 'moved' };
  }
  function resize(next: GeometryBackingSize): Readonly<{ state: 'resized' }> | Refused {
    if (disposed) return refused('disposed'); if (cleanupFailed) return refused('cleanupFailed'); if (!graphics || mounting || disposing || retiringScene) return refused('busy');
    if (!validBackingSize(next)) return refused('resourceLimit');
    if (next.width === size.width && next.height === size.height) return { state: 'resized' };
    if (!isCurrent()) { retireView(); notify('stale'); return refused('stale'); }
    const renderer = graphics;
    try {
      renderer.setSize(next.width, next.height, false);
      const gl = renderer.getContext();
      if (gl.drawingBufferWidth !== next.width || gl.drawingBufferHeight !== next.height || canvas.width !== next.width || canvas.height !== next.height) throw new Error('Geometry backing size unavailable');
    } catch { retireView(); notify(cleanupFailed ? 'cleanupFailed' : 'renderFailed'); return refused(cleanupFailed ? 'cleanupFailed' : 'renderFailed'); }
    size = Object.freeze({ width: next.width, height: next.height }); schedule();
    return disposed ? refused(cleanupFailed ? 'cleanupFailed' : 'renderFailed') : { state: 'resized' };
  }
  function bindControls() {
    const interactable = () => {
      if (disposed || mounted === null) return false;
      const previous = mounted, current = isCurrent();
      return current && !disposed && mounted === previous;
    };
    const add = (name: string, listener: EventListener, config?: AddEventListenerOptions) => {
      canvas.addEventListener(name, listener, config); controls.push(() => canvas.removeEventListener(name, listener, config));
    };
    add('keydown', event => {
      if (!interactable() || canvas.ownerDocument.activeElement !== canvas) return;
      const key = (event as KeyboardEvent).key; let action: GeometryCameraAction|undefined;
      if (key === 'ArrowLeft' || key === 'ArrowRight') action = { kind: 'rotate', yaw: (key === 'ArrowRight' ? 1 : -1) * Math.PI / 12, elevation: 0 };
      else if (key === 'ArrowUp' || key === 'ArrowDown') action = { kind: 'rotate', yaw: 0, elevation: (key === 'ArrowUp' ? 1 : -1) * Math.PI / 12 };
      else if (key === '+' || key === '=') action = { kind: 'zoom', ticks: -1 };
      else if (key === '-' || key === '−') action = { kind: 'zoom', ticks: 1 };
      else if (key === 'r' || key === 'R') action = { kind: 'reset' };
      if (action) { event.preventDefault(); move(action); }
    });
    add('wheel', event => {
      const wheel = event as WheelEvent;
      if (!interactable() || canvas.ownerDocument.activeElement !== canvas || !Number.isFinite(wheel.deltaY) || wheel.deltaY === 0) return;
      event.preventDefault(); move({ kind: 'zoom', ticks: Math.sign(wheel.deltaY) });
    }, { passive: false });
    add('pointerdown', event => {
      const value = event as PointerEvent;
      if (!interactable() || value.button !== 0 || value.isPrimary === false || pointer || !Number.isFinite(value.clientX) || !Number.isFinite(value.clientY)) return;
      canvas.focus({ preventScroll: true });
      try { canvas.setPointerCapture(value.pointerId); pointer = { id: value.pointerId, x: value.clientX, y: value.clientY }; event.preventDefault(); } catch { pointer = null; }
    });
    add('pointermove', event => {
      const value = event as PointerEvent;
      if (!interactable() || !pointer || pointer.id !== value.pointerId || !Number.isFinite(value.clientX) || !Number.isFinite(value.clientY)) return;
      const rect = canvas.getBoundingClientRect(); if (!Number.isFinite(rect.width) || !Number.isFinite(rect.height) || rect.width <= 0 || rect.height <= 0) { endDrag(); return; }
      const yaw = Math.max(-Math.PI, Math.min(Math.PI, (value.clientX - pointer.x) / rect.width * Math.PI));
      const elevation = Math.max(-Math.PI, Math.min(Math.PI, -(value.clientY - pointer.y) / rect.height * Math.PI));
      pointer = { id: pointer.id, x: value.clientX, y: value.clientY }; event.preventDefault(); move({ kind: 'rotate', yaw, elevation });
    });
    for (const name of ['pointerup', 'pointercancel', 'pointerleave', 'lostpointercapture']) add(name, event => {
      if (pointer?.id === (event as PointerEvent).pointerId) endDrag();
    });
    add('blur', () => endDrag());
  }
  const owner: GeometrySceneOwner = Object.freeze({
    mount(input: ValidatedGeometry, identity: GeometrySceneIdentity, retired: GeometryBodyRetired) {
      if (mounting || disposing || retiringScene) return refused('busy');
      mounting = true;
      try {
      if (disposed) return refused('disposed'); if (observerFailed) return refused('observerFailed'); if (cleanupFailed || debt) return refused('cleanupFailed');
      if (!isCurrent()) return refused('stale'); if (mounted) return refused('busy');
      if (disposed) return refused('disposed'); if (cleanupFailed || debt) return refused('cleanupFailed');
      if (!validIdentity(identity) || identity === null || !retired || !same(identity, retired) || retired.retired !== true) return refused('invalidIdentity');
      if (consumedToken === identity.operationToken) return refused('retirementRequired');
      if (input.manifest.artifactId !== identity.geometryArtifactId || !(input.positions instanceof Float32Array) || input.positions.buffer.byteLength !== input.manifest.byteLength)
        return refused('invalidGeometry');
      if (isAppearance(input) ? !(input.uvs instanceof Float32Array) || !(input.indices instanceof Uint32Array) || input.uvs.buffer !== input.positions.buffer
          || input.indices.buffer !== input.positions.buffer || input.manifest.byteLength > 10485760 || input.textures.some(t => t.texels.buffer !== input.positions.buffer)
        : !(input.lineIndices instanceof Uint32Array) || input.positions.buffer !== input.lineIndices.buffer
          || input.positions.byteLength + input.lineIndices.byteLength > 2097152) return refused('invalidGeometry');
      const fitted = fitGeometryCamera(input.manifest.bounds); if (fitted.state !== 'ready') return refused('cameraFitUnsupported');
      const camera = new PerspectiveCamera(45, 4 / 3, fitted.value.near, fitted.value.far);
      if (!installCamera(camera, fitted.value, input.manifest.bounds)) return refused('cameraFitUnsupported');
      let confirmed = false; try { confirmed = options.confirmRetirement(identity, retired); } catch { /* No GPU or attribute owner exists yet. */ }
      if (!confirmed) return refused('retirementRequired');
      if (disposed) return refused('disposed'); if (cleanupFailed || debt) return refused('cleanupFailed');
      if (!isCurrent()) return refused('stale');
      if (disposed) return refused('disposed'); if (cleanupFailed || debt) return refused('cleanupFailed');
      consumedToken = identity.operationToken;
      const geometry = new BufferGeometry(), scene = new Scene();
      let line: Object3D, materials: Material[], textures: DataTexture[] = [];
      if (isAppearance(input)) {
        // Unlit: each drawn batch samples its one RGBA8 texture as decoded in C#. No colour, weight or lighting animation is evaluated.
        textures = input.textures.map(t => {
          const texture = new DataTexture(t.texels, t.width, t.height, RGBAFormat, UnsignedByteType);
          texture.colorSpace = SRGBColorSpace; texture.flipY = false; texture.generateMipmaps = true;
          texture.magFilter = LinearFilter; texture.minFilter = LinearMipmapLinearFilter;
          texture.wrapS = t.wrapU ? RepeatWrapping : ClampToEdgeWrapping; texture.wrapT = t.wrapV ? RepeatWrapping : ClampToEdgeWrapping;
          texture.needsUpdate = true; return texture;
        });
        materials = input.draws.map(d => new MeshBasicMaterial({ map: textures[d.slot], side: d.twoSided ? DoubleSide : FrontSide, transparent: false,
          alphaTest: d.alphaKey ? input.manifest.alphaReference / 255 : 0, depthTest: d.depthTest, depthWrite: d.depthWrite, toneMapped: false }));
        line = new Mesh(geometry, materials);
      } else {
        materials = [new LineBasicMaterial({ color: '#176e66', linewidth: 1, opacity: 1, transparent: false, toneMapped: false })];
        line = new LineSegments(geometry, materials[0]);
      }
      mounted = { scene, geometry, materials, textures, line, camera, cameraState: fitted.value, bounds: input.manifest.bounds, identity: snapshot(identity) };
      try {
        geometry.setAttribute('position', new BufferAttribute(input.positions, 3));
        if (isAppearance(input)) {
          geometry.setAttribute('uv', new BufferAttribute(input.uvs, 2)); geometry.setIndex(new BufferAttribute(input.indices, 1));
          input.draws.forEach((d, index) => geometry.addGroup(d.indexStart, d.indexCount, index)); geometry.setDrawRange(0, input.indices.length);
        } else { geometry.setIndex(new BufferAttribute(input.lineIndices, 1)); geometry.setDrawRange(0, input.lineIndices.length); }
        line.frustumCulled = false; line.matrixAutoUpdate = false; line.matrixWorldAutoUpdate = false;
        line.matrix.copy(rotation); line.matrixWorld.copy(rotation); scene.matrixAutoUpdate = false; scene.matrixWorldAutoUpdate = false;
        scene.add(line); bindControls(); schedule();
        return disposed ? refused(cleanupFailed ? 'cleanupFailed' : 'renderFailed') : { state: 'mounted' as const };
      } catch { retireView(); notify(cleanupFailed ? 'cleanupFailed' : 'renderFailed'); return refused(cleanupFailed ? 'cleanupFailed' : 'renderFailed'); }
      } finally { mounting = false; }
    },
    move,
    resize,
    disposeScene(identity: GeometrySceneIdentity|null) {
      if (disposing || retiringScene) return refused('busy');
      const mismatch = !validIdentity(identity) || mounted !== null && (identity === null || !same(identity, mounted.identity));
      retireScene(); if (mismatch) return refused('invalidIdentity'); return notice(identity, 'scene');
    },
    disposeView(identity: GeometrySceneIdentity|null) {
      if (disposing || retiringScene) return refused('busy');
      const mismatch = !validIdentity(identity) || mounted !== null && (identity === null || !same(identity, mounted.identity));
      retireView(); if (mismatch) return refused('invalidIdentity'); return notice(identity, 'view');
    },
  });
  try {
    if (acquireContext) { constructingContext = acquireContext(); if (!constructingContext) throw new Error('WebGL2 unavailable'); }
    graphics = factory({ canvas, ...(constructingContext ? { context: constructingContext } : {}),
      alpha: false, depth: true, stencil: false, antialias: false, preserveDrawingBuffer: false });
    constructingContext = null; // The constructed renderer now owns the exact context.
    graphics.setPixelRatio(1); graphics.setSize(size.width, size.height, false); graphics.setClearColor('#fffefa', 1);
    graphics.outputColorSpace = SRGBColorSpace; graphics.toneMapping = NoToneMapping; graphics.sortObjects = false; graphics.shadowMap.enabled = false;
    const context = graphics.getContext(), attributes = context.getContextAttributes();
    if (!attributes || attributes.antialias || attributes.preserveDrawingBuffer || attributes.stencil || attributes.alpha || !attributes.depth
      || context.isContextLost() || !/^WebGL 2\.0(?:\s|$)/.test(String(context.getParameter(context.VERSION)))) throw new Error('Unsupported geometry context');
    if (context.drawingBufferWidth !== size.width || context.drawingBufferHeight !== size.height || canvas.width !== size.width || canvas.height !== size.height) {
      retireView(); return { state: 'refused', code: cleanupFailed ? 'cleanupFailed' : 'resourceLimit', ...(cleanupFailed ? { owner } : {}) };
    }
    graphics.debug.checkShaderErrors = true; graphics.debug.onShaderError = () => { shaderFailed = true; };
    canvas.addEventListener('webglcontextlost', lost); lossListener = true;
    return { state: 'available', owner };
  } catch {
    retireView(); return { state: 'refused', code: cleanupFailed ? 'cleanupFailed' : 'webglUnsupported', ...(cleanupFailed ? { owner } : {}) };
  }
}
