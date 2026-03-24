using System.Collections.Concurrent;
using Elwood.Core.Abstractions;

namespace Elwood.Core.Compilation;

/// <summary>
/// Thread-safe cache for compiled Elwood expressions.
/// Keyed by expression source text.
/// </summary>
public sealed class CompilationCache
{
    private readonly ConcurrentDictionary<string, CompiledExpression> _cache = new();

    public CompiledExpression GetOrAdd(string sourceText, Func<CompiledExpression> compile)
        => _cache.GetOrAdd(sourceText, _ => compile());

    public int Count => _cache.Count;

    public void Clear() => _cache.Clear();
}

/// <summary>
/// A compiled Elwood expression — either a fully compiled delegate or null (fallback to interpreter).
/// </summary>
public sealed class CompiledExpression
{
    public static readonly CompiledExpression NotCompilable = new(null);

    /// <summary>
    /// The compiled delegate. Takes (input, factory) and returns the result.
    /// Null if the expression could not be compiled.
    /// </summary>
    public Func<IElwoodValue, IElwoodValueFactory, IElwoodValue>? Delegate { get; }

    public bool IsCompiled => Delegate is not null;

    public CompiledExpression(Func<IElwoodValue, IElwoodValueFactory, IElwoodValue>? @delegate)
    {
        Delegate = @delegate;
    }
}
