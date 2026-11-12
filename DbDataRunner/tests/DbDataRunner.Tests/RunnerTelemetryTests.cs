using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Telemetry;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>Plan 22a GP28: the runner with <c>telemetry: enabled</c> makes the request span, counts the task start and the run by outcome and duration; the task's output and a failure's text are in no attribute.</summary>
public sealed class RunnerTelemetryTests
{
    [Fact]
    public async Task A_task_start_and_its_run_are_counted_and_the_request_has_a_span()
    {
        using var capture = TelemetryCapture.Start();
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        File.WriteAllText(Path.Combine(h.Directory, "server.yaml"), "telemetry:\n  enabled: true\n  endpoint: http://127.0.0.1:1\n");
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var (queued, started) = await h.PostAsync("/api/v1/tasks/sample/dev/ok/run");
        Assert.Equal(HttpStatusCode.Accepted, queued);
        var runId = (string)started!["run_id"]!;
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&channel=dev&task=ok")).FirstOrDefault(r => (string?)r!["id"] == runId && r["finished"] is not null), "the run to end");
        capture.Flush(h.Services);

        Assert.Equal(1, capture.Sum("dbdatakit.runner.task_starts", (HostTelemetry.NameTag, "ok")));
        Assert.Equal(1, capture.Sum("dbdatakit.runs", (HostTelemetry.ProductTag, "dbdatarunner"), (HostTelemetry.OutcomeTag, "succeeded")));
        Assert.Equal(1, capture.Count("dbdatakit.run.duration", (HostTelemetry.ProductTag, "dbdatarunner")));
        Assert.Contains(capture.Spans, s => (string?)s.GetTagItem("http.route") == "/api/v1/tasks/{package}/{channel}/{task}/run" || ((string?)s.GetTagItem("http.route"))?.EndsWith("/run", StringComparison.Ordinal) == true);
        var run = Assert.Single(capture.Spans, s => s.OperationName == "run task");
        Assert.Equal("succeeded", run.GetTagItem(HostTelemetry.OutcomeTag));
        Assert.DoesNotContain("hello from ok", string.Join('\n', capture.AllAttributes()));   // the task's own output is never an attribute
    }
}
