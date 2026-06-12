import { describe, it, expect } from 'vitest';
import { execute, evaluate } from '../../src/index.js';
import { parseExpression } from '../../src/parser.js';

/**
 * Regression tests for runtime diagnostic positions. The parser used to build
 * path segment spans from the token *after* the consumed identifier, and the
 * evaluator threw plain Errors with no span at all — so runtime diagnostics
 * carried no line/column. Mirrors the .NET SpanRegressionTests; the let-binding
 * script below must report line 1, col 13 in both engines (cross-engine parity).
 */
describe('Runtime diagnostics', () => {
  it('reports property-not-found in a let binding at the property position', () => {
    const script = 'let foo = $.bar\n\nreturn {\n  orderDetails: $\n}';
    const result = execute(script, { data: 1, extensions: {} });

    expect(result.success).toBe(false);
    const diag = result.diagnostics[0];
    expect(diag.message).toContain("'bar'");
    expect(diag.line).toBe(1);
    expect(diag.column).toBe(13);
  });

  it('reports property-not-found in an expression at the property position', () => {
    const result = evaluate('$.metadata.missing', { metadata: { version: '1.0' } });

    expect(result.success).toBe(false);
    const diag = result.diagnostics[0];
    expect(diag.message).toContain("'missing'");
    expect(diag.line).toBe(1);
    expect(diag.column).toBe(12);
  });

  it('keeps suggestion as a separate field, not in the message', () => {
    const result = evaluate('$.naem', { name: 'Alice' });

    expect(result.success).toBe(false);
    const diag = result.diagnostics[0];
    expect(diag.message).toBe("Property 'naem' not found on Object.");
    expect(diag.suggestion).toContain("'name'");
  });

  it('attaches a position to runtime errors without their own span', () => {
    const result = evaluate('$.a + nope', { a: 1 });

    expect(result.success).toBe(false);
    const diag = result.diagnostics[0];
    expect(diag.message).toContain('nope');
    expect(diag.line).toBe(1);
    expect(diag.column).toBeGreaterThan(0);
  });
});

describe('Path segment spans', () => {
  function pathSegments(expression: string) {
    const { ast } = parseExpression(expression);
    expect(ast.type).toBe('Path');
    return (ast as any).segments;
  }

  it('property segments point at the identifier', () => {
    const segments = pathSegments('$.foo.bar');
    expect(segments[0].span).toMatchObject({ line: 1, column: 3 });
    expect(segments[0].span.end - segments[0].span.start).toBe('foo'.length);
    expect(segments[1].span).toMatchObject({ line: 1, column: 7 });
    expect(segments[1].span.end - segments[1].span.start).toBe('bar'.length);
  });

  it('optional chaining segments point at the identifier', () => {
    const segments = pathSegments('$?.foo');
    expect(segments[0].optional).toBe(true);
    expect(segments[0].span).toMatchObject({ line: 1, column: 4 });
  });

  it('recursive descent segments point at the identifier', () => {
    const segments = pathSegments('$.foo..name');
    expect(segments[1].type).toBe('RecursiveDescent');
    expect(segments[1].span).toMatchObject({ line: 1, column: 8 });
  });

  it('index and slice segments span the brackets', () => {
    const segments = pathSegments('$.items[0].parts[1:3]');
    expect(segments[1].type).toBe('Index');
    expect(segments[1].span).toMatchObject({ line: 1, column: 8 });
    expect(segments[1].span.end - segments[1].span.start).toBe('[0]'.length);
    expect(segments[3].type).toBe('Slice');
    expect(segments[3].span).toMatchObject({ line: 1, column: 17 });
    expect(segments[3].span.end - segments[3].span.start).toBe('[1:3]'.length);
  });
});
