import { describe, it, expect } from 'vitest';
import { evaluate } from '../src/index.js';

describe('Performance Benchmark', () => {
  // Generate 100K items
  const users = Array.from({ length: 100_000 }, (_, i) => ({
    name: ['Alice', 'Bob', 'Charlie', 'Diana', 'Eve', 'Frank', 'Grace', 'Henry', 'Iris', 'Jack'][i % 10] + i,
    age: 15 + (i % 55),
    active: i % 3 !== 0,
  }));
  const input = { users };

  it('100K where+select name', () => {
    const expr = '$.users[*] | where u => u.active | select u => u.name';

    // Warmup
    evaluate(expr, input);

    const times: number[] = [];
    for (let i = 0; i < 5; i++) {
      const start = performance.now();
      const r = evaluate(expr, input);
      times.push(performance.now() - start);
      expect(r.success).toBe(true);
      expect((r.value as any[]).length).toBeGreaterThan(60000);
    }

    const avg = times.reduce((a, b) => a + b) / times.length;
    const min = Math.min(...times);
    const max = Math.max(...times);
    console.log(`  TS where+select: min=${min.toFixed(0)}ms max=${max.toFixed(0)}ms avg=${avg.toFixed(0)}ms`);
  });

  it('100K select with toString + charArray concat', () => {
    const expr = '$.users[*] | select u => { active: u.active.toString(), name: u.name.toCharArray() | concat("-") }';

    // Warmup
    evaluate(expr, input);

    const times: number[] = [];
    for (let i = 0; i < 5; i++) {
      const start = performance.now();
      const r = evaluate(expr, input);
      times.push(performance.now() - start);
      expect(r.success).toBe(true);
    }

    const avg = times.reduce((a, b) => a + b) / times.length;
    const min = Math.min(...times);
    const max = Math.max(...times);
    console.log(`  TS toString+concat: min=${min.toFixed(0)}ms max=${max.toFixed(0)}ms avg=${avg.toFixed(0)}ms`);
  });
});
