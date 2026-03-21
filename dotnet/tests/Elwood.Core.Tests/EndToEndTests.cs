using Elwood.Core;
using Elwood.Json;

namespace Elwood.Core.Tests;

public class EndToEndTests
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;

    private const string SampleJson = """
        {
            "users": [
                { "name": "Alice", "age": 30, "role": "admin", "active": true },
                { "name": "Bob", "age": 17, "role": "user", "active": true },
                { "name": "Charlie", "age": 25, "role": "admin", "active": false },
                { "name": "Diana", "age": 42, "role": "user", "active": true }
            ],
            "metadata": {
                "version": "1.0",
                "count": 4
            }
        }
        """;

    [Fact]
    public void SimplePropertyAccess()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.metadata.version", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal("1.0", result.Value!.GetStringValue());
    }

    [Fact]
    public void ArrayWildcard()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.users[*]", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(4, result.Value!.GetArrayLength());
    }

    [Fact]
    public void PipeWhere()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.users[*] | where u => u.age > 18", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(3, result.Value!.GetArrayLength()); // Alice(30), Charlie(25), Diana(42)
    }

    [Fact]
    public void PipeWhereAndSelect()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | where u => u.age > 18 | select u => u.name", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var items = result.Value!.EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("Alice", items[0].GetStringValue());
        Assert.Equal("Charlie", items[1].GetStringValue());
        Assert.Equal("Diana", items[2].GetStringValue());
    }

    [Fact]
    public void PipeSelectToObject()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | select u => { name: u.name, isAdult: u.age >= 18 }", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var items = result.Value!.EnumerateArray().ToList();
        Assert.Equal(4, items.Count);
        Assert.Equal("Alice", items[0].GetProperty("name")!.GetStringValue());
        Assert.True(items[0].GetProperty("isAdult")!.GetBooleanValue());
        Assert.False(items[1].GetProperty("isAdult")!.GetBooleanValue()); // Bob is 17
    }

    [Fact]
    public void PipeWhereWithBooleanLogic()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | where u => u.age > 18 && u.active == true", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(2, result.Value!.GetArrayLength()); // Alice(30,active), Diana(42,active)
    }

    [Fact]
    public void PipeDistinct()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | select u => u.role | distinct", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(2, result.Value!.GetArrayLength()); // "admin", "user"
    }

    [Fact]
    public void PipeCount()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.users[*] | count", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(4.0, result.Value!.GetNumberValue());
    }

    [Fact]
    public void PipeOrderBy()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | orderBy u => u.age asc | select u => u.name", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var items = result.Value!.EnumerateArray().ToList();
        Assert.Equal("Bob", items[0].GetStringValue());     // 17
        Assert.Equal("Charlie", items[1].GetStringValue());  // 25
        Assert.Equal("Alice", items[2].GetStringValue());    // 30
        Assert.Equal("Diana", items[3].GetStringValue());    // 42
    }

    [Fact]
    public void PipeGroupBy()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | groupBy u => u.role", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var groups = result.Value!.EnumerateArray().ToList();
        Assert.Equal(2, groups.Count); // admin, user groups

        var adminGroup = groups.First(g => g.GetProperty("key")!.GetStringValue() == "admin");
        Assert.Equal(2, adminGroup.GetProperty("items")!.GetArrayLength()); // Alice, Charlie
    }

    [Fact]
    public void PipeTakeAndSkip()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.users[*] | take 2 | select u => u.name", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(2, result.Value!.GetArrayLength());

        var result2 = _engine.Evaluate("$.users[*] | skip 2 | count", input);
        Assert.True(result2.Success, string.Join("; ", result2.Diagnostics));
        Assert.Equal(2.0, result2.Value!.GetNumberValue());
    }

    [Fact]
    public void MethodChaining_ToLower()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | select u => u.name.toLower()", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var items = result.Value!.EnumerateArray().ToList();
        Assert.Equal("alice", items[0].GetStringValue());
        Assert.Equal("bob", items[1].GetStringValue());
    }

    [Fact]
    public void IfThenElse()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            "$.users[*] | select u => if u.age >= 18 then \"adult\" else \"minor\"",
            input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var items = result.Value!.EnumerateArray().ToList();
        Assert.Equal("adult", items[0].GetStringValue());  // Alice 30
        Assert.Equal("minor", items[1].GetStringValue());   // Bob 17
        Assert.Equal("adult", items[2].GetStringValue());  // Charlie 25
    }

    [Fact]
    public void Arithmetic()
    {
        var input = _factory.Parse("""{ "price": 100, "tax": 0.21 }""");
        var result = _engine.Evaluate("$.price * (1 + $.tax)", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(121.0, result.Value!.GetNumberValue(), 0.001);
    }

    [Fact]
    public void ErrorReporting_UnknownProperty()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.metadata.vresion", input);

        Assert.False(result.Success);
        var diag = result.Diagnostics[0];
        Assert.Contains("vresion", diag.Message);
        Assert.NotNull(diag.Suggestion);
        Assert.Contains("version", diag.Suggestion); // Did you mean suggestion
    }

    [Fact]
    public void StringInterpolation()
    {
        var input = _factory.Parse("""{ "first": "Max", "last": "Favilli" }""");
        var result = _engine.Evaluate("`{$.first} {$.last}`", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal("Max Favilli", result.Value!.GetStringValue());
    }

    [Fact]
    public void ImplicitDollarContext()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.users[*] | where $.active | select $.name", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(3, result.Value!.GetArrayLength()); // Alice, Bob, Diana (active ones)
    }

    [Fact]
    public void PipeBatch()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate("$.users[*] | batch 2", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(2, result.Value!.GetArrayLength()); // 2 batches of 2
        Assert.Equal(2, result.Value!.EnumerateArray().First().GetArrayLength());
    }

    [Fact]
    public void Script_LetBindingsAndReturn()
    {
        var input = _factory.Parse(SampleJson);
        var script = """
            let adults = $.users[*] | where u => u.age >= 18
            let admins = adults | where u => u.role == "admin"
            return { adultCount: adults | count, adminNames: admins | select u => u.name }
            """;
        var result = _engine.Execute(script, input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(3.0, result.Value!.GetProperty("adultCount")!.GetNumberValue());
        var adminNames = result.Value!.GetProperty("adminNames")!.EnumerateArray().ToList();
        Assert.Equal(2, adminNames.Count);
        Assert.Equal("Alice", adminNames[0].GetStringValue());
        Assert.Equal("Charlie", adminNames[1].GetStringValue());
    }

    [Fact]
    public void PipeMatch()
    {
        var input = _factory.Parse("""
            { "items": [
                { "status": "active", "name": "A" },
                { "status": "retired", "name": "B" },
                { "status": "pending", "name": "C" }
            ]}
            """);
        var result = _engine.Evaluate(
            """$.items[*] | select i => { name: i.name, color: i.status | match "active" => "#00FF00", "retired" => "#FF0000", _ => "#999999" }""",
            input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var items = result.Value!.EnumerateArray().ToList();
        Assert.Equal("#00FF00", items[0].GetProperty("color")!.GetStringValue());
        Assert.Equal("#FF0000", items[1].GetProperty("color")!.GetStringValue());
        Assert.Equal("#999999", items[2].GetProperty("color")!.GetStringValue());
    }

    [Fact]
    public void ComplexPipeline_GroupBySelectCount()
    {
        var input = _factory.Parse(SampleJson);
        var result = _engine.Evaluate(
            """$.users[*] | groupBy u => u.role | select g => { role: g.key, count: g.items | count }""",
            input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var groups = result.Value!.EnumerateArray().ToList();
        Assert.Equal(2, groups.Count);

        var admin = groups.First(g => g.GetProperty("role")!.GetStringValue() == "admin");
        Assert.Equal(2.0, admin.GetProperty("count")!.GetNumberValue());

        var user = groups.First(g => g.GetProperty("role")!.GetStringValue() == "user");
        Assert.Equal(2.0, user.GetProperty("count")!.GetNumberValue());
    }

    [Fact]
    public void NewGuid_ReturnsValidGuid()
    {
        var input = _factory.Parse("{}");
        var result = _engine.Evaluate("newGuid()", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var guid = result.Value!.GetStringValue();
        Assert.NotNull(guid);
        Assert.Equal(36, guid!.Length); // "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
        Assert.True(Guid.TryParse(guid, out _), $"'{guid}' is not a valid GUID");
    }

    [Fact]
    public void NewGuid_UniquePerCall()
    {
        var input = _factory.Parse("{}");
        var result = _engine.Evaluate(
            "{ a: newGuid(), b: newGuid() }", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var a = result.Value!.GetProperty("a")!.GetStringValue();
        var b = result.Value!.GetProperty("b")!.GetStringValue();
        Assert.NotEqual(a, b); // each call produces a unique GUID
    }

    [Fact]
    public void Now_ReturnsFormattedDate()
    {
        var input = _factory.Parse("{}");
        var result = _engine.Evaluate("now(\"yyyy-MM-dd\")", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var dateStr = result.Value!.GetStringValue();
        Assert.NotNull(dateStr);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", dateStr!);
        Assert.True(DateTime.TryParse(dateStr, out _));
    }

    [Fact]
    public void UtcNow_ReturnsFormattedDate()
    {
        var input = _factory.Parse("{}");
        var result = _engine.Evaluate("utcNow(\"yyyy-MM-ddTHH:mm\")", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var dateStr = result.Value!.GetStringValue();
        Assert.NotNull(dateStr);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$", dateStr!);
    }
}
