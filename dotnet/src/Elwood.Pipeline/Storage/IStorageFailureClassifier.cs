namespace Elwood.Pipeline.Storage;

/// <summary>Provider adapters identify transient SDK faults without coupling the runner to that SDK.</summary>
public interface IStorageFailureClassifier
{
    bool IsTransient(Exception exception);
}
