import type { SettingsSnapshot, SettingsSetParams, StorageMeasurement } from '../../../../contracts/generated/discovery';
export type { SettingsSnapshot, SettingsSetParams, StorageMeasurement };
const decimal = (x: unknown): x is string => typeof x === 'string' && /^(0|[1-9][0-9]{0,18})$/.test(x) && BigInt(x) <= 9223372036854775807n;
const instant = (x: unknown) => typeof x === 'string' && !Number.isNaN(Date.parse(x));
/** The only change the renderer can ask for: one known switch, true or false. */
export function validSettingsSet(x: unknown): x is SettingsSetParams {
  if (!x || typeof x !== 'object' || Array.isArray(x)) return false;
  const p = x as SettingsSetParams;
  return Object.keys(p).length === 2 && p.name === 'communityBuildHistory' && typeof p.value === 'boolean';
}
export const validEmpty = (x: unknown) => !!x && typeof x === 'object' && !Array.isArray(x) && Object.keys(x).length === 0;
/** Cross-field rules the schema can't express: a problem belongs to an unreadable value, and an unreadable value is the default. */
export function settingsSnapshotMatches(s: SettingsSnapshot): boolean {
  const c = s.communityBuildHistory, l = s.downloadLimits;
  if ((s.file === 'unreadable' || s.file === 'newer') !== (s.problem !== null)) return false;
  if ((c.source === 'unreadable') !== (c.problem !== null) || c.source !== 'saved' && c.value !== c.defaultValue) return false;
  if ((s.file === 'unreadable' || s.file === 'newer') && c.source !== 'unreadable' || s.file === 'absent' && (c.source !== 'default' || s.lastChangedAt !== null)) return false;
  if (s.lastChangedAt !== null && !instant(s.lastChangedAt)) return false;
  return l.backgroundDownloads <= l.parallelDownloads && (l.bandwidthCapBytesPerSecond === null || decimal(l.bandwidthCapBytesPerSecond));
}
/** A committed change returns the value that was asked for, saved in a readable file. */
export const settingsSetMatches = (s: SettingsSnapshot, p: SettingsSetParams) => settingsSnapshotMatches(s) && s.file === 'read' && s.communityBuildHistory.source === 'saved' && s.communityBuildHistory.value === p.value;
export function storageMeasurementMatches(m: StorageMeasurement): boolean {
  return [m.bytes, m.files, m.skipped].every(decimal) && instant(m.startedAt) && instant(m.measuredAt) && Date.parse(m.measuredAt) >= Date.parse(m.startedAt);
}
