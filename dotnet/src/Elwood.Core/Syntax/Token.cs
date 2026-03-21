using Elwood.Core.Diagnostics;

namespace Elwood.Core.Syntax;

public readonly record struct Token(TokenKind Kind, string Text, SourceSpan Span)
{
    public override string ToString() => $"{Kind}({Text})";
}
