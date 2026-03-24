using Elwood.Core.Abstractions;

namespace Elwood.Core.Compilation;

/// <summary>
/// Fast-path helpers that bypass IElwoodValue interface dispatch for common operations.
/// These are called by compiled delegates when type checks pass.
/// </summary>
public static class FastPathHelpers
{
    /// <summary>
    /// Fast property access + string comparison — the most common where predicate pattern.
    /// Returns true if obj.propertyName == expected (string comparison).
    /// </summary>
    public static bool PropertyEqualsString(IElwoodValue obj, string propertyName, string expected)
    {
        var prop = obj.GetProperty(propertyName);
        if (prop is null) return false;
        return prop.GetStringValue() == expected;
    }

    /// <summary>
    /// Fast property access + numeric comparison.
    /// </summary>
    public static bool PropertyGreaterThan(IElwoodValue obj, string propertyName, double threshold)
    {
        var prop = obj.GetProperty(propertyName);
        if (prop is null) return false;
        return prop.GetNumberValue() > threshold;
    }

    public static bool PropertyGreaterThanOrEqual(IElwoodValue obj, string propertyName, double threshold)
    {
        var prop = obj.GetProperty(propertyName);
        if (prop is null) return false;
        return prop.GetNumberValue() >= threshold;
    }

    public static bool PropertyLessThan(IElwoodValue obj, string propertyName, double threshold)
    {
        var prop = obj.GetProperty(propertyName);
        if (prop is null) return false;
        return prop.GetNumberValue() < threshold;
    }

    /// <summary>
    /// Fast truthiness check for a property value.
    /// </summary>
    public static bool PropertyIsTruthy(IElwoodValue obj, string propertyName)
    {
        var prop = obj.GetProperty(propertyName);
        if (prop is null) return false;
        return CompilerHelpers.IsTruthy(prop);
    }

    /// <summary>
    /// Fast property access returning IElwoodValue (avoids GetPropertySafe overhead for non-null cases).
    /// </summary>
    public static IElwoodValue GetPropertyFast(IElwoodValue obj, string name, IElwoodValueFactory factory)
    {
        return obj.GetProperty(name) ?? factory.CreateNull();
    }
}
