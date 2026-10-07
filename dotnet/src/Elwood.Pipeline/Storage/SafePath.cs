using System.Text.RegularExpressions;

namespace Elwood.Pipeline.Storage;

/// <summary>Portable names and containment checks for server-owned files.</summary>
public static class SafePath
{
    public static string Id(string id)
    {
        if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}\z") ||
            Regex.IsMatch(id, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase))
            throw new ArgumentException("Invalid identifier.");
        return id;
    }

    public static string Script(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Script names must end in .elwood.");
        Relative(name);
        return name;
    }

    public static void Relative(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Split('/').Any(part =>
            part is "" or "." or ".." || part.EndsWith('.') ||
            !Regex.IsMatch(part, @"\A[a-zA-Z0-9_.-]+\z") ||
            Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase)))
            throw new ArgumentException("Only contained relative paths with portable names are allowed.");
    }

    public static string Within(string root, string relative)
    {
        Relative(relative);
        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Path escapes its configured root.");
        // Reject links/junctions, including ancestors of the configured root.
        for (var current = result; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links and junctions are not allowed in storage paths.");
        return result;
    }

    public static void Content(Registry.PipelineContent content)
    {
        if (content.Yaml is null || content.Scripts is null) throw new ArgumentException("YAML and scripts are required.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, script) in content.Scripts)
        {
            Script(name);
            if (!names.Add(name) || script is null) throw new ArgumentException("Duplicate or null script.");
        }
    }
}
