using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Json;

var factory = JsonNodeValueFactory.Instance;
var engine = new ElwoodEngine(factory);

if (args.Length == 0)
{
    RunRepl(engine, factory);
    return 0;
}

var command = args[0].ToLower();

switch (command)
{
    case "eval":
        return RunEval(args, engine, factory);
    case "run":
        return RunScript(args, engine, factory);
    case "repl":
        RunRepl(engine, factory);
        return 0;
    default:
        PrintUsage();
        return 1;
}

static int RunEval(string[] args, ElwoodEngine engine, JsonNodeValueFactory factory)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: elwood eval <expression> [--input <file>] [--json <inline-json>]");
        return 1;
    }

    var expression = args[1];
    var input = GetInputJson(args, factory);
    var result = engine.Evaluate(expression, input);

    return PrintResult(result);
}

static int RunScript(string[] args, ElwoodEngine engine, JsonNodeValueFactory factory)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: elwood run <script-file> [--input <file>] [--json <inline-json>]");
        return 1;
    }

    var scriptPath = args[1];
    if (!File.Exists(scriptPath))
    {
        Console.Error.WriteLine($"Script file not found: {scriptPath}");
        return 1;
    }

    var script = File.ReadAllText(scriptPath);
    var input = GetInputJson(args, factory);
    var result = engine.Execute(script, input);

    return PrintResult(result);
}

static void RunRepl(ElwoodEngine engine, JsonNodeValueFactory factory)
{
    Console.WriteLine("Elwood REPL — interactive mode");
    Console.WriteLine("Commands:");
    Console.WriteLine("  :load <file>    Load JSON input from file");
    Console.WriteLine("  :json <json>    Set inline JSON as input");
    Console.WriteLine("  :input          Show current input");
    Console.WriteLine("  :script         Toggle multi-line script mode (let bindings + return)");
    Console.WriteLine("  :quit           Exit");
    Console.WriteLine();

    var input = factory.Parse("{}");
    var scriptMode = false;

    Console.WriteLine("Input: {} (use :load or :json to set input data)");
    Console.WriteLine();

    while (true)
    {
        Console.Write(scriptMode ? "script> " : "elwood> ");
        var line = Console.ReadLine();
        if (line is null) break;
        line = line.Trim();
        if (line.Length == 0) continue;

        // Commands
        if (line.StartsWith(':'))
        {
            var parts = line.Split(' ', 2);
            var cmd = parts[0].ToLower();

            switch (cmd)
            {
                case ":quit" or ":q" or ":exit":
                    return;

                case ":load":
                    if (parts.Length < 2) { Console.WriteLine("Usage: :load <file>"); break; }
                    try
                    {
                        var json = File.ReadAllText(parts[1].Trim());
                        input = factory.Parse(json);
                        Console.WriteLine($"Loaded {parts[1].Trim()} ({json.Length} chars)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                    }
                    break;

                case ":json":
                    if (parts.Length < 2) { Console.WriteLine("Usage: :json <inline-json>"); break; }
                    try
                    {
                        input = factory.Parse(parts[1].Trim());
                        Console.WriteLine("Input set.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                    }
                    break;

                case ":input":
                    var node = ((JsonNodeValue)input).Node;
                    var pretty = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
                    Console.WriteLine(pretty);
                    break;

                case ":script":
                    scriptMode = !scriptMode;
                    Console.WriteLine(scriptMode
                        ? "Script mode ON — enter multiple lines, end with an empty line to execute."
                        : "Script mode OFF — single expression per line.");
                    break;

                default:
                    Console.WriteLine($"Unknown command: {cmd}");
                    break;
            }
            continue;
        }

        // Script mode: collect lines until empty line
        if (scriptMode)
        {
            var lines = new List<string> { line };
            while (true)
            {
                Console.Write("  ...> ");
                var next = Console.ReadLine();
                if (next is null || next.Trim().Length == 0) break;
                lines.Add(next);
            }
            var script = string.Join("\n", lines);
            var result = engine.Execute(script, input);
            PrintResultRepl(result);
            continue;
        }

        // Single expression
        {
            var result = engine.Evaluate(line, input);
            PrintResultRepl(result);
        }
    }
}

static void PrintResultRepl(ElwoodResult result)
{
    if (!result.Success)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        foreach (var diag in result.Diagnostics)
            Console.WriteLine($"  {diag}");
        Console.ResetColor();
        return;
    }

    if (result.Value is null)
    {
        Console.WriteLine("null");
        return;
    }

    if (result.Value is JsonNodeValue jnv)
    {
        var node = jnv.Node;
        var json = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(json);
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine(result.Value.GetStringValue() ?? "null");
    }
}

static int PrintResult(ElwoodResult result)
{
    if (!result.Success)
    {
        foreach (var diag in result.Diagnostics)
            Console.Error.WriteLine(diag);
        return 1;
    }

    if (result.Value is JsonNodeValue jnv)
    {
        var node = jnv.Node;
        Console.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
    }
    else
    {
        Console.WriteLine(result.Value?.GetStringValue() ?? "null");
    }
    return 0;
}

static Elwood.Core.Abstractions.IElwoodValue GetInputJson(string[] args, JsonNodeValueFactory factory)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--input" || args[i] == "-i")
        {
            var path = args[i + 1];
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Input file not found: {path}");
                Environment.Exit(1);
            }
            return factory.Parse(File.ReadAllText(path));
        }
        if (args[i] == "--json" || args[i] == "-j")
        {
            return factory.Parse(args[i + 1]);
        }
    }

    // Try stdin if piped
    if (Console.IsInputRedirected)
    {
        var stdin = Console.In.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(stdin))
            return factory.Parse(stdin);
    }

    return factory.Parse("{}");
}

static void PrintUsage()
{
    Console.WriteLine("Elwood — JSON transformation DSL");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  elwood                                    Start REPL");
    Console.WriteLine("  elwood eval <expr> [--input file.json]    Evaluate expression");
    Console.WriteLine("  elwood run <script.elwood> [--input file] Execute script");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  elwood eval \"$.users[*] | where u => u.active\" --input data.json");
    Console.WriteLine("  echo '{\"x\":1}' | elwood eval \"$.x + 1\"");
    Console.WriteLine("  elwood run transform.elwood --input payload.json");
}
