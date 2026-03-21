namespace Elwood.Core.Abstractions;

/// <summary>
/// Factory for creating IElwoodValue instances from raw data.
/// Each JSON library adapter provides its own implementation.
/// </summary>
public interface IElwoodValueFactory
{
    IElwoodValue Parse(string json);
    IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties);
    IElwoodValue CreateArray(IEnumerable<IElwoodValue> items);
    IElwoodValue CreateString(string value);
    IElwoodValue CreateNumber(double value);
    IElwoodValue CreateBool(bool value);
    IElwoodValue CreateNull();
}
