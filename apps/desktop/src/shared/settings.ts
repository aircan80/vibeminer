import type { SettingsSnapshot, SettingsSetParams, StorageMeasurement, PreparedDataList, PreparedBuild, PreparedDataFreed, PreparedDataFreeParams, PreparedFileCatalog } from '../../../../contracts/generated/discovery';
export type { SettingsSnapshot, SettingsSetParams, StorageMeasurement, PreparedDataList, PreparedBuild, PreparedDataFreed, PreparedDataFreeParams, PreparedFileCatalog };
/** The switches this version knows. */
export const SETTING_SWITCHES = ['communityBuildHistory', 'fetchTableDefinitions'] as const;
export type SettingSwitch = typeof SETTING_SWITCHES[number];
const decimal = (x: unknown): x is string => typeof x === 'string' && /^(0|[1-9][0-9]{0,18})$/.test(x) && BigInt(x) <= 9223372036854775807n;
const instant = (x: unknown) => typeof x === 'string' && !Number.isNaN(Date.parse(x));
/** The only change the renderer can ask for: one known switch, true or false. */
export function validSettingsSet(x: unknown): x is SettingsSetParams {
  if (!x || typeof x !== 'object' || Array.isArray(x)) return false;
  const p = x as SettingsSetParams;
  return Object.keys(p).length === 2 && (SETTING_SWITCHES as readonly unknown[]).includes(p.name) && typeof p.value === 'boolean';
}
export const validEmpty = (x: unknown) => !!x && typeof x === 'object' && !Array.isArray(x) && Object.keys(x).length === 0;
/** Cross-field rules the schema can't express: a problem belongs to an unreadable value, and an unreadable value is the default. */
export function settingsSnapshotMatches(s: SettingsSnapshot): boolean {
  const l = s.downloadLimits;
  if ((s.file === 'unreadable' || s.file === 'newer') !== (s.problem !== null)) return false;
  for (const name of SETTING_SWITCHES) {
    const c = s[name];
    // A default reads as the default; an unreadable value is never used and reads as Off (D081).
    if ((c.source === 'unreadable') !== (c.problem !== null) || c.source === 'default' && c.value !== c.defaultValue || c.source === 'unreadable' && c.value) return false;
    if ((s.file === 'unreadable' || s.file === 'newer') && c.source !== 'unreadable' || s.file === 'absent' && c.source !== 'default') return false;
  }
  if (s.file === 'absent' && s.lastChangedAt !== null) return false;
  if (s.lastChangedAt !== null && !instant(s.lastChangedAt)) return false;
  return l.backgroundDownloads <= l.parallelDownloads && (l.bandwidthCapBytesPerSecond === null || decimal(l.bandwidthCapBytesPerSecond));
}
/** A committed change returns the value that was asked for, saved in a readable file. */
export const settingsSetMatches = (s: SettingsSnapshot, p: SettingsSetParams) => settingsSnapshotMatches(s) && s.file === 'read' && s[p.name].source === 'saved' && s[p.name].value === p.value;
export function storageMeasurementMatches(m: StorageMeasurement): boolean {
  return [m.bytes, m.files, m.skipped].every(decimal) && instant(m.startedAt) && instant(m.measuredAt) && Date.parse(m.measuredAt) >= Date.parse(m.startedAt);
}
// Prepared data per opened build (D085, contracts/settings-v1.md#prepared-data-per-build).
/** The only free the renderer can ask for: one exact build, named by product and identity. */
export function validPreparedDataFree(x: unknown): x is PreparedDataFreeParams {
  if (!x || typeof x !== 'object' || Array.isArray(x)) return false;
  const p = x as Record<string, unknown>;
  return Object.keys(p).length === 2 && typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId)
    && typeof p.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(p.buildIdentity);
}
/** No files means no bytes, and a catalog that was never queued has nothing on disk. */
const fileCatalogMeasured = (c: PreparedFileCatalog) => decimal(c.bytes) && (c.files > 0 || c.bytes === '0') && (c.state !== 'none' || c.files === 0);
function preparedBuildMatches(b: PreparedBuild): boolean {
  const t = b.tables, d = b.downloads;
  if (!fileCatalogMeasured(b.fileCatalog) || !instant(b.lastOpenedAt)) return false;
  if (![t.bytes, t.sharedBytes].every(decimal) || BigInt(t.sharedBytes) > BigInt(t.bytes) || (t.count === 0) !== (t.bytes === '0')) return false;
  if ((d.storedObjects === null) !== (d.storedBytes === null)) return false;
  if (d.state === 'notMeasured' ? d.storedBytes !== null : d.state !== 'unavailable' && d.storedBytes === null) return false;
  return d.storedBytes === null || decimal(d.storedBytes) && decimal(d.storedObjects) && (BigInt(d.storedObjects!) === 0n) === (BigInt(d.storedBytes) === 0n);
}
/** Each build once, each consistent with itself. */
export function preparedDataListMatches(list: PreparedDataList): boolean {
  const keys = new Set(list.builds.map(b => `${b.productId}|${b.buildIdentity}`));
  return keys.size === list.builds.length && instant(list.measuredAt) && list.builds.every(preparedBuildMatches);
}
/** A free reply is for the build it named, says what it deleted and leaves the file catalog removed (or never queued). */
export function preparedDataFreedMatches(f: PreparedDataFreed, p: PreparedDataFreeParams): boolean {
  return f.productId === p.productId && f.buildIdentity === p.buildIdentity && decimal(f.freedBytes) && (f.freedFiles > 0 || f.freedBytes === '0')
    && fileCatalogMeasured(f.fileCatalog) && (f.fileCatalog.state === 'removed' || f.fileCatalog.state === 'none');
}
