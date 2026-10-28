using DbDataKit.Dialect;
using DbDataKit.Transfer;

namespace DbDataBuild.Execution;

/// <summary>A column of the rows being copied: its name and its declared logical type, which decides what the values are made into on the way.</summary>
public sealed record TransferColumn(string Name, string LogicalType);

/// <summary>A value could not be copied. The message names the column, never the value.</summary>
public sealed class TransferException(string message) : Exception(message);

/// <summary>
/// The values of a copy are converted by the column's **declared logical type**, not by whatever the origin's driver happened to return, so every route between engines gives the destination the same
/// kind of value (DuckDB's types are the common language, as everywhere else in the tool). The type is read by the shared DuckDB dialect into a canonical type and the value is made that type by the
/// shared converter (plan 13, B08), which is also what the bulk writers take. A value that does not fit the type is an error that names the column and never the value.
/// </summary>
public static class TransferValues
{
    private static readonly ISqlDialect Duck = DialectCatalog.For(EngineId.DuckDb);

    /// <summary>The canonical column of a declared one, or an error naming the column when a copy cannot carry its type.</summary>
    public static DbDataKit.Transfer.TransferColumn Canonical(TransferColumn column)
    {
        var mapping = Duck.ToCanonical(column.LogicalType.Trim());
        if (!mapping.IsMapped || mapping.Result is not { } type || type.Kind == CanonicalKind.Unmappable)
            throw new TransferException($"column `{column.Name}` has type {column.LogicalType}, which a copy cannot carry.");
        return new DbDataKit.Transfer.TransferColumn(column.Name, type);
    }

    /// <summary>The canonical schema of the declared columns, in order: what the bulk writers are told the rows hold.</summary>
    public static TransferSchema Schema(IReadOnlyList<TransferColumn> columns) => new(columns.Select(Canonical));

    /// <summary>
    /// The conversion of a row of values read from an origin into the canonical values of <paramref name="columns"/>. Planned once, applied to every row.
    /// </summary>
    public static RowConversion Plan(IReadOnlyList<TransferColumn> columns) => new(columns, Schema(columns));

    /// <summary>Converts one value read from the origin to the canonical CLR type of <paramref name="column"/>. A null stays null.</summary>
    public static object? Convert(object? value, TransferColumn column) => Plan([column]).Convert([value])[0];

    public sealed class RowConversion
    {
        private readonly IReadOnlyList<TransferColumn> _columns;
        private readonly ConversionPlan[] _plans;

        internal RowConversion(IReadOnlyList<TransferColumn> columns, TransferSchema schema)
        {
            _columns = columns;
            Schema = schema;
            // The origin's type is not known beyond its value, so the source is the declared type itself: nothing is judged lossy that the value does not show
            _plans = [.. schema.Columns.Select(c => ValueConverter.Plan(c, c, Duck))];
        }

        public TransferSchema Schema { get; }

        public object?[] Convert(object?[] row)
        {
            var converted = new object?[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                try { converted[i] = _plans[i].Convert(row[i]); }
                catch (ValueConversionException ex) { throw new TransferException($"column `{_columns[i].Name}`: a value does not fit {_columns[i].LogicalType} ({ex.Failure})."); }
            }
            return converted;
        }
    }
}
