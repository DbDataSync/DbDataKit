using System.Text.Json.Nodes;

namespace DbDataKit.Deploy.Tests;

public sealed class RunnerCallsTests
{
    private static readonly DeployTarget Runner = new("jobs", "https://runner:8495", "r1", DeployProducts.Runner);

    [Fact]
    public void Only_a_runner_runs_a_task()
    {
        var gateway = Runner with { Name = "edge", Product = DeployProducts.Gateway };

        var refused = Assert.Throws<DeployException>(() => new RunnerCalls(new FakeLink(), gateway));

        Assert.Equal("'edge' is a gateway (https://runner:8495), not a Runner; only a Runner runs a task.", refused.Message);
    }

    [Fact]
    public async Task A_run_of_a_staged_version_goes_through_the_link_for_the_person()
    {
        using var link = new FakeLink(Runner.Url) { Answer = (method, path, _) => (method, path) == ("POST", "tasks/sales/dev/setup/run") ? (202, new JsonObject { ["run_id"] = "run-1" }) : null };
        var steps = new List<string>();

        var answer = await new RunnerCalls(link, Runner).RunStagedTaskAsync("sales", "dev", "3", "setup", new Dictionary<string, string> { ["since"] = "2026-01-01" }, "Ada", steps.Add);

        var sent = JsonNode.Parse(link.Calls.Single().Body!)!;
        Assert.Equal(("3", "Ada", "2026-01-01"), ((string?)sent["version"], (string?)sent["on_behalf_of"], (string?)sent["inputs"]!["since"]));
        Assert.Equal(("run-1", "3", "jobs", "https://runner:8495", "r1", "Ada"),
            ((string?)answer["run_id"], (string?)answer["version"], (string?)answer["target"], (string?)answer["runner"], (string?)answer["instance"], (string?)answer["on_behalf_of"]));
        Assert.Equal("Started setup of sales version 3 on dev: run run-1.", steps.Last());
    }

    [Fact]
    public async Task A_run_of_the_served_version_names_no_version()
    {
        using var link = new FakeLink(Runner.Url) { Answer = (_, _, _) => (202, new JsonObject { ["run_id"] = "run-2" }) };

        var answer = await new RunnerCalls(link, Runner).RunServedTaskAsync("sales", "prod", "drain", null, "Ada", _ => { });

        Assert.Null(JsonNode.Parse(link.Calls.Single().Body!)!["version"]);
        Assert.Equal("served", (string?)answer["version"]);
    }

    [Theory]
    [InlineData(409, 409, "sales/dev/setup is queued or running already on jobs; wait for that run to end.")]
    [InlineData(403, 403, "The runner refused to run sales/dev/setup (403): The caller is not an operator.")]
    [InlineData(404, 404, "The runner refused to run sales/dev/setup (404): The caller is not an operator.")]
    [InlineData(400, 409, "The runner refused to run sales/dev/setup (400): The caller is not an operator.")]
    public async Task A_refusal_of_a_run_keeps_the_runner_s_words_with_a_status_of_its_kind(int answered, int status, string message)
    {
        using var link = new FakeLink(Runner.Url) { Answer = (_, _, _) => (answered, new JsonObject { ["error"] = "The caller is not an operator." }) };

        var refused = await Assert.ThrowsAsync<DeployException>(() => new RunnerCalls(link, Runner).RunStagedTaskAsync("sales", "dev", "3", "setup", null, "Ada", _ => { }));

        Assert.Equal((status, message), (refused.Status, refused.Message));
    }

    [Fact]
    public async Task A_runner_that_cannot_be_reached_is_a_502_naming_the_link()
    {
        using var link = new FakeLink(Runner.Url) { Unreachable = true };

        var refused = await Assert.ThrowsAsync<DeployException>(() => new RunnerCalls(link, Runner).TestBindingAsync("sales", "dev", "source", "read"));

        Assert.Equal((502, "https://runner:8495 could not be reached (Connection refused)."), (refused.Status, refused.Message));
    }

    [Fact]
    public async Task A_run_is_read_with_only_the_steps_recorded_while_it_ran()
    {
        var run = Guid.NewGuid();
        using var link = new FakeLink(Runner.Url)
        {
            Answer = (_, path, _) => FakeLink.PathOf(path) switch
            {
                var p when p == $"runs/{run}" => (200, new JsonObject { ["id"] = run.ToString(), ["task"] = "contract", ["outcome"] = "succeeded", ["started"] = "2026-11-23T10:00:00Z", ["finished"] = "2026-11-23T10:05:00Z", ["package_version"] = "3" }),
                "tasks/sales/dev/contract/steps/contract-report" => (200, new JsonObject
                {
                    ["steps"] = new JsonArray(
                        new JsonObject { ["key"] = "earlier", ["started"] = "2026-11-23T09:00:00Z", ["result"] = new JsonObject { ["hash"] = "old" } },
                        new JsonObject { ["key"] = "this", ["started"] = "2026-11-23T10:01:00Z", ["completed"] = "2026-11-23T10:02:00Z", ["result"] = new JsonObject { ["hash"] = "abc" } }),
                }),
                _ => null,
            },
        };
        var calls = new RunnerCalls(link, Runner);

        var read = await calls.RunOfAsync("sales", "dev", run, "contract-report");

        Assert.Equal(("contract", "3"), ((string?)read!["task"], (string?)read["version"]));
        var step = Assert.Single(read["steps"]!.AsArray());
        Assert.Equal(("this", "abc"), ((string?)step!["key"], (string?)step["result"]!["hash"]));
        Assert.Null(await calls.RunOfAsync("sales", "dev", Guid.NewGuid(), null));
    }

    [Fact]
    public async Task A_binding_test_answers_what_the_runner_found_and_where()
    {
        using var link = new FakeLink(Runner.Url)
        {
            Answer = (method, path, _) => (method, path) == ("POST", "bindings/dev/integration/sales/test") ? (200, new JsonObject { ["ok"] = false, ["step"] = "open", ["message"] = "Login failed for user 'etl'.", ["connected"] = false }) : null,
        };

        var answer = await new RunnerCalls(link, Runner).TestBindingAsync("sales", "dev", "source", "read");

        Assert.Equal(("source", false, "open", "Login failed for user 'etl'.", "jobs", "r1"),
            ((string?)answer["slot"], (bool)answer["ok"]!, (string?)answer["step"], (string?)answer["message"], (string?)answer["target"], (string?)answer["instance"]));
        Assert.Equal("read", (string?)JsonNode.Parse(link.Calls.Single().Body!)!["role"]);
    }
}
