using DbDataKit.Runner.Api;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>Plan 28a R13: the runner's webhook endpoint, on a real server: a signature or a trusted client gets a body into the inbox and the task queued; a bad signature is kept, refused with 401 and audited.</summary>
public sealed class WebhookTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private const string Path = "/api/v1/webhooks/sample/dev/shop";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"order":"secret-order-4711"}""");

    private static PackageTask Listening() =>
        SamplePackage.Task("ok") with { Triggers = [TaskTrigger.OnWebhook("shop", "shop_hook")] };

    private static string Sign(byte[] body, string secret = RunnerHarness.WebhookSecret) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static Dictionary<string, string> Signed(byte[] body, string secret = RunnerHarness.WebhookSecret) => new() { ["X-Signature"] = Sign(body, secret) };

    private static ProgressStore Inbox(RunnerHarness h) => new(h.State, new ProgressKey("sample", "dev", "ok"));

    [Theory, MemberData(nameof(Engines))]
    public async Task A_signed_webhook_is_stored_queues_the_task_and_is_audited_without_its_body(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(Listening());
        await h.StartAsync();

        var (status, reply) = await h.PostRawAsync(Path, Body, Signed(Body));

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("ok", (string)reply!["signature"]!);
        var delivery = Assert.Single(reply["deliveries"]!.AsArray());
        Assert.Equal("ok", (string)delivery!["task"]!);
        var record = Assert.Single(Inbox(h).InboxRecords());
        Assert.True(record.SignatureOk);
        Assert.NotNull(record.BodyPayload);
        await h.WaitAsync(async () => await h.RunsAsync("?package=sample&channel=dev&outcome=succeeded") is { Count: >= 1 } r ? r : null, "the run the webhook queued");

        var entry = Assert.Single(h.Audit.Read(new AuditQuery(Verb: "runner.webhook.receive")));
        Assert.Equal((AuditEvent.Ok, "sample/dev/shop"), (entry.Entry.Outcome, entry.Entry.TargetId));
        Assert.Equal("ok", (string)System.Text.Json.Nodes.JsonNode.Parse(entry.Entry.Detail!)!["signature"]!);
        Assert.DoesNotContain("4711", entry.Entry.Detail ?? "", StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_signed_body_that_arrives_again_is_refused_as_a_replay_makes_no_item_and_is_audited_and_another_body_is_not_a_replay(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(Listening());
        await h.StartAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await h.PostRawAsync(Path, Body, Signed(Body))).Status);
        var (status, reply) = await h.PostRawAsync(Path, Body, Signed(Body));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("replay", (string)reply!["error"]!, StringComparison.Ordinal);
        Assert.Single(Inbox(h).InboxRecords());                                       // the replay made no item
        var entries = h.Audit.Read(new AuditQuery(Verb: "runner.webhook.receive")).Select(e => e.Entry).ToList();
        Assert.Equal([AuditEvent.Ok, AuditEvent.Refused], entries.OrderBy(e => e.Outcome == AuditEvent.Ok ? 0 : 1).Select(e => e.Outcome).ToArray());
        Assert.Contains(entries, e => e.Detail!.Replace(" ", "").Contains("\"replays\":1", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => (e.Detail ?? "").Contains("4711", StringComparison.Ordinal));

        var other = Encoding.UTF8.GetBytes("""{"order":"another-order"}""");
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostRawAsync(Path, other, Signed(other))).Status);
        Assert.Equal(2, Inbox(h).InboxRecords().Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_bad_signature_is_answered_401_stored_with_signature_ok_false_audited_and_not_triggered(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(Listening());
        await h.StartAsync();

        var (status, _) = await h.PostRawAsync(Path, Body, Signed(Body, "the wrong secret"));
        await Task.Delay(600);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        var record = Assert.Single(Inbox(h).InboxRecords());
        Assert.False(record.SignatureOk);
        Assert.Equal(ProgressStore.RefusedBySignature, record.TakenBy);
        Assert.Equal(0, Inbox(h).InboxWaiting());
        var runs = await h.RunsAsync();
        Assert.True(runs.Count == 0, $"nothing should have run, but there are {runs.Count}: {runs.ToJsonString()}\n{string.Join('\n', h.Log)}");   // nothing ran
        var entry = Assert.Single(h.Audit.Read(new AuditQuery(Verb: "runner.webhook.receive")));
        Assert.Equal(AuditEvent.Refused, entry.Entry.Outcome);
        Assert.Equal("invalid", (string)System.Text.Json.Nodes.JsonNode.Parse(entry.Entry.Detail!)!["signature"]!);
        Assert.DoesNotContain("4711", entry.Entry.Detail ?? "", StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Without_a_signature_or_a_token_the_call_is_401_and_nothing_is_kept_and_an_unknown_source_is_404(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(Listening());
        await h.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.PostRawAsync(Path, Body)).Status);
        Assert.Empty(Inbox(h).InboxRecords());
        Assert.Equal(HttpStatusCode.NotFound, (await h.PostRawAsync("/api/v1/webhooks/sample/dev/nobody", Body, Signed(Body))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostRawAsync("/api/v1/webhooks/sample/dev/bad%20source", Body, Signed(Body))).Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_trusted_client_delivers_a_webhook_an_operator_may_and_a_viewer_may_not(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(Listening());
        await h.StartAsync();

        var (viewer, _) = await h.PostRawAsync(Path, Body, role: ServerRole.Viewer);
        var (operatorStatus, reply) = await h.PostRawAsync(Path, Body, role: ServerRole.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, viewer);
        Assert.Equal(HttpStatusCode.Accepted, operatorStatus);
        Assert.Equal("trusted client", (string)reply!["signature"]!);
        Assert.Single(Inbox(h).InboxRecords(), r => r.SignatureOk);
        Assert.Equal([AuditEvent.Ok, AuditEvent.Refused], h.Audit.Read(new AuditQuery(Verb: "runner.webhook.receive")).Select(a => a.Entry.Outcome).Order().ToArray());
    }
}
