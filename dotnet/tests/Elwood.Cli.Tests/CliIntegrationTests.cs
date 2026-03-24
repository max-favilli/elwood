using System.Diagnostics;

namespace Elwood.Cli.Tests;

/// <summary>
/// Integration tests that invoke the CLI as a subprocess and verify stdout/stderr/exit code.
/// The CLI is built once via a class fixture, then each test runs the built DLL directly.
/// </summary>
public class CliIntegrationTests : IClassFixture<CliFixture>
{
    private readonly CliFixture _fixture;

    public CliIntegrationTests(CliFixture fixture)
    {
        _fixture = fixture;
    }

    // ── eval ──

    [Fact]
    public async Task Eval_SimpleExpression_WithJsonInput()
    {
        // Pass JSON via stdin to avoid shell escaping issues with braces
        var result = await _fixture.Run(new[] { "eval", "$.users[0].name" },
            stdin: "{\"users\":[{\"name\":\"Alice\"}]}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alice", result.Stdout);
    }

    [Fact]
    public async Task Eval_InlineJson()
    {
        var result = await _fixture.Run(new[] { "eval", "$.x + 1" },
            stdin: "{\"x\":41}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("42", result.Stdout);
    }

    [Fact]
    public async Task Eval_StdinPipe()
    {
        var result = await _fixture.Run(new[] { "eval", "$.name" }, stdin: "{\"name\":\"Bob\"}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Bob", result.Stdout);
    }

    // ── input format auto-detection ──

    [Fact]
    public async Task Eval_CsvInput_AutoDetect()
    {
        var csvPath = _fixture.SpecPath("81-fromcsv-file", "input.csv");
        var result = await _fixture.Run("eval", "$.fromCsv() | select r => r.name", "--input", csvPath);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alice", result.Stdout);
        Assert.Contains("Bob", result.Stdout);
    }

    [Fact]
    public async Task Eval_XmlInput_AutoDetect()
    {
        var xmlPath = _fixture.SpecPath("85-fromxml-file", "input.xml");
        var result = await _fixture.Run("eval", "$.fromXml().orders.order | select o => o.customer", "--input", xmlPath);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alice", result.Stdout);
    }

    [Fact]
    public async Task Eval_TxtInput_AutoDetect()
    {
        var txtPath = _fixture.SpecPath("82-fromtext-file", "input.txt");
        var result = await _fixture.Run("eval", "$.fromText() | count", "--input", txtPath);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("5", result.Stdout);
    }

    [Fact]
    public async Task Eval_CsvStdin_WithInputFormat()
    {
        var result = await _fixture.Run(
            new[] { "eval", "$.fromCsv() | select r => r.name", "--input-format", "csv" },
            stdin: "name,age\nAlice,30\nBob,25");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alice", result.Stdout);
    }

    // ── output format ──

    [Fact]
    public async Task Eval_OutputFormat_Csv()
    {
        var csvPath = _fixture.SpecPath("81-fromcsv-file", "input.csv");
        var result = await _fixture.Run("eval",
            "$.fromCsv() | select r => { name: r.name, age: r.age }",
            "--input", csvPath, "--output-format", "csv");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("name,age", result.Stdout);
        Assert.Contains("Alice,30", result.Stdout);
    }

    [Fact]
    public async Task Eval_OutputFormat_Xml()
    {
        var result = await _fixture.Run("eval",
            "{ items: { item: $.fromCsv() | select r => { name: r.name } } }",
            "--input", _fixture.SpecPath("81-fromcsv-file", "input.csv"),
            "--output-format", "xml");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("<name>Alice</name>", result.Stdout);
    }

    // ── run ──

    [Fact]
    public async Task Run_ScriptFile()
    {
        var scriptPath = _fixture.SpecPath("12-script-let-return", "script.elwood");
        var inputPath = _fixture.SpecPath("12-script-let-return", "input.json");
        var result = await _fixture.Run("run", scriptPath, "--input", inputPath);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
    }

    // ── error handling ──

    [Fact]
    public async Task Eval_InvalidExpression_ReturnsError()
    {
        var result = await _fixture.Run("eval", "$.???", "--json", "{}");
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
    }

    [Fact]
    public async Task Run_MissingFile_ReturnsError()
    {
        var result = await _fixture.Run("run", "nonexistent.elwood");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Stderr);
    }

    [Fact]
    public async Task UnknownCommand_PrintsUsage()
    {
        var result = await _fixture.Run("unknown-command");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Usage", result.Stdout);
    }
}

/// <summary>
/// Builds the CLI project once and provides a helper to run it.
/// </summary>
public class CliFixture : IDisposable
{
    private readonly string _cliDll;
    private readonly string _specDir;

    public CliFixture()
    {
        var repoRoot = FindRepoRoot();
        _specDir = System.IO.Path.Combine(repoRoot, "spec", "test-cases");
        var cliProject = System.IO.Path.Combine(repoRoot, "dotnet", "src", "Elwood.Cli", "Elwood.Cli.csproj");

        // Build once
        var buildResult = RunProcess("dotnet", $"build \"{cliProject}\" -c Release --nologo -v q");
        if (buildResult.ExitCode != 0)
            throw new Exception($"CLI build failed: {buildResult.Stderr}");

        // Find the built DLL
        var cliDir = System.IO.Path.Combine(repoRoot, "dotnet", "src", "Elwood.Cli", "bin", "Release");
        _cliDll = Directory.GetFiles(cliDir, "Elwood.Cli.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTime)
            .First();
    }

    public string SpecPath(string testCase, string file)
        => System.IO.Path.Combine(_specDir, testCase, file);

    public Task<CliResult> Run(params string[] args)
        => Run(args, stdin: null);

    public async Task<CliResult> Run(string[] args, string? stdin)
    {
        var quotedArgs = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{_cliDll}\" {quotedArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // Always redirect so we can close it
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;

        // Write stdin if provided, then always close to prevent CLI from waiting
        if (stdin is not null)
            await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            return new CliResult(-1, "", "Test timed out after 15s");
        }

        return new CliResult(process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    private static CliResult RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir, "spec", "test-cases")))
                return dir;
            dir = System.IO.Path.GetDirectoryName(dir)!;
        }
        throw new Exception("Could not find repo root (looking for spec/test-cases/)");
    }

    public void Dispose() { }
}

public record CliResult(int ExitCode, string Stdout, string Stderr);
