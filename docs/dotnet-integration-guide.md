# Elwood .NET Integration Guide

How to add Elwood scripting to a .NET 10 project for JSON data transformation.

---

## Installation

Add the two required NuGet packages:

```bash
dotnet add package Elwood.Core --version 0.4.0
dotnet add package Elwood.Json --version 0.4.0
```

**Elwood.Core** contains the engine, parser, and evaluator. **Elwood.Json** provides the `System.Text.Json` adapter. Both target `net8.0` and `net10.0`.

Source: https://github.com/max-favilli/elwood

---

## Minimal Example

```csharp
using Elwood.Core;
using Elwood.Json;

var factory = JsonNodeValueFactory.Instance;
var engine = new ElwoodEngine(factory);

// Parse JSON input
var input = factory.Parse("""
    { "users": [
        { "name": "Alice", "age": 30, "role": "admin" },
        { "name": "Bob",   "age": 17, "role": "user" },
        { "name": "Carol", "age": 25, "role": "admin" }
    ]}
""");

// Evaluate an expression
var result = engine.Evaluate("$.users[*] | where u => u.age >= 18 | select u => u.name", input);

if (result.Success)
{
    // result.Value is an IElwoodValue (array: ["Alice", "Carol"])
    foreach (var item in result.Value!.EnumerateArray())
        Console.WriteLine(item.GetStringValue());
}
else
{
    foreach (var diag in result.Diagnostics)
        Console.WriteLine(diag.ToString());
}
```

---

## Expressions vs Scripts

Elwood has two evaluation modes:

### Expression (single pipeline)

A single Elwood expression. No `let` or `return`.

```csharp
var result = engine.Evaluate("$.users[*] | where u => u.active | select u => u.name", input);
```

### Script (let bindings + return)

Multiple `let` bindings followed by `return`. Use `Execute` instead of `Evaluate`.

```csharp
var script = """
    let adults = $.users[*] | where u => u.age >= 18
    let names = adults | select u => u.name.toLower()
    return { names: names, count: adults | count }
""";

var result = engine.Execute(script, input);
```

### Auto-detection

If you don't know whether the user will provide an expression or a script:

```csharp
bool isScript = source.TrimStart().StartsWith("let ") ||
                source.Contains("\nlet ") ||
                source.Contains("return ");

var result = isScript
    ? engine.Execute(source, input)
    : engine.Evaluate(source.Trim(), input);
```

---

## Core API Reference

### ElwoodEngine

Namespace: `Elwood.Core`

```csharp
public sealed class ElwoodEngine
{
    public ElwoodEngine(IElwoodValueFactory factory);

    // Evaluate a single expression
    public ElwoodResult Evaluate(
        string expression,
        IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings = null);

    // Execute a script (let bindings + return)
    public ElwoodResult Execute(
        string script,
        IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings = null);

    // Register a custom method callable from Elwood expressions
    public void RegisterMethod(string name, ElwoodMethodHandler handler);
}
```

### ElwoodResult

```csharp
public sealed class ElwoodResult
{
    public IElwoodValue? Value { get; }                        // null if evaluation failed
    public IReadOnlyList<ElwoodDiagnostic> Diagnostics { get; }
    public bool Success { get; }                               // true if no errors
}
```

### IElwoodValue

Namespace: `Elwood.Core.Abstractions`

The abstract representation of any JSON value. All input and output goes through this interface.

```csharp
public interface IElwoodValue
{
    ElwoodValueKind Kind { get; }   // Object, Array, String, Number, Boolean, Null

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

    // Deep clone
    IElwoodValue DeepClone();
}
```

### IElwoodValueFactory

Namespace: `Elwood.Core.Abstractions`

```csharp
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
```

The primary implementation is `JsonNodeValueFactory.Instance` from `Elwood.Json`.

### ElwoodDiagnostic

Namespace: `Elwood.Core.Diagnostics`

```csharp
public sealed class ElwoodDiagnostic
{
    public DiagnosticSeverity Severity { get; init; }  // Error, Warning, Info
    public string Message { get; init; }
    public SourceSpan Span { get; init; }              // line, column, start, end
    public string? Suggestion { get; init; }           // e.g. "Did you mean 'version'?"

    public override string ToString();  // "Error at line 1, col 23: Property 'vresion' not found..."
}
```

---

## Getting JSON Out of a Result

`result.Value` is an `IElwoodValue`. To get a `System.Text.Json.Nodes.JsonNode` or a JSON string:

```csharp
using System.Text.Json;
using Elwood.Json;

if (result.Success && result.Value is JsonNodeValue jnv)
{
    // Access the underlying JsonNode
    JsonNode? node = jnv.Node;

    // Serialize to JSON string
    string json = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
}
```

To read individual values:

```csharp
var val = result.Value!;

// String
string name = val.GetProperty("name")!.GetStringValue()!;

// Number
double age = val.GetProperty("age")!.GetNumberValue();

// Array iteration
foreach (var item in val.GetProperty("items")!.EnumerateArray())
    Console.WriteLine(item.GetStringValue());

// Check type
if (val.Kind == ElwoodValueKind.Array)
    Console.WriteLine($"Array with {val.GetArrayLength()} items");
```

---

## Passing Variables (Bindings)

You can inject additional named values accessible from Elwood expressions:

```csharp
var bindings = new Dictionary<string, IElwoodValue>
{
    ["$config"]  = factory.Parse("""{ "maxItems": 100 }"""),
    ["$secrets"] = factory.Parse("""{ "apiKey": "abc123" }"""),
};

var result = engine.Evaluate(
    "$.items[*] | take $config.maxItems | select i => { ...i, key: $secrets.apiKey }",
    input,
    bindings
);
```

Binding names should start with `$` to follow Elwood conventions.

---

## Registering Custom Methods

You can extend Elwood with custom methods callable using dot notation:

```csharp
using Elwood.Core.Extensions;

engine.RegisterMethod("mask", (target, args, factory) =>
{
    var text = target.GetStringValue() ?? "";
    int keep = args.Count > 0 ? (int)args[0].GetNumberValue() : 4;
    var masked = new string('*', Math.Max(0, text.Length - keep)) + text[^Math.Min(keep, text.Length)..];
    return factory.CreateString(masked);
});

// Now usable in expressions:
// $.creditCard.mask(4)  →  "************1234"
```

The handler signature:

```csharp
public delegate IElwoodValue ElwoodMethodHandler(
    IElwoodValue target,           // value the method is called on
    List<IElwoodValue> args,       // method arguments
    IElwoodValueFactory factory);  // to create return values
```

Custom methods cannot override built-in methods.

---

## DI Registration Pattern

For ASP.NET or similar DI-based applications:

```csharp
// In Program.cs or Startup
services.AddSingleton(Elwood.Json.JsonNodeValueFactory.Instance);
services.AddSingleton<ElwoodEngine>(sp =>
    new ElwoodEngine(sp.GetRequiredService<IElwoodValueFactory>()));
```

Then inject `ElwoodEngine` wherever you need it:

```csharp
public class MyService(ElwoodEngine engine, IElwoodValueFactory factory)
{
    public string Transform(string inputJson, string script)
    {
        var input = factory.Parse(inputJson);
        var result = engine.Execute(script, input);

        if (!result.Success)
            throw new InvalidOperationException(
                string.Join("; ", result.Diagnostics.Select(d => d.ToString())));

        return ((JsonNodeValue)result.Value!).Node?.ToJsonString() ?? "null";
    }
}
```

---

## Error Handling

Elwood never throws from `Evaluate`/`Execute`. Errors are returned in `result.Diagnostics`:

```csharp
var result = engine.Evaluate("$.users[*] | where u => u.nme == 'Alice'", input);

if (!result.Success)
{
    foreach (var diag in result.Diagnostics)
    {
        // "Error at line 1, col 33: Property 'nme' not found on Object.
        //  Did you mean 'name'? Available: name, age, role"
        Console.WriteLine(diag.ToString());
    }
}
```

---

## Elwood Syntax Quick Reference

Elwood combines JSONPath navigation, KQL-style pipes, and lambda expressions.

### Navigation

```
$                    Root document
$.field              Property access
$["special-key"]     Bracket access (for keys with special characters)
$[0]                 Array index
$[*]                 All array elements
$[2:5]               Slice (elements 2, 3, 4)
$..field             Recursive descent
```

### Pipes

```
| where predicate       Filter
| select projection     Transform (map)
| selectMany projection Flatten
| groupBy key           Group (produces .key and .items)
| orderBy key [asc|desc] Sort
| distinct              Deduplicate
| take n                First n items
| skip n                Skip n items
| count / sum / min / max  Aggregations
| first / last          First or last item
| concat separator      Join array into string
| reduce (acc, x) => expr [from init]  Fold
```

### Lambdas and Let Bindings

```
u => u.name.toLower()                    Single-parameter lambda
(x, y) => x.value + y.value             Multi-parameter lambda
let adults = $.users[*] | where u => u.age >= 18
return { count: adults | count }
```

### Literals and Object Construction

```
{ key: value, ...spread, [dynamic]: expr }   Object literal with spread and computed keys
[1, 2, 3]                                    Array literal
`Hello {$.name}, you are {$.age}`            String interpolation
if condition then expr else expr             Conditional
```

### Built-in Methods (partial list)

| Category | Examples |
|---|---|
| String | `.toLower()`, `.toUpper()`, `.trim()`, `.split(sep)`, `.replace(a, b)`, `.substring(start, len)`, `.contains(s)`, `.startsWith(s)`, `.regex(pattern)` |
| Numeric | `.round(n)`, `.floor()`, `.ceiling()`, `.abs()` |
| Collection | `.count()`, `.first()`, `.last()`, `.length()`, `.keep(p1, p2)`, `.remove(p1, p2)` |
| Type | `.toString()`, `.toNumber()`, `.parseJson()`, `.isNull()`, `.isEmpty()` |
| DateTime | `.dateFormat(fmt)`, `.add(timespan)`, `.toUnixTimeSeconds()`, `now()`, `utcNow()` |
| Format I/O | `.fromCsv()`, `.toCsv()`, `.fromXml()`, `.toXml()` |
| Hashing | `.hash()`, `.hash(length)` |
| Utility | `newGuid()`, `range(start, count)`, `boolean(val)` |

For the complete syntax reference, see [syntax-reference.md](syntax-reference.md).

---

## Complete Working Example

A service that takes a JSON array of products and groups them by category:

```csharp
using Elwood.Core;
using Elwood.Json;

var factory = JsonNodeValueFactory.Instance;
var engine = new ElwoodEngine(factory);

var productsJson = """
[
    { "name": "Widget A", "category": "tools", "price": 9.99 },
    { "name": "Widget B", "category": "tools", "price": 14.99 },
    { "name": "Gadget X", "category": "electronics", "price": 49.99 }
]
""";

var script = """
    let grouped = $[*] | groupBy p => p.category
    return grouped | select g => {
        category: g.key,
        count: g.items | count,
        totalPrice: g.items | select i => i.price | sum,
        products: g.items | select i => i.name
    }
""";

var input = factory.Parse(productsJson);
var result = engine.Execute(script, input);

if (result.Success && result.Value is JsonNodeValue jnv)
{
    Console.WriteLine(jnv.Node?.ToJsonString(
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
```

Output:

```json
[
  {
    "category": "tools",
    "count": 2,
    "totalPrice": 24.98,
    "products": ["Widget A", "Widget B"]
  },
  {
    "category": "electronics",
    "count": 1,
    "totalPrice": 49.99,
    "products": ["Gadget X"]
  }
]
```

---

## Loading Scripts from Files

Scripts are plain text files with the `.elwood` extension. A common pattern is loading them from embedded resources or disk:

```csharp
// From file
string script = File.ReadAllText("transforms/my-transform.elwood");
var result = engine.Execute(script, input);

// From embedded resource
using var stream = Assembly.GetExecutingAssembly()
    .GetManifestResourceStream("MyApp.Transforms.my_transform.elwood")!;
using var reader = new StreamReader(stream);
string script = reader.ReadToEnd();
var result = engine.Execute(script, input);
```

---

## Thread Safety

- `ElwoodEngine` is **thread-safe** for `Evaluate`/`Execute` calls after construction.
- `JsonNodeValueFactory.Instance` is a singleton and thread-safe.
- `IElwoodValue` instances are immutable; they can be shared across threads.
- Do not call `RegisterMethod` concurrently with `Evaluate`/`Execute`. Register all methods during setup.
