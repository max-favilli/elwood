using Elwood.Core.Diagnostics;
using Elwood.Core.Syntax;

namespace Elwood.Core.Parsing;

/// <summary>
/// Tokenizes Elwood source text into a sequence of tokens.
/// </summary>
public sealed class Lexer
{
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private readonly List<ElwoodDiagnostic> _diagnostics = [];

    public IReadOnlyList<ElwoodDiagnostic> Diagnostics => _diagnostics;

    public Lexer(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (_pos < _source.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _source.Length) break;

            var token = ReadToken();
            if (token.Kind != TokenKind.Newline) // skip newlines for now; parser is newline-insensitive
                tokens.Add(token);
        }

        tokens.Add(MakeToken(TokenKind.Eof, "", _pos, _pos));
        return tokens;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            var c = _source[_pos];

            // Whitespace (but not newline)
            if (c is ' ' or '\t' or '\r')
            {
                Advance();
                continue;
            }

            // Newline
            if (c == '\n')
            {
                Advance();
                _line++;
                _col = 1;
                continue;
            }

            // Single-line comment: //
            if (c == '/' && Peek(1) == '/')
            {
                while (_pos < _source.Length && _source[_pos] != '\n')
                    Advance();
                continue;
            }

            // Multi-line comment: /* ... */
            if (c == '/' && Peek(1) == '*')
            {
                Advance(); Advance(); // skip /*
                while (_pos < _source.Length - 1 && !(_source[_pos] == '*' && _source[_pos + 1] == '/'))
                {
                    if (_source[_pos] == '\n') { _line++; _col = 1; }
                    Advance();
                }
                if (_pos < _source.Length - 1) { Advance(); Advance(); } // skip */
                continue;
            }

            break;
        }
    }

    private Token ReadToken()
    {
        var start = _pos;
        var startLine = _line;
        var startCol = _col;
        var c = _source[_pos];

        // String literals
        if (c is '"' or '\'')
            return ReadString(c);

        // Backtick string (interpolated)
        if (c == '`')
            return ReadInterpolatedString();

        // Numbers
        if (char.IsDigit(c) || (c == '.' && Peek(1) is char next && char.IsDigit(next)))
            return ReadNumber();

        // $ (dollar — start of path, standalone, or $-prefixed identifier like $source, $idm)
        if (c == '$')
        {
            Advance();
            if (Current == '.')
            {
                if (Peek(1) == '.')
                {
                    Advance(); Advance();
                    return MakeToken(TokenKind.DotDot, "$..", start, _pos, startLine, startCol);
                }
                Advance();
                return MakeToken(TokenKind.DollarDot, "$.", start, _pos, startLine, startCol);
            }
            // $identifier — named binding (e.g., $source, $idm, $output, $secrets, $root)
            if (Current is char nc && (char.IsLetter(nc) || nc == '_'))
            {
                while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
                    Advance();
                return MakeToken(TokenKind.Identifier, _source[start.._pos], start, _pos, startLine, startCol);
            }
            return MakeToken(TokenKind.Dollar, "$", start, _pos, startLine, startCol);
        }

        // Identifiers and keywords
        if (char.IsLetter(c) || c == '_')
            return ReadIdentifierOrKeyword();

        // Three-character operators
        if (_pos + 2 < _source.Length)
        {
            var three = _source.Substring(_pos, 3);
            if (three == "...")
            {
                Advance(); Advance(); Advance();
                return MakeToken(TokenKind.Spread, "...", start, _pos, startLine, startCol);
            }
        }

        // Two-character operators
        if (_pos + 1 < _source.Length)
        {
            var two = _source.Substring(_pos, 2);
            var kind2 = two switch
            {
                "=>" => TokenKind.FatArrow,
                "==" => TokenKind.EqualEqual,
                "!=" => TokenKind.BangEqual,
                "<=" => TokenKind.LessThanOrEqual,
                ">=" => TokenKind.GreaterThanOrEqual,
                "&&" => TokenKind.AmpersandAmpersand,
                "||" => TokenKind.PipePipe,
                ".." => TokenKind.DotDot,
                _ => (TokenKind?)null
            };
            if (kind2 is not null)
            {
                Advance(); Advance();
                return MakeToken(kind2.Value, two, start, _pos, startLine, startCol);
            }
        }

        // Single-character operators
        Advance();
        var kind1 = c switch
        {
            '.' => TokenKind.Dot,
            '|' => TokenKind.Pipe,
            ',' => TokenKind.Comma,
            ':' => TokenKind.Colon,
            '(' => TokenKind.LeftParen,
            ')' => TokenKind.RightParen,
            '[' => TokenKind.LeftBracket,
            ']' => TokenKind.RightBracket,
            '{' => TokenKind.LeftBrace,
            '}' => TokenKind.RightBrace,
            '*' => TokenKind.Star,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '/' => TokenKind.Slash,
            '<' => TokenKind.LessThan,
            '>' => TokenKind.GreaterThan,
            '!' => TokenKind.Bang,
            '=' => TokenKind.Assign,
            '_' => TokenKind.Underscore,
            '\n' => TokenKind.Newline,
            _ => (TokenKind?)null
        };

        if (kind1 is not null)
            return MakeToken(kind1.Value, c.ToString(), start, _pos, startLine, startCol);

        _diagnostics.Add(new ElwoodDiagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Message = $"Unexpected character '{c}'",
            Span = new SourceSpan(start, _pos, startLine, startCol)
        });

        return MakeToken(TokenKind.Eof, "", start, _pos, startLine, startCol);
    }

    private Token ReadString(char quote)
    {
        var start = _pos;
        var startLine = _line;
        var startCol = _col;
        Advance(); // skip opening quote

        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length && _source[_pos] != quote)
        {
            if (_source[_pos] == '\\' && _pos + 1 < _source.Length)
            {
                Advance();
                sb.Append(_source[_pos] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    var q when q == quote => quote,
                    var other => other
                });
                Advance();
            }
            else
            {
                sb.Append(_source[_pos]);
                Advance();
            }
        }

        if (_pos < _source.Length) Advance(); // skip closing quote

        return MakeToken(TokenKind.StringLiteral, sb.ToString(), start, _pos, startLine, startCol);
    }

    private Token ReadInterpolatedString()
    {
        // For now, read the entire backtick string as a single token.
        // The parser will handle interpolation parsing.
        var start = _pos;
        var startLine = _line;
        var startCol = _col;
        Advance(); // skip opening backtick

        var sb = new System.Text.StringBuilder();
        var depth = 0;
        while (_pos < _source.Length && !(_source[_pos] == '`' && depth == 0))
        {
            if (_source[_pos] == '{') depth++;
            else if (_source[_pos] == '}') depth--;
            sb.Append(_source[_pos]);
            Advance();
        }

        if (_pos < _source.Length) Advance(); // skip closing backtick

        return MakeToken(TokenKind.Backtick, sb.ToString(), start, _pos, startLine, startCol);
    }

    private Token ReadNumber()
    {
        var start = _pos;
        var startLine = _line;
        var startCol = _col;

        while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
            Advance();

        var text = _source[start.._pos];
        return MakeToken(TokenKind.NumberLiteral, text, start, _pos, startLine, startCol);
    }

    private Token ReadIdentifierOrKeyword()
    {
        var start = _pos;
        var startLine = _line;
        var startCol = _col;

        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            Advance();

        var text = _source[start.._pos];

        var kind = text switch
        {
            "let" => TokenKind.Let,
            "if" => TokenKind.If,
            "then" => TokenKind.Then,
            "else" => TokenKind.Else,
            "match" => TokenKind.Match,
            "return" => TokenKind.Return,
            "true" => TokenKind.TrueLiteral,
            "false" => TokenKind.FalseLiteral,
            "null" => TokenKind.NullLiteral,
            "asc" => TokenKind.Asc,
            "desc" => TokenKind.Desc,
            "on" => TokenKind.On,
            "equals" => TokenKind.Equals,
            "into" => TokenKind.Into,
            "from" => TokenKind.From,
            "memo" => TokenKind.Memo,
            "_" => TokenKind.Underscore,
            _ => TokenKind.Identifier
        };

        return MakeToken(kind, text, start, _pos, startLine, startCol);
    }

    private char Current => _pos < _source.Length ? _source[_pos] : '\0';
    private char? Peek(int offset) => _pos + offset < _source.Length ? _source[_pos + offset] : null;
    private void Advance() { _pos++; _col++; }

    private Token MakeToken(TokenKind kind, string text, int start, int end, int? line = null, int? col = null)
        => new(kind, text, new SourceSpan(start, end, line ?? _line, col ?? _col));
}
