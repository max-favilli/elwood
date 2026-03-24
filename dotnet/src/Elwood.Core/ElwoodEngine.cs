using Elwood.Core.Abstractions;
using Elwood.Core.Compilation;
using Elwood.Core.Diagnostics;
using Elwood.Core.Evaluation;
using Elwood.Core.Extensions;
using Elwood.Core.Parsing;
using Elwood.Core.Syntax;

namespace Elwood.Core;

/// <summary>
/// Main entry point for the Elwood DSL engine.
/// </summary>
public sealed class ElwoodEngine
{
    private readonly IElwoodValueFactory _factory;
    private readonly ElwoodExtensionRegistry _extensions = new();
    private readonly CompilationCache _compilationCache = new();
    private readonly ElwoodCompiler _compiler = new();

    /// <summary>
    /// When true, the engine attempts to compile expressions to delegates
    /// for faster execution. Falls back to the interpreter for unsupported constructs.
    /// </summary>
    public bool CompiledMode { get; set; }

    public ElwoodEngine(IElwoodValueFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Register a custom method provided by an extension package.
    /// Extensions cannot override built-in methods.
    /// </summary>
    public void RegisterMethod(string name, ElwoodMethodHandler handler)
        => _extensions.RegisterMethod(name, handler);

    /// <summary>
    /// Evaluate a single Elwood expression against input data.
    /// </summary>
    public ElwoodResult Evaluate(string expression, IElwoodValue input)
    {
        var diagnostics = new List<ElwoodDiagnostic>();

        try
        {
            var lexer = new Lexer(expression);
            var tokens = lexer.Tokenize();
            diagnostics.AddRange(lexer.Diagnostics);

            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new ElwoodResult(null, diagnostics);

            var parser = new Parser(tokens);
            var ast = parser.ParseExpression();
            diagnostics.AddRange(parser.Diagnostics);

            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new ElwoodResult(null, diagnostics);

            // Try compiled mode first
            if (CompiledMode)
            {
                var compiled = _compilationCache.GetOrAdd(expression, () =>
                    new CompiledExpression(_compiler.TryCompile(ast)));
                if (compiled.IsCompiled)
                    return new ElwoodResult(compiled.Delegate!(input, _factory), diagnostics);
            }

            // Fallback to interpreter
            var evaluator = new Evaluator(_factory, _extensions);
            var env = new ElwoodEnvironment();
            env.Set("$root", input);
            var result = evaluator.Evaluate(ast, input, env);

            return new ElwoodResult(result, diagnostics);
        }
        catch (ElwoodParseException ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return new ElwoodResult(null, diagnostics);
        }
        catch (ElwoodEvaluationException ex)
        {
            diagnostics.Add(new ElwoodDiagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = ex.BaseMessage,
                Span = ex.Span,
                Suggestion = ex.Suggestion
            });
            return new ElwoodResult(null, diagnostics);
        }
    }

    /// <summary>
    /// Execute an Elwood script (with let bindings and return) against input data.
    /// </summary>
    public ElwoodResult Execute(string script, IElwoodValue input)
    {
        var diagnostics = new List<ElwoodDiagnostic>();

        try
        {
            var lexer = new Lexer(script);
            var tokens = lexer.Tokenize();
            diagnostics.AddRange(lexer.Diagnostics);

            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new ElwoodResult(null, diagnostics);

            var parser = new Parser(tokens);
            var ast = parser.ParseScript();
            diagnostics.AddRange(parser.Diagnostics);

            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new ElwoodResult(null, diagnostics);

            // Try compiled mode first
            if (CompiledMode)
            {
                var compiled = _compilationCache.GetOrAdd("script:" + script, () =>
                    new CompiledExpression(_compiler.TryCompile(ast)));
                if (compiled.IsCompiled)
                    return new ElwoodResult(compiled.Delegate!(input, _factory), diagnostics);
            }

            // Fallback to interpreter
            var evaluator = new Evaluator(_factory, _extensions);
            var result = evaluator.EvaluateScript(ast, input);

            return new ElwoodResult(result, diagnostics);
        }
        catch (ElwoodParseException ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return new ElwoodResult(null, diagnostics);
        }
        catch (ElwoodEvaluationException ex)
        {
            diagnostics.Add(new ElwoodDiagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = ex.BaseMessage,
                Span = ex.Span,
                Suggestion = ex.Suggestion
            });
            return new ElwoodResult(null, diagnostics);
        }
    }
}

/// <summary>
/// Result of an Elwood evaluation, including any diagnostics.
/// </summary>
public sealed class ElwoodResult
{
    public IElwoodValue? Value { get; }
    public IReadOnlyList<ElwoodDiagnostic> Diagnostics { get; }
    public bool Success => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public ElwoodResult(IElwoodValue? value, IReadOnlyList<ElwoodDiagnostic> diagnostics)
    {
        Value = value;
        Diagnostics = diagnostics;
    }
}
