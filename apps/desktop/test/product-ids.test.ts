import { describe, expect, it } from 'vitest';
import catalog from '../../../contracts/examples/discovery-catalog-page.json';
import { validProduct } from '../src/shared/bridge';
import { validDownloadRead } from '../src/shared/download';
import { validInventoryRead } from '../src/shared/inventory';
import { validRetentionTarget } from '../src/shared/retention';
import { validTableDefinition, validTablesCatalog } from '../src/shared/tables';

// The catalog example lists every registered product; the engine contract test keeps it equal to products.list (#42).
const products = catalog.products.map(status => status.productId);
const hash = 'a'.repeat(64);
describe('every listed product passes the desktop open-path validators', () => {
  it('covers the long products that used to fail', () => { expect(products).toEqual(expect.arrayContaining(['wow_classic_era_ptr', 'wow_classic_titan'])); });
  it.each(products)('%s', productId => {
    const context = { productId, buildIdentity: hash, sourceRegion: 'us' };
    expect(validProduct(productId)).toBe(true);
    expect(validTablesCatalog({ ...context, limit: 50 })).toBe(true);
    expect(validTableDefinition({ ...context, tableId: 'b977271e' })).toBe(true);
    expect(validInventoryRead({ ...context, generation: '1', limit: 50 })).toBe(true);
    expect(validDownloadRead({ ...context, ledgerId: hash, limit: 50 })).toBe(true);
    expect(validRetentionTarget({ ...context, ledgerId: hash })).toBe(true);
  });
  it('keeps a bound on the product ID', () => {
    expect(validProduct('w'.repeat(65))).toBe(false);
    expect(validTablesCatalog({ productId: 'w'.repeat(65), buildIdentity: hash, sourceRegion: 'us' })).toBe(false);
  });
});
