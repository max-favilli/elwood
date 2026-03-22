using Elwood.Core.Diagnostics;
using Elwood.Core.Syntax;

namespace Elwood.Core.Parsing;

/// <summary>
/// Recursive descent parser for Elwood expressions.
/// Builds an AST from a token stream.
/// </summary>
public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;
    private int _pipeDepth; // >0 means we're parsing a pipe argument — lambdas should not consume |
    private readonly List<ElwoodDiagnostic> _diagnostics = [];

    public IReadOnlyList<ElwoodDiagnostic> Diagnostics => _diagnostics;

    public Parser(List<Token> tokens)
    {
        _tokens = tokens;
    }

    // ── Entry Points ──

    public ScriptNode ParseScript()
    {
        var bindings = new List<LetBindingNode>();
        ElwoodExpression? returnExpr = null;
        var start = Current.Span;

        while (!IsAtEnd)
        {
            if (Check(TokenKind.Let))
            {
                bindings.Add(ParseLetBinding());
            }
            else if (Check(TokenKind.Return))
            {
                Advance(); // skip 'return'
                returnExpr = ParseExpression();
                break;
            }
            else
            {
                // Implicit return — last expression
                returnExpr = ParseExpression();
                if (!IsAtEnd && !Check(TokenKind.Eof))
                {
                    // More tokens — this was a let without the keyword? Error.
                    Error($"Unexpected token '{Current.Text}'. Did you mean 'let'?");
                    break;
                }
            }
        }

        return new ScriptNode(bindings, returnExpr, Span(start));
    }

    public ElwoodExpression ParseExpression()
    {
        return ParsePipeline();
    }

    // ── Pipeline: expr | op1 | op2 ──

    private ElwoodExpression ParsePipeline()
    {
        var start = Current.Span;
        var expr = ParseTernary();

        var ops = new List<PipeOperation>();
        while (Match(TokenKind.Pipe))
        {
            ops.Add(ParsePipeOperation());
        }

        return ops.Count > 0
            ? new PipelineExpression(expr, ops, Span(start))
            : expr;
    }

    private PipeOperation ParsePipeOperation()
    {
        var start = Current.Span;
        var name = Current.Text;

        if (Check(TokenKind.Match))
        {
            Advance();
            return new MatchOperation(ParseMatchArms(), Span(start));
        }

        if (!Check(TokenKind.Identifier))
        {
            Error($"Expected pipe operator name after '|', got '{Current.Text}'");
            return new AggregateOperation("error", Span(start));
        }

        Advance(); // consume the operator name

        return name switch
        {
            "where" => new WhereOperation(ParsePipeArgExpression(), Span(start)),
            "select" => new SelectOperation(ParsePipeArgExpression(), Span(start)),
            "selectMany" => new SelectManyOperation(ParsePipeArgExpression(), Span(start)),
            "orderBy" => ParseOrderBy(start),
            "groupBy" => new GroupByOperation(ParsePipeArgExpression(), Span(start)),
            "distinct" => new DistinctOperation(Span(start)),
            "first" or "last" => ParseFirstLast(name, start),
            "count" or "sum" or "min" or "max" or "index" =>
                new AggregateOperation(name, Span(start)),
            "take" or "skip" =>
                new SliceOperation(name, ParsePipeArgExpression(), Span(start)),
            "takeWhile" => new TakeWhileOperation(ParsePipeArgExpression(), Span(start)),
            "batch" => new BatchOperation(ParsePipeArgExpression(), Span(start)),
            "join" => ParseJoin(start),
            "concat" => ParseConcat(start),
            "reduce" => ParseReduce(start),
            "any" or "all" =>
                new QuantifierOperation(name, ParsePipeArgExpression(), Span(start)),
            _ => throw Error($"Unknown pipe operator '{name}'")
        };
    }

    private ElwoodExpression ParsePipeArgExpression()
    {
        // The argument to a pipe operation can be a lambda or a simple expression,
        // but NOT another pipeline (pipes are left-associative at the top level).
        _pipeDepth++;
        try
        {
            return ParseTernary();
        }
        finally
        {
            _pipeDepth--;
        }
    }

    private OrderByOperation ParseOrderBy(SourceSpan start)
    {
        var keys = new List<(ElwoodExpression Key, bool Ascending)>();

        do
        {
            var keyExpr = ParsePipeArgExpression();
            var ascending = true;
            if (Check(TokenKind.Asc)) { Advance(); ascending = true; }
            else if (Check(TokenKind.Desc)) { Advance(); ascending = false; }
            keys.Add((keyExpr, ascending));
        } while (Match(TokenKind.Comma));

        return new OrderByOperation(keys, Span(start));
    }

    private PipeOperation ParseFirstLast(string name, SourceSpan start)
    {
        // Check if there's a predicate (lambda or expression) following
        if (IsAtEnd || Check(TokenKind.Pipe) || Check(TokenKind.RightBrace) ||
            Check(TokenKind.RightParen) || Check(TokenKind.Eof) || Check(TokenKind.Comma))
        {
            return new AggregateOperation(name, Span(start));
        }
        // Has a predicate — parse like where
        return new AggregateOperation(name, ParsePipeArgExpression(), Span(start));
    }

    private ReduceOperation ParseReduce(SourceSpan start)
    {
        var accumulator = ParsePipeArgExpression();
        ElwoodExpression? initialValue = null;
        if (Match(TokenKind.From))
        {
            initialValue = ParsePipeArgExpression();
        }
        return new ReduceOperation(accumulator, initialValue, Span(start));
    }

    private ConcatOperation ParseConcat(SourceSpan start)
    {
        // Separator is optional — check if next token looks like an argument
        // (string literal, identifier, parenthesized expression) vs next pipe or end
        if (IsAtEnd || Check(TokenKind.Pipe) || Check(TokenKind.RightBrace) ||
            Check(TokenKind.RightParen) || Check(TokenKind.Eof) || Check(TokenKind.Comma))
        {
            return new ConcatOperation(null, Span(start));
        }
        return new ConcatOperation(ParsePipeArgExpression(), Span(start));
    }

    private JoinOperation ParseJoin(SourceSpan start)
    {
        var source = ParsePipeArgExpression();
        Expect(TokenKind.On, "Expected 'on' after join source");
        var leftKey = ParsePipeArgExpression();
        Expect(TokenKind.Equals, "Expected 'equals' in join");
        var rightKey = ParsePipeArgExpression();

        string? into = null;
        if (Match(TokenKind.Into))
        {
            into = Expect(TokenKind.Identifier, "Expected alias after 'into'").Text;
        }

        // Join mode: inner (default), left, right, full
        var mode = JoinMode.Inner;
        if (Check(TokenKind.Identifier))
        {
            mode = Current.Text.ToLower() switch
            {
                "inner" => JoinMode.Inner,
                "left" => JoinMode.Left,
                "right" => JoinMode.Right,
                "full" => JoinMode.Full,
                _ => JoinMode.Inner
            };
            if (Current.Text.ToLower() is "inner" or "left" or "right" or "full")
                Advance();
        }

        return new JoinOperation(source, leftKey, rightKey, into, mode, Span(start));
    }

    // ── Ternary: if cond then expr else expr ──

    private ElwoodExpression ParseTernary()
    {
        if (Match(TokenKind.If))
        {
            var start = Previous.Span;
            var cond = ParseOr();
            Expect(TokenKind.Then, "Expected 'then' after if condition");
            var thenBranch = ParseTernary();
            Expect(TokenKind.Else, "Expected 'else' after then branch");
            var elseBranch = ParseTernary();
            return new IfExpression(cond, thenBranch, elseBranch, Span(start));
        }
        return ParseOr();
    }

    // ── Boolean: or, and ──

    private ElwoodExpression ParseOr()
    {
        var start = Current.Span;
        var left = ParseAnd();
        while (Match(TokenKind.PipePipe))
        {
            var right = ParseAnd();
            left = new BinaryExpression(left, BinaryOperator.Or, right, Span(start));
        }
        return left;
    }

    private ElwoodExpression ParseAnd()
    {
        var start = Current.Span;
        var left = ParseEquality();
        while (Match(TokenKind.AmpersandAmpersand))
        {
            var right = ParseEquality();
            left = new BinaryExpression(left, BinaryOperator.And, right, Span(start));
        }
        return left;
    }

    // ── Comparison ──

    private ElwoodExpression ParseEquality()
    {
        var start = Current.Span;
        var left = ParseComparison();
        while (Check(TokenKind.EqualEqual) || Check(TokenKind.BangEqual))
        {
            var op = Advance().Kind == TokenKind.EqualEqual ? BinaryOperator.Equal : BinaryOperator.NotEqual;
            var right = ParseComparison();
            left = new BinaryExpression(left, op, right, Span(start));
        }
        return left;
    }

    private ElwoodExpression ParseComparison()
    {
        var start = Current.Span;
        var left = ParseAdditive();
        while (Check(TokenKind.LessThan) || Check(TokenKind.LessThanOrEqual) ||
               Check(TokenKind.GreaterThan) || Check(TokenKind.GreaterThanOrEqual))
        {
            var tok = Advance();
            var op = tok.Kind switch
            {
                TokenKind.LessThan => BinaryOperator.LessThan,
                TokenKind.LessThanOrEqual => BinaryOperator.LessThanOrEqual,
                TokenKind.GreaterThan => BinaryOperator.GreaterThan,
                _ => BinaryOperator.GreaterThanOrEqual
            };
            var right = ParseAdditive();
            left = new BinaryExpression(left, op, right, Span(start));
        }
        return left;
    }

    // ── Arithmetic ──

    private ElwoodExpression ParseAdditive()
    {
        var start = Current.Span;
        var left = ParseMultiplicative();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            var op = Advance().Kind == TokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            var right = ParseMultiplicative();
            left = new BinaryExpression(left, op, right, Span(start));
        }
        return left;
    }

    private ElwoodExpression ParseMultiplicative()
    {
        var start = Current.Span;
        var left = ParseUnary();
        while (Check(TokenKind.Star) || Check(TokenKind.Slash))
        {
            var op = Advance().Kind == TokenKind.Star ? BinaryOperator.Multiply : BinaryOperator.Divide;
            var right = ParseUnary();
            left = new BinaryExpression(left, op, right, Span(start));
        }
        return left;
    }

    private ElwoodExpression ParseUnary()
    {
        if (Match(TokenKind.Bang))
            return new UnaryExpression(UnaryOperator.Not, ParseUnary(), Span(Previous.Span));
        if (Match(TokenKind.Minus))
            return new UnaryExpression(UnaryOperator.Negate, ParseUnary(), Span(Previous.Span));
        return ParsePostfix();
    }

    // ── Postfix: member access, indexing, method calls ──

    private ElwoodExpression ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Match(TokenKind.Dot))
            {
                var start = Previous.Span;
                var name = Expect(TokenKind.Identifier, "Expected property name after '.'").Text;

                // Check if it's a method call: .name(
                if (Match(TokenKind.LeftParen))
                {
                    var args = ParseArgumentList();
                    Expect(TokenKind.RightParen, "Expected ')' after arguments");
                    expr = new MethodCallExpression(expr, name, args, Span(start));
                }
                else
                {
                    expr = new MemberAccessExpression(expr, name, Span(start));
                }
            }
            else if (Match(TokenKind.LeftBracket))
            {
                var start = Previous.Span;
                if (Match(TokenKind.Star))
                {
                    Expect(TokenKind.RightBracket, "Expected ']' after '[*'");
                    expr = new IndexExpression(expr, null, Span(start));
                }
                else
                {
                    var index = ParseExpression();
                    Expect(TokenKind.RightBracket, "Expected ']'");
                    expr = new IndexExpression(expr, index, Span(start));
                }
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    // ── Primary expressions ──

    private ElwoodExpression ParsePrimary()
    {
        var start = Current.Span;

        // Dollar path: $.foo.bar or just $
        if (Check(TokenKind.DollarDot) || Check(TokenKind.Dollar))
        {
            return ParsePath();
        }

        // Identifier — could be variable, function call, or lambda
        if (Check(TokenKind.Identifier))
        {
            return ParseIdentifierOrLambda();
        }

        // String literal
        if (Match(TokenKind.StringLiteral))
            return new LiteralExpression(Previous.Text, Span(start));

        // Number literal
        if (Match(TokenKind.NumberLiteral))
            return new LiteralExpression(double.Parse(Previous.Text, System.Globalization.CultureInfo.InvariantCulture), Span(start));

        // Boolean literals
        if (Match(TokenKind.TrueLiteral))
            return new LiteralExpression(true, Span(start));
        if (Match(TokenKind.FalseLiteral))
            return new LiteralExpression(false, Span(start));

        // Null
        if (Match(TokenKind.NullLiteral))
            return new LiteralExpression(null, Span(start));

        // Interpolated string
        if (Match(TokenKind.Backtick))
            return ParseInterpolatedStringContent(Previous.Text, start);

        // Object literal: { key: value, ... }
        if (Check(TokenKind.LeftBrace))
            return ParseObjectLiteral();

        // Array literal: [ expr, ... ]
        if (Match(TokenKind.LeftBracket))
        {
            var items = new List<ElwoodExpression>();
            if (!Check(TokenKind.RightBracket))
            {
                do { items.Add(ParseExpression()); } while (Match(TokenKind.Comma));
            }
            Expect(TokenKind.RightBracket, "Expected ']'");
            return new ArrayExpression(items, Span(start));
        }

        // Parenthesized expression or multi-param lambda: (x, y) => expr
        if (Match(TokenKind.LeftParen))
        {
            // Check for multi-param lambda: (x, y) => ...
            if (Check(TokenKind.Identifier) && LookAheadForMultiParamLambda())
            {
                var parameters = new List<string>();
                do
                {
                    parameters.Add(Expect(TokenKind.Identifier, "Expected parameter name").Text);
                } while (Match(TokenKind.Comma));
                Expect(TokenKind.RightParen, "Expected ')' after lambda parameters");
                Expect(TokenKind.FatArrow, "Expected '=>' after lambda parameters");
                var body = _pipeDepth > 0 ? ParseTernary() : ParseExpression();
                return new LambdaExpression(parameters, body, Span(start));
            }

            var expr = ParseExpression();
            Expect(TokenKind.RightParen, "Expected ')'");
            return expr;
        }

        // Memo: memo params => body
        if (Match(TokenKind.Memo))
        {
            // Reset pipe depth so memo lambda body can contain pipes
            // even when memo is used inside a pipe operation
            var savedDepth = _pipeDepth;
            _pipeDepth = 0;
            try
            {
                var inner = ParseTernary();
                if (inner is not LambdaExpression lambdaExpr)
                    throw Error("Expected lambda expression after 'memo'");
                return new MemoExpression(lambdaExpr, Span(start));
            }
            finally
            {
                _pipeDepth = savedDepth;
            }
        }

        // Wildcard (in match contexts)
        if (Match(TokenKind.Underscore))
            return new IdentifierExpression("_", Span(start));

        throw Error($"Unexpected token '{Current.Text}'");
    }

    private ElwoodExpression ParsePath()
    {
        var start = Current.Span;
        var segments = new List<PathSegment>();

        if (Match(TokenKind.DollarDot))
        {
            // $.something
            segments.AddRange(ParsePathSegments());
        }
        else if (Match(TokenKind.Dollar))
        {
            // Standalone $
        }

        var path = new PathExpression(segments, true, Span(start));

        // After the path, there might be postfix operations
        return path;
    }

    private List<PathSegment> ParsePathSegments()
    {
        var segments = new List<PathSegment>();

        // First segment after $. must be an identifier
        if (Check(TokenKind.Identifier))
        {
            segments.Add(new PropertySegment(Advance().Text, Span(Current.Span)));
        }

        // Continue with .prop or [index] or ..prop
        while (true)
        {
            if (Match(TokenKind.DotDot))
            {
                var name = Expect(TokenKind.Identifier, "Expected property name after '..'").Text;
                segments.Add(new RecursiveDescentSegment(name, Span(Current.Span)));
            }
            else if (Check(TokenKind.Dot) && !Check(TokenKind.DotDot))
            {
                // Lookahead: if .identifier( then stop — it's a method call, not a path segment
                if (Check(TokenKind.Dot) && PeekAt(1)?.Kind == TokenKind.Identifier && PeekAt(2)?.Kind == TokenKind.LeftParen)
                {
                    break; // let ParsePostfix() handle .method()
                }

                Advance(); // consume the dot
                if (Check(TokenKind.Identifier))
                {
                    segments.Add(new PropertySegment(Advance().Text, Span(Current.Span)));
                }
                else
                {
                    break; // dot not followed by identifier — let caller handle
                }
            }
            else if (Match(TokenKind.LeftBracket))
            {
                if (Match(TokenKind.Star))
                {
                    Expect(TokenKind.RightBracket, "Expected ']' after '[*'");
                    segments.Add(new IndexSegment(null, Span(Current.Span)));
                }
                else if (Check(TokenKind.NumberLiteral) || Check(TokenKind.Minus) || Check(TokenKind.Colon))
                {
                    // Parse [index], [start:end], [:end], [start:], [-n:]
                    int? start = TryParseBracketInt();

                    if (Match(TokenKind.Colon))
                    {
                        int? end = TryParseBracketInt();
                        Expect(TokenKind.RightBracket, "Expected ']'");
                        segments.Add(new SliceSegment(start, end, Span(Current.Span)));
                    }
                    else if (start.HasValue)
                    {
                        Expect(TokenKind.RightBracket, "Expected ']'");
                        segments.Add(new IndexSegment(start.Value, Span(Current.Span)));
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return segments;
    }

    private ElwoodExpression ParseIdentifierOrLambda()
    {
        var start = Current.Span;
        var name = Advance().Text;

        // Lambda: x => expr
        if (Check(TokenKind.FatArrow))
        {
            Advance(); // consume =>
            // Inside a pipe argument, lambda body must not consume | (pipe is left-associative)
            var body = _pipeDepth > 0 ? ParseTernary() : ParseExpression();
            return new LambdaExpression([name], body, Span(start));
        }

        // Function call: name(args)
        if (Match(TokenKind.LeftParen))
        {
            var args = ParseArgumentList();
            Expect(TokenKind.RightParen, "Expected ')' after function arguments");
            return new FunctionCallExpression(name, args, Span(start));
        }

        // Plain identifier (variable reference)
        return new IdentifierExpression(name, Span(start));
    }

    private ObjectExpression ParseObjectLiteral()
    {
        var start = Current.Span;
        Expect(TokenKind.LeftBrace, "Expected '{'");
        var properties = new List<ObjectProperty>();

        if (!Check(TokenKind.RightBrace))
        {
            do
            {
                var propStart = Current.Span;

                // Spread: ...expr
                if (Match(TokenKind.Spread))
                {
                    var spreadExpr = ParseExpression();
                    properties.Add(new ObjectProperty("", spreadExpr, Span(propStart), IsSpread: true));
                    continue;
                }

                // Computed key: [expr]: value
                if (Match(TokenKind.LeftBracket))
                {
                    var keyExpr = ParseExpression();
                    Expect(TokenKind.RightBracket, "Expected ']' after computed key");
                    Expect(TokenKind.Colon, "Expected ':' after computed key");
                    var compValue = ParseExpression();
                    properties.Add(new ObjectProperty("", compValue, Span(propStart), ComputedKey: keyExpr));
                    continue;
                }

                var key = Current.Text;

                // Key can be identifier or string literal
                if (Check(TokenKind.Identifier))
                    Advance();
                else if (Check(TokenKind.StringLiteral))
                    Advance();
                else
                    throw Error($"Expected property name, '[' (computed), or '...', got '{Current.Text}'");

                Expect(TokenKind.Colon, "Expected ':' after property name");
                var value = ParseExpression();
                properties.Add(new ObjectProperty(key, value, Span(propStart)));
            } while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.RightBrace, "Expected '}'");
        return new ObjectExpression(properties, Span(start));
    }

    private InterpolatedStringExpression ParseInterpolatedStringContent(string raw, SourceSpan start)
    {
        var parts = new List<InterpolationPart>();
        var currentText = new System.Text.StringBuilder();
        var i = 0;

        while (i < raw.Length)
        {
            if (raw[i] == '{')
            {
                if (currentText.Length > 0)
                {
                    parts.Add(new TextPart(currentText.ToString(), start));
                    currentText.Clear();
                }
                i++; // skip {
                var depth = 1;
                var exprText = new System.Text.StringBuilder();
                while (i < raw.Length && depth > 0)
                {
                    if (raw[i] == '{') depth++;
                    else if (raw[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                    exprText.Append(raw[i]);
                    i++;
                }
                // Parse the inner expression
                var innerLexer = new Lexer(exprText.ToString());
                var innerTokens = innerLexer.Tokenize();
                var innerParser = new Parser(innerTokens);
                var expr = innerParser.ParseExpression();
                parts.Add(new ExpressionPart(expr, start));
            }
            else
            {
                currentText.Append(raw[i]);
                i++;
            }
        }

        if (currentText.Length > 0)
            parts.Add(new TextPart(currentText.ToString(), start));

        return new InterpolatedStringExpression(parts, start);
    }

    private IReadOnlyList<MatchArm> ParseMatchArms()
    {
        var arms = new List<MatchArm>();

        // Match arms can follow on same line or next lines
        // pattern => result
        while (!IsAtEnd && !Check(TokenKind.Pipe) && !Check(TokenKind.Eof) &&
               !Check(TokenKind.RightBrace) && !Check(TokenKind.RightParen))
        {
            var armStart = Current.Span;
            ElwoodExpression? pattern;

            if (Check(TokenKind.Underscore))
            {
                Advance();
                pattern = null; // wildcard
            }
            else
            {
                pattern = ParsePrimary();
            }

            Expect(TokenKind.FatArrow, "Expected '=>' in match arm");
            var result = ParseTernary();
            arms.Add(new MatchArm(pattern, result, Span(armStart)));

            // Arms can be comma-separated or just newline-separated
            Match(TokenKind.Comma);
        }

        return arms;
    }

    private List<ElwoodExpression> ParseArgumentList()
    {
        var args = new List<ElwoodExpression>();
        if (!Check(TokenKind.RightParen))
        {
            do { args.Add(ParseExpression()); } while (Match(TokenKind.Comma));
        }
        return args;
    }

    private LetBindingNode ParseLetBinding()
    {
        var start = Current.Span;
        Expect(TokenKind.Let, "Expected 'let'");
        var name = Expect(TokenKind.Identifier, "Expected variable name after 'let'").Text;

        Expect(TokenKind.Assign, "Expected '=' after variable name in let binding");
        var value = ParseExpression();
        return new LetBindingNode(name, value, Span(start));
    }

    // ── Lookahead ──

    private bool LookAheadForMultiParamLambda()
    {
        // Check if we have: identifier (, identifier)* ) =>
        var saved = _pos;
        try
        {
            if (!Check(TokenKind.Identifier)) return false;
            Advance();
            while (Check(TokenKind.Comma))
            {
                Advance();
                if (!Check(TokenKind.Identifier)) return false;
                Advance();
            }
            if (!Check(TokenKind.RightParen)) return false;
            Advance();
            return Check(TokenKind.FatArrow);
        }
        finally
        {
            _pos = saved;
        }
    }

    // ── Helpers ──

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
    private Token Previous => _tokens[_pos - 1];
    private bool IsAtEnd => _pos >= _tokens.Count || Current.Kind == TokenKind.Eof;

    private bool Check(TokenKind kind) => !IsAtEnd && Current.Kind == kind;

    private int? TryParseBracketInt()
    {
        var negate = Match(TokenKind.Minus);
        if (Check(TokenKind.NumberLiteral))
        {
            var val = (int)double.Parse(Advance().Text, System.Globalization.CultureInfo.InvariantCulture);
            return negate ? -val : val;
        }
        return null;
    }

    private Token? PeekAt(int offset)
        => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : null;

    private bool Match(TokenKind kind)
    {
        if (!Check(kind)) return false;
        _pos++;
        return true;
    }

    private Token Advance()
    {
        var token = Current;
        _pos++;
        return token;
    }

    private Token Expect(TokenKind kind, string message)
    {
        if (Check(kind)) return Advance();
        throw Error(message);
    }

    private SourceSpan Span(SourceSpan start)
        => new(start.Start, Current.Span.End, start.Line, start.Column);

    private ElwoodParseException Error(string message)
    {
        var diag = new ElwoodDiagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Message = message,
            Span = Current.Span
        };
        _diagnostics.Add(diag);
        return new ElwoodParseException(diag);
    }
}

public class ElwoodParseException : Exception
{
    public ElwoodDiagnostic Diagnostic { get; }

    public ElwoodParseException(ElwoodDiagnostic diagnostic)
        : base(diagnostic.ToString())
    {
        Diagnostic = diagnostic;
    }
}
