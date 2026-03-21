using Elwood.Core.Abstractions;

namespace Elwood.Core.Evaluation;

/// <summary>
/// A lazy array that wraps an IEnumerable without materializing it.
/// Used by streaming pipe operators (where, select, take, skip, etc.)
/// to avoid creating intermediate arrays at each pipeline stage.
/// Materializes on demand when array length, indexing, or serialization is needed.
/// </summary>
internal sealed class LazyArrayValue : IElwoodValue
{
    private readonly IEnumerable<IElwoodValue> _source;
    private readonly IElwoodValueFactory _factory;
    private List<IElwoodValue>? _materialized;

    public LazyArrayValue(IEnumerable<IElwoodValue> source, IElwoodValueFactory factory)
    {
        _source = source;
        _factory = factory;
    }

    public ElwoodValueKind Kind => ElwoodValueKind.Array;

    /// <summary>
    /// Enumerate without materializing — streams elements one at a time.
    /// This is the key method that enables lazy pipelines.
    /// </summary>
    public IEnumerable<IElwoodValue> EnumerateArray()
    {
        if (_materialized is not null)
            return _materialized;
        return _source;
    }

    /// <summary>
    /// Force materialization. Called by operators that need all data
    /// (orderBy, groupBy, count, batch, etc.) and by serialization.
    /// </summary>
    public List<IElwoodValue> Materialize()
    {
        _materialized ??= _source.ToList();
        return _materialized;
    }

    public int GetArrayLength() => Materialize().Count;

    // These don't apply to arrays — return defaults
    public string? GetStringValue() => null;
    public double GetNumberValue() => 0;
    public bool GetBooleanValue() => false;
    public IElwoodValue? GetProperty(string name) => null;
    public IEnumerable<string> GetPropertyNames() => [];
    public IElwoodValue? Parent => null;

    // Factory delegates — create new values through the factory
    public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties) => _factory.CreateObject(properties);
    public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items) => new LazyArrayValue(items, _factory);
    public IElwoodValue CreateString(string value) => _factory.CreateString(value);
    public IElwoodValue CreateNumber(double value) => _factory.CreateNumber(value);
    public IElwoodValue CreateBool(bool value) => _factory.CreateBool(value);
    public IElwoodValue CreateNull() => _factory.CreateNull();
    public IElwoodValue DeepClone()
    {
        // Must materialize to clone
        return _factory.CreateArray(Materialize().Select(v => v.DeepClone()));
    }

    /// <summary>
    /// Materialize to a concrete IElwoodValue (JsonNodeValue) for final output.
    /// </summary>
    public IElwoodValue ToConcreteValue() => _factory.CreateArray(Materialize());
}
