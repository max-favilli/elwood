namespace Elwood.Pipeline.Azure;

/// <summary>Indexed maintenance queues. Each call returns at most the requested number of records.</summary>
public enum RetentionMaintenanceKind { Artifacts, ExecutionReservations }

internal static class RetentionMaintenance
{
    public const int MaximumPageSize = 256;

    public static void Validate(RetentionMaintenanceKind kind, int limit)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    public static DateTimeOffset DueAt(BlobRetentionEntry entry, RetentionMaintenanceKind kind)
    {
        var due = kind == RetentionMaintenanceKind.Artifacts ? entry.ExpiresAt : entry.RegisteredAt.AddMinutes(10);
        return entry.LastAttemptAt is { } attempted && attempted > due ? attempted : due;
    }

    public static IReadOnlyList<BlobRetentionEntry> Select(IEnumerable<BlobRetentionEntry> entries,
        RetentionMaintenanceKind kind, DateTimeOffset now, int limit) => entries
        .Where(e => (kind == RetentionMaintenanceKind.ExecutionReservations ? e.Kind == "execution" : e.Kind != "execution") && DueAt(e, kind) <= now)
        .OrderBy(e => DueAt(e, kind)).ThenBy(e => e.Id, StringComparer.Ordinal).Take(limit).Select(e => e with { }).ToArray();
}
