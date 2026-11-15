using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.Api.Runner;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Owner decision 2026-11-14 (plan 28c E22): Run now while a drain of the replication is already going starts nothing new and joins it. The API answers with that run's id and when it began, so the screen opens its
/// live panel and says so; with no drain going, Run now starts one and says nothing of joining.
/// </summary>
public sealed class RunNowJoinsTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record Trigger(List<Guid> RunIds, DateTimeOffset? JoinedSince);

    private static string NewName() => "joins-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<Trigger> RunNowAsync(string replication)
    {
        var response = await _client.PostAsync($"/api/replications/{replication}/runs", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Trigger>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    [Fact]
    public async Task Run_now_while_a_drain_is_going_joins_it_and_says_since_when()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        var since = DateTimeOffset.UtcNow.AddMinutes(-7);
        var going = factory.Runner.AddRun(RunnerAccess.TaskOf(name), "running", since);

        var joined = await RunNowAsync(name);

        Assert.Equal(going.Id, Assert.Single(joined.RunIds));
        Assert.Equal(since, joined.JoinedSince!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Run_now_with_no_drain_going_starts_one_and_joins_nothing()
    {
        var name = NewName();
        factory.RunnerOwns(name);

        var started = await RunNowAsync(name);

        Assert.Single(started.RunIds);
        Assert.Null(started.JoinedSince);
    }
}
