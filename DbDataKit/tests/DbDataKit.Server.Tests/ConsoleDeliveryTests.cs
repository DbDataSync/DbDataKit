using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Delivery;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Delivery in the console (plan 22a GP18) through the API the screens call: the channels of <c>settings.yaml</c> with their readiness, a test send that arrives at a local listener signed, the outbox (an administrator sees every message, a person their own),
/// and subscriptions (strict request, a person's own, everyone's for an administrator, pause, resume, send now). No mail server and no network but the loopback.
/// </summary>
public sealed class ConsoleDeliveryTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-delivery-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<(string Method, string Path, string Body, string? Signature)> hits = [];
    private HttpListener listener = null!;
    private int port;
    private int answer = 200;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = "";
    private string viewer = "";
    private string otherViewer = "";
    private string viewerId = "";
    private string otherViewerId = "";
    private bool rendererOn;

    private sealed class FakeRenderer : ISubscriptionRenderer
    {
        public Task<RenderResult> RenderAsync(SubscriptionRender request, CancellationToken cancellationToken) =>
            Task.FromResult<RenderResult>(new RenderResult.Rendered(Encoding.UTF8.GetBytes("rows for " + request.Subscriber), "text/csv", "sales.csv", "Sales", "The sales are attached.", null, []));
    }

    public async ValueTask InitializeAsync()
    {
        // a free port is asked for and then used: another test of this process can take it in between, so a port that is taken is asked for again
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 10) { try { listener.Close(); } catch (HttpListenerException) { } }
        }
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                lock (hits) hits.Add((context.Request.HttpMethod, context.Request.Url!.AbsolutePath, body, context.Request.Headers["X-DbData-Signature"]));
                context.Response.StatusCode = answer;
                var bytes = Encoding.UTF8.GetBytes(answer == 200 ? "ok" : "no");
                try { await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); } catch (Exception) { /* gone */ }
            }
        });

        Environment.SetEnvironmentVariable($"DDK_TEST_HOOK_TOKEN_{tag}", $"token-{tag}");
        Environment.SetEnvironmentVariable($"DDK_TEST_HOOK_SECRET_{tag}", "shared-signing-secret-" + tag);
        Directory.CreateDirectory(Path.Combine(directory, "connections"));
        // a signed webhook (production's), a sandbox of it (another path), and a Slack webhook whose secret is not set
        File.WriteAllText(Path.Combine(directory, "connections", "hook-conn.yml"), $"schema: connection/2\nkind: http\nname: hook-conn\nendpoint: \"http://127.0.0.1:{port}/in/{{token}}\"\nendpoint_secrets: {{ token: env:DDK_TEST_HOOK_TOKEN_{tag} }}\nwebhook: {{ format: signed, allow_private: [127.0.0.1] }}\nsign_in: {{ method: signed_webhook, key_secret: env:DDK_TEST_HOOK_SECRET_{tag} }}\n");
        var tokenFile = Path.Combine(directory, "sandbox-token.txt");
        File.WriteAllText(tokenFile, $"token-{tag}\n");      // the sandbox's token is a file: reference
        File.WriteAllText(Path.Combine(directory, "connections", "hook-sandbox.yml"), $"schema: connection/2\nkind: http\nname: hook-sandbox\nendpoint: \"http://127.0.0.1:{port}/sandbox/{{token}}\"\nendpoint_secrets: {{ token: \"file:{tokenFile.Replace('\\', '/')}\" }}\nwebhook: {{ format: signed, allow_private: [127.0.0.1] }}\nsign_in: {{ method: signed_webhook, key_secret: env:DDK_TEST_HOOK_SECRET_{tag} }}\n");
        File.WriteAllText(Path.Combine(directory, "connections", "slack-unfinished.yml"), $"schema: connection/2\nkind: http\nname: slack-unfinished\nendpoint: https://hooks.slack.com/services/{{token}}\nendpoint_secrets: {{ token: env:DDK_TEST_NOT_SET_{tag} }}\nwebhook: {{ format: slack }}\n");
        File.WriteAllText(Path.Combine(directory, "settings.yaml"), """
            # the console's settings: this comment is not part of the delivery section
            delivery:
              retry:
                max_attempts: 2
                backoff_seconds: [60]
            # kept after the section
            """);
        // which connection each channel name uses in each release channel is a binding (plan 27a IX23)
        File.WriteAllText(Path.Combine(directory, "bindings.yml"), "bindings: 1\ndelivery:\n  hook: hook-conn\n  unfinished: slack-unfinished\n  alerts:\n    prod: hook-conn\n    test: hook-sandbox\n    dev: hook-sandbox\n");
        await StartAsync();
    }

    private async Task StartAsync(bool renderer = false)
    {
        rendererOn = renderer;
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services =>
        {
            if (rendererOn) services.AddSingleton<ISubscriptionRenderer>(new FakeRenderer());
        });
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada Admin", UserRole.Admin, "ada@example.com", out _);
        viewer = SignIn("Vic Viewer", UserRole.Viewer, "vic@example.com", out viewerId);
        otherViewer = SignIn("Olga Viewer", UserRole.Viewer, "olga@example.com", out otherViewerId);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        // HttpListener on Linux can throw "Address already in use" while it removes its prefix on close (another test host took the port back); the listener is gone either way
        try { listener.Close(); } catch (HttpListenerException) { }
        Environment.SetEnvironmentVariable($"DDK_TEST_HOOK_TOKEN_{tag}", null);
        Environment.SetEnvironmentVariable($"DDK_TEST_HOOK_SECRET_{tag}", null);
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SignIn(string name, UserRole role, string email, out string id)
    {
        var user = app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, Guid.NewGuid().ToString("N")[..6] + email, role);
        id = user.Id;
        return app.Services.GetRequiredService<ISessionStore>().Create(user.Id).Id;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string? session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private static JsonObject Sub(string name = "Weekly sales", string channel = "hook", string? cron = null) => new()
    {
        ["name"] = name, ["kind"] = "dashboard", ["view"] = "sales", ["channel"] = channel, ["format"] = "csv", ["widget"] = "by_region",
        ["parameters"] = new JsonObject { ["region"] = new JsonArray("east") }, ["schedule"] = new JsonObject { ["cron"] = cron ?? "0 7 * * 1", ["time_zone"] = "Europe/Berlin" },
    };

    [Fact]
    public async Task The_overview_lists_the_channels_with_the_connection_each_release_channel_uses_and_never_a_secret_or_an_address()
    {
        var (status, body) = await Call(HttpMethod.Get, "/api/delivery", viewer);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body!["problem"]);
        var channels = body["channels"]!.AsArray().ToDictionary(c => (string)c!["name"]!, c => c!);
        Assert.Equal(["alerts", "hook", "unfinished"], channels.Keys.Order());
        Assert.Equal(("webhook", true), (((string)channels["hook"]["kind"]!), (bool)channels["hook"]["ready"]!));
        Assert.False((bool)channels["unfinished"]["ready"]!);
        Assert.Contains($"endpoint_secrets.token: env:DDK_TEST_NOT_SET_{tag} cannot be read", (string)channels["unfinished"]["missing"]![0]!);

        // a name alone is bound in prod; alerts is bound in prod, test and dev, each to its own connection, and the screen says which
        var alerts = channels["alerts"]["bindings"]!.AsArray().ToDictionary(x => (string)x!["release"]!, x => x!);
        Assert.Equal(["dev", "prod", "test"], alerts.Keys.Order());
        Assert.Equal("hook-conn", (string)alerts["prod"]["connection"]!);
        Assert.Equal("hook-sandbox", (string)alerts["test"]["connection"]!);
        var only = Assert.Single(channels["hook"]["bindings"]!.AsArray())!;
        Assert.Equal(("prod", "hook-conn", true), ((string)only["release"]!, (string)only["connection"]!, (bool)only["own"]!));
        Assert.Equal(["prod", "test", "dev"], body["releaseChannels"]!.AsArray().Select(r => (string)r!));

        // the connection is shown by its template, never the composed address or a secret
        Assert.Equal($"http://127.0.0.1:{port}/in/{{token}}", (string)only["target"]!);
        Assert.DoesNotContain("shared-signing-secret", body.ToJsonString());
        Assert.DoesNotContain($"token-{tag}", body.ToJsonString());
        Assert.False((bool)body["canRender"]!);
        Assert.Equal(2, (int)body["policy"]!["maxAttempts"]!);
        Assert.Contains(body["connectionKinds"]!.AsArray(), k => (string)k!["kind"]! == "smtp");
    }

    [Fact]
    public async Task A_test_send_arrives_at_the_listener_signed_and_the_outbox_shows_it_sent_with_its_try()
    {
        var (status, body) = await Call(HttpMethod.Post, "/api/delivery/channels/hook/test", admin, new TestSendBody());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sent", (string)body!["message"]!["status"]!);
        Assert.Equal("sent", (string)Assert.Single(body["attempts"]!.AsArray())!["outcome"]!);
        var hit = Assert.Single(hits);
        Assert.Equal(($"/in/token-{tag}", "POST"), (hit.Path, hit.Method));
        Assert.StartsWith("t=", hit.Signature);
        Assert.Contains("[TEST, prod] DbData test message", hit.Body);
        Assert.Contains("THIS IS A TEST MESSAGE", hit.Body);
        var verified = DbDataKit.Delivery.Webhooks.WebhookSignature.Verify("shared-signing-secret-" + tag, hit.Signature, hit.Body, DateTimeOffset.UtcNow);
        Assert.Equal(DbDataKit.Delivery.Webhooks.SignatureCheck.Valid, verified);

        var (_, list) = await Call(HttpMethod.Get, "/api/delivery/messages?status=sent", admin);
        Assert.Single(list!["messages"]!.AsArray());
        var id = (string)body["id"]!;
        var (_, detail) = await Call(HttpMethod.Get, $"/api/delivery/messages/{id}", admin);
        Assert.Equal("hook", (string)detail!["message"]!["channel"]!);
    }

    [Fact]
    public async Task A_test_send_goes_through_the_connection_of_the_release_channel_and_a_test_channel_never_reaches_the_prod_connection()
    {
        var (status, body) = await Call(HttpMethod.Post, "/api/delivery/channels/alerts/test", admin, new TestSendBody(Channel: "test"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("test", "hook-sandbox"), (((string)body!["releaseChannel"]!), (string)body["connection"]!));
        Assert.Equal("test", (string)body["message"]!["releaseChannel"]!);
        Assert.Equal("sent", (string)body["message"]!["status"]!);
        var hit = Assert.Single(hits);
        Assert.Equal($"/sandbox/token-{tag}", hit.Path);                                      // the sandbox's address, not production's
        Assert.Contains("[TEST, test] DbData test message", hit.Body);
        Assert.DoesNotContain(hits, h => h.Path.StartsWith("/in/", StringComparison.Ordinal));

        // a release channel the name is not bound in is refused naming where it is bound, and nothing is sent: `hook` is bound in prod only
        var (refused, error) = await Call(HttpMethod.Post, "/api/delivery/channels/hook/test", admin, new TestSendBody(Channel: "test"));
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Equal("not_bound", (string)error!["code"]!);
        Assert.Contains("not bound in the release channel 'test' (it is bound in: prod)", (string)error["error"]!);
        Assert.Single(hits);
    }

    [Fact]
    public async Task A_test_send_that_the_far_side_refuses_shows_its_words_and_a_parked_message_can_be_sent_again()
    {
        answer = 410;
        var (_, body) = await Call(HttpMethod.Post, "/api/delivery/channels/hook/test", admin, new TestSendBody());
        Assert.Equal(("dead", "permanent"), (((string)body!["message"]!["status"]!), (string)body["message"]!["lastOutcome"]!));
        Assert.Contains("410", (string)body["message"]!["lastDetail"]!);
        Assert.Contains("The webhook was removed at the other end", (string)body["message"]!["lastDetail"]!);
        Assert.DoesNotContain($"token-{tag}", body.ToJsonString());                                                           // the address carries a token and is never shown
        var id = (string)body["id"]!;

        answer = 200;
        var (status, again) = await Call(HttpMethod.Post, $"/api/delivery/messages/{id}/requeue", admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("pending", (string)again!["message"]!["status"]!);
        await app.Services.GetRequiredService<ConsoleDelivery>().Service.RunOnceAsync();
        var (_, after) = await Call(HttpMethod.Get, $"/api/delivery/messages/{id}", admin);
        Assert.Equal("sent", (string)after!["message"]!["status"]!);
        Assert.Equal(["permanent", "sent"], after["attempts"]!.AsArray().Select(a => (string)a!["outcome"]!));
        var (conflict, _) = await Call(HttpMethod.Post, $"/api/delivery/messages/{id}/requeue", admin);
        Assert.Equal(HttpStatusCode.Conflict, conflict);
    }

    [Fact]
    public async Task Only_an_administrator_tests_a_channel_and_a_person_sees_only_their_own_messages()
    {
        var (forbidden, _) = await Call(HttpMethod.Post, "/api/delivery/channels/hook/test", viewer, new TestSendBody());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
        var (missing, error) = await Call(HttpMethod.Post, "/api/delivery/channels/nowhere/test", admin, new TestSendBody());
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Contains("There is no channel 'nowhere'", (string)error!["error"]!);

        var delivery = app.Services.GetRequiredService<ConsoleDelivery>();
        var mine = await delivery.Service.SendAsync(new DeliveryRequest("hook", "For Vic", Text: "x", OwnerPerson: viewerId));
        await delivery.Service.SendAsync(new DeliveryRequest("hook", "For Olga", Text: "y", OwnerPerson: otherViewerId));
        var (_, vic) = await Call(HttpMethod.Get, "/api/delivery/messages", viewer);
        Assert.Equal(["For Vic"], vic!["messages"]!.AsArray().Select(m => (string)m!["subject"]!));
        var (_, everyone) = await Call(HttpMethod.Get, "/api/delivery/messages", admin);
        Assert.Equal(2, everyone!["messages"]!.AsArray().Count);
        var (ok, _) = await Call(HttpMethod.Get, $"/api/delivery/messages/{mine}", viewer);
        Assert.Equal(HttpStatusCode.OK, ok);
        var (hidden, _) = await Call(HttpMethod.Get, $"/api/delivery/messages/{mine}", otherViewer);
        Assert.Equal(HttpStatusCode.NotFound, hidden);
        var (badStatus, _) = await Call(HttpMethod.Get, "/api/delivery/messages?status=lost", admin);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
    }

    [Fact]
    public async Task A_person_subscribes_with_a_strict_request_and_sees_their_own_and_an_administrator_sees_everyones()
    {
        var (created, body) = await Call(HttpMethod.Post, "/api/delivery/subscriptions", viewer, Sub());
        Assert.Equal(HttpStatusCode.Created, created);
        var subscription = body!["subscription"]!;
        Assert.Equal(("Weekly sales", "active", viewerId, viewerId), (((string)subscription["name"]!), (string)subscription["status"]!, (string)subscription["person"]!, (string)subscription["owner"]!));
        Assert.Equal("Europe/Berlin", (string)subscription["schedule"]!["timeZone"]!);
        Assert.NotNull(subscription["nextRunAt"]);
        Assert.Null(subscription["channelProblem"]);

        var (bad, error) = await Call(HttpMethod.Post, "/api/delivery/subscriptions", viewer, Sub(cron: "*/10 * * * *"));
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("a subscription is at most hourly", (string)error!["error"]!);
        var noChannel = Sub(channel: "carrier");
        var (unknown, unknownBody) = await Call(HttpMethod.Post, "/api/delivery/subscriptions", viewer, noChannel);
        Assert.Equal(HttpStatusCode.BadRequest, unknown);
        Assert.Equal("unknown_channel", (string)unknownBody!["code"]!);
        var extra = Sub();
        extra["when"] = "now";
        var (strict, strictBody) = await Call(HttpMethod.Post, "/api/delivery/subscriptions", viewer, extra);
        Assert.Equal(HttpStatusCode.BadRequest, strict);
        Assert.Contains("a subscription has no key `when`", (string)strictBody!["error"]!);

        await Call(HttpMethod.Post, "/api/delivery/subscriptions", otherViewer, Sub("Olga's"));
        var (_, vic) = await Call(HttpMethod.Get, "/api/delivery/subscriptions", viewer);
        Assert.Equal(["Weekly sales"], vic!["subscriptions"]!.AsArray().Select(s => (string)s!["name"]!));
        var (notAll, _) = await Call(HttpMethod.Get, "/api/delivery/subscriptions?all=true", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, notAll);
        var (_, all) = await Call(HttpMethod.Get, "/api/delivery/subscriptions?all=true", admin);
        Assert.Equal(["Olga's", "Weekly sales"], all!["subscriptions"]!.AsArray().Select(s => (string)s!["name"]!).Order());
        var (forOther, _) = await Call(HttpMethod.Post, $"/api/delivery/subscriptions?for={otherViewerId}", viewer, Sub("Sneaky"));
        Assert.Equal(HttpStatusCode.Forbidden, forOther);
        var (byAdmin, adminBody) = await Call(HttpMethod.Post, $"/api/delivery/subscriptions?for={otherViewerId}", admin, Sub("Set up for Olga"));
        Assert.Equal(HttpStatusCode.Created, byAdmin);
        Assert.Equal((otherViewerId, "Ada Admin"), (((string)adminBody!["subscription"]!["person"]!), (string)adminBody["subscription"]!["owner"]! == otherViewerId ? "" : "Ada Admin"));
    }

    [Fact]
    public async Task A_subscription_stores_its_channels_kind_for_the_gateway_and_the_consoles_sync_follows_it()
    {
        // plan 32 DBR18: the Gateway has no channels and enqueues by this kind; the console's sender then sends the row through its own channel
        var (_, body) = await Call(HttpMethod.Post, "/api/delivery/subscriptions", viewer, Sub());
        var id = Guid.Parse((string)body!["subscription"]!["id"]!);
        var delivery = app.Services.GetRequiredService<ConsoleDelivery>();
        var kind = delivery.Channels.Find("hook")!.Kind;
        Assert.Equal(kind, (string)body["subscription"]!["channelKind"]!);
        Assert.Equal(kind, delivery.Subscriptions.Find(id)!.Fields.ChannelKind);
        Assert.Equal(kind, delivery.Subscriptions.ChannelKinds()["hook"]);

        // a row from before the column, or a channel bound to another kind of connection since: the sync writes the current kind and leaves the version alone
        delivery.Subscriptions.SetChannelKind("hook", "old-kind");
        var version = delivery.Subscriptions.Find(id)!.Version;
        Assert.Equal(1, delivery.SyncSubscriptionChannelKinds());
        Assert.Equal(kind, delivery.Subscriptions.Find(id)!.Fields.ChannelKind);
        Assert.Equal(version, delivery.Subscriptions.Find(id)!.Version);
        Assert.Equal(0, delivery.SyncSubscriptionChannelKinds());
    }

    [Fact]
    public async Task A_subscription_is_edited_paused_resumed_and_removed_by_its_person_and_nobody_else_can_see_it()
    {
        var (_, created) = await Call(HttpMethod.Post, "/api/delivery/subscriptions", viewer, Sub());
        var id = (string)created!["subscription"]!["id"]!;
        var version = (long)created["subscription"]!["version"]!;

        var (hidden, _) = await Call(HttpMethod.Put, $"/api/delivery/subscriptions/{id}", otherViewer, Sub("Taken"));
        Assert.Equal(HttpStatusCode.NotFound, hidden);
        var (updated, edited) = await Call(HttpMethod.Put, $"/api/delivery/subscriptions/{id}?version={version}", viewer, Sub("Monday sales", cron: "0 8 * * 1"));
        Assert.Equal(HttpStatusCode.OK, updated);
        Assert.Equal("Monday sales", (string)edited!["subscription"]!["name"]!);
        var (stale, staleBody) = await Call(HttpMethod.Put, $"/api/delivery/subscriptions/{id}?version={version}", viewer, Sub("Late"));
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Contains("changed by someone else", (string)staleBody!["error"]!);

        var (_, paused) = await Call(HttpMethod.Post, $"/api/delivery/subscriptions/{id}/pause", viewer);
        Assert.Equal(("paused", null), (((string)paused!["subscription"]!["status"]!), (string?)paused["subscription"]!["nextRunAt"]));
        var (_, resumed) = await Call(HttpMethod.Post, $"/api/delivery/subscriptions/{id}/resume", viewer);
        Assert.Equal("active", (string)resumed!["subscription"]!["status"]!);
        Assert.NotNull(resumed["subscription"]!["nextRunAt"]);

        var (conflict, conflictBody) = await Call(HttpMethod.Post, $"/api/delivery/subscriptions/{id}/run", viewer);
        Assert.Equal(HttpStatusCode.Conflict, conflict);
        Assert.Equal("cannot_render", (string)conflictBody!["code"]!);
        var (_, runs) = await Call(HttpMethod.Get, $"/api/delivery/subscriptions/{id}/runs", viewer);
        Assert.Empty(runs!["runs"]!.AsArray());

        var (gone, _) = await Call(HttpMethod.Delete, $"/api/delivery/subscriptions/{id}", viewer);
        Assert.Equal(HttpStatusCode.NoContent, gone);
        var (after, _) = await Call(HttpMethod.Get, $"/api/delivery/subscriptions/{id}/runs", viewer);
        Assert.Equal(HttpStatusCode.NotFound, after);
    }

    [Fact]
    public async Task A_subscription_whose_channel_was_removed_reports_the_problem_and_send_now_works_where_a_renderer_exists()
    {
        var delivery = app.Services.GetRequiredService<ConsoleDelivery>();
        var orphan = delivery.Subscriptions.Create(new SubscriptionFields("Orphan", "report", "summary", null, null, "pdf", null, "removed-channel", null, "0 7 * * *", "UTC"), viewerId, "Vic Viewer", viewerId, DateTimeOffset.UtcNow.AddDays(1));
        var (_, list) = await Call(HttpMethod.Get, "/api/delivery/subscriptions", viewer);
        Assert.Equal("The channel 'removed-channel' is not configured.", (string)list!["subscriptions"]![0]!["channelProblem"]!);
        _ = orphan;

        // a console that is given a renderer (the process that has the views module) can send now: the message is in the outbox and the run is recorded
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        await StartAsync(renderer: true);
        var (_, created) = await Call(HttpMethod.Post, "/api/delivery/subscriptions", viewer, Sub("Now"));
        var id = (string)created!["subscription"]!["id"]!;
        var (status, run) = await Call(HttpMethod.Post, $"/api/delivery/subscriptions/{id}/run", viewer);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sent", (string)run!["result"]!["outcome"]!);
        var (_, messages) = await Call(HttpMethod.Get, "/api/delivery/messages?source=subscription:" + id, admin);
        var message = Assert.Single(messages!["messages"]!.AsArray())!;
        Assert.Equal("sales.csv", (string)message["attachments"]![0]!["name"]!);
        var (_, runs) = await Call(HttpMethod.Get, $"/api/delivery/subscriptions/{id}/runs", viewer);
        Assert.Equal("sent", (string)runs!["runs"]![0]!["outcome"]!);
    }

    [Fact]
    public async Task A_binding_is_saved_through_the_strict_reader_and_the_next_message_uses_it_and_a_wrong_one_writes_nothing_and_production_in_a_test_channel_is_a_confirmed_recorded_choice()
    {
        var file = Path.Combine(directory, "bindings.yml");
        var before = File.ReadAllText(file);
        var body = new SlotBody(new() { ["prod"] = [new("hook-conn")], ["test"] = [new("hook-conn")] });     // the sandbox's binding is changed: a test would go to the connection of /in/
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/bindings/slots/delivery/alerts", viewer, body)).Status);

        // refused, and nothing is written: an unknown connection, a connection that is not a delivery kind, a model for a channel, a release channel that is not a name
        File.WriteAllText(Path.Combine(directory, "connections", "crm.yml"), "schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\n");
        foreach (var (bad, word) in new (SlotBody Body, string Word)[]
        {
            (new SlotBody(new() { ["prod"] = [new("nowhere")] }), "does not exist"),
            (new SlotBody(new() { ["prod"] = [new("crm")] }), "cannot send"),
            (new SlotBody(new() { ["prod"] = [new("hook-conn", "some-model")] }), "Unknown key"),
            (new SlotBody(new() { ["te st"] = [new("hook-conn")] }), "not a release channel"),
        })
        {
            var (status, error) = await Call(HttpMethod.Put, "/api/bindings/slots/delivery/hook", admin, bad);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Contains(word, error!["diagnostics"]!.ToJsonString());
        }

        Assert.Equal(before, File.ReadAllText(file));

        // production's connection in the test release channel of a delivery channel needs the administrator's confirmed choice (IX17): refused with what it would do, nothing written
        var (conflict, needs) = await Call(HttpMethod.Put, "/api/bindings/slots/delivery/alerts", admin, body);
        Assert.Equal(HttpStatusCode.Conflict, conflict);
        var need = Assert.Single(needs!["needsConfirmation"]!.AsArray())!;
        Assert.Equal("bindings/delivery/alerts/test", (string)need["scope"]!);
        Assert.Contains("which production uses", (string)need["effect"]!);
        Assert.Equal(before, File.ReadAllText(file));

        // confirmed: written, recorded with who and when, and the screen's view says so
        var (saved, view) = await Call(HttpMethod.Put, "/api/bindings/slots/delivery/alerts", admin, body with { Confirm = ["bindings/delivery/alerts/test"], Note = "the test channel may use the live hook this week" });
        Assert.Equal(HttpStatusCode.OK, saved);
        var bindings = app.Services.GetRequiredService<ConsoleBindings>();
        Assert.True(bindings.Confirmed("bindings/delivery/alerts/test"));
        var choice = bindings.ChoiceOf("bindings/delivery/alerts/test")!;
        Assert.False(string.IsNullOrEmpty(choice.ChosenBy));
        Assert.Equal("the test channel may use the live hook this week", choice.Note);
        var alerts = view!["view"]!["slots"]!.AsArray().Single(x => (string)x!["name"]! == "alerts")!;
        var testRow = alerts["rows"]!.AsArray().Single(r => (string)r!["release"]! == "test")!;
        Assert.True((bool)testRow["needsConfirmation"]!);
        Assert.True((bool)testRow["confirmed"]!);
        var written = File.ReadAllText(file);
        Assert.Contains("\"alerts\":\n    \"prod\": \"hook-conn\"\n    \"test\": \"hook-conn\"", written, StringComparison.Ordinal);
        Assert.Contains("\"unfinished\": \"slack-unfinished\"", written, StringComparison.Ordinal);

        // the file reads again as the console reads it, and the next message on the test channel goes where the new binding says
        Assert.Null((await Call(HttpMethod.Get, "/api/delivery", viewer)).Body!["problem"]);
        var (_, sent) = await Call(HttpMethod.Post, "/api/delivery/channels/alerts/test", admin, new TestSendBody(Channel: "test"));
        Assert.Equal("hook-conn", (string)sent!["connection"]!);
        Assert.Equal($"/in/token-{tag}", Assert.Single(hits).Path);

        // moved back to the sandbox: the choice is taken back with the binding
        var back = new SlotBody(new() { ["prod"] = [new("hook-conn")], ["test"] = [new("hook-sandbox")] });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/bindings/slots/delivery/alerts", admin, back)).Status);
        Assert.False(bindings.Confirmed("bindings/delivery/alerts/test"));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Delete, "/api/bindings/slots/delivery/unfinished", admin)).Status);
        Assert.DoesNotContain("unfinished", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mapping_in_settings_yaml_and_the_earlier_form_of_a_channel_are_refused_naming_the_file_and_the_new_place()
    {
        var settings = Path.Combine(directory, "settings.yaml");
        File.WriteAllText(settings, "delivery:\n  channels:\n    hook: hook-conn\n");
        app.Services.GetRequiredService<ConsoleDelivery>().Reload();
        Assert.Contains("delivery.channels is not kept here any more", (string)(await Call(HttpMethod.Get, "/api/delivery", viewer)).Body!["problem"]!);
        File.WriteAllText(settings, "delivery:\n  retry:\n    max_attempts: 2\n");
        File.WriteAllText(Path.Combine(directory, "bindings.yml"), "bindings: 1\ndelivery:\n  hook: { kind: webhook, url: \"env:X\" }\n");
        var (_, view) = await Call(HttpMethod.Get, "/api/bindings", viewer);
        Assert.Contains("Channels are connections now", view!["diagnostics"]!.ToJsonString());
        Assert.Contains("bindings.yml", view["diagnostics"]!.ToJsonString());
    }

    [Fact]
    public async Task Alert_rules_are_set_by_an_administrator_with_a_reason_for_every_refusal_and_a_failed_run_alerts_once()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/delivery/alerts", viewer)).Status);
        var (status, body) = await Call(HttpMethod.Get, "/api/delivery/alerts", admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["failed-run", "late-data", "approvals-waiting", "expiring-preview"], body!["rules"]!.AsArray().Select(r => (string)r!["kind"]!));
        Assert.All(body["rules"]!.AsArray(), r => Assert.False((bool)r!["enabled"]!));

        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/delivery/alerts/failed-run", viewer, new { enabled = true, channel = "hook", version = 0 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, "/api/delivery/alerts/mood", admin, new { enabled = true, version = 0 })).Status);
        var unknown = await Call(HttpMethod.Put, "/api/delivery/alerts/failed-run", admin, new { enabled = true, channel = "nowhere", version = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("'nowhere' is not configured", (string)unknown.Body!["error"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/delivery/alerts/failed-run", admin, new { enabled = true, channel = "hook", recipients = new[] { "not an address" }, version = 0 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/delivery/alerts/failed-run", admin, new { enabled = true, recipients = new[] { "ops@example.com" }, version = 0 })).Status);

        var set = await Call(HttpMethod.Put, "/api/delivery/alerts/failed-run", admin, new { enabled = true, channel = "hook", cooldownMinutes = 30, version = 0 });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.Equal(1, (long)set.Body!["rule"]!["version"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, "/api/delivery/alerts/failed-run", admin, new { enabled = false, channel = "hook", version = 0 })).Status);

        // a failed run in the run history is told once through the channel, not every pass
        var state = app.Services.GetRequiredService<StateDatabase>();
        var runs = new RunStore(state, "test");
        var id = runs.Start("nightly-load");
        runs.Finish(id, DbDataKit.Monitoring.RunOutcome.Failed, failure: new DbDataKit.Monitoring.RunFailure("Permanent", "the source is gone"));
        var alerts = app.Services.GetRequiredService<ConsoleDelivery>();
        Assert.Equal(1, (await alerts.AlertRunner.RunOnceAsync()).Sent);
        Assert.Equal(0, (await alerts.AlertRunner.RunOnceAsync()).Sent);
        await alerts.Service.RunOnceAsync();
        var message = Assert.Single(alerts.Outbox.List(source: "alert:failed-run"));
        Assert.Contains("nightly-load", message.BodyText);
        Assert.Equal("sent", message.Status);
        var after = await Call(HttpMethod.Get, "/api/delivery/alerts", admin);
        Assert.Equal("failed:nightly-load", (string)after.Body!["failing"]![0]!["source"]!);
    }
}
