namespace Elwood.Pipeline.Connectors;

/// <summary>Provider-neutral failure facts. Retry authorization belongs to the pipeline.</summary>
public sealed class ConnectorException(string message, string code, bool retryable = false,
    bool outcomeUncertain = false, int? statusCode = null, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool OutcomeUncertain { get; } = outcomeUncertain;
    public int? StatusCode { get; } = statusCode;
}
