using System.Globalization;
using Apache.Arrow;
using DbDataKit.Semantic.Abstractions;
using DuckDB.NET.Data;

namespace DbDataKit.Serving.Semantic.Tests;

/// <summary>
/// A host's query service over one in-memory DuckDB database holding the conformance data, reporting itself as DuckDB. It records every request it was given, so a test can see what the module sent and as whom.
/// </summary>
internal sealed class DuckDbHost : IServingQueries, IServingCatalog, IDisposable
{
    private readonly DuckDBConnection _connection = new("Data Source=:memory:");

    public List<(ServingContext Context, ServingRequest.Sql Request)> Received { get; } = [];

    /// <summary>Source names this host refuses, as a host would for a caller without access.</summary>
    public HashSet<string> Denied { get; } = [];

    public DuckDbHost()
    {
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "seed.sql"));
        cmd.ExecuteNonQuery();
    }

    public Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
    {
        var sql = request as ServingRequest.Sql ?? throw new ServingInvalidException($"The host runs SQL, not {request.GetType().Name}.");
        Received.Add((context, sql));
        if (Denied.Contains(sql.Source)) throw new ServingDeniedException($"Source '{sql.Source}' is not available to {context.Caller.Name}.");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql.Text;
        foreach (var p in sql.Parameters ?? [])
            cmd.Parameters.Add(new DuckDBParameter(p.Type switch
            {
                "int64" => long.Parse(p.Text!, CultureInfo.InvariantCulture),
                "double" => double.Parse(p.Text!, CultureInfo.InvariantCulture),
                "decimal" => decimal.Parse(p.Text!, CultureInfo.InvariantCulture),
                "bool" => bool.Parse(p.Text!),
                "null" => DBNull.Value,
                _ => p.Text!,
            }));
        var outcome = QueryRowReader.ReadColumnar(cmd, int.MaxValue);
        var batch = outcome.Columnar?.Batch ?? throw new NotSupportedException("The test host reads only columns Arrow can hold.");
        return Task.FromResult<IServingAnswer>(new BatchAnswer(batch, new ServingProvenance(sql.Text, null, context.Channel, sql.Source)));
    }

    public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) => Task.FromResult(new ServingStatementInfo(0));

    public Task<ServingSourceFacts> FactsAsync(ServingContext context, string source, CancellationToken cancellationToken = default) =>
        Denied.Contains(source) ? throw new ServingDeniedException($"Source '{source}' is not available to {context.Caller.Name}.") : Task.FromResult(new ServingSourceFacts("DuckDB", null, ReadOnly: true));

    public async IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, bool withColumns = false, bool withPrimaryKeys = false, string source = "", [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ServingObject(ServingObjectKind.Table, ["fact_sales"]);
        await Task.CompletedTask;
    }

    public void Dispose() => _connection.Dispose();

    private sealed class BatchAnswer(RecordBatch batch, ServingProvenance provenance) : IServingAnswer
    {
        public Schema Schema => batch.Schema;

        public ServingProvenance Provenance { get; } = provenance;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return batch;
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The core services a module is given, with the test host's query service and catalog; nothing else is offered.</summary>
internal sealed class TestServices(DuckDbHost host) : IServingServices
{
    public IServingIdentity Identity => throw new NotSupportedException();

    public IServingSessions Sessions => throw new NotSupportedException();

    public IServingQueries Queries => host;

    public IServingCatalog Catalog => host;

    public IServingEntities? Entities => null;

    public IServingUpdates? Updates => null;
}
