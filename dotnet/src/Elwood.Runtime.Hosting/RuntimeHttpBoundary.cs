using System.Text.Json;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Async;

namespace Elwood.Runtime.Api;

public sealed class RuntimeHttpBoundary(ILogger<RuntimeHttpBoundary> logger)
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is not null && endpoint.Metadata.GetMetadata<PublicEndpoint>() is null)
            {
                var permission = endpoint.Metadata.GetMetadata<ManagementPermission>()
                    ?? throw new InvalidOperationException("Management endpoint has no permission metadata.");
                var identity = context.RequestServices.GetRequiredService<ManagementSecurity>().Authenticate(context);
                if (identity is null)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await context.Response.WriteAsJsonAsync(new { error = "Management authentication required." });
                    return;
                }
                context.Items["identity"] = identity;
                var pipeline = permission.PipelineScoped ? context.Request.RouteValues["id"]?.ToString() : null;
                if (pipeline is not null) SafePath.Id(pipeline);
                if (permission.Name is not null && !identity.Can(permission.Name, pipeline))
                    throw new PermissionDeniedException("This identity does not have permission for this operation.");
            }
            await next(context);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or DeploymentConflictException or RecoveryConflictException or KeyNotFoundException or PermissionDeniedException)
        {
            context.Response.StatusCode = ex switch
            {
                PermissionDeniedException => 403, DeploymentConflictException or RecoveryConflictException => 409,
                KeyNotFoundException => 404, _ => 400,
            };
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Storage operation failed for {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "A storage operation failed. Consult the runtime logs." });
        }
    }
}
