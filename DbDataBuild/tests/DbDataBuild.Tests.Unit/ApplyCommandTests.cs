using System.Data;
using System.Diagnostics;
using DbDataBuild.Apply;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using DbDataBuild.State;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The refusals of `apply`, `plan`, `check` and `ack` that need no database: they must all happen before anything is connected to or executed.</summary>
public class ApplyCommandTests
{
    private static Plan PlanWith(params PlanStep[] steps) => new("2026-10-12-abcd1234", "sqlserver", null, false, "0.1.0", [], [], steps, []);

    private static PlanStep Step(string id, RiskClass risk, string obj = "marts.fct") =>
        new(id, StepType.Ddl, obj, $"step {id}", $"ALTER TABLE x{id}", risk, ["col.removed"], null, []);

    private static string Write(Plan plan, string? dir = null)
    {
        dir ??= NewProjectDir();
        var path = Path.Combine(dir, "p.plan.yml");
        File.WriteAllText(path, PlanDocument.Serialize(plan));
        return path;
    }

    private static (int Exit, string Out, string Err) Cli(Func<string, string?>? env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: env ?? (_ => null)), o.ToString(), e.ToString());
    }

    private static readonly Dictionary<string, string?> BothLogins = new()
    {
        ["DBDATABUILD_SQLSERVER_READ"] = "Server=127.0.0.1,1;User Id=r;Password=hunter2;Connect Timeout=1",
        ["DBDATABUILD_SQLSERVER_WRITE"] = "Server=127.0.0.1,1;User Id=w;Password=hunter2;Connect Timeout=1",
    };

    [Fact]
    public void A_missing_plan_file_is_a_usage_error()
    {
        var (exit, _, err) = Cli(null, "connection", "deploy", "--apply-plan", Path.Combine(NewProjectDir(), "none.plan.yml"));
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("does not exist", err);
    }

    [Fact]
    public void An_edited_plan_is_refused_before_any_login_is_read()
    {
        var path = Write(PlanWith(Step("1", RiskClass.Safe)));
        File.WriteAllText(path, File.ReadAllText(path).Replace("ALTER TABLE x1", "DROP TABLE x1"));
        var (exit, output, err) = Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--apply-plan", path, "--project", Path.GetDirectoryName(path)!);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-435", err);
        Assert.DoesNotContain("effect:", output);                      // the header (which names logins) is never reached
    }

    [Fact]
    public void Risky_and_destructive_steps_need_their_allowances_and_are_refused_before_connecting()
    {
        var path = Write(PlanWith(Step("1", RiskClass.Risky), Step("2", RiskClass.Destructive, "marts.a"), Step("3", RiskClass.Destructive, "marts.b")));
        var dir = Path.GetDirectoryName(path)!;
        var (exit, output, err) = Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--apply-plan", path, "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Equal(3, err.Split("error DDB-436").Length - 1);              // risky, and one per destructive object
        Assert.Contains("--allow-risky", err);
        Assert.Contains("--allow-destructive marts.a", err);
        Assert.Contains("--allow-destructive marts.b", err);
        Assert.Contains("Nothing was executed.", output);
        Assert.DoesNotContain("hunter2", output + err);
        Assert.False(Directory.Exists(Path.Combine(dir, ".dbdatabuild")));   // not even a statement log

        // one allowance does not stand in for another, and none implies "all"
        var (_, _, partial) = Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--apply-plan", path, "--project", dir, "--allow-risky", "--allow-destructive", "marts.a");
        Assert.Equal(1, partial.Split("error DDB-436").Length - 1);
        Assert.Contains("marts.b", partial);
    }

    [Fact]
    public void Apply_without_the_write_login_stops_with_the_login_diagnostic_and_a_dry_run_does_not_need_it()
    {
        var path = Write(PlanWith(Step("1", RiskClass.Safe)));
        var dir = Path.GetDirectoryName(path)!;
        var readOnly = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = BothLogins["DBDATABUILD_SQLSERVER_READ"] };
        var (exit, _, err) = Cli(readOnly.GetValueOrDefault, "connection", "deploy", "--apply-plan", path, "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-501", err);
        Assert.Contains("DBDATABUILD_SQLSERVER_WRITE", err);

        var none = Cli(null, "connection", "deploy", "--apply-plan", path, "--project", dir, "--dry-run");
        Assert.Contains("DBDATABUILD_SQLSERVER_READ", none.Err);        // even a dry run needs the read login: it verifies against the live target
        Assert.DoesNotContain("WRITE", none.Err);
    }

    [Fact]
    public void Plan_and_check_without_a_read_login_stop_before_doing_anything()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        foreach (var command in new[] { new[] { "connection", "deploy", "--write-plan" }, new[] { "connection", "status" } })
        {
            var (exit, _, err) = Cli(null, [.. command, "--project", dir]);
            Assert.Equal(CliApp.ExitFindings, exit);
            Assert.Contains("DDB-501", err);
            Assert.Contains("DBDATABUILD_SQLSERVER_READ", err);
        }
        Assert.False(Directory.Exists(Path.Combine(dir, "plans")));
    }

    [Fact]
    public void Plan_needs_an_answers_file_that_loads_before_it_looks_at_the_database()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        var answers = Path.Combine(dir, "answers.yml");
        File.WriteAllText(answers, "answers:\n  - id: not-a-question-id\n    choice: x\n");
        var (exit, _, err) = Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--write-plan", "--project", dir, "--answers", answers);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-106", err);
    }

    [Fact]
    public void Ack_needs_a_reason_and_a_known_kind()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        Assert.Equal(CliApp.ExitUsage, Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--ack", "drift:marts.fct", "--project", dir).Exit);
        var (exit, _, err) = Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--ack", "banana:marts.fct", "--reason", "x", "--project", dir);
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("Unknown acknowledgement", err);
    }

    [Fact]
    public void Allowance_checks_are_exact_per_object()
    {
        var plan = PlanWith(Step("1", RiskClass.Destructive, "marts.a"), Step("2", RiskClass.Safe, "marts.b"));
        ApplyOptions Opts(params string[] allowed) => new(false, false, allowed.ToHashSet(), null, false, "me");
        Assert.Single(ApplyEngine.CheckAllowances(plan, Opts()));
        Assert.Single(ApplyEngine.CheckAllowances(plan, Opts("marts.b")));
        Assert.Empty(ApplyEngine.CheckAllowances(plan, Opts("marts.a")));
        Assert.Empty(ApplyEngine.CheckAllowances(PlanWith(Step("1", RiskClass.Safe)), Opts()));
    }

    [Fact]
    public void Plan_parameters_become_typed_driver_values_and_invalid_text_never_reaches_the_driver()
    {
        Assert.Equal((DbType.DateTime2, new DateTime(2024, 1, 4, 8, 30, 15)), ((Func<DbDataBuild.Execution.GateParameter, (DbType, object?)>)(g => (g.Type, g.Value)))(ApplyEngine.ToGate(new("w", "TIMESTAMP", "runtime", "2024-01-04 08:30:15"))));
        var date = ApplyEngine.ToGate(new("d", "DATE", "runtime", "2024-01-04"));
        Assert.Equal((DbType.Date, new DateTime(2024, 1, 4)), (date.Type, date.Value));
        var n = ApplyEngine.ToGate(new("n", "BIGINT", "runtime", "-42"));
        Assert.Equal((DbType.Int64, -42L), (n.Type, n.Value));
        var nothing = ApplyEngine.ToGate(new("w", "TIMESTAMP", "resolver", null));
        Assert.Equal((DbType.DateTime2, null), (nothing.Type, nothing.Value));
        Assert.Throws<FormatException>(() => ApplyEngine.ToGate(new("w", "TIMESTAMP", "runtime", "2024-01-04'; DROP TABLE t; --")));
        Assert.Throws<FormatException>(() => ApplyEngine.ToGate(new("n", "BIGINT", "runtime", "1; DROP")));
    }

    private static bool GitAvailable()
    {
        try { using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false }); p!.WaitForExit(); return p.ExitCode == 0; }
        catch (Exception) { return false; }
    }

    [Fact]
    public void A_dirty_working_tree_refuses_a_real_apply_but_only_warns_for_a_dry_run()
    {
        if (!GitAvailable()) return; // the check cannot be exercised without git; the refusal itself is not git-specific
        var dir = NewProjectDir();
        void Git(string args) { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!; p.WaitForExit(); }
        Git("init -q");
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        Git("add -A");
        Git("-c user.name=t -c user.email=t@example.com commit -q -m init");
        File.WriteAllText(Path.Combine(dir, "uncommitted.txt"), "x");
        var path = Write(PlanWith(Step("1", RiskClass.Safe)), dir);

        var (exit, output, err) = Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--apply-plan", path, "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-442", err);
        Assert.Contains("Nothing was executed.", output);

        // the dry run proceeds past the warning (and then fails to reach the unreachable test server, which is not what is under test)
        var dry = Cli(BothLogins.GetValueOrDefault, "connection", "deploy", "--apply-plan", path, "--project", dir, "--dry-run");
        Assert.Contains("warning DDB-442", dry.Err);
    }
}
