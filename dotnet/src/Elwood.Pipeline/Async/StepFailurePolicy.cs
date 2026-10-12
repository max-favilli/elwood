using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Async;

internal static class StepFailurePolicy
{
    internal sealed record Decision(bool Uncertain, bool Retryable);
    internal static Decision Classify(Exception error, StepAttempt attempt, RetryPolicy? policy, bool storageFault)
    {
        var status = error is HttpRequestException http ? (int?)http.StatusCode : (error as ConnectorFailure)?.StatusCode ?? (error as ConnectorException)?.StatusCode;
        if (status == 0) status = null;
        var transport = storageFault || error is HttpRequestException or OperationCanceledException or TimeoutException || error is ConnectorFailure && status is null;
        var uncertain = attempt.ExternalCallStarted && attempt.MayHaveWritten && status is null && (transport || error is IOException);
        if (error is ConnectorException classified) uncertain = classified.OutcomeUncertain;
        var retryable = status is not null ? policy?.StatusCodes.Contains(status.Value) == true : transport && policy?.TransportErrors == true;
        if (error is ConnectorException failure && status is null) retryable = failure.Retryable && policy?.TransportErrors == true;
        return new(uncertain, retryable);
    }
    internal static bool CanRetry(StepState step, RetryPolicy? policy)
        => policy is { Mode: "automatic" } && step.Attempts.Count < 100 && step.Attempts.Count - step.RetryBase < policy.MaxAttempts;
    internal static double Delay(StepState step, RetryPolicy policy)
        => Math.Min(policy.MaxDelayMs, policy.DelayMs * Math.Pow(policy.BackoffMultiplier, Math.Max(0, step.Attempts.Count - step.RetryBase - 1)));
}
