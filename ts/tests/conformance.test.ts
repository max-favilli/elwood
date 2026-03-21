import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { evaluate, execute } from '../src/index.js';

const specDir = resolve(__dirname, '../../spec/test-cases');

interface TestCase {
  name: string;
  script: string;
  input: unknown;
  expected: unknown;
}

function discoverTestCases(): TestCase[] {
  if (!existsSync(specDir)) {
    console.warn(`spec/test-cases/ not found at ${specDir}`);
    return [];
  }

  const dirs = readdirSync(specDir, { withFileTypes: true })
    .filter(d => d.isDirectory())
    .map(d => d.name)
    .sort();

  const cases: TestCase[] = [];

  for (const dir of dirs) {
    const scriptPath = join(specDir, dir, 'script.elwood');
    const inputPath = join(specDir, dir, 'input.json');
    const expectedPath = join(specDir, dir, 'expected.json');

    // Skip benchmark stubs (no input/expected)
    if (!existsSync(scriptPath) || !existsSync(inputPath) || !existsSync(expectedPath)) {
      continue;
    }

    const script = readFileSync(scriptPath, 'utf-8');
    const input = JSON.parse(readFileSync(inputPath, 'utf-8'));
    const expected = JSON.parse(readFileSync(expectedPath, 'utf-8'));

    cases.push({ name: dir, script, input, expected });
  }

  return cases;
}

const testCases = discoverTestCases();

describe('Conformance Tests', () => {
  it.each(testCases)('$name', ({ name, script, input, expected }) => {
    // Determine if script mode (has let/return) or expression mode
    const isScript = script.trimStart().startsWith('let ') ||
                     script.includes('\nlet ') ||
                     script.includes('return ');

    const result = isScript
      ? execute(script, input)
      : evaluate(script.trim(), input);

    expect(result.success).toBe(true);
    expect(result.value).toEqual(expected);
  });

  it('should discover test cases', () => {
    expect(testCases.length).toBeGreaterThan(0);
    console.log(`Discovered ${testCases.length} conformance test cases`);
  });
});
