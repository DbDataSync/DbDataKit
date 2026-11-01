using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataKit.Dialect;
using DuckDB.NET.Data;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>History keys (plan 25 section 1a, K09): the durable key of a business key, and a version key that changes with the moment a version starts.</summary>
public class HistoryKeyTests
{
    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        TestFiles.WriteAllText(full, text);
    }

    private static string Project(string validFromType = "TIMESTAMP")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", "defaults: {connections: [sqlserver]}\ntracking: none\n");
        Write(dir, "models/marts/dim_product.yml", $"name: marts.dim_product\nkind: view\ncolumns:\n  - {{name: product_id, type: BIGINT}}\n  - {{name: valid_from, type: {validFromType}}}\n  - {{name: color, type: \"VARCHAR(10)\"}}\n  - {{name: product_key, type: BIGINT}}\n  - {{name: product_version_key, type: BIGINT}}\nkeys:\n  product_key: {{from: [product_id], history: valid_from, version_key: product_version_key}}\n");
        Write(dir, "models/marts/dim_product.sql",
            "SELECT product_id, valid_from, color, product_key(product_id) AS product_key, product_version_key(product_id, valid_from) AS product_version_key\n" +
            $"FROM (VALUES (7, CAST('2024-01-01 08:30:00.75' AS {validFromType}), 'red'), (7, CAST('2024-06-01 00:00:00' AS {validFromType}), 'blue'), (8, CAST('2024-01-01 08:30:00.75' AS {validFromType}), 'red')) AS t(product_id, valid_from, color)\n");
        return dir;
    }

    private static List<object?[]> Run(string sql)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        return rows;
    }

    [Fact]
    public void A_new_version_of_a_row_gets_a_new_version_key_and_the_same_durable_key()
    {
        var ctx = ProjectContext.Load(Project());
        Assert.DoesNotContain(ctx.Diagnostics, d => d.Severity == Severity.Error);
        var built = ctx.Project.BuiltModels.Single();
        var (sql, error) = ctx.Lowering.LowerForDuckDb(built, File.ReadAllText(Path.Combine(ctx.Root, built.QueryFile)));
        Assert.Null(error);

        var rows = Run(sql!).Select(r => (Id: Convert.ToInt64(r[0]), From: (DateTime)r[1]!, Durable: Convert.ToInt64(r[3]), Version: Convert.ToInt64(r[4]))).OrderBy(r => r.Id).ThenBy(r => r.From).ToList();
        var (first, second, other) = (rows[0], rows[1], rows[2]);

        Assert.Equal(first.Durable, second.Durable);                                        // one product, two versions
        Assert.NotEqual(first.Version, second.Version);                                     // the second began later
        Assert.NotEqual(first.Durable, other.Durable);                                      // another product
        Assert.Equal(SurrogateKey.Compute(7L), first.Durable);
        Assert.Equal(SurrogateKey.Compute(7L, new DateTime(2024, 1, 1, 8, 30, 0)), first.Version);      // the fraction .75 is cut: the version began at 08:30:00
        Assert.Equal(SurrogateKey.Compute(7L, new DateTime(2024, 6, 1)), second.Version);
        Assert.NotEqual(first.Version, other.Version);                                      // the same moment on another product is another version key
    }

    [Theory]
    [InlineData("sqlserver", "CONVERT(NVARCHAR(19)")]
    [InlineData("postgres", "to_char(")]
    public void The_version_key_writes_the_moment_the_same_way_on_each_engine(string engine, string expected)
    {
        var dir = Project();
        TestFiles.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{engine}]}}\ntracking: none\nstring_semantics:\n  case: sensitive\n  trailing_space: {(engine == "postgres" ? "significant" : "ignored")}\n  collations:\n    default: {{ duckdb: NFC, {engine}: {(engine == "postgres" ? "en_US.utf8" : "Latin1_General_100_CS_AS_SC_UTF8")} }}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/dim_product.yml"), File.ReadAllText(Path.Combine(dir, "models/marts/dim_product.yml")).Replace("kind: view", "kind: full"));
        var o = new StringWriter(); var e = new StringWriter();

        var exit = CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null);

        Assert.True(exit == 0, o.ToString() + e);
        var script = File.ReadAllText(Path.Combine(dir, "rendered", engine, "marts.dim_product", "load.default.sql"));
        Assert.Contains(expected, script);
    }

    [Fact]
    public void A_date_valid_from_still_works_as_a_date()
    {
        var ctx = ProjectContext.Load(Project("DATE"));
        var built = ctx.Project.BuiltModels.Single();
        var (sql, error) = ctx.Lowering.LowerForDuckDb(built, File.ReadAllText(Path.Combine(ctx.Root, built.QueryFile)));
        Assert.Null(error);

        var first = Run(sql!).OrderBy(r => Convert.ToInt64(r[0])).ThenBy(r => r[1]).First();

        Assert.Equal(SurrogateKey.Compute(7L, new DateOnly(2024, 1, 1)), Convert.ToInt64(first[4]));
    }
}
