using Elwood.Runtime.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Elwood.Runtime.Azure.Functions;

/// <summary>Both management and pipeline triggers use the same handlers and authorization as the local API.</summary>
public sealed class HttpTriggerFunction(RuntimeHttpDispatcher http)
{
    [Function("RuntimeHttp")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "delete", "options", Route = "{*path}")] HttpRequest request)
    {
        await http.DispatchAsync(request.HttpContext);
        // The dispatcher has written the response. IActionResult declares the native
        // HTTP output binding; EmptyResult must not serialize or overwrite it again.
        return new EmptyResult();
    }
}
