namespace Elwood.Pipeline.Storage;

/// <summary>Finite limits shared by authoring, execution and provider lifecycle rules.</summary>
public static class RetentionPolicy
{
    public const int DefaultSeconds = 30 * 24 * 60 * 60;
    public const int MaximumSeconds = 365 * 24 * 60 * 60;
    public const int DefaultExecutionSeconds = 24 * 60 * 60;
    public const int MaximumExecutionSeconds = 7 * 24 * 60 * 60;
    public const int NativeBackstopDays = 380;

    public static int Effective(int? seconds)
    {
        var value = seconds ?? DefaultSeconds;
        if (value is <= 0 or > MaximumSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Retention must be between 1 second and 365 days.");
        return value;
    }
}
