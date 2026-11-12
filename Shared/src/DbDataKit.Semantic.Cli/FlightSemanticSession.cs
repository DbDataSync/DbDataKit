using Apache.Arrow;
using Apache.Arrow.Flight.Sql;
using DbDataKit.Connections.FlightSql;

namespace DbDataKit.Semantic.Cli;

/// <summary>
/// A <see cref="ISemanticSession"/> over the shared Flight SQL client, for a product that has no Flight client of its own (plan 14a SL22: Build): a session as the holder of a token on a server that serves semantic models,
/// the statement going to the model's source (<c>semantic:&lt;model&gt;</c>, the <c>x-datasource</c> header) and run as that person. The token is sent as the bearer credential on every call, so a revoked one is refused at the next
/// statement with the server's own message. Nothing here reads the environment: the product that builds the session does, and never from a flag.
/// </summary>
public sealed class FlightSemanticSession(Uri address, string token, bool skipCertificateValidation = false, string? caFile = null) : ISemanticSession
{
    public async Task<SemanticTable> RunAsync(string model, string statement, int maxRows, CancellationToken cancellationToken)
    {
        var settings = new FlightSqlSettings(address, FlightSqlAuthMethod.Bearer, null, token, skipCertificateValidation, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120),
            FlightSqlParameterMode.Inline, DataSource: "semantic:" + model, CaFile: caFile);
        await using var connection = new FlightSqlConnection(settings, _ => throw new NotSupportedException("A semantic statement takes no parameters of its own."));
        await connection.OpenAsync(cancellationToken);
        var options = connection.CallOptions();
        var info = await connection.Client.ExecuteAsync(statement, Transaction.NoTransaction, options, cancellationToken);

        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        var schema = info.Schema;
        foreach (var endpoint in info.Endpoints)
            await foreach (var batch in connection.DoGetAsync(endpoint.Ticket, options, cancellationToken))
            {
                schema ??= batch.Schema;
                if (schema.Metadata is not { Count: > 0 } && batch.Schema.Metadata is { Count: > 0 }) schema = batch.Schema;
                for (int r = 0; r < batch.Length; r++)
                {
                    if (rows.Count >= maxRows) { truncated = true; continue; }
                    rows.Add([.. Enumerable.Range(0, batch.ColumnCount).Select(c => Value(batch.Column(c), r))]);
                }
            }

        var columns = (schema?.FieldsList ?? []).Select(f => new SemanticColumn(f.Name, f.DataType.Name)).ToList();
        var notes = (schema?.Metadata ?? new Dictionary<string, string>()).Where(m => m.Key.StartsWith("qis.", StringComparison.Ordinal) && m.Key != "qis.descriptor").ToDictionary(m => m.Key, m => m.Value);
        return new SemanticTable(columns, rows, truncated, notes);
    }

    private static object? Value(IArrowArray array, int row) => array.IsNull(row) ? null : array switch
    {
        StringArray a => a.GetString(row),
        BooleanArray a => a.GetValue(row),
        Int8Array a => (long)a.GetValue(row)!.Value,
        Int16Array a => (long)a.GetValue(row)!.Value,
        Int32Array a => (long)a.GetValue(row)!.Value,
        Int64Array a => a.GetValue(row),
        FloatArray a => (double)a.GetValue(row)!.Value,
        DoubleArray a => a.GetValue(row),
        Decimal128Array a => a.GetValue(row),
        _ => array.GetType().Name,
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
