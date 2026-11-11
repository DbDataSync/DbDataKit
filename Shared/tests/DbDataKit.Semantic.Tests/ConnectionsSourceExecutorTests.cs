using DbDataKit.Connections;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Connections;

namespace DbDataKit.Semantic.Tests;

public sealed class ConnectionsSourceExecutorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dbdatakit-semantic-exec-");
    private readonly string _file;

    public ConnectionsSourceExecutorTests()
    {
        _file = Path.Combine(_dir.FullName, "t.duckdb");
        using var con = new DuckDB.NET.Data.DuckDBConnection($"DataSource={_file}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (a INTEGER, b VARCHAR); INSERT INTO t VALUES (1, 'x'), (2, 'y'), (3, 'z')";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { try { _dir.Delete(true); } catch (IOException) { } }

    private ConnectionsSourceExecutor Executor(ISourceExecutor? fallback = null) =>
        new(new Dictionary<string, SemanticSource> { ["duck"] = ConnectionContexts.DuckDb(_file) }, fallback);

    private static CompiledQuery Query(string source, string duckSql, string dialect = "tsql", params object?[] parameters) =>
        new(source, new NativeQuery(dialect, duckSql), duckSql, [.. parameters]);

    [Fact]
    public void RunsTheDuckDbTwinWithPositionalParametersAndStopsAtMaxRows()
    {
        var rows = Executor().Execute(Query("duck", "SELECT a, b FROM t WHERE a >= ? ORDER BY a", "tsql", 2m), 10);
        Assert.Equal(["a", "b"], rows.Columns);
        Assert.Equal([2, 3], rows.Rows.Select(r => Convert.ToInt32(r["a"])));
        Assert.Single(Executor().Execute(Query("duck", "SELECT a FROM t ORDER BY a"), 1).Rows);
    }

    [Fact]
    public void ADriversFailureComesBackAsAFailureWithoutItsText()
    {
        var e = Assert.Throws<ConnectionFailureException>(() => Executor().Execute(Query("duck", "SELEC nonsense"), 10));
        Assert.Equal(ConnectionFailureKind.Syntax, e.Failure.Kind);
        Assert.DoesNotContain("nonsense", e.Message);
    }

    [Fact]
    public void TheReadOnlySandboxRefusesAWrite()
    {
        var e = Assert.Throws<ConnectionFailureException>(() => Executor().Execute(Query("duck", "DELETE FROM t"), 10));
        Assert.Equal(ConnectionFailureKind.Permission, e.Failure.Kind);
    }

    [Fact]
    public void ASourceWithoutAConnectionGoesToTheFallbackOrFailsNamingIt()
    {
        var rows = new QueryRows(["x"], [new() { ["x"] = 1 }]);
        Assert.Same(rows, Executor(new Fallback(rows)).Execute(Query("ssas", "SELECT 1"), 10));
        var e = Assert.Throws<NotSupportedException>(() => Executor().Execute(Query("ssas", "SELECT 1"), 10));
        Assert.Contains("'ssas'", e.Message);
    }

    [Fact]
    public void NativeTextIsNotRun()
    {
        var executor = Executor();
        Assert.False(executor.RunsNativeText);
        Assert.Throws<NotSupportedException>(() => executor.ExecuteNativeText("duck", new NativeQuery("tsql", "SELECT 1"), 10));
    }

    private sealed class Fallback(QueryRows rows) : ISourceExecutor
    {
        public bool RunsNativeText => false;
        public QueryRows Execute(CompiledQuery query, int maxRows) => rows;
        public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) => throw new NotSupportedException();
    }
}
