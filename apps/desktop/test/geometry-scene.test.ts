import { afterEach, describe, expect, it, vi } from 'vitest';
import { BufferAttribute, BufferGeometry, Camera, LineBasicMaterial, LineSegments, Scene, SRGBColorSpace, NoToneMapping } from 'three';
import { createGeometryScene, createGeometrySceneForTests, geometryBackingSize, geometryCanvasBudgetBytes, geometryCanvasBytesPerPixel, type GeometryGraphics, type GeometrySceneIdentity } from '../src/renderer/geometry-scene';
import { validateGeometry } from '../src/renderer/geometry-vmg';
import { geometryCSharpGolden, geometryFixture } from './geometry-fixtures';

function harness() {
  const canvas = document.createElement('canvas'); canvas.tabIndex = 0; document.body.append(canvas);
  vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({ width: 512, height: 384, left: 0, top: 0, right: 512, bottom: 384, x: 0, y: 0, toJSON() {} });
  let captured: number|null = null;
  canvas.setPointerCapture = id => { captured = id; }; canvas.hasPointerCapture = id => captured === id;
  canvas.releasePointerCapture = () => { captured = null; };
  const frames = new Map<number, FrameRequestCallback>(); let sequence = 0;
  const attributes = { alpha: false, depth: true, stencil: false, antialias: false, preserveDrawingBuffer: false };
  const gl = { VERSION: 0x1f02, NO_ERROR: 0, drawingBufferWidth: 588, drawingBufferHeight: 441,
    getParameter: () => 'WebGL 2.0 (authored)', getContextAttributes: () => attributes, isContextLost: () => false, getError: () => 0 };
  let seen: { scene: Scene; camera: Camera }|null = null;
  const renderer: GeometryGraphics = {
    setPixelRatio: vi.fn(), setSize: vi.fn((width, height) => { canvas.width = width; canvas.height = height; }),
    setClearColor: vi.fn(), getContext: () => gl as unknown as WebGL2RenderingContext,
    render: vi.fn((scene, camera) => { seen = { scene, camera }; }), dispose: vi.fn(), forceContextLoss: vi.fn(),
    renderLists: { dispose: vi.fn() }, outputColorSpace: SRGBColorSpace, toneMapping: NoToneMapping, sortObjects: true,
    shadowMap: { enabled: false }, debug: { checkShaderErrors: true, onShaderError: null },
  };
  let current = true, confirmed = true;
  const status = vi.fn(); const confirm = vi.fn(() => confirmed); const currentCheck = vi.fn(() => current);
  const result = createGeometrySceneForTests(canvas, { viewId: 'aa'.repeat(16), viewGeneration: 1,
    isCurrent: currentCheck, confirmRetirement: confirm, onStatus: status }, () => renderer,
  { request: callback => { const id = ++sequence; frames.set(id, callback); return id; }, cancel: id => { frames.delete(id); } });
  if (result.state !== 'available') throw Error(`scene creation refused: ${result.code}`);
  const identity: GeometrySceneIdentity = { viewId: 'aa'.repeat(16), viewGeneration: 1, operationToken: 'bb'.repeat(16),
    geometryArtifactId: geometryCSharpGolden().manifest.artifactId };
  return { canvas, frames, renderer, attributes, gl, status, confirm, currentCheck, owner: result.owner, identity,
    ack: { ...identity, retired: true as const }, getSeen: () => seen, captured: () => captured,
    deny: () => { confirmed = false; }, stale: () => { current = false; },
    frame() { const pending = [...frames]; frames.clear(); for (const [, callback] of pending) callback(0); },
  };
}
async function geometry(f = geometryCSharpGolden()) {
  const result = await validateGeometry(f.buffer, f.manifest, { signal: new AbortController().signal, isCurrent: () => true });
  if (result.state !== 'valid') throw Error('invalid authored geometry'); return result.value;
}
function pointer(canvas: HTMLCanvasElement, type: string, id: number, x: number, y: number, button = 0) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y, button });
  Object.defineProperties(event, { pointerId: { value: id }, isPrimary: { value: true } }); canvas.dispatchEvent(event); return event;
}
afterEach(() => { vi.restoreAllMocks(); document.body.replaceChildren(); });

describe('bounded Three diagnostic scene owner', () => {
  it('mounts only after matching actual-retirement confirmation and uses the original typed views/matrices', async () => {
    const h = harness(), input = await geometry();
    expect(h.renderer.render).not.toHaveBeenCalled(); expect(h.confirm).not.toHaveBeenCalled();
    expect(h.owner.mount(input, h.identity, { ...h.ack, operationToken: 'cc'.repeat(16) })).toMatchObject({ state: 'refused' });
    expect(h.owner.mount(input, h.identity, h.ack)).toEqual({ state: 'mounted' });
    expect(h.confirm).toHaveBeenCalledOnce(); expect(h.frames.size).toBe(1); h.frame();
    const seen = h.getSeen()!; const line = seen.scene.children[0] as LineSegments<BufferGeometry, LineBasicMaterial>;
    expect(line.isLineSegments).toBe(true); expect(line.frustumCulled).toBe(false); expect(h.renderer.sortObjects).toBe(false);
    expect(line.geometry.getAttribute('position').array).toBe(input.positions);
    expect(line.geometry.getIndex()!.array).toBe(input.lineIndices);
    expect(line.geometry.getAttribute('position')).toBeInstanceOf(BufferAttribute);
    expect(line.geometry.drawRange).toEqual({ start: 0, count: 12 }); expect(line.geometry.groups).toEqual([]);
    expect(line.geometry.boundingSphere).toBe(null); expect(line.geometry.boundingBox).toBe(null);
    expect(line.matrix.elements).toEqual([1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1]);
    expect(seen.camera.matrixWorldAutoUpdate).toBe(false); expect(seen.camera.matrixAutoUpdate).toBe(false);
    expect(line.matrixAutoUpdate).toBe(false); expect(seen.scene.matrixWorldAutoUpdate).toBe(false);
    expect(h.renderer.outputColorSpace).toBe(SRGBColorSpace); expect(h.renderer.toneMapping).toBe(NoToneMapping);
    expect(h.renderer.setPixelRatio).toHaveBeenCalledExactlyOnceWith(1);
    expect(h.renderer.setSize).toHaveBeenCalledExactlyOnceWith(588, 441, false);
    expect(h.status).toHaveBeenCalledWith({ code: 'drawn' }); expect(h.frames.size).toBe(0);
    h.owner.disposeView(h.identity);
  });
  it('refuses denied/stale/replayed/busy mounting without allocating a second scene', async () => {
    const input = await geometry(), h = harness(); h.deny();
    expect(h.owner.mount(input, h.identity, h.ack)).toMatchObject({ code: 'retirementRequired' }); expect(h.frames.size).toBe(0);
    const other = harness(); expect(other.owner.mount(input, other.identity, other.ack).state).toBe('mounted');
    expect(other.owner.mount(input, other.identity, other.ack)).toMatchObject({ code: 'busy' });
    expect(other.owner.disposeScene(other.identity)).toMatchObject({ state: 'disposed', notice: { scope: 'scene', operationToken: other.identity.operationToken } });
    expect(other.owner.mount(input, other.identity, other.ack)).toMatchObject({ code: 'retirementRequired' });
    const identity = { ...other.identity, operationToken: 'dd'.repeat(16) };
    expect(other.owner.mount(input, identity, { ...identity, retired: true }).state).toBe('mounted');
    other.owner.disposeView(identity); h.owner.disposeView(null);
    const stale = harness(); stale.stale(); expect(stale.owner.mount(input, stale.identity, stale.ack)).toMatchObject({ code: 'stale' });
    stale.owner.disposeView(null);
  });
  it('coalesces camera updates, keeps canvas-only keys/wheel and preserves focus for native button actions', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack); h.frame();
    const seen = h.getSeen()!, before = [...seen.camera.matrixWorldInverse.elements];
    const outside = document.createElement('button'); document.body.append(outside); outside.focus();
    const unfocused = new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
    h.canvas.dispatchEvent(unfocused); expect(unfocused.defaultPrevented).toBe(false); expect(h.frames.size).toBe(0);
    const scroll = new WheelEvent('wheel', { deltaY: 1, cancelable: true }); h.canvas.dispatchEvent(scroll);
    expect(scroll.defaultPrevented).toBe(false);
    h.owner.move({ kind: 'rotate', yaw: Math.PI / 12, elevation: 0 }); expect(document.activeElement).toBe(outside);
    h.owner.move({ kind: 'zoom', ticks: -1 }); expect(h.frames.size).toBe(1); h.frame();
    expect(seen.camera.matrixWorldInverse.elements).not.toEqual(before);
    h.canvas.focus(); const tab = new KeyboardEvent('keydown', { key: 'Tab', cancelable: true }); h.canvas.dispatchEvent(tab);
    expect(tab.defaultPrevented).toBe(false);
    for (const key of ['ArrowUp', 'ArrowDown', 'ArrowLeft', '+', '-', 'R']) {
      const event = new KeyboardEvent('keydown', { key, cancelable: true }); h.canvas.dispatchEvent(event); expect(event.defaultPrevented).toBe(true);
    }
    const wheel = new WheelEvent('wheel', { deltaY: -1, cancelable: true }); h.canvas.dispatchEvent(wheel); expect(wheel.defaultPrevented).toBe(true);
    expect(h.frames.size).toBe(1); h.owner.disposeView(h.identity); expect(h.frames.size).toBe(0);
  });
  it('ends only transient drag on leave/blur, ignores other pointers and keeps keyboard focus usable', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack); h.frame();
    pointer(h.canvas, 'pointerdown', 1, 0, 0, 2); expect(h.captured()).toBe(null);
    pointer(h.canvas, 'pointerdown', 1, 100, 100); expect(document.activeElement).toBe(h.canvas); expect(h.captured()).toBe(1);
    pointer(h.canvas, 'pointermove', 2, 200, 200); expect(h.frames.size).toBe(0);
    pointer(h.canvas, 'pointermove', 1, 200, 150); expect(h.frames.size).toBe(1);
    pointer(h.canvas, 'pointerleave', 1, 200, 150); expect(h.captured()).toBe(null); h.frame();
    const key = new KeyboardEvent('keydown', { key: 'r', cancelable: true }); h.canvas.dispatchEvent(key);
    expect(key.defaultPrevented).toBe(true); expect(h.frames.size).toBe(1);
    h.owner.disposeView(h.identity);
  });
  it('synchronously drops scene objects/attributes/control/frame owners before returning retirement notices', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack); h.frame();
    const scene = h.getSeen()!.scene, line = scene.children[0] as LineSegments<BufferGeometry, LineBasicMaterial>;
    const geometryDispose = vi.spyOn(line.geometry, 'dispose'), materialDispose = vi.spyOn(line.material, 'dispose');
    h.owner.move({ kind: 'zoom', ticks: 1 });
    const sceneResult = h.owner.disposeScene(h.identity);
    expect(sceneResult).toEqual({ state: 'disposed', notice: { viewId: h.identity.viewId, operationToken: h.identity.operationToken, scope: 'scene' } });
    expect(geometryDispose).toHaveBeenCalledOnce(); expect(materialDispose).toHaveBeenCalledOnce(); expect(scene.children).toHaveLength(0);
    expect(line.geometry.getIndex()).toBe(null); expect(line.geometry.getAttribute('position')).toBeUndefined();
    expect(h.frames.size).toBe(0); expect(h.renderer.dispose).not.toHaveBeenCalled();
    expect(h.owner.disposeView(h.identity)).toMatchObject({ state: 'disposed', notice: { scope: 'view' } });
    expect(h.renderer.dispose).toHaveBeenCalledOnce(); expect(h.renderer.forceContextLoss).toHaveBeenCalledOnce();
    expect(h.canvas.width).toBe(0); expect(h.canvas.height).toBe(0);
    h.canvas.focus(); const key = new KeyboardEvent('keydown', { key: 'ArrowRight', cancelable: true }); h.canvas.dispatchEvent(key);
    expect(key.defaultPrevented).toBe(false); expect(h.owner.disposeView(h.identity).state).toBe('disposed');
    expect(h.renderer.dispose).toHaveBeenCalledOnce();
  });
  it('attempts every cleanup owner and retains failed cleanup debt instead of returning a notice', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack); h.frame();
    const line = h.getSeen()!.scene.children[0] as LineSegments<BufferGeometry, LineBasicMaterial>;
    vi.spyOn(line.geometry, 'dispose').mockImplementation(() => { throw Error('authored delete fault'); });
    const material = vi.spyOn(line.material, 'dispose');
    expect(h.owner.disposeView(h.identity)).toEqual({ state: 'refused', code: 'cleanupFailed' });
    expect(material).toHaveBeenCalledOnce(); expect(h.renderer.dispose).toHaveBeenCalledOnce(); expect(h.renderer.forceContextLoss).toHaveBeenCalledOnce();
    expect(h.owner.disposeView(h.identity)).toEqual({ state: 'refused', code: 'cleanupFailed' });
    expect(h.owner.mount(await geometry(), h.identity, h.ack)).toMatchObject({ state: 'refused' });
  });
  it('context loss retires local owners without restoring/uploading/focusing and reports presentation-only loss', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack);
    const outside = document.createElement('button'); document.body.append(outside); outside.focus();
    h.canvas.dispatchEvent(new Event('webglcontextlost', { cancelable: true }));
    expect(h.frames.size).toBe(0); expect(h.renderer.dispose).toHaveBeenCalledOnce(); expect(document.activeElement).toBe(outside);
    expect(h.status).toHaveBeenCalledWith({ code: 'contextLost' });
    h.canvas.dispatchEvent(new Event('webglcontextrestored')); expect(h.frames.size).toBe(0);
    expect(h.owner.mount(await geometry(), h.identity, h.ack)).toMatchObject({ code: 'disposed' });
    expect(h.owner.disposeView(h.identity)).toMatchObject({ state: 'disposed', notice: { scope: 'view' } });
  });
  it('reports shader/draw/currentness faults without an animation loop or stale redraw', async () => {
    for (const kind of ['shader', 'draw', 'stale'] as const) {
      const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack);
      if (kind === 'shader') vi.mocked(h.renderer.render).mockImplementation(() => h.renderer.debug.onShaderError?.({} as WebGLRenderingContext,
        {} as Parameters<NonNullable<GeometryGraphics['debug']['onShaderError']>>[1], {} as WebGLShader, {} as WebGLShader));
      if (kind === 'draw') vi.mocked(h.renderer.render).mockImplementation(() => { throw Error('draw fault'); });
      if (kind === 'stale') h.stale(); h.frame();
      expect(h.status).toHaveBeenCalledWith({ code: kind === 'stale' ? 'stale' : 'renderFailed' }); expect(h.frames.size).toBe(0);
      h.owner.disposeView(h.identity);
    }
  });
  it('keeps an unsupported camera separate from a structurally valid geometry artifact before attribute admission', async () => {
    const f = geometryFixture({ positions: [0, 0, 0], triangles: [[0, 0, 0]], sections: [{ start: 0, count: 3, vertexCount: 1 }] });
    const input = await geometry(f), h = harness(); const identity = { ...h.identity, geometryArtifactId: f.manifest.artifactId };
    expect(h.owner.mount(input, identity, { ...identity, retired: true })).toMatchObject({ code: 'cameraFitUnsupported' });
    expect(h.confirm).not.toHaveBeenCalled(); expect(h.renderer.render).not.toHaveBeenCalled(); expect(h.frames.size).toBe(0);
    h.owner.disposeView(identity);
  });
  it('preserves camera/scene/focus on rejected motion and deduplicates local refusal status', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack); h.frame(); h.canvas.focus();
    const before = [...h.getSeen()!.camera.matrixWorldInverse.elements];
    for (let index = 0; index < 3; index++) expect(h.owner.move({ kind: 'zoom', ticks: Infinity })).toMatchObject({ code: 'cameraFitUnsupported' });
    expect(h.status.mock.calls.filter(([value]) => value.code === 'cameraFitUnsupported')).toHaveLength(1);
    expect(h.getSeen()!.camera.matrixWorldInverse.elements).toEqual(before); expect(document.activeElement).toBe(h.canvas); expect(h.frames.size).toBe(0);
    h.owner.disposeView(h.identity);
  });
  it('does not return a disposal acknowledgement during reentrant resource teardown', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack); h.frame();
    const line = h.getSeen()!.scene.children[0] as LineSegments<BufferGeometry, LineBasicMaterial>;
    const nested: unknown[] = [];
    line.geometry.addEventListener('dispose', () => { nested.push(h.owner.disposeView(h.identity)); nested.push(h.owner.disposeScene(h.identity)); });
    const result = h.owner.disposeView(h.identity);
    expect(nested).toEqual([{ state: 'refused', code: 'busy' }, { state: 'refused', code: 'busy' }]);
    expect(result).toMatchObject({ state: 'disposed', notice: { scope: 'view' } }); expect(h.renderer.dispose).toHaveBeenCalledOnce();
    expect(h.owner.disposeView(h.identity)).toEqual(result); expect(h.renderer.dispose).toHaveBeenCalledOnce();
  });
  it('refuses replacement mounting from a resource disposal event until actual retirement returns', async () => {
    const h = harness(), input = await geometry(); h.owner.mount(input, h.identity, h.ack); h.frame();
    const line = h.getSeen()!.scene.children[0] as LineSegments<BufferGeometry, LineBasicMaterial>;
    const identity = { ...h.identity, operationToken: 'dd'.repeat(16) }; let nested: unknown;
    line.geometry.addEventListener('dispose', () => { nested = h.owner.mount(input, identity, { ...identity, retired: true }); });
    expect(h.owner.disposeScene(h.identity).state).toBe('disposed');
    expect(nested).toMatchObject({ state: 'refused', code: 'busy' }); expect(h.frames.size).toBe(0);
    expect(h.owner.mount(input, identity, { ...identity, retired: true }).state).toBe('mounted'); h.owner.disposeView(identity);
  });
  it('does not recreate references or controls when retirement confirmation synchronously disposes the view', async () => {
    const h = harness(), input = await geometry(); let disposal: unknown;
    h.confirm.mockImplementation(() => { disposal = h.owner.disposeView(h.identity); return true; });
    expect(h.owner.mount(input, h.identity, h.ack)).toMatchObject({ state: 'refused', code: 'disposed' });
    expect(disposal).toMatchObject({ state: 'disposed', notice: { scope: 'view' } });
    expect(h.frames.size).toBe(0); expect(h.renderer.render).not.toHaveBeenCalled();
    h.canvas.focus(); const key = new KeyboardEvent('keydown', { key: 'ArrowRight', cancelable: true }); h.canvas.dispatchEvent(key);
    expect(key.defaultPrevented).toBe(false);
  });
  it('serializes reentrant mount from the trusted confirmation port without replacing an admitted owner', async () => {
    const h = harness(), input = await geometry(); let nested: unknown;
    const identity = { ...h.identity, operationToken: 'dd'.repeat(16) };
    h.confirm.mockImplementationOnce(() => { nested = h.owner.mount(input, identity, { ...identity, retired: true }); return true; });
    expect(h.owner.mount(input, h.identity, h.ack).state).toBe('mounted');
    expect(nested).toMatchObject({ state: 'refused', code: 'busy' }); expect(h.frames.size).toBe(1);
    h.owner.disposeView(h.identity); expect(h.frames.size).toBe(0);
  });
  it('keeps a synchronous actual context-loss outcome distinct from a second draw-failure report', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack);
    vi.mocked(h.renderer.render).mockImplementationOnce(() => h.canvas.dispatchEvent(new Event('webglcontextlost', { cancelable: true })));
    h.frame(); expect(h.status).toHaveBeenCalledWith({ code: 'contextLost' });
    expect(h.status.mock.calls.some(([value]) => value.code === 'renderFailed' || value.code === 'drawn')).toBe(false);
    expect(h.frames.size).toBe(0); expect(h.renderer.dispose).toHaveBeenCalledOnce();
  });
  it('fences currentness callbacks that retire ownership during movement or pointer admission', async () => {
    const h = harness(); h.owner.mount(await geometry(), h.identity, h.ack); h.frame();
    h.currentCheck.mockImplementationOnce(() => { h.owner.disposeView(h.identity); return true; });
    expect(h.owner.move({ kind: 'zoom', ticks: 1 })).toMatchObject({ code: 'disposed' }); expect(h.frames.size).toBe(0);
    const other = harness(); other.owner.mount(await geometry(), other.identity, other.ack); other.frame();
    const outside = document.createElement('button'); document.body.append(outside); outside.focus();
    other.currentCheck.mockImplementationOnce(() => { other.owner.disposeView(other.identity); return true; });
    pointer(other.canvas, 'pointerdown', 1, 10, 10);
    expect(document.activeElement).toBe(outside); expect(other.captured()).toBe(null); expect(other.frames.size).toBe(0);
  });
  it('refuses a second owner for the same canvas while retaining failed cleanup capacity', async () => {
    const h = harness(), factory = vi.fn(() => h.renderer);
    const options = { viewId: h.identity.viewId, viewGeneration: 1, isCurrent: () => true, confirmRetirement: () => true, onStatus: () => {} };
    const schedule = { request: () => 1, cancel: () => {} };
    expect(createGeometrySceneForTests(h.canvas, options, factory, schedule)).toMatchObject({ code: 'busy' }); expect(factory).not.toHaveBeenCalled();
    vi.mocked(h.renderer.dispose).mockImplementation(() => { throw Error('retained renderer debt'); });
    expect(h.owner.disposeView(null)).toMatchObject({ code: 'cleanupFailed' });
    expect(createGeometrySceneForTests(h.canvas, options, factory, schedule)).toMatchObject({ code: 'busy' }); expect(factory).not.toHaveBeenCalled();
  });
  it('releases a completely retired canvas for a new view and accepts its failed-fetch identity without mounting', () => {
    const h = harness(); expect(h.owner.disposeView(h.identity)).toMatchObject({ state: 'disposed' });
    const fresh = createGeometrySceneForTests(h.canvas, { viewId: 'dd'.repeat(16), viewGeneration: 2, isCurrent: () => true,
      confirmRetirement: () => true, onStatus: () => {} }, () => h.renderer, { request: () => 1, cancel: () => {} });
    expect(fresh.state).toBe('available'); if (fresh.state !== 'available') throw Error('refused');
    const lateIdentity = { ...h.identity, viewId: 'dd'.repeat(16), viewGeneration: 2 };
    expect(fresh.owner.disposeView(lateIdentity)).toEqual({ state: 'disposed', notice: { viewId: lateIdentity.viewId, operationToken: lateIdentity.operationToken, scope: 'view' } });
  });
  it('tears down and reports draw failure if RAF admission itself throws', async () => {
    const h = harness(); h.owner.disposeView(null);
    const created = createGeometrySceneForTests(h.canvas, { viewId: h.identity.viewId, viewGeneration: 1, isCurrent: () => true,
      confirmRetirement: () => true, onStatus: h.status }, () => h.renderer, { request: () => { throw Error('RAF refused'); }, cancel: () => {} });
    if (created.state !== 'available') throw Error('refused');
    expect(created.owner.mount(await geometry(), h.identity, h.ack)).toMatchObject({ code: 'renderFailed' });
    expect(h.renderer.render).not.toHaveBeenCalled(); expect(h.status).toHaveBeenCalledWith({ code: 'renderFailed' });
  });
  it('refuses unsupported attributes/version/drawing-buffer dimensions and tears down before refusal', () => {
    for (const failure of ['antialias', 'stencil', 'preserveDrawingBuffer', 'alpha', 'depth', 'version', 'size'] as const) {
      const h = harness(); h.owner.disposeView(null); vi.mocked(h.renderer.dispose).mockClear();
      if (failure === 'version') h.gl.getParameter = () => 'WebGL 1.0';
      else if (failure === 'size') h.gl.drawingBufferWidth = 589;
      else h.attributes[failure] = failure !== 'depth';
      const created = createGeometrySceneForTests(h.canvas, { viewId: h.identity.viewId, viewGeneration: 1, isCurrent: () => true,
        confirmRetirement: () => true, onStatus: () => {} }, () => h.renderer, { request: () => 1, cancel: () => {} });
      expect(created).toMatchObject({ state: 'refused', code: failure === 'size' ? 'resourceLimit' : 'webglUnsupported' });
      expect(h.renderer.dispose).toHaveBeenCalledOnce(); expect(h.canvas.width).toBe(0); expect(h.canvas.height).toBe(0);
    }
  });
  it('observes status callback faults without leaking a frame or surviving scene owners', async () => {
    const h = harness(); h.status.mockImplementation(() => { throw Error('observer fault'); });
    h.owner.mount(await geometry(), h.identity, h.ack); h.frame();
    expect(h.frames.size).toBe(0); expect(h.renderer.dispose).toHaveBeenCalledOnce();
    expect(h.owner.disposeView(h.identity)).toMatchObject({ state: 'disposed' });
  });
  it('explicitly requests the approved alpha-false context and retires a partially constructed real renderer', () => {
    const canvas = document.createElement('canvas'); const lose = vi.fn();
    const raw = { getContextAttributes: () => ({ alpha: false }), isContextLost: () => false,
      getExtension: () => ({ loseContext: lose }) };
    // This intentionally incomplete context makes Three fail during initialization, after ownership acquisition.
    const get = vi.spyOn(canvas, 'getContext').mockReturnValue(raw as unknown as WebGL2RenderingContext);
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const result = createGeometryScene(canvas, { viewId: 'aa'.repeat(16), viewGeneration: 1, isCurrent: () => true,
      confirmRetirement: () => true, onStatus: () => {} });
    expect(get).toHaveBeenCalledExactlyOnceWith('webgl2', { alpha: false, depth: true, stencil: false, antialias: false, preserveDrawingBuffer: false });
    expect(result).toMatchObject({ state: 'refused', code: 'webglUnsupported' }); expect(lose).toHaveBeenCalledOnce();
    expect(canvas.width).toBe(0); expect(canvas.height).toBe(0);
  });
});

describe('backing store follows the displayed size (#59)', () => {
  it('is displayed size × devicePixelRatio at 4:3, clamped to the 8 MiB canvas share', () => {
    expect(geometryBackingSize(512, 384, 1)).toEqual({ width: 512, height: 384 });
    expect(geometryBackingSize(400, 300, 2)).toEqual({ width: 800, height: 600 });
    expect(geometryBackingSize(1022, 766.5, 1)).toEqual({ width: 836, height: 627 });
    expect(geometryBackingSize(1022, 766.5, 2)).toEqual({ width: 836, height: 627 });
    expect(geometryBackingSize(0, 0, 1)).toEqual({ width: 588, height: 441 });
    expect(geometryBackingSize(10, 7.5, 1)).toEqual({ width: 64, height: 48 });
    expect(836 * 627 * geometryCanvasBytesPerPixel).toBeLessThanOrEqual(geometryCanvasBudgetBytes);
  });
  it('resizes only the drawing buffer, redraws with the unchanged camera, and refuses an over-budget size', async () => {
    const h = harness(), input = await geometry();
    h.renderer.setSize = vi.fn((width: number, height: number) => { h.canvas.width = width; h.canvas.height = height; h.gl.drawingBufferWidth = width; h.gl.drawingBufferHeight = height; });
    expect(h.owner.mount(input, h.identity, h.ack)).toEqual({ state: 'mounted' }); h.frame();
    const projection = [...h.getSeen()!.camera.projectionMatrix.elements], view = [...h.getSeen()!.camera.matrixWorldInverse.elements];
    expect(h.owner.resize!({ width: 800, height: 600 })).toEqual({ state: 'resized' });
    expect(h.renderer.setSize).toHaveBeenLastCalledWith(800, 600, false); expect(h.frames.size).toBe(1); h.frame();
    expect([...h.getSeen()!.camera.projectionMatrix.elements]).toEqual(projection); expect([...h.getSeen()!.camera.matrixWorldInverse.elements]).toEqual(view);
    expect(h.owner.resize!({ width: 900, height: 675 })).toEqual({ state: 'refused', code: 'resourceLimit' });
    expect(h.owner.resize!({ width: 800, height: 500 })).toEqual({ state: 'refused', code: 'resourceLimit' });
    expect(h.renderer.setSize).toHaveBeenCalledTimes(1); expect(h.status).not.toHaveBeenCalledWith({ code: 'renderFailed' });
  });
  it('retires the view when the driver does not provide the requested drawing buffer', async () => {
    const h = harness(), input = await geometry();
    expect(h.owner.mount(input, h.identity, h.ack)).toEqual({ state: 'mounted' }); h.frame();
    expect(h.owner.resize!({ width: 800, height: 600 })).toEqual({ state: 'refused', code: 'renderFailed' });
    expect(h.status).toHaveBeenLastCalledWith({ code: 'renderFailed' }); expect(h.renderer.dispose).toHaveBeenCalled();
  });
});
