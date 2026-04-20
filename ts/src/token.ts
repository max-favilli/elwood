export enum TokenKind {
  // Literals
  StringLiteral = 'StringLiteral',
  NumberLiteral = 'NumberLiteral',
  TrueLiteral = 'TrueLiteral',
  FalseLiteral = 'FalseLiteral',
  NullLiteral = 'NullLiteral',

  // Identifiers & paths
  Identifier = 'Identifier',
  Dollar = 'Dollar',
  DollarDot = 'DollarDot',
  Dot = 'Dot',
  QuestionDot = 'QuestionDot',
  DotDot = 'DotDot',

  // Brackets
  LeftBracket = 'LeftBracket',
  RightBracket = 'RightBracket',
  LeftParen = 'LeftParen',
  RightParen = 'RightParen',
  LeftBrace = 'LeftBrace',
  RightBrace = 'RightBrace',

  // Operators
  Pipe = 'Pipe',
  FatArrow = 'FatArrow',
  Comma = 'Comma',
  Colon = 'Colon',
  Star = 'Star',
  Spread = 'Spread',

  // Arithmetic
  Plus = 'Plus',
  Minus = 'Minus',
  Slash = 'Slash',

  // Assignment
  Assign = 'Assign',

  // Comparison
  EqualEqual = 'EqualEqual',
  BangEqual = 'BangEqual',
  LessThan = 'LessThan',
  LessThanOrEqual = 'LessThanOrEqual',
  GreaterThan = 'GreaterThan',
  GreaterThanOrEqual = 'GreaterThanOrEqual',

  // Logical
  AmpersandAmpersand = 'AmpersandAmpersand',
  PipePipe = 'PipePipe',
  Bang = 'Bang',

  // Keywords
  Let = 'Let',
  If = 'If',
  Then = 'Then',
  Else = 'Else',
  Match = 'Match',
  Return = 'Return',
  From = 'From',
  Asc = 'Asc',
  Desc = 'Desc',
  On = 'On',
  Equals = 'Equals',
  Into = 'Into',
  Underscore = 'Underscore',
  Memo = 'Memo',

  // Interpolation
  Backtick = 'Backtick',

  // Special
  Eof = 'Eof',
}

export interface SourceSpan {
  start: number;
  end: number;
  line: number;
  column: number;
}

export interface Token {
  kind: TokenKind;
  text: string;
  span: SourceSpan;
}
