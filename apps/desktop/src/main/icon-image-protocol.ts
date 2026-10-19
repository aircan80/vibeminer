import { randomBytes, createHash } from 'node:crypto';
import { constants } from 'node:fs';
import { lstat, realpath, open, type FileHandle } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import { isAbsolute, join, resolve } from 'node:path';
import type { ItemsContext } from '../../../../contracts/generated/discovery';

const MAX_BYTES = 5 * 1024 * 1024;
const MAX_GRANTS = 200;
const MAX_REQUESTS = 64;
const MAX_ACTIVE = 4;
const CHUNK_BYTES = 64 * 1024;
const GRANT_LIFETIME_MS = 120_000;
const REQUEST_LIFETIME_MS = 10_000;
const HASH = /^[a-f0-9]{64}$/;
const PNG_SIGNATURE = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);

export interface IconImageRequest {
  context: ItemsContext;
  featureArtifact: string;
  itemId: string;
  artifactId: string;
}
export interface IconImageDescriptor { artifactId: string; byteLength: string; width: number; height: number }
export interface IconImageLease extends IconImageDescriptor { leaseId: string; sha256: string }
export interface IconImageProtocolOptions {
  /** Trusted main-process configuration, never renderer input. */
  dataDirectory: string;
  /** Must observe cancellation, or finish with its lease so it can be closed. */
  openIcon(request: IconImageRequest, signal: AbortSignal): Promise<IconImageLease>;
  /** Must be idempotent; integration also retains the engine's finite lease TTL. */
  closeIcon(leaseId: string): Promise<void>;
  /** Integration must independently gate the dedicated Electron session/frame. */
  authorizeRequest?(request: Request): boolean;
  now?(): number;
}
interface Grant { request: IconImageRequest; image: IconImageDescriptor; generation: number; expires: number }
interface Operation {
  controller: AbortController;
  generation: number;
  acquired: boolean;
  finished: boolean;
  done: Promise<void>;
  resolveDone(): void;
  finish(): void;
}
interface Waiter { operation: Operation; resolve(): void; reject(error: Error): void; abort(): void }
class Refusal extends Error { constructor(readonly status: number) { super('Icon image unavailable.'); } }
class CleanupFailure extends Refusal { constructor() { super(502); } }

/** Main-only capability and verified byte delivery; no Electron, game parser or network dependency. */
export class IconImageProtocol {
  private readonly grants = new Map<string, Grant>();
  private readonly operations = new Set<Operation>();
  private readonly queue: Waiter[] = [];
  // A cleanup failure closes admission immediately; at most four uncertain handles can remain.
  private readonly uncertainFiles = new Set<FileHandle>();
  private readonly uncertainLeases = new Set<string>();
  private cleanup?: Promise<void>;
  private readonly root: string;
  private readonly now: () => number;
  private active = 0;
  private generation = 0;
  private closed = false;
  constructor(private readonly options: IconImageProtocolOptions) {
    if (!isAbsolute(options.dataDirectory)) throw new Error('Icon storage requires an absolute app-owned directory.');
    this.root = join(resolve(options.dataDirectory), 'objects-v1');
    this.now = options.now ?? (() => performance.now());
  }

  /** Only issue after the main bridge has validated the exact ready batch and IPC sender. */
  issue(request: IconImageRequest, image: IconImageDescriptor): string {
    if (this.closed) throw new Refusal(410);
    if (!validRequest(request) || !validImage(image) || request.artifactId !== image.artifactId) throw new Refusal(400);
    this.prune();
    if (this.grants.size >= MAX_GRANTS) throw new Refusal(429);
    let capability: string;
    do { capability = randomBytes(16).toString('hex'); } while (this.grants.has(capability));
    this.grants.set(capability, {
      request: cloneRequest(request),
      image: { artifactId: image.artifactId, byteLength: image.byteLength, width: image.width, height: image.height },
      generation: this.generation, expires: this.now() + GRANT_LIFETIME_MS,
    });
    return `vibeminer-image://icon/${capability}`;
  }

  get stats(): Readonly<{ grants: number; admitted: number; active: number; queued: number }> {
    this.prune();
    return { grants: this.grants.size, admitted: this.operations.size, active: this.active, queued: this.queue.length };
  }

  async handle(request: Request): Promise<Response> {
    let grant: Grant;
    try {
      if (this.closed) throw new Refusal(410);
      if (request.method !== 'GET') throw new Refusal(405);
      if (request.headers.has('range')) throw new Refusal(416);
      // Match original spelling as well as parsed fields: no normalization, credentials or encoded paths.
      const match = /^vibeminer-image:\/\/icon\/([a-f0-9]{32})$/.exec(request.url);
      if (!match) throw new Refusal(404);
      if (this.options.authorizeRequest && !this.options.authorizeRequest(request)) throw new Refusal(403);
      this.prune();
      const found = this.grants.get(match[1]);
      if (!found) throw new Refusal(404);
      grant = found;
      this.checkGrant(grant);
      if (request.signal.aborted) throw new Refusal(499);
      if (this.operations.size >= MAX_REQUESTS) throw new Refusal(429);
    } catch (error) { return failure(error); }

    const controller = new AbortController();
    let resolveDone!: () => void;
    const done = new Promise<void>(resolveDoneValue => { resolveDone = resolveDoneValue; });
    const abort = () => controller.abort();
    const timer = setTimeout(abort, REQUEST_LIFETIME_MS);
    timer.unref?.();
    request.signal.addEventListener('abort', abort, { once: true });
    let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
    let bytes: Buffer | undefined;
    const operation: Operation = {
      controller, generation: this.generation, acquired: false, finished: false, done, resolveDone,
      finish: () => {
        if (operation.finished) return;
        operation.finished = true;
        bytes = undefined;
        clearTimeout(timer);
        request.signal.removeEventListener('abort', abort);
        controller.signal.removeEventListener('abort', abortBody);
        this.operations.delete(operation);
        if (operation.acquired) { this.active--; operation.acquired = false; this.advance(); }
        resolveDone();
      },
    };
    const abortBody = () => {
      // Before body creation, the copy/open finally block is responsible for actual retirement.
      if (streamController) { streamController.error(new Error('Icon image request ended.')); operation.finish(); }
    };
    controller.signal.addEventListener('abort', abortBody, { once: true });
    this.operations.add(operation);
    try {
      await this.acquire(operation);
      this.checkOperation(operation, grant);
      bytes = await this.copyVerified(grant, controller.signal);
      this.checkOperation(operation, grant);
      let offset = 0;
      const length = bytes.length;
      const body = new ReadableStream<Uint8Array>({
        start(value) { streamController = value; },
        pull: value => {
          if (controller.signal.aborted || operation.finished || !bytes) {
            if (!operation.finished) value.error(new Error('Icon image request ended.'));
            operation.finish(); return;
          }
          const end = Math.min(offset + CHUNK_BYTES, length);
          // Copy only a small delivery chunk so a final queued chunk cannot retain the large snapshot.
          value.enqueue(Uint8Array.from(bytes.subarray(offset, end)));
          offset = end;
          if (offset === length) { value.close(); operation.finish(); }
        },
        cancel: () => { controller.abort(); operation.finish(); },
      }, { highWaterMark: 0 });
      return new Response(body, { status: 200, headers: {
        'Content-Type': 'image/png', 'Content-Length': String(length),
        'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'no-store',
      } });
    } catch (error) {
      operation.finish();
      return failure(error instanceof CleanupFailure ? error : controller.signal.aborted ? new Refusal(499) : error);
    }
  }

  /** Call on navigation/document replacement. Completion means all old operations actually retired. */
  async revokeDocument(): Promise<void> {
    this.generation++;
    this.grants.clear();
    const retiring = [...this.operations];
    for (const operation of retiring) operation.controller.abort();
    await Promise.all(retiring.map(operation => operation.done));
    await this.retryCleanup();
  }
  async dispose(): Promise<void> { this.closed = true; await this.revokeDocument(); }

  private failClosed(): void {
    this.closed = true;
    this.generation++;
    this.grants.clear();
    for (const operation of this.operations) operation.controller.abort();
  }
  private async retryCleanup(): Promise<void> {
    if (this.cleanup) return this.cleanup;
    const cleanup = this.retryCleanupCore();
    this.cleanup = cleanup;
    try { await cleanup; }
    finally { if (this.cleanup === cleanup) this.cleanup = undefined; }
  }
  private async retryCleanupCore(): Promise<void> {
    // No new work is admitted after a debt; concurrent revocation joins the same finite cleanup.
    const files = await Promise.allSettled([...this.uncertainFiles].map(async file => {
      await file.close(); this.uncertainFiles.delete(file);
    }));
    const leases = await Promise.allSettled([...this.uncertainLeases].map(async id => {
      await this.options.closeIcon(id); this.uncertainLeases.delete(id);
    }));
    if (files.some(result => result.status === 'rejected') || leases.some(result => result.status === 'rejected')) throw new CleanupFailure();
  }

  private prune(): void {
    const now = this.now();
    for (const [key, grant] of this.grants) if (grant.expires <= now || grant.generation !== this.generation) this.grants.delete(key);
  }
  private checkGrant(grant: Grant): void {
    if (this.closed || grant.generation !== this.generation || grant.expires <= this.now()) throw new Refusal(410);
  }
  private checkOperation(operation: Operation, grant: Grant): void {
    if (operation.controller.signal.aborted || operation.generation !== this.generation) throw new Refusal(499);
    this.checkGrant(grant);
  }
  private acquire(operation: Operation): Promise<void> {
    if (operation.controller.signal.aborted) return Promise.reject(new Refusal(499));
    if (this.active < MAX_ACTIVE) { operation.acquired = true; this.active++; return Promise.resolve(); }
    return new Promise<void>((resolveWait, reject) => {
      const waiter: Waiter = {
        operation, resolve: resolveWait, reject,
        abort: () => {
          const index = this.queue.indexOf(waiter);
          if (index >= 0) this.queue.splice(index, 1);
          operation.controller.signal.removeEventListener('abort', waiter.abort);
          reject(new Refusal(499));
        },
      };
      this.queue.push(waiter);
      operation.controller.signal.addEventListener('abort', waiter.abort, { once: true });
    });
  }
  private advance(): void {
    while (this.active < MAX_ACTIVE && this.queue.length) {
      const waiter = this.queue.shift()!;
      waiter.operation.controller.signal.removeEventListener('abort', waiter.abort);
      if (waiter.operation.controller.signal.aborted) { waiter.reject(new Refusal(499)); continue; }
      waiter.operation.acquired = true;
      this.active++;
      waiter.resolve();
    }
  }

  private async copyVerified(grant: Grant, signal: AbortSignal): Promise<Buffer> {
    let lease: IconImageLease | undefined;
    try {
      lease = await this.options.openIcon(cloneRequest(grant.request), signal);
      signal.throwIfAborted();
      if (!validLease(lease) || lease.artifactId !== grant.image.artifactId || lease.byteLength !== grant.image.byteLength
        || lease.width !== grant.image.width || lease.height !== grant.image.height) throw new Refusal(502);
      const shard = join(this.root, lease.sha256.slice(0, 2));
      const path = join(shard, lease.sha256.slice(2));
      // No links at the trusted root, shard or leaf; realpaths must stay at the exact canonical CAS shape.
      const rootInfo = await lstat(this.root);
      if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink() || await realpath(this.root) !== this.root) throw new Refusal(502);
      const shardInfo = await lstat(shard);
      if (!shardInfo.isDirectory() || shardInfo.isSymbolicLink() || await realpath(shard) !== shard) throw new Refusal(502);
      const leafInfo = await lstat(path);
      if (!leafInfo.isFile() || leafInfo.isSymbolicLink()) throw new Refusal(502);
      signal.throwIfAborted();
      const input = await open(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0));
      try {
        const initial = await input.stat();
        const length = Number(lease.byteLength);
        if (!initial.isFile() || initial.size !== length) throw new Refusal(502);
        const output = Buffer.allocUnsafe(length);
        const hash = createHash('sha256');
        let position = 0;
        while (position < length) {
          signal.throwIfAborted();
          const result = await input.read(output, position, Math.min(CHUNK_BYTES, length - position), position);
          if (result.bytesRead === 0) throw new Refusal(502);
          hash.update(output.subarray(position, position + result.bytesRead));
          position += result.bytesRead;
        }
        const tail = await input.read(Buffer.alloc(1), 0, 1, length);
        const final = await input.stat();
        if (tail.bytesRead !== 0 || final.size !== length || !final.isFile() || hash.digest('hex') !== lease.sha256
          || !output.subarray(0, 8).equals(PNG_SIGNATURE)) throw new Refusal(502);
        signal.throwIfAborted();
        return output;
      } finally {
        try { await input.close(); }
        catch {
          this.uncertainFiles.add(input); this.failClosed(); throw new CleanupFailure();
        }
      }
    } finally {
      // Even an invalid/late open response with a syntactically valid lease must be retired.
      if (lease && validLeaseId(lease.leaseId)) {
        try { await this.options.closeIcon(lease.leaseId); }
        catch {
          this.uncertainLeases.add(lease.leaseId); this.failClosed(); throw new CleanupFailure();
        }
      }
    }
  }
}

function cloneRequest(value: IconImageRequest): IconImageRequest {
  return { context: { productId: value.context.productId, buildIdentity: value.context.buildIdentity,
    sourceRegion: value.context.sourceRegion, researchLocale: value.context.researchLocale },
    featureArtifact: value.featureArtifact, itemId: value.itemId, artifactId: value.artifactId };
}
function validLeaseId(value: unknown): value is string { return typeof value === 'string' && /^[a-f0-9]{32}$/.test(value); }
function validRequest(value: IconImageRequest): boolean {
  return !!value && !!value.context && typeof value.context.productId === 'string' && typeof value.context.buildIdentity === 'string'
    && typeof value.context.sourceRegion === 'string' && typeof value.featureArtifact === 'string' && typeof value.artifactId === 'string'
    && typeof value.itemId === 'string' && /^[a-z][a-z0-9_]{0,63}$/.test(value.context.productId)
    && HASH.test(value.context.buildIdentity) && /^[a-z]{2,8}$/.test(value.context.sourceRegion)
    && value.context.researchLocale === 'enUS' && HASH.test(value.featureArtifact) && HASH.test(value.artifactId)
    && /^(0|[1-9][0-9]{0,9})$/.test(value.itemId) && BigInt(value.itemId) <= 4294967295n;
}
function validImage(value: IconImageDescriptor): boolean {
  return !!value && typeof value.artifactId === 'string' && HASH.test(value.artifactId) && typeof value.byteLength === 'string' && /^[1-9][0-9]{0,7}$/.test(value.byteLength)
    && Number(value.byteLength) >= 8 && Number(value.byteLength) <= MAX_BYTES
    && Number.isInteger(value.width) && Number.isInteger(value.height) && value.width >= 1 && value.height >= 1
    && value.width <= 1024 && value.height <= 1024 && value.width * value.height <= 1024 * 1024;
}
function validLease(value: IconImageLease): boolean { return validImage(value) && validLeaseId(value.leaseId) && typeof value.sha256 === 'string' && HASH.test(value.sha256); }
function failure(error: unknown): Response {
  return new Response('Icon image unavailable.', { status: error instanceof Refusal ? error.status : 502,
    headers: { 'Content-Type': 'text/plain', 'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'no-store' } });
}
