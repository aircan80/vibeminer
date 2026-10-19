import type { Build, CdnConfigObservation } from '../../../../contracts/generated/discovery';

/**
 * The CDN configurations one source region advertises now: the entries advertised there at the last check,
 * or, when the region no longer advertises the build, the entries with that region's newest receipt.
 */
export function regionCdnConfigs(build: Build, region: string): CdnConfigObservation[] {
  const advertised = build.cdnConfigHistory.filter(entry => entry.advertisedRegions.includes(region));
  if (advertised.length) return advertised;
  const seen = build.cdnConfigHistory.flatMap(entry => {
    const at = Date.parse(entry.sourceReceipts.find(receipt => receipt.region === region && receipt.role === 'versions')?.retrievedAt ?? '');
    return Number.isFinite(at) ? [{ entry, at }] : [];
  });
  const latest = Math.max(...seen.map(item => item.at));
  return seen.filter(item => item.at === latest).map(item => item.entry);
}

/**
 * The CDN configurations the open build's own source region now advertises instead of the one it was opened
 * with. Empty when the region still advertises the open one (also within a same-check tie) or nothing is known.
 * Never compares against another region's newer configuration.
 */
export function newerRegionCdnConfigs(build: Build, region: string, openCdnConfig: string): string[] {
  const current = regionCdnConfigs(build, region);
  if (current.some(entry => entry.cdnConfig === openCdnConfig)) return [];
  return current.map(entry => entry.cdnConfig);
}

/** Entries seen first and last in the same checks are ordered only by key; that order means nothing. */
export const sameCheck = (a: CdnConfigObservation, b: CdnConfigObservation) =>
  a.firstSeenAt === b.firstSeenAt && a.lastSeenAt === b.lastSeenAt;

/** How many leading history entries tie with the top one. 1 means the top entry really is the newest. */
export function newestGroupSize(build: Build): number {
  const [top] = build.cdnConfigHistory;
  if (!top) return 0;
  let size = 1;
  while (size < build.cdnConfigHistory.length && sameCheck(top, build.cdnConfigHistory[size])) size++;
  return size;
}

/** Source details label per entry: a same-check tie at the top is not presented as an order. */
export function historyLabels(build: Build): ('Newest' | 'Also advertised' | 'Earlier')[] {
  const top = newestGroupSize(build);
  return build.cdnConfigHistory.map((_, i) => i >= top ? 'Earlier' : top > 1 ? 'Also advertised' : 'Newest');
}
