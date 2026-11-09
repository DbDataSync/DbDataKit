using System.Text.Json;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using System.Text.Json.Serialization;
using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>Plan 28b decision K8: the manifest generated from the job types, and the typed progress that the declaration describes.</summary>
public sealed class ManifestAndProgressTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static PackageManifest Generate() =>
        ManifestGenerator.ForAssembly(typeof(Orders).Assembly, "kit-samples", new PackageLaunch(LaunchKind.Assembly, "bin/DbDataKit.Runner.Kit.Tests.dll"), "The sample jobs of the kit");

    private static PackageTask Task(string name) => Generate().Tasks.Single(t => t.Name == name);

    [Fact]
    public void The_generated_manifest_passes_the_validator_and_round_trips_through_the_writer_and_reader()
    {
        var manifest = Generate();

        Assert.Empty(PackageManifestValidator.Problems(manifest));

        var text = PackageManifestWriter.Write(manifest);
        var read = PackageManifestReader.Load(text, "generated/package.yaml");
        Assert.True(read.Ok, string.Join("\n", read.Diagnostics));
        Assert.Equal(JsonSerializer.Serialize(manifest, Json), JsonSerializer.Serialize(read.Manifest, Json));
        Assert.Equal(text, PackageManifestWriter.Write(read.Manifest!));
        Assert.Contains("schema: package/1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manifest_lists_every_job_with_its_entry_and_its_launch()
    {
        var manifest = Generate();

        Assert.Equal(["backfill", "big", "customers", "daily-drop", "grant", "invoices", "orders", "probe", "queue-worker", "reporter", "stoppable", "stubborn", "trouble", "typed", "waits", "with-inputs"], manifest.Tasks.Select(t => t.Name));
        Assert.Equal(new PackageLaunch(LaunchKind.Assembly, "bin/DbDataKit.Runner.Kit.Tests.dll"), manifest.Launch);
        Assert.Equal(RunnerProtocol.V1, manifest.BuiltAgainst.Protocol);
        Assert.Equal("DbDataKit.Runner.Kit.Tests.Samples.Orders", Task("orders").Entry.Type);
        Assert.Equal("The sample jobs of the kit", manifest.Description);
    }

    [Fact]
    public void A_task_gets_its_inputs_slots_progress_steps_units_and_counters_from_its_types()
    {
        var orders = Task("orders");
        Assert.Empty(orders.Inputs);                                                   // an input record with no properties
        Assert.Equal([new ConnectionSlot("warehouse", "warehouse")], orders.Connections);
        var checkpoint = Assert.Single(orders.Progress.Checkpoints);
        Assert.Equal("orders_modified", checkpoint.Name);
        Assert.Equal(new DeclaredPart(ProgressType.Time), checkpoint.Parts["value"]);
        Assert.Equal(["rows"], orders.CounterList);

        var drop = Task("daily-drop");
        Assert.Equal(new TaskInput("day", ProgressType.Date, Required: true), Assert.Single(drop.Inputs));
        Assert.Equal([new TaskStep("download", StepKind.Stream, "DateOnly"), new TaskStep("archive", StepKind.Value, "DateOnly", "ArchiveReceipt")], drop.StepList);
        Assert.Empty(drop.Progress.Checkpoints);

        var customers = Task("customers");
        var perKey = Assert.Single(customers.Progress.Checkpoints);
        Assert.Equal("customer", perKey.Name);
        Assert.Equal(["customer"], perKey.Per);
        Assert.Equal(new DeclaredPart(ProgressType.Time), perKey.Parts["modified"]);
        var state = Assert.Single(customers.Progress.States);
        Assert.Equal(("customer_cursor", ProgressType.Text), (state.Name, state.Type));
        Assert.Equal(["customer"], state.Per);
        Assert.Equal([new TaskUnit("customer", "Customer")], customers.UnitList);
        Assert.Equal(["rows"], customers.CounterList);                                 // found beside the job: CustomerCounters is named in [RunnerDeclarations]

        var invoices = Task("invoices");
        Assert.Equal([new TaskStep("send-letter", StepKind.Value, "Invoice", "LetterReceipt")], invoices.StepList);
        Assert.Equal(["letters"], invoices.CounterList);
        Assert.Empty(invoices.Connections);
    }

    [Fact]
    public void Every_kind_of_progress_value_has_a_type_in_the_declaration()
    {
        var typed = Task("typed").Progress;

        Assert.Equal(
            [("label", ProgressType.Text), ("whole", ProgressType.Int), ("small", ProgressType.Int), ("money", ProgressType.Decimal), ("at", ProgressType.Time), ("day", ProgressType.Date)],
            typed.Checkpoints.Select(c => (c.Name, c.Parts["value"].Type)));
        Assert.Equal(
            [("flag", ProgressType.Bool), ("pace", ProgressType.Text), ("id", ProgressType.Text), ("tags", ProgressType.Json), ("maybe", ProgressType.Int), ("blob", ProgressType.Bytes)],
            typed.States.Select(s => (s.Name, s.Type)));
        Assert.DoesNotContain(typed.States, s => s.Name == "not_kept");                // no attribute: not kept
        Assert.Empty(PackageManifestValidator.Problems(Generate()));
    }

    [Fact]
    public void An_input_with_a_default_or_a_nullable_type_is_optional()
    {
        var inputs = ManifestGenerator.TaskOf(typeof(InputJob)).Inputs;

        Assert.Equal(
            [new TaskInput("since", ProgressType.Date, Required: true), new TaskInput("limit", ProgressType.Int, Required: false, Default: "100"), new TaskInput("note", ProgressType.Text, Required: false), new TaskInput("full_reload", ProgressType.Bool, Required: false, Default: "false")],
            inputs);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Every_kind_of_progress_value_is_stored_as_its_type_and_read_back_as_the_same(string engine)
    {
        using var h = new Harness(engine);

        var first = await h.Run<TypedProgress>();
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);
        Assert.Equal(new AllSeen(null, 0, 0, 0, default, default, false, Speed.Fast, Guid.Empty, null, null, -1), h.World.Seen);   // nothing stored yet: the members' defaults

        var store = h.ProgressOf("typed");
        Assert.Equal("délta/token=1", store.GetCheckpoint("label")!.Parts["value"].AsText);
        Assert.Equal(9_000_000_000, store.GetCheckpoint("whole")!.Parts["value"].AsInt);
        Assert.Equal(ProgressType.Int, store.GetCheckpoint("small")!.Parts["value"].Type);
        Assert.Equal(123456.789m, store.GetCheckpoint("money")!.Parts["value"].AsDecimal);
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T08:09:10Z"), store.GetCheckpoint("at")!.Parts["value"].AsTime);
        Assert.Equal(new DateOnly(2026, 11, 7), store.GetCheckpoint("day")!.Parts["value"].AsDate);
        Assert.True(store.GetState("flag")!.AsBool);
        Assert.Equal("Slow", store.GetState("pace")!.AsText);
        Assert.Equal(TypedProgress.TheId.ToString("D"), store.GetState("id")!.AsText);
        Assert.Equal("[\"a\",\"b\"]", store.GetState("tags")!.AsText.Replace(" ", "", StringComparison.Ordinal));
        Assert.Equal(5, store.GetState("maybe")!.AsInt);
        Assert.Equal(ProgressType.Bytes, store.GetState("blob")!.Type);
        Assert.Empty(store.StateKeys("not_kept"));

        var second = await h.Run<TypedProgress>(attempt: 1, chain: Guid.NewGuid().ToString("D"));
        Assert.Equal(RunOutcome.Succeeded, second.Outcome);
        var seen = Assert.IsType<AllSeen>(h.World.Seen);
        Assert.Equal(("délta/token=1", 9_000_000_000, 7, 123456.789m), (seen.Label, seen.Whole, seen.Small, seen.Money));
        Assert.Equal((DateTimeOffset.Parse("2026-11-07T08:09:10Z"), new DateOnly(2026, 11, 7), true, Speed.Slow), (seen.At, seen.Day, seen.Flag, seen.Pace));
        Assert.Equal((TypedProgress.TheId, 5, 3000L), (seen.Id, seen.Maybe, seen.BlobSize));
        Assert.Equal(["a", "b"], seen.Tags);

        // only the value the second run moved was written
        Assert.Equal(123458.289m, store.GetCheckpoint("money")!.Parts["value"].AsDecimal);
        Assert.Equal(2, store.GetCheckpoint("money")!.Version);
        Assert.Equal(1, store.GetCheckpoint("label")!.Version);
    }
}

public sealed record InputRecord(DateOnly Since, int Limit = 100, string? Note = null, bool FullReload = false);

[RunnerTask("with-inputs")]
public sealed class InputJob : RunnerJob<InputRecord, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}
