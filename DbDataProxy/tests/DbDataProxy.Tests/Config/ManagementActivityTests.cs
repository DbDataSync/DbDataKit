using System.Net;
using System.Text.Json.Nodes;
using Apache.Arrow;
using DbDataKit.Web.Trust;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Data;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataProxy.Tests.Config;

/// <summary>What the proxy is doing now (plan 13, P06): the sessions, the running queries and their cancellation, the limits with the slots in use, and the login throttle.</summary>
public sealed partial class ManagementTests
{
    private async Task<JsonNode> GetJson(PairedServer server, string path) =>
        JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, path, ServerRole.Admin)).Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_running_query_is_listed_with_its_user_source_and_statement_and_cancelling_it_cancels_the_token_it_runs_under()
    {
        await StartAsync();
        var server = await PairAsync();
        var queries = app.Services.GetRequiredService<ActiveQueries>();
        using var running = queries.Begin("alice", "erp", "query", "SELECT * FROM big_table", CancellationToken.None);

        var listed = (await GetJson(server, "/api/v1/queries"))["queries"]!.AsArray();
        var query = Assert.Single(listed)!;
        Assert.Equal("alice", (string)query["user"]!);
        Assert.Equal("erp", (string)query["data_source"]!);
        Assert.Equal("SELECT * FROM big_table", (string)query["sql"]!);
        var id = (string)query["id"]!;

        Assert.False(running.Token.IsCancellationRequested);
        var cancelled = await CallAsync(server, HttpMethod.Post, $"/api/v1/queries/{id}/cancel", ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.True(running.Token.IsCancellationRequested);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Post, "/api/v1/queries/nothing/cancel", ServerRole.Admin)).StatusCode);

        running.Dispose();
        Assert.Empty((await GetJson(server, "/api/v1/queries"))["queries"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Post, $"/api/v1/queries/{id}/cancel", ServerRole.Admin)).StatusCode);   // it has ended
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/queries", ServerRole.Viewer)).StatusCode);
    }

    /// <summary>A query that does not end until it is told to, and says when it has started.</summary>
    private sealed class BlockedExecutor : IQueryExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken) => Task.FromResult(0L);
    }

    [Fact]
    public async Task A_query_that_is_cancelled_by_an_administrator_ends_as_cancelled_and_leaves_the_list_and_one_that_the_caller_abandons_is_not_reported_as_cancelled_by_an_administrator()
    {
        var queries = new ActiveQueries();
        var inner = new BlockedExecutor();
        var executor = new TrackedExecutor(inner, queries, "alice", "erp", includeSql: true);

        var call = executor.ExecuteQueryAsync(new QueryRequest("SELECT slow"), CancellationToken.None);
        await inner.Started.Task;                                   // it is running: no waiting, no timing
        var running = Assert.Single(queries.List());
        Assert.Equal("SELECT slow", running.Sql);

        Assert.True(queries.Cancel(running.Id));
        var ex = await Assert.ThrowsAsync<RpcException>(() => call);
        Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
        Assert.Empty(queries.List());

        // the caller going away is the caller's cancellation, not an administrator's
        var abandoned = new CancellationTokenSource();
        var inner2 = new BlockedExecutor();
        var call2 = new TrackedExecutor(inner2, queries, "alice", "erp", includeSql: true).ExecuteQueryAsync(new QueryRequest("SELECT slow"), abandoned.Token);
        await inner2.Started.Task;
        abandoned.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call2);
        Assert.Empty(queries.List());
    }

    [Fact]
    public async Task The_statement_is_shown_only_when_the_proxy_logs_sql_and_is_cut_to_a_length()
    {
        var queries = new ActiveQueries();
        var quiet = new TrackedExecutor(new BlockedExecutor(), queries, "alice", "erp", includeSql: false);
        var inner = new BlockedExecutor();
        var loud = new TrackedExecutor(inner, queries, "bob", "erp", includeSql: true);
        using var stop = new CancellationTokenSource();
        var a = quiet.ExecuteQueryAsync(new QueryRequest("SELECT secret_literal"), stop.Token);
        var b = loud.ExecuteQueryAsync(new QueryRequest("SELECT '" + new string('x', 2000) + "'"), stop.Token);
        await inner.Started.Task;
        await Task.Yield();
        var list = queries.List();
        Assert.Null(list.Single(q => q.User == "alice").Sql);
        var shown = list.Single(q => q.User == "bob").Sql!;
        Assert.Equal(ActiveQueries.MaxSqlChars + 1, shown.Length);
        Assert.EndsWith("…", shown);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
    }

    [Fact]
    public async Task Sessions_are_listed_by_an_id_that_is_not_a_token_and_a_session_can_be_ended()
    {
        await StartAsync();
        var server = await PairAsync();
        var store = app.Services.GetRequiredService<SessionStore>();
        var token = store.Issue(new ProxyPrincipal("alice", "password", ["erp"]));

        var sessions = (await GetJson(server, "/api/v1/sessions"))["sessions"]!.AsArray();
        var session = Assert.Single(sessions)!;
        Assert.Equal("alice", (string)session["user"]!);
        Assert.Equal("password", (string)session["method"]!);
        Assert.DoesNotContain(token, session.ToJsonString());
        var id = (string)session["id"]!;
        Assert.Equal(16, id.Length);

        Assert.NotNull(store.TryGet(token));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Delete, $"/api/v1/sessions/{id}", ServerRole.Admin)).StatusCode);
        Assert.Null(store.TryGet(token));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Delete, $"/api/v1/sessions/{id}", ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Delete, "/api/v1/sessions/ab", ServerRole.Admin)).StatusCode);   // a short prefix names nothing
    }

    [Fact]
    public async Task The_limits_of_each_data_source_are_reported_with_the_slots_in_use_and_the_throttle_with_who_it_holds_and_a_lockout_can_be_lifted()
    {
        await StartAsync();
        var server = await PairAsync();
        Write("connections/capped.yaml", Conn.Yaml("capped", "duckdb", "Data Source=:memory:", proxy: ["max_concurrent_queries: 2", "max_concurrent_queries_per_user: 1", "acquire_timeout_seconds: 5"]));
        Write("connections/open.yaml", Conn.Yaml("open", "duckdb", "Data Source=:memory:"));
        ((Microsoft.Extensions.Configuration.IConfigurationRoot)app.Configuration).Providers.OfType<DbDataProxy.Server.Configuration.ProxyConfigDirectoryProvider>().Single().ReloadNow();   // read now: the files were written by the test, not by an API call

        var gate = app.Services.GetRequiredService<QueryLimits>().For("capped", Options.CurrentValue.DataSources["capped"])!;
        using var slot = await gate.EnterAsync("alice");

        var limits = await GetJson(server, "/api/v1/limits");
        var capped = limits["data_sources"]!.AsArray().Single(d => (string)d!["data_source"]! == "capped")!;
        Assert.True((bool)capped["limited"]!);
        Assert.Equal(2, (int)capped["max_concurrent_queries"]!);
        Assert.Equal(1, (int)capped["max_concurrent_queries_per_user"]!);
        Assert.Equal(1, (int)capped["active"]!);
        Assert.False((bool)limits["data_sources"]!.AsArray().Single(d => (string)d!["data_source"]! == "open")!["limited"]!);

        var throttle = app.Services.GetRequiredService<LoginThrottle>();
        for (var i = 0; i < Options.CurrentValue.Auth.MaxFailedLogins; i++) throttle.RecordFailure("10.0.0.7", "mallory");
        Assert.True(throttle.IsLockedOut("10.0.0.7", "mallory"));
        var held = (await GetJson(server, "/api/v1/limits"))["login_throttle"]!["held"]!.AsArray();
        var entry = Assert.Single(held)!;
        Assert.Equal("mallory", (string)entry["user"]!);
        Assert.NotNull(entry["locked_until"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Delete, "/api/v1/throttle", ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Delete, "/api/v1/throttle?client=10.0.0.7&user=mallory", ServerRole.Admin)).StatusCode);
        Assert.False(throttle.IsLockedOut("10.0.0.7", "mallory"));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Delete, "/api/v1/throttle?client=10.0.0.7&user=mallory", ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/limits", ServerRole.Viewer)).StatusCode);

        // the audit says what was done, to what, and by whom
        var audit = File.ReadAllLines(Path.Combine(dataDirectory, "management-audit.log")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(audit, a => (string)a["command"]! == "throttle clear" && (string)a["outcome"]! == "cleared" && (string)a["userId"]! == "ada");
    }
}

/// <summary>The server certificate and the recent failures (plan 13, P07).</summary>
public sealed partial class ManagementTests
{
    [Fact]
    public async Task The_certificate_is_described_without_its_key_and_the_recent_failures_are_the_kind_and_code_a_client_was_told()
    {
        await StartAsync();
        var server = await PairAsync();
        var holder = app.Services.GetRequiredService<DbDataProxy.Server.Tls.TlsCertificateHolder>();

        var certificate = await GetJson(server, "/api/v1/certificate");
        Assert.Equal("selfsigned", (string)certificate["mode"]!);
        Assert.True((bool)certificate["present"]!);
        Assert.True((bool)certificate["self_signed"]!);
        Assert.Equal(holder.Current!.Thumbprint, (string)certificate["thumbprint"]!);
        Assert.True((int)certificate["days_left"]! > 30);
        Assert.False((bool)certificate["needs_renewal"]!);
        Assert.Contains("localhost", certificate["names"]!.AsArray().Select(n => (string)n!));
        Assert.DoesNotContain("PRIVATE", certificate.ToJsonString());

        var sink = app.Services.GetRequiredService<RecentFailures>();
        sink.Failed("erp", new DbDataKit.Connections.ConnectionFailure(DbDataKit.Connections.ConnectionFailureKind.Login, "18456", new DbDataKit.Dialect.EngineId("sqlserver"), raw: $"Login failed for user 'sa'. {Sentinel}"));
        sink.Failed("lake", new DbDataKit.Connections.ConnectionFailure(DbDataKit.Connections.ConnectionFailureKind.Network, null, new DbDataKit.Dialect.EngineId("duckdb")));
        var failures = (await GetJson(server, "/api/v1/failures"))["failures"]!.AsArray();
        Assert.Equal(["lake", "erp"], failures.Select(f => (string)f!["data_source"]!).ToList());     // newest first
        Assert.Equal("The login was rejected. [sqlserver 18456]", (string)failures[1]!["message"]!);
        Assert.Equal("Login", (string)failures[1]!["kind"]!);
        Assert.Equal("18456", (string)failures[1]!["code"]!);
        Assert.DoesNotContain(Sentinel, failures.ToJsonString());                                       // the driver's text stays in the log
        Assert.DoesNotContain("Login failed for user", failures.ToJsonString());

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/certificate", ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/failures", ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task Renewing_a_self_signed_certificate_asks_first_puts_the_new_one_in_use_at_once_and_the_console_that_pinned_the_old_one_has_to_pair_again()
    {
        await StartAsync();
        var server = await PairAsync();
        var holder = app.Services.GetRequiredService<DbDataProxy.Server.Tls.TlsCertificateHolder>();
        var oldThumbprint = holder.Current!.Thumbprint;

        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Post, "/api/v1/certificate/renew", ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(server, HttpMethod.Post, "/api/v1/certificate/renew", ServerRole.Admin, "{\"confirm\": false}")).StatusCode);
        Assert.Equal(oldThumbprint, holder.Current!.Thumbprint);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Post, "/api/v1/certificate/renew", ServerRole.Viewer, "{\"confirm\": true}")).StatusCode);

        var renewed = await CallAsync(server, HttpMethod.Post, "/api/v1/certificate/renew", ServerRole.Admin, "{\"confirm\": true}");
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var body = JsonNode.Parse(await renewed.Content.ReadAsStringAsync())!;
        Assert.True((bool)body["renewed"]!);
        Assert.Contains("paired again", (string)body["warning"]!);
        Assert.NotEqual(oldThumbprint, holder.Current!.Thumbprint);
        Assert.Equal(holder.Current.Thumbprint, (string)body["thumbprint"]!);

        // the old pin no longer matches what the server presents; pairing again, with the new certificate, works
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Viewer));
        var again = await PairAsync();
        Assert.NotEqual(server.CertificateFingerprint, again.CertificateFingerprint);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(again, HttpMethod.Get, "/api/v1/about", ServerRole.Viewer)).StatusCode);
    }
}

/// <summary>The proxy tells the notification feed what a monitoring view should show (plan 13, P10).</summary>
public sealed partial class ManagementTests
{
    private async Task<IReadOnlyList<JsonNode>> NotificationsAsync(PairedServer server, ServerRole role = ServerRole.Viewer, long? since = null) =>
        JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/notifications" + (since is null ? "" : $"?since_id={since}"), role)).Content.ReadAsStringAsync())!["notifications"]!.AsArray().Select(n => n!).ToList();

    [Fact]
    public async Task A_lockout_after_failed_logins_is_one_notification_naming_the_user_and_the_client_and_a_retry_does_not_repeat_it()
    {
        await StartAsync();
        var server = await PairAsync();
        var throttle = app.Services.GetRequiredService<LoginThrottle>();
        Assert.Empty(await NotificationsAsync(server));

        var max = Options.CurrentValue.Auth.MaxFailedLogins;
        for (var i = 0; i < max + 3; i++) throttle.RecordFailure("10.0.0.7", "mallory");   // past the limit: still one lockout
        var feed = await NotificationsAsync(server);
        var lockout = Assert.Single(feed);
        Assert.Equal("LoginLockedOut", (string)lockout["kind"]!);
        Assert.Contains("'mallory'", (string)lockout["message"]!);
        Assert.Contains("10.0.0.7", (string)lockout["message"]!);

        // lifted and locked again within the quiet period: the same event, not a second one
        throttle.Clear("10.0.0.7", "mallory");
        for (var i = 0; i < max; i++) throttle.RecordFailure("10.0.0.7", "mallory");
        Assert.Single(await NotificationsAsync(server));
        // another client is another subject
        for (var i = 0; i < max; i++) throttle.RecordFailure("10.0.0.8", "mallory");
        Assert.Equal(2, (await NotificationsAsync(server)).Count);

        // a console reads from where it left off
        var newest = (long)feed[0]["id"]!;
        Assert.Single(await NotificationsAsync(server, since: newest));
    }

    [Fact]
    public async Task A_data_source_that_turns_a_caller_away_and_a_statement_refused_on_a_read_only_source_are_notifications_without_the_statement()
    {
        await StartAsync();
        var server = await PairAsync();
        var events = app.Services.GetRequiredService<ProxyEvents>();

        // a gate with one slot, held; a second caller is refused at once (the wait is one millisecond)
        var gate = new DbDataKit.Connections.ConcurrencyGate(1, null, TimeSpan.FromMilliseconds(1));
        using var held = await gate.EnterAsync("alice");
        var executor = new LimitedExecutor(new BlockedExecutor(), gate, "bob", "erp",
            (source, user) => events.Raise(DbDataKit.Monitoring.NotificationKinds.LimitReached, source, $"Data source '{source}' turned a caller away ('{user}'): every slot is in use."));
        var ex = await Assert.ThrowsAsync<RpcException>(() => executor.ExecuteQueryAsync(new QueryRequest("SELECT 1"), CancellationToken.None));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        await Assert.ThrowsAsync<RpcException>(() => executor.ExecuteQueryAsync(new QueryRequest("SELECT 1"), CancellationToken.None));   // and again: one notification

        // a strict read-only source refuses a write
        Write("connections/ro.yaml", Conn.Yaml("ro", "duckdb", "Data Source=:memory:", proxy: ["classifier: strict"]));
        ((Microsoft.Extensions.Configuration.IConfigurationRoot)app.Configuration).Providers.OfType<DbDataProxy.Server.Configuration.ProxyConfigDirectoryProvider>().Single().ReloadNow();
        var principal = new ProxyPrincipal("carol", "password", ["*"]);
        var serving = app.Services.GetRequiredService<DbDataProxy.Server.Serving.ProxyServing>();
        await Assert.ThrowsAsync<DbDataKit.Serving.ServingDeniedException>(() => serving.Queries.PrepareAsync(
            new DbDataKit.Serving.ServingContext(new DbDataProxy.Server.Serving.ProxyCaller(principal)), new DbDataKit.Serving.ServingRequest.Sql("ro", "DROP TABLE secrets_table")));

        var feed = await NotificationsAsync(server);
        Assert.Equal(["LimitReached", "StatementRefused"], feed.Select(n => (string)n["kind"]!).Order(StringComparer.Ordinal).ToList());
        var limit = feed.Single(n => (string)n["kind"]! == "LimitReached");
        Assert.Contains("'bob'", (string)limit["message"]!);
        var refusal = feed.Single(n => (string)n["kind"]! == "StatementRefused");
        Assert.Contains("'carol'", (string)refusal["message"]!);
        Assert.Contains("'ro'", (string)refusal["message"]!);
        Assert.DoesNotContain("secrets_table", refusal.ToJsonString());      // the rule, never the statement
    }
}

/// <summary>The overview and the history of the configuration (plan 13, P08 and P09).</summary>
public sealed partial class ManagementTests
{
    [Fact]
    public async Task The_overview_says_what_this_is_and_how_busy_and_how_troubled_it_is_in_one_call_for_a_viewer()
    {
        await StartAsync();
        var server = await PairAsync();
        var overview = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/overview", ServerRole.Viewer)).Content.ReadAsStringAsync())!;
        Assert.Equal("dbdataproxy", (string)overview["product"]!);
        Assert.Equal(server.InstanceId, (string)overview["instance_id"]!);
        Assert.Equal(1, (int)overview["data_sources"]!);
        Assert.Equal(1, (int)overview["users"]!);
        Assert.True((bool)overview["jwt"]!);
        Assert.Equal(0, (int)overview["sessions"]!);
        Assert.Equal(0, (int)overview["running_queries"]!);
        Assert.True((long)overview["uptime_seconds"]! >= 0);
        Assert.DoesNotContain(Sentinel, overview.ToJsonString());

        app.Services.GetRequiredService<SessionStore>().Issue(new ProxyPrincipal("alice", "password", ["*"]));
        using var running = app.Services.GetRequiredService<ActiveQueries>().Begin("alice", "erp", "query", null, CancellationToken.None);
        var busy = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/overview", ServerRole.Viewer)).Content.ReadAsStringAsync())!;
        Assert.Equal(1, (int)busy["sessions"]!);
        Assert.Equal(1, (int)busy["running_queries"]!);
    }

    [Fact]
    public async Task The_history_starts_with_the_configuration_as_found_each_change_is_a_commit_and_a_diff_shows_both_sides_without_a_hash_or_a_key()
    {
        await StartAsync();
        var server = await PairAsync();
        Write("connections/scratch.yaml", Scratch("scratch"));
        await CallAsync(server, HttpMethod.Put, "/api/v1/users/carol", ServerRole.Admin, UserBody("a password", ["*"]));

        var commits = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/config/history", ServerRole.Admin)).Content.ReadAsStringAsync())!["commits"]!.AsArray();
        Assert.Equal(["Add user carol", "Configuration as found"], commits.Select(c => (string)c!["message"]!).ToList());
        Assert.Equal("Ada", (string)commits[0]!["author"]!);

        // the first commit holds the files as they were found, with the secrets in them masked
        var initial = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, $"/api/v1/config/history/{(string)commits[1]!["sha"]!}", ServerRole.Admin)).Content.ReadAsStringAsync())!;
        var users = initial["changes"]!.AsArray().Single(c => ((string)c!["path"]!).EndsWith("users.yaml"))!;
        Assert.Equal("added", (string)users["kind"]!);
        Assert.Contains("passwordHash: ********", (string)users["after"]!);
        Assert.DoesNotContain(Sentinel, initial.ToJsonString());

        // the change adds an entry; the hash of the new password is not in the diff either
        var change = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, $"/api/v1/config/history/{(string)commits[0]!["sha"]!}", ServerRole.Admin)).Content.ReadAsStringAsync())!;
        var modified = change["changes"]!.AsArray().Single()!;
        Assert.Equal("modified", (string)modified["kind"]!);
        Assert.DoesNotContain("PBKDF2", change.ToJsonString());
        Assert.Contains("carol", (string)modified["after"]!);
        Assert.DoesNotContain("carol", (string)modified["before"]!);

        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Get, "/api/v1/config/history/0000000", ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(server, HttpMethod.Get, "/api/v1/config/history/not-a-sha", ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Get, "/api/v1/config/history", ServerRole.Viewer)).StatusCode);
    }
}
