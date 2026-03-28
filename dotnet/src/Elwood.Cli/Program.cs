using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
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
    case "pipeline":
        return RunPipeline(args, factory);
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
        Console.Error.WriteLine("Usage: elwood eval <expression> [--input <file>] [--input-format csv|txt|xml] [--output-format csv|txt|xml]");
        return 1;
    }

    var expression = args[1];
    var inputFormat = GetFlag(args, "--input-format") ?? GetFlag(args, "-if") ?? DetectInputFormat(args);
    var outputFormat = GetFlag(args, "--output-format") ?? GetFlag(args, "-of");
    var input = GetInput(args, factory, inputFormat);
    var result = engine.Evaluate(expression, input);

    return PrintResult(result, factory, outputFormat);
}

static int RunScript(string[] args, ElwoodEngine engine, JsonNodeValueFactory factory)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: elwood run <script-file> [--input <file>] [--input-format csv|txt|xml] [--output-format csv|txt|xml]");
        return 1;
    }

    var scriptPath = args[1];
    if (!File.Exists(scriptPath))
    {
        Console.Error.WriteLine($"Script file not found: {scriptPath}");
        return 1;
    }

    var script = File.ReadAllText(scriptPath);
    var inputFormat = GetFlag(args, "--input-format") ?? GetFlag(args, "-if") ?? DetectInputFormat(args);
    var outputFormat = GetFlag(args, "--output-format") ?? GetFlag(args, "-of");
    var input = GetInput(args, factory, inputFormat);
    var result = engine.Execute(script, input);

    return PrintResult(result, factory, outputFormat);
}

static int RunPipeline(string[] args, JsonNodeValueFactory factory)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  elwood pipeline run <yaml-file> [options]");
        Console.Error.WriteLine("  elwood pipeline validate <yaml-file>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --source name=file           Provide payload data for a named source");
        Console.Error.WriteLine("  --source-envelope name=file  Provide envelope (source metadata + payload)");
        Console.Error.WriteLine("  --output-dir <dir>           Write outputs to directory (default: stdout)");
        return 1;
    }

    var subCommand = args[1].ToLower();
    var yamlPath = args[2];

    if (!File.Exists(yamlPath))
    {
        Console.Error.WriteLine($"Pipeline YAML not found: {yamlPath}");
        return 1;
    }

    var parser = new Elwood.Pipeline.PipelineParser();
    var pipeline = parser.Parse(yamlPath);

    if (subCommand == "validate")
    {
        if (!pipeline.IsValid)
        {
            foreach (var err in pipeline.Errors)
                Console.Error.WriteLine($"  ERROR: {err}");
            return 1;
        }
        Console.WriteLine($"Pipeline '{pipeline.Config.Name}' is valid.");
        Console.WriteLine($"  Sources: {pipeline.Config.Sources.Count}");
        Console.WriteLine($"  Outputs: {pipeline.Config.Outputs.Count}");
        Console.WriteLine($"  Scripts: {pipeline.Scripts.Count}");
        return 0;
    }

    if (subCommand != "run")
    {
        Console.Error.WriteLine($"Unknown pipeline subcommand: {subCommand}");
        Console.Error.WriteLine("Expected: run or validate");
        return 1;
    }

    if (!pipeline.IsValid)
    {
        Console.Error.WriteLine("Pipeline has errors:");
        foreach (var err in pipeline.Errors)
            Console.Error.WriteLine($"  {err}");
        return 1;
    }

    // Parse --source and --source-envelope arguments
    var sourceInputs = new Dictionary<string, Elwood.Pipeline.SourceInput>();
    for (var i = 3; i < args.Length; i++)
    {
        if ((args[i] == "--source" || args[i] == "--source-envelope") && i + 1 < args.Length)
        {
            var isEnvelope = args[i] == "--source-envelope";
            var parts = args[i + 1].Split('=', 2);
            if (parts.Length != 2)
            {
                Console.Error.WriteLine($"Invalid {args[i]} format: {args[i + 1]} (expected name=file)");
                return 1;
            }
            var sourceName = parts[0];
            var filePath = parts[1];
            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"Source file not found: {filePath}");
                return 1;
            }
            sourceInputs[sourceName] = isEnvelope
                ? Elwood.Pipeline.SourceInput.FromEnvelopeFile(filePath, factory)
                : Elwood.Pipeline.SourceInput.FromDataFile(filePath, factory);
            i++;
        }
        else if (args[i] == "--output-dir" && i + 1 < args.Length)
        {
            i++; // TODO: implement output directory
        }
    }

    // Execute the pipeline
    var executor = new Elwood.Pipeline.PipelineExecutor(
        new Elwood.Pipeline.Secrets.EnvironmentSecretProvider());
    var result = executor.Execute(pipeline, sourceInputs);

    if (!result.IsSuccess)
    {
        Console.Error.WriteLine("Pipeline execution failed:");
        foreach (var err in result.Errors)
            Console.Error.WriteLine($"  {err}");
        return 1;
    }

    // Output results
    foreach (var (name, value) in result.Outputs)
    {
        if (result.Outputs.Count > 1)
            Console.WriteLine($"--- {name} ---");

        if (value is JsonNodeValue jnv)
        {
            Console.WriteLine(jnv.Node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null");
        }
        else if (value.Kind == Elwood.Core.Abstractions.ElwoodValueKind.Array)
        {
            var materialized = factory.CreateArray(value.EnumerateArray());
            Console.WriteLine(((JsonNodeValue)materialized).Node?.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "[]");
        }
        else
        {
            Console.WriteLine(value.GetStringValue() ?? "null");
        }
    }

    return 0;
}

static void RunRepl(ElwoodEngine engine, JsonNodeValueFactory factory)
{
    Console.WriteLine("Elwood REPL — interactive mode");
    Console.WriteLine("Commands:");
    Console.WriteLine("  :load <file>    Load input from file (auto-detects format from extension)");
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
                        var path = parts[1].Trim();
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        var content = File.ReadAllText(path);
                        input = ext is ".csv" or ".txt" or ".xml"
                            ? factory.CreateString(content)
                            : factory.Parse(content);
                        var formatLabel = ext switch { ".csv" => "CSV", ".txt" => "Text", ".xml" => "XML", _ => "JSON" };
                        Console.WriteLine($"Loaded {path} as {formatLabel} ({content.Length} chars)");
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

static int PrintResult(ElwoodResult result, JsonNodeValueFactory factory, string? outputFormat)
{
    if (!result.Success)
    {
        foreach (var diag in result.Diagnostics)
            Console.Error.WriteLine(diag);
        return 1;
    }

    var value = result.Value;

    // If output format is specified, convert the result
    if (outputFormat is not null && value is not null)
    {
        var engine = new ElwoodEngine(factory);
        var conversionExpr = outputFormat.ToLower() switch
        {
            "csv" => "$.toCsv()",
            "xml" => "$.toXml()",
            "text" or "txt" => "$.toText()",
            _ => null
        };

        if (conversionExpr is not null)
        {
            var converted = engine.Evaluate(conversionExpr, value);
            if (converted.Success && converted.Value is not null)
            {
                Console.WriteLine(converted.Value.GetStringValue() ?? "");
                return 0;
            }
        }
    }

    // Default: output as JSON
    if (value is JsonNodeValue jnv)
    {
        var node = jnv.Node;
        Console.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
    }
    else
    {
        Console.WriteLine(value?.GetStringValue() ?? "null");
    }
    return 0;
}

static string? GetFlag(string[] args, string flagName)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flagName, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

/// <summary>
/// Auto-detect input format from --input file extension.
/// </summary>
static string DetectInputFormat(string[] args)
{
    var inputPath = GetFlag(args, "--input") ?? GetFlag(args, "-i");
    if (inputPath is null) return "json";
    return Path.GetExtension(inputPath).ToLowerInvariant() switch
    {
        ".csv" => "csv",
        ".txt" => "txt",
        ".xml" => "xml",
        ".bin" or ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".zip" or ".gz" or ".parquet" => "binary",
        _ => "json"
    };
}

static IElwoodValue GetInput(string[] args, JsonNodeValueFactory factory, string format)
{
    // Check for --input / -i flag
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
            if (format == "binary")
            {
                // Binary files: read as bytes, pass as base64 string
                var bytes = File.ReadAllBytes(path);
                return factory.CreateString(Convert.ToBase64String(bytes));
            }
            var content = File.ReadAllText(path);
            return format is "csv" or "txt" or "xml"
                ? factory.CreateString(content)
                : factory.Parse(content);
        }
        if (args[i] == "--json" || args[i] == "-j")
        {
            return factory.Parse(args[i + 1]);
        }
    }

    // Try stdin if piped
    if (Console.IsInputRedirected)
    {
        if (format == "binary")
        {
            using var ms = new MemoryStream();
            Console.OpenStandardInput().CopyTo(ms);
            return factory.CreateString(Convert.ToBase64String(ms.ToArray()));
        }
        var stdin = Console.In.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(stdin))
        {
            return format is "csv" or "txt" or "xml"
                ? factory.CreateString(stdin)
                : factory.Parse(stdin);
        }
    }

    return factory.Parse("{}");
}

static void PrintUsage()
{
    Console.WriteLine("Elwood — JSON transformation DSL");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  elwood                                    Start REPL");
    Console.WriteLine("  elwood eval <expr> [options]              Evaluate expression");
    Console.WriteLine("  elwood run <script.elwood> [options]      Execute script");
    Console.WriteLine("  elwood pipeline run <yaml> [--source name=file ...]  Execute pipeline");
    Console.WriteLine("  elwood pipeline validate <yaml>           Validate pipeline YAML");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --input <file>, -i        Input file (format auto-detected from extension)");
    Console.WriteLine("  --json <json>, -j         Inline JSON input");
    Console.WriteLine("  --input-format <fmt>, -if Override input format: json, csv, txt, xml, binary");
    Console.WriteLine("  --output-format <fmt>, -of Convert output: csv, txt, xml");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  elwood eval \"$.users[*] | where u => u.active\" --input data.json");
    Console.WriteLine("  elwood run transform.elwood --input data.csv");
    Console.WriteLine("  elwood run transform.elwood --input data.json --output-format csv");
    Console.WriteLine("  cat data.csv | elwood eval \"$.fromCsv()\" --input-format csv");
    Console.WriteLine("  echo '{\"x\":1}' | elwood eval \"$.x + 1\"");
}
