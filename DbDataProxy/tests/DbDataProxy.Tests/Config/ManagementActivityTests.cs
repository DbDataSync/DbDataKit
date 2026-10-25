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
