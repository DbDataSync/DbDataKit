using System.Text.Json.Nodes;
using DbDataBuild.Cli.Server;
using DbDataKit.Lineage;
using DbDataKit.State;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 23a FQ09 on the real engines: the checks stored with a model (format 2) are evaluated after a refresh, through the shared evaluator and a read-only connection, and recorded in the `quality` state module under the refresh's id. The
/// model's own tests (`project tests`) are not touched (QF7).
/// </summary>
public partial class ApplyConformanceTests
{
    private const string CheckedYaml = "grain: [order_id]\nchecks:\n"
        + "  - {id: amount_present, kind: not_null, column: amount, severity: block}\n"
        + "  - {id: id_unique, kind: unique, column: order_id}\n"
        + "  - {id: amount_not_negative, kind: range, column: amount, min: 0}\n"
        + "  - {id: loaded_recently, kind: freshness, max_age: 1h}\n";

    private static async Task<(Run Run, string ResultsDirectory)> ChecksSetUp(string name)
    {
        var run = await SetUp(name);
        foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
        run.Write("models/marts/a_orders.yml", "schemaVersion: 2\nname: marts.a_orders\nkind: full\ncolumns:\n" + OrdersColumns + CheckedYaml);
        run.Write("models/marts/a_orders.sql", "SELECT order_id, amount FROM staging.orders\n");
        Ok(run.Cli("connection", "init", "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");
        return (run, Path.Combine(Path.GetTempPath(), "ddb-e2e-results-" + Guid.NewGuid().ToString("N")));
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_refresh_then_its_checks_a_failing_not_null_is_a_fail_with_sample_keys_and_a_block_and_fixing_the_data_clears_it(string name)
    {
        var (run, results) = await ChecksSetUp(name);
        await using var engine = run.Engine;
        try
        {
            // without a results directory the refresh is what it always was: no checks, nothing written
            var plain = run.Cli("connection", "refresh", name, "--format", "json");
            Ok(plain, "refresh without checks");
            Assert.Null(JsonNode.Parse(plain.Out)!["data"]!["checks"]);
            Assert.False(Directory.Exists(results));

            var first = run.Cli("connection", "refresh", name, "--check-results", results, "--format", "json");
            Ok(first, "refresh with checks");                                            // a failed check is a finding of its own: the loads ended as they did
            var data = JsonNode.Parse(first.Out)!["data"]!;
            Assert.Equal("completed", (string)data["outcome"]!);
            var checks = data["checks"]!;
            Assert.Equal((4, 3, 1, 0, 1), ((int)checks["evaluated"]!, (int)checks["passed"]!, (int)checks["failed"]!, (int)checks["errors"]!, (int)checks["blocking"]!));
            var byId = checks["results"]!.AsArray().ToDictionary(r => (string)r!["check"]!, r => r!);
            var notNull = byId["amount_present"];
            Assert.Equal(("marts.a_orders", "fail", "block", 1m), ((string)notNull["model"]!, (string)notNull["status"]!, (string)notNull["severity"]!, (decimal)notNull["measured"]!));
            Assert.Equal(["order_id=3"], notNull["sample_keys"]!.AsArray().Select(k => (string)k!).ToArray());           // the row's key (the model's grain), never its other columns
            Assert.All(new[] { "id_unique", "amount_not_negative", "loaded_recently" }, id => Assert.Equal("pass", (string)byId[id]["status"]!));

            // the results are in the state module under the refresh's own id, and the failing block check blocks
            var identity = Identity(run, name, "marts", "a_orders");
            var store = new QualityStore(BuildState.Open(results, environment: run.Env));
            var runId = (string)checks["run_id"]!;
            var failed = Assert.Single(store.History(identity, "amount_present"));
            Assert.Equal((CheckOutcome.Fail, runId), (failed.Outcome, failed.RunId));
            Assert.Equal(["order_id=3"], failed.SampleKeys);
            Assert.Equal(["amount_present"], store.BlocksOf(identity).Select(b => b.CheckId));
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            Assert.True(await CountAsync(run, T("run_log"), $"{run.Q("plan_id")} = '{runId}'") > 0, "the run id is the id the tracking tables record the refresh under");
            Assert.StartsWith("ref-", runId);

            // the model's own tests are not the checks (QF7)
            var tests = run.Cli("project", "tests", "list");
            Ok(tests, "tests list");
            Assert.DoesNotContain("amount_present", tests.Out);

            // fix the data, refresh again: the check passes, the block clears in the same write as the result, the history keeps both
            await engine.ExecAsync("UPDATE staging.orders SET amount = 30.00 WHERE amount IS NULL");
            var second = run.Cli("connection", "refresh", name, "--check-results", results, "--format", "json");
            Ok(second, "refresh after the fix");
            Assert.Equal(0, (int)JsonNode.Parse(second.Out)!["data"]!["checks"]!["failed"]!);
            Assert.Empty(store.BlocksOf(identity));
            Assert.Equal([CheckOutcome.Pass, CheckOutcome.Fail], store.History(identity, "amount_present").Select(h => h.Outcome));
            Assert.Equal(CheckOutcome.Pass, store.Status(identity, "amount_present")!.Outcome);

            // the words of the text form
            await engine.ExecAsync("UPDATE staging.orders SET amount = NULL WHERE order_id = 1");
            var text = run.Cli("connection", "refresh", name, "--check-results", results);
            Ok(text, "text refresh");
            Assert.Contains("Checks: 3 passed, 1 failed, 0 could not run, 1 blocking", text.Out);
            Assert.Contains("block amount_present (not_null): failed, measured 1 (allowed 0); for example order_id=1", text.Out);

            // a refresh that is a dry run evaluates nothing
            var before = store.History(identity, "amount_present", 100).Count;
            Ok(run.Cli("connection", "refresh", name, "--check-results", results, "--dry-run"), "dry run");
            Assert.Equal(before, store.History(identity, "amount_present", 100).Count);
        }
        finally
        {
            if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true);
            try { if (Directory.Exists(results)) Directory.Delete(results, true); } catch (IOException) { /* a state file the process still holds (Windows) */ }
        }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_model_whose_load_failed_is_not_evaluated_and_one_that_loaded_is(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        var results = Path.Combine(Path.GetTempPath(), "ddb-e2e-results-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            Hooks(run, name);
            await engine.ExecAsync("CREATE TABLE staging.flag (x INT)");
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "refresh:\n  plans:\n    p: { on_failure: continue }\n");
            // a_orders fails in its hook while staging.flag has a row; z_other loads; both carry a check that would fail (the table has a null amount)
            run.Write("models/marts/a_orders.yml", "schemaVersion: 2\nname: marts.a_orders\nkind: full\ncolumns:\n" + OrdersColumns + CheckedYaml + "hooks:\n  - {name: gate, event: pre_load, script: hooks/gate.sql, effect: data}\nrefresh: { p: default }\n");
            run.Write("models/marts/a_orders.sql", "SELECT order_id, amount FROM staging.orders\n");
            run.Write("models/marts/z_orders.yml", "schemaVersion: 2\nname: marts.z_orders\nkind: full\ncolumns:\n" + OrdersColumns + "checks:\n  - {id: z_amount_present, kind: not_null, column: amount}\nrefresh: { p: default }\n");
            run.Write("models/marts/z_orders.sql", "SELECT order_id, amount FROM staging.orders\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");

            await engine.ExecAsync("INSERT INTO staging.flag VALUES (1)");
            var refresh = run.Cli("connection", "refresh", name, "p", "--check-results", results, "--format", "json");
            Assert.Equal(1, refresh.Exit);                                               // a_orders failed
            var data = JsonNode.Parse(refresh.Out)!["data"]!;
            Assert.Equal("completed_with_failures", (string)data["outcome"]!);
            var evaluated = data["checks"]!["results"]!.AsArray().Select(r => ((string)r!["model"]!, (string)r["check"]!, (string)r["status"]!)).ToList();
            Assert.Equal([("marts.z_orders", "z_amount_present", "fail")], evaluated);   // only what loaded
        }
        finally
        {
            if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true);
            try { if (Directory.Exists(results)) Directory.Delete(results, true); } catch (IOException) { }
        }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_model_downstream_of_a_blocked_dataset_is_not_run_and_says_which_check_and_clearing_the_block_lets_it_run(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        var results = Path.Combine(Path.GetTempPath(), "ddb-e2e-results-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            // a_orders carries a block check; z_orders reads it; y_other reads only staging
            run.Write("models/marts/a_orders.yml", "schemaVersion: 2\nname: marts.a_orders\nkind: full\ncolumns:\n" + OrdersColumns + "checks:\n  - {id: amount_present, kind: not_null, column: amount, severity: block}\n");
            run.Write("models/marts/a_orders.sql", "SELECT order_id, amount FROM staging.orders\n");
            run.Write("models/marts/z_orders.yml", "name: marts.z_orders\nkind: full\ncolumns:\n" + OrdersColumns);
            run.Write("models/marts/z_orders.sql", "SELECT order_id, amount FROM marts.a_orders\n");
            run.Write("models/marts/y_other.yml", "name: marts.y_other\nkind: full\ncolumns:\n" + OrdersColumns);
            run.Write("models/marts/y_other.sql", "SELECT order_id, amount FROM staging.orders\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            Task<int> Status(string model, string status) => CountAsync(run, T("run_log"), $"{run.Q("model")} = '{model}' AND status = '{status}'");

            // the first refresh loads everything (nothing is blocked yet) and the check sets the block
            var first = run.Cli("connection", "refresh", name, "--check-results", results, "--format", "json");
            Ok(first, "first refresh");
            Assert.Equal(1, (int)JsonNode.Parse(first.Out)!["data"]!["checks"]!["blocking"]!);

            // the second: the blocked dataset's own load goes on (a block stops dependents, D7), the model that reads it does not run and names the check, the unrelated one runs
            var (aBefore, yBefore, zBefore) = (await Status("marts.a_orders", "ok"), await Status("marts.y_other", "ok"), await Status("marts.z_orders", "ok"));
            var second = run.Cli("connection", "refresh", name, "--check-results", results);
            Assert.Equal(1, second.Exit);
            Assert.Contains("skipped: marts.z_orders", second.Out);
            Assert.Contains("marts.a_orders is blocked by the check amount_present", second.Out);
            Assert.Equal(1, await Status("marts.z_orders", "skipped: blocked by check"));
            Assert.Equal(aBefore + 1, await Status("marts.a_orders", "ok"));
            Assert.Equal(yBefore + 1, await Status("marts.y_other", "ok"));
            Assert.Equal(zBefore, await Status("marts.z_orders", "ok"));      // the dependent did not load

            // without a results directory the refresh asks nothing of the checks: it is what it was
            Ok(run.Cli("connection", "refresh", name), "refresh without checks");
            Assert.Equal(zBefore + 1, await Status("marts.z_orders", "ok"));

            // fix the data: the check passes on this refresh and clears the block, and the next refresh runs the dependent again
            await engine.ExecAsync("UPDATE staging.orders SET amount = 30.00 WHERE amount IS NULL");
            // (the block stands while this refresh starts, so the dependent is held once more; the check passes at its end)
            Assert.Equal(1, run.Cli("connection", "refresh", name, "--check-results", results).Exit);
            var third = run.Cli("connection", "refresh", name, "--check-results", results);
            Ok(third, "refresh with the block cleared");
            Assert.DoesNotContain("blocked by the check", third.Out);
            Assert.Equal(2, await Status("marts.z_orders", "skipped: blocked by check"));
            Assert.Equal(zBefore + 2, await Status("marts.z_orders", "ok"));   // the refresh without checks, and this one
        }
        finally
        {
            if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true);
            try { if (Directory.Exists(results)) Directory.Delete(results, true); } catch (IOException) { }
        }
    }

    private static string Identity(Run run, string name, string schema, string table)
    {
        var (login, _) = DbDataBuild.Execution.LoginSettings.FromEnvironment(name, name, DbDataBuild.Execution.Login.Read, run.Env);
        return LineageIdentity.Dataset(login!.AddressOnlyDefinition(), schema, table).ToString();
    }
}
