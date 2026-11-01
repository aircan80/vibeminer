import type { BuildCatalog, BuildCatalogRefresh, CatalogBuild, CatalogProduct, CatalogRefreshFailure, CatalogSummary } from '../../../../contracts/generated/discovery';
/** Times as the old Discovery screen shows them: the viewer's locale and time zone. */
export const catalogTime = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
export type CatalogRow = { key: string; product: CatalogProduct; build: CatalogBuild };
export const catalogKey = (productId: string, buildId: string) => `${productId}:${buildId}`;
/** Every listed build in list order: products in the engine's (registry) order, each product's builds as layer 3 orders them. */
export const catalogRows = (catalog: BuildCatalog | null): CatalogRow[] => productRows(catalog?.products ?? []);
/** A build's regions, upper case in listed order. A current build lists only the regions whose listing is current. */
export const regionText = (regions: readonly string[]) => regions.length ? regions.map(r => r.toUpperCase()).join(', ') : 'No region listed';
/**
 * The group heading's second part: how many builds, or why there are none. A product the summary lists with no versions document
 * (`withoutVersions`, D476) has no history after a refresh either, so it reads as listing no builds.
 */
export function productLine(product: CatalogProduct, withoutVersions: readonly string[] = []): string {
  if (!product.buildCount) return product.lastObservedAt === null && !withoutVersions.includes(product.productId) ? 'No history yet' : 'No builds listed';
  const builds = product.buildCount === 1 ? '1 build' : `${product.buildCount} builds`;
  return product.builds.length < product.buildCount ? `${builds}, newest ${product.builds.length} shown` : builds;
}
/** No product has any build: nothing has been stored yet. */
export const catalogEmpty = (catalog: BuildCatalog) => catalog.products.every(p => p.buildCount === 0);
/**
 * A note only when the summary can't name the other WoW products (D476): none recorded yet, or the newest one unreadable. A
 * readable summary needs no note; its products are the list below.
 */
export function summaryNote(summary: CatalogSummary | null, time: (value: string) => string = catalogTime): string | null {
  if (!summary) return 'No product summary recorded yet. Refresh fetches it, and with it the other WoW products.';
  return summary.readable ? null : `The summary of ${time(summary.observedAt)} can't be read, so other WoW products aren't listed.`;
}
/** The reviewed products, in the engine's order, and the WoW products the summary lists that aren't reviewed (D476). */
export function splitProducts(catalog: BuildCatalog | null): { reviewed: CatalogProduct[]; others: CatalogProduct[] } {
  const products = catalog?.products ?? [];
  return { reviewed: products.filter(p => p.reviewed), others: products.filter(p => !p.reviewed) };
}
/** The rows of some products in list order, as catalogRows gives them. */
export const productRows = (products: readonly CatalogProduct[]): CatalogRow[] =>
  products.flatMap(product => product.builds.map(build => ({ key: catalogKey(product.productId, build.id), product, build })));
/** The other products' heading count: how many products, and how many builds they list. */
export function othersLine(others: readonly CatalogProduct[]): string {
  const builds = others.reduce((n, p) => n + p.buildCount, 0);
  return `${others.length === 1 ? '1 product' : `${others.length} products`}, ${builds === 1 ? '1 build' : `${builds} builds`}`;
}
/** The newest versions reply of any product, or null when no product has one. */
export function lastChecked(catalog: BuildCatalog | null): string | null {
  const times = (catalog?.products ?? []).map(p => p.lastObservedAt).filter((t): t is string => t !== null).sort((a, b) => Date.parse(a) - Date.parse(b));
  return times.at(-1) ?? null;
}
/**
 * Refresh progress (D476): the engine asks for the product summary first, as step 0 of 1, then counts it as the first of
 * products + 1 and every finished product after it, so the text agrees with "Checked 49 products and the summary" at the end.
 */
export function progressText(progress: { completed: number; total: number } | null): string {
  if (!progress || progress.total < 1) return 'Checking the version service…';
  if (progress.completed < 1) return 'Checking the version service (the product summary)';
  const products = progress.total - 1, done = Math.min(progress.completed, progress.total) - 1;
  return done < products ? `Checking the version service (${done} of ${products} products)` : `Checked ${products} of ${products} products and the summary`;
}
/** The status line after a refresh, in the same terms as its progress. */
export const checkedText = (checked: number) => `${checked === 1 ? '1 product' : `${checked} products`} and the summary`;
export const OPEN_REASON = 'Opening on the new storage comes next. The Old build list still opens builds.';
export const SWITCHED_TEXT = 'Refresh stopped when you switched lists. Products checked before stopping keep their new results.';
export const OFFLINE_TEXT = "Couldn't reach the version service. Showing the stored list.";
export const BUSY_TEXT = 'Another VibeMiner process, such as vm browse, is writing the build store. Close it, then refresh.';
export type RefreshNotice = { kind: 'offline'; text: string } | { kind: 'partial'; text: string; items: string[] };
const failureCode = (f: CatalogRefreshFailure) => f.code ?? f.state;
/**
 * What a finished refresh leaves to say. Every product failed and only as unavailable: offline. Some failed: which,
 * with their codes; the stored list is kept for them. Null when everything answered.
 */
export function refreshNotice(result: BuildCatalogRefresh): RefreshNotice | null {
  if (!result.failures.length) return null;
  const products = result.catalog.products, byProduct = new Map<string | null, CatalogRefreshFailure[]>();
  for (const f of result.failures) byProduct.set(f.productId, [...(byProduct.get(f.productId) ?? []), f]);
  const failedProducts = [...byProduct.keys()].filter((id): id is string => id !== null);
  if (failedProducts.length >= result.checked && result.checked > 0 && result.failures.every(f => f.state === 'unavailable')) return { kind: 'offline', text: OFFLINE_TEXT };
  const label = (id: string | null) => id === null ? 'Product summary' : products.find(p => p.productId === id)?.label ?? id;
  const items = [...byProduct].map(([id, failures]) => `${label(id)}${id ? ` (${id})` : ''}: ${[...new Set(failures.map(failureCode))].join(', ')}`);
  const names = failedProducts.length;
  const text = names ? `${names === 1 ? '1 product' : `${names} products`} couldn't be checked. Their stored builds are kept.` : "The product summary couldn't be read. Other WoW products are as the last summary listed them.";
  return { kind: 'partial', text, items };
}
