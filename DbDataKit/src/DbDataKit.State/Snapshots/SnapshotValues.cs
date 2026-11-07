using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace DbDataKit.State.Snapshots;

/// <summary>What a value read from any engine becomes in a table file: one neutral form per logical type (see <see cref="SnapshotColumnTypes"/>).</summary>
internal static class SnapshotValues
{
    /// <summary>The decimal with the trailing zeros of its scale removed: SQL Server's <c>DECIMAL(38,10)</c> gives <c>1.5000000000</c> for what SQLite stores as <c>1.5</c>.</summary>
    internal static string DecimalText(decimal value) => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes <paramref name="raw"/> (what <c>DbDataReader.GetValue</c> returned) as the neutral value of <paramref name="type"/>.</summary>
    public static void Write(Utf8JsonWriter json, string type, object raw)
    {
        if (raw is DBNull)
        {
            json.WriteNullValue();
            return;
        }

        switch (type)
        {
            case SnapshotColumnTypes.Uuid:
                json.WriteStringValue(StateValues.ToGuid(raw).ToString("D", CultureInfo.InvariantCulture));
                break;
            case SnapshotColumnTypes.Time:
                json.WriteStringValue(StateValues.FormatTime(StateValues.ToTime(raw)));
                break;
            case SnapshotColumnTypes.Bool:
                json.WriteBooleanValue(Convert.ToBoolean(raw, CultureInfo.InvariantCulture));
                break;
            case SnapshotColumnTypes.Int:
                json.WriteNumberValue(Convert.ToInt64(raw, CultureInfo.InvariantCulture));
                break;
            case SnapshotColumnTypes.Decimal:
                json.WriteStringValue(DecimalText(StateValues.ToDecimal(raw)));
                break;
            case SnapshotColumnTypes.Json:
                json.WriteStringValue(CanonicalJson(Convert.ToString(raw, CultureInfo.InvariantCulture)!));
                break;
            case SnapshotColumnTypes.Blob:
                json.WriteBase64StringValue((byte[])raw);
                break;
            default:
                json.WriteStringValue(Convert.ToString(raw, CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>
    /// A JSON document with its object members sorted by name (ordinal) and no white space; numbers and strings are kept as they were written. PostgreSQL's <c>jsonb</c> reorders members and adds
    /// spaces, the other engines keep the text, and the snapshot holds the one form all three agree on. A value that is not valid JSON (which the engines other than PostgreSQL do not check) is kept as it is.
    /// </summary>
    internal static string CanonicalJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                WriteCanonical(writer, document.RootElement);
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
