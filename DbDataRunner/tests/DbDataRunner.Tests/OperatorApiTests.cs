using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28a R25 and R19 over the management API of a real server: the operator answers an in-doubt step, replays and discards dead letters, reads the table of units of a run; an administrator reads and sets the posture choices with their effect shown.
/// Each act is audited and a role below the one needed is refused.
/// </summary>
public sealed class OperatorApiTests
{
    private const string Base = "/api/v1/tasks/sample/dev/ok";
    private static readonly ProgressKey Key = new("sample", "dev", "ok");

    [Fact]
    public async Task An_in_doubt_step_is_listed_and_answered_once_by_an_operator_and_the_answer_is_audited()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var steps = new StepStore(h.State, Key);
        Assert.IsType<StepBegin.Run>(steps.Begin("chain-1", "charge", "", 1));
        Assert.IsType<StepBegin.Run>(steps.Begin("chain-1", "email", "", 1));
        steps.MarkInDoubt("chain-1");

        var (status, body) = await h.GetAsync(Base + "/in-doubt", ServerRole.Operator);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["charge", "email"], body!["steps"]!.AsArray().Select(s => (string)s!["name"]!).Order().ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync(Base + "/in-doubt", ServerRole.Viewer)).Status);

        // it happened: its result is stored and the next attempt reuses it
        var (ok, answer) = await h.PostAsync(Base + "/steps/charge/resolve", ServerRole.Operator, new { chain = "chain-1", key = "", happened = true, result = new { id = 42 } });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.False(answer!["retry_queued"]!.GetValue<bool>());
        var stored = steps.Get("chain-1", "charge", "")!;
        Assert.Equal((StepState.Completed, "{\"id\":42}"), (stored.State, stored.ResultJson));

        // it did not happen: the step is failed, so a retry runs it
        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync(Base + "/steps/email/resolve", ServerRole.Operator, new { chain = "chain-1", key = "", happened = false })).Status);
        Assert.Equal(StepState.Failed, steps.Get("chain-1", "email", "")!.State);

        // answered twice, a missing result, a viewer
        Assert.Equal(HttpStatusCode.Conflict, (await h.PostAsync(Base + "/steps/charge/resolve", ServerRole.Operator, new { chain = "chain-1", key = "", happened = false })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync(Base + "/steps/charge/resolve", ServerRole.Operator, new { chain = "chain-1", happened = true })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostAsync(Base + "/steps/charge/resolve", ServerRole.Viewer, new { chain = "chain-1", happened = false })).Status);
        Assert.Empty((await h.GetAsync(Base + "/in-doubt", ServerRole.Operator)).Body!["steps"]!.AsArray());

        var audit = h.Audit.Read(new AuditQuery(Verb: RunnerControl.ResolveStepVerb, Limit: 20));
        Assert.Contains(audit, r => r.Entry.Outcome == AuditEvent.Ok && r.Entry.TargetId == "sample/dev/ok");
        Assert.Contains(audit, r => r.Entry.Outcome == AuditEvent.Refused);
    }

    [Fact]
    public async Task A_dead_letter_is_replayed_into_the_inbox_exactly_once_or_discarded()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var progress = new ProgressStore(h.State, Key);
        var payloads = new PayloadStore(h.State);
        var replayable = h.State.NewId();
        Guid body;
        using (var writer = payloads.OpenWrite(new PayloadOptions("application/json", ProgressModule.Name, replayable.ToString("D"), "test")))
        {
            body = writer.Id;
            writer.Write("{\"order\":7}"u8);
            writer.Complete();
        }

        progress.AddDeadLetter("record:Order", "the far side said 422", replayable, body);
        var discardable = progress.AddDeadLetter("record:Order", "the far side said 500");

        var (status, listed) = await h.GetAsync(Base + "/dead-letters", ServerRole.Operator);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, listed!["dead_letters"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.GetAsync(Base + "/dead-letters", ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync(Base + "/dead-letters?status=nope", ServerRole.Operator)).Status);

        var (accepted, replay) = await h.PostAsync($"{Base}/dead-letters/{replayable}/replay", ServerRole.Operator);
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        Assert.Equal(replayable.ToString(), (string)replay!["inbox_item"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await h.PostAsync($"{Base}/dead-letters/{replayable}/replay", ServerRole.Operator)).Status);   // not open any more

        // taken once, with its body
        var run = Guid.NewGuid();
        var item = progress.TakeNext(run) ?? throw new InvalidOperationException("the replayed letter is not in the inbox");
        Assert.Equal(("record:Order", true), (item.Source, item.SignatureOk));
        using (var read = payloads.OpenRead(item.BodyPayload!.Value))
        using (var text = new StreamReader(read))
            Assert.Equal("{\"order\":7}", text.ReadToEnd());
        Assert.Null(progress.TakeNext(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync($"{Base}/dead-letters/{discardable}/discard", ServerRole.Operator)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await h.PostAsync($"{Base}/dead-letters/{discardable}/discard", ServerRole.Operator)).Status);
        Assert.Empty((await h.GetAsync(Base + "/dead-letters", ServerRole.Operator)).Body!["dead_letters"]!.AsArray());
        Assert.Single((await h.GetAsync(Base + "/dead-letters?status=replayed", ServerRole.Operator)).Body!["dead_letters"]!.AsArray());
        Assert.Single((await h.GetAsync(Base + "/dead-letters?status=discarded", ServerRole.Operator)).Body!["dead_letters"]!.AsArray());

        Assert.Single(h.Audit.Read(new AuditQuery(Verb: RunnerControl.ReplayVerb, Limit: 20)), r => r.Entry.Outcome == AuditEvent.Ok);
        Assert.Contains(h.Audit.Read(new AuditQuery(Verb: RunnerControl.DiscardVerb, Limit: 20)), r => r.Entry.Outcome == AuditEvent.Ok);
    }

    [Fact]
    public async Task The_units_and_steps_of_a_run_are_read_from_its_attempt_chain()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        // an item of an owner this runner does not serve, so no loop picks it up; it carries the chain the way every item does
        var payload = new WorkPayload { Task = "sample/dev/ok", Version = "1", Chain = "chain-u" };
        var runId = new ModuleWorkQueueStore(h.State, RunnerNames.EngineService).Enqueue("other/dev", "task", "ok", payloadJson: payload.ToJson());
        h.Runs.Start("sample/dev/ok", runId, package: "sample", packageVersion: "1", channel: "dev", attempt: 1);
        var units = new UnitStore(h.State, Key);
        Assert.Equal(UnitBegin.Run, units.Begin("chain-u", "table", "orders", 1));
        units.Complete("chain-u", "table", "orders", 1, new Dictionary<string, long> { ["rows"] = 120 });
        Assert.Equal(UnitBegin.Run, units.Begin("chain-u", "table", "lines", 1));
        units.Fail("chain-u", "table", "lines", 1, "timeout");
        Assert.Equal(UnitBegin.Run, units.Begin("chain-u", "table", "stock", 1));

        var (status, body) = await h.GetAsync($"/api/v1/runs/{runId}/units");

        Assert.Equal(HttpStatusCode.OK, status);
        var summary = body!["summary"]!;
        Assert.Equal((3, 1, 1, 1), ((int)summary["total"]!, (int)summary["done"]!, (int)summary["failed"]!, (int)summary["running"]!));
        var orders = body["units"]!.AsArray().Single(u => (string)u!["key"]! == "orders")!;
        Assert.Equal((120L, "completed"), ((long)orders["rows"]!, (string)orders["state"]!));
        Assert.NotNull(orders["duration_ms"]);
        Assert.Equal("timeout", (string)body["units"]!.AsArray().Single(u => (string)u!["key"]! == "lines")!["error"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync($"/api/v1/runs/{Guid.NewGuid()}/units")).Status);

        new StepStore(h.State, Key).Begin("chain-u", "ask", "", 1);
        var steps = (await h.GetAsync($"/api/v1/runs/{runId}/steps")).Body!["steps"]!.AsArray();
        Assert.Equal(("ask", "started"), ((string)steps[0]!["name"]!, (string)steps[0]!["state"]!));
    }

    [Fact]
    public async Task The_posture_is_read_with_its_effects_and_chosen_by_an_administrator_who_confirms_it()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();

        var (status, posture) = await h.GetAsync("/api/v1/posture-choices");
        Assert.Equal(HttpStatusCode.OK, status);
        var longer = posture!["settings"]!.AsArray().Single(s => (string)s!["name"]! == "longer_limits")!;
        Assert.Contains("more time and memory", (string)longer["effect"]!);
        Assert.Empty(posture["choices"]!.AsArray());

        // not confirmed: nothing changes and the answer names the effect
        var (refused, text) = await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Admin, new { setting = "longer_limits", scope = "sample" });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("not confirmed", (string)text!["error"]!);
        Assert.Empty((await h.GetAsync("/api/v1/posture-choices")).Body!["choices"]!.AsArray());

        // a viewer and an operator cannot choose; a bad setting and a bad scope are refused
        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Operator, new { setting = "longer_limits", scope = "sample", confirm = true })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Admin, new { setting = "nothing", scope = "sample", confirm = true })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Admin, new { setting = "longer_limits", scope = "Bad Scope", confirm = true })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Admin, new { setting = "production_binding_on_non_production", scope = "sample", confirm = true })).Status);

        var (chosen, choice) = await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Admin, new { setting = "longer_limits", scope = "sample", confirm = true, note = "nightly load" }, subject: "grace");
        Assert.Equal(HttpStatusCode.OK, chosen);
        Assert.Equal(("grace", "nightly load"), ((string)choice!["chosen_by"]!, (string)choice["note"]!));
        var listed = Assert.Single((await h.GetAsync("/api/v1/posture-choices")).Body!["choices"]!.AsArray())!;
        Assert.Equal(("longer_limits", "sample"), ((string)listed["setting"]!, (string)listed["scope"]!));
        Assert.Contains(h.Audit.Read(new AuditQuery(Verb: PostureGuard.ChooseVerb, Limit: 20)), r => r.Entry.ActorId == "grace" && r.Entry.TargetId == "longer_limits");

        // revoke: the default applies again; a second revoke finds nothing
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Admin, new { setting = "longer_limits", scope = "sample", revoke = true })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.CallAsync(HttpMethod.Put, "/api/v1/posture-choices", ServerRole.Admin, new { setting = "longer_limits", scope = "sample", revoke = true })).Status);
        Assert.Empty((await h.GetAsync("/api/v1/posture-choices")).Body!["choices"]!.AsArray());
        Assert.Contains(h.Audit.Read(new AuditQuery(Verb: PostureGuard.RevokeVerb, Limit: 20)), r => r.Entry.TargetId == "longer_limits");
    }
}
