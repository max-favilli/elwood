using Elwood.Core.Abstractions;

namespace Elwood.Core.Evaluation;

/// <summary>
/// Conversion of the evaluator's internal lazy values into concrete factory values.
/// Applied once at the engine boundary so hosts always receive concrete JSON.
/// </summary>
internal static class LazyValues
{
    public static IElwoodValue ToConcrete(IElwoodValue value) => value switch
    {
        LazyArrayValue lazyArray => lazyArray.ToConcreteValue(),
        LazyObjectValue lazyObject => lazyObject.ToConcreteValue(),
        _ => value
    };
}
