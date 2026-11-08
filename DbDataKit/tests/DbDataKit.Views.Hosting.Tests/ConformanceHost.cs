using System.Globalization;
using Apache.Arrow;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Serving;
using DuckDB.NET.Data;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// A host's query service over one in-memory DuckDB database holding the semantic engine's conformance data, with an access rule of its own (the serving core's rows rules are deferred, plan 22): a caller whose claims say
/// <c>region: West</c> reads only the customers of the West and their orders, so every order of another region is out of their sums, their tables and their control's members. It records each SQL request with its caller and lets a test hold one.
/// </summary>
internal sealed class ConformanceHost : IServingQueries, IDisposable
{
    private readonly DuckDBConnection connection = new("Data Source=:memory:");
    private readonly object gate = new();

    public List<(ServingContext Context, ServingRequest.Sql Request)> Received { get; } = [];

    /// <summary>What the database said when it refused a statement (the module shows only the kind of an unexpected failure).</summary>
    public List<string> Failures { get; } = [];

    /// <summary>Called first with each request: a test holds, denies or fails it here.</summary>
    public Func<ServingContext, ServingRequest.Sql, Task>? Before { get; set; }

    public ConformanceHost()
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "seed.sql"));
        command.ExecuteNonQuery();
        // the customers are read through a view per caller (see RunAsync), so the tables are kept under other names
        Execute("ALTER TABLE dim_customer RENAME TO base_dim_customer");
        Execute("ALTER TABLE fact_sales RENAME TO base_fact_sales");
    }

    public async Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
    {
        var sql = request as ServingRequest.Sql ?? throw new ServingInvalidException($"The host runs SQL, not {request.GetType().Name}.");
        lock (Received) Received.Add((context, sql));
        if (Before is not null) await Before(context, sql).ConfigureAwait(false);
        try
        {
        lock (gate)
        {
            // the rule: the caller's region narrows the customers they can see; a temporary view shadows the table for this call (the connection is shared, so the whole call holds the lock)
            var rule = context.Caller.Claims.TryGetValue("region", out var region) ? $" WHERE region = '{region.Replace("'", "''", StringComparison.Ordinal)}'" : "";
            Execute("CREATE OR REPLACE TEMP VIEW dim_customer AS SELECT * FROM base_dim_customer" + rule);
            // the orders follow their customers: a sum over the facts alone is the caller's too
            Execute("CREATE OR REPLACE TEMP VIEW fact_sales AS SELECT * FROM base_fact_sales WHERE customer_key IN (SELECT customer_key FROM base_dim_customer" + rule + ")");
            using var command = connection.CreateCommand();
            command.CommandText = sql.Text;
            foreach (var p in sql.Parameters ?? [])
                command.Parameters.Add(new DuckDBParameter(p.Type switch
                {
                    "int64" => long.Parse(p.Text!, CultureInfo.InvariantCulture),
                    "double" => double.Parse(p.Text!, CultureInfo.InvariantCulture),
                    "decimal" => decimal.Parse(p.Text!, CultureInfo.InvariantCulture),
                    "bool" => bool.Parse(p.Text!),
                    "null" => DBNull.Value,
                    _ => p.Text!,
                }));
            var outcome = QueryRowReader.ReadColumnar(command, int.MaxValue);
            var batch = outcome.Columnar?.Batch ?? throw new NotSupportedException("The test host reads only columns Arrow can hold.");
            return new Answer(batch, new ServingProvenance(sql.Text, null, context.Channel, sql.Source));
        }
        }
        catch (DuckDBException e)
        {
            lock (Failures) Failures.Add(e.Message + "\n" + sql.Text);
            throw;
        }
    }

    private void Execute(string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) => Task.FromResult(new ServingStatementInfo(0));

    public void Dispose() => connection.Dispose();

    private sealed class Answer(RecordBatch batch, ServingProvenance provenance) : IServingAnswer
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

/// <summary>The core services a module is given: the test host's query service; nothing else is offered.</summary>
internal sealed class HostedServices(IServingQueries queries) : IServingServices
{
    public IServingIdentity Identity => throw new NotSupportedException();

    public IServingSessions Sessions => throw new NotSupportedException();

    public IServingQueries Queries => queries;

    public IServingCatalog Catalog { get; } = new NoCatalog();

    public IServingEntities? Entities => null;

    public IServingUpdates? Updates => null;
}

/// <summary>A host with nothing to list: the module asks the host for no catalog, the semantic wrapper only holds it.</summary>
internal sealed class NoCatalog : IServingCatalog
{
    public Task<ServingSourceFacts> FactsAsync(ServingContext context, string source, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServingSourceFacts("DuckDB", null, ReadOnly: true));

    public async IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, bool withColumns = false, bool withPrimaryKeys = false, string source = "", [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
