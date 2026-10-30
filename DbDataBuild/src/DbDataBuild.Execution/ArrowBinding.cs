using System.Data;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataBuild.State;
using DbDataKit.Connections.FlightSql;

namespace DbDataBuild.Execution;

/// <summary>
/// What Build adds to the shared Flight SQL package's reading of Arrow (<see cref="ArrowValueReader"/>): a field as the tool's <see cref="ColumnShape"/>, typed as `information_schema` names it so one table of types (the
/// dialect's `types:`) serves a source found through a catalog and one found through Arrow, and the parameters of a plan as the one-row batch Flight SQL binds.
/// </summary>
internal static class ArrowBinding
{
    /// <summary>The column a field of an Arrow schema is (an Arrow name such as `list` is what the dialect's `types:` can map, and is otherwise said not to have a logical type).</summary>
    public static ColumnShape Column(Field field)
    {
        var type = ArrowValueReader.ColumnType(field);
        return new(field.Name, type.Name, null, type.Precision, type.Scale, field.IsNullable, null, null);
    }

    public static IReadOnlyList<ColumnShape> Columns(Schema schema) => schema.FieldsList.Select(Column).ToList();

    /// <summary>
    /// The parameters of a prepared statement as the one-row record batch Flight SQL binds, a column per `?` in the order the plan gives them (a placeholder used twice is two columns). A column's Arrow type follows the
    /// parameter's <see cref="DbType"/>; a NULL is a null of that type.
    /// </summary>
    public static RecordBatch ParameterBatch(IReadOnlyList<GateParameter> parameters)
    {
        var fields = new List<Field>();
        var arrays = new List<IArrowArray>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var (array, type) = ParameterArray(parameters[i]);
            fields.Add(new Field($"p{i}", type, true));
            arrays.Add(array);
        }
        return new RecordBatch(new Schema(fields, null), arrays, 1);
    }

    private static System.Globalization.CultureInfo Inv => System.Globalization.CultureInfo.InvariantCulture;

    private static (IArrowArray, IArrowType) ParameterArray(GateParameter p)
    {
        var v = p.Value;
        switch (p.Type)
        {
            case DbType.Int64 or DbType.UInt32 or DbType.UInt64:
            {
                var b = new Int64Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToInt64(v, Inv));
                return (b.Build(), Int64Type.Default);
            }
            case DbType.Int32 or DbType.UInt16:
            {
                var b = new Int32Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToInt32(v, Inv));
                return (b.Build(), Int32Type.Default);
            }
            case DbType.Int16 or DbType.Byte or DbType.SByte:
            {
                var b = new Int16Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToInt16(v, Inv));
                return (b.Build(), Int16Type.Default);
            }
            case DbType.Boolean:
            {
                var b = new BooleanArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToBoolean(v, Inv));
                return (b.Build(), BooleanType.Default);
            }
            case DbType.Double:
            {
                var b = new DoubleArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToDouble(v, Inv));
                return (b.Build(), DoubleType.Default);
            }
            case DbType.Single:
            {
                var b = new FloatArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToSingle(v, Inv));
                return (b.Build(), FloatType.Default);
            }
            case DbType.Decimal or DbType.Currency or DbType.VarNumeric:
            {
                var d = v == null ? 0m : Convert.ToDecimal(v, Inv);
                var scale = (decimal.GetBits(d)[3] >> 16) & 0x7F;
                var type = new Decimal128Type(Math.Max(28, scale + 1), scale);
                var b = new Decimal128Array.Builder(type);
                if (v == null) b.AppendNull(); else b.Append(d);
                return (b.Build(), type);
            }
            case DbType.Date:
            {
                var b = new Date32Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(ToDateTime(v));
                return (b.Build(), Date32Type.Default);
            }
            case DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset:
            {
                // a clock reading with no zone: the plan's watermark is compared with a TIMESTAMP column, which has none either
                var type = new TimestampType(TimeUnit.Microsecond, (string?)null);
                var b = new TimestampArray.Builder(type);
                if (v == null) b.AppendNull();
                else b.Append(v is DateTimeOffset o ? o : new DateTimeOffset(DateTime.SpecifyKind(ToDateTime(v), DateTimeKind.Utc)));
                return (b.Build(), type);
            }
            case DbType.Binary:
            {
                var b = new BinaryArray.Builder();
                if (v is byte[] bytes) b.Append(bytes.AsSpan()); else b.AppendNull();
                return (b.Build(), BinaryType.Default);
            }
            default:
            {
                var b = new StringArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToString(v, Inv)!);
                return (b.Build(), StringType.Default);
            }
        }
    }

    private static DateTime ToDateTime(object v) => v switch
    {
        DateTime d => d,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => Convert.ToDateTime(v, System.Globalization.CultureInfo.InvariantCulture),
    };
}
