using System.Text.RegularExpressions;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Secrets;

namespace Elwood.Pipeline;

/// <summary>
/// Resolves dynamic string values in pipeline YAML:
/// - {$.field} or {$source.name} — inline Elwood expressions
/// - $secrets.path.to.value — secret references
/// - ${ENV_VAR} — environment variable substitution
/// </summary>
public sealed class StringResolver
{
    private static readonly Regex InlineExprPattern = new(@"\{(\$[^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex SecretPattern = new(@"\$secrets\.([a-zA-Z0-9_.]+)", RegexOptions.Compiled);
    private static readonly Regex EnvVarPattern = new(@"\$\{([A-Z_][A-Z0-9_]*)\}", RegexOptions.Compiled);

    private readonly ElwoodEngine _engine;
    private readonly JsonNodeValueFactory _factory;
    private readonly ISecretProvider? _secretProvider;

    public StringResolver(ISecretProvider? secretProvider = null)
    {
        _factory = JsonNodeValueFactory.Instance;
        _engine = new ElwoodEngine(_factory);
        _secretProvider = secretProvider;
    }

    /// <summary>
    /// Resolve a YAML string value. Returns the resolved string, or the original if no patterns match.
    /// </summary>
    public string Resolve(string value, IElwoodValue? current = null,
        Dictionary<string, IElwoodValue>? bindings = null)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var result = value;

        // 1. Resolve $secrets.path references
        result = SecretPattern.Replace(result, match =>
        {
            var path = match.Groups[1].Value;
            return _secretProvider?.GetSecret(path) ?? match.Value;
        });

        // 2. Resolve ${ENV_VAR} references
        result = EnvVarPattern.Replace(result, match =>
        {
            var envName = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(envName) ?? match.Value;
        });

        // 3. Resolve {$expression} inline Elwood expressions
        result = InlineExprPattern.Replace(result, match =>
        {
            var expr = match.Groups[1].Value;
            try
            {
                var input = current ?? _factory.CreateNull();
                var evalResult = _engine.Evaluate(expr, input, bindings);
                if (evalResult.Success && evalResult.Value is not null)
                {
                    var val = evalResult.Value;
                    return val.Kind switch
                    {
                        ElwoodValueKind.String => val.GetStringValue() ?? "",
                        ElwoodValueKind.Number => val.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ElwoodValueKind.Boolean => val.GetBooleanValue() ? "true" : "false",
                        _ => val.GetStringValue() ?? ""
                    };
                }
            }
            catch { /* Keep original if expression fails */ }
            return match.Value;
        });

        return result;
    }
}
