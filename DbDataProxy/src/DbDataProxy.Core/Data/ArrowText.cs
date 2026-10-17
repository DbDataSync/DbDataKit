using System.Globalization;
using Apache.Arrow;

namespace DbDataProxy.Data;

/// <summary>Reads a single Arrow value as text; null for nulls. Used for small metadata results and diagnostics.</summary>
public static class ArrowText
{
    public static string? Get(IArrowArray array, int row)
    {
        if (array.IsNull(row)) return null;
        return array switch
        {
            StringArray a => a.GetString(row),
            Int8Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            Int16Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            Int32Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            Int64Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            UInt8Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            UInt16Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            UInt32Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            UInt64Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            FloatArray a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            DoubleArray a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            BooleanArray a => a.GetValue(row)!.Value.ToString(),
            Decimal128Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
            Date32Array a => a.GetDateTimeOffset(row)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimestampArray a => a.GetTimestamp(row)!.Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            _ => "<" + array.Data.DataType.Name + ">",
        };
    }
}
