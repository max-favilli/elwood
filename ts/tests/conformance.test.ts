import { describe, it, expect, afterAll } from 'vitest';
import { readFileSync, readdirSync, existsSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { evaluate, execute } from '../src/index.js';

const specDir = resolve(__dirname, '../../spec/test-cases');
const timingLogDir = resolve(__dirname, '../benchmarks');
const timingLogPath = join(timingLogDir, 'timing.log');

interface TestCase {
  name: string;
  script: string;
  input: unknown;
  expected: unknown;
  bindings?: Record<string, unknown>;
}

// Collect timings during the run
const timings: { name: string; ms: number }[] = [];

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

  // Supported input extensions in priority order
  const inputExtensions = ['.json', '.csv', '.txt', '.xml'];

  for (const dir of dirs) {
    const scriptPath = join(specDir, dir, 'script.elwood');
    const expectedPath = join(specDir, dir, 'expected.json');

    // Skip benchmark stubs (no script/expected)
    if (!existsSync(scriptPath) || !existsSync(expectedPath)) continue;

    // Find the first matching input file
    const inputExt = inputExtensions.find(ext => existsSync(join(specDir, dir, `input${ext}`)));
    if (!inputExt) continue;

    const inputPath = join(specDir, dir, `input${inputExt}`);
    const script = readFileSync(scriptPath, 'utf-8');
    const inputContent = readFileSync(inputPath, 'utf-8');

    // JSON input is parsed; all other formats (csv, txt, xml) are passed as raw strings
    const input = inputExt === '.json' ? JSON.parse(inputContent) : inputContent;
    const expected = JSON.parse(readFileSync(expectedPath, 'utf-8'));

    const bindingsPath = join(specDir, dir, 'bindings.json');
    const bindings = existsSync(bindingsPath)
      ? JSON.parse(readFileSync(bindingsPath, 'utf-8')) as Record<string, unknown>
      : undefined;

    cases.push({ name: dir, script, input, expected, bindings });
  }

  return cases;
}

function getPreviousTimings(lines: string[]): Map<string, number> {
  const firstRunIdx = lines.findIndex(l => l.startsWith('=== Run'));
  if (firstRunIdx < 0) return new Map();
  const result = new Map<string, number>();
  for (let i = firstRunIdx + 1; i < lines.length; i++) {
    const line = lines[i].trim();
    if (line.startsWith('=== Run') || line === '') break;
    const match = line.match(/^(.+?)\s+(\d+)ms/);
    if (match) result.set(match[1].trim(), parseInt(match[2]));
  }
  return result;
}

function deltaIndicator(name: string, ms: number, prev: Map<string, number>): string {
  const p = prev.get(name);
  if (p === undefined) return '  (new)';
  if (p === 0 && ms === 0) return '  (=)';
  if (p === 0) return ms <= 2 ? '  (~)' : `  (+${ms}ms)`;
  const ratio = ms / p;
  if (ratio > 2.0 && ms - p > 5) return `  (!! +${ms - p}ms SLOWER)`;
  if (ratio > 1.3 && ms - p > 3) return '  (+ slower)';
  if (ratio < 0.5 && p - ms > 5) return `  (-- ${p - ms}ms faster)`;
  if (ratio < 0.7 && p - ms > 3) return '  (- faster)';
  return '  (=)';
}

function flushTimingLog() {
  try {
    mkdirSync(timingLogDir, { recursive: true });

    const existing = existsSync(timingLogPath)
      ? readFileSync(timingLogPath, 'utf-8').split('\n')
      : [];

    const prev = getPreviousTimings(existing);

    // Build new run block sorted by name
    const now = new Date().toISOString().replace('T', ' ').slice(0, 19);
    const block = [`=== Run ${now} ===`];
    for (const { name, ms } of timings.sort((a, b) => a.name.localeCompare(b.name))) {
      const delta = deltaIndicator(name, ms, prev);
      block.push(`  ${name.padEnd(45)} ${String(ms).padStart(6)}ms${delta}`);
    }
    block.push('');

    // Prepend new run on top
    const lines = [...block, ...existing];

    // Keep last 10 runs
    const runStarts = lines.reduce<number[]>((acc, l, i) => {
      if (l.startsWith('=== Run')) acc.push(i);
      return acc;
    }, []);

    const output = runStarts.length > 10
      ? lines.slice(0, lines.findIndex((l, i) => i > runStarts[9] && l.startsWith('=== Run')) || lines.length)
      : lines;

    writeFileSync(timingLogPath, output.join('\n'));
  } catch { /* don't fail tests if logging fails */ }
}

const testCases = discoverTestCases();

describe('Conformance Tests', () => {
  it.each(testCases)('$name', ({ name, script, input, expected, bindings }) => {
    const isScript = script.trimStart().startsWith('let ') ||
                     script.includes('\nlet ') ||
                     script.includes('return ');

    const start = performance.now();
    const result = isScript
      ? execute(script, input, bindings)
      : evaluate(script.trim(), input, bindings);
    const ms = Math.round(performance.now() - start);

    timings.push({ name, ms });

    expect(result.success).toBe(true);
    expect(result.value).toEqual(expected);
  });

  it('should discover test cases', () => {
    expect(testCases.length).toBeGreaterThan(0);
    console.log(`Discovered ${testCases.length} conformance test cases`);
  });

  afterAll(() => {
    flushTimingLog();
  });
});
