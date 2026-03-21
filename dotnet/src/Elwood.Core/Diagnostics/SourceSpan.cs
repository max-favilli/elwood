namespace Elwood.Core.Diagnostics;

/// <summary>
/// Represents a location range in source text for error reporting.
/// </summary>
public readonly record struct SourceSpan(int Start, int End, int Line, int Column)
{
    public int Length => End - Start;

    public static SourceSpan Empty => new(0, 0, 0, 0);
}

/// <summary>
/// A diagnostic message (error, warning, info) with source location.
/// </summary>
public sealed class ElwoodDiagnostic
{
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public SourceSpan Span { get; init; }
    public string? Suggestion { get; init; }

    public override string ToString()
    {
        var prefix = Severity switch
        {
            DiagnosticSeverity.Error => "Error",
            DiagnosticSeverity.Warning => "Warning",
            _ => "Info"
        };
        var location = Span.Line > 0 ? $" at line {Span.Line}, col {Span.Column}" : "";
        var suggestion = Suggestion is not null ? $" {Suggestion}" : "";
        return $"{prefix}{location}: {Message}{suggestion}";
    }
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}
