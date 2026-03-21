namespace Elwood.Core.Syntax;

public enum TokenKind
{
    // Literals
    StringLiteral,      // "hello" or 'hello'
    NumberLiteral,      // 42, 3.14
    TrueLiteral,        // true
    FalseLiteral,       // false
    NullLiteral,        // null

    // Identifiers & paths
    Identifier,         // variable names, function names
    Dollar,             // $
    DollarDot,          // $.
    Dot,                // .
    DotDot,             // ..  (recursive descent)

    // Brackets
    LeftBracket,        // [
    RightBracket,       // ]
    LeftParen,          // (
    RightParen,         // )
    LeftBrace,          // {
    RightBrace,         // }

    // Operators
    Pipe,               // |
    FatArrow,           // =>
    Comma,              // ,
    Colon,              // :
    Star,               // *
    Spread,             // ...

    // Arithmetic
    Plus,               // +
    Minus,              // -
    Slash,              // /

    // Assignment
    Assign,             // =

    // Comparison
    EqualEqual,         // ==
    BangEqual,          // !=
    LessThan,           // <
    LessThanOrEqual,    // <=
    GreaterThan,        // >
    GreaterThanOrEqual, // >=

    // Logical
    AmpersandAmpersand, // &&
    PipePipe,           // ||
    Bang,               // !

    // Keywords
    Let,                // let
    If,                 // if
    Then,               // then
    Else,               // else
    Match,              // match
    Return,             // return
    From,               // from
    Asc,                // asc
    Desc,               // desc
    On,                 // on
    Equals,             // equals
    Into,               // into
    Underscore,         // _ (wildcard in match)
    Memo,               // memo

    // Interpolation
    Backtick,           // `
    InterpolationStart, // { inside backtick string
    InterpolationEnd,   // } inside backtick string

    // Special
    Newline,
    Eof
}
