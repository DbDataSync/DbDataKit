using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner.Abstractions.Tests;

public sealed class PackageManifestTests
{
    private const string File = "orders/package.yaml";

    private const string Full = """
        schema: package/1
        name: orders-sync
        description: "Loads orders from the shop, every night"
        launch: { kind: assembly, assembly: bin/Orders.dll }
        built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
        tasks:
          - name: extract
            entry: { type: Orders.Tasks.Extract }
            schedule: { cron: "0 2 * * *", time_zone: Europe/Berlin, misfire: skip, misfire_grace: 5m }
            triggers:
              - webhook: { source: shop }
              - file: { connection: inbox, path: "in/*.csv", poll: 30s }
              - store_change: { model: orders, entity: order }
              - manual: true
            inputs:
              - { name: since, type: date, required: false, default: "2024-01-01", description: "First day to load" }
              - { name: dry_run, type: bool, default: "false" }
            connections:
              - { name: source, kind: sqlserver }
              - { name: inbox, kind: folder }
            progress:
              checkpoints:
                - name: orders
                  per: [customer]
                  parts:
                    - { name: modified, type: time, default: "2024-01-01T00:00:00Z" }
                    - { name: id, type: int, default: "0" }
              state:
                - { name: last_file, type: text }
                - { name: total, type: decimal, per: [customer], default: "0.0" }
            steps:
              - { name: download, kind: stream, input: DateOnly }
              - { name: archive, input: DateOnly, result: ArchiveReceipt }
            units:
              - { name: customer, item: Customer }
            counters: [rows, files]
            limits: { timeout: 30m, memory_mb: 512, cpu: 1.5, concurrency: 2, attempts: 3, backoff: 30s, max_backoff: 10m }
            requires: [network:outside, os:linux]
            capabilities: [network]
          - name: report
            entry: { type: Orders.Tasks.Report, method: Build }
            schedule: { every: 15m }
            triggers:
              - after: { task: extract, on: [succeeded, completed_with_failures] }
            allow_overlap: true
        """;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The manifest as comparable text: records hold lists, which compare by reference, so equality is compared on what each field says.</summary>
    private static string Canonical(PackageManifest manifest) => JsonSerializer.Serialize(manifest, Json);

    private static PackageManifest Valid(string yaml = Full) => PackageManifestReader.Load(yaml, File) is { Ok: true } result ? result.Manifest! : throw new Xunit.Sdk.XunitException(Describe(yaml));

    private static string Describe(string yaml) => string.Join("\n", PackageManifestReader.Load(yaml, File).Diagnostics);

    private static string Problems(string yaml) => Describe(yaml);

    /// <summary>The full manifest with <paramref name="from"/> replaced by <paramref name="to"/>.</summary>
    private static string Edited(string from, string to)
    {
        Assert.Contains(from, Full);
        return Full.Replace(from, to, StringComparison.Ordinal);
    }

    // a valid manifest ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_complete_manifest_is_read_in_full()
    {
        var m = Valid();

        Assert.Equal("orders-sync", m.Name);
        Assert.Equal(new PackageLaunch(LaunchKind.Assembly, "bin/Orders.dll"), m.Launch);
        Assert.Equal(new BuiltAgainst("10.0", "1", "dbdatakit.runner.v1"), m.BuiltAgainst);
        Assert.Equal(["extract", "report"], m.Tasks.Select(t => t.Name));

        var extract = m.Tasks[0];
        Assert.Equal("Orders.Tasks.Extract", extract.Entry.Type);
        Assert.Equal("Run", extract.Entry.Method);
        Assert.Equal(("0 2 * * *", "Europe/Berlin", MisfirePolicy.Skip, TimeSpan.FromMinutes(5)), (extract.Schedule.Cron, extract.Schedule.TimeZoneId, extract.Schedule.Misfire, extract.Schedule.MisfireGrace));
        Assert.Equal([TriggerKind.Webhook, TriggerKind.File, TriggerKind.StoreChange, TriggerKind.Manual], extract.Triggers.Select(t => t.Kind));
        Assert.Equal(TimeSpan.FromSeconds(30), extract.Triggers[1].Poll);
        Assert.Equal(["source", "inbox"], extract.Connections.Select(c => c.Name));
        Assert.Equal(ProgressType.Date, extract.Inputs[0].Type);
        Assert.Equal(new TaskLimits(TimeSpan.FromMinutes(30), 512, 1.5m, 2, 3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)), extract.Limits);
        Assert.Equal(["network:outside", "os:linux"], extract.Requires);
        Assert.Equal(["network"], extract.Capabilities);

        Assert.Equal([new TaskStep("download", StepKind.Stream, "DateOnly"), new TaskStep("archive", StepKind.Value, "DateOnly", "ArchiveReceipt")], extract.StepList);
        Assert.Equal([new TaskUnit("customer", "Customer")], extract.UnitList);
        Assert.Equal(["rows", "files"], extract.CounterList);

        var report = m.Tasks[1];
        Assert.Empty(report.StepList);                                  // none declared: none required
        Assert.Null(report.Steps);
        Assert.Equal(TimeSpan.FromMinutes(15), report.Schedule.Every);
        Assert.Equal([RunOutcome.Succeeded, RunOutcome.CompletedWithFailures], report.Triggers[0].Outcomes);
        Assert.True(report.AllowOverlap);
    }

    [Fact]
    public void A_valid_manifest_round_trips()
    {
        var first = Valid();

        var text = PackageManifestWriter.Write(first);
        var second = Valid(text);

        Assert.StartsWith("schema: package/1\n", text);
        Assert.Equal(Canonical(first), Canonical(second));
        Assert.Equal(text, PackageManifestWriter.Write(second));
    }

    [Fact]
    public void A_manifest_built_in_code_round_trips_with_every_kind_of_trigger_and_an_executable_launch()
    {
        var task = new PackageTask("pull", new TaskEntry(Args: ["--full"]))
        {
            Schedule = Schedule.Interval(TimeSpan.FromMilliseconds(1500), MisfirePolicy.Skip),
            Triggers = [TaskTrigger.AfterRun("push", RunOutcome.Failed), TaskTrigger.OnWebhook("shop", "shop_hook"), TaskTrigger.OnFile("drop", "*.csv"), TaskTrigger.OnStoreChange("orders"), TaskTrigger.ByHand()],
            Connections = [new ConnectionSlot("drop", "folder")],
            Inputs = [new TaskInput("note", Default: "say \"hi\": ok")],
            Limits = new TaskLimits(Timeout: TimeSpan.FromHours(2)),
        };
        var push = new PackageTask("push", new TaskEntry());
        var manifest = new PackageManifest("py-pull", null, new PackageLaunch(LaunchKind.Executable, Command: "bin/pull", Args: ["--quiet", "a b"]), new BuiltAgainst(null, null, "dbdatakit.runner.v1"), [task, push]);

        Assert.Empty(PackageManifestValidator.Problems(manifest));
        var read = Valid(PackageManifestWriter.Write(manifest));

        Assert.Equal(Canonical(manifest), Canonical(read));
    }

    [Fact]
    public void An_after_trigger_can_say_any_outcome_and_a_webhook_trigger_can_name_a_secret_and_both_survive_writing()
    {
        var m = Valid("""
            schema: package/1
            name: hooks
            launch: { kind: executable, command: bin/x }
            built_against: { protocol: dbdatakit.runner.v1 }
            tasks:
              - name: first
              - name: second
                triggers:
                  - after: { task: first, on: [any] }
                  - webhook: { source: shop, secret: shop_hook }
            """);

        var triggers = m.Tasks[1].Triggers;
        Assert.Equal(5, triggers[0].Outcomes.Count);
        Assert.Contains(RunOutcome.Failed, triggers[0].Outcomes);
        Assert.Equal("shop_hook", triggers[1].Secret);
        var again = Valid(PackageManifestWriter.Write(m)).Tasks[1].Triggers;
        Assert.Equal(triggers[0].Outcomes, again[0].Outcomes);
        Assert.Equal("shop_hook", again[1].Secret);
        Assert.Contains("[any]", PackageManifestWriter.Write(m), StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_are_what_the_schedule_and_entry_types_say()
    {
        var m = Valid("""
            schema: package/1
            name: tiny
            launch: { kind: executable, command: bin/tiny }
            built_against: { protocol: dbdatakit.runner.v1 }
            tasks:
              - name: only
            """);

        var task = Assert.Single(m.Tasks);
        Assert.False(task.Schedule.HasSchedule);
        Assert.Equal(new Schedule(), task.Schedule);
        Assert.Equal(TaskLimits.None, task.Limits);
        Assert.Empty(task.Triggers);
        Assert.Equal(ProgressDeclaration.Empty.Checkpoints, task.Progress.Checkpoints);
    }

    [Fact]
    public void The_declared_progress_is_the_model_the_migration_planner_takes()
    {
        var declaration = Valid().Tasks[0].Progress;

        var checkpoint = Assert.Single(declaration.Checkpoints);
        Assert.Equal("orders", checkpoint.Name);
        Assert.Equal(["customer"], checkpoint.Per);
        Assert.Equal(ProgressType.Time, checkpoint.Parts["modified"].Type);
        Assert.Equal("0", checkpoint.Parts["id"].Default);
        Assert.Equal(2, declaration.States.Count);

        // from nothing, every value is new: no change loses progress
        var plan = ProgressMigration.Plan(ProgressDeclaration.Empty, declaration);
        Assert.NotEmpty(plan.Steps);
        Assert.Empty(plan.Losses);

        // the manifest's own declaration, changed in the manifest, plans the way the planner always did
        var changed = Valid(Edited("{ name: id, type: int, default: \"0\" }", "{ name: id, type: text, default: \"0\" }")).Tasks[0].Progress;
        var step = Assert.Single(ProgressMigration.Plan(declaration, changed).Steps);
        Assert.Equal((MigrationKind.ConvertValue, "orders.id"), (step.Kind, step.Target));
    }

    [Fact]
    public void Renames_formats_resets_and_fill_reach_the_declaration()
    {
        var m = Valid("""
            schema: package/1
            name: p
            launch: { kind: executable, command: c }
            built_against: { protocol: dbdatakit.runner.v1 }
            tasks:
              - name: t
                progress:
                  checkpoints:
                    - name: files
                      per: [customer, region]
                      fill: all-regions
                      renamed_from: docs
                      reset: true
                      parts:
                        - { name: day, type: date, format: "yyyyMMdd", renamed_from: stamp, reset: true }
                  state:
                    - { name: cursor, type: time, format: "yyyy-MM-dd", per: [customer], renamed_from: pos, fill: all }
            """);

        var checkpoint = m.Tasks[0].Progress.Checkpoints[0];
        Assert.Equal(("docs", true, "all-regions"), (checkpoint.RenamedFrom, checkpoint.Reset, checkpoint.KeyFill));
        Assert.Equal(new DeclaredPart(ProgressType.Date, null, "stamp", "yyyyMMdd", true), checkpoint.Parts["day"]);
        Assert.Equal(new DeclaredState("cursor", ProgressType.Time, null, null, "pos", "yyyy-MM-dd", false, "all"), m.Tasks[0].Progress.States[0] with { Per = null });
        Assert.Equal(["customer"], m.Tasks[0].Progress.States[0].Per);
    }

    [Fact]
    public void A_task_with_no_progress_and_no_schedule_is_valid_and_manual()
    {
        Assert.Empty(PackageManifestValidator.Problems(Valid()));
    }

    // what is refused, each naming the file and the key ---------------------------------------------------------------------------------

    private static void AssertRefused(string yaml, string key, string words)
    {
        var diagnostics = PackageManifestReader.Load(yaml, File).Diagnostics;
        var hit = diagnostics.FirstOrDefault(d => d.KeyPath == key);
        Assert.True(hit is not null, $"no problem at '{key}'; got:\n{string.Join("\n", diagnostics)}");
        var line = hit!.ToString();
        Assert.StartsWith(File + ":", line);   // the file, then line and column
        Assert.Contains($"`{key}`", line);
        Assert.Contains(words, line);
        Assert.True(hit.Line > 0, "the problem has a line in the file");
        Assert.Null(PackageManifestReader.Load(yaml, File).Manifest);
    }

    [Fact]
    public void Declared_steps_units_and_counters_are_checked_by_name_and_kind()
    {
        AssertRefused(Edited("{ name: archive, input: DateOnly,", "{ name: download, input: DateOnly,"), "tasks[0].steps[1].name", "listed twice");
        AssertRefused(Edited("{ name: download, kind: stream,", "{ name: download, kind: streem,"), "tasks[0].steps[0].kind", "Did you mean `stream`?");
        AssertRefused(Edited("{ name: download, kind: stream, input: DateOnly }", "{ name: download, kind: stream, imput: DateOnly }"), "tasks[0].steps[0].imput", "unknown key");
        AssertRefused(Edited("counters: [rows, files]", "counters: [rows, Rows!]"), "tasks[0].counters[1]", "not a name for a counter");
        AssertRefused(Edited("counters: [rows, files]", "counters: [rows, rows]"), "tasks[0].counters[1]", "listed twice");
        AssertRefused(Edited("{ name: customer, item: Customer }", "{ name: customer, item: Customer }\n      - { name: customer }"), "tasks[0].units[1].name", "listed twice");
    }

    [Fact]
    public void Steps_units_and_counters_built_in_code_round_trip_and_leave_no_trace_when_empty()
    {
        var task = new PackageTask("t", new TaskEntry("T.Job"), new Schedule(), [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], [],
            Steps: [new TaskStep("make", StepKind.Stream), new TaskStep("send-letter", StepKind.Value, "Invoice", "LetterReceipt")], Units: [new TaskUnit("item")], Counters: ["rows"]);
        var manifest = new PackageManifest("p", null, new PackageLaunch(LaunchKind.Assembly, "a.dll"), new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), [task]);

        var text = PackageManifestWriter.Write(manifest);

        Assert.Contains("    steps:\n      - { name: make, kind: stream }\n", text);
        Assert.Contains("    counters: [rows]\n", text);
        Assert.Equal(Canonical(manifest), Canonical(Valid(text)));
        var bare = manifest with { Tasks = [new PackageTask("t", new TaskEntry("T.Job"))] };
        var bareText = PackageManifestWriter.Write(bare);
        Assert.DoesNotContain("steps:", bareText);
        Assert.DoesNotContain("units:", bareText);
        Assert.DoesNotContain("counters:", bareText);
    }

    [Fact]
    public void An_unknown_key_is_refused_with_the_nearest_key()
    {
        AssertRefused(Edited("limits: {", "limts: {"), "tasks[0].limts", "Did you mean `limits`?");
        AssertRefused(Edited("launch: {", "lunch: {"), "lunch", "Did you mean `launch`?");
        AssertRefused(Edited("misfire_grace: 5m", "misfire_graze: 5m"), "tasks[0].schedule.misfire_graze", "Did you mean `misfire_grace`?");
        AssertRefused(Edited("{ source: shop }", "{ sauce: shop }"), "tasks[0].triggers[0].webhook.sauce", "unknown key");
    }

    [Fact]
    public void A_bad_cron_is_refused_at_the_cron()
    {
        AssertRefused(Edited("cron: \"0 2 * * *\"", "cron: \"every night\""), "tasks[0].schedule.cron", "'every night' is not a cron expression");
    }

    [Fact]
    public void An_unknown_time_zone_and_a_cron_with_an_interval_are_refused()
    {
        AssertRefused(Edited("Europe/Berlin", "Mars/Olympus"), "tasks[0].schedule.time_zone", "'Mars/Olympus' is not a time zone");
        AssertRefused(Edited("schedule: { every: 15m }", "schedule: { every: 15m, cron: \"* * * * *\" }"), "tasks[1].schedule.every", "either an interval or a cron expression");
    }

    [Fact]
    public void A_connection_slot_that_is_used_but_not_declared_is_refused()
    {
        AssertRefused(Edited("{ connection: inbox, path", "{ connection: shared, path"), "tasks[0].triggers[1].file.connection", "does not declare a slot of that name");
        AssertRefused(Edited("      - { name: inbox, kind: folder }\n", ""), "tasks[0].triggers[1].file.connection", "'inbox'");
    }

    [Fact]
    public void A_progress_declaration_with_two_values_of_one_name_is_refused()
    {
        AssertRefused(Edited("- { name: last_file, type: text }", "- { name: last_file, type: text }\n        - { name: last_file, type: int }"), "tasks[0].progress.state[1].name", "declared twice");
        AssertRefused(Edited("- { name: id, type: int, default: \"0\" }", "- { name: id, type: int, default: \"0\" }\n            - { name: id, type: text }"), "tasks[0].progress.checkpoints[0].parts[2].name", "declared twice");
        AssertRefused(Edited("- name: orders\n", "- name: orders\n          parts: [{ name: a, type: int }]\n        - name: orders\n"), "tasks[0].progress.checkpoints[1].name", "declared twice");
    }

    [Fact]
    public void A_name_that_is_both_a_checkpoint_and_a_state_value_is_refused()
    {
        AssertRefused(Edited("- { name: last_file, type: text }", "- { name: orders, type: text }"), "tasks[0].progress.state[0].name", "both a checkpoint and a state value");
    }

    [Theory]
    [InlineData("timeout: 30m", "timeout: 0s", "tasks[0].limits.timeout")]
    [InlineData("memory_mb: 512", "memory_mb: 0", "tasks[0].limits.memory_mb")]
    [InlineData("cpu: 1.5", "cpu: 0", "tasks[0].limits.cpu")]
    [InlineData("concurrency: 2", "concurrency: 0", "tasks[0].limits.concurrency")]
    [InlineData("attempts: 3", "attempts: 0", "tasks[0].limits.attempts")]
    public void A_limit_of_zero_is_refused(string from, string to, string key)
    {
        AssertRefused(Edited(from, to), key, "a limit is above zero");
    }

    [Fact]
    public void A_backoff_that_stops_before_it_starts_is_refused()
    {
        AssertRefused(Edited("max_backoff: 10m", "max_backoff: 10s"), "tasks[0].limits.max_backoff", "shorter than `backoff`");
    }

    [Fact]
    public void A_trigger_loop_is_refused_naming_the_tasks()
    {
        var looped = Edited("triggers:\n      - webhook", "triggers:\n      - after: { task: report }\n      - webhook");

        AssertRefused(looped, "tasks[0].triggers[0].after", "extract runs, then report runs, then extract");
    }

    [Fact]
    public void The_trigger_cycle_helper_finds_loops_and_only_loops()
    {
        PackageManifest Of(params (string Task, string? After)[] tasks) => new("p", null, new PackageLaunch(LaunchKind.Executable, Command: "c"), new BuiltAgainst(null, null, "dbdatakit.runner.v1"),
            [.. tasks.Select(t => new PackageTask(t.Task, new TaskEntry()) { Triggers = t.After is null ? [] : [TaskTrigger.AfterRun(t.After)] })]);

        Assert.Null(TriggerGraph.FindCycle(Of(("a", null), ("b", "a"), ("c", "b"))));
        Assert.Equal(["a", "b", "a"], TriggerGraph.FindCycle(Of(("a", "b"), ("b", "a"))));
        Assert.Equal(["a", "a"], TriggerGraph.FindCycle(Of(("a", "a"))));
        Assert.Equal(["b", "c", "b"], TriggerGraph.FindCycle(Of(("a", null), ("b", "c"), ("c", "b"))));
    }

    [Fact]
    public void An_after_trigger_for_a_task_the_package_lacks_is_refused_but_another_package_is_not_judged()
    {
        AssertRefused(Edited("after: { task: extract,", "after: { task: extrakt,"), "tasks[1].triggers[0].after.task", "which this package does not have");
        Valid(Edited("after: { task: extract,", "after: { package: other-pack, task: elsewhere,"));
    }

    [Fact]
    public void Launch_and_contract_problems_are_refused()
    {
        AssertRefused(Edited("{ kind: assembly, assembly: bin/Orders.dll }", "{ kind: assembly, assembly: ../Orders.dll }"), "launch.assembly", "not a path inside the package");
        AssertRefused(Edited("{ kind: assembly, assembly: bin/Orders.dll }", "{ kind: assembly }"), "launch.assembly", "names the assembly");
        AssertRefused(Edited("{ kind: assembly, assembly: bin/Orders.dll }", "{ kind: dll, assembly: bin/Orders.dll }"), "launch.kind", "not one of assembly, executable");
        AssertRefused(Edited("protocol: dbdatakit.runner.v1", "protocol: v1"), "built_against.protocol", "not a protocol name");
        AssertRefused(Edited("dotnet: \"10.0\", ", ""), "built_against.dotnet", "which .NET runtime");
        AssertRefused(Edited("entry: { type: Orders.Tasks.Extract }", "entry: { method: Go }"), "tasks[0].entry.type", "names the type");
    }

    [Fact]
    public void Bad_values_are_refused_where_they_are()
    {
        AssertRefused(Edited("default: \"2024-01-01\"", "default: \"January\""), "tasks[0].inputs[0].default", "not a date");
        AssertRefused(Edited("type: bool, default: \"false\"", "type: bool, default: \"nope\""), "tasks[0].inputs[1].default", "not a bool");
        AssertRefused(Edited("{ name: id, type: int, default: \"0\" }", "{ name: id, type: integer }"), "tasks[0].progress.checkpoints[0].parts[1].type", "not one of");
        AssertRefused(Edited("{ name: last_file, type: text }", "{ name: last_file, type: text, format: \"yyyy\" }"), "tasks[0].progress.state[0].format", "only says how text becomes a date or a time");
        AssertRefused(Edited("per: [customer]\n          parts", "per: [customer, customer]\n          parts"), "tasks[0].progress.checkpoints[0].per[1]", "listed twice");
        AssertRefused(Edited("timeout: 30m", "timeout: soon"), "tasks[0].limits.timeout", "a number and a unit");
        AssertRefused(Edited("name: extract\n    entry", "name: Extract!\n    entry"), "tasks[0].name", "not a name for a task");
        AssertRefused(Edited("requires: [network:outside, os:linux]", "requires: [network:outside, \"two words\"]"), "tasks[0].requires[1]", "not a required label");
        AssertRefused(Edited("triggers:\n      - after: { task: extract, on: [succeeded, completed_with_failures] }", "triggers:\n      - after: { task: extract, on: [succeded] }"), "tasks[1].triggers[0].after.on[0]", "Did you mean `succeeded`?");
        AssertRefused(Edited("- webhook: { source: shop }", "- webhook: { source: shop }\n      - { webhook: { source: a }, manual: true }"), "tasks[0].triggers[1]", "exactly one of");
    }

    [Fact]
    public void A_missing_required_key_is_named()
    {
        AssertRefused("schema: package/1\nname: p\nbuilt_against: { protocol: dbdatakit.runner.v1 }\ntasks:\n  - name: t\n", "launch", "required key is missing");
        AssertRefused("schema: package/1\nname: p\nlaunch: { kind: executable, command: c }\nbuilt_against: { protocol: dbdatakit.runner.v1 }\n", "tasks", "required key is missing");
        AssertRefused("schema: package/1\nname: p\nlaunch: { kind: executable, command: c }\nbuilt_against: { protocol: dbdatakit.runner.v1 }\ntasks:\n  - entry: {}\n", "tasks[0].name", "required key is missing");
    }

    // the format --------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("schema: package/2\nname: p\n", "format 'package/2'", "newer version")]
    [InlineData("schema: package/0\nname: p\n", "'package/0'", "not a format id")]
    [InlineData("schema: engine/1\nname: p\n", "'engine/1' file", "not a package manifest")]
    [InlineData("schemaVersion: 1\nname: sales\nversion: 1.0.0\nentry: main.ts\n", "has no `schema` key", "package/1")]
    [InlineData("name: p\ntasks: []\n", "has no `schema` key", "package/1")]
    [InlineData("schema: package\nname: p\n", "'package'", "not a format id")]
    [InlineData("name: p\nschema: package/1\n", "has to be the first key", "package/1")]
    [InlineData("- just\n- a list\n", "not a package manifest", "package/1")]
    public void A_file_in_another_format_or_version_is_refused_naming_the_file(string yaml, string words, string more)
    {
        var result = PackageManifestReader.Load(yaml, File);

        Assert.False(result.Ok);
        Assert.Null(result.Manifest);
        var message = Assert.Single(result.Diagnostics).Message;
        Assert.Contains(File, message);
        Assert.Contains(words, message);
        Assert.Contains(more, message);
        Assert.All(result.Diagnostics, d => Assert.Equal(File, d.Path));
    }

    [Fact]
    public void Text_that_is_not_yaml_at_all_and_an_empty_file_are_refused_naming_the_file()
    {
        var broken = PackageManifestReader.Load("schema: package/1\nname: [unclosed\n", File);
        Assert.False(broken.Ok);
        Assert.All(broken.Diagnostics, d => Assert.Equal(File, d.Path));

        var empty = PackageManifestReader.Load("", File);
        Assert.Contains(File, Assert.Single(empty.Diagnostics).Message);

        var duplicate = PackageManifestReader.Load("schema: package/1\nname: a\nname: b\n", File);
        Assert.Contains(duplicate.Diagnostics, d => d.Message.Contains("more than once", StringComparison.Ordinal) && d.Path == File);
    }

    [Fact]
    public void Reading_throws_with_every_problem_on_its_own_line_starting_with_the_file()
    {
        var error = Assert.Throws<PackageManifestException>(() => PackageManifestReader.Read(Edited("timeout: 30m", "timeout: 0s").Replace("concurrency: 2", "concurrency: 0", StringComparison.Ordinal), File));

        var lines = error.Message.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith(File + ":", line));
        Assert.Contains("tasks[0].limits.timeout", lines[0]);
        Assert.Contains("tasks[0].limits.concurrency", lines[1]);
    }

    [Fact]
    public void A_manifest_file_is_read_from_disk_and_a_missing_one_is_named()
    {
        var folder = Path.Combine(Path.GetTempPath(), "runner-manifest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "package.yaml");
            System.IO.File.WriteAllText(path, Full);
            Assert.True(PackageManifestReader.LoadFile(path).Ok);

            var missing = PackageManifestReader.LoadFile(Path.Combine(folder, "nope.yaml"));
            Assert.False(missing.Ok);
            Assert.Contains("nope.yaml", Assert.Single(missing.Diagnostics).ToString());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_validator_works_on_a_model_built_in_code_and_names_the_key()
    {
        var task = new PackageTask("t", new TaskEntry("T"))
        {
            Schedule = Schedule.OnCron("nonsense"),
            Limits = new TaskLimits(Timeout: TimeSpan.Zero),
            Triggers = [TaskTrigger.OnFile("ghost", "*")],
        };
        var manifest = new PackageManifest("p", null, new PackageLaunch(LaunchKind.Assembly, "a.dll"), new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), [task, task]);

        var keys = PackageManifestValidator.Validate(manifest, "built/package.yaml").Select(d => d.KeyPath).ToList();

        Assert.Contains("tasks[0].schedule.cron", keys);
        Assert.Contains("tasks[0].limits.timeout", keys);
        Assert.Contains("tasks[0].triggers[0].file.connection", keys);
        Assert.Contains("tasks[1].name", keys);   // the name is used twice
        Assert.All(PackageManifestValidator.Validate(manifest, "built/package.yaml"), d => Assert.StartsWith("built/package.yaml: error", d.ToString(), StringComparison.Ordinal));
    }
}
