using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbDataKit.State.Snapshots;

/// <summary>What a value read from any engine becomes in a table file: one neutral form per logical type (see <see cref="SnapshotColumnTypes"/>).</summary>
internal static class SnapshotValues
{
    /// <summary>The decimal with the trailing zeros of its scale removed: SQL Server's <c>DECIMAL(38,10)</c> gives <c>1.5000000000</c> for what SQLite stores as <c>1.5</c>.</summary>
    internal static string DecimalText(decimal value) => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>The writer options of a table file's lines and of the row hashes (the same bytes): no white space, non-ASCII text as it is.</summary>
    internal static readonly JsonWriterOptions RowOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    /// <summary>
    /// The neutral form of what a reader returned for a column of <paramref name="type"/> as a string, or for a blob a <see cref="byte"/> array; null for NULL. The verifier orders and pages keys by it
    /// and the table file writes the same text (an int and a bool are a number and a boolean there, <see cref="Write"/> says so).
    /// </summary>
    internal static object? Normalise(string type, object? raw)
    {
        if (raw is null or DBNull) return null;
        return type switch
        {
            SnapshotColumnTypes.Uuid => StateValues.ToGuid(raw).ToString("D", CultureInfo.InvariantCulture),
            SnapshotColumnTypes.Time => StateValues.FormatTime(StateValues.ToTime(raw)),
            SnapshotColumnTypes.Bool => Convert.ToBoolean(raw, CultureInfo.InvariantCulture) ? "true" : "false",
            SnapshotColumnTypes.Int => Convert.ToInt64(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            SnapshotColumnTypes.Decimal => DecimalText(StateValues.ToDecimal(raw)),
            SnapshotColumnTypes.Json => CanonicalJson(raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture)!),
            SnapshotColumnTypes.Blob => raw as byte[] ?? throw new InvalidCastException($"A blob column held a {raw.GetType().Name}."),
            _ => raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Writes <paramref name="raw"/> (what <c>DbDataReader.GetValue</c> returned) as the neutral value of <paramref name="type"/>.</summary>
    public static void Write(Utf8JsonWriter json, string type, object? raw)
    {
        if (raw is null or DBNull)
        {
            json.WriteNullValue();
            return;
        }

        switch (type)
        {
            case SnapshotColumnTypes.Bool:
                json.WriteBooleanValue(Convert.ToBoolean(raw, CultureInfo.InvariantCulture));
                break;
            case SnapshotColumnTypes.Int:
                json.WriteNumberValue(Convert.ToInt64(raw, CultureInfo.InvariantCulture));
                break;
            case SnapshotColumnTypes.Blob:
                json.WriteBase64StringValue((byte[])raw);
                break;
            default:
                json.WriteStringValue((string)Normalise(type, raw)!);
                break;
        }
    }

    /// <summary>
    /// The hash of one row: SHA-256 of its table-file line (a JSON array, no white space, no line break) of the columns' neutral values in declaration order. What the exporter hashes and the
    /// verifier hashes, so a database and a manifest agree.
    /// </summary>
    internal static byte[] HashRow(IReadOnlyList<string> types, object?[] raw)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer, RowOptions))
        {
            json.WriteStartArray();
            for (var i = 0; i < types.Count; i++) Write(json, types[i], raw[i]);
            json.WriteEndArray();
        }

        return SHA256.HashData(buffer.WrittenSpan);
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
