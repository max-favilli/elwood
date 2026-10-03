using System.Buffers;
using System.Text.Json;
using Elwood.Core.Abstractions;

namespace Elwood.Json;

/// <summary>
/// Writes an <see cref="IElwoodValue"/> straight to UTF-8 JSON.
/// </summary>
/// <remarks>
/// Pairs with <c>ElwoodEngine.EvaluateTo</c>/<c>ExecuteTo</c>: those hand over a result that has
/// not been turned into a <c>JsonNode</c> graph, and this writes it out without building one. For
/// a large projection the output graph is the single biggest retained allocation of the output
/// stage, and this path skips it entirely.
/// <para>
/// Values are walked through the public <see cref="IElwoodValue"/> surface, so this works for any
/// adapter's values, with a fast path when a value is already a <see cref="JsonNodeValue"/>.
/// </para>
/// </remarks>
public static class ElwoodJsonWriter
{
    /// <summary>Writes <paramref name="value"/> to <paramref name="writer"/>. Does not flush.</summary>
    public static void Write(Utf8JsonWriter writer, IElwoodValue? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Fast path: an already-concrete node knows how to write itself.
        if (value is JsonNodeValue { Node: var node })
        {
            if (node is null) writer.WriteNullValue();
            else node.WriteTo(writer);
            return;
        }

        switch (value.Kind)
        {
            case ElwoodValueKind.Null:
                writer.WriteNullValue();
                break;

            case ElwoodValueKind.String:
                writer.WriteStringValue(value.GetStringValue());
                break;

            case ElwoodValueKind.Number:
                writer.WriteNumberValue(value.GetNumberValue());
                break;

            case ElwoodValueKind.Boolean:
                writer.WriteBooleanValue(value.GetBooleanValue());
                break;

            case ElwoodValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;

            case ElwoodValueKind.Object:
                writer.WriteStartObject();
                foreach (var name in value.GetPropertyNames())
                {
                    writer.WritePropertyName(name);
                    Write(writer, value.GetProperty(name));
                }
                writer.WriteEndObject();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> into a buffer the caller owns, then flushes the writer.
    /// Nothing reaches a network or file stream until the caller copies the buffer out, which is
    /// what makes it safe to abandon the write when evaluation reports a failure.
    /// </summary>
    public static void Write(IBufferWriter<byte> buffer, IElwoodValue? value, JsonWriterOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        using var writer = new Utf8JsonWriter(buffer, options);
        Write(writer, value);
        writer.Flush();
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to a UTF-8 byte array, without building a JSON graph.
    /// Cheaper than materialising and calling <c>ToJsonString</c>, which also allocates a
    /// UTF-16 string about twice the size of the output.
    /// </summary>
    public static byte[] ToUtf8Bytes(IElwoodValue? value, JsonWriterOptions options = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, value, options);
        return buffer.WrittenSpan.ToArray();
    }
}
