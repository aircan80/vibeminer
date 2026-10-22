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

/**
 * The catalog's projection of the open build when it is that exact build and no older than the open snapshot;
 * otherwise the snapshot's own. A refresh only moves the catalog forward, so this is the newer history.
 */
export function latestOpenBuild(open: Build, catalog: Build | null | undefined): Build {
  if (!catalog || catalog.productId !== open.productId || catalog.id !== open.id) return open;
  return Date.parse(catalog.lastSeenAt) >= Date.parse(open.lastSeenAt) ? catalog : open;
}

/** The history counts the inspector and Source details both state, so the two can never disagree. */
export function historyCounts(build: Build) {
  const tied = newestGroupSize(build), shown = build.cdnConfigHistory.length;
  const total = Math.max(build.cdnConfigHistoryCount, shown);
  return { tied, total, earlier: total - tied, shownEarlier: shown - tied, omitted: total - shown };
}

/**
 * One sentence about the configurations before the newest check. Empty when there are none, and in Source details
 * when the list already shows every one.
 */
export function earlierHistoryText(build: Build, where: 'inspector' | 'details'): string {
  const { earlier, shownEarlier, omitted } = historyCounts(build);
  if (earlier < 1 || where === 'details' && omitted < 1) return '';
  const kept = `${earlier} earlier CDN configuration${earlier === 1 ? ' is' : 's are'} kept`;
  if (omitted < 1) return `${kept} in Source details.`;
  const list = where === 'inspector' ? 'Source details lists' : 'This list shows';
  const rest = `${omitted === 1 ? ' stays' : ' stay'} in the catalog with ${omitted === 1 ? 'its receipts' : 'their receipts'}.`;
  if (shownEarlier < 1) return `${kept}. ${list} none of them; ${omitted === 1 ? 'it' : 'they'}${rest}`;
  return `${kept}. ${list} the newest ${shownEarlier === 1 ? 'one' : shownEarlier}; the other ${omitted === 1 ? 'one' : omitted}${rest}`;
}
