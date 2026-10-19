// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { encodedPercentage, exactBytes, inventoryAccountingValid, readableBytes } from '../src/shared/inventory';
import fixture from '../../../contracts/examples/inventory-workspace.json';
describe('exact encoded accounting', () => {
  it('admits correct accounting beyond Number precision and rejects a one-byte discrepancy', () => {
    const inventory = { ...fixture.inventory, totalEncodedBytes: '9007199254740993', verifiedEncodedBytes: '9007199254740992', remainingEncodedBytes: '1' };
    expect(inventoryAccountingValid(inventory as any)).toBe(true);
    expect(inventoryAccountingValid({ ...inventory, totalEncodedBytes: '9007199254740992' } as any)).toBe(false);
    expect(exactBytes(inventory.totalEncodedBytes).replaceAll(',', '')).toBe('9007199254740993 bytes');
    expect(encodedPercentage(inventory.verifiedEncodedBytes, inventory.totalEncodedBytes)).toBe('99.99%');
  });
  it('keeps shared bytes within verified bytes and metadata separate from the encoded ledger', () => {
    expect(inventoryAccountingValid({ ...fixture.inventory, sharedVerifiedEncodedBytes: '101' } as any)).toBe(false);
    expect(inventoryAccountingValid({ ...fixture.inventory, metadataEncodedBytes: '9999' } as any)).toBe(true);
  });
  it('handles a complete zero-byte ledger without an invented percentage or divide-by-zero', () => {
    const inventory = { ...fixture.inventory, totalEncodedBytes: '0', verifiedEncodedBytes: '0', remainingEncodedBytes: '0', sharedVerifiedEncodedBytes: '0' };
    expect(inventoryAccountingValid(inventory as any)).toBe(true);
    expect(encodedPercentage('0', '0')).toBe('0 bytes required'); expect(readableBytes('0')).toBe('0 bytes');
  });
});
