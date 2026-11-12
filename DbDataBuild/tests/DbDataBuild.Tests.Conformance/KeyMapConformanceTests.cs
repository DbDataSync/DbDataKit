using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataKit.Transfer;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 25 K10 on the real engines: a key kept in a map (`method: key_map`) gets an integer per business key from a table of the target, new business keys are added by the load that reads them, a key never changes or is
/// given to another business key, and two business keys that differ only in case are two keys.
/// </summary>
public partial class ApplyConformanceTests
{
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_key_kept_in_a_map_is_stable_across_reloads_never_reused_and_shared_by_the_models_that_call_it(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            var text = engine.ColumnType("VARCHAR(10)");
            await engine.ExecAsync($"CREATE TABLE staging.customers ({run.Q("source_system")} {text} NOT NULL, {run.Q("customer_id")} BIGINT NOT NULL)");
            await engine.ExecAsync($"CREATE TABLE staging.sales ({run.Q("source_system")} {text} NOT NULL, {run.Q("customer_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
            await engine.ExecAsync("INSERT INTO staging.customers VALUES ('SAP', 1), ('SAP', 2), ('sap', 1)");
            await engine.ExecAsync("INSERT INTO staging.sales VALUES ('SAP', 2, 5.00), ('sap', 1, 7.00), ('SAP', 9, 1.00)");
            run.Write("models/staging/customers.yml", "name: staging.customers\nkind: mapped\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n");
            run.Write("models/staging/sales.yml", "name: staging.sales\nkind: mapped\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
            run.Write("models/marts/dim_customer.yml", "name: marts.dim_customer\nkind: full\ncolumns:\n  - {name: customer_key, type: BIGINT}\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n"
                + "keys:\n  customer_key: {from: [source_system, customer_id], method: key_map}\n");
            run.Write("models/marts/dim_customer.sql", "SELECT customer_key(c.source_system, c.customer_id) AS customer_key, c.source_system, c.customer_id FROM staging.customers c\n");
            run.Write("models/marts/fct_sales.yml", "name: marts.fct_sales\nkind: full\ncolumns:\n  - {name: customer_key, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
            run.Write("models/marts/fct_sales.sql", "SELECT customer_key(s.source_system, s.customer_id) AS customer_key, s.amount FROM staging.sales s\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            var compile = run.Cli("project", "compile"); Ok(compile, "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");

            var map = $"{run.Q("marts")}.{run.Q("customer_key_map")}";
            var dim = $"{run.Q("marts")}.{run.Q("dim_customer")}";
            var fct = $"{run.Q("marts")}.{run.Q("fct_sales")}";
            async Task<Dictionary<string, long>> Map() => (await engine.RowsAsync($"SELECT {run.Q("key_text")}, {run.Q("key_value")} FROM {map}"))
                .Select(r => r.Split('|')).ToDictionary(p => p[0].Replace("\u001f", "|"), p => long.Parse(p[1]));
            async Task<Dictionary<string, long>> Dim() => (await engine.RowsAsync($"SELECT {run.Q("source_system")}, {run.Q("customer_id")}, {run.Q("customer_key")} FROM {dim}"))
                .Select(r => r.Split('|')).ToDictionary(p => p[0] + ":" + p[1], p => long.Parse(p[2]));

            Ok(run.Cli("connection", "refresh", name), "first refresh");
            var first = await Map();
            Assert.Equal(4, first.Count);                                                       // SAP:1, SAP:2 and sap:1 are three business keys (a key map compares the text exactly), and the fact's SAP:9 is a fourth
            Assert.Equal(4, first.Values.Distinct().Count());
            var dim1 = await Dim();
            Assert.Equal(3, dim1.Values.Distinct().Count());
            Assert.Subset(first.Values.ToHashSet(), dim1.Values.ToHashSet());                    // the dimension holds the keys the map gave
            // the fact called the key for a business key the dimension has (SAP:2, sap:1) and one it has not (SAP:9): the load added it, so no fact row is left without a key
            var fact1 = (await engine.RowsAsync($"SELECT {run.Q("customer_key")}, {run.Q("amount")} FROM {fct}")).Select(r => r.Split('|')).ToDictionary(p => decimal.Parse(p[1]), p => long.Parse(p[0]));
            Assert.Equal(dim1["SAP:2"], fact1[5.00m]);
            Assert.Equal(dim1["sap:1"], fact1[7.00m]);
            Assert.DoesNotContain(fact1[1.00m], dim1.Values);

            // a second refresh changes nothing: a business key keeps its integer
            Ok(run.Cli("connection", "refresh", name), "second refresh");
            Assert.Equal(first, await Map());
            Assert.Equal(dim1, await Dim());

            // a business key that goes away keeps its row in the map; a new one gets a larger integer; the old one coming back gets its own integer again
            var gone = dim1["SAP:1"];
            await engine.ExecAsync("DELETE FROM staging.customers WHERE source_system = 'SAP' AND customer_id = 1");
            await engine.ExecAsync("INSERT INTO staging.customers VALUES ('SAP', 3)");
            Ok(run.Cli("connection", "refresh", name), "refresh after a change");
            var dim2 = await Dim();
            Assert.DoesNotContain("SAP:1", dim2.Keys);
            Assert.True(dim2["SAP:3"] > dim1.Values.Max(), "a new business key gets a new integer, never one given before");
            Assert.Contains(gone, (await Map()).Values);
            await engine.ExecAsync("INSERT INTO staging.customers VALUES ('SAP', 1)");
            Ok(run.Cli("connection", "refresh", name), "refresh after it came back");
            Assert.Equal(gone, (await Dim())["SAP:1"]);
        }
        finally
        {
            if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true);
        }
    }

    /// <summary>
    /// The administrator's repair (plan 25 K10, owner decision of 2026-11-12) on the real engines: `show` reads the map; a dry run changes nothing and creates nothing; `--apply` makes one operation and writes its audit row in the same
    /// transaction; an addition is followed by the identity, a used integer is not removed without `--force`, a reseed raises a lowered identity, and a repair whose guard fails rolls back with the log untouched.
    /// </summary>
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_administrator_repairs_a_key_map_with_a_dry_run_first_and_every_applied_repair_is_recorded_with_who_and_why(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            var text = engine.ColumnType("VARCHAR(10)");
            await engine.ExecAsync($"CREATE TABLE staging.customers ({run.Q("source_system")} {text} NOT NULL, {run.Q("customer_id")} BIGINT NOT NULL)");
            await engine.ExecAsync($"CREATE TABLE staging.sales ({run.Q("source_system")} {text} NOT NULL, {run.Q("customer_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
            await engine.ExecAsync("INSERT INTO staging.customers VALUES ('SAP', 1), ('SAP', 2)");
            await engine.ExecAsync("INSERT INTO staging.sales VALUES ('SAP', 2, 5.00), ('SAP', 9, 1.00)");
            run.Write("models/staging/customers.yml", "name: staging.customers\nkind: mapped\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n");
            run.Write("models/staging/sales.yml", "name: staging.sales\nkind: mapped\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
            run.Write("models/marts/dim_customer.yml", "name: marts.dim_customer\nkind: full\ncolumns:\n  - {name: customer_key, type: BIGINT}\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n"
                + "keys:\n  customer_key: {from: [source_system, customer_id], method: key_map}\n");
            run.Write("models/marts/dim_customer.sql", "SELECT customer_key(c.source_system, c.customer_id) AS customer_key, c.source_system, c.customer_id FROM staging.customers c\n");
            run.Write("models/marts/fct_sales.yml", "name: marts.fct_sales\nkind: full\ncolumns:\n  - {name: customer_key, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
            run.Write("models/marts/fct_sales.sql", "SELECT customer_key(s.source_system, s.customer_id) AS customer_key, s.amount FROM staging.sales s\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");
            Ok(run.Cli("connection", "refresh", name), "refresh");

            var map = $"{run.Q("marts")}.{run.Q("customer_key_map")}";
            var log = $"{run.Q("marts")}.{run.Q("customer_key_map_repair")}";
            async Task<Dictionary<long, string>> Map() => (await engine.RowsAsync($"SELECT {run.Q("key_value")}, {run.Q("key_text")} FROM {map}")).Select(r => r.Split('|', 2)).ToDictionary(p => long.Parse(p[0]), p => p[1].Replace("\u001f", "|"));
            async Task<long> LogRows() => long.Parse((await engine.RowsAsync($"SELECT COUNT(*) FROM {log}")).Single());
            async Task<bool> LogExists() => long.Parse((await engine.RowsAsync($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'marts' AND TABLE_NAME = 'customer_key_map_repair'")).Single()) > 0;
            async Task<long> Valued(string textKey) => (await Map()).Single(p => p.Value == textKey).Key;
            string Bool(bool b) => name == "postgres" ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0");
            var first = await Map();
            Assert.Equal(3, first.Count);                                                       // SAP:1, SAP:2, and the fact's SAP:9
            var used = await Valued("SAP|9");
            var identityOf = new Func<Task<long>>(async () => name == "postgres"
                ? long.Parse((await engine.RowsAsync($"SELECT pg_sequence_last_value(pg_get_serial_sequence('marts.customer_key_map', 'key_value'))")).Single())
                : long.Parse((await engine.RowsAsync("SELECT CAST(IDENT_CURRENT('marts.customer_key_map') AS BIGINT)")).Single()));

            // show: counts always, the business keys only when asked, a filter by integer, text or part
            var counts = run.Cli("connection", "key-map", "show", "customer_key");
            Ok(counts, "show"); Assert.Contains("3 mapping(s)", counts.Out); Assert.DoesNotContain("SAP", counts.Out);
            var values = run.Cli("connection", "key-map", "show", "customer_key", "--show-values");
            Ok(values, "show values"); Assert.Contains("SAP | 9", values.Out);
            var one = run.Cli("connection", "key-map", "show", name, "customer_key", "--part", "SAP", "--part", "9", "--show-values", "--format", "json");
            Ok(one, "show a part"); Assert.Contains("\"matching\": 1", one.Out); Assert.Contains($"\"value\": {used}", one.Out);
            Assert.Contains("\"matching\": 3", run.Cli("connection", "key-map", "show", "customer_key", "--contains", "SAP", "--format", "json").Out);
            Assert.Contains("\"matching\": 1", run.Cli("connection", "key-map", "show", "customer_key", "--value", used.ToString(), "--format", "json").Out);
            Assert.NotEqual(0, run.Cli("connection", "key-map", "show", "no_such_key").Exit);

            // a dry run is the default: it prints the change and the statements, and changes nothing, not even the creation of the log
            var dry = run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "add", "--value", "100", "--part", "ZZ", "--part", "5", "--show-values");
            Ok(dry, "dry run"); Assert.Contains("Nothing was executed", dry.Out); Assert.Contains("adds 1 mapping: 100 -> ZZ | 5", dry.Out); Assert.Contains("BEGIN", dry.Out);
            Assert.Equal(first, await Map());
            Assert.False(await LogExists());
            Ok(run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "add", "--value", "100", "--part", "ZZ", "--part", "5", "--format", "json"), "dry run json");
            Assert.False(await LogExists());

            // applying says why, or it does not run
            Assert.Equal(2, run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "add", "--value", "100", "--part", "ZZ", "--part", "5", "--apply").Exit);
            Assert.Equal(first, await Map());

            // an addition: the integer is the one given, the audit row is in the same transaction, and the next business key the loads add gets a larger integer (the identity followed it)
            var added = run.CliAs("Ada via Test console", "connection", "key-map", "repair", "customer_key", "--operation", "add", "--value", "100", "--part", "ZZ", "--part", "5", "--reason", "restored from the 11 Nov backup", "--apply");
            Ok(added, "apply add"); Assert.Contains("Applied.", added.Out); Assert.Contains("Ada via Test console", added.Out);
            var afterAdd = await Map();
            Assert.Equal(first.Count + 1, afterAdd.Count);
            Assert.Equal("ZZ|5", afterAdd[100]);
            Assert.Equal(first.Where(p => p.Key != 100).OrderBy(p => p.Key), afterAdd.Where(p => p.Key != 100).OrderBy(p => p.Key));      // nothing else moved
            Assert.Equal(1, await LogRows());
            var logged = (await engine.RowsAsync($"SELECT {run.Q("repaired_by")}, {run.Q("operation")}, {run.Q("key_value")}, {run.Q("text_before")}, {run.Q("text_after")}, {run.Q("forced")}, {run.Q("reason")} FROM {log}")).Single().Split('|');
            Assert.Equal("Ada via Test console", logged[0]); Assert.Equal("add", logged[1]); Assert.Equal("100", logged[2]);
            Assert.Equal("∅", logged[3]); Assert.Equal("ZZ\u001f5", logged[4].Replace("|", "\u001f")); Assert.Equal("restored from the 11 Nov backup", logged[6]);
            Assert.Equal(1, long.Parse((await engine.RowsAsync($"SELECT COUNT(*) FROM {log} WHERE {run.Q("repaired_at")} IS NOT NULL AND {run.Q("db_login")} IS NOT NULL AND {run.Q("run_id")} IS NOT NULL")).Single()));
            Assert.True(await identityOf() >= 100, "the identity follows an explicit integer");
            await engine.ExecAsync("INSERT INTO staging.customers VALUES ('SAP', 3)");
            Ok(run.Cli("connection", "refresh", name), "refresh after the addition");
            Assert.True(await Valued("SAP|3") > 100, "a new business key never gets an integer at or below one that was added by hand");
            // adding it again, or an integer that is taken, or a business key that has one, is refused and changes nothing
            foreach (var again in new[] { new[] { "--value", "100", "--part", "QQ", "--part", "1" }, new[] { "--value", "555", "--part", "ZZ", "--part", "5" } })
            {
                var refused = run.Cli(["connection", "key-map", "repair", "customer_key", "--operation", "add", .. again, "--reason", "again", "--apply"]);
                Assert.Equal(1, refused.Exit); Assert.Contains("Nothing was changed", refused.Err);
            }
            Assert.Equal(1, await LogRows());

            // a change of text keeps the integer and records the text before and after
            var set = run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "set-text", "--value", "100", "--part", "ZZ", "--part", "6", "--reason", "a bug wrote 5", "--apply");
            Ok(set, "apply set-text");
            Assert.Equal("ZZ|6", (await Map())[100]);
            Assert.Equal(["set-text|ZZ\u001f5|ZZ\u001f6"], await engine.RowsAsync($"SELECT {run.Q("operation")}, {run.Q("text_before")}, {run.Q("text_after")} FROM {log} WHERE {run.Q("operation")} = 'set-text'"));

            // a used integer is not removed: the refusal names the table, in a dry run too; a forced removal is recorded as forced
            var usedDry = run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "remove", "--value", used.ToString());
            Assert.Equal(1, usedDry.Exit); Assert.Contains("marts.fct_sales.customer_key (1 row(s))", usedDry.Err); Assert.Contains("--force", usedDry.Err);
            Assert.Equal(1, run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "remove", "--value", used.ToString(), "--reason", "x", "--apply").Exit);
            Assert.Contains(used, (await Map()).Keys);

            // an integer nothing uses is removed, with the text before in the log
            var logsBefore = await LogRows();
            var removed = run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "remove", "--value", "100", "--reason", "created by the 12 Nov bug", "--apply");
            Ok(removed, "apply remove");
            Assert.DoesNotContain(100, (await Map()).Keys);
            Assert.Equal(logsBefore + 1, await LogRows());
            Assert.Equal(["remove|ZZ\u001f6"], await engine.RowsAsync($"SELECT {run.Q("operation")}, {run.Q("text_before")} FROM {log} WHERE {run.Q("operation")} = 'remove' AND {run.Q("key_value")} = 100 AND {run.Q("forced")} = {Bool(false)}"));
            Assert.Equal(1, run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "remove", "--value", "100", "--reason", "again", "--apply").Exit);   // nothing to remove

            // a forced removal of a used integer is allowed and recorded as forced
            Ok(run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "remove", "--value", used.ToString(), "--force", "--reason", "the integer was written by the bug; the fact rows are reloaded next", "--apply"), "forced remove");
            Assert.DoesNotContain(used, (await Map()).Keys);
            Assert.Equal(1, long.Parse((await engine.RowsAsync($"SELECT COUNT(*) FROM {log} WHERE {run.Q("operation")} = 'remove' AND {run.Q("key_value")} = {used} AND {run.Q("forced")} = {Bool(true)}")).Single()));

            // a reseed raises an identity that was lowered, never lowers one, and says so when there is nothing to do
            var nothing = run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "reseed");
            Ok(nothing, "reseed with nothing to do"); Assert.Contains("nothing to do", nothing.Out);
            var max = (await Map()).Keys.Max();
            await engine.ExecAsync(name == "postgres" ? "SELECT setval(pg_get_serial_sequence('marts.customer_key_map', 'key_value'), 1)" : "DBCC CHECKIDENT ('marts.customer_key_map', RESEED, 1) WITH NO_INFOMSGS");
            Assert.True(await identityOf() < max);
            var below = run.Cli("connection", "key-map", "show", "customer_key"); Ok(below, "show below"); Assert.Contains("BELOW", below.Out);
            var reseedDry = run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "reseed"); Ok(reseedDry, "reseed dry"); Assert.Contains("Nothing was executed", reseedDry.Out);
            Assert.True(await identityOf() < max, "a dry run does not move the identity");
            Ok(run.Cli("connection", "key-map", "repair", "customer_key", "--operation", "reseed", "--reason", "the identity was reset by a restore", "--apply"), "reseed");
            Assert.Equal(max, await identityOf());
            Assert.Equal(1, long.Parse((await engine.RowsAsync($"SELECT COUNT(*) FROM {log} WHERE {run.Q("operation")} = 'reseed' AND {run.Q("identity_before")} = 1 AND {run.Q("identity_after")} = {max}")).Single()));
            await engine.ExecAsync("INSERT INTO staging.customers VALUES ('SAP', 4)");
            Ok(run.Cli("connection", "refresh", name), "refresh after the reseed");
            Assert.Equal(max + 1, await Valued("SAP|4"));

            // a repair whose guard fails (the map changed between the read and the write) rolls everything back, the log row included
            var write = LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Write, run.Env).Settings!;
            var rows = await Map(); var logRows = await LogRows();
            var taken = rows.Single(p => p.Value == "SAP|4").Key;
            var script = KeyMapRepairScript.Build(name, new KeyMapTables(name, "marts", "customer_key"), KeyMapOperation.Add, 555, "SAP\u001f4", false, "race", Guid.NewGuid(), "the text already has an integer");
            var gateLog = new FileStatementLog(Path.Combine(run.Dir, ".dbdatabuild", "statement-log"), "test", Guid.NewGuid());
            await using (var gate = await MutationGate.OpenAsync(write, "test", StatementKind.Ddl | StatementKind.Data, gateLog, Guid.NewGuid()))
                await Assert.ThrowsAnyAsync<Exception>(() => gate.ExecuteAsync(GateStatement.FromPlanStep("race", StatementKind.Ddl, script.Text, script.Parameters)));
            Assert.Equal(rows, await Map());
            Assert.Equal(logRows, await LogRows());
            Assert.Equal("SAP|4", (await Map())[taken]);
        }
        finally
        {
            if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true);
        }
    }
}
