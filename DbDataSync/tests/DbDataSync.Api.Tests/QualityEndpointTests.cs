using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbDataKit.State;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.MsSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>Plan 23a FQ17: <c>/api/quality</c>, the results of the replications' checks as a console reads them: the overview, one check's history and an evaluation on request. No engine is needed: a target that cannot be reached is an <c>error</c> result.</summary>
public sealed class QualityEndpointTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
    private readonly HttpClient _client = factory.CreateClient();

    private QualityStore Store => new(factory.Services.GetRequiredService<DbDataSync.State.StateDatabase>());

    private static CheckResultRecord Fail(string dataset, string id, params string[] samples) => new(id, dataset, CheckOutcome.Fail, At, 2, 0, samples, "run-7", "block", "alert");

    [Fact]
    public async Task The_overview_lists_the_datasets_checks_and_block_and_says_this_server_can_run_them()
    {
        var dataset = $"sqlserver://sql01/ovw{Guid.NewGuid():N}.dbo.orders";
        Store.Record(Fail(dataset, "orders_name_present", "Id=2"));

        var document = JsonNode.Parse(await _client.GetStringAsync("/api/quality"))!;

        Assert.True((bool)document["can_run"]!);
        var mine = Assert.Single(document["datasets"]!.AsArray(), d => (string)d!["dataset"]! == dataset)!;
        Assert.Equal(1, (int)mine["blocking"]!);
        var check = mine["checks"]![0]!;
        Assert.Equal(("orders_name_present", "fail", true), ((string)check["check_id"]!, (string)check["status"]!, (bool)check["blocking"]!));
    }

    [Fact]
    public async Task The_history_of_a_check_has_its_sample_keys_and_asks_for_a_dataset_and_a_check()
    {
        var dataset = $"sqlserver://sql01/hist{Guid.NewGuid():N}.dbo.orders";
        Store.Record(Fail(dataset, "orders_name_present", "Id=2", "Id=5"));

        var history = JsonNode.Parse(await _client.GetStringAsync($"/api/quality/history?dataset={Uri.EscapeDataString(dataset)}&check=orders_name_present"))!;

        var result = Assert.Single(history["results"]!.AsArray())!;
        Assert.Equal(["Id=2", "Id=5"], result["sample_keys"]!.AsArray().Select(k => (string)k!));
        Assert.Equal("run-7", (string)result["run_id"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/quality/history?dataset=x")).StatusCode);
    }

    [Fact]
    [Trait("Category", "Slow")]   // 16 s: waits out an unreachable target (docs/testing-environment.md, "Fast verifier runs")
    public async Task A_run_evaluates_the_replications_checks_and_records_a_result_even_when_the_target_cannot_be_reached()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var connection = $"q-{suffix}";
        var replication = $"q-repl-{suffix}";
        (await _client.PutAsJsonAsync($"/api/connections/{connection}", new ConnectionInput
        {
            Name = connection, DriverType = DriverIds.MsSql, Host = "127.0.0.1", Port = 1, Database = "Nowhere",
            Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true", ["Connect Timeout"] = "2" }, AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "unused",
        }, JsonOptions)).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/replications/{replication}", new ReplicationTaskConfig
        {
            Name = replication, Enabled = false,
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = connection, Database = "Src" }, Target = new EndpointRef { ConnectionName = connection, Database = "Nowhere" } },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = MsSqlDriverKinds.Cdc }, Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable }, Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge } },
        }, JsonOptions)).EnsureSuccessStatusCode();
        File.AppendAllText(Path.Combine(factory.RepoRoot, "config", "replications", replication, "task.yaml"),
            $"\nchecks:\n  - {{id: name_present, kind: not_null, dataset: dbo.orders_{suffix}, column: Name, severity: block}}\n");

        var table = $"dbo.orders_{suffix}";
        Assert.DoesNotContain(JsonNode.Parse(await _client.GetStringAsync("/api/quality"))!["datasets"]!.AsArray(), d => (string)d!["dataset"]! == table);      // nothing is evaluated by looking

        var response = await _client.PostAsJsonAsync("/api/quality/run", new { dataset = table });
        var run = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(replication, (string)run["replication"]!);
        Assert.Equal((1, 1), ((int)run["evaluated"]!, (int)run["errors"]!));
        var overview = JsonNode.Parse(await _client.GetStringAsync("/api/quality"))!;
        var recorded = Assert.Single(overview["datasets"]!.AsArray(), d => (string)d!["dataset"]! == table)!;
        Assert.Equal("error", (string)recorded["checks"]![0]!["status"]!);
        Assert.False((bool)recorded["checks"]![0]!["blocking"]!);        // a check that cannot run alerts; it blocks only with on_error: block (QF10)
    }

    [Fact]
    public async Task A_run_names_what_is_missing_a_dataset_nothing_has_checks_about_is_not_found()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/quality/run", new { })).StatusCode);
        var unknown = await _client.PostAsJsonAsync("/api/quality/run", new { dataset = "sqlserver://nowhere/db.dbo.none" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("No replication has checks", await unknown.Content.ReadAsStringAsync());
    }
}

/// <summary>Who may do what on <c>/api/quality</c>: a viewer reads, evaluating needs an administrator (it opens connections to the target).</summary>
public sealed class QualityEndpointRolesTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    [Fact]
    public async Task A_viewer_reads_the_results_and_cannot_run_them_and_nobody_signed_in_reads_nothing()
    {
        var viewer = await factory.SignedInAsAsync(UserRole.Viewer);
        var admin = await factory.SignedInAsAsync(UserRole.Admin);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/quality")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/quality/history?dataset=a&check=b")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/quality/run", new { dataset = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/quality/run", new { dataset = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/quality")).StatusCode);
    }

    [Fact]
    public async Task An_administrator_releases_a_block_and_a_viewer_cannot()
    {
        var viewer = await factory.SignedInAsAsync(UserRole.Viewer);
        var admin = await factory.SignedInAsAsync(UserRole.Admin);
        var dataset = $"sqlserver://sql01/rel{Guid.NewGuid():N}.dbo.orders";
        var store = new QualityStore(factory.Services.GetRequiredService<DbDataSync.State.StateDatabase>());
        store.Record(new CheckResultRecord("orders_name_present", dataset, CheckOutcome.Fail, DateTimeOffset.Parse("2026-11-08T12:00:00Z"), 2, 0, ["Id=2"], "run-7", "block", "alert"));

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/quality/release", new { dataset, check = "orders_name_present" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/quality/release", new { dataset })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/quality/release", new { dataset, check = "orders_name_present" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/quality/release", new { dataset, check = "orders_name_present" })).StatusCode);
        Assert.Empty(store.BlocksOf(dataset));
        var overview = JsonNode.Parse(await admin.GetStringAsync("/api/quality"))!;
        Assert.True((bool)overview["can_release"]!);
        Assert.NotNull(store.Status(dataset, "orders_name_present")!.ReleasedSince);
    }
}
