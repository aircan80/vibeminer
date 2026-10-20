import type { DiscoveryCatalogPage, DiscoveryPage, ProductDiscoveryStatus } from '../../../contracts/generated/discovery';
/** Authored registry order used by the renderer tests. */
export const testProducts = [{ id: 'wow', label: 'Retail', description: 'World of Warcraft' }, { id: 'wowt', label: 'PTR', description: 'Public test' }];
export const notChecked = (productId: string): ProductDiscoveryStatus => ({ productId, lastCheckedAt: null, checkedRegions: [], errors: [], hasSavedHistory: false, connection: 'notChecked' });
/** The status a single-product page states for its product. */
export const statusOf = (page: DiscoveryPage): ProductDiscoveryStatus => ({ productId: page.productId, lastCheckedAt: page.lastCheckedAt, checkedRegions: page.checkedRegions, errors: page.errors, hasSavedHistory: page.hasSavedHistory, connection: page.connection });
/** A cross-product page holding a single-product page's builds and status; the other test products were never checked. */
export function catalog(page: DiscoveryPage, patch: Partial<DiscoveryCatalogPage> = {}, statuses: ProductDiscoveryStatus[] = []): DiscoveryCatalogPage {
  const products = testProducts.map(product => statuses.find(status => status.productId === product.id) ?? (product.id === page.productId ? statusOf(page) : notChecked(product.id)));
  return { productId: null, filter: 'all', builds: page.builds, nextCursor: page.nextCursor, products, refreshProblems: [], ...patch };
}
