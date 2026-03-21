using Elwood.Core.Abstractions;

namespace Elwood.Core.Evaluation;

/// <summary>
/// Variable scope for Elwood evaluation. Supports nested scopes (let bindings, lambdas).
/// </summary>
public sealed class ElwoodEnvironment
{
    private readonly Dictionary<string, IElwoodValue> _variables = new();
    private readonly ElwoodEnvironment? _parent;

    public ElwoodEnvironment(ElwoodEnvironment? parent = null)
    {
        _parent = parent;
    }

    public void Set(string name, IElwoodValue value) => _variables[name] = value;

    public IElwoodValue? Get(string name)
    {
        if (_variables.TryGetValue(name, out var value))
            return value;
        return _parent?.Get(name);
    }

    public bool Has(string name)
    {
        if (_variables.ContainsKey(name))
            return true;
        return _parent?.Has(name) ?? false;
    }

    public ElwoodEnvironment CreateChild() => new(this);
}
