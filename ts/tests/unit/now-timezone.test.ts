import { describe, it, expect } from 'vitest';
import { execute, evaluate } from '../../src/index.js';
import { formatDateInZone } from '../../src/evaluator.js';

/**
 * Regression tests for now(format, timezone). The TS evaluator used to ignore
 * the timezone argument and format UTC, so now(..., "Europe/Berlin") returned
 * UTC instead of CEST/CET. Mirrors the .NET EndToEndTests timezone cases.
 */
describe('now() with timezone', () => {
  it('converts UTC to the requested zone (live offset)', () => {
    const result = execute(
      'return {\n  utc: utcNow("yyyy-MM-dd HH:mm:ss"),\n  berlin: now("yyyy-MM-dd HH:mm:ss", "Europe/Berlin")\n}',
      {},
    );
    expect(result.success).toBe(true);
    const { utc, berlin } = result.value as { utc: string; berlin: string };

    const parse = (s: string) => {
      const [d, t] = s.split(' ');
      const [y, mo, da] = d.split('-').map(Number);
      const [h, mi, se] = t.split(':').map(Number);
      return Date.UTC(y, mo - 1, da, h, mi, se);
    };
    const offsetHours = Math.round((parse(berlin) - parse(utc)) / 3_600_000);

    // Berlin is CET (+1) in winter, CEST (+2) in summer — never 0 (the bug).
    expect([1, 2]).toContain(offsetHours);
    expect(offsetHours).toBe(liveBerlinOffsetHours());
  });

  it.each([
    // 2026-07-12 16:57 UTC → CEST (UTC+2) → 18:57
    [Date.UTC(2026, 6, 12, 16, 57, 0), 'Europe/Berlin', '2026-07-12 18:57:00'],
    [Date.UTC(2026, 6, 12, 16, 57, 0), 'Europe/Rome', '2026-07-12 18:57:00'],
    // 2026-01-12 16:57 UTC → CET (UTC+1) → 17:57
    [Date.UTC(2026, 0, 12, 16, 57, 0), 'Europe/Berlin', '2026-01-12 17:57:00'],
  ])('converts %i in %s across DST boundaries', (utcMs, tz, expected) => {
    expect(formatDateInZone(new Date(utcMs as number), 'yyyy-MM-dd HH:mm:ss', tz as string)).toBe(expected);
  });

  it('reports a diagnostic for an unknown timezone', () => {
    const result = evaluate('now("yyyy-MM-dd", "Mars/Olympus")', {});
    expect(result.success).toBe(false);
    expect(result.diagnostics[0].message.toLowerCase()).toContain('timezone');
  });

  it('utcNow ignores any timezone argument', () => {
    const result = execute(
      'return {\n  a: utcNow("yyyy-MM-dd HH:mm:ss"),\n  b: utcNow("yyyy-MM-dd HH:mm:ss", "Europe/Berlin")\n}',
      {},
    );
    const { a, b } = result.value as { a: string; b: string };
    // Both are UTC; allow a 1s skew between the two calls.
    expect(Math.abs(Date.parse(a + 'Z') - Date.parse(b + 'Z'))).toBeLessThanOrEqual(1000);
  });
});

function liveBerlinOffsetHours(): number {
  const now = new Date();
  const parts = Object.fromEntries(
    new Intl.DateTimeFormat('en-US', {
      timeZone: 'Europe/Berlin', hour12: false,
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit',
    }).formatToParts(now).map(p => [p.type, p.value]),
  );
  const asUtc = Date.UTC(+parts.year, +parts.month - 1, +parts.day, +parts.hour, +parts.minute, +parts.second);
  return Math.round((asUtc - now.getTime()) / 3_600_000);
}
