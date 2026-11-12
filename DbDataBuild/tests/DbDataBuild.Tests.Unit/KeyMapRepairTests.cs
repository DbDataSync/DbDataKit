using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;
using DbDataKit.Dialect;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The administrator's repair of a key map (plan 25 K10, owner decision of 2026-11-12): `connection key-map show` and `repair`. The statements are text, so what they touch is tested here for both engines; the engines themselves
/// run the same operations in `KeyMapConformanceTests`.
/// </summary>
public class KeyMapRepairTests
{
    private static readonly KeyMapTables SqlServer = new("sqlserver", "marts", "customer_key");
    private static readonly KeyMapTables Postgres = new("postgres", "marts", "customer_key");

    private static KeyMapRepairScript Script(KeyMapTables t, KeyMapOperation op, long? value = 7, string? text = "SAP\u001f1", bool force = false) =>
        KeyMapRepairScript.Build(t.Engine, t, op, op == KeyMapOperation.Reseed ? null : value, op is KeyMapOperation.Add or KeyMapOperation.SetText ? text : null, force, "Ada via Main console", Guid.Parse("11111111-2222-3333-4444-555555555555"), "bug 123");

    public static TheoryData<string, KeyMapOperation> Cases()
    {
        var data = new TheoryData<string, KeyMapOperation>();
        foreach (var engine in new[] { "sqlserver", "postgres" }) foreach (var op in Enum.GetValues<KeyMapOperation>()) data.Add(engine, op);
        return data;
    }

    [Theory, MemberData(nameof(Cases))]
    public void Every_repair_is_one_transaction_that_ends_with_its_audit_row_and_names_only_the_keys_map_and_log(string engine, KeyMapOperation op)
    {
        var tables = engine == "sqlserver" ? SqlServer : Postgres;
        var text = Script(tables, op).Text;
        var (open, close) = engine == "sqlserver" ? ("BEGIN TRANSACTION;", "COMMIT TRANSACTION;") : ("BEGIN;", "COMMIT;");
        var lines = text.TrimEnd().Split('\n');
        Assert.Equal(open, lines[engine == "sqlserver" ? 1 : 0]);
        Assert.Equal(close, lines[^1]);
        Assert.Single(Regex.Matches(text, Regex.Escape(open)));                                                       // one transaction
        Assert.DoesNotContain("ROLLBACK", text);

        // the audit row is written by the same script, before the commit
        Assert.Contains($"INSERT INTO {tables.Log}", text);
        Assert.True(text.LastIndexOf($"INSERT INTO {tables.Log}", StringComparison.Ordinal) < text.LastIndexOf(close, StringComparison.Ordinal));

        // no table but the map and the log of this key: every name after FROM, INTO, UPDATE, TABLE and JOIN is one of the two
        var named = Regex.Matches(text, @"\b(?:FROM|INTO|UPDATE|TABLE|JOIN)\s+(?:IF NOT EXISTS\s+)?(\[?""?\w+[\]""]?\.\[?""?\w+[\]""]?)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(named);
        Assert.All(named, n => Assert.True(n == tables.Map || n == tables.Log, $"`{n}` is not the map or the log of the key"));
        // and no statement runs over the whole map without its guard or its integer, except the reseed (which touches the sequence only)
        if (op is KeyMapOperation.SetText or KeyMapOperation.Remove) Assert.Matches(op == KeyMapOperation.Remove ? @"DELETE FROM .* WHERE .*key_value.* = @value;" : @"UPDATE .* SET .*key_text.* = @text WHERE .*key_value.* = @value;", text);
    }

    [Theory, MemberData(nameof(Cases))]
    public void The_values_are_bound_never_written_into_the_text(string engine, KeyMapOperation op)
    {
        var tables = engine == "sqlserver" ? SqlServer : Postgres;
        var script = Script(tables, op, 7, "SAP\u001f1'; DROP TABLE x; --");
        Assert.DoesNotContain("DROP TABLE x", script.Text);
        Assert.DoesNotContain("SAP", script.Text);
        Assert.DoesNotContain("Ada", script.Text);
        Assert.DoesNotContain("bug 123", script.Text);
        var names = script.Parameters.Select(p => p.Name).ToList();
        Assert.Contains("by", names); Assert.Contains("run", names); Assert.Contains("reason", names); Assert.Contains("forced", names);
        Assert.Equal(op != KeyMapOperation.Reseed, names.Contains("value"));
        Assert.Equal(op is KeyMapOperation.Add or KeyMapOperation.SetText, names.Contains("text"));
        foreach (var name in names) Assert.Contains("@" + name, script.Text);                    // no parameter is bound for nothing
    }

    [Fact]
    public void The_guards_stop_the_transaction_when_the_map_is_not_as_the_repair_assumed()
    {
        foreach (var tables in new[] { SqlServer, Postgres })
        {
            var add = Script(tables, KeyMapOperation.Add).Text;
            Assert.Contains("= @value));", add);                                              // the integer is free ...
            Assert.Contains("= @text));", add);                                               // ... and so is the business key
            Assert.Contains("SELECT 1 / (1 - (SELECT COUNT(*) FROM", add);
            Assert.Contains("SELECT 1 / (SELECT COUNT(*) FROM", add);                         // read back: the row is there after the insert
            Assert.Contains("SELECT 1 / (SELECT COUNT(*) FROM", Script(tables, KeyMapOperation.Remove).Text);
            var set = Script(tables, KeyMapOperation.SetText).Text;
            Assert.True(set.IndexOf("SELECT 1 / (", StringComparison.Ordinal) < set.IndexOf("UPDATE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void An_added_integer_is_inserted_with_the_engines_own_override_and_the_identity_follows_it()
    {
        var sql = Script(SqlServer, KeyMapOperation.Add).Text;
        Assert.Contains("SET IDENTITY_INSERT [marts].[customer_key_map] ON;", sql);
        Assert.Contains("SET IDENTITY_INSERT [marts].[customer_key_map] OFF;", sql);
        Assert.Contains("SET XACT_ABORT ON;", sql);
        Assert.Contains("WITH (TABLOCKX)", sql);
        var pg = Script(Postgres, KeyMapOperation.Add).Text;
        Assert.Contains("OVERRIDING SYSTEM VALUE", pg);
        Assert.Contains("setval(", pg);                                                      // a sequence does not follow an explicit value by itself
        Assert.Contains("LOCK TABLE \"marts\".\"customer_key_map\" IN SHARE ROW EXCLUSIVE MODE;", pg);
    }

    [Fact]
    public void A_reseed_only_ever_raises_the_identity()
    {
        var sql = Script(SqlServer, KeyMapOperation.Reseed).Text;
        Assert.Contains("IF @max IS NOT NULL AND @last < @max", sql);
        Assert.Contains("DBCC CHECKIDENT", sql);
        var pg = Script(Postgres, KeyMapOperation.Reseed).Text;
        Assert.Equal(2, Regex.Matches(pg, @"WHERE m\.max_value > COALESCE\(pg_sequence_last_value\(s\.seq\), 0\)").Count);
        Assert.True(pg.IndexOf("INSERT INTO", StringComparison.Ordinal) < pg.IndexOf("setval(", StringComparison.Ordinal));      // the log row reads the old value before it moves
    }

    [Fact]
    public void A_fabric_or_duckdb_target_has_no_map_to_repair()
    {
        Assert.Throws<NotSupportedException>(() => KeyMapRepairScript.Build("fabric", new KeyMapTables("fabric", "marts", "customer_key"), KeyMapOperation.Reseed, null, null, false, "a", Guid.Empty, null));
    }

    // ------------------------------------------------------------------ the checks before anything is written

    private static readonly KeyMapState Healthy = new(true, 3, 3, 3);
    private static readonly KeyMapUse NoRows = new("marts.fct_sales", "customer_key", 0, null);

    private static (string? Refusal, string? Nothing) Check(KeyMapOperation op, KeyMapState? state = null, long? value = 2, string? text = "B", string? textOfValue = "A", long? valueOfText = null, IReadOnlyList<KeyMapUse>? uses = null, bool force = false, string engine = "sqlserver") =>
        KeyMapCommand.Check(op, engine, state ?? Healthy, value, text, textOfValue, valueOfText, uses ?? [], force, "customer_key");

    [Fact]
    public void Nothing_is_repaired_in_a_map_that_does_not_exist()
    {
        foreach (var op in Enum.GetValues<KeyMapOperation>()) Assert.Contains("does not exist", Check(op, new KeyMapState(false, 0, null, null)).Refusal);
    }

    [Fact]
    public void An_addition_needs_a_free_integer_a_new_business_key_and_a_value_of_one_or_more()
    {
        Assert.Equal((null, null), Check(KeyMapOperation.Add, value: 9, textOfValue: null));
        Assert.Contains("already belongs to a business key", Check(KeyMapOperation.Add, value: 2, textOfValue: "A").Refusal);
        Assert.Contains("already has the integer 5", Check(KeyMapOperation.Add, value: 9, textOfValue: null, valueOfText: 5).Refusal);
        Assert.Contains("1 or more", Check(KeyMapOperation.Add, value: 0, textOfValue: null).Refusal);
        Assert.Contains("1 or more", Check(KeyMapOperation.Add, value: null, textOfValue: null).Refusal);
        Assert.Contains("--text", Check(KeyMapOperation.Add, value: 9, text: "", textOfValue: null).Refusal);
        Assert.Contains("850", Check(KeyMapOperation.Add, value: 9, text: new string('x', 851), textOfValue: null).Refusal);
        Assert.Null(Check(KeyMapOperation.Add, value: 9, text: new string('x', 851), textOfValue: null, engine: "postgres").Refusal);
    }

    [Fact]
    public void A_change_of_text_needs_a_mapping_that_is_there_and_a_text_that_is_not_another_ones()
    {
        Assert.Equal((null, null), Check(KeyMapOperation.SetText));
        Assert.Contains("No mapping has the integer 2", Check(KeyMapOperation.SetText, textOfValue: null).Refusal);
        Assert.Contains("already has that business key", Check(KeyMapOperation.SetText, text: "A", textOfValue: "A").Nothing);
        Assert.Contains("already has the integer 3", Check(KeyMapOperation.SetText, valueOfText: 3).Refusal);
    }

    [Fact]
    public void A_removal_is_refused_while_a_table_uses_the_integer_and_names_the_tables_unless_forced()
    {
        Assert.Equal((null, null), Check(KeyMapOperation.Remove, uses: [NoRows]));
        Assert.Equal((null, null), Check(KeyMapOperation.Remove, uses: []));
        var refused = Check(KeyMapOperation.Remove, uses: [new KeyMapUse("marts.fct_sales", "customer_key", 12, null), new KeyMapUse("marts.fct_returns", "return_customer_key", 1, null), NoRows]).Refusal!;
        Assert.Contains("marts.fct_sales.customer_key (12 row(s))", refused);
        Assert.Contains("marts.fct_returns.return_customer_key (1 row(s))", refused);
        Assert.DoesNotContain("marts.fct_sales.customer_key (0", refused);
        Assert.Contains("--force", refused);
        Assert.Equal((null, null), Check(KeyMapOperation.Remove, uses: [new KeyMapUse("marts.fct_sales", "customer_key", 12, null)], force: true));
        // a table that could not be counted is not assumed unused
        Assert.Contains("could not be checked", Check(KeyMapOperation.Remove, uses: [new KeyMapUse("etl.big", "customer_key", null, "SqlException (error 229)")]).Refusal);
        Assert.Contains("No mapping has the integer 2", Check(KeyMapOperation.Remove, textOfValue: null).Refusal);
    }

    [Fact]
    public void A_reseed_has_nothing_to_do_when_the_identity_is_already_high_enough_or_the_map_is_empty()
    {
        Assert.Equal((null, null), KeyMapCommand.Check(KeyMapOperation.Reseed, "sqlserver", new KeyMapState(true, 5, 9, 4), null, null, null, null, [], false, "k"));
        Assert.Contains("already at or above", KeyMapCommand.Check(KeyMapOperation.Reseed, "sqlserver", new KeyMapState(true, 5, 9, 9), null, null, null, null, [], false, "k").NothingToDo);
        Assert.Contains("empty", KeyMapCommand.Check(KeyMapOperation.Reseed, "postgres", new KeyMapState(true, 0, null, null), null, null, null, null, [], false, "k").NothingToDo);
        Assert.True(new KeyMapState(true, 5, 9, null).IdentityBelowMaximum);                 // an identity that cannot be read is treated as low
    }

    [Fact]
    public void A_business_key_given_by_parts_is_written_as_the_key_writes_it()
    {
        var key = new MappedKey("customer_key", "marts.dim_customer", "marts", 2, 1);
        Assert.Equal(("SAP\u001f42", null), KeyMapCommand.BusinessKeyText(null, ["  SAP ", "42"], key));
        Assert.Contains("has 2 part(s)", KeyMapCommand.BusinessKeyText(null, ["SAP"], key).Problem);
        Assert.Contains("not both", KeyMapCommand.BusinessKeyText("x", ["a", "b"], key).Problem);
        Assert.Equal(("raw\u001ftext", null), KeyMapCommand.BusinessKeyText("raw\u001ftext", [], key));      // --text is the stored text exactly
        Assert.Equal((null, null), KeyMapCommand.BusinessKeyText(null, [], key));
        Assert.Equal("SAP | 42", KeyMapCommand.Display("SAP\u001f42"));
    }

    // ------------------------------------------------------------------ the project and the command line

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        TestFiles.WriteAllText(full, text);
    }

    private static string Project(string engine = "sqlserver", string method = "key_map")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", $"defaults: {{connections: [{engine}]}}\ntracking: none\nstring_semantics:\n  case: sensitive\n  trailing_space: {(engine == "postgres" ? "significant" : "ignored")}\n  collations:\n    sqlserver: {{case_sensitive: Latin1_General_100_CS_AS}}\n");
        Write(dir, "models/staging/sales.yml", "name: staging.sales\nkind: mapped\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\"}\n  - {name: customer_id, type: BIGINT}\n");
        Write(dir, "models/marts/dim_customer.yml", "name: marts.dim_customer\nkind: full\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\"}\n  - {name: customer_id, type: BIGINT}\n  - {name: customer_key, type: BIGINT}\n"
            + $"keys:\n  customer_key: {{method: {method}, from: [source_system, customer_id]}}\n");
        Write(dir, "models/marts/dim_customer.sql", "SELECT customer_key(s.source_system, s.customer_id) AS customer_key, s.source_system, s.customer_id FROM staging.sales s\n");
        return dir;
    }

    private static (int Exit, string Out, string Err) Run(Func<string, string?>? env, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: env ?? (_ => null)), o.ToString(), e.ToString());
    }

    [Fact]
    public void A_key_is_found_by_its_name_and_only_a_mapped_one_has_a_map()
    {
        var ctx = ProjectContext.Load(Project());
        var (key, problem) = KeyMapCommand.FindKey(ctx, "customer_key", "sqlserver");
        Assert.Null(problem);
        Assert.Equal(new MappedKey("customer_key", "marts.dim_customer", "marts", 2, key!.Line), key);
        Assert.Contains("is not a key of this project that is kept in a map", KeyMapCommand.FindKey(ctx, "nothing_key", "sqlserver").Problem);
        Assert.Contains("customer_key", KeyMapCommand.FindKey(ctx, "nothing_key", "sqlserver").Problem);                 // the ones there are
        Assert.Contains("not built on the connection", KeyMapCommand.FindKey(ctx, "customer_key", "postgres").Problem);
        Assert.Contains("is a hash key", KeyMapCommand.FindKey(ProjectContext.Load(Project(method: "hash")), "customer_key", "sqlserver").Problem);
    }

    [Fact]
    public void The_commands_refuse_what_they_cannot_do_before_they_connect_to_anything()
    {
        var dir = Project();
        var before = Snapshot(dir);
        // no operation
        var none = Run(null, "connection", "key-map", "repair", "customer_key", "--project", dir);
        Assert.Equal(2, none.Exit); Assert.Contains("--operation is required", none.Err);
        // an unknown operation is the parser's
        Assert.NotEqual(0, Run(null, "connection", "key-map", "repair", "customer_key", "--operation", "truncate", "--project", dir).Exit);
        // applying says why, or it does not run
        var noReason = Run(null, "connection", "key-map", "repair", "customer_key", "--operation", "reseed", "--apply", "--project", dir);
        Assert.Equal(2, noReason.Exit); Assert.Contains("--reason is required with --apply", noReason.Err);
        Assert.DoesNotContain("login:", noReason.Out);
        // reseed names nothing; remove names the integer only
        Assert.Equal(2, Run(null, "connection", "key-map", "repair", "customer_key", "--operation", "reseed", "--value", "3", "--project", dir).Exit);
        Assert.Equal(2, Run(null, "connection", "key-map", "repair", "customer_key", "--operation", "remove", "--value", "3", "--text", "x", "--project", dir).Exit);
        // a hash key and an unknown key have no map
        var hash = Project(method: "hash");
        Assert.Contains("is a hash key", Run(null, "connection", "key-map", "show", "customer_key", "--project", hash).Err);
        Assert.Contains("is not a key of this project that is kept in a map", Run(null, "connection", "key-map", "show", "other_key", "--project", dir).Err);
        Assert.Contains("The key is required", Run(null, "connection", "key-map", "show", "--project", dir).Err);
        // the part count is the key's
        var parts = Run(null, "connection", "key-map", "repair", "customer_key", "--operation", "add", "--value", "3", "--part", "SAP", "--project", dir);
        Assert.Equal(2, parts.Exit); Assert.Contains("has 2 part(s)", parts.Err);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void A_dry_run_and_an_apply_both_need_the_read_login_and_an_apply_needs_the_write_login_too_and_nothing_is_written_without_them()
    {
        var dir = Project();
        var dry = Run(null, "connection", "key-map", "repair", "customer_key", "--operation", "reseed", "--project", dir);
        Assert.Equal(1, dry.Exit);
        Assert.Contains("DBDATAKIT_BUILD_SQLSERVER_READ", dry.Err);
        Assert.Contains("write none (not applying)", dry.Out);
        var apply = Run(name => name == "DBDATAKIT_BUILD_SQLSERVER_READ" ? "Server=127.0.0.1,1;User Id=r;Password=x;Database=d;Connect Timeout=1" : null, "connection", "key-map", "repair", "customer_key", "--operation", "reseed", "--reason", "why", "--apply", "--project", dir);
        Assert.Equal(1, apply.Exit);
        Assert.Contains("DBDATAKIT_BUILD_SQLSERVER_WRITE", apply.Err);
        Assert.False(Directory.Exists(Path.Combine(dir, ".dbdatabuild", "statement-log")) && Directory.EnumerateFiles(Path.Combine(dir, ".dbdatabuild", "statement-log")).Any());
    }

    [Fact]
    public void The_commands_are_specified_as_a_read_and_a_target_write_and_an_agent_never_gets_to_read_business_keys_and_a_read_only_surface_has_no_apply()
    {
        var show = CommandSpecs.All.Single(c => c.Name == "connection key-map show");
        var repair = CommandSpecs.All.Single(c => c.Name == "connection key-map repair");
        Assert.Equal(DbDataKit.Cli.EffectClass.TargetReadOnly, show.Effect);
        Assert.Equal(DbDataKit.Cli.EffectClass.TargetWrites, repair.Effect);
        var commands = new TuiCommand.CliHost(_ => null).Commands;
        var info = commands.Single(c => c.Name == "connection key-map repair");
        Assert.Equal("--apply", info.WriteFlag);
        Assert.Equal(Impact.Target, info.Impact);
        // where writes are withheld the tool has no --apply, and an agent never has --show-values
        var surface = new ToolSurface(NewProjectDir(), commands, withholdWriteFlags: true);
        var tool = surface.Describe(info)["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).ToList();
        Assert.DoesNotContain("apply", tool);
        Assert.DoesNotContain("show_values", tool);
        Assert.Contains("operation", tool); Assert.Contains("reason", tool);
        Assert.DoesNotContain("show_values", surface.Describe(commands.Single(c => c.Name == "connection key-map show"))["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key));
    }
}
