import type { SourceSpan } from './token.js';

// ── Expressions ──

export type ElwoodExpression =
  | PipelineExpression
  | PathExpression
  | IdentifierExpression
  | LambdaExpression
  | ObjectExpression
  | ArrayExpression
  | LiteralExpression
  | InterpolatedStringExpression
  | BinaryExpression
  | UnaryExpression
  | IfExpression
  | MatchExpression
  | MemoExpression
  | MethodCallExpression
  | FunctionCallExpression
  | MemberAccessExpression
  | IndexExpression;

// ── Top-level ──

export interface ScriptNode {
  type: 'Script';
  bindings: LetBindingNode[];
  returnExpression: ElwoodExpression | null;
  span: SourceSpan;
}

export interface LetBindingNode {
  type: 'LetBinding';
  name: string;
  value: ElwoodExpression;
  span: SourceSpan;
}

// ── Expression nodes ──

export interface PipelineExpression {
  type: 'Pipeline';
  source: ElwoodExpression;
  operations: PipeOperation[];
  span: SourceSpan;
}

export interface PathExpression {
  type: 'Path';
  segments: PathSegment[];
  isRooted: boolean;
  span: SourceSpan;
}

export interface IdentifierExpression {
  type: 'Identifier';
  name: string;
  span: SourceSpan;
}

export interface LambdaExpression {
  type: 'Lambda';
  parameters: string[];
  body: ElwoodExpression;
  span: SourceSpan;
}

export interface ObjectExpression {
  type: 'Object';
  properties: ObjectProperty[];
  span: SourceSpan;
}

export interface ObjectProperty {
  key: string;
  value: ElwoodExpression;
  span: SourceSpan;
  isSpread?: boolean;
  computedKey?: ElwoodExpression;
}

export interface ArrayExpression {
  type: 'Array';
  items: ElwoodExpression[];
  span: SourceSpan;
}

export interface LiteralExpression {
  type: 'Literal';
  value: string | number | boolean | null;
  span: SourceSpan;
}

export interface InterpolatedStringExpression {
  type: 'InterpolatedString';
  parts: InterpolationPart[];
  span: SourceSpan;
}

export type InterpolationPart =
  | { type: 'Text'; text: string; span: SourceSpan }
  | { type: 'Expression'; expression: ElwoodExpression; span: SourceSpan };

export interface BinaryExpression {
  type: 'Binary';
  left: ElwoodExpression;
  operator: BinaryOperator;
  right: ElwoodExpression;
  span: SourceSpan;
}

export interface UnaryExpression {
  type: 'Unary';
  operator: UnaryOperator;
  operand: ElwoodExpression;
  span: SourceSpan;
}

export interface IfExpression {
  type: 'If';
  condition: ElwoodExpression;
  thenBranch: ElwoodExpression;
  elseBranch: ElwoodExpression;
  span: SourceSpan;
}

export interface MatchExpression {
  type: 'Match';
  input: ElwoodExpression;
  arms: MatchArm[];
  span: SourceSpan;
}

export interface MatchArm {
  pattern: ElwoodExpression | null; // null = wildcard
  result: ElwoodExpression;
  span: SourceSpan;
}

export interface MemoExpression {
  type: 'Memo';
  lambda: LambdaExpression;
  span: SourceSpan;
}

export interface MethodCallExpression {
  type: 'MethodCall';
  target: ElwoodExpression;
  methodName: string;
  arguments: ElwoodExpression[];
  span: SourceSpan;
}

export interface FunctionCallExpression {
  type: 'FunctionCall';
  functionName: string;
  arguments: ElwoodExpression[];
  span: SourceSpan;
}

export interface MemberAccessExpression {
  type: 'MemberAccess';
  target: ElwoodExpression;
  memberName: string;
  span: SourceSpan;
}

export interface IndexExpression {
  type: 'Index';
  target: ElwoodExpression;
  index: ElwoodExpression | null; // null = [*]
  span: SourceSpan;
}

// ── Pipe Operations ──

export type PipeOperation =
  | WhereOperation
  | SelectOperation
  | SelectManyOperation
  | OrderByOperation
  | GroupByOperation
  | DistinctOperation
  | AggregateOperation
  | SliceOperation
  | TakeWhileOperation
  | BatchOperation
  | JoinOperation
  | ConcatOperation
  | ReduceOperation
  | QuantifierOperation
  | MatchOperation;

export interface WhereOperation { type: 'Where'; predicate: ElwoodExpression; span: SourceSpan }
export interface SelectOperation { type: 'Select'; projection: ElwoodExpression; span: SourceSpan }
export interface SelectManyOperation { type: 'SelectMany'; projection: ElwoodExpression; span: SourceSpan }
export interface OrderByOperation { type: 'OrderBy'; keys: { key: ElwoodExpression; ascending: boolean }[]; span: SourceSpan }
export interface GroupByOperation { type: 'GroupBy'; keySelector: ElwoodExpression; span: SourceSpan }
export interface DistinctOperation { type: 'Distinct'; span: SourceSpan }
export interface AggregateOperation { type: 'Aggregate'; name: string; predicate?: ElwoodExpression; span: SourceSpan }
export interface SliceOperation { type: 'Slice'; kind: 'take' | 'skip'; count: ElwoodExpression; span: SourceSpan }
export interface TakeWhileOperation { type: 'TakeWhile'; predicate: ElwoodExpression; span: SourceSpan }
export interface BatchOperation { type: 'Batch'; size: ElwoodExpression; span: SourceSpan }
export interface JoinOperation {
  type: 'Join';
  source: ElwoodExpression;
  leftKey: ElwoodExpression;
  rightKey: ElwoodExpression;
  intoAlias?: string;
  mode: JoinMode;
  span: SourceSpan;
}
export interface ConcatOperation { type: 'Concat'; separator?: ElwoodExpression; span: SourceSpan }
export interface ReduceOperation { type: 'Reduce'; accumulator: ElwoodExpression; initialValue?: ElwoodExpression; span: SourceSpan }
export interface QuantifierOperation { type: 'Quantifier'; kind: 'any' | 'all'; predicate: ElwoodExpression; span: SourceSpan }
export interface MatchOperation { type: 'MatchOp'; arms: MatchArm[]; span: SourceSpan }

export type JoinMode = 'inner' | 'left' | 'right' | 'full';

// ── Path Segments ──

export type PathSegment =
  | { type: 'Property'; name: string; span: SourceSpan }
  | { type: 'Index'; index: number | null; span: SourceSpan }  // null = wildcard [*]
  | { type: 'Slice'; start: number | null; end: number | null; span: SourceSpan }
  | { type: 'RecursiveDescent'; name: string; span: SourceSpan };

// ── Operators ──

export type BinaryOperator =
  | 'Add' | 'Subtract' | 'Multiply' | 'Divide'
  | 'Equal' | 'NotEqual'
  | 'LessThan' | 'LessThanOrEqual' | 'GreaterThan' | 'GreaterThanOrEqual'
  | 'And' | 'Or';

export type UnaryOperator = 'Negate' | 'Not';
