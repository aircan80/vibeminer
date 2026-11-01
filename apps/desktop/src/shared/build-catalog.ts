import type { BuildCatalog, BuildCatalogRefresh } from '../../../../contracts/generated/discovery';
import { validEmpty } from './settings';
/** catalog.list and catalog.refresh take exactly {} (build-catalog-v1); refresh also needs a request id. */
export const validCatalogParams = (params: unknown): params is Record<string, never> => validEmpty(params);
/**
 * What the schema can't say about a list: each product once, a build once per product with distinct regions, and no
 * more builds than the product's total.
 */
export function buildCatalogMatches(value: BuildCatalog): boolean {
  const products = new Set(value.products.map(p => p.productId));
  return products.size === value.products.length && value.products.every(p => {
    const ids = new Set(p.builds.map(b => b.id));
    return ids.size === p.builds.length && p.builds.length <= p.buildCount && p.problems.length <= p.problemCount
      && p.builds.every(b => new Set(b.regions).size === b.regions.length && Date.parse(b.firstSeenAt) <= Date.parse(b.lastSeenAt));
  });
}
/** A refresh answers as the writer, and names only products it lists (or null for the summary). */
export function buildCatalogRefreshMatches(value: BuildCatalogRefresh): boolean {
  const products = new Set(value.catalog.products.map(p => p.productId));
  return value.catalog.access === 'writer' && buildCatalogMatches(value.catalog) && value.checked <= value.catalog.products.length
    && value.failures.every(f => f.productId === null ? f.document === 'summary' : products.has(f.productId) && f.document !== 'summary');
}
