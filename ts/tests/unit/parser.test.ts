import { describe, it, expect } from 'vitest';
import { parseExpression, parseScript } from '../../src/parser.js';

describe('Parser', () => {
  describe('Literals', () => {
    it('parses string literal', () => {
      const { ast } = parseExpression('"hello"');
      expect(ast.type).toBe('Literal');
      if (ast.type === 'Literal') expect(ast.value).toBe('hello');
    });

    it('parses number', () => {
      const { ast } = parseExpression('42');
      expect(ast.type).toBe('Literal');
      if (ast.type === 'Literal') expect(ast.value).toBe(42);
    });

    it('parses boolean', () => {
      expect(parseExpression('true').ast).toMatchObject({ type: 'Literal', value: true });
      expect(parseExpression('false').ast).toMatchObject({ type: 'Literal', value: false });
    });

    it('parses null', () => {
      expect(parseExpression('null').ast).toMatchObject({ type: 'Literal', value: null });
    });
  });

  describe('Paths', () => {
    it('parses $', () => {
      const { ast } = parseExpression('$');
      expect(ast.type).toBe('Path');
      if (ast.type === 'Path') {
        expect(ast.isRooted).toBe(true);
        expect(ast.segments).toHaveLength(0);
      }
    });

    it('parses $.field', () => {
      const { ast } = parseExpression('$.name');
      expect(ast.type).toBe('Path');
      if (ast.type === 'Path') {
        expect(ast.segments).toHaveLength(1);
        expect(ast.segments[0]).toMatchObject({ type: 'Property', name: 'name' });
      }
    });

    it('parses $.field[*]', () => {
      const { ast } = parseExpression('$.items[*]');
      expect(ast.type).toBe('Path');
      if (ast.type === 'Path') {
        expect(ast.segments).toHaveLength(2);
        expect(ast.segments[0]).toMatchObject({ type: 'Property', name: 'items' });
        expect(ast.segments[1]).toMatchObject({ type: 'Index', index: null });
      }
    });

    it('parses slice $[2:5]', () => {
      const { ast } = parseExpression('$.items[2:5]');
      if (ast.type === 'Path') {
        expect(ast.segments[1]).toMatchObject({ type: 'Slice', start: 2, end: 5 });
      }
    });
  });

  describe('Pipelines', () => {
    it('parses simple pipe', () => {
      const { ast } = parseExpression('$.items[*] | count');
      expect(ast.type).toBe('Pipeline');
      if (ast.type === 'Pipeline') {
        expect(ast.operations).toHaveLength(1);
        expect(ast.operations[0]).toMatchObject({ type: 'Aggregate', name: 'count' });
      }
    });

    it('parses where with lambda', () => {
      const { ast } = parseExpression('$.items[*] | where x => x.active');
      if (ast.type === 'Pipeline') {
        expect(ast.operations[0].type).toBe('Where');
      }
    });

    it('parses multi-stage pipeline', () => {
      const { ast } = parseExpression('$[*] | where x => x.a > 1 | select x => x.b | take 5');
      if (ast.type === 'Pipeline') {
        expect(ast.operations).toHaveLength(3);
        expect(ast.operations[0].type).toBe('Where');
        expect(ast.operations[1].type).toBe('Select');
        expect(ast.operations[2].type).toBe('Slice');
      }
    });
  });

  describe('Expressions', () => {
    it('parses binary arithmetic', () => {
      const { ast } = parseExpression('1 + 2 * 3');
      // Should be 1 + (2 * 3) due to precedence
      expect(ast.type).toBe('Binary');
      if (ast.type === 'Binary') {
        expect(ast.operator).toBe('Add');
        expect(ast.right.type).toBe('Binary');
      }
    });

    it('parses if/then/else', () => {
      const { ast } = parseExpression('if true then 1 else 2');
      expect(ast.type).toBe('If');
    });

    it('parses lambda', () => {
      const { ast } = parseExpression('x => x + 1');
      expect(ast.type).toBe('Lambda');
      if (ast.type === 'Lambda') {
        expect(ast.parameters).toEqual(['x']);
      }
    });

    it('parses multi-param lambda', () => {
      const { ast } = parseExpression('(a, b) => a + b');
      expect(ast.type).toBe('Lambda');
      if (ast.type === 'Lambda') {
        expect(ast.parameters).toEqual(['a', 'b']);
      }
    });

    it('parses object literal', () => {
      const { ast } = parseExpression('{ name: "test", age: 42 }');
      expect(ast.type).toBe('Object');
      if (ast.type === 'Object') {
        expect(ast.properties).toHaveLength(2);
        expect(ast.properties[0].key).toBe('name');
      }
    });

    it('parses spread in object', () => {
      const { ast } = parseExpression('{ ...x, y: 1 }');
      if (ast.type === 'Object') {
        expect(ast.properties[0].isSpread).toBe(true);
        expect(ast.properties[1].key).toBe('y');
      }
    });

    it('parses computed key', () => {
      const { ast } = parseExpression('{ [$.key]: "value" }');
      if (ast.type === 'Object') {
        expect(ast.properties[0].computedKey).toBeDefined();
      }
    });

    it('parses method call', () => {
      const { ast } = parseExpression('$.name.toLower()');
      expect(ast.type).toBe('MethodCall');
      if (ast.type === 'MethodCall') {
        expect(ast.methodName).toBe('toLower');
      }
    });

    it('parses function call', () => {
      const { ast } = parseExpression('range(1, 10)');
      expect(ast.type).toBe('FunctionCall');
      if (ast.type === 'FunctionCall') {
        expect(ast.functionName).toBe('range');
        expect(ast.arguments).toHaveLength(2);
      }
    });

    it('parses interpolated string', () => {
      const { ast } = parseExpression('`hello {$.name}`');
      expect(ast.type).toBe('InterpolatedString');
    });

    it('parses memo', () => {
      const { ast } = parseExpression('memo x => x + 1');
      expect(ast.type).toBe('Memo');
    });
  });

  describe('String concatenation edge cases', () => {
    it('parses simple three-part concat', () => {
      const { ast } = parseExpression('"a" + "b" + "c"');
      expect(ast.type).toBe('Binary');
      if (ast.type === 'Binary') {
        expect(ast.operator).toBe('Add');
        expect(ast.left.type).toBe('Binary'); // ("a" + "b")
        if (ast.left.type === 'Binary') {
          expect(ast.left.left).toMatchObject({ type: 'Literal', value: 'a' });
          expect(ast.left.right).toMatchObject({ type: 'Literal', value: 'b' });
        }
        expect(ast.right).toMatchObject({ type: 'Literal', value: 'c' });
      }
    });

    it('parses string + method + string (three-part concat)', () => {
      const { ast, diagnostics } = parseExpression('"a" + $.x.toString() + "b"');
      expect(diagnostics.filter(d => d.severity === 'error')).toHaveLength(0);
      expect(ast.type).toBe('Binary');
      if (ast.type === 'Binary') {
        expect(ast.left.type).toBe('Binary');
        expect(ast.right.type).toBe('Literal');
      }
    });

    it('parses the graphql concat expression', () => {
      const { ast, diagnostics } = parseExpression(
        '"query { products(first: " + $.pageSize.toString() + ") { edges { node { id } } } }"'
      );
      expect(diagnostics.filter(d => d.severity === 'error')).toHaveLength(0);
      expect(ast.type).toBe('Binary');
      if (ast.type === 'Binary') {
        expect(ast.operator).toBe('Add');
        // Right side should be the closing string
        expect(ast.right.type).toBe('Literal');
        if (ast.right.type === 'Literal') {
          expect(ast.right.value).toBe(') { edges { node { id } } } }');
        }
      }
    });
  });

  describe('Scripts', () => {
    it('parses let bindings', () => {
      const { ast } = parseScript('let x = 42\nlet y = x + 1\nreturn y');
      expect(ast.type).toBe('Script');
      expect(ast.bindings).toHaveLength(2);
      expect(ast.bindings[0].name).toBe('x');
      expect(ast.returnExpression).not.toBeNull();
    });

    it('parses implicit return', () => {
      const { ast } = parseScript('$.name');
      expect(ast.returnExpression).not.toBeNull();
    });
  });
});
