import { describe, it, expect, afterEach } from 'vitest';
import { execute, evaluate, setScanWarningThreshold, DEFAULT_SCAN_WARNING_THRESHOLD } from '../../src/index.js';

/**
 * The repeated-scan warning beyond any/all: first/last with a predicate and a nested where
 * are counted too, and a site is reported only when every run scans the same collection —
 * not when each run scans the row's own. Mirrors the .NET RepeatedScanTests.
 */
describe('repeated-scan warning: first, last, where, and row-dependent input', () => {
  afterEach(() => setScanWarningThreshold(DEFAULT_SCAN_WARNING_THRESHOLD));

  // 50 rows and a 50-entry list with nothing in common, plus 50 orders of 50 lines each.
  const input = () => ({
    rows: Array.from({ length: 50 }, (_, i) => `r${i}`),
    list: Array.from({ length: 50 }, (_, i) => `l${i}`),
    orders: Array.from({ length: 50 }, (_, o) => ({
      id: o,
      lines: Array.from({ length: 50 }, (_, l) => `o${o}l${l}`),
    })),
  });

  function run(script: string, threshold = 2000) {
    setScanWarningThreshold(threshold);
    const result = execute(script, input());
    expect(result.diagnostics.filter(d => d.severity === 'error')).toEqual([]);
    return result;
  }

  it('warns for first inside a memo that never hits', () => {
    const result = run(
      'let find = memo name => ($.list[*] | first x => x == name)\n' +
      'return $.rows[*] | where r => find(r) == null | count');

    expect(result.value).toBe(50);
    expect(result.diagnostics).toHaveLength(1);
    expect(result.diagnostics[0].severity).toBe('warning');
    expect(result.diagnostics[0].message).toContain("'first' evaluated its predicate 2,500 times over 50 runs");
    expect(result.diagnostics[0].suggestion).toContain('indexBy');
    expect(result.diagnostics[0].line).toBe(1);
  });

  it('is silent for a memo that always hits', () => {
    const result = run(
      'let find = memo name => ($.list[*] | first x => x == name)\n' +
      "return $.rows[*] | where r => find('absent') == null | count", 60);
    expect(result.diagnostics).toEqual([]);
  });

  it.each([
    ['first', 'let list = $.list[*] | select x => x\nreturn $.rows[*] | select r => (list | first x => x == r) | count'],
    ['last', 'let list = $.list[*] | select x => x\nreturn $.rows[*] | select r => (list | last x => x == r) | count'],
    ['where', 'let list = $.list[*] | select x => x\nreturn $.rows[*] | select r => (list | where x => x == r | count) | count'],
  ])('warns for a per-row %s over a fixed list', (name, script) => {
    const result = run(script);
    expect(result.diagnostics).toHaveLength(1);
    expect(result.diagnostics[0].message).toContain(`'${name}' evaluated its predicate 2,500 times over 50 runs`);
  });

  it('never warns for a where that runs once', () => {
    const result = run("return $.orders[*] | selectMany o => o.lines | where l => l == 'absent' | count", 100);
    expect(result.diagnostics).toEqual([]);
  });

  it('reports the where, not the first that reads its output', () => {
    const result = run(
      'let list = $.list[*] | select x => x\n' +
      "return $.rows[*] | select r => (list | where x => x != r | first y => y == 'absent') | count");
    expect(result.diagnostics).toHaveLength(1);
    expect(result.diagnostics[0].message.startsWith("'where'")).toBe(true);
  });

  it.each([
    "return $.orders[*] | where o => (o.lines | any l => l == 'absent') | count",
    "return $.orders[*] | where o => (o.lines | all l => l != 'absent') | count",
    "return $.orders[*] | select o => (o.lines | first l => l == 'absent') | count",
    "return $.orders[*] | select o => (o.lines | last l => l == 'absent') | count",
    "return $.orders[*] | select o => (o.lines | where l => l == 'absent' | count) | count",
    "return $.orders[*] | where ($.lines | any l => l == 'absent') | count",
    "return $.orders[*] | select ($.lines[*] | first l => l == 'absent') | count",
    "return $.orders[*] | select o => (o.lines | select l => l | first l => l == 'absent') | count",
    "return $.orders[*] | select o =>\n  let own = o.lines\n  let hit = own | first l => l == 'absent'\n  { hit: hit }",
    "return $.orders[*] | select o =>\n  let own = o.lines\n  let again = own\n  let hit = again | any l => l == 'absent'\n  { hit: hit }",
  ])('is silent when each run scans the row\'s own collection: %s', script => {
    // 50 orders x 50 lines = 2,500 evaluations over 50 runs: past the threshold, and linear.
    expect(run(script).diagnostics).toEqual([]);
  });

  it.each([
    // A let inside the lambda that does not come from the row leaves the scan repeated.
    "return $.orders[*] | select o =>\n  let whole = $.list\n  let hit = whole | first l => l == 'absent'\n  { hit: hit }",
    // The root, read from inside a named lambda, is the same for every row.
    'return $.orders[*] | select o => ($.list[*] | first l => l == o.id) | count',
    // An inner lambda reusing the row's name does not make the outer scan depend on the row.
    "let list = $.list\nreturn $.orders[*] | select o => ((list | select o => o) | first l => l == 'absent') | count",
    // A row-dependent predicate over a fixed list is exactly the case to report.
    'let list = $.list\nreturn $.orders[*] | where o => (list | any l => l == o.id) | count',
  ])('warns when every run scans the same collection: %s', script => {
    expect(run(script).diagnostics).toHaveLength(1);
  });

  it('uses the nearest enclosing row for nested lambdas', () => {
    const result = run(
      "return $.orders[*] | select o => (o.lines | where l => (o.lines | any t => t == 'absent') | count) | count",
      100_000);
    expect(result.value).toBe(50);
    expect(result.diagnostics).toHaveLength(1);
    expect(result.diagnostics[0].message).toContain("'any' evaluated its predicate 125,000 times over 2,500 runs");
  });

  it('analyses an expression as well as a script', () => {
    setScanWarningThreshold(2000);
    expect(evaluate("$.orders[*] | select o => (o.lines | first l => l == 'absent') | count", input()).diagnostics).toEqual([]);
    expect(evaluate("$.orders[*] | select o => ($.list[*] | first l => l == 'absent') | count", input()).diagnostics).toHaveLength(1);
  });
});
