using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataSync.Api.Runner;
using DbDataSync.State;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>The API with a runner that is a fake (<see cref="FakeRunnerRuns"/>) behind its real link and owner file: what the replications the runner owns show of the runs the runner ran (plan 28c E13).</summary>
public sealed class RunnerOwnedApiFactory : TestApiFactory
{
    public FakeRunnerRuns Runner { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<RunnerAccess>();
        services.AddSingleton(sp => new RunnerAccess(sp.GetRequiredService<RunnerLinkFile>(), _ => Runner));
    }

    /// <summary>
    /// Links the runner (the file is read at every use) and makes the replication one of this server's (its folder in the configuration): every replication of this server is the runner's
    /// since plan 28c E22, and the readers ask the runner for the tasks of the replications they know.
    /// </summary>
    public void RunnerOwns(string replication)
    {
        var file = Services.GetRequiredService<RunnerLinkFile>();
        file.SetLink(new RunnerLink("runner-1", "prod", "sales-set"));
        Directory.CreateDirectory(Path.Combine(RepoRoot, "config", "replications", replication));
    }
}

/// <summary>
/// Plan 28c E13 over the Sync API: the history of a replication the runner owns merges the runner's passes and drains with what this server holds from before the move, a runner that cannot be asked is said and never an empty
/// history, the run's log endpoint serves the runner's lines, and the live view sends a line of a run on the runner to the hub within a second of its being written.
/// </summary>
public sealed class RunnerOwnedRunsTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddHours(-1);

    private sealed record Page(List<TaskRunRecord> Runs, string? NextCursor, string? RunnerUnavailable);

    /// <summary>A replication that exists in this server's history under a name of its own (the owner file is per name).</summary>
    private static string NewName() => "owned-" + Guid.NewGuid().ToString("N")[..10];

    private PassRecord Pass(string replication, string mapping, string status, DateTimeOffset started, Guid? drain = null, string kind = "Primary", long read = 0, long written = 0, string? summary = null, string? failure = null) =>
        new(Guid.NewGuid(), drain ?? Guid.NewGuid(), replication, kind, mapping, null, status, started, started.AddSeconds(3), read, written, summary, failure, null, null);

    private async Task<Page> HistoryAsync(string replication, string query = "")
    {
        var response = await _client.GetAsync($"/api/replications/{replication}/runs{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Page>(JsonOptions))!;
    }

    [Fact]
    public async Task Cancel_of_a_run_the_supervisor_does_not_know_is_forwarded_to_the_runner_which_answers_for_its_own_runs()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        var going = factory.Runner.AddRun(DbDataSync.Api.Runner.RunnerAccess.TaskOf(name), "running", DateTimeOffset.UtcNow.AddMinutes(-1));

        var cancelled = await _client.PostAsync($"/api/runs/{going.Id}/cancel", null);
        var unknown = await _client.PostAsync($"/api/runs/{Guid.NewGuid()}/cancel", null);
        factory.Runner.Down = true;
        var down = await _client.PostAsync($"/api/runs/{going.Id}/cancel", null);
        factory.Runner.Down = false;

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal(2, factory.Runner.CancelRequests.Count);   // the unreachable runner was asked and did not answer
        Assert.Equal(going.Id, factory.Runner.CancelRequests[0]);
    }

    [Fact]
    public async Task The_history_merges_the_runners_passes_and_drains_with_this_servers_own_with_counters_timings_and_failures()
    {
        var name = NewName();
        var task = DbDataSync.Api.Runner.RunnerAccess.TaskOf(name);
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<TaskRunStore>();
        // from before the move: one pass of this server's own
        var old = queue.Enqueue(name, RunKind.Primary, "orders");
        runs.BeginRun(old, 1);
        runs.CompleteRun(old, RunStatus.Succeeded, 4, 4, null);
        factory.RunnerOwns(name);
        var drainOk = Guid.NewGuid();
        var good = Pass(name, "orders", "Succeeded", DateTimeOffset.UtcNow.AddMinutes(-9), drainOk, read: 12, written: 10);
        var expired = Pass(name, "orders", "Failed", DateTimeOffset.UtcNow.AddMinutes(-7), summary: "the source discarded the history", failure: RunFailureKinds.PositionExpired);
        var lost = Pass(name, "orders", "Succeeded", DateTimeOffset.UtcNow.AddMinutes(-5), summary: "Not run: mapping 'orders' is still loading — an initial load is in flight and its watermark is not durable yet.");
        foreach (var p in new[] { good, expired, lost }) factory.Runner.AddPass(task, p);
        // the drain of the failed pass fails with it: it is not a row of its own; a drain that crashed with no pass to say why is; one that is going is
        factory.Runner.AddRun(task, "failed", expired.StartedAt, expired.EndedAt, 0, "orders#1a2b3c4d", "Permanent", "The pass over 'orders' ended Failed", expired.DrainRunId);
        var crashed = factory.Runner.AddRun(task, "failed", DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-3).AddSeconds(2), 0, null, "ProcessExited", "The process exited with code 139.");
        var going = factory.Runner.AddRun(task, "running", DateTimeOffset.UtcNow.AddMinutes(-1));
        factory.Runner.AddRun(task, "succeeded", good.StartedAt, good.EndedAt, 10, id: drainOk);

        var page = await HistoryAsync(name);

        Assert.Null(page.RunnerUnavailable);
        // newest first (this server's own pass was queued just now: the move is only a file): then the drain going, the crashed drain, the lost-race pass, the failed pass, the good pass
        Assert.Equal([old, going.Id, crashed.Id, lost.RunId, expired.RunId, good.RunId], page.Runs.Select(r => r.RunId));
        var byId = page.Runs.ToDictionary(r => r.RunId);
        Assert.Equal((RunStatus.Succeeded, 12L, 10L), (byId[good.RunId].Status, byId[good.RunId].RowsRead, byId[good.RunId].RowsWritten));
        Assert.Equal(good.StartedAt, byId[good.RunId].StartedAtUtc);
        Assert.Equal(good.EndedAt, byId[good.RunId].EndedAtUtc);
        // a failed run carries its mapping and the kind Sync classified, as the supervisor path's does
        Assert.Equal((RunStatus.Failed, "orders", RunFailureKinds.PositionExpired, "the source discarded the history"), (byId[expired.RunId].Status, byId[expired.RunId].MappingName, byId[expired.RunId].FailureKind, byId[expired.RunId].ErrorSummary));
        // the pass that lost the initial-load race is a run that Succeeded and says why it did nothing
        Assert.Equal((RunStatus.Succeeded, 0L, 0L), (byId[lost.RunId].Status, byId[lost.RunId].RowsRead, byId[lost.RunId].RowsWritten));
        Assert.StartsWith("Not run:", byId[lost.RunId].ErrorSummary, StringComparison.Ordinal);
        Assert.Null(byId[lost.RunId].FailureKind);
        Assert.Equal((RunStatus.Failed, "ProcessExited"), (byId[crashed.Id].Status, byId[crashed.Id].FailureKind));
        Assert.Equal(RunStatus.Running, byId[going.Id].Status);
        Assert.Equal(RunStatus.Succeeded, byId[old].Status);

        // filters apply to both sources, and a page ends where the cursor says
        Assert.Equal([expired.RunId], (await HistoryAsync(name, "?status=Failed&kind=Primary&mappingName=orders")).Runs.Select(r => r.RunId));
        var first = await HistoryAsync(name, "?limit=2");
        Assert.Equal([old, going.Id], first.Runs.Select(r => r.RunId));
        Assert.NotNull(first.NextCursor);
        var second = await HistoryAsync(name, $"?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal([crashed.Id, lost.RunId], second.Runs.Select(r => r.RunId));
        var third = await HistoryAsync(name, $"?limit=2&cursor={Uri.EscapeDataString(second.NextCursor!)}");
        Assert.Equal([expired.RunId, good.RunId], third.Runs.Select(r => r.RunId));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task A_runner_that_cannot_be_asked_is_said_in_the_history_and_this_servers_own_runs_are_still_listed()
    {
        var name = NewName();
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<TaskRunStore>();
        var old = queue.Enqueue(name, RunKind.Primary, "orders");
        runs.BeginRun(old, 1);
        runs.CompleteRun(old, RunStatus.Failed, 0, 0, "boom", RunFailureKinds.PositionExpired);
        factory.RunnerOwns(name);
        factory.Runner.Down = true;
        try
        {
            var page = await HistoryAsync(name);

            Assert.Contains("could not be reached", page.RunnerUnavailable);
            Assert.Equal([old], page.Runs.Select(r => r.RunId));
        }
        finally
        {
            factory.Runner.Down = false;
        }

    }

    [Fact]
    public async Task The_runs_log_endpoint_serves_the_runners_lines_for_a_drain_and_for_a_pass_and_says_when_the_runner_is_down()
    {
        var name = NewName();
        var task = DbDataSync.Api.Runner.RunnerAccess.TaskOf(name);
        factory.RunnerOwns(name);
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        var drain = factory.Runner.AddRun(task, "running", started);
        var one = factory.Runner.Log(drain.Id, "line before the pass", started);
        var inside = factory.Runner.Log(drain.Id, "line during the pass", started.AddSeconds(11));
        factory.Runner.Log(drain.Id, "line long after the pass", started.AddSeconds(60));
        var pass = Pass(name, "orders", "Succeeded", started.AddSeconds(10), drain.Id);
        factory.Runner.AddPass(task, pass);

        var all = await _client.GetFromJsonAsync<List<LogEntryRecord>>($"/api/runs/{drain.Id}/logs", JsonOptions);
        var later = await _client.GetFromJsonAsync<List<LogEntryRecord>>($"/api/runs/{drain.Id}/logs?sinceId={one}", JsonOptions);
        var ofPass = await _client.GetFromJsonAsync<List<LogEntryRecord>>($"/api/runs/{pass.RunId}/logs", JsonOptions);

        Assert.Equal(["line before the pass", "line during the pass", "line long after the pass"], all!.Select(l => l.Message));
        Assert.Equal(["line during the pass", "line long after the pass"], later!.Select(l => l.Message));
        Assert.Equal(["line during the pass"], ofPass!.Select(l => l.Message));   // the drain's lines between the pass's start and end
        Assert.Equal(inside, ofPass[0].Id);
        Assert.All(all, l => Assert.Equal(drain.Id, l.RunId));

        // and the run itself: a drain going is Running, a pass is what it recorded, an id nobody has is 404
        var run = await _client.GetFromJsonAsync<TaskRunRecord>($"/api/runs/{drain.Id}", JsonOptions);
        Assert.Equal((RunStatus.Running, name), (run!.Status, run.TaskName));
        Assert.Equal(RunStatus.Succeeded, (await _client.GetFromJsonAsync<TaskRunRecord>($"/api/runs/{pass.RunId}", JsonOptions))!.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/runs/{Guid.NewGuid()}")).StatusCode);

        factory.Runner.Down = true;
        try
        {
            var down = await _client.GetAsync($"/api/runs/{drain.Id}/logs");
            Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
            Assert.Contains("could not be reached", await down.Content.ReadAsStringAsync());
        }
        finally
        {
            factory.Runner.Down = false;
        }
    }

    [Fact]
    public async Task A_line_of_a_run_on_the_runner_reaches_the_hub_within_a_second_of_being_written_and_the_end_follows()
    {
        var name = NewName();
        var task = DbDataSync.Api.Runner.RunnerAccess.TaskOf(name);
        factory.RunnerOwns(name);
        var drain = factory.Runner.AddRun(task, "running", DateTimeOffset.UtcNow);
        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{factory.Server.BaseAddress}hubs/run", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Caller.Handler(UserRole.Admin, "test-admin", "Test Admin", factory.Server.CreateHandler());
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        var arrived = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("logLine", entry => arrived.TryAdd(entry.GetProperty("message").GetString()!, DateTimeOffset.UtcNow));
        hub.On<JsonElement>("runCompleted", payload => completed.TrySetResult(payload));
        await hub.StartAsync();
        await hub.InvokeAsync("JoinRun", drain.Id.ToString());

        // lines written while the run goes, a few hundred milliseconds apart: each reaches the hub within a second
        var written = new Dictionary<string, DateTimeOffset>();
        for (var i = 1; i <= 4; i++)
        {
            var message = $"live line {i}";
            written[message] = DateTimeOffset.UtcNow;
            factory.Runner.Log(drain.Id, message);
            await Task.Delay(700);
        }

        foreach (var (message, at) in written)
        {
            Assert.True(arrived.TryGetValue(message, out var seen), $"'{message}' never reached the hub");
            Assert.True(seen - at < TimeSpan.FromSeconds(1), $"'{message}' took {(seen - at).TotalMilliseconds:0} ms");
        }

        factory.Runner.Log(drain.Id, "last line");
        factory.Runner.Finish(drain.Id, "succeeded", DateTimeOffset.UtcNow, 9);
        var end = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("Succeeded", 9), (end.GetProperty("status").GetString(), end.GetProperty("rowsWritten").GetInt32()));
        Assert.Contains("last line", arrived.Keys);   // the lines that came last are sent before the end is announced
        // a client that joins late reads the endpoint first: everything is there
        var lines = await _client.GetFromJsonAsync<List<LogEntryRecord>>($"/api/runs/{drain.Id}/logs", JsonOptions);
        Assert.Equal(5, lines!.Count);
    }

    [Fact]
    public async Task The_shared_monitoring_record_of_a_failed_run_on_the_runner_carries_the_mapping_and_the_failure_kind_and_an_unreachable_runner_is_an_error_not_an_empty_history()
    {
        var name = NewName();
        var task = DbDataSync.Api.Runner.RunnerAccess.TaskOf(name);
        factory.RunnerOwns(name);
        var history = factory.Services.GetRequiredService<IRunHistory>();
        Assert.Null(await history.LastRunAsync(name));   // owned, and nothing ran there yet

        factory.Runner.AddPass(task, Pass(name, "orders", "Succeeded", DateTimeOffset.UtcNow.AddMinutes(-5), read: 3, written: 3));
        factory.Runner.AddPass(task, Pass(name, "customers", "Failed", DateTimeOffset.UtcNow.AddMinutes(-1), summary: "history discarded", failure: RunFailureKinds.PositionExpired));
        factory.Runner.AddPass(task, Pass(name, "customers", "Succeeded", DateTimeOffset.UtcNow.AddMinutes(-30), kind: "BulkLoad"));   // a reload says nothing about being overdue

        var last = (await history.LastRunAsync(name))!;

        Assert.Equal(RunOutcome.Failed, last.Outcome);
        Assert.Equal("customers", last.FailedItem);
        Assert.Equal(new RunFailure(RunFailureKinds.PositionExpired, "history discarded"), last.Failure);
        var health = await new HealthEvaluator().EvaluateAsync([new JobExpectation(name)], history);
        Assert.Contains("customers", health.Single().Findings.Single(f => f.Rule == HealthRule.Failed).Message);
        Assert.Contains(RunFailureKinds.PositionExpired, health.Single().Findings.Single(f => f.Rule == HealthRule.Failed).Message);

        factory.Runner.Down = true;
        try
        {
            var down = await Assert.ThrowsAsync<RunnerUnavailableException>(() => history.LastRunAsync(name));
            Assert.Contains("could not be reached", down.Message);
        }
        finally
        {
            factory.Runner.Down = false;
        }
    }
}
