using Elwood.Core.Diagnostics;

namespace Elwood.Core.Syntax;

// ── Base ──

public abstract record ElwoodNode(SourceSpan Span);

// ── Top-level ──

/// <summary>A complete Elwood script: let bindings + optional return expression.</summary>
public sealed record ScriptNode(
    IReadOnlyList<LetBindingNode> Bindings,
    ElwoodExpression? ReturnExpression,
    SourceSpan Span
) : ElwoodNode(Span);

/// <summary>let name = expression</summary>
public sealed record LetBindingNode(
    string Name,
    ElwoodExpression Value,
    SourceSpan Span
) : ElwoodNode(Span);

// ── Expressions ──

public abstract record ElwoodExpression(SourceSpan Span) : ElwoodNode(Span);

/// <summary>A pipeline: expr | op1 | op2 | ...</summary>
public sealed record PipelineExpression(
    ElwoodExpression Source,
    IReadOnlyList<PipeOperation> Operations,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>JSONPath navigation: $.foo.bar[*].baz</summary>
public sealed record PathExpression(
    IReadOnlyList<PathSegment> Segments,
    bool IsRooted, // starts with $
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>A named variable reference (from let binding or lambda parameter).</summary>
public sealed record IdentifierExpression(
    string Name,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>Lambda: x => expr  or  (x, y) => expr</summary>
public sealed record LambdaExpression(
    IReadOnlyList<string> Parameters,
    ElwoodExpression Body,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>Object literal: { key: expr, key2: expr2, ... }</summary>
public sealed record ObjectExpression(
    IReadOnlyList<ObjectProperty> Properties,
    SourceSpan Span
) : ElwoodExpression(Span);

public sealed record ObjectProperty(string Key, ElwoodExpression Value, SourceSpan Span, bool IsSpread = false, ElwoodExpression? ComputedKey = null);

/// <summary>Array literal: [expr, expr, ...]</summary>
public sealed record ArrayExpression(
    IReadOnlyList<ElwoodExpression> Items,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>String, number, bool, null literals.</summary>
public sealed record LiteralExpression(
    object? Value, // string, double, bool, or null
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>String interpolation: `Hello {expr} world`</summary>
public sealed record InterpolatedStringExpression(
    IReadOnlyList<InterpolationPart> Parts,
    SourceSpan Span
) : ElwoodExpression(Span);

public abstract record InterpolationPart(SourceSpan Span);
public sealed record TextPart(string Text, SourceSpan Span) : InterpolationPart(Span);
public sealed record ExpressionPart(ElwoodExpression Expression, SourceSpan Span) : InterpolationPart(Span);

/// <summary>Binary expression: a + b, a > b, a && b</summary>
public sealed record BinaryExpression(
    ElwoodExpression Left,
    BinaryOperator Operator,
    ElwoodExpression Right,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>Unary expression: !a, -a</summary>
public sealed record UnaryExpression(
    UnaryOperator Operator,
    ElwoodExpression Operand,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>if cond then expr else expr</summary>
public sealed record IfExpression(
    ElwoodExpression Condition,
    ElwoodExpression ThenBranch,
    ElwoodExpression ElseBranch,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>expr | match { pattern => result, ... }</summary>
public sealed record MatchExpression(
    ElwoodExpression Input,
    IReadOnlyList<MatchArm> Arms,
    SourceSpan Span
) : ElwoodExpression(Span);

public sealed record MatchArm(
    ElwoodExpression? Pattern, // null = wildcard (_)
    ElwoodExpression Result,
    SourceSpan Span
);

/// <summary>Memoized lambda: memo params => body. Cached by argument values.</summary>
public sealed record MemoExpression(
    LambdaExpression Lambda,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>Method call on a value: expr.method(args)</summary>
public sealed record MethodCallExpression(
    ElwoodExpression Target,
    string MethodName,
    IReadOnlyList<ElwoodExpression> Arguments,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>Standalone function call: functionName(args)</summary>
public sealed record FunctionCallExpression(
    string FunctionName,
    IReadOnlyList<ElwoodExpression> Arguments,
    SourceSpan Span
) : ElwoodExpression(Span);

/// <summary>Property access on an expression: expr.property or expr?.property</summary>
public sealed record MemberAccessExpression(
    ElwoodExpression Target,
    string MemberName,
    SourceSpan Span,
    bool Optional = false
) : ElwoodExpression(Span);

/// <summary>Index access: expr[index] or expr[*]</summary>
public sealed record IndexExpression(
    ElwoodExpression Target,
    ElwoodExpression? Index, // null means [*] (all elements)
    SourceSpan Span
) : ElwoodExpression(Span);

// ── Pipe Operations ──

public abstract record PipeOperation(SourceSpan Span);

/// <summary>| where predicate</summary>
public sealed record WhereOperation(ElwoodExpression Predicate, SourceSpan Span) : PipeOperation(Span);

/// <summary>| select projection</summary>
public sealed record SelectOperation(ElwoodExpression Projection, SourceSpan Span) : PipeOperation(Span);

/// <summary>| selectMany projection</summary>
public sealed record SelectManyOperation(ElwoodExpression Projection, SourceSpan Span) : PipeOperation(Span);

/// <summary>| orderBy key [asc|desc]</summary>
public sealed record OrderByOperation(
    IReadOnlyList<(ElwoodExpression Key, bool Ascending)> Keys,
    SourceSpan Span
) : PipeOperation(Span);

/// <summary>| groupBy key</summary>
public sealed record GroupByOperation(ElwoodExpression KeySelector, SourceSpan Span) : PipeOperation(Span);

/// <summary>| distinct</summary>
public sealed record DistinctOperation(SourceSpan Span) : PipeOperation(Span);

/// <summary>| first, | last, | count, | sum, | min, | max</summary>
public sealed record AggregateOperation(string Name, SourceSpan Span, ElwoodExpression? Predicate = null) : PipeOperation(Span)
{
    public AggregateOperation(string name, ElwoodExpression predicate, SourceSpan span)
        : this(name, span, predicate) { }
}

/// <summary>| take n, | skip n</summary>
public sealed record SliceOperation(string Kind, ElwoodExpression Count, SourceSpan Span) : PipeOperation(Span);

/// <summary>| takeWhile predicate</summary>
public sealed record TakeWhileOperation(ElwoodExpression Predicate, SourceSpan Span) : PipeOperation(Span);

/// <summary>| batch n</summary>
public sealed record BatchOperation(ElwoodExpression Size, SourceSpan Span) : PipeOperation(Span);

/// <summary>| join other on leftKey equals rightKey [into alias] [inner|left|right|full]</summary>
public sealed record JoinOperation(
    ElwoodExpression Source,
    ElwoodExpression LeftKey,
    ElwoodExpression RightKey,
    string? IntoAlias,
    JoinMode Mode,
    SourceSpan Span
) : PipeOperation(Span);

public enum JoinMode { Inner, Left, Right, Full }

/// <summary>| concat separator?</summary>
public sealed record ConcatOperation(ElwoodExpression? Separator, SourceSpan Span) : PipeOperation(Span);

/// <summary>| reduce (acc, item) => expr [from initialValue]</summary>
public sealed record ReduceOperation(ElwoodExpression Accumulator, ElwoodExpression? InitialValue, SourceSpan Span) : PipeOperation(Span);

/// <summary>| any pred, | all pred</summary>
public sealed record QuantifierOperation(string Kind, ElwoodExpression Predicate, SourceSpan Span) : PipeOperation(Span);

/// <summary>| match { arms }</summary>
public sealed record MatchOperation(IReadOnlyList<MatchArm> Arms, SourceSpan Span) : PipeOperation(Span);

// ── Path Segments ──

public abstract record PathSegment(SourceSpan Span);

/// <summary>Simple property: .foo or ?.foo (optional chaining)</summary>
public sealed record PropertySegment(string Name, SourceSpan Span, bool Optional = false) : PathSegment(Span);

/// <summary>Array index: [0] or wildcard: [*]</summary>
public sealed record IndexSegment(int? Index, SourceSpan Span) : PathSegment(Span); // null = wildcard

/// <summary>Array slice: [start:end], [:end], [start:]</summary>
public sealed record SliceSegment(int? Start, int? End, SourceSpan Span) : PathSegment(Span);

/// <summary>Recursive descent: ..foo</summary>
public sealed record RecursiveDescentSegment(string Name, SourceSpan Span) : PathSegment(Span);

// ── Operators ──

public enum BinaryOperator
{
    Add, Subtract, Multiply, Divide,
    Equal, NotEqual,
    LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual,
    And, Or
}

public enum UnaryOperator
{
    Negate, Not
}
