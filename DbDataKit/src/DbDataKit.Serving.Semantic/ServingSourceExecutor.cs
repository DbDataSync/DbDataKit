using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// Runs the queries the semantic engine plans through the host's own query service, as the caller: each scan is a SQL request against the serving source the model maps its catalog source to, so the host's access rules, classifier and limits
/// apply to it exactly as they do to a client's SQL. Which text runs depends on the product the source reports (<see cref="NativeRoute"/>). The engine is synchronous, so this blocks the thread it runs on; the module runs the engine off the request's thread.
/// </summary>
internal sealed class ServingSourceExecutor(IServingQueries queries, IServingCatalog catalog, ServingContext context, SemanticModel model, CancellationToken cancellationToken) : ISourceExecutor
{
    private readonly Dictionary<string, SourceEngine> _engines = [];

    public bool RunsNativeText => false;

    public QueryRows Execute(CompiledQuery query, int maxRows)
    {
        var kind = model.Catalog.Doc.Sources.TryGetValue(query.Source, out var def) ? def.Kind : "";
        if (kind == "ssas") throw new ServingInvalidException($"Source '{query.Source}' of model '{model.Name}' is Analysis Services, which this server does not run yet.");

        var servingSource = model.ServingSource(query.Source);
        var engine = EngineOf(servingSource);
        var text = NativeRoute.For(engine, query, out var why) ?? throw new ServingInvalidException($"Model '{model.Name}' {why}.");
        var parameters = text.Parameters.Select((value, i) => Parameter(text.Named ? $"p{i + 1}" : null, value)).ToList();
        return ReadAsync(servingSource, text.Text, parameters, maxRows).GetAwaiter().GetResult();
    }

    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) =>
        throw new NotSupportedException("Agent-written native text is not run by the serving module (RunsNativeText is false).");

    private SourceEngine EngineOf(string servingSource)
    {
        if (_engines.TryGetValue(servingSource, out var known)) return known;
        var facts = catalog.FactsAsync(context, servingSource, cancellationToken).GetAwaiter().GetResult();
        var engine = facts.Product switch
        {
            var p when p.Contains("SQL Server", StringComparison.OrdinalIgnoreCase) => SourceEngine.SqlServer,
            var p when p.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase) => SourceEngine.Postgres,
            var p when p.Contains("DuckDB", StringComparison.OrdinalIgnoreCase) => SourceEngine.DuckDb,
            var p => throw new ServingInvalidException($"Source '{servingSource}' is {(p.Length == 0 ? "of an unknown product" : p)}, which the semantic module cannot run queries on."),
        };
        return _engines[servingSource] = engine;
    }

    private async Task<QueryRows> ReadAsync(string servingSource, string sql, IReadOnlyList<ServingParameter> parameters, int maxRows)
    {
        await using var answer = await queries.RunAsync(context, new ServingRequest.Sql(servingSource, sql, parameters), cancellationToken).ConfigureAwait(false);
        var columns = answer.Schema.FieldsList.Select(f => f.Name).ToList();
        var rows = new List<Dictionary<string, object?>>();
        await foreach (var batch in answer.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            for (int r = 0; r < batch.Length && rows.Count < maxRows; r++)
            {
                var row = new Dictionary<string, object?>(columns.Count);
                for (int c = 0; c < columns.Count; c++) row[columns[c]] = Value(batch.Column(c), r);
                rows.Add(row);
            }
            if (rows.Count >= maxRows) break;
        }
        return new QueryRows(columns, rows);
    }

    private static ServingParameter Parameter(string? name, object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            null => new(name, "null", null),
            bool b => new(name, "bool", b ? "true" : "false"),
            long or int or short or byte => new(name, "int64", Convert.ToInt64(value, inv).ToString(inv)),
            double or float => new(name, "double", Convert.ToDouble(value, inv).ToString("R", inv)),
            decimal d => new(name, "decimal", d.ToString(inv)),
            DateOnly d => new(name, "date", d.ToDateTime(TimeOnly.MinValue).ToString("o", inv)),
            DateTime t => new(name, "timestamp", t.ToString("o", inv)),
            _ => new(name, "string", Convert.ToString(value, inv)),
        };
    }

    /// <summary>The value at a row in the forms the engine reads: whole numbers as long, numbers as double or decimal, dates as ISO text.</summary>
    private static object? Value(IArrowArray array, int row)
    {
        if (array.IsNull(row)) return null;
        return array switch
        {
            Int8Array a => (long)a.GetValue(row)!.Value,
            Int16Array a => (long)a.GetValue(row)!.Value,
            Int32Array a => (long)a.GetValue(row)!.Value,
            Int64Array a => a.GetValue(row)!.Value,
            UInt8Array a => (long)a.GetValue(row)!.Value,
            UInt16Array a => (long)a.GetValue(row)!.Value,
            UInt32Array a => (long)a.GetValue(row)!.Value,
            UInt64Array a => a.GetValue(row)!.Value <= long.MaxValue ? (long)a.GetValue(row)!.Value : (decimal)a.GetValue(row)!.Value,
            FloatArray a => (double)a.GetValue(row)!.Value,
            DoubleArray a => a.GetValue(row)!.Value,
            Decimal128Array a => a.GetValue(row)!.Value,
            BooleanArray a => a.GetValue(row)!.Value,
            StringArray a => a.GetString(row),
            LargeStringArray a => a.GetString(row),
            Date32Array a => a.GetDateTime(row)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Date64Array a => a.GetDateTime(row)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimestampArray a => a.GetTimestamp(row)!.Value.UtcDateTime,
            _ => throw new ServingInvalidException($"A source column of Arrow type {array.Data.DataType.Name} cannot be used by a semantic query."),
        };
    }
}
