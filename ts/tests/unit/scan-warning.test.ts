import { describe, it, expect, afterEach } from 'vitest';
import { execute, setScanWarningThreshold, DEFAULT_SCAN_WARNING_THRESHOLD } from '../../src/index.js';

/**
 * An any/all that runs once per row of an enclosing collection scans its input
 * rows x list times. The result then carries a warning naming it; it stays successful
 * and its value is untouched. Mirrors the .NET ScanWarningTests.
 */
describe('repeated-scan warning for any/all', () => {
  afterEach(() => setScanWarningThreshold(DEFAULT_SCAN_WARNING_THRESHOLD));

  // 50 rows, none of which is in the 50-entry list: every run scans the whole list.
  const nested = 'let list = $.list[*] | select x => x\nreturn $.rows[*] | where r => !(list | any n => n == r) | count';
  const input = (rows = 50, list = 50) => ({
    rows: Array.from({ length: rows }, (_, i) => `r${i}`),
    list: Array.from({ length: list }, (_, i) => `l${i}`),
  });

  it('warns past the threshold and still succeeds', () => {
    setScanWarningThreshold(2000);
    const result = execute(nested, input());

    expect(result.success).toBe(true);
    expect(result.value).toBe(50);
    expect(result.diagnostics).toHaveLength(1);
    const [warning] = result.diagnostics;
    expect(warning.severity).toBe('warning');
    expect(warning.message).toContain("'any' evaluated its predicate 2,500 times over 50 runs");
    expect(warning.suggestion).toContain('.in(list)');
    expect(warning.line).toBe(2);
  });

  it('is silent below the threshold', () => {
    setScanWarningThreshold(2501);
    expect(execute(nested, input()).diagnostics).toEqual([]);
  });

  it('is silent at the default threshold for ordinary sizes', () => {
    expect(execute(nested, input()).diagnostics).toEqual([]);
  });

  it('never warns for a single large scan', () => {
    setScanWarningThreshold(100);
    const result = execute("return $.rows[*] | any r => r == 'absent'", input(5000));
    expect(result.success).toBe(true);
    expect(result.diagnostics).toEqual([]);
  });

  it('is disabled by a threshold of zero', () => {
    setScanWarningThreshold(0);
    expect(execute(nested, input()).diagnostics).toEqual([]);
  });

  it('reports all like any', () => {
    setScanWarningThreshold(2000);
    const result = execute(
      'let list = $.list[*] | select x => x\nreturn $.rows[*] | where r => (list | all n => n != r) | count',
      input(),
    );
    expect(result.value).toBe(50);
    expect(result.diagnostics[0].message).toContain("'all' evaluated its predicate 2,500 times over 50 runs");
  });

  it('does not carry counts from one evaluation into the next', () => {
    setScanWarningThreshold(4000);
    expect(execute(nested, input()).diagnostics).toEqual([]);
    expect(execute(nested, input()).diagnostics).toEqual([]);
  });

  it('accompanies an error raised later', () => {
    setScanWarningThreshold(2000);
    const result = execute(
      'let n = $.rows[*] | where r => !($.list[*] | any x => x == r) | count\nreturn $.missing.property',
      input(),
    );
    expect(result.success).toBe(false);
    expect(result.diagnostics.map(d => d.severity).sort()).toEqual(['error', 'warning']);
  });
});
