import { describe, it, expect } from 'vitest';
import { execute, evaluate } from '../../src/index.js';

/**
 * `| indexBy key` builds an object keyed by the selector, and `index[key]` reads it.
 * Mirrors the .NET IndexByTests.
 */
describe('indexBy', () => {
  const rows = [
    { k: 'a', n: 1 },
    { k: 'b', n: 2 },
    { k: 'a', n: 3 },
    { k: null, n: 4 },
    { k: 7, n: 5 },
    { k: true, n: 6 },
  ];

  const value = (expression: string, input: unknown = rows) => {
    const result = evaluate(expression, input);
    expect(result.diagnostics).toEqual([]);
    return result.value;
  };

  it('builds an object without null keys, the first row winning', () => {
    expect(value('$[*] | indexBy r => r.k')).toEqual({
      a: { k: 'a', n: 1 },
      b: { k: 'b', n: 2 },
      7: { k: 7, n: 5 },
      true: { k: true, n: 6 },
    });
  });

  it.each([
    ["($[*] | indexBy r => r.k)['a'].n", 1],
    ["($[*] | indexBy r => r.k)['b'].n", 2],
    ["($[*] | indexBy r => r.k)['zz']", null],
    ['($[*] | indexBy r => r.k)[null]', null],
    ['($[*] | indexBy r => r.k)[7].n', 5],
    ["($[*] | indexBy r => r.k)['7'].n", 5],
    ['($[*] | indexBy r => r.k)[true].n', 6],
    ["($[*] | indexBy $.k)['b'].n", 2],
    ['($[*] | indexBy r => r.n * 10)[30].k', 'a'],
    ["([] | indexBy r => r.k)['a']", null],
  ])('looks up %s', (expression, expected) => {
    expect(value(expression)).toEqual(expected);
  });

  it('treats inherited object names as ordinary keys', () => {
    // An index has no prototype, so these are entries when present and null when not.
    const input = [{ k: 'constructor', n: 1 }, { k: '__proto__', n: 2 }];
    expect(value("($[*] | indexBy r => r.k)['constructor'].n", input)).toBe(1);
    expect(value("($[*] | indexBy r => r.k)['__proto__'].n", input)).toBe(2);
    expect(value("($[*] | indexBy r => r.k)['toString']", input)).toBe(null);
    expect(value("($[*] | indexBy r => r.k)['hasOwnProperty']", input)).toBe(null);
  });

  it('gives every match through groupBy then indexBy', () => {
    expect(value("($[*] | where r => r.k != null | groupBy r => r.k | indexBy g => g.key)['a'].items | select r => r.n"))
      .toEqual([1, 3]);
  });

  it.each([
    ["{ a: 1, b: 2 }['b']", 2],
    ["{ a: 1, '7': 2 }[7]", 2],
    ['{ a: 1 }[0]', null],
    ['{ a: 1 }[null]', null],
    ['[10, 20, 30][1]', 20],
  ])('indexing an object reads a property: %s', (expression, expected) => {
    expect(value(expression, {})).toEqual(expected);
  });

  it('gives what first gives on the lookup it replaces, in linear time', () => {
    const n = 20000;
    const pad = (i: number) => String(i).padStart(6, '0');
    const input = {
      // Every tenth file is new; every seventh known file changed size.
      files: Array.from({ length: n }, (_, i) => ({ name: `IMG_${pad(i)}.jpg`, size: i % 7 === 0 ? i + 1 : i })),
      history: Array.from({ length: n }, (_, i) => ({ fileName: `${i % 10 === 0 ? 'OLD' : 'IMG'}_${pad(i)}.jpg`, size: i })),
    };
    const small = { files: input.files.slice(0, 300), history: input.history.slice(0, 300) };

    const viaFirst =
      'let find = memo name => $.history[*] | first h => h.fileName == name\n' +
      "return $.files[*] | select f => { name: f.name, action: if find(f.name) == null then 'N' else if find(f.name).size != f.size then 'U' else 'X' }";
    const viaIndex =
      'let byName = $.history[*] | indexBy h => h.fileName\n' +
      "return $.files[*] | select f => { name: f.name, action: if byName[f.name] == null then 'N' else if byName[f.name].size != f.size then 'U' else 'X' }";

    expect(execute(viaIndex, small).value).toEqual(execute(viaFirst, small).value);

    const start = performance.now();
    const result = execute(viaIndex, input);
    const elapsed = performance.now() - start;

    const actions = (result.value as { action: string }[]).map(r => r.action);
    expect(actions.filter(a => a === 'N').length).toBe(2000);
    // Scanning is 20,000 x 20,000 comparisons, several seconds; indexed it is milliseconds.
    expect(elapsed).toBeLessThan(2000);
    expect(result.diagnostics).toEqual([]);
  });
});
