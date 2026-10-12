using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

public sealed class AzureStorageFailureClassifier : IStorageFailureClassifier
{
    public bool IsTransient(Exception exception) => AzureTransportFault.IsTransient(exception);
}
