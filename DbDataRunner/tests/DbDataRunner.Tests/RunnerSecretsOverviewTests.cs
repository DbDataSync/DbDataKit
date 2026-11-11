using System.Net;
using DbDataKit.Connections.Inventory;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>UI06: <c>GET /api/v1/secrets/overview</c> of a runner says which secrets its store holds, which references the connections bound to its packages name, which of those no secret answers (missing) and which secrets nothing names (unused). Never a value.</summary>
public sealed class RunnerSecretsOverviewTests
{
    private const string Bound = "dbdatakit:sync:connection:crm:run";
    private const string Spare = "dbdatakit:sync:connection:old:run";
    private const string Value = "overview-secret-value-4417";

    private static async Task Bind(RunnerHarness h, string reference)
    {
        h.Install(SamplePackage.Task("fails"));
        var service = new RunnerPublishing(h.State);
        service.Bind("sample", "dev", new Dictionary<string, string> { ["source"] = reference }, null, RunnerActor.Local());
        var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
        var manifest = PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "sqlserver")] }));
        service.Publish(new PublishInput("sample", manifest, files, "dev", PublishMode.Stage), RunnerActor.Local());
        await service.ActivateAsync("sample", "dev", 1, RunnerActor.Local(), new ActivateOptions(Anyway: true));
    }

    [Fact]
    public async Task A_reference_no_secret_answers_is_missing_a_secret_nothing_names_is_unused_and_a_value_is_in_no_answer()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await Bind(h, "secret:" + Bound);
        await h.StartAsync();
        await h.CallAsync(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(Spare)}", ServerRole.Admin, new { value = Value });

        var (status, overview) = await h.GetAsync("/api/v1/secrets/overview", ServerRole.Admin);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["secret:" + Bound], overview!["missing"]!.AsArray().Select(n => (string)n!));
        Assert.Equal([Spare], overview["unused"]!.AsArray().Select(n => (string)n!));
        var held = Assert.Single(overview["secrets"]!.AsArray())!;
        Assert.Equal(Spare, (string)held["name"]!);
        Assert.NotNull((string?)held["updated"]);
        var reference = Assert.Single(overview["references"]!.AsArray())!;
        Assert.Equal("missing", (string)reference["status"]!);
        Assert.Contains("sample/ok", reference["used_by"]!.AsArray().Select(n => (string)n!));
        Assert.DoesNotContain(Value, overview.ToJsonString());

        // once the secret is written the reference is present
        await h.CallAsync(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(Bound)}", ServerRole.Admin, new { value = Value });
        var (_, after) = await h.GetAsync("/api/v1/secrets/overview", ServerRole.Admin);
        Assert.Empty(after!["missing"]!.AsArray());
        Assert.Equal([Spare], after["unused"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("present", (string)after["references"]![0]!["status"]!);
    }

    [Fact]
    public async Task A_reference_to_the_environment_is_outside_and_only_an_administrator_reads_the_overview()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await Bind(h, "env:DBDATAKIT_TEST_UI06_PASSWORD");
        await h.StartAsync();

        var (status, overview) = await h.GetAsync("/api/v1/secrets/overview", ServerRole.Admin);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("outside", (string)overview!["references"]![0]!["status"]!);
        Assert.Empty(overview["missing"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync("/api/v1/secrets/overview", ServerRole.Operator)).Status);
    }
}
