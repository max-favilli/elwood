using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Json;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();

var engine = new ElwoodEngine(JsonNodeValueFactory.Instance);
var factory = JsonNodeValueFactory.Instance;

app.MapPost("/api/evaluate", async (HttpContext ctx) =>
{
    JsonNode? body;
    try
    {
        body = await JsonNode.ParseAsync(ctx.Request.Body);
    }
    catch
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid JSON body" });
        return;
    }

    var script = body?["script"]?.GetValue<string>();
    var input = body?["input"];

    if (string.IsNullOrWhiteSpace(script))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing 'script' field" });
        return;
    }

    var elwoodInput = input is not null ? new JsonNodeValue(input.DeepClone()) : factory.CreateNull();

    var isScript = script.TrimStart().StartsWith("let ") ||
                   script.Contains("\nlet ") ||
                   script.Contains("return ");

    var result = isScript
        ? engine.Execute(script, elwoodInput)
        : engine.Evaluate(script.Trim(), elwoodInput);

    if (result.Success)
    {
        var outputNode = result.Value is JsonNodeValue jnv ? jnv.Node : null;
        await ctx.Response.WriteAsJsonAsync(new
        {
            success = true,
            value = outputNode,
            diagnostics = Array.Empty<object>()
        });
    }
    else
    {
        ctx.Response.StatusCode = 200; // evaluation errors are not HTTP errors
        await ctx.Response.WriteAsJsonAsync(new
        {
            success = false,
            value = (object?)null,
            diagnostics = result.Diagnostics.Select(d => new
            {
                severity = d.Severity.ToString().ToLower(),
                message = d.Message,
                line = d.Span.Line,
                column = d.Span.Column,
                suggestion = d.Suggestion
            })
        });
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.1.0" }));

app.Run();
