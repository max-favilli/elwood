using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Options;

namespace Elwood.Runtime.Api;

/// <summary>
/// Explicit HTTP dispatch shared by Kestrel and the Functions HTTP entry point.
/// Functions does not expose the ASP.NET middleware/router pipeline; bind handlers
/// with RequestDelegateFactory and invoke the same authorization boundary directly.
/// </summary>
public sealed class RuntimeHttpDispatcher
{
    private readonly CompiledRoute[] _routes;
    private readonly RuntimeHttpBoundary _boundary;
    private readonly ICorsService _cors;
    private readonly CorsPolicy _policy;

    public RuntimeHttpDispatcher(IServiceProvider services, RuntimeHttpBoundary boundary, ICorsService cors, IOptions<CorsOptions> corsOptions)
    {
        _boundary = boundary; _cors = cors;
        _policy = corsOptions.Value.GetPolicy(corsOptions.Value.DefaultPolicyName)
            ?? throw new InvalidOperationException("The runtime CORS policy is required.");
        var definitions = new RuntimeRouteCollection(); RuntimeEndpoints.Register(definitions);
        _routes = definitions.Routes.Select(route =>
        {
            var pattern = RoutePatternFactory.Parse(route.Pattern);
            var builder = new RouteEndpointBuilder(null, pattern, 0) { DisplayName = route.Method + " " + route.Pattern };
            foreach (var metadata in route.Metadata) builder.Metadata.Add(metadata);
            var result = RequestDelegateFactory.Create(route.Handler, new RequestDelegateFactoryOptions
            {
                ServiceProvider = services, EndpointBuilder = builder,
                RouteParameterNames = pattern.Parameters.Select(parameter => parameter.Name),
                DisableInferBodyFromParameters = route.Method is "GET" or "DELETE"
            });
            builder.RequestDelegate = result.RequestDelegate;
            return new CompiledRoute(route.Method, new TemplateMatcher(TemplateParser.Parse(route.Pattern.Replace("{**", "{*")), new()), builder.Build());
        }).ToArray();
    }

    public async Task DispatchAsync(HttpContext context)
    {
        var cors = _cors.EvaluatePolicy(context, _policy);
        _cors.ApplyResult(cors, context.Response);
        if (cors.IsPreflightRequest) { context.Response.StatusCode = 204; return; }
        var oldEndpoint = context.GetEndpoint(); var oldValues = context.Request.RouteValues;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var route in _routes)
            {
                var values = new RouteValueDictionary();
                if (!route.Matcher.TryMatch(context.Request.Path, values)) continue;
                allowed.Add(route.Method);
                if (!string.Equals(context.Request.Method, route.Method, StringComparison.OrdinalIgnoreCase)) continue;
                context.Request.RouteValues = values; context.SetEndpoint(route.Endpoint);
                await _boundary.InvokeAsync(context, route.Endpoint.RequestDelegate!);
                return;
            }
            context.Response.StatusCode = allowed.Count == 0 ? 404 : 405;
            if (allowed.Count > 0) context.Response.Headers.Allow = string.Join(", ", allowed.Order());
        }
        finally { context.SetEndpoint(oldEndpoint); context.Request.RouteValues = oldValues; }
    }

    private sealed record CompiledRoute(string Method, TemplateMatcher Matcher, Endpoint Endpoint);
}

public sealed class RuntimeRouteCollection
{
    internal List<RuntimeRoute> Routes { get; } = [];
    public RuntimeRoute MapGet(string pattern, Delegate handler) => Add("GET", pattern, handler);
    public RuntimeRoute MapPost(string pattern, Delegate handler) => Add("POST", pattern, handler);
    public RuntimeRoute MapPut(string pattern, Delegate handler) => Add("PUT", pattern, handler);
    public RuntimeRoute MapDelete(string pattern, Delegate handler) => Add("DELETE", pattern, handler);
    private RuntimeRoute Add(string method, string pattern, Delegate handler)
    {
        var route = new RuntimeRoute(method, pattern, handler); Routes.Add(route); return route;
    }
}

public sealed class RuntimeRoute(string method, string pattern, Delegate handler)
{
    internal string Method { get; } = method;
    internal string Pattern { get; } = pattern;
    internal Delegate Handler { get; } = handler;
    internal List<object> Metadata { get; } = [];
    public RuntimeRoute WithMetadata(params object[] metadata) { Metadata.AddRange(metadata); return this; }
}
