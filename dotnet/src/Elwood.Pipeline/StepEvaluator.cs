using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Core.Parsing;
using Elwood.Core.Syntax;
using Elwood.Json;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline;

/// <summary>Caller-side evaluation contract shared by pipeline executors and API tools.</summary>
public sealed class StepEvaluator(ElwoodEngine engine)
{
    // The lexer skips comments and treats strings as single tokens. Only a leading
    // statement keyword introduces a script; keywords inside values are expressions.
    public static bool IsScript(string source)
        => new Lexer(source).Tokenize()[0].Kind is TokenKind.Let or TokenKind.Return;

    public IElwoodValue Evaluate(string reference, IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings, IReadOnlyDictionary<string, string> scripts,
        List<StepDiagnostic> diagnostics)
    {
        var expression = reference;
        if (reference.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase) &&
            !scripts.TryGetValue(reference, out expression))
        {
            var message = $"Script not found: {reference}";
            diagnostics.Add(new StepDiagnostic { Reference = reference, Severity = "error", Message = message });
            throw new InvalidOperationException(message);
        }

        var result = IsScript(expression)
            ? engine.Execute(expression, input, bindings)
            : engine.Evaluate(expression.Trim(), input, bindings);
        diagnostics.AddRange(result.Diagnostics.Select(d => new StepDiagnostic
        {
            Reference = reference,
            Severity = d.Severity.ToString().ToLowerInvariant(),
            Message = d.Message,
            Line = d.Span.Line,
            Column = d.Span.Column,
            Suggestion = d.Suggestion,
        }));
        if (!result.Success)
            throw new InvalidOperationException($"Evaluation '{reference}' failed: " +
                string.Join("; ", result.Diagnostics.Where(d => d.Severity == Core.Diagnostics.DiagnosticSeverity.Error)));

        // DSL null is a value, never the signal for an unsuccessful evaluation.
        return result.Value ?? JsonNodeValueFactory.Instance.CreateNull();
    }
}
