using System.Text;

namespace Elwood.Core.Abstractions;

/// <summary>
/// Factory for creating IElwoodValue instances from raw data.
/// Each JSON library adapter provides its own implementation.
/// </summary>
public interface IElwoodValueFactory
{
    IElwoodValue Parse(string json);

    /// <summary>
    /// Parse UTF-8 encoded JSON without going through a UTF-16 string.
    /// Prefer this when the caller already holds bytes: a string copy of the
    /// document costs roughly twice the document's size, and measurably more
    /// to parse. A leading UTF-8 byte order mark is skipped.
    /// </summary>
    /// <remarks>
    /// The default implementation transcodes to a string and calls
    /// <see cref="Parse(string)"/>, so every adapter works unchanged. Adapters
    /// whose backing library can read UTF-8 directly should override it.
    /// </remarks>
    IElwoodValue ParseUtf8(ReadOnlySpan<byte> utf8Json)
        => Parse(Encoding.UTF8.GetString(StripBom(utf8Json)));

    /// <summary>
    /// Parse UTF-8 encoded JSON from a stream without buffering it as a string.
    /// The stream is read to its end but not disposed.
    /// </summary>
    /// <remarks>
    /// The default implementation reads the stream into memory and delegates to
    /// <see cref="ParseUtf8(ReadOnlySpan{byte})"/>.
    /// </remarks>
    IElwoodValue ParseUtf8(Stream utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        using var buffer = new MemoryStream();
        utf8Json.CopyTo(buffer);
        return ParseUtf8(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties);
    IElwoodValue CreateArray(IEnumerable<IElwoodValue> items);
    IElwoodValue CreateString(string value);
    IElwoodValue CreateNumber(double value);
    IElwoodValue CreateBool(bool value);
    IElwoodValue CreateNull();

    /// <summary>Skips a leading UTF-8 byte order mark, which is not valid JSON.</summary>
    protected static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> utf8)
        => utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF
            ? utf8[3..]
            : utf8;
}
