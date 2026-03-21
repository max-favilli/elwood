import { Token, TokenKind, SourceSpan } from './token.js';

export interface Diagnostic {
  severity: 'error' | 'warning' | 'info';
  message: string;
  span: SourceSpan;
}

const KEYWORDS = new Map<string, TokenKind>([
  ['let', TokenKind.Let],
  ['if', TokenKind.If],
  ['then', TokenKind.Then],
  ['else', TokenKind.Else],
  ['match', TokenKind.Match],
  ['return', TokenKind.Return],
  ['true', TokenKind.TrueLiteral],
  ['false', TokenKind.FalseLiteral],
  ['null', TokenKind.NullLiteral],
  ['asc', TokenKind.Asc],
  ['desc', TokenKind.Desc],
  ['on', TokenKind.On],
  ['equals', TokenKind.Equals],
  ['into', TokenKind.Into],
  ['from', TokenKind.From],
  ['memo', TokenKind.Memo],
  ['_', TokenKind.Underscore],
]);

const SINGLE_CHAR: Record<string, TokenKind> = {
  '.': TokenKind.Dot,
  '|': TokenKind.Pipe,
  ',': TokenKind.Comma,
  ':': TokenKind.Colon,
  '(': TokenKind.LeftParen,
  ')': TokenKind.RightParen,
  '[': TokenKind.LeftBracket,
  ']': TokenKind.RightBracket,
  '{': TokenKind.LeftBrace,
  '}': TokenKind.RightBrace,
  '*': TokenKind.Star,
  '+': TokenKind.Plus,
  '-': TokenKind.Minus,
  '/': TokenKind.Slash,
  '<': TokenKind.LessThan,
  '>': TokenKind.GreaterThan,
  '!': TokenKind.Bang,
  '=': TokenKind.Assign,
};

const TWO_CHAR: Record<string, TokenKind> = {
  '=>': TokenKind.FatArrow,
  '==': TokenKind.EqualEqual,
  '!=': TokenKind.BangEqual,
  '<=': TokenKind.LessThanOrEqual,
  '>=': TokenKind.GreaterThanOrEqual,
  '&&': TokenKind.AmpersandAmpersand,
  '||': TokenKind.PipePipe,
  '..': TokenKind.DotDot,
};

function isDigit(c: string): boolean {
  return c >= '0' && c <= '9';
}

function isLetter(c: string): boolean {
  return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}

function isLetterOrDigit(c: string): boolean {
  return isLetter(c) || isDigit(c);
}

/**
 * Tokenizes Elwood source text into a sequence of tokens.
 */
export function tokenize(source: string): { tokens: Token[]; diagnostics: Diagnostic[] } {
  let pos = 0;
  let line = 1;
  let col = 1;
  const tokens: Token[] = [];
  const diagnostics: Diagnostic[] = [];

  function current(): string {
    return pos < source.length ? source[pos] : '\0';
  }

  function peek(offset: number): string | undefined {
    return pos + offset < source.length ? source[pos + offset] : undefined;
  }

  function advance(): void {
    pos++;
    col++;
  }

  function makeToken(kind: TokenKind, text: string, start: number, end: number, startLine: number, startCol: number): Token {
    return { kind, text, span: { start, end, line: startLine, column: startCol } };
  }

  function skipWhitespaceAndComments(): void {
    while (pos < source.length) {
      const c = source[pos];

      if (c === ' ' || c === '\t' || c === '\r') {
        advance();
        continue;
      }

      if (c === '\n') {
        advance();
        line++;
        col = 1;
        continue;
      }

      // Single-line comment: //
      if (c === '/' && peek(1) === '/') {
        while (pos < source.length && source[pos] !== '\n') advance();
        continue;
      }

      // Multi-line comment: /* ... */
      if (c === '/' && peek(1) === '*') {
        advance(); advance();
        while (pos < source.length - 1 && !(source[pos] === '*' && source[pos + 1] === '/')) {
          if (source[pos] === '\n') { line++; col = 1; }
          advance();
        }
        if (pos < source.length - 1) { advance(); advance(); }
        continue;
      }

      break;
    }
  }

  function readString(quote: string): Token {
    const start = pos;
    const startLine = line;
    const startCol = col;
    advance(); // skip opening quote

    let value = '';
    while (pos < source.length && source[pos] !== quote) {
      if (source[pos] === '\\' && pos + 1 < source.length) {
        advance();
        const escaped = source[pos];
        switch (escaped) {
          case 'n': value += '\n'; break;
          case 't': value += '\t'; break;
          case 'r': value += '\r'; break;
          case '\\': value += '\\'; break;
          default:
            if (escaped === quote) value += quote;
            else value += escaped;
        }
        advance();
      } else {
        value += source[pos];
        advance();
      }
    }

    if (pos < source.length) advance(); // skip closing quote

    return makeToken(TokenKind.StringLiteral, value, start, pos, startLine, startCol);
  }

  function readInterpolatedString(): Token {
    const start = pos;
    const startLine = line;
    const startCol = col;
    advance(); // skip opening backtick

    let value = '';
    let depth = 0;
    while (pos < source.length && !(source[pos] === '`' && depth === 0)) {
      if (source[pos] === '{') depth++;
      else if (source[pos] === '}') depth--;
      value += source[pos];
      advance();
    }

    if (pos < source.length) advance(); // skip closing backtick

    return makeToken(TokenKind.Backtick, value, start, pos, startLine, startCol);
  }

  function readNumber(): Token {
    const start = pos;
    const startLine = line;
    const startCol = col;

    while (pos < source.length && (isDigit(source[pos]) || source[pos] === '.')) {
      advance();
    }

    return makeToken(TokenKind.NumberLiteral, source.slice(start, pos), start, pos, startLine, startCol);
  }

  function readIdentifierOrKeyword(): Token {
    const start = pos;
    const startLine = line;
    const startCol = col;

    while (pos < source.length && (isLetterOrDigit(source[pos]) || source[pos] === '_')) {
      advance();
    }

    const text = source.slice(start, pos);
    const kind = KEYWORDS.get(text) ?? TokenKind.Identifier;

    return makeToken(kind, text, start, pos, startLine, startCol);
  }

  function readToken(): Token {
    const start = pos;
    const startLine = line;
    const startCol = col;
    const c = source[pos];

    // String literals
    if (c === '"' || c === "'") return readString(c);

    // Backtick string (interpolated)
    if (c === '`') return readInterpolatedString();

    // Numbers
    if (isDigit(c) || (c === '.' && peek(1) !== undefined && isDigit(peek(1)!))) {
      return readNumber();
    }

    // $ (dollar — start of path)
    if (c === '$') {
      advance();
      if (current() === '.') {
        if (peek(1) === '.') {
          advance(); advance();
          return makeToken(TokenKind.DotDot, '$..', start, pos, startLine, startCol);
        }
        advance();
        return makeToken(TokenKind.DollarDot, '$.', start, pos, startLine, startCol);
      }
      return makeToken(TokenKind.Dollar, '$', start, pos, startLine, startCol);
    }

    // Identifiers and keywords
    if (isLetter(c) || c === '_') return readIdentifierOrKeyword();

    // Three-character operators
    if (pos + 2 < source.length && source.slice(pos, pos + 3) === '...') {
      advance(); advance(); advance();
      return makeToken(TokenKind.Spread, '...', start, pos, startLine, startCol);
    }

    // Two-character operators
    if (pos + 1 < source.length) {
      const two = source.slice(pos, pos + 2);
      const kind2 = TWO_CHAR[two];
      if (kind2 !== undefined) {
        advance(); advance();
        return makeToken(kind2, two, start, pos, startLine, startCol);
      }
    }

    // Single-character operators
    advance();
    const kind1 = SINGLE_CHAR[c];
    if (kind1 !== undefined) {
      return makeToken(kind1, c, start, pos, startLine, startCol);
    }

    // Newline (already advanced)
    if (c === '\n') {
      return makeToken(TokenKind.Eof, '', start, pos, startLine, startCol); // skipped by caller
    }

    diagnostics.push({
      severity: 'error',
      message: `Unexpected character '${c}'`,
      span: { start, end: pos, line: startLine, column: startCol },
    });

    return makeToken(TokenKind.Eof, '', start, pos, startLine, startCol);
  }

  // Main loop
  while (pos < source.length) {
    skipWhitespaceAndComments();
    if (pos >= source.length) break;

    const token = readToken();
    if (token.kind !== TokenKind.Eof || pos >= source.length) {
      tokens.push(token);
    }
  }

  tokens.push(makeToken(TokenKind.Eof, '', pos, pos, line, col));
  return { tokens, diagnostics };
}
