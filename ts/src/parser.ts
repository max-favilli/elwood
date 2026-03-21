import type { Token, SourceSpan } from './token.js';
import { TokenKind } from './token.js';
import { tokenize, type Diagnostic } from './lexer.js';
import type {
  ElwoodExpression, ScriptNode, LetBindingNode, PipeOperation, PathSegment,
  ObjectProperty, MatchArm, InterpolationPart, JoinMode,
} from './ast.js';

export class ParseError extends Error {
  constructor(public diagnostic: Diagnostic) { super(diagnostic.message); }
}

export function parseExpression(source: string): { ast: ElwoodExpression; diagnostics: Diagnostic[] } {
  const { tokens, diagnostics: lexDiags } = tokenize(source);
  const parser = new Parser(tokens);
  const ast = parser.parseExpression();
  return { ast, diagnostics: [...lexDiags, ...parser.diagnostics] };
}

export function parseScript(source: string): { ast: ScriptNode; diagnostics: Diagnostic[] } {
  const { tokens, diagnostics: lexDiags } = tokenize(source);
  const parser = new Parser(tokens);
  const ast = parser.parseScript();
  return { ast, diagnostics: [...lexDiags, ...parser.diagnostics] };
}

class Parser {
  private pos = 0;
  private pipeDepth = 0;
  readonly diagnostics: Diagnostic[] = [];

  constructor(private tokens: Token[]) {}

  // ── Entry points ──

  parseScript(): ScriptNode {
    const bindings: LetBindingNode[] = [];
    let returnExpr: ElwoodExpression | null = null;
    const start = this.current().span;

    while (!this.isAtEnd()) {
      if (this.check(TokenKind.Let)) {
        bindings.push(this.parseLetBinding());
      } else if (this.check(TokenKind.Return)) {
        this.advance();
        returnExpr = this.parseExpression();
        break;
      } else {
        returnExpr = this.parseExpression();
        if (!this.isAtEnd() && !this.check(TokenKind.Eof)) {
          this.error(`Unexpected token '${this.current().text}'. Did you mean 'let'?`);
          break;
        }
      }
    }

    return { type: 'Script', bindings, returnExpression: returnExpr, span: this.span(start) };
  }

  parseExpression(): ElwoodExpression {
    return this.parsePipeline();
  }

  // ── Pipeline ──

  private parsePipeline(): ElwoodExpression {
    const start = this.current().span;
    let expr = this.parseTernary();
    const ops: PipeOperation[] = [];

    while (this.match(TokenKind.Pipe)) {
      ops.push(this.parsePipeOperation());
    }

    return ops.length > 0 ? { type: 'Pipeline', source: expr, operations: ops, span: this.span(start) } : expr;
  }

  private parsePipeOperation(): PipeOperation {
    const start = this.current().span;
    const name = this.current().text;

    if (this.check(TokenKind.Match)) {
      this.advance();
      return { type: 'MatchOp', arms: this.parseMatchArms(), span: this.span(start) };
    }

    if (!this.check(TokenKind.Identifier)) {
      this.error(`Expected pipe operator name after '|', got '${this.current().text}'`);
      return { type: 'Aggregate', name: 'error', span: this.span(start) };
    }

    this.advance();

    switch (name) {
      case 'where': return { type: 'Where', predicate: this.parsePipeArg(), span: this.span(start) };
      case 'select': return { type: 'Select', projection: this.parsePipeArg(), span: this.span(start) };
      case 'selectMany': return { type: 'SelectMany', projection: this.parsePipeArg(), span: this.span(start) };
      case 'orderBy': return this.parseOrderBy(start);
      case 'groupBy': return { type: 'GroupBy', keySelector: this.parsePipeArg(), span: this.span(start) };
      case 'distinct': return { type: 'Distinct', span: this.span(start) };
      case 'first': case 'last': return this.parseFirstLast(name, start);
      case 'count': case 'sum': case 'min': case 'max': case 'index':
        return { type: 'Aggregate', name, span: this.span(start) };
      case 'take': case 'skip':
        return { type: 'Slice', kind: name as 'take' | 'skip', count: this.parsePipeArg(), span: this.span(start) };
      case 'batch': return { type: 'Batch', size: this.parsePipeArg(), span: this.span(start) };
      case 'join': return this.parseJoin(start);
      case 'concat': return this.parseConcat(start);
      case 'reduce': return this.parseReduce(start);
      case 'any': case 'all':
        return { type: 'Quantifier', kind: name as 'any' | 'all', predicate: this.parsePipeArg(), span: this.span(start) };
      default: throw this.error(`Unknown pipe operator '${name}'`);
    }
  }

  private parsePipeArg(): ElwoodExpression {
    this.pipeDepth++;
    try { return this.parseTernary(); }
    finally { this.pipeDepth--; }
  }

  private parseOrderBy(start: SourceSpan): PipeOperation {
    const keys: { key: ElwoodExpression; ascending: boolean }[] = [];
    do {
      const key = this.parsePipeArg();
      let ascending = true;
      if (this.check(TokenKind.Asc)) { this.advance(); ascending = true; }
      else if (this.check(TokenKind.Desc)) { this.advance(); ascending = false; }
      keys.push({ key, ascending });
    } while (this.match(TokenKind.Comma));
    return { type: 'OrderBy', keys, span: this.span(start) };
  }

  private parseFirstLast(name: string, start: SourceSpan): PipeOperation {
    if (this.isAtEnd() || this.check(TokenKind.Pipe) || this.check(TokenKind.RightBrace) ||
        this.check(TokenKind.RightParen) || this.check(TokenKind.Eof) || this.check(TokenKind.Comma)) {
      return { type: 'Aggregate', name, span: this.span(start) };
    }
    return { type: 'Aggregate', name, predicate: this.parsePipeArg(), span: this.span(start) };
  }

  private parseReduce(start: SourceSpan): PipeOperation {
    const accumulator = this.parsePipeArg();
    let initialValue: ElwoodExpression | undefined;
    if (this.match(TokenKind.From)) initialValue = this.parsePipeArg();
    return { type: 'Reduce', accumulator, initialValue, span: this.span(start) };
  }

  private parseConcat(start: SourceSpan): PipeOperation {
    if (this.isAtEnd() || this.check(TokenKind.Pipe) || this.check(TokenKind.RightBrace) ||
        this.check(TokenKind.RightParen) || this.check(TokenKind.Eof) || this.check(TokenKind.Comma)) {
      return { type: 'Concat', span: this.span(start) };
    }
    return { type: 'Concat', separator: this.parsePipeArg(), span: this.span(start) };
  }

  private parseJoin(start: SourceSpan): PipeOperation {
    const source = this.parsePipeArg();
    this.expect(TokenKind.On, "Expected 'on' after join source");
    const leftKey = this.parsePipeArg();
    this.expect(TokenKind.Equals, "Expected 'equals' in join");
    const rightKey = this.parsePipeArg();

    let intoAlias: string | undefined;
    if (this.match(TokenKind.Into)) {
      intoAlias = this.expect(TokenKind.Identifier, "Expected alias after 'into'").text;
    }

    let mode: JoinMode = 'inner';
    if (this.check(TokenKind.Identifier)) {
      const m = this.current().text.toLowerCase();
      if (m === 'inner' || m === 'left' || m === 'right' || m === 'full') {
        mode = m; this.advance();
      }
    }

    return { type: 'Join', source, leftKey, rightKey, intoAlias, mode, span: this.span(start) };
  }

  // ── Ternary ──

  private parseTernary(): ElwoodExpression {
    if (this.match(TokenKind.If)) {
      const start = this.previous().span;
      const condition = this.parseOr();
      this.expect(TokenKind.Then, "Expected 'then'");
      const thenBranch = this.parseTernary();
      this.expect(TokenKind.Else, "Expected 'else'");
      const elseBranch = this.parseTernary();
      return { type: 'If', condition, thenBranch, elseBranch, span: this.span(start) };
    }
    return this.parseOr();
  }

  // ── Boolean ──

  private parseOr(): ElwoodExpression {
    const start = this.current().span;
    let left = this.parseAnd();
    while (this.match(TokenKind.PipePipe)) {
      left = { type: 'Binary', left, operator: 'Or', right: this.parseAnd(), span: this.span(start) };
    }
    return left;
  }

  private parseAnd(): ElwoodExpression {
    const start = this.current().span;
    let left = this.parseEquality();
    while (this.match(TokenKind.AmpersandAmpersand)) {
      left = { type: 'Binary', left, operator: 'And', right: this.parseEquality(), span: this.span(start) };
    }
    return left;
  }

  // ── Comparison ──

  private parseEquality(): ElwoodExpression {
    const start = this.current().span;
    let left = this.parseComparison();
    while (this.check(TokenKind.EqualEqual) || this.check(TokenKind.BangEqual)) {
      const op = this.advance().kind === TokenKind.EqualEqual ? 'Equal' as const : 'NotEqual' as const;
      left = { type: 'Binary', left, operator: op, right: this.parseComparison(), span: this.span(start) };
    }
    return left;
  }

  private parseComparison(): ElwoodExpression {
    const start = this.current().span;
    let left = this.parseAdditive();
    while (this.check(TokenKind.LessThan) || this.check(TokenKind.LessThanOrEqual) ||
           this.check(TokenKind.GreaterThan) || this.check(TokenKind.GreaterThanOrEqual)) {
      const tok = this.advance();
      const op = tok.kind === TokenKind.LessThan ? 'LessThan' as const
        : tok.kind === TokenKind.LessThanOrEqual ? 'LessThanOrEqual' as const
        : tok.kind === TokenKind.GreaterThan ? 'GreaterThan' as const
        : 'GreaterThanOrEqual' as const;
      left = { type: 'Binary', left, operator: op, right: this.parseAdditive(), span: this.span(start) };
    }
    return left;
  }

  // ── Arithmetic ──

  private parseAdditive(): ElwoodExpression {
    const start = this.current().span;
    let left = this.parseMultiplicative();
    while (this.check(TokenKind.Plus) || this.check(TokenKind.Minus)) {
      const op = this.advance().kind === TokenKind.Plus ? 'Add' as const : 'Subtract' as const;
      left = { type: 'Binary', left, operator: op, right: this.parseMultiplicative(), span: this.span(start) };
    }
    return left;
  }

  private parseMultiplicative(): ElwoodExpression {
    const start = this.current().span;
    let left = this.parseUnary();
    while (this.check(TokenKind.Star) || this.check(TokenKind.Slash)) {
      const op = this.advance().kind === TokenKind.Star ? 'Multiply' as const : 'Divide' as const;
      left = { type: 'Binary', left, operator: op, right: this.parseUnary(), span: this.span(start) };
    }
    return left;
  }

  private parseUnary(): ElwoodExpression {
    if (this.match(TokenKind.Bang))
      return { type: 'Unary', operator: 'Not', operand: this.parseUnary(), span: this.span(this.previous().span) };
    if (this.match(TokenKind.Minus))
      return { type: 'Unary', operator: 'Negate', operand: this.parseUnary(), span: this.span(this.previous().span) };
    return this.parsePostfix();
  }

  // ── Postfix ──

  private parsePostfix(): ElwoodExpression {
    let expr = this.parsePrimary();

    while (true) {
      if (this.match(TokenKind.Dot)) {
        const start = this.previous().span;
        const name = this.expect(TokenKind.Identifier, "Expected property name after '.'").text;
        if (this.match(TokenKind.LeftParen)) {
          const args = this.parseArgList();
          this.expect(TokenKind.RightParen, "Expected ')'");
          expr = { type: 'MethodCall', target: expr, methodName: name, arguments: args, span: this.span(start) };
        } else {
          expr = { type: 'MemberAccess', target: expr, memberName: name, span: this.span(start) };
        }
      } else if (this.match(TokenKind.LeftBracket)) {
        const start = this.previous().span;
        if (this.match(TokenKind.Star)) {
          this.expect(TokenKind.RightBracket, "Expected ']'");
          expr = { type: 'Index', target: expr, index: null, span: this.span(start) };
        } else {
          const index = this.parseExpression();
          this.expect(TokenKind.RightBracket, "Expected ']'");
          expr = { type: 'Index', target: expr, index, span: this.span(start) };
        }
      } else {
        break;
      }
    }

    return expr;
  }

  // ── Primary ──

  private parsePrimary(): ElwoodExpression {
    const start = this.current().span;

    // Dollar path
    if (this.check(TokenKind.DollarDot) || this.check(TokenKind.Dollar))
      return this.parsePath();

    // Identifier / lambda / function call
    if (this.check(TokenKind.Identifier))
      return this.parseIdentifierOrLambda();

    // Literals
    if (this.match(TokenKind.StringLiteral))
      return { type: 'Literal', value: this.previous().text, span: this.span(start) };
    if (this.match(TokenKind.NumberLiteral))
      return { type: 'Literal', value: parseFloat(this.previous().text), span: this.span(start) };
    if (this.match(TokenKind.TrueLiteral))
      return { type: 'Literal', value: true, span: this.span(start) };
    if (this.match(TokenKind.FalseLiteral))
      return { type: 'Literal', value: false, span: this.span(start) };
    if (this.match(TokenKind.NullLiteral))
      return { type: 'Literal', value: null, span: this.span(start) };

    // Interpolated string
    if (this.match(TokenKind.Backtick))
      return this.parseInterpolatedContent(this.previous().text, start);

    // Object literal
    if (this.check(TokenKind.LeftBrace))
      return this.parseObjectLiteral();

    // Array literal
    if (this.match(TokenKind.LeftBracket)) {
      const items: ElwoodExpression[] = [];
      if (!this.check(TokenKind.RightBracket)) {
        do { items.push(this.parseExpression()); } while (this.match(TokenKind.Comma));
      }
      this.expect(TokenKind.RightBracket, "Expected ']'");
      return { type: 'Array', items, span: this.span(start) };
    }

    // Parenthesized or multi-param lambda
    if (this.match(TokenKind.LeftParen)) {
      if (this.check(TokenKind.Identifier) && this.lookAheadMultiParamLambda()) {
        const params: string[] = [];
        do { params.push(this.expect(TokenKind.Identifier, "Expected parameter name").text); } while (this.match(TokenKind.Comma));
        this.expect(TokenKind.RightParen, "Expected ')'");
        this.expect(TokenKind.FatArrow, "Expected '=>'");
        const body = this.pipeDepth > 0 ? this.parseTernary() : this.parseExpression();
        return { type: 'Lambda', parameters: params, body, span: this.span(start) };
      }
      const expr = this.parseExpression();
      this.expect(TokenKind.RightParen, "Expected ')'");
      return expr;
    }

    // Memo
    if (this.match(TokenKind.Memo)) {
      const saved = this.pipeDepth;
      this.pipeDepth = 0;
      try {
        const inner = this.parseTernary();
        if (inner.type !== 'Lambda') throw this.error("Expected lambda after 'memo'");
        return { type: 'Memo', lambda: inner, span: this.span(start) };
      } finally {
        this.pipeDepth = saved;
      }
    }

    // Wildcard
    if (this.match(TokenKind.Underscore))
      return { type: 'Identifier', name: '_', span: this.span(start) };

    throw this.error(`Unexpected token '${this.current().text}'`);
  }

  // ── Path ──

  private parsePath(): ElwoodExpression {
    const start = this.current().span;
    const segments: PathSegment[] = [];

    if (this.match(TokenKind.DollarDot)) {
      segments.push(...this.parsePathSegments());
    } else {
      this.match(TokenKind.Dollar); // standalone $
    }

    return { type: 'Path', segments, isRooted: true, span: this.span(start) };
  }

  private parsePathSegments(): PathSegment[] {
    const segments: PathSegment[] = [];

    if (this.check(TokenKind.Identifier)) {
      segments.push({ type: 'Property', name: this.advance().text, span: this.span(this.current().span) });
    }

    while (true) {
      if (this.match(TokenKind.DotDot)) {
        const name = this.expect(TokenKind.Identifier, "Expected name after '..'").text;
        segments.push({ type: 'RecursiveDescent', name, span: this.span(this.current().span) });
      } else if (this.check(TokenKind.Dot) && !this.check(TokenKind.DotDot)) {
        // Stop if .identifier( — it's a method call
        if (this.peekAt(1)?.kind === TokenKind.Identifier && this.peekAt(2)?.kind === TokenKind.LeftParen) break;
        this.advance();
        if (this.check(TokenKind.Identifier)) {
          segments.push({ type: 'Property', name: this.advance().text, span: this.span(this.current().span) });
        } else break;
      } else if (this.match(TokenKind.LeftBracket)) {
        if (this.match(TokenKind.Star)) {
          this.expect(TokenKind.RightBracket, "Expected ']'");
          segments.push({ type: 'Index', index: null, span: this.span(this.current().span) });
        } else if (this.check(TokenKind.NumberLiteral) || this.check(TokenKind.Minus) || this.check(TokenKind.Colon)) {
          const s = this.tryParseBracketInt();
          if (this.match(TokenKind.Colon)) {
            const e = this.tryParseBracketInt();
            this.expect(TokenKind.RightBracket, "Expected ']'");
            segments.push({ type: 'Slice', start: s, end: e, span: this.span(this.current().span) });
          } else if (s !== null) {
            this.expect(TokenKind.RightBracket, "Expected ']'");
            segments.push({ type: 'Index', index: s, span: this.span(this.current().span) });
          } else break;
        } else break;
      } else break;
    }

    return segments;
  }

  private tryParseBracketInt(): number | null {
    const negate = this.match(TokenKind.Minus);
    if (this.check(TokenKind.NumberLiteral)) {
      const val = parseInt(this.advance().text, 10);
      return negate ? -val : val;
    }
    return null;
  }

  // ── Identifier / Lambda / Function call ──

  private parseIdentifierOrLambda(): ElwoodExpression {
    const start = this.current().span;
    const name = this.advance().text;

    if (this.check(TokenKind.FatArrow)) {
      this.advance();
      const body = this.pipeDepth > 0 ? this.parseTernary() : this.parseExpression();
      return { type: 'Lambda', parameters: [name], body, span: this.span(start) };
    }

    if (this.match(TokenKind.LeftParen)) {
      const args = this.parseArgList();
      this.expect(TokenKind.RightParen, "Expected ')'");
      return { type: 'FunctionCall', functionName: name, arguments: args, span: this.span(start) };
    }

    return { type: 'Identifier', name, span: this.span(start) };
  }

  // ── Object literal ──

  private parseObjectLiteral(): ElwoodExpression {
    const start = this.current().span;
    this.expect(TokenKind.LeftBrace, "Expected '{'");
    const properties: ObjectProperty[] = [];

    if (!this.check(TokenKind.RightBrace)) {
      do {
        const propStart = this.current().span;

        if (this.match(TokenKind.Spread)) {
          properties.push({ key: '', value: this.parseExpression(), span: this.span(propStart), isSpread: true });
          continue;
        }

        if (this.match(TokenKind.LeftBracket)) {
          const keyExpr = this.parseExpression();
          this.expect(TokenKind.RightBracket, "Expected ']'");
          this.expect(TokenKind.Colon, "Expected ':'");
          properties.push({ key: '', value: this.parseExpression(), span: this.span(propStart), computedKey: keyExpr });
          continue;
        }

        let key: string;
        if (this.check(TokenKind.Identifier)) key = this.advance().text;
        else if (this.check(TokenKind.StringLiteral)) key = this.advance().text;
        else throw this.error(`Expected property name, got '${this.current().text}'`);

        this.expect(TokenKind.Colon, "Expected ':'");
        properties.push({ key, value: this.parseExpression(), span: this.span(propStart) });
      } while (this.match(TokenKind.Comma));
    }

    this.expect(TokenKind.RightBrace, "Expected '}'");
    return { type: 'Object', properties, span: this.span(start) };
  }

  // ── Interpolated string ──

  private parseInterpolatedContent(raw: string, start: SourceSpan): ElwoodExpression {
    const parts: InterpolationPart[] = [];
    let text = '';
    let i = 0;

    while (i < raw.length) {
      if (raw[i] === '{') {
        if (text) { parts.push({ type: 'Text', text, span: start }); text = ''; }
        i++;
        let depth = 1;
        let exprText = '';
        while (i < raw.length && depth > 0) {
          if (raw[i] === '{') depth++;
          else if (raw[i] === '}') { depth--; if (depth === 0) { i++; break; } }
          exprText += raw[i]; i++;
        }
        const { ast } = parseExpression(exprText);
        parts.push({ type: 'Expression', expression: ast, span: start });
      } else {
        text += raw[i]; i++;
      }
    }

    if (text) parts.push({ type: 'Text', text, span: start });
    return { type: 'InterpolatedString', parts, span: start };
  }

  // ── Match arms ──

  private parseMatchArms(): MatchArm[] {
    const arms: MatchArm[] = [];
    while (!this.isAtEnd() && !this.check(TokenKind.Pipe) && !this.check(TokenKind.Eof) &&
           !this.check(TokenKind.RightBrace) && !this.check(TokenKind.RightParen)) {
      const armStart = this.current().span;
      let pattern: ElwoodExpression | null;
      if (this.check(TokenKind.Underscore)) { this.advance(); pattern = null; }
      else pattern = this.parsePrimary();
      this.expect(TokenKind.FatArrow, "Expected '=>'");
      const result = this.parseTernary();
      arms.push({ pattern, result, span: this.span(armStart) });
      this.match(TokenKind.Comma);
    }
    return arms;
  }

  // ── Let binding ──

  private parseLetBinding(): LetBindingNode {
    const start = this.current().span;
    this.expect(TokenKind.Let, "Expected 'let'");
    const name = this.expect(TokenKind.Identifier, "Expected variable name").text;
    this.expect(TokenKind.Assign, "Expected '='");
    const value = this.parseExpression();
    return { type: 'LetBinding', name, value, span: this.span(start) };
  }

  // ── Helpers ──

  private parseArgList(): ElwoodExpression[] {
    const args: ElwoodExpression[] = [];
    if (!this.check(TokenKind.RightParen)) {
      do { args.push(this.parseExpression()); } while (this.match(TokenKind.Comma));
    }
    return args;
  }

  private lookAheadMultiParamLambda(): boolean {
    const saved = this.pos;
    try {
      if (!this.check(TokenKind.Identifier)) return false;
      this.advance();
      while (this.check(TokenKind.Comma)) {
        this.advance();
        if (!this.check(TokenKind.Identifier)) return false;
        this.advance();
      }
      if (!this.check(TokenKind.RightParen)) return false;
      this.advance();
      return this.check(TokenKind.FatArrow);
    } finally { this.pos = saved; }
  }

  private current(): Token { return this.pos < this.tokens.length ? this.tokens[this.pos] : this.tokens[this.tokens.length - 1]; }
  private previous(): Token { return this.tokens[this.pos - 1]; }
  private isAtEnd(): boolean { return this.pos >= this.tokens.length || this.current().kind === TokenKind.Eof; }
  private check(kind: TokenKind): boolean { return !this.isAtEnd() && this.current().kind === kind; }
  private peekAt(offset: number): Token | undefined { return this.tokens[this.pos + offset]; }

  private match(kind: TokenKind): boolean {
    if (!this.check(kind)) return false;
    this.pos++;
    return true;
  }

  private advance(): Token {
    const token = this.current();
    this.pos++;
    return token;
  }

  private expect(kind: TokenKind, message: string): Token {
    if (this.check(kind)) return this.advance();
    throw this.error(message);
  }

  private span(start: SourceSpan): SourceSpan {
    return { start: start.start, end: this.current().span.end, line: start.line, column: start.column };
  }

  private error(message: string): ParseError {
    const diag: Diagnostic = {
      severity: 'error',
      message,
      span: this.current().span,
    };
    this.diagnostics.push(diag);
    return new ParseError(diag);
  }
}
