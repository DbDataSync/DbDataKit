using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// The answer of a run a person waits for (<c>GET /api/v1/runs/{id}/answer?on_behalf_of=</c>): handed once to the console that started the run, for the same person, from the runner's memory; another person,
/// a viewer and a call that names no person are refused; nothing of the answer is in the run's log or the audit. Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept
/// </summary>
public sealed class RunAnswerApiTests
{
    private const string RowValue = "row-value-5512";

    [Fact]
    public async Task An_answer_goes_once_to_the_person_who_started_the_run_and_to_nobody_else_and_is_kept_nowhere()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        var (accepted, body) = await h.PostAsync("/api/v1/tasks/sample/dev/ok/run", ServerRole.Operator, new { on_behalf_of = "Ada" });
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        var run = Guid.Parse((string)body!["run_id"]!);
        // what a task's Answer call does on the runner: the answer is held in this process's memory for the run
        h.Engine.Answers!.Put(run, new JsonObject { ["rows"] = new JsonArray(new JsonArray(1, RowValue)) }.ToJsonString());
        var path = $"/api/v1/runs/{run}/answer?on_behalf_of=Ada";

        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync(path, ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync($"/api/v1/runs/{run}/answer", ServerRole.Operator)).Status);
        var (other, why) = await h.GetAsync($"/api/v1/runs/{run}/answer?on_behalf_of=Bob", ServerRole.Operator, subject: "bob");
        Assert.Equal(HttpStatusCode.Forbidden, other);
        Assert.Contains("only to the person who asked", (string)why!["error"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync(path, ServerRole.Operator, subject: "bob")).Status);   // Bob cannot take Ada's answer by naming her
        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync($"/api/v1/runs/{Guid.NewGuid()}/answer?on_behalf_of=Ada", ServerRole.Operator)).Status);

        var (taken, answer) = await h.GetAsync(path, ServerRole.Operator);
        Assert.Equal(HttpStatusCode.OK, taken);
        Assert.Equal("done", (string?)answer!["state"]);
        Assert.Equal(RowValue, (string?)answer["answer"]!["rows"]![0]![1]);

        // taken once: once the run has ended, the runner says the answer is gone and why
        await h.WaitAsync(async () => h.Runs.Get(run) is { Run.Finished: not null } ended ? ended : null, "the run to end");
        var (again, gone) = await h.GetAsync(path, ServerRole.Operator);
        Assert.Equal(HttpStatusCode.Gone, again);
        Assert.Contains("taken already", (string)gone!["error"]!);

        Assert.DoesNotContain(h.Runs.Logs(run, limit: 1000), l => l.Message.Contains(RowValue, StringComparison.Ordinal));
        Assert.DoesNotContain(h.Audit.Read(new AuditQuery(Limit: 500)), e => (e.Entry.Detail ?? "").Contains(RowValue, StringComparison.Ordinal));
    }
}
