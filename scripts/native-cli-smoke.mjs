import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';

const binary = resolve(process.argv[2]);
const directory = mkdtempSync(join(tmpdir(), 'elwood-native-'));
function run(args) {
  const result = spawnSync(binary, args, { encoding: 'utf8', timeout: 30000 });
  assert.ifError(result.error);
  return result;
}
try {
  const expression = run(['eval', '1 + 2']);
  assert.equal(expression.status, 0, expression.stderr);
  assert.equal(JSON.parse(expression.stdout), 3);
  const input = join(directory, 'input.json');
  const script = join(directory, 'transform.elwood');
  writeFileSync(input, '{"value":41}');
  writeFileSync(script, 'let value = $.value\nreturn value + 1\n');
  const transformation = run(['run', script, '--input', input]);
  assert.equal(transformation.status, 0, transformation.stderr);
  assert.equal(JSON.parse(transformation.stdout), 42);
  const pipeline = run(['pipeline', 'validate', join(directory, 'unused.yaml')]);
  assert.equal(pipeline.status, 2, pipeline.stderr);
  assert.match(pipeline.stderr, /require the managed Elwood\.Cli \.NET tool/);
  console.log('Native DSL eval/run and explicit managed-only pipeline diagnostic passed.');
} finally {
  assert.equal(dirname(resolve(directory)), resolve(tmpdir()));
  assert.ok(basename(directory).startsWith('elwood-native-'));
  rmSync(directory, { recursive: true, force: true });
}
