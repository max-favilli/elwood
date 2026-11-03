using Elwood.Pipeline.Async;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Runtime.Api;

internal static class WorkItemEndpoints
{
    internal static void Register(RuntimeRouteCollection routes)
    {
        routes.MapGet("/api/executions/{id}/work-stages", async (string id, HttpContext context) =>
        {
            var state = await Parent(id, context);
            return Results.Ok(Reader(context).Stages(state));
        }).WithMetadata(new ManagementPermission("read", false));
        routes.MapGet("/api/executions/{id}/work-stages/{stage}/items", async
            (string id, string stage, string? status, string? cursor, int? limit, HttpContext context) =>
        {
            var state = await Parent(id, context);
            return Results.Ok(await Reader(context).ItemsAsync(state, stage, status, cursor, limit ?? 50, context.RequestAborted));
        }).WithMetadata(new ManagementPermission("read", false));
        routes.MapGet("/api/executions/{id}/work-stages/{stage}/items/{ordinal}", async
            (string id, string stage, int ordinal, string? attemptCursor, int? limit, HttpContext context) =>
        {
            var state = await Parent(id, context);
            return Results.Ok(await Reader(context).ItemAsync(state, stage, ordinal, attemptCursor, limit ?? 10, context.RequestAborted));
        }).WithMetadata(new ManagementPermission("read", false));
        routes.MapGet("/api/executions/{id}/work-stages/{stage}/items/{ordinal}/inspection", async
            (string id, string stage, int ordinal, int? attempt, HttpContext context) =>
        {
            var state = await Parent(id, context);
            return Results.Ok(await Reader(context).InspectAsync(state, stage, ordinal, attempt, context.RequestAborted));
        }).WithMetadata(new ManagementPermission("read", false));
        routes.MapPost("/api/executions/{id}/work-stages/{stage}/retry", async
            (string id, string stage, WorkRetryRequest request, HttpContext context) =>
        {
            await Parent(id, context, recover: true);
            // Registration is an explicit host capability; do not accept intent that
            // a host without the distributed scheduler cannot reconcile after a crash.
            var recovery = context.RequestServices.GetService<WorkItemRecovery>()
                ?? throw new RecoveryConflictException("Distributed recovery is not enabled on this host.");
            return Results.Ok(await recovery.RetryAsync(id, stage, request, context.RequestAborted));
        }).WithMetadata(new ManagementPermission("recover", false));
    }
    private static WorkItemManagement Reader(HttpContext context) => new(
        context.RequestServices.GetService<DurableWorkJournal>() ?? throw new RecoveryConflictException("Item journal is not configured on this host."),
        context.RequestServices.GetRequiredService<IDocumentStore>());
    private static async Task<ExecutionState> Parent(string id, HttpContext context, bool recover = false)
    {
        SafePath.Id(id);
        var identity = ManagementSecurity.Identity(context);
        var state = await context.RequestServices.GetRequiredService<IStateStore>().GetExecutionAsync(id);
        if (state is null || state.Deleting || state.ExpiresAt <= DateTime.UtcNow ||
            state.PipelineId is null && !identity.Pipelines.Contains("*") ||
            !identity.Can("read", state.PipelineId) || recover && !identity.Can("recover", state.PipelineId))
            throw new KeyNotFoundException("Execution not found.");
        return state;
    }
}
