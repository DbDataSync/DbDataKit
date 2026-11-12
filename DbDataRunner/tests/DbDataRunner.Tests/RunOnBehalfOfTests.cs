using System.Net;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E21: a paired server that starts a manual run for a person it signed in says who (<c>on_behalf_of</c>); the audit names the person, a name that is not text or is too long is refused, and the
/// name a run records is the person with the caller beside them, never the person alone.
/// </summary>
public sealed class RunOnBehalfOfTests
{
    [Fact]
    public async Task A_run_started_for_a_person_is_audited_with_the_person_and_a_bad_name_is_refused()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        var (accepted, body) = await h.PostAsync("/api/v1/tasks/sample/dev/ok/run", ServerRole.Operator, new { on_behalf_of = "Ada via Main console" });
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        Assert.NotNull(body!["run_id"]);
        var detail = Assert.Single(h.Audit.Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 20))).Entry.Detail;
        Assert.Contains("\"on_behalf_of\":\"Ada via Main console\"", detail, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync("/api/v1/tasks/sample/dev/ok/run", ServerRole.Operator, new { on_behalf_of = 7 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync("/api/v1/tasks/sample/dev/ok/run", ServerRole.Operator, new { on_behalf_of = new string('x', RunnerControl.MaxOnBehalfOf + 1) })).Status);
    }

    [Fact]
    public void The_name_a_run_records_is_the_person_with_the_caller_beside_them_and_the_caller_alone_without_one()
    {
        var caller = new RunnerActor("client-1", "Build server", "api", "client-1");
        Assert.Equal("Build server", RunnerControl.RequestedBy(caller, null));
        Assert.Equal("Build server", RunnerControl.RequestedBy(caller, "   "));
        Assert.Equal("Ada via Main console (sent by Build server)", RunnerControl.RequestedBy(caller, " Ada via Main console "));
        // a control character cannot make a second line of a log or a record
        Assert.Equal("Ada (sent by Build server)", RunnerControl.RequestedBy(caller, "Ada\r\n"));
        Assert.Equal(RunnerControl.MaxOnBehalfOf + " (sent by Build server)".Length, RunnerControl.RequestedBy(caller, new string('x', 5000)).Length);
    }
}
