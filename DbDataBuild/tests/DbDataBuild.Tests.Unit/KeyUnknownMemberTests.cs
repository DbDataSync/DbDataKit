using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataKit.Dialect;
using DuckDB.NET.Data;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`unknown_member: true` (plan 25 section 1a, K08): a key called with a NULL part gives the reserved key, and the owning model holds the row it stands for.</summary>
public class KeyUnknownMemberTests
{
    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        TestFiles.WriteAllText(full, text);
    }

    private static string Project(string key, string kind = "full")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", "defaults: {connections: [sqlserver]}\ntracking: none\n");
        Write(dir, "models/marts/dim_customer.yml", $"name: marts.dim_customer\nkind: {kind}\ngrain: [customer_key]\ncolumns:\n  - {{name: customer_key, type: BIGINT, nullable: false}}\n  - {{name: source_system, type: \"VARCHAR(10)\", nullable: false}}\n  - {{name: customer_id, type: BIGINT}}\n  - {{name: born, type: DATE, nullable: false}}\nkeys:\n  customer_key: {key}\n");
        Write(dir, "models/marts/dim_customer.sql", "SELECT customer_key(source_system, customer_id) AS customer_key, source_system, customer_id, DATE '2000-01-02' AS born\nFROM (VALUES ('SAP', 42), ('SAP', 43)) AS t(source_system, customer_id)\n");
        Write(dir, "models/marts/fct_sales.yml", "name: marts.fct_sales\nkind: view\ncolumns:\n  - {name: customer_key, type: BIGINT}\n");
        Write(dir, "models/marts/fct_sales.sql", "SELECT customer_key(CAST(NULL AS VARCHAR), 7) AS customer_key\n");
        return dir;
    }

    private const string Unknown = "{from: [source_system, customer_id], unknown_member: true}";

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

    private static string DuckDbQuery(ProjectContext ctx, string model)
    {
        var built = ctx.Project.BuiltModels.Single(m => m.Definition.Name == model);
        var (sql, error) = ctx.Lowering.LowerForDuckDb(built, File.ReadAllText(Path.Combine(ctx.Root, built.QueryFile)));
        Assert.Null(error);
        return sql!;
    }

    [Fact]
    public void A_null_part_gives_the_reserved_key_and_without_the_setting_it_still_gives_null()
    {
        var ctx = ProjectContext.Load(Project(Unknown));
        Assert.DoesNotContain(ctx.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(SurrogateKey.UnknownKey, Convert.ToInt64(Assert.Single(Run(DuckDbQuery(ctx, "marts.fct_sales")))[0]));

        var plain = ProjectContext.Load(Project("{from: [source_system, customer_id]}"));
        Assert.Null(Assert.Single(Run(DuckDbQuery(plain, "marts.fct_sales")))[0]);
    }

    [Fact]
    public void The_owning_model_holds_the_unknown_row_beside_its_own_with_plain_values_where_a_column_cannot_be_null()
    {
        var ctx = ProjectContext.Load(Project(Unknown));

        var rows = Run(DuckDbQuery(ctx, "marts.dim_customer")).OrderBy(r => Convert.ToInt64(r[0]) != 0).ToList();      // the unknown row first

        Assert.Equal(3, rows.Count);
        var unknown = rows[0];                                                           // customer_key, source_system, customer_id, born
        Assert.Equal(0L, Convert.ToInt64(unknown[0]));
        Assert.Equal("Unknown", unknown[1]);                                             // NOT NULL text
        Assert.Null(unknown[2]);                                                         // may be NULL
        Assert.Equal(new DateOnly(1900, 1, 1), unknown[3]);           // NOT NULL date
        Assert.Equal(SurrogateKey.Compute("SAP", 42L), Convert.ToInt64(rows.Single(r => Equals(r[2], 42L)) [0]));     // the model's own rows are as they were
    }

    [Fact]
    public void A_model_without_the_setting_gets_no_extra_row()
    {
        var ctx = ProjectContext.Load(Project("{from: [source_system, customer_id]}"));

        Assert.Equal(2, Run(DuckDbQuery(ctx, "marts.dim_customer")).Count);
    }

    [Fact]
    public void The_rendered_load_of_each_model_carries_what_it_needs_on_the_engine()
    {
        var dir = Project(Unknown);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_sales.sql"), "SELECT customer_key(s, 7) AS customer_key FROM (SELECT CAST(NULL AS VARCHAR) AS s UNION ALL SELECT 'SAP') AS t\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_sales.yml"), File.ReadAllText(Path.Combine(dir, "models/marts/fct_sales.yml")).Replace("kind: view", "kind: full"));
        var o = new StringWriter(); var e = new StringWriter();

        var exit = CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null);

        Assert.True(exit == 0, o.ToString() + e);
        var fact = File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "marts.fct_sales", "load.default.sql"));
        Assert.True(fact.Contains("COALESCE(") || fact.Contains("ISNULL("), fact);                  // the reserved key where a part is NULL
        var dimension = File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "marts.dim_customer", "load.default.sql"));
        Assert.Contains("'Unknown'", dimension);                                           // the unknown row, in the load of the owner
        Assert.Contains("UNION ALL", dimension);
    }

    [Fact]
    public void A_kind_that_loads_by_period_cannot_hold_the_row_and_the_definition_says_so()
    {
        var dir = Project(Unknown);
        File.WriteAllText(Path.Combine(dir, "models/marts/dim_customer.yml"), File.ReadAllText(Path.Combine(dir, "models/marts/dim_customer.yml"))
            .Replace("kind: full", "kind: {incremental_by_time_range: {time_column: born}}"));

        var ctx = ProjectContext.Load(dir);

        Assert.Contains(ctx.Diagnostics, d => d.Severity == Severity.Error && d.Found.Contains("unknown_member: true") && d.Location.File.EndsWith("dim_customer.yml"));
    }
}
