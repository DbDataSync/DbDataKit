using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataKit.Dialect;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A key kept in a map in the target (plan 25 K10, `method: key_map`): one table per key, an integer per business key, new ones added by the load before it reads the map.</summary>
public class KeyMapTests
{
    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        TestFiles.WriteAllText(full, text);
    }

    private const string Columns = "columns:\n  - {name: source_system, type: \"VARCHAR(10)\"}\n  - {name: customer_id, type: BIGINT}\n  - {name: customer_key, type: BIGINT}\n";

    private static string Project(string engine, string factQuery = "SELECT customer_key(source_system, customer_id) AS customer_key FROM staging.sales\n", string keyMethod = "method: key_map, ")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", $"defaults: {{connections: [{engine}]}}\ntracking: none\nstring_semantics:\n  case: sensitive\n  trailing_space: {(engine == "postgres" ? "significant" : "ignored")}\n  collations:\n    default: {{ duckdb: NFC, {engine}: {(engine == "postgres" ? "en_US.utf8" : "Latin1_General_100_CS_AS_SC_UTF8")} }}\n");
        Write(dir, "models/staging/sales.yml", "name: staging.sales\nkind: mapped\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\"}\n  - {name: customer_id, type: BIGINT}\n");
        Write(dir, "models/marts/dim_customer.yml", "name: marts.dim_customer\nkind: full\n" + Columns + $"keys:\n  customer_key: {{{keyMethod}from: [source_system, customer_id]}}\n");
        Write(dir, "models/marts/dim_customer.sql", "SELECT customer_key(s.source_system, s.customer_id) AS customer_key, s.source_system, s.customer_id FROM staging.sales s\n");
        Write(dir, "models/marts/fct_sales.yml", "name: marts.fct_sales\nkind: full\ncolumns:\n  - {name: customer_key, type: BIGINT}\n");
        Write(dir, "models/marts/fct_sales.sql", factQuery);
        return dir;
    }

    private static (int Exit, string Err) Compile(string dir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null), e.ToString());
    }

    private static string Script(string dir, string engine, string model) => File.ReadAllText(Path.Combine(dir, "rendered", engine, model, "load.default.sql"));

    [Fact]
    public void The_marker_names_round_trip_and_the_map_is_one_table_per_key_in_the_owners_schema()
    {
        var name = SurrogateKey.KeyMapMarkerName([KeyPartType.Text, KeyPartType.Integer], "customer_key");
        Assert.Equal("ddb_keymap_ti_customer_key", name);
        var parsed = SurrogateKey.ParseKeyMapMarker(name)!.Value;
        Assert.Equal([KeyPartType.Text, KeyPartType.Integer], parsed.Types);
        Assert.Equal(("customer_key", false), (parsed.KeyName, parsed.AsText));
        Assert.True(SurrogateKey.ParseKeyMapMarker("ddb_keytext_d_dim_date")!.Value.AsText);
        Assert.Null(SurrogateKey.ParseKeyMapMarker("ddb_key_ti"));
        Assert.Equal(("marts", "customer_key_map"), SurrogateKey.KeyMapTable("marts.dim_customer", "customer_key"));
    }

    [Theory]
    [InlineData("sqlserver", "IF OBJECT_ID(N'[marts].[customer_key_map]', N'U') IS NULL", "IGNORE_DUP_KEY = ON", "COLLATE Latin1_General_100_BIN2")]
    [InlineData("postgres", "CREATE TABLE IF NOT EXISTS \"marts\".\"customer_key_map\"", "GENERATED ALWAYS AS IDENTITY", "ON CONFLICT (\"key_text\") DO NOTHING")]
    public void A_load_that_calls_a_mapped_key_creates_the_map_adds_the_new_business_keys_then_reads_it(string engine, string create, string identity, string insert)
    {
        var dir = Project(engine);
        var (exit, err) = Compile(dir);
        Assert.True(exit == 0, err);
        foreach (var model in new[] { "marts.dim_customer", "marts.fct_sales" })
        {
            var script = Script(dir, engine, model);
            Assert.Contains(create, script);
            Assert.Contains(identity, script);
            Assert.Contains(insert, script);
            Assert.Contains("key_map", script);
            Assert.DoesNotContain("ddb_key", script);
            // the map is filled before the load's own statements read it
            Assert.True(script.IndexOf("customer_key_map", StringComparison.Ordinal) < script.IndexOf(engine == "postgres" ? "CREATE TEMP TABLE" : "INTO #ddb_stage", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("HASHBYTES", Script(dir, engine, "marts.dim_customer"));
    }

    [Fact]
    public void A_call_that_is_not_a_selected_column_is_refused_because_there_is_no_column_to_add_the_keys_from()
    {
        var dir = Project("sqlserver", "SELECT 1 AS one FROM staging.sales s WHERE customer_key(s.source_system, s.customer_id) > 0\n");
        var (exit, err) = Compile(dir);
        Assert.NotEqual(0, exit);
        Assert.Contains("is selected as a column", err);
    }

    [Fact]
    public void A_query_that_calls_a_mapped_key_cannot_run_on_duckdb()
    {
        var dir = Project("sqlserver");
        var ctx = ProjectContext.Load(dir);
        var built = ctx.Project.BuiltModels.Single(m => m.Definition.Name == "marts.fct_sales");
        var (sql, error) = ctx.Lowering.LowerForDuckDb(built, File.ReadAllText(Path.Combine(ctx.Root, built.QueryFile)));
        Assert.Null(sql);
        Assert.Contains("method: key_map", error);
    }

    [Fact]
    public void The_text_of_a_business_key_is_the_text_the_hash_is_taken_over()
    {
        var parts = new[] { new KeyPart("[a]", KeyPartType.Text), new KeyPart("[b]", KeyPartType.Integer) };
        Assert.Contains("NCHAR(31)", SurrogateKey.TextSql("sqlserver", parts));
        Assert.Contains("chr(31)", SurrogateKey.TextSql("postgres", [new("a", KeyPartType.Text), new("b", KeyPartType.Date)]));
        Assert.Throws<NotSupportedException>(() => SurrogateKey.TextSql("fabric", parts));
    }
}
