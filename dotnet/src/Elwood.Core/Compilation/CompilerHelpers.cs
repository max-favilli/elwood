using Elwood.Core.Abstractions;

namespace Elwood.Core.Compilation;

/// <summary>
/// Static helper methods called by compiled delegates. These are extracted from
/// the Evaluator so that compiled Expression Trees can call them directly.
/// </summary>
public static class CompilerHelpers
{
    public static bool IsTruthy(IElwoodValue value) => value.Kind switch
    {
        ElwoodValueKind.Null => false,
        ElwoodValueKind.Boolean => value.GetBooleanValue(),
        ElwoodValueKind.Number => value.GetNumberValue() != 0,
        ElwoodValueKind.String => !string.IsNullOrEmpty(value.GetStringValue()),
        ElwoodValueKind.Array => value.GetArrayLength() > 0,
        ElwoodValueKind.Object => true,
        _ => false
    };

    public static bool ValuesEqual(IElwoodValue a, IElwoodValue b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            ElwoodValueKind.Null => true,
            ElwoodValueKind.Boolean => a.GetBooleanValue() == b.GetBooleanValue(),
            ElwoodValueKind.Number => Math.Abs(a.GetNumberValue() - b.GetNumberValue()) < 1e-10,
            ElwoodValueKind.String => a.GetStringValue() == b.GetStringValue(),
            _ => Serialize(a) == Serialize(b)
        };
    }

    public static int CompareValues(IElwoodValue a, IElwoodValue b)
    {
        if (a.Kind == ElwoodValueKind.Number && b.Kind == ElwoodValueKind.Number)
            return a.GetNumberValue().CompareTo(b.GetNumberValue());
        var sa = a.GetStringValue() ?? "";
        var sb = b.GetStringValue() ?? "";
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    public static string ValueToString(IElwoodValue value) => value.Kind switch
    {
        ElwoodValueKind.String => value.GetStringValue() ?? "",
        ElwoodValueKind.Number => value.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ElwoodValueKind.Boolean => value.GetBooleanValue() ? "true" : "false",
        ElwoodValueKind.Null => "",
        _ => Serialize(value)
    };

    public static string Serialize(IElwoodValue value) => value.Kind switch
    {
        ElwoodValueKind.Null => "null",
        ElwoodValueKind.Boolean => value.GetBooleanValue() ? "true" : "false",
        ElwoodValueKind.Number => value.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ElwoodValueKind.String => $"\"{value.GetStringValue()}\"",
        ElwoodValueKind.Array => $"[{string.Join(",", value.EnumerateArray().Select(Serialize))}]",
        ElwoodValueKind.Object => $"{{{string.Join(",", value.GetPropertyNames().Select(n => $"\"{n}\":{Serialize(value.GetProperty(n)!)}"))}}}",
        _ => "?"
    };

    /// <summary>
    /// Safe property access — returns factory.CreateNull() for missing properties.
    /// </summary>
    public static IElwoodValue GetPropertySafe(IElwoodValue target, string name, IElwoodValueFactory factory)
    {
        if (target.Kind == ElwoodValueKind.Object)
            return target.GetProperty(name) ?? factory.CreateNull();
        if (target.Kind == ElwoodValueKind.Array)
        {
            // Auto-map over arrays
            var mapped = target.EnumerateArray()
                .Select(item => item.GetProperty(name))
                .Where(p => p is not null)
                .Cast<IElwoodValue>();
            return factory.CreateArray(mapped);
        }
        return factory.CreateNull();
    }
}
