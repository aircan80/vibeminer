// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mkdtemp, mkdir, writeFile, rm, symlink, rename, open as openFile } from 'node:fs/promises';
vi.mock('node:fs/promises', async importOriginal => {
  const actual = await importOriginal<typeof import('node:fs/promises')>();
  return { ...actual, open: vi.fn(actual.open) };
});
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { deflateSync } from 'node:zlib';
import { IconImageProtocol, type IconImageLease, type IconImageProtocolOptions, type IconImageRequest } from '../src/main/icon-image-protocol';

// Original one-pixel PNG fixture, optionally with an authored ancillary padding chunk.
function png(padding = 0): Buffer {
  const chunk = (type: string, bytes: Buffer) => {
    const name = Buffer.from(type); const body = Buffer.concat([name, bytes]); let crc = 0xffffffff;
    for (const byte of body) { crc ^= byte; for (let i = 0; i < 8; i++) crc = crc & 1 ? (crc >>> 1) ^ 0xedb88320 : crc >>> 1; }
    const head = Buffer.alloc(4); head.writeUInt32BE(bytes.length);
    const tail = Buffer.alloc(4); tail.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
    return Buffer.concat([head, body, tail]);
  };
  const header = Buffer.alloc(13); header.writeUInt32BE(1, 0); header.writeUInt32BE(1, 4); header[8] = 8; header[9] = 6;
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', header),
    ...(padding ? [chunk('vpAD', Buffer.alloc(padding, 123))] : []),
    chunk('IDAT', deflateSync(Buffer.from([0, 255, 0, 128, 255]))), chunk('IEND', Buffer.alloc(0))]);
}
const artifactId = 'a'.repeat(64);
const request: IconImageRequest = {
  context: { productId: 'wow', buildIdentity: 'b'.repeat(64), sourceRegion: 'us', researchLocale: 'enUS' },
  featureArtifact: 'c'.repeat(64), itemId: '123', artifactId,
};
const protocols: IconImageProtocol[] = [];
const directories: string[] = [];
async function setup(overrides: Partial<IconImageProtocolOptions> = {}, data = png()) {
  const directory = await mkdtemp(join(tmpdir(), 'vibeminer-icon-')); directories.push(directory);
  const sha256 = createHash('sha256').update(data).digest('hex');
  const root = join(directory, 'objects-v1'); const shard = join(root, sha256.slice(0, 2)); const path = join(shard, sha256.slice(2));
  await mkdir(shard, { recursive: true }); await writeFile(path, data);
  const image = { artifactId, width: 1, height: 1, byteLength: String(data.length) };
  const lease: IconImageLease = { ...image, leaseId: 'f'.repeat(32), sha256 };
  const openIcon = vi.fn(async () => ({ ...lease })); const closeIcon = vi.fn(async () => {});
  const protocol = new IconImageProtocol({ dataDirectory: directory, openIcon, closeIcon, ...overrides }); protocols.push(protocol);
  const url = protocol.issue(request, image);
  return { directory, root, shard, path, sha256, data, image, lease, openIcon, closeIcon, protocol, url };
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
async function settle() { await new Promise<void>(resolve => setImmediate(resolve)); }
afterEach(async () => {
  vi.useRealTimers();
  vi.clearAllMocks();
  await Promise.all(protocols.splice(0).map(protocol => protocol.dispose()));
  await Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

describe('main-only verified icon image capabilities', () => {
  it('delivers exact authored PNG with restrictive headers and closes the engine lease before serving', async () => {
    const f = await setup(); const response = await f.protocol.handle(new Request(f.url));
    expect(response.status).toBe(200); expect(f.openIcon).toHaveBeenCalledWith(request, expect.any(AbortSignal));
    expect(f.closeIcon).toHaveBeenCalledExactlyOnceWith(f.lease.leaseId);
    expect(response.headers.get('content-type')).toBe('image/png');
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(response.headers.get('x-content-type-options')).toBe('nosniff');
    expect(f.protocol.stats.active).toBe(1);
    expect(Buffer.from(await response.arrayBuffer())).toEqual(f.data);
    expect(f.protocol.stats).toMatchObject({ active: 0, admitted: 0, queued: 0 });
  });

  it('rejects forged and decorated URLs and disallowed methods without opening anything', async () => {
    const f = await setup();
    for (const url of [f.url + '?a=1', f.url + '#x', f.url.replace('icon/', 'icon:99/'),
      f.url.replace('icon/', 'user@icon/'), f.url.replace('icon/', 'other/'),
      'vibeminer-image://icon/' + '0'.repeat(32), f.url.replace('/' + f.url.split('/').at(-1), '/%61')]) {
      const forged = new Request(f.url); Object.defineProperty(forged, 'url', { value: url });
      expect((await f.protocol.handle(forged)).status).toBe(404);
    }
    expect((await f.protocol.handle(new Request(f.url, { method: 'POST' }))).status).toBe(405);
    expect(f.openIcon).not.toHaveBeenCalled();
  });

  it('refuses an integration policy failure without touching the engine', async () => {
    const f = await setup({ authorizeRequest: () => false });
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(403);
    expect(f.openIcon).not.toHaveBeenCalled();
  });

  it('expires grants, caps them at 200, and does not retain mutable caller identities', async () => {
    let now = 1; const f = await setup({ now: () => now });
    const copy = structuredClone(request); const mutableImage = { ...f.image };
    const url = f.protocol.issue(copy, mutableImage); copy.itemId = '999'; copy.context.productId = 'wow_beta'; mutableImage.width = 4;
    const response = await f.protocol.handle(new Request(url)); await response.body!.cancel();
    expect(f.openIcon).toHaveBeenCalledWith(request, expect.any(AbortSignal));
    for (let i = 2; i < 200; i++) f.protocol.issue(request, f.image);
    expect(() => f.protocol.issue(request, f.image)).toThrow();
    now += 120_000;
    expect((await f.protocol.handle(new Request(url))).status).toBe(404);
    expect(f.protocol.stats.grants).toBe(0); expect(() => f.protocol.issue(request, f.image)).not.toThrow();
  });

  it.each([
    { byteLength: '01' }, { byteLength: '5242881' }, { byteLength: '0' }, { width: 1025 }, { height: 0 },
    { width: 1.5 }, { artifactId: 'A'.repeat(64) },
  ])('rejects invalid grant metadata %j', async invalid => {
    const f = await setup(); expect(() => f.protocol.issue(request, { ...f.image, ...invalid })).toThrow();
    expect(f.openIcon).not.toHaveBeenCalled();
  });

  it('refuses invalid exact identity and mismatched granted image before engine work', async () => {
    const f = await setup();
    for (const invalid of [{ ...request, itemId: '4294967296' }, { ...request, itemId: '0123' },
      { ...request, featureArtifact: '../feature' }, { ...request, context: { ...request.context, researchLocale: 'enGB' } }]) {
      expect(() => f.protocol.issue(invalid as IconImageRequest, f.image)).toThrow();
    }
    expect(() => f.protocol.issue(request, { ...f.image, artifactId: 'd'.repeat(64) })).toThrow();
    expect(f.openIcon).not.toHaveBeenCalled();
  });

  it.each([
    { sha256: '../secret' }, { artifactId: 'd'.repeat(64) }, { byteLength: '01' }, { byteLength: '99' },
    { width: 0 }, { width: 2 }, { leaseId: '../lease' },
  ])('refuses malformed or mismatched engine metadata %j without leaking paths', async invalid => {
    const f = await setup(); f.openIcon.mockResolvedValue({ ...f.lease, ...invalid });
    const response = await f.protocol.handle(new Request(f.url)); expect(response.status).toBe(502);
    expect(await response.text()).toBe('Icon image unavailable.'); expect(f.protocol.stats.admitted).toBe(0);
    if (!('leaseId' in invalid)) expect(f.closeIcon).toHaveBeenCalledExactlyOnceWith(f.lease.leaseId);
  });

  it.each(['short', 'long', 'same-length-corrupt', 'not-png'] as const)('refuses %s bytes before delivery', async kind => {
    const f = await setup();
    if (kind === 'short') await writeFile(f.path, f.data.subarray(0, -1));
    if (kind === 'long') await writeFile(f.path, Buffer.concat([f.data, Buffer.from([0])]));
    if (kind === 'same-length-corrupt') { const bytes = Buffer.from(f.data); bytes[20] ^= 1; await writeFile(f.path, bytes); }
    if (kind === 'not-png') {
      const data = Buffer.alloc(f.data.length, 1); const hash = createHash('sha256').update(data).digest('hex');
      const shard = join(f.root, hash.slice(0, 2)); await mkdir(shard, { recursive: true }); await writeFile(join(shard, hash.slice(2)), data);
      f.openIcon.mockResolvedValue({ ...f.lease, sha256: hash });
    }
    const response = await f.protocol.handle(new Request(f.url)); expect(response.status).toBe(502);
    expect(await response.text()).toBe('Icon image unavailable.'); expect(f.closeIcon).toHaveBeenCalledTimes(1);
  });

  it.each(['root', 'shard', 'leaf'] as const)('refuses %s symlinks', async kind => {
    const f = await setup(); const target = kind === 'root' ? f.root : kind === 'shard' ? f.shard : f.path;
    const relocated = target + '-actual'; await rename(target, relocated); await symlink(relocated, target);
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(502);
    expect(f.closeIcon).toHaveBeenCalledTimes(1);
  });

  it('holds four snapshot permits through body drainage and bounds all admitted requests at 64', async () => {
    const f = await setup(); const first = await Promise.all(Array.from({ length: 4 }, () => f.protocol.handle(new Request(f.url))));
    expect(f.protocol.stats).toMatchObject({ active: 4, admitted: 4, queued: 0 });
    const queued = Array.from({ length: 60 }, () => f.protocol.handle(new Request(f.url)));
    expect(f.protocol.stats).toMatchObject({ active: 4, admitted: 64, queued: 60 });
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(429); expect(f.openIcon).toHaveBeenCalledTimes(4);
    await first[0].body!.cancel();
    const next = await queued[0]; expect(next.status).toBe(200); expect(f.protocol.stats.active).toBe(4);
    await f.protocol.revokeDocument();
    const remaining = await Promise.all(queued.slice(1)); expect(remaining.every(response => response.status === 499)).toBe(true);
    expect(f.protocol.stats).toMatchObject({ active: 0, admitted: 0, queued: 0, grants: 0 });
  });

  it('removes a canceled queued waiter without canceling active independent consumers', async () => {
    const f = await setup(); const active = await Promise.all(Array.from({ length: 4 }, () => f.protocol.handle(new Request(f.url))));
    const cancel = new AbortController(); const waiting = f.protocol.handle(new Request(f.url, { signal: cancel.signal }));
    cancel.abort(); expect((await waiting).status).toBe(499);
    expect(f.protocol.stats).toMatchObject({ active: 4, admitted: 4, queued: 0 });
    expect(Buffer.from(await active[0].arrayBuffer())).toEqual(f.data);
    await Promise.all(active.slice(1).map(response => response.body!.cancel()));
  });

  it('awaits a late successful engine open and closes its lease on canceled requests', async () => {
    const opening = deferred<IconImageLease>(); const f = await setup({ openIcon: () => opening.promise });
    const cancel = new AbortController(); const pending = f.protocol.handle(new Request(f.url, { signal: cancel.signal }));
    await settle(); cancel.abort();
    let retired = false; const revoking = f.protocol.revokeDocument().then(() => { retired = true; }); await settle();
    expect(retired).toBe(false); expect(f.protocol.stats.admitted).toBe(1);
    opening.resolve(f.lease); expect((await pending).status).toBe(499); await revoking;
    expect(f.closeIcon).toHaveBeenCalledExactlyOnceWith(f.lease.leaseId); expect(f.protocol.stats.admitted).toBe(0);
  });


  it('awaits an in-progress descriptor read and closes file plus engine lease before canceled copy retirement', async () => {
    const reading = deferred<void>(); const release = deferred<void>(); const closed = vi.fn();
    const actual = await vi.importActual<typeof import('node:fs/promises')>('node:fs/promises');
    vi.mocked(openFile).mockImplementationOnce(async (...args: Parameters<typeof actual.open>) => {
      const input = await actual.open(...args); const originalRead = input.read.bind(input); const originalClose = input.close.bind(input);
      // Interpose a real descriptor operation, rather than replacing the storage verification with a fake.
      input.read = (async (...readArgs: unknown[]) => {
        reading.resolve(); await release.promise;
        return (originalRead as (...args: unknown[]) => Promise<unknown>)(...readArgs);
      }) as typeof input.read;
      input.close = async () => { await originalClose(); closed(); };
      return input;
    });
    const f = await setup({}, png(150_000)); const cancel = new AbortController();
    const pending = f.protocol.handle(new Request(f.url, { signal: cancel.signal }));
    await reading.promise; cancel.abort();
    let retired = false; const revoking = f.protocol.revokeDocument().then(() => { retired = true; }); await settle();
    expect(retired).toBe(false); expect(f.closeIcon).not.toHaveBeenCalled(); expect(closed).not.toHaveBeenCalled();
    release.resolve(); expect((await pending).status).toBe(499); await revoking;
    expect(closed).toHaveBeenCalledTimes(1); expect(f.closeIcon).toHaveBeenCalledTimes(1); expect(f.protocol.stats.admitted).toBe(0);
  });

  it('rechecks grant expiry and generation after a late engine open before returning headers', async () => {
    let now = 1; const opening = deferred<IconImageLease>();
    const f = await setup({ now: () => now, openIcon: () => opening.promise });
    const pending = f.protocol.handle(new Request(f.url)); await settle(); now += 120_000; opening.resolve(f.lease);
    expect((await pending).status).toBe(410); expect(f.closeIcon).toHaveBeenCalledTimes(1);
  });

  it('cancels a streaming body, releases its buffer permit and rejects stale URLs after revocation', async () => {
    const f = await setup({}, png(200_000)); const cancel = new AbortController();
    const response = await f.protocol.handle(new Request(f.url, { signal: cancel.signal })); const reader = response.body!.getReader();
    const first = await reader.read(); expect(first.value!.length).toBe(65536); expect(f.protocol.stats.active).toBe(1);
    cancel.abort(); await expect(reader.read()).rejects.toThrow('Icon image request ended.');
    expect(f.protocol.stats.active).toBe(0); await f.protocol.revokeDocument();
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(404);
  });

  it('bounds each response pull to 64 KiB for the largest permitted copied PNG', async () => {
    const maximum = 5 * 1024 * 1024; const data = png(maximum - png().length - 12);
    expect(data.length).toBe(maximum); const f = await setup({}, data);
    const response = await f.protocol.handle(new Request(f.url)); expect(response.status).toBe(200);
    const reader = response.body!.getReader(); const hash = createHash('sha256'); let total = 0;
    for (;;) {
      const part = await reader.read(); if (part.done) break;
      expect(part.value.length).toBeLessThanOrEqual(65536); hash.update(part.value); total += part.value.length;
    }
    expect(total).toBe(maximum); expect(hash.digest('hex')).toBe(f.sha256); expect(f.protocol.stats.admitted).toBe(0);
  });

  it('delivers a verified snapshot even if its CAS path is replaced before body consumption', async () => {
    const f = await setup({}, png(150_000)); const response = await f.protocol.handle(new Request(f.url));
    await writeFile(f.path, Buffer.alloc(f.data.length, 0));
    expect(Buffer.from(await response.arrayBuffer())).toEqual(f.data);
  });

  it('expires abandoned bodies without keeping buffers or request admission forever', async () => {
    const f = await setup(); const response = await f.protocol.handle(new Request(f.url));
    vi.useFakeTimers();
    // Deadline was created before fake timers; create the measured second request under controlled time.
    const second = await f.protocol.handle(new Request(f.url)); await vi.advanceTimersByTimeAsync(10_000);
    expect(f.protocol.stats.active).toBe(1); await expect(second.arrayBuffer()).rejects.toThrow();
    await response.body!.cancel();
  });

  it('fails closed on uncertain descriptor cleanup and retries only that bounded handle during disposal', async () => {
    const actual = await vi.importActual<typeof import('node:fs/promises')>('node:fs/promises'); const closed = vi.fn();
    vi.mocked(openFile).mockImplementationOnce(async (...args: Parameters<typeof actual.open>) => {
      const input = await actual.open(...args); const originalClose = input.close.bind(input); let first = true;
      input.close = async () => { if (first) { first = false; throw new Error('Authored close failure'); } await originalClose(); closed(); };
      return input;
    });
    const f = await setup(); expect((await f.protocol.handle(new Request(f.url))).status).toBe(502);
    expect(f.closeIcon).toHaveBeenCalledTimes(1); expect(closed).not.toHaveBeenCalled();
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(410);
    expect(() => f.protocol.issue(request, f.image)).toThrow(); expect(f.openIcon).toHaveBeenCalledTimes(1);
    await f.protocol.dispose(); expect(closed).toHaveBeenCalledTimes(1);
  });

  it('rejects unsupported range requests instead of silently changing byte semantics', async () => {
    const f = await setup(); expect((await f.protocol.handle(new Request(f.url, { headers: { Range: 'bytes=0-3' } }))).status).toBe(416);
    expect(f.openIcon).not.toHaveBeenCalled();
  });

  it('does not serve bytes when the engine lease close fails', async () => {
    const closeIcon = vi.fn(async () => {}).mockRejectedValueOnce(new Error('/private/engine/path'));
    const f = await setup({ closeIcon });
    const response = await f.protocol.handle(new Request(f.url)); expect(response.status).toBe(502);
    expect(await response.text()).toBe('Icon image unavailable.'); expect(f.protocol.stats.admitted).toBe(0);
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(410);
    expect(() => f.protocol.issue(request, f.image)).toThrow();
    await f.protocol.dispose(); expect(closeIcon).toHaveBeenCalledTimes(2);
  });

  it('coalesces uncertain engine-lease cleanup across concurrent revocation and reports unresolved debt', async () => {
    const retrying = deferred<void>(); const release = deferred<void>(); let calls = 0;
    const closeIcon = vi.fn(async () => {
      calls++;
      if (calls === 1) throw new Error('Authored uncertain close');
      if (calls === 2) { retrying.resolve(); await release.promise; throw new Error('Still uncertain'); }
    });
    const f = await setup({ closeIcon }); expect((await f.protocol.handle(new Request(f.url))).status).toBe(502);
    const a = f.protocol.revokeDocument(); const b = f.protocol.dispose();
    const results = Promise.allSettled([a, b]); await retrying.promise;
    expect(closeIcon).toHaveBeenCalledTimes(2); release.resolve();
    expect((await results).every(result => result.status === 'rejected')).toBe(true);
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(410);
    await f.protocol.dispose(); expect(closeIcon).toHaveBeenCalledTimes(3);
  });

  it('uses monotonic expiry unaffected by system clock changes', async () => {
    const clock = vi.spyOn(Date, 'now').mockReturnValue(9_000_000_000_000);
    try {
      const f = await setup(); clock.mockReturnValue(0);
      const response = await f.protocol.handle(new Request(f.url)); expect(response.status).toBe(200); await response.body!.cancel();
      clock.mockReturnValue(99_000_000_000_000);
      const next = await f.protocol.handle(new Request(f.url)); expect(next.status).toBe(200); await next.body!.cancel();
    } finally { clock.mockRestore(); }
  });

  it('refuses new grants and requests after shutdown', async () => {
    const f = await setup(); await f.protocol.dispose();
    expect(() => f.protocol.issue(request, f.image)).toThrow();
    expect((await f.protocol.handle(new Request(f.url))).status).toBe(410); expect(f.openIcon).not.toHaveBeenCalled();
  });
});
