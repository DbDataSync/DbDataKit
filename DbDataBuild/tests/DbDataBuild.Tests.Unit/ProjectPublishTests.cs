using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Runner;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using DbDataKit.Runner;
using Json.Schema;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// `project publish --to runner` (plan 28c E07), the part that needs no runner: the committed files become a `package/1` manifest (one task per refresh plan) and a file set, the checks the project would meet at run time are made before
/// anything is sent (DDB-106 for a schedule, DDB-448 for a script that is not the one `loads.yml` was compiled from), and the manifest comes back from the runner's own reader as the same manifest. Sending, staging on two runners and promotion
/// are in <c>ProjectPublishOnRunnerTests</c> (conformance project, real runners).
/// </summary>
public class ProjectPublishTests
{
    private const string Plans = "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: Europe/Berlin, max_runtime: 30m }\n    quiet: {}\n";

    private static string CompiledProject()
    {
        var dir = RefreshPlansTests.Project("refresh: { hourly: incremental }\n", Plans, summaryRefresh: "refresh: { quiet: default }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(dir).Exit);
        return dir;
    }

    private static BuiltPackage Built(string dir, string package = "sales")
    {
        var diagnostics = new List<Diagnostic>();
        var built = RunnerProject.Build(dir, package, "0.4.x", diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Select(DiagnosticFormatter.Format)));
        return built!;
    }

    private static List<Diagnostic> Refused(string dir)
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(RunnerProject.Build(dir, "sales", "0.4.x", diagnostics));
        return diagnostics;
    }

    [Fact]
    public void Each_refresh_plan_is_a_task_with_its_schedule_zone_slots_and_limits_and_the_manifest_round_trips_through_the_runners_reader()
    {
        var dir = CompiledProject();
        var built = Built(dir);

        Assert.Equal(["sqlserver-hourly", "sqlserver-quiet"], built.Manifest.Tasks.Select(t => t.Name));
        var hourly = built.Manifest.Tasks[0];
        Assert.Equal("15 * * * *", hourly.Schedule.Cron);
        Assert.Equal("Europe/Berlin", hourly.Schedule.TimeZoneId);
        Assert.False(built.Manifest.Tasks[1].Schedule.HasSchedule);       // a plan with no schedule is run by a person or a trigger
        Assert.Equal([new ConnectionSlot("sqlserver", "sqlserver", SlotEnvironmentFormats.BuildLogin)], hourly.Connections);
        Assert.Equal(1, hourly.Limits.Concurrency);
        Assert.False(hourly.AllowOverlap);
        Assert.Equal([("connection", "sqlserver"), ("plan", "hourly")], hourly.Inputs.Select(i => (i.Name, i.Default!)));
        Assert.Equal(new RuntimeReference(RunnerProject.RuntimeName, "0.4.x"), built.Manifest.Launch.Runtime);

        // the limit is what the plan says: 30 minutes for each of its loads (the plan's max_runtime), added up, and the grace
        var members = RefreshPlanDocument.Parse(File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/refresh/hourly.yml")), "hourly.yml", [])!.Members.Count;
        Assert.Equal(TimeSpan.FromMinutes(30) * members + RunnerProject.TimeoutGrace, hourly.Limits.Timeout);
        Assert.Null(built.Manifest.Tasks[1].Limits.Timeout);              // no max_runtime: nothing bounds the run, so the task has no timeout either

        var load = PackageManifestReader.Load(built.ManifestText, "package.yaml");
        Assert.True(load.Ok, string.Join("\n", load.Diagnostics));
        Assert.Equal(built.ManifestText, PackageManifestWriter.Write(load.Manifest!));
        Assert.Empty(PackageManifestValidator.Validate(load.Manifest!, "package.yaml"));
        Assert.Equal(built.Manifest.Tasks.Select(t => (t.Name, t.Schedule.Cron, t.Connections.Count)), load.Manifest!.Tasks.Select(t => (t.Name, t.Schedule.Cron, t.Connections.Count)));
    }

    [Fact]
    public void The_package_holds_the_committed_files_and_nothing_else_and_is_the_same_bytes_whatever_the_line_endings_of_the_checkout()
    {
        var dir = CompiledProject();
        var built = Built(dir);
        var paths = built.Files.Select(f => f.Path).ToList();
        Assert.Contains("dbdatabuild.yml", paths);
        Assert.Contains("rendered/sqlserver/loads.yml", paths);
        Assert.Contains("rendered/sqlserver/refresh/hourly.yml", paths);
        Assert.All(paths.Where(p => p.StartsWith("connections/", StringComparison.Ordinal)), p => Assert.EndsWith(".yaml", p, StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.StartsWith("models/", StringComparison.Ordinal) || p.StartsWith(".dbdatabuild", StringComparison.Ordinal) || p.StartsWith("plans/", StringComparison.Ordinal));

        var first = Path.Combine(dir, "out1");
        RunnerProject.Write(built, first);
        // a checkout with CRLF files: the same package
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "rendered"), "*.yml", SearchOption.AllDirectories))
            File.WriteAllText(file, File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n"));
        var second = Path.Combine(dir, "out2");
        RunnerProject.Write(Built(dir), second);

        string Hashes(string d) => string.Join("\n", Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => Path.GetRelativePath(d, f).Replace('\\', '/') + " " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))));
        Assert.Equal(Hashes(first), Hashes(second));
    }

    [Fact]
    public void A_plan_with_a_schedule_that_is_not_cron_is_refused_naming_the_plans_file_and_the_key_as_DDB_106()
    {
        var dir = CompiledProject();
        var path = Path.Combine(dir, "rendered/sqlserver/refresh/hourly.yml");
        var plan = RefreshPlanDocument.Parse(File.ReadAllText(path), "hourly.yml", [])!;
        // a file that is valid in every way (its hash is right) except that its schedule is not a schedule: what a hand edit that also fixed the hash, or a tool of another version, would leave
        TestFiles.WriteAllText(path, RefreshPlanDocument.Serialize(plan with { Settings = plan.Settings with { Schedule = "every fifteen minutes" } }));

        var diagnostics = Refused(dir);

        var refused = Assert.Single(diagnostics);
        Assert.Equal("DDB-106", refused.Code);
        Assert.Equal("rendered/sqlserver/refresh/hourly.yml", refused.Location.File);
        Assert.Contains("`schedule`", refused.Found);
        Assert.Contains(DbDataKit.Scheduling.Schedule.OnCron("every fifteen minutes", "Europe/Berlin").Validate()!, refused.Found);
    }

    [Fact]
    public void A_script_that_is_not_the_one_loads_yml_was_compiled_from_is_refused_as_DDB_448_naming_it()
    {
        var dir = CompiledProject();
        var script = Directory.EnumerateFiles(Path.Combine(dir, "rendered/sqlserver"), "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        File.AppendAllText(script, "-- edited by hand\n");

        var diagnostics = Refused(dir);

        Assert.All(diagnostics, d => Assert.Equal("DDB-448", d.Code));
        Assert.Contains(diagnostics, d => d.Location.File == "sqlserver/" + Path.GetRelativePath(Path.Combine(dir, "rendered/sqlserver"), script).Replace('\\', '/') || d.Location.File.EndsWith(Path.GetFileName(script), StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Found.Contains("is not the one `loads.yml` was compiled from", StringComparison.Ordinal));

        // and a script that is gone
        File.Delete(script);
        Assert.Contains(Refused(dir), d => d.Code == "DDB-448" && d.Found.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_that_was_never_compiled_has_nothing_to_publish_and_a_package_name_the_runner_would_refuse_is_refused_here()
    {
        var empty = RefreshPlansTests.Project("refresh: { hourly: incremental }\n");
        Assert.Equal("DDB-446", Assert.Single(Refused(empty)).Code);

        var diagnostics = new List<Diagnostic>();
        Assert.Null(RunnerProject.Build(CompiledProject(), "Sales Data", "0.4.x", diagnostics));
        Assert.Equal("DDB-106", Assert.Single(diagnostics).Code);
        Assert.Equal("sales-data", RunnerProject.NameOf("Sales Data"));
    }

    [Fact]
    public void The_runtime_range_is_the_tools_minor_line_and_a_name_is_written_in_the_runners_alphabet()
    {
        Assert.Equal("0.4.x", RunnerProject.RuntimeRange("0.4.0"));
        Assert.Equal("1.12.x", RunnerProject.RuntimeRange("1.12.3-beta.4+abc"));
        Assert.Equal("*", RunnerProject.RuntimeRange("dev"));
        Assert.Equal("sqlserver-hourly", RunnerProject.TaskName("sqlserver", "hourly"));
        Assert.Equal("my-wh-nightly-1", RunnerProject.TaskName("My.Wh", "Nightly 1"));
    }

    private static (int Exit, JsonNode Doc, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--format", "json"], o, e, environment: _ => null);
        var doc = JsonNode.Parse(o.ToString())!;
        var result = SchemaConformanceTests.LoadSchema("output").Evaluate(JsonSerializer.SerializeToNode(doc), new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, $"output does not satisfy output.schema.json:\n{doc}");
        return (exit, doc, e.ToString());
    }

    [Fact]
    public void The_command_writes_the_package_and_sends_nothing_with_out_and_refuses_what_it_cannot_send_with_a_usage_message()
    {
        var dir = CompiledProject();
        var target = Path.Combine(dir, "package-out");

        var written = Cli("project", "publish", "--to", "runner", "--project", dir, "--package", "sales", "--out", target);
        Assert.Equal(0, written.Exit);
        Assert.True(File.Exists(Path.Combine(target, "package.yaml")));
        Assert.True(File.Exists(Path.Combine(target, "files", "rendered", "sqlserver", "loads.yml")));
        Assert.Equal(["sqlserver-hourly", "sqlserver-quiet"], written.Doc["data"]!["tasks"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.Null(written.Doc["data"]!["version"]);                      // nothing was sent

        Assert.Equal(CliApp.ExitUsage, Cli("project", "publish", "--to", "somewhere", "--project", dir).Exit);
        Assert.Equal(CliApp.ExitUsage, Cli("project", "publish", "--to", "runner", "--project", dir).Exit);          // no runner and no channel
        Assert.Equal(CliApp.ExitUsage, Cli("project", "publish", "--to", "runner", "--project", dir, "--server", "x", "--channel", "dev", "--bind", "nonsense").Exit);

        // a project that disagrees with itself is refused before the runner is looked for, with the diagnostic in the document
        File.AppendAllText(Directory.EnumerateFiles(Path.Combine(dir, "rendered/sqlserver"), "*.sql", SearchOption.AllDirectories).First(), "-- x\n");
        var refused = Cli("project", "publish", "--to", "runner", "--project", dir, "--server", "unpaired", "--channel", "dev", "--client-dir", Path.Combine(dir, "client"));
        Assert.Equal(CliApp.ExitFindings, refused.Exit);
        Assert.Contains(refused.Doc["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "DDB-448");
    }

    [Fact]
    public void A_runner_this_machine_is_not_paired_with_is_refused_with_how_to_pair()
    {
        var dir = CompiledProject();
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "publish", "--to", "runner", "--project", dir, "--server", "nowhere", "--channel", "dev", "--client-dir", Path.Combine(dir, "client")], o, e, environment: _ => null);
        var result = (Exit: exit, Err: e.ToString());
        Assert.Equal(CliApp.ExitFindings, result.Exit);
        Assert.Contains("not paired with a runner called or at 'nowhere'", result.Err);
        Assert.Contains("--code", result.Err);
    }
}
