using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.Api.Runner;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c E21 (Sync half, step d): the Setup card's Apply and the replication-wide page's Apply start the set's manual task <c>provision</c> on the linked runner for the signed-in person, and the run is read back from the
/// runner with the steps the task reported. The server opens no connection for it: the fake runner is all there is.
/// </summary>
public sealed class ProvisioningOnRunnerTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = factory.CreateClient();

    private static string NewName() => "prov-" + Guid.NewGuid().ToString("N")[..10];

    private (string Task, IReadOnlyDictionary<string, string>? Inputs, string? OnBehalfOf) LastRequest() => factory.Runner.RunNowRequests.Last(r => r.Task == RunnerTaskNames.Provision);

    [Fact]
    public async Task The_Setup_cards_Apply_starts_the_provision_task_for_the_person_with_the_mapping_and_its_action()
    {
        var name = NewName();
        factory.RunnerOwns(name);

        var response = await _client.PostAsync($"/api/replications/{name}/table-mappings/main/provisioning/createTargetTable/run", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = (await response.Content.ReadFromJsonAsync<ProvisioningRunStarted>(Web))!;
        Assert.NotNull(started.RunId);
        factory.Runner.Finish(Guid.Parse(started.RunId!), "succeeded", DateTimeOffset.UtcNow);   // the fixture is shared: one provisioning run at a time
        var (_, inputs, person) = LastRequest();
        Assert.Equal(name, inputs!["replication"]);
        Assert.Equal("main", inputs["mapping"]);
        Assert.Equal("createTargetTable", inputs["action"]);
        Assert.Equal("", inputs["steps"]);
        Assert.Equal("repo", inputs["repo_dir"]);
        Assert.False(string.IsNullOrWhiteSpace(person));
        Assert.Equal(person, started.OnBehalfOf);
    }

    [Fact]
    public async Task The_replication_wide_Apply_sends_the_chosen_steps_and_refuses_none()
    {
        var name = NewName();
        factory.RunnerOwns(name);

        var response = await _client.PostAsJsonAsync($"/api/replications/{name}/provisioning/run", new { stepIds = new[] { "a1", "b2" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        factory.Runner.Finish(Guid.Parse((await response.Content.ReadFromJsonAsync<ProvisioningRunStarted>(Web))!.RunId!), "succeeded", DateTimeOffset.UtcNow);
        var (_, inputs, _) = LastRequest();
        Assert.Equal("a1,b2", inputs!["steps"]);
        Assert.Equal("", inputs["mapping"]);

        var none = await _client.PostAsJsonAsync($"/api/replications/{name}/provisioning/run", new { stepIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task The_run_is_read_back_with_the_steps_the_task_reported()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);
        var run = factory.Runner.AddRun(RunnerTaskNames.Provision, "failed", started, started.AddSeconds(2), message: "The step 'Create table' failed: no permission");
        factory.Runner.AddStep(RunnerTaskNames.Provision, RunnerProvisioning.AppliedStep, "main",
            """{"replication":"r","mapping":"main","steps":[{"title":"Create table","succeeded":false,"error":"no permission"}]}""", started.AddSeconds(1));

        var view = (await _client.GetFromJsonAsync<ProvisioningRunView>($"/api/replications/{name}/provisioning/runs/{run.Id}", Web))!;

        Assert.Equal("failed", view.Outcome);
        var step = Assert.Single(view.Steps!);
        Assert.Equal(("Create table", false, "no permission"), (step.Title, step.Succeeded, step.Error));

        var drain = factory.Runner.AddRun(RunnerAccess.TaskOf(name), "succeeded", started, started.AddSeconds(1));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/replications/{name}/provisioning/runs/{drain.Id}")).StatusCode);
    }
}
