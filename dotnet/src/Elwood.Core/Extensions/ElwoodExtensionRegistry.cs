using Elwood.Core.Abstractions;

namespace Elwood.Core.Extensions;

/// <summary>
/// Delegate for extension-provided methods.
/// Target is the value the method is called on, args are the arguments, factory creates return values.
/// </summary>
public delegate IElwoodValue ElwoodMethodHandler(
    IElwoodValue target,
    List<IElwoodValue> args,
    IElwoodValueFactory factory);

/// <summary>
/// Registry for extension-provided methods. Extensions register handlers here;
/// the evaluator consults the registry when a method name is not a built-in.
/// </summary>
public sealed class ElwoodExtensionRegistry
{
    private readonly Dictionary<string, ElwoodMethodHandler> _methods = new(StringComparer.Ordinal);

    public void RegisterMethod(string name, ElwoodMethodHandler handler)
    {
        _methods[name] = handler;
    }

    public bool TryGetMethod(string name, out ElwoodMethodHandler? handler)
        => _methods.TryGetValue(name, out handler);
}
