import { describe, it, expect } from 'vitest';
import { execute, evaluate } from '../../src/index.js';

/**
 * x.in(list) indexes the strings of a list that is tested more than once. These guard
 * that the indexed answer is the answer a scan gives. Mirrors the .NET InMembershipTests.
 */
describe('in() tested once per row', () => {
  function diffInput(n: number) {
    // Every tenth history entry has no current file.
    const pad = (i: number) => String(i).padStart(6, '0');
    return {
      files: Array.from({ length: n }, (_, i) => ({ name: `IMG_${pad(i)}.jpg`, type: 'image/jpeg' })),
      history: Array.from({ length: n }, (_, i) => ({ fileName: `${i % 10 === 0 ? 'GONE' : 'IMG'}_${pad(i)}.jpg` })),
    };
  }

  it('matches any-with-equality over a let-bound pipeline', () => {
    const input = diffInput(300);
    const setup = "let names = $.files[*] | where f => f.type != 'application/json' | select f => f.name\n";
    const viaIn = execute(setup + 'return $.history[*] | where h => !h.fileName.in(names) | select h => h.fileName', input);
    const viaAny = execute(setup + 'return $.history[*] | where h => !(names | any n => n == h.fileName) | select h => h.fileName', input);

    expect(viaIn.success && viaAny.success).toBe(true);
    expect(viaIn.value).toEqual(viaAny.value);
    expect((viaIn.value as unknown[]).length).toBe(30);
  });

  it('keeps kinds apart in a mixed list', () => {
    const result = execute(
      'let allowed = $.allowed\nreturn $.rows[*] | select r => r.in(allowed)',
      { allowed: ['7', 'true', 2, null, 'a'], rows: ['7', 7, 'true', true, 2, '2', null, 'a', 'A'] },
    );
    expect(result.value).toEqual([true, false, true, false, true, false, true, true, false]);
  });

  it('uses each row\'s own list when the list changes per call', () => {
    const result = evaluate('$[*] | select r => r.v.in(r.list)', [
      { v: 'a', list: ['a', 'b'] },
      { v: 'a', list: ['b', 'c'] },
      { v: 'c', list: ['b', 'c'] },
      { v: 'c', list: ['a'] },
    ]);
    expect(result.value).toEqual([true, false, true, false]);
  });

  it('does not carry an index from one evaluation into the next', () => {
    // A host may change an input array between calls.
    const input = { allowed: ['a'], rows: ['a', 'b', 'a', 'b'] };
    const script = 'let allowed = $.allowed\nreturn $.rows[*] | select r => r.in(allowed)';

    expect(execute(script, input).value).toEqual([true, false, true, false]);
    input.allowed[0] = 'b';
    expect(execute(script, input).value).toEqual([false, true, false, true]);
  });

  it('stays linear for a large list', () => {
    const input = diffInput(20000);
    const start = performance.now();
    const result = execute(
      'let names = $.files[*] | select f => f.name\nreturn $.history[*] | where h => !h.fileName.in(names) | count',
      input,
    );
    const elapsed = performance.now() - start;

    expect(result.value).toBe(2000);
    // Scanning is 20,000 x 20,000 comparisons, several seconds; indexed it is milliseconds.
    expect(elapsed).toBeLessThan(2000);
  });
});
