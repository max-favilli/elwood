using Azure;

namespace Elwood.Pipeline.Azure;

internal static class AzureTransportFault
{
    internal static bool IsTransient(Exception exception) => exception is
        RequestFailedException { Status: 0 or 408 or 429 or 500 or 502 or 503 or 504 }
        or HttpRequestException or TimeoutException or OperationCanceledException;
}
