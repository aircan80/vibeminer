import type { BuildAvailabilityParams } from '../../../../contracts/generated/discovery';

/** builds.availability.check (#9, D106): one exact saved build, and whether to ask for every archive too. */
export function validAvailabilityCheck(value: unknown): value is BuildAvailabilityParams {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const p = value as BuildAvailabilityParams;
  return typeof p.productId === 'string' && /^[a-z][a-z0-9_-]{0,63}$/.test(p.productId) && typeof p.buildIdentity === 'string' && /^[0-9a-f]{64}$/.test(p.buildIdentity)
    && typeof p.archives === 'boolean' && Object.keys(p).length === 3;
}
