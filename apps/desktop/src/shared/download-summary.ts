import type { BuildDownloadSummary, BuildStoredCoverage, DownloadSummaries, DownloadSummariesParams } from '../../../../contracts/generated/discovery';

const unsigned = /^(0|[1-9][0-9]{0,18})$/;
const hash = /^[0-9a-f]{64}$/;
const product = /^[a-z][a-z0-9_-]{0,63}$/;
const region = /^[a-z]{2,8}$/;
const decimal = (value: unknown): value is string => typeof value === 'string' && unsigned.test(value) && BigInt(value) <= 9223372036854775807n;
const instant = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value));
export const MAX_SUMMARY_BUILDS = 100;
export const summaryKey = (productId: string, buildIdentity: string) => `${productId}:${buildIdentity}`;

/** One to 100 distinct exact builds; the engine resolves regions and ledgers itself. */
export function validSummariesParams(value: unknown): value is DownloadSummariesParams {
  if (!value || typeof value !== 'object' || Object.keys(value).length !== 1) return false;
  const builds = (value as DownloadSummariesParams).builds;
  if (!Array.isArray(builds) || builds.length < 1 || builds.length > MAX_SUMMARY_BUILDS) return false;
  const keys = new Set<string>();
  for (const build of builds) {
    if (!build || typeof build !== 'object' || Object.keys(build).length !== 2 || typeof build.productId !== 'string' || !product.test(build.productId) ||
      typeof build.buildIdentity !== 'string' || !hash.test(build.buildIdentity)) return false;
    keys.add(summaryKey(build.productId, build.buildIdentity));
  }
  return keys.size === builds.length;
}

/** Internally consistent stored figure: never more than the scope, and all objects only with all bytes. */
export function storedCoverageValid(d: BuildStoredCoverage): boolean {
  const numbers = [d.objectCount, d.totalEncodedBytes, d.storedObjects, d.storedBytes, d.presence.observedObjects, d.presence.missingObjects, d.presence.corruptObjects, d.unresolvableRootContent];
  if (!hash.test(d.ledgerId) || d.scope !== 'downloadManifestAllVariants' && d.scope !== 'downloadManifestAndRootAllVariants' || !numbers.every(decimal) || !instant(d.updatedAt)) return false;
  // Only a measured file list widens the scope or reports files it could not resolve; until then the figure is not current.
  if ((d.scopeState === 'measured') !== (d.scope === 'downloadManifestAndRootAllVariants')) return false;
  if (d.scope === 'downloadManifestAllVariants' && d.unresolvableRootContent !== '0') return false;
  if (d.scopeState === 'measuring' && d.countState === 'current' || d.scopeState === 'measured' && d.scopeProblem !== null) return false;
  if (d.scopeProblem && (!d.scopeProblem.code.trim() || !d.scopeProblem.message.trim())) return false;
  if (d.changesPending && d.countState === 'unavailable') return false;
  const count = BigInt(d.objectCount), total = BigInt(d.totalEncodedBytes), stored = BigInt(d.storedObjects), bytes = BigInt(d.storedBytes);
  if (stored > count || bytes > total || stored === 0n && bytes !== 0n || stored === count && bytes !== total) return false;
  const p = d.presence;
  if (BigInt(p.observedObjects) > count || BigInt(p.missingObjects) + BigInt(p.corruptObjects) > BigInt(p.observedObjects)) return false;
  if (p.state === 'assessed' ? !instant(p.assessedAt) : p.assessedAt !== null) return false;
  if ((d.countState === 'unavailable') !== (d.countProblem !== null)) return false;
  if (d.countProblem && (!d.countProblem.code.trim() || !d.countProblem.message.trim())) return false;
  if (!d.sourceRegions.length || !d.sourceRegions.every(r => region.test(r)) || new Set(d.sourceRegions).size !== d.sourceRegions.length) return false;
  // A running Download all pass belongs to an unpaused Download all intent.
  return !d.running || d.downloadAll;
}

export function summaryValid(s: BuildDownloadSummary): boolean {
  return s.state === 'measured' ? !!s.download && storedCoverageValid(s.download) : s.download === null;
}

/** A reply answers exactly the requested builds, in request order. */
export function summariesMatch(value: DownloadSummaries, params: DownloadSummariesParams): boolean {
  return value.summaries.length === params.builds.length && value.summaries.every((s, i) =>
    s.productId === params.builds[i].productId && s.buildIdentity === params.builds[i].buildIdentity && summaryValid(s));
}

/**
 * Share of the build's whole download manifest (every locale and platform variant) stored on this machine.
 * Null when the figure is not current. It rounds down and never reaches 100% before every object is stored.
 */
export function storedPercent(d: BuildStoredCoverage | null | undefined): string | null {
  if (!d || d.countState !== 'current') return null;
  const scope = BigInt(d.totalEncodedBytes), stored = BigInt(d.storedBytes), complete = d.storedObjects === d.objectCount;
  if (scope <= 0n) return null;
  if (complete) return '100%';
  const tenths = Number(stored * 1000n / scope);
  return tenths >= 990 ? '99%' : tenths >= 10 ? `${Math.floor(tenths / 10)}%` : tenths > 0 ? `${(tenths / 10).toFixed(1)}%` : stored > 0n ? '<0.1%' : '0%';
}
