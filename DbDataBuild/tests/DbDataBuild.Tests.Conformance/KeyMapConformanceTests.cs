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
}
