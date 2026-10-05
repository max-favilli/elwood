using Elwood.Core.Abstractions;
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

    public ElwoodEngine(IElwoodValueFactory factory)
    {
        _factory = factory;
    }

    /// <summary>The default for <see cref="ScanWarningThreshold"/>.</summary>
    public const long DefaultScanWarningThreshold = 10_000_000;

    /// <summary>
    /// How many predicate evaluations a single <c>any</c>/<c>all</c> in a script may add up to,
    /// over repeated runs of it, before the result carries a <see cref="DiagnosticSeverity.Warning"/>
    /// naming it. Such a total means the quantifier scans its input once per row of an enclosing
    /// collection, which is quadratic. The result is still successful and its value unaffected.
    /// Set to zero to disable.
    /// </summary>
    public long ScanWarningThreshold { get; set; } = DefaultScanWarningThreshold;

    /// <summary>
    /// Register a custom method provided by an extension package.
    /// Extensions cannot override built-in methods.
    /// </summary>
    public void RegisterMethod(string name, ElwoodMethodHandler handler)
        => _extensions.RegisterMethod(name, handler);

    /// <summary>
    /// Evaluate a single Elwood expression against input data.
    /// </summary>
    public ElwoodResult Evaluate(string expression, IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings = null)
        => Run(expression, isScript: false, input, bindings, LazyValues.ToConcrete);

    /// <summary>
    /// Execute an Elwood script (with let bindings and return) against input data.
    /// </summary>
    public ElwoodResult Execute(string script, IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings = null)
        => Run(script, isScript: true, input, bindings, LazyValues.ToConcrete);

    /// <summary>
    /// Evaluate an expression and hand the result to <paramref name="consume"/> <b>without</b>
    /// building a concrete JSON graph for it. Intended for writing a result straight to an
    /// output stream: the graph that <see cref="Evaluate"/> would materialise never exists.
    /// </summary>
    /// <remarks>
    /// The value passed to <paramref name="consume"/> holds references into the input and into
    /// the evaluator's own structures. Read it inside the callback and do not retain it.
    /// <para>
    /// <paramref name="consume"/> runs inside the engine's error handling, so an Elwood
    /// evaluation failure triggered while the value is being read is reported as a diagnostic
    /// rather than thrown. Other exceptions, such as an I/O failure from the writer, propagate.
    /// Write into a buffer and only flush it once <see cref="ElwoodResult.Success"/> is true, so
    /// a failure cannot leave a partially written response behind.
    /// </para>
    /// <para>The returned result's <see cref="ElwoodResult.Value"/> is always null: the value
    /// was consumed, not materialised.</para>
    /// </remarks>
    public ElwoodResult EvaluateTo(string expression, IElwoodValue input,
        Action<IElwoodValue> consume, Dictionary<string, IElwoodValue>? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(consume);
        return Run(expression, isScript: false, input, bindings, Consuming(consume));
    }

    /// <summary>
    /// Execute a script and hand the result to <paramref name="consume"/> without building a
    /// concrete JSON graph. See <see cref="EvaluateTo"/> for the contract.
    /// </summary>
    public ElwoodResult ExecuteTo(string script, IElwoodValue input,
        Action<IElwoodValue> consume, Dictionary<string, IElwoodValue>? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(consume);
        return Run(script, isScript: true, input, bindings, Consuming(consume));
    }

    private static Func<IElwoodValue, IElwoodValue?> Consuming(Action<IElwoodValue> consume)
        => value => { consume(value); return null; };

    /// <summary>
    /// Shared pipeline: lex, parse, evaluate, then hand the raw result to <paramref name="finish"/>,
    /// which either materialises it or streams it. Running <paramref name="finish"/> inside the
    /// try block is what lets a streaming consumer report failures as diagnostics.
    /// </summary>
    private ElwoodResult Run(string source, bool isScript, IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings, Func<IElwoodValue, IElwoodValue?> finish)
    {
        var diagnostics = new List<ElwoodDiagnostic>();
        Evaluator? evaluator = null;

        try
        {
            var lexer = new Lexer(source);
            var tokens = lexer.Tokenize();
            diagnostics.AddRange(lexer.Diagnostics);

            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new ElwoodResult(null, diagnostics);

            var parser = new Parser(tokens);
            ElwoodExpression? expressionAst = null;
            ScriptNode? scriptAst = null;
            if (isScript) scriptAst = parser.ParseScript();
            else expressionAst = parser.ParseExpression();
            diagnostics.AddRange(parser.Diagnostics);

            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new ElwoodResult(null, diagnostics);

            evaluator = new Evaluator(_factory, _extensions) { ScanWarningThreshold = ScanWarningThreshold };
            IElwoodValue result;
            if (isScript)
            {
                result = evaluator.EvaluateScript(scriptAst!, input, bindings);
            }
            else
            {
                var env = new ElwoodEnvironment();
                env.Set("$", input);
                env.Set("$root", input);
                if (bindings is not null)
                    foreach (var (key, value) in bindings)
                        env.Set(key, value);
                result = evaluator.Evaluate(expressionAst!, input, env);
            }

            // Evaluation is lazy: much of the work happens while the result is consumed, so
            // the evaluator's diagnostics are complete only after that.
            var finished = finish(result);
            diagnostics.AddRange(evaluator.Diagnostics);
            return new ElwoodResult(finished, diagnostics);
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
            if (evaluator is not null) diagnostics.AddRange(evaluator.Diagnostics);
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
