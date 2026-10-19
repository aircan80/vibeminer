import { describe, expect, it } from 'vitest';
import type { Build, CdnConfigObservation } from '../../../contracts/generated/discovery';
import fixture from '../../../contracts/examples/discovery-page.json';
import { historyLabels, newerRegionCdnConfigs, newestGroupSize, regionCdnConfigs } from '../src/shared/cdn-config';

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
