using System.Globalization;
using Apache.Arrow;

namespace DbDataKit.Serving.FlightSql;

/// <summary>Reads the parameter batches a Flight SQL client sends (<c>DoPut</c> on a prepared statement) into <see cref="ServingParameter"/> rows.</summary>
public static class ArrowParameterReader
{
    /// <summary>Reads one row of a parameter batch; each column is one parameter, in order.</summary>
    public static List<ServingParameter> ReadRow(RecordBatch batch, int row)
    {
        var parameters = new List<ServingParameter>(batch.ColumnCount);
        for (int c = 0; c < batch.ColumnCount; c++)
            parameters.Add(Read(batch.Schema.GetFieldByIndex(c).Name, batch.Column(c), row));
        return parameters;
    }

    private static string ListAsJson(string name, ListArray list, int row)
    {
        var start = list.ValueOffsets[row];
        var end = list.ValueOffsets[row + 1];
        var elements = new System.Text.Json.Nodes.JsonArray();
        for (int i = start; i < end; i++)
        {
            var element = Read(name, list.Values, i);
            elements.Add(element.Type switch
            {
                "null" => null,
                "bool" => System.Text.Json.Nodes.JsonValue.Create(string.Equals(element.Text, "true", StringComparison.OrdinalIgnoreCase)),
                "int64" => System.Text.Json.Nodes.JsonValue.Create(long.Parse(element.Text!, CultureInfo.InvariantCulture)),
                "double" => System.Text.Json.Nodes.JsonValue.Create(double.Parse(element.Text!, CultureInfo.InvariantCulture)),
                "decimal" => System.Text.Json.Nodes.JsonValue.Create(decimal.Parse(element.Text!, CultureInfo.InvariantCulture)),
                _ => System.Text.Json.Nodes.JsonValue.Create(element.Text),
            });
        }
        return elements.ToJsonString();
    }

    private static ServingParameter Read(string name, IArrowArray array, int row)
    {
        var inv = CultureInfo.InvariantCulture;
        if (array.IsNull(row)) return new ServingParameter(name, "null", null);
        return array switch
        {
            BooleanArray a => new(name, "bool", a.GetValue(row)!.Value.ToString()),
            Int8Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            Int16Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            Int32Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            Int64Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt8Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt16Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt32Array a => new(name, "int64", a.GetValue(row)!.Value.ToString(inv)),
            UInt64Array a => new(name, "uint64", a.GetValue(row)!.Value.ToString(inv)),
            FloatArray a => new(name, "double", ((double)a.GetValue(row)!.Value).ToString("R", inv)),
            DoubleArray a => new(name, "double", a.GetValue(row)!.Value.ToString("R", inv)),
            Decimal128Array a => new(name, "decimal", a.GetValue(row)!.Value.ToString(inv)),
            StringArray a => new(name, "string", a.GetString(row)),
            LargeStringArray a => new(name, "string", a.GetString(row)),
            Date32Array a => new(name, "date", a.GetDateTime(row)!.Value.Date.ToString("o", inv)),
            Date64Array a => new(name, "date", a.GetDateTime(row)!.Value.Date.ToString("o", inv)),
            TimestampArray a => new(name, "timestamp", a.GetTimestamp(row)!.Value.UtcDateTime.ToString("o", inv)),
            Time32Array a => new(name, "time", TimeSpan.FromTicks(a.GetTime(row)!.Value.Ticks).ToString("c", inv)),
            Time64Array a => new(name, "time", TimeSpan.FromTicks(a.GetTime(row)!.Value.Ticks).ToString("c", inv)),
            BinaryArray a => new(name, "binary", Convert.ToBase64String(a.GetBytes(row))),
            // a list parameter (a semantic model's): its elements as a JSON array
            ListArray a => new(name, "list", ListAsJson(name, a, row)),
            _ => throw new ServingInvalidException($"Parameter '{name}' has Arrow type {array.Data.DataType.Name}, which is not supported."),
        };
    }
}
