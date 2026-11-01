import type { BuildCatalog, BuildCatalogRefresh, CatalogBuild, CatalogProduct, CatalogRefreshFailure, CatalogSummary } from '../../../../contracts/generated/discovery';
/** Times as the old Discovery screen shows them: the viewer's locale and time zone. */
export const catalogTime = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
export type CatalogRow = { key: string; product: CatalogProduct; build: CatalogBuild };
export const catalogKey = (productId: string, buildId: string) => `${productId}:${buildId}`;
/** Every listed build in list order: products in the engine's (registry) order, each product's builds as layer 3 orders them. */
export function catalogRows(catalog: BuildCatalog | null): CatalogRow[] {
  return (catalog?.products ?? []).flatMap(product => product.builds.map(build => ({ key: catalogKey(product.productId, build.id), product, build })));
}
/** A build's regions, upper case in listed order. A current build lists only the regions whose listing is current. */
export const regionText = (regions: readonly string[]) => regions.length ? regions.map(r => r.toUpperCase()).join(', ') : 'No region listed';
/** The group heading's second part: how many builds, or why there are none. */
export function productLine(product: CatalogProduct): string {
  if (!product.buildCount) return product.lastObservedAt === null ? 'No history yet' : 'No builds listed';
  const builds = product.buildCount === 1 ? '1 build' : `${product.buildCount} builds`;
  return product.builds.length < product.buildCount ? `${builds}, newest ${product.builds.length} shown` : builds;
}
/** No product has any build: nothing has been stored yet. */
export const catalogEmpty = (catalog: BuildCatalog) => catalog.products.every(p => p.buildCount === 0);
/** The newest stored summary, worded as vm browse words it. */
export function summaryText(summary: CatalogSummary | null, time: (value: string) => string = catalogTime): string {
  if (!summary) return 'No product summary recorded yet. Refresh fetches one.';
  const at = time(summary.observedAt);
  if (summary.newProducts.length) return `New in the ${at} summary: ${summary.newProducts.join(', ')} (not reviewed).`;
  if (!summary.readable) return `The summary of ${at} can't be read, so new products are unknown.`;
  return `Summary of ${at}: no new WoW products.`;
}
/** The newest versions reply of any product, or null when no product has one. */
export function lastChecked(catalog: BuildCatalog | null): string | null {
  const times = (catalog?.products ?? []).map(p => p.lastObservedAt).filter((t): t is string => t !== null).sort((a, b) => Date.parse(a) - Date.parse(b));
  return times.at(-1) ?? null;
}
/**
 * Refresh progress counts products only. The engine's total also counts the product summary as its last step (products + 1),
 * so the text agrees with "Checked 13 products and the summary" at the end.
 */
export function progressText(progress: { completed: number; total: number } | null): string {
  if (!progress || progress.total < 1) return 'Checking the version service…';
  const products = progress.total - 1, done = Math.min(progress.completed, products);
  if (progress.completed < products) return `Checking the version service (${done} of ${products} products)`;
  return progress.completed < progress.total ? `Checking the version service (${products} of ${products} products, then the summary)` : `Checked ${products} of ${products} products and the summary`;
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
  const text = names ? `${names === 1 ? '1 product' : `${names} products`} couldn't be checked. Their stored builds are kept.` : "The product summary couldn't be read. New products are unknown until the next refresh.";
  return { kind: 'partial', text, items };
}
