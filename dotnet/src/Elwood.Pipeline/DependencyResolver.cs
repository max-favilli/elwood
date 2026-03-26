using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline;

/// <summary>
/// Resolves source dependencies into ordered execution stages.
/// Sources in the same stage have all their dependencies satisfied and can run concurrently.
/// </summary>
public static class DependencyResolver
{
    /// <summary>
    /// Resolve sources into execution stages based on their depends declarations.
    /// Returns a list of stages, where each stage is a list of sources that can run concurrently.
    /// </summary>
    public static List<List<SourceConfig>> ResolveStages(List<SourceConfig> sources)
    {
        var nameMap = sources.ToDictionary(s => s.Name);
        var resolved = new HashSet<string>();
        var stages = new List<List<SourceConfig>>();
        var remaining = new HashSet<string>(sources.Select(s => s.Name));

        // Safety limit to detect circular dependencies
        var maxIterations = sources.Count + 1;
        var iteration = 0;

        while (remaining.Count > 0)
        {
            if (++iteration > maxIterations)
            {
                var circular = string.Join(", ", remaining);
                throw new InvalidOperationException(
                    $"Circular dependency detected among sources: {circular}");
            }

            // Find all sources whose dependencies are fully resolved
            var stage = remaining
                .Where(name =>
                {
                    var deps = nameMap[name].GetDependencies();
                    return deps.All(d => resolved.Contains(d));
                })
                .ToList();

            if (stage.Count == 0)
            {
                var unresolved = remaining.Select(name =>
                {
                    var deps = nameMap[name].GetDependencies();
                    var missing = deps.Where(d => !resolved.Contains(d));
                    return $"{name} → [{string.Join(", ", missing)}]";
                });
                throw new InvalidOperationException(
                    $"Unresolvable dependencies: {string.Join("; ", unresolved)}");
            }

            stages.Add(stage.Select(name => nameMap[name]).ToList());
            foreach (var name in stage)
            {
                resolved.Add(name);
                remaining.Remove(name);
            }
        }

        return stages;
    }

    /// <summary>
    /// Validate that all dependency references point to existing source names.
    /// </summary>
    public static List<string> ValidateDependencies(List<SourceConfig> sources)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(sources.Select(s => s.Name));

        foreach (var source in sources)
        {
            foreach (var dep in source.GetDependencies())
            {
                if (!names.Contains(dep))
                    errors.Add($"Source '{source.Name}' depends on '{dep}' which does not exist");
            }
        }

        // Check for duplicate names
        var duplicates = sources.GroupBy(s => s.Name).Where(g => g.Count() > 1);
        foreach (var dup in duplicates)
            errors.Add($"Duplicate source name: '{dup.Key}'");

        return errors;
    }
}
