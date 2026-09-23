using Elwood.Core.Abstractions;

namespace Elwood.Core.Evaluation;

/// <summary>
/// A lightweight object value that holds references to its property values without
/// building a JSON node graph. Used by <c>groupBy</c> for the <c>{ key, items }</c> group
/// objects so that grouping never copies the rows of the input: nested groupings, counts,
/// <c>first</c>, <c>where</c> and so on all run over references. Rows are materialized
/// (and cloned, once) only when a group is embedded into the final output.
/// </summary>
internal sealed class LazyObjectValue : IElwoodValue
{
    private readonly KeyValuePair<string, IElwoodValue>[] _properties;
    private readonly IElwoodValueFactory _factory;

    private Dictionary<string, IElwoodValue>? _index;

    public LazyObjectValue(KeyValuePair<string, IElwoodValue>[] properties, IElwoodValueFactory factory)
    {
        _properties = properties;
        _factory = factory;
    }

    /// <summary>
    /// Builds a lazy object from evaluated properties, applying the same duplicate-key rule as
    /// the concrete JSON object it stands in for: a repeated key (produced by spread followed by
    /// an explicit override) keeps its first position but takes the last value.
    /// </summary>
    public static LazyObjectValue Create(List<KeyValuePair<string, IElwoodValue>> properties, IElwoodValueFactory factory)
    {
        var duplicates = false;
        if (properties.Count > 1)
        {
            var seen = new HashSet<string>(properties.Count);
            foreach (var p in properties)
            {
                if (!seen.Add(p.Key)) { duplicates = true; break; }
            }
        }

        if (!duplicates)
            return new LazyObjectValue(properties.ToArray(), factory);

        var positions = new Dictionary<string, int>(properties.Count);
        var merged = new List<KeyValuePair<string, IElwoodValue>>(properties.Count);
        foreach (var p in properties)
        {
            if (positions.TryGetValue(p.Key, out var at)) merged[at] = p;
            else { positions[p.Key] = merged.Count; merged.Add(p); }
        }
        return new LazyObjectValue(merged.ToArray(), factory);
    }

    public ElwoodValueKind Kind => ElwoodValueKind.Object;

    public IElwoodValue? GetProperty(string name)
    {
        // Linear scan is cheaper than a dictionary for the small objects that dominate;
        // build an index once for wide objects that are read repeatedly.
        if (_properties.Length <= 8)
        {
            foreach (var p in _properties)
                if (p.Key == name) return p.Value;
            return null;
        }

        if (_index is null)
        {
            _index = new Dictionary<string, IElwoodValue>(_properties.Length);
            foreach (var p in _properties) _index[p.Key] = p.Value;
        }
        return _index.TryGetValue(name, out var value) ? value : null;
    }

    public IEnumerable<string> GetPropertyNames() => _properties.Select(p => p.Key);

    // Mirror JsonNodeValue: a non-array value enumerates as a single-element array.
    public IEnumerable<IElwoodValue> EnumerateArray() => [this];
    public int GetArrayLength() => 1;

    public string? GetStringValue() => null;
    public double GetNumberValue() => 0;
    public bool GetBooleanValue() => false;
    public IElwoodValue? Parent => null;

    public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties) => _factory.CreateObject(properties);
    public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items) => new LazyArrayValue(items, _factory);
    public IElwoodValue CreateString(string value) => _factory.CreateString(value);
    public IElwoodValue CreateNumber(double value) => _factory.CreateNumber(value);
    public IElwoodValue CreateBool(bool value) => _factory.CreateBool(value);
    public IElwoodValue CreateNull() => _factory.CreateNull();

    public IElwoodValue DeepClone()
        => _factory.CreateObject(_properties.Select(p => new KeyValuePair<string, IElwoodValue>(p.Key, p.Value.DeepClone())));

    /// <summary>Materialize to a concrete factory value for final output.</summary>
    public IElwoodValue ToConcreteValue() => _factory.CreateObject(_properties);
}
