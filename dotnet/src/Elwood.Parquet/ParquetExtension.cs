using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Elwood.Core;
using Elwood.Core.Abstractions;

namespace Elwood.Parquet;

/// <summary>
/// Registers fromParquet and toParquet methods with an ElwoodEngine.
/// Usage: ParquetExtension.Register(engine);
/// </summary>
public static class ParquetExtension
{
    public static void Register(ElwoodEngine engine)
    {
        engine.RegisterMethod("fromParquet", FromParquet);
        engine.RegisterMethod("toParquet", ToParquet);
    }

    /// <summary>
    /// Parse base64-encoded Parquet bytes → array of objects.
    /// Schema is inferred from the Parquet file metadata.
    /// </summary>
    private static IElwoodValue FromParquet(IElwoodValue target, List<IElwoodValue> args, IElwoodValueFactory factory)
    {
        var base64 = target.GetStringValue() ?? "";

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch { return factory.CreateArray([]); }

        using var stream = new MemoryStream(bytes);
        using var reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();

        var schema = reader.Schema;
        var dataFields = schema.GetDataFields();
        var rows = new List<IElwoodValue>();

        for (var groupIdx = 0; groupIdx < reader.RowGroupCount; groupIdx++)
        {
            using var rowGroup = reader.OpenRowGroupReader(groupIdx);
            var columns = new DataColumn[dataFields.Length];
            for (var c = 0; c < dataFields.Length; c++)
                columns[c] = rowGroup.ReadColumnAsync(dataFields[c]).GetAwaiter().GetResult();

            var rowCount = columns.Length > 0 ? columns[0].Data.Length : 0;
            for (var r = 0; r < rowCount; r++)
            {
                var props = new List<KeyValuePair<string, IElwoodValue>>();
                for (var c = 0; c < dataFields.Length; c++)
                {
                    var name = dataFields[c].Name;
                    var val = columns[c].Data.GetValue(r);
                    props.Add(new KeyValuePair<string, IElwoodValue>(name, ConvertToElwood(val, factory)));
                }
                rows.Add(factory.CreateObject(props));
            }
        }

        return factory.CreateArray(rows);
    }

    /// <summary>
    /// Array of objects + schema → base64-encoded Parquet bytes.
    /// Options: schema (required — array of {name, type}), compression (default "snappy").
    /// </summary>
    private static IElwoodValue ToParquet(IElwoodValue target, List<IElwoodValue> args, IElwoodValueFactory factory)
    {
        IElwoodValue? schemaValue = null;
        var compression = CompressionMethod.Snappy;

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            schemaValue = args[0].GetProperty("schema");
            var comp = args[0].GetProperty("compression")?.GetStringValue()?.ToLower();
            compression = comp switch
            {
                "none" => CompressionMethod.None,
                "gzip" => CompressionMethod.Gzip,
                "snappy" => CompressionMethod.Snappy,
                "lzo" => CompressionMethod.Lzo,
                "brotli" => CompressionMethod.Brotli,
                "lz4" or "lz4raw" => CompressionMethod.LZ4,
                "zstd" => CompressionMethod.Zstd,
                _ => CompressionMethod.Snappy
            };
        }

        if (schemaValue is null || schemaValue.Kind != ElwoodValueKind.Array)
            throw new Exception("toParquet requires a schema option: { schema: [{ name: \"col\", type: \"string\" }, ...] }");

        var items = target.EnumerateArray().ToList();

        // Parse schema
        var fields = schemaValue.EnumerateArray().Select(f =>
        {
            var name = f.GetProperty("name")?.GetStringValue() ?? "unknown";
            var type = f.GetProperty("type")?.GetStringValue()?.ToLower() ?? "string";
            var nullable = f.GetProperty("nullable")?.GetBooleanValue() ?? true;
            return (name, type, nullable);
        }).ToList();

        var dataFields = fields.Select(f => new DataField(f.name, ToParquetType(f.type), f.nullable)).ToArray();
        var schema = new ParquetSchema(dataFields);

        using var stream = new MemoryStream();
        using (var writer = ParquetWriter.CreateAsync(schema, stream).GetAwaiter().GetResult())
        {
            writer.CompressionMethod = compression;

            using var rowGroup = writer.CreateRowGroup();
            for (var c = 0; c < dataFields.Length; c++)
            {
                var field = fields[c];
                var values = items.Select(item =>
                {
                    var prop = item.GetProperty(field.name);
                    return ConvertToParquetValue(prop, field.type);
                }).ToArray();

                var column = new DataColumn(dataFields[c], CreateTypedArray(values, field.type));
                rowGroup.WriteColumnAsync(column).GetAwaiter().GetResult();
            }
        }

        return factory.CreateString(Convert.ToBase64String(stream.ToArray()));
    }

    private static Type ToParquetType(string type) => type switch
    {
        "string" => typeof(string),
        "int" or "int32" or "integer" => typeof(int),
        "long" or "int64" => typeof(long),
        "float" => typeof(float),
        "double" => typeof(double),
        "bool" or "boolean" => typeof(bool),
        "date" => typeof(DateOnly),
        "datetime" or "timestamp" => typeof(DateTimeOffset),
        "decimal" => typeof(decimal),
        _ => typeof(string)
    };

    private static object? ConvertToParquetValue(IElwoodValue? value, string type)
    {
        if (value is null || value.Kind == ElwoodValueKind.Null) return null;

        return type switch
        {
            "string" => value.Kind == ElwoodValueKind.String ? (value.GetStringValue() ?? "") : value.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "int" or "int32" or "integer" => (int)value.GetNumberValue(),
            "long" or "int64" => (long)value.GetNumberValue(),
            "float" => (float)value.GetNumberValue(),
            "double" => value.GetNumberValue(),
            "bool" or "boolean" => value.GetBooleanValue(),
            "decimal" => (decimal)value.GetNumberValue(),
            "date" => value.Kind == ElwoodValueKind.String && DateOnly.TryParse(value.GetStringValue(), out var d) ? d : DateOnly.MinValue,
            "datetime" or "timestamp" => value.Kind == ElwoodValueKind.String && DateTimeOffset.TryParse(value.GetStringValue(), out var dt) ? dt : DateTimeOffset.MinValue,
            _ => value.Kind == ElwoodValueKind.String ? (value.GetStringValue() ?? "") : value.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static Array CreateTypedArray(object?[] values, string type) => type switch
    {
        "int" or "int32" or "integer" => values.Select(v => v is int i ? (int?)i : null).ToArray(),
        "long" or "int64" => values.Select(v => v is long l ? (long?)l : null).ToArray(),
        "float" => values.Select(v => v is float f ? (float?)f : null).ToArray(),
        "double" => values.Select(v => v is double d ? (double?)d : null).ToArray(),
        "bool" or "boolean" => values.Select(v => v is bool b ? (bool?)b : null).ToArray(),
        "decimal" => values.Select(v => v is decimal m ? (decimal?)m : null).ToArray(),
        "date" => values.Select(v => v is DateOnly dt ? (DateOnly?)dt : null).ToArray(),
        "datetime" or "timestamp" => values.Select(v => v is DateTimeOffset dto ? (DateTimeOffset?)dto : null).ToArray(),
        _ => values.Select(v => v?.ToString()).ToArray() // string
    };

    private static IElwoodValue ConvertToElwood(object? value, IElwoodValueFactory factory) => value switch
    {
        null => factory.CreateNull(),
        string s => factory.CreateString(s),
        int i => factory.CreateNumber(i),
        long l => factory.CreateNumber(l),
        float f => factory.CreateNumber(f),
        double d => factory.CreateNumber(d),
        decimal m => factory.CreateNumber((double)m),
        bool b => factory.CreateBool(b),
        DateOnly dt => factory.CreateString(dt.ToString("yyyy-MM-dd")),
        DateTimeOffset dto => factory.CreateString(dto.ToString("yyyy-MM-ddTHH:mm:ssZ")),
        DateTime dt => factory.CreateString(dt.ToString("yyyy-MM-ddTHH:mm:ssZ")),
        _ => factory.CreateString(value.ToString() ?? "")
    };
}
