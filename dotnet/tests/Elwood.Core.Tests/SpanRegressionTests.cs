using Elwood.Core.Diagnostics;
using Elwood.Core.Parsing;
using Elwood.Core.Syntax;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// Regression tests for path segment spans. The parser used to build segment
/// spans from the token *after* the consumed identifier, so runtime errors like
/// "Property 'x' not found" were reported at the next token — which could be a
/// whole statement away in multi-statement scripts.
/// </summary>
public class SpanRegressionTests
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;

    [Fact]
    public void PropertyNotFound_InLetBinding_ReportsPositionOfProperty()
    {
        // 'bar' starts at line 1, column 13. Before the fix the error was
        // reported at the 'return' keyword (line 3, column 1).
        var script = "let foo = $.bar\n\nreturn { x: foo }";
        var input = _factory.Parse("""{ "baz": 1 }""");

        var result = _engine.Execute(script, input);

        Assert.False(result.Success);
        var diag = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("'bar'", diag.Message);
        Assert.Equal(1, diag.Span.Line);
        Assert.Equal(13, diag.Span.Column);
    }

    [Fact]
    public void PropertyNotFound_InExpression_ReportsPositionOfProperty()
    {
        var input = _factory.Parse("""{ "metadata": { "version": "1.0" } }""");

        // 'missing' starts at column 12
        var result = _engine.Evaluate("$.metadata.missing", input);

        Assert.False(result.Success);
        var diag = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("'missing'", diag.Message);
        Assert.Equal(1, diag.Span.Line);
        Assert.Equal(12, diag.Span.Column);
    }

    [Fact]
    public void PropertySegmentSpans_PointAtTheIdentifier()
    {
        var path = ParsePath("$.foo.bar");

        var foo = Assert.IsType<PropertySegment>(path.Segments[0]);
        Assert.Equal(1, foo.Span.Line);
        Assert.Equal(3, foo.Span.Column);
        Assert.Equal("foo".Length, foo.Span.Length);

        var bar = Assert.IsType<PropertySegment>(path.Segments[1]);
        Assert.Equal(1, bar.Span.Line);
        Assert.Equal(7, bar.Span.Column);
        Assert.Equal("bar".Length, bar.Span.Length);
    }

    [Fact]
    public void OptionalChainingSegmentSpan_PointsAtTheIdentifier()
    {
        var path = ParsePath("$?.foo");

        var foo = Assert.IsType<PropertySegment>(path.Segments[0]);
        Assert.True(foo.Optional);
        Assert.Equal(1, foo.Span.Line);
        Assert.Equal(4, foo.Span.Column);
        Assert.Equal("foo".Length, foo.Span.Length);
    }

    [Fact]
    public void RecursiveDescentSegmentSpan_PointsAtTheIdentifier()
    {
        var path = ParsePath("$.foo..name");

        var name = Assert.IsType<RecursiveDescentSegment>(path.Segments[1]);
        Assert.Equal(1, name.Span.Line);
        Assert.Equal(8, name.Span.Column);
        Assert.Equal("name".Length, name.Span.Length);
    }

    [Fact]
    public void IndexAndSliceSegmentSpans_PointAtTheBrackets()
    {
        var path = ParsePath("$.items[0].parts[1:3]");

        var index = Assert.IsType<IndexSegment>(path.Segments[1]);
        Assert.Equal(8, index.Span.Column); // '[' of [0]
        Assert.Equal("[0]".Length, index.Span.Length);

        var slice = Assert.IsType<SliceSegment>(path.Segments[3]);
        Assert.Equal(17, slice.Span.Column); // '[' of [1:3]
        Assert.Equal("[1:3]".Length, slice.Span.Length);
    }

    private static PathExpression ParsePath(string expression)
    {
        var lexer = new Lexer(expression);
        var parser = new Parser(lexer.Tokenize());
        var ast = parser.ParseExpression();
        return Assert.IsType<PathExpression>(ast);
    }
}
