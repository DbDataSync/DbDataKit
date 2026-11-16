using DbDataBuild.Cli;
using DbDataBuild.Cli.Runner;
using DbDataBuild.Core;
using DbDataKit.Runner;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 28c E21, the part that needs no runner and no engine: the person-driven writes (apply a plan, acknowledge a block, seed) as the manual-only tasks of a package of their own, the command line each task runs, the refusal of inputs
/// that are not what a command should be given, and who a run is for (the console starts such a task on the Runner; the Build server's route that did was deleted, plan 33 SC23). The real runner and a real engine are
/// in the conformance project (<c>BuildOperationsOnRunnerTests</c>).
/// </summary>
public sealed class BuildOperationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-ops-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;

    public BuildOperationsTests()
    {
        project = RefreshPlansTests.Project("refresh: { hourly: incremental }\n", "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
    }

    public void Dispose()
    {
        foreach (var d in new[] { root, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private BuiltPackage Built()
    {
        var diagnostics = new List<Diagnostic>();
        var built = OperationsPackage.Build(project, "sales-ops", "0.4.x", diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Select(DiagnosticFormatter.Format)));
        return built!;
    }

    [Fact]
    public void The_operations_package_has_three_manual_only_tasks_the_used_connections_as_slots_and_the_sources_but_no_plan_and_no_compiled_file()
    {
        var built = Built();
        Assert.Equal(["apply_plan", "ack", "seed"], built.Manifest.Tasks.Select(t => t.Name));
        Assert.All(built.Manifest.Tasks, t =>
        {
            Assert.False(t.Schedule.HasSchedule);                                    // nothing starts it but a person
            Assert.Equal([TriggerKind.Manual], t.Triggers.Select(x => x.Kind));
            Assert.Equal(1, t.Limits.Concurrency);
            Assert.Null(t.Limits.Attempts);                                          // a write is never repeated by the runner
            // only what the project uses (owner, 2026-11-15): its models build on `sqlserver`, so the built-in `postgres` and `fabric` it never touches are no slot
            Assert.Equal(["sqlserver"], t.Connections.Select(c => c.Name));
            Assert.All(t.Connections, c => Assert.Equal(SlotEnvironmentFormats.BuildLogin, c.Env));
        });
        Assert.All(built.Manifest.Tasks, t => Assert.DoesNotContain('-', t.Name));   // a refresh task is `connection-plan`: a plan is never taken for an operation
        var paths = built.Files.Select(f => f.Path).ToList();
        Assert.Contains("dbdatabuild.yml", paths);
        Assert.Contains(paths, p => p.StartsWith("models/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.StartsWith("plans/", StringComparison.Ordinal) || p.StartsWith("rendered/", StringComparison.Ordinal) || p.StartsWith(".dbdatabuild", StringComparison.Ordinal));

        // the runner's own reader and validator accept the manifest, and it comes back the same
        var load = PackageManifestReader.Load(built.ManifestText, "package.yaml");
        Assert.True(load.Ok, string.Join("\n", load.Diagnostics));
        Assert.Equal(built.ManifestText, PackageManifestWriter.Write(load.Manifest!));
        Assert.Empty(PackageManifestValidator.Validate(load.Manifest!, "package.yaml"));
        Assert.Equal(["connection", "plan", "plan_text", "allow_risky", "allow_destructive", "allow_consumers", "input_pins"], load.Manifest!.Tasks[0].Inputs.Select(i => i.Name));
    }

    [Fact]
    public void The_refresh_package_is_what_it_was_and_holds_none_of_the_operations()
    {
        var diagnostics = new List<Diagnostic>();
        var refresh = RunnerProject.Build(project, "sales", "0.4.x", diagnostics)!;
        Assert.DoesNotContain(refresh.Manifest.Tasks, t => BuildOperations.All.Contains(t.Name));
        Assert.DoesNotContain(refresh.Files, f => f.Path.StartsWith("models/", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_task_runs_the_command_of_that_name_with_the_inputs_it_was_given_and_a_dry_run_is_the_commands_own()
    {
        Dictionary<string, string> Inputs(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);
        var apply = BuildOperations.Arguments(BuildOperations.ApplyPlan, Inputs(("connection", "wh"), ("plan", "2026-11-12-0a1b2c3d"), ("allow_risky", "true"), ("allow_destructive", "marts.fct, marts.dim"), ("input_pins", "a.b=1\nc=2")), "/p", null, dryRun: false);
        Assert.Equal(["connection", "deploy", "wh", "--apply-plan", "2026-11-12-0a1b2c3d", "--allow-risky", "--allow-destructive", "marts.fct", "--allow-destructive", "marts.dim", "--input=a.b=1", "--input=c=2", "--project", "/p", "--format", "json"], apply);
        Assert.Contains("--dry-run", BuildOperations.Arguments(BuildOperations.ApplyPlan, Inputs(("connection", "wh"), ("plan", "x")), "/p", "/scratch/x.plan.yml", dryRun: true));
        Assert.Contains("/scratch/x.plan.yml", BuildOperations.Arguments(BuildOperations.ApplyPlan, Inputs(("connection", "wh"), ("plan", "x")), "/p", "/scratch/x.plan.yml", dryRun: false));

        Assert.Equal(["connection", "deploy", "wh", "--ack", "drift:marts.fct", "--reason=it was me", "--project", "/p", "--format", "json"],
            BuildOperations.Arguments(BuildOperations.Ack, Inputs(("connection", "wh"), ("kind", "drift"), ("name", "marts.fct"), ("reason", "it was me")), "/p", null, dryRun: false));

        var seed = BuildOperations.Arguments(BuildOperations.Seed, Inputs(("connection", "wh"), ("seed", "7"), ("scale", "100"), ("replace", "true")), "/p", null, dryRun: false);
        Assert.Equal(["connection", "seed", "wh", "--seed", "7", "--scale", "100", "--replace", "--apply", "--project", "/p", "--format", "json"], seed);
        Assert.DoesNotContain("--apply", BuildOperations.Arguments(BuildOperations.Seed, Inputs(("connection", "wh")), "/p", null, dryRun: true));
    }

    [Theory]
    [InlineData("apply_plan", "connection=--help;plan=x")]
    [InlineData("apply_plan", "connection=wh;plan=../../etc/passwd")]
    [InlineData("apply_plan", "connection=wh;plan=x;allow_destructive=--yes")]
    [InlineData("apply_plan", "connection=wh;plan=x;input_pins=--project=/")]
    [InlineData("ack", "connection=wh;kind=nothing;name=a;reason=r")]
    [InlineData("ack", "connection=wh;kind=drift;name=--x;reason=r")]
    [InlineData("ack", "connection=wh;kind=drift;name=a;reason= ")]
    [InlineData("seed", "connection=wh;seed=many")]
    [InlineData("deploy", "connection=wh")]
    public void An_input_that_is_an_option_or_a_path_or_not_the_kind_of_thing_asked_for_is_refused_before_anything_runs(string task, string pairs)
    {
        var inputs = pairs.Split(';').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.NotNull(BuildOperations.Refusal(task, inputs));
    }

    [Fact]
    public void A_run_is_for_the_person_the_runner_recorded_and_for_the_run_itself_when_nobody_was()
    {
        var asked = BuildOperations.ActorOf(n => n == "DBDATAKIT_RUN_REQUESTED_BY" ? "Ada via Main console (sent by Build server)" : null, "sales-ops", "prod");
        Assert.Equal("Ada via Main console (sent by Build server)", asked);
        Assert.Equal("runner sales-ops/prod", BuildOperations.ActorOf(_ => null, "sales-ops", "prod"));
        // and it is the name the commands write into the tracking tables
        Assert.Equal(asked, Actor.Of(n => n == Actor.Variable ? asked : null, "login"));
    }
}
