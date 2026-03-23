import { describe, it, expect } from 'vitest';
import { evaluate } from '@elwood-lang/core';
import '../src/index.js'; // registers fromXlsx/toXlsx

import * as XLSX from 'xlsx';

function createTestXlsx(data: unknown[][], sheetName = 'Sheet1'): string {
  const wb = XLSX.utils.book_new();
  const ws = XLSX.utils.aoa_to_sheet(data);
  XLSX.utils.book_append_sheet(wb, ws, sheetName);
  return XLSX.write(wb, { type: 'base64', bookType: 'xlsx' });
}

describe('XLSX Extension', () => {
  it('fromXlsx with headers', () => {
    const base64 = createTestXlsx([
      ['name', 'age', 'city'],
      ['Alice', 30, 'Berlin'],
      ['Bob', 25, 'Munich'],
    ]);
    const r = evaluate('$.fromXlsx()', base64);
    expect(r.success).toBe(true);
    expect(r.value).toEqual([
      { name: 'Alice', age: 30, city: 'Berlin' },
      { name: 'Bob', age: 25, city: 'Munich' },
    ]);
  });

  it('fromXlsx without headers', () => {
    const base64 = createTestXlsx([
      ['Alice', 30],
      ['Bob', 25],
    ]);
    const r = evaluate('$.fromXlsx({ headers: false })', base64);
    expect(r.success).toBe(true);
    expect(r.value).toEqual([
      { A: 'Alice', B: '30' },
      { A: 'Bob', B: '25' },
    ]);
  });

  it('toXlsx round-trip', () => {
    const input = [
      { name: 'Alice', age: 30 },
      { name: 'Bob', age: 25 },
    ];
    const r = evaluate('$.toXlsx()', input);
    expect(r.success).toBe(true);
    expect(typeof r.value).toBe('string');

    // Round-trip: parse the base64 back
    const r2 = evaluate('$.fromXlsx()', r.value);
    expect(r2.success).toBe(true);
    expect(r2.value).toEqual([
      { name: 'Alice', age: 30 },
      { name: 'Bob', age: 25 },
    ]);
  });

  it('fromXlsx with pipe transform', () => {
    const base64 = createTestXlsx([
      ['name', 'score'],
      ['Alice', 90],
      ['Bob', 60],
      ['Charlie', 85],
    ]);
    const r = evaluate('$.fromXlsx() | where u => u.score > 70 | select u => u.name', base64);
    expect(r.success).toBe(true);
    expect(r.value).toEqual(['Alice', 'Charlie']);
  });
});
