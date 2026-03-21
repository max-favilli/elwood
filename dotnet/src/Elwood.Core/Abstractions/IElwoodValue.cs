namespace Elwood.Core.Abstractions;

/// <summary>
/// Abstract representation of a JSON value, decoupled from any specific JSON library.
/// Implementations exist for System.Text.Json (Elwood.Json) and Newtonsoft.Json (Elwood.Newtonsoft).
/// </summary>
public interface IElwoodValue
{
    ElwoodValueKind Kind { get; }

    // Scalar access
    string? GetStringValue();
    double GetNumberValue();
    bool GetBooleanValue();

    // Object access
    IElwoodValue? GetProperty(string name);
    IEnumerable<string> GetPropertyNames();

    // Array access
    IEnumerable<IElwoodValue> EnumerateArray();
    int GetArrayLength();

    // Navigation
    IElwoodValue? Parent { get; }

    // Mutation — create new values (immutable pattern)
    IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties);
    IElwoodValue CreateArray(IEnumerable<IElwoodValue> items);
    IElwoodValue CreateString(string value);
    IElwoodValue CreateNumber(double value);
    IElwoodValue CreateBool(bool value);
    IElwoodValue CreateNull();

    // Deep clone
    IElwoodValue DeepClone();
}

public enum ElwoodValueKind
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null
}
