using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Elwood.Pipeline.Registry;

/// <summary>
/// Git-backed pipeline store. Every save is a git commit, revisions come from
/// <c>git log</c>, restore checks out files at a previous revision and commits.
/// </summary>
/// <remarks>
/// Wraps <see cref="FileSystemPipelineStore"/> for file I/O and adds git
/// operations via the git CLI (no LibGit2Sharp — avoids native binary issues).
///
/// The repository is expected to already exist (cloned or init'd externally).
/// The store does NOT manage remotes, push, or pull — that's the API server's
/// responsibility (webhook → <c>git pull</c> → rebuild Redis cache).
///
/// Directory layout (same as FileSystemPipelineStore):
/// <code>
///   {repoDir}/{pipeline-id}/pipeline.elwood.yaml
///   {repoDir}/{pipeline-id}/*.elwood
/// </code>
/// </remarks>
public sealed class GitPipelineStore : IPipelineStore
{
    private readonly FileSystemPipelineStore _fs;
    private readonly GitHelper _git;
    private readonly string _repoDir;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Create a git-backed pipeline store.
    /// </summary>
    /// <param name="repoDir">
    /// Path to the git repository's working tree. Must be an existing directory
    /// (either a clone or a fresh <c>git init</c>). The store calls
    /// <c>git init</c> on first use if the .git directory is missing.
    /// </param>
    public GitPipelineStore(string repoDir)
    {
        _repoDir = repoDir;
        _fs = new FileSystemPipelineStore(repoDir);
        _git = new GitHelper(repoDir);
    }

    /// <summary>Ensure the backing directory is a git repo. Called once during app startup.</summary>
    public Task InitAsync() => _git.EnsureRepoAsync();

    // ── Read operations — delegated to FileSystemPipelineStore (reads working tree) ──

    public Task<List<PipelineSummary>> ListPipelinesAsync(string? nameFilter = null)
        => _fs.ListPipelinesAsync(nameFilter);

    public Task<PipelineDefinition?> GetPipelineAsync(string id)
        => _fs.GetPipelineAsync(id);

    // ── Write operations — file I/O + git commit ──

    public async Task SavePipelineAsync(string id, PipelineContent content,
        string? author = null, string? message = null)
    {
        await _git.EnsureRepoAsync();

        // Write files to disk (creates/updates pipeline directory)
        await _fs.SavePipelineAsync(id, content, author, message);

        // Stage all changes in the pipeline directory
        await _git.AddAllAsync(id);

        // Commit
        var commitMsg = message ?? $"Update pipeline '{id}'";
        var commitAuthor = FormatAuthor(author);
        await _git.CommitAsync(commitMsg, commitAuthor);
    }

    public async Task DeletePipelineAsync(string id)
    {
        await _git.EnsureRepoAsync();

        // Remove files from disk
        await _fs.DeletePipelineAsync(id);

        // Stage the deletion
        await _git.AddAllAsync(id);

        // Commit
        await _git.CommitAsync($"Delete pipeline '{id}'");
    }

    // ── Revision operations — git log + git show ──

    public async Task<List<PipelineRevision>> GetRevisionsAsync(string id, int limit = 20)
    {
        var entries = await _git.LogAsync(relativePath: id, limit: limit);
        return entries.Select(e => new PipelineRevision
        {
            RevisionId = e.Hash,
            Author = e.Author,
            Message = e.Message,
            Timestamp = e.Timestamp,
        }).ToList();
    }

    public async Task RestoreRevisionAsync(string id, string revisionId)
    {
        await _git.EnsureRepoAsync();

        var pipelineDir = Path.Combine(_repoDir, id);

        // 1. Read the pipeline's files at the target revision
        var files = await _git.ListFilesAtRevisionAsync(revisionId, id);
        if (files.Count == 0)
            throw new InvalidOperationException(
                $"Pipeline '{id}' not found at revision {revisionId[..Math.Min(8, revisionId.Length)]}.");

        // 2. Clear current pipeline directory (so removed scripts don't linger)
        if (Directory.Exists(pipelineDir))
        {
            foreach (var file in Directory.GetFiles(pipelineDir))
                File.Delete(file);
        }
        else
        {
            Directory.CreateDirectory(pipelineDir);
        }

        // 3. Write each file from the target revision
        foreach (var fileName in files)
        {
            var content = await _git.ShowFileAtRevisionAsync(revisionId, $"{id}/{fileName}");
            if (content is not null)
                File.WriteAllText(Path.Combine(pipelineDir, fileName), content);
        }

        // 4. Stage and commit the restore
        await _git.AddAllAsync(id);
        var shortRev = revisionId.Length > 8 ? revisionId[..8] : revisionId;
        await _git.CommitAsync($"Restore pipeline '{id}' to revision {shortRev}");
    }

    // ── helpers ──

    /// <summary>
    /// Format author for git commit. Accepts "Name", "name@email", or "Name &lt;email&gt;".
    /// Returns null if input is null/empty (git uses its default config).
    /// </summary>
    private static string? FormatAuthor(string? author)
    {
        if (string.IsNullOrWhiteSpace(author)) return null;
        // Already in "Name <email>" format
        if (author.Contains('<') && author.Contains('>')) return author;
        // Bare email
        if (author.Contains('@')) return $"{author.Split('@')[0]} <{author}>";
        // Just a name — use a noreply email
        return $"{author} <{author}@elwood.pipeline>";
    }
}
