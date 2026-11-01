using DbDataBuild.Cli;
using DbDataBuild.Models;
using DbDataBuild.Semantic;
using DbDataKit.Dialect;
using DuckDB.NET.Data;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The collision guard of a hashed key (plan 25, K06): the model that owns the key is tested for having each value on one row.</summary>
public class KeyChecksTests
{
    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        TestFiles.WriteAllText(full, text);
    }

    private static string Project(string keys, string columns = "  - {name: source_system, type: \"VARCHAR(10)\"}\n  - {name: customer_id, type: BIGINT}\n  - {name: customer_key, type: BIGINT}\n")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", "defaults: {connections: [sqlserver, postgres]}\ntracking: none\nstring_semantics:\n  case: insensitive\n  accent: sensitive\n  trailing_space: ignored\n");
        Write(dir, "models/marts/dim_customer.yml", "name: marts.dim_customer\nkind: view\ncolumns:\n" + columns + keys);
        Write(dir, "models/marts/dim_customer.sql", "SELECT 'SAP' AS source_system, 42 AS customer_id, customer_key('SAP', 42) AS customer_key\n");
        return dir;
    }

    private static IReadOnlyList<SemanticCheck> ChecksOf(string dir)
    {
        var ctx = ProjectContext.Load(dir);
        return KeyChecks.Generate(ProjectModels.Of(ctx.Project, ctx.Config.DefaultConnections), ctx.Config);
    }

    [Fact]
    public void An_owned_key_is_tested_on_every_connection_the_model_is_on()
    {
        var checks = ChecksOf(Project("keys:\n  customer_key: {from: [source_system, customer_id]}\n"));

        Assert.Equal(["model.marts.dim_customer.key.customer_key.unique@postgres", "model.marts.dim_customer.key.customer_key.unique@sqlserver"], checks.Select(c => c.Name).Order());
        var sqlServer = checks.Single(c => c.Connection == "sqlserver");
        Assert.Equal("SELECT [customer_key] AS [key], COUNT(*) AS [row_count] FROM [marts].[dim_customer] WHERE [customer_key] IS NOT NULL GROUP BY [customer_key] HAVING COUNT(*) > 1", sqlServer.Sql);
        Assert.Contains("two business keys whose hashes collided", sqlServer.Description);
        Assert.Equal("models/marts/dim_customer.yml", sqlServer.File);
    }

    [Fact]
    public void A_key_with_history_is_tested_through_its_version_key_since_the_durable_key_repeats_by_design()
    {
        var dir = Project("keys:\n  customer_key: {from: [customer_id], history: valid_from, version_key: customer_version_key}\n",
            "  - {name: customer_id, type: BIGINT}\n  - {name: valid_from, type: DATE}\n  - {name: customer_key, type: BIGINT}\n  - {name: customer_version_key, type: BIGINT}\n");

        var checks = ChecksOf(dir);

        Assert.All(checks, c => Assert.Contains(".key.customer_version_key.unique@", c.Name));
        Assert.DoesNotContain(checks, c => c.Name.Contains(".key.customer_key."));
    }

    [Fact]
    public void A_key_the_model_does_not_hold_as_a_column_has_nothing_to_count()
    {
        Assert.Empty(ChecksOf(Project("keys:\n  customer_key: {from: [source_system, customer_id]}\n", "  - {name: customer_id, type: BIGINT}\n")));
    }

    [Fact]
    public void Two_rows_with_one_key_value_are_returned_and_a_missing_member_is_not_a_repeat()
    {
        var sql = KeyChecks.UniquenessSql(DialectCatalog.For(new EngineId("duckdb")), "marts.dim_customer", "customer_key");
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        Run(connection, "CREATE SCHEMA marts");
        Run(connection, "CREATE TABLE marts.dim_customer (customer_key BIGINT)");
        Run(connection, "INSERT INTO marts.dim_customer VALUES (1), (2), (NULL), (NULL)");
        Assert.Empty(Rows(connection, sql));

        Run(connection, "INSERT INTO marts.dim_customer VALUES (2)");      // a hash forced to collide
        var repeated = Assert.Single(Rows(connection, sql));
        Assert.Equal((2L, 2L), repeated);
    }

    [Fact]
    public void The_checks_are_listed_with_the_other_generated_tests()
    {
        var dir = Project("keys:\n  customer_key: {from: [source_system, customer_id]}\n");
        var o = new StringWriter(); var e = new StringWriter();

        var exit = CliApp.Run(["project", "tests", "list", "--kind", "semantic", "--project", dir], o, e, environment: _ => null);

        Assert.True(exit == 0, o.ToString() + e);
        Assert.Contains("model.marts.dim_customer.key.customer_key.unique@sqlserver", o.ToString());
    }

    private static void Run(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<(long Key, long Rows)> Rows(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<(long, long)>();
        while (reader.Read()) rows.Add((Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1))));
        return rows;
    }
}
