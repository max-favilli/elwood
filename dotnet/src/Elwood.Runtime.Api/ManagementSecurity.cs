using System.Net;
using System.Security.Cryptography;
using System.Text;
using Elwood.Pipeline.Secrets;

namespace Elwood.Runtime.Api;

public sealed class ManagementIdentity
{
    public string Name { get; set; } = "";
    public string TokenSha256 { get; set; } = "";
    public string[] Permissions { get; set; } = [];
    public string[] Pipelines { get; set; } = [];
    public bool LocalOnly { get; set; }
    public bool Can(string operation, string? pipeline = null) =>
        Permissions.Contains(operation) && (pipeline is null || Pipelines.Contains("*") || Pipelines.Contains(pipeline));
}

public sealed class ManagementSecurity
{
    private readonly ManagementIdentity[] _identities;
    private readonly IConfiguration _configuration;

    public ManagementSecurity(IConfiguration configuration, string stateDir)
    {
        _configuration = configuration;
        var identities = configuration.GetSection("Elwood:Management:Tokens").Get<ManagementIdentity[]>() ?? [];
        if (configuration.GetValue<bool>("Elwood:Management:LocalDevelopment"))
        {
            Directory.CreateDirectory(stateDir);
            var path = Elwood.Pipeline.Storage.SafePath.Within(stateDir, "management-token");
            if (!File.Exists(path))
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(path, options);
                file.Write(Encoding.UTF8.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
            }
            identities = [.. identities, new ManagementIdentity
            {
                Name = "local-developer", LocalOnly = true,
                TokenSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim()))),
                Permissions = ["read", "write", "test", "deploy", "recover"], Pipelines = ["*"],
            }];
            Console.WriteLine($"Local management token file: {path} (loopback access only)");
        }
        foreach (var identity in identities)
            if (identity.TokenSha256.Length != 64 || !identity.TokenSha256.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(identity.Name))
                throw new InvalidOperationException("Every management identity needs a name and SHA-256 token hash.");
        _identities = identities;
    }

    public ManagementIdentity? Authenticate(HttpContext context)
    {
        var local = context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
        // Remote bearer credentials require TLS. Forwarded headers are not implicitly trusted.
        if (!local && !context.Request.IsHttps) return null;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length > 1024) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]));
        return _identities.FirstOrDefault(identity => (!identity.LocalOnly || local) &&
            CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(identity.TokenSha256)));
    }

    public ISecretProvider Secrets(ISecretProvider inner, string pipeline, bool isTest) => new ScopedSecretProvider(inner,
        _configuration.GetSection($"Elwood:SecretGrants:{pipeline}:{(isTest ? "Test" : "Run")}").Get<string[]>() ?? []);

    public static ManagementIdentity Identity(HttpContext context) => (ManagementIdentity)context.Items["identity"]!;
}

public sealed record ManagementPermission(string? Name, bool PipelineScoped = false);
public sealed record PublicEndpoint;
