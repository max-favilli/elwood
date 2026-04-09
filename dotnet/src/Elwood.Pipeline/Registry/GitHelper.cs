using System.Diagnostics;
using System.Text;

namespace Elwood.Pipeline.Registry;

/// <summary>
/// Thin wrapper around the git CLI. Shells out to the <c>git</c> command
/// rather than using LibGit2Sharp — avoids native binary compatibility
/// issues on newer .NET and is more portable (git CLI is always available
/// on servers, CI runners, and developer machines).
/// </summary>
public sealed class GitHelper
{
    private readonly string _repoDir;

    public GitHelper(string repoDir)
    {
        _repoDir = repoDir;
    }

    /// <summary>Ensure the directory is a git repository. Runs <c>git init</c> if not.</summary>
    public async Task EnsureRepoAsync()
    {
        if (Directory.Exists(Path.Combine(_repoDir, ".git"))) return;
        Directory.CreateDirectory(_repoDir);
        await RunAsync("init");
        // Set local identity so commits work on machines without global git config
        // (CI runners, fresh containers, etc.). This is repo-local — no global side effects.
        await RunAsync("config", "user.email", "elwood@pipeline.local");
        await RunAsync("config", "user.name", "Elwood Pipeline");
    }

    /// <summary>Stage specific files.</summary>
    public Task AddAsync(params string[] relativePaths)
        => RunAsync(["add", "--", ..relativePaths]);

    /// <summary>Stage all changes (additions, modifications, deletions) under a path.</summary>
    public Task AddAllAsync(string relativePath = ".")
        => RunAsync("add", "-A", relativePath);

    /// <summary>Create a commit with the given message and optional author.</summary>
    public async Task<string> CommitAsync(string message, string? author = null)
    {
        var args = new List<string> { "commit", "-m", message, "--allow-empty" };
        if (!string.IsNullOrEmpty(author))
            args.AddRange(["--author", author]);

        var (exitCode, stdout, stderr) = await RunAsync(args.ToArray());

        // "nothing to commit" is not an error — return empty hash
        if (exitCode != 0 && stderr.Contains("nothing to commit"))
            return string.Empty;

        if (exitCode != 0)
            throw new InvalidOperationException($"git commit failed: {stderr}");

        // Extract the commit hash from the output
        return await GetHeadHashAsync();
    }

    /// <summary>Get the HEAD commit hash.</summary>
    public async Task<string> GetHeadHashAsync()
    {
        var (_, stdout, _) = await RunAsync("rev-parse", "HEAD");
        return stdout.Trim();
    }

    /// <summary>
    /// Get the commit log for a path (file or directory).
    /// Returns list of (hash, author, message, date).
    /// </summary>
    public async Task<List<GitLogEntry>> LogAsync(string? relativePath = null, int limit = 20)
    {
        // Format: hash|author|date|message (one line per commit)
        var args = new List<string>
        {
            "log",
            $"--max-count={limit}",
            "--format=%H|%an|%aI|%s",
        };
        if (!string.IsNullOrEmpty(relativePath))
        {
            args.Add("--");
            args.Add(relativePath);
        }

        var (exitCode, stdout, _) = await RunAsync(args.ToArray());
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return [];

        var entries = new List<GitLogEntry>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 4);
            if (parts.Length < 4) continue;
            entries.Add(new GitLogEntry
            {
                Hash = parts[0],
                Author = parts[1],
                Timestamp = DateTime.TryParse(parts[2], out var dt) ? dt.ToUniversalTime() : DateTime.MinValue,
                Message = parts[3],
            });
        }
        return entries;
    }

    /// <summary>
    /// Get the content of a file at a specific revision.
    /// Returns null if the file didn't exist at that revision.
    /// </summary>
    public async Task<string?> ShowFileAtRevisionAsync(string revisionHash, string relativePath)
    {
        var (exitCode, stdout, _) = await RunAsync("show", $"{revisionHash}:{relativePath}");
        return exitCode == 0 ? stdout : null;
    }

    /// <summary>
    /// List files in a directory at a specific revision.
    /// Returns relative paths within the directory.
    /// </summary>
    public async Task<List<string>> ListFilesAtRevisionAsync(string revisionHash, string relativeDir)
    {
        // ls-tree lists the contents of a tree object
        var (exitCode, stdout, _) = await RunAsync(
            "ls-tree", "--name-only", $"{revisionHash}:{relativeDir}");

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return [];

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Run a git command and return (exitCode, stdout, stderr).</summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout, stderr);
    }
}

/// <summary>A single entry from <c>git log</c>.</summary>
public sealed class GitLogEntry
{
    public string Hash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
