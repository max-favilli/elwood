using System.Buffers;
using System.Security.Cryptography;

namespace Elwood.Pipeline.Azure;

internal static class PayloadBlockTransfer
{
    internal const int BlockBytes = 4 * 1024 * 1024;
    internal const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    internal sealed record Result(long Bytes, string Sha256);

    internal static async Task<Result> CopyAsync(Stream input, long maximumBytes, long? expectedBytes,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> stage, CancellationToken token)
    {
        if (maximumBytes is < 1 or > MaximumBytes || expectedBytes is < 0 || expectedBytes > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var limit = expectedBytes ?? maximumBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(BlockBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            while (true)
            {
                var filled = 0;
                var ended = false;
                while (filled < BlockBytes)
                {
                    token.ThrowIfCancellationRequested();
                    // Read at most one byte beyond the bound. Neither an absent
                    // Content-Length nor a dishonest declared length bypasses it.
                    var count = await input.ReadAsync(buffer.AsMemory(filled,
                        (int)Math.Min(BlockBytes - filled, limit - total - filled + 1)), token);
                    if (count == 0) { ended = true; break; }
                    filled += count;
                    if (total + filled > limit) throw new InvalidDataException("Payload exceeds its byte limit or declared length.");
                }
                if (filled > 0)
                {
                    hash.AppendData(buffer, 0, filled);
                    await stage(buffer.AsMemory(0, filled), token);
                    total += filled;
                }
                if (!ended) continue;
                if (expectedBytes is not null && total != expectedBytes)
                    throw new InvalidDataException("Payload ended before its declared length.");
                return new(total, Convert.ToHexString(hash.GetHashAndReset()));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
