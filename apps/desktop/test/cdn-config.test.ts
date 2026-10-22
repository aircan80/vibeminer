import { describe, expect, it } from 'vitest';
import type { Build, CdnConfigObservation } from '../../../contracts/generated/discovery';
import fixture from '../../../contracts/examples/discovery-page.json';
import { earlierHistoryText, historyCounts, historyLabels, latestOpenBuild, newerRegionCdnConfigs, newestGroupSize, regionCdnConfigs } from '../src/shared/cdn-config';

const base = fixture.builds[0] as Build, receipt = base.sourceReceipts[0];
const OLD = 'd'.repeat(32), NEW = 'e'.repeat(32), KEY_FIRST = '0'.repeat(32);
function entry(cdnConfig: string, advertised: string[], seen: [string, string], receipts: [string, string][]): CdnConfigObservation {
  return { cdnConfig, firstSeenAt: seen[0], lastSeenAt: seen[1], regions: receipts.map(([region]) => region), advertisedRegions: advertised,
    sourceReceipts: receipts.map(([region, retrievedAt]) => ({ ...receipt, region, role: 'versions', retrievedAt })) };
}
const build = (history: CdnConfigObservation[]): Build => ({ ...base, cdnConfig: history[0].cdnConfig, cdnConfigHistory: history });
const T1 = '2026-10-18T12:00:00Z', T2 = '2026-10-18T13:00:00Z';

describe('region-scoped CDN configuration', () => {
  it('reports the open region republishing, not the newest config overall', () => {
    const b = build([entry(NEW, ['us'], [T2, T2], [['us', T2]]), entry(OLD, ['eu'], [T1, T2], [['us', T1], ['eu', T2]])]);
    expect(newerRegionCdnConfigs(b, 'us', OLD)).toEqual([NEW]);
    // eu still advertises the open one, so an eu open sees nothing new even though NEW is the global newest.
    expect(newerRegionCdnConfigs(b, 'eu', OLD)).toEqual([]);
    expect(newerRegionCdnConfigs(b, 'us', NEW)).toEqual([]);
  });
  it('a same-check tie broken only by key order never tells a US open to switch', () => {
    // us advertises NEW and eu KEY_FIRST in the same check; KEY_FIRST sorts first by key only.
    const b = build([entry(KEY_FIRST, ['eu'], [T2, T2], [['eu', T2]]), entry(NEW, ['us'], [T2, T2], [['us', T2]])]);
    expect(b.cdnConfig).toBe(KEY_FIRST);
    expect(newerRegionCdnConfigs(b, 'us', NEW)).toEqual([]);
    expect(newerRegionCdnConfigs(b, 'eu', NEW)).toEqual([KEY_FIRST]);
  });
  it('one region advertising two configurations keeps an open on either one quiet', () => {
    const b = build([entry(KEY_FIRST, ['us'], [T2, T2], [['us', T2]]), entry(NEW, ['us'], [T2, T2], [['us', T2]])]);
    expect(newerRegionCdnConfigs(b, 'us', NEW)).toEqual([]);
    expect(newerRegionCdnConfigs(b, 'us', OLD)).toEqual([KEY_FIRST, NEW]);
  });
  it('falls back to the region\'s newest receipt when the region no longer advertises the build', () => {
    const b = build([entry(NEW, [], [T2, T2], [['us', T2]]), entry(OLD, [], [T1, T1], [['us', T1]])]);
    expect(regionCdnConfigs(b, 'us').map(e => e.cdnConfig)).toEqual([NEW]);
    expect(newerRegionCdnConfigs(b, 'us', OLD)).toEqual([NEW]);
    expect(newerRegionCdnConfigs(b, 'kr', OLD)).toEqual([]);
  });
});

describe('history labels', () => {
  it('labels a real republish Newest then Earlier', () => {
    const b = build([entry(NEW, ['us'], [T2, T2], [['us', T2]]), entry(OLD, [], [T1, T1], [['us', T1]])]);
    expect(newestGroupSize(b)).toBe(1); expect(historyLabels(b)).toEqual(['Newest', 'Earlier']);
  });
  it('labels a same-check tie Also advertised instead of implying an order', () => {
    const b = build([entry(KEY_FIRST, ['eu'], [T2, T2], [['eu', T2]]), entry(NEW, ['us'], [T2, T2], [['us', T2]]), entry(OLD, [], [T1, T1], [['us', T1]])]);
    expect(newestGroupSize(b)).toBe(2); expect(historyLabels(b)).toEqual(['Also advertised', 'Also advertised', 'Earlier']);
  });
  it('appearing later in the same last check is a real order', () => {
    const b = build([entry(NEW, ['us'], [T2, T2], [['us', T2]]), entry(OLD, ['eu'], [T1, T2], [['eu', T2]])]);
    expect(historyLabels(b)).toEqual(['Newest', 'Earlier']);
  });
});

describe('history counts shared by the inspector and Source details', () => {
  const earlier = (count: number) => Array.from({ length: count }, (_, i) => entry(i.toString(16).padStart(32, '1'), [], [T1, T1], [['us', T1]]));
  const capped = (shown: number, total: number) => ({ ...build([entry(NEW, ['us'], [T2, T2], [['us', T2]]), ...earlier(shown - 1)]), cdnConfigHistoryCount: total });
  it('says nothing in Source details while the list shows every configuration', () => {
    expect(earlierHistoryText(capped(1, 1), 'inspector')).toBe('');
    expect(earlierHistoryText(capped(2, 2), 'inspector')).toBe('1 earlier CDN configuration is kept in Source details.');
    expect(earlierHistoryText(capped(3, 3), 'inspector')).toBe('2 earlier CDN configurations are kept in Source details.');
    expect(earlierHistoryText(capped(3, 3), 'details')).toBe('');
  });
  it('states the same earlier, listed and omitted counts in both places', () => {
    expect(historyCounts(capped(16, 19))).toEqual({ tied: 1, total: 19, earlier: 18, shownEarlier: 15, omitted: 3 });
    expect(earlierHistoryText(capped(16, 19), 'inspector')).toBe('18 earlier CDN configurations are kept. Source details lists the newest 15; the other 3 stay in the catalog with their receipts.');
    expect(earlierHistoryText(capped(16, 19), 'details')).toBe('18 earlier CDN configurations are kept. This list shows the newest 15; the other 3 stay in the catalog with their receipts.');
    expect(earlierHistoryText(capped(2, 3), 'details')).toBe('2 earlier CDN configurations are kept. This list shows the newest one; the other one stays in the catalog with its receipts.');
    expect(earlierHistoryText(capped(1, 2), 'inspector')).toBe('1 earlier CDN configuration is kept. Source details lists none of them; it stays in the catalog with its receipts.');
  });
});

describe('the open build after a refresh', () => {
  const opened = build([entry(OLD, ['us'], [T1, T1], [['us', T1]])]);
  const refreshed = { ...build([entry(NEW, ['us'], [T2, T2], [['us', T2]]), entry(OLD, [], [T1, T1], [['us', T1]])]), lastSeenAt: T2 };
  it('uses the catalog projection of the same exact build', () => {
    expect(latestOpenBuild({ ...opened, lastSeenAt: T1 }, refreshed)).toBe(refreshed);
    expect(newerRegionCdnConfigs(latestOpenBuild({ ...opened, lastSeenAt: T1 }, refreshed), 'us', OLD)).toEqual([NEW]);
  });
  it('keeps the snapshot for another build, another product or an older catalog read', () => {
    const open = { ...opened, lastSeenAt: T2 };
    expect(latestOpenBuild(open, null)).toBe(open);
    expect(latestOpenBuild(open, { ...refreshed, id: 'f'.repeat(64) })).toBe(open);
    expect(latestOpenBuild(open, { ...refreshed, productId: 'wowt' })).toBe(open);
    expect(latestOpenBuild(open, { ...refreshed, lastSeenAt: T1 })).toBe(open);
  });
});
