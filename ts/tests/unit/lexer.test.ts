import { describe, it, expect } from 'vitest';
import { tokenize } from '../../src/lexer.js';
import { TokenKind } from '../../src/token.js';

function kinds(source: string): TokenKind[] {
  const { tokens } = tokenize(source);
  return tokens.map(t => t.kind);
}

function texts(source: string): string[] {
  const { tokens } = tokenize(source);
  return tokens.map(t => t.text);
}

describe('Lexer', () => {
  describe('Literals', () => {
    it('tokenizes string literals', () => {
      expect(kinds('"hello"')).toEqual([TokenKind.StringLiteral, TokenKind.Eof]);
      expect(texts('"hello"')[0]).toBe('hello');
    });

    it('tokenizes single-quoted strings', () => {
      expect(texts("'world'")[0]).toBe('world');
    });

    it('handles escape sequences', () => {
      expect(texts('"line\\nbreak"')[0]).toBe('line\nbreak');
      expect(texts('"tab\\there"')[0]).toBe('tab\there');
    });

    it('tokenizes numbers', () => {
      expect(kinds('42')).toEqual([TokenKind.NumberLiteral, TokenKind.Eof]);
      expect(texts('42')[0]).toBe('42');
      expect(texts('3.14')[0]).toBe('3.14');
    });

    it('tokenizes booleans and null', () => {
      expect(kinds('true')).toEqual([TokenKind.TrueLiteral, TokenKind.Eof]);
      expect(kinds('false')).toEqual([TokenKind.FalseLiteral, TokenKind.Eof]);
      expect(kinds('null')).toEqual([TokenKind.NullLiteral, TokenKind.Eof]);
    });
  });

  describe('Paths', () => {
    it('tokenizes $ alone', () => {
      expect(kinds('$')).toEqual([TokenKind.Dollar, TokenKind.Eof]);
    });

    it('tokenizes $.field', () => {
      expect(kinds('$.name')).toEqual([TokenKind.DollarDot, TokenKind.Identifier, TokenKind.Eof]);
    });

    it('tokenizes $.field[*]', () => {
      expect(kinds('$.items[*]')).toEqual([
        TokenKind.DollarDot, TokenKind.Identifier,
        TokenKind.LeftBracket, TokenKind.Star, TokenKind.RightBracket,
        TokenKind.Eof,
      ]);
    });

    it('tokenizes recursive descent $..field', () => {
      expect(kinds('$..name')).toEqual([TokenKind.DotDot, TokenKind.Identifier, TokenKind.Eof]);
      expect(texts('$..name')[0]).toBe('$..');
    });
  });

  describe('Operators', () => {
    it('tokenizes pipe', () => {
      expect(kinds('|')).toEqual([TokenKind.Pipe, TokenKind.Eof]);
    });

    it('tokenizes fat arrow', () => {
      expect(kinds('=>')).toEqual([TokenKind.FatArrow, TokenKind.Eof]);
    });

    it('tokenizes comparison operators', () => {
      expect(kinds('==')).toEqual([TokenKind.EqualEqual, TokenKind.Eof]);
      expect(kinds('!=')).toEqual([TokenKind.BangEqual, TokenKind.Eof]);
      expect(kinds('<=')).toEqual([TokenKind.LessThanOrEqual, TokenKind.Eof]);
      expect(kinds('>=')).toEqual([TokenKind.GreaterThanOrEqual, TokenKind.Eof]);
      expect(kinds('<')).toEqual([TokenKind.LessThan, TokenKind.Eof]);
      expect(kinds('>')).toEqual([TokenKind.GreaterThan, TokenKind.Eof]);
    });

    it('tokenizes logical operators', () => {
      expect(kinds('&&')).toEqual([TokenKind.AmpersandAmpersand, TokenKind.Eof]);
      expect(kinds('||')).toEqual([TokenKind.PipePipe, TokenKind.Eof]);
      expect(kinds('!')).toEqual([TokenKind.Bang, TokenKind.Eof]);
    });

    it('tokenizes arithmetic', () => {
      expect(kinds('+ - * /')).toEqual([
        TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Eof,
      ]);
    });

    it('tokenizes spread', () => {
      expect(kinds('...')).toEqual([TokenKind.Spread, TokenKind.Eof]);
    });

    it('tokenizes dot and dotdot', () => {
      expect(kinds('..')).toEqual([TokenKind.DotDot, TokenKind.Eof]);
      expect(kinds('.')).toEqual([TokenKind.Dot, TokenKind.Eof]);
    });

    it('tokenizes assign vs equality', () => {
      expect(kinds('=')).toEqual([TokenKind.Assign, TokenKind.Eof]);
      expect(kinds('==')).toEqual([TokenKind.EqualEqual, TokenKind.Eof]);
    });
  });

  describe('Keywords', () => {
    it('recognizes all keywords', () => {
      expect(kinds('let')[0]).toBe(TokenKind.Let);
      expect(kinds('if')[0]).toBe(TokenKind.If);
      expect(kinds('then')[0]).toBe(TokenKind.Then);
      expect(kinds('else')[0]).toBe(TokenKind.Else);
      expect(kinds('match')[0]).toBe(TokenKind.Match);
      expect(kinds('return')[0]).toBe(TokenKind.Return);
      expect(kinds('asc')[0]).toBe(TokenKind.Asc);
      expect(kinds('desc')[0]).toBe(TokenKind.Desc);
      expect(kinds('on')[0]).toBe(TokenKind.On);
      expect(kinds('equals')[0]).toBe(TokenKind.Equals);
      expect(kinds('into')[0]).toBe(TokenKind.Into);
      expect(kinds('from')[0]).toBe(TokenKind.From);
      expect(kinds('memo')[0]).toBe(TokenKind.Memo);
      expect(kinds('_')[0]).toBe(TokenKind.Underscore);
    });

    it('treats unknown words as identifiers', () => {
      expect(kinds('foo')[0]).toBe(TokenKind.Identifier);
      expect(kinds('myVar')[0]).toBe(TokenKind.Identifier);
      expect(kinds('camelCase123')[0]).toBe(TokenKind.Identifier);
    });
  });

  describe('Interpolated strings', () => {
    it('tokenizes backtick strings', () => {
      expect(kinds('`hello {$.name}`')).toEqual([TokenKind.Backtick, TokenKind.Eof]);
      expect(texts('`hello {$.name}`')[0]).toBe('hello {$.name}');
    });

    it('handles nested braces', () => {
      expect(texts('`{a + {b}}`')[0]).toBe('{a + {b}}');
    });
  });

  describe('Comments', () => {
    it('skips single-line comments', () => {
      expect(kinds('42 // comment\n"hi"')).toEqual([
        TokenKind.NumberLiteral, TokenKind.StringLiteral, TokenKind.Eof,
      ]);
    });

    it('skips multi-line comments', () => {
      expect(kinds('42 /* block\ncomment */ "hi"')).toEqual([
        TokenKind.NumberLiteral, TokenKind.StringLiteral, TokenKind.Eof,
      ]);
    });
  });

  describe('Complex expressions', () => {
    it('tokenizes a pipeline', () => {
      const k = kinds('$.users[*] | where u => u.age > 18');
      expect(k).toEqual([
        TokenKind.DollarDot, TokenKind.Identifier,  // $.users
        TokenKind.LeftBracket, TokenKind.Star, TokenKind.RightBracket,  // [*]
        TokenKind.Pipe,  // |
        TokenKind.Identifier,  // where
        TokenKind.Identifier, TokenKind.FatArrow,  // u =>
        TokenKind.Identifier, TokenKind.Dot, TokenKind.Identifier,  // u.age
        TokenKind.GreaterThan,  // >
        TokenKind.NumberLiteral,  // 18
        TokenKind.Eof,
      ]);
    });

    it('tokenizes object literal with spread', () => {
      const k = kinds('{ ...o, name: "test" }');
      expect(k).toEqual([
        TokenKind.LeftBrace,
        TokenKind.Spread, TokenKind.Identifier, TokenKind.Comma,
        TokenKind.Identifier, TokenKind.Colon, TokenKind.StringLiteral,
        TokenKind.RightBrace,
        TokenKind.Eof,
      ]);
    });

    it('tokenizes let binding', () => {
      const k = kinds('let x = 42');
      expect(k).toEqual([
        TokenKind.Let, TokenKind.Identifier, TokenKind.Assign, TokenKind.NumberLiteral,
        TokenKind.Eof,
      ]);
    });

    it('tokenizes slice', () => {
      const k = kinds('$[2:5]');
      expect(k).toEqual([
        TokenKind.Dollar,
        TokenKind.LeftBracket, TokenKind.NumberLiteral, TokenKind.Colon, TokenKind.NumberLiteral, TokenKind.RightBracket,
        TokenKind.Eof,
      ]);
    });
  });

  describe('Position tracking', () => {
    it('tracks line and column', () => {
      const { tokens } = tokenize('a\nb');
      expect(tokens[0].span.line).toBe(1);
      expect(tokens[0].span.column).toBe(1);
      expect(tokens[1].span.line).toBe(2);
      expect(tokens[1].span.column).toBe(1);
    });
  });
});
