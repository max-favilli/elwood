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

    // ── Arithmetic with string concatenation ──

    public static IElwoodValue Add(IElwoodValue left, IElwoodValue right, IElwoodValueFactory f)
    {
        if (left.Kind == ElwoodValueKind.String || right.Kind == ElwoodValueKind.String)
            return f.CreateString(CompilerHelpers.ValueToString(left) + CompilerHelpers.ValueToString(right));
        return f.CreateNumber(left.GetNumberValue() + right.GetNumberValue());
    }

    // ── Compiled method implementations ──
    // These replace the big switch in EvaluateBuiltinMethod for compiled delegates.

    public static IElwoodValue MethodToLower(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateString((target.GetStringValue() ?? "").ToLowerInvariant());

    public static IElwoodValue MethodToUpper(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateString((target.GetStringValue() ?? "").ToUpperInvariant());

    public static IElwoodValue MethodTrim(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateString((target.GetStringValue() ?? "").Trim());

    public static IElwoodValue MethodTrimStart(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateString((target.GetStringValue() ?? "").TrimStart());

    public static IElwoodValue MethodTrimEnd(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateString((target.GetStringValue() ?? "").TrimEnd());

    public static IElwoodValue MethodLength(IElwoodValue target, IElwoodValueFactory f)
        => target.Kind == ElwoodValueKind.Array
            ? f.CreateNumber(target.GetArrayLength())
            : f.CreateNumber((target.GetStringValue() ?? "").Length);

    public static IElwoodValue MethodContains(IElwoodValue target, IElwoodValue arg, IElwoodValueFactory f)
        => f.CreateBool((target.GetStringValue() ?? "").Contains(arg.GetStringValue() ?? "", StringComparison.OrdinalIgnoreCase));

    public static IElwoodValue MethodStartsWith(IElwoodValue target, IElwoodValue arg, IElwoodValueFactory f)
        => f.CreateBool((target.GetStringValue() ?? "").StartsWith(arg.GetStringValue() ?? "", StringComparison.OrdinalIgnoreCase));

    public static IElwoodValue MethodEndsWith(IElwoodValue target, IElwoodValue arg, IElwoodValueFactory f)
        => f.CreateBool((target.GetStringValue() ?? "").EndsWith(arg.GetStringValue() ?? "", StringComparison.OrdinalIgnoreCase));

    public static IElwoodValue MethodReplace(IElwoodValue target, IElwoodValue search, IElwoodValue replacement, IElwoodValueFactory f)
        => f.CreateString((target.GetStringValue() ?? "").Replace(search.GetStringValue() ?? "", replacement.GetStringValue() ?? ""));

    public static IElwoodValue MethodSubstring(IElwoodValue target, IElwoodValue start, IElwoodValue? length, IElwoodValueFactory f)
    {
        var s = target.GetStringValue() ?? "";
        var startIdx = Math.Max(0, (int)start.GetNumberValue());
        if (startIdx >= s.Length) return f.CreateString("");
        if (length is not null)
        {
            var len = Math.Max(0, (int)length.GetNumberValue());
            return f.CreateString(s.Substring(startIdx, Math.Min(len, s.Length - startIdx)));
        }
        return f.CreateString(s[startIdx..]);
    }

    public static IElwoodValue MethodSplit(IElwoodValue target, IElwoodValue delimiter, IElwoodValueFactory f)
    {
        var parts = (target.GetStringValue() ?? "").Split(delimiter.GetStringValue() ?? ",");
        return f.CreateArray(parts.Select(p => f.CreateString(p)));
    }

    public static IElwoodValue MethodToString(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateString(CompilerHelpers.ValueToString(target));

    public static IElwoodValue MethodToNumber(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateNumber(double.TryParse(target.GetStringValue(), out var n) ? n : 0);

    public static IElwoodValue MethodNot(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateBool(!CompilerHelpers.IsTruthy(target));

    public static IElwoodValue MethodIsNull(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateBool(target.Kind == ElwoodValueKind.Null);

    public static IElwoodValue MethodIsNullOrEmpty(IElwoodValue target, IElwoodValueFactory f)
        => f.CreateBool(target.Kind == ElwoodValueKind.Null || string.IsNullOrEmpty(target.GetStringValue()));

    public static IElwoodValue MethodIsNullOrEmptyWithFallback(IElwoodValue target, IElwoodValue fallback, IElwoodValueFactory f)
        => target.Kind == ElwoodValueKind.Null || string.IsNullOrEmpty(target.GetStringValue()) ? fallback : target;
}
