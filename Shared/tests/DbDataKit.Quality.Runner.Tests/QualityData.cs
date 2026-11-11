using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Connections.DuckDb;
using DbDataKit.Dialect;
using DuckDB.NET.Data;

namespace DbDataKit.Quality.Runner.Tests;

/// <summary>A DuckDB file in a scratch directory with the table <c>orders</c> the checks look at, and the datasets over it as a host would give them (a read-only connection through the provider's recipe).</summary>
public sealed class QualityData : IDisposable
{
    public const string Orders = "duckdb:///orders";

    private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-qrun-").FullName;

    public QualityData()
    {
        File = Path.Combine(_directory, "data.duckdb");
        Execute("""
            CREATE TABLE orders (id INTEGER NOT NULL, customer_id INTEGER NULL, status VARCHAR NULL, loaded_at TIMESTAMP NOT NULL);
            INSERT INTO orders VALUES (1, 10, 'open', TIMESTAMP '2026-11-08 10:00:00'), (2, NULL, 'open', TIMESTAMP '2026-11-08 10:00:00'), (3, 12, 'closed', TIMESTAMP '2026-11-08 10:00:00'),
                (4, NULL, 'open', TIMESTAMP '2026-11-08 10:00:00'), (5, 15, 'open', TIMESTAMP '2026-11-08 10:00:00');
            """);
    }

    public string File { get; }

    public int OpenCount;

    public void Execute(string sql)
    {
        using var connection = new DuckDBConnection($"Data Source={File}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public long Count()
    {
        using var connection = new DuckDBConnection($"Data Source={File}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orders";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public ConnectionDefinition Definition(string? database = null) => new() { Name = "duck", Engine = "duckdb", Address = new AddressSpec { Database = database ?? File } };

    public static CheckDefinition NotNullCustomer(string id = "customer_present", CheckSeverity severity = CheckSeverity.Block, CheckOnError onError = CheckOnError.Alert) =>
        new(id, CheckKind.NotNull, new CheckAttachment("orders", ["customer_id"]), NoParameters.Instance, severity, onError);

    public static CheckDefinition RowCount(string id = "rows") => new(id, CheckKind.RowCountChange, new CheckAttachment("orders", []), new RowCountChangeParameters(50, 50));

    public static CheckDefinition Unique(string id = "id_unique") => new(id, CheckKind.Unique, new CheckAttachment("orders", ["id"]), NoParameters.Instance);

    /// <summary>The dataset <see cref="Orders"/> with these checks, over the file; <paramref name="down"/> points the connection where nothing is.</summary>
    public QualityDataset Dataset(IReadOnlyList<CheckDefinition> checks, bool down = false, IReadOnlyList<string>? keys = null)
    {
        var provider = new DuckDbProvider();
        var definition = Definition(down ? File + ".missing" : null);
        return new QualityDataset(Orders, checks, DialectCatalog.Builtin.Get("duckdb"), token =>
        {
            Interlocked.Increment(ref OpenCount);
            return ReadOnlyConnections.Opener(provider, definition, null)(token);
        }, name => name is "orders" or Orders ? ["orders"] : null, Aliases: ["orders"], KeyColumns: keys ?? ["id"]);
    }

    public IQualitySource Source(IReadOnlyList<CheckDefinition> checks, bool down = false) => new StaticQualitySource([Dataset(checks, down)]);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }
}
