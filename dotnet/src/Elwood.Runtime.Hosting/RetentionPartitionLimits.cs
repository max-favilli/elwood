namespace Elwood.Runtime.Api;

/// <summary>Finite per-partition limits; all workers in an installation must use the same values.</summary>
public sealed record RetentionPartitionLimits(int Objects, long Bytes, int WritesPerHour, long BytesPerHour, int WorkItems, long WorkAttempts)
{
    public static RetentionPartitionLimits Read(IConfiguration configuration, bool partitioned)
    {
        long Read(string name, long fallback, long minimum, long maximum)
        {
            var value = configuration.GetValue("Elwood:Retention:Partition:" + name, fallback);
            if (value < minimum || value > maximum)
                throw new InvalidOperationException($"Elwood:Retention:Partition:{name} must be between {minimum} and {maximum}.");
            return value;
        }
        return new((int)Read("MaxObjects", 2048, 16, 2048),
            Read("MaxBytes", (partitioned ? 16L : 1L) * 1024 * 1024 * 1024, 16 * 1024 * 1024, 1024L * 1024 * 1024 * 1024),
            (int)Read("MaxWritesPerHour", 1000, 1, 100000),
            Read("MaxBytesPerHour", (partitioned ? 1024L : 64L) * 1024 * 1024, 1, 1024L * 1024 * 1024 * 1024),
            (int)Read("MaxWorkItems", 100000, 1, 1000000), Read("MaxWorkAttempts", 1000000, 1, 10000000));
    }
}
